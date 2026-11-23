using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

/// <summary><paramref name="PersistentId"/> is the number the website issued, from the package's <c>ids</c>.</summary>
public sealed record RundownBlock(string Id, string Type, JsonObject Data, uint PersistentId);
public sealed record RundownPack(string Package, string Version, string Title, string Rundown,
    IReadOnlyList<RundownBlock> Blocks, JsonObject? PlayerOverrides, string Path)
{
    public uint RundownId { get; } = Blocks.Single(block => block.Id == Rundown && block.Type == "Rundown").PersistentId;
    /// <summary>The replaced original menu position, 1 to 8. 0 marks a single-Rundown package (<c>menuMode: "single"</c>):
    /// the main menu lists only this Rundown and opens it directly, and the other seven originals are not shown.</summary>
    public int MenuSlot { get; init; }
    internal bool SingleRundown => MenuSlot == 0;
    public string? DefaultTextLanguage { get; init; }
    /// <summary>Levels whose name has several languages (<c>levelNames</c>): <c>(tier letter, index in the tier)</c> → the
    /// package's Text block with every language column. The level's PublicName holds the primary-language text.</summary>
    internal IReadOnlyDictionary<(char Tier, int Index), uint> LevelNames { get; init; } = new Dictionary<(char, int), uint>();
    internal IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    internal bool DiscoveryAdmitted { get; init; }

    /// <summary>The authored tier and index of every level of this package's Rundown.</summary>
    internal IEnumerable<(char Tier, int Index)> AuthoredLevels()
    {
        var block = Blocks.Single(row => row.Type == "Rundown" && row.Id == Rundown);
        for (char tier = 'A'; tier <= 'E'; tier++)
            if (block.Data["Tier" + tier] is JsonArray rows)
                for (int index = 0; index < rows.Count; index++) if (rows[index] is JsonObject) yield return (tier, index);
    }
}
public readonly record struct RundownDiagnostic(string Code, string Package, string Entry, string Other, string Detail,
    int Slot = -1);

/// <summary>Startup snapshot of all installed Rundowns. No package can win a collision by directory order.</summary>
public static class RundownPacks
{
    public const string SlotConflictCode = "rundown.slot-conflict";
    public const string RejectedCode = "rundown.rejected";
    public const int MaximumBytes = Wire.PackageLimits.MaximumPublishedBytes;
    private static readonly Regex PackagePattern = new(@"\A[A-Za-z0-9_]{1,128}-[A-Za-z0-9_]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex EntryPattern = new(@"\A[a-z0-9](?:[a-z0-9_-]{0,62}[a-z0-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex DependencyPattern = new(@"\A([A-Za-z0-9_]{1,128}-[A-Za-z0-9_]{1,128})-(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<RundownPack> Discover(string root, Action<RundownDiagnostic> report)
    {
        var packs = new List<RundownPack>();
        var refusedRoots = new HashSet<string>(StringComparer.Ordinal);
        var headers = new List<(string Package, string Path)>();
        long bytes = 0; int count = 0;
        string[] directories;
        try
        {
            if (!CanonicalChild(root, "", "plugins", true, optional: true)) return packs;
            string plugins = PublishedResources.ResolveWithinRoot(root, "plugins");
            var children = Children(plugins);
            directories = children.Where(path => (File.GetAttributes(path) & FileAttributes.Directory) != 0).ToArray();
            foreach (var group in children.GroupBy(path => System.IO.Path.GetFileName(path).TrimEnd(' ', '.'), StringComparer.OrdinalIgnoreCase))
                if (group.Count() != 1 || System.IO.Path.GetFileName(group.First()) != group.Key)
                    foreach (var path in group) refusedRoots.Add(System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'));
        }
        catch (Exception error) when (ReadFailure(error))
        { report(new(RejectedCode, "", "plugins", "", error.Message)); return packs; }
        foreach (string directory in directories)
        {
            string packageRoot = System.IO.Path.GetRelativePath(root, directory).Replace('\\', '/');
            string relative = packageRoot + "/forge/rundown.forge";
            try
            {
                if (!CanonicalChild(root, packageRoot, "forge", true, optional: true)
                    || !CanonicalChild(root, packageRoot + "/forge", "rundown.forge", false, optional: true)) continue;
                if (++count > 128) throw new InvalidDataException("Installed Rundown count budget exceeded.");
                string path = PublishedResources.ResolveWithinRoot(root, relative);
                long size = new FileInfo(path).Length;
                if (size < 1 || size > MaximumBytes || size > Wire.PackageLimits.MaximumPublishedBytes - bytes)
                    throw new InvalidDataException("Installed Rundown byte budget exceeded.");
                bytes += size;
                if (!PublishedResources.TryRead(new(relative, path, size), MaximumBytes,
                    detail => report(new(RejectedCode, packageRoot, relative, "", detail)), out var text)) continue;
                // A syntactically valid owner remains in the conflict set even when its block/reference
                // payload is refused. Otherwise a malformed second version would let its sibling win.
                if (TryReadOwner(text!, out var owner)) headers.Add((owner, relative));
                var pack = Parse(text!, relative);
                int packIndex = packs.Count; packs.Add(pack);
                bool canonical = packageRoot == "plugins/" + pack.Package && !refusedRoots.Contains(packageRoot);
                // Distribution manifest/README/icon belong to the archive, not the runtime projection.
                // The wrapper pins exact dependencies; transport import verifies its archive metadata.
                packs[packIndex] = pack with { DiscoveryAdmitted = canonical };
                if (!canonical) report(new(RejectedCode, pack.Package, relative, "", "Noncanonical or conflicting installed package root."));
            }
            catch (Exception error) when (ReadFailure(error))
            { report(new(RejectedCode, System.IO.Path.GetFileName(directory), relative, "", error.Message)); }
        }
        // Do not drop an invalid root until all identities were compared: two versions/copies reject both.
        var refused = new HashSet<string>(packs.Where(pack => !pack.DiscoveryAdmitted).Select(pack => pack.Package), StringComparer.OrdinalIgnoreCase);
        foreach (var group in headers.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            refused.Add(group.Key);
            report(new(RejectedCode, group.Key, group.Key, string.Join(",", group.Select(pack => pack.Path)), "Duplicate installed Rundown package."));
        }
        return Array.AsReadOnly(packs.Where(pack => !refused.Contains(pack.Package)).ToArray());
    }

    private static bool TryReadOwner(string text, out string package)
    {
        package = "";
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (string name in new[] { "format", "package", "version" })
            if (root.EnumerateObject().Count(property => property.Name == name) != 1) return false;
        return root.GetProperty("format").ValueKind == JsonValueKind.String && root.GetProperty("format").GetString() == "forge-rundown-pack"
            && root.GetProperty("package").ValueKind == JsonValueKind.String && PackagePattern.IsMatch(package = root.GetProperty("package").GetString()!)
            && root.GetProperty("version").ValueKind == JsonValueKind.String
            && Regex.IsMatch(root.GetProperty("version").GetString()!, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z");
    }
    internal static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate Rundown JSON field: " + property.Name);
                RequireUniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RequireUniqueFields(child);
    }

    private static bool ReadFailure(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException
        or JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException or NotSupportedException;
    private static string[] Children(string path)
    {
        var children = new List<string>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (children.Count == 4096) throw new InvalidDataException("Rundown directory child budget exceeded.");
            children.Add(child);
        }
        children.Sort(StringComparer.Ordinal); return children.ToArray();
    }
    private static bool CanonicalChild(string root, string parent, string name, bool directory, bool optional = false)
    {
        string parentPath = parent.Length == 0 ? System.IO.Path.GetDirectoryName(PublishedResources.ResolveWithinRoot(root, name))!
            : PublishedResources.ResolveWithinRoot(root, parent);
        var matches = Children(parentPath).Where(path => string.Equals(System.IO.Path.GetFileName(path).TrimEnd(' ', '.'), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && optional) return false;
        if (matches.Length != 1 || System.IO.Path.GetFileName(matches[0]) != name)
            throw new InvalidDataException("Rundown path is missing or has a Windows alias: " + name);
        var path = PublishedResources.ResolveWithinRoot(root, parent.Length == 0 ? name : parent + "/" + name);
        if (((File.GetAttributes(path) & FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("Unexpected Rundown path kind: " + name);
        return true;
    }
    private static IReadOnlyList<string> ReadDependencies(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256) throw new InvalidDataException("Invalid Rundown dependencies.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var rows = new List<string>();
        foreach (var row in value.EnumerateArray())
        {
            string text = row.ValueKind == JsonValueKind.String ? row.GetString()! : "";
            var match = DependencyPattern.Match(text);
            if (!match.Success || !names.Add(match.Groups[1].Value)) throw new InvalidDataException("Invalid or duplicate exact Rundown dependency.");
            rows.Add(text);
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    public static RundownPack Parse(string text, string path)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Rundown document fields.");
        RequireUniqueFields(document.RootElement);
        var dependencies = document.RootElement.TryGetProperty("dependencies", out var pins) ? ReadDependencies(pins) : Array.Empty<string>();
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject
            ?? throw new InvalidDataException("Rundown must be an object.");
        string Read(string key) => root[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing " + key);
        if (Read("format") != "forge-rundown-pack") throw new InvalidDataException("Unsupported Rundown format.");
        string package = Read("package"), version = Read("version"), title = Read("title"), rundown = Read("rundown");
        // menuMode is absent for the position mode; "single" owns the whole menu and takes no menuSlot.
        bool single = false;
        if (document.RootElement.TryGetProperty("menuMode", out var mode))
        {
            string? modeName = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
            if (modeName is not ("slot" or "single")) throw new InvalidDataException("Rundown menuMode must be \"slot\" or \"single\".");
            single = modeName == "single";
        }
        int menuSlot = 0;
        bool hasSlot = document.RootElement.TryGetProperty("menuSlot", out var slot);
        if (single ? hasSlot : !hasSlot || slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt32(out menuSlot) || menuSlot is < 1 or > 8)
            throw new InvalidDataException(single ? "A single-Rundown package takes no menuSlot." : "Rundown menuSlot must be an integer from 1 to 8.");
        string? defaultTextLanguage = null;
        if (document.RootElement.TryGetProperty("defaultTextLanguage", out var language))
        {
            if (language.ValueKind != JsonValueKind.String
                || !RuntimeEnumSets.Members("text_language").Contains(language.GetString()!, StringComparer.Ordinal))
                throw new InvalidDataException("Invalid Rundown defaultTextLanguage.");
            defaultTextLanguage = language.GetString();
        }
        if (!PackagePattern.IsMatch(package) || !Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")
            || title.Length is 0 or > 250) throw new InvalidDataException("Invalid Rundown identity or title.");
        var array = root["blocks"] as JsonArray ?? throw new InvalidDataException("Missing blocks.");
        if (array.Count is 0 or > 4096) throw new InvalidDataException("Rundown block budget exceeded.");
        // The website issued every number; the package carries exactly one per block.
        var ids = root["ids"] as JsonObject ?? throw new InvalidDataException("Missing ids.");
        if (ids.Count != array.Count) throw new InvalidDataException("Rundown ids do not match its blocks.");
        var blocks = new List<RundownBlock>();
        foreach (var node in array)
        {
            var row = node as JsonObject ?? throw new InvalidDataException("Invalid block.");
            string id = row["id"]?.GetValue<string>() ?? "", type = row["type"]?.GetValue<string>() ?? "";
            if (!id.StartsWith(package + "/", StringComparison.Ordinal) || !EntryPattern.IsMatch(id.Substring(package.Length + 1))
                || !Regex.IsMatch(type, @"\A[A-Za-z][A-Za-z0-9]{0,63}\z") || type is "GameSetup" or "Player")
                throw new InvalidDataException("Invalid block identity: " + id);
            var data = row["data"] as JsonObject ?? throw new InvalidDataException("Missing block data: " + id);
            if (data.ContainsKey("persistentID")) throw new InvalidDataException("Package declares numeric persistentID: " + id);
            if (ids[id] is not JsonValue issued || !issued.TryGetValue<uint>(out uint number))
                throw new InvalidDataException("Rundown issues no number for " + id + ".");
            if (!ContentDataBlockIds.InIssuedRange(type, number))
                throw new InvalidDataException("Issued number " + number + " for " + id + " is outside the " + type + " range.");
            blocks.Add(new(id, type, data, number));
        }
        if (blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Count() != blocks.Count
            || blocks.Count(block => block.Type == "Rundown") != 1 || !blocks.Any(block => block.Id == rundown && block.Type == "Rundown"))
            throw new InvalidDataException("Duplicate identity or missing unique Rundown.");
        var player = root["playerOverrides"] as JsonObject;
        if (root["playerOverrides"] != null && player == null || player?.ContainsKey("persistentID") == true)
            throw new InvalidDataException("Invalid player overrides.");
        if (player != null) ValidatePlayerOverrides(player);
        return new(package, version, title, rundown, Array.AsReadOnly(blocks.ToArray()), player, path)
        {
            MenuSlot = menuSlot,
            DefaultTextLanguage = defaultTextLanguage,
            LevelNames = ReadLevelNames(root["levelNames"], blocks),
            Dependencies = dependencies
        };
    }
    private static IReadOnlyDictionary<(char Tier, int Index), uint> ReadLevelNames(JsonNode? node, List<RundownBlock> blocks)
    {
        var names = new Dictionary<(char, int), uint>();
        if (node == null) return names;
        if (node is not JsonObject rows || rows.Count > 4096) throw new InvalidDataException("Invalid Rundown levelNames.");
        foreach (var row in rows)
        {
            var key = Regex.Match(row.Key, @"\A([A-E]):(0|[1-9][0-9]{0,3})\z", RegexOptions.CultureInvariant);
            if (!key.Success || row.Value is not JsonValue value || !value.TryGetValue<uint>(out uint text)
                || !blocks.Any(block => block.Type == "Text" && block.PersistentId == text))
                throw new InvalidDataException("Invalid Rundown level name: " + row.Key);
            names.Add((key.Groups[1].Value[0], int.Parse(key.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)), text);
        }
        return names;
    }

    // Mirrors the authoring rules in rundown-player.ts.
    private static readonly IReadOnlyDictionary<string, (string Kind, double Min, double Max)> PlayerFields = PlayerRules();
    private static IReadOnlyDictionary<string, (string Kind, double Min, double Max)> PlayerRules()
    {
        var fields = new Dictionary<string, (string, double, double)>(StringComparer.Ordinal);
        void Add(string paths, string kind = "number")
        {
            (double min, double max) = kind switch
            {
                "integer" => (int.MinValue, int.MaxValue),
                "boolean" => (0, 1),
                _ => (-(double)float.MaxValue, (double)float.MaxValue)
            };
            foreach (string path in paths.Split(' ')) fields.Add(path, (kind, min, max));
        }
        Add("health");
        Add("healthRegenPerSecond healthRegenStartDelayAfterDamage healthRegenDelay fallDamageMinHeight fallDamageMaxHeight fallDamageMin fallDamageMax StaminaTimeBeforeResting");
        Add("healthRegenRelMax StaminaMinimumCapWhenNotInCombat StaminaMaximumCapWhenInCombat");
        Add("friendlyFireMulti walkMoveSpeed runMoveSpeed airMoveSpeed crouchMoveSpeed ladderMoveSpeed jumpVelInitial");
        Add("jumpVerticalVelocityMax");
        Add("jumpGravityMulDefault jumpGravityMulButtonReleased jumpGravityMulAfterPeak jumpGravityMulFalling quickStrafeTime");
        Add("throttleSmoothAcc throttleSmoothStop throttleSmoothVertical walkFootstepLength runFootstepLength crouchFootstepLength");
        Add("camFovRunDif ChromaticAbberationIntensityMax implantSmallTriggerDelay implantBigTriggerDelay");
        Add("smallBatteryConsumtionPerSec mediumBatteryConsumtionPerSec largeBatteryConsumtionPerSec");
        Add("noAirTimeToEmpty noAirDamageRel noAirDamageDelay");
        foreach (string vector in new[] { "camPosDefault", "camPosCrouch" })
            foreach (string axis in new[] { "x", "y", "z" })
                Add(vector + "." + axis);
        foreach (string dof in new[] { "DofDefault", "DofInElevator", "DofInTerminal" })
        {
            Add(dof + ".enabled", kind: "boolean");
            Add(dof + ".focusDistance " + dof + ".aperture " + dof + ".focalLength");
        }
        foreach (string kind in new[] { "Standard", "Special", "Class" })
            foreach (string part in new[] { "Initial", "InitialOnDropin", "MaxCap", "ResourcePackMaxCap" })
                Add("Ammo" + kind + part, kind: "integer");
        Add("StaminaRegenRestingInCombat StaminaRegenNotRestingInCombat StaminaRegenRestingOutOfCombat StaminaRegenNotRestingOutOfCombat StaminaMaximumCapWhenInCombatFallRate StaminaHighMovespeedModifier StaminaLowMovespeedModifier StaminaMoveSpeedCurveExponent StaminaHighMeleeSpeedModifier StaminaLowMeleeSpeedModifier StaminaMeleeSpeedCurveExponent StaminaHighStrafeSpeedModifier StaminaLowStrafeSpeedModifier StaminaStrafeSpeedCurveExponent");
        Add("StaminaEnableAffectMoveSpeed StaminaEnableAffectMeleeSpeed StaminaEnableAffectStrafeSpeed", kind: "boolean");
        foreach (string action in new[] { "Jump", "Sneak", "Walk", "Run", "CrouchEnter", "QuickStrafe" })
            foreach (string part in new[] { "baseStaminaCostInCombat", "baseStaminaCostOutOfCombat" })
                Add("Stamina" + action + "Cost." + part);
        return fields;
    }
    private static void ValidatePlayerOverrides(JsonObject values)
    {
        foreach (var row in values)
        {
            if (!PlayerFields.TryGetValue(row.Key, out var field)) throw new InvalidDataException("Unknown player field: " + row.Key);
            if (row.Value is not JsonValue value) throw new InvalidDataException("Invalid player value: " + row.Key);
            if (field.Kind == "boolean")
            {
                if (!value.TryGetValue<bool>(out _)) throw new InvalidDataException("Player field must be boolean: " + row.Key);
            }
            else if (!value.TryGetValue<double>(out double number) || !double.IsFinite(number)
                || number < field.Min || number > field.Max || field.Kind == "integer" && number != Math.Truncate(number))
                throw new InvalidDataException("Player field is not a number in range: " + row.Key);
        }
    }

    public static IReadOnlyList<RundownPack> Validate(IReadOnlyList<RundownPack> packs,
        IEnumerable<ContentVanillaId> occupied, Action<RundownDiagnostic> report)
        => Validate(packs, occupied, report, null);

    internal static IReadOnlyList<RundownPack> Validate(IReadOnlyList<RundownPack> packs,
        IEnumerable<ContentVanillaId> occupied, Action<RundownDiagnostic> report, Action<RundownPack>? validateNativeFields)
    {
        var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in packs.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            refused.Add(group.Key);
            report(new(RejectedCode, group.Key, group.Key, string.Join(",", group.Select(pack => pack.Path)), "Duplicate installed package."));
        }
        var rows = packs.SelectMany(pack => pack.Blocks).ToArray();
        foreach (var conflict in ContentDataBlockIds.Conflicts(rows.Select(block => new ContentIssuedBlock(block.Id, block.Type, block.PersistentId)), occupied))
        {
            string package = conflict.Id.Split('/')[0]; refused.Add(package);
            // A Rundown package is refused whole on any collision; a local-band number says so by its own code.
            bool local = rows.Any(block => block.Id == conflict.Id && ContentDataBlockIds.IsLocal(block.Type, conflict.PersistentId));
            report(new(RejectedCode, package, conflict.Id, conflict.Other,
                (local ? "content-id-local-collision: " : "content-id-collision: ") + conflict.Id + " and " + conflict.Other + " are both issued " + conflict.PersistentId));
        }
        foreach (var pack in packs)
        {
            try
            {
                validateNativeFields?.Invoke(pack);
                var ids = new HashSet<string>(pack.Blocks.Select(block => block.Id), StringComparer.Ordinal);
                foreach (var block in pack.Blocks)
                    foreach (string reference in References(block.Data))
                        if (!ids.Contains(reference))
                        {
                            refused.Add(pack.Package);
                            report(new(RejectedCode, pack.Package, block.Id, reference, "Unknown or unadmitted typed DataBlock reference."));
                        }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException or JsonException or KeyNotFoundException or ArgumentException)
            { refused.Add(pack.Package); report(new(RejectedCode, pack.Package, pack.Rundown, "", error.Message)); }
        }
        return packs.Where(pack => !refused.Contains(pack.Package)).OrderBy(pack => pack.Package, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$ref"))
            {
                if (obj.Count != 1) throw new InvalidDataException("A DataBlock reference must contain only $ref.");
                yield return obj["$ref"]?.GetValue<string>() ?? throw new InvalidDataException("A DataBlock reference cannot be null.");
            }
            else foreach (var property in obj) foreach (var id in References(property.Value)) yield return id;
        }
        else if (node is JsonArray array) foreach (var item in array) foreach (var id in References(item)) yield return id;
    }
    public static string Materialize(RundownBlock block, IReadOnlyList<RundownBlock> blocks)
    {
        return Materialize(block, OwnedIds(blocks));
    }
    /// <summary>The package's own stable-ID to persistentID table. A host materializing every block of one pack
    /// builds it once instead of once per block.</summary>
    internal static IReadOnlyDictionary<string, uint> OwnedIds(IReadOnlyList<RundownBlock> blocks)
        => blocks.ToDictionary(row => row.Id, row => row.PersistentId, StringComparer.Ordinal);
    internal static string Materialize(RundownBlock block, IReadOnlyDictionary<string, uint> ids)
    {
        JsonNode? Rewrite(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("$ref"))
                {
                    if (obj.Count != 1) throw new InvalidDataException("A DataBlock reference must contain only $ref.");
                    string id = obj["$ref"]!.GetValue<string>();
                    return JsonValue.Create(ids.TryGetValue(id, out var number) ? number : throw new InvalidDataException("Unknown package-owned DataBlock reference."));
                }
                var result = new JsonObject(); foreach (var property in obj) result[property.Key] = Rewrite(property.Value); return result;
            }
            if (node is JsonArray array) { var result = new JsonArray(); foreach (var value in array) result.Add(Rewrite(value)); return result; }
            return node == null ? null : JsonNode.Parse(node.ToJsonString());
        }
        var data = (JsonObject)Rewrite(block.Data)!;
        data["persistentID"] = block.PersistentId;
        // Every block keeps the readable name the website wrote; rows are found by number, never by name.
        return data.ToJsonString();
    }

    public static IReadOnlyList<RundownPack> SelectSlots(IReadOnlyList<RundownPack> packs, Action<RundownDiagnostic> report)
    {
        var slots = new Dictionary<int, RundownPack>();
        var ordered = packs.OrderBy(pack => pack.Rundown, StringComparer.Ordinal).ToArray();
        // A single-Rundown package owns the whole menu: the first one by stable ID wins and every other package,
        // single or positioned, is refused by name. Otherwise packages only collide on the same position.
        var owner = ordered.FirstOrDefault(pack => pack.SingleRundown);
        if (owner != null)
        {
            foreach (var pack in ordered.Where(pack => !ReferenceEquals(pack, owner)))
                report(new(SlotConflictCode, pack.Package, pack.Rundown, owner.Rundown,
                    "The main menu is owned by the single-Rundown package " + owner.Package + " (" + owner.Rundown
                    + "); refusing the entire " + pack.Package + " (" + pack.Rundown + ") package.", Slot: pack.MenuSlot));
            return new[] { owner };
        }
        foreach (var pack in ordered)
        {
            if (pack.MenuSlot is < 1 or > 8) throw new InvalidDataException("Rundown menuSlot must be an integer from 1 to 8.");
            if (slots.TryGetValue(pack.MenuSlot, out var first))
                report(new(SlotConflictCode, pack.Package, pack.Rundown, first.Rundown,
                    "Menu slot " + pack.MenuSlot + " is occupied by " + first.Package + " (" + first.Rundown
                    + "); refusing the entire " + pack.Package + " (" + pack.Rundown + ") package.", Slot: pack.MenuSlot));
            else slots.Add(pack.MenuSlot, pack);
        }
        return slots.Values.OrderBy(pack => pack.Rundown, StringComparer.Ordinal).ToArray();
    }
}
