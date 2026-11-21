using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeDevelopment.Native;

// MTFO 4.6.3 ConfigManager searches BepInEx/GameData then plugins for GameData_*.json.
// RundownPacks.Discover searches plugins/<package>/forge/rundown.forge; mods.yml is not a runtime gate.
internal static class LevelGuideCapturePurity
{
    internal static string? Refusal(string bep, bool official)
    {
        if (!official) return null;
        var contaminants = new List<string>();
        foreach (var root in new[] { Path.Combine(bep, "GameData"), Path.Combine(bep, "plugins") })
        {
            foreach (var path in Files(root))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("GameData_", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    contaminants.Add("MTFO DataBlock override: " + path);
            }
        }
        return contaminants.Count == 0 ? null : "Official capture refused; installed input can change game data: "
            + string.Join("; ", contaminants.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Capture cannot verify a reparse point: " + root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Capture cannot verify a reparse point: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Files(path)) yield return child;
            }
            else yield return path;
        }
    }
}
