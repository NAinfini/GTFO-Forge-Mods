namespace ForgeCore.Tests;

internal static class RecordingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        VarInts(check);
        Quantization(check);
        Quaternions(check);
        ChunkFiles(check);
    }

    private static void VarInts(Action<bool, string> check)
    {
        var buffer = new byte[Compact.MaxVarBytes * 8];
        var unsigned = new ulong[] { 0, 1, 127, 128, 16383, 16384, uint.MaxValue, ulong.MaxValue };
        var signed = new long[] { 0, -1, 1, -64, 63, long.MinValue, long.MaxValue };
        var w = 0;
        foreach (var v in unsigned) Compact.WriteVar(buffer, ref w, v);
        var r = 0;
        var ok = true;
        foreach (var v in unsigned) ok &= Compact.ReadVar(buffer, ref r) == v;
        check(ok && r == w, "compact: unsigned variable-length integers round-trip across byte boundaries and the 64-bit extremes");
        w = 0;
        foreach (var v in signed) Compact.WriteVarSigned(buffer, ref w, v);
        r = 0; ok = true;
        foreach (var v in signed) ok &= Compact.ReadVarSigned(buffer, ref r) == v;
        check(ok, "compact: signed zig-zag integers round-trip, including long.MinValue");
        w = 0;
        Compact.WriteVar(buffer, ref w, 300);
        check(w == 2 && buffer[0] == 0xAC && buffer[1] == 0x02, "compact: 300 is the bytes AC 02 (byte lock)");
        w = 0;
        Compact.WriteDelta(buffer, ref w, 1000, 998);
        r = 0;
        check(w == 1 && Compact.ReadDelta(buffer, ref r, 1000) == 998, "compact: a small delta takes one byte and restores the value");
        var truncated = new byte[] { 0x80, 0x80 };
        var rt = 0;
        var threw = false;
        try { Compact.ReadVar(truncated, ref rt); } catch (FormatException) { threw = true; }
        var over = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        rt = 0;
        var threwOver = false;
        try { Compact.ReadVar(over, ref rt); } catch (FormatException) { threwOver = true; }
        check(threw && threwOver, "compact: a truncated or overlong variable-length integer is refused, not guessed");
    }

    private static void Quantization(Action<bool, string> check)
    {
        var ok = true;
        for (var v = -100f; v <= 100f; v += 0.37f)
        {
            var back = Compact.Dequantize(Compact.Quantize(v, -100f, 0.01f, 20000), -100f, 0.01f);
            ok &= MathF.Abs(back - v) <= 0.0051f;
        }
        check(ok, "compact: centimetre quantization stays within half a step");
        check(Compact.Quantize(500f, -100f, 0.01f, 20000) == 20000 && Compact.Quantize(-500f, -100f, 0.01f, 20000) == 0
            && Compact.Quantize(float.NaN, -100f, 0.01f, 20000) == 0, "compact: out-of-range and NaN values saturate instead of wrapping");
    }

    private static void Quaternions(Action<bool, string> check)
    {
        var rng = new Random(7);
        var worst = 0.0;
        var buffer = new byte[Compact.QuaternionBytes];
        for (var i = 0; i < 2000; i++)
        {
            double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1, w = rng.NextDouble() * 2 - 1;
            var n = Math.Sqrt(x * x + y * y + z * z + w * w);
            x /= n; y /= n; z /= n; w /= n;
            var p = 0;
            Compact.WriteQuaternion(buffer, ref p, (float)x, (float)y, (float)z, (float)w);
            p = 0;
            Compact.ReadQuaternion(buffer, ref p, out var rx, out var ry, out var rz, out var rw);
            var sign = x * rx + y * ry + z * rz + w * rw < 0 ? -1.0 : 1.0; // q and -q are the same rotation
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(x - sign * rx), Math.Abs(y - sign * ry)), Math.Max(Math.Abs(z - sign * rz), Math.Abs(w - sign * rw))));
        }
        check(worst < 6e-5, "compact: smallest-three quaternions keep every component within 6e-5, under 0.01 degrees of rotation (worst " + worst.ToString("0.000000") + ")");
        var q = 0;
        Compact.WriteQuaternion(buffer, ref q, 0f, 0f, 0f, 1f);
        q = 0;
        Compact.ReadQuaternion(buffer, ref q, out var ix, out var iy, out var iz, out var iw);
        check(MathF.Abs(ix) < 1e-3f && MathF.Abs(iy) < 1e-3f && MathF.Abs(iz) < 1e-3f && MathF.Abs(iw - 1f) < 1e-5f, "compact: the identity rotation round-trips");
    }

    private static byte[] Payload(int seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)((i / 16 + seed) & 0x3F); // compressible
        return data;
    }

    private static byte[] Written(int chunks, bool finish)
    {
        var stream = new MemoryStream();
        var writer = ChunkLog.Create(stream, 5);
        for (var i = 0; i < chunks; i++) writer.Append((ushort)(i % 3), (uint)(i * 1000), (uint)(i * 1000 + 999), Payload(i, 2000 + i * 17));
        if (finish) writer.Finish();
        return stream.ToArray();
    }

    private static bool SameChunks(ChunkReader reader, int count)
    {
        if (reader.Chunks.Count != count) return false;
        for (var i = 0; i < count; i++)
        {
            var c = reader.Chunks[i];
            if (c.Type != i % 3 || c.StartMs != i * 1000 || c.EndMs != i * 1000 + 999) return false;
            if (!reader.Read(c).AsSpan().SequenceEqual(Payload(i, 2000 + i * 17))) return false;
        }
        return true;
    }

    private static void ChunkFiles(Action<bool, string> check)
    {
        var full = Written(6, finish: true);
        var reader = ChunkLog.Open(new MemoryStream(full), 5);
        check(reader.IsComplete && SameChunks(reader, 6), "chunklog: a finished log reads back every chunk through its index, with type, time range and contents");

        var raw = new MemoryStream();
        var w = ChunkLog.Create(raw, 5);
        var noise = new byte[512];
        new Random(3).NextBytes(noise);
        w.Append(9, 0, 1, noise);
        w.Finish();
        var noiseReader = ChunkLog.Open(new MemoryStream(raw.ToArray()), 5);
        check(noiseReader.Read(noiseReader.Chunks[0]).AsSpan().SequenceEqual(noise) && raw.Length < 12 + 28 + 512 + 24 + 20 + 1,
            "chunklog: incompressible data is stored as is, not grown by compression");

        var unfinished = Written(6, finish: false);
        var scanned = ChunkLog.Open(new MemoryStream(unfinished), 5);
        check(!scanned.IsComplete && SameChunks(scanned, 6), "chunklog: without a trailer the chunks are found by scanning");

        var cut = unfinished.AsSpan(0, unfinished.Length - 100).ToArray();
        var cutReader = ChunkLog.Open(new MemoryStream(cut), 5);
        check(!cutReader.IsComplete && SameChunks(cutReader, 5), "chunklog: a log cut inside its last chunk keeps every earlier whole chunk");

        var lastStart = (int)ChunkLog.Open(new MemoryStream(full), 5).Chunks[5].Offset;
        var flipped = (byte[])full.Clone();
        flipped[lastStart + ChunkLog.ChunkHeaderBytes + 3] ^= 0xFF;
        var flippedReader = ChunkLog.Open(new MemoryStream(flipped), 5);
        var rejected = false;
        try { flippedReader.Read(flippedReader.Chunks[5]); } catch (InvalidDataException) { rejected = true; }
        check(flippedReader.IsComplete && rejected, "chunklog: a flipped byte inside a chunk is caught by its checksum when read");

        var badTrailer = (byte[])full.Clone();
        badTrailer[^1] ^= 0xFF;
        var rebuilt = ChunkLog.Open(new MemoryStream(badTrailer), 5);
        check(!rebuilt.IsComplete && rebuilt.Chunks.Count == 6, "chunklog: a damaged trailer falls back to scanning and still lists all whole chunks");

        var corruptMiddle = (byte[])unfinished.Clone();
        var secondStart = (int)ChunkLog.Open(new MemoryStream(unfinished), 5).Chunks[2].Offset;
        corruptMiddle[secondStart + ChunkLog.ChunkHeaderBytes + 1] ^= 0xFF;
        var stopped = ChunkLog.Open(new MemoryStream(corruptMiddle), 5);
        check(stopped.Chunks.Count == 2, "chunklog: scanning stops at the first chunk that fails its checksum and never skips ahead");

        var message = "";
        try { ChunkLog.Open(new MemoryStream(full), 6); } catch (ChunkLogFormatException e) { message = e.Message; }
        var notLog = "";
        try { ChunkLog.Open(new MemoryStream(new byte[40]), 5); } catch (ChunkLogFormatException e) { notLog = e.Message; }
        var oldContainer = (byte[])full.Clone();
        oldContainer[4] = 9;
        var containerMessage = "";
        try { ChunkLog.Open(new MemoryStream(oldContainer), 5); } catch (ChunkLogFormatException e) { containerMessage = e.Message; }
        check(message.Contains("5") && message.Contains("6") && notLog.Contains("not a chunk log") && containerMessage.Contains("container version"),
            "chunklog: another format version, a foreign file and another container version are refused with the difference named");
    }
}
