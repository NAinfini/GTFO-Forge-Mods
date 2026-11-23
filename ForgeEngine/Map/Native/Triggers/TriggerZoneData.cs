using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>
/// The trigger zones this install authored, read once at plugin start from the package documents that declare them.
///
/// A package writes one document at `plugins/&lt;package&gt;/forge/trigger-zones.forge` — the same one-level package
/// directory rule plans and the other authored content files use. The data is the author's own placement, so the
/// reader is strict and all-or-nothing per document: a document that does not parse is refused whole with one
/// diagnostic, never half-applied. Nothing here reads a native object: the volume is the exported data, which is
/// what makes every machine build the same zones and the same blocking colliders.
///
/// The snapshot is immutable once loaded: there is no watch and no reload, because two machines disagreeing about
/// the volume a player is standing in would be a different world on each of them.
/// </summary>
internal static class TriggerZoneData
{
    /// <summary>The refusals of reading a document of this family.</summary>
    private static readonly PublishedTextCodes Codes = new("trigger-zone-path", "trigger-zone-unreadable", "trigger-zone-file-size",
        "trigger-zone-json", "trigger-zone-utf8");

    /// <summary>Every zone every package of this install declares, in ordinal document order and, inside one
    /// document, in the order the author wrote them. A zone id claimed by two documents is withdrawn whole: which
    /// volume an id names never depends on the order the files happened to be found in.</summary>
    internal static IReadOnlyList<TriggerZone> Load(string bepInExRoot, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(rejected);
        var parsed = new List<(string Relative, TriggerZone Zone)>();
        foreach (var document in ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", TriggerZoneManifest.FileNameOnly, Codes, rejected))
        {
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, Codes, rejected, out var text))
                continue;
            if (!TriggerZoneManifest.TryParse(text!, out var zones, out var code, out var reason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            foreach (var zone in zones!) parsed.Add((document.Relative, zone));
        }
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, zone) in parsed)
            claims[zone.Id] = claims.TryGetValue(zone.Id, out var count) ? count + 1 : 1;
        var loaded = new List<TriggerZone>(parsed.Count);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, zone) in parsed)
        {
            if (claims[zone.Id] == 1) { loaded.Add(zone); continue; }
            if (withdrawn.Add(zone.Id))
                rejected(PublishedResources.Diagnostic(relative, "trigger-zone-duplicate-id",
                    "Zone id `" + zone.Id + "` is claimed by " + claims[zone.Id]
                    + " documents; the zone is left out of this install."));
        }
        var crowded = loaded.GroupBy(zone => zone.Level).FirstOrDefault(group => group.Count() > TriggerZoneModule.MaximumZones);
        if (crowded != null)
        {
            rejected("trigger-zone-limit: this install declares " + crowded.Count() + " zones for level " + crowded.Key
                + "; the cap is " + TriggerZoneModule.MaximumZones + " per level. No zone was loaded.");
            return Array.Empty<TriggerZone>();
        }
        return Array.AsReadOnly(loaded.ToArray());
    }

    /// <summary>The default install root, so the one caller that has no argument of its own names the same path the
    /// plan scanner does.</summary>
    internal static IReadOnlyList<TriggerZone> Load(Action<string> rejected) => Load(Paths.BepInExRootPath, rejected);
}
