using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ForgeCore;

/// <summary>A chunk of a chunked log: what the writer's owner called it, the time range it covers, and where it sits.</summary>
public readonly record struct ChunkInfo(ushort Type, uint StartMs, uint EndMs, long Offset, int RawLength);

/// <summary>The file does not belong to this container version or format, or its start is damaged. The message says which.</summary>
public sealed class ChunkLogFormatException : Exception
{
    public ChunkLogFormatException(string message) : base(message) { }
}

/// <summary>An append-only file of independently compressed and checksummed chunks, for logs that must survive a crash:
/// a 12-byte start (container version and the owner's format version), then chunks in time order, then on a clean finish an
/// index and a trailer. Without the trailer the reader scans chunk headers and keeps every chunk whose checksum holds, so a
/// crash or a cut-off write loses only the last partial chunk. What the bytes inside a chunk mean is the owner's.</summary>
public static class ChunkLog
{
    internal const uint FileMagic = 0x474C4346;   // "FCLG"
    internal const uint ChunkMagic = 0x4B4E4843;  // "CHNK"
    internal const uint IndexMagic = 0x58494346;  // "FCIX"
    internal const ushort ContainerVersion = 1;
    internal const int StartBytes = 12;
    internal const int ChunkHeaderBytes = 28;
    internal const int IndexEntryBytes = 24;
    internal const int TrailerBytes = 20;

    /// <summary>Starts a log on <paramref name="stream"/> (positioned at its start). <paramref name="formatVersion"/> is the
    /// owner's own format number; a reader that expects another number refuses the file.</summary>
    public static ChunkWriter Create(Stream stream, uint formatVersion) => new(stream, formatVersion);

    /// <summary>Opens a log for reading. Throws <see cref="ChunkLogFormatException"/> when it is not a chunk log, its container
    /// version differs, or its format version differs from <paramref name="expectedFormatVersion"/>.</summary>
    public static ChunkReader Open(Stream stream, uint expectedFormatVersion) => new(stream, expectedFormatVersion);

    internal static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    internal static void PutU32(Span<byte> s, int at, uint v) { for (var i = 0; i < 4; i++) s[at + i] = (byte)(v >> (8 * i)); }
    internal static void PutU64(Span<byte> s, int at, ulong v) { for (var i = 0; i < 8; i++) s[at + i] = (byte)(v >> (8 * i)); }
    internal static void PutU16(Span<byte> s, int at, ushort v) { s[at] = (byte)v; s[at + 1] = (byte)(v >> 8); }
    internal static uint GetU32(ReadOnlySpan<byte> s, int at) { uint v = 0; for (var i = 0; i < 4; i++) v |= (uint)s[at + i] << (8 * i); return v; }
    internal static ulong GetU64(ReadOnlySpan<byte> s, int at) { ulong v = 0; for (var i = 0; i < 8; i++) v |= (ulong)s[at + i] << (8 * i); return v; }
    internal static ushort GetU16(ReadOnlySpan<byte> s, int at) => (ushort)(s[at] | (s[at + 1] << 8));

}

/// <summary>Appends chunks. Not thread-safe: one writer thread. A failed write throws and leaves the file readable up to the last
/// chunk that was written whole; abandon the writer then, do not retry.</summary>
public sealed class ChunkWriter
{
    private readonly Stream _stream;
    private readonly List<ChunkInfo> _index = new();
    private readonly MemoryStream _scratch = new();
    private readonly byte[] _header = new byte[ChunkLog.ChunkHeaderBytes];
    private long _end;
    private bool _finished;

    internal ChunkWriter(Stream stream, uint formatVersion)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Span<byte> start = stackalloc byte[ChunkLog.StartBytes];
        ChunkLog.PutU32(start, 0, ChunkLog.FileMagic);
        ChunkLog.PutU16(start, 4, ChunkLog.ContainerVersion);
        ChunkLog.PutU16(start, 6, 0);
        ChunkLog.PutU32(start, 8, formatVersion);
        _stream.Write(start);
        _stream.Flush();
        _end = ChunkLog.StartBytes;
    }

    /// <summary>Bytes written so far, including headers.</summary>
    public long Length => _end;

    /// <summary>Compresses (when it helps) and appends one chunk, then flushes the stream so the chunk is on disk.</summary>
    public void Append(ushort type, uint startMs, uint endMs, ReadOnlySpan<byte> data)
    {
        if (_finished) throw new InvalidOperationException("The log is finished.");
        if (endMs < startMs) throw new ArgumentException("A chunk's end is not before its start.");
        _scratch.SetLength(0);
        using (var deflate = new DeflateStream(_scratch, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(data);
        var useDeflate = _scratch.Length < data.Length;
        ReadOnlySpan<byte> payload = useDeflate ? new ReadOnlySpan<byte>(_scratch.GetBuffer(), 0, (int)_scratch.Length) : data;
        ChunkLog.PutU32(_header, 0, ChunkLog.ChunkMagic);
        ChunkLog.PutU16(_header, 4, type);
        ChunkLog.PutU16(_header, 6, useDeflate ? (ushort)1 : (ushort)0);
        ChunkLog.PutU32(_header, 8, startMs);
        ChunkLog.PutU32(_header, 12, endMs);
        ChunkLog.PutU32(_header, 16, (uint)data.Length);
        ChunkLog.PutU32(_header, 20, (uint)payload.Length);
        ChunkLog.PutU32(_header, 24, ChunkLog.Crc32(payload, ChunkLog.Crc32(_header.AsSpan(0, 24))));
        _stream.Write(_header);
        _stream.Write(payload);
        _stream.Flush();
        _index.Add(new ChunkInfo(type, startMs, endMs, _end, data.Length));
        _end += ChunkLog.ChunkHeaderBytes + payload.Length;
    }

    /// <summary>Writes the index and the trailer. After this the log reads as complete.</summary>
    public void Finish()
    {
        if (_finished) return;
        var indexOffset = _end;
        var body = new byte[_index.Count * ChunkLog.IndexEntryBytes];
        for (var i = 0; i < _index.Count; i++)
        {
            var e = _index[i];
            var at = i * ChunkLog.IndexEntryBytes;
            ChunkLog.PutU64(body, at, (ulong)e.Offset);
            ChunkLog.PutU16(body, at + 8, e.Type);
            ChunkLog.PutU16(body, at + 10, 0);
            ChunkLog.PutU32(body, at + 12, e.StartMs);
            ChunkLog.PutU32(body, at + 16, e.EndMs);
            ChunkLog.PutU32(body, at + 20, (uint)e.RawLength);
        }
        var trailer = new byte[ChunkLog.TrailerBytes];
        ChunkLog.PutU32(trailer, 0, ChunkLog.IndexMagic);
        ChunkLog.PutU64(trailer, 4, (ulong)indexOffset);
        ChunkLog.PutU32(trailer, 12, (uint)_index.Count);
        ChunkLog.PutU32(trailer, 16, ChunkLog.Crc32(body, ChunkLog.Crc32(trailer.AsSpan(0, 16))));
        _stream.Write(body);
        _stream.Write(trailer);
        _stream.Flush();
        _finished = true;
    }
}

public sealed class ChunkReader
{
    private readonly Stream _stream;
    private readonly List<ChunkInfo> _chunks = new();

    internal ChunkReader(Stream stream, uint expectedFormatVersion)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Span<byte> start = stackalloc byte[ChunkLog.StartBytes];
        if (ReadAt(0, start) != ChunkLog.StartBytes || ChunkLog.GetU32(start, 0) != ChunkLog.FileMagic)
            throw new ChunkLogFormatException("This file is not a chunk log.");
        var container = ChunkLog.GetU16(start, 4);
        if (container != ChunkLog.ContainerVersion)
            throw new ChunkLogFormatException("The chunk container version is " + container + ", this library reads " + ChunkLog.ContainerVersion + ".");
        FormatVersion = ChunkLog.GetU32(start, 8);
        if (FormatVersion != expectedFormatVersion)
            throw new ChunkLogFormatException("The format version is " + FormatVersion + ", expected " + expectedFormatVersion + ".");
        if (!TryReadIndex()) Scan();
    }

    /// <summary>The owner's format version stored at the start of the file.</summary>
    public uint FormatVersion { get; }

    /// <summary>True when the writer finished cleanly (the trailer and index were intact). False when the chunks were found by
    /// scanning, which is what a crash leaves.</summary>
    public bool IsComplete { get; private set; }

    public IReadOnlyList<ChunkInfo> Chunks => _chunks;

    /// <summary>Reads and decompresses one chunk. Throws <see cref="InvalidDataException"/> when its checksum or length is wrong.</summary>
    public byte[] Read(ChunkInfo chunk)
    {
        var header = new byte[ChunkLog.ChunkHeaderBytes];
        if (ReadAt(chunk.Offset, header) != header.Length || ChunkLog.GetU32(header, 0) != ChunkLog.ChunkMagic)
            throw new InvalidDataException("There is no chunk at offset " + chunk.Offset + ".");
        var stored = checked((int)ChunkLog.GetU32(header, 20));
        var payload = new byte[stored];
        if (ReadAt(chunk.Offset + ChunkLog.ChunkHeaderBytes, payload) != stored)
            throw new InvalidDataException("The chunk at offset " + chunk.Offset + " is cut off.");
        if (ChunkLog.GetU32(header, 24) != ChunkLog.Crc32(payload, ChunkLog.Crc32(header.AsSpan(0, 24))))
            throw new InvalidDataException("The chunk at offset " + chunk.Offset + " fails its checksum.");
        var raw = checked((int)ChunkLog.GetU32(header, 16));
        if (ChunkLog.GetU16(header, 6) == 0)
        {
            if (raw != stored) throw new InvalidDataException("The stored chunk at offset " + chunk.Offset + " has two lengths.");
            return payload;
        }
        var result = new byte[raw];
        using var deflate = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
        var got = 0;
        while (got < raw)
        {
            var n = deflate.Read(result, got, raw - got);
            if (n == 0) throw new InvalidDataException("The chunk at offset " + chunk.Offset + " decompresses short.");
            got += n;
        }
        return result;
    }

    private int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > _stream.Length) return 0;
        _stream.Position = offset;
        var got = 0;
        while (got < buffer.Length)
        {
            var n = _stream.Read(buffer.Slice(got));
            if (n == 0) break;
            got += n;
        }
        return got;
    }

    private bool TryReadIndex()
    {
        var length = _stream.Length;
        if (length < ChunkLog.StartBytes + ChunkLog.TrailerBytes) return false;
        var trailer = new byte[ChunkLog.TrailerBytes];
        if (ReadAt(length - ChunkLog.TrailerBytes, trailer) != trailer.Length || ChunkLog.GetU32(trailer, 0) != ChunkLog.IndexMagic) return false;
        var indexOffset = (long)ChunkLog.GetU64(trailer, 4);
        var count = ChunkLog.GetU32(trailer, 12);
        var bodyLength = (long)count * ChunkLog.IndexEntryBytes;
        if (indexOffset < ChunkLog.StartBytes || indexOffset + bodyLength + ChunkLog.TrailerBytes != length) return false;
        var body = new byte[bodyLength];
        if (ReadAt(indexOffset, body) != body.Length) return false;
        if (ChunkLog.GetU32(trailer, 16) != ChunkLog.Crc32(body, ChunkLog.Crc32(trailer.AsSpan(0, 16)))) return false;
        for (var i = 0; i < count; i++)
        {
            var at = i * ChunkLog.IndexEntryBytes;
            _chunks.Add(new ChunkInfo(ChunkLog.GetU16(body, at + 8), ChunkLog.GetU32(body, at + 12), ChunkLog.GetU32(body, at + 16),
                (long)ChunkLog.GetU64(body, at), (int)ChunkLog.GetU32(body, at + 20)));
        }
        IsComplete = true;
        return true;
    }

    private void Scan()
    {
        var header = new byte[ChunkLog.ChunkHeaderBytes];
        var offset = (long)ChunkLog.StartBytes;
        var length = _stream.Length;
        while (offset + ChunkLog.ChunkHeaderBytes <= length)
        {
            if (ReadAt(offset, header) != header.Length || ChunkLog.GetU32(header, 0) != ChunkLog.ChunkMagic) break;
            var stored = ChunkLog.GetU32(header, 20);
            if (offset + ChunkLog.ChunkHeaderBytes + stored > length) break;
            var payload = new byte[stored];
            if (ReadAt(offset + ChunkLog.ChunkHeaderBytes, payload) != payload.Length) break;
            if (ChunkLog.GetU32(header, 24) != ChunkLog.Crc32(payload, ChunkLog.Crc32(header.AsSpan(0, 24)))) break;
            _chunks.Add(new ChunkInfo(ChunkLog.GetU16(header, 4), ChunkLog.GetU32(header, 8), ChunkLog.GetU32(header, 12), offset, (int)ChunkLog.GetU32(header, 16)));
            offset += ChunkLog.ChunkHeaderBytes + stored;
        }
    }
}
