using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The placement authority produced by this startup's successful native Rundown publications.
/// It cannot be constructed from a caller's approval flag or from the fact that an Item number is installed.</summary>
internal sealed class RundownPlacementAdmission
{
    private sealed record Owner(HashSet<MapLevelReference> Levels, HashSet<uint> Items);
    private readonly IReadOnlyDictionary<string, Owner> _owners;
    private readonly HashSet<uint> _vanillaItems;
    internal IReadOnlyList<RundownPack> Packs { get; }
    /// <summary>Every EnemyGroup row a Forge package published (the published Rundowns' own and the accepted content entries'),
    /// by number, with its package: the ownership the native random pool is scoped by (<see cref="EnemyGroupPool"/>).</summary>
    internal IReadOnlyDictionary<uint, string> EnemyGroupOwners { get; }

    private RundownPlacementAdmission(IReadOnlyList<RundownPack> packs,
        IReadOnlyDictionary<string, Owner> owners, HashSet<uint> vanillaItems, IReadOnlyDictionary<uint, string> enemyGroupOwners)
    { Packs = packs; _owners = owners; _vanillaItems = vanillaItems; EnemyGroupOwners = enemyGroupOwners; }

    /// <summary>Validate against actual accepted content, then publish. A refused or failed publication never
    /// gives its sidecars authority. The injected publisher is the real native table transaction in production.</summary>
    internal static RundownPlacementAdmission Publish(AdmittedContentSnapshot content,
        IReadOnlyList<RundownPack> installed, IEnumerable<ContentVanillaId> occupied,
        Action<RundownPack> publish, Action<RundownDiagnostic> report)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(occupied);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(report);
        if (NativeContentRefusal.Stopped != null)
            throw new InvalidOperationException("Native tables failed their ownership audit; restart before Rundown publication.");
        var observed = occupied.ToArray();
        var vanilla = new HashSet<uint>();
        foreach (uint id in observed.Where(row => row.Type == "Item").Select(row => row.Id).Distinct())
        {
            if (!content.IsOriginalItem(id)) continue;
            try { NativeDataBlockTables.RequireBlock("Item", id); vanilla.Add(id); }
            catch (Exception error) when (error is InvalidOperationException or System.Reflection.TargetInvocationException)
            { report(new(RundownPacks.RejectedCode, "", "Item/" + id, "", "Original Item is not loaded: " + error.GetBaseException().Message)); }
        }
        var loaded = new List<RundownPack>();
        var owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        foreach (var pack in content.ValidateRundowns(RundownPacks.SelectSlots(installed, report), observed, report))
        {
            try
            {
                string root = OwningDirectory(pack.Path);
                if (owners.ContainsKey(root)) throw new InvalidDataException("Rundown root has more than one owner: " + root);
                var levels = MapPackageAdmission.Levels(pack);
                var items = new HashSet<uint>(pack.Blocks.Where(block => block.Type == "Item").Select(block => block.PersistentId)
                    .Concat(content.DependencyItems(pack)));
                publish(pack);
                // Commit provenance only after the native publisher has returned successfully.
                owners.Add(root, new(levels, items));
                loaded.Add(pack);
            }
            catch (Exception error)
            {
                if (NativeContentRefusal.StopsOn(error)) throw;
                report(new(RundownPacks.RejectedCode, pack.Package, pack.Rundown, "", error.GetBaseException().Message));
            }
        }
        var groups = new Dictionary<uint, string>();
        foreach (var ((type, id), package) in content.BlockPackages)
            if (type == "EnemyGroup") groups[id] = package;
        foreach (var pack in loaded)
            foreach (var block in pack.Blocks)
                if (block.Type == "EnemyGroup") groups[block.PersistentId] = pack.Package;
        return new(Array.AsReadOnly(loaded.ToArray()), owners, vanilla, groups);
    }

    // Both addresses come from the shared path-safe discovery. Runtime owns which installation projection is
    // canonical; Map only joins an admitted Rundown and its own sibling sidecar, without guessing outer folders.
    private static string OwningDirectory(string relative)
    {
        string normalized = ForgeCore.PosixPath.ToPosix(relative);
        int slash = normalized.LastIndexOf('/');
        if (slash < 0) throw new InvalidDataException("Placement document has no owning package root.");
        string directory = normalized.Substring(0, slash);
        if (!directory.EndsWith("/forge", StringComparison.Ordinal))
            throw new InvalidDataException("Placement document is outside its owning forge directory.");
        return directory.Substring(0, directory.Length - "/forge".Length);
    }

    private bool TryOwner(PublishedResource document, out Owner? owner)
    {
        owner = null;
        try { return _owners.TryGetValue(OwningDirectory(document.Relative), out owner); }
        catch (InvalidDataException) { return false; }
    }

    internal bool AllowsDocument(PublishedResource document, string prefix, Action<string> rejected)
    {
        if (TryOwner(document, out _)) return true;
        rejected(PublishedResources.Diagnostic(document.Relative, ForgeEngine.Logging.DynamicDiagnosticCodes.Of("{prefix}-owner", prefix),
            "The sidecar has no successfully published owning Rundown in this startup."));
        return false;
    }

    internal bool AllowsLevel(PublishedResource document, MapLevelReference level, string prefix, Action<string> rejected)
    {
        if (TryOwner(document, out var owner) && owner!.Levels.Contains(level)) return true;
        rejected(PublishedResources.Diagnostic(document.Relative, ForgeEngine.Logging.DynamicDiagnosticCodes.Of("{prefix}-level-owner", prefix),
            "Level " + level + " is not an authored tier/index of this sidecar's published Rundown."));
        return false;
    }

    internal bool AllowsItem(PublishedResource document, uint itemId, Action<string> rejected)
    {
        if (TryOwner(document, out var owner) && (_vanillaItems.Contains(itemId) || owner!.Items.Contains(itemId))) return true;
        rejected(PublishedResources.Diagnostic(document.Relative, "fixed-item-provenance",
            "Item " + itemId + " is neither a verified loaded original, a published own Item nor an Item of a declared dependency."));
        return false;
    }
}
