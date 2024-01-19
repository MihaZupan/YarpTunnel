using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections;

namespace YarpTunnel.Backend;

/// <summary>
/// This has the core logic that creates and maintains connections to the proxy.
/// </summary>
internal sealed class TunnelConnectionListener : IConnectionListener
{
    private readonly TunnelOptions _options;
    private readonly SemaphoreSlim _connectionLock;
    private readonly ConcurrentDictionary<ConnectionContext, byte> _connections = new();
    private readonly CancellationTokenSource _closedCts = new();
    private readonly HttpMessageInvoker _httpMessageInvoker = new(new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        KeepAlivePingDelay = TimeSpan.FromSeconds(10),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

    public TunnelConnectionListener(TunnelOptions options, EndPoint endpoint)
    {
        _options = options;
        _connectionLock = new(options.MaxConnectionCount);
        EndPoint = endpoint;

        if (endpoint is not UriEndPoint)
        {
            throw new NotSupportedException("UriEndPoint is required for HTTP/2 transport.");
        }
    }

    public EndPoint EndPoint { get; }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closedCts.Token, cancellationToken);
            cancellationToken = cts.Token;

            // Kestrel will keep an active accept call open as long as the transport is active
            await _connectionLock.WaitAsync(cancellationToken);

            int retryWaitMs = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var connection = await HttpClientConnectionContext.ConnectAsync(_httpMessageInvoker, ((UriEndPoint)EndPoint).Uri, _options, cancellationToken);

                    // Track this connection lifetime
                    _connections.TryAdd(connection, 0);

                    using (ExecutionContext.SuppressFlow())
                    {
                        _ = Task.Run(async () =>
                        {
                            // When the connection is disposed, release it
                            try
                            {
                                await connection.ExecutionTask;
                            }
                            catch { }
                            finally
                            {
                                _connections.TryRemove(connection, out _);

                                // Allow more connections in
                                _connectionLock.Release();
                            }
                        }, CancellationToken.None);
                    }

                    return connection;
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    retryWaitMs = Math.Clamp(retryWaitMs + 100, 1_000, 10_000);

                    await Task.Delay(retryWaitMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task>? tasks = null;

        foreach (var (connection, _) in _connections)
        {
            tasks ??= new();
            tasks.Add(connection.DisposeAsync().AsTask());
        }

        if (tasks is null)
        {
            return;
        }

        await Task.WhenAll(tasks);
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _closedCts.Cancel();

        foreach (var (connection, _) in _connections)
        {
            // REVIEW: Graceful?
            connection.Abort();
        }

        return ValueTask.CompletedTask;
    }
}