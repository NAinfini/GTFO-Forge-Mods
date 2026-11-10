using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Enemies;
using Expedition;
using GTFO.API;
using IRF;
using UnityEngine;
using UnityEngine.Rendering;
using AirParticles = AirParticleSystem.AirParticleSystem;


namespace ForgePerformance.LoadShedding;

// The collector remains an independent observer. This class owns only local
// presentation and a level lifetime; it never submits a game/network command.
internal sealed partial class LoadSheddingController
{
    internal static LoadSheddingController? Current;
    internal bool Active { get; private set; }
    internal int LevelSequence { get; private set; }
    private readonly ManualLogSource _log;
    internal readonly SheddingSettings Settings;
    internal readonly DecorationShedding Decorations;
    // Level-start pieces, drained a few milliseconds per frame (LevelWork).
    internal readonly LevelWork Work;
    internal readonly AmbientDustSuppression Dust = new();
    internal readonly VisorLiquidSuppression Visor;
    internal readonly ExtraCuts Extras;
    internal readonly ClosedDoorDepth Doors;
    private readonly SheddingHooks _hooks = new();
    private bool _installed, _restoring, _evicting;
    private readonly Dictionary<IntPtr, ShadowState> _shadows = new();
    private readonly Dictionary<IntPtr, ES_Dead> _ragdolls = new();
    private readonly HashSet<IntPtr> _requestedRagdolls = new();
    private readonly RagdollOrder _order;
    internal readonly LocalCorpseVisuals Corpses;
    private readonly Dictionary<IntPtr, ES_Dead._DecayDelayed_d__20> _corpseIterators = new();
    private readonly Dictionary<IntPtr, WeaponShellManager._EjectShellInternal_d__11> _shellIterators = new(16);
    private readonly Dictionary<IntPtr, AmbientState> _ambient = new();
    private readonly Dictionary<IntPtr, AirState> _air = new();
    private readonly HashSet<IntPtr> _gardens = new();
    private readonly Dictionary<IntPtr, GardenFeature> _gardenFeatures = new();
    private readonly Dictionary<IntPtr, GardenFeature> _gardenRenderers = new();
    private int _shellFrame = -1, _shellBudget;
    private long _gardenSeen, _gardenChanged;
    private long _corpseShadowSeen, _corpseShadowChanged, _corpseShadowRestored;
    private readonly Dictionary<IntPtr, Renderer> _stealthBodies = new();
    private long _stealthHidden, _stealthRestored;
    private long _ragdollSeen, _ragdollEvicted, _ambientChanged;
    private long _ragdollRefreshSeen, _ragdollRefreshed, _ragdollRefreshFailures;
    private long _shellSeen, _shellSuppressed;
    private long _gardenRestored, _ambientRestored;
    private int _restoreFailures;
    private long _gardenInstances, _gardenShadowCalls, _gardenScopesReleased;
    private long _ambientUpdateCalls, _ambientSkippedUpdates;

    private readonly SheddingHookPlan _plan;

    internal LoadSheddingController(ManualLogSource log, SheddingHookPlan plan)
    {
        _plan = plan;
        var settings = plan.Settings;
        _log = log; Settings = settings; Work = new LevelWork(line => log.LogError(line)); _order = new RagdollOrder(settings.RagdollLimit);
        Corpses = new LocalCorpseVisuals(settings.CorpseLifetime);
        Decorations = new DecorationShedding(this);
        Visor = new VisorLiquidSuppression(RefuseVisor);
        Extras = new ExtraCuts(this, settings);
        Doors = new ClosedDoorDepth(settings.ClosedDoorDepth);
    }

    internal void Install()
    {
        if (_installed) throw new InvalidOperationException("Load shedding is already installed.");
        if (NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out var reason) == IntPtr.Zero)
            throw new InvalidOperationException("Load shedding refuses an unverified game assembly: " + reason);
        SheddingHooks.VerifyLayout(Settings);
        Current = this;
        try
        {
            _hooks.Install(_plan);
            Extras.Install();
            // GTFO-API's event: the game's own GameStateManager.OnGameStateChanged delegate added from a mod never fires.
            EventAPI.OnGameStateChanged += OnStateChanged;
            _installed = true;
            if (LevelBuilt(GameStateManager.CurrentStateName)) BeginLevel();
        }
        catch
        {
            EventAPI.OnGameStateChanged -= OnStateChanged;
            Extras.Dispose(); _hooks.Dispose(); Current = null;
            throw;
        }
    }

    private void OnStateChanged(eGameStateName oldState, eGameStateName newState)
    {
        // A failing EndLevel must not stop the event's other subscribers or the BeginLevel below.
        if (Active && !LevelBuilt(newState))
            try { EndLevel(); } catch (Exception error) { LogExtraFailure("end-level", error.GetType().Name + ": " + error.Message); }
        if (!Active && LevelBuilt(newState)) BeginLevel();
    }
    // The game builds the level while the state is still Generating. The level is complete when the elevator ride waits to stop (ReadyToStopElevatorRide),
    // the stretch of loading before InLevel in which the game itself stalls and the player sees no gameplay: the level is set up from there on, not on the landing frame.
    // A checkpoint recall comes back straight into InLevel and sets up there.
    private static bool LevelBuilt(eGameStateName state) => state is eGameStateName.ReadyToStopElevatorRide or eGameStateName.StopElevatorRide
        or eGameStateName.ReadyToStartLevel or eGameStateName.InLevel;

    // The game builds the level while the state is still Generating, so by the time the level is built it is complete:
    // what needs the finished level (decoration scan, spitters, door graph) is queued at the end of BeginLevel and runs over the next frames.
    private void SetupBuiltLevel()
    {
        Decorations.ScanLevel();
        Work.Add("spitters", Extras.BuildDone);
        if (Settings.ClosedDoorDepthLimit)
            Work.Add("doors", () =>
            {
                if (!Active) return;
                try { Doors.BuildDone(); }
                catch (Exception error)
                {
                    // The door events are left again and the graph dropped; the doors stay vanilla for this level.
                    try { Doors.EndLevel(); } catch (Exception restore) { error = new AggregateException(error, restore); }
                    LogExtraFailure("closed-door-depth", error.GetType().Name + ": " + error.Message);
                }
            });
    }

    // Only an enemy that is really dead (its locomotion is in the Dead state, or the Dead state is being entered) is touched.
    // EnemyAgent.Alive is not usable here: during EnemyAgent.Setup it is not set yet, so every enemy, stealth ones
    // included, looked dead and had its renderers switched off.
    internal void EnemyVisuals(EnemyAgent enemy, bool entering = false)
    {
        if (!Active || enemy == null || !Settings.CorpseShadows || IsRefused(SheddingCut.CorpseShadows)) return;
        if (!entering && enemy.Locomotion?.CurrentStateEnum != ES_StateEnum.Dead) return;
        // Allocation only on death, never a frame scan.
        foreach (var renderer in enemy.GetComponentsInChildren<Renderer>(true))
        {
            Shadow(renderer, false, true);
        }
        RefreshShadow(enemy);
    }
    // The enemy's shadow is not drawn from the renderers' current state: C_MovingCuller_Cullbucket keeps a list of
    // shadow renderers (every one whose shadowCastingMode is not Off), rebuilds it only when its command buffer is
    // marked dirty, and draws it with CommandBuffer.DrawRenderer, which ignores Renderer.enabled. A lying corpse also
    // stops asking the lights for a new composite shadow. Without this the corpse's shadow stays until the native decay
    // unregisters the culler, seconds after the body is gone. Called once per change, on the frame of the change.
    internal static void RefreshShadow(EnemyAgent enemy)
    {
        var moving = enemy.MovingCuller;
        if (moving == null) return;
        var culler = moving.Culler;
        if (culler == null) return;
        culler.SetDirtyCMDBuffer();
        culler.NeedsShadowRefresh = true;
    }
    // A stealth enemy's body is a ShadowsOnly renderer: the visible shadow itself. The moment the enemy dies it is switched
    // off here, so the shadow goes with the death instead of waiting for the native decay or any corpse timer. The enemy,
    // its colliders and the native decay coroutine are untouched; the renderer is switched back on when the enemy is set up
    // again (pooled) or the level ends.
    internal void HideStealthBody(EnemyAgent enemy)
    {
        if (!Active || enemy == null) return;
        var changed = false;
        foreach (var renderer in enemy.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !renderer.enabled || renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly) continue;
            // Off as well as disabled: the culler's shadow list draws disabled renderers (see RefreshShadow).
            renderer.enabled = false; renderer.shadowCastingMode = ShadowCastingMode.Off;
            _stealthBodies[renderer.Pointer] = renderer; _stealthHidden++; changed = true;
        }
        if (changed) RefreshShadow(enemy);
    }
    // A pooled enemy set up again is alive: the shadows its corpse lost come back now, not at the end of the level.
    internal void RestoreCorpseShadows(EnemyAgent enemy)
    {
        if (!Settings.CorpseShadows || _shadows.Count == 0) return;
        var changed = false;
        foreach (var renderer in enemy.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !_shadows.TryGetValue(renderer.Pointer, out var state) || !state.Corpse) continue;
            _shadows.Remove(renderer.Pointer);
            if (RestoreShadow(state)) { _corpseShadowRestored++; changed = true; }
        }
        if (changed) RefreshShadow(enemy);
    }
    private void RestoreStealthBody(Renderer renderer)
    {
        renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly; renderer.enabled = true; _stealthRestored++;
    }
    internal void RestoreStealthBodies(EnemyAgent? enemy)
    {
        if (_stealthBodies.Count == 0) return;
        if (enemy == null)
        {
            foreach (var renderer in _stealthBodies.Values) if (renderer != null) RestoreStealthBody(renderer);
            _stealthBodies.Clear(); return;
        }
        var changed = false;
        foreach (var renderer in enemy.GetComponentsInChildren<Renderer>(true))
            if (renderer != null && _stealthBodies.Remove(renderer.Pointer)) { RestoreStealthBody(renderer); changed = true; }
        if (changed) RefreshShadow(enemy);
    }
    private void Shadow(Renderer renderer, bool garden, bool corpse = false)
    {
        if (renderer == null) return;
        // A ShadowsOnly renderer is the visible body of a stealth (Shadow-type) enemy: the model is the cast shadow.
        // It is content, not decoration, so it is never touched and is dropped from the table if a new setup made it so.
        if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) { _shadows.Remove(renderer.Pointer); return; }
        if (_shadows.TryGetValue(renderer.Pointer, out var existing))
        {
            // A new native setup may have replaced either field. Capture the
            // new value before writing again; never retain the first snapshot.
            if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                existing = existing with { Original = renderer.shadowCastingMode };
            if (existing.Corpse != corpse && !garden)
            {
                existing = existing with { Corpse = corpse };
                if (corpse) { _corpseShadowSeen++; if (existing.Original != ShadowCastingMode.Off) _corpseShadowChanged++; }
            }
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _shadows[renderer.Pointer] = existing;
            return;
        }
        var original = renderer.shadowCastingMode;
        _shadows.Add(renderer.Pointer, new(renderer, original, renderer.enabled, garden, corpse, false));
        if (garden) _gardenSeen++; else _corpseShadowSeen++;
        if (original == ShadowCastingMode.Off) return;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        if (garden) _gardenChanged++; else _corpseShadowChanged++;
    }
    private static bool RestoreShadow(ShadowState state)
    {
        if (state.Renderer == null) return false;
        var restored = false;
        if (state.Original != ShadowCastingMode.Off && state.Renderer.shadowCastingMode == ShadowCastingMode.Off)
        { state.Renderer.shadowCastingMode = state.Original; restored = true; }
        if (state.Disabled && !state.Renderer.enabled)
        { state.Renderer.enabled = state.Enabled; restored = true; }
        return restored;
    }
    private void ApplyGarden()
    {
        foreach (var room in UnityEngine.Object.FindObjectsOfType<LG_Geomorph>())
        {
            if (room.m_assignedSubComplexFromZone != SubComplex.Gardens) continue;
            _gardens.Add(room.Pointer);
            var garden = room;
            Work.Add("garden", () => SetupCut(SheddingCut.Garden, () =>
            {
                // Keep geometry, colliders, culling/instancing and every behaviour.
                // Generic particles may be warning/mission cues; no blanket edit.
                foreach (var renderer in garden.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.enabled && renderer.gameObject.isStatic) Shadow(renderer, true);
                foreach (var feature in garden.GetComponentsInChildren<InstancedRenderFeature>(true)) GardenFeatureEnabled(feature);
            }));
        }
    }
    internal void GardenFeatureEnabled(InstancedRenderFeature feature)
    {
        if (!Active || IsRefused(SheddingCut.Garden) || feature == null || _gardenFeatures.ContainsKey(feature.Pointer)) return;
        var room = feature.GetComponentInParent<LG_Geomorph>();
        if (room == null || room.m_assignedSubComplexFromZone != SubComplex.Gardens || feature.Renderer == null) return;
        var entry = new GardenFeature(feature, feature.Renderer);
        _gardenFeatures.Add(feature.Pointer, entry); _gardenRenderers.Add(entry.Renderer.Pointer, entry);
        _gardenSeen++; _gardenInstances += feature.Instances?.Count ?? 0;
    }
    internal void GardenFeatureDisabled(InstancedRenderFeature feature)
    {
        if (!_gardenFeatures.TryGetValue(feature.Pointer, out var entry)) return;
        _gardenFeatures.Remove(feature.Pointer); _gardenRenderers.Remove(entry.Renderer.Pointer);
        if (entry.Suppressed) _gardenScopesReleased++;
    }
    internal bool DrawGardenShadow(InstancedRenderFeatureRenderer renderer)
    {
        if (!Active || IsRefused(SheddingCut.Garden) || !_gardenRenderers.TryGetValue(renderer.Pointer, out var entry)) return true;
        // This per-feature renderer submits only shadow CommandBuffer draws.
        // Its normal colour draw, instance data, descriptor and cull lists stay
        // native. No extra timer/query/scene scan is added to its frame path.
        if (!entry.Suppressed) { entry.Suppressed = true; _gardenChanged++; }
        _gardenShadowCalls++; return false;
    }
    internal void CorpseTiming(ES_Dead._DecayDelayed_d__20 iterator)
    {
        if (!Active || Marshal.ReadInt32(iterator.Pointer, 0x10) != 0) return;
        // Root the wrapper across subsequent native coroutine frames. Otherwise
        // interop's weak object pool could recreate it after a managed GC.
        _corpseIterators[iterator.Pointer] = iterator;
        var state = new ES_Dead(Marshal.ReadIntPtr(iterator.Pointer, 0x28));
        if (Settings.FastCorpseHide) Corpses.Schedule(state, iterator.delay, iterator.duration);
        if (Settings.CorpseRecycle) Extras.RecycleCorpse(iterator);
    }
    internal void Tick()
    {
        if (!Active) return;
        Work.Run(GameStateManager.CurrentStateName == eGameStateName.InLevel ? LevelWork.LevelSliceMilliseconds : LevelWork.ElevatorSliceMilliseconds);
        if (Settings.FastCorpseHide && !IsRefused(SheddingCut.CorpseTimer)) Corpses.Tick();
        if (Settings.CombatDecals) Decorations.TickDecals();
        if (Settings.DecayCleanup || Settings.DebrisFreeze) Extras.Tick();
    }
    internal void CorpseComplete(ES_Dead._DecayDelayed_d__20 iterator) => _corpseIterators.Remove(iterator.Pointer);
    internal void Ragdoll(ES_Dead state, bool enabled, bool wasRagdoll)
    {
        if (!Active || _restoring) return;
        if (!enabled && wasRagdoll && Settings.RagdollNodeRefresh) RefreshCorpseNode(state);
        if (!Settings.RagdollCap) return;
        var key = state.Pointer;
        if (!enabled)
        {
            _order.Disabled(key);
            if (!_evicting) { _requestedRagdolls.Remove(key); _ragdolls.Remove(key); }
            return;
        }
        if (!state.m_isRagdoll) return;
        if (_requestedRagdolls.Add(key)) _ragdollSeen++;
        _ragdolls[key] = state;
        var victim = _order.Enabled(key);
        if (victim == IntPtr.Zero || !_ragdolls.TryGetValue(victim, out var old)) return;
        _evicting = true;
        try { old.SetRagdollEnabled(false); _ragdollEvicted++; }
        finally { _evicting = false; }
    }
    // A flying ragdoll only moves its culling node by portal-crossing checks (C_MovingCuller.UpdatePosition(Bounds)
    // has no full node lookup), so a body thrown through a room boundary can stay registered in the room it left and be
    // culled with that room. Once the body stops, the game's own RefreshNode looks the node up again from the last
    // bounds centre. Culling only: the enemy's course node, collision and despawn stay native.
    private void RefreshCorpseNode(ES_Dead state)
    {
        _ragdollRefreshSeen++;
        try
        {
            var enemy = state.m_enemyAgent;
            var culler = enemy == null ? null : enemy.MovingCuller;
            if (culler == null) return;
            culler.RefreshNode(enemy!.DimensionIndex);
            _ragdollRefreshed++;
        }
        catch (Exception error)
        {
            if (_ragdollRefreshFailures++ == 0) LogExtraFailure("ragdoll-node-refresh", error.GetType().Name + ": " + error.Message);
        }
    }
    internal void RagdollHidden(ES_Dead state)
    {
        _order.Disabled(state.Pointer); _requestedRagdolls.Remove(state.Pointer); _ragdolls.Remove(state.Pointer);
    }

    internal bool AllowShell(WeaponShellManager._EjectShellInternal_d__11 iterator)
    {
        if (!Active || Marshal.ReadInt32(iterator.Pointer, 0x10) != 0) return true;
        _shellSeen++;
        var frame = Time.frameCount;
        if (_shellFrame != frame) { _shellFrame = frame; _shellBudget = 0; }
        if (SheddingPolicy.ConsumeShell(ref _shellBudget, Settings.ShellLimit)) { _shellIterators[iterator.Pointer] = iterator; return true; }
        // This iterator creates ONLY the visual shell. No shot, ammo or hit call.
        Marshal.WriteInt32(iterator.Pointer, 0x10, -1); _shellSuppressed++; return false;
    }
    internal void ShellComplete(WeaponShellManager._EjectShellInternal_d__11 iterator) => _shellIterators.Remove(iterator.Pointer);
    internal void EndLevel()
    {
        if (!Active) return;
        Active = false; _restoring = true;
        Work.Clear();
        Exception? first = null;
        var dustSeen = Dust.Seen;
        if (Settings.AmbientDustOff)
            try { Dust.EndLevel(); } catch (Exception error) { first ??= error; }
        if (Settings.VisorLiquidOff)
            try { Visor.EndLevel(); } catch (Exception error) { first ??= error; }
        if (Settings.FastCorpseHide)
            try { Corpses.EndLevel(); } catch (Exception error) { first ??= error; }
        try { Decorations.EndLevel(); } catch (Exception error) { first ??= error; }
        try { Extras.EndLevel(); } catch (Exception error) { first ??= error; }
        var doors = Doors.Counts;
        if (Settings.ClosedDoorDepthLimit)
            try { Doors.EndLevel(); } catch (Exception error) { first ??= error; }
        // Try every restore even if one native call fails. Failures remain
        // explicit; the counts distinguish restoration from requested changes.
        try { RestoreStealthBodies(null); } catch (Exception error) { first ??= error; }
        foreach (var value in _shadows.Values)
            try
            {
                if (RestoreShadow(value))
                { if (value.Garden) _gardenRestored++; else _corpseShadowRestored++; }
            }
            catch (Exception error) { first ??= error; }
        foreach (var value in _ambient.Values)
            try
            {
                if (value.Particles != null && value.Original != value.Reduced && value.Particles.instanceCount == value.Reduced)
                { value.Particles.instanceCount = value.Original; value.Particles.ResetSystem(); _ambientRestored++; }
            }
            catch (Exception error) { first ??= error; }
        foreach (var value in _air.Values)
            try
            {
                if (value.Particles != null && value.Original != value.Reduced && value.Particles.m_maxParticles == value.Reduced)
                { value.Particles.m_maxParticles = value.Original; value.Particles.NotifyConfigChange(); _ambientRestored++; }
            }
            catch (Exception error) { first ??= error; }
        // Evicted ragdolls are not switched back on: leaving the level destroys their bodies, and waking many at once is a physics spike.
        if (first != null) RestorationFailed();
        foreach (var entry in _gardenFeatures.Values) if (entry.Suppressed) _gardenScopesReleased++;
        if (Settings.SummaryLog)
        {
            if (Settings.CorpseShadows || Settings.FastCorpseHide) LogItem("stealth-body", _stealthHidden, _stealthHidden, _stealthRestored, "value=ShadowsOnly renderer off at death");
            if (Settings.CorpseShadows) LogItem("corpse-shadows", _corpseShadowSeen, _corpseShadowChanged, _corpseShadowRestored, "value=Off");
            if (Settings.FastCorpseHide) LogItem("corpse-timer", Corpses.Seen, Corpses.Hidden, Corpses.Restored,
                "requested_visible_s=" + Settings.CorpseLifetime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "\tvisible_fade=none\tnative_decay=unchanged\trenderer_changes=" + Corpses.RendererChanges
                + "\tmaximum_checks_per_frame=64\tmaximum_renderer_visits_per_frame=128\tchecks=" + Corpses.Checks
                + "\tmaximum_hide_lateness_s=" + Corpses.MaximumHideLateness.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            if (Settings.RagdollCap) LogItem("ragdoll-cap", _ragdollSeen, _ragdollEvicted, 0, "limit=" + Settings.RagdollLimit + "\torder=oldest_first");
            if (Settings.RagdollNodeRefresh) LogItem("ragdoll-node-refresh", _ragdollRefreshSeen, _ragdollRefreshed, 0,
                "trigger=ragdoll_stopped\tfailures=" + _ragdollRefreshFailures);
            if (Settings.ClosedDoorDepthLimit) LogItem("closed-door-depth", doors.Doors, doors.FramesLimited, 0,
                "depth=" + Settings.ClosedDoorDepth + "\tdoors_without_sync=" + doors.NoSync + "\tshared_portals=" + doors.Shared
                + "\treleased_damaged_or_destroyed=" + doors.Released + "\tnodes=" + doors.Nodes + "\tportals=" + doors.Portals
                + "\trecomputes=" + doors.Recomputes + "\tportal_closes_total=" + doors.ClosesTotal
                + "\tmaximum_closed_per_pass=" + doors.MaximumClosed + "\tfailures=" + doors.Failures);
            if (Settings.GardenShadows) LogItem("garden-props", _gardenSeen, _gardenChanged, _gardenRestored, "value=Off\trooms=" + _gardens.Count
                + "\tirf_instances=" + _gardenInstances + "\tskipped_shadow_draws=" + _gardenShadowCalls
                + "\treleased_shadow_scopes=" + _gardenScopesReleased + "\tcoverage=enabled_static_mesh_renderers_and_garden_irf_shadow_commands");
            if (Settings.AmbientParticles) LogItem("ambient-particles", _ambient.Count + _air.Count, _ambientChanged, _ambientRestored,
                "count_multiplier=" + Settings.ParticleFactor + "\tminimum_nonzero=1\tambient_systems=" + _ambient.Count + "\tair_systems=" + _air.Count
                + "\tambient_update_hz=" + Settings.ParticleHz + "\tupdate_calls=" + _ambientUpdateCalls + "\tskipped_updates=" + _ambientSkippedUpdates
                + "\tprecedence=" + (Settings.AmbientDustOff ? "AmbientDustOff" : "thinning"));
            if (Settings.AmbientDustOff) LogItem("ambient-dust-off", dustSeen, Dust.Changed, Dust.Restored,
                "state=" + (IsRefused(SheddingCut.Dust) ? "vanilla_refused" : "off") + "\tskipped_simulation=" + Dust.SkippedSimulation
                + "\tskipped_air_commands=" + Dust.SkippedAirCommands + "\trefusals_session=" + ((_refusedCuts & SheddingCut.Dust) != 0 ? 1 : 0) + "\tclassification=AmbientParticles_AirParticleSystem");
            if (Settings.VisorLiquidOff) LogItem("visor-liquid-off", Visor.Seen, Visor.Changed, Visor.Restored,
                "rendering=" + (Visor.Refused ? "vanilla_refused" : "off") + "\trefusals_session=" + Visor.Refusals + "\tskipped_commands=" + Visor.SkippedCommands + "\thealth=unchanged\tinfection=unchanged");
            if (Settings.ShellEjection) LogItem("shell-ejection", _shellSeen, _shellSuppressed, 0, "limit_per_frame=" + Settings.ShellLimit + "\trestore=scope_ended");
            if (_levelSetupFailures != 0) LogItem("setup-failures", _levelSetupFailures, 0, 0, "scope=level;retry=next_level");
        }
        _shadows.Clear(); _ambient.Clear(); _air.Clear(); _ragdolls.Clear(); _requestedRagdolls.Clear();
        _corpseIterators.Clear(); _shellIterators.Clear();
        _order.Clear(); _gardens.Clear();
        _gardenFeatures.Clear(); _gardenRenderers.Clear();
        _restoring = false;
        if (first != null) { _log.LogError("Load shedding restore failed: " + first); throw first; }
    }
    internal void LogItem(string item, long seen, long changed, long restored, string values)
    {
        var line = "type=load_shedding\tlevel_seq=" + LevelSequence + "\titem=" + item
            + "\tmode=configured\tlocal_only=true\tseen=" + seen + "\tchanged=" + changed
            + "\tskipped=" + Math.Max(0, seen - changed) + "\trestored=" + restored
            + "\trestore_failed=" + _restoreFailures + "\t" + values;
        if (Settings.SummaryLog) _log.LogInfo(line);
    }
    internal void RestorationFailed() => _restoreFailures = 1;
    internal void LogExtraFailure(string item, string reason)
    {
        var line = "type=load_shedding_setup_failure\titem=" + item + "\tlevel_seq=" + LevelSequence + "\treason=" + reason;
        _log.LogError(line);
    }
    private void RefuseVisor(string reason, bool session) => RefuseCut(SheddingCut.Visor, reason, session);
    internal void LogInspectionFailure(string reason)
    {
        var line = "type=load_shedding_inspection\titem=decoration-culling\tlevel_seq=" + LevelSequence
            + "\treason=" + reason;
        _log.LogError(line);
    }
    private void LogSetupFailure(string item, string reason)
    {
        var line = "type=load_shedding_setup_failure\titem=" + item + "\tlevel_seq=" + LevelSequence
            + "\tfailures_level=" + _levelSetupFailures + "\treason=" + reason;
        _log.LogError(line);
    }
    private void LogRefusal(string item, string reason)
    {
        var line = "type=load_shedding_refusal\titem=" + item + "\trefusals_session=1\treason=" + reason;
        _log.LogError(line);
    }
    internal void Dispose()
    {
        try { EndLevel(); }
        finally
        {
            EventAPI.OnGameStateChanged -= OnStateChanged;
            Extras.Dispose(); _hooks.Dispose(); _installed = false; Current = null;
        }
    }

    private sealed record ShadowState(Renderer Renderer, ShadowCastingMode Original, bool Enabled, bool Garden, bool Corpse, bool Disabled);
    private sealed class AmbientState
    {
        internal readonly AmbientParticles Particles;
        internal readonly int Original, Reduced;
        internal float AccumulatedTime;
        internal AmbientState(AmbientParticles particles, int original, int reduced)
        { Particles = particles; Original = original; Reduced = reduced; }
    }
    private sealed record AirState(AirParticles Particles, int Original, int Reduced);
    private sealed class GardenFeature
    {
        internal readonly InstancedRenderFeature Feature;
        internal readonly InstancedRenderFeatureRenderer Renderer;
        internal bool Suppressed;
        internal GardenFeature(InstancedRenderFeature feature, InstancedRenderFeatureRenderer renderer)
        { Feature = feature; Renderer = renderer; }
    }
}
