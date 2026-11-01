namespace PhoneDrive.Core.Files;

/// <summary>
/// 限定长度的只读子流：从内部流的 offset 处开始，最多可读取 count 字节。
/// OpenReadRange 的返回类型，保证随机读取不会越过请求范围。
/// </summary>
internal sealed class SubReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public SubReadStream(Stream inner, long offset, long count, bool leaveOpen = false)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        _inner = inner;
        _start = offset;
        _length = count;
        _leaveOpen = leaveOpen;
        _inner.Seek(offset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) target = 0;
        if (target > _length) target = _length;
        _position = target;
        _inner.Seek(_start + target, SeekOrigin.Begin);
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0) return 0;
        var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _position);
        if (allowed <= 0) return 0;
        var read = _inner.Read(buffer[..allowed]);
        _position += read;
        return read;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}
