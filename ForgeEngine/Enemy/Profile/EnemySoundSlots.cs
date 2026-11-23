using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEnemy.Profile;

/// <summary>The native sound events of an enemy an author can give a sound of their own. Each maps to fields of the
/// enemy's EnemySFX block (or its melee timing entries), and those are the only places the game reads these sounds from.
/// Tentacle sounds are left out: the game posts them on the tentacle's own sound player, which has no enemy to follow.
/// The ladder field is left out too: no code of the game reads it, so a sound written there never plays.</summary>
internal enum EnemySoundSlot
{
    Walk, Run, HeartbeatPulse, HibernateIdle, DetectionStart, WakeUp, Scream, StuckInGlue, ReleaseFromGlue, AttackWindUp, AttackFire,
    HurtSmall, HurtBig, SleepingDeath, Death, JumpStart, JumpInAir, JumpLand, MeleeForward, MeleeBackward
}

/// <summary>One replaced event: which slot, and either the audio resource that takes it over (its revision, the hash its
/// `&lt;revision&gt;.ogg` file is found by in the profile's package) or a game sound event to post instead (zero when
/// it is a resource).</summary>
internal readonly record struct EnemySoundEntry(EnemySoundSlot Slot, string? Resource, uint GameEvent);

/// <summary>The `sounds` object of an enemy document: {slot name: audio resource revision or game sound event}.</summary>
internal static class EnemySoundSlots
{
    private static readonly Dictionary<string, EnemySoundSlot> Names = new(StringComparer.Ordinal)
    {
        ["walk"] = EnemySoundSlot.Walk,
        ["run"] = EnemySoundSlot.Run,
        ["heartbeatPulse"] = EnemySoundSlot.HeartbeatPulse,
        ["hibernateIdle"] = EnemySoundSlot.HibernateIdle,
        ["hibernateDetectionStart"] = EnemySoundSlot.DetectionStart,
        ["hibernateWakeUp"] = EnemySoundSlot.WakeUp,
        ["scream"] = EnemySoundSlot.Scream,
        ["stuckInGlue"] = EnemySoundSlot.StuckInGlue,
        ["releaseFromGlue"] = EnemySoundSlot.ReleaseFromGlue,
        ["attackWindUp"] = EnemySoundSlot.AttackWindUp,
        ["attackFire"] = EnemySoundSlot.AttackFire,
        ["hurtSmall"] = EnemySoundSlot.HurtSmall,
        ["hurtBig"] = EnemySoundSlot.HurtBig,
        ["hibernateDie"] = EnemySoundSlot.SleepingDeath,
        ["die"] = EnemySoundSlot.Death,
        ["jumpStart"] = EnemySoundSlot.JumpStart,
        ["jumpInAir"] = EnemySoundSlot.JumpInAir,
        ["jumpLand"] = EnemySoundSlot.JumpLand,
        ["meleeForward"] = EnemySoundSlot.MeleeForward,
        ["meleeBackward"] = EnemySoundSlot.MeleeBackward
    };

    /// <summary>Reads `sounds`; returns the refusal's detail, or null with <paramref name="sounds"/> filled.</summary>
    internal static string? Read(JsonElement element, out EnemySoundEntry[] sounds)
    {
        sounds = Array.Empty<EnemySoundEntry>();
        if (element.ValueKind != JsonValueKind.Object) return "`sounds` must be an object.";
        var read = new List<EnemySoundEntry>();
        var seen = new HashSet<EnemySoundSlot>();
        foreach (var property in element.EnumerateObject())
        {
            if (!Names.TryGetValue(property.Name, out var slot)) return "`sounds` names an unknown sound event `" + property.Name + "`.";
            if (!seen.Add(slot)) return "`sounds` names `" + property.Name + "` twice.";
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetUInt32(out var gameEvent) && gameEvent != 0)
                read.Add(new EnemySoundEntry(slot, null, gameEvent));
            else if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } resource
                     && ForgeEngine.Framework.PublishedResources.IsSha256(resource))
                read.Add(new EnemySoundEntry(slot, resource, 0));
            else return "`sounds." + property.Name + "` must be the revision of an audio resource or a game sound event.";
        }
        if (read.Count == 0) return "`sounds` replaces nothing.";
        sounds = read.ToArray();
        return null;
    }
}
