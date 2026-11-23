using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeEnemy.Profile;

/// <summary>
/// The per-type look the website authors beyond the region assembly: the material float properties
/// (`forge.eec.Model.Material.MaterialSets`, including the `full` set), the part looks (`forge.enemyAppearance.Looks`:
/// a colour and a fixed glow for the whole body, one body region, a tumour or an appendage), the glow pulses
/// (`forge.eec.Model.Glow.PulseEffects`), the propagated wake-up colour (`forge.eec.Model.Glow.PropagateWakeupColor`)
/// and the tag marker (`forge.eec.Model.Marker`: its icon, colour, size, the blinks and marking while hibernating).
/// Every member here is already checked: the reader refuses a value it cannot apply by name, so the native side only
/// decides what the game has.
/// </summary>
internal sealed class EnemyLookProfile
{
    internal IReadOnlyList<EnemyMaterialSetProfile> MaterialSets { get; set; } = Array.Empty<EnemyMaterialSetProfile>();
    internal IReadOnlyList<EnemyPartLook> PartLooks { get; set; } = Array.Empty<EnemyPartLook>();
    internal IReadOnlyList<EnemyPulseProfile> Pulses { get; set; } = Array.Empty<EnemyPulseProfile>();
    internal EnemyMarkerProfile? Marker { get; set; }
    /// <summary>`AllowMarkingOnHibernate`: the spawn clears the life's scanner sound index, as EEC does.</summary>
    internal bool AllowMarkingOnHibernate { get; set; }
    /// <summary>The colour this type's propagated wake-up fades to, in place of the game's one static colour.</summary>
    internal float[]? PropagateWakeupColor { get; set; }

    /// <summary>Whether the prefab pass has anything to write (the material float properties).</summary>
    internal bool HasMaterialPass => MaterialSets.Count > 0;

    /// <summary>Whether the instance part-look pass has anything to write.</summary>
    internal bool HasPartLooks => PartLooks.Count > 0;

    /// <summary>Whether a spawn has per-life state to keep for this look.</summary>
    internal bool HasLifeState => Pulses.Count != 0 || Marker != null || PropagateWakeupColor != null;

    /// <summary>Whether the kernel tick has anything to advance for a life of this look.</summary>
    internal bool IsTicked => Pulses.Count != 0 || Marker?.Blink != null;

    internal bool IsEmpty => !HasMaterialPass && !HasPartLooks && !HasLifeState && !AllowMarkingOnHibernate;
}

/// <summary>`forge.eec.Model.Marker`, checked: the icon, the marker colour, the icon size and the two blinks. A
/// member the author left out keeps the marker's own value: an unset size is not written at all.</summary>
internal sealed class EnemyMarkerProfile
{
    internal bool BlinkIn { get; set; }
    internal EnemyMarkerBlink? Blink { get; set; }
    /// <summary>`MarkerColor`: the colour the marker is set to (`NavMarker.SetColor`); null keeps the game's.</summary>
    internal float[]? Color { get; set; }
    /// <summary>`SpriteScale`: a multiple of the icon part's own scale (`NavMarker.m_enemySubObj`), so 1 is the
    /// vanilla size; null keeps it.</summary>
    internal float? SpriteScale { get; set; }
    /// <summary>`Icon`: which shape the icon takes; <see cref="EnemyMarkerIcon.Vanilla"/> keeps the game's.</summary>
    internal EnemyMarkerIcon Icon { get; set; }
}

/// <summary>`Blink` with its window: blinks run for `min(Duration, the type's TagTime)` seconds after the marker is
/// placed, the first at 0.4 s plus one draw from `[MinDelay, MaxDelay)`, each next one a further draw later — EEC's
/// schedule. The reader guarantees `0 &lt;= MinDelay &lt; MaxDelay` and a positive duration.</summary>
internal sealed class EnemyMarkerBlink
{
    internal const float FirstOffset = 0.4f;

    internal EnemyMarkerBlink(float duration, float minDelay, float maxDelay)
    { Duration = duration; MinDelay = minDelay; MaxDelay = maxDelay; }

    internal float Duration { get; }
    internal float MinDelay { get; }
    internal float MaxDelay { get; }

    /// <summary>One delay drawn deterministically from the enemy id and the blink's number.</summary>
    internal float Delay(ushort enemyId, int counter)
        => MinDelay + (float)ForgeCore.DeterministicRandom.Unit(enemyId, 0x40000000u | (uint)counter) * (MaxDelay - MinDelay);
}

/// <summary>A colour written as text: `#RRGGBB`/`RRGGBB`/`#RRGGBBAA` (0..1 per channel), or three or four finite
/// numbers separated by commas, optionally wrapped in parentheses (`9,3.9,3,1` — the glow colours are HDR, so a
/// component above 1 is a real value). The field table types colours `string` and writes its defaults in the comma
/// form, which is why both spellings are one parser.</summary>
internal static class EnemyColorText
{
    internal static bool TryParse(string? text, out float[] color)
    {
        color = Array.Empty<float>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (trimmed.IndexOf(',') >= 0)
        {
            if (trimmed.StartsWith("(", StringComparison.Ordinal) && trimmed.EndsWith(")", StringComparison.Ordinal))
                trimmed = trimmed[1..^1];
            var parts = trimmed.Split(',');
            if (parts.Length is not (3 or 4)) return false;
            var values = new float[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                if (!float.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value)) return false;
                values[index] = value;
            }
            color = values;
            return true;
        }
        string hex = trimmed.StartsWith("#", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
        if (hex.Length is not (6 or 8)) return false;
        var parsed = new float[hex.Length == 8 ? 4 : 3];
        for (int index = 0; index < parsed.Length; index++)
        {
            if (!int.TryParse(hex.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)) return false;
            parsed[index] = value / 255f;
        }
        color = parsed;
        return true;
    }
}

/// <summary>One cached step of a glow pattern: how many pattern characters it lasts and the progression from the
/// default glow towards the pulse colour. A negative progression is a delay, which writes nothing.</summary>
internal readonly struct EnemyGlowStep
{
    internal EnemyGlowStep(int length, float progression) { Length = length; Progression = progression; }
    internal int Length { get; }
    internal float Progression { get; }
    internal bool IsDelay => Progression < 0f;
}

/// <summary>
/// The EEC glow pattern (`PulseEffectData.CachePattern`, ExtraEnemyCustomization @34af773c), step for step:
/// `0`–`9` are progressions 0.0–0.9, `f` is 1, `-` is a delay and `+` holds the previous step one character longer.
/// A value closes the running step; a delay closes a running value and extends a running delay; the last step is
/// closed at the end. Where EEC skips an unknown character silently this reader refuses it by name, and it refuses
/// a leading `+` (EEC throws), an empty pattern and a pattern of one step (EEC's routine plays nothing for it).
/// </summary>
internal static class EnemyGlowPattern
{
    internal static bool TryParse(string? text, out EnemyGlowStep[] steps, out string error)
    {
        steps = Array.Empty<EnemyGlowStep>();
        if (string.IsNullOrEmpty(text)) { error = "the pattern is empty"; return false; }
        var list = new List<EnemyGlowStep>();
        int length = 0;
        float progression = 0f;
        for (int position = 0; position < text.Length; position++)
        {
            char character = text[position];
            float value;
            if (character is >= '0' and <= '9') value = (character - '0') / 10f;
            else if (character == 'f') value = 1f;
            else if (character == '-')
            {
                if (length > 0 && progression >= 0f) { list.Add(new EnemyGlowStep(length, progression)); length = 0; progression = 0f; }
                if (length > 0 && progression < 0f) length++;
                else { length = 1; progression = -1f; }
                continue;
            }
            else if (character == '+')
            {
                if (length == 0) { error = "`+` at position 1 has no step before it to hold"; return false; }
                length++;
                continue;
            }
            else
            {
                error = "character `" + character + "` at position " + (position + 1).ToString(CultureInfo.InvariantCulture)
                    + " is not one of 0-9, f, - or +";
                return false;
            }
            if (length > 0) list.Add(new EnemyGlowStep(length, progression));
            length = 1;
            progression = value;
        }
        if (length > 0) list.Add(new EnemyGlowStep(length, progression));
        if (list.Count < 2) { error = "the pattern has one step, and a one-step pattern never pulses"; return false; }
        steps = list.ToArray();
        error = string.Empty;
        return true;
    }
}

/// <summary>The agent modes a pulse plays in: EEC's `AgentModeTarget` flags, which are not the game's own
/// `Agents.AgentMode` numbering.</summary>
[Flags]
internal enum EnemyModeTarget
{
    None = 0,
    Off = 1,
    Hibernate = 2,
    Agressive = 4,
    Scout = 8,
    Patrolling = 16,
    All = Off | Hibernate | Agressive | Scout | Patrolling
}

internal static class EnemyModeTargets
{
    /// <summary>A comma- or bar-separated list of mode names with EEC's aliases (`dead` for off, `combat` for
    /// agressive, `hibernation`/`hibernating`/`sleeping` for hibernate, `scoutpatrolling` for scout), matched without
    /// case, plus `All`; or the whole flag number. `None` and an unknown name are refused by name, because a pulse
    /// that targets no mode never plays.</summary>
    internal static bool TryParse(string? text, int? number, out EnemyModeTarget target, out string error)
    {
        target = EnemyModeTarget.None;
        error = string.Empty;
        if (number is int flags)
        {
            if (flags <= 0 || flags > (int)EnemyModeTarget.All) { error = "flag number " + flags.ToString(CultureInfo.InvariantCulture) + " names no mode"; return false; }
            target = (EnemyModeTarget)flags;
            return true;
        }
        if (string.IsNullOrWhiteSpace(text)) { error = "no mode is named"; return false; }
        foreach (string raw in text.Split(',', '|'))
        {
            string name = raw.Trim().ToLowerInvariant();
            switch (name)
            {
                case "off": case "dead": target |= EnemyModeTarget.Off; break;
                case "agressive": case "combat": target |= EnemyModeTarget.Agressive; break;
                case "hibernate": case "hibernation": case "hibernating": case "sleeping": target |= EnemyModeTarget.Hibernate; break;
                case "scout": case "scoutpatrolling": target |= EnemyModeTarget.Scout; break;
                case "patrolling": target |= EnemyModeTarget.Patrolling; break;
                case "all": target |= EnemyModeTarget.All; break;
                default: error = "mode `" + raw.Trim() + "` is not Off, Hibernate, Agressive, Scout, Patrolling or All"; return false;
            }
        }
        return true;
    }

    /// <summary>The flag for one game mode, by the game's own `Agents.AgentMode` value (Off 0, Agressive 1,
    /// Patrolling 2, Scout 3, Hibernate 4).</summary>
    internal static EnemyModeTarget OfGameMode(int mode) => mode switch
    {
        0 => EnemyModeTarget.Off,
        1 => EnemyModeTarget.Agressive,
        2 => EnemyModeTarget.Patrolling,
        3 => EnemyModeTarget.Scout,
        4 => EnemyModeTarget.Hibernate,
        _ => EnemyModeTarget.None
    };
}

/// <summary>One `PulseEffects` entry, checked: the cached steps, the whole cycle's length in seconds, the colour
/// the steps lerp towards, the modes it plays in and EEC's three switches (which default to off, the value an
/// EEC entry deserialised without them has).</summary>
internal sealed class EnemyPulseProfile
{
    internal EnemyPulseProfile(EnemyGlowStep[] steps, float duration, float[] color, EnemyModeTarget target)
    { Steps = steps; Duration = duration; Color = color; Target = target; }

    internal EnemyGlowStep[] Steps { get; }
    /// <summary>The whole pattern's length in seconds; one pattern character lasts `Duration / Steps.Length`,
    /// exactly as EEC divides it.</summary>
    internal float Duration { get; }
    internal float[] Color { get; }
    internal EnemyModeTarget Target { get; }
    internal bool AlwaysPulse { get; set; }
    internal bool KeepOnDead { get; set; }
    internal bool RandomizeTime { get; set; }

    /// <summary>EEC's own step delay: the cycle divided by the number of cached steps (not characters).</summary>
    internal float StepSeconds => Duration / Steps.Length;
}

/// <summary>One `MaterialSets` entry's property writes, checked: the material it finds by name among the type's
/// material references (`From`, without the ` (Instance)` suffix, matched without case) — or, for the `full` region
/// set, every material that has the property — and the float properties it writes on each found material's
/// per-type copy. Colours are not written here: they are part looks (<see cref="EnemyPartLook"/>), which choose
/// the property by the shader.</summary>
internal sealed class EnemyMaterialSetProfile
{
    internal EnemyMaterialSetProfile(string from) { From = from; }

    internal string From { get; }

    /// <summary>The `full` region set: each property goes on every material of the type that has it.</summary>
    internal bool AllMaterials { get; set; }
    internal List<(string Name, float Value)> Floats { get; } = new();
}
