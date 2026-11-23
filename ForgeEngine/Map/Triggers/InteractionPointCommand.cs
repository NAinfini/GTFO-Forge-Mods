using System;
using System.Buffers.Binary;

namespace ForgeMap;

internal static class InteractionPointCommand
{
    public const string Endpoint = "forge.map.interaction_point";
    public const int PayloadBytes = 13;

    public static byte[] Payload(InteractionPointPhase phase, ulong sequence, float progress)
    {
        var payload = new byte[PayloadBytes];
        payload[0] = (byte)phase;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9, 4), BitConverter.SingleToInt32Bits(progress));
        return payload;
    }

    /// <summary>The number one phase of one hold is predicted under: the player's own request counter and the phase,
    /// which the request already carries, so the host names it without anything more on the wire. Never zero.</summary>
    public static long Token(InteractionPointPhase phase, ulong sequence) => (long)(sequence << 3 | (byte)phase);

    public static bool TryRead(byte[]? payload, out byte phase, out ulong sequence, out float progress)
    {
        phase = 0; sequence = 0; progress = 0;
        if (payload is not { Length: PayloadBytes }) return false;
        phase = payload[0];
        sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1, 8));
        progress = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(9, 4)));
        return float.IsFinite(progress);
    }
}
