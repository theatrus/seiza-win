using System.Buffers.Binary;

namespace Seiza.App.Rendering;

/// <summary>
/// Exposes immutable display pixels as a top-down BMP for WIC/Direct2D's virtual
/// bitmap loader. No encoded copy, temporary file or full GPU texture is needed.
/// </summary>
internal sealed class BgraBitmapStream : Stream
{
    private readonly byte[] _header = new byte[54];
    private readonly object _gate = new();
    private byte[]? _pixels;
    private long _position;
    private readonly long _length;

    public BgraBitmapStream(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("The image has an invalid BGRA pixel buffer.", nameof(pixels));
        _pixels = pixels; _length = 54L + pixels.Length;
        _header[0] = (byte)'B'; _header[1] = (byte)'M';
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(offset), value);
        Write(2, checked((int)_length)); Write(10, 54); Write(14, 40);
        Write(18, width); Write(22, -height); // Negative height keeps row zero at the top.
        BinaryPrimitives.WriteUInt16LittleEndian(_header.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(_header.AsSpan(28), 32);
        Write(34, pixels.Length); Write(38, 3780); Write(42, 3780);
    }

    public override bool CanRead => _pixels is not null;
    public override bool CanSeek => _pixels is not null;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get { lock (_gate) return _position; } set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            int count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
            int headerCount = (int)Math.Min(count, Math.Max(0, _header.Length - _position));
            if (headerCount > 0) _header.AsSpan((int)_position, headerCount).CopyTo(buffer);
            int pixelCount = count - headerCount;
            if (pixelCount > 0) _pixels.AsSpan((int)(_position + headerCount - _header.Length), pixelCount).CopyTo(buffer[headerCount..]);
            _position += count; return count;
        }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            long position = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position,
                SeekOrigin.End => _length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }));
            if (position < 0) throw new IOException("Cannot seek before the bitmap.");
            return _position = position;
        }
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { lock (_gate) _pixels = null; base.Dispose(disposing); }
}
