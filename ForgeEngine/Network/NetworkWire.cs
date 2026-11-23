using System;
using System.Buffers.Binary;
using System.Text;

namespace ForgeEngine.Network;

/// <summary>Little-endian writer over a handshake detail whose exact size the caller computed.</summary>
internal ref struct NetWriter
{
    private readonly Span<byte> _target;
    private int _offset;

    internal NetWriter(Span<byte> target) { _target = target; _offset = 0; }

    internal void Byte(byte value) => _target[_offset++] = value;
    internal void UInt16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_target[_offset..], value); _offset += 2; }
    internal void UInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(_target[_offset..], value); _offset += 8; }
    /// <summary>A text field the reader takes back unchanged: at most <see cref="NetworkProtocol.TextSlotBytes"/> characters,
    /// each one Latin-1 byte. A value outside that is this machine's own fault and is refused instead of truncated, so
    /// the peer never sees a malformed identity in place of the real difference.</summary>
    internal void Text(string value)
    {
        if (value.Length > NetworkProtocol.TextSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Text of " + value.Length + " characters exceeds its capacity.");
        foreach (var character in value)
            if (character > 'ÿ')
                throw new NetworkContractException(NetworkCodes.FormatInvalid, "Text holds a character outside Latin-1.");
        Byte((byte)value.Length);
        _offset += Encoding.Latin1.GetBytes(value, _target[_offset..]);
    }
}

/// <summary>Little-endian reader over a handshake detail, the exact inverse of <see cref="NetWriter"/>. Running out of bytes or a
/// length past its field's capacity is a peer's fault, refused by name.</summary>
internal ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _offset;

    internal NetReader(ReadOnlySpan<byte> source) { _source = source; _offset = 0; }

    internal readonly void RequireComplete()
    {
        if (_offset != _source.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body read " + _offset + " of " + _source.Length + " bytes.");
    }

    private ReadOnlySpan<byte> Take(int width)
    {
        if (_offset + width > _source.Length)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Message body ended after " + _offset + " of " + _source.Length + " bytes.");
        var slice = _source.Slice(_offset, width);
        _offset += width;
        return slice;
    }

    internal byte Byte() => Take(1)[0];
    internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    internal string Text()
    {
        var length = Byte();
        if (length > NetworkProtocol.TextSlotBytes)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Text of " + length + " characters exceeds its capacity.");
        return Encoding.Latin1.GetString(Take(length));
    }
}
