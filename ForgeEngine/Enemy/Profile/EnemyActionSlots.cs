using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>The native actions of an enemy an author can put another action in place of. Each is a group of the states the game
/// cross-fades its animator to; the game's animator keeps running underneath.</summary>
internal enum EnemyActionSlot
{
    Idle, Walk, Run, Ranged, Melee, HitLight, HitHeavy, Death, Wake, Scream, Hibernate, Jump, Ladder
}

/// <summary>One replaced action: an animation resource (its revision, the hash its `&lt;revision&gt;.glb` file is
/// found by in the profile's package), or a clip the game itself has (<see cref="GameEnemy"/> is the enemy whose
/// animator holds it, nothing is shipped).</summary>
internal readonly record struct EnemyActionEntry(EnemyActionSlot Slot, string? Resource, uint GameEnemy, string? GameClip);

/// <summary>The `actions` object of an enemy document: {action name: animation resource revision or {enemy, clip}}.</summary>
internal static class EnemyActionSlots
{
    internal const int Count = (int)EnemyActionSlot.Ladder + 1;

    private static readonly Dictionary<string, EnemyActionSlot> Names = new(StringComparer.Ordinal)
    {
        ["idle"] = EnemyActionSlot.Idle,
        ["walk"] = EnemyActionSlot.Walk,
        ["run"] = EnemyActionSlot.Run,
        ["ranged"] = EnemyActionSlot.Ranged,
        ["melee"] = EnemyActionSlot.Melee,
        ["hitLight"] = EnemyActionSlot.HitLight,
        ["hitHeavy"] = EnemyActionSlot.HitHeavy,
        ["death"] = EnemyActionSlot.Death,
        ["wake"] = EnemyActionSlot.Wake,
        ["scream"] = EnemyActionSlot.Scream,
        ["hibernate"] = EnemyActionSlot.Hibernate,
        ["jump"] = EnemyActionSlot.Jump,
        ["ladder"] = EnemyActionSlot.Ladder
    };

    /// <summary>Reads `actions`; returns the refusal's detail, or null with <paramref name="actions"/> filled.</summary>
    internal static string? Read(JsonElement element, out EnemyActionEntry[] actions)
    {
        actions = Array.Empty<EnemyActionEntry>();
        if (element.ValueKind != JsonValueKind.Object) return "`actions` must be an object.";
        var read = new List<EnemyActionEntry>();
        var seen = new HashSet<EnemyActionSlot>();
        foreach (var property in element.EnumerateObject())
        {
            if (!Names.TryGetValue(property.Name, out var slot)) return "`actions` names an unknown action `" + property.Name + "`.";
            if (!seen.Add(slot)) return "`actions` names `" + property.Name + "` twice.";
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } resource && ForgeEngine.Framework.PublishedResources.IsSha256(resource))
                read.Add(new EnemyActionEntry(slot, resource, 0, null));
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("enemy", out var enemy) && enemy.TryGetUInt32(out var enemyId) && enemyId != 0
                     && value.TryGetProperty("clip", out var clip) && clip.ValueKind == JsonValueKind.String && clip.GetString() is { Length: > 0 } clipName)
                read.Add(new EnemyActionEntry(slot, null, enemyId, clipName));
            else return "`actions." + property.Name + "` must be the revision of an animation resource or {enemy, clip}.";
        }
        if (read.Count == 0) return "`actions` replaces nothing.";
        actions = read.ToArray();
        return null;
    }
}
