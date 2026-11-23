using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEngine.GameBindings;

/// <summary>A primary native entry accepted by its domain consumer, from the host's frozen startup discovery.
/// A native child Item of PlayerOfflineGear is deliberately not an independently referenceable entry.</summary>
internal sealed class AdmittedContentEntry
{
    internal AdmittedContentEntry(string root, string package, string version, string stableId, string nativeType, uint persistentId)
    { PackageRoot = root; Package = package; Version = version; StableId = stableId; NativeType = nativeType; PersistentId = persistentId; }
    public string PackageRoot { get; }
    public string Package { get; }
    public string Version { get; }
    public string StableId { get; }
    public string NativeType { get; }
    public uint PersistentId { get; }
}

/// <summary>Host-assembly integration only; it does not extend the public Framework SDK or its fingerprint.
/// No caller can construct an acceptance proof from a number or a discovered-but-refused content file.</summary>
internal sealed class AdmittedContentSnapshot
{
    private readonly Dictionary<string, AdmittedContentEntry> entries;
    private readonly HashSet<string> refusedRoots;
    internal AdmittedContentSnapshot(ContentCatalog catalog, IReadOnlySet<string> accepted)
    {
        refusedRoots = new(catalog.RefusedPackageRoots, StringComparer.Ordinal);
        var packages = catalog.Packs.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1 && !refusedRoots.Contains(group.Single().Path))
            .ToDictionary(group => group.Single().Package, group => group.Single(), StringComparer.Ordinal);
        entries = new(StringComparer.Ordinal);
        var blocks = new Dictionary<(string Type, uint Id), string>();
        foreach (var row in catalog.Accepted.Where(row => accepted.Contains(row.Entry.Id)))
        {
            var identities = ContentPackDiscovery.NativeIdentities(row.Entry);
            if (identities.Count != 0 && ContentIdentity.TryParse(row.Entry.Id, out var package, out _))
                foreach (var block in identities)
                    if (row.BlockIds.TryGetValue(block.Id, out uint number)) blocks[(block.Type, number)] = package;
            var primary = identities.SingleOrDefault(block => block.Id == row.Entry.Id);
            if (!(row.Entry.Domain == "weapon" && primary.Type == "Item" || row.Entry.Domain == "enemy" && primary.Type == "Enemy")) continue;
            if (!ContentIdentity.TryParse(row.Entry.Id, out var owner, out _) || !packages.TryGetValue(owner, out var pack))
                throw new InvalidDataException("Accepted entry does not match its canonical typed discovery identity.");
            entries.Add(row.Entry.Id, new(pack.Path, pack.Package, pack.Version, row.Entry.Id, primary.Type, row.PersistentId));
        }
        Entries = Array.AsReadOnly(entries.Values.OrderBy(item => item.StableId, StringComparer.Ordinal).ToArray());
        BlockPackages = blocks;
    }
    public IReadOnlyList<AdmittedContentEntry> Entries { get; }
    /// <summary>Every native row an accepted content entry publishes (primary and child blocks), by table and number, with its package.</summary>
    public IReadOnlyDictionary<(string Type, uint Id), string> BlockPackages { get; }
    public bool IsOriginalItem(uint persistentId) => RundownContentSchema.IsOriginalItem(persistentId);
    /// <summary>The accepted external Item numbers of the packages pack pins as exact dependencies: the only external Items it may reference.</summary>
    public IReadOnlyList<uint> DependencyItems(RundownPack pack)
        => Entries.Where(item => item.NativeType == "Item" && pack.Dependencies.Contains(item.Package + "-" + item.Version, StringComparer.Ordinal))
            .Select(item => item.PersistentId).ToArray();
    public IReadOnlyList<RundownPack> ValidateRundowns(IReadOnlyList<RundownPack> packs,
        IEnumerable<ContentVanillaId> occupied, Action<RundownDiagnostic> report)
    {
        var usable = new List<RundownPack>();
        foreach (var pack in packs)
        {
            if (!pack.DiscoveryAdmitted || refusedRoots.Contains(PublishedResources.PackageRoot(pack.Path)))
            { report(new(RundownPacks.RejectedCode, pack.Package, pack.Rundown, "", "Rundown owning root was not admitted at startup.")); continue; }
            usable.Add(pack);
        }
        // Accepted native entries participate even when the caller's occupied-table scan omitted that native table.
        return RundownPacks.Validate(usable, occupied.Concat(Entries.Select(item => new ContentVanillaId(item.NativeType, item.PersistentId, item.StableId))), report, ValidateNativeFields);
    }
    public string Materialize(RundownBlock block, RundownPack pack)
    {
        if (!pack.DiscoveryAdmitted || refusedRoots.Contains(PublishedResources.PackageRoot(pack.Path)))
            throw new InvalidDataException("Cannot materialize a block outside an admitted Rundown root.");
        var index = Index(pack);
        if (!index.Blocks.Contains(block))
            throw new InvalidDataException("Cannot materialize a block outside an admitted Rundown root.");
        var gear = RundownContentSchema.MaterializeGear(block, pack);
        ValidateNativeFields(pack);
        return RundownPacks.Materialize(gear, index.Ids);
    }
    // Startup materializes every block of a pack, and Map validates the same pack more than once. The whole-pack
    // native field walk and the owned-ID table are properties of this immutable snapshot and that pack instance,
    // so they are computed once per pack rather than once per block (which made publication quadratic).
    // Only a successful validation is remembered: an invalid pack is re-validated and refused on every call.
    private sealed class PackIndex
    {
        internal PackIndex(RundownPack pack)
        {
            Blocks = new HashSet<RundownBlock>(pack.Blocks);
            Ids = RundownPacks.OwnedIds(pack.Blocks);
        }
        internal readonly HashSet<RundownBlock> Blocks;
        internal readonly IReadOnlyDictionary<string, uint> Ids;
        internal bool FieldsValidated;
    }
    private readonly Dictionary<RundownPack, PackIndex> indexes = new(ReferenceEqualityComparer.Instance);
    private PackIndex Index(RundownPack pack)
    {
        if (!indexes.TryGetValue(pack, out var index)) indexes.Add(pack, index = new PackIndex(pack));
        return index;
    }
    private void ValidateNativeFields(RundownPack pack)
    {
        var index = Index(pack);
        if (index.FieldsValidated) return;
        RundownContentSchema.Validate(pack);
        index.FieldsValidated = true;
    }
}

/// <summary>One-shot write-capable host initialization after content installation and before plans/network Ready.
/// This is deliberately not a lifecycle observer; those remain strictly read-only.</summary>
internal static class ContentPackHostStartup
{
    private sealed class State
    {
        internal bool Dispatched;
        internal readonly List<Registration> Callbacks = new();
    }
    private sealed class Registration : IDisposable
    {
        internal Action<AdmittedContentSnapshot>? Callback;
        internal readonly IReadOnlyList<RundownIdentity> Rundowns;
        internal Registration(Action<AdmittedContentSnapshot> callback, IReadOnlyList<RundownPack>? rundowns)
        {
            Callback = callback;
            if (rundowns?.Count > 128) throw new InvalidDataException("Captured Rundown count budget exceeded.");
            Rundowns = Array.AsReadOnly((rundowns ?? Array.Empty<RundownPack>()).Select(pack =>
            {
                if (!pack.DiscoveryAdmitted || pack.Path != "plugins/" + pack.Package + "/forge/rundown.forge")
                    throw new InvalidDataException("Host startup requires the canonical discovered Rundown snapshot.");
                return new RundownIdentity("plugins/" + pack.Package, pack.Package, pack.Version, pack.Rundown, pack.MenuSlot,
                    Array.AsReadOnly(pack.Dependencies.OrderBy(pin => pin, StringComparer.Ordinal).ToArray()));
            }).ToArray());
        }
        public void Dispose() => Callback = null;
    }
    private sealed record RundownIdentity(string Root, string Package, string Version, string StableId, int MenuSlot, IReadOnlyList<string> Dependencies);
    private static readonly ConditionalWeakTable<RuntimeKernel, State> States = new();
    // A callback with no Rundown work supplies no list. Map supplies its already captured discovery; only
    // immutable identity values are retained here, never caller-mutable JsonObject or record collections.
    public static IDisposable Register(IForgeEngine runtime, Action<AdmittedContentSnapshot> callback,
        IReadOnlyList<RundownPack>? discoveredRundowns = null)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(callback);
        if (runtime is not RuntimeKernel kernel) throw new InvalidOperationException("Content startup requires the live Runtime host.");
        kernel.RequireContentHostAccess(registering: true);
        var state = States.GetValue(kernel, _ => new State());
        if (state.Dispatched || state.Callbacks.Count >= 32) throw new InvalidOperationException("Content startup registration is closed or full.");
        var registration = new Registration(callback, discoveredRundowns); state.Callbacks.Add(registration); return registration;
    }
    internal static ContentCatalog CheckPackageIdentities(RuntimeKernel kernel, ContentCatalog catalog)
    {
        kernel.RequireContentHostAccess(registering: false);
        var state = States.GetValue(kernel, _ => new State());
        if (state.Dispatched) throw new InvalidOperationException("Content identity preflight cannot follow dispatch.");
        var refused = new HashSet<string>(catalog.RefusedPackageRoots, StringComparer.Ordinal);
        var errors = new List<ContentRejection>();
        var priorExtraFailures = catalog.ExtraDependencies?.Failures.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, ContentRejection>(StringComparer.Ordinal);
        var structuralFailures = new Dictionary<string, ContentRejection>(StringComparer.Ordinal);
        var rundowns = state.Callbacks.Where(callback => callback.Callback != null).SelectMany(callback => callback.Rundowns)
            .GroupBy(identity => identity.Root, StringComparer.Ordinal).Select(group => group.First()).OrderBy(identity => identity.StableId, StringComparer.Ordinal);
        var menuSlots = new Dictionary<int, RundownIdentity>();
        var slotRefused = new HashSet<string>(catalog.MenuSlotRefusedPackageRoots, StringComparer.Ordinal);
        // A single-Rundown package (MenuSlot 0) owns the whole menu: positioned packages lose to it, other singles collide on 0.
        bool singleOwned = rundowns.Any(identity => identity.MenuSlot == 0);
        foreach (var identity in rundowns)
        {
            if (!menuSlots.TryAdd(identity.MenuSlot, identity) || singleOwned && identity.MenuSlot != 0)
            {
                refused.Add(identity.Root); slotRefused.Add(identity.Root);
                catalog.ExtraDependencies?.PreserveStructuralRefusal(identity.Root);
            }
        }
        foreach (var group in state.Callbacks.Where(callback => callback.Callback != null).SelectMany(callback => callback.Rundowns)
            .GroupBy(identity => identity.Root, StringComparer.Ordinal))
        {
            var first = group.First();
            bool Same(string package, string version, IEnumerable<string> dependencies)
                => first.Package == package && first.Version == version
                    && first.Dependencies.SequenceEqual(dependencies.OrderBy(pin => pin, StringComparer.Ordinal));
            bool conflict = group.Any(identity => identity.StableId != first.StableId || identity.MenuSlot != first.MenuSlot || !Same(identity.Package, identity.Version, identity.Dependencies))
                || catalog.Packs.Where(pack => pack.Path == group.Key).Any(pack => !Same(pack.Package, pack.Version, pack.Dependencies));
            if (!conflict) continue;
            refused.Add(group.Key);
            catalog.ExtraDependencies?.PreserveStructuralRefusal(group.Key);
            var failure = new ContentRejection(ContentPackDiscovery.CodeInvalid, group.Key + "/forge/rundown.forge",
                "Coexisting runtime package identities have different package, version or exact dependencies.", first.Package,
                group.Key + "/forge/rundown.forge");
            structuralFailures[group.Key] = failure; errors.Add(failure);
        }
        if (catalog.ExtraDependencies is { } extra)
        {
            var known = new HashSet<string>(extra.Failures.Keys, StringComparer.Ordinal);
            foreach (var identity in rundowns) extra.CheckRundown(identity.Root, identity.Dependencies);
            foreach (var failure in extra.Failures)
            {
                refused.Add(failure.Key);
                if (!known.Contains(failure.Key)) errors.Add(failure.Value);
            }
        }
        var blocked = new HashSet<string>(catalog.Packs.Where(pack => refused.Contains(pack.Path))
            .SelectMany(pack => pack.Entries).Select(entry => entry.Id), StringComparer.Ordinal);
        var accepted = new HashSet<string>(catalog.Accepted.Select(row => row.Entry.Id), StringComparer.Ordinal);
        var supersededPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in catalog.Packs.Where(pack => refused.Contains(pack.Path)))
        {
            var extraFailure = catalog.ExtraDependencies?.Failure(pack.Path);
            bool structuralOverride = priorExtraFailures.ContainsKey(pack.Path) && extraFailure == null;
            if (structuralOverride) supersededPaths.Add(priorExtraFailures[pack.Path].Path);
            foreach (var entry in pack.Entries)
            {
                if (!accepted.Contains(entry.Id) && !structuralOverride) continue;
                if (structuralOverride) supersededPaths.Add(entry.Path);
                var failure = extraFailure ?? (structuralFailures.TryGetValue(pack.Path, out var structuralFailure) ? structuralFailure
                    : new ContentRejection(ContentPackDiscovery.CodeInvalid, entry.Id,
                        "The owning package failed the captured Rundown/content identity preflight.", null, entry.Path));
                errors.Add(failure with { Entry = entry.Id, Path = entry.Path });
            }
        }
        return catalog with
        {
            Accepted = catalog.Accepted.Where(row => !blocked.Contains(row.Entry.Id)).ToArray(),
            RefusedPackageRoots = Array.AsReadOnly(refused.OrderBy(path => path, StringComparer.Ordinal).ToArray()),
            MenuSlotRefusedPackageRoots = Array.AsReadOnly(slotRefused.OrderBy(path => path, StringComparer.Ordinal).ToArray()),
            Rejections = catalog.Rejections.Where(row => !supersededPaths.Contains(row.Path)).Concat(errors)
                .OrderBy(row => row.Entry, StringComparer.Ordinal).ToArray()
        };
    }

    internal static void Dispatch(RuntimeKernel kernel, AdmittedContentSnapshot snapshot)
    {
        kernel.RequireContentHostAccess(registering: false);
        var state = States.GetValue(kernel, _ => new State());
        if (state.Dispatched) throw new InvalidOperationException("Content host startup already dispatched.");
        state.Dispatched = true;
        try
        {
            foreach (var registration in state.Callbacks)
            {
                if (NativeContentRefusal.Stopped != null) throw new InvalidOperationException("Native tables failed their ownership audit; restart before loading plans.");
                registration.Callback?.Invoke(snapshot);
            }
            if (NativeContentRefusal.Stopped != null) throw new InvalidOperationException("Native tables failed their ownership audit; restart before loading plans.");
        }
        finally { foreach (var registration in state.Callbacks) registration.Dispose(); state.Callbacks.Clear(); }
    }
}
