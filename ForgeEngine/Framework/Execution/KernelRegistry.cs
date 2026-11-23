using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>What is registered with the kernel: the registry tables, the module tokens, each provider's package,
/// published services, declared definition fields, and the typed views read off the tables. It decides nothing about
/// plans, the world or dispatch; the facade drives a registration through it and tells the layers above.</summary>
internal sealed class KernelRegistry
{
    /// <summary>The registry tables every plan parse and every provider lookup reads.</summary>
    internal RuntimeRegistry Tables = new();
    /// <summary>provider id → the token its current registration was given.</summary>
    internal readonly Dictionary<string, long> Modules = new(StringComparer.Ordinal);
    private long generation;
    internal readonly Dictionary<Type, (string Owner, object Service)> Services = new();

    internal bool IsRegistered(string provider, long token) => Modules.TryGetValue(provider, out var current) && current == token;

    /// <summary>The registry one module would produce, checked against the registration budgets and the packages
    /// already registered. Nothing is installed: a refusal leaves every table as it was.</summary>
    internal RuntimeRegistry Admit(RuntimeModule module, string apiVersion, out string provider)
    {
        RuntimeJson.Require(Modules.Count < 128, "module-budget", "Module capacity reached.");
        var next = Tables.WithModule(module, apiVersion, out provider);
        RuntimeJson.Require(next.Providers.Count <= 2048 && next.Capabilities.Count <= 2048 && next.Bindings.Count <= 2048, "registry-budget", "Registration capacity reached.");
        RequirePackages(module, provider, next);
        return next;
    }

    /// <summary>Installs an admitted registry and hands the provider its token.</summary>
    internal long Install(RuntimeRegistry next, string provider)
    {
        Tables = next; var token = ++generation; Modules.Add(provider, token); ModuleTableVersion++;
        return token;
    }

    /// <summary>The load-bound declarations a detached migration image reads: the same tables and views, by reference
    /// where they are immutable snapshots and by copy where the live kernel keeps mutating them.</summary>
    internal void CopyLoadBoundTo(KernelRegistry image)
    {
        image.Tables = Tables;
        image.ModuleTableVersion = ModuleTableVersion;
        image.ResolverTable = ResolverTable;
        image.EntityObserverTable = EntityObserverTable;
        foreach (var pair in Modules) image.Modules.Add(pair.Key, pair.Value);
        foreach (var pair in BindingViews) image.BindingViews.Add(pair.Key, pair.Value);
    }

    internal void PublishService(Type type, string provider, object service)
    {
        RuntimeJson.Require(!Services.ContainsKey(type), "service-conflict", type.FullName ?? type.Name);
        Services.Add(type, (provider, service));
    }

    internal bool TryService<T>([MaybeNullWhen(false)] out T service) where T : class
    {
        if (Services.TryGetValue(typeof(T), out var entry)) { service = (T)entry.Service; return true; }
        service = null; return false;
    }

    internal void RemoveServices(string provider)
    {
        foreach (var type in Services.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            Services.Remove(type);
    }

    /// <summary>Records one provider's declared fields. The whole list is checked before any of it is recorded.</summary>
    internal void DeclareDefinitionFields(string provider, IReadOnlyCollection<string> fieldIds)
    {
        // The whole list is checked before any of it is recorded, so a declaration naming one bad or duplicate
        // field leaves the table exactly as it was — the same atomicity every other registration refusal has.
        var declared = new List<string>(fieldIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fieldId in fieldIds)
        {
            RuntimeJson.Require(fieldId != null && DefinitionFieldId.IsMatch(fieldId), "definition-field",
                "A definition field id must be <host>.forge.<...>: " + (fieldId ?? "(null)"));
            RuntimeJson.Require(seen.Add(fieldId!) && !DefinitionFields.ContainsKey(fieldId!),
                "definition-field-conflict", fieldId!);
            declared.Add(fieldId!);
        }
        foreach (var fieldId in declared) DefinitionFields.Add(fieldId, provider);
    }

    /// <summary>A declared definition field is a fact about its provider's appliers, so it leaves with them; a
    /// leftover would keep a field implemented in the manifest while nothing writes it any more.</summary>
    internal void RemoveDefinitionFields(string provider)
    {
        foreach (var fieldId in DefinitionFields.Where(x => x.Value == provider).Select(x => x.Key).ToArray())
            DefinitionFields.Remove(fieldId);
    }

    /// <summary>One declared Forge block field id to the provider whose appliers write it. The manifest
    /// exports this table in field-id order; a second declaration of the same id is refused instead of
    /// replacing the first owner.</summary>
    internal readonly Dictionary<string, string> DefinitionFields = new(StringComparer.Ordinal);
    /// <summary>`<host>.forge.<...>`: the host is one object-editor kind, every following segment is a block
    /// or field path, and only a `forge.` block is one a mod's own applier writes. Segments carry the
    /// capitals and hyphens the block names use (`forge.vanilla.item.consumable-spawn-distribution`).</summary>
    private static readonly Regex DefinitionFieldId =
        new(@"^[a-z][a-z0-9_]*\.forge(\.[A-Za-z0-9_-]+)+$", RegexOptions.CultureInvariant);


    private static readonly Regex PackageName = new(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    /// <summary>provider id → the package it declared (<see cref="RuntimeModule.Package"/>).</summary>
    internal readonly Dictionary<string, string> Packages = new(System.StringComparer.Ordinal);

    /// <summary>Every `Owner-Name-x.y.z` pin of the registering provider's row must name a package some registered
    /// provider ships, at exactly that version. The pins' grammar is already checked by
    /// <see cref="RuntimeRegistry.ValidatePackages"/> inside <c>WithModule</c>.</summary>
    private void RequirePackages(RuntimeModule module, string provider, RuntimeRegistry next)
    {
        RuntimeJson.Require(module.Package == null || PackageName.IsMatch(module.Package), "package-name", module.Package ?? "");
        foreach (var pin in RuntimeJson.Strings(next.Providers[provider].GetProperty("dependencies")))
        {
            int cut = pin.LastIndexOf('-');
            string name = pin[..cut], version = pin[(cut + 1)..];
            var owners = Packages.Where(x => x.Value == name).Select(x => x.Key).ToArray();
            RuntimeJson.Require(owners.Length > 0, "package-missing", pin);
            RuntimeJson.Require(owners.All(owner => RuntimeJson.Text(Tables.Providers[owner], "version") == version),
                "package-version-mismatch", pin);
        }
    }

    internal readonly Dictionary<string, BindingView> BindingViews = new(StringComparer.Ordinal);
    /// <summary>Every registered entity kind with the resolver that answers for it, read by the kind's own text
    /// span so a reference is routed without cutting its kind out as a string.</summary>
    internal EntityKindResolver[] ResolverTable = Array.Empty<EntityKindResolver>();
    internal (string Kind, Func<EntityReference, RuntimeEntitySnapshot?> Observe, EntityPoseObserver? Pose)[] EntityObserverTable
        = Array.Empty<(string, Func<EntityReference, RuntimeEntitySnapshot?>, EntityPoseObserver?)>();
    /// <summary>Moves with every module registration and unregistration: the one table a loaded plan's module
    /// tokens are checked against, so a plan checked at the current number needs no second look.</summary>
    internal long ModuleTableVersion;


    /// <summary>Views for the bindings that arrived and none for the ones that left.</summary>
    internal void RefreshBindingViews(IEnumerable<string>? removedBindings)
    {
        if (removedBindings != null) foreach (var id in removedBindings) BindingViews.Remove(id);
        foreach (var binding in Tables.Bindings)
            if (!BindingViews.ContainsKey(binding.Key))
                BindingViews.Add(binding.Key, new BindingView(binding.Key, binding.Value,
                    Tables.Capabilities[RuntimeJson.Text(binding.Value, "capabilityId")]));
    }

    internal void RefreshEntityObserverTable()
        => EntityObserverTable = Tables.EntityObservers.Select(x => (x.Key, x.Value.Observe,
            Tables.EntityPoseObservers.TryGetValue(x.Key, out var pose) ? pose.Observe : null)).ToArray();

    /// <summary>The resolver that answers for one reference's kind — the text before its first colon — or false
    /// where the reference names no kind or a kind nothing registered.</summary>
    internal bool TryResolver(string entityId, out Func<EntityReference, bool> resolve)
    {
        var found = TryEntityKind(entityId, out var kind);
        resolve = found ? kind.Resolve : null!;
        return found;
    }

    /// <summary>The registered kind one reference routes to, as the object a capture keeps.</summary>
    internal bool TryEntityKind(string entityId, out EntityKindResolver kind)
    {
        var split = entityId.IndexOf(':');
        if (split > 0)
        {
            var name = entityId.AsSpan(0, split);
            var table = ResolverTable;
            for (var i = 0; i < table.Length; i++)
                if (name.SequenceEqual(table[i].Kind)) { kind = table[i]; return true; }
        }
        kind = null!;
        return false;
    }

    /// <summary>Rebuilds the kind table from the  A kind whose registration is unchanged keeps its object, so
    /// what a capture holds stays the current kind; one whose resolver, owner or end-report promise went away is
    /// retired, and every capture still holding it asks its captured resolver again from then on.</summary>
    internal void RefreshResolverTable()
    {
        var previous = ResolverTable;
        var next = new EntityKindResolver[Tables.Resolvers.Count];
        var index = 0;
        foreach (var (kind, (owner, resolve)) in Tables.Resolvers)
        {
            var reports = Tables.EntityEndReports.TryGetValue(kind, out var reporter) && reporter == owner;
            EntityKindResolver? kept = null;
            foreach (var entry in previous)
                if (entry.Kind == kind && entry.Owner == owner && ReferenceEquals(entry.Resolve, resolve) && entry.ReportsEnd == reports)
                { kept = entry; break; }
            next[index++] = kept ?? new EntityKindResolver(kind, owner, resolve, reports);
        }
        foreach (var entry in previous)
            if (Array.IndexOf(next, entry) < 0) entry.Retired = true;
        ResolverTable = next;
    }

    internal bool TryEntityObserver(string entityId, out Func<EntityReference, RuntimeEntitySnapshot?> observe)
        => TryEntityObserver(entityId, out observe, out _);

    internal bool TryEntityObserver(string entityId, out Func<EntityReference, RuntimeEntitySnapshot?> observe, out EntityPoseObserver? pose)
    {
        for (var i = 0; i < EntityObserverTable.Length; i++)
            if (OfKind(entityId, EntityObserverTable[i].Kind))
            { observe = EntityObserverTable[i].Observe; pose = EntityObserverTable[i].Pose; return true; }
        observe = null!; pose = null;
        return false;
    }

    private string? exportedManifest;
    private long exportedManifestRegistry = -1;
    private int exportedManifestFields = -1;
    internal string ExportManifest(RuntimeIdentity identity, RuntimeLimits limits)
    {
        // Registry revision covers Snapshot and Support, including support updates without registration.
        // Definition fields only grow between module removals (which also advance the registry revision).
        // Identity and Limits are immutable constructor inputs.
        if (exportedManifest != null && exportedManifestRegistry == Tables.ManifestVersion
            && exportedManifestFields == DefinitionFields.Count) return exportedManifest;
        var text = RuntimeJson.StableText(RuntimeJson.From(new
        {
            schemaVersion = ForgeCore.FormatVersion.Current,
            runtime = identity,
            registry = Tables.Snapshot(),
            definitionFields = DefinitionFields.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new { fieldId = x.Key, providerId = x.Value }).ToArray(),
            bindingSupport = Tables.Support.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(),
            // The action bindings whose module registered the callback that ends a lasting effect: only these may
            // carry an `effect` without an interval (RuntimePlan "effect-unsupported"), so authoring reads it here.
            effectRestores = Tables.EffectRestores.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            limits
        }));
        exportedManifestRegistry = Tables.ManifestVersion;
        exportedManifestFields = DefinitionFields.Count;
        return exportedManifest = text;
    }
}
