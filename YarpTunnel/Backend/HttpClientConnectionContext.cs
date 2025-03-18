using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;

namespace YarpTunnel.Backend;

internal sealed class HttpClientConnectionContext : ConnectionContext,
    IConnectionLifetimeFeature,
    IConnectionEndPointFeature,
    IConnectionItemsFeature,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IDuplexPipe
{
    public const string ConnectionIdPrefix = "yarp-tunnel-";

    private static long s_connectionCounter;

    private readonly TaskCompletionSource _executionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<HttpClientConnectionContext> _logger;
    private readonly TunnelBackendOptions _options;
    private readonly long _connectionNumber = Interlocked.Increment(ref s_connectionCounter);

    private HttpClientConnectionContext(ILogger<HttpClientConnectionContext> logger, TunnelBackendOptions options)
    {
        _logger = logger;
        _options = options;
        Transport = this;

        Features.Set<IConnectionIdFeature>(this);
        Features.Set<IConnectionTransportFeature>(this);
        Features.Set<IConnectionItemsFeature>(this);
        Features.Set<IConnectionEndPointFeature>(this);
        Features.Set<IConnectionLifetimeFeature>(this);

        _logger.LogDebug("Connection {Number} created with {Id}.", _connectionNumber, ConnectionId);
    }

    public Task ExecutionTask => _executionTcs.Task;

    public override string ConnectionId { get; set; } = $"{ConnectionIdPrefix}{Guid.NewGuid():n}";

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override IDictionary<object, object?> Items { get; set; } = new ConnectionItems();
    public override IDuplexPipe Transport { get; set; }

    public override EndPoint? LocalEndPoint { get; set; }

    public override EndPoint? RemoteEndPoint { get; set; }

    public PipeReader Input { get; set; } = default!;

    public PipeWriter Output { get; set; } = default!;

    public override CancellationToken ConnectionClosed { get; set; }

    public HttpResponseMessage HttpResponseMessage { get; set; } = default!;

    public override void Abort()
    {
        _logger.LogDebug("Connection {Id} aborted.", ConnectionId);

        HttpResponseMessage?.Dispose();

        _executionTcs.TrySetResult();

        Input?.CancelPendingRead();
        Output?.CancelPendingFlush();
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _logger.LogDebug(abortReason, "Connection {Id} aborted.", ConnectionId);

        Abort();
    }

    public override ValueTask DisposeAsync()
    {
        Abort();

        return base.DisposeAsync();
    }

    public static async ValueTask<HttpClientConnectionContext> ConnectAsync(HttpMessageInvoker invoker, Uri uri, TunnelBackendOptions options, ILogger<HttpClientConnectionContext> logger, CancellationToken cancellationToken)
    {
        var connection = new HttpClientConnectionContext(logger, options);

        try
        {
            Stream transport;

            // Timeout for connection attempt + response headers
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(30));
                transport = await connection.ConnectAsyncCore(invoker, uri, connectCts.Token);
            }

            try
            {
                await connection.PingPongAsync(transport, cancellationToken);

                connection.Input = PipeReader.Create(transport);
                connection.Output = PipeWriter.Create(transport);
            }
            catch
            {
                await transport.DisposeAsync();
                throw;
            }

            return connection;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error during connection to tunnel frontend.");

            await connection.DisposeAsync();
            throw;
        }
    }


    private async Task<Stream> ConnectAsyncCore(HttpMessageInvoker invoker, Uri uri, CancellationToken cancellationToken)
    {
        // This request will serve as the transport for a reverse HTTP/2 connection.
        // Using HTTP/1.1 for now as it should have lower overhead.
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11
        };

        request.Headers.Upgrade.Add(new ProductHeaderValue("YarpTunnel", "0.1.5"));
        request.Headers.Connection.Add("Upgrade");

        if (!string.IsNullOrEmpty(_options.AuthorizationHeaderValue))
        {
            request.Headers.Add(HeaderNames.Authorization, _options.AuthorizationHeaderValue);
        }

        HttpResponseMessage = await invoker.SendAsync(request, cancellationToken);

        if (HttpResponseMessage.StatusCode != HttpStatusCode.SwitchingProtocols)
        {
            throw new InvalidOperationException($"Unexpected status code {HttpResponseMessage.StatusCode} received from tunnel frontend.");
        }

        var transport = await HttpResponseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ReadOnlySpan<byte> ExpectedMagicString() => "YarpTunnel"u8;

            byte[] magicStringBuffer = new byte[ExpectedMagicString().Length];
            await transport.ReadExactlyAsync(magicStringBuffer, cancellationToken);

            if (!ExpectedMagicString().SequenceEqual(magicStringBuffer))
            {
                throw new InvalidOperationException("Invalid magic string received from tunnel frontend.");
            }

            return transport;
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private async Task PingPongAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = timeoutCts.Token;

        using var writeTimerCts = new CancellationTokenSource();

        byte[] sendBuffer = [1];
        byte[] receiveBuffer = [0];
        bool doneSending = false;

        Task sendTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

                while (await timer.WaitForNextTickAsync(writeTimerCts.Token) && !Volatile.Read(ref doneSending))
                {
                    await stream.WriteAsync(sendBuffer, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch { }
        }, CancellationToken.None);

        Exception? receiveException = null;

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(receiveBuffer, cancellationToken);
                if (read == 0)
                {
                    throw new InvalidOperationException("Connection closed by tunnel frontend.");
                }

                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                if (receiveBuffer[0] == 42)
                {
                    _logger.LogDebug("Received ready signal from tunnel frontend on connection {Id}.", ConnectionId);
                    break;
                }

                _logger.LogDebug("Received keepalive pong from tunnel frontend on connection {Id}.", ConnectionId);
            }
        }
        catch (Exception ex)
        {
            receiveException = ex;
        }

        Volatile.Write(ref doneSending, true);
        writeTimerCts.Cancel();
        await sendTask;

        if (receiveException is not null)
        {
            throw new InvalidOperationException("Error during ping-pong with tunnel frontend.", receiveException);
        }

        sendBuffer[0] = 42;
        await stream.WriteAsync(sendBuffer, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}