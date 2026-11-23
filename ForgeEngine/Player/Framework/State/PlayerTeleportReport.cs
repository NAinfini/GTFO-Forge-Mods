using System;

namespace ForgePlayer;

/// <summary>The body of a client's "my player was warped" report (`forge.player.input`, resource `teleported`): where the
/// player stood before the warp and where it stands after it, as the machine that ran the warp read them. Six
/// little-endian floats, from then to. The game applies a warp only on the machine that simulates the player
/// (`Player/evidence/player-actions.json`, fact warp-replication), so that machine is the only one that can read both
/// ends; the report names no player, the host publishes it for the sender's own.</summary>
internal static class PlayerTeleportReport
{
    internal const string Resource = "teleported";
    internal const int Length = 24;

    internal static byte[] Encode(double[] from, double[] to)
    {
        var payload = new byte[Length];
        for (var axis = 0; axis < 3; axis++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(axis * 4), (float)from[axis]);
            BitConverter.TryWriteBytes(payload.AsSpan(12 + axis * 4), (float)to[axis]);
        }
        return payload;
    }

    /// <summary>False for a body of another length or one that carries a value that is not finite.</summary>
    internal static bool TryDecode(byte[] payload, out double[] from, out double[] to)
    {
        from = to = Array.Empty<double>();
        if (payload.Length != Length) return false;
        var values = new double[6];
        for (var index = 0; index < 6; index++)
        {
            var value = BitConverter.ToSingle(payload, index * 4);
            if (!float.IsFinite(value)) return false;
            values[index] = value;
        }
        from = values[..3];
        to = values[3..];
        return true;
    }
}
