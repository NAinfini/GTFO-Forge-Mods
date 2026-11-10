using System;
using System.Collections.Generic;
using Player;
using ForgeUI;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>What the local player is doing with the call key, for the display.</summary>
internal enum OrdnancePhase { Idle, Entering, Holding }

/// <summary>The per-frame work: network pump, host work, the local input state machine (idle, entering a code, holding the
/// ball, restoring the inventory) and the thrown balls being followed to their landing. A plain class driven by
/// <c>Plugin.Tick</c>; <see cref="OrdnanceHud"/> draws from the read-only state below.
/// The ball is a real consumable (<see cref="BeaconItem"/>) put into the consumable slot for the duration; the game's own
/// throwing weapon charges and throws it. What the slot held before is saved and put back afterwards.</summary>
internal sealed class OrdnanceController
{
    private enum Phase { Idle, Command, Holding, Restoring }

    private const float BallTimeout = 20f;
    /// <summary>How long the ball may take to reach the hand after it is placed.</summary>
    private const float WieldSeconds = 3f;
    /// <summary>How long after a throw the game may take to empty the consumable slot before the ball is cleared by force.</summary>
    private const float ClearSeconds = 3f;
    /// <summary>How long after a throw the game may take to report the thrown ball.</summary>
    private const float SpawnSeconds = 5f;
    /// <summary>The shake of a package landing without a container of its own.</summary>
    private static readonly ShakeSpec LandingShake = new(0.25f, 40f);
    private static readonly InputAction[] Switching =
    {
        InputAction.SelectStandard, InputAction.SelectSpecial, InputAction.SelectTool, InputAction.SelectMelee,
        InputAction.SelectResourcePack, InputAction.SelectConsumable, InputAction.SelectHackingTool
    };

    private readonly OrdnanceSession _session;
    private readonly List<Direction> _typed = new();
    private readonly List<ThrownBeacon> _thrown = new();
    private readonly List<OrdnanceBeacon> _beacons = new();
    private Phase _phase;
    private OrdnanceDefinition? _held;
    // The call-in whose ball has been thrown (or is about to be) and has not yet been reported by the game's item spawn.
    private OrdnanceDefinition? _awaiting;
    private float _awaitUntil;
    private ulong _throwToken;
    private Item? _matchedBall;
    // The consumable slot and wielded slot as they were before the ball was put in hand. _carrying is true from the moment
    // the ball is placed until the original state has been put back.
    private bool _carrying;
    private uint _savedItem;
    private float _savedAmmo;
    private InventorySlot _savedSlot;
    private bool _ballWielded;
    private float _carryStart;
    private int _emptyFrame;
    private float _restoreDeadline;
    private uint _throwCounter;
    private readonly record struct CarrySnapshot(uint Item, float Ammo, InventorySlot Slot);
    private CarrySnapshot? _checkpointCarry;
    private bool _checkpointRestoring;

    internal OrdnanceController(OrdnanceSession session)
    {
        _session = session;
        _session.Notice += text => Notify(text, true);
        _session.ThrowRejected += ThrowRejected;
        _session.BeaconStarted += BeaconStarted;
        _session.BeaconArrived += BeaconArrived;
        _session.BeaconFailed += RemoveBeacon;
        _session.PresentationReset += ClearPresentation;
        // Native checkpoint packets can contain the temporary ball. Keep the checkpoint-time original independently,
        // and apply it only after native recall has finished; post-checkpoint inventory must never replace it.
        ForgeCore.GameEvents.CheckpointReached += CheckpointReached;
        ForgeCore.GameEvents.CheckpointReloaded += CheckpointReloaded;
        ForgeCore.GameEvents.LevelEnded += DiscardInput;
        BeaconItem.Spawned += BallSpawned;
        BeaconItem.ThrowCallback = CaptureThrow;
    }

    internal OrdnancePhase CurrentPhase => _phase switch { Phase.Command => OrdnancePhase.Entering, Phase.Holding => OrdnancePhase.Holding, _ => OrdnancePhase.Idle };
    /// <summary>The code steps entered so far in this hold.</summary>
    internal IReadOnlyList<Direction> Entered => _typed;
    /// <summary>A call is being typed or held, or a thrown ball has not landed yet; the loadout must not change under it.</summary>
    internal bool CallInProgress => _phase != Phase.Idle || _awaiting != null || _thrown.Exists(ball => !ball.Reported);

    /// <summary>The call-in whose ball is in hand, while <see cref="CurrentPhase"/> is Holding.</summary>
    internal OrdnanceDefinition? Held => _held;
    /// <summary>A refusal or failure to tell the player; the call-in list shows it for its own time (see <see cref="OrdnanceHud"/>).</summary>
    internal event Action<string>? NoticePosted;

    /// <summary>Leaving a level: stop any input hold and drop everything on screen. The backpack is rebuilt by the game, so
    /// nothing is restored.</summary>
    internal void LevelLeft()
    {
        DiscardInput();
        ClearPresentation();
    }

    internal void Update()
    {
        try
        {
            OrdnanceNetwork.Pump();
            _session.Tick();
            GearPayload.Tick(Time.time);
            if (!_session.InLevel)
            {
                if (_phase != Phase.Idle || _carrying) EndInput(false);
                return;
            }
            TickThrown();
            TickBeacons();
            TickInput();
            TickAwaiting();
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Ordnance update failed; input hold released: " + error);
            try { EndInput(true); } catch (Exception inner) { Plugin.Logger.ErrorOnce("Releasing input after a failure also failed: " + inner); }
        }
    }

    // ---- input ---------------------------------------------------------------------------------------------

    /// <summary>The local player while able to act (in first person and alive), else null.</summary>
    private static PlayerAgent? LiveAgent()
    {
        var agent = ForgeCore.Players.Local;
        return FocusStateManager.CurrentState == eFocusState.FPS && agent != null && agent.Alive ? agent : null;
    }

    private void TickInput()
    {
        var key = OrdnanceKeys.Summon;
        PlayerAgent? agent;
        switch (_phase)
        {
            case Phase.Idle:
                if (!key.Pressed || _session.CallMenuOff || OrdnanceApi.CallsBlocked) return;
                agent = LiveAgent();
                if (agent == null) return;
                if (!_session.Enabled) { Notify(OrdnanceText.T("notice.off", OrdnanceText.T(_session.DisabledReason ?? "reject.Other"))); return; }
                if (_session.LocalLoadout().Count == 0) { Notify(OrdnanceText.T("notice.emptyLoadout", OrdnanceText.T("lobby.button"))); return; }
                if (_awaiting != null) { Notify(OrdnanceText.T("notice.awaiting")); return; }
                if (!BeginCarry(agent)) return;
                _typed.Clear();
                _phase = Phase.Command;
                OrdnanceCues.Local(OrdnanceSound.CallOpen);
                HoldInput(InputAction.MoveHorizontal, InputAction.MoveVertical, InputAction.Fire, InputAction.Aim, InputAction.ScrollItems,
                    InputAction.SelectStandard, InputAction.SelectSpecial, InputAction.SelectTool, InputAction.SelectMelee,
                    InputAction.SelectResourcePack, InputAction.SelectConsumable, InputAction.SelectHackingTool);
                break;
            case Phase.Command:
                agent = LiveAgent();
                if (agent == null) { Cancel(); return; }
                if (OrdnanceApi.CallsBlocked) { Cancel(); return; }
                if (!key.Held) { Cancel(); return; }
                if (!BallInHand(agent, true)) { Cancel(); return; }
                if (ReadDirection() is { } step) Typed(step);
                break;
            case Phase.Holding:
                // Consumption can precede both native slot cleanup and the spawn callback (or a failed spawn).
                // Check it before switch/cancel input: the game may already have auto-wielded a weapon.
                if (BallConsumed()) { Thrown(); return; }
                if (ThrowWatch.Attempted && _awaitUntil == float.MaxValue) _awaitUntil = Time.time + SpawnSeconds;
                agent = LiveAgent();
                if (agent == null) { Cancel(); return; }
                if (OrdnanceApi.CallsBlocked) { Cancel(); return; }
                ThrowWatch.Tick(agent);
                if (key.Pressed) { Cancel(); return; }
                if (SwitchPressed()) { Cancel(); return; }
                if (!BallInHand(agent, false)) { Cancel(); return; }
                break;
            case Phase.Restoring:
                TickRestoring();
                break;
        }
    }

    private void Cancel()
    {
        OrdnanceCues.Local(OrdnanceSound.CallCancel);
        EndInput(true);
    }

    /// <summary>A code step: the player's own movement keys (read from the game's bindings; the game does not get them while a code is
    /// entered) or the code keys.</summary>
    private static Direction? ReadDirection()
    {
        if (OrdnanceKeys.Up.Pressed || UIKitKeys.GameKeyDown(InputAction.MoveVertical)) return Direction.Up;
        if (OrdnanceKeys.Down.Pressed || UIKitKeys.GameKeyDown(InputAction.MoveVertical, true)) return Direction.Down;
        if (OrdnanceKeys.Left.Pressed || UIKitKeys.GameKeyDown(InputAction.MoveHorizontal, true)) return Direction.Left;
        if (OrdnanceKeys.Right.Pressed || UIKitKeys.GameKeyDown(InputAction.MoveHorizontal)) return Direction.Right;
        return null;
    }

    private static OrdnanceSound KeySound(Direction step) => step switch
    {
        Direction.Up => OrdnanceSound.KeyUp,
        Direction.Down => OrdnanceSound.KeyDown,
        Direction.Left => OrdnanceSound.KeyLeft,
        _ => OrdnanceSound.KeyRight,
    };

    private static bool SwitchPressed()
    {
        foreach (var action in Switching) if (ForgeCore.InputLease.Down(action)) return true;
        return Math.Abs(ForgeCore.InputLease.Axis(InputAction.ScrollItems)) > 0.1f;
    }

    private void Typed(Direction step)
    {
        _typed.Add(step);
        var loadout = _session.LocalLoadout();
        var match = CommandMatcher.Match(loadout, _typed);
        if (match.Kind == MatchKind.Prefix)
        {
            OrdnanceCues.Local(KeySound(step));
            return;
        }
        if (match.Kind == MatchKind.None)
        {
            OrdnanceCues.Local(OrdnanceSound.KeyWrong);
            Notify(OrdnanceText.T("notice.noMatch"), false);
            _typed.Clear();
            return;
        }
        var definition = loadout[match.Index];
        var reason = _session.Book.Check(OrdnanceSession.Local, definition.Key, Time.time);
        if (reason != Rejection.None)
        {
            Notify(OrdnanceText.T("notice.callInRefused", OrdnanceContent.Name(definition), OrdnanceText.Reason(reason)));
            _typed.Clear();
            return;
        }
        BeginHolding(definition);
    }

    /// <summary>The code is right: the ball stays in hand after the call key is released. The input hold is released completely,
    /// so the game's throwing weapon reads fire through the game's own input functions; a weapon switch is caught by reading the
    /// switch keys (or by the ball leaving the hand) and cancels the call.</summary>
    private void BeginHolding(OrdnanceDefinition definition)
    {
        _held = definition;
        _awaiting = definition;
        _throwToken++;
        _matchedBall = null;
        _awaitUntil = float.MaxValue;
        _phase = Phase.Holding;
        OrdnanceCues.Local(OrdnanceSound.CodeComplete);
        HandPulse.Start(definition.BeamRgb);
        ReleaseInput();
        ThrowWatch.Begin();
    }

    /// <summary>The game consumed the ball (the throw): release the input and wait for the game's own clean-up, then restore.</summary>
    private void Thrown()
    {
        if (_phase != Phase.Holding) return;
        _held = null;
        _typed.Clear();
        ReleaseInput();
        ThrowWatch.End(true);
        _phase = Phase.Restoring;
        _emptyFrame = -1;
        _restoreDeadline = Time.time + ClearSeconds;
        _awaitUntil = Time.time + SpawnSeconds;
    }

    /// <summary>After a throw the game empties the consumable slot and wields the first weapon. Restoring waits until the slot
    /// has been empty for a full frame, so the game's own steps are finished, then puts the saved state back.</summary>
    private void TickRestoring()
    {
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected || !agent.Alive) return;
        if (!BallInSlot())
        {
            if (_emptyFrame < 0) { _emptyFrame = Time.frameCount; return; }
            if (Time.frameCount <= _emptyFrame) return;
        }
        else if (Time.time < _restoreDeadline) return;
        else if (_restoreDeadline > 0f) Plugin.Logger.ErrorOnce("The summoning ball was still in the consumable slot " + ClearSeconds + " s after the throw; it is cleared by force.");
        Restore();
        if (_carrying) return;
        _phase = Phase.Idle;
    }

    private void DiscardInput()
    {
        _checkpointCarry = null;
        EndInput(false);
    }

    private void CheckpointReached()
    {
        _checkpointCarry = _carrying ? new CarrySnapshot(_savedItem, _savedAmmo, _savedSlot) : null;
        if (_carrying) EndInput(true);
    }

    private void CheckpointReloaded()
    {
        EndInput(false);
        if (_checkpointCarry is not { } saved) return;
        _savedItem = saved.Item; _savedAmmo = saved.Ammo; _savedSlot = saved.Slot;
        _carrying = _checkpointRestoring = true;
        _phase = Phase.Restoring;
        _emptyFrame = -1; _restoreDeadline = 0f;
    }

    /// <summary>Cancels the call and releases input. Inventory restoration stays pending until the player is alive and
    /// the backpack is available; only native inventory recovery or level teardown discards it.</summary>
    private void EndInput(bool restore)
    {
        if (_phase == Phase.Holding) ThrowWatch.End(false);
        _phase = Phase.Idle;
        _held = null;
        _awaiting = null;
        _throwToken++; _matchedBall = null;
        _awaitUntil = 0f;
        _typed.Clear();
        try { ReleaseInput(); }
        finally
        {
            if (!restore) { _carrying = false; _checkpointRestoring = false; }
            else if (_carrying)
            {
                _phase = Phase.Restoring;
                _emptyFrame = -1;
                _restoreDeadline = 0f;
                Restore();
                if (!_carrying) _phase = Phase.Idle;
            }
        }
    }

    /// <summary>This mod's ForgeCore input lease while a code is entered or the ball is held: the blocked actions read as not
    /// pressed everywhere the game reads them; ForgeCore also releases it at level cleanup, death and leaving.</summary>
    private static ForgeCore.InputLease? _input;

    /// <summary>The input lease is held now.</summary>
    internal static bool InputHeld => _input is { IsHeld: true };

    /// <summary>Blocks exactly <paramref name="actions"/> (replacing what was blocked).</summary>
    private static void HoldInput(params InputAction[] actions)
    {
        if (!InputHeld) _input = ForgeCore.InputLease.Take(Plugin.Id);
        _input!.Block(actions);
    }

    private static void ReleaseInput()
    {
        _input?.Dispose();
        _input = null;
    }

    // ---- the ball in hand ----------------------------------------------------------------------------------

    private static bool BallInSlot()
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        return backpack != null && backpack.TryGetBackpackItem(InventorySlot.Consumable, out var item) && item != null && item.ItemID == BeaconItem.BlockId;
    }

    private static bool BallConsumed()
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        return backpack != null && (!BallInSlot() || backpack.AmmoStorage.ConsumableAmmo.BulletsInPack <= 0);
    }

    /// <summary>Records what the consumable slot and the hand hold now, then puts the ball in the slot and wields it. False
    /// (with a notice, nothing changed) when that is not possible.</summary>
    private bool BeginCarry(PlayerAgent agent)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null || agent.Inventory == null) { Plugin.Logger.ErrorOnce("The local backpack is not available for a call-in."); return false; }
        var wielded = agent.Inventory.WieldedSlot;
        if (wielded is InventorySlot.InLevelCarry or InventorySlot.ConsumableHeavy or InventorySlot.Pickup)
        {
            Notify(OrdnanceText.T("notice.carrying"));
            return false;
        }
        _savedSlot = wielded;
        _savedItem = 0;
        _savedAmmo = 0f;
        if (backpack.TryGetBackpackItem(InventorySlot.Consumable, out var current) && current != null)
        {
            _savedItem = current.ItemID;
            _savedAmmo = backpack.AmmoStorage.ConsumableAmmo.AmmoInPack;
        }
        _carrying = true;
        _ballWielded = false;
        _carryStart = Time.time;
        try
        {
            PlayerBackpackManager.AddDebugItem(BeaconItem.BlockId, 1f);
            var storage = backpack.AmmoStorage;
            if (!BallInSlot()) throw new InvalidOperationException("AddDebugItem did not put the ball in the consumable slot");
            var pool = storage.ConsumableAmmo;
            if (pool.BulletsInPack != 1) storage.SetAmmo(AmmoType.CurrentConsumable, pool.CostOfBullet);
            if (pool.BulletsInPack != 1) throw new InvalidOperationException("the ball has " + pool.BulletsInPack + " charges instead of 1");
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Readying the summoning ball failed; the inventory is put back: " + error);
            Notify(OrdnanceText.T("notice.ballFailed"));
            EndInput(true);
            return false;
        }
        return true;
    }

    /// <summary>The ball must stay in the consumable slot and, once it has reached the hand, stay wielded; leaving the slot
    /// by any means is a cancel. <paramref name="beforeWield"/> allows the short wait while the game switches.</summary>
    private bool BallInHand(PlayerAgent agent, bool beforeWield)
    {
        if (beforeWield && !BallInSlot()) return false;
        var slot = agent.Inventory.WieldedSlot;
        if (slot == InventorySlot.Consumable)
        {
            _ballWielded = true;
            return true;
        }
        if (_ballWielded) return false;
        if (Time.time - _carryStart > WieldSeconds)
        {
            Plugin.Logger.ErrorOnce("The summoning ball did not reach the hand within " + WieldSeconds + " s; the inventory is put back.");
            Notify(OrdnanceText.T("notice.ballFailed"));
            return false;
        }
        return true;
    }

    /// <summary>Puts back what <see cref="BeginCarry"/> saved: clears the ball if it is still in the slot, restores the
    /// original consumable with its exact ammunition, and wields the original slot again. Retried after revival or while
    /// the player/backpack is temporarily unavailable; the saved state is released only after restoration succeeds.</summary>
    private void Restore()
    {
        if (!_carrying) return;
        if (_checkpointRestoring && !ForgeCore.Checkpoint.Settled) return;
        var agent = ForgeCore.Players.Local;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (agent == null || agent.WasCollected || !agent.Alive || backpack == null || agent.Inventory == null) return;
        try
        {
            if (BallInSlot()) PlayerBackpackManager.ClearLocalSlot(InventorySlot.Consumable, true, false);
            if (_savedItem != 0)
            {
                PlayerBackpackManager.AddDebugItem(_savedItem, _savedAmmo);
                var storage = backpack.AmmoStorage;
                if (Math.Abs(storage.ConsumableAmmo.AmmoInPack - _savedAmmo) > 0.001f) storage.SetAmmo(AmmoType.CurrentConsumable, _savedAmmo);
                storage.UpdateSlotAmmoUI(InventorySlot.Consumable);
            }
            if (_savedSlot != InventorySlot.None && !(_savedItem != 0 && _savedSlot == InventorySlot.Consumable))
                agent.Sync.WantsToWieldSlot(_savedSlot);
            _carrying = _checkpointRestoring = false;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Putting the inventory back after a call-in failed: " + error); }
    }

    // ---- thrown balls and beacons --------------------------------------------------------------------------

    /// <summary>Captures the current local call at the native request, before any delayed spawn notification.</summary>
    private Action<ISyncedItem, PlayerAgent>? CaptureThrow()
    {
        if (_awaiting == null) return null;
        var token = _throwToken;
        return (item, _) =>
        {
            if (token != _throwToken || _awaiting == null)
            {
                Plugin.Logger.ErrorOnce("A summoning ball spawn arrived after its call expired; it is ignored.");
                return;
            }
            _matchedBall = item.TryCast<Item>() ?? throw new InvalidOperationException("The ball callback did not return an item.");
        };
    }

    /// <summary>Only the item identified by this call's native callback can bind its landing tracker.</summary>
    private void BallSpawned(BeaconSpawn spawn)
    {
        if (!spawn.Local || _matchedBall == null || _matchedBall.Pointer != spawn.Item.Pointer) return;
        _matchedBall = null;
        var definition = _awaiting;
        if (definition == null)
        {
            Plugin.Logger.ErrorOnce("A summoning ball of yours appeared with no call-in locked in; it is ignored.");
            return;
        }
        _awaiting = null;
        _throwCounter = Math.Max(_throwCounter, _session.ThrowFloor);
        if (_throwCounter == uint.MaxValue) { Notify(OrdnanceText.T("notice.idsExhausted")); EndInput(true); return; }
        ++_throwCounter;
        _thrown.Add(new ThrownBeacon(_throwCounter, definition.Key, new Vec3(spawn.Position.x, spawn.Position.y, spawn.Position.z), spawn.Item, spawn.Replicator));
        if (_phase == Phase.Holding) Thrown();
    }

    private void TickAwaiting()
    {
        if (_awaiting == null || Time.time <= _awaitUntil) return;
        Plugin.Logger.ErrorOnce("The thrown summoning ball for '" + _awaiting.Id + "' never appeared; the call is lost.");
        Notify(OrdnanceText.T("notice.noBeacon"));
        _awaiting = null;
        _throwToken++; _matchedBall = null;
        // Throw may have failed even before consuming its charge. A throw attempt has a finite deadline too.
        if (_phase == Phase.Holding) EndInput(true);
    }

    private void TickThrown()
    {
        for (var i = _thrown.Count - 1; i >= 0; i--)
        {
            var ball = _thrown[i];
            if (!ball.Reported && ball.Settled())
            {
                ball.Reported = true;
                var p = ball.Position;
                _session.ReportLanding(new LandingMessage { ThrowId = ball.ThrowId, Key = ball.Key, Replicator = ball.Replicator, Origin = ball.Origin, Landing = new Vec3(p.x, p.y, p.z) });
            }
            else if (!ball.Reported && (ball.Gone || ball.Fell || ball.Age > BallTimeout))
            {
                Notify(OrdnanceText.T("notice.refused", OrdnanceText.Reason(Rejection.LandingTimeout)));
                _thrown.RemoveAt(i);
            }
            else if (ball.Reported && ball.Age > BallTimeout + 10f) _thrown.RemoveAt(i);
        }
    }

    private void ThrowRejected(RejectMessage message)
    {
        for (var i = _thrown.Count - 1; i >= 0; i--)
            if (_thrown[i].ThrowId == message.ThrowId) _thrown.RemoveAt(i);
    }

    private void BeaconStarted(AcceptedMessage message, OrdnanceDefinition definition)
    {
        if (message.Thrower == OrdnanceSession.Local)
            for (var i = _thrown.Count - 1; i >= 0; i--)
                if (_thrown[i].ThrowId == message.ThrowId) _thrown.RemoveAt(i);
        _beacons.Add(new OrdnanceBeacon(message.Thrower, message.ThrowId, definition,
            new Vector3(message.Landing.X, message.Landing.Y, message.Landing.Z), message.Delay));
    }

    private void BeaconArrived(ArrivedMessage message, OrdnanceDefinition definition)
    {
        RemoveBeacon(message.Thrower, message.ThrowId);
        // A direct arrival has nothing landing; the drop pod and turret shell shake on their own impact, a moment later.
        if (message.Arrival == (byte)OrdnanceArrivalMode.Direct) return;
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload) && payload.HasOwnArrival) return;
        CameraShake.Shake(new Vector3(message.Landing.X, message.Landing.Y, message.Landing.Z), in LandingShake);
    }

    private void RemoveBeacon(ulong thrower, uint throwId)
    {
        for (var i = _beacons.Count - 1; i >= 0; i--)
        {
            var beacon = _beacons[i];
            if (beacon.Thrower != thrower || beacon.ThrowId != throwId) continue;
            beacon.Destroy();
            _beacons.RemoveAt(i);
        }
    }

    private void TickBeacons()
    {
        // The arrival message normally removes a beacon; this only clears one whose message never came.
        for (var i = _beacons.Count - 1; i >= 0; i--)
        {
            if (Time.time > _beacons[i].ArriveAt + 10f) { _beacons[i].Destroy(); _beacons.RemoveAt(i); }
            else _beacons[i].Tick();
        }
    }

    private void ClearPresentation(bool hostChange = false)
    {
        // A host change keeps restoration pending if downed. Checkpoint recovery keeps only its checkpoint-time snapshot;
        // the restoration barrier prevents writing over native recall while it runs.
        EndInput(_session.InLevel);
        // The thrown balls are the game's own items and are left to it; only the landing reports and beacons are dropped.
        _thrown.Clear();
        foreach (var beacon in _beacons) beacon.Destroy();
        _beacons.Clear();
        // The world did not roll back at a host change: a pod still holding something to claim stays, and its new host opens it again.
        if (hostChange) { Deliveries.HostChanged(); OrdnanceClaims.HostChanged(); }
        // A checkpoint reload replays the level's own drops under the same call numbers, so a sentry's identity and timer of the old world must not meet the replayed one.
        else { Deliveries.Clear(); OrdnanceClaims.Clear(); GearPayload.Clear(); }
    }

    // ---- notices -------------------------------------------------------------------------------------------

    private void Notify(string text) => Notify(text, true);

    /// <summary>Shows a refusal or failure; <paramref name="denied"/> adds the local refusal sound (the wrong-code path plays its own).</summary>
    private void Notify(string text, bool denied)
    {
        if (denied) OrdnanceCues.Local(OrdnanceSound.Denied);
        NoticePosted?.Invoke(text);
    }
}
