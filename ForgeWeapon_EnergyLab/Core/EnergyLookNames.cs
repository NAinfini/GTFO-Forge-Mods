using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeWeaponEnergyLab;

/// <summary>What a definition's <c>Look</c> may replace: the names of the sounds the presentation plays and of the effect slots it draws,
/// and how a file's keys are written. The cue names are the cues of <c>audio/cues.forge</c>; the effect names are the slots of
/// <c>fx-slots.forge</c> without their <c>energy.</c> prefix.</summary>
internal static class EnergyLookNames
{
    /// <summary>Every cue the presentation plays. The preload hands them to ForgeAudio's <c>Require</c>, which refuses to start without all of them.</summary>
    internal static readonly string[] Cues =
    {
        "beam-start", "beam-loop", "beam-stop", "beam-hit",
        "arc-fire", "arc-loop", "arc-hit",
        "plasma-charge-rise", "plasma-charge-loop", "plasma-charge-full", "plasma-charge-cancel",
        "orb-fire", "orb-fly-loop", "shock-fly-loop", "orb-explode",
        "shock-fire", "shock-fade", "shock-hit", "shock-pulse",
        "blast-fire", "blast-cycle", "blast-explode",
        "disc-fire", "disc-fly-loop", "disc-bounce", "disc-drop",
        "flame-start", "flame-loop", "flame-stop", "flame-hit", "flame-crackle",
        "gravity-fire", "gravity-fly-loop", "gravity-open", "gravity-close", "gravity-field-loop",
    };

    /// <summary>Every effect slot the presentation draws, as the table names it (<c>energy.beam</c>).</summary>
    internal static readonly string[] FxSlots =
    {
        "energy.beam", "energy.beam.muzzle", "energy.beam.hit", "energy.arc", "energy.arc.hit", "energy.flame", "energy.flame.hit",
        "energy.orb", "energy.orb.charge", "energy.orb.charged", "energy.orb.trail", "energy.orb.muzzle", "energy.plasma.blast",
        "energy.shock.orb", "energy.shock.burst", "energy.discharge",
        "energy.blast", "energy.blast.muzzle", "energy.sparks",
        "energy.disc", "energy.disc.hit", "energy.disc.launch",
        "energy.hole", "energy.hole.shell", "energy.hole.launch", "energy.hole.open", "energy.hole.collapse",
    };

    private const string Prefix = "energy.";

    /// <summary>The cue a mode's shot plays; <c>Look.Sounds.fire</c> names it.</summary>
    internal static string FireCue(EnergyMode mode) => mode switch
    {
        EnergyMode.Beam => "beam-start",
        EnergyMode.ArcChain => "arc-fire",
        EnergyMode.PlasmaBlast => "orb-fire",
        EnergyMode.PlasmaArc => "shock-fire",
        EnergyMode.Blast => "blast-fire",
        EnergyMode.Disc => "disc-fire",
        EnergyMode.Flame => "flame-start",
        EnergyMode.BlackHole => "gravity-fire",
        _ => "",
    };

    /// <summary>The cue a Sounds key names (<c>fire</c> for the mode's shot, else the cue name itself), or null for a key that is neither.</summary>
    internal static string? CueOf(EnergyMode mode, string key)
        => key == "fire" ? FireCue(mode) is { Length: > 0 } fire ? fire : null : Array.IndexOf(Cues, key) >= 0 ? key : null;

    /// <summary>The slot an Fx key names (the slot name with or without <c>energy.</c>), or null for a key that is no slot.</summary>
    internal static string? SlotOf(string key)
    {
        var slot = key.StartsWith(Prefix, StringComparison.Ordinal) ? key : Prefix + key;
        return Array.IndexOf(FxSlots, slot) >= 0 ? slot : null;
    }

    /// <summary>A definition's <c>Look</c> read from a file: each key is a cue or slot name already spelled in full, each value what to play instead.</summary>
    internal static WeaponLook Read(JsonElement look, EnergyMode mode)
    {
        ForgeCore.StrictJson.UniqueMembers(look, "Look.");
        ForgeCore.StrictJson.OnlyKnown(look, "Look.", new[] { "Sounds", "Fx" });
        return new WeaponLook(Map(look, "Sounds", key => CueOf(mode, key), "a cue name of the Energy Lab sounds (or fire)"),
            Map(look, "Fx", SlotOf, "an effect slot name such as beam.muzzle"));
    }

    private static IReadOnlyDictionary<string, string> Map(JsonElement look, string field, Func<string, string?> canonical, string expected)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!look.TryGetProperty(field, out _)) return result;
        var map = ForgeCore.StrictJson.Object(look, "Look.", field);
        ForgeCore.StrictJson.UniqueMembers(map, "Look." + field + ".");
        foreach (var entry in map.EnumerateObject())
        {
            var where = "Look." + field + "." + entry.Name;
            if (canonical(entry.Name) is not { } name) throw new ForgeCore.StrictJsonException(where, "is not " + expected + ".");
            if (entry.Value.ValueKind != JsonValueKind.String) throw new ForgeCore.StrictJsonException(where, "must be a string.");
            if (!result.TryAdd(name, entry.Value.GetString()!)) throw new ForgeCore.StrictJsonException(where, "names '" + name + "' a second time.");
        }
        return result;
    }

    /// <summary>Throws an <see cref="System.IO.InvalidDataException"/> naming the field when a look is not what a definition may carry.</summary>
    internal static void Validate(WeaponLook look, EnergyMode mode, string path)
    {
        foreach (var (key, value) in look.Sounds)
        {
            if (Array.IndexOf(Cues, key) < 0) throw new System.IO.InvalidDataException(path + ".sounds." + key + ": not a cue name");
            Value(value, path + ".sounds." + key);
        }
        foreach (var (key, value) in look.Fx)
        {
            if (Array.IndexOf(FxSlots, key) < 0) throw new System.IO.InvalidDataException(path + ".fx." + key + ": not an effect slot name");
            Value(value, path + ".fx." + key);
        }
    }

    private static void Value(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsWhiteSpace))
            throw new System.IO.InvalidDataException(path + ": expected a name of at most 128 characters without spaces");
    }

    /// <summary>The look as one canonical text for the multiplayer check: sorted, empty for no look.</summary>
    internal static string Canonical(WeaponLook? look)
        => look == null ? "" : string.Join(";", look.Sounds.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "s:" + p.Key + "=" + p.Value)
            .Concat(look.Fx.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => "f:" + p.Key + "=" + p.Value)));
}
