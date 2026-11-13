using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>The <c>Loadouts</c> of a level's rules: the call-ins its author gives each player, by the player's slot in the party
/// (<c>"0"</c>..<c>"3"</c>), with <c>"All"</c> for everyone else. Used while <c>LobbyPicker</c> is off. The host hands them to the same
/// loadout path a lobby pick takes (<see cref="LoadoutValidator"/>, the slot book, the broadcast and the late-join resend).</summary>
internal static class AuthoredLoadouts
{
    internal const string All = "All";
    internal const int PlayerSlots = 4;
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None = new Dictionary<string, IReadOnlyList<string>>();

    private static bool IsKey(string key) => key == All || key.Length == 1 && key[0] >= '0' && key[0] < '0' + PlayerSlots;

    /// <summary>The Ids written for the player in <paramref name="slot"/>: its own list, else <c>All</c>, else null (nothing written).</summary>
    internal static IReadOnlyList<string>? For(IReadOnlyDictionary<string, IReadOnlyList<string>> loadouts, int slot)
    {
        if (slot >= 0 && slot < PlayerSlots && loadouts.TryGetValue(slot.ToString(System.Globalization.CultureInfo.InvariantCulture), out var own)) return own;
        return loadouts.TryGetValue(All, out var everyone) ? everyone : null;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(JsonElement element, string where, List<string> errors)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) { errors.Add(where + " Loadouts: must be an object keyed by player slot (\"0\"..\"3\") or \"All\"."); return result; }
        foreach (var member in element.EnumerateObject())
        {
            var at = where + " Loadouts." + member.Name;
            if (!IsKey(member.Name)) { errors.Add(at + ": unknown key; use \"All\" or a player slot \"0\".." + "\"" + (PlayerSlots - 1) + "\"."); continue; }
            if (member.Value.ValueKind != JsonValueKind.Array || member.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
            { errors.Add(at + ": must be an array of call-in Ids."); continue; }
            var ids = member.Value.EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (ids.Length > Messages.MaxLoadout) { errors.Add(at + ": at most " + Messages.MaxLoadout + " call-ins."); continue; }
            result[member.Name] = ids;
        }
        return result;
    }

    /// <summary>Reports every key and list of loadouts a plugin built in code that <see cref="Parse"/> would have refused.</summary>
    internal static void Check(IReadOnlyDictionary<string, IReadOnlyList<string>> loadouts, string where, List<string> errors)
    {
        foreach (var (key, ids) in loadouts)
        {
            var at = where + " Loadouts." + key;
            if (!IsKey(key)) errors.Add(at + ": unknown key; use \"All\" or a player slot \"0\".." + "\"" + (PlayerSlots - 1) + "\".");
            else if (ids == null || ids.Count > Messages.MaxLoadout || ids.Any(string.IsNullOrWhiteSpace)) errors.Add(at + ": must be 0.." + Messages.MaxLoadout + " call-in Ids.");
        }
    }

    /// <summary>The loadouts of <paramref name="rules"/> cut to the call-ins its players could be given (loaded, callable, allowed, within the
    /// slot count, no code that is the beginning of another): the same checks a lobby loadout gets, each refusal reported once with its
    /// place and left out. An empty list is not an error: a player nothing is written for has no call-ins.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Usable(OrdnanceLevelRules rules, DefinitionSet set, string where, List<string> errors)
    {
        if (rules.Loadouts.Count == 0) return rules.Loadouts;
        if (rules.LobbyPicker) errors.Add(where + " Loadouts: ignored while LobbyPicker is true (players choose in the lobby); set LobbyPicker to false to give them these.");
        else if (!rules.CallMenu) errors.Add(where + " Loadouts: have no effect while CallMenu is false (no call list, no calls).");
        var usable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, ids) in rules.Loadouts)
        {
            var kept = new List<OrdnanceDefinition>(ids.Count);
            for (var i = 0; i < ids.Count; i++)
            {
                var at = where + " Loadouts." + key + "[" + i + "]";
                if (!set.TryGetById(ids[i], out var definition)) { errors.Add(at + ": '" + ids[i] + "' is not a loaded call-in; left out."); continue; }
                var trial = kept.Select(d => d.Key).Append(definition.Key).ToList();
                var result = LoadoutValidator.Validate(trial, set, rules);
                if (!result.Ok) { errors.Add(at + ": " + result.Detail + " Left out."); continue; }
                kept.Add(definition);
            }
            usable[key] = kept.Select(d => d.Id).ToArray();
        }
        return usable;
    }
}
