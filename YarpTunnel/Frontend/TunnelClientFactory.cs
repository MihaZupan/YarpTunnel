using System.Collections.Concurrent;
using System.Net.Sockets;
using Yarp.ReverseProxy.Forwarder;

namespace YarpTunnel.Frontend;

internal sealed class TunnelClientFactory : ForwarderHttpClientFactory
{
    private sealed class ConnectionQueue
    {
        private readonly object _lock = new();
        private readonly Queue<DuplexHttpStream> _connections = new();
        private readonly Queue<TaskCompletionSource<DuplexHttpStream>> _waiters = new();

        public void AddConnection(DuplexHttpStream stream)
        {
            lock (_lock)
            {
                while (_waiters.TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult(stream))
                    {
                        return;
                    }
                }

                while (_connections.TryPeek(out var firstConnection) && firstConnection.DisposedTask.IsCompleted)
                {
                    _connections.Dequeue();
                }

                _connections.Enqueue(stream);
            }
        }

        public async ValueTask<DuplexHttpStream> GetConnectionAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DuplexHttpStream? stream;
                TaskCompletionSource<DuplexHttpStream>? waiter = null;

                lock (_lock)
                {
                    if (!_connections.TryDequeue(out stream))
                    {
                        while (_waiters.TryPeek(out var firstWaiter) && firstWaiter.Task.IsCompleted)
                        {
                            _waiters.Dequeue();
                        }

                        waiter = new TaskCompletionSource<DuplexHttpStream>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiters.Enqueue(waiter);
                    }
                }

                if (waiter is not null)
                {
                    using var _ = cancellationToken.UnsafeRegister(static (s, ct) => ((TaskCompletionSource<DuplexHttpStream>)s!).TrySetCanceled(ct), waiter);

                    stream = await waiter.Task;
                }

                if (stream!.DisposedTask.IsCompleted)
                {
                    continue;
                }

                return stream;
            }
        }
    }

    private readonly ConcurrentDictionary<string, ConnectionQueue> _connections = new(StringComparer.OrdinalIgnoreCase);

    private ConnectionQueue GetConnectionQueue(string host) =>
        _connections.GetOrAdd(host, static _ => new ConnectionQueue());

    public void AddConnection(string host, DuplexHttpStream stream) =>
        GetConnectionQueue(host).AddConnection(stream);

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);

        var previous = handler.ConnectCallback ?? DefaultConnectCallback;

        handler.ConnectCallback = TunnelConnectCallbackAsync;

        static async ValueTask<Stream> DefaultConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        async ValueTask<Stream> TunnelConnectCallbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = context.DnsEndPoint.Host;

            if (host.EndsWith(".tunnel", StringComparison.OrdinalIgnoreCase))
            {
                if (context.InitialRequestMessage.RequestUri?.Scheme != "http")
                {
                    throw new NotSupportedException("Tunnel connections must not use HTTPS.");
                }

                return await GetConnectionQueue(host).GetConnectionAsync(cancellationToken);
            }

            return await previous(context, cancellationToken);
        }
    }
}