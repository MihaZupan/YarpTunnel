using Microsoft.AspNetCore.Http;

namespace YarpTunnel.Frontend;

internal sealed class DuplexHttpStream : Stream
{
    private readonly Stream _requestBody;
    private readonly Stream _responseBody;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _disposedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DuplexHttpStream(HttpContext context)
    {
        _requestBody = context.Request.Body;
        _responseBody = context.Response.Body;
    }

    public Task DisposedTask => _disposedTcs.Task;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        _cts.Token.ThrowIfCancellationRequested();

        var flushTask = _responseBody.FlushAsync(_cts.Token);

        return flushTask.IsCompleted
            ? flushTask
            : AwaitFlushTask(flushTask, cancellationToken);

        async Task AwaitFlushTask(Task flushTask, CancellationToken cancellationToken)
        {
            using var _ = cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _cts);

            await flushTask;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _cts.Token.ThrowIfCancellationRequested();

        var writeTask = _responseBody.WriteAsync(buffer, _cts.Token);

        return writeTask.IsCompletedSuccessfully
            ? writeTask
            : AwaitWriteTask(writeTask, cancellationToken);

        async ValueTask AwaitWriteTask(ValueTask writeTask, CancellationToken cancellationToken)
        {
            using var _ = cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _cts);

            await writeTask;
        }
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _cts.Token.ThrowIfCancellationRequested();

        var readTask = _requestBody.ReadAsync(buffer, _cts.Token);

        return readTask.IsCompletedSuccessfully
            ? readTask
            : AwaitReadTask(readTask, cancellationToken);

        async ValueTask<int> AwaitReadTask(ValueTask<int> readTask, CancellationToken cancellationToken)
        {
            using var _ = cancellationToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _cts);

            return await readTask;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _cts.CancelAsync().ContinueWith(static (task, s) =>
        {
            if (task.IsFaulted)
            {
                _ = task.Exception;
            }

            ((TaskCompletionSource)s!).TrySetResult();
        }, _disposedTcs);
    }

    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
