using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>The Forge packages installed together must satisfy one release: the host carries the release identity it was built with
/// (<c>Mods/Release/release.json</c>, embedded), and every installed Forge package, base package or library alike, must have the same
/// major version as that release and a minor at or above it (contract 1.4; only a major change breaks).
/// Only Forge's own packages in that file are compared; other plugins are never inspected, and a package that is not installed is not an error.</summary>
internal static class ReleaseCoherence
{
    internal const string Code = "release-mismatch";

    /// <summary>The installed Forge packages that do not satisfy the release; empty when all do.</summary>
    internal static List<(string Package, string Installed, string Version)> Differences(IReadOnlyDictionary<string, (string Package, string Version)> release, Func<string, string?> installedVersion)
    {
        var differing = new List<(string, string, string)>();
        foreach (var (guid, (package, version)) in release.OrderBy(row => row.Value.Package, StringComparer.Ordinal))
        {
            var installed = installedVersion(guid);
            if (installed != null && !ForgeVersion.Meets(installed, version))
                differing.Add((package, installed, version));
        }
        return differing;
    }

    /// <summary>One "package installed X, requires Y" line each; null when all agree.</summary>
    internal static string? Mismatch(IReadOnlyDictionary<string, (string Package, string Version)> release, Func<string, string?> installedVersion)
    {
        var differing = Differences(release, installedVersion);
        return differing.Count == 0 ? null
            : "Installed Forge packages do not match this release (" + string.Join("; ", differing.Select(row => row.Package + " installed " + row.Installed + ", requires " + row.Version + " or newer of the same major version"))
                + "). Install every Forge package at the stated version or a newer release of the same major version.";
    }
}
