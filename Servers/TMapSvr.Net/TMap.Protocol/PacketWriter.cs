using System.Buffers.Binary;
using System.Text;

namespace TMap.Protocol;

/// <summary>
/// Builds an outgoing packet body, mirroring the C++ <c>CPacket::operator&lt;&lt;</c> overloads.
/// Strings are length-prefixed with a 4-byte little-endian int and carry no NUL terminator.
/// All scalars are little-endian (x86). The 16-byte header is reserved up front; <see cref="ToArray"/>
/// finalizes <c>wSize</c>.
/// </summary>
public sealed class PacketWriter
{
    // Latin1 is a lossless single-byte codec matching the server's MBCS CString handling.
    private static readonly Encoding DefaultEncoding = Encoding.Latin1;

    private readonly Encoding _encoding;
    private byte[] _buf;
    private int _len;

    public PacketWriter(ushort id, Encoding? encoding = null, int capacity = 256)
    {
        _encoding = encoding ?? DefaultEncoding;
        _buf = new byte[Math.Max(PacketHeader.Size, capacity)];
        _len = PacketHeader.Size;
        PacketHeader.WriteId(_buf, id);
    }

    public ushort Id => PacketHeader.ReadId(_buf);

    private void Ensure(int extra)
    {
        if (extra < 0 || extra > PacketHeader.MaxPacketSize - _len)
            throw new InvalidDataException("Packet exceeds the 16-bit wire length.");
        if (_len + extra <= _buf.Length) return;
        int newSize = _buf.Length * 2;
        while (newSize < _len + extra) newSize *= 2;
        Array.Resize(ref _buf, newSize);
    }

    public PacketWriter WriteByte(byte v)
    {
        Ensure(1);
        _buf[_len++] = v;
        return this;
    }

    public PacketWriter WriteSByte(sbyte v) => WriteByte(unchecked((byte)v));

    public PacketWriter WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    public PacketWriter WriteUInt16(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len), v);
        _len += 2;
        return this;
    }

    public PacketWriter WriteInt16(short v) => WriteUInt16(unchecked((ushort)v));

    public PacketWriter WriteUInt32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    public PacketWriter WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

    public PacketWriter WriteInt64(long v)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_len), v);
        _len += 8;
        return this;
    }

    public PacketWriter WriteUInt64(ulong v) => WriteInt64(unchecked((long)v));

    public PacketWriter WriteFloat(float v)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_len), v);
        _len += 4;
        return this;
    }

    /// <summary>Writes a 4-byte length prefix followed by the (NUL-free) string bytes.</summary>
    public PacketWriter WriteString(string? s)
    {
        s ??= string.Empty;
        int count = _encoding.GetByteCount(s);
        Ensure(sizeof(int) + count);
        WriteInt32(count);
        _len += _encoding.GetBytes(s.AsSpan(), _buf.AsSpan(_len, count));
        return this;
    }

    /// <summary>Appends raw bytes verbatim (used to forward trailing blobs unchanged).</summary>
    public PacketWriter WriteRaw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buf.AsSpan(_len));
        _len += bytes.Length;
        return this;
    }

    /// <summary>Finalizes the header <c>wSize</c> field and returns a right-sized copy of the packet.</summary>
    public byte[] ToArray()
    {
        return AsSpan().ToArray();
    }

    /// <summary>A finalized view, valid until this writer is changed. Network queues copy it before returning.</summary>
    public ReadOnlySpan<byte> AsSpan()
    {
        PacketHeader.WriteSize(_buf, checked((ushort)_len));
        return _buf.AsSpan(0, _len);
    }
}
