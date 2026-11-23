using System;
using AIGraph;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The Harmony patches behind the door and terminal interaction rows, and the readbacks that follow ForgeCore's
/// shared patches (<c>GameHooks</c>). Each one only chooses when to read; the derivation is in
/// `DoorTerminalDerivations` and the publication in `DoorTerminalPublisher`, so a patch holds no rule of its own
/// and a test can drive the same body without a patch.
///
/// The patches are declared here rather than added to `MapObjectHooks.cs` because that file,
/// `MapNativeHooks.cs` and `MapPluginSession.cs` are shared registration points this slice must not edit; the
/// integration batch adds the types below to `MapNativeHooks.Types` from `integration.json`.
///
/// Terminal commands are observed at the interpreter's ReceiveCommand, after manager validation and before
/// synchronous execution. The send request is not proof that the terminal received or accepted a command.
/// The fact reader asks the interpreter for the registered name; input text is not a command identity.
/// </summary>
/// <summary>The two stages of a weak door, read from the game's own replication callbacks on the door. They are
/// the sync layer's notifications — `LG_WeakDoor.OnSyncDoorGotDamage` while the door is being hit and
/// `OnSyncDoorGotDestroyed` when it breaks — so the host that computed the damage and every client that applies
/// it both run them, and the publisher's authority gate is what keeps the fact host-only. Both carry the
/// `SNet_Player` that caused the damage, which is the row's `attacker` port; a hit with no player behind it
/// publishes no attacker rather than the last one that touched the door.</summary>
/// <remarks>Subscribed to <c>ForgeCore.GameHooks.WeakDoorState</c> (see <see cref="MapGameHooks"/>); ForgeCore patches the game method.</remarks>
internal static class WeakDoorStateReadback
{
    internal static void Changed(LG_WeakDoor door, pDoorState state)
    {
        if (!Session.IsHost) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects,
                module => module.DoorStateChanged(door, DoorStateReadback.Instigator(state)),
                "map-object observation");
    }
}

[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.OnSyncDoorGotDamage))]
internal static class WeakDoorAttackedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakDoor __instance, SNet_Player instigatorPlayer)
    {
        if (!Session.IsHost) return;
        var enemy = instigatorPlayer == null ? WeakDoorEnemyDamageScope.EnemyFor(__instance) : null;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakDoorAttacked(__instance, instigatorPlayer, enemy));
    }
}

/// <summary>The enemy behind one weak-door hit. Vanilla door breaking is
/// `EB_InCombat_MoveToNextNode_DestroyDoor.UpdateBehaviour` (build 20403457, RVA 0x166FE50) calling, at 0x16703bd,
/// `iLG_Door_Core.AttemptDamage(eDoorDamageType.EnemyLight, position, enemyAgent)`; `LG_WeakDoor.AttemptDamage`
/// (RVA 0x15C2200) then enters `LG_Door_Sync.AttemptDoorInteraction`, which keeps only a player owner, so the
/// enemy is gone before `OnSyncDoorGotDamage`. This scope holds the source agent for the duration of the one
/// `AttemptDamage` call when it is an `EnemyAgent`; the sync callbacks the host runs inside that call read it back
/// for the same door. It is not a per-frame hook: it runs only when a door is hit.
///
/// Damage Forge itself issues through <see cref="DoorActions"/> is entered with <see cref="Issuing"/> set, so a
/// behavior that answers an enemy's door hit by damaging the door is never reported as that enemy hitting it
/// again. A nested scope shadows its parent and the finalizer restores the previous one on every exit.</summary>
[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.AttemptDamage))]
internal static class WeakDoorEnemyDamageScope
{
    internal sealed record Frame(IntPtr Door, Enemies.EnemyAgent Enemy);

    [ThreadStatic] private static Frame? _current;
    [ThreadStatic] private static bool _issuing;

    /// <summary>Set by Forge's own door damage for the length of its native call.</summary>
    internal static bool Issuing { get => _issuing; set => _issuing = value; }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_WeakDoor __instance, Agents.Agent sourceAgent, out Frame? __state)
        => __state = Enter(__instance, sourceAgent);

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        _current = __state;
        return __exception;
    }

    internal static Frame? Enter(LG_WeakDoor door, Agents.Agent? sourceAgent)
    {
        var previous = _current;
        _current = !_issuing && !ForgeCore.NativeObjects.Gone(door) && !ForgeCore.NativeObjects.Gone(sourceAgent)
            && sourceAgent.TryCast<Enemies.EnemyAgent>() is { } enemy && !ForgeCore.NativeObjects.Gone(enemy)
            ? new Frame(door.Pointer, enemy) : null;
        return previous;
    }

    internal static void Restore(Frame? previous) => _current = previous;

    /// <summary>The enemy hitting exactly this door right now, or null outside such a hit.</summary>
    internal static Enemies.EnemyAgent? EnemyFor(LG_WeakDoor door)
        => _current is { } frame && !ForgeCore.NativeObjects.Gone(door) && frame.Door == door.Pointer && !ForgeCore.NativeObjects.Gone(frame.Enemy) ? frame.Enemy : null;
}

[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.OnSyncDoorGotDestroyed))]
internal static class WeakDoorBrokenReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakDoor __instance, SNet_Player instigatorPlayer)
    {
        if (!Session.IsHost) return;
        var enemy = instigatorPlayer == null ? WeakDoorEnemyDamageScope.EnemyFor(__instance) : null;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakDoorBroken(__instance, instigatorPlayer, enemy));
    }
}

/// <summary>The synchronous native scope that ties the weak-door destruction effect to the exact
/// <c>NoiseManager.MakeNoise</c> call it makes. A nested or refused scope shadows its parent and the finalizer
/// restores the previous frame on both normal and exceptional exits.</summary>
[HarmonyPatch(typeof(LG_WeakDoor_Destruction), nameof(LG_WeakDoor_Destruction.TriggerExplosionEffect))]
internal static class WeakDoorExplosionNoiseScope
{
    internal sealed record Frame(LG_WeakDoor Door, LG_WeakDoor_Destruction Destruction, LG_Gate Gate,
        AIG_CourseNode Node, MapObjectReference Address);

    [ThreadStatic] private static Frame? _current;
    internal static Frame? Current => _current;

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_WeakDoor_Destruction __instance, out Frame? __state)
        => __state = Enter(__instance);

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        Restore(__state);
        return __exception;
    }

    /// <summary>Installs one exact frame and returns the prior frame for Harmony's per-invocation state.</summary>
    internal static Frame? Enter(LG_WeakDoor_Destruction destruction)
    {
        var previous = _current;
        _current = Resolve(destruction);
        return previous;
    }

    internal static void Restore(Frame? previous) => _current = previous;

    private static Frame? Resolve(LG_WeakDoor_Destruction destruction)
    {
        try
        {
            if (ForgeCore.NativeObjects.Gone(destruction))
                return Refuse(destruction, "door-noise-destruction-stale");
            var gate = destruction.m_gate;
            if (ForgeCore.NativeObjects.Gone(gate)) return Refuse(destruction, "door-noise-gate-missing");
            var fromCore = destruction.m_core?.TryCast<LG_WeakDoor>();
            var fromGate = gate.SpawnedDoor?.TryCast<LG_WeakDoor>();
            if (fromCore != null && fromGate != null && fromCore.Pointer != fromGate.Pointer)
                return Refuse(destruction, "door-noise-door-ambiguous");
            var door = fromCore ?? fromGate;
            if (ForgeCore.NativeObjects.Gone(door) || door.Gate == null || door.Gate.Pointer != gate.Pointer)
                return Refuse(destruction, "door-noise-door-missing");
            var owned = door.m_destructionComp?.TryCast<LG_WeakDoor_Destruction>();
            if (ForgeCore.NativeObjects.Gone(owned) || owned.Pointer != destruction.Pointer)
                return Refuse(destruction, "door-noise-destruction-mismatch");
            // Address already reads the authored address back to this exact native door. A null answer is a door no
            // authored room or interior door names — a stock weak door, the ordinary case in any level that is not
            // authored — which has no identity to publish and was promised nothing, so it is not a refusal. An
            // authored door whose address no longer resolves has already been recorded by the lookup that failed.
            if (WeakDoorObservation.Address(door) is not { } address) return null;
            var node = gate.CoursePortal?.m_nodeA;
            if (node == null || node.WasCollected) return Refuse(destruction, "door-noise-node-missing");
            return new Frame(door, destruction, gate, node, address);
        }
        catch (Exception error)
        {
            return Refuse(destruction, "door-noise-native-read-failed:" + error.GetType().Name);
        }
    }

    private static Frame? Refuse(LG_WeakDoor_Destruction? destruction, string reason)
    {
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.WeakDoorRefused,
            "Weak door noise could not be bound to its authored native instance.",
            RuntimeLogValue.Of("instance", destruction?.GetInstanceID() ?? 0),
            RuntimeLogValue.Of("reason", reason));
        return null;
    }
}

/// <summary>The successful return of the exact native noise call inside a weak-door explosion. The full packet
/// fingerprint prevents any other nested noise from being attributed to the door. This reports submission to
/// the native noise queue, never that an enemy heard the noise.</summary>
/// <remarks>Subscribed to <c>ForgeCore.GameHooks.NoiseMade</c> (see <see cref="MapGameHooks"/>); ForgeCore patches the game method.</remarks>
internal static class WeakDoorNoiseReadback
{
    internal static void Completed(NM_NoiseData noiseData, bool ranOriginal)
    {
        // The managed scope is the cheap gate; the native master read comes after it.
        if (!ranOriginal || WeakDoorExplosionNoiseScope.Current is not { } frame || !Session.IsHost) return;
        if (!Matches(frame, noiseData)) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakDoorNoise(frame.Door, noiseData));
    }

    internal static bool Matches(WeakDoorExplosionNoiseScope.Frame frame, NM_NoiseData noise)
    {
        try
        {
            if (ForgeCore.NativeObjects.Gone(frame.Door) || ForgeCore.NativeObjects.Gone(frame.Destruction) || ForgeCore.NativeObjects.Gone(frame.Gate)
                || frame.Door.Gate?.Pointer != frame.Gate.Pointer
                || frame.Door.m_destructionComp?.TryCast<LG_WeakDoor_Destruction>()?.Pointer != frame.Destruction.Pointer
                || frame.Gate.SpawnedDoor?.TryCast<LG_WeakDoor>()?.Pointer != frame.Door.Pointer
                || WeakDoorObservation.ByAddress(frame.Address)?.Pointer != frame.Door.Pointer
                || noise.noiseMaker != null || noise.node == null || noise.node.Pointer != frame.Node.Pointer
                || noise.radiusMin != 11f || noise.radiusMax != 13f || noise.yScale != 1f
                || noise.type != NM_NoiseType.Detectable || !noise.includeToNeightbourAreas || noise.raycastFirstNode)
                return false;
            var position = frame.Destruction.transform.position;
            var heard = noise.position;
            return ForgeCore.Vec.Finite(heard) && heard.x == position.x && heard.y == position.y && heard.z == position.z;
        }
        catch (Exception error)
        {
            MapLog.Contained("door-terminal.noise-match", error);
            return false;
        }
    }
}

/// <remarks>Subscribed to <c>ForgeCore.GameHooks.TerminalCommand</c> before the game's body (see <see cref="MapGameHooks"/>).</remarks>
internal static class TerminalCommandReadbackFixed
{
    /// <summary>How many commands the host's interpreters have received, and the terminal of the last one. A command row
    /// compares them around its own send to see whether its terminal received the command inside that call.</summary>
    internal static long Count { get; private set; }
    internal static uint LastTerminal { get; private set; }

    internal static void Received(LG_ComputerTerminalCommandInterpreter interpreter, TERM_Command cmd, string inputLine)
    {
        if (!Session.IsHost || !LG_Factory.BuildDone || interpreter == null || interpreter.WasCollected
            || interpreter.m_terminal is not { } terminal || ForgeCore.NativeObjects.Gone(terminal)) return;
        Count++; LastTerminal = terminal.SyncID;
        if (TerminalStateReadback.IsReactorConsole(terminal)) return;
        if (Withdrawn(terminal, cmd)) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects, module => module.TerminalCommandStarted(terminal, (int)cmd), "terminal command observation");
        DoorTerminalFacts.Current?.Guard(facts =>
            facts.TerminalCommandEntry(terminal.SyncID, (int)cmd, inputLine));
    }

    private static readonly System.Collections.Generic.HashSet<(uint, int)> _reported = new();

    /// <summary>A command the terminal has withdrawn (its replicated `RemovedCommands`, written by the terminal-content
    /// remove / hide actions and by the game's own events) is not a command this row publishes. The game's own typed-line
    /// check turns a withdrawn command away only for the `OnlyOnceDelete` rule; a `Normal` or `OnlyOnce` command that
    /// was merely hidden from the list still reaches ReceiveCommand, so the refusal is made here, on the host that
    /// decides every command fact, whichever machine typed it. The refusal is logged once per terminal and command.</summary>
    private static bool Withdrawn(LG_ComputerTerminal terminal, TERM_Command cmd)
    {
        try
        {
            if (!terminal.CommandIsHidden(cmd)) return false;
            if (_reported.Add((terminal.SyncID, (int)cmd)))
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.TerminalCommandWithdrawn,
                    "A terminal command that was removed from the terminal was typed; no command fact was published.",
                    RuntimeLogValue.Of("terminal", (long)terminal.SyncID),
                    RuntimeLogValue.Of("command", (int)cmd));
            return true;
        }
        catch (Exception error)
        {
            MapLog.Contained("door-terminal.command-withdrawn", error);
            return false;
        }
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), "TryUnlockingTerminal")]
internal static class TerminalPasswordResultReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminalCommandInterpreter __instance, int __result)
    {
        if (!Session.IsHost || !LG_Factory.BuildDone || __instance == null || __instance.WasCollected
            || __instance.m_terminal is not { } terminal || ForgeCore.NativeObjects.Gone(terminal)) return;
        if (TerminalStateReadback.IsReactorConsole(terminal)) return;
        // Build 20403457, RVA 0x12930D0: equality chooses 1 or -1. Success clears password
        // protection and returns to PlayerInteracting, which is a session, not an outcome state.
        int? outcome = __result switch
        {
            1 when !terminal.IsPasswordProtected => MapObjectOutcomes.Succeeded,
            -1 when terminal.IsPasswordProtected => MapObjectOutcomes.Failed,
            _ => null
        };
        if (outcome is { } settled && MapHost.Session is { } session)
            session.Run(session.MapObjects,
                module => module.TerminalCommandFinished(terminal, (int)TERM_Command.TryUnlockingTerminal, settled),
                "terminal result observation");
    }
}

/// <summary>The data download's own update is the only native body that can prove the asynchronous command
/// completed. A terminal state change cannot: any caller of <c>ChangeState</c> can independently force the same
/// DataMining-to-PlayerInteracting transition. Build 20403457 reads these four fields before its completion
/// branch, calls <c>OnDataMiningDone</c>, then requests PlayerInteracting.</summary>
[HarmonyPatch(typeof(LG_TERM_DataMining), nameof(LG_TERM_DataMining.Update))]
internal static class TerminalDownloadResultReadback
{
    internal readonly struct Frame
    {
        internal Frame(IntPtr state, IntPtr terminal, bool started, float introTimer, float durationTimer, float time)
        {
            State = state; Terminal = terminal; Started = started;
            IntroTimer = introTimer; DurationTimer = durationTimer; Time = time;
        }

        internal IntPtr State { get; }
        internal IntPtr Terminal { get; }
        internal bool Started { get; }
        internal float IntroTimer { get; }
        internal float DurationTimer { get; }
        internal float Time { get; }
        internal bool CompletionDue => State != IntPtr.Zero && Started && Time > IntroTimer && Time > DurationTimer;
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_TERM_DataMining __instance, out Frame __state)
        => __state = Capture(__instance);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_TERM_DataMining __instance, bool __runOriginal, Frame __state)
        => Completed(__instance, __state, __runOriginal);

    internal static Frame Capture(LG_TERM_DataMining state)
    {
        if (!Session.IsHost || !LG_Factory.BuildDone || state == null || state.WasCollected
            || state.m_terminal is not { } terminal || ForgeCore.NativeObjects.Gone(terminal)
            || terminal.CurrentStateName != TERM_State.DataMining
            || TerminalStateReadback.IsReactorConsole(terminal)) return default;
        return new Frame(state.Pointer, terminal.Pointer, state.m_downloadStarted,
            state.m_introTimer, state.m_durationTimer, Clock.Time);
    }

    internal static void Completed(LG_TERM_DataMining state, Frame frame, bool ranOriginal)
    {
        if (!ranOriginal || !frame.CompletionDue || !Session.IsHost || !LG_Factory.BuildDone
            || state == null || state.WasCollected || state.Pointer != frame.State
            || state.m_terminal is not { } terminal || ForgeCore.NativeObjects.Gone(terminal) || terminal.Pointer != frame.Terminal
            || state.m_downloadStarted != frame.Started || state.m_introTimer != frame.IntroTimer
            || state.m_durationTimer != frame.DurationTimer || TerminalStateReadback.IsReactorConsole(terminal)) return;
        if (MapHost.Session is { } session)
            session.Run(session.MapObjects,
                module => module.TerminalCommandFinished(terminal, (int)TERM_Command.DownloadData,
                    MapObjectOutcomes.Succeeded),
                "terminal result observation");
    }
}

/// <summary>The weak lock's own replicated state, which is the only place a smashed or hacked lock is visible.
/// The lock has no address of its own, so the fact is published against the door the lock belongs to; a weak
/// lock whose door cannot be addressed publishes nothing rather than a lock with an invented owner.</summary>
[HarmonyPatch(typeof(LG_WeakLock), nameof(LG_WeakLock.OnStateChange))]
internal static class WeakLockBrokenReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakLock __instance)
    {
        if (!Session.IsHost) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakLockChanged(__instance));
    }
}
