using System;
using System.IO;
using System.Linq;
using BepInEx;

namespace ForgeDevelopment.Native;

/// <summary>The installed game build and the native prefab-name rule that Development's vanilla-value captures name their
/// subjects with.</summary>
internal static class GameIdentity
{
    private static string? _gameBuild;
    // Application.version is an unstripped Unity icall absent from this dump/catalog. A binary
    // fingerprint is an exact installed-build identity without depending on that generated wrapper.
    internal static string GameBuild
    {
        get
        {
            return _gameBuild ??= "GameAssembly:sha256:" + ForgeCore.FileHashes.Sha256Hex(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
        }
    }

    internal static string NativePrefabName(string name)
    {
        // These are native instance decorations. Authored numbers and variant labels remain intact.
        foreach (var suffix in new[] { "(Clone)", "_GO_UID:", "_terminalKey:", " GUIAng:" })
        {
            var at = name.IndexOf(suffix, StringComparison.Ordinal);
            if (at >= 0) name = name[..at];
        }
        name = name.TrimEnd();
        const string serial = "_TERMINAL_";
        var terminal = name.LastIndexOf(serial, StringComparison.Ordinal);
        if (terminal > 0 && terminal + serial.Length < name.Length
            && name[(terminal + serial.Length)..].All(value => value is >= '0' and <= '9'))
            name = name[..terminal];
        return name;
    }
}
