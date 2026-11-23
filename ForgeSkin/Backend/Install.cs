using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// `Dropped`: other copies of this skin the listing just moved to the Recycle Bin (file names), for the editor's log. `Source`:
// "editor" (the skins folder's `<id>.forgeskin`), "hand" (another name there: put in by hand or by another tool) or "mod" (listed
// by the skin mod `Mod` under BepInEx/plugins; the editor cannot remove those). `Modified`: the pack file's last write (ISO),
// which the editor compares with its project's to tell an edit not applied yet.
// `InUse`: the skin the game shows for its target, as the plugin decides it (Skins.Resolve): a pending 应用到游戏 pick, else the
// in-game picker's choice, else active.json, else the first by path.
public sealed record InstalledSkin(string Id, string Name, string File, string? Weapon, bool Active, string? Error, string[]? Dropped = null, string Source = "editor", string? Mod = null, bool InUse = false, string? Modified = null);

/// <summary>What a plugin install did or found that the user is told (PLAN §4.5): `Code` and `Args` make the short message the editor
/// shows, `Detail` is the full account (what was checked, paths, versions, cause, fix) that goes to the logs.</summary>
public sealed record InstallNote(string Code, Dictionary<string, string> Args, string Detail);

// Skins and the plugin in a BepInEx profile (PLAN §3.2). Skin writes are atomic file replaces the plugin hot-reloads,
// so they work while the game runs; only replacing the plugin DLL needs the game closed.
public static class Install
{
    // The DLL ForgeSkin editors before the manifest installed into the editor's former folder (ProgramManifest.SkinProfile only).
    const string PluginDll = "ForgeSkin.Plugin.dll";
    static ProgramManifest Manifest => ProgramManifest.Current;
    // Left in the plugin folder by 1.0.3 and earlier: the format library is compiled into the plugin now and NVorbis ships
    // with ForgeCore, whose copy a second NVorbis.dll would clash with.
    static readonly string[] RetiredFiles = ["ForgeSkin.Format.dll", "NVorbis.dll"];

    internal static string Skins(string profile) => SkinFolders.Skins(Game.BepInEx(profile));
    static string ActiveFile(string profile) => Path.Combine(Skins(profile), "active.json");

    internal static JsonObject ReadActive(string profile)
    {
        var path = ActiveFile(profile);
        var json = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        json ??= new JsonObject();
        if (json["targets"] is not JsonObject) json["targets"] = new JsonObject();
        return json;
    }

    internal static void WriteActive(string profile, JsonObject json)
    {
        AtomicFile.WriteText(ActiveFile(profile), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // A skin for no gear (older editors made those for every gear) is refused; active.json's old `global` list of them, which
    // the plugin no longer reads, goes at the first write.
    static void SetActive(string profile, SkinDocument doc, bool active)
    {
        if (active && doc.Target.Key() == null)
            throw new BackendException("no-gear-target", $"{doc.Name}: the skin names no gear; make it a skin of the gun that carries it");
        var json = ReadActive(profile);
        var targets = json["targets"]!.AsObject();
        foreach (var key in targets.Where(kv => (string?)kv.Value == doc.Id).Select(kv => kv.Key).ToList()) targets.Remove(key);
        if (json.Remove("global", out var global)) Log.Write($"active.json: dropped its global list {global?.ToJsonString()}; skins for every gear are no longer supported");
        if (active) targets[doc.Target.Key()!] = doc.Id;
        WriteActive(profile, json);
    }

    internal static void RequireProfile(string profile)
    {
        if (!Directory.Exists(Path.Combine(profile, "BepInEx"))) throw new BackendException("not-found", $"{profile} has no BepInEx folder");
    }

    // `pick` (the editor's 应用到游戏): the skin also goes before the player's in-game picker choice for its target. Written
    // as a one-shot `<ticks>.pick` file beside active.json ({"target", "skin"}); the plugin applies it to its own choices at
    // its next reload (at once in a running game, else at the next launch) and deletes it. Only the
    // plugin writes ForgeSkin.choices.json, so a choice the player makes meanwhile is never written over.
    public static object Pack(string pack, string profile, bool activate, bool pick = false)
    {
        RequireProfile(profile);
        SkinDocument doc;
        using (var p = SkinPack.Open(pack))
        {
            PackBuilder.CheckSounds(p);
            doc = p.Manifest;
        }
        OldSkinMove.Run(profile);
        var dir = Skins(profile);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Preview.Safe(doc.Id) + SkinPack.Extension);
        using (var file = new AtomicFile(target))
        {
            File.Copy(pack, file.Temp, true);
            file.Commit();
        }
        if (activate) SetActive(profile, doc, true);
        if (pick) WritePick(dir, doc);
        var dropped = Dedupe(Read(dir).Where(p => p.Doc?.Id == doc.Id).ToList()).GetValueOrDefault(doc.Id) ?? [];
        return new { installed = target, active = activate, dropped };
    }

    // Every pack in the skins folder with its manifest, or why it cannot be read.
    static List<(string File, SkinDocument? Doc, string? Error)> Read(string dir) =>
        Directory.GetFiles(dir, "*" + SkinPack.Extension).Order().Select(file =>
        {
            try
            {
                using var p = SkinPack.Open(file);
                return (file, (SkinDocument?)p.Manifest, (string?)null);
            }
            catch (Exception e) when (e is SkinFormatException or IOException or JsonException or InvalidDataException)
            {
                return (file, null, e is SkinFormatException f ? $"{f.Code}: {f.Message}" : e.Message);
            }
        }).ToList();

    // One pack per skin id. The editor installs `<id>.forgeskin` and replaces it in place; a copy of the same skin under another
    // name (put in by hand or by another tool) makes the plugin load only one of the two. The editor's own copy stays, else the
    // newest; the others go to the Recycle Bin and leave `packs`. Returns the file names moved away per id; one that cannot be
    // moved (held open) stays and is logged, so the install itself never fails for it.
    static Dictionary<string, string[]> Dedupe(List<(string File, SkinDocument? Doc, string? Error)> packs)
    {
        var dropped = new Dictionary<string, string[]>();
        foreach (var group in packs.Where(p => p.Doc != null).GroupBy(p => p.Doc!.Id).Where(g => g.Count() > 1))
        {
            var keep = group.FirstOrDefault(p => Path.GetFileName(p.File).Equals(Preview.Safe(group.Key) + SkinPack.Extension, StringComparison.OrdinalIgnoreCase)).File
                ?? group.MaxBy(p => File.GetLastWriteTimeUtc(p.File)).File;
            var gone = new List<string>();
            foreach (var (file, _, _) in group.Where(p => p.File != keep).ToList())
                try
                {
                    RecycleBin.Send(file);
                    packs.RemoveAll(p => p.File == file);
                    gone.Add(Path.GetFileName(file));
                    Log.Write($"{file}: another copy of skin {group.Key} (kept {Path.GetFileName(keep)}), moved to the Recycle Bin");
                }
                catch (IOException e) { Log.Write($"{file}: another copy of skin {group.Key} not moved: {e.Message}"); }
            if (gone.Count > 0) dropped[group.Key] = [.. gone];
        }
        return dropped;
    }

    // Every skin the profile's game loads: the skins folder's packs, then the skin mods' (read only). `tidy` (the editor's
    // listing): copies of one skin under several names in the skins folder are cleared first (Dedupe).
    public static List<InstalledSkin> List(string profile, bool tidy = false)
    {
        OldSkinMove.Run(profile);
        var dir = Skins(profile);
        var json = Directory.Exists(dir) ? ReadActive(profile) : new JsonObject { ["targets"] = new JsonObject() };
        var targets = json["targets"]!.AsObject().Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        var active = targets.Values.ToHashSet();
        var packs = Directory.Exists(dir) ? Read(dir) : [];
        var dropped = tidy ? Dedupe(packs) : new();
        var mods = SkinFolders.ModPacks(Path.Combine(profile, "BepInEx", "plugins")).Where(m => m.Problem == null).Select(m => m.Pack).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = packs.Select(p => (p.File, p.Doc, p.Error, Mod: (string?)null))
            .Concat(mods.Select(file => { var (f, d, e) = ReadOne(file); return (File: f, Doc: d, Error: e, Mod: (string?)Path.GetFileName(Path.GetDirectoryName(file))); })).ToList();
        // What the game shows per target, in the plugin's order.
        var shown = new Dictionary<string, string>();
        foreach (var (key, id) in ReadChoices(profile)) shown[key] = id;
        foreach (var (key, id) in ReadPicks(dir)) shown[key] = id;
        foreach (var (key, id) in targets) if (id != null) shown.TryAdd(key, id);
        // A skin mod's pack is worn unless something above picks for its target; among them the first by path.
        foreach (var (_, d, _, _) in all.Where(p => p.Mod != null).OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase))
            if (d?.Target.Key() is { } key) shown.TryAdd(key, d.Id);
        return all.Select(p => p.Doc is { } d
            ? new InstalledSkin(d.Id, d.Name, p.File, d.Target.Weapon, active.Contains(d.Id), null, dropped.GetValueOrDefault(d.Id), SourceOf(p.File, d.Id, p.Mod), p.Mod,
                d.Target.Key() is { } key && shown.GetValueOrDefault(key) == d.Id, Stamp(p.File))
            : new InstalledSkin(Path.GetFileNameWithoutExtension(p.File), Path.GetFileName(p.File), p.File, null, false, p.Error, null, SourceOf(p.File, null, p.Mod), p.Mod, false, Stamp(p.File))).ToList();
    }

    static string SourceOf(string file, string? id, string? mod) =>
        mod != null ? "mod" : id != null && Path.GetFileName(file).Equals(Preview.Safe(id) + SkinPack.Extension, StringComparison.OrdinalIgnoreCase) ? "editor" : "hand";

    static string? Stamp(string file)
    {
        try { return File.GetLastWriteTimeUtc(file).ToString("O"); }
        catch (IOException) { return null; }
    }

    static (string File, SkinDocument? Doc, string? Error) ReadOne(string file)
    {
        try
        {
            using var p = SkinPack.Open(file);
            return (file, p.Manifest, null);
        }
        catch (Exception e) when (e is SkinFormatException or IOException or JsonException or InvalidDataException)
        {
            return (file, null, e is SkinFormatException f ? $"{f.Code}: {f.Message}" : e.Message);
        }
    }

    // The in-game picker's choices (target key -> skin id, "" = the original look), written by the plugin only.
    static Dictionary<string, string> ReadChoices(string profile)
    {
        var file = Path.Combine(profile, "BepInEx", "config", "ForgeSkin.choices.json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    // 应用到游戏 picks the plugin has not taken yet, oldest first (a later one wins, as in the plugin).
    static IEnumerable<(string Target, string Skin)> ReadPicks(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*" + SkinPack.PickExtension).Order())
        {
            JsonObject? pick;
            try { pick = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (Exception e) when (e is IOException or JsonException) { continue; }
            if (pick?["target"]?.GetValue<string>() is { } target && pick["skin"]?.GetValue<string>() is { } skin) yield return (target, skin);
        }
    }

    // A character pack's card picture (the one the game's skin page shows) as base64 PNG. A pack without one gives its converted
    // model instead, unpacked under `cache`, for the editor to draw the picture from (`character`: the model files and bone match).
    public static object Picture(string pack, string cache)
    {
        using var p = SkinPack.Open(pack);
        if (p.Manifest.Character?.Picture is { } picture) return new { png = Convert.ToBase64String(p.ReadBytes(picture)), character = (CharacterSkin?)null };
        var doc = SkinJson.Read(SkinJson.Write(p.Manifest));
        if (doc.Character != null) Projects.Unpack(p, doc, cache, new HashSet<string>());
        return new { png = (string?)null, character = doc.Character };
    }

    // Takes a skin out of the profile: its pack goes to the Recycle Bin (the player can put it back), and active.json forgets it.
    public static object Remove(string profile, string skinId)
    {
        // A skin mod's pack belongs to the mod manager: only the skins folder's copies are taken out.
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId && s.Source != "mod") ?? throw new BackendException("not-found", $"Skin {skinId} is not in the skins folder");
        SetActive(profile, new SkinDocument { Id = skinId }, false);
        RecycleBin.Send(skin.File);
        return new { removed = skin.File };
    }

    // Turning a skin on also goes before the player's in-game picker choice for its target (a pick, as 应用到游戏 does), so the
    // game shows what the editor says.
    public static object Activate(string profile, string skinId)
    {
        var skin = List(profile).FirstOrDefault(s => s.Id == skinId && s.Error == null) ?? throw new BackendException("not-found", $"Skin {skinId} is not installed");
        SkinDocument doc;
        using (var p = SkinPack.Open(skin.File)) doc = p.Manifest;
        SetActive(profile, doc, true);
        WritePick(Skins(profile), doc);
        return new { id = skinId };
    }

    // An older pick for the same target that the plugin has not taken (it is not running) is dropped first, as the newer one
    // would win anyway; picks for other targets stay.
    static void WritePick(string dir, SkinDocument doc)
    {
        if (doc.Target.Key() is not { } key) return;
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(dir, "*" + SkinPack.PickExtension))
        {
            try
            {
                if ((JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["target"]?.GetValue<string>() == key) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { } // the plugin took it meanwhile
        }
        AtomicFile.WriteText(Path.Combine(dir, $"{DateTime.UtcNow.Ticks}{SkinPack.PickExtension}"), JsonSerializer.Serialize(new { target = key, skin = doc.Id }));
    }

    /// <summary>The primary package's version this program ships (what a profile's plugin is held to), or null without a plugin build.</summary>
    public static string? ShippedVersion()
    {
        try { return DllVersion(Path.Combine(PluginSource(), Manifest.PrimaryPackage.Folder, Manifest.PrimaryPackage.Dll)).ToString(3); }
        catch (BackendException) { return null; }
    }

    // The plugin the app ships, next to the backend: plugin/<package folder>/<dll> for every package of the manifest (ForgeSkin's:
    // plugin/ForgeSkin/ForgeSkin.Plugin.dll and plugin/ForgeCore/, the plugins/ForgeCore/ folder of the NAinfini-ForgeCore package it
    // depends on). In a checkout the same layout that Mods/ForgeSkin/tools/build-editor-release.ps1 stages under the manifest's PluginRoot.
    internal static string PluginSource()
    {
        var needed = Manifest.Packages;
        // The folder to name when none is complete: the first that exists (a half-staged one), else the checkout's (the
        // folder holding .git), else plugin/ next to the backend.
        string? named = null, checkout = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var plugin in new[] { Path.Combine(dir.FullName, "plugin"), Path.Combine(dir.FullName, Manifest.PluginRoot) })
            {
                if (needed.All(p => File.Exists(Path.Combine(plugin, p.Folder, p.Dll)))) return plugin;
                if (Directory.Exists(plugin)) named ??= plugin;
            }
            if (checkout == null && Path.Exists(Path.Combine(dir.FullName, ".git"))) checkout = Path.Combine(dir.FullName, Manifest.PluginRoot);
        }
        var where = named ?? checkout ?? Path.Combine(AppContext.BaseDirectory, "plugin");
        var missing = needed.Select(p => Path.Combine(where, p.Folder, p.Dll)).Where(f => !File.Exists(f));
        throw new BackendException("no-plugin", "The plugin files this program installs are missing: " + string.Join("; ", missing)
            + ". A packaged program carries them in resources/publish/plugin (reinstall it); a checkout stages them (ForgeSkin: Mods/ForgeSkin/tools/build-editor-release.ps1 -PluginOnly).");
    }

    static Version DllVersion(string dll)
    {
        var v = FileVersionInfo.GetVersionInfo(dll);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    // The packages the program ships and keeps in step (PLAN §4.5) are ProgramManifest.Packages, in install order (ForgeSkin's: ForgeCore
    // first, since whether a newer ForgeSkin may stay depends on the ForgeCore the profile ends with).

    /// <summary>Where a mod manager installs a package, BepInEx/plugins/&lt;team&gt;-&lt;name&gt;/&lt;name&gt;/: the one copy of it a profile keeps.</summary>
    public static string Home(string profile, string name) =>
        Path.Combine(Plugins(profile), (Manifest.Packages.FirstOrDefault(p => p.Name == name)?.Team ?? "NAinfini") + "-" + name, name);

    internal static string Plugins(string profile) => Path.Combine(profile, "BepInEx", "plugins");

    // Every folder under BepInEx/plugins holding the DLL: BepInEx loads from each (a copy the mod manager disabled has another file name).
    static List<string> Folders(string profile, string dll) => Directory.Exists(Plugins(profile))
        ? [.. Directory.EnumerateFiles(Plugins(profile), dll, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Select(f => Path.GetDirectoryName(Path.GetFullPath(f))!)]
        : [];

    static bool SameFolder(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Whether every file of the shipped folder `source` is in `target` with the same content (`cached`: for a status report).
    static bool Same(string source, string target, bool cached = false) =>
        Directory.GetFiles(source, "*", SearchOption.AllDirectories).All(file =>
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            return File.Exists(to) && FileHash.Sha256(to, cached) == FileHash.Sha256(file, cached);
        });

    // Copies the shipped folder `source` over `target`; refuses while GTFO holds one of the files.
    static void CopyOver(string source, string target)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        foreach (var file in files) RequireUnlocked(Path.Combine(target, Path.GetRelativePath(source, file)));
        foreach (var file in files)
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
    }

    /// <summary>The DLL of `package` (default the primary one) the game loads from this profile (the one at its home, else the newest elsewhere), or null.</summary>
    public static string? InstalledPlugin(string profile, PackageSpec? package = null)
    {
        package ??= Manifest.PrimaryPackage;
        var home = Path.Combine(Home(profile, package.Name), package.Dll);
        return File.Exists(home) ? home : Folders(profile, package.Dll).Select(f => Path.Combine(f, package.Dll)).MaxBy(DllVersion);
    }

    // A copy the editor leaves alone: one a mod manager installed from the store, newer than the shipped one. From the store means
    // its package folder (the first folder under BepInEx/plugins) holds the Thunderstore manifest.json whose version_number is the
    // DLL's own version, so nothing wrote over it since. Store versions only go up, so a higher number there is a newer build. Any
    // other copy (one unpacked by hand, one an editor already wrote over, the pre-store 1.1.x ForgeSkin and 1.0.2 ForgeCore of the
    // GitHub editors) follows the shipped one by content, whatever its number.
    static bool NewerFromStore(string profile, string dll, string shippedDll)
    {
        if (!File.Exists(dll)) return false;
        var version = DllVersion(dll);
        if (version <= DllVersion(shippedDll)) return false;
        var plugins = Path.GetFullPath(Plugins(profile));
        var top = Path.GetRelativePath(plugins, Path.GetFullPath(dll)).Split(Path.DirectorySeparatorChar);
        if (top.Length < 2 || top[0] == "..") return false;
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(plugins, top[0], "manifest.json")))?["version_number"]?.GetValue<string>() is { } number
                && Version.TryParse(number, out var store) && store == version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    // What one package of the profile ends as. `Keep`: the newer store copy at its home stays, else the shipped files go there.
    // `Others`: every other folder BepInEx would load it from, all of which go. `Why`: what a keep or a refused keep rests on.
    sealed record Step(string Name, string Source, string Home, string Dll, bool Keep, List<string> Others, string Why);

    // The rule (PLAN §4.5): each package ends as one loadable copy, at its home. A newer copy straight from the store at its home
    // stays, but only while every package of the manifest decided before it ends at least at the minimum its [BepInDependency]
    // declares (read from its DLL), else the shipped copy replaces it. Any other home copy takes the shipped files.
    static List<Step> Plan(string profile, string source)
    {
        var steps = new List<Step>();
        var ends = new Dictionary<string, (string Name, Version Version)>(); // plugin GUID -> the package the profile ends with
        foreach (var spec in Manifest.Packages)
        {
            var (name, dll) = (spec.Name, spec.Dll);
            var from = Path.Combine(source, spec.Folder);
            var home = Home(profile, name);
            var at = Path.Combine(home, dll);
            var shipped = DllVersion(Path.Combine(from, dll));
            var keep = NewerFromStore(profile, at, Path.Combine(from, dll));
            var why = keep ? $"{at} {DllVersion(at)} came from the store and is newer than the shipped {shipped}, so it stays" : "";
            if (keep && PluginMeta.Read(at) is { } meta)
                foreach (var (guid, (need, have)) in ends)
                    if (meta.Minimum(guid) is { } min && min > have)
                    {
                        keep = false;
                        why = $"{at} {DllVersion(at)} came from the store and is newer than the shipped {shipped}, but it needs {need} >={min} and the profile ends with {need} {have}, so the shipped {name} replaces it";
                        break;
                    }
            if (PluginMeta.Read(Path.Combine(from, dll))?.Guid is { } own) ends[own] = (name, keep ? DllVersion(at) : shipped);
            steps.Add(new Step(name, from, home, dll, keep, [.. Folders(profile, dll).Where(f => !SameFolder(f, home))], why));
        }
        return steps;
    }

    // One package of a plan is current: a single copy at its home, the shipped files or a store copy that stays.
    static bool StepCurrent(Step s, bool cached = true) => s.Others.Count == 0 && (s.Keep || Same(s.Source, s.Home, cached));

    /// <summary>What a profile holds of each package of the manifest and of each plugin it only checks. Package: `Version` installed (null: none),
    /// `Shipped` this program's, `Current` (one copy at its home, same content as the shipped files by hash or a newer store copy that stays),
    /// `BelowMinimum` (installed, under the manifest's MinVersion). Dependency: `Ok` = found at least at its MinVersion.</summary>
    public static (List<PackageStatus> Packages, List<DependencyStatus> Dependencies) Status(string profile)
    {
        string? source = null;
        try { source = PluginSource(); } catch (BackendException) { }
        var plan = source == null ? null : Plan(profile, source);
        var packages = Manifest.Packages.Select(spec =>
        {
            var dll = InstalledPlugin(profile, spec);
            var version = dll == null ? null : DllVersion(dll);
            var step = plan?.First(s => s.Name == spec.Name);
            return new PackageStatus(spec.Name, version?.ToString(3), step == null ? null : DllVersion(Path.Combine(step.Source, step.Dll)).ToString(3),
                dll != null && step != null && StepCurrent(step), version != null && Version.TryParse(spec.MinVersion, out var min) && version < min);
        }).ToList();
        Dictionary<string, List<(string Path, string Version)>>? found = null;
        var dependencies = Manifest.Dependencies.Select(d =>
        {
            found ??= PluginsByGuid(profile);
            var versions = found.TryGetValue(d.Guid, out var copies) ? copies.Select(c => c.Version).ToList() : [];
            // A plugin's version string may carry a SemVer suffix (MTFO's "4.6.3+git169eb91-main"): the number before it decides.
            var best = versions.Select(v => Version.TryParse(v.Split('+', '-')[0], out var p) ? p : null).Where(p => p != null).Max();
            return new DependencyStatus(d.Name, d.Package, best?.ToString(3), best != null && (!Version.TryParse(d.MinVersion, out var min) || best >= min));
        }).ToList();
        return (packages, dependencies);
    }

    // Every plugin under BepInEx/plugins by GUID, one (path, version) per copy (the account of a missing dependency, LoadLog).
    internal static Dictionary<string, List<(string Path, string Version)>> PluginsByGuid(string profile)
    {
        var found = new Dictionary<string, List<(string, string)>>();
        if (!Directory.Exists(Plugins(profile))) return found;
        foreach (var dll in Directory.EnumerateFiles(Plugins(profile), "*.dll", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            if (PluginMeta.Read(dll) is { } meta)
                (found.TryGetValue(meta.Guid, out var list) ? list : found[meta.Guid] = []).Add((dll, meta.Version));
        return found;
    }

    /// <summary>The profile's store copies newer than the shipped ones, which the editor leaves alone ("ForgeCore 1.0.3"), for the editor to say so.</summary>
    public static string[] Newer(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return []; }
        return [.. Plan(profile, source).Where(s => s.Keep).Select(s => $"{s.Name} {DllVersion(Path.Combine(s.Home, s.Dll)).ToString(3)}")];
    }

    // Current: each package is one copy at its home (Plan), the shipped files or a newer store copy that stays, and nothing an
    // older version left in the editor's former folder.
    public static bool PluginCurrent(string profile)
    {
        string source;
        try { source = PluginSource(); } catch (BackendException) { return false; }
        var former = Game.PluginDir(profile);
        return !(Manifest.SkinProfile && RetiredFiles.Any(f => File.Exists(Path.Combine(former, f)))) && Plan(profile, source).All(s => StepCurrent(s));
    }

    // The plugin's in-game texts (lang/*.forge, beside its DLL) under `folder`, as paths relative to it.
    static IEnumerable<string> Texts(string folder) =>
        Directory.Exists(Path.Combine(folder, "lang")) ? Directory.GetFiles(Path.Combine(folder, "lang"), "*.forge").Select(f => Path.Combine("lang", Path.GetFileName(f))) : [];

    // Refuses while GTFO runs and holds the DLL. Only DLLs: .forge content is read by the running game and swapped under it (Content).
    internal static void RequireUnlocked(string dll)
    {
        if (!File.Exists(dll) || !Game.Running) return;
        try { using var _ = File.Open(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BackendException("game-running", $"Close GTFO first: it has {dll} loaded, and the plugin files cannot be replaced while it runs"); }
    }

    // Every package of the manifest ends as one loadable copy, at its home where a mod manager puts it (Plan). Every other copy
    // goes: ForgeSkin's own former folder plugins/ForgeSkin/ is cleared, and a copy the program did not put there goes to the
    // Recycle Bin (whole when its folder holds nothing but the package's files, else its DLL alone) and is reported in `notes`
    // with its path. Nothing is touched while the game holds one of the files.
    public static object Plugin(string profile)
    {
        RequireProfile(profile);
        var source = PluginSource();
        if (Manifest.SkinProfile)
        {
            MovePreRenameInstall(profile);
            Directory.CreateDirectory(Skins(profile));
        }
        var former = Game.PluginDir(profile);
        var plan = Plan(profile, source);
        if (Manifest.SkinProfile) foreach (var f in RetiredFiles.Append(PluginDll)) RequireUnlocked(Path.Combine(former, f));
        foreach (var s in plan)
        {
            foreach (var other in s.Others) RequireUnlocked(Path.Combine(other, s.Dll));
            if (!s.Keep) RequireUnlocked(Path.Combine(s.Home, s.Dll));
        }
        var notes = new List<InstallNote>();
        var replaced = new List<string>();
        var newer = new List<string>();
        foreach (var s in plan)
        {
            var kept = s.Keep ? DllVersion(Path.Combine(s.Home, s.Dll)) : DllVersion(Path.Combine(s.Source, s.Dll));
            var copies = string.Join(", ", s.Others.Prepend(s.Home).Where(f => File.Exists(Path.Combine(f, s.Dll))).Select(f => $"{f} {DllVersion(Path.Combine(f, s.Dll))}"));
            foreach (var other in s.Others)
                if (SameFolder(other, former))
                {
                    foreach (var f in Texts(other).Append(s.Dll).Concat(RetiredFiles).Select(f => Path.Combine(other, f)).Where(File.Exists)) File.Delete(f);
                    Log.Write($"plugin.install {profile}: removed {s.Name} from {other}, where editors before this one installed it; it is at {s.Home} now");
                }
                else notes.Add(MoveAway(profile, s, other, copies, kept));
            if (s.Why.Length > 0) Log.Write($"plugin.install {profile}: {s.Name}: {s.Why}");
            if (s.Keep) newer.Add($"{s.Name} {kept.ToString(3)}");
            else if (!Same(s.Source, s.Home))
            {
                CopyOver(s.Source, s.Home);
                // Copies keep the shipped files' times: the folder's time says when the profile changed (LoadLog.Check).
                Directory.SetLastWriteTimeUtc(s.Home, DateTime.UtcNow);
                replaced.Add(s.Home);
            }
        }
        if (Manifest.SkinProfile) foreach (var f in RetiredFiles.Select(f => Path.Combine(former, f)).Where(File.Exists)) File.Delete(f);
        return new { installed = Home(profile, Manifest.PrimaryPackage.Name), replaced, newer, notes, version = Game.ProfileAt(profile).PluginVersion };
    }

    // A loadable copy the editor did not install: to the Recycle Bin (never a plain delete), its folder whole when it holds only
    // what the shipped package holds (never BepInEx/plugins itself), else its DLL, which is what makes BepInEx load it.
    static InstallNote MoveAway(string profile, Step s, string other, string copies, Version kept)
    {
        var dll = Path.Combine(other, s.Dll);
        var version = DllVersion(dll);
        var shipped = Directory.GetFiles(s.Source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(s.Source, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var whole = !SameFolder(other, Plugins(profile)) && Directory.GetFiles(other, "*", SearchOption.AllDirectories)
            .All(f => Path.GetRelativePath(other, f) is var rel && (shipped.Contains(rel) || rel.StartsWith("lang" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        var moved = whole ? other : dll;
        RecycleBin.Send(moved);
        var detail = $"plugin.install {profile}: BepInEx would load {s.Name} from more than one folder ({copies}); it loads one and skips the others "
            + $"(\"Skipping [...] because a newer version exists\" in BepInEx/LogOutput.log), so which one runs is not ours to choose. The profile keeps one copy, "
            + $"{s.Name} {kept} at {s.Home}, where a mod manager installs it. {moved} ({s.Name} {version}) was not installed there by the editor, so it was moved "
            + $"to the Recycle Bin, not deleted{(whole ? "" : $"; the other files in {other} stay")}. Fix if it was wanted: restore it from the Recycle Bin and remove "
            + $"the copy at {s.Home} instead; if a mod manager lists the moved copy, remove it there too.";
        Log.Write(detail);
        return new InstallNote("plugin-copy-removed", new() { ["package"] = s.Name, ["path"] = moved }, detail);
    }

    // Moves a pre-rename install (GTFO Skin Forge -> ForgeSkin) to the new names: skins/ of plugins/SkinForge (packs get the
    // new extension, active.json kept) into the new plugin folder, config/SkinForge.cfg and the BepInEx/SkinForge readback
    // folder to theirs; then the old DLLs and log are deleted and the old folder too once nothing else is left in it.
    static void MovePreRenameInstall(string profile)
    {
        var bepInEx = Path.Combine(profile, "BepInEx");
        var old = Path.Combine(bepInEx, "plugins", "SkinForge");
        if (Directory.Exists(old))
        {
            string[] oldFiles = ["SkinForge.Plugin.dll", "SkinForge.Format.dll", "NVorbis.dll", "skinforge.log"];
            foreach (var f in oldFiles) RequireUnlocked(Path.Combine(old, f));
            var oldSkins = Path.Combine(old, "skins");
            if (Directory.Exists(oldSkins))
            {
                Directory.CreateDirectory(Skins(profile));
                foreach (var file in Directory.GetFiles(oldSkins))
                {
                    var name = Path.GetExtension(file).Equals(".gtfskin", StringComparison.OrdinalIgnoreCase)
                        ? Path.ChangeExtension(Path.GetFileName(file), SkinPack.Extension) : Path.GetFileName(file);
                    var to = Path.Combine(Skins(profile), name);
                    if (!File.Exists(to)) File.Move(file, to);
                }
                if (Directory.GetFileSystemEntries(oldSkins).Length == 0) Directory.Delete(oldSkins);
            }
            foreach (var f in oldFiles.Select(f => Path.Combine(old, f)).Where(File.Exists)) File.Delete(f);
            if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
            else Log.Write($"pre-rename {old} kept: it still holds files that were not moved");
        }
        var oldConfig = Path.Combine(bepInEx, "config", "SkinForge.cfg");
        if (File.Exists(oldConfig) && !File.Exists(ConfigFile(profile))) File.Move(oldConfig, ConfigFile(profile));
        var oldReadback = Path.Combine(bepInEx, "SkinForge");
        // Readback files are debug output the plugin writes again; a folder held open elsewhere stays where it is.
        try { if (Directory.Exists(oldReadback) && !Directory.Exists(SkinFolders.Root(bepInEx))) Directory.Move(oldReadback, SkinFolders.Root(bepInEx)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"pre-rename {oldReadback} kept: {e.Message}"); }
    }

    static string ConfigFile(string profile) => Path.Combine(profile, "BepInEx", "config", "ForgeSkin.cfg");

    // Removes the primary plugin (ForgeSkin's: ForgeSkin) the program installed: its home copy unless a mod manager owns it (a Thunderstore
    // manifest.json in its package folder), and what is left in ForgeSkin's former folder. Installed skins and the other packages stay.
    public static object PluginUninstall(string profile)
    {
        var primary = Manifest.PrimaryPackage;
        var home = Home(profile, primary.Name);
        var dirs = new List<string>();
        if (Manifest.SkinProfile) dirs.Add(Game.PluginDir(profile));
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(home)!, "manifest.json"))) dirs.Add(home);
        else Log.Write($"plugin.uninstall {profile}: {home} kept, a mod manager installed it (remove it there)");
        var files = dirs.SelectMany(dir => new[] { primary.Dll }.Concat(Manifest.SkinProfile ? RetiredFiles : []).Concat(Texts(dir)).Select(f => Path.Combine(dir, f))).ToList();
        foreach (var f in files) RequireUnlocked(f);
        foreach (var f in files.Where(File.Exists)) File.Delete(f);
        return new { removed = dirs };
    }
}
