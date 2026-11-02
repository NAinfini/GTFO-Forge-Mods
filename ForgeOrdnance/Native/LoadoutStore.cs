using System;
using System.Collections.Generic;

namespace ForgeOrdnance;

/// <summary>The local player's lobby picks, kept in ForgeCore's local store (<c>BepInEx/ForgeCore/Store</c>) and submitted to the host
/// on entering a level. Slots hold definition Ids; an Id no loaded definition carries stays in the file (another rundown may
/// load it) but counts as empty here.</summary>
internal static class LoadoutStore
{
    private static string?[] _slots = Array.Empty<string?>();

    /// <summary>A slot changed (<see cref="Set"/>).</summary>
    internal static event Action? Changed;

    internal static void Load()
    {
        var read = ForgeCore.Store.Read<LoadoutSave>(Plugin.Id);
        if (read.Status == ForgeCore.StoreStatus.Corrupt) Plugin.Report("Saved loadout " + read.Detail + "; starting empty.");
        var errors = new List<string>();
        _slots = read.Status == ForgeCore.StoreStatus.Read ? LoadoutFile.Clean(read.Value!.Slots, errors) : Array.Empty<string?>();
        foreach (var error in errors) Plugin.Report("Saved loadout " + read.Detail + ": " + error);
    }

    /// <summary>The definition in slot <paramref name="index"/>, or null when empty or not loaded.</summary>
    internal static OrdnanceDefinition? Get(int index)
        => index < _slots.Length && _slots[index] is { } id && OrdnanceContent.Set.TryGetById(id, out var definition) ? definition : null;

    /// <summary>Slots <c>0..size-1</c> as definitions (null for empty).</summary>
    internal static OrdnanceDefinition?[] Slots(int size)
    {
        var result = new OrdnanceDefinition?[size];
        for (var i = 0; i < size; i++) result[i] = Get(i);
        return result;
    }

    /// <summary>Puts <paramref name="definition"/> (or nothing) into slot <paramref name="index"/>; the same definition leaves
    /// any other slot it was in. Saves at once.</summary>
    internal static void Set(int index, OrdnanceDefinition? definition)
    {
        if (index < 0 || index >= Messages.MaxLoadout) throw new ArgumentOutOfRangeException(nameof(index));
        var slots = new List<string?>(_slots);
        while (slots.Count <= index) slots.Add(null);
        if (definition != null)
            for (var i = 0; i < slots.Count; i++)
                if (slots[i] == definition.Id) slots[i] = null;
        slots[index] = definition?.Id;
        while (slots.Count > 0 && slots[^1] == null) slots.RemoveAt(slots.Count - 1);
        _slots = slots.ToArray();
        Save();
        Changed?.Invoke();
    }

    /// <summary>The keys visible under the host's effective slot count and allow-list. Saved entries outside that view remain
    /// untouched for another expedition.</summary>
    internal static List<uint> KeysFor(OrdnanceLevelRules rules)
    {
        var keys = new List<uint>();
        for (var i = 0; i < Math.Min(rules.LoadoutSize, _slots.Length); i++)
        {
            if (_slots[i] is not { } id) continue;
            if (!OrdnanceContent.Set.TryGetById(id, out var definition))
            {
                Plugin.Report("Saved loadout slot " + (i + 1) + " names '" + id + "', which is not a loaded call-in definition; it is left out.");
                continue;
            }
            if (definition.Callable && rules.IsAllowed(definition.Id)) keys.Add(definition.Key);
        }
        return keys;
    }

    private static void Save() => ForgeCore.Store.Write(Plugin.Id, new LoadoutSave { Slots = _slots });
}