using System;
using ForgeEngine.Framework;
using LevelGeneration;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeWeapon.Native;

/// <summary>The inventory actions this package executes for `gtfo.player`, submitted through the game's own
/// replicated pickup and removal paths rather than backpack field writes. A give creates the requested block's
/// native Pickup instance and issues its `LG_PickupItem_Sync.AttemptPickupInteraction`; the resulting native
/// state change owns the valid item replicator, inventory packet, slot/stack mutation and world-item lifetime.
/// A removal uses `TryMasterRemovePocketItemWithID` or `MasterRemoveItem`.
///
/// What each catalog row can honestly become is decided by that evidence, not by the catalog text:
///
/// - `forge.action.inventory.give` — the pickup state change reaches `MasterAddItem(Item, PlayerAgent)`, whose
///   private `AddItem` body branches on the item's own slot byte (decoded `AddItem` 0x174C7D0: `cmp al,7` at
///   0x174CBF9). Before that branch the body requires the pickup's real replicator (`IsValid` at 0x174CA01 and
///   `TryGetID` at 0x174CA19), which is why a fabricated `pItemData` with an empty reference is not a give:
///
///   - `InventorySlot.InPocket` (7) at 0x174D64B appends one `PocketItem` to the group the backpack keys by
///     `pItemData.itemID_gearCRC` (`PlayerBackpack.ItemIDToPocketItemGroup`) — the group
///     `PlayerBackpack.CountPocketItem` counts. That group is the game's own stack: a second add for an id the
///     backpack already holds joins the existing group and the slot table is never consulted, so `quantity` is
///     that native add repeated, exactly as the game's own pickups build a stack. There is no single-call
///     multi-unit path and `pItemData_Custom.ammo` is not stored on a pocket entry, so a charge count has no
///     destination here and a pocket request that asks for one is refused rather than silently dropped.
///   - Every other slot falls through to `PlayerBackpack.SpawnAndEquipItem(itemID, slot, data)` (0x1761280) and
///     then `backpack.AmmoStorage.SetAmmo(GetAmmoTypeFromSlot(slot), data.custom.ammo)` (0x174D0AD). One item is
///     one submission there — the slot holds one `BackpackItem` — so `quantity` has to be 1, and `custom.ammo`
///     is the charge count that lands in that slot's own ammo pool. The same body reads `PlayerBackpack.Slots`
///     (0x174CE36, `[backpack+0x28]` indexed by the slot byte) and, when that slot is occupied, calls
///     `DropItemFromBackpack` (0x174F000) on the occupant before it spawns the new one; the store it then
///     reaches clears the slot first (`evidence/w1-native-hooks.json`). This adapter refuses an occupied slot as
///     `slot-occupied` instead of inheriting a world drop the plan never asked for, and the slots it does not
///     place items in at all — the three gear slots, the carry slot and the pickup slot — are refused as
///     `item-slot-unsupported`.
///
///   The item itself has to come from an `item` resource bound to the game's own persistent id; that binding is
///   the runtime resource registry (`RuntimeModule.ResourceProviders`), which does not exist yet, so it is an
///   injected resolver and an unbound environment refuses by name instead of guessing an id. The resolver answers
///   the block's own `inventorySlot` with the id, and no second resolver exists: the adapter is what decides which
///   slots each action may use.
/// - `forge.action.inventory.give` with a negative quantity removes pocket units through
///   `TryMasterRemovePocketItemWithID`, or one current consumable/pack through `MasterRemoveItem(Item, SNet_Player)`.
///   Both are native replication points. The latter resolves the instance from the addressed backpack now and
///   confirms its own slot became empty; it never accepts an earlier world instance as a removal target.
/// - `drop` — no row and no body: the node list carries no drop node, so the binding row was deleted at
///   integration (ruling 110.5) and the native body went with it (ruling 133.3). The game's own drop path is not
///   reachable from a plan, and an implementation nothing declares is not kept.
/// - `pickup`, `equip`, `transfer`, `stack_set` — refused with the missing capability named. See
///   evidence/inventory-actions.json for the entry point each one lacks.
///
/// Nothing here writes a backpack field directly: `PlayerBackpack.AddPocketItem` / `RemovePocketItem` are local
/// list edits with no packet in their bodies, so a host that used them would desync every client.</summary>
internal sealed class InventoryActionAdapter
{

    /// <summary>The one item id and slot the resolver answers with: the game's own `ItemDataBlock.persistentID`,
    /// which is the number the game itself carries in `pItemData.itemID_gearCRC` for a non-gear item, and the
    /// block's own `ItemDataBlock.inventorySlot`, which is the byte `MasterAddItem` places the item by. Both
    /// values come from the one block lookup, so a slot cannot disagree with the id it belongs to.</summary>
    internal delegate bool ItemResolver(string resourceId, out uint itemId, out InventorySlot slot);

    /// <summary>What the runtime resource registry must answer for one `forge.resource.item` reference: the
    /// native item id and the slot its own block declares, or false when this environment cannot bind that
    /// resource. The signature is batch C's (`RuntimeModule.ResourceProviders` → `Resolve(resourceId)`), narrowed
    /// to the two values an item action needs, so the integration only has to forward its own answer.</summary>
    private readonly ItemResolver _resolveItem;

    /// <summary>The native object a `gtfo.player` reference names, or null when this environment cannot name one.
    /// The kernel's entity tables are one-way — a native object is mapped to the reference its owner allocated,
    /// never back — so this half is the missing direction, and the runtime's own entry point for it is batch C's
    /// `RuntimeKernel.ResolveEntity(object)` plus its object-resolver list. Until that exists this is an injected
    /// lookup: an action that cannot name the player behind a reference refuses the recipient by name instead of
    /// guessing one from a slot, a name or an agent.</summary>
    internal delegate object? PlayerLookup(EntityReference reference);

    /// <summary>The other direction: the player life a native player is recorded under, or null — the check that a
    /// backpack's owner is the very player the reference named.</summary>
    internal delegate EntityReference? OwnerLookup(SNet_Player player);

    private readonly PlayerLookup _playerOf;
    private readonly OwnerLookup _referenceOf;

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;

    /// <summary>What one `give` call may hand out or take away. The native pocket add has no cap of its own — it
    /// appends one entry per call and refuses nothing — and the native removal takes one unit per call, so this is
    /// the adapter's own bound on the host work one recipient may cost in either direction; a larger request is
    /// refused rather than clamped to a number nobody asked for. A slotted item is one submission, so the bound only
    /// ever applies to the pocket path.</summary>
    internal const int MaximumQuantity = 64;
    /// <summary>The largest charge count a `pItemData` can carry: `custom.ammo` is a float, and a value beyond
    /// this is not a charge count any weapon or consumable in this build uses.</summary>
    internal const float MaximumCharges = 9999f;
    /// <summary>The charge count a request that names none carries, and the only one a pocket item can honour:
    /// the pocket branch stores no `custom.ammo` at all, so a pocket request whose charge count differs from this
    /// default is refused rather than accepted and silently dropped.</summary>
    internal const int DefaultCharges = 1;

    internal InventoryActionAdapter(IForgeEngine kernel, Func<bool> canExecute, ItemResolver resolveItem,
        PlayerLookup playerOf, OwnerLookup referenceOf, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _resolveItem = resolveItem ?? throw new ArgumentNullException(nameof(resolveItem));
        _playerOf = playerOf ?? throw new ArgumentNullException(nameof(playerOf));
        _referenceOf = referenceOf ?? throw new ArgumentNullException(nameof(referenceOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>What one recipient's submission did. `Fact` is the number of items or units the submission really
    /// moved: a confirmed write reports it, a refusal that changed nothing reports zero, and an unconfirmable commit
    /// reports no number at all rather than a guess.</summary>
    internal readonly record struct GiveOutcome(Status Status, Commit Commit, string Code, int Fact);

    /// <summary>The request every recipient's step of one `give` shares, decided whole before the first recipient.</summary>
    private struct GiveRequest
    {
        internal InventoryActionAdapter Adapter;
        internal uint ItemId;
        internal InventorySlot Slot;
        internal int Quantity, Charges;
        internal bool Remove;
    }

    internal CommandResult HandleGive(CommandContext context) => HandleGive(this, context);

    /// <summary>The `forge.action.inventory.give` command handler: one item resource, one signed quantity, one
    /// charge count, and the recipients the runtime already validated as `gtfo.player`. A negative quantity takes
    /// that many units away through the game's own pocket removal instead (<see cref="Consume"/>,
    /// <see cref="ConsumeSlotted"/>).
    ///
    /// The whole request check runs before the first recipient, because a command that cannot be carried out as
    /// asked must not half-apply. The quantity bound is the adapter's own loop bound, since the native pocket add
    /// has no cap; the item id and its own slot are resolved once, so a resource this environment cannot bind
    /// refuses every recipient with the same code instead of adding a different item per recipient. The item's own
    /// `inventorySlot` then decides the rest of the request: a slot this action does not place items in is refused
    /// whole, a pocket item may not ask for a charge count the pocket branch cannot store, and a slotted item is
    /// one `BackpackItem` so its quantity has to be 1. A removal takes pocket units by id, or the one consumable or
    /// pack a slot holds; charge counts are not item quantities, so a removal names none.
    ///
    /// Every recipient is then handled on its own through the action skeleton, whatever the recipients before it
    /// answered.</summary>
    internal static CommandResult HandleGive(InventoryActionAdapter adapter, CommandContext context)
    {
        if (!adapter._canExecute() || !Session.IsHost || !adapter._kernel.Lifecycle.IsReadyHost)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!TryInteger(context.Integer(InventoryActionContract.GiveQuantitySlot), -MaximumQuantity, MaximumQuantity,
                1, out int quantity) || quantity == 0)
            return Outcome.Rejected(ResultCode.QuantityOutOfRange);
        var remove = quantity < 0;
        if (remove) quantity = -quantity;
        if (!TryInteger(context.Integer(InventoryActionContract.GiveChargesSlot), DefaultCharges,
                remove ? DefaultCharges : (int)MaximumCharges, DefaultCharges, out int charges))
            return Outcome.Rejected(ResultCode.ChargesOutOfRange);
        string? resourceId = ResourceId(context);
        if (resourceId == null) return Outcome.Rejected(ResultCode.ItemResourceUnresolved);
        if (!adapter._resolveItem(resourceId, out uint itemId, out var slot)) return Outcome.Rejected(ResultCode.ItemResourceUnresolved);
        if (!IsPlaceableSlot(slot)) return Outcome.Rejected(ResultCode.ItemSlotUnsupported);
        // The two request rules the item's own slot decides, both refused before any recipient: the pocket branch
        // stores no `custom.ammo`, so a charge count that differs from the port's default has no destination, and a
        // slotted item is one entry, so a multi-unit request cannot be honoured either way.
        if (slot == InventorySlot.InPocket)
        {
            if (charges != DefaultCharges) return Outcome.Rejected(ResultCode.ChargesOutOfRange);
        }
        else if (quantity != 1)
        {
            return Outcome.Rejected(ResultCode.QuantityOutOfRange);
        }

        var request = new GiveRequest
        {
            Adapter = adapter,
            ItemId = itemId,
            Slot = slot,
            Quantity = quantity,
            Charges = charges,
            Remove = remove
        };
        return context.EachTarget(ref request, GiveStep, EntityKinds.Player, CommandResult.MaximumFacts,
            ResultCode.NativeCommitException, doneWhenSucceeded: true, countColumn: Blocks.ActionInventoryGive.RowTargetCount);
    }

    /// <summary>One recipient's row: the command's target count and the signed number of items this recipient really
    /// gained, negative for a removal. The amount is the number the row can stand behind: a confirmed commit reports
    /// what really moved, a refusal that provably changed nothing reports zero, and a commit nobody could read back
    /// reports null with the row's own `unknown` state — a number there would claim an amount no read observed.</summary>
    private static readonly TargetStep<GiveRequest> GiveStep = static (ref GiveRequest request, EntityReference recipient, int index, RowWriter rows) =>
    {
        var adapter = request.Adapter;
        var outcome = !request.Remove ? adapter.Give(recipient, request.ItemId, request.Slot, request.Quantity, request.Charges)
            : request.Slot == InventorySlot.InPocket ? adapter.Consume(recipient, request.ItemId, request.Quantity)
            : adapter.ConsumeSlotted(recipient, request.ItemId, request.Slot);
        if (outcome.Commit == Commit.Confirmed) rows.Integer(Blocks.ActionInventoryGive.RowQuantity, request.Remove ? -outcome.Fact : outcome.Fact);
        else if (outcome.Commit == Commit.Unknown) rows.Null(Blocks.ActionInventoryGive.RowQuantity);
        else rows.Integer(Blocks.ActionInventoryGive.RowQuantity, 0);
        rows.End(outcome.Status, outcome.Commit, outcome.Code);
    };

    /// <summary>The item resource's own id, or null when the port is absent or carries something that is not a
    /// resource frame. The item id is never derived from a display name or an index: only the resource the plan
    /// bound can name one, and an environment that cannot resolve that resource refuses the whole command. The
    /// frame is read here rather than through a helper because the runtime's own resource reader is part of the
    /// batch that adds the registry, and this action must compile against the framework as it stands.</summary>
    private static string? ResourceId(CommandContext context)
    {
        if (!context.TryResource(InventoryActionContract.GiveItemSlot, out var item)
            || item.ResourceKind != ItemResourceKind) return null;
        return item.ResourceId.Length == 0 ? null : item.ResourceId;
    }

    /// <summary>Adds the resolved item through the game's own replicated pickup transition, which reaches the
    /// backpack branch selected by the item's own slot. The caller has already checked that this action places
    /// items in that slot, that a pocket item asked for no charge count and that a slotted item asked for one unit,
    /// so the remaining decisions come from the native pickup prerequisites and the recipient's readback.</summary>
    internal GiveOutcome Give(EntityReference recipient, uint itemId, InventorySlot slot, int quantity, int charges)
        => slot == InventorySlot.InPocket
            ? GivePocket(recipient, itemId, quantity, charges)
            : GiveSlotted(recipient, itemId, slot, charges);

    /// <summary>Adds <paramref name="quantity"/> units of one pocket item through the game's own replicated pickup
    /// transition. The unit count is read before the first submission and after the last, and the units that really
    /// landed are the difference the same backpack reports: the game's pocket branch appends one entry per call to
    /// the group keyed by the item id, so a backpack that already holds the id joins that group rather than being
    /// cleared or refused, and a request the game keeps only part of is reported as partial with what it kept.
    /// A submission the count cannot confirm stays unknown rather than being reported as a success or a refusal,
    /// because the game's own body sends its packet before it applies the change.</summary>
    private GiveOutcome GivePocket(EntityReference recipient, uint itemId, int quantity, int charges)
    {
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(refusal);
        // The lookup proved both halves, so the owner is the player the reference resolved to.
        SNet_Player owner = backpack!.Owner!;
        int before;
        try { before = backpack!.CountPocketItem(itemId); }
        catch (Exception error) { return ReadFailure(recipient, error); }
        int submitted = 0;
        for (int index = 0; index < quantity; index++)
        {
            if (!_canExecute() || !Session.IsHost || !_kernel.Lifecycle.IsReadyHost)
                return submitted == 0 ? Refuse(ResultCode.AuthorityOrPhase) : Unknown(ResultCode.AuthorityOrPhase, submitted);
            try
            {
                if (!TryPickup(owner, itemId, InventorySlot.InPocket, charges, backpack))
                    return submitted == 0 ? Refuse(ResultCode.ItemReplicatorUnavailable) : Unknown(ResultCode.ItemReplicatorUnavailable, submitted);
            }
            catch (Exception error)
            {
                // The pickup transition may have submitted its replicated state before the exception. It is not
                // retried or despawned after that boundary: either could duplicate or erase a real commit.
                _report("weapon.give-commit-exception: " + error.GetType().Name + ": " + error.Message);
                return new GiveOutcome(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, submitted);
            }
            submitted++;
        }
        var after = ReadCount(recipient, itemId);
        if (after == null) return Unknown(ResultCode.CommitUnreadBack, submitted);
        int landed = after.Value - before;
        if (landed <= 0 || landed > submitted) return Unknown(ResultCode.StateChangedBeforeCommit, submitted);
        if (landed < quantity)
            return new GiveOutcome(Status.Partial, Commit.Confirmed, ResultCode.StateChangedBeforeCommit, landed);
        return Confirmed(landed);
    }

    /// <summary>Adds one unit of a consumable or resource pack into the slot the item's own block declares,
    /// through the same replicated pickup transition a world pickup uses. Its `AddItem` body reaches
    /// `PlayerBackpack.SpawnAndEquipItem` for that byte and then writes `custom.ammo` into the slot's own ammo pool
    /// through `PlayerAmmoStorage.SetAmmo`, which is where a charge count lands.
    ///
    /// The slot is read before the submission and refused when it already holds an item, because the game's own
    /// body drops that occupant into the world first — a side effect no `give` asked for. After the submission the
    /// same slot, live instance and requested charges are read back. A missing or mismatched charge pool is
    /// unknown, not a confirmed grant. Cross-tick settlement still needs a host-command deferral contract;
    /// this synchronous result proves only the native state read at the end of the request.</summary>
    private GiveOutcome GiveSlotted(EntityReference recipient, uint itemId, InventorySlot slot, int charges)
    {
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(refusal);
        SNet_Player owner = backpack!.Owner!;
        uint? occupant;
        try { occupant = SlotItemId(backpack!, slot); }
        catch (Exception error) { return ReadFailure(recipient, error); }
        // An occupied slot and a slot table that cannot answer both leave the current occupant unproven, and the
        // native body would drop whatever is there: the add is refused rather than written blindly.
        if (occupant == null || occupant.Value != 0u) return Refuse(ResultCode.SlotOccupied);
        if (!_canExecute() || !Session.IsHost || !_kernel.Lifecycle.IsReadyHost)
            return Refuse(ResultCode.AuthorityOrPhase);
        try
        {
            if (!TryPickup(owner, itemId, slot, charges, backpack)) return Refuse(ResultCode.ItemReplicatorUnavailable);
        }
        catch (Exception error)
        {
            _report("weapon.give-commit-exception: " + error.GetType().Name + ": " + error.Message);
            return new GiveOutcome(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, 0);
        }
        try
        {
            var committed = SlotItemId(backpack!, slot);
            if (committed == null) return Unknown(ResultCode.CommitUnreadBack, 0);
            if (committed.Value != itemId) return Unknown(ResultCode.StateChangedBeforeCommit, 0);
            var instance = backpack!.Slots[(int)slot]?.Instance;
            var pool = backpack!.AmmoStorage?.GetInventorySlotAmmo(slot);
            if (instance == null || instance.WasCollected || instance.ItemDataBlock?.persistentID != itemId
                || pool == null || !float.IsFinite(pool.AmmoInPack))
                return Unknown(ResultCode.CommitUnreadBack, 0);
            if (pool.AmmoInPack != charges) return Unknown(ResultCode.StateChangedBeforeCommit, 0);
            return Confirmed(1);
        }
        catch (Exception failure) { NativeCallFailure.Note("InventoryActions.GiveSlotted", failure); return Unknown(ResultCode.CommitUnreadBack, 0); }
    }

    /// <summary>Creates one real Pickup instance and submits its own synced pickup interaction. This is the native
    /// lifecycle which supplies the valid replicator `AddItem` requires before it even inspects the slot, hides the
    /// world pickup, creates the held representation when the slot has one, and preserves the same replicator for a
    /// later drop. A block without a Pickup prefab is not an item this action can give. The replication manager's
    /// return slot is the exact instance this spawn made; no global search or same-id fallback is used.</summary>
    private bool TryPickup(SNet_Player owner, uint itemId, InventorySlot slot, int charges, PlayerBackpack backpack)
    {
        var agent = owner.PlayerAgent?.TryCast<PlayerAgent>();
        var node = agent?.CourseNode;
        if (agent == null || node == null || node.WasCollected) return false;
        var custom = new pItemData_Custom { ammo = charges, byteId = 0, byteState = 0 };
        var data = new pItemData { custom = custom, itemID_gearCRC = itemId, slot = slot };
        var created = ForgeCore.Items.Spawn(itemId, slot, custom, ItemMode.Pickup, agent.Position, UnityEngine.Quaternion.identity, node, out var why);
        if (why is ForgeCore.ItemSpawnRefusal.NotReady or ForgeCore.ItemSpawnRefusal.ModeMissing) return false;
        if (created == null)
            throw new InvalidOperationException("item-spawn-result-unavailable"); var sync = created.Item?.TryCast<ItemInLevel>()?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null)
        {
            ForgeCore.Items.Despawn(created.Cast<IReplicator>());
            // DeSpawn is a void submission, not a readback. Even when it returned, the spawn happened and the
            // command cannot claim a no-commit refusal; the caller reports the result as unknown.
            throw new InvalidOperationException("spawned-pickup-sync-unavailable");
        }
        LogPickupBoundary(sync, backpack, itemId, slot, data.custom, "spawned");
        // Pickup reads the replicator's current custom state, not SpawnItem's data or its own custom argument.
        // Commit through the native replicated state transition before asking that same pickup to enter the pack.
        sync.SetCustomData(data.custom, true);
        LogPickupBoundary(sync, backpack, itemId, slot, data.custom, "custom-committed");
        var committed = sync.GetCustomData();
        if (committed.ammo != data.custom.ammo || committed.byteId != data.custom.byteId || committed.byteState != data.custom.byteState)
            throw new InvalidOperationException("spawned-pickup-custom-unconfirmed");
        sync.AttemptPickupInteraction(ePickupItemInteractionType.Pickup, owner);
        LogPickupBoundary(sync, backpack, itemId, slot, data.custom, "pickup-after");
        return true;
    }

    private void LogPickupBoundary(LG_PickupItem_Sync sync, PlayerBackpack backpack, uint itemId, InventorySlot slot,
        pItemData_Custom requested, string stage)
    {
        var session = WeaponNativeSession.Offer(WeaponFeature.Inventory);
        if (session == null || !ReferenceEquals(session.Inventory, this) || !session.Log.Logs(RuntimeLogLevel.Trace)) return;
        try
        {
            var custom = sync.GetCustomData();
            var held = backpack.Slots[(int)slot];
            var pool = backpack.AmmoStorage?.GetInventorySlotAmmo(slot);
            session.Log.Write(RuntimeLogLevel.Trace, "weapon.supply-trace", new RuntimeLogFacts
            {
                Values = new[]
                {
                    RuntimeLogValue.Of("phase", "inventory-pickup-boundary"), RuntimeLogValue.Of("stage", stage),
                    RuntimeLogValue.Of("itemId", (long)itemId), RuntimeLogValue.Of("slot", (long)slot),
                    RuntimeLogValue.Of("requestedCharges", (double)requested.ammo), RuntimeLogValue.Of("customAmmo", (double)custom.ammo),
                    RuntimeLogValue.Of("customByteId", (long)custom.byteId), RuntimeLogValue.Of("customByteState", (long)custom.byteState),
                    RuntimeLogValue.Of("slotItem", (long)(held?.ItemID ?? 0u)), RuntimeLogValue.Of("instance", held?.Instance != null),
                    RuntimeLogValue.Of("poolReadable", pool != null), RuntimeLogValue.Of("ammoInPack",
                        pool?.AmmoInPack.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable"),
                    RuntimeLogValue.Of("wieldedSlot", (long)(backpack.Owner?.PlayerAgent?.TryCast<PlayerAgent>()?.Inventory?.WieldedSlot ?? InventorySlot.None))
                }
            });
        }
        catch (Exception failure)
        {
            session.Log.Text(RuntimeLogLevel.Trace, "weapon.supply-trace",
                "phase=inventory-pickup-boundary stage=" + stage + " read=unavailable " + failure.GetType().Name + ": " + failure.Message);
        }
    }

    /// <summary>What one slot of the backpack's own slot table holds, or null when the table cannot answer for
    /// that slot at all — no table, or a table shorter than the slot the item's block declares. A slot that is
    /// there and empty answers zero, which is a real answer: it is what "nothing is in this slot" looks like. The
    /// item's own number is read from the entry, which is the `itemID_gearCRC` the add was asked for.</summary>
    private static uint? SlotItemId(PlayerBackpack backpack, InventorySlot slot)
    {
        var slots = backpack.Slots;
        int index = (int)slot;
        if (slots == null || index < 0 || index >= slots.Length) return null;
        var item = slots[index];
        return item == null ? 0u : item.ItemID;
    }

    /// <summary>Spends <paramref name="count"/> units of one pocket item through the game's own host removal
    /// entry point. The count is read before the first call and after every call, so the units a run actually
    /// removed are reported even when a later call finds nothing left.</summary>
    internal GiveOutcome Consume(EntityReference recipient, uint itemId, int count)
    {
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(refusal);
        // The lookup proved both halves, so the owner is the player the reference resolved to.
        SNet_Player owner = backpack!.Owner!;
        int before;
        try { before = backpack!.CountPocketItem(itemId); }
        catch (Exception error) { return ReadFailure(recipient, error); }
        if (before <= 0) return Refuse(ResultCode.InventoryInsufficient);
        int spent = 0;
        for (int index = 0; index < count; index++)
        {
            if (!_canExecute() || !Session.IsHost || !_kernel.Lifecycle.IsReadyHost)
                return spent == 0 ? Refuse(ResultCode.AuthorityOrPhase) : Unknown(ResultCode.AuthorityOrPhase, spent);
            bool removed;
            try { removed = PlayerBackpackManager.TryMasterRemovePocketItemWithID(itemId, owner); }
            catch (Exception error)
            {
                _report("weapon.consume-commit-exception: " + error.GetType().Name);
                return new GiveOutcome(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, spent);
            }
            if (!removed) break;
            spent++;
        }
        if (spent == 0) return Refuse(ResultCode.StateChangedBeforeCommit);
        var after = ReadCount(recipient, itemId);
        if (after == null || after.Value != before - spent) return Unknown(ResultCode.CommitUnreadBack, spent);
        if (spent < count)
            return new GiveOutcome(Status.Partial, Commit.Confirmed, ResultCode.InventoryInsufficient, spent);
        return Confirmed(spent);
    }

    /// <summary>Remove this recipient's current consumable or pack through the replicated native removal.
    /// The live slot and Item instance must both name the requested id; never remove a replacement or a stale
    /// world instance. This removes one item, not a number of uses from its ammunition pool.</summary>
    internal GiveOutcome ConsumeSlotted(EntityReference recipient, uint itemId, InventorySlot slot)
    {
        if (slot == InventorySlot.InPocket || !IsPlaceableSlot(slot)) return Refuse(ResultCode.ItemSlotUnsupported);
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(refusal);
        Item? instance;
        try
        {
            var slots = backpack!.Slots;
            var index = (int)slot;
            if (slots == null || index >= slots.Length) return Refuse(ResultCode.CommitUnreadBack);
            var held = slots[index];
            if (held == null || held.ItemID != itemId) return Refuse(ResultCode.InventoryInsufficient);
            instance = held.Instance;
            if (instance == null || instance.ItemDataBlock?.persistentID != itemId)
                return Refuse(ResultCode.StateChangedBeforeCommit);
        }
        catch (Exception error) { return ReadFailure(recipient, error); }
        if (!_canExecute() || !Session.IsHost || !_kernel.Lifecycle.IsReadyHost) return Refuse(ResultCode.AuthorityOrPhase);
        try { PlayerBackpackManager.MasterRemoveItem(instance, backpack!.Owner!); }
        catch (Exception error)
        {
            _report("weapon.consume-commit-exception: " + error.GetType().Name);
            return new GiveOutcome(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, 0);
        }
        try
        {
            var remaining = SlotItemId(backpack!, slot);
            return remaining == 0 ? Confirmed(1) : Unknown(ResultCode.CommitUnreadBack, 0);
        }
        catch (Exception failure) { NativeCallFailure.Note("InventoryActions.ConsumeSlotted", failure); return Unknown(ResultCode.CommitUnreadBack, 0); }
    }

    /// <summary>The recipient's own backpack, through the game's own lookup. The reference is asked for the native
    /// player it names, the game's own backpack lookup is asked for that player's backpack, and the backpack's
    /// owner has to resolve back to the very reference that was asked about. A name, a slot or an agent is never
    /// used to find a player: a reference this environment cannot name is refused, not guessed at.</summary>
    private bool TryBackpack(EntityReference recipient, out PlayerBackpack? backpack, out string code)
    {
        backpack = null;
        if (recipient == null || !EntityKinds.Is(recipient, EntityKinds.Player)) { code = ResultCode.UnsupportedRecipient; return false; }
        if (!_kernel.IsEntityCurrent(recipient)) { code = ResultCode.StaleEntity; return false; }
        object? context;
        try { context = _playerOf(recipient); }
        catch (Exception failure) { NativeCallFailure.Note("InventoryActions.TryBackpack", failure); code = ResultCode.StaleEntity; return false; }
        if (context is not SNet_Player player) { code = ResultCode.StaleEntity; return false; }
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(player, out var found) || found == null)
            { code = ResultCode.TargetHasNoBackpack; return false; }
            // The owner has to be the same player the reference resolved to; a backpack that answers for another
            // owner is a disagreement this action refuses to write through.
            var owner = found.Owner;
            if (owner == null || _referenceOf(owner) != recipient)
            { code = ResultCode.ReceiverChangedDuringCommit; return false; }
            backpack = found;
            code = "";
            return true;
        }
        catch (Exception failure) { NativeCallFailure.Note("InventoryActions.TryBackpack#2", failure); code = ResultCode.StaleEntity; return false; }
    }

    /// <summary>One item id's pocket count after a write, or null when the count could not be read at all. The
    /// game's own count is the group's size, and a backpack holding none of the id answers zero, which is a real
    /// answer: it is what "the item is not there" looks like.</summary>
    private int? ReadCount(EntityReference recipient, uint itemId)
    {
        if (!TryBackpack(recipient, out var backpack, out _)) return null;
        try { return backpack!.CountPocketItem(itemId); }
        catch (Exception failure) { NativeCallFailure.Note("InventoryActions.ReadCount", failure); return null; }
    }

    private GiveOutcome ReadFailure(EntityReference recipient, Exception error)
    {
        _report("weapon.inventory-read-exception: " + error.GetType().Name);
        return new GiveOutcome(Status.Failed, Commit.Unknown, ResultCode.CommitUnreadBack, 0);
    }

    private static GiveOutcome Confirmed(int fact)
        => new(Status.Succeeded, Commit.Confirmed, ResultCode.Committed, fact);

    private static GiveOutcome Refuse(string code)
        => new(Status.Rejected, Commit.None, code, 0);

    private static GiveOutcome Unknown(string code, int fact)
        => new(Status.Failed, Commit.Unknown, code, fact);

    /// <summary>The one resource kind the `item` port carries; another kind on that port is not an item request.</summary>
    internal const string ItemResourceKind = "item";

    /// <summary>The slots `give` places an item in, spelled as the game spells them. The pocket slot is the game's
    /// own stack, and the consumable pair and the resource pack are filled by the add body's
    /// `SpawnAndEquipItem` branch, which is also where `custom.ammo` lands as that slot's own charge count
    /// (`InventorySlot` members and the branch byte are decoded in evidence/inventory-actions.json). The three
    /// gear slots, the in-level carry slot, the pickup slot and the empty slot are not placeable here: their own
    /// paths need a spawned `Item` instance or a world pickup this package does not own.</summary>
    internal static bool IsPlaceableSlot(InventorySlot slot)
        => slot is InventorySlot.InPocket or InventorySlot.Consumable or InventorySlot.ConsumableHeavy
            or InventorySlot.ResourcePack;

    /// <summary>A value input that is either absent — the plan asked for <paramref name="fallback"/> — or an
    /// integer inside the bound. A non-numeric or out-of-range value is a refusal, never a clamp.</summary>
    private static bool TryInteger(long? declared, int minimum, int maximum, int fallback, out int value)
    {
        value = fallback;
        if (declared is not { } parsed) return true;
        if (parsed < minimum || parsed > maximum) return false;
        value = (int)parsed;
        return true;
    }
}
