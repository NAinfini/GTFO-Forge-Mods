using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

// Domain index only. Runtime remains the sole registry, clock and transaction owner.
internal sealed class EquipmentIdentityIndex
{
    internal sealed record Entry(EquipmentObservation Value, long Revision);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> retiredLives = new(StringComparer.Ordinal);
    private readonly Dictionary<(EntityReference Owner, string Slot), EntityReference> slots = new();
    private readonly int maxActive, maxHistory;
    private long revision;
    internal long WorldEpoch { get; private set; }
    internal int Count => entries.Count;
    internal int HistoryCount => retiredLives.Count;
    internal EquipmentIdentityIndex(int maxActive, int maxHistory)
    {
        Check(maxActive is > 0 and <= 4096 && maxHistory >= maxActive && maxHistory <= 16384,
            "equipment.invalid-limits");
        this.maxActive = maxActive; this.maxHistory = maxHistory;
    }
    internal void BeginWorld(long worldEpoch)
    {
        // The kernel's lifecycle names the world; a client's count may start over below the one it left.
        Check(worldEpoch >= 0 && worldEpoch <= RuntimeJson.MaxSafeInteger,
            "equipment.stale-world");
        if (worldEpoch == WorldEpoch) return;
        Clear(); retiredLives.Clear(); WorldEpoch = worldEpoch;
    }
    internal void Clear() { entries.Clear(); slots.Clear(); }
    internal Entry? Get(EntityReference reference)
        => reference != null && reference.WorldEpoch == WorldEpoch
        && entries.TryGetValue(reference.Id, out var entry) && entry.Value.Entity == reference ? entry : null;
    internal bool Remove(EntityReference reference)
    {
        var entry = Get(reference);
        if (entry == null) return false;
        RemoveSlot(entry.Value); entries.Remove(reference.Id); return true;
    }
    private void RemoveSlot(EquipmentObservation value)
    {
        if (value.Location == EquipmentLocation.Inventory)
            slots.Remove((value.Owner!, value.Slot!));
    }
    internal Entry Record(EquipmentObservation value)
    {
        Validate(value); entries.TryGetValue(value.Entity.Id, out var previous);
        if (previous != null)
        {
            Check(previous.Value.Entity == value.Entity, "equipment.instance-still-live");
            Check(previous.Value.ResourceId == value.ResourceId && previous.Value.ResourceRevision == value.ResourceRevision,
                "equipment.definition-changed");
            if (previous.Value == value) return previous;
        }
        else
        {
            Check(!retiredLives.TryGetValue(value.Entity.Id, out var life) || value.Entity.LifeEpoch > life,
                "equipment.retired-life");
            Check(entries.Count < maxActive, "equipment.active-budget");
            Check(retiredLives.ContainsKey(value.Entity.Id) || retiredLives.Count < maxHistory,
                "equipment.history-budget");
        }
        if (value.Location == EquipmentLocation.Inventory)
            Check(!slots.TryGetValue((value.Owner!, value.Slot!), out var occupied) || occupied == value.Entity,
                "equipment.slot-occupied");
        var next = new Entry(value, checked(revision + 1));
        if (previous != null) RemoveSlot(previous.Value);
        entries[value.Entity.Id] = next; retiredLives[value.Entity.Id] = value.Entity.LifeEpoch;
        if (value.Location == EquipmentLocation.Inventory) slots[(value.Owner!, value.Slot!)] = value.Entity;
        revision = next.Revision; return next;
    }
    internal void Validate(EquipmentObservation value)
    {
        ArgumentNullException.ThrowIfNull(value); Reference(value.Entity);
        // The index holds backpack equipment lives only: a world deployment has the same namespace but its own
        // shape, and the placement table owns it. A wrong namespace and a malformed life id are the same refusal.
        Check(EquipmentIdentityId.ShapeOf(value.Entity.Id) == EquipmentEntityShape.Life, "equipment.entity-namespace");
        Check(value.Entity.WorldEpoch == WorldEpoch, "equipment.stale-world");
        Check(Text(value.ResourceId) && Text(value.ResourceRevision), "equipment.resource-reference");
        Check(value.Location is EquipmentLocation.Inventory or EquipmentLocation.World or EquipmentLocation.Deployed, "equipment.location");
        if (value.Owner != null)
        {
            Reference(value.Owner);
            Check(value.Owner.WorldEpoch == WorldEpoch, "equipment.stale-owner-world");
            Check(value.Owner != value.Entity, "equipment.self-owner");
        }
        if (value.Location == EquipmentLocation.Inventory)
            Check(value.Owner != null && Text(value.Slot), "equipment.inventory-location");
        else Check(value.Slot == null && !value.IsWielded, "equipment.non-inventory-location");
        Check(!value.IsWielded || value.IsReady, "equipment.wielded-not-ready");
    }
    internal static void Reference(EntityReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = ValidatedReferences.GetValue(value, static reference =>
        {
            _ = RuntimeJson.Entity(RuntimeJson.From(reference));
            return ValidReference;
        });
    }
    // Only immutable reference syntax is cached. World/life validity is still checked at every observation;
    // weak keys cannot retain an ended life, and a `with` copy must pass the same SDK validator on its own.
    private static readonly ConditionalWeakTable<EntityReference, object> ValidatedReferences = new();
    private static readonly object ValidReference = new();
    private static bool Text(string? value)
    {
        if (value == null || value.Length is < 1 or > 256 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) return false;
        foreach (var c in value.AsSpan()) if (char.IsControl(c)) return false;
        return true;
    }
    internal static void Check(bool condition, string code)
    { if (!condition) throw new RuntimeContractException(code, code); }
}
