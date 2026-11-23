using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ForgeSkin.Backend;

// Content packs in a BepInEx profile: an exported Rundown or content zip is one package folder BepInEx/plugins/<Team-Name>/ (and its
// MTFO tables in BepInEx/GameData/<Team-Name>/), laid out as r2modman lays out a package zip (Tools/Game/game_profile.py
// manager_layout_files, export_pack_files, install_export_pack: the same routes, the same refusals, the same record). Each package
// folder carries `.forge-installed.json` ({path: sha256}, the files we wrote, GameData ones as `GameData/<file>`), which is also what
// marks a folder as ours for content.list and content.remove.
public static partial class Content
{
    const string Record = ".forge-installed.json", DataPrefix = "GameData/";

    [GeneratedRegex("^[A-Za-z0-9_]+-[A-Za-z0-9_]+$")]
    private static partial Regex PackageName();

    static string Plugins(string profile, string package) => Path.Combine(Install.Plugins(profile), Name(package));
    static string Data(string profile, string package) => Path.Combine(Game.BepInEx(profile), "GameData", Name(package));
    static string Staging(string profile) => Path.Combine(Game.BepInEx(profile), ".forge-staging");

    static string Name(string package) =>
        PackageName().IsMatch(package) ? package : throw new BackendException("bad-request", $"Package must be one Author-Name (letters, digits, _): {package}");

    // The zip's files as (route, path below the route folder), by r2modman's rules: an entry inside a folder named plugins or GameData
    // (any depth, any case) keeps its path below it; any other file goes by its name alone to plugins. Other routes, traversal and two
    // entries landing on one path are refused before anything is written.
    static Dictionary<string, ZipArchiveEntry> Layout(ZipArchive zip)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var parts = entry.FullName.Split('/');
            if (entry.FullName.Contains('\\') || parts.Any(p => p is "" or "." or ".." || p.Contains(':')))
                throw new BackendException("unsafe-entry", $"The zip has an unsafe entry, nothing installed: {entry.FullName}");
            var route = Array.FindIndex(parts, 0, parts.Length - 1, p => p.Equals("plugins", StringComparison.OrdinalIgnoreCase) || p.Equals("GameData", StringComparison.OrdinalIgnoreCase));
            string key;
            if (route >= 0) key = (parts[route].Equals("GameData", StringComparison.OrdinalIgnoreCase) ? DataPrefix : "") + string.Join('/', parts[(route + 1)..]);
            else if (Regex.IsMatch(entry.FullName, @"(^|/)(core|patchers|monomod|config|Assets)/", RegexOptions.IgnoreCase))
                throw new BackendException("unsafe-entry", $"The zip puts a file outside the plugin and GameData folders, nothing installed: {entry.FullName}");
            else key = parts[^1];
            if (!files.TryAdd(key, entry)) throw new BackendException("unsafe-entry", $"Two zip entries land on {key}, nothing installed");
        }
        return files;
    }

    // The package named by the zip's identity document, the one the Rundown or content pack declares.
    static string Package(Dictionary<string, ZipArchiveEntry> files)
    {
        var identity = files.GetValueOrDefault("forge/rundown.forge") ?? files.GetValueOrDefault("forge/content/pack.forge")
            ?? throw new BackendException("no-identity", "The zip has no forge/rundown.forge or forge/content/pack.forge, nothing installed");
        try
        {
            using var r = new StreamReader(identity.Open());
            return (string?)JsonNode.Parse(r.ReadToEnd().TrimStart('﻿'))?["package"] ?? throw new JsonException("no package field");
        }
        catch (JsonException e) { throw new BackendException("no-identity", $"{identity.FullName}: {e.Message}"); }
    }

    static bool IsDll(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    // The game may run: .forge tables and media are hot-loaded, so only DLLs (loaded for the whole run) need it closed.
    static void RequireDllsUnlocked(IEnumerable<string> files)
    {
        foreach (var f in files.Where(IsDll)) Install.RequireUnlocked(f);
    }

    static IEnumerable<string> FilesUnder(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories) : [];

    /// <summary>Installs the content zip as its package: staged beside the profile's plugins, then each folder renamed whole into place; a
    /// package of the same name goes to the Recycle Bin first. Refused while GTFO runs only when a DLL in the old or new package
    /// would be replaced.</summary>
    public static object Add(string zip, string profile)
    {
        Install.RequireProfile(profile);
        using var archive = ZipFile.OpenRead(zip);
        var files = Layout(archive);
        var package = Package(files);
        var plugins = Plugins(profile, package);
        var data = Data(profile, package);
        RequireDllsUnlocked(files.Keys.Where(k => !k.StartsWith(DataPrefix)).Select(k => Path.Combine(plugins, k)).Concat(FilesUnder(plugins)));

        var stage = Path.Combine(Staging(profile), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(stage, "plugins"));
            var record = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, entry) in files)
            {
                var to = Path.GetFullPath(Path.Combine(stage, key.StartsWith(DataPrefix) ? "GameData" : "plugins", key.StartsWith(DataPrefix) ? key[DataPrefix.Length..] : key));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                entry.ExtractToFile(to);
                record[key] = FileHash.Sha256(to).ToLowerInvariant();
            }
            AtomicFile.WriteText(Path.Combine(stage, "plugins", Record), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true, Encoder = Program.Json.Encoder }));
            var replaced = Directory.Exists(plugins) || Directory.Exists(data);
            Place(Path.Combine(stage, "plugins"), plugins);
            if (Directory.Exists(Path.Combine(stage, "GameData"))) Place(Path.Combine(stage, "GameData"), data);
            else if (Directory.Exists(data)) Place(null, data);
            Directory.SetLastWriteTimeUtc(plugins, DateTime.UtcNow); // the folder's time says when the profile changed (LoadLog.Check)
            Log.Write($"content.install {profile}: {package}, {record.Count} files{(replaced ? ", the earlier copy is in the Recycle Bin" : "")}");
            return new { package, installed = plugins, data = Directory.Exists(data) ? data : null, files = record, replaced };
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            var root = Staging(profile);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }

    // `from` becomes `target` as a whole folder; what was there goes to the Recycle Bin. A null `from` only clears `target`.
    static void Place(string? from, string target)
    {
        try
        {
            if (Directory.Exists(target)) RecycleBin.Send(target);
            if (from != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(from, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackendException("file-in-use", $"{target} could not be replaced, something holds a file in it open: {e.Message}");
        }
    }

    /// <summary>The packages content.install put in the profile (a folder under BepInEx/plugins with its record).</summary>
    public static object List(string profile)
    {
        Install.RequireProfile(profile);
        var root = Install.Plugins(profile);
        var packages = Directory.Exists(root) ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, Record))).Order().Select(dir =>
        {
            var package = Path.GetFileName(dir);
            var count = (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Record))) as JsonObject)?.Count ?? 0;
            return new { package, folder = dir, files = count, modified = File.GetLastWriteTimeUtc(Path.Combine(dir, Record)).ToString("O") };
        }).ToList() : [];
        return new { packages };
    }

    /// <summary>Takes a package content.install put in out of the profile: its folders go to the Recycle Bin. A folder without our record
    /// (a mod manager's, a hand-made one) is never touched.</summary>
    public static object Remove(string profile, string package)
    {
        Install.RequireProfile(profile);
        var plugins = Plugins(profile, package);
        if (!File.Exists(Path.Combine(plugins, Record))) throw new BackendException("not-found", $"{package} was not installed by content.install in {profile}");
        var data = Data(profile, package);
        RequireDllsUnlocked(FilesUnder(plugins));
        var removed = new[] { plugins, data }.Where(Directory.Exists).ToArray();
        foreach (var folder in removed) Place(null, folder);
        return new { removed };
    }
}
