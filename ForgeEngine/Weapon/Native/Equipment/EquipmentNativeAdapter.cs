using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeWeapon.Native;

/// <summary>Reconciles post-body native backpack readback with the single W1 identity index.
/// The handle table only maps already-recorded lives to native objects; facts live in the index.</summary>
internal sealed partial class EquipmentNativeAdapter
{
    // The definition key is observed on this reviewed build; datablock content revisions are not observable here.
    internal const string ResourceRevision = "gtfo-build-20403457";
    // The game's own record of the offline gear block a gear came from: GearManager.LoadOfflineGearDatas builds
    // GearIDRange.PlayfabItemInstanceId as this prefix plus PlayerOfflineGearDataBlock.persistentID, and the same
    // spelling is what the game's favorites file stores as LastEquipped_*. Evidence: evidence/w5-gear-block.json.
    internal const string GearBlockPrefix = "OfflineGear_ID_";
    // Player lives belong to ForgePlayer. Weapon only asks the SDK which current reference an SNet_Player has,
    // so it never derives a player from slots, agents, names, pointers or the account key. Enemy lives belong to
    // ForgeEnemy the same way: a hit asks its agent's own domain for the current reference and never invents one.
    // Map objects — a door, a terminal — belong to ForgeMap: a hit hands the object it was resolved
    // against to that kind's own instance lookup, which is what knows what a map object is and how it is
    // addressed. Weapon never reads a door or a terminal type, so the map domain can answer this kind without
    // this package depending on it.
    internal sealed record Handle(EntityReference Entity, PlayerBackpack Backpack, IntPtr BackpackPointer,
        IntPtr ItemPointer, IntPtr InstancePointer, int SlotIndex, string ResourceId, EntityReference Owner, bool Deployed)
    {
        // IL2CPP wrappers may expire while the same native backpack remains registered. Rebind the wrapper
        // only after checking the owning player life and the original backpack pointer.
        public PlayerBackpack Backpack { get => Life.Backpack; set => Life.Backpack = value; }
        /// <summary>Everything that accumulates during this one equipment life. A rebuilt handle shares this
        /// object, so a `with` copy never loses the shot count, the placement count or the open world instance.</summary>
        internal Life Life { get; } = new(Backpack);
    }
    /// <summary>Per-life mutable state: shots of this equipment life, world instances this life has placed, and
    /// the placement that is currently open. A new Handle is made whenever a life starts, so none of it leaks
    /// across a recall, a redeploy or a slot change; a shotgun's several hits still share the one shot.</summary>
    internal sealed class Life
    {
        internal Life(PlayerBackpack backpack) => Backpack = backpack;
        internal PlayerBackpack Backpack;
        internal bool Active = true;
        internal HolderBinding? Holder;
        internal long Shots;
        internal long Placements;
        internal PlacedObject? Placed;
        internal EquipmentObservation? Observation;
        internal string? MountReference;
    }
    /// <summary>One deployed world object: a sentry or mine instance that currently exists in the world. Its
    /// identity is separate from the equipment life that placed it, because one life can place several in turn.
    /// The owner is re-read from the native object on every check, never cached from the spawn body. The kind is
    /// the spawning family's own declaration — `SentryGunInstance` or `MineDeployerInstance` — because the two
    /// shells are the same `Item` at this layer and the game keeps no member that spells which one an instance
    /// is.</summary>
    internal sealed class PlacedObject
    {
        internal PlacedObject(EntityReference entity, Item instance, Handle handle, string kind)
        { Entity = entity; Instance = instance; InstancePointer = instance.Pointer; Handle = handle; Kind = kind; }
        internal EntityReference Entity { get; }
        internal Item Instance { get; }
        internal IntPtr InstancePointer { get; }
        internal Handle Handle { get; }
        internal string Kind { get; }
        internal bool Recalled { get; set; }
        /// <summary>Why this life ended. A closed life is not resolvable any more, so a repeated ending is refused
        /// by the missing placement rather than by a flag; the reason is kept only for one diagnostic line.</summary>
        internal string? Ended { get; set; }
    }
    private readonly Dictionary<IntPtr, Handle> _byInstance = new();
    private readonly Dictionary<string, Handle> _byEntity = new(StringComparer.Ordinal);
    private readonly HashSet<EntityReference> _retiredOwnerMappings = new();
    private readonly HashSet<EntityReference> _skippedOwnerMappings = new();
    private long _ownerMappingWorld = -1;
    private readonly Dictionary<IntPtr, PlacedObject> _placements = new();
    private readonly Dictionary<string, PlacedObject> _placementsByEntity = new(StringComparer.Ordinal);
    /// <summary>The open placement of each equipment life, so a world instance this machine never mapped can still
    /// be joined to the life that owns it. One life places one device at a time — the slot the backpack marked
    /// deployed is one slot — so this is one entry per life and never a list to choose from.</summary>
    private readonly Dictionary<string, PlacedObject> _placedByLife = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> _unresolvedReported = new();
    private readonly HashSet<string> _unmatchedReferences = new(StringComparer.Ordinal);
    private readonly IForgeEngine _kernel;
    /// <summary>ForgePlayer's player directory: the one answer to which player life a native player is, and which
    /// native player a life names. This package never resolves a player itself.</summary>
    private readonly Func<bool> _canExecute, _canExecuteOwner;
    private readonly WeaponLog _log;
    private EquipmentIdentitySession? _identity;
    private RuntimeSubscriptionGate? _deployGate, _recallGate, _despawnGate;
    private long _world = -1, _next, _deploySequence;
    internal LevelSentryIdentityMap LevelSentries { get; }

    // Info records are one per life start/end, location change or table clear, so in-game checks can follow each
    // step without a consuming plan. They carry only Forge references, slot and resource keys. A fact the kernel
    // queued or refused is the kernel's own record (`trigger.fired` / `event.rejected`), never a line here.
    private readonly IPlayerDirectory _players;
    internal EquipmentNativeAdapter(IForgeEngine kernel, IPlayerDirectory players, Func<bool> canExecute,
        Func<bool> canExecuteOwner, WeaponLog log)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _canExecuteOwner = canExecuteOwner ?? throw new ArgumentNullException(nameof(canExecuteOwner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        LevelSentries = new LevelSentryIdentityMap(kernel,
            () => _identity is { Registration.IsRegistered: true } && _canExecute(), ReleaseObjectScope);
    }

    internal void Attach(EquipmentIdentitySession identity)
    {
        if (_identity != null) throw new InvalidOperationException("The adapter already feeds an identity session.");
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        identity.Registration.RegisterOwnerIdentities(ExportOwnerMappings, ImportOwnerMappings);
        _deployGate = identity.GateOf(ModuleDefinition.DeployCompletedBinding);
        _recallGate = identity.GateOf(ModuleDefinition.RecallCompletedBinding);
        _despawnGate = identity.GateOf(ModuleDefinition.DespawnedBinding);
    }

    /// <summary>The ending of an owner-slot placement guess retired because the producer named the exact level sentry.</summary>
    internal const string LevelSentryBoundReason = "level-sentry-bound";

    /// <summary>Map hands over the exact ISyncedItem from SpawnGear; Map's matcher reads this same table rather than
    /// deriving an equipment id.</summary>
    internal EntityReference BindLevelSentry(string address, long world, object? spawned)
    {
        var entity = LevelSentries.Bind(address, world, spawned);
        var instance = LevelSentries.ItemOf(entity)!;
        // OnSpawn precedes the producer callback. Retire any owner-slot heuristic that observed this item
        // before the producer supplied its exact identity; it must never remain another event subject.
        if (_placements.TryGetValue(instance.Pointer, out var guessed))
        {
            guessed.Ended = LevelSentryBoundReason;
            ReleaseObjectScope(guessed); Close(guessed);
        }
        // The completion callback is after the real SpawnGear/OnSpawn path. Its exact instance is now bound,
        // so the map deployment has the same subject as its later firing and pickup facts.
        if (_deployGate is { HasSubscribers: true })
        {
            var id = "gtfo.equipment.deploy:" + Number(world) + ":" + Number(checked(++_deploySequence));
            var fact = Begin(_deployGate, world);
            WeaponPlacementContract.DeployOutputs(fact.Values, null, entity,
                WeaponPlacementContract.KindIndex(WeaponPlacementContract.SentryGunKind), Position(instance));
            fact.Raise(id, world, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(world));
        }
        return entity;
    }

    internal EntityReference? DeployedEntityOf(IntPtr pointer)
        => LevelSentries.EntityOf(pointer) ?? PlacementOf(pointer)?.Entity;

    internal int PlacementCount => _placements.Count;

}

/// <summary>A fact scope spelled as a fixed prefix and one entity's id, kept for the last entity it was asked about, so a run
/// of facts on one item spells its scope once (the scope of a whole world is <see cref="WorldScope"/>).</summary>
internal sealed class EntityScope
{
    private sealed record Spelled(string Id, string Scope);
    internal static readonly EntityScope Equipment = new(EntityKinds.Equipment + ":");
    internal static readonly EntityScope Player = new(EntityKinds.Player + ":");
    private readonly string _prefix;
    private Spelled _last = new("", "");

    internal EntityScope(string prefix) { _prefix = prefix; }

    internal string Of(EntityReference entity)
    {
        var last = _last;
        if (string.Equals(last.Id, entity.Id, StringComparison.Ordinal)) return last.Scope;
        _last = last = new Spelled(entity.Id, _prefix + entity.Id);
        return last.Scope;
    }
}
