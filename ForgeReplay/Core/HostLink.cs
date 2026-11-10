using System;
using ForgeCore;

namespace ForgeReplay;

/// <summary>What a hit the host settled looks like on the wire and in a client's recording (PLAN 5.7 host records). <see cref="Key"/> is the enemy's
/// replicated id (<c>EnemyAgent.GlobalID</c>, the same number on every machine), not a recording entity number: the receiver puts it to its own entity.</summary>
internal readonly record struct HostHit(ushort Key, int Kind, int Slot, int Limb, bool Lethal, bool Behind, uint TakenTenths, uint Gear, uint DistanceCm, uint AgeMs);

/// <summary>A wake-up the host named the cause of. <see cref="Detection"/> is the game's target detection type (7: none), <see cref="Player"/> the slot
/// plus one of who it woke for (0 none), <see cref="PropagatorKey"/> the replicated id of the enemy that passed the wake-up on (<see cref="HasPropagator"/>).</summary>
internal readonly record struct HostWake(ushort Key, bool Propagated, bool Heard, int Detection, int Player, int Action, bool HasPropagator, ushort PropagatorKey, uint AgeMs);

/// <summary>A hit the host settled on a player, with the player's slot, the damage kind (0 bullet, 1 melee, 2 explosion, 3 tongue, 4 projectile, 5 fall,
/// 6 anything else), the attacking player's slot (-1 none) and the attacking enemy's replicated id (0 none).</summary>
internal readonly record struct HostHurt(int Slot, int Kind, int FromPlayer, ushort EnemyKey, uint DamageTenths, uint AgeMs);

/// <summary>A resource handed over on the host's machine: who gave, who received and the kind (0 health, 1 ammunition, 2 disinfection).</summary>
internal readonly record struct HostSupply(int Giver, int Receiver, int Kind, uint AgeMs);

/// <summary>A resource pack applied on the host's machine (its own player or a bot).</summary>
internal readonly record struct HostPack(int Slot, uint AgeMs);

/// <summary>A shot fired on the host's machine (its own player or a bot): <see cref="Hit"/> when its ray ended on an enemy limb.</summary>
internal readonly record struct HostRay(int Slot, bool Hit, uint AgeMs);

/// <summary>One player's cumulative host-settled numbers, as the snapshot carries them (damage in tenths). <see cref="Packs"/> and <see cref="Supplied"/>
/// are the extended snapshot's (mode 2); a recording of the older snapshot (mode 1) has them 0 and they are not levelled.</summary>
internal struct HostTotals
{
    internal uint BulletTenths, MeleeTenths, ExplosionTenths, Kills, Landed, Weak, Backstabs, Woken;
    internal uint Packs, Supplied;
}

/// <summary>The host-to-client records (PLAN 5.7): the wire layout of a packet and the events a client writes into its own recording. Core, no game
/// reference. A packet is one header (type byte, sequence, host clock in ms, 4 bytes each, little endian) and records until its end; every number inside a
/// record is a <see cref="Compact"/> variable-length integer. A packet is refused as a whole when anything in it is cut off or out of range.</summary>
internal static class HostLink
{
    internal const byte DataPacket = 0x48;
    internal const int HeaderBytes = 9, MaxPacketBytes = 1200, RecordRoom = 64;
    internal const int RecordHit = 1, RecordWake = 2, RecordSnapshot = 3, RecordHurt = 4, RecordSupply = 5, RecordPack = 6, RecordRay = 7;
    /// <summary>Beyond these a record is not a real one: ten thousand health in a hit, a million tenths on a player, ten minutes between the record and its packet.</summary>
    internal const uint MaxTakenTenths = 100000, MaxPlayerTenths = 1000000, MaxAgeMs = 600000, MaxDistanceCm = 100000, MaxCount = 1000000, MaxDamageTenths = 100000000;
    internal const int MaxHurtKind = 6, MaxSupplyKind = 2;

    /// <summary>What the handshake's content holds, so two builds of different protocols refuse each other: the recording format and this protocol's own version.</summary>
    internal static string Content(int recordingFormat) => "frpl-" + recordingFormat + "/host-records-2";

    // ---- Packet writer -------------------------------------------------------------------------------------------------------------

    /// <summary>Builds the next packet in one preallocated buffer; <see cref="Seal"/> copies it out exactly sized. Main thread only.</summary>
    internal sealed class Writer
    {
        private readonly byte[] _buffer = new byte[MaxPacketBytes];
        private int _length = HeaderBytes;

        internal bool Empty => _length == HeaderBytes;

        /// <summary>No room for another record: send now.</summary>
        internal bool Full => _length > MaxPacketBytes - RecordRoom;

        internal int Length => _length;

        internal void Reset() => _length = HeaderBytes;

        internal void Hit(in HostHit hit)
        {
            _buffer[_length++] = RecordHit;
            _buffer[_length++] = (byte)(hit.Kind & 3 | (hit.Slot & 3) << 2 | (hit.Limb & 3) << 4 | (hit.Lethal ? 64 : 0) | (hit.Behind ? 128 : 0));
            _buffer[_length++] = (byte)hit.Key; _buffer[_length++] = (byte)(hit.Key >> 8);
            Compact.WriteVar(_buffer, ref _length, hit.TakenTenths);
            Compact.WriteVar(_buffer, ref _length, hit.AgeMs);
            Compact.WriteVar(_buffer, ref _length, hit.Gear);
            Compact.WriteVar(_buffer, ref _length, hit.DistanceCm);
        }

        internal void Wake(in HostWake wake)
        {
            _buffer[_length++] = RecordWake;
            _buffer[_length++] = (byte)(wake.Key); _buffer[_length++] = (byte)(wake.Key >> 8);
            _buffer[_length++] = (byte)((wake.Propagated ? 1 : 0) | (wake.Heard ? 2 : 0) | (wake.Detection & 7) << 2 | (wake.HasPropagator ? 32 : 0));
            _buffer[_length++] = (byte)((wake.Player & 7) | (wake.Action & 15) << 3);
            if (wake.HasPropagator) { _buffer[_length++] = (byte)wake.PropagatorKey; _buffer[_length++] = (byte)(wake.PropagatorKey >> 8); }
            Compact.WriteVar(_buffer, ref _length, wake.AgeMs);
        }

        internal void Hurt(in HostHurt hurt)
        {
            _buffer[_length++] = RecordHurt;
            _buffer[_length++] = (byte)(hurt.Slot & 3);
            _buffer[_length++] = (byte)((hurt.Kind & 7) | ((hurt.FromPlayer + 1 & 7) << 3));
            _buffer[_length++] = (byte)hurt.EnemyKey; _buffer[_length++] = (byte)(hurt.EnemyKey >> 8);
            Compact.WriteVar(_buffer, ref _length, hurt.DamageTenths);
            Compact.WriteVar(_buffer, ref _length, hurt.AgeMs);
        }

        internal void Supply(in HostSupply supply)
        {
            _buffer[_length++] = RecordSupply;
            _buffer[_length++] = (byte)(supply.Giver & 3);
            _buffer[_length++] = (byte)(supply.Receiver & 3);
            _buffer[_length++] = (byte)(supply.Kind & 3);
            Compact.WriteVar(_buffer, ref _length, supply.AgeMs);
        }

        internal void Pack(in HostPack pack)
        {
            _buffer[_length++] = RecordPack;
            _buffer[_length++] = (byte)(pack.Slot & 3);
            Compact.WriteVar(_buffer, ref _length, pack.AgeMs);
        }

        internal void Ray(in HostRay ray)
        {
            _buffer[_length++] = RecordRay;
            _buffer[_length++] = (byte)(ray.Slot & 3 | (ray.Hit ? 4 : 0));
            Compact.WriteVar(_buffer, ref _length, ray.AgeMs);
        }

        /// <summary>A whole snapshot packet: the totals of the players in <paramref name="mask"/> (bit per slot). Meant to be the only record of its packet.</summary>
        internal void Snapshot(int mask, ReadOnlySpan<HostTotals> totals)
        {
            _buffer[_length++] = RecordSnapshot;
            _buffer[_length++] = (byte)(mask & 15);
            for (var slot = 0; slot < 4; slot++)
            {
                if ((mask >> slot & 1) == 0) continue;
                var t = totals[slot];
                Compact.WriteVar(_buffer, ref _length, t.BulletTenths); Compact.WriteVar(_buffer, ref _length, t.MeleeTenths); Compact.WriteVar(_buffer, ref _length, t.ExplosionTenths);
                Compact.WriteVar(_buffer, ref _length, t.Kills); Compact.WriteVar(_buffer, ref _length, t.Landed); Compact.WriteVar(_buffer, ref _length, t.Weak);
                Compact.WriteVar(_buffer, ref _length, t.Backstabs); Compact.WriteVar(_buffer, ref _length, t.Woken);
                Compact.WriteVar(_buffer, ref _length, t.Packs); Compact.WriteVar(_buffer, ref _length, t.Supplied);
            }
        }

        /// <summary>The finished packet (header filled in) as an array of exactly its length, and the buffer starts again. One array per packet sent, at most ten a second.</summary>
        internal byte[] Seal(uint sequence, uint hostMs)
        {
            _buffer[0] = DataPacket;
            WriteU32(_buffer, 1, sequence); WriteU32(_buffer, 5, hostMs);
            var packet = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, packet, 0, _length);
            _length = HeaderBytes;
            return packet;
        }
    }

    internal static void WriteU32(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)value; buffer[at + 1] = (byte)(value >> 8); buffer[at + 2] = (byte)(value >> 16); buffer[at + 3] = (byte)(value >> 24);
    }

    internal static uint ReadU32(byte[] buffer, int at) => buffer[at] | (uint)buffer[at + 1] << 8 | (uint)buffer[at + 2] << 16 | (uint)buffer[at + 3] << 24;

    // ---- Packet reader -------------------------------------------------------------------------------------------------------------

    /// <summary>What a receiver does with a packet's records, in order. Nothing is called for a packet that fails <see cref="Read"/>'s checks as a whole: every
    /// record is checked before the first one is handed out.</summary>
    internal interface IRecords
    {
        void OnHit(in HostHit hit);
        void OnWake(in HostWake wake);
        void OnSnapshot(int mask, ReadOnlySpan<HostTotals> totals);
        void OnHurt(in HostHurt hurt);
        void OnSupply(in HostSupply supply);
        void OnPack(in HostPack pack);
        void OnRay(in HostRay ray);
    }

    /// <summary>Checks <paramref name="data"/> from start to end and, when it is sound, hands every record to <paramref name="into"/>. Returns false (and hands out
    /// nothing) for a packet that is too short or too long, of another type, or has a record that is cut off or out of range; <paramref name="sequence"/> is the
    /// header's sequence when the header was readable.</summary>
    internal static bool Read(byte[]? data, IRecords into, out uint sequence, out uint hostMs)
    {
        sequence = 0; hostMs = 0;
        if (data == null || data.Length <= HeaderBytes || data.Length > MaxPacketBytes || data[0] != DataPacket) return false;
        sequence = ReadU32(data, 1); hostMs = ReadU32(data, 5);
        uint span = 0;
        if (!Walk(data, null, ref span)) return false;
        Walk(data, into, ref span);
        return true;
    }

    // One pass over the records: with no receiver it only checks them and measures the span (the largest offset a record carries); with one, a record is handed
    // out with its age, the span minus its offset: how long before the packet's last record it happened.
    private static bool Walk(byte[] data, IRecords? into, ref uint span)
    {
        var at = HeaderBytes;
        Span<HostTotals> totals = stackalloc HostTotals[4];
        try
        {
            while (at < data.Length)
            {
                var type = data[at++];
                switch (type)
                {
                    case RecordHit:
                        {
                            if (at + 3 > data.Length) return false;
                            var b = data[at++];
                            var key = (ushort)(data[at] | data[at + 1] << 8); at += 2;
                            var taken = Compact.ReadVar(data, ref at); var age = Compact.ReadVar(data, ref at); var gear = Compact.ReadVar(data, ref at); var distance = Compact.ReadVar(data, ref at);
                            if (taken > MaxTakenTenths || age > MaxAgeMs || gear > uint.MaxValue || distance > MaxDistanceCm) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            var limb = b >> 4 & 3;
                            if (limb == 3) return false;
                            into?.OnHit(new HostHit(key, b & 3, b >> 2 & 3, limb, (b & 64) != 0, (b & 128) != 0, (uint)taken, (uint)gear, (uint)distance, span - (uint)age));
                            break;
                        }
                    case RecordWake:
                        {
                            if (at + 4 > data.Length) return false;
                            var key = (ushort)(data[at] | data[at + 1] << 8); at += 2;
                            var flags = data[at++]; var more = data[at++];
                            var has = (flags & 32) != 0;
                            ushort propagator = 0;
                            if (has) { if (at + 2 > data.Length) return false; propagator = (ushort)(data[at] | data[at + 1] << 8); at += 2; }
                            var age = Compact.ReadVar(data, ref at);
                            var player = more & 7;
                            if (age > MaxAgeMs || player > 4 || (flags & 0xC0) != 0) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            into?.OnWake(new HostWake(key, (flags & 1) != 0, (flags & 2) != 0, flags >> 2 & 7, player, more >> 3 & 15, has, propagator, span - (uint)age));
                            break;
                        }
                    case RecordSnapshot:
                        {
                            if (at >= data.Length) return false;
                            var mask = data[at++];
                            if ((mask & ~15) != 0) return false;
                            totals.Clear();
                            for (var slot = 0; slot < 4; slot++)
                            {
                                if ((mask >> slot & 1) == 0) continue;
                                totals[slot] = new HostTotals
                                {
                                    BulletTenths = Damage(data, ref at),
                                    MeleeTenths = Damage(data, ref at),
                                    ExplosionTenths = Damage(data, ref at),
                                    Kills = Count(data, ref at),
                                    Landed = Count(data, ref at),
                                    Weak = Count(data, ref at),
                                    Backstabs = Count(data, ref at),
                                    Woken = Count(data, ref at),
                                    Packs = Count(data, ref at),
                                    Supplied = Count(data, ref at),
                                };
                            }
                            into?.OnSnapshot(mask, totals);
                            break;
                        }
                    case RecordHurt:
                        {
                            if (at + 4 > data.Length) return false;
                            var slot = data[at++] & 3;
                            var flags = data[at++];
                            var key = (ushort)(data[at] | data[at + 1] << 8); at += 2;
                            var kind = flags & 7;
                            var fromPlayer = (flags >> 3 & 7) - 1;
                            var damage = Compact.ReadVar(data, ref at); var age = Compact.ReadVar(data, ref at);
                            if (kind > MaxHurtKind || fromPlayer > 3 || damage > MaxPlayerTenths || age > MaxAgeMs || (flags & 0xC0) != 0) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            into?.OnHurt(new HostHurt(slot, kind, fromPlayer, key, (uint)damage, span - (uint)age));
                            break;
                        }
                    case RecordSupply:
                        {
                            if (at + 3 > data.Length) return false;
                            var giver = data[at++] & 3;
                            var receiver = data[at++] & 3;
                            var kind = data[at++];
                            var age = Compact.ReadVar(data, ref at);
                            if (kind > MaxSupplyKind || age > MaxAgeMs) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            into?.OnSupply(new HostSupply(giver, receiver, kind, span - (uint)age));
                            break;
                        }
                    case RecordPack:
                        {
                            if (at >= data.Length) return false;
                            var slot = data[at++] & 3;
                            var age = Compact.ReadVar(data, ref at);
                            if (age > MaxAgeMs) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            into?.OnPack(new HostPack(slot, span - (uint)age));
                            break;
                        }
                    case RecordRay:
                        {
                            if (at >= data.Length) return false;
                            var flags = data[at++];
                            var age = Compact.ReadVar(data, ref at);
                            if (age > MaxAgeMs || (flags & 0xF8) != 0) return false;
                            if (into == null) span = Math.Max(span, (uint)age);
                            into?.OnRay(new HostRay(flags & 3, (flags & 4) != 0, span - (uint)age));
                            break;
                        }
                    default: return false;
                }
            }
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static uint Damage(byte[] data, ref int at)
    {
        var value = Compact.ReadVar(data, ref at);
        if (value > MaxDamageTenths) throw new FormatException("A damage total is out of range.");
        return (uint)value;
    }

    private static uint Count(byte[] data, ref int at)
    {
        var value = Compact.ReadVar(data, ref at);
        if (value > MaxCount) throw new FormatException("A count is out of range.");
        return (uint)value;
    }

    // ---- Events in a client's recording (kinds 104 to 111) -----------------------------------------------------------------------

    /// <summary>104 HostHit: U entity, U kind, U slot, U taken x10, U limb type, U flags (1 lethal, 2 behind), U gear category, U distance cm, U age ms (how long before
    /// this event the host settled the hit). The numbers of an EnemyHurt the host settled, as the host recorded them.</summary>
    internal static void WriteHit(PayloadWriter w, int entity, in HostHit h)
        => w.U((ulong)entity).U((ulong)h.Kind).U((ulong)h.Slot).U(h.TakenTenths).U((ulong)h.Limb).U((h.Lethal ? 1UL : 0UL) | (h.Behind ? 2UL : 0UL)).U(h.Gear).U(h.DistanceCm).U(h.AgeMs);

    /// <summary>105 HostWake: U entity, U propagated, U detection type (7 none), U heard, U player + 1 (0 none), U action, U propagator entity + 1 (0 none), U age ms.</summary>
    internal static void WriteWake(PayloadWriter w, int entity, int propagatorEntity, in HostWake k)
        => w.U((ulong)entity).U(k.Propagated ? 1UL : 0UL).U((ulong)k.Detection).U(k.Heard ? 1UL : 0UL).U((ulong)k.Player).U((ulong)k.Action).U(propagatorEntity < 0 ? 0UL : (ulong)propagatorEntity + 1).U(k.AgeMs);

    /// <summary>106 HostLink: U mode (2 the host's records are in force from here, with the totals that follow; 1 the same totals of a recording before
    /// packs and supplies; 0 they ended: the host left or has no records), then for a live mode U slot mask and, per slot in the mask, U bullet, melee and
    /// explosion damage x10, kills, landed, weak, backstabs and woken, then for mode 2 U packs and U supplies received: what the host has settled so far.</summary>
    internal static void WriteLink(PayloadWriter w, bool live, int mask, ReadOnlySpan<HostTotals> totals)
    {
        w.U(live ? 2UL : 0UL);
        if (!live) return;
        w.U((ulong)(mask & 15));
        for (var slot = 0; slot < 4; slot++)
        {
            if ((mask >> slot & 1) == 0) continue;
            var t = totals[slot];
            w.U(t.BulletTenths).U(t.MeleeTenths).U(t.ExplosionTenths).U(t.Kills).U(t.Landed).U(t.Weak).U(t.Backstabs).U(t.Woken).U(t.Packs).U(t.Supplied);
        }
    }

    /// <summary>The totals of a HostLink event, in slot order. <paramref name="extended"/> is true for mode 2, which carries packs and supplies; a mode 1
    /// event of an older recording has neither and those counters must stay as the recording counted them.</summary>
    internal static HostTotals[] ReadLink(PayloadReader read, out bool live, out int mask, out bool extended)
    {
        var mode = (int)read.U();
        live = mode is 1 or 2; extended = mode == 2; mask = 0;
        var totals = new HostTotals[4];
        if (!live) return totals;
        mask = (int)read.U() & 15;
        for (var slot = 0; slot < 4; slot++)
        {
            if ((mask >> slot & 1) == 0) continue;
            totals[slot] = new HostTotals
            {
                BulletTenths = (uint)read.U(),
                MeleeTenths = (uint)read.U(),
                ExplosionTenths = (uint)read.U(),
                Kills = (uint)read.U(),
                Landed = (uint)read.U(),
                Weak = (uint)read.U(),
                Backstabs = (uint)read.U(),
                Woken = (uint)read.U(),
                Packs = extended ? (uint)read.U() : 0,
                Supplied = extended ? (uint)read.U() : 0,
            };
        }
        return totals;
    }

    /// <summary>111 HostHurt: U slot, U kind (0 bullet, 1 melee, 2 explosion, 3 tongue, 4 projectile, 5 fall, 6 anything else), U damage x10, U the attacking
    /// player slot + 1 (0 none), U the attacking enemy entity + 1 as this recording numbers it (0 none), U age ms.</summary>
    internal static void WriteHurt(PayloadWriter w, in HostHurt h, int entity)
        => w.U((ulong)h.Slot).U((ulong)h.Kind).U(h.DamageTenths).U((ulong)(h.FromPlayer + 1)).U(entity < 0 ? 0UL : (ulong)entity + 1).U(h.AgeMs);

    /// <summary>109 HostSupply: U giver slot, U receiver slot, U kind (0 health, 1 ammunition, 2 disinfection), U age ms.</summary>
    internal static void WriteSupply(PayloadWriter w, in HostSupply s)
        => w.U((ulong)s.Giver).U((ulong)s.Receiver).U((ulong)s.Kind).U(s.AgeMs);

    /// <summary>110 HostPack: U slot, U age ms.</summary>
    internal static void WritePack(PayloadWriter w, in HostPack p)
        => w.U((ulong)p.Slot).U(p.AgeMs);

    /// <summary>112 HostRay: U slot, U 1 when the ray ended on an enemy limb, U age ms.</summary>
    internal static void WriteRay(PayloadWriter w, in HostRay r)
        => w.U((ulong)r.Slot).U(r.Hit ? 1UL : 0UL).U(r.AgeMs);
}
