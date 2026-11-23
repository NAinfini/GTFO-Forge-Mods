using System;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>A module can cast the handles its own package owns, publish its own bindings and cancel its own
/// scopes. This is ownership isolation, not an untrusted-code sandbox.</summary>
public sealed partial class RuntimeModuleHandle
{
    /// <summary>
    /// Casts one handle of a kind this provider owns. The kind's own name in the shared handle-kind table and the
    /// lifetime it lives for are the two facts a consuming port is checked against, so both come from the shared
    /// vocabulary rather than from a free-form string. The handle goes through the kernel's one allocation path,
    /// which means the same pool, the same `handle-budget` ceiling and the same `stale-handle` rule every other
    /// handle obeys; nothing here is a second handle mechanism.
    /// </summary>
    public RuntimeHandle CastEffectHandle(string lifetime) => kernel.CastHandle(this, "effect", lifetime);
    public RuntimeHandle CastChargeHandle(string lifetime) => kernel.CastHandle(this, "charge", lifetime);
    public RuntimeHandle CastPoolHandle(string lifetime) => kernel.CastHandle(this, "pool", lifetime);
    public RuntimeHandle CastRequestHandle(string lifetime) => kernel.CastHandle(this, "request", lifetime);
    public RuntimeHandle CastProjectileHandle(string lifetime) => kernel.CastHandle(this, "projectile", lifetime);

    /// <summary>Attaches the native object a provider's own handle stands for, so the package that owns both can
    /// read its handle back as the native thing it tracks. One handle holds one object, and the table is dropped
    /// with the handle, with the world and with this module's registration: a handle that outlived its object
    /// would be a name for nothing.
    ///
    /// <paramref name="isLive"/> is the provider's own answer to whether that object is still there, asked on the
    /// advance sweep. It is what a native kind no entity resolver governs needs — a chained puzzle, a wave event, a
    /// spawn solution, a marker — because the kernel cannot answer for a kind nobody registered. A provider that
    /// hands none still gets the kernel's own answer for an object the resolvers do know: a kind they recognize is
    /// live exactly while its entity life is current, and a kind none of them recognizes is held by the kernel
    /// until the handle's declared lifetime ends rather than released for being unrecognized.</summary>
    public void RegisterNative(RuntimeHandle handle, object native, Func<object, bool>? isLive = null)
        => kernel.RegisterNativeHandle(this, handle, native, isLive);

    /// <summary>The native object a live handle stands for, or false when the handle holds none, is not this
    /// module's, or is no longer live.</summary>
    public bool TryNative(RuntimeHandle handle, out object? native)
        => kernel.TryNativeHandle(this, handle, out native);

    /// <summary>Whether one of this module's own handles still names a live slot of this world: a handle it never
    /// cast and one whose life has ended are both simply not live for it. Nothing is thrown and nothing allocated.</summary>
    public bool IsLive(RuntimeHandle handle) => kernel.IsHandleLive(this, handle);

    /// <summary>
    /// Registers what cancelling this handle has to do beyond releasing it. A timer handle's cancellation is the
    /// kernel's own — it ends the schedule the handle names — so this hook is for the kinds a provider owns: the
    /// package that started an effect or cast a charge is the only one that can stop it.
    /// The callback is detached before it runs and the handle is spent even if it throws. It never replaces the
    /// handle check: a handle that is not this module's, or not live, is refused before the callback is reached.
    /// </summary>
    public void RegisterCancel(RuntimeHandle handle, Action cancel)
        => kernel.RegisterHandleCancel(this, handle, cancel);

    /// <summary>
    /// Binds this provider's ordinary handle to its scope and the entity lives captured by the current trigger,
    /// plus an optional explicit life. Register before starting the native change. A scope, entity, world,
    /// registration (including a captured entity resolver's registration) or authority ending invokes
    /// <paramref name="cancel"/> once with its reason, as does an
    /// explicit handle cancellation. The callback is detached first and the handle is spent even if it throws.
    /// A native object that ends normally, or <see cref="ReleaseEndedHandle"/>, instead releases silently.
    /// This ownership uses the existing handle slot, not a schedule, and cannot be combined with
    /// <see cref="RegisterCancel"/> or attached to a kernel-owned schedule/effect. Ending callbacks cannot
    /// register new owned lifetimes. Active owned lifetimes must end before host migration is captured/adopted.
    /// </summary>
    public void RegisterOwnedLifetime(RuntimeHandle handle, string scope, Action<string> cancel,
        EntityReference? lifetime = null) => kernel.RegisterOwnedHandleLifetime(this, handle, scope, cancel, lifetime);

    /// <summary>
    /// Gives back one handle whose change this module has already ended, from the place that ended it — including
    /// inside a lifecycle notification, where every other kernel entry is refused because observers are read-only.
    /// The cancel hook is not run: the caller has just undone what the hook would undo. A handle that names a
    /// schedule or a kernel-managed effect belongs to the kernel and is refused by name.
    /// </summary>
    public bool ReleaseEndedHandle(RuntimeHandle handle, HandlePort port, string detail)
        => kernel.ReleaseEndedHandle(this, handle, port, detail);

    /// <summary>Cancels one live handle this module owns, through the same kernel path as a plan's own `cancel`:
    /// the handle is checked against the kind and lifetime <paramref name="port"/> declares.</summary>
    public int CancelHandle(RuntimeHandle handle, HandlePort port, string detail) => kernel.CancelHandle(this, handle, port, detail);
}

/// <summary>
/// The kernel's handle pool and the budgeted query session a `query` step reads the world through. Both exist so
/// that a plan can only ever touch what an earlier step gave it: a handle is a pool address plus a generation, so
/// a recycled slot can never be mistaken for the handle that used to live there, and a world read is a metered
/// call that reports its refusal instead of answering with an empty result.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>One live handle. Its kind and lifetime are what a consuming port must agree with; the schedule
    /// identity is what `cancel` resolves, so a handle never carries a schedule string of its own; the effect
    /// identity is what a kernel-managed effect resolves, so cancelling the handle ends the effect instead of
    /// releasing a slot the effect still names. The native object and the cancel callback belong to the provider
    /// that cast the handle, and neither outlives the slot.</summary>
    internal sealed class HandleSlot
    {
        internal string Kind = "";
        internal string Lifetime = "";
        internal string ProviderId = "";
        internal int Generation;
        internal bool Active;
        internal string? ScheduleId;
        internal int EffectId = -1;
        /// <summary>The declared status an effect handle was cast for, or empty for an ordinary effect. The handle
        /// names the status instance as well as the module's own write, which is what lets a dispel address one by
        /// (receiver, status kind) and a status ending travel with the handle it names.</summary>
        internal string StatusKind = "";
        internal object? Native;
        /// <summary>The owning provider's own answer to whether <see cref="Native"/> is still there, or null when
        /// it handed none. The kernel asks it before the resolver fallback, because the provider is the only side
        /// that knows the native kind it tracks.</summary>
        internal Func<object, bool>? NativeIsLive;
        /// <summary>The entity life <see cref="Native"/> belongs to, kept once it resolved to a kind whose owner reports
        /// its ends: the sweep then asks the end reports about this life instead of resolving the object every tick.</summary>
        internal EntityCheck? NativeLife;
        internal Action? Cancel;
        internal OwnedHandleLifetime? OwnedLifetime;
    }
    /// <summary>Only lifecycle ownership. The native object remains in the same handle slot and owns its own
    /// generation, updates and normal completion; no tick, pending work or second handle identity is kept here.</summary>
    internal sealed class OwnedHandleLifetime
    {
        internal readonly long Generation;
        internal readonly string Scope;
        internal readonly EntityCheck[] Entities;
        internal readonly OwnedEntityResolver[] Resolvers;
        internal Action<string>? Cancel;
        internal long ValidatedGeneration;
        internal OwnedHandleLifetime(long generation, string scope, EntityCheck[] entities,
            OwnedEntityResolver[] resolvers, Action<string> cancel, long validatedGeneration)
        {
            Generation = generation; Scope = scope; Entities = entities; Resolvers = resolvers;
            Cancel = cancel; ValidatedGeneration = validatedGeneration;
        }
    }
    // A bounded stamp for each captured entity, not another resolver registry. A provider can unregister and
    // re-register the exact same delegate object; its existing module generation distinguishes those lives.
    internal readonly record struct OwnedEntityResolver(string Kind, string Provider, long Generation);
    /// <summary>The first refusal of the query step currently being evaluated, or null. Cleared before every
    /// evaluation; read once after it, which is how a partial read becomes an explicit `query-budget` rejection
    /// rather than a frame that silently holds fewer candidates than the world has.</summary>
    private string? queryRefusal;

    /// <summary>One wire-form handle as its value, with the refusals the wire boundary always gave a malformed one.</summary>
    internal static RuntimeHandle ReadHandle(JsonElement value)
    {
        RuntimeJson.Handle(value);
        return new RuntimeHandle((int)RuntimeJson.Integer(value.GetProperty("worldEpoch"), 0, int.MaxValue),
            (int)RuntimeJson.Integer(value.GetProperty("lifeEpoch"), 0, int.MaxValue),
            (int)RuntimeJson.Integer(value.GetProperty("local"), 0, int.MaxValue),
            (int)RuntimeJson.Integer(value.GetProperty("provider"), 0, int.MaxValue));
    }

    /// <summary>
    /// One provider's own handle, cast through the kernel's single allocation path. The lifetime is checked against
    /// the shared lifetime table here rather than at every consumer, because a lifetime no port could declare would
    /// make the handle unusable the moment it was cast.
    /// </summary>
    internal RuntimeHandle CastHandle(RuntimeModuleHandle owner, string kind, string lifetime)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var kindName = kind ?? "";
        var lifetimeName = lifetime ?? "";
        RuntimeJson.Require(Wire.HandleKinds.Contains(kindName), RuntimeAbiCodes.HandleKind, kindName);
        RuntimeJson.Require(Wire.HandleLifetimes.Contains(lifetimeName), RuntimeAbiCodes.HandleLifetime, lifetimeName);
        return kernelEffects.AllocateHandle(owner.ProviderId, kindName, lifetimeName, out _);
    }

    internal void RegisterNativeHandle(RuntimeModuleHandle owner, RuntimeHandle value, object native,
        Func<object, bool>? isLive = null)
    {
        Thread(); ArgumentNullException.ThrowIfNull(native);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = kernelEffects.HandleOf(owner, value);
        RuntimeJson.Require(slot.Native == null, "handle-native-conflict", owner.ProviderId);
        slot.Native = native;
        slot.NativeIsLive = isLive;
        if (isLive == null) slot.NativeLife = ReportedNativeLife(native);
    }

    internal bool TryNativeHandle(RuntimeModuleHandle owner, RuntimeHandle value, out object? native)
    {
        Thread(); native = null;
        if (!owner.IsRegistered || kernelEffects.HandleRefusal(owner, value, out var slot) != null) return false;
        native = slot!.Native;
        return native != null;
    }

    internal void RegisterHandleCancel(RuntimeModuleHandle owner, RuntimeHandle value, Action cancel)
    {
        Thread(); ArgumentNullException.ThrowIfNull(cancel);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = kernelEffects.HandleOf(owner, value);
        RuntimeJson.Require(slot.Cancel == null && slot.OwnedLifetime == null, "handle-cancel-conflict", owner.ProviderId);
        slot.Cancel = cancel;
    }

    internal void RegisterOwnedHandleLifetime(RuntimeModuleHandle owner, RuntimeHandle value, string scope,
        Action<string> cancel, EntityReference? lifetime)
    {
        Thread(); ArgumentNullException.ThrowIfNull(cancel);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(world.worldStarted, "stale-world", scope);
        RuntimeJson.Require(world.worldHost != false, "not-host", scope);
        RuntimeJson.Require(kernelEffects.endingOwnedLifetimes == 0, "handle-lifetime-ending", scope);
        RuntimeJson.Text(scope);
        var slot = kernelEffects.HandleOf(owner, value);
        RuntimeJson.Require(slot.ScheduleId == null && slot.EffectId < 0, "handle-kernel-owned", scope);
        RuntimeJson.Require(slot.Cancel == null, "handle-cancel-conflict", scope);
        RuntimeJson.Require(slot.OwnedLifetime == null, "handle-lifetime-conflict", scope);
        RuntimeJson.Require(!world.cancelled.Contains((owner.ProviderId, scope)), "scope-cancelled", scope);
        // Match module continuations: ownership belongs to the registration, while every entity output of
        // the triggering event is captured, not merely the source/recipient selected by this action.
        var entities = currentCommand != null && executor.currentPending != null
            ? executor.LivenessOf(executor.currentPending.Event, registry.BindingViews[executor.currentPending.Event.BindingId])
            : Array.Empty<EntityCheck>();
        if (lifetime != null)
        {
            RuntimeEntityReferences.Validate(lifetime);
            RuntimeJson.Require(lifetime.WorldEpoch == WorldEpoch, "stale-entity", lifetime.Id);
            RuntimeJson.Require(entityLives.TryEntityCheck(lifetime, out var check), "entity-resolver-unavailable", lifetime.Id);
            var capture = new EntityCheck[entities.Length + 1];
            entities.CopyTo(capture, 0); capture[^1] = check; entities = capture;
        }
        RuntimeJson.Require(entities.Length <= MaximumScheduleEntityReferences, "handle-lifetime-target-budget", scope);
        var resolvers = kernelEffects.CaptureOwnedEntityResolvers(entities);
        var code = entityLives.EntityLivenessCode(entities, out _, admission: true);
        RuntimeJson.Require(code == null, code ?? "stale-entity", scope);
        // Resolvers are provider delegates. Recheck the address and ownership after calling them so a bad
        // reentrant resolver cannot attach this obligation to a spent slot or bypass a newly cancelled scope.
        Thread();
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        RuntimeJson.Require(world.worldHost != false, "not-host", scope);
        RuntimeJson.Require(ReferenceEquals(kernelEffects.HandleOf(owner, value), slot), RuntimeAbiCodes.StaleHandle, scope);
        RuntimeJson.Require(slot.Cancel == null, "handle-cancel-conflict", scope);
        RuntimeJson.Require(slot.OwnedLifetime == null, "handle-lifetime-conflict", scope);
        RuntimeJson.Require(!world.cancelled.Contains((owner.ProviderId, scope)), "scope-cancelled", scope);
        RuntimeJson.Require(kernelEffects.OwnedEntityResolversCurrent(entities, resolvers), "source-lifecycle", scope);
        // No partial attachment on rejection. This is the only new state, bounded by the existing handle pool.
        slot.OwnedLifetime = new OwnedHandleLifetime(owner.Generation, scope, entities, resolvers, cancel, world.lifecycleGeneration);
    }

    /// <summary>
    /// Whether one module's own handle value still names a live slot of this world. This is the same allocation,
    /// generation and ownership rule <see cref="HandleOf"/> applies, asked as a question instead of as a refusal,
    /// so a provider that keeps a handle across frames can ask before it reads the native object behind it — and
    /// can tell a handle it never cast from one whose life has ended, because both are simply not live for it.
    /// </summary>
    public bool IsHandleLive(RuntimeModuleHandle owner, RuntimeHandle value)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        return kernelEffects.HandleRefusal(owner, value, out _) == null;
    }

    /// <summary>
    /// Whether one effect handle still names a running effect instance, asked of the kernel's own slot table rather
    /// than of any world state. The value must be a well-formed handle whose world is this one, whose slot is still
    /// occupied and whose generation is the slot's own; anything else — a spent handle, one from an ended world, one
    /// that was never cast — answers false, because "this effect is not active any more" is the answer the row asks
    /// for and not a refusal. A value that still names a live slot of another kind or lifetime is refused by the
    /// code a consuming port refuses one with, so a charge handle wired into an effect port is never reported as an
    /// effect that has ended.
    /// </summary>
    internal bool EffectHandleActive(RuntimeHandle value, string handleKind, string lifetime)
    {
        ReadThread();
        if (value.WorldEpoch != WorldEpoch || (uint)value.Local >= (uint)kernelEffects.handleSlots.Count) return false;
        if (kernelEffects.handleSlots[value.Local] is not { Active: true } slot || slot.Generation != value.LifeEpoch) return false;
        RuntimeJson.Require(slot.Kind == handleKind, RuntimeAbiCodes.HandleKind, "The handle is not an effect handle.");
        RuntimeJson.Require(slot.Lifetime == lifetime, RuntimeAbiCodes.HandleLifetime,
            "The handle does not live for the lifetime this port declares.");
        return true;
    }

    /// <summary>
    /// One provider handle's own cancellation, as the `cancel` control performs it: the handle is checked against
    /// the kind and lifetime its port declares, the hook the provider registered runs once, and the handle is
    /// spent. A remote proxy instead raises `owner-cancel-pending` until the owner control receipt arrives;
    /// an unknown owner result raises `owner-cancel-failed`, and neither releases the proxy. This is the entry point a package that stops its own effect outside a plan ends through, so a
    /// provider never grows a second, weaker cancellation path.
    /// </summary>
    public int CancelHandle(RuntimeModuleHandle owner, RuntimeHandle value, HandlePort port, string detail)
    {
        Thread(); ArgumentNullException.ThrowIfNull(owner);
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = kernelEffects.CheckHandle(value, port.HandleKind, port.Lifetime, detail, out _);
        // The canonical cancellation action consumes any provider's effect handle. Ordinary module calls
        // retain ownership isolation; knowing a foreign handle does not grant a module a cancellation API.
        RuntimeJson.Require(slot.ProviderId == owner.ProviderId
            || (slot.Kind == "effect" && effectCancellationProvider == owner.ProviderId), "handle-owner", owner.ProviderId);
        return CancelHandle(value, port.HandleKind, port.Lifetime, detail);
    }

    /// <summary>
    /// Gives back one handle whose change the provider has already ended, from the place that ended it.
    ///
    /// This is the one handle entry a lifecycle observer may call. A change that ends by its own duration ends
    /// inside the kernel's lifecycle notification, and the guard that keeps observers read-only refuses every
    /// other kernel entry there — while returning a slot is the release the kernel performs on that same pass for
    /// a handle whose native object's life has ended (<see cref="CleanHandleLifetimes"/>). The cancel hook is
    /// deliberately not run: the caller has just undone what the hook would undo, and running provider code from
    /// inside a notification is what the guard refuses. A handle the kernel owns — one that names a schedule or a
    /// kernel-managed effect — is refused, because ending those is the kernel's own business.
    /// </summary>
    internal bool ReleaseEndedHandle(RuntimeModuleHandle owner, RuntimeHandle value, HandlePort port, string detail)
    {
        // Ordinary lifecycle cleanup may return an ended slot even when runtime work is closed. Provider
        // handover borrows that read-only scope but may only prepare/adopt private tables, never end handles.
        ReadThread(); ArgumentNullException.ThrowIfNull(owner);
        if (readingProviderHandover) NoLifecycleMutation();
        RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
        var slot = kernelEffects.CheckHandle(value, port.HandleKind, port.Lifetime, detail, out var address);
        RuntimeJson.Require(slot.ProviderId == owner.ProviderId, "handle-owner", owner.ProviderId);
        RuntimeJson.Require(slot.EffectId < 0 && slot.ScheduleId == null, "handle-kernel-owned", detail);
        slot.Cancel = null;
        kernelEffects.ReleaseHandle(address);
        return true;
    }

    /// <summary>
    /// The handles whose life has ended. A handle the kernel cast for its own schedule dies with the world and is
    /// ended by its schedule's own cancellation; a handle a provider cast dies when the life its native object
    /// belonged to does. That life is answered by the provider that attached the object — the only side that knows
    /// the kind it tracks — and only in the absence of that answer by the resolver fallback: an object a registered
    /// resolver knows is live while its entity life is current, and an object no resolver recognizes is held by
    /// the kernel until the handle's declared lifetime ends, because "unrecognized" is not "dead". Both are the one
    /// lifetime rule the handle pool has, so nothing here invents a second expiry: a handle that no longer answers
    /// for its object is released, and every later read of its value is `stale-handle`.
    /// </summary>
    private void CleanHandleLifetimes()
    {
        for (var slot = 0; slot < kernelEffects.handleSlots.Count; slot++)
        {
            if (kernelEffects.handleSlots[slot] is not { Active: true } handle) continue;
            // Source ownership is checked before natural native completion: an ended source still owes its
            // cancellation even if the native liveness predicate would also answer false on this pass.
            if (handle.OwnedLifetime is { } owned)
            {
                if (owned.Cancel == null) continue; // A reentrant advance cannot finish an active callback.
                if (kernelEffects.OwnedHandleEndCode(handle, owned) is { } code)
                { kernelEffects.EndOwnedHandle(slot, handle, code); continue; }
                if (slot >= kernelEffects.handleSlots.Count || !ReferenceEquals(kernelEffects.handleSlots[slot], handle)) continue;
            }
            if (handle.Native is not { } native || NativeObjectLive(handle, native)) continue;
            if (slot >= kernelEffects.handleSlots.Count || !ReferenceEquals(kernelEffects.handleSlots[slot], handle)) continue;
            // An effect handle is not released on its own: the module that applied the effect is called back first,
            // which is the same ending its own tick pass would write.
            if (handle.EffectId >= 0 && kernelEffects.effects.TryGetValue(handle.EffectId, out var effect)) { EndEffect(effect, EffectEndReasons.Reclaimed, CurrentTick); continue; }
            kernelEffects.ReleaseHandle(slot);
        }
    }

    /// <summary>Whether the native object of one handle is still there. The provider's own predicate answers when it
    /// gave one; otherwise the object's kind is asked of the registered resolvers, and a kind none of them claims is
    /// held rather than released — an object this runtime cannot name is not an object whose life it can pronounce
    /// over. A predicate that throws is a failure of the provider's own table, and the object is treated as gone so
    /// the slot is never kept by a table that cannot answer.</summary>
    private bool NativeObjectLive(HandleSlot handle, object native)
    {
        if (handle.NativeIsLive is { } isLive)
        {
            try { return isLive(native); }
            catch (Exception error)
            {
                log.LogCallbackFailed("handle-native-live-failed provider=" + handle.ProviderId, error);
                return false;
            }
        }
        if (handle.NativeLife is { } held)
        {
            if (!held.Kind.Retired) return entityLives.EntityLifeCode(in held) == null;
            handle.NativeLife = null;
        }
        string kind;
        try { kind = ResolveEntityObject(native); }
        catch (RuntimeContractException) { return true; }
        if (kind == "") return true;
        if (!IsEntityCurrentKind(kind, native)) return false;
        // The object resolved to a live entity; where its kind's ends are reported, that life is kept and every
        // later sweep asks the reports about it rather than resolving the object again.
        handle.NativeLife = ReportedNativeLife(native);
        return true;
    }

    /// <summary>The life one native object belongs to when its kind's owner reports the ends of that kind, or null:
    /// an unrecognized object, a kind that is polled, or a lookup that failed leave the handle on the sweep's own
    /// per-tick resolution.</summary>
    private EntityCheck? ReportedNativeLife(object native)
    {
        string kind;
        try { kind = ResolveEntityObject(native); }
        catch (RuntimeContractException) { return null; }
        if (kind == "" || !registry.TryEntityKind(kind + ":", out var owner) || !owner.Reported) return null;
        EntityReference? reference;
        try { reference = EntityInstanceOf(kind, native); }
        catch (RuntimeContractException) { return null; }
        if (reference == null || reference.WorldEpoch != WorldEpoch) return null;
        var life = new EntityCheck(reference, owner);
        return entityLives.EntityLifeCode(in life, admission: true) == null ? life : null;
    }

    /// <summary>Whether the life the native object of one handle belongs to is still current: the question is put
    /// to the resolver that owns the object's kind and to nobody else, exactly as an instance lookup is.</summary>
    private bool IsEntityCurrentKind(string kind, object instance)
    {
        if (!registry.Tables.Resolvers.TryGetValue(kind, out var resolver)) return false;
        EntityReference? reference;
        try { reference = EntityInstanceOf(kind, instance); }
        catch (RuntimeContractException) { return false; }
        if (reference == null) return false;
        try { return resolver.Resolve(reference); }
        catch (Exception error)
        {
            log.LogCallbackFailed("entity-resolver-failed entity=" + reference.Id, error);
            return false;
        }
    }

    /// <summary>Cancels the schedule one timer handle holds, ends the effect one effect handle names, or runs the
    /// cancel hook the provider registered for a handle of its own. The count is the contract's own answer — 0 when
    /// the handle names a schedule that already ended, or a provider handle that had nothing left to stop — and a
    /// handle that is no longer live is refused by name. A confirmed local effect restoration spends its handle;
    /// a failed restore retains it for explicit cleanup retry. Owner cleanup remains pending until its receipt,
    /// and a provider hook follows its separately documented cancellation contract.</summary>
    private int CancelHandle(JsonElement value, JsonElement port, string detail)
        => CancelHandle(ReadHandle(value), RuntimeGraphContracts.OptionalText(port, "handleKind"), RuntimeGraphContracts.OptionalText(port, "lifetime"), detail);

    private int CancelHandle(RuntimeHandle value, string? kind, string? lifetime, string detail)
    {
        var slot = kernelEffects.CheckHandle(value, kind, lifetime, detail, out var address);
        kernelOwner.RequireOwnerCancelReceipt(address, slot);
        if (slot.OwnedLifetime is { } owned)
        {
            if (owned.Cancel == null) return 0; // The same callback is already ending this handle.
            if (!kernelEffects.EndOwnedHandle(address, slot, "explicit-cancel"))
                throw new RuntimeContractException("handle-cancel-failed", detail);
            return 1;
        }
        var cancelled = 0;
        if (slot.ScheduleId != null && kernelFlow.schedules.TryGetValue((slot.ProviderId, slot.ScheduleId), out var job))
        {
            kernelFlow.EndSchedule(job, "cancelled", "explicit-cancel");
            kernelFlow.PruneQueue();
            cancelled = 1;
        }
        // An effect handle is the kernel's own to end: the module that applied the effect is called back with the
        // cancellation, so the module never registers a second cancel hook for the same state.
        if (slot.EffectId >= 0 && kernelEffects.effects.TryGetValue(slot.EffectId, out var effect))
        {
            // Stopping the host's clock does not prove that its holder restored the native write. EndEffect
            // spends each layer's existing owner restore reservation and prevents any later local pulse.
            var remoteRestore = effect.Layers.Any(layer => layer.OwnerApplication != null);
            if (!EndEffect(effect, EffectEndReasons.Cancelled, CurrentTick))
                throw new RuntimeContractException("handle-cancel-failed", detail);
            if (remoteRestore) throw new RuntimeContractException("owner-cancel-pending", detail);
            return 1;
        }
        if (slot.Cancel is { } hook)
        {
            slot.Cancel = null;
            try { hook(); }
            catch (Exception error)
            {
                log.LogCallbackFailed("handle-cancel-failed provider=" + slot.ProviderId + " detail=" + detail, error);
                throw new RuntimeContractException("handle-cancel-failed", detail);
            }
            finally
            {
                // ReleaseHandle only clears the slot and cannot invoke provider code. A hook may itself release
                // this handle and allocate another in its place; never release that newer generation.
                if (address < kernelEffects.handleSlots.Count && ReferenceEquals(kernelEffects.handleSlots[address], slot)) kernelEffects.ReleaseHandle(address);
            }
            return 1;
        }
        kernelEffects.ReleaseHandle(address);
        return cancelled;
    }

    /// <summary>Records the first refusal of the query step being evaluated. The session reports it once, and the
    /// step turns it into a rejection: an exhausted budget is never answered with a partial frame.</summary>
    internal void QueryRefused(string code)
    {
        queryRefusal ??= code switch
        {
            "entity-query-budget" or "entity-query-tick-budget" => RuntimeAbiCodes.QueryBudget,
            "entity-observer-unavailable" => RuntimeAbiCodes.QueryObserverUnavailable,
            _ => code
        };
    }

    /// <summary>Starts one query step's evaluation: the session it hands out is live only for that call, and the
    /// refusal it may record belongs to this step alone.</summary>
    private RuntimeQuerySession BeginQuery(string nodeId)
    {
        queryRefusal = null;
        return new RuntimeQuerySession(this, nodeId, true);
    }

    /// <summary>The world read a `pure` step is never given; every attempt is refused rather than answered.</summary>
    private RuntimeQuerySession NoQuery(string nodeId)
    {
        // A refusal recorded by an earlier query step belongs to that step alone. Without this reset it would
        // outlive its step and reject the next pure step as `pure-world-port` although that step read nothing.
        queryRefusal = null;
        return new(this, nodeId, false);
    }
}

