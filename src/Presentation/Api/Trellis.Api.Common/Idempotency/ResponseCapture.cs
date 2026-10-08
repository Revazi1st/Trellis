namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// A write-through tee over the response body: every byte goes to the client exactly as before, and a
/// copy is kept up to <paramref name="capacity"/>. Past the cap the copy is dropped and
/// <see cref="Overflowed"/> says so — the response itself is never truncated or buffered.
/// </summary>
internal sealed class ResponseCapture(Stream inner, int capacity) : Stream
{
    private readonly MemoryStream _copy = new();

    public bool Overflowed { get; private set; }

    public byte[] ToArray() => _copy.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Keep(buffer);
        inner.Write(buffer);
    }

    public override void WriteByte(byte value) => Write([value]);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Keep(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Keep(ReadOnlySpan<byte> bytes)
    {
        if (Overflowed)
        {
            return;
        }

        if (_copy.Length + bytes.Length > capacity)
        {
            Overflowed = true;
            _copy.SetLength(0);
            return;
        }

        _copy.Write(bytes);
    }

    protected override void Dispose(bool disposing)
    {
        // The inner stream is the server's response body: never ours to dispose.
        if (disposing)
        {
            _copy.Dispose();
        }

        base.Dispose(disposing);
    }
}
