using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The four equipment slots the action can change, named by the category of gear that lives in each. The
/// hacking tool is deliberately not one of them: it is a fixed carried tool with its own path.</summary>
internal enum GearSlot { Standard, Special, Melee, Tool }

/// <summary>How this machine stands to the player a step names.</summary>
internal enum GearRecipientState { Local, NotAddressedHere, Stale, NotPlayer, NoBackpack }

/// <summary>How one authored or vanilla gear reference reads against the game's own gear blocks.</summary>
internal enum GearResolution { Found, Unresolved, SlotUnsupported }

/// <summary>One gear the game can be asked to equip: the slot it belongs in, the persistent id of its block when
/// the game spells one, and the native object the port will hand back to the game. <see cref="Gear"/> is opaque to
/// this file — it is whatever the port needs to make the call.</summary>
internal readonly record struct GearTarget(GearSlot Slot, long? GearId, object Gear);

/// <summary>What one slot of the recipient's backpack shows right now. `Readable` is false when the backpack cannot
/// answer for the slot at all; an empty slot is readable and shows no gear.</summary>
internal readonly record struct GearSlotReading(bool Readable, long? GearId, int? Clip, double? Reserve);

/// <summary>The game half of the action: every call that touches a game type. The action itself is the same code in
/// the game and in the offline suite, and only the port differs.</summary>
internal interface IGearEquipPort
{
    /// <summary>Whether this machine may write now: a running game whose owner-tier writes are open.</summary>
    bool CanWrite { get; }

    GearRecipientState Locate(EntityReference recipient);

    /// <summary>The gear block one `weapon` or `tool` resource names, with the slot its base item declares.</summary>
    GearResolution Resolve(string resourceKind, string resourceId, out GearTarget target);

    /// <summary>The gear this recipient's slot held before the first write this level made to it. False when no
    /// write of this action has replaced that slot's gear.</summary>
    bool TryDeparture(EntityReference recipient, GearSlot slot, out GearTarget target);

    GearSlotReading Read(EntityReference recipient, GearSlot slot);

    /// <summary>The gear this recipient's slot holds now, as a target the slot can be given back by the same request. False when the
    /// slot is empty or its gear cannot be read; nothing is written.</summary>
    bool TryHeld(EntityReference recipient, GearSlot slot, out GearTarget target);

    /// <summary>The equipment life the game's item in this recipient's slot is recorded under right now; null when the slot is empty
    /// or its item has no recorded life. Nothing is written.</summary>
    EntityReference? HeldEquipment(EntityReference recipient, GearSlot slot);

    /// <summary>Makes the target's assembly assets ready through the game's own shard loader. The first call for a gear that
    /// lacks them starts the load and answers `Loading`; later calls read it until `Ready`, or `Failed` when the load
    /// itself failed. Idempotent: it writes nothing to the player.</summary>
    GearAssets PrepareAssets(GearTarget target);

    /// <summary>Keeps the gear the slot holds as its departure gear, once per slot and level. Called before the
    /// first write to the slot; a slot that already has a kept gear is left alone.</summary>
    void KeepDeparture(EntityReference recipient, GearSlot slot);

    /// <summary>Another mod holds this recipient's slot on loan (ForgeCore's special slot lease), so a write from here would fight it.
    /// Only the local player's special slot can be leased.</summary>
    bool SlotLeased(EntityReference recipient, GearSlot slot);

    /// <summary>The native request: the game's own `PlayerBackpackManager.EquipLocalGear`. Throws what the game threw.</summary>
    void Equip(EntityReference recipient, GearTarget target);
}

/// <summary>
/// The `forge.action.inventory.equip_gear` handler body, game-independent.
///
/// Order of decisions, each refusing before anything is written: this machine may write; the step names a player;
/// exactly one source is named; this machine is that player's; the gear (or the kept departure gear) resolves to a
/// slot this action changes; the slot is readable; the slot does not already show the gear. Then the gear's assembly
/// assets are made ready through the game's own shard loader (a gear the level did not start with has none loaded, and
/// the game's assembly throws on a missing one), the departure gear of the slot is kept, the game is asked once, and the
/// slot is read back. A load that fails or does not finish in <see cref="GearEquipContract.AssetSeconds"/> ends the row
/// before anything is written.
///
/// The game assembles gear asynchronously, so the read right after the call usually still shows the old gear. The row
/// is therefore settled later, through the kernel's owner deferral: a slot that shows the requested gear is
/// `succeeded/confirmed`; when <see cref="GearEquipContract.ConfirmSeconds"/> pass without the slot showing it the row is
/// `failed/unknown` with <see cref="ResultCode.EquipGearUnconfirmed"/>. The request is real, its outcome is not proven,
/// and nothing here claims more. The row carries the slot as it read, so a plan never reads a number the request merely
/// asked for.
/// </summary>
internal sealed class GearEquipAction
{
    private readonly IGearEquipPort _port;
    private readonly Action<string> _report;
    // The world each open (player, slot) request was made in. Entries are removed when the row settles; a request whose
    // world ended is dropped by the kernel unanswered and is ignored here by its world.
    private readonly Dictionary<(string Player, GearSlot Slot), long> _open = new();

    public GearEquipAction(IGearEquipPort port, Action<string> report)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public CommandResult Handle(CommandContext context)
    {
        if (!context.TryEntity(GearEquipContract.RecipientSlot, out var recipient))
            return Outcome.Rejected(ResultCode.NoRecipients);
        var rows = context.Rows;
        if (!_port.CanWrite) return Refuse(rows, recipient, ResultCode.AuthorityOrPhase, null);

        switch (_port.Locate(recipient))
        {
            case GearRecipientState.NotPlayer: return Refuse(rows, recipient, ResultCode.UnsupportedRecipient, null);
            case GearRecipientState.Stale: return Refuse(rows, recipient, ResultCode.StaleEntity, null);
            case GearRecipientState.NotAddressedHere: return Refuse(rows, recipient, ResultCode.OwnerNotAddressed, null);
            case GearRecipientState.NoBackpack: return Refuse(rows, recipient, ResultCode.TargetHasNoBackpack, null);
        }

        if (!TryTarget(context, recipient, out var target, out var refusal)) return Refuse(rows, recipient, refusal, null);
        var slot = target.Slot;

        var before = _port.Read(recipient, slot);
        if (!before.Readable) return Refuse(rows, recipient, ResultCode.EquipGearReadbackUnavailable, slot);
        if (target.GearId != null && before.GearId == target.GearId)
            return Refuse(rows, recipient, ResultCode.EquipGearAlreadyHeld, slot, before);

        if (_port.SlotLeased(recipient, slot)) return Refuse(rows, recipient, ResultCode.EquipGearSlotLeased, slot, before);

        var key = (recipient.Id, slot);
        // One open request per player and slot; a request of a world that has ended no longer holds the slot.
        if (_open.TryGetValue(key, out var openWorld) && openWorld == context.WorldEpoch)
            return Refuse(rows, recipient, ResultCode.EquipGearBusy, slot, before);

        // The window is counted in ticks of the running world. A world that never reported its rate is refused by
        // name before anything is written, instead of converting at a rate the world never declared.
        int loadTicks, confirmTicks;
        try { (loadTicks, confirmTicks) = Windows(context.TicksPerSecond); }
        catch (RuntimeContractException error) { return Refuse(rows, recipient, error.Code, slot, before); }

        if (!_port.CanWrite) return Refuse(rows, recipient, ResultCode.AuthorityOrPhase, slot, before);

        // The game assembles a gear from assets only the level's starting gear has loaded, and an assembly that finds one
        // missing throws inside its own spawn callback and leaves a half-built gun. So the assets come first, through the
        // game's own shard loader, and the vanilla request is sent only when they are there. A load that fails answers
        // before anything is written to the player.
        var assets = _port.PrepareAssets(target);
        if (assets.State == GearAssetState.Failed)
        {
            _report("weapon.equip-gear-assets-failed: " + assets.Detail);
            return Refuse(rows, recipient, ResultCode.EquipGearAssetsLoadFailed, slot, before);
        }
        if (assets.State == GearAssetState.Loading)
        {
            _open[key] = context.WorldEpoch;
            var confirmAfterLoad = Math.Min(confirmTicks, RuntimeKernel.MaximumOwnerDeferralTicks - loadTicks);
            try
            {
                return context.DeferOwnerResult(loadTicks + confirmAfterLoad,
                    new Pending(this, recipient, target, key, loadTicks, confirmAfterLoad, sentCall: -1), SettleLoading);
            }
            catch (RuntimeContractException)
            {
                // A context that cannot answer later still owes an honest row: the load was started and nothing is equipped.
                _open.Remove(key);
                return Refuse(rows, recipient, ResultCode.EquipGearAssetsLoading, slot, before);
            }
        }

        if (Write(rows, recipient, target) is { } thrown) return thrown;

        var after = _port.Read(recipient, slot);
        if (Shows(after, target.GearId))
            return Result(rows, Status.Succeeded, Commit.Confirmed, ResultCode.Committed, recipient, slot, after);

        // The game builds the gear after the request returns, so the row is not settled here: the holder's kernel asks
        // `Settle` once per advance while this request is open and sends the one result when the slot shows the gear
        // or the window ends. The host's graph continues through `next` only then.
        _open[key] = context.WorldEpoch;
        try { return context.DeferOwnerResult(confirmTicks, new Pending(this, recipient, target, key, 0, confirmTicks, sentCall: 0), SettleSent); }
        catch (RuntimeContractException)
        {
            // A context that cannot answer later still owes an honest row: the request was made and is not shown.
            _open.Remove(key);
            return Result(rows, Status.Failed, Commit.Unknown, ResultCode.EquipGearUnconfirmed, recipient, slot,
                after.Readable ? after : null);
        }
    }

    /// <summary>Keeps the slot's departure gear and sends the game's request once. Null when the call returned; the
    /// terminal row when the game threw.</summary>
    private Outcome? Write(RowWriter rows, EntityReference recipient, GearTarget target)
    {
        _port.KeepDeparture(recipient, target.Slot);
        try { _port.Equip(recipient, target); }
        catch (Exception error)
        {
            // Whether the game kept part of the request is not observable from here, so the commit stays unknown.
            _report("weapon.equip-gear-exception: " + error.GetType().Name);
            return Result(rows, Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, recipient, target.Slot, null);
        }
        return null;
    }

    /// <summary>One request the holder answers later: what its settlement reads, and how far it has got. A request whose
    /// assets load first counts its own looks (<see cref="Calls"/>) and the look it was sent on (<see cref="SentCall"/>,
    /// -1 until then); a request sent in the handler starts sent.</summary>
    private struct Pending
    {
        internal readonly GearEquipAction Action;
        internal readonly EntityReference Recipient;
        internal readonly GearTarget Target;
        internal readonly (string, GearSlot) Key;
        internal readonly int LoadTicks, ConfirmTicks;
        internal int Calls, SentCall;

        internal Pending(GearEquipAction action, EntityReference recipient, GearTarget target, (string, GearSlot) key,
            int loadTicks, int confirmTicks, int sentCall)
        {
            Action = action; Recipient = recipient; Target = target; Key = key; LoadTicks = loadTicks; ConfirmTicks = confirmTicks;
            Calls = 0; SentCall = sentCall;
        }
    }

    /// <summary>The settlement of a request sent in the handler: it ends when the slot shows the gear or the kernel's
    /// window ends.</summary>
    private static readonly OwnerSettlement<Pending> SettleSent = static (ref Pending pending, bool expired, RowWriter rows)
        => pending.Action.Settle(rows, pending.Recipient, pending.Target.Slot, pending.Target.GearId, pending.Key, expired);

    /// <summary>One look at a request whose assets are still loading. Before the request is sent nothing has been written, so
    /// every ending is `none`; once the assets are ready the vanilla request is sent exactly once and the open request
    /// settles like any other, with its confirmation window counted from the send.</summary>
    private static readonly OwnerSettlement<Pending> SettleLoading = static (ref Pending pending, bool expired, RowWriter rows)
        => pending.Action.Loading(ref pending, expired, rows);

    private Outcome? Loading(ref Pending pending, bool expired, RowWriter rows)
    {
        var recipient = pending.Recipient; var target = pending.Target; var key = pending.Key; var slot = target.Slot;
        pending.Calls++;
        if (pending.SentCall < 0)
        {
            var assets = _port.PrepareAssets(target);
            if (assets.State == GearAssetState.Failed)
            {
                _open.Remove(key);
                _report("weapon.equip-gear-assets-failed: " + assets.Detail);
                return Result(rows, Status.Failed, Commit.None, ResultCode.EquipGearAssetsLoadFailed, recipient, slot, ReadOrNull(recipient, slot));
            }
            if (assets.State == GearAssetState.Loading)
            {
                if (!expired && pending.Calls <= pending.LoadTicks) return null;
                _open.Remove(key);
                return Result(rows, Status.Failed, Commit.None, ResultCode.EquipGearAssetsLoadTimeout, recipient, slot, ReadOrNull(recipient, slot));
            }
            if (_port.Locate(recipient) != GearRecipientState.Local)
            {
                _open.Remove(key);
                return Result(rows, Status.Rejected, Commit.None, ResultCode.ReceiverChangedDuringCommit, recipient, slot, null);
            }
            if (!_port.CanWrite)
            {
                _open.Remove(key);
                return Result(rows, Status.Rejected, Commit.None, ResultCode.AuthorityOrPhase, recipient, slot, ReadOrNull(recipient, slot));
            }
            if (_port.SlotLeased(recipient, slot))
            {
                _open.Remove(key);
                return Result(rows, Status.Rejected, Commit.None, ResultCode.EquipGearSlotLeased, recipient, slot, ReadOrNull(recipient, slot));
            }
            if (Write(rows, recipient, target) is { } thrown) { _open.Remove(key); return thrown; }
            pending.SentCall = pending.Calls;
        }
        return Settle(rows, recipient, slot, target.GearId, key, expired || pending.Calls - pending.SentCall >= pending.ConfirmTicks);
    }

    private GearSlotReading? ReadOrNull(EntityReference recipient, GearSlot slot)
    {
        var reading = _port.Read(recipient, slot);
        return reading.Readable ? reading : null;
    }

    /// <summary>One look at an open request. A slot that shows the gear settles it as confirmed; before the window ends
    /// anything else keeps it open (null); at the end it settles as unconfirmed with what the slot showed, or as
    /// <see cref="ResultCode.ReceiverChangedDuringCommit"/> when the recipient is no longer this machine's player.</summary>
    private Outcome? Settle(RowWriter rows, EntityReference recipient, GearSlot slot, long? gearId, (string, GearSlot) key, bool expired)
    {
        var now = _port.Read(recipient, slot);
        if (Shows(now, gearId))
        {
            _open.Remove(key);
            return Result(rows, Status.Succeeded, Commit.Confirmed, ResultCode.Committed, recipient, slot, now);
        }
        if (!expired) return null;
        _open.Remove(key);
        if (_port.Locate(recipient) != GearRecipientState.Local)
            return Result(rows, Status.Failed, Commit.Unknown, ResultCode.ReceiverChangedDuringCommit, recipient, slot, null);
        return Result(rows, Status.Failed, Commit.Unknown, ResultCode.EquipGearUnconfirmed, recipient, slot,
            now.Readable ? now : null);
    }

    private static bool Shows(GearSlotReading reading, long? gearId) => reading.Readable && gearId != null && reading.GearId == gearId;

    /// <summary>The two windows of one request in ticks: how long the slot may take to show the gear after the request is sent,
    /// and how long the assets may take to load first (at most half of the kernel's deferral limit, so a request that has to
    /// load still keeps room to confirm).</summary>
    private static (int Load, int Confirm) Windows(double ticksPerSecond)
    {
        if (!(ticksPerSecond > 0) || double.IsInfinity(ticksPerSecond))
            throw new RuntimeContractException(ResultCode.RuntimeNotReady, "The world's tick rate is not usable.");
        return ((int)Math.Clamp(Math.Ceiling(GearEquipContract.AssetSeconds * ticksPerSecond), 1, RuntimeKernel.MaximumOwnerDeferralTicks / 2),
            (int)Math.Clamp(Math.Ceiling(GearEquipContract.ConfirmSeconds * ticksPerSecond), 1, RuntimeKernel.MaximumOwnerDeferralTicks));
    }

    /// <summary>The one source the step names, resolved to a gear the port can hand to the game. Exactly one of the
    /// `weapon` port, the `tool` port and the `departure_slot` parameter may be present.</summary>
    private bool TryTarget(CommandContext context, EntityReference recipient, out GearTarget target, out string refusal)
    {
        target = default;
        var weapon = context.Present(GearEquipContract.WeaponSlot);
        var tool = context.Present(GearEquipContract.ToolInputSlot);
        var departureName = context.Text(GearEquipContract.DepartureSlot);
        var departure = !string.IsNullOrEmpty(departureName);
        if ((weapon ? 1 : 0) + (tool ? 1 : 0) + (departure ? 1 : 0) != 1)
        { refusal = ResultCode.EquipGearSourceInvalid; return false; }

        if (departure)
        {
            var named = GearEquipContract.SlotOf(departureName);
            if (named == null) { refusal = ResultCode.EquipGearSourceInvalid; return false; }
            if (!_port.TryDeparture(recipient, named.Value, out target))
            {
                // Nothing of this action has replaced the slot, so the slot still holds what the level started with
                // as far as this action can tell; it writes nothing and says why.
                refusal = ResultCode.EquipGearDepartureNotReplaced;
                return false;
            }
            refusal = "";
            return true;
        }

        var kind = weapon ? GearEquipContract.WeaponKind : GearEquipContract.ToolKind;
        if (!context.TryResource(weapon ? GearEquipContract.WeaponSlot : GearEquipContract.ToolInputSlot, out var frame)
            || frame.ResourceKind != kind || string.IsNullOrEmpty(frame.ResourceId))
        { refusal = ResultCode.EquipGearSourceInvalid; return false; }
        switch (_port.Resolve(kind, frame.ResourceId, out target))
        {
            case GearResolution.Found: refusal = ""; return true;
            case GearResolution.SlotUnsupported: refusal = ResultCode.EquipGearSlotUnsupported; return false;
            default: refusal = ResultCode.EquipGearUnresolved; return false;
        }
    }

    private static Outcome Refuse(RowWriter rows, EntityReference recipient, string code, GearSlot? slot, GearSlotReading? reading = null)
        => Result(rows, Status.Rejected, Commit.None, code, recipient, slot, reading);

    /// <summary>The recipient's one row — the four fixed columns and what the slot showed when the action read it last;
    /// `slot`, `gear_id`, `clip` and `reserve` are the readback and are null when there was nothing to read — and the
    /// command's conclusion, which is that row's own.</summary>
    private static Outcome Result(RowWriter rows, Status status, Commit commit, string code, EntityReference recipient,
        GearSlot? slot, GearSlotReading? reading)
    {
        var row = rows.Begin(0, recipient);
        if (slot is { } named) row.Text(Blocks.ActionInventoryEquipGear.RowSlot, GearEquipContract.NameOf(named));
        else row.Null(Blocks.ActionInventoryEquipGear.RowSlot);
        if (reading?.GearId is { } gearId) row.Integer(Blocks.ActionInventoryEquipGear.RowGearId, gearId);
        else row.Null(Blocks.ActionInventoryEquipGear.RowGearId);
        if (reading?.Clip is { } clip) row.Integer(Blocks.ActionInventoryEquipGear.RowClip, clip);
        else row.Null(Blocks.ActionInventoryEquipGear.RowClip);
        if (reading?.Reserve is { } reserve) row.Number(Blocks.ActionInventoryEquipGear.RowReserve, reserve);
        else row.Null(Blocks.ActionInventoryEquipGear.RowReserve);
        row.End(status, commit, code);
        return new Outcome(status, commit, code);
    }
}
