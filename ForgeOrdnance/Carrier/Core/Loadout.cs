using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeOrdnance;

internal enum MatchKind { None, Prefix, Exact }

internal readonly struct MatchResult
{
    public MatchResult(MatchKind kind, int index) { Kind = kind; Index = index; }
    public MatchKind Kind { get; }
    /// <summary>The loadout slot whose code equals the input; -1 unless <see cref="Kind"/> is Exact.</summary>
    public int Index { get; }
}

internal static class CommandMatcher
{
    /// <summary>Exact when the input equals a slot's whole code, Prefix while at least one code still starts with the input,
    /// None when nothing can match any more. An empty input is a prefix of every code. Loadouts never hold one code as the
    /// prefix of another (see <see cref="LoadoutValidator"/>), so an exact match is never ambiguous.</summary>
    public static MatchResult Match(IReadOnlyList<OrdnanceDefinition> loadout, IReadOnlyList<Direction> input)
    {
        var prefix = false;
        for (var i = 0; i < loadout.Count; i++)
        {
            var code = loadout[i].Code;
            if (!StartsWith(code, input)) continue;
            if (code.Count == input.Count) return new MatchResult(MatchKind.Exact, i);
            prefix = true;
        }
        return new MatchResult(prefix ? MatchKind.Prefix : MatchKind.None, -1);
    }

    /// <summary>True while <paramref name="code"/> still begins with everything typed so far.</summary>
    public static bool StartsWith(IReadOnlyList<Direction> code, IReadOnlyList<Direction> input)
    {
        if (input.Count > code.Count) return false;
        for (var i = 0; i < input.Count; i++) if (code[i] != input[i]) return false;
        return true;
    }
}

internal sealed class LoadoutResult
{
    public List<OrdnanceDefinition> Accepted { get; } = new();
    public Rejection Reason { get; set; }
    public string Detail { get; set; } = "";
    public bool Ok => Reason == Rejection.None;
}

internal static class LoadoutValidator
{
    /// <summary>Checks a submitted loadout against the definitions and the level's rules: known keys, no repeats, within the size
    /// limit and the allowed list, and no code that is the beginning of another code (so a typed sequence is never ambiguous).</summary>
    public static LoadoutResult Validate(IReadOnlyList<uint> keys, DefinitionSet set, OrdnanceLevelRules rules)
    {
        var result = new LoadoutResult();
        if (keys.Count > rules.LoadoutSize)
        {
            result.Reason = Rejection.LoadoutInvalid;
            result.Detail = keys.Count + " entries, the limit is " + rules.LoadoutSize + ".";
            return result;
        }
        foreach (var key in keys)
        {
            if (!set.TryGet(key, out var definition))
            {
                result.Reason = Rejection.UnknownDefinition;
                result.Detail = "key " + key.ToString("x8") + " is not a definition on the host.";
                return result;
            }
            if (!definition.Callable)
            {
                result.Reason = Rejection.LoadoutInvalid;
                result.Detail = "'" + definition.Id + "' is not callable.";
                return result;
            }
            if (result.Accepted.Any(d => d.Key == key))
            {
                result.Reason = Rejection.LoadoutInvalid;
                result.Detail = "'" + definition.Id + "' is listed twice.";
                return result;
            }
            if (!rules.IsAllowed(definition.Id))
            {
                result.Reason = Rejection.LoadoutInvalid;
                result.Detail = "'" + definition.Id + "' is not allowed in this level.";
                return result;
            }
            result.Accepted.Add(definition);
        }
        for (var a = 0; a < result.Accepted.Count; a++)
            for (var b = 0; b < result.Accepted.Count; b++)
                if (a != b && CommandMatcher.StartsWith(result.Accepted[b].Code, result.Accepted[a].Code))
                {
                    result.Reason = Rejection.LoadoutInvalid;
                    result.Detail = "the code of '" + result.Accepted[a].Id + "' is the beginning of the code of '" + result.Accepted[b].Id + "' (or equal to it).";
                    return result;
                }
        return result;
    }

    /// <summary>The definition in <paramref name="slots"/> whose code would clash with <paramref name="candidate"/> placed in
    /// <paramref name="slot"/>: one code equal to, or the beginning of, the other. The slot being replaced and slots holding the
    /// candidate itself (it moves) do not count. Null when the candidate fits.</summary>
    public static OrdnanceDefinition? CodeConflict(IReadOnlyList<OrdnanceDefinition?> slots, int slot, OrdnanceDefinition candidate)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var other = slots[i];
            if (i == slot || other == null || other.Key == candidate.Key) continue;
            if (CommandMatcher.StartsWith(other.Code, candidate.Code) || CommandMatcher.StartsWith(candidate.Code, other.Code)) return other;
        }
        return null;
    }
}

/// <summary>The player's saved lobby picks as ForgeCore's store keeps them: one entry per loadout slot, null for an empty slot.
/// Ids are kept as written; whether they name a loaded definition is decided where they are used.</summary>
internal sealed class LoadoutSave
{
    public string?[] Slots = Array.Empty<string?>();
}

internal static class LoadoutFile
{
    /// <summary>The slots a read file may hold: cut to the protocol's limit and with every entry that is not an Id left empty,
    /// each reported in <paramref name="errors"/>.</summary>
    public static string?[] Clean(string?[]? slots, List<string> errors)
    {
        if (slots == null) { errors.Add("must be an object with a \"Slots\" array."); return Array.Empty<string?>(); }
        var result = new List<string?>();
        foreach (var slot in slots)
        {
            if (result.Count == Messages.MaxLoadout) { errors.Add("Slots: more than " + Messages.MaxLoadout + " entries; the rest are ignored."); break; }
            if (slot == null) result.Add(null);
            else if (slot.Length > 0) result.Add(slot);
            else { errors.Add("Slots[" + result.Count + "]: must be a call-in Id or null; left empty."); result.Add(null); }
        }
        return result.ToArray();
    }
}
