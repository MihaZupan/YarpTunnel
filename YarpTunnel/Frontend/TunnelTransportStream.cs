namespace YarpTunnel.Frontend;

internal sealed class TunnelTransportStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationTokenSource _disposedCts = new();
    private readonly TaskCompletionSource _disposedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _ready;
    private readonly SemaphoreSlim _readyWriteLock = new(1);
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TunnelTransportStream(Stream inner)
    {
        _inner = inner;

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] receiveBuffer = new byte[1];
                    while (true)
                    {
                        int bytesRead = await _inner.ReadAsync(receiveBuffer, _disposedCts.Token);
                        if (bytesRead == 0)
                        {
                            await DisposeAsync();
                            break;
                        }

                        await _readyWriteLock.WaitAsync(_disposedCts.Token);
                        try
                        {
                            if (receiveBuffer[0] == 42)
                            {
                                _readyTcs.TrySetResult();
                                break;
                            }

                            if (!Volatile.Read(ref _ready))
                            {
                                await _inner.WriteAsync(receiveBuffer, _disposedCts.Token);
                                await _inner.FlushAsync(_disposedCts.Token);
                            }
                        }
                        finally
                        {
                            _readyWriteLock.Release();
                        }
                    }
                }
                catch
                {
                    await DisposeAsync();
                }
            });
        }
    }

    public async Task ReadyAsync()
    {
        await _readyWriteLock.WaitAsync(_disposedCts.Token);
        try
        {
            if (_readyTcs.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Backend sent us a premature ready signal?");
            }

            await _inner.WriteAsync(new byte[] { 42 }, _disposedCts.Token);
            await _inner.FlushAsync(_disposedCts.Token);

            Volatile.Write(ref _ready, true);
        }
        finally
        {
            _readyWriteLock.Release();
        }

        await _readyTcs.Task;
    }

    public Task DisposedTask => _disposedTcs.Task;

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;

    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override void Flush() => _inner.Flush();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        _disposedCts.Cancel();
        _disposedTcs.TrySetResult();
        _readyTcs.TrySetCanceled();

        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _disposedCts.Cancel();
        _disposedTcs.TrySetResult();
        _readyTcs.TrySetCanceled();

        return _inner.DisposeAsync();
    }
}