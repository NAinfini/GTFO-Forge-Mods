using System;
using System.Collections.Generic;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

internal static class InteractionPointData
{
    private static readonly PublishedTextCodes Codes = new("interaction-point-path", "interaction-point-unreadable",
        "interaction-point-file-size", "interaction-point-json", "interaction-point-utf8");

    internal static IReadOnlyList<InteractionPoint> Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<(string Relative, InteractionPoint Point)>();
        foreach (var document in ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", InteractionPointManifest.FileNameOnly, Codes, rejected))
        {
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, Codes, rejected,
                    out var text)) continue;
            if (!InteractionPointManifest.TryParse(text!, out var points, out var code, out var reason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            foreach (var point in points!) parsed.Add((document.Relative, point));
        }

        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, point) in parsed)
            claims[point.Id] = claims.TryGetValue(point.Id, out var count) ? count + 1 : 1;
        var loaded = new List<InteractionPoint>(parsed.Count);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, point) in parsed)
        {
            if (claims[point.Id] == 1) { loaded.Add(point); continue; }
            if (withdrawn.Add(point.Id))
                rejected(PublishedResources.Diagnostic(relative, "interaction-point-duplicate-id",
                    "Point id `" + point.Id + "` is claimed by " + claims[point.Id]
                    + " documents; the point is left out of this install."));
        }
        return Array.AsReadOnly(loaded.ToArray());
    }

    internal static IReadOnlyList<InteractionPoint> Load(Action<string> rejected)
        => Load(Paths.BepInExRootPath, rejected);
}
