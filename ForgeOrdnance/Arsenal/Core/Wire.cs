using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeOrdnance.Arsenal;

/// <summary>Tags of the per-call data Arsenal sends through the carrier's <c>OrdnanceApi.SendData</c> (host to every
/// peer). Each record has a fixed little-endian layout (the rack's name trails it); <see cref="Wire"/> writes and reads it.
/// <see cref="Zone"/>, <see cref="Beam"/> and <see cref="Rack"/> are sent with <c>SendState</c>, so a player who joins later
/// gets the latest one; <see cref="ZoneEnd"/> and <see cref="BeamEnd"/> end them (and clear the kept state). The claim of a rack
/// (reservation, commit, cancel) is the carrier's own message, not a tag of Arsenal's.</summary>
internal enum DataTag : byte
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
    /// <summary>The second rack of a pod holding two weapons (same record as <see cref="Rack"/>); tag is <c>Rack</c> plus the bay.</summary>
    RackBay1 = 8,
    /// <summary>The host cannot retain this rack; remove it without granting a weapon (empty payload).</summary>
    RackUnavailable = 10,
    /// <summary>A mine device's field of mines (<see cref="MineWire"/>); sent with <c>SendState</c>, so a player who joins later gets the latest.</summary>
    Mines = 20,
    /// <summary>A strafing run starts: origin, direction, length, speed and lead seconds.</summary>
    Strafe = 11,
    /// <summary>The hunter's laser: its phase, where it points and how long until the shell (<see cref="LaserRecord"/>).</summary>
    Laser = 21,
    /// <summary>The beacon landed: the ground this call can hurt and for how long (<see cref="DangerRecord"/>).</summary>
    Danger = 22,
}

/// <summary>What the hunter's laser is doing: sweeping for a target, on one, released (the shell is coming) or giving up with no target.</summary>
internal enum LaserPhase : byte { Search = 0, Lock = 1, Fire = 2, Abort = 3 }

internal readonly struct LaserRecord
{
    public LaserRecord(LaserPhase phase, float x, float y, float z, float floor, float top, float velocityX, float velocityZ, float left)
    {
        Phase = phase; X = x; Y = y; Z = z; Floor = floor; Top = top; VelocityX = velocityX; VelocityZ = velocityZ; Left = left;
    }
    public readonly LaserPhase Phase;
    public readonly float X, Y, Z, Floor, Top, VelocityX, VelocityZ;
    /// <summary>Seconds until the shell drops (<see cref="LaserPhase.Lock"/>) or lands (<see cref="LaserPhase.Fire"/>).</summary>
    public readonly float Left;
}

internal readonly struct RackRecord
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

internal readonly struct ShellRecord
{
    public ShellRecord(ShellLook look, StrikeKind kind, float x, float y, float z, float top, float fall, float blast)
    {
        Look = look; Kind = kind; X = x; Y = y; Z = z; Top = top; Fall = fall; Blast = blast;
    }
    public readonly ShellLook Look;
    public readonly StrikeKind Kind;
    public readonly float X, Y, Z, Top, Fall, Blast;
}

internal readonly struct ZoneRecord
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

internal readonly struct StrafeRecord
{
    public StrafeRecord(float x, float y, float z, float dirX, float dirZ, float length, float speed, float lead, float halfWidth)
    {
        X = x; Y = y; Z = z; DirX = dirX; DirZ = dirZ; Length = length; Speed = speed; Lead = lead; HalfWidth = halfWidth;
    }
    public readonly float X, Y, Z, DirX, DirZ, Length, Speed, Lead, HalfWidth;
}

/// <summary>A call's <see cref="StrikeFootprint"/> at its beacon, facing the way its strip runs.</summary>
internal readonly struct DangerRecord
{
    public DangerRecord(StrikeKind kind, float x, float y, float z, float dirX, float dirZ, StrikeFootprint footprint)
    {
        Kind = kind; X = x; Y = y; Z = z; DirX = dirX; DirZ = dirZ; Footprint = footprint;
    }
    public readonly StrikeKind Kind;
    public readonly float X, Y, Z, DirX, DirZ;
    public readonly StrikeFootprint Footprint;

    /// <summary>True for a point on the ground this call can hurt: inside the disc or strip, on about the beacon's floor.</summary>
    public bool Covers(float x, float y, float z)
    {
        if (MathF.Abs(y - Y) > 4f) return false;
        float dx = x - X, dz = z - Z;
        if (Footprint.Radius > 0f) return dx * dx + dz * dz <= Footprint.Radius * Footprint.Radius;
        var along = dx * DirX + dz * DirZ;
        var across = dx * DirZ - dz * DirX;
        return along >= Footprint.Back && along <= Footprint.Front && MathF.Abs(across) <= Footprint.HalfWidth;
    }
}

internal readonly struct BeamRecord
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

internal static class Wire
{
    public const int ShellBytes = 2 + 6 * 4;
    public const int ZoneBytes = 1 + 6 * 4;
    public const int StrafeBytes = 9 * 4;
    public const int DangerBytes = 1 + 10 * 4;
    public const int LaserBytes = 1 + 8 * 4;
    public const int BeamBytes = 16 + 1 + 6 * 4;
    /// <summary>The fixed part of a rack record; the Id's UTF-8 bytes follow, cut to fit <see cref="MaxDataBytes"/>.</summary>
    public const int RackBytes = 4 + 4 + (8 + ArchetypeTuning.Count + 5) * 4;
    /// <summary>The carrier's per-call data limit.</summary>
    public const int MaxDataBytes = Messages.MaxPayloadData;

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

    public static byte[] Strafe(in StrafeRecord r)
    {
        var b = new byte[StrafeBytes];
        Put(b, 0, r.X); Put(b, 1, r.Y); Put(b, 2, r.Z); Put(b, 3, r.DirX); Put(b, 4, r.DirZ); Put(b, 5, r.Length); Put(b, 6, r.Speed); Put(b, 7, r.Lead); Put(b, 8, r.HalfWidth);
        return b;
    }

    public static byte[] Danger(in DangerRecord r)
    {
        var b = new byte[DangerBytes];
        b[0] = (byte)r.Kind;
        var s = b.AsSpan(1);
        var f = r.Footprint;
        Put(s, 0, r.X); Put(s, 1, r.Y); Put(s, 2, r.Z); Put(s, 3, r.DirX); Put(s, 4, r.DirZ);
        Put(s, 5, f.Radius); Put(s, 6, f.Back); Put(s, 7, f.Front); Put(s, 8, f.HalfWidth); Put(s, 9, f.Seconds);
        return b;
    }

    public static bool TryDanger(byte[] b, out DangerRecord r)
    {
        r = default;
        if (b.Length != DangerBytes || !Enum.IsDefined(typeof(StrikeKind), b[0])) return false;
        var s = b.AsSpan(1);
        var f = new StrikeFootprint(F(s, 5), F(s, 6), F(s, 7), F(s, 8), F(s, 9));
        r = new DangerRecord((StrikeKind)b[0], F(s, 0), F(s, 1), F(s, 2), F(s, 3), F(s, 4), f);
        var length = r.DirX * r.DirX + r.DirZ * r.DirZ;
        return float.IsFinite(r.X + r.Y + r.Z + f.Radius + f.Back + f.Front + f.HalfWidth + f.Seconds) && length > 0.9f && length < 1.1f
            && f.Seconds > 0f && f.Seconds <= 120f && f.Radius >= 0f && f.HalfWidth >= 0f && (f.Radius > 0f || (f.Front > f.Back && f.HalfWidth > 0f));
    }

    public static bool TryStrafe(byte[] b, out StrafeRecord r)
    {
        r = default;
        if (b.Length != StrafeBytes) return false;
        r = new StrafeRecord(F(b, 0), F(b, 1), F(b, 2), F(b, 3), F(b, 4), F(b, 5), F(b, 6), F(b, 7), F(b, 8));
        var length = r.DirX * r.DirX + r.DirZ * r.DirZ;
        return float.IsFinite(r.X + r.Y + r.Z + r.Length + r.Speed + r.Lead + r.HalfWidth) && length > 0.9f && length < 1.1f && r.HalfWidth >= 0f
            && r.Length > 0f && r.Length <= 100f && r.Speed >= 5f && r.Speed <= 80f && r.Lead >= 0f && r.Lead <= 10f;
    }

    public static byte[] Laser(in LaserRecord r)
    {
        var b = new byte[LaserBytes];
        b[0] = (byte)r.Phase;
        var s = b.AsSpan(1);
        Put(s, 0, r.X); Put(s, 1, r.Y); Put(s, 2, r.Z); Put(s, 3, r.Floor); Put(s, 4, r.Top); Put(s, 5, r.VelocityX); Put(s, 6, r.VelocityZ); Put(s, 7, r.Left);
        return b;
    }

    public static bool TryLaser(byte[] b, out LaserRecord r)
    {
        r = default;
        if (b.Length != LaserBytes || !Enum.IsDefined(typeof(LaserPhase), b[0])) return false;
        var s = b.AsSpan(1);
        r = new LaserRecord((LaserPhase)b[0], F(s, 0), F(s, 1), F(s, 2), F(s, 3), F(s, 4), F(s, 5), F(s, 6), F(s, 7));
        return float.IsFinite(r.X + r.Y + r.Z + r.Floor + r.Top + r.VelocityX + r.VelocityZ + r.Left) && r.Left >= 0f && r.Left < 30f;
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
        // The true-damage blast writes its edge share (0 to 1); every other weapon writes -1.
        Put(b.AsSpan(8), 8 + ArchetypeTuning.Count, r.Spec.Grenade is { TrueDamage: true } g ? g.EdgeShare : -1f);
        Put(b.AsSpan(8), 9 + ArchetypeTuning.Count, r.Spec.Homing?.Angle ?? 0f);
        Put(b.AsSpan(8), 10 + ArchetypeTuning.Count, r.Spec.Homing?.Range ?? 0f);
        Put(b.AsSpan(8), 11 + ArchetypeTuning.Count, r.Spec.RecoilScale);
        // How many racks the call laid down: a peer that joins later spaces a pod-less (Direct) row the way the first ones did.
        Put(b.AsSpan(8), 12 + ArchetypeTuning.Count, r.Spec.Count);
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
        var grenade = damage == 0f && radius == 0f && speed == 0f ? null : new GrenadeSpec(damage, radius, speed, F(s, 8 + ArchetypeTuning.Count) >= 0f, F(s, 8 + ArchetypeTuning.Count) >= 0f ? F(s, 8 + ArchetypeTuning.Count) : 0.5f);
        float angle = F(s, 9 + ArchetypeTuning.Count), reach = F(s, 10 + ArchetypeTuning.Count);
        var homing = angle == 0f && reach == 0f ? null : new HomingSpec(angle, reach);
        var spec = HeavyWeaponSpec.Restore(model, gearId, F(s, 0), grenade, ArchetypeTuning.FromWire(Tuned(s)), homing, F(s, 11 + ArchetypeTuning.Count), F(s, 12 + ArchetypeTuning.Count));
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

/// <summary>Client to host: what became of the loan a rack's claim started (the claim itself is the carrier's). Event <see cref="Name"/>,
/// fixed size, no references.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LoanNotice
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
internal enum LoanAction : byte { Returned, Stashed }

/// <summary>A lent launcher fired a grenade. Event <see cref="Name"/>: the shooter sends it to the host, the host checks the
/// committed loan, held gear, origin, cadence, sequence and ammunition, then sends canonical parameters to every other peer. Every peer flies the grenade the same way; the host's flight decides the burst
/// and its damage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct GrenadeShot
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
    /// <summary>Host output only: the edge share of a true-damage blast, or -1 for the game's explosion.</summary>
    public float TrueEdge;
}

/// <summary>A tracking rifle's shot, for the tracer only: the shooter's muzzle and the point the shot found its target at. The shooter sends
/// it to the host, the host checks the sender and sends it to every other peer; no damage is decided by it (the shooter's own game
/// settles the hit as for any bullet). Event <see cref="Name"/>, fixed size, no references.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HomingShot
{
    public const string Name = "Homing";
    /// <summary>Longest tracer the host passes on (metres).</summary>
    public const float MaxReach = 150f;
    public ulong Shooter;
    public float OX, OY, OZ, TX, TY, TZ;
    /// <summary>Picks which side the tracer bends out to, the same on every peer.</summary>
    public uint Seed;
}

/// <summary>Host-authoritative grenade settlement, including for the predicting shooter. Fixed 41-byte layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct GrenadeBurst
{
    public const string Name = "GrenadeBurst";
    public ulong Shooter;
    public Guid Loan;
    public uint Sequence;
    public float X, Y, Z;
    /// <summary>1 for a rocket's blast (a true-damage round), 0 for a grenade's: a peer that never saw the round fly (it joined later, or the flight timed out here) shows the right one.</summary>
    public byte Rocket;
}
