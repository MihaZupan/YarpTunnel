using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace YarpTunnel.Backend;

internal sealed class HttpClientConnectionContext : ConnectionContext,
    IConnectionLifetimeFeature,
    IConnectionEndPointFeature,
    IConnectionItemsFeature,
    IConnectionIdFeature,
    IConnectionTransportFeature,
    IDuplexPipe
{
    private readonly TaskCompletionSource _executionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HttpClientConnectionContext()
    {
        Transport = this;

        Features.Set<IConnectionIdFeature>(this);
        Features.Set<IConnectionTransportFeature>(this);
        Features.Set<IConnectionItemsFeature>(this);
        Features.Set<IConnectionEndPointFeature>(this);
        Features.Set<IConnectionLifetimeFeature>(this);
    }

    public Task ExecutionTask => _executionTcs.Task;

    public override string ConnectionId { get; set; } = $"yarp-tunnel-{Guid.NewGuid():n}";

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
        HttpResponseMessage?.Dispose();

        _executionTcs.TrySetCanceled();

        Input?.CancelPendingRead();
        Output?.CancelPendingFlush();
    }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        Abort();
    }

    public override ValueTask DisposeAsync()
    {
        Abort();

        return base.DisposeAsync();
    }

    public static async ValueTask<HttpClientConnectionContext> ConnectAsync(HttpMessageInvoker invoker, Uri uri, TunnelOptions options, CancellationToken cancellationToken)
    {
        // Timeout for connection attempt + response headers
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = connectCts.Token;

        var connection = new HttpClientConnectionContext();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Version = new Version(2, 0),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new HttpClientConnectionContextContent(connection)
            };

            if (!string.IsNullOrEmpty(options.AuthorizationHeaderValue))
            {
                request.Headers.Add(HeaderNames.Authorization, options.AuthorizationHeaderValue);
            }

            connection.HttpResponseMessage = await invoker.SendAsync(request, cancellationToken);

            connection.HttpResponseMessage.EnsureSuccessStatusCode();

            var responseStream = await connection.HttpResponseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            ReadOnlySpan<byte> ExpectedMagicString() => "YarpTunnel"u8;

            byte[] magicStringBuffer = new byte[ExpectedMagicString().Length];
            await responseStream.ReadExactlyAsync(magicStringBuffer, cancellationToken);

            if (!ExpectedMagicString().SequenceEqual(magicStringBuffer))
            {
                throw new InvalidOperationException("Invalid magic string received from tunnel frontend.");
            }

            connection.Input = PipeReader.Create(responseStream);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private class HttpClientConnectionContextContent : HttpContent
    {
        private readonly HttpClientConnectionContext _connectionContext;

        public HttpClientConnectionContextContent(HttpClientConnectionContext connectionContext)
        {
            _connectionContext = connectionContext;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _connectionContext.Output = PipeWriter.Create(stream);

            // Immediately flush request stream to send headers
            // https://github.com/dotnet/corefx/issues/39586#issuecomment-516210081
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var _ = cancellationToken.UnsafeRegister(state => ((HttpClientConnectionContext)state!).Abort(), _connectionContext);

            await _connectionContext.ExecutionTask.ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}