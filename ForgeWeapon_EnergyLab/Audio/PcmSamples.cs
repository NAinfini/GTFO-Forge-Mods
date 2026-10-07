using System;
using System.Buffers.Binary;

namespace ForgeWeaponEnergyLabExperimental;

// The embedded sounds are standard mono signed 16-bit little-endian PCM, at 48 kHz, at most 12 seconds each.
internal static class PcmSamples
{
    public const int Rate = 48000;
    public const int MaxBytes = Rate * 2 * 12;

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || (bytes.Length & 1) != 0 || bytes.Length > MaxBytes)
            throw new ArgumentException("Invalid or oversized mono PCM resource.", nameof(bytes));
        var samples = new float[bytes.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)) / 32768f;
        return samples;
    }
}
