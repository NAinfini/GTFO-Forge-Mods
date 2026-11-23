using System.Text.RegularExpressions;

namespace ForgeSkin.Backend;

// plugin.loadCheck (PLAN §4.5): what BepInEx itself said about the manifest's packages (ForgeSkin's: ForgeCore and ForgeSkin) the last time the game started from the
// profile. Only BepInEx/LogOutput.log is read, never written. A plugin BepInEx refused ("Could not load [...] because ...") or
// skipped ("Skipping [...] because ...") becomes a note: a short message for the player and the full account for the logs.
public static partial class LoadLog
{
    [GeneratedRegex(@"(?<verdict>Could not load|Skipping) \[(?<plugin>[^\]]+)\] because (?<why>.+)$")]
    private static partial Regex Verdict();
    [GeneratedRegex(@"has missing dependencies: (?<deps>.+)$")]
    private static partial Regex MissingDeps();
    [GeneratedRegex(@"(?<guid>[^\s,()]+)(?:\s*\((?<range>[^)]*)\))?")]
    private static partial Regex Dependency();
    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionIn();
    [GeneratedRegex(@"is incompatible with: (?<with>.+)$")]
    private static partial Regex Incompatible();

    // Dependencies of ours that are not ours, by GUID: the name a player knows and the mod manager package to install.
    // The manifest's own dependencies come first.
    static readonly Dictionary<string, (string Name, string Package)> Known = new() { ["dev.gtfomodding.gtfo-api"] = ("GTFO-API", "GTFOModding-GTFO_API") };

    static bool KnownDependency(string guid, out (string Name, string Package) known)
    {
        if (ProgramManifest.Current.Dependencies.FirstOrDefault(d => d.Guid == guid) is { } d) { known = (d.Name, d.Package); return true; }
        return Known.TryGetValue(guid, out known);
    }

    // What was already written to the backend log, so a check repeated every few seconds logs a skip or a finding once.
    static readonly HashSet<string> Said = [];
    static void Once(string line)
    {
        lock (Said) if (!Said.Add(line)) return;
        Log.Write(line);
    }

    public static object Check(string profile)
    {
        var log = Path.Combine(profile, "BepInEx", "LogOutput.log");
        var notes = new List<InstallNote>();
        if (!File.Exists(log))
        {
            Once($"plugin.loadCheck {profile}: {log} does not exist (the game has not started from this profile), nothing to check");
            return new { log, notes };
        }
        var written = File.GetLastWriteTimeUtc(log);
        var changed = LastPluginChange(profile);
        if (written < changed)
        {
            Once($"plugin.loadCheck {profile}: {log} (written {written:O}) is older than the last change under BepInEx/plugins ({changed:O}), so it says nothing about the plugins there now; checked again after the next game start");
            return new { log, notes };
        }
        // The BepInPlugin names BepInEx prints ("Forge Core 1.0.3"), from the copies at home, else the shipped ones.
        var ours = new List<(string Package, string Name, string Guid)>();
        string? source = null;
        try { source = Install.PluginSource(); } catch (BackendException e) { Once($"plugin.loadCheck {profile}: no shipped plugin to fall back on ({e.Message}); only the copies at home are read"); }
        foreach (var spec in ProgramManifest.Current.Packages)
        {
            var (name, dll) = (spec.Name, spec.Dll);
            var home = Path.Combine(Install.Home(profile, name), dll);
            var meta = File.Exists(home) ? PluginMeta.Read(home) : source != null ? PluginMeta.Read(Path.Combine(source, spec.Folder, dll)) : null;
            if (meta != null) ours.Add((name, meta.Name, meta.Guid));
            else Once($"plugin.loadCheck {profile}: {name} has no readable DLL at its home nor in the shipped plugin, so its lines are not looked for");
        }
        string[] lines;
        using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(s)) lines = r.ReadToEnd().Split('\n');
        Dictionary<string, List<(string Path, string Version)>>? installed = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (Verdict().Match(line) is not { Success: true } v) continue;
            var plugin = v.Groups["plugin"].Value;
            var (package, _, _) = ours.FirstOrDefault(o => plugin.StartsWith(o.Name + " ", StringComparison.Ordinal));
            if (package == null) continue;
            var why = v.Groups["why"].Value;
            var head = $"BepInEx did not load {package} ({plugin}) from profile {profile}. Checked {log} (last written {written.ToLocalTime():yyyy-MM-dd HH:mm:ss}), line: \"{line.Trim()}\".";
            if (MissingDeps().Match(why) is { Success: true } missing)
                foreach (Match d in Dependency().Matches(missing.Groups["deps"].Value))
                {
                    var guid = d.Groups["guid"].Value;
                    var range = d.Groups["range"].Success ? d.Groups["range"].Value.Trim() : "";
                    var min = VersionIn().Match(range).Value;
                    installed ??= Install.PluginsByGuid(profile);
                    var (shown, store) = KnownDependency(guid, out var k) ? k
                        : ours.FirstOrDefault(o => o.Guid == guid) is { Package: { } p } ? (p, ProgramManifest.Current.Packages.First(x => x.Name == p).Team + "-" + p) : (guid, guid);
                    var found = installed.TryGetValue(guid, out var copies) ? string.Join("; ", copies.Select(c => $"{c.Path} {c.Version}")) : "none";
                    notes.Add(new InstallNote(min.Length > 0 ? "load-missing-dependency" : "load-missing-dependency-any", new() { ["plugin"] = package, ["dependency"] = shown, ["version"] = min },
                        $"{head} Cause: {package} requires {guid}{(range.Length > 0 ? " " + range : "")} ({shown}); found under BepInEx/plugins: {found}. "
                        + $"Fix: install or update {shown} (mod manager package {store}){(min.Length > 0 ? $" to {min} or newer" : "")} in the mod manager, then start the game again."));
                }
            else if (Incompatible().Match(why) is { Success: true } clash)
                notes.Add(new InstallNote("load-incompatible", new() { ["plugin"] = package, ["other"] = clash.Groups["with"].Value },
                    $"{head} Cause: {package} declares it cannot run beside {clash.Groups["with"].Value}. Fix: disable one of the two in the mod manager."));
            else if (why.StartsWith("a newer version exists", StringComparison.Ordinal))
                notes.Add(new InstallNote("load-duplicate", new() { ["plugin"] = package },
                    $"{head} Cause: more than one copy of {package} is under BepInEx/plugins, so BepInEx loaded one and skipped the others. Fix: apply once from the editor (it keeps the copy at {Install.Home(profile, package)} and moves the others to the Recycle Bin), or remove the extra copies in the mod manager."));
            else if (why.StartsWith("it has a dependency that was not loaded", StringComparison.Ordinal))
                notes.Add(new InstallNote("load-dependency-failed", new() { ["plugin"] = package },
                    $"{head} Cause: a plugin {package} needs was not loaded itself; the lines before this one in the log say why. Fix: that plugin's problem first (the editor reports a ForgeCore problem on its own)."));
            else
                notes.Add(new InstallNote("load-refused", new() { ["plugin"] = package },
                    $"{head} Cause: BepInEx's own words above. Fix: see the line; the editor has no plain-language reading of it."));
        }
        notes = [.. notes.DistinctBy(n => n.Detail)];
        foreach (var n in notes) Once(n.Detail);
        return new { log, notes };
    }

    [GeneratedRegex(@"^\[(?<level>Fatal|Error|Warning|Message|Info|Debug)\s*:\s*(?<source>[^\]]*)\]\s?(?<text>.*)$")]
    private static partial Regex Header();

    // plugin.gameLog: the lines of BepInEx/LogOutput.log a problem report and the program's game-log reading need, read only. Every
    // Fatal, Error and Warning line, and every line of a source whose name holds "Forge" (whatever its level; the caller keeps the ones
    // it wants, such as those carrying a Forge diagnostic code). A line without a header continues the one before it (a stack trace).
    // `stale`: the log was last written before the last change under BepInEx/plugins, so it says nothing about the plugins there now.
    public static object Lines(string profile)
    {
        var log = Path.Combine(profile, "BepInEx", "LogOutput.log");
        if (!File.Exists(log)) return new { log, exists = false, written = (DateTime?)null, changed = (DateTime?)null, stale = false, lines = Array.Empty<GameLine>() };
        var written = File.GetLastWriteTimeUtc(log);
        var changed = LastPluginChange(profile);
        string[] raw;
        using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var r = new StreamReader(s)) raw = r.ReadToEnd().Split('\n');
        var lines = new List<GameLine>();
        GameLine? last = null;
        foreach (var part in raw)
        {
            var line = part.TrimEnd('\r');
            if (Header().Match(line) is { Success: true } m)
            {
                var (level, source) = (m.Groups["level"].Value, m.Groups["source"].Value.Trim());
                last = level is "Fatal" or "Error" or "Warning" || source.Contains("Forge", StringComparison.OrdinalIgnoreCase) ? new GameLine(level, source, m.Groups["text"].Value) : null;
                if (last != null) lines.Add(last);
            }
            else if (last != null && line.Length > 0) last.Text += "\n" + line;
        }
        return new { log, exists = true, written = (DateTime?)written, changed = (DateTime?)changed, stale = written < changed, lines };
    }

    public sealed class GameLine(string level, string source, string text)
    {
        public string Level { get; } = level;
        public string Source { get; } = source;
        public string Text { get; set; } = text;
    }

    // plugin.mods: every plugin under BepInEx/plugins whose GUID or name holds "Forge", with its version, the DLL's path and SHA-256. Read only.
    public static object Mods(string profile)
    {
        var plugins = Install.Plugins(profile);
        var mods = new List<object>();
        if (Directory.Exists(plugins))
            foreach (var dll in Directory.EnumerateFiles(plugins, "*.dll", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                if (PluginMeta.Read(dll) is { } meta && (meta.Guid.Contains("Forge", StringComparison.OrdinalIgnoreCase) || meta.Name.Contains("Forge", StringComparison.OrdinalIgnoreCase)))
                    mods.Add(new { guid = meta.Guid, name = meta.Name, version = meta.Version, path = dll, sha256 = FileHash.Sha256(dll, cached: true).ToLowerInvariant() });
        return new { plugins, mods };
    }

    // When the profile's plugins last changed: BepInEx/plugins and each folder in it (a mod manager adds, removes or replaces
    // package folders; the editor stamps the folders it installs into). A log older than that predates the plugins there now.
    static DateTime LastPluginChange(string profile)
    {
        var plugins = Install.Plugins(profile);
        if (!Directory.Exists(plugins)) return DateTime.MinValue;
        var folders = Directory.GetDirectories(plugins).Concat(ProgramManifest.Current.Packages.Select(p => Install.Home(profile, p.Name)).Where(Directory.Exists)).Append(plugins);
        return folders.Max(Directory.GetLastWriteTimeUtc);
    }
}
