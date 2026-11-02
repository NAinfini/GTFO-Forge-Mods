using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ForgeOrdnance;

/// <summary>The rules of one level: loadout size, allowed call-ins, multipliers, the call menu switch and the developer drops. What a
/// level gets from <c>Settings.json</c> after its overrides, and what <see cref="OrdnanceApi.DeclareLevel"/> takes. Unlike a
/// <c>Settings.json</c> level, a declared one does not inherit the file's global rules: every field not set here has its default.</summary>
public sealed class OrdnanceLevelRules
{
    public int LoadoutSize { get; init; } = OrdnanceSettings.DefaultLoadoutSize;
    /// <summary>Ids players may choose; null means every definition.</summary>
    public IReadOnlyList<string>? Allowed { get; init; }
    public double CooldownMultiplier { get; init; } = 1;
    public double UsesMultiplier { get; init; } = 1;
    /// <summary>Furthest a beacon may land from where it was thrown, in metres.</summary>
    public double MaxThrowDistance { get; init; } = OrdnanceSettings.DefaultMaxThrowDistance;
    /// <summary>False when the level's developer turned the players' call-ins off: no call list, call key, lobby loadout or
    /// cooldown display, and the host refuses throws. Developer drops are not affected.</summary>
    public bool CallMenu { get; init; } = true;
    /// <summary>The level's developer drops.</summary>
    public IReadOnlyList<OrdnanceLevelDrop> Drops { get; init; } = Array.Empty<OrdnanceLevelDrop>();

    public bool IsAllowed(string id) => Allowed == null || Allowed.Contains(id, StringComparer.Ordinal);
}

/// <summary><c>Settings.json</c>: level rules with optional per-level overrides, keyed by the expedition's unique key.</summary>
internal sealed class OrdnanceSettings
{
    public const int DefaultLoadoutSize = 4;
    public const double DefaultMaxThrowDistance = 80;
    private const double MaxMultiplier = 10, MinThrowDistance = 5, MaxThrowDistanceLimit = 500;
    private static readonly string[] LevelFields = { "LoadoutSize", "Allowed", "CooldownMultiplier", "UsesMultiplier", "MaxThrowDistance", "CallMenu", "Drops" };
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
    /// <c>Settings.json</c> also sets keeps the file's rules and the declaration is reported.</summary>
    public OrdnanceSettings WithDeclared(IReadOnlyDictionary<string, OrdnanceLevelRules> declared, List<string> errors)
    {
        if (declared.Count == 0) return this;
        var levels = new Dictionary<string, OrdnanceLevelRules>(_levels, StringComparer.Ordinal);
        var keys = new HashSet<string>(_declared, StringComparer.Ordinal);
        foreach (var (key, rules) in declared)
        {
            if (levels.ContainsKey(key)) { errors.Add("DeclareLevel '" + key + "' ignored: Settings.json Levels." + key + " already sets this level; the file wins."); continue; }
            levels[key] = rules;
            keys.Add(key);
        }
        return new OrdnanceSettings(_global, levels, keys);
    }

    /// <summary>Null, or the one-line warning that <c>Settings.json</c> has per-level rules but none for <paramref name="current"/>, the
    /// level being played: a mistyped key never matches anything and would otherwise fail silently. Levels declared from code are not
    /// checked, and a declared level being played is a match.</summary>
    public string? Unmatched(string? current)
    {
        var fileKeys = _levels.Keys.Where(k => !_declared.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (fileKeys.Count == 0 || current != null && _levels.ContainsKey(current)) return null;
        return "Warning: Settings.json sets no level rules for the level being played ('" + (current ?? "unknown") + "'); it sets them only for "
               + string.Join(", ", fileKeys.Select(k => "'" + k + "'")) + ".";
    }

    /// <summary>Reports every field of rules a plugin built in code that <c>Settings.json</c> would have refused.</summary>
    public static void Check(OrdnanceLevelRules rules, string where, List<string> errors)
    {
        if (rules.LoadoutSize < 1 || rules.LoadoutSize > Messages.MaxLoadout) errors.Add(where + " LoadoutSize: must be a whole number 1.." + Messages.MaxLoadout + ".");
        if (rules.Allowed != null && rules.Allowed.Any(string.IsNullOrWhiteSpace)) errors.Add(where + " Allowed: must hold definition Ids only.");
        CheckRange(rules.CooldownMultiplier, 0, MaxMultiplier, "CooldownMultiplier", where, errors);
        CheckRange(rules.UsesMultiplier, 0, MaxMultiplier, "UsesMultiplier", where, errors);
        CheckRange(rules.MaxThrowDistance, MinThrowDistance, MaxThrowDistanceLimit, "MaxThrowDistance", where, errors);
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
    /// (<see cref="DeveloperDrops.Usable"/>); each skipped drop is reported once, also when several levels inherit its list.</summary>
    public OrdnanceSettings WithUsableDrops(DefinitionSet set, Func<string, bool> supportsDirect, List<string> errors)
    {
        var checkedLists = new Dictionary<IReadOnlyList<OrdnanceLevelDrop>, IReadOnlyList<OrdnanceLevelDrop>>(ReferenceEqualityComparer.Instance);
        OrdnanceLevelRules Check(OrdnanceLevelRules rules, string where)
        {
            if (rules.Drops.Count == 0) return rules;
            if (!checkedLists.TryGetValue(rules.Drops, out var usable))
                checkedLists[rules.Drops] = usable = DeveloperDrops.Usable(rules.Drops, set, supportsDirect, where, errors);
            return new OrdnanceLevelRules
            {
                LoadoutSize = rules.LoadoutSize, Allowed = rules.Allowed, CooldownMultiplier = rules.CooldownMultiplier, UsesMultiplier = rules.UsesMultiplier,
                MaxThrowDistance = rules.MaxThrowDistance, CallMenu = rules.CallMenu, Drops = usable
            };
        }
        string Where(string key) => _declared.Contains(key) ? "DeclareLevel " + key : "Settings.json Levels." + key;
        var global = Check(_global, "Settings.json");
        var levels = new Dictionary<string, OrdnanceLevelRules>(StringComparer.Ordinal);
        foreach (var (key, rules) in _levels) levels[key] = Check(rules, Where(key));
        return new OrdnanceSettings(global, levels, _declared);
    }

    /// <summary>Returns the settings, or null with <paramref name="errors"/> filled when the file is invalid.</summary>
    public static OrdnanceSettings? Parse(string json, List<string> errors)
    {
        const string file = "Settings.json";
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException error) { errors.Add(file + ": invalid JSON: " + error.Message); return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { errors.Add(file + ": must be a JSON object."); return null; }
            var before = errors.Count;
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
            return errors.Count == before ? new OrdnanceSettings(global, levels) : null;
        }
    }

    private static OrdnanceLevelRules Read(JsonElement owner, OrdnanceLevelRules inherited, string[] known, string where, List<string> errors)
    {
        foreach (var member in owner.EnumerateObject())
            if (Array.IndexOf(known, member.Name) < 0) errors.Add(where + " " + member.Name + ": unknown field.");
        var size = inherited.LoadoutSize;
        if (owner.TryGetProperty("LoadoutSize", out var sizeElement))
        {
            if (sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt32(out var value) && value >= 1 && value <= Messages.MaxLoadout) size = value;
            else errors.Add(where + " LoadoutSize: must be a whole number 1.." + Messages.MaxLoadout + ".");
        }
        var allowed = inherited.Allowed;
        if (owner.TryGetProperty("Allowed", out var allowedElement))
        {
            if (allowedElement.ValueKind == JsonValueKind.Null) allowed = null;
            else if (allowedElement.ValueKind != JsonValueKind.Array || allowedElement.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                errors.Add(where + " Allowed: must be an array of definition Ids, or null for all.");
            else allowed = allowedElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        var callMenu = inherited.CallMenu;
        if (owner.TryGetProperty("CallMenu", out var menuElement))
        {
            if (menuElement.ValueKind is JsonValueKind.True or JsonValueKind.False) callMenu = menuElement.GetBoolean();
            else errors.Add(where + " CallMenu: must be true or false.");
        }
        var drops = owner.TryGetProperty("Drops", out var dropsElement) ? DeveloperDrops.Parse(dropsElement, where, errors) : inherited.Drops;
        return new OrdnanceLevelRules
        {
            LoadoutSize = size, Allowed = allowed, CallMenu = callMenu, Drops = drops,
            CooldownMultiplier = Finite(owner, "CooldownMultiplier", 0, MaxMultiplier, inherited.CooldownMultiplier, where, errors),
            UsesMultiplier = Finite(owner, "UsesMultiplier", 0, MaxMultiplier, inherited.UsesMultiplier, where, errors),
            MaxThrowDistance = Finite(owner, "MaxThrowDistance", MinThrowDistance, MaxThrowDistanceLimit, inherited.MaxThrowDistance, where, errors)
        };
    }

    private static double Finite(JsonElement owner, string field, double min, double max, double fallback, string where, List<string> errors)
    {
        if (!owner.TryGetProperty(field, out var element)) return fallback;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max) return value;
        errors.Add(where + " " + field + ": must be a finite number " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture) + ".");
        return fallback;
    }
}
