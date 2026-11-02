using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeOrdnanceArsenal;

/// <summary>Tags of the per-call data this package sends through ForgeOrdnance's <c>OrdnanceApi.SendData</c> (host to every
/// peer). Each record has a fixed little-endian layout (the rack's name trails it); <see cref="Wire"/> writes and reads it.
/// <see cref="Zone"/>, <see cref="Beam"/> and <see cref="Rack"/> are sent with <c>SendState</c>, so a player who joins later
/// gets the latest one; <see cref="ZoneEnd"/> and <see cref="BeamEnd"/> end them (and clear the kept state). The claim of a rack
/// (reservation, commit, cancel) is ForgeOrdnance's own message, not a tag of this package.</summary>
public enum DataTag : byte
{
    /// <summary>A shell starts falling: look, kind, impact x y z, top y, fall seconds, blast radius.</summary>
    Shell = 1,
    /// <summary>An area effect starts: kind, centre x y z, radius, seconds.</summary>
    Zone = 2,
    /// <summary>The beam aims: reset generation, origin and target; HasTarget distinguishes an idle beam.</summary>
    Beam = 3,
    /// <summary>The beam ends in the specified reset generation (16-byte GUID).</summary>
    BeamEnd = 4,
    /// <summary>The area effect ended.</summary>
    ZoneEnd = 6,
    /// <summary>A heavy weapon lies in its opened pod: the weapon, the landing point and arrival yaw, the call-in's Id.</summary>
    Rack = 7,
    /// <summary>The host cannot retain this rack; remove it without granting a weapon (empty payload).</summary>
    RackUnavailable = 10,
}

public readonly struct RackRecord
{
    public RackRecord(HeavyWeaponSpec spec, uint gearId, float x, float y, float z, float yaw, string id)
    {
        Spec = spec; GearId = gearId; X = x; Y = y; Z = z; Yaw = yaw; Id = id;
    }
    /// <summary>The weapon's parameters, with the gear resolved to <see cref="GearId"/>.</summary>
    public readonly HeavyWeaponSpec Spec;
    public readonly uint GearId;
    public readonly float X, Y, Z, Yaw;
    /// <summary>The call-in definition's Id: every peer shows its name in its own language.</summary>
    public readonly string Id;
}

public readonly struct ShellRecord
{
    public ShellRecord(ShellLook look, StrikeKind kind, float x, float y, float z, float top, float fall, float blast)
    {
        Look = look; Kind = kind; X = x; Y = y; Z = z; Top = top; Fall = fall; Blast = blast;
    }
    public readonly ShellLook Look;
    public readonly StrikeKind Kind;
    public readonly float X, Y, Z, Top, Fall, Blast;
}

public readonly struct ZoneRecord
{
    public ZoneRecord(StrikeKind kind, float x, float y, float z, float radius, float seconds, float slow = 1f)
    {
        Kind = kind; X = x; Y = y; Z = z; Radius = radius; Seconds = seconds; Slow = slow;
    }
    public readonly StrikeKind Kind;
    public readonly float X, Y, Z, Radius, Seconds;
    /// <summary>Stasis: the share of their own speed enemies keep inside; 1 (no slow) for the other kinds.</summary>
    public readonly float Slow;
}

public readonly struct BeamRecord
{
    public BeamRecord(Guid generation, float ox, float oy, float oz, float tx, float ty, float tz, bool hasTarget)
    {
        Generation = generation;
        OX = ox; OY = oy; OZ = oz; TX = tx; TY = ty; TZ = tz; HasTarget = hasTarget;
    }
    public readonly Guid Generation;
    public readonly float OX, OY, OZ, TX, TY, TZ;
    public readonly bool HasTarget;
}

public static class Wire
{
    public const int ShellBytes = 2 + 6 * 4;
    public const int ZoneBytes = 1 + 6 * 4;
    public const int BeamBytes = 16 + 1 + 6 * 4;
    /// <summary>The fixed part of a rack record; the Id's UTF-8 bytes follow, cut to fit <see cref="MaxDataBytes"/>.</summary>
    public const int RackBytes = 4 + 4 + (8 + ArchetypeTuning.Count) * 4;
    /// <summary>ForgeOrdnance's per-call data limit (<c>OrdnanceApi.MaxDataBytes</c>).</summary>
    public const int MaxDataBytes = 192;

    public static byte[] Shell(in ShellRecord r)
    {
        var b = new byte[ShellBytes];
        b[0] = (byte)r.Look;
        b[1] = (byte)r.Kind;
        Put(b.AsSpan(2), 0, r.X);
        Put(b.AsSpan(2), 1, r.Y);
        Put(b.AsSpan(2), 2, r.Z);
        Put(b.AsSpan(2), 3, r.Top);
        Put(b.AsSpan(2), 4, r.Fall);
        Put(b.AsSpan(2), 5, r.Blast);
        return b;
    }

    public static bool TryShell(byte[] b, out ShellRecord r)
    {
        r = default;
        if (b.Length != ShellBytes || !Enum.IsDefined(typeof(ShellLook), b[0]) || !Enum.IsDefined(typeof(StrikeKind), b[1])) return false;
        var s = b.AsSpan(2);
        r = new ShellRecord((ShellLook)b[0], (StrikeKind)b[1], F(s, 0), F(s, 1), F(s, 2), F(s, 3), F(s, 4), F(s, 5));
        return (float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z) && float.IsFinite(r.Top) && float.IsFinite(r.Fall) && float.IsFinite(r.Blast)) && r.Fall >= 0f && r.Fall < 30f;
    }

    public static byte[] Zone(in ZoneRecord r)
    {
        var b = new byte[ZoneBytes];
        b[0] = (byte)r.Kind;
        Put(b.AsSpan(1), 0, r.X);
        Put(b.AsSpan(1), 1, r.Y);
        Put(b.AsSpan(1), 2, r.Z);
        Put(b.AsSpan(1), 3, r.Radius);
        Put(b.AsSpan(1), 4, r.Seconds);
        Put(b.AsSpan(1), 5, r.Slow);
        return b;
    }

    public static bool TryZone(byte[] b, out ZoneRecord r)
    {
        r = default;
        if (b.Length != ZoneBytes || !Enum.IsDefined(typeof(StrikeKind), b[0])) return false;
        var s = b.AsSpan(1);
        r = new ZoneRecord((StrikeKind)b[0], F(s, 0), F(s, 1), F(s, 2), F(s, 3), F(s, 4), F(s, 5));
        return (float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z) && float.IsFinite(r.Radius) && float.IsFinite(r.Seconds)) && r.Radius > 0f && r.Seconds > 0f && r.Seconds <= 600f
            && r.Slow > 0f && r.Slow <= 1f;
    }

    public static byte[] Beam(in BeamRecord r)
    {
        var b = new byte[BeamBytes];
        b[0] = r.HasTarget ? (byte)1 : (byte)0;
        Put(b.AsSpan(1), 0, r.OX);
        Put(b.AsSpan(1), 1, r.OY);
        Put(b.AsSpan(1), 2, r.OZ);
        Put(b.AsSpan(1), 3, r.TX);
        Put(b.AsSpan(1), 4, r.TY);
        Put(b.AsSpan(1), 5, r.TZ);
        r.Generation.TryWriteBytes(b.AsSpan(BeamBytes - 16));
        return b;
    }

    public static bool TryBeam(byte[] b, out BeamRecord r)
    {
        r = default;
        if (b.Length != BeamBytes || b[0] > 1) return false;
        var s = b.AsSpan(1);
        r = new BeamRecord(new Guid(b.AsSpan(BeamBytes - 16)), F(s, 0), F(s, 1), F(s, 2), F(s, 3), F(s, 4), F(s, 5), b[0] == 1);
        return r.Generation != Guid.Empty && (float.IsFinite(r.OX) && float.IsFinite(r.OY) && float.IsFinite(r.OZ) && float.IsFinite(r.TX) && float.IsFinite(r.TY) && float.IsFinite(r.TZ));
    }

    public static byte[] Rack(in RackRecord r)
    {
        var name = Utf8Prefix(r.Id, MaxDataBytes - RackBytes);
        var b = new byte[RackBytes + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), r.Spec.ModelHash);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), r.GearId);
        Put(b.AsSpan(8), 0, r.Spec.AmmoRatio);
        // A weapon without a grenade writes zeros; a grenade's radius is never zero.
        Put(b.AsSpan(8), 1, r.Spec.Grenade?.Damage ?? 0f);
        Put(b.AsSpan(8), 2, r.Spec.Grenade?.Radius ?? 0f);
        Put(b.AsSpan(8), 3, r.Spec.Grenade?.Speed ?? 0f);
        Put(b.AsSpan(8), 4, r.X);
        Put(b.AsSpan(8), 5, r.Y);
        Put(b.AsSpan(8), 6, r.Z);
        Put(b.AsSpan(8), 7, r.Yaw);
        for (var i = 0; i < ArchetypeTuning.Count; i++) Put(b.AsSpan(8), 8 + i, r.Spec.Tuning[i]);
        name.CopyTo(b, RackBytes);
        return b;
    }

    public static bool TryRack(byte[] b, out RackRecord r)
    {
        r = default;
        if (b.Length < RackBytes || b.Length > MaxDataBytes) return false;
        var s = b.AsSpan(8);
        float x = F(s, 4), y = F(s, 5), z = F(s, 6), yaw = F(s, 7);
        if (!(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(yaw))) return false;
        // A model hash this install never accepted is not a weapon it can show: the whole record is dropped (the caller writes the error).
        if (!HeavyModels.TryId(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4)), out var model)) return false;
        var gearId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4, 4));
        float damage = F(s, 1), radius = F(s, 2), speed = F(s, 3);
        var grenade = damage == 0f && radius == 0f && speed == 0f ? null : new GrenadeSpec(damage, radius, speed);
        var spec = HeavyWeaponSpec.Restore(model, gearId, F(s, 0), grenade, ArchetypeTuning.FromWire(Tuned(s)));
        if (spec == null) return false;
        string name;
        try { name = new UTF8Encoding(false, true).GetString(b, RackBytes, b.Length - RackBytes); }
        catch (ArgumentException) { return false; }
        r = new RackRecord(spec, gearId, x, y, z, yaw, name);
        return true;
    }

    /// <summary>True when <paramref name="b"/> is a rack record whose model hash this install does not know.</summary>
    public static bool UnknownRackModel(byte[] b, out uint hash)
    {
        hash = 0;
        if (b.Length < RackBytes || b.Length > MaxDataBytes) return false;
        hash = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0, 4));
        return !HeavyModels.TryId(hash, out _);
    }

    /// <summary>The longest whole-character UTF-8 prefix of <paramref name="text"/> within <paramref name="limit"/> bytes.</summary>
    private static byte[] Utf8Prefix(string text, int limit)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= limit) return bytes;
        var end = limit;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80) end--;
        return bytes.AsSpan(0, end).ToArray();
    }

    private static float[] Tuned(ReadOnlySpan<byte> s)
    {
        var values = new float[ArchetypeTuning.Count];
        for (var i = 0; i < values.Length; i++) values[i] = F(s, 8 + i);
        return values;
    }

    private static void Put(Span<byte> into, int index, float value)
        => BinaryPrimitives.WriteSingleLittleEndian(into.Slice(index * 4, 4), value);

    private static float F(ReadOnlySpan<byte> s, int index) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(index * 4, 4));

}

/// <summary>Client to host: what became of the loan a rack's claim started (the claim itself is ForgeOrdnance's). Event <see cref="Name"/>,
/// fixed size, no references.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LoanNotice
{
    public const string Name = "Loan";
    public ulong Summoner;
    public uint ThrowId;
    /// <summary>The claim's attempt number; the notice must match the committed loan.</summary>
    public uint Attempt;
    public LoanAction Action;
}

/// <summary><see cref="Returned"/>: the borrower gave the heavy weapon back. <see cref="Stashed"/>: the borrower is switching to their own special
/// weapon; the loan stays committed.</summary>
public enum LoanAction : byte { Returned, Stashed }

/// <summary>A lent launcher fired a grenade. Event <see cref="Name"/>: the shooter sends it to the host, the host checks the
/// committed loan, held gear, origin, cadence, sequence and ammunition, then sends canonical parameters to every other peer. Every peer flies the grenade the same way; the host's flight decides the burst
/// and its damage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GrenadeShot
{
    public const string Name = "Grenade";
    public ulong Shooter;
    public Guid Loan;
    public uint Sequence;
    public float OX, OY, OZ;
    /// <summary>Client request: unit aim direction. Host broadcast: authoritative velocity.</summary>
    public float VX, VY, VZ;
    /// <summary>Host output only; client-supplied values are ignored.</summary>
    public float Damage, Radius;
}

/// <summary>Host-authoritative grenade settlement, including for the predicting shooter. Fixed 40-byte layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GrenadeBurst
{
    public const string Name = "GrenadeBurst";
    public ulong Shooter;
    public Guid Loan;
    public uint Sequence;
    public float X, Y, Z;
}
