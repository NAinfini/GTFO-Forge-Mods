using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BepInEx;
using CellMenu;
using GTFO.API;
using LevelGeneration;
using Player;
using SNetwork;

namespace ForgeDevelopment.Native;

// These actions run only on the existing experiment runner. They request native operations; receipts do not
// certify a thermal material, doorway clearance, plan effect or successful level generation.
internal static partial class AuthoringGameDrive
{
    private sealed record AimLease(NativeLife Actor, long Player, long Holder, long Item,
        bool Force, bool Trigger);
    private sealed record Expedition(uint Rundown, int Tier, int Ordinal);
    private static AimLease? _aim;
    private static bool _checkpointArmed, _checkpointRestore;
    private static int _checkpointEvents, _reloadBefore, _usageBefore;
    private static ulong _recallsBefore;
    private static float _checkpointIssuedAt;
    private static long _checkpointWorld = long.MinValue;
    // How long a confirm waits for the game to settle a save or reload before it answers with the native state it is stuck in.
    private const float CheckpointSettleSeconds = 60f;
    private static ExitScanSettleGate? _returnGate;
    private static bool _returnReady;
    private static Expedition? _expedition;
    private static readonly MenuDrive Menu = new(_ => { });

    internal static ExperimentCallResult Call(ExperimentTargetResult target, ExperimentStep step)
    {
        try
        {
            if (IsSpatialOperation(step.Name)) return CallSpatial(target, step);
            RecObservation.RequireNativeRead();
            if (target.IsSet || target.Value as Type != typeof(AuthoringGameDrive)
                || step.ResultArgument || step.ArgumentNames.Any(name => name.Length != 0))
                throw new InvalidOperationException("drive.static-target-and-positional-arguments-required");
            RequireAuthoring();
            var count = step.Name switch
            {
                "drive.player.begin" => 1,
                "drive.enemy.begin" => 2,
                "drive.file.swap" => 2,
                "drive.ads.sample" => 1,
                "drive.enemies.spawn" => 1,
                "drive.doors.read" or "drive.doors.open" => 1,
                "drive.nav.sample" or "drive.nav.path" or "drive.nav.voxel" or "drive.nav.near" or "drive.nav.course" or "drive.noise.make" => 1,
                "drive.enemy.hit" or "drive.enemy.limbs" or "drive.enemy.follow" => 1,
                _ => 0
            };
            if (step.Arguments.Count != count) throw new ArgumentException("drive.argument-count");
            if (step.Name == "drive.return.issue") return IssueReturn();
            if (step.Name == "drive.lobby.leave") return LeaveLobby();
            if (step.Name == "drive.reenter.drop") return DropPrevious();
            if (step.Name is "drive.checkpoint.save" or "drive.checkpoint.restore") return BeginCheckpoint(step.Name == "drive.checkpoint.restore");
            if (step.Name == "drive.checkpoint.confirm") return ConfirmCheckpoint();
            string Text(int index) => step.Arguments[index].ValueKind == JsonValueKind.String
                ? step.Arguments[index].GetString()! : throw new ArgumentException("drive.string-argument");
            if (step.Name == "drive.enemies.spawn") return AuthoringEnemyBatch.Spawn(Text(0));
            if (step.Name is "drive.doors.read" or "drive.doors.open")
            {
                var doors = step.Name == "drive.doors.read" ? AuthoringDoors.Read(Text(0)) : AuthoringDoors.Open(Text(0));
                return ExperimentCallResult.Done(doors, doors);
            }
            if (step.Name.StartsWith("drive.nav.", StringComparison.Ordinal))
            {
                var nav = step.Name switch
                {
                    "drive.nav.sample" => AuthoringNavReadback.Sample(Text(0)),
                    "drive.nav.path" => AuthoringNavReadback.Path(Text(0)),
                    "drive.nav.voxel" => AuthoringNavReadback.Voxel(Text(0)),
                    "drive.nav.near" => AuthoringNavReadback.Near(Text(0)),
                    "drive.nav.course" => AuthoringNavReadback.Course(Text(0)),
                    _ => throw new ArgumentException("drive.unknown-operation")
                };
                return ExperimentCallResult.Done(nav, nav);
            }
            if (AuthoringEnemyProbe.Handles(step.Name))
            {
                var answer = AuthoringEnemyProbe.Call(step.Name, Text(0));
                return ExperimentCallResult.Done(answer, answer);
            }
            var result = step.Name switch
            {
                "drive.ads.begin" => BeginAim(),
                "drive.ads.sample" => SampleAim(Text(0)),
                "drive.ads.end" => EndAim(),
                "drive.player.begin" => AuthoringTraversal.BeginPlayer(Text(0)),
                "drive.player.tick" => AuthoringTraversal.TickPlayer(),
                "drive.player.finish" => AuthoringTraversal.FinishPlayer(),
                "drive.enemy.begin" => AuthoringTraversal.BeginEnemy(step.Arguments[0].GetInt32(), Text(1)),
                "drive.enemy.tick" => AuthoringTraversal.TickEnemy(),
                "drive.enemy.finish" => AuthoringTraversal.FinishEnemy(),
                "drive.file.swap" => SwapFile(Text(0), Text(1)),
                "drive.plans.reload" => ReloadPlans(),
                "drive.checkpoint.wipe" => WipePlayers(),
                "drive.return.prepare" => PrepareReturn(),
                "drive.reenter.select" => SelectPrevious(),
                "drive.reenter.ready" => ReadyPrevious(),
                "drive.lifecycle.read" => Lifecycle(),
                "drive.graph.dump" => AuthoringNoise.Graph(),
                "drive.noise.make" => AuthoringNoise.Make(Text(0)),
                "drive.enemies.modes" => AuthoringEnemyBatch.Modes(),
                "drive.enemies.read" => AuthoringEnemyBatch.Read(),
                "drive.enemies.sample" => AuthoringEnemyBatch.Sample(),
                "drive.enemies.report" => AuthoringEnemyBatch.Report(),
                "drive.enemies.clear" => AuthoringEnemyBatch.Cleanup(),
                "drive.enemies.clear_groups" => AuthoringWaveDrive.ClearGroups(),
                "drive.player.protect" => AuthoringWaveDrive.Protect(),
                "drive.player.unprotect" => AuthoringWaveDrive.Unprotect(),
                _ => throw new ArgumentException("drive.unknown-operation")
            };
            return ExperimentCallResult.Done(result, result);
        }
        catch (Exception error) { return ExperimentCallResult.Failed(error.Message); }
    }

    private static void RequireAuthoring()
    {
        if (!RecSession.Active || !ExperimentRunner.Running)
            throw new InvalidOperationException("drive.recorder-runner-required");
    }

    private static void RequireHostLevel()
    {
        if (!ForgeCore.Session.IsHost || GameStateManager.CurrentStateName != eGameStateName.InLevel)
            throw new InvalidOperationException("drive.host-level-required");
    }

    private static PlayerAgent LocalPlayer()
    {
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive || !player.IsEnabled)
            throw new InvalidOperationException("drive.local-player-unavailable-or-stale");
        return player;
    }

    private static string BeginAim()
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel || _aim != null)
            throw new InvalidOperationException("drive.ads-context-or-lease");
        var player = LocalPlayer();
        var holder = player.FPItemHolder;
        var item = holder?.WieldedItem;
        if (holder == null || holder.WasCollected || item == null || item.WasCollected
            || item.TryCast<Gear.BulletWeapon>() == null || holder.CurrentItemState == null
            || holder.CurrentItemState.IsBusy || !holder.ExtItemAimAllowed
            || holder.ForceAimTrigger || holder.ItemAimTrigger)
            throw new InvalidOperationException("drive.ads-idle-unaimed-firearm-required");
        _aim = new AimLease(NativeLife.Of(player)!, player.Pointer.ToInt64(),
            holder.Pointer.ToInt64(), item.Pointer.ToInt64(), holder.ForceAimTrigger, holder.ItemAimTrigger);
        // Request the holder's native override; subsequent state samples independently check whether the
        // game adopted it. No input device or state enum is fabricated.
        holder.ForceAimTrigger = true;
        holder.ItemAimTrigger = true;
        if (!holder.ForceAimTrigger || !holder.ItemAimTrigger) throw new InvalidOperationException("drive.ads-request-readback-mismatch");
        return "native-aim-request-issued; world=" + _aim.Actor.WorldEpoch + ";instance=" + _aim.Actor.Instance;
    }

    private static FirstPersonItemHolder AimHolder()
    {
        var lease = _aim ?? throw new InvalidOperationException("drive.ads-not-owned");
        var player = LocalPlayer();
        var holder = player.FPItemHolder;
        if (!lease.Actor.IsCurrent(player) || player.Pointer.ToInt64() != lease.Player
            || holder == null || holder.WasCollected || holder.Pointer.ToInt64() != lease.Holder
            || holder.WieldedItem == null || holder.WieldedItem.WasCollected || holder.WieldedItem.Pointer.ToInt64() != lease.Item)
            throw new InvalidOperationException("drive.ads-owner-life-or-item-changed");
        return holder;
    }

    private static string SampleAim(string expected)
    {
        if (expected is not ("aimed" or "released")) throw new ArgumentException("drive.ads-sample-phase");
        var holder = AimHolder();
        var state = holder.CurrentItemState;
        bool aimed = state?.TryCast<FirstPersonItem.FPIS_Aim>() != null;
        if (state == null || !float.IsFinite(holder.m_aimWeight)
            || (expected == "aimed" ? !holder.ItemAimTrigger || !holder.ForceAimTrigger || !aimed
                : holder.ItemAimTrigger || holder.ForceAimTrigger || aimed))
            throw new InvalidOperationException("drive.ads-native-state-mismatch:" + expected);
        return "native-ads=" + expected + ";frame=" + RecTime.Frame + ";aimWeight="
            + holder.m_aimWeight.ToString("R", CultureInfo.InvariantCulture)
            + ";transition-stability-thermal-material-and-camera-verdicts-require-observation";
    }

    private static string EndAim()
    {
        RestoreAim();
        // Keep the scalar lease until the released-phase samples have been taken. Cleanup releases it.
        return "native-aim-release-issued";
    }

    private static void RestoreAim()
    {
        if (_aim == null) return;
        RecObservation.RequireNativeRead();
        var holder = AimHolder();
        holder.ForceAimTrigger = _aim.Force;
        holder.ItemAimTrigger = _aim.Trigger;
        if (holder.ForceAimTrigger != _aim.Force || holder.ItemAimTrigger != _aim.Trigger)
            throw new InvalidOperationException("drive.ads-cleanup-readback-mismatch");
    }

    private static string SwapFile(string name, string variant)
    {
        if (!ForgeCore.Session.IsHost) throw new InvalidOperationException("drive.file-host-required");
        return AuthoringFileSwap.Swap(Paths.BepInExRootPath, name, variant);
    }

    private static string ReloadPlans()
    {
        RequireHostLevel();
        return EngineLookup.Call<string>("ReloadPlans");
    }

    /// <summary>Arms the checkpoint readback and issues the native save or reload. The arming lasts until the next save or restore in
    /// the same world, so a confirm may be its own command; a recall or capture still in progress (an earlier restore or save) is
    /// waited out on later frames, never refused, so the checks can be repeated in one level.</summary>
    private static ExperimentCallResult BeginCheckpoint(bool restore)
    {
        // A team wipe puts the game on its failure screen, where vanilla offers the reload; only a restore may start there.
        var failScreen = restore && ForgeCore.Session.IsHost && GameStateManager.CurrentStateName == eGameStateName.ExpeditionFail;
        if (!failScreen) RequireHostLevel();
        // Vanilla reloads a checkpoint only from that screen; inside a running level the session refuses the ReloadCheckpoint command.
        if (restore && !failScreen) throw new InvalidOperationException("drive.checkpoint-restore-needs-wipe");
        if (CheckpointManager.Current?.m_stateReplicator == null) throw new InvalidOperationException("drive.checkpoint-context-unavailable");
        var capture = SNet.Capture ?? throw new InvalidOperationException("drive.checkpoint-capture-unavailable");
        // ForgeCore.Checkpoint.Recalling reads the replicated checkpoint state and the capture recall, not the static flag a reload
        // that never completed leaves set across levels.
        if (ForgeCore.Checkpoint.Recalling || capture.IsCapturing || capture.IsRecalling)
            return ExperimentCallResult.Await(() => BeginCheckpoint(restore));
        DisarmCheckpoint();
        // A reload recalls the capture's Checkpoint buffer. Without one the game enters its reload and never finishes it (the
        // replicated isReloadingCheckpoint stays set and nothing moves), so a restore with no captured buffer is refused here.
        if (restore && (CheckpointManager.Current.m_stateReplicator.State.lastInteraction
            is not (eCheckpointInteractionType.StoreCheckpoint or eCheckpointInteractionType.ReloadCheckpoint)
            || !capture.GotBuffer(eBufferType.Checkpoint)))
            throw new InvalidOperationException("drive.checkpoint-save-required");
        // A restore follows a team wipe, so no player is alive to name; only a save reads the local player's position.
        var player = restore ? null : LocalPlayer();
        // The game's only save is a checkpoint door's scan finishing (LG_CheckpointScannerJob): one store and one capture per
        // door, each at its own position, which is why the save event (EventAPI.OnCheckpointReached, which Forge snapshots on)
        // is a moved door lock position. A second save at the stored position would capture the game's buffer again without
        // that event, leaving Forge's snapshot behind the game's, so it is refused before anything is written.
        if (!restore && CheckpointManager.Current.m_stateReplicator.State.doorLockPosition == player!.Position)
            throw new InvalidOperationException("drive.checkpoint-save-same-position");
        _checkpointRestore = restore;
        _reloadBefore = CheckpointManager.Current.m_stateReplicator.State.reloadCount;
        _usageBefore = CheckpointManager.CheckpointUsage;
        _recallsBefore = capture.GetRecallCount(eBufferType.Checkpoint);
        _checkpointIssuedAt = UnityEngine.Time.realtimeSinceStartup;
        _checkpointEvents = 0;
        _checkpointArmed = true;
        _checkpointWorld = RecTime.WorldEpoch;
        EventAPI.OnCheckpointReached += CheckpointSaved;
        EventAPI.OnCheckpointReloaded += CheckpointRestored;
        // A restore is the failure screen's own "restart from checkpoint" button (CM_PageExpeditionFail.m_btnRestartCheckpoint): the
        // session's ReloadCheckpoint command, then the checkpoint reload and the recall with every synced player. The replicated
        // ReloadCheckpoint interaction alone only marks the reload and raises reloadCount, it never starts the recall.
        if (restore)
        {
            if (!SNet.Sync.SessionCommand(eSessionCommandType.ReloadCheckpoint, 0))
            {
                DisarmCheckpoint();
                throw new InvalidOperationException("drive.checkpoint-session-refused");
            }
            CheckpointManager.ReloadCheckpoint();
            SNet.Sync.StartRecallCheckpointWithAllSyncedPlayers();
        }
        else
        {
            // The vanilla checkpoint door stores the replicated state, captures the game state into the Checkpoint buffer and sends it;
            // StoreCheckpoint alone leaves no buffer for a restore to recall.
            CheckpointManager.StoreCheckpoint(player!.Position);
            capture.CaptureGameState(eBufferType.Checkpoint);
            capture.SendBuffer(eBufferType.Checkpoint, null, false);
        }
        var issued = "native-checkpoint-" + (restore ? "reload" : "save") + "-issued; independent-event-and-state-readback-required";
        return ExperimentCallResult.Done(issued, issued);
    }

    private static void DisarmCheckpoint()
    {
        EventAPI.OnCheckpointReached -= CheckpointSaved;
        EventAPI.OnCheckpointReloaded -= CheckpointRestored;
        _checkpointArmed = false;
        _checkpointWorld = long.MinValue;
    }

    // The pack test's team wipe through the game's own synchronized no-air damage entry: each alive player takes exactly its live health.
    private static string WipePlayers()
    {
        RequireHostLevel();
        var hit = 0;
        ForgeCore.Players.Each(info =>
        {
            var damage = info.Agent.Alive ? info.Agent.Damage : null;
            if (damage == null || !float.IsFinite(damage.Health) || damage.Health <= 0f) return;
            damage.NoAirDamage(damage.Health);
            hit++;
        });
        if (hit == 0) throw new InvalidOperationException("drive.checkpoint-wipe-no-alive-targets");
        return "native-team-wipe-issued;players=" + hit;
    }

    private static void CheckpointSaved() { if (_checkpointArmed && !_checkpointRestore) _checkpointEvents++; }
    private static void CheckpointRestored() { if (_checkpointArmed && _checkpointRestore) _checkpointEvents++; }

    internal static string? CheckpointCompletion
    {
        get
        {
            if (!_checkpointArmed) throw new InvalidOperationException("drive.checkpoint-not-armed");
            // A save and a reload both keep the world: the reload recalls the saved state into it. A changed world number means the
            // level was left or replaced (a host stalled by a failed recall steps it on its own), never a finished reload.
            const string worldChanged = "drive.checkpoint-world-changed";
            if (RecTime.WorldEpoch != _checkpointWorld)
                throw new InvalidOperationException(worldChanged + ":" + _checkpointWorld + "->" + RecTime.WorldEpoch);
            RecObservation.RequireNativeRead();
            if (_checkpointEvents == 0) return null;
            var manager = CheckpointManager.Current;
            if (manager?.m_stateReplicator == null) throw new InvalidOperationException("drive.checkpoint-native-readback-missing");
            var state = manager.m_stateReplicator.State;
            if (ForgeCore.Checkpoint.Recalling) return null;
            // The reload recalls the Checkpoint buffer, which puts the replicated lastInteraction back to the saved StoreCheckpoint, so a finished
            // restore reads StoreCheckpoint again; its proof is the advanced reload and usage counters (the pack test's restore check reads the
            // same two) plus the capture's advanced Checkpoint recall count (a reload that never recalled leaves isReloadingCheckpoint set).
            if (_checkpointRestore ? state.reloadCount <= _reloadBefore || CheckpointManager.CheckpointUsage <= _usageBefore
                    || SNet.Capture == null || SNet.Capture.GetRecallCount(eBufferType.Checkpoint) <= _recallsBefore
                : state.lastInteraction != eCheckpointInteractionType.StoreCheckpoint)
                throw new InvalidOperationException("drive.checkpoint-native-readback-mismatch");
            return "native-checkpoint-confirmed; " + CheckpointState();
        }
    }

    /// <summary>The console's confirm: waits on later frames until the game settled the armed save or reload, and after
    /// <see cref="CheckpointSettleSeconds"/> answers with the native state the game is stuck in instead of a bare timeout.</summary>
    private static ExperimentCallResult ConfirmCheckpoint()
    {
        if (CheckpointCompletion is { } done) return ExperimentCallResult.Done(done, done);
        if (UnityEngine.Time.realtimeSinceStartup - _checkpointIssuedAt < CheckpointSettleSeconds) return ExperimentCallResult.Await(ConfirmCheckpoint);
        const string code = "drive.checkpoint-readback-missing";
        return ExperimentCallResult.Failed(code + ":" + CheckpointState());
    }

    /// <summary>The game's own checkpoint readback: the replicated state, the capture's recall of the Checkpoint buffer and the local player.</summary>
    private static string CheckpointState()
    {
        var state = CheckpointManager.Current.m_stateReplicator.State;
        var capture = SNet.Capture;
        var player = ForgeCore.Players.Local;
        return (_checkpointRestore ? "restore" : "save") + ";events=" + _checkpointEvents + ";reloadCount=" + _reloadBefore + "->" + state.reloadCount
            + ";checkpointUsage=" + _usageBefore + "->" + CheckpointManager.CheckpointUsage
            + ";checkpointRecalls=" + _recallsBefore + "->" + (capture == null ? "unavailable" : capture.GetRecallCount(eBufferType.Checkpoint).ToString(CultureInfo.InvariantCulture))
            + ";isReloadingCheckpoint=" + state.isReloadingCheckpoint + ";recalling=" + (capture?.IsRecalling.ToString() ?? "unavailable")
            + ";lastInteraction=" + state.lastInteraction + ";alive=" + (player != null && player.Alive)
            + ";position=" + (player == null ? "none" : player.Position.x.ToString("0.##", CultureInfo.InvariantCulture) + "," + player.Position.y.ToString("0.##", CultureInfo.InvariantCulture) + "," + player.Position.z.ToString("0.##", CultureInfo.InvariantCulture))
            + ";world=" + RecTime.WorldEpoch;
    }

    /// <summary>The return to the lobby is the same native quit from the level and from its result screen: the expedition stays
    /// active in ExpeditionSuccess and ExpeditionFail until the quit, as the pack test's own return stage relies on.</summary>
    private static void RequireHostLevelOrResult()
    {
        if (!ForgeCore.Session.IsHost || GameStateManager.CurrentStateName is not (eGameStateName.InLevel
            or eGameStateName.ExpeditionSuccess or eGameStateName.ExpeditionFail))
            throw new InvalidOperationException("drive.host-level-required");
    }

    /// <summary>A lobby keeps the rundown it was created with (Global.RundownIdToLoad), so another rundown is reached by leaving
    /// the cleaned lobby for NoLobby and creating a new one, as the pack test does between levels.</summary>
    private static ExperimentCallResult LeaveLobby()
    {
        if (!ForgeCore.Session.IsHost || !SNet.IsInLobby || GameStateManager.CurrentStateName != eGameStateName.Lobby || Builder.Current?.m_currentFloor != null)
            throw new InvalidOperationException("drive.cleaned-host-lobby-required");
        return ExperimentRunner.LeaveLobby(() =>
        {
            LobbyGate.CloseBotSlotsBeforeLeaving();
            SNet.SessionHub.LeaveHub();
        }, () =>
        {
            if (SNet.IsInLobby || SNet.LocalPlayer?.IsInSessionHub == true) return false;
            // The rundown page is where the next lobby is created; switching to it again on a later poll is a no-op.
            GuiManager.MainMenuLayer.ChangePage(eCM_MenuPage.CMP_RUNDOWN_NEW);
            return true;
        });
    }

    private static string PrepareReturn()
    {
        RequireHostLevelOrResult();
        if (_returnGate != null) throw new InvalidOperationException("drive.return-already-prepared");
        var active = RundownManager.GetActiveExpeditionData();
        if (active.rundownType != eRundownKey.Local || RundownManager.ActiveExpedition == null)
            throw new InvalidOperationException("drive.reentry-local-expedition-required");
        _expedition = new Expedition(Globals.Global.RundownIdToLoad, (int)active.tier, active.expeditionIndex);
        _returnReady = false;
        _returnGate = ExitScanSettle.Begin();
        return "native-return-preparation-started; expedition=" + _expedition;
    }

    private static ExperimentCallResult IssueReturn()
    {
        RequireHostLevelOrResult();
        if (_returnGate == null || CM_PageLoadout.Current == null)
            throw new InvalidOperationException("drive.return-native-gate-not-ready");
        // The existing runner polls the shared native gate only until it is ready.
        // Its bounded readback never issues quit twice.
        if (!_returnReady) _returnReady = _returnGate.Tick(RecTime.WorldEpoch, RecTime.Frame);
        if (!_returnReady) return ExperimentCallResult.Await(IssueReturn);
        return ExperimentRunner.ChangeWorld(true, () =>
        {
            ExitScanSettle.BeforeQuit();
            CM_PageLoadout.Current.EXT_QuitToMain();
        });
    }

    private static Expedition Previous()
    {
        if (!ForgeCore.Session.IsHost || GameStateManager.CurrentStateName != eGameStateName.Lobby || Builder.Current?.m_currentFloor != null)
            throw new InvalidOperationException("drive.cleaned-host-lobby-required");
        return _expedition ?? throw new InvalidOperationException("drive.previous-expedition-unavailable");
    }

    private static string SelectPrevious()
    {
        var previous = Previous();
        Menu.SelectExpedition("authoring-reentry", previous.Rundown, previous.Tier, previous.Ordinal);
        return "native-expedition-selection-issued; " + previous;
    }

    private static string ReadyPrevious()
    {
        var previous = Previous();
        var loadout = CM_PageLoadout.Current;
        if (!MenuDrive.ActiveMatches(previous.Rundown, previous.Tier, previous.Ordinal) || loadout == null
            || GuiManager.MainMenuLayer.m_currentPageEnum != eCM_MenuPage.CMP_LOADOUT)
            throw new InvalidOperationException("drive.reentry-selection-readback-mismatch");
        if (GameStateManager.IsReady) return "native-player-already-ready";
        if (loadout.m_readyButton?.m_collider?.enabled != true)
            throw new InvalidOperationException("drive.reentry-ready-button-unavailable");
        loadout.OnReadyButtonClicked();
        return "native-ready-button-issued";
    }

    private static ExperimentCallResult DropPrevious()
    {
        var previous = Previous();
        return ExperimentRunner.ChangeWorld(false, () =>
        {
            if (!Menu.TryDrop(previous.Rundown, previous.Tier, previous.Ordinal, () => { }))
                throw new InvalidOperationException("drive.reentry-native-drop-not-ready");
        });
    }

    private static string Lifecycle() => "state=" + GameStateManager.CurrentStateName + ";world=" + RecTime.WorldEpoch
        + ";floorPresent=" + (Builder.Current?.m_currentFloor != null) + ";host=" + SNet.IsMaster
        + ";checkpointEvents=" + _checkpointEvents + ";expedition=" + _expedition;

    internal static void Cleanup()
    {
        var failures = new List<Exception>();
        try { RestoreAim(); _aim = null; } catch (Exception error) { failures.Add(error); }
        try { AuthoringTraversal.Cleanup(); } catch (Exception error) { failures.Add(error); }
        try { CleanupSpatial(); } catch (Exception error) { failures.Add(error); }
        try { CleanupModelShot(); } catch (Exception error) { failures.Add(error); }
        try { AuthoringEnemyBatch.Cleanup(); } catch (Exception error) { failures.Add(error); }
        AuthoringEnemyProbe.Cleanup();
        try { GameInputDrive.ReleaseKeysOfEndedWorld(); } catch (Exception error) { failures.Add(error); }
        _returnGate = null;
        _returnReady = false;
        if (failures.Count > 0) throw new AggregateException("drive.cleanup-failed", failures);
    }
}

