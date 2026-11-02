using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ForgeUI;

/// <summary>Puts the Harmony patches of a <see cref="PatchGroup"/> on the first time the feature it serves is used. Once on they stay:
/// the game keeps a detour for a patched method after an unpatch. A group that cannot be patched is reported once and its feature
/// stays unhooked; the other groups are not affected.</summary>
internal static class PatchGroups
{
    private static readonly bool[] On = new bool[Enum.GetValues(typeof(PatchGroup)).Length];
    private static Dictionary<PatchGroup, List<Type>>? _types;
    private static Harmony? _harmony;

    /// <summary>The group's feature has been used (its patches are on, or were attempted).</summary>
    internal static bool Has(PatchGroup group) => On[(int)group];

    /// <summary>Puts the group's patches on the first time; every later call is one comparison.</summary>
    internal static void Ensure(PatchGroup group)
    {
        if (On[(int)group]) return;
        On[(int)group] = true;
        try
        {
            _types ??= Scan();
            if (_types.TryGetValue(group, out var types))
            {
                _harmony ??= new Harmony(Plugin.Id);
                foreach (var type in types) _harmony.CreateClassProcessor(type).Patch();
            }
            Subscribe(group);
        }
        catch (Exception error)
        {
            Plugin.Error("Hooks of " + group + " could not be put on; the feature stays unhooked: " + error);
        }
    }

    private static Dictionary<PatchGroup, List<Type>> Scan()
    {
        var found = new Dictionary<PatchGroup, List<Type>>();
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(PatchGroupAttribute), false) is not { Length: > 0 } attributes) continue;
            var group = ((PatchGroupAttribute)attributes[0]).Group;
            if (!found.TryGetValue(group, out var list)) found[group] = list = new List<Type>();
            list.Add(type);
        }
        return found;
    }

    /// <summary>What a group needs besides patches, once.</summary>
    private static void Subscribe(PatchGroup group)
    {
        switch (group)
        {
            case PatchGroup.Objectives: GameObjectives.Subscribe(); UIKit.ObjectivesReading.ReadInFull(); break;
            case PatchGroup.Inventory: UIKit.InventoryReading.ReadInFull(); break;
            case PatchGroup.Intel: GameWardenIntel.Subscribe(); break;
        }
    }
}
