using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>The rules of one level: loadout size, allowed call-ins, multipliers, the two player switches (<see cref="LobbyPicker"/>, <see cref="CallMenu"/>), the authored loadouts and the developer drops. What a
/// level gets from <c>Settings.forge</c> after its overrides, and what <see cref="OrdnanceApi.DeclareLevel"/> takes. Unlike a
/// <c>Settings.forge</c> level, a declared one does not inherit the file's global rules: every field not set here has its default.</summary>
public sealed record OrdnanceLevelRules
{
    public int LoadoutSize { get; init; } = OrdnanceSettings.DefaultLoadoutSize;
    /// <summary>Ids players may choose; null means every definition.</summary>
    public IReadOnlyList<string>? Allowed { get; init; }
    public double CooldownMultiplier { get; init; } = 1;
    public double UsesMultiplier { get; init; } = 1;
    /// <summary>Furthest a beacon may land from where it was thrown, in metres.</summary>
    public double MaxThrowDistance { get; init; } = OrdnanceSettings.DefaultMaxThrowDistance;
    /// <summary>False when the level's developer turned the lobby choice off: no ORDNANCE button, no loadout row on the player bars, and
    /// players' own picks are not used; the level's <see cref="Loadouts"/> say who has what. Independent of <see cref="CallMenu"/>.</summary>
    public bool LobbyPicker { get; init; } = true;
    /// <summary>False when the level's developer turned the in-level call-ins off: no call list, call key or cooldown display, and the host
    /// refuses throws. Developer drops are not affected. Independent of <see cref="LobbyPicker"/>.</summary>
    public bool CallMenu { get; init; } = true;
    /// <summary>The call-ins the author gives each player while <see cref="LobbyPicker"/> is off: Ids by player slot ("0".."3") with "All" for
    /// the rest; a slot's own list replaces "All". A player nothing is written for has none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Loadouts { get; init; } = AuthoredLoadouts.None;
    /// <summary>The level's developer drops.</summary>
    public IReadOnlyList<OrdnanceLevelDrop> Drops { get; init; } = Array.Empty<OrdnanceLevelDrop>();

    public bool IsAllowed(string id) => Allowed == null || Allowed.Contains(id, StringComparer.Ordinal);
}

/// <summary><c>Settings.forge</c>: level rules with optional per-level overrides, keyed by the expedition's unique key.</summary>
internal sealed class OrdnanceSettings
{
    public const int DefaultLoadoutSize = 4;
    public const double DefaultMaxThrowDistance = 80;
    private const double MaxMultiplier = 10, MinThrowDistance = 5, MaxThrowDistanceLimit = 500;
    private static readonly string[] LevelFields = { "LoadoutSize", "Allowed", "CooldownMultiplier", "UsesMultiplier", "MaxThrowDistance", "LobbyPicker", "CallMenu", "Loadouts", "Drops" };
    private static readonly string[] RootFields = LevelFields.Concat(new[] { "Levels" }).ToArray();
    private readonly OrdnanceLevelRules _global;
    private readonly Dictionary<string, OrdnanceLevelRules> _levels;
    private readonly HashSet<string> _declared;

    private OrdnanceSettings(OrdnanceLevelRules global, Dictionary<string, OrdnanceLevelRules> levels, HashSet<string>? declared = null)
    {
        _global = global; _levels = levels; _declared = declared ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public static OrdnanceSettings Default { get; } = new(new OrdnanceLevelRules(), new Dictionary<string, OrdnanceLevelRules>());

    public OrdnanceLevelRules For(string? levelKey) => levelKey != null && _levels.TryGetValue(levelKey, out var level) ? level : _global;

    /// <summary>The same settings with the levels plugins declared (<see cref="OrdnanceApi.DeclareLevel"/>) added. A level that
    /// <c>Settings.forge</c> also sets keeps the file's rules and the declaration is reported.</summary>
    public OrdnanceSettings WithDeclared(IReadOnlyDictionary<string, OrdnanceLevelRules> declared, List<string> errors)
    {
        if (declared.Count == 0) return this;
        var levels = new Dictionary<string, OrdnanceLevelRules>(_levels, StringComparer.Ordinal);
        var keys = new HashSet<string>(_declared, StringComparer.Ordinal);
        foreach (var (key, rules) in declared)
        {
            if (levels.ContainsKey(key)) { errors.Add("DeclareLevel '" + key + "' ignored: Settings.forge Levels." + key + " already sets this level; the file wins."); continue; }
            levels[key] = rules;
            keys.Add(key);
        }
        return new OrdnanceSettings(_global, levels, keys);
    }

    /// <summary>Reports every field of rules a plugin built in code that <c>Settings.forge</c> would have refused.</summary>
    public static void Check(OrdnanceLevelRules rules, string where, List<string> errors)
    {
        if (rules.LoadoutSize < 1 || rules.LoadoutSize > Messages.MaxLoadout) errors.Add(where + " LoadoutSize: must be a whole number 1.." + Messages.MaxLoadout + ".");
        if (rules.Allowed != null && rules.Allowed.Any(string.IsNullOrWhiteSpace)) errors.Add(where + " Allowed: must hold definition Ids only.");
        CheckRange(rules.CooldownMultiplier, 0, MaxMultiplier, "CooldownMultiplier", where, errors);
        CheckRange(rules.UsesMultiplier, 0, MaxMultiplier, "UsesMultiplier", where, errors);
        CheckRange(rules.MaxThrowDistance, MinThrowDistance, MaxThrowDistanceLimit, "MaxThrowDistance", where, errors);
        AuthoredLoadouts.Check(rules.Loadouts, where, errors);
        DeveloperDrops.Check(rules.Drops, where, errors);
    }

    private static void CheckRange(double value, double min, double max, string field, string where, List<string> errors)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            errors.Add(where + " " + field + ": must be a finite number " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>Allowed ids no definition carries, over the global rule and every level override.</summary>
    public IEnumerable<string> UnknownAllowed(DefinitionSet set)
        => _levels.Values.Prepend(_global).Where(s => s.Allowed != null).SelectMany(s => s.Allowed!).Where(id => !set.TryGetById(id, out _)).Distinct(StringComparer.Ordinal);

    /// <summary>The same rules with every <c>Drops</c> list cut to the drops that can run with the loaded call-ins
    /// (<see cref="DeveloperDrops.Usable"/>; each skipped drop is reported once, also when several levels inherit its list) and every
    /// <c>Loadouts</c> cut to the call-ins its players could be given (<see cref="AuthoredLoadouts.Usable"/>).</summary>
    public OrdnanceSettings WithUsable(DefinitionSet set, Func<string, bool> supportsDirect, Func<OrdnanceDefinition, IReadOnlyDictionary<string, double>, string?> checkOverrides, List<string> errors)
    {
        var checkedLists = new Dictionary<IReadOnlyList<OrdnanceLevelDrop>, IReadOnlyList<OrdnanceLevelDrop>>(ReferenceEqualityComparer.Instance);
        OrdnanceLevelRules Check(OrdnanceLevelRules rules, string where)
        {
            var usable = rules.Drops;
            if (usable.Count != 0 && !checkedLists.TryGetValue(rules.Drops, out usable))
                checkedLists[rules.Drops] = usable = DeveloperDrops.Usable(rules.Drops, set, supportsDirect, checkOverrides, where, errors);
            return rules with { Drops = usable, Loadouts = AuthoredLoadouts.Usable(rules, set, where, errors) };
        }
        string Where(string key) => _declared.Contains(key) ? "DeclareLevel " + key : "Settings.forge Levels." + key;
        var global = Check(_global, "Settings.forge");
        var levels = new Dictionary<string, OrdnanceLevelRules>(StringComparer.Ordinal);
        foreach (var (key, rules) in _levels) levels[key] = Check(rules, Where(key));
        return new OrdnanceSettings(global, levels, _declared);
    }

    /// <summary>Returns the settings. A field or entry that is wrong is reported in <paramref name="errors"/> and only that one is lost (a bad field keeps
    /// its inherited value, a bad drop or loadout key is skipped); null only when the file is not a JSON object at all.</summary>
    public static OrdnanceSettings? Parse(string json, List<string> errors)
    {
        const string file = "Settings.forge";
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException error) { errors.Add(file + ": invalid JSON: " + error.Message); return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { errors.Add(file + ": must be a JSON object."); return null; }
            var global = Read(root, new OrdnanceLevelRules(), RootFields, file, errors);
            var levels = new Dictionary<string, OrdnanceLevelRules>(StringComparer.Ordinal);
            if (root.TryGetProperty("Levels", out var levelsElement))
            {
                if (levelsElement.ValueKind != JsonValueKind.Object) errors.Add(file + " Levels: must be an object keyed by level.");
                else
                    foreach (var level in levelsElement.EnumerateObject())
                    {
                        if (level.Value.ValueKind != JsonValueKind.Object) { errors.Add(file + " Levels." + level.Name + ": must be an object."); continue; }
                        levels[level.Name] = Read(level.Value, global, LevelFields, file + " Levels." + level.Name, errors);
                    }
            }
            return new OrdnanceSettings(global, levels);
        }
    }

    private static OrdnanceLevelRules Read(JsonElement owner, OrdnanceLevelRules inherited, string[] known, string where, List<string> errors)
    {
        foreach (var member in owner.EnumerateObject())
            if (Array.IndexOf(known, member.Name) < 0) errors.Add(where + " " + member.Name + ": unknown field.");
        var size = StrictRead.Collect(() => (int)ForgeCore.StrictJson.Integer(owner, "", "LoadoutSize", inherited.LoadoutSize, 1, Messages.MaxLoadout),
            where, errors, inherited.LoadoutSize);
        var allowed = inherited.Allowed;
        if (owner.TryGetProperty("Allowed", out var allowedElement))
        {
            if (allowedElement.ValueKind == JsonValueKind.Null) allowed = null;
            else if (allowedElement.ValueKind != JsonValueKind.Array || allowedElement.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                errors.Add(where + " Allowed: must be an array of definition Ids, or null for all.");
            else allowed = allowedElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        var callMenu = inherited.CallMenu;
        var callMenuWritten = false;
        if (owner.TryGetProperty("CallMenu", out var menuElement))
        {
            if (menuElement.ValueKind is JsonValueKind.True or JsonValueKind.False) { callMenu = menuElement.GetBoolean(); callMenuWritten = true; }
            else errors.Add(where + " CallMenu: must be true or false.");
        }
        // A file that writes only CallMenu keeps its old meaning: CallMenu false turns the lobby choice off with it. LobbyPicker, when written, decides alone.
        var lobbyPicker = callMenuWritten ? callMenu : inherited.LobbyPicker;
        if (owner.TryGetProperty("LobbyPicker", out var pickerElement))
        {
            if (pickerElement.ValueKind is JsonValueKind.True or JsonValueKind.False) lobbyPicker = pickerElement.GetBoolean();
            else errors.Add(where + " LobbyPicker: must be true or false.");
        }
        var loadouts = owner.TryGetProperty("Loadouts", out var loadoutsElement) ? AuthoredLoadouts.Parse(loadoutsElement, where, errors) : inherited.Loadouts;
        var drops = owner.TryGetProperty("Drops", out var dropsElement) ? DeveloperDrops.Parse(dropsElement, where, errors) : inherited.Drops;
        return new OrdnanceLevelRules
        {
            LoadoutSize = size,
            Allowed = allowed,
            LobbyPicker = lobbyPicker,
            CallMenu = callMenu,
            Loadouts = loadouts,
            Drops = drops,
            CooldownMultiplier = Finite(owner, "CooldownMultiplier", 0, MaxMultiplier, inherited.CooldownMultiplier, where, errors),
            UsesMultiplier = Finite(owner, "UsesMultiplier", 0, MaxMultiplier, inherited.UsesMultiplier, where, errors),
            MaxThrowDistance = Finite(owner, "MaxThrowDistance", MinThrowDistance, MaxThrowDistanceLimit, inherited.MaxThrowDistance, where, errors)
        };
    }

    private static double Finite(JsonElement owner, string field, double min, double max, double fallback, string where, List<string> errors)
        => StrictRead.Collect(() => ForgeCore.StrictJson.Number(owner, "", field, fallback, min, max), where, errors, fallback);
}
