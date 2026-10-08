using System.Runtime.ExceptionServices;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// A write-through tee over the response body: every byte goes to the client exactly as before, and a
/// copy is kept up to <paramref name="capacity"/>. Past the cap the copy is dropped and
/// <see cref="Overflowed"/> says so — the response itself is never truncated or buffered.
/// <para>
/// A fault on the wire (the client went away while the body was being written — the very case a retry
/// exists for) does not stop the copy: the result finishes serialising into it, so the stored response
/// is whole and the retry can be replayed. The first such fault is kept and rethrown by
/// <see cref="ThrowIfWireFaulted"/> once the outcome has been recorded, so the request still ends the
/// way it would have without the capture.
/// </para>
/// </summary>
internal sealed class ResponseCapture(Stream inner, int capacity) : Stream
{
    private readonly MemoryStream _copy = new();
    private ExceptionDispatchInfo? _wireFault;

    public bool Overflowed { get; private set; }

    public byte[] ToArray() => _copy.ToArray();

    public void ThrowIfWireFaulted() => _wireFault?.Throw();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        if (_wireFault is not null)
        {
            return;
        }

        try
        {
            inner.Flush();
        }
        catch (Exception exception)
        {
            _wireFault = ExceptionDispatchInfo.Capture(exception);
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_wireFault is not null)
        {
            return;
        }

        try
        {
            await inner.FlushAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _wireFault = ExceptionDispatchInfo.Capture(exception);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Keep(buffer);
        if (_wireFault is not null)
        {
            return;
        }

        try
        {
            inner.Write(buffer);
        }
        catch (Exception exception)
        {
            _wireFault = ExceptionDispatchInfo.Capture(exception);
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Keep(buffer.Span);
        if (_wireFault is not null)
        {
            return;
        }

        try
        {
            await inner.WriteAsync(buffer, cancellationToken);
        }
        catch (Exception exception)
        {
            _wireFault = ExceptionDispatchInfo.Capture(exception);
        }
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
