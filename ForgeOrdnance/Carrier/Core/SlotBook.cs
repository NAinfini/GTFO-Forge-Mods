using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeOrdnance;

/// <summary>Cooldown and remaining uses of every slot of every player, plus each player's loadout. The host mutates it and
/// writes the changes into its tables; every other peer holds a mirror it updates from those rows, which is also what a new host
/// takes over. Times are the game clock in seconds. A slot outlives a loadout change, so swapping a call-in out and back in
/// cannot refresh its uses; the swap itself clears its cooldown.</summary>
internal sealed class SlotBook
{
    private sealed class Slot
    {
        internal double CooldownEnd;
        internal int Uses;
        internal int InitialUses;
    }

    private sealed class PlayerState
    {
        internal readonly List<uint> Loadout = new();
        internal readonly Dictionary<uint, Slot> Slots = new();
    }

    private readonly Dictionary<ulong, PlayerState> _players = new();
    internal ulong LoadoutVersion { get; private set; }

    public IEnumerable<ulong> Players => _players.Keys;

    public IReadOnlyList<uint> LoadoutOf(ulong player) => _players.TryGetValue(player, out var state) ? state.Loadout : Array.Empty<uint>();

    /// <summary>Host side: the player's accepted loadout. A call-in seen for the first time starts with its definition's uses
    /// times <paramref name="usesMultiplier"/>, rounded down; an unlimited one stays unlimited (uses
    /// <see cref="OrdnanceDefinition.UnlimitedUses"/>).</summary>
    public void SetLoadout(ulong player, IReadOnlyList<OrdnanceDefinition> definitions, double usesMultiplier)
    {
        LoadoutVersion++;
        var state = State(player);
        var before = state.Loadout.ToHashSet();
        var after = definitions.Select(d => d.Key).ToHashSet();
        // Swapping a call-in in or out clears its cooldown (the uses stay), so swapping back finds it ready.
        foreach (var (key, slot) in state.Slots)
            if (before.Contains(key) != after.Contains(key)) slot.CooldownEnd = 0;
        state.Loadout.Clear();
        foreach (var d in definitions)
        {
            state.Loadout.Add(d.Key);
            if (state.Slots.ContainsKey(d.Key)) continue;
            var uses = d.Unlimited ? OrdnanceDefinition.UnlimitedUses : (int)Math.Floor(d.Uses * usesMultiplier + 1e-9);
            state.Slots[d.Key] = new Slot { Uses = uses, InitialUses = uses };
        }
    }

    /// <summary>Mirror side: the loadout in the host's row. Slots arrive in their own rows.</summary>
    public void ApplyLoadout(ulong player, IReadOnlyList<uint> keys)
    {
        LoadoutVersion++;
        var state = State(player);
        state.Loadout.Clear();
        state.Loadout.AddRange(keys);
    }

    /// <summary>Mirror side: a slot as the host reports it.</summary>
    public void Apply(ulong player, uint key, int uses, int initialUses, double cooldownRemaining, double now)
    {
        var state = State(player);
        if (!state.Slots.TryGetValue(key, out var slot)) state.Slots[key] = slot = new Slot();
        slot.InitialUses = initialUses;
        slot.Uses = uses;
        slot.CooldownEnd = now + Math.Max(0, cooldownRemaining);
    }

    public Rejection Check(ulong player, uint key, double now)
    {
        if (!_players.TryGetValue(player, out var state) || !state.Loadout.Contains(key) || !state.Slots.TryGetValue(key, out var slot))
            return Rejection.NotInLoadout;
        if (slot.Uses == 0) return Rejection.NoUses;
        if (now < slot.CooldownEnd) return Rejection.Cooldown;
        return Rejection.None;
    }

    /// <summary>Spends one use and starts the cooldown. Throws when <see cref="Check"/> would refuse, so a caller cannot skip it.</summary>
    public void Consume(ulong player, uint key, double now, double cooldownSeconds)
    {
        var reason = Check(player, key, now);
        if (reason != Rejection.None) throw new InvalidOperationException("Cannot consume: " + reason + ".");
        var slot = _players[player].Slots[key];
        if (slot.Uses > 0) slot.Uses--;
        slot.CooldownEnd = now + cooldownSeconds;
    }

    /// <summary>Undoes a <see cref="Consume"/> whose call found nothing to do: the use comes back and at most <paramref name="cooldownSeconds"/> of the cooldown stay.</summary>
    public void Refund(ulong player, uint key, double now, double cooldownSeconds)
    {
        if (!_players.TryGetValue(player, out var state) || !state.Slots.TryGetValue(key, out var slot)) return;
        if (slot.Uses >= 0 && slot.Uses < slot.InitialUses) slot.Uses++;
        slot.CooldownEnd = Math.Min(slot.CooldownEnd, now + cooldownSeconds);
    }

    /// <summary>Remaining uses (negative: unlimited) and cooldown of one slot.</summary>
    public bool TryGet(ulong player, uint key, double now, out int uses, out double cooldownRemaining)
    {
        uses = 0; cooldownRemaining = 0;
        if (!_players.TryGetValue(player, out var state) || !state.Slots.TryGetValue(key, out var slot)) return false;
        uses = slot.Uses;
        cooldownRemaining = Math.Max(0, slot.CooldownEnd - now);
        return true;
    }

    /// <summary>The uses a slot started with (what <see cref="Refund"/> may restore to); 0 for a slot not known.</summary>
    public int InitialOf(ulong player, uint key) => _players.TryGetValue(player, out var state) && state.Slots.TryGetValue(key, out var slot) ? slot.InitialUses : 0;

    public void Clear() { _players.Clear(); LoadoutVersion++; }

    /// <summary>Remaining cooldowns are stored relative to <paramref name="now"/>, so a restore on a later clock keeps them.</summary>
    public SlotSnapshot Capture(double now)
    {
        var entries = new List<SlotSnapshot.Entry>();
        foreach (var (player, state) in _players)
            foreach (var (key, slot) in state.Slots)
                entries.Add(new SlotSnapshot.Entry(player, key, Math.Max(0, slot.CooldownEnd - now), slot.Uses));
        return new SlotSnapshot(entries);
    }

    /// <summary>Puts every slot back as it was when captured. A slot that did not exist then is unused again.</summary>
    public void Restore(SlotSnapshot snapshot, double now)
    {
        var restored = new HashSet<(ulong, uint)>();
        foreach (var e in snapshot.Entries)
        {
            var state = State(e.Player);
            if (!state.Slots.TryGetValue(e.Key, out var slot)) state.Slots[e.Key] = slot = new Slot { InitialUses = e.Uses };
            slot.Uses = e.Uses;
            slot.CooldownEnd = now + e.CooldownRemaining;
            restored.Add((e.Player, e.Key));
        }
        foreach (var (player, state) in _players)
            foreach (var (key, slot) in state.Slots)
                if (!restored.Contains((player, key))) { slot.Uses = slot.InitialUses; slot.CooldownEnd = 0; }
    }

    private PlayerState State(ulong player)
    {
        if (!_players.TryGetValue(player, out var state)) _players[player] = state = new PlayerState();
        return state;
    }
}

internal sealed class SlotSnapshot
{
    public readonly record struct Entry(ulong Player, uint Key, double CooldownRemaining, int Uses);

    private const int EntryBytes = 24;

    public SlotSnapshot(IReadOnlyList<Entry> entries) => Entries = entries;
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The snapshot as the host's checkpoint copy: the entry count, then per entry the player, the key, the uses and the frozen cooldown.</summary>
    public byte[] Encode()
    {
        var data = new byte[4 + Entries.Count * EntryBytes];
        BitConverter.TryWriteBytes(data.AsSpan(0), Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            var at = data.AsSpan(4 + i * EntryBytes);
            BitConverter.TryWriteBytes(at, Entries[i].Player);
            BitConverter.TryWriteBytes(at.Slice(8), Entries[i].Key);
            BitConverter.TryWriteBytes(at.Slice(12), Entries[i].Uses);
            BitConverter.TryWriteBytes(at.Slice(16), Entries[i].CooldownRemaining);
        }
        return data;
    }

    /// <summary>Null for bytes <see cref="Encode"/> did not write: another length, or a cooldown that is not a finite number of seconds from zero up.</summary>
    public static SlotSnapshot? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        var count = BitConverter.ToInt32(data);
        if (count < 0 || data.Length != 4 + (long)count * EntryBytes) return null;
        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var at = data.Slice(4 + i * EntryBytes);
            var cooldown = BitConverter.ToDouble(at.Slice(16));
            if (!double.IsFinite(cooldown) || cooldown < 0) return null;
            entries[i] = new Entry(BitConverter.ToUInt64(at), BitConverter.ToUInt32(at.Slice(8)), cooldown, BitConverter.ToInt32(at.Slice(12)));
        }
        return new SlotSnapshot(entries);
    }
}

/// <summary>The mod's one checkpoint copy, which ForgeCore keeps on every peer: the length of the slot book's part, that part
/// (<see cref="SlotSnapshot.Encode"/>; empty before the level's first checkpoint), then the content parts' own bytes (the suits).</summary>
internal static class CheckpointCopy
{
    public static byte[] Join(ReadOnlySpan<byte> slots, ReadOnlySpan<byte> content)
    {
        var copy = new byte[4 + slots.Length + content.Length];
        BitConverter.TryWriteBytes(copy, slots.Length);
        slots.CopyTo(copy.AsSpan(4));
        content.CopyTo(copy.AsSpan(4 + slots.Length));
        return copy;
    }

    /// <summary>False, with both parts null, when this peer holds no copy or the bytes are not what <see cref="Join"/> wrote.</summary>
    public static bool TrySplit(byte[]? copy, out byte[]? slots, out byte[]? content)
    {
        slots = content = null;
        if (copy == null || copy.Length < 4) return false;
        var length = BitConverter.ToInt32(copy);
        if (length < 0 || length > copy.Length - 4) return false;
        slots = copy.AsSpan(4, length).ToArray();
        content = copy.AsSpan(4 + length).ToArray();
        return true;
    }
}

/// <summary>A call the host accepted and has not yet delivered: a player's throw (it has a beacon, its landing is snapped onto the
/// navigation mesh and it faces away from the thrower), or a developer drop (<see cref="Developer"/>: thrower 0, the exact point,
/// facing <see cref="Yaw"/>, no beacon, arriving as <see cref="Arrival"/>, with its <see cref="Overrides"/>).</summary>
internal readonly record struct PendingSummon(ulong Thrower, uint ThrowId, uint Key, float X, float Y, float Z, double ArriveAt,
    bool Developer = false, OrdnanceArrivalMode Arrival = OrdnanceArrivalMode.Drop, float Yaw = 0f, int Dimension = -1,
    IReadOnlyDictionary<string, double>? Overrides = null);

/// <summary>Accepted calls waiting for their arrival time. Checkpoint recovery and a host change void them.</summary>
internal sealed class SummonScheduler
{
    private readonly List<PendingSummon> _pending = new();

    public int Count => _pending.Count;


    public bool Has(ulong thrower, uint throwId) => _pending.Any(p => p.Thrower == thrower && p.ThrowId == throwId);

    public void Add(PendingSummon summon)
    {
        if (Has(summon.Thrower, summon.ThrowId)) throw new InvalidOperationException("Throw " + summon.ThrowId + " of " + summon.Thrower + " is already scheduled.");
        _pending.Add(summon);
    }

    /// <summary>Moves every due summon into the caller's reusable buffer in stable arrival order.
    /// The detached batch stays valid when delivery callbacks add or clear pending summons.</summary>
    public void TakeDue(double now, List<PendingSummon> due)
    {
        due.Clear();
        if (_pending.Count == 0) return;
        var first = 0;
        while (first < _pending.Count && !(_pending[first].ArriveAt <= now)) first++;
        if (first == _pending.Count) return;
        var kept = first;
        for (var i = first; i < _pending.Count; i++)
        {
            var summon = _pending[i];
            if (summon.ArriveAt <= now) due.Add(summon);
            else _pending[kept++] = summon;
        }
        _pending.RemoveRange(kept, _pending.Count - kept);
        // Stable ordering also preserves acceptance order for equal arrival times.
        // ponytail: quadratic in the due batch size; use sequence keys if large batches become supported.
        for (var i = 1; i < due.Count; i++)
        {
            var summon = due[i];
            var j = i;
            while (j > 0 && due[j - 1].ArriveAt > summon.ArriveAt) { due[j] = due[j - 1]; j--; }
            due[j] = summon;
        }
    }

    /// <summary>Voids everything pending and returns how many were dropped.</summary>
    public int Clear()
    {
        var dropped = _pending.Count;
        _pending.Clear();
        return dropped;
    }
}

internal static class ThrowValidator
{
    /// <summary>The host's checks in order: the level has call-ins, the call is in the sender's loadout with a use and no
    /// cooldown, the landing is a finite point within range of the throw, and the landing is reachable ground.
    /// <paramref name="landingReachable"/> runs last because it queries the level.</summary>
    internal static Rejection Evaluate(SlotBook book, bool callMenu, ulong player, uint key, Vec3 origin, Vec3 landing,
        double now, double maxDistance, Func<bool> landingReachable)
    {
        if (!callMenu) return Rejection.CallMenuOff;
        var slot = book.Check(player, key, now);
        if (slot != Rejection.None) return slot;
        if (!origin.IsFinite || !landing.IsFinite) return Rejection.InvalidLanding;
        if (Vec3.Distance(origin, landing) > maxDistance) return Rejection.TooFar;
        return landingReachable() ? Rejection.None : Rejection.InvalidLanding;
    }
}
