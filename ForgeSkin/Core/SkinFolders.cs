namespace ForgeSkin.Format;

/// <summary>Where ForgeSkin keeps its data, from a BepInEx folder: the running game's (plugin) or a profile's (editor). Everything
/// lives in BepInEx/ForgeSkin/, outside plugins/, so a mod manager updating or removing the plugin never touches it.</summary>
public static class SkinFolders
{
    public static string Root(string bepInEx) => Path.Combine(bepInEx, "ForgeSkin");

    /// <summary>The installed packs, active.json and the editor's one-shot .pick files.</summary>
    public static string Skins(string bepInEx) => Path.Combine(Root(bepInEx), "skins");

    /// <summary>The plugin's log for the editor (what loaded, what failed; every detail with Development on), read after 应用到游戏.</summary>
    public static string AuthorLog(string bepInEx) => Path.Combine(Root(bepInEx), "forgeskin.log");

    /// <summary>The file a skin mod (a mod folder directly under BepInEx/plugins) lists its packs in, one path per line.</summary>
    public const string ModMarker = "forgeskin.forge";

    /// <summary>Every pack the skin mods under `plugins` list: the mod folder and the pack's full path, or why the line names no pack.</summary>
    public static IEnumerable<(string Mod, string Pack, string? Problem)> ModPacks(string plugins)
    {
        if (!Directory.Exists(plugins)) yield break;
        foreach (var mod in Directory.GetDirectories(plugins))
        {
            var marker = Path.Combine(mod, ModMarker);
            if (!File.Exists(marker)) continue;
            foreach (var raw in File.ReadAllLines(marker))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var pack = Path.GetFullPath(Path.Combine(mod, line));
                var problem = !pack.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase) ? $"{Path.GetFileName(mod)}/{ModMarker}: {line} is not a {SkinPack.Extension} pack"
                    : !File.Exists(pack) ? $"{Path.GetFileName(mod)}/{ModMarker}: {line} not found" : null;
                yield return (mod, pack, problem);
            }
        }
    }

    /// <summary>Skin files under `plugins` that ModPacks never reads, as a line each saying where they belong: a marker not directly in a
    /// mod folder (in plugins itself, or a folder deeper, as a zip with a top folder unpacks), and a pack in plugins itself or in a mod
    /// folder with no marker at its top. A pack a marker leaves out is the mod's own choice and not listed. Names only; nothing is read.</summary>
    public static IEnumerable<(string File, string Problem)> Misplaced(string plugins)
    {
        if (!Directory.Exists(plugins)) yield break;
        var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(plugins, "*", all))
        {
            var name = Path.GetFileName(file);
            bool marker = name.Equals(ModMarker, StringComparison.OrdinalIgnoreCase);
            if (!marker && !name.EndsWith(SkinPack.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(plugins, file).Replace('\\', '/');
            int depth = rel.Count(c => c == '/');
            if (marker && depth != 1)
                yield return (file, $"plugins/{rel}: a skin mod's {ModMarker} is read only directly inside its mod folder (plugins/<folder>/{ModMarker}); this one is not read");
            else if (!marker && (depth == 0 || !File.Exists(Path.Combine(plugins, rel[..rel.IndexOf('/')], ModMarker))))
                yield return (file, $"plugins/{rel}: a skin pack is read from the plugins folder only when plugins/<folder>/{ModMarker} lists it; this one is not read (or install it into BepInEx/ForgeSkin/skins)");
        }
    }

    public static string Readback(string bepInEx) => Path.Combine(Root(bepInEx), "readback");

    /// <summary>Where earlier editors installed their own copy of the plugin DLL (the editor now installs where a mod manager does,
    /// plugins/NAinfini-ForgeSkin/ForgeSkin/, and clears this one); old skins folders lived under it.</summary>
    public static string Plugin(string bepInEx) => Path.Combine(bepInEx, "plugins", "ForgeSkin");
}
