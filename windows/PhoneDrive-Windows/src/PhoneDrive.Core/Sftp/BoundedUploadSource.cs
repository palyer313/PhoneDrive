namespace PhoneDrive.Core.Sftp;

/// <summary>One source read supplies one SSH.NET WRITE. Only an acknowledged WRITE returns its permit.</summary>
public sealed class BoundedUploadSource : Stream
{
    private readonly Stream _source;
    private readonly SemaphoreSlim _slots;
    private readonly Func<CancellationToken, Task> _beforeRead;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private bool _closed;

    public BoundedUploadSource(Stream source, int maxInFlight,
        Func<CancellationToken, Task> beforeRead, TimeSpan responseTimeout)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxInFlight is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        _source = source; _slots = new(maxInFlight, maxInFlight);
        _beforeRead = beforeRead; _timeout = responseTimeout;
    }

    public void AcknowledgeWrite()
    {
        lock (_sync) { if (!_closed) _slots.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (buffer.IsEmpty) return 0;
        await _beforeRead(ct).ConfigureAwait(false);
        if (!await _slots.WaitAsync(_timeout, ct).ConfigureAwait(false))
            throw new IOException("SFTP 上传等待写入确认超时");
        try
        {
            var read = await _source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) AcknowledgeWrite(); // EOF does not produce a WRITE/ACK.
            return read;
        }
        catch { AcknowledgeWrite(); throw; }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use asynchronous upload");
    public override bool CanRead => !_closed && _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_sync) { if (!_closed) { _closed = true; _slots.Dispose(); } }
        base.Dispose(disposing); // caller owns the original input stream
    }
}
