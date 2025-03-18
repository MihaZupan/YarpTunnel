using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace YarpTunnel.Backend;

/// <summary>
/// This has the core logic that creates and maintains connections to the proxy.
/// </summary>
internal sealed class TunnelConnectionListener : IConnectionListener
{
    private readonly TunnelBackendOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _connectionLock;
    private readonly ConcurrentDictionary<ConnectionContext, byte> _connections = new();
    private readonly CancellationTokenSource _closedCts = new();
    private readonly HttpMessageInvoker _httpMessageInvoker = new(new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    public TunnelConnectionListener(TunnelBackendOptions options, EndPoint endpoint, ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionLock = new(options.MaxConnectionCount);
        EndPoint = endpoint;
        _loggerFactory = loggerFactory;

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
                    var connection = await HttpClientConnectionContext.ConnectAsync(_httpMessageInvoker, ((UriEndPoint)EndPoint).Uri, _options, _loggerFactory.CreateLogger<HttpClientConnectionContext>(), cancellationToken);

                    // Track this connection lifetime
                    _connections.TryAdd(connection, 0);

                    _ = connection.ExecutionTask.ContinueWith(t =>
                    {
                        _connections.TryRemove(connection, out _);
                        _connectionLock.Release();
                    }, CancellationToken.None);

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