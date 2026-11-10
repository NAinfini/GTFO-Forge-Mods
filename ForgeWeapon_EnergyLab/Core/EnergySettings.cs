using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeWeaponEnergyLab;

/// <summary>The rundown's own rules for the energy guns, from <c>ForgeEnergyLab/Settings.forge</c> in the rundown's custom folder (written by
/// hand): which guns players may choose (<c>Allowed</c>, for the whole rundown and per level under <c>Levels</c>, keyed by the
/// expedition's unique key such as <c>Local_31_TierA_0</c>) and the guns' numbers (<c>Tuning</c>, the same fields as a definition's
/// <c>Tuning</c>, keyed by gun Id). Nothing here adds a gun; definitions do that.</summary>
internal sealed class EnergySettings
{
    internal const string FileName = "Settings.forge";
    private static readonly string[] RootFields = { "Allowed", "Tuning", "Levels" };
    private static readonly string[] LevelFields = { "Allowed" };

    internal static readonly EnergySettings None = new(null, new Dictionary<string, IReadOnlyList<string>?>(), new Dictionary<string, JsonElement>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>?> _levels;

    private EnergySettings(IReadOnlyList<string>? allowed, IReadOnlyDictionary<string, IReadOnlyList<string>?> levels, IReadOnlyDictionary<string, JsonElement> tuning)
    {
        Allowed = allowed; _levels = levels; Tuning = tuning;
    }

    /// <summary>The Ids players may choose in every level that sets no list of its own; null means every gun.</summary>
    internal IReadOnlyList<string>? Allowed { get; }

    /// <summary>Gun Id to the tuning fields the rundown sets; each is checked when the guns are frozen.</summary>
    internal IReadOnlyDictionary<string, JsonElement> Tuning { get; }

    /// <summary>True when some level, or the whole rundown, offers fewer than every gun.</summary>
    internal bool Restricts => Allowed != null || _levels.Values.Any(list => list != null);

    /// <summary>The Ids players may choose in the level with this expedition key (null: no expedition selected); null means every gun.</summary>
    internal IReadOnlyList<string>? AllowedFor(string? levelKey) => levelKey != null && _levels.TryGetValue(levelKey, out var list) ? list : Allowed;

    /// <summary>Every Allowed Id that <paramref name="isLoaded"/> does not know, once.</summary>
    internal IEnumerable<string> UnknownAllowed(Func<string, bool> isLoaded)
        => _levels.Values.Prepend(Allowed).Where(list => list != null).SelectMany(list => list!).Where(id => !isLoaded(id)).Distinct(StringComparer.Ordinal);

    /// <summary>The Allowed lists as one text for the multiplayer check, so a host and a client with different lists are refused. Tuning is
    /// part of every gun's own tuning, which the check already holds.</summary>
    internal string Canonical()
    {
        static string List(IReadOnlyList<string>? ids) => ids == null ? "*" : string.Join(",", ids.OrderBy(i => i, StringComparer.Ordinal));
        return "allowed:" + List(Allowed) + string.Concat(_levels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => ";" + p.Key + ":" + List(p.Value)));
    }

    /// <summary>The settings, or null with <paramref name="errors"/> filled when the file is invalid (it is then ignored whole).</summary>
    internal static EnergySettings? Parse(string json, List<string> errors)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, EnergyConfiguration.FileOptions); }
        catch (JsonException error) { errors.Add(FileName + ": invalid JSON at line " + (error.LineNumber + 1) + ", byte " + (error.BytePositionInLine + 1) + "."); return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { errors.Add(FileName + ": must be a JSON object."); return null; }
            var before = errors.Count;
            IReadOnlyList<string>? allowed = null;
            var levels = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            var tuning = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            Guard(errors, () => { ForgeCore.StrictJson.UniqueMembers(root); ForgeCore.StrictJson.OnlyKnown(root, "", RootFields); });
            if (root.TryGetProperty("Allowed", out var allowedElement)) Guard(errors, () => allowed = Ids(allowedElement, "Allowed"));
            if (root.TryGetProperty("Tuning", out var tuningElement))
                Guard(errors, () =>
                {
                    if (tuningElement.ValueKind != JsonValueKind.Object) throw new ForgeCore.StrictJsonException("Tuning", "must be an object keyed by gun Id.");
                    ForgeCore.StrictJson.UniqueMembers(tuningElement, "Tuning.");
                    foreach (var entry in tuningElement.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Object) throw new ForgeCore.StrictJsonException("Tuning." + entry.Name, "must be an object of tuning fields.");
                        tuning[entry.Name] = entry.Value.Clone();
                    }
                });
            if (root.TryGetProperty("Levels", out var levelsElement))
                Guard(errors, () =>
                {
                    if (levelsElement.ValueKind != JsonValueKind.Object) throw new ForgeCore.StrictJsonException("Levels", "must be an object keyed by the expedition's unique key.");
                    ForgeCore.StrictJson.UniqueMembers(levelsElement, "Levels.");
                    foreach (var level in levelsElement.EnumerateObject())
                    {
                        var where = "Levels." + level.Name;
                        if (level.Value.ValueKind != JsonValueKind.Object) throw new ForgeCore.StrictJsonException(where, "must be an object.");
                        ForgeCore.StrictJson.UniqueMembers(level.Value, where + ".");
                        ForgeCore.StrictJson.OnlyKnown(level.Value, where + ".", LevelFields);
                        // A level that writes no Allowed offers what the rundown offers.
                        levels[level.Name] = level.Value.TryGetProperty("Allowed", out var list) ? Ids(list, where + ".Allowed") : allowed;
                    }
                });
            return errors.Count == before ? new EnergySettings(allowed, levels, tuning) : null;
        }
    }

    private static void Guard(List<string> errors, Action read)
    {
        try { read(); }
        catch (ForgeCore.StrictJsonException error) { errors.Add(FileName + ": " + error.Field + (error.Field.Length == 0 ? "" : ": ") + error.Message); }
    }

    private static IReadOnlyList<string>? Ids(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString())))
            throw new ForgeCore.StrictJsonException(field, "must be an array of gun Ids, or null for every gun.");
        return element.EnumerateArray().Select(e => e.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }
}
