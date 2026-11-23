using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeEngine.Framework;

internal sealed class RuntimeRegistry
{
    private readonly Dictionary<string, JsonElement> providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> capabilities = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, JsonElement> Providers => providers;
    internal IReadOnlyDictionary<string, JsonElement> Capabilities => capabilities;
    /// <summary>What each capability reads besides its ports, by capability id, in declaration order. Kept beside
    /// the definitions rather than inside them because it is a registration fact a plan step is judged by: a
    /// `query` step's authority is its world ports or this list, and a `pure` step may have neither.</summary>
    internal readonly Dictionary<string, string[]> CapabilityReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> bindings = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, JsonElement> Bindings => bindings;
    internal readonly Dictionary<string, CommandHandler> Handlers = new(StringComparer.Ordinal);
    /// <summary>The restore callback of each binding whose module registered one, keyed by binding id and owned by
    /// the provider that registered it. A binding without one cannot carry an `effect` block in a plan, which is
    /// how the kernel knows a duration it hands out will be undone.</summary>
    internal readonly Dictionary<string, (string Owner, EffectRestoreHandler Restore)> EffectRestores = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, EvaluatorHandler> Evaluators = new(StringComparer.Ordinal);
    /// <summary>Each binding's resolved handler shape, by binding id: the registration-time answer to "which port
    /// is this handler's amount", kept for the dispatch path so it never asks again.</summary>
    internal readonly Dictionary<string, HandlerShape> Shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BindingSupport> support = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, BindingSupport> Support => support;
    internal long ManifestVersion { get; private set; }

    // All writers of the manifest's four tables are private or pass through these versioned entry points.
    // JsonElement values are immutable; support permissions are copied so callers cannot mutate the cache key.
    internal void SetSupport(BindingSupport value)
    {
        RuntimeJson.Require(Bindings.ContainsKey(value.BindingId), "binding-unavailable", value.BindingId);
        RuntimeJson.Require(value.Verification is "implementation-only" or "game-verified", "verification", value.BindingId);
        var permissions = value.RequiredPermissions.ToArray();
        RuntimeJson.Require(permissions.All(RuntimeJson.IsId) && permissions.Distinct(StringComparer.Ordinal).Count() == permissions.Length,
            "permissions", value.BindingId);
#if !FORGE_RELEASE_MANIFEST_EXPORT
        var provider = RuntimeJson.Text(Bindings[value.BindingId], "providerId");
        foreach (var permission in permissions)
            RuntimeJson.Require(RuntimePermissions.IsAllowed(provider, permission), "permission-scope", value.BindingId);
#endif
        if (support.TryGetValue(value.BindingId, out var prior) && prior.Verification == value.Verification
            && prior.RequiredPermissions.SequenceEqual(permissions)) return;
        support[value.BindingId] = value with { RequiredPermissions = Array.AsReadOnly(permissions) };
        ManifestVersion++;
    }

    internal void RemoveModuleManifest(string provider, IEnumerable<string> ownedCaps, IEnumerable<string> ownedBindings)
    {
        providers.Remove(provider);
        foreach (var id in ownedCaps) capabilities.Remove(id);
        foreach (var id in ownedBindings) { bindings.Remove(id); support.Remove(id); }
        ManifestVersion++;
    }
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, bool> Resolve)> Resolvers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> CapabilityRegistrants = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, RuntimeEntitySnapshot?> Observe)> EntityObservers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (string Owner, EntityPoseObserver Observe)> EntityPoseObservers = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (string Owner, Func<object, EntityReference?> Resolve)> EntityInstanceResolvers = new(StringComparer.Ordinal);
    /// <summary>What each kind's own provider answers for a live reference of it: the native object the reference
    /// names, or null. Registered beside the kind's resolver and owned by the same provider, so a step that has to
    /// touch another module's entity asks the kernel instead of that module's table.</summary>
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, object?> Instance)> EntityInstances = new(StringComparer.Ordinal);
    /// <summary>One candidate source per entity kind, owned by the provider that owns that kind's resolver: it is
    /// the provider's own answer to "which entities of my kind exist right now", never a kernel-side scan.</summary>
    internal readonly Dictionary<string, (string Owner, Func<IReadOnlyList<EntityReference>> Candidates)> EntityCandidates = new(StringComparer.Ordinal);
    /// <summary>The entity kinds whose ends their owner reports (<see cref="RuntimeModule.EntityEndReports"/>), each
    /// with the provider that owns the kind's resolver and made the promise.</summary>
    internal readonly Dictionary<string, string> EntityEndReports = new(StringComparer.Ordinal);
    /// <summary>One zone responder per entity kind, owned by the provider that owns that kind's resolver: it is
    /// the provider's own answer to "which zone is this entity of my kind standing in right now", never a
    /// kernel-side volume test.</summary>
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, EntityReference?> Zone)> EntityZones = new(StringComparer.Ordinal);
    /// <summary>One area responder per entity kind, owned by the same provider that owns that kind's zone
    /// responder: it is the provider's own answer to "which area of its own zone is this entity of my kind
    /// standing in right now", as the index the level's zone lists it under. The two tables are two halves of one
    /// placement and are registered together, so a reader that needs both asks the same owner twice.</summary>
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, int?> ZoneArea)> EntityZoneAreas = new(StringComparer.Ordinal);
    /// <summary>One mount matcher per attachment kind, owned by the provider that registered it. The registration
    /// carries whether the kind is matched against event subjects or judged from the mount target alone.</summary>
    internal readonly Dictionary<string, (string Owner, AttachmentMatcherRegistration Matcher)> AttachmentMatchers = new(StringComparer.Ordinal);
    /// <summary>One resource provider per resource kind, owned by the package that knows the kind. Exactly like a
    /// candidate source, the owner is the only one who may answer for the kind, so a second registration is a
    /// conflict rather than a quiet replacement.</summary>
    internal readonly Dictionary<string, (string Owner, RuntimeResourceProvider Provider)> ResourceProviders = new(StringComparer.Ordinal);
    /// <summary>Every registered native-object lookup, in registration order, each with the module that owns it.
    /// Order is the whole contract these resolvers have with each other: the first one that recognizes an object
    /// answers for it, so a package only ever has to know its own native types.</summary>
    internal readonly List<(string Owner, ObjectEntityResolver Resolver)> ObjectEntityResolvers = new();
    /// <summary>The player sessions one provider's `presentation` steps are addressed to, keyed by provider. The
    /// answer is a function of the step's own recipient entities rather than of the process: a value frame is
    /// routed to the machines it is about, and a step whose recipients this process cannot name is refused by
    /// name instead of being widened to everyone.</summary>
    internal readonly Dictionary<string, (string Owner, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?> Sessions)> PresentationSessions = new(StringComparer.Ordinal);
    /// <summary>The owner-session answer one provider's `owner` steps route by, keyed by the capability the step
    /// implements. The question is about the step's own subject rather than about the process, so the answer is a
    /// function of the equipment reference the step names; the provider that owns the binding is the only one that
    /// may answer, exactly as it is for a presentation step's recipients.</summary>
    internal readonly Dictionary<string, (string Owner, Func<EntityReference, string?> Holder)> OwnerSessions = new(StringComparer.Ordinal);

    internal RuntimeRegistry() { }
    private RuntimeRegistry(RuntimeRegistry source)
    {
        ManifestVersion = source.ManifestVersion;
        foreach (var x in source.Providers) providers.Add(x.Key, x.Value);
        foreach (var x in source.Capabilities) capabilities.Add(x.Key, x.Value);
        foreach (var x in source.CapabilityReads) CapabilityReads.Add(x.Key, x.Value);
        foreach (var x in source.Bindings) bindings.Add(x.Key, x.Value);
        foreach (var x in source.Handlers) Handlers.Add(x.Key, x.Value);
        foreach (var x in source.EffectRestores) EffectRestores.Add(x.Key, x.Value);
        foreach (var x in source.Evaluators) Evaluators.Add(x.Key, x.Value);
        foreach (var x in source.Shapes) Shapes.Add(x.Key, x.Value);
        foreach (var x in source.Support) support.Add(x.Key, x.Value);
        foreach (var x in source.Resolvers) Resolvers.Add(x.Key, x.Value);
        foreach (var x in source.EntityObservers) EntityObservers.Add(x.Key, x.Value);
        foreach (var x in source.EntityPoseObservers) EntityPoseObservers.Add(x.Key, x.Value);
        foreach (var x in source.EntityInstanceResolvers) EntityInstanceResolvers.Add(x.Key, x.Value);
        foreach (var x in source.EntityInstances) EntityInstances.Add(x.Key, x.Value);
        foreach (var x in source.EntityCandidates) EntityCandidates.Add(x.Key, x.Value);
        foreach (var x in source.EntityEndReports) EntityEndReports.Add(x.Key, x.Value);
        foreach (var x in source.EntityZones) EntityZones.Add(x.Key, x.Value);
        foreach (var x in source.EntityZoneAreas) EntityZoneAreas.Add(x.Key, x.Value);
        foreach (var x in source.AttachmentMatchers) AttachmentMatchers.Add(x.Key, x.Value);
        foreach (var x in source.ResourceProviders) ResourceProviders.Add(x.Key, x.Value);
        foreach (var resolver in source.ObjectEntityResolvers) ObjectEntityResolvers.Add(resolver);
        foreach (var x in source.PresentationSessions) PresentationSessions.Add(x.Key, x.Value);
        foreach (var x in source.OwnerSessions) OwnerSessions.Add(x.Key, x.Value);
        foreach (var x in source.CapabilityRegistrants) CapabilityRegistrants.Add(x.Key, x.Value);
    }
    internal RuntimeRegistry WithModule(RuntimeModule module, string apiVersion, out string providerId)
    {
        RuntimeJson.Require(module.ApiVersion == apiVersion, "api-version", "Module API version does not match runtime.");
        RuntimeJson.Require(module.ApiFingerprint == RuntimeApi.Fingerprint, "api-fingerprint", "Module public SDK fingerprint does not match runtime; expected " + RuntimeApi.Fingerprint + ". Rebuild the module against this SDK.");
        var declaration = module.Declaration;
        var provider = declaration.Provider;
        providerId = RuntimeJson.Id(provider, "id");
        var next = new RuntimeRegistry(this);
        Add(next.providers, new[] { provider }, "provider-conflict");
        next.ManifestVersion++;
        var capabilities = declaration.Capabilities;
        foreach (var capability in capabilities)
        {
            RuntimeJson.Require(RuntimeJson.Text(capability, "owner") == providerId, "capability-owner", "A module can only declare its own capabilities; reference existing canonical IDs instead.");
            RuntimeJson.Require(next.CapabilityRegistrants.TryAdd(RuntimeJson.Id(capability, "id"), providerId), "capability-conflict", RuntimeJson.Text(capability, "id"));
        }
        Add(next.capabilities, capabilities, "capability-conflict");
        var bindings = declaration.Bindings.Select(row => RuntimeJson.From(row.Row(declaration.Id))).ToArray();
        foreach (var binding in bindings)
            RuntimeJson.Require(RuntimeJson.Text(binding, "providerId") == providerId, "binding-owner", "A module can only register its own bindings.");
        Add(next.bindings, bindings, "binding-conflict");
        // The declared contract is validated before anything is wired to it: a handler's shape resolves against a
        // graph that already passed its own checks, so a malformed capability reports its own error and never a
        // confusing offset failure from resolving a name that graph does not really declare.
        next.Validate();
        var suppliedHandlers = new Dictionary<string, CommandHandler>(module.Handlers, StringComparer.Ordinal);
        var usedHandlers = new HashSet<string>(StringComparer.Ordinal);
        // A shape is supplied per handler name and resolved per binding: the capability it is declared against is the
        // one that binding implements, so the same name can never quietly mean two different port addresses.
        var suppliedShapes = new Dictionary<string, HandlerShape>(declaration.Shapes, StringComparer.Ordinal);
        var usedShapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var id = RuntimeJson.Text(binding, "id");
            RuntimeJson.Require(RuntimeJson.Text(binding, "status") == "implemented", "binding-unimplemented", id);
            if (RuntimeJson.Text(binding, "status") != "implemented" || RuntimeJson.Text(binding, "role") != "execute") continue;
            RuntimeJson.Require(next.Capabilities.TryGetValue(RuntimeJson.Text(binding, "capabilityId"), out var capability), "missing-capability", id);
            // A control step is walked by the kernel itself. A provider handler claiming one would be a second
            // router for the same successor table, so the claim is refused instead of silently left unused.
            if (RuntimeJson.Text(capability, "kind") == "control")
            {
                RuntimeJson.Require(!suppliedHandlers.ContainsKey(RuntimeJson.Text(binding, "handler")), RuntimeAbiCodes.ControlBound, id);
                continue;
            }
            // A kernel-dispatched action is the same kind of row: the kernel owns the state it changes, so a
            // provider body claiming it would be a second implementation of the same write.
            if (RuntimeJson.Text(capability, "kind") == "action"
                && StatusContract.IsDispel(RuntimeJson.Text(capability, "id")))
            {
                RuntimeJson.Require(!suppliedHandlers.ContainsKey(RuntimeJson.Text(binding, "handler")),
                    RuntimeAbiCodes.KernelActionBound, id);
                continue;
            }
            if (RuntimeJson.Text(capability, "kind") != "action") continue;
            var handlerName = RuntimeJson.Text(binding, "handler");
            RuntimeJson.Require(suppliedHandlers.TryGetValue(handlerName, out var handler) && handler != null, "missing-handler", id);
            next.Handlers.Add(id, handler!); usedHandlers.Add(handlerName);
            next.Shapes.Add(id, ShapeFor(suppliedShapes, handlerName, capability, id, usedShapes));
        }
        RuntimeJson.ExactSet(suppliedHandlers.Keys, usedHandlers, "unused-handler");
        // A restore callback travels with the handler it belongs to and is resolved per binding for the same
        // reason a shape is: the plan's `effect` block is checked against the binding that would apply it, so a
        // callback wired to a binding nobody implements would be a promise about a step that never runs.
        var suppliedRestores = module.EffectRestores == null
            ? new Dictionary<string, EffectRestoreHandler>(StringComparer.Ordinal)
            : new Dictionary<string, EffectRestoreHandler>(module.EffectRestores, StringComparer.Ordinal);
        var usedRestores = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var id = RuntimeJson.Text(binding, "id");
            if (RuntimeJson.Text(binding, "status") != "implemented" || RuntimeJson.Text(binding, "role") != "execute") continue;
            if (!next.Capabilities.TryGetValue(RuntimeJson.Text(binding, "capabilityId"), out var capability)
                || RuntimeJson.Text(capability, "kind") != "action") continue;
            if (!suppliedRestores.TryGetValue(RuntimeJson.Text(binding, "handler"), out var restore) || restore == null) continue;
            next.EffectRestores.Add(id, (providerId, restore)); usedRestores.Add(RuntimeJson.Text(binding, "handler"));
        }
        RuntimeJson.ExactSet(suppliedRestores.Keys, usedRestores, "unused-effect-restore");
        var suppliedEvaluators = new Dictionary<string, EvaluatorHandler>(module.Evaluators, StringComparer.Ordinal);
        var usedEvaluators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var id = RuntimeJson.Text(binding, "id");
            if (RuntimeJson.Text(binding, "status") != "implemented") continue;
            var role = RuntimeJson.Text(binding, "role");
            if (role != "evaluate" && role != "observe") continue;
            RuntimeJson.Require(next.Capabilities.TryGetValue(RuntimeJson.Text(binding, "capabilityId"), out var capability), "missing-capability", id);
            // `observe` is also the trigger role, whose event payload arrives as a whole frame and needs no
            // evaluator. Only an observation that is evaluated on demand — a selector, a condition or a value row
            // — registers one, and it does so in the same table `evaluate` uses: there is one evaluator
            // mechanism. A value row is declared `kind: state` with `execution: query`, except where the catalog
            // categorizes it `modifier` and still declares a world read: that row is answered on demand like a
            // state row, so its kind is in this list too. A pure `modifier` is an `evaluate` binding and never
            // reaches this branch.
            if (role == "observe" && RuntimeJson.Text(capability, "kind") is not ("selector" or "condition" or "state" or "modifier")) continue;
            var handlerName = RuntimeJson.Text(binding, "handler");
            RuntimeJson.Require(suppliedEvaluators.TryGetValue(handlerName, out var evaluator) && evaluator != null, "missing-evaluator", id);
            next.Evaluators.Add(id, evaluator!); usedEvaluators.Add(handlerName);
            next.Shapes.Add(id, ShapeFor(suppliedShapes, handlerName, capability, id, usedShapes));
        }
        RuntimeJson.ExactSet(suppliedEvaluators.Keys, usedEvaluators, "unused-evaluator");
        RuntimeJson.ExactSet(suppliedShapes.Keys, usedShapes, "unused-shape");
        foreach (var row in declaration.Bindings)
            next.SetSupport(new BindingSupport(row.Id, row.Verification, row.RequiredPermissions));
        if (module.EntityResolvers != null)
        {
            foreach (var resolver in module.EntityResolvers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(resolver.Key) && resolver.Value != null, "entity-namespace", resolver.Key);
                RuntimeJson.Require(next.Resolvers.TryAdd(resolver.Key, (providerId, resolver.Value!)), "entity-namespace-conflict", resolver.Key);
            }
        }
        if (module.EntityObservers != null)
        {
            RuntimeJson.Require(module.EntityObservers.Count <= RuntimeKernel.MaximumEntityObservers,
                "entity-observer-budget", "Entity observer budget exceeded.");
            foreach (var item in module.EntityObservers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(item.Key) && item.Value != null,
                    "entity-observer", "Invalid entity observer.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(item.Key, out var resolver) && resolver.Owner == providerId,
                    "entity-observer-owner", "An observer requires this provider's resolver.");
                RuntimeJson.Require(next.EntityObservers.TryAdd(item.Key, (providerId, item.Value!)),
                    "entity-observer-conflict", item.Key);
            }
            RuntimeJson.Require(next.EntityObservers.Count <= RuntimeKernel.MaximumEntityObservers,
                "entity-observer-budget", "Entity observer budget exceeded.");
        }
        if (module.EntityPoseObservers != null)
            foreach (var item in module.EntityPoseObservers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(item.Key) && item.Value != null,
                    "entity-observer", "Invalid entity pose observer.");
                RuntimeJson.Require(next.EntityObservers.TryGetValue(item.Key, out var observer) && observer.Owner == providerId,
                    "entity-observer-owner", "A pose observer requires this provider's snapshot observer.");
                RuntimeJson.Require(next.EntityPoseObservers.TryAdd(item.Key, (providerId, item.Value!)),
                    "entity-observer-conflict", item.Key);
            }
        if (module.EntityInstanceResolvers != null)
        {
            foreach (var item in module.EntityInstanceResolvers)
            {
                RuntimeJson.Require(RuntimeJson.IsId(item.Key) && item.Value != null,
                    "entity-instance-resolver", "Invalid entity instance resolver.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(item.Key, out var resolver) && resolver.Owner == providerId,
                    "entity-instance-resolver-owner", "An instance resolver requires this provider's resolver.");
                RuntimeJson.Require(next.EntityInstanceResolvers.TryAdd(item.Key, (providerId, item.Value!)),
                    "entity-instance-resolver-conflict", item.Key);
            }
        }
        if (module.EntityInstances != null)
        {
            foreach (var item in module.EntityInstances)
            {
                RuntimeJson.Require(RuntimeJson.IsId(item.Key) && item.Value != null,
                    "entity-instance", "Invalid entity instance lookup.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(item.Key, out var resolver) && resolver.Owner == providerId,
                    "entity-instance-owner", "An instance lookup requires this provider's resolver.");
                RuntimeJson.Require(next.EntityInstances.TryAdd(item.Key, (providerId, item.Value!)),
                    "entity-instance-conflict", item.Key);
            }
        }
        if (module.EntityCandidates != null)
        {
            foreach (var source in module.EntityCandidates)
            {
                // Discovery follows ownership: only the provider that already resolves a kind may say which
                // entities of it exist, and one kind has exactly one such provider.
                var kind = source.Key ?? "";
                RuntimeJson.Require(RuntimeJson.IsId(kind) && source.Value != null,
                    "entity-candidate-source", "Invalid entity candidate source.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(kind, out var resolver) && resolver.Owner == providerId,
                    "entity-candidate-source-owner", "A candidate source requires this provider's resolver.");
                RuntimeJson.Require(next.EntityCandidates.TryAdd(kind, (providerId, source.Value!)),
                    "entity-candidate-source-conflict", kind);
            }
        }
        if (module.EntityEndReports != null)
        {
            foreach (var kind in module.EntityEndReports)
            {
                // Only the provider that resolves a kind can know every way a life of it ends, so only that provider
                // may promise to report them; a kind is promised once.
                RuntimeJson.Require(RuntimeJson.IsId(kind ?? ""), "entity-end-report", "Invalid entity end report kind.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(kind!, out var resolver) && resolver.Owner == providerId,
                    "entity-end-report-owner", "An end report requires this provider's resolver.");
                RuntimeJson.Require(next.EntityEndReports.TryAdd(kind!, providerId), "entity-end-report-conflict", kind!);
            }
        }
        if (module.EntityZones != null)
        {
            foreach (var responder in module.EntityZones)
            {
                // Who a kind's entities are is the same ownership a candidate source follows: only the provider
                // that already resolves the kind may say where one of its entities stands, and one kind has
                // exactly one such provider.
                var kind = responder.Key ?? "";
                RuntimeJson.Require(RuntimeJson.IsId(kind) && responder.Value != null,
                    "entity-zone-responder", "Invalid entity zone responder.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(kind, out var resolver) && resolver.Owner == providerId,
                    "entity-zone-responder-owner", "A zone responder requires this provider's resolver.");
                RuntimeJson.Require(next.EntityZones.TryAdd(kind, (providerId, responder.Value!)),
                    "entity-zone-responder-conflict", kind);
            }
        }
        if (module.EntityZoneAreas != null)
        {
            foreach (var responder in module.EntityZoneAreas)
            {
                // The area is the other half of the same placement, so it follows the zone responder's own
                // ownership exactly: only the provider that already resolves the kind and answers where it stands
                // may say which of that zone's areas it is in, and one kind has exactly one such provider.
                var kind = responder.Key ?? "";
                RuntimeJson.Require(RuntimeJson.IsId(kind) && responder.Value != null,
                    "entity-zone-area-responder", "Invalid entity zone area responder.");
                RuntimeJson.Require(next.Resolvers.TryGetValue(kind, out var resolver) && resolver.Owner == providerId,
                    "entity-zone-area-responder-owner", "A zone area responder requires this provider's resolver.");
                RuntimeJson.Require(next.EntityZones.ContainsKey(kind),
                    "entity-zone-area-responder-owner", "A zone area responder requires this provider's zone responder.");
                RuntimeJson.Require(next.EntityZoneAreas.TryAdd(kind, (providerId, responder.Value!)),
                    "entity-zone-area-responder-conflict", kind);
            }
        }
        if (module.AttachmentMatchers != null)
        {
            foreach (var matcher in module.AttachmentMatchers)
            {
                // A mount kind belongs to one provider, and the registration itself says whether the kind is
                // matched against the event's subjects or judged from the mount target alone. Every kind in the
                // plan vocabulary can be claimed, so a plan is never accepted for a kind nothing can match.
                var kind = matcher.Key ?? "";
                RuntimeJson.Require(Wire.AttachmentKinds.Contains(kind) && matcher.Value != null,
                    RuntimeAbiCodes.AttachmentKind, kind);
                RuntimeJson.Require(next.AttachmentMatchers.TryAdd(kind, (providerId, matcher.Value!)),
                    RuntimeAbiCodes.AttachmentKind, kind);
            }
        }
        if (module.ResourceProviders != null)
        {
            foreach (var entry in module.ResourceProviders)
            {
                // A resource kind belongs to one provider for the same reason a mount kind does: the package that
                // knows the kind is the only one that can say what of it exists, and a second owner would leave
                // every reader guessing which of the two answered.
                var kind = entry.Key ?? "";
                RuntimeJson.Require(Wire.ResourceKinds.Contains(kind) && entry.Value != null,
                    RuntimeAbiCodes.ResourceKind, kind);
                RuntimeJson.Require(next.ResourceProviders.TryAdd(kind, (providerId, entry.Value!)),
                    RuntimeAbiCodes.ResourceKind, kind);
            }
        }
        if (module.ObjectEntityResolvers != null)
        {
            foreach (var resolver in module.ObjectEntityResolvers)
            {
                RuntimeJson.Require(resolver?.Resolve != null, "entity-resolver", "Invalid object entity resolver.");
                next.ObjectEntityResolvers.Add((providerId, resolver!));
            }
        }
        if (module.PresentationSessions != null)
        {
            foreach (var entry in module.PresentationSessions)
            {
                // One provider answers for its own presentation steps. Every module answers under its own declared
                // provider id, so the registration needs no key a caller could get wrong.
                var sessions = entry.Value;
                RuntimeJson.Require(sessions != null, "player-session-provider", providerId);
                RuntimeJson.Require(next.PresentationSessions.TryAdd(providerId, (providerId, sessions!)),
                    "player-session-provider", providerId);
            }
        }
        if (module.OwnerSessions != null)
        {
            foreach (var entry in module.OwnerSessions)
            {
                // The owner tier's answer is per capability rather than per provider: one module may declare an
                // owner step about a weapon and another about a terminal, and each row's holder is its own
                // question. A capability may only be answered once, by the provider that declared it.
                var holder = entry.Value;
                RuntimeJson.Require(holder != null, "owner-session-provider", entry.Key);
                RuntimeJson.Require(next.OwnerSessions.TryAdd(entry.Key, (providerId, holder!)),
                    "owner-session-provider", entry.Key);
            }
        }
        return next;
    }
    /// <summary>One handler's shape, resolved against the capability its binding implements. A handler that takes
    /// values out of a frame cannot be registered without one, which is why the check is the same exact-set rule the
    /// handler and evaluator tables already use.</summary>
    private static HandlerShape ShapeFor(Dictionary<string, HandlerShape> supplied, string handlerName, JsonElement capability,
        string bindingId, HashSet<string> used)
    {
        RuntimeJson.Require(supplied.TryGetValue(handlerName, out var shape) && shape != null, "missing-shape", bindingId);
        RuntimeJson.Require(capability.TryGetProperty("graph", out var graph), "shape-port", bindingId + " has no graph to resolve against.");
        try { shape!.Resolve(graph); }
        catch (RuntimeContractException error)
        {
            var capabilityId = RuntimeJson.Id(capability, "id");
            throw new RuntimeContractException(error.Code, bindingId + " -> " + capabilityId + ": " + error.Message);
        }
        // Only a presentation command names its play; a handler of any other execution that says it keeps plays would
        // never be handed one, so the claim is refused here rather than ignored.
        if (shape.KeyedByPlay && !(graph.TryGetProperty("execution", out var execution)
                && execution.ValueKind == JsonValueKind.String && execution.ValueEquals("presentation")))
            throw new RuntimeContractException("shape-port", bindingId + " keeps plays, but only a presentation handler is named one.");
        used.Add(handlerName);
        return shape;
    }
    private static void Add(Dictionary<string, JsonElement> target, IEnumerable<JsonElement> rows, string code)
    {
        foreach (var row in rows)
        { var id = RuntimeJson.Id(row, "id"); RuntimeJson.Require(target.TryAdd(id, row.Clone()), code, id); }
    }
    private void Validate()
    {
        foreach (var p in Providers.Values)
        {
            RuntimeJson.Shape(p, "id kind version dependencies");
            var id = RuntimeJson.Id(p, "id"); var kind = RuntimeJson.Text(p, "kind");
            RuntimeJson.Require(kind is "native" or "adapter" or "extension", "provider-kind", id);
            RuntimeJson.Require(!id.StartsWith("forge.", StringComparison.Ordinal) || kind == "native", "reserved-provider", id);
            RuntimeJson.Version(p, "version"); ValidatePackages(RuntimeJson.Strings(p.GetProperty("dependencies")));
        }
        foreach (var c in Capabilities.Values)
        {
            // The row's own id travels with the refusal: `Shape` reports the offending member by name, and a member
            // name alone does not say which row carried it.
            try { RuntimeJson.Shape(c, "id owner kind label version parameters", "labelEn graph"); }
            catch (RuntimeContractException error)
            {
                var at = c.TryGetProperty("id", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString() : "?";
                throw new RuntimeContractException(error.Code, at + ": " + error.Message);
            }
            var id = RuntimeJson.Id(c, "id"); var owner = RuntimeJson.Text(c, "owner");
            RuntimeJson.Require(Providers.TryGetValue(owner, out var provider), "capability-owner", id);
            RuntimeJson.Require(id.StartsWith("forge.", StringComparison.Ordinal)
                ? RuntimeJson.Text(provider, "kind") == "native" && owner.StartsWith("forge.", StringComparison.Ordinal)
                : id.StartsWith(owner + ".", StringComparison.Ordinal), "capability-namespace", id);
            RuntimeJson.Require(new[] { "trigger", "selector", "condition", "modifier", "control", "action", "variable", "state", "event", "component" }.Contains(RuntimeJson.Text(c, "kind")), "capability-kind", id);
            RuntimeJson.Text(c, "label"); RuntimeJson.Version(c, "version");
            // The English side of the row name the catalog builds. It is optional — a module may register a row
            // under its own language alone — but where it is present it is the same text a label is, and it is kept
            // in the row exactly as it arrived. `unavailable` is deliberately not in this list: the catalog withholds
            // a row with it, so a module registration carrying one is a shape this runtime does not know.
            if (c.TryGetProperty("labelEn", out var labelEn)) RuntimeJson.Text(labelEn);
            RuntimeJson.Require(c.GetProperty("parameters").ValueKind == JsonValueKind.Object, "parameter-metadata", id);
            if (c.TryGetProperty("graph", out var graph))
            {
                RuntimeGraphContracts.ValidateCapability(RuntimeJson.Text(c, "kind"), graph, id);
                // A plan step's authority is judged from these two facts, so both are read once, at registration,
                // from the definition that just passed its own checks. Rebuilt, not appended: a later module's
                // registration runs this over every capability the registry holds, its own and the ones it inherited.
                CapabilityReads[id] = RuntimeGraphContracts.ReadDeclarations(graph);
            }
        }
        foreach (var b in Bindings.Values)
        {
            RuntimeJson.Shape(b, "id capabilityId providerId handler role status dependencies requires", "equipmentAttachment waitable");
            var id = RuntimeJson.Id(b, "id"); var providerId = RuntimeJson.Text(b, "providerId");
            RuntimeJson.Require(Providers.TryGetValue(providerId, out var provider) && id.StartsWith(providerId + ".", StringComparison.Ordinal), "binding-provider", id);
            RuntimeJson.Require(Capabilities.TryGetValue(RuntimeJson.Text(b, "capabilityId"), out var capability), "binding-capability", id);
            EquipmentAttachmentPorts.Resolve(b, capability);
            // A wait resumes on the first event its binding publishes, so only an observed trigger binding can be one.
            if (b.TryGetProperty("waitable", out var waitable))
                RuntimeJson.Require(waitable.ValueKind == JsonValueKind.True && RuntimeJson.Text(b, "role") == "observe"
                    && RuntimeJson.Text(capability, "kind") == "trigger", "binding-role", id);
            RuntimeJson.Require(RuntimeJson.Text(b, "status") is "planned" or "implemented", "binding-status", id);
            var role = RuntimeJson.Text(b, "role");
            RuntimeJson.Require(role is "execute" or "observe" or "evaluate", "binding-role", id);
            // An evaluated binding names a selector, a condition or a value row (`kind: state`). A `modifier` is
            // accepted only where the pure layer runs: a `modifier` that reads the world is a query value row,
            // answered as an observation rather than in the pure layer, so it is refused here.
            if (role == "evaluate")
            {
                var evaluatedKind = RuntimeJson.Text(capability, "kind");
                RuntimeJson.Require(evaluatedKind is "selector" or "condition" or "state"
                    || (evaluatedKind == "modifier" && capability.TryGetProperty("graph", out var evaluatedGraph)
                        && RuntimeJson.Text(evaluatedGraph, "execution") == "pure"), "binding-role", id);
            }
            RuntimeJson.Text(b, "handler"); ValidatePackages(RuntimeJson.Strings(b.GetProperty("dependencies")));
            foreach (var required in RuntimeJson.Strings(b.GetProperty("requires"))) RuntimeJson.Require(Bindings.ContainsKey(required), "required-binding", required);
            if (RuntimeJson.Text(capability, "kind") == "trigger" && RuntimeJson.Text(capability, "id").StartsWith("forge.", StringComparison.Ordinal) && RuntimeJson.Text(provider, "kind") != "native")
                RuntimeJson.Require(RuntimeJson.Text(b, "role") == "observe", "trigger-owner", id);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            RuntimeJson.Require(visiting.Add(id), "binding-cycle", id);
            foreach (var required in RuntimeJson.Strings(Bindings[id].GetProperty("requires"))) Visit(required);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in Bindings.Keys) Visit(id);
        foreach (var id in Bindings.Keys) Packages(Closure(new[] { id }));
    }
    internal static void ValidatePackages(IEnumerable<string> packages)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var match = Regex.Match(package, @"^([A-Za-z0-9_]+-[A-Za-z0-9_]+)-((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$");
            RuntimeJson.Require(match.Success, "package-version", package);
            RuntimeJson.Require(!versions.TryGetValue(match.Groups[1].Value, out var version) || version == match.Groups[2].Value, "package-conflict", package);
            versions[match.Groups[1].Value] = match.Groups[2].Value;
        }
    }
    internal HashSet<string> Closure(IEnumerable<string> selected)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void AddBinding(string id)
        {
            RuntimeJson.Require(Bindings.TryGetValue(id, out var binding) && RuntimeJson.Text(binding, "status") == "implemented", "binding-unavailable", id);
            if (!result.Add(id)) return;
            foreach (var dependency in RuntimeJson.Strings(binding.GetProperty("requires"))) AddBinding(dependency);
        }
        foreach (var id in selected) AddBinding(id);
        return result;
    }
    internal string[] Packages(IEnumerable<string> bindings)
    {
        var packages = bindings.SelectMany(id => RuntimeJson.Strings(Bindings[id].GetProperty("dependencies"))
            .Concat(RuntimeJson.Strings(Providers[RuntimeJson.Text(Bindings[id], "providerId")].GetProperty("dependencies")))).Distinct(StringComparer.Ordinal).ToArray();
        ValidatePackages(packages); return packages;
    }
    /// <summary>Whether a registered binding declared itself waitable: the one table a `wait_event` target is judged by.</summary>
    internal bool IsWaitable(string bindingId)
        => bindings.TryGetValue(bindingId, out var binding) && binding.TryGetProperty("waitable", out var waitable)
            && waitable.ValueKind == JsonValueKind.True;
    /// <summary>What one registered capability reads besides its ports; empty on one that declares no read.</summary>
    internal string[] Reads(string capabilityId)
        => CapabilityReads.TryGetValue(capabilityId, out var reads) ? reads : Array.Empty<string>();
    internal object Snapshot() => ManifestSnapshot(providers, capabilities, bindings);
    // A static builder cannot read the registry's other instance tables: all dependencies are explicit arguments.
    private static object ManifestSnapshot(IReadOnlyDictionary<string, JsonElement> providers,
        IReadOnlyDictionary<string, JsonElement> capabilities, IReadOnlyDictionary<string, JsonElement> bindings) => new
        {
            providers = providers.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(),
            capabilities = capabilities.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray(),
            bindings = bindings.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray()
        };
}
