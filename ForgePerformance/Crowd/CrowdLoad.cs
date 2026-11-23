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
    private readonly bool _captureRenderers, _captureSync;
    private float _nextSlowTick;
    private bool _host;
    private long _skinsChanged, _budgetWrites, _avoidanceWrites, _pathWrites;
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
        internal bool FarSkin;
        internal Entry(EnemyAgent enemy, IntPtr sync, SkinnedMeshRenderer[] skins)
        {
            Enemy = enemy; Key = enemy.Pointer; Sync = sync; Skins = skins;
            Quality = new SkinQuality[skins.Length]; MotionVectors = new bool[skins.Length];
            for (var i = 0; i < skins.Length; i++) { Quality[i] = skins[i].quality; MotionVectors[i] = skins[i].skinnedMotionVectors; }
        }
    }

    internal CrowdLoad(SheddingSettings settings, Action<string> error)
    {
        _settings = settings; _error = error;
        Current = this;
        _captureRenderers = settings.Probe || settings.FarSkinQuality;
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
        _enemies.Add(new Entry(enemy, sync == null ? IntPtr.Zero : sync.Pointer, skins));
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
        if (now < _nextSlowTick) return;
        _nextSlowTick = now + SlowTickSeconds;
        try { SlowTick(); }
        catch (Exception error) { _error("type=load_shedding_error\titem=crowd\treason=" + error.GetType().Name + ": " + error.Message); }
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
            + ";not_arrived=" + (alive - near) + ";farthest_m=" + far.ToString("0.0", CultureInfo.InvariantCulture);
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
        var syncFar = _settings.HiddenSyncDistance * _settings.HiddenSyncDistance;
        ThrottledSyncs.Clear();
        var farSkins = 0;
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
            if (CrowdSwitches.ClientHiddenSyncThrottle && !_host && hasPlayer && entry.Sync != IntPtr.Zero && distance > syncFar
                && enemy.MovingCuller is { } culler && !culler.IsShown)
                ThrottledSyncs.Add(entry.Sync);
        }
        Probe.Enemies = _enemies.Count; Probe.FarSkins = farSkins; Probe.ThrottledSyncs = ThrottledSyncs.Count;
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

    private void Release(Entry entry, bool alive)
    {
        if (alive && entry.FarSkin) SetSkins(entry, false);
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
            + ";agents_retyped=" + _agentsRetyped + ";sync_skipped=" + CrowdPatches.SyncSkippedTotal + ";deferred_ticks=" + DeferredTicks;
    }

    internal string Summary()
        => "enemies_registered_now=" + _enemies.Count + "\tfar_skin_changes=" + _skinsChanged
            + "\tsync_frames_skipped=" + CrowdPatches.SyncSkippedTotal
            + "\tbudget_writes=" + _budgetWrites + "\tavoidance_writes=" + _avoidanceWrites + "\tagents_retyped=" + _agentsRetyped + "\tpathfinding_writes=" + _pathWrites
            + "\tdeferred_ticks=" + DeferredTicks;

    internal void EndLevel()
    {
        foreach (var entry in _enemies) Release(entry, entry.Enemy != null);
        _enemies.Clear(); ThrottledSyncs.Clear();
        // Globals go back to the game's values at the end of each level; the next level applies them again.
        _host = false;
        SyncGlobals();
        _budgetManager = IntPtr.Zero; _vanillaBudgets = null;
        Probe.Enemies = Probe.FarSkins = Probe.ThrottledSyncs = 0;
    }
}
