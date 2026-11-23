using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The reload and inventory half of the Weapon provider's trigger rows: the three combat reload facts, the
/// six equipment inventory and use facts, the large item a player carries and an ammunition pool changing.
/// <b>Every one of the eleven is declared by the runtime's own
/// trigger contract</b> — the provider `forge.contract.trigger` owns the shape of a trigger id and a second copy
/// of one would be the drift that contract exists to prevent — so this file names their ids and their bindings and
/// reads the shipped rows back from <see cref="TriggerContracts"/> where a caller needs them. A label, a sentence
/// or a port list belongs in `Tools/Forge/capability-copy.json` and the reviewed Primitive, never here.
///
/// The rows are declared together because they share one lifetime and one readback discipline: the reload rows are
/// decided by a single observation of one reload life, and the inventory rows are decided by one readback of the
/// backpack and pool after a native body returned. Every binding this file names is observe-only — this provider
/// writes no inventory, no pool and no reload state — so the whole family is `implementation-only`.
///
/// A reload life that moved no ammunition closes silently: the node list's `e-w-reload` is start and completion,
/// so there is no interrupted-reload row and no reason code for one. The tool-energy row is gone for the same
/// reason — the node list has no energy-change node, and the game's own `OnBatteryLevelChange` member carries no
/// owner, which made the row the one fact this family could publish for somebody else's tool.</summary>
internal static class ReloadInventoryContract
{
    public const string ReloadStartedCapability = Blocks.TriggerCombatReloadStarted.Id;
    public const string ReloadCompletedCapability = Blocks.TriggerCombatReloadCompleted.Id;
    public const string ReloadTransferredCapability = Blocks.TriggerCombatReloadTransferred.Id;
    public const string StackChangedCapability = Blocks.TriggerEquipmentStackChanged.Id;
    public const string PickedUpCapability = Blocks.TriggerEquipmentPickedUp.Id;
    public const string DroppedCapability = Blocks.TriggerEquipmentDropped.Id;
    public const string UseStartedCapability = Blocks.TriggerEquipmentUseStarted.Id;
    public const string UseCompletedCapability = Blocks.TriggerEquipmentUseCompleted.Id;
    public const string UseFailedCapability = Blocks.TriggerEquipmentUseFailed.Id;
    /// <summary>The large item a player is carrying changing, which is the backpack's own in-level carry slot and
    /// not one of the pockets: the row is about the item a player hauls through the level.</summary>
    public const string CarriedItemChangedCapability = Blocks.TriggerPlayerCarriedItemChanged.Id;
    /// <summary>One of a player's ammunition pools changing: the rounds a slot's own pack holds moving up or down,
    /// read back after a native body that could have written a pool. The row's `pool` port names the slot's pool
    /// through this provider's own `pool` resource kind, `value`/`delta` are the readback's own numbers, and
    /// `reason` says which kind of body moved it: refill, pickup or consume.</summary>
    public const string AmmoChangedCapability = Blocks.TriggerCombatAmmoChanged.Id;

    /// <summary>The `ammo_change_reason` members the ammunition row's `reason` port publishes. `refill` is a pool
    /// gaining rounds from a delivery — a resource pack, an ammunition pickup, a pool that arrived over the network;
    /// `pickup` is a pool gaining rounds because an item was stored into the backpack with its own charges; and
    /// `consume` is a pool losing rounds, which includes the rounds a reload moves out of the reserve.</summary>
    public const string RefillReason = "refill";
    public const string PickupReason = "pickup";
    public const string ConsumeReason = "consume";

    /// <summary>The eleven capability ids, in the order the catalog and the binding rows below carry them. The
    /// shapes live in the runtime's own trigger contract, which a module cannot declare for itself, so this file
    /// spells the ids once and a caller that walks the family never restates the set.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds = Array.AsReadOnly(new[]
    {
        ReloadStartedCapability, ReloadCompletedCapability, ReloadTransferredCapability,
        StackChangedCapability, PickedUpCapability, DroppedCapability, UseStartedCapability, UseCompletedCapability,
        UseFailedCapability, CarriedItemChangedCapability, AmmoChangedCapability
    });

    public const string ReloadStartedBinding = ModuleDefinition.ProviderId + ".binding.reload_started";
    public const string ReloadCompletedBinding = ModuleDefinition.ProviderId + ".binding.reload_completed";
    public const string ReloadTransferredBinding = ModuleDefinition.ProviderId + ".binding.reload_transferred";
    public const string StackChangedBinding = ModuleDefinition.ProviderId + ".binding.stack_changed";
    public const string PickedUpBinding = ModuleDefinition.ProviderId + ".binding.picked_up";
    public const string DroppedBinding = ModuleDefinition.ProviderId + ".binding.dropped";
    public const string UseStartedBinding = ModuleDefinition.ProviderId + ".binding.use_started";
    public const string UseCompletedBinding = ModuleDefinition.ProviderId + ".binding.use_completed";
    public const string UseFailedBinding = ModuleDefinition.ProviderId + ".binding.use_failed";
    public const string CarriedItemChangedBinding = ModuleDefinition.ProviderId + ".binding.carried_item_changed";
    public const string AmmoChangedBinding = ModuleDefinition.ProviderId + ".binding.ammo_changed";

    /// <summary>The permissions these facts read under. Ammunition and the equipment that carries it are one
    /// subject, so the reload and inventory rows read the same permission the wield facts already read.</summary>
    public const string AmmunitionReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The one observer the three reload bindings are implemented by, keyed by the equipment entity the
    /// reload belongs to.</summary>
    public const string ReloadHandler = "gtfo.weapon.reload_observe";
    /// <summary>The one observer the eight readback bindings are implemented by: one observer over a player's
    /// backpack and pools.</summary>
    public const string InventoryHandler = "gtfo.weapon.inventory_observe";

    /// <summary>The eleven rows as the runtime's trigger contract carries them, in <see cref="CapabilityIds"/>
    /// order: the catalog's own text with the reviewed Primitive's own graph. This family's cases hold the ports
    /// the two observers really fill to that shape — the shape the kernel checks a published fact with.</summary>
    public static IReadOnlyList<JsonElement> RowsJson() => TriggerContracts.Rows(CapabilityIds);

    /// <summary>The resource kind the ammunition row's `pool` port carries. The kind is a member of the
    /// framework's own resource vocabulary; this provider is its owner, because a pool is one of the ammunition
    /// tables this package already reads and no other package registers the kind.
    ///
    /// <b>What one pool is.</b> A pool's id is the game's own `InventorySlot` member text of the slot whose pack
    /// it is — the same text the native readback keys its per-slot table by, so the reference a fact publishes and
    /// the key the readback decided it from are one spelling. A pool is therefore level-independent vocabulary
    /// rather than a per-player instance: the player a pool belongs to travels in the fact's own `actor` port, and
    /// a plan can carry the reference as a compiled literal, which a per-player instance id could never be. The
    /// slots below are the ones this build's backpack answers a pool for; a slot with no pool is absent from the
    /// readback rather than answered as zero, and the provider resolves only the members it lists.</summary>
    public const string PoolResourceKind = "pool";
    public static readonly IReadOnlyList<string> PoolSlots = Array.AsReadOnly(new[]
    {
        "GearStandard", "GearSpecial", "GearClass", "ResourcePack", "Consumable", "ConsumableHeavy"
    });

    private static readonly IReadOnlyList<ResourceRef> CachedPools = EnumeratePools();

    /// <summary>The one resource provider this package registers, for the kind above: the kit the module
    /// definition and the focused tests both take, so the vocabulary the fact publishes under and the vocabulary
    /// the kernel resolves are one table.</summary>
    public static IReadOnlyDictionary<string, RuntimeResourceProvider> ResourceProviders()
        => new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
        {
            [PoolResourceKind] = RuntimeResourceProvider.Of(EnumeratePools, ResolvePool)
        };

    /// <summary>The pools this provider answers for. The table is the vocabulary above and not a scan of a
    /// running world: a pool is named by its slot, so it exists in every world the game itself built one in, and
    /// the fact that carries it is what says which player's pool changed.</summary>
    private static IReadOnlyList<ResourceRef> EnumeratePools()
    {
        var references = new ResourceRef[PoolSlots.Count];
        for (var index = 0; index < PoolSlots.Count; index++) references[index] = new ResourceRef(PoolResourceKind, PoolSlots[index]);
        return references;
    }

    private static ResourceRef? ResolvePool(string id)
    {
        for (var index = 0; index < CachedPools.Count; index++)
            if (string.Equals(PoolSlots[index], id, StringComparison.Ordinal)) return CachedPools[index];
        return null;
    }

    /// <summary>The pool reference one readback's slot key publishes, or null for a key that is not a pool this
    /// provider owns. The readback's own key is the slot text, so this is the one place the two are joined.</summary>
    public static ResourceRef? PoolReference(string slot)
        => ResolvePool(slot);

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(ReloadStartedBinding, ReloadStartedCapability, ReloadHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(ReloadCompletedBinding, ReloadCompletedCapability, ReloadHandler, "observe", new[] { AmmunitionReadPermission }) { Waitable = true },
        new ProviderBinding(ReloadTransferredBinding, ReloadTransferredCapability, ReloadHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(StackChangedBinding, StackChangedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(PickedUpBinding, PickedUpCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(DroppedBinding, DroppedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(UseStartedBinding, UseStartedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(UseCompletedBinding, UseCompletedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }) { Waitable = true },
        new ProviderBinding(UseFailedBinding, UseFailedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(CarriedItemChangedBinding, CarriedItemChangedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission }),
        new ProviderBinding(AmmoChangedBinding, AmmoChangedCapability, InventoryHandler, "observe", new[] { AmmunitionReadPermission })
    };
}
