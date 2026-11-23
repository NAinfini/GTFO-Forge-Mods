using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine.AI;
using AirNavigation;
using ForgeCore;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal const string ActionStateKey = "forge.enemy.action-state";
    private sealed record ActionLife(EntityReference Target, uint EnemyType, int Seed)
    {
        internal Entry? Local;
        internal bool AwaitSpawn;
    }
    private sealed record SavedAction(EntityReference Target, ActionStateKind Kind, int LimbId,
        ActionValue Baseline, ActionLayer[] Layers);

    private DomainStateChannel.NativeStateSource? _actionStateSource;
    // Admission-time mapping only. Runtime hooks continue to use pre-resolved ActionState receivers.
    private readonly Dictionary<ushort, ActionLife> _actionLives = new();
    private readonly long[] _retiredActionLives = new long[ushort.MaxValue + 1];
    private readonly List<SavedAction> _pendingActions = new();
    private bool _restoringActions, _actionStateMaster;

    private static void RequireActionSnapshot(bool accepted, string code, string message)
    {
        if (!accepted) throw new RuntimeContractException(code, message);
    }
    private static void ActionSnapshotShape(JsonElement row, string fields)
    {
        RequireActionSnapshot(row.ValueKind == JsonValueKind.Object, "enemy-action-snapshot", "Expected an action-state object.");
        var expected = new HashSet<string>(fields.Split(' '), StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject())
            RequireActionSnapshot(expected.Remove(property.Name), "enemy-action-snapshot", "Unknown or duplicate action-state field: " + property.Name);
        RequireActionSnapshot(expected.Count == 0, "enemy-action-snapshot", "Missing action-state field.");
    }

    private void InitializeActionState()
    {
        _actionStateMaster = Session.IsHost;
        _actionStateSource = DomainStateChannel.RegisterNativeState(_kernel, ActionStateKey,
            CaptureActionState, RestoreActionState);
    }
    private void DisposeActionState() { _actionStateSource?.Dispose(); _actionStateSource = null; }
    private void ActionStateChanged()
    {
        if (Session.IsHost && !_restoringActions) _actionStateSource?.Changed();
    }
    private static ushort ActionLifeId(EntityReference reference)
    {
        const string prefix = EntityKinds.Enemy + ":";
        RequireActionSnapshot(reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            && ushort.TryParse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _),
            "enemy-action-life", "Enemy action identity is invalid.");
        ushort id = ushort.Parse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);
        RequireActionSnapshot(reference.Id == prefix + id.ToString(CultureInfo.InvariantCulture) && reference.LifeEpoch > 0,
            "enemy-action-life", "Enemy action identity is not canonical.");
        return id;
    }
    private void TrackActionLife(Entry entry)
    {
        if (Session.IsHost && !_followingMigration)
        {
            if (_actionLives.TryGetValue(entry.GlobalId, out var pending) && pending.Local == null && !pending.AwaitSpawn
                && pending.Target.LifeEpoch > _retiredActionLives[entry.GlobalId])
            {
                BindActionLife(pending, entry);
                ApplyPendingActions();
                ReconcileVolumes();
                RefreshActionSources();
                return;
            }
            // No action names a life that has just begun: the snapshot, which lists only named lives, has not changed.
            entry.ActionLife = entry.Reference;
            _actionLives[entry.GlobalId] = new(entry.Reference, entry.Enemy.EnemyDataID, entry.Enemy.Seed) { Local = entry };
            return;
        }
        if (_actionLives.TryGetValue(entry.GlobalId, out var life)
            && life.Target.LifeEpoch > _retiredActionLives[entry.GlobalId])
        {
            BindActionLife(life, entry);
            ApplyPendingActions();
            ReconcileVolumes();
            RefreshActionSources();
        }
    }
    private void BindActionLife(ActionLife life, Entry entry)
    {
        ValidateActionLife(life, entry);
        life.Local = entry; life.AwaitSpawn = false; entry.ActionLife = life.Target;
        _nextLife = Math.Max(_nextLife, life.Target.LifeEpoch);
    }
    private void ValidateActionLife(ActionLife life, Entry entry)
    {
        RequireActionSnapshot(life.Target.WorldEpoch == _kernel.WorldEpoch
            && IsLive(entry) && entry.Enemy.EnemyDataID == life.EnemyType && entry.Enemy.Seed == life.Seed,
            "enemy-action-native-life-mismatch", "Native spawn does not match the admitted enemy life.");
    }
    private void ForgetActionLife(Entry entry)
    {
        if (entry.ActionLife is not { } authority) return;
        _retiredActionLives[entry.GlobalId] = Math.Max(_retiredActionLives[entry.GlobalId], authority.LifeEpoch);
        if (_actionLives.TryGetValue(entry.GlobalId, out var life) && ReferenceEquals(life.Local, entry))
        {
            life.Local = null; life.AwaitSpawn = true;
            if (Session.IsHost) _actionLives.Remove(entry.GlobalId);
        }
        // A standing action on this life leaves through RemoveActionState, which reports the change itself.
        if (_pendingActions.RemoveAll(row => row.Target == authority) != 0) ActionStateChanged();
    }
    private void ClearActionLives()
    {
        _actionLives.Clear(); _pendingActions.Clear();
        Array.Clear(_retiredActionLives, 0, _retiredActionLives.Length);
    }
    private void PumpActionAuthority()
    {
        if (_actionStateMaster == Session.IsHost) return;
        _actionStateMaster = Session.IsHost;
        if (!_actionStateMaster) return;
        EndVolumeSync();
        // A migrated host activates the already admitted receivers once, on the Runtime's own tick.
        foreach (var state in _actionStates)
            if (state.Kind != ActionStateKind.Stagger && ActionReceiverCurrent(state) && state.Entry.Enemy.Alive)
                WriteActionState(state, state.Current);
        ActionStateChanged();
    }

    private ActionValue CanonicalActionValue(ActionValue value)
    {
        if (value.Policy.Source is not { } source || !EntityKinds.Is(source, EntityKinds.Enemy)) return value;
        var entry = Resolve(source);
        return entry?.ActionLife is { } canonical ? value with { Policy = value.Policy with { Source = canonical } } : value;
    }
    private ActionValue LocalActionValue(ActionValue value)
    {
        if (value.Policy.Source is not { } source || !EntityKinds.Is(source, EntityKinds.Enemy)) return value;
        return _actionLives.TryGetValue(ActionLifeId(source), out var life) && life.Target == source && life.Local is { } local
            ? value with { Policy = value.Policy with { Source = local.Reference } } : value;
    }
    private void RefreshActionSources()
    {
        // A snapshot may precede its source's native spawn. Bind that exact source token at spawn admission,
        // never to a later occupant of the same GlobalID, and never by a hot-path entity lookup.
        foreach (var state in _actionStates)
        {
            if (state.Kind != ActionStateKind.DamageRule) continue;
            var previous = state.Current;
            state.Baseline = LocalActionValue(state.Baseline);
            for (int index = 0; index < state.Layers.Count; index++)
            {
                var layer = state.Layers[index];
                var mapped = LocalActionValue(layer.Value);
                if (mapped != layer.Value) state.Layers[index] = layer with { Value = mapped };
            }
            if (previous != state.Current && Session.IsHost && ActionReceiverCurrent(state)) WriteActionState(state, state.Current);
        }
    }
    private JsonElement CaptureActionState()
    {
        CheckThread();
        // Only the lives an action names travel: a client binds exactly those, and every other enemy has nothing to bind.
        var named = new SortedDictionary<ushort, Entry>();
        void Name(Entry? entry) { if (entry != null && IsLive(entry)) named[entry.GlobalId] = entry; }
        void NameSource(ActionValue value)
        {
            if (value.Policy.Source is { } source && EntityKinds.Is(source, EntityKinds.Enemy)) Name(Resolve(source));
        }
        var states = new List<object>();
        foreach (var state in _actionStates)
        {
            // A synchronous immunity loan must never become a standing checkpoint or late-join state.
            if (state.Kind == ActionStateKind.Stagger || state.Layers.Count == 0 || !ActionReceiverCurrent(state)
                || !state.Entry.Enemy.Alive) continue;
            Name(state.Entry); NameSource(state.Baseline);
            foreach (var layer in state.Layers) NameSource(layer.Value);
            states.Add(new
            {
                target = state.Entry.ActionLife ?? state.Entry.Reference,
                kind = (int)state.Kind,
                limbId = state.LimbId,
                baseline = CanonicalActionValue(state.Baseline),
                layers = state.Layers.Select(layer => new
                {
                    handle = layer.Key?.Handle.ToJson(),
                    value = CanonicalActionValue(layer.Value),
                    blocked = layer.Blocked
                }).ToArray()
            });
        }
        // The volumes the host placed travel beside the states: every machine places its own copy (EnemyModule.VolumeState).
        var volumes = CaptureVolumes(Name);
        var lives = named.Values.Select(entry => (object)new
        {
            target = entry.ActionLife ?? entry.Reference,
            enemyType = entry.Enemy.EnemyDataID,
            seed = entry.Enemy.Seed
        }).ToList();
        return RuntimeJson.From(new { schemaVersion = "1.0.0", worldEpoch = _kernel.WorldEpoch, lives, states, volumes });
    }

    private void RestoreActionState(JsonElement snapshot)
    {
        CheckThread();
        ActionSnapshotShape(snapshot, "schemaVersion worldEpoch lives states volumes");
        RequireActionSnapshot(snapshot.GetProperty("schemaVersion").GetString() == "1.0.0"
            && snapshot.GetProperty("worldEpoch").GetInt64() == _kernel.WorldEpoch,
            "enemy-action-snapshot", "Enemy action snapshot belongs to another format or world; regenerate it.");
        var lives = new Dictionary<ushort, ActionLife>();
        foreach (var row in snapshot.GetProperty("lives").EnumerateArray())
        {
            ActionSnapshotShape(row, "target enemyType seed");
            var target = RuntimeJson.Entity(row.GetProperty("target"));
            ushort id = ActionLifeId(target);
            RequireActionSnapshot(target.WorldEpoch == _kernel.WorldEpoch && !lives.ContainsKey(id),
                "enemy-action-life", "Duplicate or stale enemy action life.");
            lives.Add(id, new(target, row.GetProperty("enemyType").GetUInt32(), row.GetProperty("seed").GetInt32()));
        }
        var actions = new List<SavedAction>();
        var keys = new HashSet<(ushort, int, int)>();
        foreach (var row in snapshot.GetProperty("states").EnumerateArray())
        {
            ActionSnapshotShape(row, "target kind limbId baseline layers");
            var target = RuntimeJson.Entity(row.GetProperty("target"));
            ushort id = ActionLifeId(target);
            int kind = row.GetProperty("kind").GetInt32(), limb = row.GetProperty("limbId").GetInt32();
            RequireActionSnapshot(lives.TryGetValue(id, out var life) && life.Target == target
                && kind >= 0 && kind <= (int)ActionStateKind.WakeLock && limb >= 0
                && (kind == (int)ActionStateKind.Limb || limb == 0) && keys.Add((id, kind, limb)),
                "enemy-action-state", "Action does not name one admitted enemy receiver.");
            // A checkpoint reload re-creates the level's enemies, and a death can reach a client before the snapshot
            // written while the enemy lived: either way the life the row names has ended, and the row ends with it.
            if (target.LifeEpoch <= _retiredActionLives[id]) { ActionLifeRetired(target); continue; }
            var layers = new List<ActionLayer>();
            foreach (var layer in row.GetProperty("layers").EnumerateArray())
            {
                ActionSnapshotShape(layer, "handle value blocked");
                var handle = layer.GetProperty("handle");
                RuntimeHandle restored = default;
                RequireActionSnapshot(handle.ValueKind == JsonValueKind.Null || RuntimeHandle.TryRead(handle, out restored) && restored.IsCast,
                    "enemy-action-handle", "Action restore handle must be an exact Runtime handle.");
                double blocked = layer.GetProperty("blocked").GetDouble();
                RequireActionSnapshot(double.IsFinite(blocked) && blocked >= 0, "enemy-action-state", "Invalid blocked amount.");
                layers.Add(new(handle.ValueKind == JsonValueKind.Null ? null : new ActionKey(CurrentHandle(restored), 0), ReadActionValue(layer.GetProperty("value"), (ActionStateKind)kind)) { Blocked = blocked });
            }
            RequireActionSnapshot(layers.Count > 0, "enemy-action-state", "An action must have an owned contribution.");
            actions.Add(new(target, (ActionStateKind)kind, limb, ReadActionValue(row.GetProperty("baseline"), (ActionStateKind)kind), layers.ToArray()));
        }
        var volumes = new List<SavedVolume>();
        foreach (var row in snapshot.GetProperty("volumes").EnumerateArray())
        {
            var saved = ReadSavedVolume(row, _kernel.WorldEpoch, lives);
            volumes.Add(saved with { Handle = saved.Handle is { } handle ? CurrentHandle(handle) : null });
        }
        // All structure and identities are checked before the first native write. Retain exact local bindings;
        // a new host token cannot retarget an already-bound pooled object before its native spawn boundary.
        foreach (var pair in lives)
        {
            var life = pair.Value;
            if (_actionLives.TryGetValue(pair.Key, out var prior))
            {
                if (prior.Target == life.Target) { life.Local = prior.Local; life.AwaitSpawn = prior.AwaitSpawn; }
                else life.AwaitSpawn = prior.Local != null;
            }
            if (life.Local == null && !life.AwaitSpawn && life.Target.LifeEpoch > _retiredActionLives[pair.Key]
                && _entities.TryGetValue(pair.Key, out var entry) && (entry.ActionLife == null || entry.ActionLife == life.Target))
                life.Local = entry;
            if (life.Local is { } current) ValidateActionLife(life, current);
        }
        // Resolve every receiver that already exists before changing either the live ledger or native fields.
        // A missing part must not partially apply a later row in the same admitted snapshot.
        foreach (var action in actions)
        {
            var life = lives[ActionLifeId(action.Target)];
            if (life.Local is { } entry && !life.AwaitSpawn) ResolveSavedAction(action, entry);
        }
        foreach (var life in lives.Values)
            if (life.Local is { } entry) BindActionLife(life, entry);
        _actionLives.Clear();
        foreach (var pair in lives) _actionLives.Add(pair.Key, pair.Value);
        var standing = new HashSet<(EntityReference, ActionStateKind, int)>();
        foreach (var action in actions) standing.Add((action.Target, action.Kind, action.LimbId));
        _restoringActions = true;
        try
        {
            for (int index = _actionStates.Count - 1; index >= 0; index--)
            {
                var state = _actionStates[index];
                if (state.Kind == ActionStateKind.Stagger) continue;
                if (state.Entry.ActionLife is not { } life || !standing.Contains((life, state.Kind, state.LimbId)))
                    RemoveActionState(index);
            }
            _pendingActions.Clear(); _pendingActions.AddRange(actions);
            ApplyPendingActions();
            TakeSavedVolumes(volumes);
        }
        finally { _restoringActions = false; }
    }

    private ActionValue ReadActionValue(JsonElement row, ActionStateKind kind)
    {
        ActionSnapshotShape(row, "type weakspot armor x y z speed tolerance policy behaviourState locomotionState");
        var policy = row.GetProperty("policy");
        ActionSnapshotShape(policy, "policy floor factor source damageKind flat");
        int policyId = policy.GetProperty("policy").GetInt32();
        EntityReference? source = policy.GetProperty("source").ValueKind == JsonValueKind.Null
            ? null : RuntimeJson.Entity(policy.GetProperty("source"));
        RequireActionSnapshot(source == null || source.WorldEpoch == _kernel.WorldEpoch && source.LifeEpoch > 0
            && (EntityKinds.Is(source, EntityKinds.Enemy) || EntityKinds.Is(source, EntityKinds.Player)),
            "enemy-action-source", "Damage source is not a current-world actor identity.");
        int? damageKind = policy.GetProperty("damageKind").ValueKind == JsonValueKind.Null ? null : policy.GetProperty("damageKind").GetInt32();
        float Number(JsonElement value, string name)
        {
            float number = value.GetProperty(name).GetSingle();
            RequireActionSnapshot(float.IsFinite(number), "enemy-action-state", "Nonfinite action value.");
            return number;
        }
        var value = new ActionValue(row.GetProperty("type").GetInt32(), Number(row, "weakspot"), Number(row, "armor"),
            Number(row, "x"), Number(row, "y"), Number(row, "z"), Number(row, "speed"), Number(row, "tolerance"),
            new(policyId, Number(policy, "floor"), Number(policy, "factor"), source, damageKind, Number(policy, "flat")),
            row.GetProperty("behaviourState").GetInt32(), row.GetProperty("locomotionState").GetInt32());
        RequireActionSnapshot(policyId >= 0 && policyId <= IncomingDamagePolicy.Clear && value.Policy.Floor >= 0
            && value.Policy.Factor >= 0 && value.Policy.Flat >= 0 && (damageKind == null || damageKind >= 0 && damageKind < DamageKindCount),
            "enemy-action-state", "Unknown damage policy or kind.");
        RequireActionSnapshot(kind switch
        {
            ActionStateKind.Limb => value.Type is >= 0 and <= 2 && value.Weakspot >= 0 && value.Armor >= 0,
            ActionStateKind.Phase => value.Type is >= 0 and <= byte.MaxValue,
            ActionStateKind.Move => value.Speed >= 0 && value.Tolerance >= 0,
            ActionStateKind.Stationary or ActionStateKind.WakeLock => value.Type is 0 or 1,
            _ => true
        }, "enemy-action-state", "Action value is not supported by this native receiver.");
        return value;
    }

    private ActionState ResolveSavedAction(SavedAction row, Entry entry)
    {
        var state = new ActionState { Entry = entry, Kind = row.Kind, LimbId = row.LimbId };
        var enemy = entry.Enemy;
        switch (row.Kind)
        {
            case ActionStateKind.Limb: state.Limb = EnemyLimbAccess.Get(enemy, row.LimbId); break;
            case ActionStateKind.Phase:
                state.Ai = enemy.AI;
                state.Boss = state.Ai?.m_behaviour?.TryCast<SquidBossBehaviour>();
                if (state.Boss == null) throw new RuntimeContractException("phase-unsupported", "Enemy has no native boss phase.");
                break;
            case ActionStateKind.Move:
                state.Ai = enemy.AI; state.Navigation = state.Ai?.m_navMeshAgent;
                state.Ground = state.Navigation?.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent;
                state.Air = state.Navigation?.TryCast<FlyingAirGraphAgent>();
                break;
            case ActionStateKind.DamageRule: state.Damage = enemy.Damage; break;
            case ActionStateKind.Stationary:
            case ActionStateKind.WakeLock:
                state.Ai = enemy.AI; state.Behaviour = state.Ai?.m_behaviour; state.Locomotion = state.Ai?.m_locomotion;
                break;
        }
        RequireActionSnapshot(ActionReceiverCurrent(state), "enemy-action-receiver-unavailable", "Native action receiver is unavailable.");
        return state;
    }
    /// <summary>The one answer to a saved action whose enemy life has ended: the row is dropped and named, the rest of the snapshot applies.</summary>
    private void ActionLifeRetired(EntityReference target)
        => Log.Write(RuntimeLogLevel.Info, "enemy.action-life-retired", new RuntimeLogFacts
        {
            Detail = "A saved action names an enemy life that already ended; the action was dropped.",
            Values = new[] { RuntimeLogValue.Of("target", target.Id), RuntimeLogValue.Of("lifeEpoch", target.LifeEpoch) }
        });
    private void ApplyPendingActions()
    {
        for (int index = _pendingActions.Count - 1; index >= 0; index--)
        {
            var row = _pendingActions[index];
            if (!_actionLives.TryGetValue(ActionLifeId(row.Target), out var life) || life.Target != row.Target
                || life.Local is not { } entry || life.AwaitSpawn) continue;
            // The game's death can reach this machine before the snapshot written while the enemy lived: that row ends here, by name.
            if (!IsLive(entry) || !entry.Enemy.Alive)
            {
                _pendingActions.RemoveAt(index);
                ActionLifeRetired(row.Target);
                continue;
            }
            var candidate = ResolveSavedAction(row, entry);
            var state = _actionStates.FirstOrDefault(state => state.SameReceiver(candidate) && ActionReceiverCurrent(state)) ?? candidate;
            state.Baseline = LocalActionValue(row.Baseline);
            state.Layers.Clear();
            foreach (var layer in row.Layers) state.Layers.Add(layer with { Value = LocalActionValue(layer.Value) });
            if (ReferenceEquals(state, candidate)) _actionStates.Add(state);
            // Filing before native code retains a cleanup owner even if a partial setter throws.
            WriteActionState(state, state.Current);
            _pendingActions.RemoveAt(index);
        }
    }
}
