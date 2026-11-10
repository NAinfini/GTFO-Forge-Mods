using ForgeCore;
using System;
using System.Collections.Generic;
using ChainedPuzzles;
using GameData;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeReplay;

// The recorder hooks found by walking the game system by system (the list is DEVELOPERS.md, event numbers 62 and up): what was said, what a
// terminal printed, level events, lights, the level objects that had no event, thrown things. Same rules as Hooks.cs. A method another package
// patches is not patched here: it is read by polling, or from a method beside it, or through ForgeCore.
internal static partial class Hooks
{
    private static void InstallWorldBrackets()
    {
        Subscribe("WorldEvent", () => ForgeCore.GameHooks.WorldEventExecuted.After((call, _) => WorldEventRan(call.Data, call.Result)));
        Subscribe("Intel.Sub", () => ForgeCore.GameHooks.SubObjectiveShown.After((call, _) => Intel(1, call.NewText, call.Duration)));
        Subscribe("Intel.Priority", () => ForgeCore.GameHooks.PrioritySubObjectiveShown.After((call, _) => Intel(2, call.NewText, call.Duration)));
        // The warden intel line comes from ForgeUI, which owns the one patch of PlayerGuiLayer.ShowWardenIntel; sub-objective updates are the two hooks above.
        Subscribe("Intel.Warden", () => ForgeUI.UIKit.Intel.Changed += () => { if (!ForgeUI.UIKit.Intel.IsSubObjective) Intel(0, ForgeUI.UIKit.Intel.Text, ForgeUI.UIKit.Intel.Duration); });
    }

    private static readonly Dictionary<ulong, ulong> TerminalUser = new();
    private static readonly bool[] Charging = new bool[4];
    private static string? _lastIntel, _lastReactorText;
    private static byte[]? _lastLights;
    private static readonly List<ThrownItem> ThrownItems = new();
    private static readonly Dictionary<ushort, (bool Exploded, bool Glued)> SpitterStates = new();
    private static (bool Active, int Seconds, bool Blink, string Title) _lastTimer;

    private static void ResetWorldState()
    {
        TerminalUser.Clear(); Array.Clear(Charging); _lastIntel = null; _lastReactorText = null; _lastLights = null; ThrownItems.Clear(); SpitterStates.Clear(); ItemHome.Clear(); _lastTimer = default;
    }

    // Slot + 1 of a player named in a state packet, 0 none.
    private static ulong PlayerOf(SNetStructs.pPlayer player)
        => player.TryGetPlayer(out var who) && who != null && who.PlayerSlotIndex() >= 0 ? (ulong)who.PlayerSlotIndex() + 1 : 0UL;

    private static ulong Scaled(float value, float scale) => (ulong)Mathf.Max(0, Mathf.RoundToInt(value * scale));

    // 64 Say: U voice number (the game's playerID), U sound event, U seed, U dialogue it belongs to, U dialogue it starts, U subtitle, U slot + 1. Runs on every machine.
    [HarmonyPatch]
    private static class Say
    {
        [HarmonyPatch(typeof(PlayerVoiceManager), nameof(PlayerVoiceManager.DoSayLine))]
        [HarmonyPostfix]
        private static void Said(PlayerVoiceManager __instance, PlayerVoiceManager.pSayLine data)
        {
            if (!Rec.On) return;
            try
            {
                var voices = __instance.m_playerVoices;
                var voice = voices != null && data.playerID >= 0 && data.playerID < voices.Length ? voices[data.playerID] : null;
                var owner = voice == null ? null : voice.m_owner;
                Rec.Begin().U((ulong)Math.Max(0, data.playerID)).U(data.eventID).U(data.seed).U(data.inDialogID).U(data.startDialogID).U(data.subtitleId).U(owner == null ? 0UL : (ulong)owner.PlayerSlotIndex + 1);
                Rec.Emit(EventKind.Say);
            }
            catch (Exception e) { Rec.Fail("say", e); }
        }
    }

    // 65 WorldEvent: a level event the game scheduled (it happens Delay seconds later; what it does to doors, lights, fog and waves has its own events).
    // U type (eWardenObjectiveEventType), U accepted, U trigger, U layer, U dimension, U zone, U delay x 100, U duration x 100, U enabled, U sound, U dialogue, U fog, U fog transition x 100,
    // U count, U enemy, Pos, Text warden intel, Text sub objective header, Text sub objective, Text sound subtitle, Text object filter.
    private static void WorldEventRan(WardenObjectiveEventData data, bool accepted)
    {
        if (!Rec.On || data == null) return;
        try
        {
            var p = data.Position;
            Rec.Begin().U((ulong)(int)data.Type).U(accepted ? 1UL : 0UL).U((ulong)(int)data.Trigger).U((ulong)(int)data.Layer).U((ulong)(int)data.DimensionIndex).U((ulong)(int)data.LocalIndex)
                .U(Scaled(data.Delay, 100f)).U(Scaled(data.Duration, 100f)).U(data.Enabled ? 1UL : 0UL).U(data.SoundID).U(data.DialogueID).U(data.FogSetting).U(Scaled(data.FogTransitionDuration, 100f))
                .U((ulong)Math.Max(0, data.Count)).U(data.EnemyID).Pos(p.x, p.y, p.z)
                .Text(data.WardenIntel?.ToString()).Text(data.CustomSubObjectiveHeader?.ToString()).Text(data.CustomSubObjective?.ToString()).Text(data.SoundSubtitle?.ToString()).Text(data.WorldEventObjectFilter);
            Rec.Emit(EventKind.WorldEvent);
        }
        catch (Exception e) { Rec.Fail("world-event", e); }
    }

    // 67 Intel: U kind (0 warden intel line, 1 new sub objective, 2 the priority one), U duration x 10, Text as the recorder's game showed it.
    private static void Intel(ulong kind, string? text, float duration)
    {
        if (!Rec.On || string.IsNullOrEmpty(text) || (text == _lastIntel && kind == 0)) return;
        try
        {
            if (kind == 0) _lastIntel = text;
            Rec.Begin().U(kind).U(Scaled(duration, 10f)).Text(text, 1000);
            Rec.Emit(EventKind.Intel);
        }
        catch (Exception e) { Rec.Fail("intel", e); }
    }

    // 66 ZoneLights (state, key 0): U 0, then per dimension U layer count, per layer U zone count and one U per zone (the game's light mode byte), after a U dimension count;
    // U sequence sections, U section delay x 1000, Pos sequence start. Written once at the start of play and when any of it changed. Environment (44) keeps the fog.
    internal static void ZoneLights(pEnvironmentState state)
    {
        var dimensions = state.DimensionLightState;
        if (!Rec.On || dimensions == null) return;
        var write = Rec.Begin().U(0).U((ulong)dimensions.Length);
        for (var d = 0; d < dimensions.Length; d++)
        {
            var layers = dimensions[d].LayerLightState;
            var layerCount = layers == null ? 0 : layers.Length;
            write.U((ulong)layerCount);
            for (var l = 0; l < layerCount; l++)
            {
                var zones = layers![l].ZoneLightsState;
                var zoneCount = zones == null ? 0 : zones.Length;
                write.U((ulong)zoneCount);
                for (var z = 0; z < zoneCount; z++) write.U(zones![z]);
            }
        }
        var sequence = state.LatestLightSequenceInteraction;
        write.U((ulong)Math.Max(0, sequence.SectionCount)).U(Scaled(sequence.SectionDelay, 1000f)).Pos(sequence.Position.x, sequence.Position.y, sequence.Position.z);
        var built = Rec.Built;
        if (_lastLights != null && built.SequenceEqual(_lastLights)) return;
        _lastLights = built.ToArray();
        Rec.EmitState(EventKind.ZoneLights, 0);
    }

    // The game writes the lights only when they change; the state they start in is written once here, so a jump to before the first change has something to show.
    internal static void PollLights()
    {
        if (_lastLights != null || !Rec.On || !InPlay) return;
        try
        {
            var manager = EnvironmentStateManager.Current;
            if (manager != null && manager.m_stateReplicator != null) ZoneLights(manager.m_stateReplicator.State);
        }
        catch (Exception e) { Rec.Fail("zone-lights", e); }
    }

    // Terminals: 73 TerminalLine: U key, U line type (TerminalLineType, 255 a plain line), U wait in ms, Text. 74 TerminalState (state): U key, U TERM_State, U slot + 1 of who is at it, Pos.
    // 75 TerminalInput: U key, Text (what a remote player has typed; a line that is sent is Terminal, 24).
    // A terminal prints its start-up text while the level loads; only what happens in play is written.
    private static bool InPlay => GameStateManager.CurrentStateName == eGameStateName.InLevel;

    private static void TerminalPrinted(LG_ComputerTerminalCommandInterpreter interpreter, ulong type, float wait, string? line)
    {
        if (!Rec.On || string.IsNullOrEmpty(line) || !InPlay) return;
        try
        {
            var terminal = interpreter.m_terminal;
            if (terminal == null) return;
            Rec.Begin().U(Rec.Key(terminal)).U(type).U(Scaled(wait, 1000f)).Text(line, 2000);
            Rec.Emit(EventKind.TerminalLine);
        }
        catch (Exception e) { Rec.Fail("terminal-line", e); }
    }

    [HarmonyPatch]
    private static class TerminalOutput
    {
        [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.AddOutput), new[] { typeof(TerminalLineType), typeof(string), typeof(float), typeof(TerminalSoundType), typeof(TerminalSoundType) })]
        [HarmonyPrefix]
        private static void Typed(LG_ComputerTerminalCommandInterpreter __instance, TerminalLineType type, string line, float time) => TerminalPrinted(__instance, (ulong)(int)type, time, line);

        // The typed one with a number finds its text and comes to Typed; the plain one with a number and the prefixed one print by themselves, below.
        [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.AddOutput), new[] { typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        private static void Plain(LG_ComputerTerminalCommandInterpreter __instance, string originalLine) => TerminalPrinted(__instance, 255, 0f, originalLine);

        [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.AddOutput), new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
        [HarmonyPrefix]
        private static void Lines(LG_ComputerTerminalCommandInterpreter __instance, Il2CppSystem.Collections.Generic.List<string> lines)
        {
            if (lines == null) return;
            for (var i = 0; i < lines.Count; i++) TerminalPrinted(__instance, 255, 0f, lines[i]);
        }

        [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.AddOutput), new[] { typeof(uint), typeof(bool) })]
        [HarmonyPrefix]
        private static void Numbered(LG_ComputerTerminalCommandInterpreter __instance, uint originalLine)
        {
            if (Rec.On && InPlay) TerminalPrinted(__instance, 255, 0f, Localization.Text.Get(originalLine));
        }

        [HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.AddOutputWithPrefixAndTabbedIndentation))]
        [HarmonyPrefix]
        private static void Prefixed(LG_ComputerTerminalCommandInterpreter __instance, string prefix, string textToIndent) => TerminalPrinted(__instance, 255, 0f, prefix + textToIndent);
    }

    [HarmonyPatch]
    private static class TerminalUse
    {
        [HarmonyPatch(typeof(LG_ComputerTerminalManager), nameof(LG_ComputerTerminalManager.DoChangeTerminalState))]
        [HarmonyPostfix]
        private static void State(LG_ComputerTerminalManager.pTerminalState data)
        {
            if (!Rec.On) return;
            try
            {
                var terminal = LG_ComputerTerminalManager.GetTerminal(data.ID);
                if (terminal == null) return;
                var key = Rec.Key(terminal);
                var user = PlayerOf(data.interactingPlayer);
                if (user != 0) TerminalUser[key] = user;
                World(EventKind.TerminalState, terminal, key, (ulong)Math.Max(0, data.state), user);
            }
            catch (Exception e) { Rec.Fail("terminal-state", e); }
        }

        [HarmonyPatch(typeof(LG_ComputerTerminalManager), nameof(LG_ComputerTerminalManager.DoSendTerminalString))]
        [HarmonyPostfix]
        private static void Typed(LG_ComputerTerminalManager.pTerminalString data)
        {
            if (!Rec.On) return;
            try
            {
                var terminal = LG_ComputerTerminalManager.GetTerminal(data.ID);
                if (terminal == null) return;
                Rec.Begin().U(Rec.Key(terminal)).Text(data.inputString.data);
                Rec.Emit(EventKind.TerminalInput);
            }
            catch (Exception e) { Rec.Fail("terminal-input", e); }
        }
    }

    // The player last at a terminal, slot + 1: the tail of Terminal (24).
    private static ulong TerminalUserOf(ulong key) => TerminalUser.TryGetValue(key, out var user) ? user : 0UL;

    // Level objects with one state each, the common layout U key, U state, U extra, Pos.
    // 76 WeakLock: extra = open + damage taken x 10 x 2. 77 Bulkhead, 78 HsuActivator, 79 Portal: extra = slot + 1 or the sequence flag. 80 GeneratorCluster: state = fog step.
    // 81 FogTurbine: state = status byte, extra = owner kind, then U player slot + 1. 82 Cluster: extra = progress percent. 85 Spitter: state 1 exploding, 2 glued, key = its index.
    [HarmonyPatch]
    private static class LockHook
    {
        // OnStateChange is another package's; every status change comes through here.
        [HarmonyPatch(typeof(LG_WeakLock), nameof(LG_WeakLock.OnSyncStatusChanged))]
        [HarmonyPostfix]
        private static void Lock(LG_WeakLock __instance, eWeakLockStatus status, bool open)
        {
            try { World(EventKind.WeakLock, __instance, Rec.Key(__instance), (ulong)(int)status, open ? 1UL : 0UL); } catch (Exception e) { Rec.Fail("weak-lock", e); }
        }
    }

    [HarmonyPatch]
    private static class BulkheadHook
    {
        [HarmonyPatch(typeof(LG_BulkheadDoorController_Core), nameof(LG_BulkheadDoorController_Core.OnStateChange))]
        [HarmonyPostfix]
        private static void Bulkhead(LG_BulkheadDoorController_Core __instance, pBulkheadDCState newState)
        {
            try { World(EventKind.Bulkhead, __instance, Rec.Key(__instance), (ulong)(int)newState.status, PlayerOf(newState.playerThatChangedTheState)); } catch (Exception e) { Rec.Fail("bulkhead", e); }
        }
    }

    [HarmonyPatch]
    private static class HsuHook
    {
        [HarmonyPatch(typeof(LG_HSUActivator_Core), nameof(LG_HSUActivator_Core.OnStateChange))]
        [HarmonyPostfix]
        private static void Hsu(LG_HSUActivator_Core __instance, pHSUActivatorState newState)
        {
            try { World(EventKind.HsuActivator, __instance, Rec.Key(__instance), (ulong)(int)newState.status, newState.isSequenceIncomplete ? 1UL : 0UL); } catch (Exception e) { Rec.Fail("hsu", e); }
        }
    }

    [HarmonyPatch]
    private static class PortalHook
    {
        [HarmonyPatch(typeof(LG_DimensionPortal), nameof(LG_DimensionPortal.OnStateChange))]
        [HarmonyPostfix]
        private static void Portal(LG_DimensionPortal __instance, pDimensionPortalState newState)
        {
            try { World(EventKind.Portal, __instance, Rec.Key(__instance), (ulong)(int)newState.status, (newState.isClosed ? 1UL : 0UL) | (newState.isSequenceIncomplete ? 2UL : 0UL)); } catch (Exception e) { Rec.Fail("portal", e); }
        }
    }

    [HarmonyPatch]
    private static class ClusterHook
    {
        // OnStateChange is another package's; the fog step moves here.
        [HarmonyPatch(typeof(LG_PowerGeneratorCluster), nameof(LG_PowerGeneratorCluster.SetFogIndex))]
        [HarmonyPostfix]
        private static void Cluster(LG_PowerGeneratorCluster __instance, int index)
        {
            try { World(EventKind.GeneratorCluster, __instance, Rec.Key(__instance), (ulong)Math.Max(0, index)); } catch (Exception e) { Rec.Fail("generator-cluster", e); }
        }
    }

    [HarmonyPatch]
    private static class TurbineHook
    {
        [HarmonyPatch(typeof(HeavyFogRepellerGlobalState), nameof(HeavyFogRepellerGlobalState.OnStateChange))]
        [HarmonyPostfix]
        private static void Turbine(HeavyFogRepellerGlobalState __instance, pCarryItemWithGlobalState_State newState)
        {
            if (!Rec.On) return;
            try
            {
                var key = Rec.Key(__instance);
                // The place of the object: where it stands, or, carried by a player (its static place is then empty), where the turbine is.
                var p = newState.owner == eCarryItemWithGlobalStateOwner.StaticPosition ? newState.staticPosition : __instance.transform.position;
                Rec.Begin().U(key).U(newState.status).U((ulong)(int)newState.owner).Pos(p.x, p.y, p.z).U(PlayerOf(newState.player));
                Rec.EmitState(EventKind.FogTurbine, key);
            }
            catch (Exception e) { Rec.Fail("fog-turbine", e); }
        }
    }

    [HarmonyPatch]
    private static class PuzzleClusterHook
    {
        [HarmonyPatch(typeof(CP_Cluster_Core), nameof(CP_Cluster_Core.OnSyncStateChange))]
        [HarmonyPostfix]
        private static void PuzzleCluster(CP_Cluster_Core __instance, eClusterStatus newStatus, float progress)
        {
            try { World(EventKind.Cluster, __instance, Rec.Key(__instance), (ulong)(int)newStatus, Scaled(Mathf.Clamp01(progress), 100f)); } catch (Exception e) { Rec.Fail("cluster", e); }
        }
    }

    [HarmonyPatch]
    private static class LaidHook
    {

        // 83 Holopath: U key, U phase (0 the path was laid, 1 it is being revealed, 2 shown, 3 hidden), U 0, Pos of the object, Pos from, Pos to (the tip when revealed).
        [HarmonyPatch(typeof(CP_Holopath_Spline), nameof(CP_Holopath_Spline.GeneratePath))]
        [HarmonyPostfix]
        private static void Laid(CP_Holopath_Spline __instance, Vector3 from, Vector3 to) => Path(__instance, 0, from, to);

        [HarmonyPatch(typeof(CP_Holopath_Spline), nameof(CP_Holopath_Spline.Reveal))]
        [HarmonyPostfix]
        private static void Revealed(CP_Holopath_Spline __instance) => Path(__instance, 1, Vec.Zero, __instance.TipPos);

        private static void Path(CP_Holopath_Spline spline, ulong phase, Vector3 from, Vector3 to)
        {
            if (!Rec.On) return;
            try
            {
                var key = Rec.Key(spline);
                var p = spline.transform.position;
                Rec.Begin().U(key).U(phase).U(0).Pos(p.x, p.y, p.z).Pos(from.x, from.y, from.z).Pos(to.x, to.y, to.z);
                Rec.EmitState(EventKind.Holopath, key);
            }
            catch (Exception e) { Rec.Fail("holopath", e); }
        }
    }

    [HarmonyPatch]
    private static class ScanMovedHook
    {
        // 84 ScanMove (state, key = the scan's key in Scan, 21): U key, U lerp x 1000, U paused, Pos now.
        [HarmonyPatch(typeof(CP_BasicMovable), nameof(CP_BasicMovable.OnStateChange))]
        [HarmonyPostfix]
        private static void ScanMoved(CP_BasicMovable __instance, pMovableStateSync newState)
        {
            if (!Rec.On) return;
            try
            {
                var core = __instance.GetComponent<CP_Bioscan_Core>();
                var key = core != null ? Rec.Key(core) : Rec.Key(__instance);
                var p = __instance.transform.position;
                Rec.Begin().U(key).U(Scaled(newState.lerp, 1000f)).U(newState.paused ? 1UL : 0UL).Pos(p.x, p.y, p.z);
                Rec.EmitState(EventKind.ScanMove, key);
            }
            catch (Exception e) { Rec.Fail("scan-move", e); }
        }
    }

    [HarmonyPatch]
    private static class HeldHook
    {
        // 87 InteractHold (state, key slot): U slot, U 1 holding / 0 let go, Pos of what is held (a door, a box, a revive). Local holds and, through the state change, remote ones.
        [HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.OnInteractorStateChanged))]
        [HarmonyPostfix]
        private static void Held(Interact_Timed __instance, PlayerAgent sourceAgent, bool state)
        {
            // The game also lets go of a hold when the level is torn down (Temp 11-05: right after the expedition ended), with the held
            // object already destroyed: Unity's own null says so (its transform would throw). That one release is not recorded.
            if (!Rec.On || sourceAgent == null || sourceAgent.WasCollected || __instance == null || __instance.WasCollected) return;
            try
            {
                var p = __instance.transform.position;
                Rec.Begin().U(Slot(sourceAgent)).U(state ? 1UL : 0UL).Pos(p.x, p.y, p.z);
                Rec.EmitState(EventKind.InteractHold, Slot(sourceAgent));
            }
            catch (Exception e) { Rec.Fail("interact-hold", e); }
        }
    }

    [HarmonyPatch]
    private static class RemoteChargeHook
    {
        // 63 MeleeCharge (state, key slot): U slot, U charging. A remote swing's charge arrives here; the recorder's own is polled (the game's charge state is another package's).
        // A remote swing arrives here too, as the fire flag turning on: 12 Melee with kind heavy when the weapon was charging, else light, as the
        // game's third-person weapon plays it (the recorder's own swings come from its first-person states).
        [HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.ReceiveClassItemSync))]
        [HarmonyPrefix]
        private static void Before(MeleeWeaponThirdPerson __instance, out (bool Charging, bool Firing) __state)
        {
            __state = default;
            if (!Rec.On) return;
            try { __state = (__instance.IsCharging, __instance.m_lastSyncData.inFireMode); } catch (Exception e) { Rec.Fail("melee-charge", e); }
        }

        [HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.ReceiveClassItemSync))]
        [HarmonyPostfix]
        private static void RemoteCharge(MeleeWeaponThirdPerson __instance, PlayerInventoryBase.pSimpleItemSyncData __0, (bool Charging, bool Firing) __state)
        {
            if (!Rec.On) return;
            try
            {
                var owner = __instance.Owner;
                if (__0.inFireMode && !__state.Firing && owner != null && owner != Players.Local) Melee.Swing(owner, __state.Charging ? 1UL : 0UL);
                NoteCharge(owner, __instance.IsCharging);
            }
            catch (Exception e) { Rec.Fail("melee-charge", e); }
        }
    }

    [HarmonyPatch]
    private static class MeleeHitHook
    {
        // 100 MeleeHit: U wielder slot + 1, Pos where it hit, Pos the surface's normal, U spawns a decal, U material id, U feedback id. The game's broadcast of a swing that landed, which
        // arrives on the other machines; the wielder's own machine does not get it (its hits are its EnemyHurt readings).
        [HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.IncomingMeleeHit))]
        [HarmonyPrefix]
        private static void Incoming(pMeleeHitPacketStruct data)
        {
            if (!Rec.On) return;
            try
            {
                var wielder = data.WielderAgent.TryGet(out var agent) && agent != null ? (ulong)agent.PlayerSlotIndex + 1UL : 0UL;
                Rec.Begin().U(wielder).Pos(data.Position.x, data.Position.y, data.Position.z).Pos(data.Normal.x, data.Normal.y, data.Normal.z)
                    .U(data.SpawnDecal ? 1UL : 0UL).U((ulong)Mathf.Max(0, data.MaterialID)).U(data.FeedbackID);
                Rec.Emit(EventKind.MeleeHit);
            }
            catch (Exception e) { Rec.Fail("melee-hit", e); }
        }
    }

    [HarmonyPatch]
    private static class HeartbeatHook
    {
        // 70 EnemyBeat: U entity, U strength x 100, Pos what it senses. A sleeper's heartbeat, on the host where it is decided and on a client where it arrives.
        [HarmonyPatch(typeof(Enemies.ES_Hibernate), nameof(Enemies.ES_Hibernate.DoHeartbeat))]
        [HarmonyPostfix]
        private static void Heartbeat(Enemies.ES_Hibernate __instance, float heartbeatStrength, Vector3 detectPos)
        {
            if (!Rec.On) return;
            try
            {
                var entity = EnemyRecorder.Entity(__instance.m_enemyAgent);
                if (entity < 0) return;
                Rec.Begin().U((ulong)entity).U(Scaled(heartbeatStrength, 100f)).Pos(detectPos.x, detectPos.y, detectPos.z);
                Rec.Emit(EventKind.EnemyBeat);
            }
            catch (Exception e) { Rec.Fail("enemy-beat", e); }
        }

    }

    internal static void NoteCharge(PlayerAgent? owner, bool charging)
    {
        if (owner == null) return;
        var slot = owner.PlayerSlotIndex;
        if ((uint)slot >= 4 || Charging[slot] == charging) return;
        Charging[slot] = charging;
        Rec.Begin().U((ulong)slot).U(charging ? 1UL : 0UL);
        Rec.EmitState(EventKind.MeleeCharge, (ulong)slot);
    }

    // 72 Thrown (state, key the item): U key, U phase (0 moving, 1 at rest, 2 gone), U extra (a glow stick's colour; a fog sphere's eFogRepellerSphereState + range x 10 x 16), Pos, U item data block, U owner slot + 1.
    // Whatever the game spawned as an instance (ItemMode.Instance) that is not a mine or a sentry, which have their own events.
    private sealed class ThrownItem
    {
        internal Item Item = null!;
        internal ulong Key, ItemId, Owner, Phase = ulong.MaxValue, Extra;
        internal Vector3 Last;
    }

    private const float ThrownStep = 0.05f;
    private const int InstanceMode = 3;

    internal static void NoteSpawned(pItemSpawnData data, ItemReplicator replicator)
    {
        if ((int)data.mode != InstanceMode || replicator == null) return;
        var item = replicator.Item;
        if (item == null || item.TryCast<MineDeployerInstance>() != null || item.TryCast<SentryGunInstance>() != null) return;
        ThrownItems.Add(new ThrownItem { Item = item, Key = Rec.Key(item), ItemId = data.itemData.itemID_gearCRC, Owner = OwnerExtra(item) });
        ThrownPoll(ThrownItems[^1]);
    }

    internal static void PollThrown()
    {
        for (var i = ThrownItems.Count - 1; i >= 0; i--)
            if (!ThrownPoll(ThrownItems[i])) ThrownItems.RemoveAt(i);
    }

    // False when the item is gone.
    private static bool ThrownPoll(ThrownItem thrown)
    {
        try
        {
            var item = thrown.Item;
            if (item == null || item.WasCollected || !item.gameObject.activeInHierarchy)
            {
                ThrownWrite(thrown, 2, thrown.Last, thrown.Extra);
                return false;
            }
            var p = item.transform.position;
            var moved = Vec.SqrLength(p - thrown.Last) > ThrownStep * ThrownStep;
            var extra = ThrownExtra(item);
            var phase = moved ? 0UL : 1UL;
            if (moved || phase != thrown.Phase || extra != thrown.Extra) ThrownWrite(thrown, phase, p, extra);
            return true;
        }
        catch (Exception e) { Rec.Fail("thrown", e); return false; }
    }

    private static ulong ThrownExtra(Item item)
    {
        var stick = item.TryCast<GlowstickInstance>();
        if (stick != null) return (ulong)(int)stick.GlowStickColor;
        var repeller = item.TryCast<FogRepellerInstance>();
        if (repeller?.m_repellerSphere is { } sphere) return (ulong)(uint)(int)sphere.CurrentState | Scaled(sphere.Range, 10f) << 4;
        return 0UL;
    }

    private static void ThrownWrite(ThrownItem thrown, ulong phase, Vector3 p, ulong extra)
    {
        thrown.Phase = phase; thrown.Last = p; thrown.Extra = extra;
        if (!Rec.On) return;
        Rec.Begin().U(thrown.Key).U(phase).U(extra).Pos(p.x, p.y, p.z).U(thrown.ItemId).U(thrown.Owner);
        Rec.EmitState(EventKind.Thrown, thrown.Key);
    }

    // 85 Spitter (state, key its index): U index, U 1 exploding / 2 glued / 0 neither, U 0, Pos. Polled: DoExplode is another package's.
    internal static void PollSpitters()
    {
        if (!Rec.On) return;
        try
        {
            var all = InfectionSpitter.s_allSpitters;
            if (all == null) return;
            for (var i = 0; i < all.Count; i++)
            {
                var spitter = all[i];
                if (spitter == null) continue;
                var now = (spitter.m_isExploding, spitter.m_isGlued);
                var index = spitter.m_spitterIndex;
                SpitterStates.TryGetValue(index, out var was);
                if (was == now) continue;
                SpitterStates[index] = now;
                var p = spitter.m_position;
                Rec.Begin().U(index).U(now.m_isExploding ? 1UL : now.m_isGlued ? 2UL : 0UL).U(0).Pos(p.x, p.y, p.z);
                Rec.EmitState(EventKind.Spitter, index);
            }
        }
        catch (Exception e) { Rec.Fail("spitter", e); }
    }

    // 68 ObjectiveTimer (state, key 0): U 0, U shown, U seconds left, U blinking, Text title. Polled: the game's timer calls are another package's.
    internal static void PollTimer()
    {
        if (!Rec.On) return;
        try
        {
            var timer = GuiManager.PlayerLayer?.m_objectiveTimer;
            if (timer == null) return;
            var active = timer.gameObject.activeInHierarchy;
            var now = (active, active ? Mathf.CeilToInt(Mathf.Max(0f, timer.m_lastSetTimer)) : 0, active && timer.IsBlinking, active ? timer.CurrentTimerTitle ?? "" : "");
            if (now == _lastTimer || (!now.Item1 && !_lastTimer.Active)) return;
            _lastTimer = now;
            Rec.Begin().U(0).U(now.Item1 ? 1UL : 0UL).U((ulong)now.Item2).U(now.Item3 ? 1UL : 0UL).Text(now.Item4);
            Rec.EmitState(EventKind.ObjectiveTimer, 0);
        }
        catch (Exception e) { Rec.Fail("objective-timer", e); }
    }
}
