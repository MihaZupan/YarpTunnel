using System.Collections.Concurrent;
using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace YarpTunnel.Frontend;

internal sealed class TunnelClientFactory : ForwarderHttpClientFactory
{
    private readonly ConcurrentDictionary<string, ConnectionQueue> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly TunnelFrontendOptions _options;

    public TunnelClientFactory(TunnelFrontendOptions options)
    {
        _options = options;
    }

    private sealed class ConnectionQueue
    {
        private readonly object _lock = new();
        private readonly Queue<TunnelTransportStream> _connections = new();
        private readonly Queue<TaskCompletionSource<TunnelTransportStream>> _waiters = new();

        public void AddConnection(TunnelTransportStream stream)
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

        public async ValueTask<TunnelTransportStream> GetConnectionAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TunnelTransportStream? stream;
                TaskCompletionSource<TunnelTransportStream>? waiter = null;

                lock (_lock)
                {
                    if (!_connections.TryDequeue(out stream))
                    {
                        while (_waiters.TryPeek(out var firstWaiter) && firstWaiter.Task.IsCompleted)
                        {
                            _waiters.Dequeue();
                        }

                        waiter = new TaskCompletionSource<TunnelTransportStream>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiters.Enqueue(waiter);
                    }
                }

                if (waiter is not null)
                {
                    using var _ = cancellationToken.UnsafeRegister(static (s, ct) => ((TaskCompletionSource<TunnelTransportStream>)s!).TrySetCanceled(ct), waiter);

                    stream = await waiter.Task;
                }

                if (stream!.DisposedTask.IsCompleted)
                {
                    continue;
                }

                try
                {
                    using var _ = cancellationToken.UnsafeRegister(static s => ((Stream)s!).Dispose(), stream);

                    await stream.ReadyAsync();
                }
                catch
                {
                    continue;
                }

                return stream;
            }
        }
    }

    private ConnectionQueue GetConnectionQueue(string host) =>
        _connections.GetOrAdd(host, static _ => new ConnectionQueue());

    public void AddConnection(string host, TunnelTransportStream stream) =>
        GetConnectionQueue(host).AddConnection(stream);

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        _options.ConfigureHttpClient?.Invoke(context, handler);
    }

    protected override HttpMessageHandler WrapHandler(ForwarderHttpClientContext context, HttpMessageHandler handler)
    {
        var tunnelSocketsHandler = new SocketsHttpHandler();
        ConfigureHandler(context, tunnelSocketsHandler);
        tunnelSocketsHandler.ConnectCallback = TunnelConnectCallback;

        tunnelSocketsHandler.PooledConnectionLifetime = Timeout.InfiniteTimeSpan;
        tunnelSocketsHandler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
        tunnelSocketsHandler.KeepAlivePingDelay = TimeSpan.FromSeconds(15);
        tunnelSocketsHandler.KeepAlivePingTimeout = TimeSpan.FromSeconds(10);
        tunnelSocketsHandler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;

        HttpMessageHandler tunnelHandler = tunnelSocketsHandler;
        tunnelHandler = base.WrapHandler(context, tunnelHandler);
        var tunnelInvoker = new HttpMessageInvoker(tunnelHandler);

        handler = new TunnelHandler(handler, tunnelInvoker);

        return base.WrapHandler(context, handler);
    }

    private async ValueTask<Stream> TunnelConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;

        if (!host.EndsWith(".tunnel", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException();
        }

        return await GetConnectionQueue(host).GetConnectionAsync(cancellationToken);
    }

    private sealed class TunnelHandler(HttpMessageHandler inner, HttpMessageInvoker tunnelInvoker) : DelegatingHandler(inner)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.RequestUri);

            string host = request.Headers.Host ?? request.RequestUri.Host;

            if (host.EndsWith(".tunnel", StringComparison.OrdinalIgnoreCase))
            {
                var scheme = request.RequestUri.Scheme;
                if (scheme != Uri.UriSchemeHttp)
                {
                    if (scheme == Uri.UriSchemeHttps)
                    {
                        // This allocation can be avoided by setting the tunnel destination prefix to http.
                        request.RequestUri = new UriBuilder(scheme) { Scheme = Uri.UriSchemeHttp }.Uri;
                    }
                    else
                    {
                        throw new InvalidOperationException("Unknown uri scheme.");
                    }
                }

                if ((request.VersionPolicy != HttpVersionPolicy.RequestVersionOrHigher && request.Version.Major <= 1) ||
                    (request.VersionPolicy == HttpVersionPolicy.RequestVersionExact && request.Version != HttpVersion.Version20) ||
                    (request.VersionPolicy == HttpVersionPolicy.RequestVersionOrHigher && request.Version.Major > 2))
                {
                    throw new InvalidOperationException("Only HTTP/2 is supported for tunnel connections.");
                }

                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

                return tunnelInvoker.SendAsync(request, cancellationToken);
            }

            return base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            tunnelInvoker.Dispose();
        }
    }
}