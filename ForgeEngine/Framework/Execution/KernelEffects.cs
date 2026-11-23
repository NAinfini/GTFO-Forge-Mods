using System.Collections.Generic;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>What a behavior leaves standing in the world and the handles that name it: the effect table with its identity, status and end-trigger indexes, the handle slots and their owned lifetimes, the sounds in flight, and the owner-side record of each effect. It allocates, indexes, checks and releases its own rows; ending an effect, pulsing it and publishing that it ended run above, because they call handlers and admit events.</summary>
internal sealed partial class KernelEffects
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelLog log;
    private readonly EntityLives entityLives;
    private readonly RuntimeLimits limits;

    internal KernelEffects(KernelWorld world, KernelRegistry registry, KernelLog log, EntityLives entityLives, RuntimeLimits limits)
    {
        this.world = world;
        this.registry = registry;
        this.log = log;
        this.entityLives = entityLives;
        this.limits = limits;
    }

    internal Dictionary<string, int> effectIdentity = new(StringComparer.Ordinal);

    internal Dictionary<int, EffectSlot> effects = new();

    internal Dictionary<StatusEffectKey, List<EffectSlot>> statusEffectIndex = new();

    internal bool statusEffectIndexDirty;

    internal int effectSequence;

    /// <summary>The compile-time `end_on` bindings of the live effects, each with one dense index, and the
    /// instances that declared it. A binding is indexed the first time an instance names it and keeps its index
    /// for the kernel's life, so an event on it walks only the instances that can end, never the whole table. The
    /// id list is small enough to resolve a dispatch's binding by a span of ordinal comparisons: an event carries
    /// no binding ordinal, and a dictionary would be the per-dispatch string lookup I-PLAN forbids.</summary>
    internal List<string> endOnIndexBindings = new();

    internal List<List<EffectSlot>> endOnIndexSlots = new();

    internal string? lastEndOnBinding;

    internal int lastEndOnBindingIndex = -1;

    internal readonly Stack<List<EffectSlot>> endingScratchPool = new();

    // Advance is non-recursive, so one list is the effect-pass snapshot. Its backing storage is reserved when an
    // effect is admitted, never on that effect's first periodic tick.
    internal readonly List<EffectSlot> lifetimeScratch = new();

    /// <summary>The dense index one compile-time ending binding keeps, or -1. The last answer is cached because a
    /// dispatch burst names the same trigger repeatedly; a miss costs one ordinal comparison per indexed binding,
    /// which is the small set that only effects declaring `end_on` contribute to.</summary>
    internal int EndOnIndex(string bindingId)
    {
        if (lastEndOnBinding != null && string.Equals(lastEndOnBinding, bindingId, StringComparison.Ordinal))
            return lastEndOnBindingIndex;
        var index = -1;
        for (var i = 0; i < endOnIndexBindings.Count; i++)
            if (string.Equals(endOnIndexBindings[i], bindingId, StringComparison.Ordinal)) { index = i; break; }
        lastEndOnBinding = bindingId; lastEndOnBindingIndex = index;
        return index;
    }

    internal void IndexEffectEndings(EffectSlot slot)
    {
        IndexStatusEffect(slot);
        foreach (var ending in slot.Options.EndTriggers)
        {
            var index = EndOnIndex(ending.BindingId);
            if (index < 0)
            {
                index = endOnIndexBindings.Count;
                endOnIndexBindings.Add(ending.BindingId);
                endOnIndexSlots.Add(new List<EffectSlot>());
                lastEndOnBinding = null; lastEndOnBindingIndex = -1;
            }
            var slots = endOnIndexSlots[index];
            if (!slots.Contains(slot)) slots.Add(slot);
        }
    }

    internal void UnindexEffectEndings(EffectSlot slot)
    {
        UnindexStatusEffect(slot);
        foreach (var ending in slot.Options.EndTriggers)
        {
            var index = EndOnIndex(ending.BindingId);
            if (index >= 0) endOnIndexSlots[index].Remove(slot);
        }
    }

    internal void ClearEffectEndings()
    {
        foreach (var slots in statusEffectIndex.Values) slots.Clear();
        statusEffectIndex.Clear();
        statusEffectIndexDirty = false;
        foreach (var slots in endOnIndexSlots) slots.Clear();
        endOnIndexSlots.Clear();
        endOnIndexBindings.Clear();
        lastEndOnBinding = null; lastEndOnBindingIndex = -1;
    }

    internal void IndexStatusEffect(EffectSlot slot)
    {
        if (slot.StatusKind.Length == 0) return;
        var status = slot.Options.StatusIndex;
        if (status < 0) return; // EffectOptions already rejects this; migration revalidates the loaded step.
        if (slot.Subject.Count != 0) statusEffectIndexDirty = true;
    }

    /// <summary>Rebuilds the derived target/status address only when a status action actually needs it. New effects
    /// merely dirty the view, so ordinary high-frequency applications never pay an O(live effects) insertion scan.
    /// Dictionary.Values is deliberately the source order: it is the order the replaced full-table dispel walked,
    /// including entry-slot reuse after a removal.</summary>
    internal void EnsureStatusEffectIndex()
    {
        if (!statusEffectIndexDirty) return;
        foreach (var slots in statusEffectIndex.Values) slots.Clear();
        statusEffectIndex.Clear();
        foreach (var slot in effects.Values) AddStatusEffectIndex(slot);
        statusEffectIndexDirty = false;
    }

    internal void AddStatusEffectIndex(EffectSlot slot)
    {
        if (slot.Options.StatusIndex < 0 || slot.Subject.Count == 0) return;
        for (var index = 0; index < slot.Subject.Count; index++)
        {
            var target = slot.Subject[index];
            var key = new StatusEffectKey(target.Id, target.WorldEpoch, slot.Options.StatusIndex);
            if (!statusEffectIndex.TryGetValue(key, out var slots))
                statusEffectIndex.Add(key, slots = new List<EffectSlot>());
            // One multi-recipient effect may carry a duplicate receiver. The old Any-based table walk still matched
            // the slot once, so the derived index does too.
            if (slots.Count == 0 || !ReferenceEquals(slots[^1], slot)) slots.Add(slot);
        }
    }

    internal void UnindexStatusEffect(EffectSlot slot)
    {
        if (slot.StatusKind.Length == 0) return;
        // A dirty view will be discarded before it is read, so touching its stale lists only adds removal work.
        if (statusEffectIndexDirty) return;
        var status = slot.Options.StatusIndex;
        if (status < 0) return;
        for (var index = 0; index < slot.Subject.Count; index++)
        {
            var target = slot.Subject[index];
            var key = new StatusEffectKey(target.Id, target.WorldEpoch, status);
            if (!statusEffectIndex.TryGetValue(key, out var slots)) continue;
            slots.Remove(slot);
            if (slots.Count == 0) statusEffectIndex.Remove(key);
        }
    }

    /// <summary>Whether every recipient of one effect is still current. A reference whose own resolver answers no
    /// is a life that ended; one whose kind has no resolver at all is not this kernel's to judge, so it counts as
    /// current and the instance lives out its own duration.</summary>
    internal EntityCheck[] BindEffectSubjectLiveness(IReadOnlyList<EntityReference> subject)
    {
        var live = new List<EntityCheck>(subject.Count);
        for (var index = 0; index < subject.Count; index++)
        {
            var entity = subject[index];
            if (entityLives.TryEntityCheck(entity, out var check)) live.Add(check);
        }
        return live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();
    }

    internal List<HandleSlot?> handleSlots = new();

    internal int endingOwnedLifetimes;

    internal Dictionary<string, int> providerIndex = new(StringComparer.Ordinal);

    internal int handleGeneration;

    /// <summary>The registration-order index a handle carries instead of its provider's id: stable for the life
    /// of the kernel, so a provider unregistering never renumbers another provider's handles.</summary>
    internal int ProviderIndex(string provider)
    {
        if (providerIndex.TryGetValue(provider, out var index)) return index;
        index = providerIndex.Count;
        providerIndex.Add(provider, index);
        return index;
    }

    /// <summary>Allocates one handle and returns its wire value: the identity triple in a <see cref="FrameHandle"/>
    /// plus the creating provider's index. A slot is reused only when free, and every allocation takes a new
    /// generation, so a value read after its slot was recycled fails `stale-handle` instead of naming a stranger.</summary>
    internal JsonElement CreateHandle(string provider, string kind, string lifetime, out int slot, string? scheduleId = null,
        int effectId = -1, string statusKind = "")
        => AllocateHandle(provider, kind, lifetime, out slot, scheduleId, effectId, statusKind).ToJson();

    /// <summary>The allocation itself, answering the handle as its value: nothing is written for it until something
    /// asks for the wire form.</summary>
    internal RuntimeHandle AllocateHandle(string provider, string kind, string lifetime, out int slot, string? scheduleId = null,
        int effectId = -1, string statusKind = "")
    {
        var free = handleSlots.FindIndex(entry => entry is not { Active: true });
        if (free < 0)
        {
            RuntimeJson.Require(handleSlots.Count < limits.MaxLiveHandles, RuntimeAbiCodes.HandleBudget, "Live handle capacity reached.");
            handleSlots.Add(null); free = handleSlots.Count - 1;
        }
        var generation = ++handleGeneration;
        RuntimeJson.Require(generation > 0 && generation < int.MaxValue, RuntimeAbiCodes.HandleBudget, "Handle generation exhausted.");
        var entrySlot = new HandleSlot { Kind = kind, Lifetime = lifetime, ProviderId = provider, Generation = generation, Active = true, ScheduleId = scheduleId, EffectId = effectId, StatusKind = statusKind ?? "" };
        handleSlots[free] = entrySlot;
        slot = free;
        RuntimeJson.Require(world.WorldEpoch <= int.MaxValue, RuntimeAbiCodes.HandleBudget, "The world epoch is outside a handle's range.");
        return new RuntimeHandle((int)world.WorldEpoch, generation, free, ProviderIndex(provider));
    }

    /// <summary>Releases one handle slot. The generation is left as it was: the next allocation overwrites both,
    /// and a value that still names the old generation must fail rather than read the new occupant. Everything the
    /// slot carried goes with it, so a released handle names no native object and no cancel callback even while the
    /// slot is free.</summary>
    internal void ReleaseHandle(int slot)
    {
        if (slot >= 0 && slot < handleSlots.Count) handleSlots[slot] = null;
    }

    /// <summary>The slot one live handle value names, as its own provider wrote it. Everything a provider's own
    /// handle API accepts goes through this: the value must be a well-formed handle of this world, name a live
    /// slot, and be the slot this module's provider created. The port-level `handle-kind`/`handle-lifetime` check
    /// stays where it always was — at the step that consumes the handle through a declared port, which is what
    /// `CheckHandle` is.</summary>
    internal HandleSlot HandleOf(RuntimeModuleHandle owner, RuntimeHandle value)
        => HandleRefusal(owner, value, out var slot) is { } code ? throw new RuntimeContractException(code, owner.ProviderId) : slot!;

    /// <summary>The same rule as a question: null with the slot where the value is this module's live handle, the
    /// refusal's code otherwise. Nothing is thrown, so a provider's own liveness read allocates nothing.</summary>
    internal string? HandleRefusal(RuntimeModuleHandle owner, RuntimeHandle value, out HandleSlot? slot)
    {
        slot = null;
        if (LiveHandle(value, out var live) is { } code) return code;
        if (live!.ProviderId != owner.ProviderId) return "handle-owner";
        slot = live;
        return null;
    }

    /// <summary>The liveness half every handle check shares: the value names a slot of this world that is still the
    /// life it was issued for. Null with that slot, or the refusal's code.</summary>
    private string? LiveHandle(RuntimeHandle value, out HandleSlot? live)
    {
        live = null;
        if (value.WorldEpoch != world.WorldEpoch) return "stale-world";
        if ((uint)value.Local >= (uint)handleSlots.Count || handleSlots[value.Local] is not { Active: true } slot
            || slot.Generation != value.LifeEpoch) return RuntimeAbiCodes.StaleHandle;
        live = slot;
        return null;
    }

    internal OwnedEntityResolver[] CaptureOwnedEntityResolvers(EntityCheck[] entities)
    {
        if (entities.Length == 0) return Array.Empty<OwnedEntityResolver>();
        var result = new OwnedEntityResolver[entities.Length];
        for (var index = 0; index < entities.Length; index++)
        {
            var entity = entities[index];
            var kind = RuntimeJson.KindOf(entity.Entity.Id);
            if (!registry.Tables.Resolvers.TryGetValue(kind, out var resolver)
                || !registry.Modules.TryGetValue(resolver.Owner, out var generation)
                || !ReferenceEquals(resolver.Resolve, entity.Resolve))
                throw new RuntimeContractException("source-lifecycle", entity.Entity.Id);
            result[index] = new OwnedEntityResolver(kind, resolver.Owner, generation);
        }
        return result;
    }

    internal bool OwnedEntityResolversCurrent(EntityCheck[] entities, OwnedEntityResolver[] resolvers)
    {
        for (var index = 0; index < resolvers.Length; index++)
        {
            var captured = resolvers[index];
            if (!registry.IsRegistered(captured.Provider, captured.Generation)
                || !registry.Tables.Resolvers.TryGetValue(captured.Kind, out var resolver)
                || resolver.Owner != captured.Provider || !ReferenceEquals(resolver.Resolve, entities[index].Resolve))
                return false;
        }
        return true;
    }

    /// <summary>Detaches before provider code runs. A callback may release this handle, cancel another scope,
    /// or reuse this address; the final release must never spend that replacement's generation.</summary>
    internal bool EndOwnedHandle(int address, HandleSlot slot, string code)
    {
        if (address >= handleSlots.Count || !ReferenceEquals(handleSlots[address], slot)
            || slot.OwnedLifetime?.Cancel is not { } cancel) return true;
        slot.OwnedLifetime.Cancel = null;
        var succeeded = true;
        Exception? failure = null;
        endingOwnedLifetimes++;
        try { cancel(code); }
        catch (Exception error) { succeeded = false; failure = error; }
        finally
        {
            endingOwnedLifetimes--;
            if (address < handleSlots.Count && ReferenceEquals(handleSlots[address], slot)) ReleaseHandle(address);
        }
        if (!succeeded) log.LogObserverFailed(slot.ProviderId, "handle-lifetime-cancel-failed " + code + ": " + RuntimeDiagnostics.Describe(failure!));
        return succeeded;
    }

    internal void StopOwnedHandleSource(string? provider, string? scope, string code)
    {
        // Registration during an ending callback is refused, so this walk needs no independent work list.
        var count = handleSlots.Count;
        for (var address = 0; address < count && address < handleSlots.Count; address++)
            if (handleSlots[address] is { Active: true, OwnedLifetime: { } owned } slot
                && (provider == null || slot.ProviderId == provider) && (scope == null || owned.Scope == scope))
                EndOwnedHandle(address, slot, code);
    }

    internal string? OwnedHandleEndCode(HandleSlot slot, OwnedHandleLifetime owned)
    {
        // Stable ticks only ask the captured entity delegates. Removing/replacing a registration advances
        // this generation; scope cancellation already ends matching slots synchronously.
        if (owned.ValidatedGeneration != world.lifecycleGeneration)
        {
            if (!registry.IsRegistered(slot.ProviderId, owned.Generation)) return "source-lifecycle";
            if (world.cancelled.Contains((slot.ProviderId, owned.Scope))) return "scope-cancelled";
            if (!OwnedEntityResolversCurrent(owned.Entities, owned.Resolvers)) return "source-lifecycle";
            owned.ValidatedGeneration = world.lifecycleGeneration;
        }
        return entityLives.EntityLivenessCode(owned.Entities);
    }

    /// <summary>The one place a handle is checked: at the step that consumes it, never while it is being moved.
    /// The identity must name a live slot of this world, and the port's declared kind and lifetime must be the
    /// ones the handle was created with.</summary>
    internal HandleSlot CheckHandle(JsonElement value, JsonElement port, string detail)
        => CheckHandle(ReadHandle(value), port, detail, out _);

    /// <summary>The same check, handing back the slot's own address so a caller that has to release what it just
    /// checked never reads the value a second time.</summary>
    internal HandleSlot CheckHandle(RuntimeHandle value, JsonElement port, string detail, out int address)
        => CheckHandle(value, RuntimeGraphContracts.OptionalText(port, "handleKind"), RuntimeGraphContracts.OptionalText(port, "lifetime"), detail, out address);

    /// <summary>The check itself, against the kind and lifetime a port declares: a step's own port carries the two
    /// from its resolution (<see cref="PortView.HandleKind"/>).</summary>
    internal HandleSlot CheckHandle(RuntimeHandle value, string? kind, string? lifetime, string detail, out int address)
    {
        address = value.Local;
        return HandleRefusal(value, kind, lifetime, out var slot) is { } code ? throw new RuntimeContractException(code, detail) : slot!;
    }

    /// <summary>The same check as a question: null with the slot, or the refusal's code. A caller whose detail is
    /// text it would have to build asks this and builds the text only for a refusal.</summary>
    internal string? HandleRefusal(RuntimeHandle value, string? kind, string? lifetime, out HandleSlot? slot)
    {
        slot = null;
        if (LiveHandle(value, out var live) is { } code) return code;
        if (live!.Kind != kind) return RuntimeAbiCodes.HandleKind;
        if (live.Lifetime != lifetime) return RuntimeAbiCodes.HandleLifetime;
        slot = live;
        return null;
    }

    internal Dictionary<RuntimeHandle, OwnerEffect> ownerEffects = new();

    internal Dictionary<int, SoundInstance> sounds = new();

    internal Dictionary<string, LocalSound> localSounds = new(StringComparer.Ordinal);

    // One slot per admitted identity, including host requests not played locally. True means stopped.
    internal Dictionary<string, bool> soundHistory = new(StringComparer.Ordinal);

    internal List<PresentationOutput> pendingSoundStops = new();

    internal long soundSequence;

    internal bool StopLocalSound(string id)
    {
        // A stop may precede a delayed play. Keep its identity until the local world ends.
        // Every admitted instance already reserves its eventual stop identity, whether played here or remotely.
        RuntimeJson.Require(soundHistory.ContainsKey(id) || soundHistory.Count < MaximumEventHistory,
            RuntimeAbiCodes.HandleBudget, "Sound history capacity reached.");
        soundHistory[id] = true;
        if (!localSounds.Remove(id, out var sound)) return false;
        sound.Stop();
        return true;
    }

    /// <summary>
    /// Whether the pool slot a wait recorded still holds the exact handle the wait was given: the same live slot
    /// index, created by the same provider, in the same world, with the generation the handle value carries. Every
    /// one of those is part of the handle's own identity, so a recycled slot — same index, new generation and
    /// another owner — is not this wait's to release.
    /// </summary>
    internal bool SlotHoldsWaitHandle(WaitJob job)
    {
        var slot = job.HandleSlot;
        if (slot < 0 || slot >= handleSlots.Count) return false;
        if (handleSlots[slot] is not { Active: true } entry) return false;
        if (!string.Equals(entry.ProviderId, job.Handle.ProviderId, StringComparison.Ordinal)) return false;
        var value = job.HandleValue;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!value.TryGetProperty("worldEpoch", out var world) || !world.TryGetInt64(out var worldEpoch) || worldEpoch != this.world.WorldEpoch) return false;
        if (!value.TryGetProperty("local", out var local) || !local.TryGetInt32(out var address) || address != slot) return false;
        return value.TryGetProperty("lifeEpoch", out var generation) && generation.TryGetInt32(out var token)
            && token == entry.Generation;
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelEffects other)
    {
        (handleSlots, other.handleSlots) = (other.handleSlots, handleSlots);
        (providerIndex, other.providerIndex) = (other.providerIndex, providerIndex);
        (effects, other.effects) = (other.effects, effects);
        (effectIdentity, other.effectIdentity) = (other.effectIdentity, effectIdentity);
        (statusEffectIndex, other.statusEffectIndex) = (other.statusEffectIndex, statusEffectIndex);
        (statusEffectIndexDirty, other.statusEffectIndexDirty) = (other.statusEffectIndexDirty, statusEffectIndexDirty);
        (endOnIndexBindings, other.endOnIndexBindings) = (other.endOnIndexBindings, endOnIndexBindings);
        (endOnIndexSlots, other.endOnIndexSlots) = (other.endOnIndexSlots, endOnIndexSlots);
        (lastEndOnBinding, other.lastEndOnBinding) = (other.lastEndOnBinding, lastEndOnBinding);
        (lastEndOnBindingIndex, other.lastEndOnBindingIndex) = (other.lastEndOnBindingIndex, lastEndOnBindingIndex);
        (ownerEffects, other.ownerEffects) = (other.ownerEffects, ownerEffects);
        (sounds, other.sounds) = (other.sounds, sounds);
        (localSounds, other.localSounds) = (other.localSounds, localSounds);
        (soundHistory, other.soundHistory) = (other.soundHistory, soundHistory);
        (pendingSoundStops, other.pendingSoundStops) = (other.pendingSoundStops, pendingSoundStops);
        (effectSequence, other.effectSequence) = (other.effectSequence, effectSequence);
        (handleGeneration, other.handleGeneration) = (other.handleGeneration, handleGeneration);
    }
}
