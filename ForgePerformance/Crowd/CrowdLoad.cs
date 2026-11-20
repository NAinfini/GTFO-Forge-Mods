using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgePerformance.LoadShedding;
using Player;
using UnityEngine;
using UnityEngine.AI;

namespace ForgePerformance.Crowd;

/// <summary>The enemy-crowd options that act on living enemies or on the game's global enemy settings, run from the plugin's
/// per-frame update during a level. Living enemies are registered at EnemyAgent.Setup and dropped at death or when destroyed.
/// Every change remembers the game's value and puts it back when its option is switched off or the enemy dies.</summary>
internal sealed class CrowdLoad
{
    internal static int Frame;
    // EnemyTransformSync pointers ClientHiddenSyncThrottle throttles this tick.
    internal static readonly HashSet<IntPtr> ThrottledSyncs = new();
    internal int LastSetupFrame = -1;
    internal long DeferredTicks;
    private const float SlowTickSeconds = 0.25f;
    private readonly SheddingSettings _settings;
    private readonly Action<string> _error;
    private readonly List<Entry> _enemies = new(128);
    private readonly bool _captureRenderers, _captureAnimator, _captureSync;
    private float _nextSlowTick;
    private bool _host;
    private long _skinsChanged, _animatorsThrottled, _animatorUpdates, _budgetWrites, _avoidanceWrites, _pathWrites;
    // Global game values, read the first time they are seen and written back when their option goes off.
    private IntPtr _budgetManager;
    private int[]? _vanillaBudgets;
    private bool _budgetsApplied, _avoidanceApplied, _pathApplied;
    private ObstacleAvoidanceType _vanillaAvoidance, _appliedAvoidance;
    private int _vanillaPath, _appliedPath;
    private long _agentsRetyped;
    private readonly int[] _budgets;

    private sealed class Entry
    {
        internal readonly EnemyAgent Enemy;
        internal readonly IntPtr Key, Sync;
        internal readonly SkinnedMeshRenderer[] Skins;
        internal readonly SkinQuality[] Quality;
        internal readonly bool[] MotionVectors;
        internal readonly Animator? Animator;
        internal readonly EnemyLocomotion? Locomotion;
        internal readonly bool CanThrottle; // animator and locomotion exist: fixed at setup, so the tick needs no Unity null check
        internal bool FarSkin, Throttled, KeptState;
        internal float Pending;
        internal Transform[]? Limbs;
        internal Entry(EnemyAgent enemy, IntPtr sync, SkinnedMeshRenderer[] skins, Animator? animator)
        {
            Enemy = enemy; Key = enemy.Pointer; Sync = sync; Skins = skins; Animator = animator;
            Locomotion = enemy.Locomotion;
            CanThrottle = animator != null && Locomotion != null;
            Quality = new SkinQuality[skins.Length]; MotionVectors = new bool[skins.Length];
            for (var i = 0; i < skins.Length; i++) { Quality[i] = skins[i].quality; MotionVectors[i] = skins[i].skinnedMotionVectors; }
        }
    }

    internal CrowdLoad(SheddingSettings settings, Action<string> error)
    {
        _settings = settings; _error = error;
        Current = this;
        _captureRenderers = settings.Probe || settings.FarSkinQuality;
        _captureAnimator = settings.Probe || settings.FarAnimatorThrottle;
        _captureSync = settings.Probe || settings.ClientHiddenSyncThrottle;
        _budgets = ParseBudgets(settings);
    }

    // Close, near and far for behaviour, detection, network, locomotion and physics-step locomotion, then the node group:
    // the order EnemyUpdateManager.Setup builds them in.
    private static int[] ParseBudgets(SheddingSettings settings)
    {
        var values = new List<int>(16);
        foreach (var text in new[] { settings.BudgetBehaviour, settings.BudgetDetection, settings.BudgetNetwork, settings.BudgetLocomotion, settings.BudgetFixedLocomotion })
            values.AddRange(Three(text));
        values.Add(int.TryParse(settings.BudgetNodes.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var node) && node >= 0 ? node : 1);
        return values.ToArray();
        static int[] Three(string text)
        {
            var parts = text.Split(',');
            if (parts.Length != 3) throw new FormatException("Enemy update budget needs three numbers (close, near, far): " + text);
            var result = new int[3];
            for (var i = 0; i < 3; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]) || result[i] < 0)
                    throw new FormatException("Enemy update budget is not a number of zero or more: " + text);
            return result;
        }
    }

    internal void Spawned(EnemyAgent enemy)
    {
        LastSetupFrame = Time.frameCount;
        if (enemy == null) return;
        // One enemy can be set up more than once (in game the registry grew by about 2.3 entries per spawned enemy); the earlier
        // entry gives its changes back first, so the new one captures the game's own values, never this mod's.
        Died(enemy);
        var skins = _captureRenderers ? enemy.GetComponentsInChildren<SkinnedMeshRenderer>(true) : Array.Empty<SkinnedMeshRenderer>();
        var sync = _captureSync ? enemy.GetComponent<EnemyTransformSync>() : null;
        _enemies.Add(new Entry(enemy, sync == null ? IntPtr.Zero : sync.Pointer, skins, _captureAnimator ? enemy.Locomotion?.m_animator : null));
    }

    internal void Died(EnemyAgent? enemy)
    {
        if (enemy == null) return;
        var key = enemy.Pointer;
        for (var i = 0; i < _enemies.Count; i++)
        {
            if (_enemies[i].Key != key) continue;
            Release(_enemies[i], true);
            _enemies.RemoveAt(i);
            return;
        }
    }

    internal void Tick(int frame, float now)
    {
        Frame = frame;
        if (CrowdSwitches.FarAnimatorThrottle || _animatorsThrottledNow != 0) StepAnimators();
        if (now < _nextSlowTick) return;
        _nextSlowTick = now + SlowTickSeconds;
        try { SlowTick(); }
        catch (Exception error) { _error("type=load_shedding_error\titem=crowd\treason=" + error.GetType().Name + ": " + error.Message); }
    }

    private int _animatorsThrottledNow;
    // FarAnimatorThrottle, every frame: a throttled animator is off and gets one Update with the time it missed every
    // FarAnimatorInterval frames, staggered across enemies. One that left walking or turned on root motion is given back at once.
    private void StepAnimators()
    {
        var interval = _settings.FarAnimatorInterval;
        var dt = Time.deltaTime;
        for (var i = 0; i < _enemies.Count; i++)
        {
            var entry = _enemies[i];
            if (!entry.Throttled) continue;
            var animator = entry.Animator;
            if (!CrowdSwitches.FarAnimatorThrottle || animator == null || entry.Locomotion == null
                || entry.Locomotion.CurrentStateEnum != ES_StateEnum.PathMove || animator.applyRootMotion)
            { Unthrottle(entry); continue; }
            entry.Pending += dt;
            if ((Frame + i) % interval != 0) continue;
            var limbs = MeasureLag ? LagBefore(entry) : 0;
            var before = MeasureLag ? animator.GetCurrentAnimatorStateInfo(0) : default;
            animator.Update(entry.Pending);
            if (limbs != 0) LagAfter(entry, limbs); else _lagMoved = false;
            if (MeasureLag) LagCounts(entry, before);
            entry.Pending = 0; _animatorUpdates++;
        }
    }

    // The counting half of the same developer measurement: called after a manual update only while MeasureLag is on.
    private static void LagCounts(Entry entry, AnimatorStateInfo before)
    {
        var after = entry.Animator!.GetCurrentAnimatorStateInfo(0);
        var advanced = after.fullPathHash != before.fullPathHash || after.normalizedTime != before.normalizedTime;
        _checkedUpdates++;
        if (advanced) _timeAdvanced++;
        if (_lagMoved) _poseMoved++;
        var seen = false;
        foreach (var skin in entry.Skins) if (skin != null && skin.isVisible) { seen = true; break; }
        if (!seen) return;
        _seenUpdates++;
        if (advanced) _seenTime++;
        if (_lagMoved) _seenPose++;
    }

    // Developer measurement of FarAnimatorThrottle's hit-box error (PerformanceControl.Lag): a throttled enemy's hit boxes
    // (its damage limbs, which sit on bones) keep the pose of their last animator update while the enemy root keeps moving
    // with navigation. How far each limb moves relative to the root in the catch-up update is the largest offset it had on
    // the skipped frames. Off by default; while off nothing here runs.
    internal static bool MeasureLag;
    private static readonly Vector3[] LagPositions = new Vector3[64];
    private static readonly long[] LagHistogram = new long[101]; // centimetres, last bucket is 100 cm and more
    private static long _lagSamples;
    private static double _lagSum;
    private static float _lagMax;

    private static int LagBefore(Entry entry)
    {
        entry.Limbs ??= LimbTransforms(entry.Enemy);
        var root = entry.Enemy.transform.position;
        var count = Math.Min(entry.Limbs.Length, LagPositions.Length);
        for (var i = 0; i < count; i++) LagPositions[i] = entry.Limbs[i] == null ? root : entry.Limbs[i].position - root;
        return count;
    }

    // The readback that a throttled animator really plays: of the manual updates measured, how many moved the state's time (or
    // changed state) and how many moved at least one limb relative to the root; the same three for enemies a camera drew that frame,
    // since the engine itself leaves the bones of an enemy nobody sees alone.
    private static long _checkedUpdates, _timeAdvanced, _poseMoved, _seenUpdates, _seenTime, _seenPose;
    private static bool _lagMoved;

    private static void LagAfter(Entry entry, int count)
    {
        _lagMoved = false;
        var root = entry.Enemy.transform.position;
        for (var i = 0; i < count; i++)
        {
            if (entry.Limbs![i] == null) continue;
            var offset = (entry.Limbs[i].position - root - LagPositions[i]).magnitude;
            if (offset > 0.0005f) _lagMoved = true;
            _lagSamples++; _lagSum += offset; _lagMax = Math.Max(_lagMax, offset);
            LagHistogram[Math.Min(100, (int)(offset * 100f))]++;
        }
    }

    private static Transform[] LimbTransforms(EnemyAgent enemy)
    {
        var limbs = enemy.Damage?.DamageLimbs;
        if (limbs == null) return Array.Empty<Transform>();
        var result = new Transform[limbs.Length];
        for (var i = 0; i < limbs.Length; i++) result[i] = limbs[i] == null ? null! : limbs[i].transform;
        return result;
    }

    // Developer measurement of how enemies chase (PerformanceControl.Chase / ChaseState): after the player teleports away, the
    // time until the expected number of living registered enemies stand within the radius of the local player. Read on demand.
    private static int _chaseExpected;
    private static float _chaseRadius, _chaseStart;
    internal static CrowdLoad? Current;

    internal static string Chase(int expected, float radius)
    {
        _chaseExpected = expected; _chaseRadius = radius; _chaseStart = Time.time;
        return "chase=started;expected=" + expected + ";radius_m=" + radius.ToString("0.#", CultureInfo.InvariantCulture);
    }

    internal static string ChaseState()
    {
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player == null || Current == null) return "unavailable";
        var origin = player.Position;
        var near = 0; var alive = 0; var far = 0f;
        foreach (var entry in Current._enemies)
        {
            if (entry.Enemy == null || !entry.Enemy.Alive) continue;
            alive++;
            var distance = Vector3.Distance(entry.Enemy.Position, origin);
            if (distance <= _chaseRadius) near++; else far = Math.Max(far, distance);
        }
        var seconds = Time.time - _chaseStart;
        var state = near >= _chaseExpected ? "done" : seconds >= 45f ? "done-timeout" : "waiting";
        return state + ";seconds=" + seconds.ToString("0.00", CultureInfo.InvariantCulture) + ";near=" + near + ";alive=" + alive
            + ";not_arrived=" + (alive - near) + ";farthest_m=" + far.ToString("0.0", CultureInfo.InvariantCulture)
            + ";throttled_animators=" + Probe.ThrottledAnimators;
    }

    /// <summary>Every registered enemy's animator, read on demand: how many are throttled, the nearest throttled one, how many
    /// animators are off without being throttled (must be 0), and the locomotion states of the throttled ones.</summary>
    internal static string Animators()
    {
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player == null || Current == null) return "unavailable";
        int alive = 0, throttled = 0, offUnthrottled = 0, nearOff = 0;
        var nearest = float.MaxValue;
        var states = new Dictionary<string, int>();
        foreach (var entry in Current._enemies)
        {
            if (entry.Enemy == null || !entry.Enemy.Alive || entry.Animator == null) continue;
            alive++;
            var distance = Vector3.Distance(entry.Enemy.Position, player.Position);
            if (!entry.Animator.enabled && !entry.Throttled) offUnthrottled++;
            if (!entry.Animator.enabled && distance <= Current._settings.FarAnimatorDistance) nearOff++;
            if (!entry.Throttled) continue;
            throttled++; nearest = Math.Min(nearest, distance);
            var state = entry.Locomotion == null ? "none" : entry.Locomotion.CurrentStateEnum.ToString();
            states[state] = states.TryGetValue(state, out var count) ? count + 1 : 1;
        }
        var text = "alive=" + alive + ";throttled=" + throttled + ";nearest_throttled_m=" + (throttled == 0 ? "none" : nearest.ToString("0.0", CultureInfo.InvariantCulture))
            + ";animator_off_not_throttled=" + offUnthrottled + ";animator_off_within_distance=" + nearOff + ";throttled_states=";
        foreach (var pair in states) text += pair.Key + ":" + pair.Value + ",";
        return text;
    }

    internal static string Lag(bool on)
    {
        var report = "lag_samples=" + _lagSamples + ";lag_mean_m=" + (_lagSamples == 0 ? 0 : _lagSum / _lagSamples).ToString("0.0000", CultureInfo.InvariantCulture)
            + ";lag_p95_m=" + Percentile(0.95).ToString("0.00", CultureInfo.InvariantCulture)
            + ";lag_p99_m=" + Percentile(0.99).ToString("0.00", CultureInfo.InvariantCulture)
            + ";lag_max_m=" + _lagMax.ToString("0.0000", CultureInfo.InvariantCulture)
            + ";manual_updates=" + _checkedUpdates + ";time_advanced=" + _timeAdvanced + ";pose_moved=" + _poseMoved
            + ";on_screen_updates=" + _seenUpdates + ";on_screen_time_advanced=" + _seenTime + ";on_screen_pose_moved=" + _seenPose;
        if (on && !MeasureLag) { _lagSamples = 0; _lagSum = 0; _lagMax = 0; Array.Clear(LagHistogram); _checkedUpdates = _timeAdvanced = _poseMoved = _seenUpdates = _seenTime = _seenPose = 0; }
        MeasureLag = on;
        return report;
        static double Percentile(double fraction)
        {
            var target = (long)Math.Ceiling(_lagSamples * fraction);
            long seen = 0;
            for (var i = 0; i < LagHistogram.Length; i++) if ((seen += LagHistogram[i]) >= target && target > 0) return (i + 1) / 100.0;
            return 0;
        }
    }

    private void SlowTick()
    {
        _host = Probe.HostNow = SNetwork.SNet.IsMaster;
        if ((CrowdSwitches.UpdateBudgets && _host) != _budgetsApplied || (CrowdSwitches.AvoidanceQuality && _host) != _avoidanceApplied
            || (CrowdSwitches.PathfindingIterations && _host) != _pathApplied
            || (_avoidanceApplied && (int)_appliedAvoidance != CrowdSwitches.AvoidanceType) || (_pathApplied && _appliedPath != CrowdSwitches.PathfindingIterationsPerFrame))
            SyncGlobals();
        var player = PlayerManager.GetLocalPlayerAgent();
        var hasPlayer = player != null; // one Unity null check per tick, not per enemy
        var origin = hasPlayer ? player!.Position : default;
        var skinFar = _settings.FarSkinDistance * _settings.FarSkinDistance;
        var animatorFar = _settings.FarAnimatorDistance * _settings.FarAnimatorDistance;
        var syncFar = _settings.HiddenSyncDistance * _settings.HiddenSyncDistance;
        ThrottledSyncs.Clear();
        int farSkins = 0, throttled = 0;
        for (var i = _enemies.Count - 1; i >= 0; i--)
        {
            var entry = _enemies[i];
            var enemy = entry.Enemy;
            if (enemy == null || !enemy.Alive) { Release(entry, enemy != null); _enemies.RemoveAt(i); continue; }
            var distance = 0f;
            if (hasPlayer) { var d = enemy.Position - origin; distance = d.x * d.x + d.y * d.y + d.z * d.z; }
            var farSkin = CrowdSwitches.FarSkinQuality && hasPlayer && distance > skinFar;
            if (farSkin != entry.FarSkin) SetSkins(entry, farSkin);
            if (entry.FarSkin) farSkins++;
            var throttle = CrowdSwitches.FarAnimatorThrottle && hasPlayer && distance > animatorFar && entry.CanThrottle
                && entry.Locomotion!.CurrentStateEnum == ES_StateEnum.PathMove && !entry.Animator!.applyRootMotion;
            if (throttle && !entry.Throttled) Throttle(entry);
            else if (!throttle && entry.Throttled) Unthrottle(entry);
            if (entry.Throttled) throttled++;
            if (CrowdSwitches.ClientHiddenSyncThrottle && !_host && hasPlayer && entry.Sync != IntPtr.Zero && distance > syncFar
                && enemy.MovingCuller is { } culler && !culler.IsShown)
                ThrottledSyncs.Add(entry.Sync);
        }
        Probe.Enemies = _enemies.Count; Probe.FarSkins = farSkins; Probe.ThrottledAnimators = throttled; Probe.ThrottledSyncs = ThrottledSyncs.Count;
    }

    private void SetSkins(Entry entry, bool far)
    {
        for (var i = 0; i < entry.Skins.Length; i++)
        {
            var skin = entry.Skins[i];
            if (skin == null) continue;
            if (far) { skin.quality = SkinQuality.Bone2; skin.skinnedMotionVectors = false; }
            else { skin.quality = entry.Quality[i]; skin.skinnedMotionVectors = entry.MotionVectors[i]; }
        }
        entry.FarSkin = far;
        if (far) _skinsChanged++;
    }

    // A disabled Animator drops its controller state (current state, transitions, parameters) unless it is told to keep it: the
    // manual updates then played the default state from its start, and the enemy slid in that pose, also after it was given back.
    private void Throttle(Entry entry)
    {
        ThrottledByEnemy[entry.Key] = entry;
        entry.KeptState = entry.Animator!.keepAnimatorControllerStateOnDisable;
        entry.Animator.keepAnimatorControllerStateOnDisable = true;
        entry.Animator.enabled = false;
        entry.Throttled = true; entry.Pending = 0;
        _animatorsThrottled++; _animatorsThrottledNow++;
    }
    // ES_PathMove.Exit runs inside the game's own enemy update, which Unity may run before this mod's per-frame step: a throttled
    // enemy is given its animator back before the next state's Enter calls CrossFade or Play on it.
    internal static void WalkEnding(EnemyAgent enemy)
    {
        if (Current is { _animatorsThrottledNow: > 0 } crowd && ThrottledByEnemy.TryGetValue(enemy.Pointer, out var entry)) crowd.Unthrottle(entry);
    }

    private static readonly Dictionary<IntPtr, Entry> ThrottledByEnemy = new();

    private void Unthrottle(Entry entry)
    {
        ThrottledByEnemy.Remove(entry.Key);
        if (entry.Animator != null) { entry.Animator.enabled = true; entry.Animator.keepAnimatorControllerStateOnDisable = entry.KeptState; }
        entry.Throttled = false; entry.Pending = 0;
        _animatorsThrottledNow--;
    }

    private void Release(Entry entry, bool alive)
    {
        if (!alive)
        {
            if (entry.Throttled) { entry.Throttled = false; _animatorsThrottledNow--; ThrottledByEnemy.Remove(entry.Key); }
            return;
        }
        if (entry.Throttled) Unthrottle(entry);
        if (entry.FarSkin) SetSkins(entry, false);
    }

    // UpdateBudgets, AvoidanceQuality and PathfindingIterations change global game values; each acts on the host only and
    // writes the game's value back when its option goes off or this machine stops being the host.
    private void SyncGlobals()
    {
        var manager = EnemyUpdateManager.Current;
        if (manager != null)
        {
            if (manager.Pointer != _budgetManager) { _budgetManager = manager.Pointer; _vanillaBudgets = ReadBudgets(manager); _budgetsApplied = false; }
            var want = CrowdSwitches.UpdateBudgets && _host;
            if (want != _budgetsApplied) { WriteBudgets(manager, want ? _budgets : _vanillaBudgets!); _budgetsApplied = want; _budgetWrites++; }
        }
        var avoid = CrowdSwitches.AvoidanceQuality && _host;
        if (avoid != _avoidanceApplied || (avoid && (int)_appliedAvoidance != CrowdSwitches.AvoidanceType))
        {
            if (!_avoidanceApplied) _vanillaAvoidance = EnemyAI.s_defaultAvoidanceType;
            var from = EnemyAI.s_defaultAvoidanceType;
            var to = avoid ? (ObstacleAvoidanceType)CrowdSwitches.AvoidanceType : _vanillaAvoidance;
            EnemyAI.s_defaultAvoidanceType = _appliedAvoidance = to;
            _avoidanceApplied = avoid; _avoidanceWrites++;
            Retype(from, to);
        }
        var path = CrowdSwitches.PathfindingIterations && _host;
        if (path != _pathApplied || (path && _appliedPath != CrowdSwitches.PathfindingIterationsPerFrame))
        {
            if (!_pathApplied) _vanillaPath = NavMesh.pathfindingIterationsPerFrame;
            NavMesh.pathfindingIterationsPerFrame = _appliedPath = path ? CrowdSwitches.PathfindingIterationsPerFrame : _vanillaPath;
            _pathApplied = path; _pathWrites++;
        }
    }

    // The game reads s_defaultAvoidanceType only when an agent is switched on, so the living enemies whose agent is on with the
    // type that was the default get the new one too. An agent with another type (one this mod's hit-react keeps stopped has none)
    // is left alone.
    private void Retype(ObstacleAvoidanceType from, ObstacleAvoidanceType to)
    {
        if (from == to) return;
        foreach (var entry in _enemies)
        {
            var agent = entry.Enemy != null ? entry.Enemy.AI?.m_navMeshAgent : null;
            if (agent == null || !agent.enabled || agent.obstacleAvoidanceType != from) continue;
            agent.obstacleAvoidanceType = to; _agentsRetyped++;
        }
    }

    private static SteppedUpdateGroup[] Groups(EnemyUpdateManager m) => new SteppedUpdateGroup[]
    {
        m.m_behaviourUpdatesClose, m.m_behaviourUpdatesNear, m.m_behaviourUpdatesFar,
        m.m_detectionUpdatesClose, m.m_detectionUpdatesNear, m.m_detectionUpdatesFar,
        m.m_networkUpdatesClose, m.m_networkUpdatesNear, m.m_networkUpdatesFar,
        m.m_locomotionUpdatesClose, m.m_locomotionUpdatesNear, m.m_locomotionUpdatesFar,
        m.m_fixedLocomotionUpdatesClose, m.m_fixedLocomotionUpdatesNear, m.m_fixedLocomotionUpdatesFar,
        m.m_nodeUpdates
    };
    private static int[] ReadBudgets(EnemyUpdateManager manager)
    {
        var groups = Groups(manager);
        var values = new int[groups.Length];
        for (var i = 0; i < groups.Length; i++) values[i] = groups[i].m_maxUpdateCountPerFrame;
        return values;
    }
    private static void WriteBudgets(EnemyUpdateManager manager, int[] values)
    {
        var groups = Groups(manager);
        for (var i = 0; i < groups.Length; i++) groups[i].m_maxUpdateCountPerFrame = values[i];
    }

    internal string ReadGlobals()
    {
        var manager = EnemyUpdateManager.Current;
        var budgets = manager == null ? "unavailable" : string.Join(",", ReadBudgets(manager));
        return "budgets=" + budgets + ";vanilla_budgets=" + (_vanillaBudgets == null ? "unread" : string.Join(",", _vanillaBudgets))
            + ";avoidance=" + (int)EnemyAI.s_defaultAvoidanceType + ";vanilla_avoidance=" + (_avoidanceApplied ? ((int)_vanillaAvoidance).ToString(CultureInfo.InvariantCulture) : "current")
            + ";pathfinding_iterations=" + NavMesh.pathfindingIterationsPerFrame + ";host=" + (_host ? 1 : 0)
            + ";agents_retyped=" + _agentsRetyped + ";kept_agents=" + CrowdPatches.KeptTotal + ";sync_skipped=" + CrowdPatches.SyncSkippedTotal + ";deferred_ticks=" + DeferredTicks;
    }

    internal string Summary()
        => "enemies_registered_now=" + _enemies.Count + "\tfar_skin_changes=" + _skinsChanged
            + "\tanimators_throttled=" + _animatorsThrottled + "\tthrottled_animator_updates=" + _animatorUpdates
            + "\tsync_frames_skipped=" + CrowdPatches.SyncSkippedTotal + "\tkept_agents=" + CrowdPatches.KeptTotal
            + "\tbudget_writes=" + _budgetWrites + "\tavoidance_writes=" + _avoidanceWrites + "\tagents_retyped=" + _agentsRetyped + "\tpathfinding_writes=" + _pathWrites
            + "\tdeferred_ticks=" + DeferredTicks;

    internal void EndLevel()
    {
        foreach (var entry in _enemies) Release(entry, entry.Enemy != null);
        _enemies.Clear(); ThrottledSyncs.Clear(); CrowdPatches.EndLevel();
        // Globals go back to the game's values at the end of each level; the next level applies them again.
        _host = false;
        SyncGlobals();
        _budgetManager = IntPtr.Zero; _vanillaBudgets = null;
        _animatorsThrottledNow = 0; ThrottledByEnemy.Clear();
        Probe.Enemies = Probe.FarSkins = Probe.ThrottledAnimators = Probe.ThrottledSyncs = 0;
    }
}
