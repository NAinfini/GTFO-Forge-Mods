using ForgeAudio;

namespace ForgeAudio.Tests;

internal static class AssetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var vorbis = VorbisHeader();
        var measured = AssetValidation.Ogg(vorbis);
        check(measured.Success && measured.Value == new OggInfo(1, 48000, 48000) && measured.Value.DecodedBytes == 192000,
            "Vorbis identification page accepted; one second of 48 kHz mono is 192000 decoded bytes");

        var opus = (byte[])vorbis.Clone();
        "OpusHe"u8.CopyTo(opus.AsSpan(29, 6));
        check(Code(opus) == AudioError.UnsupportedCodec, "Opus-in-Ogg rejected");
        check(Code("RIFF0000WAVEfmt "u8.ToArray().Concat(new byte[64]).ToArray()) == AudioError.UnsupportedCodec, "renamed WAV rejected");

        var noEos = (byte[])vorbis.Clone();
        noEos[5] = 2;
        check(Code(noEos) == AudioError.UnsupportedCodec, "Ogg stream without a final page rejected");
        check(Code(AppendPage(noEos, flags: 4, granule: 48000, serial: 0, sequence: 1, lacing: new byte[] { 1 }, body: Array.Empty<byte>(), omitLacing: true))
            == AudioError.UnsupportedCodec, "truncated Ogg lacing table rejected");
        check(Code(AppendPage(noEos, flags: 4, granule: 48000, serial: 0, sequence: 1, lacing: new byte[] { 2 }, body: new byte[] { 0 }))
            == AudioError.UnsupportedCodec, "truncated Ogg page body rejected");
        check(Code(AppendPage(noEos, flags: 4, granule: 48000, serial: 1, sequence: 1, lacing: Array.Empty<byte>(), body: Array.Empty<byte>()))
            == AudioError.UnsupportedCodec, "chained Ogg serial rejected");
        check(Code(AppendPage(noEos, flags: 4, granule: 100, serial: 0, sequence: 1, lacing: Array.Empty<byte>(), body: Array.Empty<byte>()))
            == AudioError.UnsupportedCodec, "non-monotonic granule rejected");

        var tooLong = (byte[])vorbis.Clone();
        BitConverter.GetBytes(48000UL * 3600 + 1).CopyTo(tooLong, 6);
        check(Code(tooLong) == AudioError.AssetLimit, "Ogg over one hour rejected before decoding");
        check(Code(new byte[AssetValidation.MaxFileBytes + 1]) == AudioError.AssetLimit, "compressed size limit enforced");
        check(AssetValidation.MaxDecodedBytes == 512L * 1024 * 1024 && AssetValidation.MaxCompressedCache == 256L * 1024 * 1024,
            "cache budgets are explicit");
    }

    private static AudioError Code(byte[] bytes)
    {
        var result = AssetValidation.Ogg(bytes);
        return result.Success ? AudioError.None : result.Error.Code;
    }

    private static byte[] VorbisHeader()
    {
        var bytes = new byte[58];
        "OggS"u8.CopyTo(bytes);
        bytes[4] = 0;
        bytes[5] = 6;
        BitConverter.GetBytes(48000UL).CopyTo(bytes, 6);
        bytes[26] = 1;
        bytes[27] = 30;
        bytes[28] = 1;
        "vorbis"u8.CopyTo(bytes.AsSpan(29, 6));
        bytes[39] = 1;
        BitConverter.GetBytes((uint)48000).CopyTo(bytes, 40);
        bytes[57] = 1;
        return bytes;
    }

    private static byte[] AppendPage(byte[] first, byte flags, ulong granule, uint serial, uint sequence,
        byte[] lacing, byte[] body, bool omitLacing = false)
    {
        var pageLength = 27 + (omitLacing ? 0 : lacing.Length) + body.Length;
        var bytes = new byte[first.Length + pageLength];
        first.CopyTo(bytes, 0);
        var offset = first.Length;
        "OggS"u8.CopyTo(bytes.AsSpan(offset));
        bytes[offset + 4] = 0;
        bytes[offset + 5] = flags;
        BitConverter.GetBytes(granule).CopyTo(bytes, offset + 6);
        BitConverter.GetBytes(serial).CopyTo(bytes, offset + 14);
        BitConverter.GetBytes(sequence).CopyTo(bytes, offset + 18);
        bytes[offset + 26] = checked((byte)lacing.Length);
        if (!omitLacing) lacing.CopyTo(bytes, offset + 27);
        body.CopyTo(bytes, offset + 27 + (omitLacing ? 0 : lacing.Length));
        return bytes;
    }
}
