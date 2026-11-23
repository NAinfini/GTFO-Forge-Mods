using System;
using System.Collections.Generic;
using AIGraph;
using Enemies;
using ForgeEngine.Framework;
using Player;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>Host-authoritative enemy behaviour actions, submitted through the game's own entry points:
///
/// * `forge.action.enemy.ability` submits `EnemyAbilities.UseAbility(AgentAbility, index)` — the game's own
///   ability trigger, whose `bool` answer says whether it triggered and which then runs that ability component's
///   `Trigger()`/`DoTrigger()`. The author names the ability through the `ability` resource this provider owns
///   (<see cref="EnemyAbilityResources"/>), including its explicit position within that kind's enabled
///   components. The target's GetAbility checks that exact position before submission and for readback.
/// * `forge.action.enemy.noise_emit` submits `NoiseManager.MakeNoise(NM_NoiseData)` — the game's only noise write
///   point. The struct carries the caller's position, the catalog's single radius, and the course node that
///   position belongs to, resolved with `AIG_CourseNode.TryGetCourseNode`, and, when the source is a player, that
///   player as the noise maker (otherwise none); the game's own listening, occlusion and propagation then run unchanged.
///
/// Both actions read their result back instead of claiming it: an ability whose native answer was false, or
/// whose machine does not report it running afterwards, is not reported as a committed one.
///
/// The rules themselves live in <see cref="AbilityDecision"/> / <see cref="NoiseDecision"/>, which name no game
/// type; this file is the bridge they are given, and the focused suite drives the same rules through its own
/// bridge.
///
/// `forge.action.enemy.investigate` is **not** implemented, and no half version of it is offered. Ruling 88
/// restates the design's reshaping of the row into a state request, and the design's own reason is that the game
/// has no "go and look at this position" API: the native behaviour machine is entered by
/// `EnemyBehaviour.ChangeState(EB_States)`, which is a different row — `forge.action.enemy.state_request`, owned
/// by the enemy control family, which also carries the handle recipient `investigate` never had. The reshaped row
/// leaves `state_request`'s ports nowhere to come from, so declaring an `investigate` row here would either
/// duplicate that row under a second id or advertise a `position`/`evidence_kind` request the game cannot carry.
/// See `ForgeEnemy/evidence/enemy-behavior-ledger.json`.
///
/// `forge.action.enemy.patrol`, `flee` and `threat_change` are deleted by ruling 88 and are not declared or
/// implemented anywhere in this package: no native entry point exists for a waypoint path, for a state machine
/// with no flee member, or for a threat table the game does not keep.</summary>
internal sealed partial class EnemyModule
{
    internal const string AbilityBinding = EnemyBehaviorContract.AbilityBinding;
    internal const string NoiseEmitBinding = EnemyBehaviorContract.NoiseEmitBinding;
    internal const string AbilityHandler = EnemyBehaviorContract.AbilityHandler;
    internal const string NoiseEmitHandler = EnemyBehaviorContract.NoiseEmitHandler;

    /// <summary>The two command handlers the contract's binding rows name, handed to the registration as one
    /// table so the binding, the handler and its shape are declared in one place.</summary>
    internal static Dictionary<string, CommandHandler> BehaviorHandlers(EnemyModule module)
        => new(StringComparer.Ordinal)
        {
            [AbilityHandler] = module.Ability,
            [NoiseEmitHandler] = module.NoiseEmit
        };

    /// <summary>How far from the requested position the course node may be sampled. The game's own node volumes
    /// are room-sized, so a radius under a metre would refuse a position on a node seam; this is the order of an
    /// agent's own footprint and is deliberately far below a room.</summary>
    private const float CourseNodeSampleDistance = 2f;

    /// <summary>`NM_NoiseData.yScale`: the vertical scale of the noise volume. The catalog declares one radius
    /// and no vertical extent, and a scale of 1 makes the vertical extent the same as the horizontal one, which
    /// is the only value the declared port can mean.</summary>
    private const float NoiseVerticalScale = 1f;
    /// <summary>`NM_NoiseData.raycastFirstNode`: the position is the caller's world point, not a sample the game
    /// has to trace back to a node; the node is resolved before the struct is built, so no first-node raycast is
    /// asked for.</summary>
    private const bool NoiseRaycastsFirstNode = false;

    /// <summary>The in-flight ability table. Owned by the module and keyed by enemy life and world epoch; the
    /// world change that empties every other per-life table here empties this one through <c>ClearWorld</c>, and a
    /// despawn drops its own row.</summary>
    private EnemyBehaviorLedger? _behaviors;
    private EnemyBehaviorLedger Behaviors => _behaviors ??= new EnemyBehaviorLedger();

    /// <summary>PERF(P5): the gate of `ability_interrupted`, resolved once at attach.</summary>
    private EnemyGate _abilityInterruptedGate = null!;

    private void CreateAbilityGates()
        => _abilityInterruptedGate = Gate(ForgeEnemy.EnemyAbilityInterruptedContract.BindingId);

    /// <summary>T2 record point `enemy.ability-index` (trace): the requested type-local index, together with the
    /// mixed table's kinds for diagnosis. The mixed table position is not the native submission index.</summary>
    private void TraceAbilityIndex(string key, EnemyAbilities abilities, Agents.AgentAbility ability, bool found, int index)
    {
        if (!Log.Logs(RuntimeLogLevel.Trace)) return;
        var comps = abilities.AllComps;
        int count = comps?.Length ?? 0;
        var layout = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i != 0) layout.Append(',');
            var component = comps![i];
            layout.Append(component == null ? "-" : ((byte)component.m_abilityType).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        Log.Write(RuntimeLogLevel.Trace, EnemyLogCodes.AbilityIndex, new RuntimeLogFacts
        {
            Entity = key,
            Values = new[]
            {
                RuntimeLogValue.Of("ability", (long)(byte)ability), RuntimeLogValue.Of("index", (long)(found ? index : -1)),
                RuntimeLogValue.Of("components", (long)count), RuntimeLogValue.Quote("layout", layout.ToString())
            }
        });
    }

    /// <summary>The bridge the two decisions ask, made once: it holds nothing but this module.</summary>
    private NativeBehaviorPorts? _behaviorPorts;
    private NativeBehaviorPorts BehaviorPorts => _behaviorPorts ??= new NativeBehaviorPorts(this);

    private CommandResult Ability(CommandContext context)
        => AbilityDecision.Run(context, BehaviorPorts, Behaviors, _kernel.WorldEpoch);

    private CommandResult NoiseEmit(CommandContext context)
        => NoiseDecision.Run(context, BehaviorPorts);

    /// <summary>Drops one enemy life's in-flight row, reporting the ability the despawn cut short. The readback is
    /// taken while the entry still resolves — the despawn path drops the row before it drops the entity — because
    /// a component the game no longer answers for proves nothing: an unreadable component publishes nothing rather
    /// than being claimed as an interruption, and neither does one the machine reports as finished. A module that
    /// never started an ability has no table to drop a row from, so the call stays a no-op rather than building
    /// one. The world's own teardown drops rows through <c>ClearBehaviors</c> and publishes nothing: a world going
    /// away is not an interruption of one ability.</summary>
    internal void ForgetBehavior(EntityReference enemy)
    {
        CheckThread();
        if (_behaviors == null) return;
        var running = _behaviors.Find(enemy.Id, enemy.WorldEpoch);
        if (running == null) return;
        _behaviors.End(enemy.Id, enemy.WorldEpoch);
        if (!TryAbilityComponent(Resolve(enemy), running.Ability, running.Index, out var component, out bool done)
            || component != running.Component) return;
        ReportAbilityDropped(running, done, ForgeEnemy.EnemyAbilityInterruptedContract.ReasonDespawn);
    }

    /// <summary>The tracked life a key names, through the module's one entry table: the key is the entity
    /// reference's own id, so a reference from another world or another life cannot name an entry here.</summary>
    private Entry? ResolveKey(string key)
    {
        const string prefix = EntityKinds.Enemy + ":";
        if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!ushort.TryParse(key.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ushort id)) return null;
        return _entities.TryGetValue(id, out var entry)
            && entry.Reference.Id == key && Resolve(entry.Reference) != null ? entry : null;
    }

    /// <summary>The ability table of one tracked life, or null when the enemy cannot be read at all.</summary>
    private static EnemyAbilities? Abilities(Entry? entry)
    {
        if (entry == null) return null;
        try { return entry.Enemy.Abilities; }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
    }

    /// <summary>The native GetAbility and UseAbility index the same per-kind Comps array. Bounds are checked
    /// before entering native code; a missing position is never replaced by the first component.</summary>
    internal static EnemyAbility? AbilityComponent(EnemyAbilities abilities, Agents.AgentAbility ability, int index)
    {
        var table = abilities.AbilityComps;
        int kind = (int)ability;
        if (table == null || kind < 0 || kind >= table.Length || index < 0) return null;
        var comps = table[kind]?.Comps;
        if (comps == null || index >= comps.Length || comps[index] == null) return null;
        var component = abilities.GetAbility(ability, index);
        return component != null && component.m_abilityType == ability && component.Pointer == comps[index].Pointer ? component : null;
    }

    /// <summary>The component one tracked life's own table holds for the requested ability, and the machine's
    /// `AbilityIsDone()` answer for it. False when the entry is gone, the ability is not registered on it, or the
    /// component cannot be reached, which is an unknown rather than an answer.</summary>
    private static bool TryAbilityComponent(Entry? entry, byte ability, int index, out IntPtr component, out bool done)
    {
        component = IntPtr.Zero;
        done = false;
        var abilities = Abilities(entry);
        if (abilities == null) return false;
        try
        {
            var abilityComponent = AbilityComponent(abilities, (Agents.AgentAbility)ability, index);
            if (abilityComponent == null) return false;
            component = abilityComponent.Pointer;
            done = abilityComponent.AbilityIsDone();
            return true;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); component = IntPtr.Zero; done = false; return false; }
    }

    /// <summary>Reports one ability end the ledger proved: the row is dropped while the machine's own
    /// `AbilityIsDone()` answer still says the ability is running. A row the machine reports as finished ended
    /// normally and publishes nothing, an ability whose native member is not one this provider owns is never
    /// published as an invented reference, and a component that could not be read was already dropped by the
    /// caller. Nothing here writes the world: the whole fact is a reading of a submission this provider already
    /// made.</summary>
    private void ReportAbilityDropped(RunningBehavior dropped, bool finished, string reason)
    {
        if (finished) return;
        if (!EnemyAbilityResources.TryReference(dropped.Ability, dropped.Index, out var ability)) return;
        var entry = ResolveKey(dropped.EnemyKey);
        if (entry == null) return;
        if (!CanPublishBehaviorFact(_abilityInterruptedGate, entry.Reference)) return;
        var fact = _abilityInterruptedGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerEnemyAbilityInterrupted.OutEnemy, entry.Reference);
        fact.Values.Resource(Blocks.TriggerEnemyAbilityInterrupted.OutAbility, ability);
        fact.Values.Text(Blocks.TriggerEnemyAbilityInterrupted.OutReason, reason);
        RaiseBehavior(fact, "gtfo.enemy.ability");
    }

    /// <summary>Drops every in-flight row. The world epoch is part of every key, so this is the explicit end for
    /// a world that is being torn down rather than a comparison every later read would have to repeat. A module
    /// that never started an ability has no table, and this stays the no-op the teardown path can always call.</summary>
    internal void ClearBehaviors() { CheckThread(); _behaviors?.Clear(); }

    /// <summary>The real bridge: every member asks this module's own tables and then the game's, and no answer
    /// leaves this type as a game object.</summary>
    internal sealed class NativeBehaviorPorts
    {
        private readonly EnemyModule _module;
        internal NativeBehaviorPorts(EnemyModule module) => _module = module;

        public bool CanExecute => _module.CanExecute;

        public bool IsCurrent(string key) => _module.ResolveKey(key) != null;

        public bool IsAlive(string key)
        {
            var entry = _module.ResolveKey(key);
            if (entry == null) return false;
            try { return entry.Enemy.Alive; }
            catch (Exception caught) { EnemyLog.Contained(caught); return false; }
        }

        public bool CanTrigger(string key)
        {
            // A puppet only shows a recording or a held pose; no ability of its own is triggered (ForgeCore Puppets).
            var entry = _module.ResolveKey(key);
            if (entry != null && ForgeCore.Puppets.IsPuppet(entry.Enemy)) return false;
            var abilities = Abilities(entry);
            if (abilities == null) return false;
            try { return abilities.CanTriggerAbilities; }
            catch (Exception caught) { EnemyLog.Contained(caught); return false; }
        }

        public bool HasAbility(string key, byte ability, int index, out IntPtr component)
        {
            component = IntPtr.Zero;
            var abilities = Abilities(_module.ResolveKey(key));
            if (abilities == null) return false;
            bool found;
            try
            {
                var selected = AbilityComponent(abilities, (Agents.AgentAbility)ability, index);
                found = selected != null;
                if (found) component = selected!.Pointer;
            }
            catch (Exception caught) { EnemyLog.Contained(caught); return false; }
            // The table read behind the trace record is native; a failure there is recorded at the same level and
            // never changes the lookup's answer.
            try { _module.TraceAbilityIndex(key, abilities, (Agents.AgentAbility)ability, found, index); }
            catch (Exception error) { _module.Log.Exception(RuntimeLogLevel.Trace, EnemyLogCodes.AbilityIndex, error); }
            return found;
        }

        public bool TryUseAbility(string key, byte ability, int index, out bool triggered)
        {
            triggered = false;
            var entry = _module.ResolveKey(key);
            var abilities = Abilities(entry);
            if (entry == null || abilities == null) return false;
            // The trigger is this action's one native write, and the entry point runs the component's own
            // `Trigger()`/`DoTrigger()` body (`evidence/enemy-behavior-ledger.json`). An exception it raises
            // therefore leaves the attempt in doubt, so it is not swallowed here: the decision half catches it and
            // reports an unknown commit, the same answer the focused suite's own bridge produces.
            triggered = abilities.UseAbility((Agents.AgentAbility)ability, index);
            return true;
        }

        public bool TryAbilityState(string key, byte ability, int index, out IntPtr component, out bool done)
            => TryAbilityComponent(_module.ResolveKey(key), ability, index, out component, out done);

        public void AbilityDropped(RunningBehavior dropped, bool finished, string reason)
            => _module.ReportAbilityDropped(dropped, finished, reason);

        // Indexed scan: Tags is a snapshot list, and a LINQ Contains would box an enumerator on the noise path.
        private static bool HasTag(IReadOnlyList<string> tags, string tag)
        {
            for (int i = 0; i < tags.Count; i++) if (string.Equals(tags[i], tag, StringComparison.Ordinal)) return true;
            return false;
        }

        // Spatial context comes from the owner of the exact source life. A held item's parent supplies only
        // its dimension; the item's own observation supplies its position and remains the result recipient.
        // A player source is also the noise's maker, as the game's own player noise names its player.
        private bool TryNoiseContext(EntityReference source, out (double X, double Y, double Z) position,
            out AIG_CourseNode? node, out PlayerAgent? maker, out string code)
        {
            position = default; node = null; maker = null; code = ResultCode.SourceMissing;
            try
            {
                if (source.WorldEpoch != _module._kernel.WorldEpoch || !_module._kernel.IsEntityCurrent(source)) return false;
                if (EntityKinds.Is(source, EntityKinds.Enemy))
                {
                    var enemy = _module.Resolve(source)?.Enemy;
                    if (enemy == null) return false;
                    var point = enemy.Position; position = (point.x, point.y, point.z); node = enemy.CourseNode;
                }
                else if (EntityKinds.Is(source, EntityKinds.Player))
                {
                    if (ForgePlayer.Native.PlayerHost.Players is not { } players) { code = ResultCode.PlayerDirectoryMissing; return false; }
                    var player = players.AgentOf(source);
                    if (player == null || players.ReferenceOf(player) != source) return false;
                    var point = player.Position; position = (point.x, point.y, point.z); node = player.CourseNode; maker = player;
                }
                else
                {
                    var inspected = _module._kernel.InspectEntities(new[] { source });
                    if (!inspected.IsComplete) { code = inspected.Code; return false; }
                    var snapshot = inspected.Items.Count == 1 ? inspected.Items[0].Snapshot : null;
                    // A deployed object's owner can move to another dimension. It is not a spatial parent.
                    // Only the equipment observer's carried-item shape warrants inheriting the holder's node.
                    code = ResultCode.SourceContextUnavailable;
                    if (!EntityKinds.Is(source, EntityKinds.Equipment) || snapshot?.Ref != source || snapshot.Kind != EntityKinds.Equipment || HasTag(snapshot.Tags, "deployable")
                        || snapshot.Parent is not { } parent || !EntityKinds.Is(parent, EntityKinds.Player)) return false;
                    if (ForgePlayer.Native.PlayerHost.Players is not { } players) { code = ResultCode.PlayerDirectoryMissing; return false; }
                    var player = players.AgentOf(parent);
                    if (player == null || players.ReferenceOf(player) != parent || !_module._kernel.IsEntityCurrent(parent)) return false;
                    position = (snapshot.Position[0], snapshot.Position[1], snapshot.Position[2]); node = player.CourseNode;
                }
                code = ResultCode.SourceContextUnavailable;
                return node != null && node.Pointer != IntPtr.Zero && node.m_zone != null;
            }
            catch (RuntimeContractException error) { code = error.Code; return false; }
            catch (Exception error) { NativeFault.Note("enemy.noise.source-context", error); code = ResultCode.SourceContextUnavailable; return false; }
        }

        public bool TrySourcePosition(EntityReference source, out (double X, double Y, double Z) position, out string code)
        {
            if (!TryNoiseContext(source, out position, out _, out _, out code)) return false;
            code = ResultCode.SourcePositionUnavailable;
            return double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z);
        }

        /// <summary>Emits one noise. A source, when given, names the dimension the position is looked up in (or, for a
        /// source origin, the node itself); without one the position alone decides, every dimension of the level
        /// being asked.</summary>
        public bool TryEmitNoise(EntityReference? source, bool sourceOrigin, (double X, double Y, double Z) position,
            double radiusMin, double radius, bool crossDoor, int noiseType, out string code)
        {
            AIG_CourseNode? sourceNode = null;
            PlayerAgent? maker = null;
            code = ResultCode.SourceMissing;
            if (source is { } named && !TryNoiseContext(named, out _, out sourceNode, out maker, out code)) return false;
            code = ResultCode.NoiseNodeUnavailable;
            var point = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return false;
            var node = sourceNode;
            try
            {
                if (sourceNode == null)
                {
                    if (sourceOrigin || !ForgeCore.Level.CourseNodeAt(point, CourseNodeSampleDistance, out node, out _)
                        || node == null || node.Pointer == IntPtr.Zero || node.m_zone == null) return false;
                }
                else
                {
                    var dimension = (eDimensionIndex)sourceNode.m_zone!.m_dimensionIndex;
                    if (!sourceOrigin && (!ForgeCore.Level.CourseNodeIn((int)dimension, point, CourseNodeSampleDistance, out node)
                        || node == null || node.Pointer == IntPtr.Zero || node.m_zone == null
                        || (eDimensionIndex)node.m_zone.m_dimensionIndex != dimension)) return false;
                }
            }
            catch (Exception error) { NativeFault.Note("enemy.noise.node", error); return false; }
            if (source is { } live && !_module._kernel.IsEntityCurrent(live)) { code = ResultCode.SourceMissing; return false; }
            try
            {
                if (!ForgeCore.Combat.Noise(point, (float)radiusMin, (float)radius, NoiseVerticalScale, node, (NM_NoiseType)noiseType,
                        crossDoor, NoiseRaycastsFirstNode, maker))
                { code = ResultCode.NoiseRadiusEmpty; return false; }
            }
            // The source life or its node was collected between the checks above and the submission: nothing was emitted.
            catch (Exception error) when (error.GetType().Name == "ObjectCollectedException")
            {
                NativeFault.Note("enemy.noise.emit", error);
                code = ResultCode.SourceMissing;
                return false;
            }
            if (source is { } ended && !_module._kernel.IsEntityCurrent(ended))
                throw new RuntimeContractException(ResultCode.SourceMissing, "Noise source life ended during native submission.");
            return true;
        }
    }
}
