using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The enemy-domain Data read operators not already owned by ForgeEngine (`alive`, `type`, `sleep`,
/// `where`, `tagged`, `group`, `phase`).
///
/// Every one of them is a `query` step, and every one of them reads the world exactly once through the kernel's
/// budgeted session: the enemy it is about is resolved through `TrySnapshot`, which is what charges the query
/// budget and proves the reference still names a life. The life state, behaviour `aiState` and position are read
/// from that snapshot. Type, tag, group and phase add the domain-specific reads their contracts require, each
/// against the same life the snapshot just verified. The zone `where` answer comes from the
/// kernel's own zone read (`RuntimeQuerySession.TryZone`), which is the provider's registered
/// `EntityZones` responder and the only zone path this package has.
///
/// A value that cannot be read is a refusal with a code, never a zero and never an empty answer: a behaviour
/// state no `ai_state` member covers is `aiState == "disabled"` and is refused rather than reported as awake, a
/// life whose type block does not read has no type (`enemy-type-unavailable`), and a life whose behaviour machine
/// is not a boss has no phase (`not-a-phase-receiver`). A zone read that cannot be made is a refusal of the same
/// kind — an enemy the provider cannot place never arrives as an absent zone — so no port in this family is
/// nullable.
///
/// The evaluator table is built here and handed to the module's own registration, so the row names and the
/// handlers that answer them cannot drift apart.</summary>
internal sealed partial class EnemyNodeValueReads
{
    /// <summary>A value read with no enemy domain behind it. It is a refusal and not an empty value: the rows
    /// exist, this session just cannot read them.</summary>
    internal const string SourceUnavailableCode = "enemy-value-source-unavailable";
    internal const string TypeUnavailableCode = "enemy-type-unavailable";
    internal const string SpawnOriginUnavailableCode = "enemy-spawn-origin-unavailable";
    internal const string StateUnavailableCode = "enemy-state-unavailable";
    internal const string TagUnavailableCode = "enemy-tag-unavailable";
    internal const string GroupUnavailableCode = "enemy-group-unavailable";
    internal const string MissingFieldCode = "missing-field";

    /// <summary>The member of the shared `ai_state` set that means "no state an author can act on". A behaviour
    /// machine that does not read is published as this member by the observer, and a question about sleep cannot
    /// be answered from it.</summary>
    private const string DisabledState = "disabled";
    /// <summary>The one member of that set which is hibernation; the observer maps both native sleep states to
    /// it, so the value row and `forge.trigger.enemy.awakened` cannot disagree about what sleep is.</summary>
    private const string HibernatingState = "hibernating";

    internal static EnemyNodeValueReads? Installed { get; private set; }

    private readonly Func<EntityReference, EnemyAgent?> _enemyOf;
    private readonly Func<EntityReference, uint?> _typeOf;
    private readonly Func<EntityReference, Agents.AgentMode?> _spawnModeOf;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    /// <summary>`enemyOf` is the module's own current-instance lookup for `gtfo.enemy`: the one way this layer
    /// reaches a native agent, and the same lookup the module's own actions resolve recipients through.
    /// `spawnModeOf` is the module's own record of how the life entered the level, which the spawn hook wrote
    /// once and nothing else publishes. `typeOf` is the module's own record of the life's type, captured when the
    /// life was tracked.</summary>
    internal EnemyNodeValueReads(IForgeEngine kernel, Func<EntityReference, EnemyAgent?> enemyOf,
        Func<EntityReference, uint?> typeOf, Func<EntityReference, Agents.AgentMode?> spawnModeOf)
    {
        _ = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _enemyOf = enemyOf ?? throw new ArgumentNullException(nameof(enemyOf));
        _typeOf = typeOf ?? throw new ArgumentNullException(nameof(typeOf));
        _spawnModeOf = spawnModeOf ?? throw new ArgumentNullException(nameof(spawnModeOf));
    }

    internal static EnemyNodeValueReads Attach(IForgeEngine kernel, Func<EntityReference, EnemyAgent?> enemyOf,
        Func<EntityReference, uint?> typeOf, Func<EntityReference, Agents.AgentMode?> spawnModeOf)
    {
        Installed = new EnemyNodeValueReads(kernel, enemyOf, typeOf, spawnModeOf);
        return Installed;
    }

    internal static void Detach(EnemyNodeValueReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table this provider's registration composes: one entry per value row, each name
    /// declared with a shape in `EnemyNodeValueContract.ValueShapes`.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [EnemyNodeValueContract.TypeHandler] = Evaluate(EnemyNodeValueContract.TypeShape, Type),
        [EnemyNodeValueContract.TypeInHandler] = Evaluate(EnemyNodeValueContract.TypeInShape, TypeIn),
        [EnemyNodeValueContract.SleepingHandler] = Evaluate(EnemyNodeValueContract.SleepingShape, Sleeping),
        [EnemyNodeValueContract.TaggedHandler] = Evaluate(EnemyNodeValueContract.TaggedShape, Tagged),
        [EnemyNodeValueContract.GroupHandler] = Evaluate(EnemyNodeValueContract.GroupShape, Group),
        [EnemyNodeValueContract.PhaseHandler] = Evaluate(EnemyNodeValueContract.PhaseShape, Phase),
        [EnemyNativeStateContract.Handler(Blocks.QueryEnemyInCombat.Id)] = Evaluate(EnemyNativeStateContract.InCombatShape, InCombat),
        [EnemyNativeStateContract.Handler(Blocks.QueryEnemyLimb.Id)] = Evaluate(EnemyNativeStateContract.LimbShape, Limb)
    };

    /// <summary>One row's evaluator: the row's `enemy` input is read through its own shape's slot, resolved once at
    /// registration, so no evaluation looks the port up by name or renders the input document.</summary>
    private static EvaluatorHandler Evaluate(HandlerShape shape,
        Action<EnemyNodeValueReads, EvaluationContext, EntityReference, RuntimeEntitySnapshot, EvaluationOutputs> answer)
    {
        var enemy = shape.InputSlot(EnemyNodeValueContract.EnemyPort);
        return (context, outputs) =>
        {
            if (Installed is not { } reads) throw new RuntimeContractException(SourceUnavailableCode, "No enemy value source is attached.");
            reads.CheckThread();
            var (reference, snapshot) = Read(context, enemy);
            answer(reads, context, reference, snapshot, outputs);
        };
    }

    /// <summary>The one budgeted read every value row starts with: the enemy the row is about, resolved through
    /// the session's own snapshot read. The row's `enemy` port is required, so a frame without one is refused by
    /// name; the entity is the row's, never the event's or the mount's.</summary>
    private static (EntityReference Reference, RuntimeEntitySnapshot Snapshot) Read(EvaluationContext context, HandlerSlot enemy)
    {
        if (!context.TryEntity(enemy, out var reference))
            throw new RuntimeContractException(MissingFieldCode, EnemyNodeValueContract.EnemyPort);
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Enemy read failed: " + code);
        return (reference, snapshot!);
    }

    // The six answers, over the one budgeted read the evaluator already made.

    /// <summary>The official type id as decimal text — the same spelling the `enemy-type` mount matcher compares,
    /// so a value an author reads and a mount an author writes are one string — beside how the life entered the
    /// level, as the index of the shared `enemy_spawn_origin` set. A life whose own block does not read and a
    /// life whose spawn mode this provider never observed are two different refusals by name: neither is a value
    /// the game answered.</summary>
    internal static void AnswerType(uint? type, int? spawnOrigin, EvaluationOutputs outputs)
    {
        if (type is not { } id)
            throw new RuntimeContractException(TypeUnavailableCode, "This life's enemy block does not read.");
        if (spawnOrigin is not { } origin)
            throw new RuntimeContractException(SpawnOriginUnavailableCode, "This life's spawn mode was not observed.");
        outputs.Text(0, id.ToString(CultureInfo.InvariantCulture));
        outputs.Enum(1, origin);
    }

    /// <summary>Native `Agents.AgentMode` to the index of the shared `enemy_spawn_origin` set: `Hibernate` and
    /// `Scout` are `hibernating`, `Agressive` is `aggressive`, and every other mode is `other`. The set names the
    /// spawn mode, not the system that spawned the agent. The members are named rather than counted so a build
    /// that reorders the enum cannot shift the set.</summary>
    internal static int SpawnOriginIndex(Agents.AgentMode mode) => mode switch
    {
        Agents.AgentMode.Hibernate or Agents.AgentMode.Scout => 0,
        Agents.AgentMode.Agressive => 1,
        _ => 2
    };

    /// <summary>Sleep is the snapshot's `aiState` compared with the shared set's own hibernation member. A frame
    /// with no behaviour state answers `disabled`, which is not an observation about sleep and is refused.</summary>
    internal static void AnswerSleeping(RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (snapshot.AiState is not { } state || state == DisabledState)
            throw new RuntimeContractException(StateUnavailableCode, "This life's behaviour state does not read.");
        outputs.Boolean(0, state == HibernatingState);
    }

    /// <summary>`remaining` is declared in ticks (contract §3.2 时间单位); the game's timer is seconds.</summary>
    private static void AnswerTagged(EnemyEntityObserver.TagReading? tag, double ticksPerSecond, EvaluationOutputs outputs)
    {
        if (tag is not { } reading)
            throw new RuntimeContractException(TagUnavailableCode, "This life's tag state does not read.");
        outputs.Boolean(0, reading.Tagged);
        outputs.Number(1, reading.RemainingSeconds * ticksPerSecond);
    }

    private static void Type(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerType(reads._typeOf(reference), reads.ReadSpawnOrigin(reference), outputs);

    private static readonly HandlerSlot TypesSlot = EnemyNodeValueContract.TypeInShape.InputSlot("types");

    /// <summary>Whether the life's official type is one of the listed `enemy` resources. The list is what the website
    /// compiled: a vanilla type, an authored enemy already renumbered to its exported block, and a kind already
    /// expanded into its member types, all as the same decimal spelling the type read answers. The type is the one
    /// <see cref="Type"/> reads; a list that does not read is a refusal, an empty one matches nothing.</summary>
    private static void TypeIn(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (reads._typeOf(reference) is not { } type)
            throw new RuntimeContractException(TypeUnavailableCode, "This life's enemy block does not read.");
        var count = context.Count(TypesSlot);
        if (count < 0) throw new RuntimeContractException(MissingFieldCode, "types");
        var match = false;
        for (var index = 0; index < count && !match; index++)
            match = context.TryResource(TypesSlot, index, out var listed)
                && uint.TryParse(listed.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var listedType) && listedType == type;
        outputs.Boolean(0, match);
    }

    /// <summary>The spawn mode the spawn hook recorded for the life, as the index of the shared
    /// `enemy_spawn_origin` set, or null when this provider never observed the life's spawn.</summary>
    private int? ReadSpawnOrigin(EntityReference reference)
        => _spawnModeOf(reference) is { } mode ? SpawnOriginIndex(mode) : null;

    private static void Sleeping(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerSleeping(snapshot, outputs);

    private static void Tagged(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerTagged(reads.ReadTag(reference), context.TicksPerSecond, outputs);

    private EnemyEntityObserver.TagReading? ReadTag(EntityReference reference)
    {
        var enemy = _enemyOf(reference);
        if (enemy == null) return null;
        var tag = EnemyEntityObserver.ReadTag(enemy);
        return ReferenceEquals(_enemyOf(reference), enemy) ? tag : null;
    }

    private static void Group(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference,
        RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs) => AnswerGroup(reads.ReadGroup(reference), outputs);

    /// <summary>One group's own facts: the state as the index of the shared `enemy_group_state` set, the kind as
    /// the index of the shared `enemy_group_kind` set, and the patrol counter as the number the game keeps.</summary>
    internal sealed record GroupReading(int State, int GroupKind, float PatrolFrustration);

    /// <summary>The group row's answer, over the reading the reader made. A life the game put in no group is a
    /// refusal with a code, not an empty state: the group a life does not have is not the group a question about
    /// a group can be answered from.</summary>
    internal static void AnswerGroup(GroupReading? reading, EvaluationOutputs outputs)
    {
        if (reading is not { } group)
            throw new RuntimeContractException(GroupUnavailableCode, "This life belongs to no live enemy group.");
        // Q3: an enum port carries the member-set index, never the member name.
        outputs.Enum(0, group.State);
        outputs.Enum(1, group.GroupKind);
        outputs.Number(2, group.PatrolFrustration);
    }

    /// <summary>The group read goes through the module's own live-instance lookup, the way every other read this
    /// layer makes does, and the same instance is looked up again afterwards: a life that ended under the read
    /// is not a reading of either life.</summary>
    private GroupReading? ReadGroup(EntityReference reference)
    {
        var enemy = _enemyOf(reference);
        if (enemy == null) return null;
        var reading = ReadLiveGroup(enemy);
        return ReferenceEquals(_enemyOf(reference), enemy) ? reading : null;
    }

    /// <summary>The three facts one live group owns, read from the public members the game's own group logic
    /// reads: the group the life reaches through `EnemyAI.m_group`, the state the group publishes to every peer
    /// inside its replicated data packet (`EnemyGroup.Data.currentState`, an `EGS` member), the type it was
    /// spawned as and its patrol frustration. The declarations are spelled out so each answer is a member of a
    /// native enum and not a number that happens to live in the same field, and the group is read back
    /// afterwards, so a group replaced under the read is not a reading of either group.
    ///
    /// A member outside either declared vocabulary is refused rather than reported: the state is published as an
    /// index into the shared `enemy_group_state` set, and an ordinal the set does not have would be an index into
    /// somebody else's member.</summary>
    private static GroupReading? ReadLiveGroup(EnemyAgent enemy)
    {
        try
        {
            if (enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
            var ai = enemy.AI;
            var group = ai?.m_group;
            if (group == null || group.Pointer == IntPtr.Zero) return null;
            EGS state = group.Data.currentState;
            EnemyGroupType groupType = group.GroupType;
            float frustration = group.PatrolFrustration;
            var again = enemy.AI?.m_group;
            if (again == null || again.Pointer != group.Pointer) return null;
            if (GroupStateIndex(state) is not { } index || GroupKindIndex(groupType) is not { } kind) return null;
            return new GroupReading(index, kind, frustration);
        }
        catch (Exception caught)
        {
            EnemyLog.Contained(caught);
            // A value question is asked from the game's own dispatch, so a group whose native members throw
            // answers "no readable group" instead of escaping into the caller.
            return null;
        }
    }

    /// <summary>Native `EGS` to the index of the shared `enemy_group_state` set. The set is the native enum in
    /// its own declaration order — `idle` first, `debug_idle` last — so the member a native value names and the
    /// index this row publishes are decided in one place, and a member the set does not carry answers nothing.
    /// The members are named rather than counted so a build that reorders the enum cannot shift the set.</summary>
    internal static int? GroupStateIndex(EGS state) => state switch
    {
        EGS.Idle => 0,
        EGS.HuntersSpawn => 1,
        EGS.HuntersHunt => 2,
        EGS.HuntersSearch => 3,
        EGS.GuardsSpawn => 4,
        EGS.GuardRespawn => 5,
        EGS.GuardsIdle => 6,
        EGS.GuardsHunting => 7,
        EGS.PatrolSpawn => 8,
        EGS.PatrolMove => 9,
        EGS.PatrolIdle => 10,
        EGS.PatrolSearch => 11,
        EGS.PatrolCombat => 12,
        EGS.SurvivalSpawn => 13,
        EGS.SurvivalHunt => 14,
        EGS.DebugSpawn => 15,
        EGS.DebugIdle => 16,
        _ => null
    };

    /// <summary>Native `EnemyGroupType` to the index of the shared `enemy_group_kind` set, which is the native enum
    /// in its own declaration order. The members are named rather than counted, as for the state above.</summary>
    internal static int? GroupKindIndex(EnemyGroupType groupType) => groupType switch
    {
        EnemyGroupType.Hibernating => 0,
        EnemyGroupType.Patrolling => 1,
        EnemyGroupType.Hunters => 2,
        EnemyGroupType.Survival => 3,
        EnemyGroupType.DebugSpawnUnit => 4,
        _ => null
    };

    // ------------------------------------------------------------------ phase read

    /// <summary>`v-e-phase`: the phase the life's own boss behaviour machine holds, read from the one member the
    /// package's write row writes through (`Native/Observation/EnemyPhaseReader.cs`). The read adds nothing to the
    /// snapshot the evaluator already made; a life whose behaviour machine is not a boss has no phase, which the
    /// answer refuses by name rather than reporting as phase zero.</summary>
    private static void Phase(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerPhase(reads.ReadPhase(reference), outputs);

    /// <summary>The phase row's answer, over the reading the reader made. A life that is not a phase receiver — a
    /// plain behaviour machine, one whose getter does not read, or a value outside the one byte the replicated
    /// field carries — is a refusal with the same word the write row refuses such a recipient with: the phase does
    /// not exist on this life, and zero is a phase a boss really can be in.</summary>
    internal static void AnswerPhase(int? phase, EvaluationOutputs outputs)
    {
        if (phase is not { } current)
            throw new RuntimeContractException(ResultCode.NotAPhaseReceiver,
                "This life's behaviour machine is not a boss with a readable phase.");
        outputs.Integer(0, current);
    }

    /// <summary>The phase read goes through the module's own live-instance lookup, like every other read here, and
    /// the same instance is looked up again afterwards: a life that ended under the read is not a reading of either
    /// life.</summary>
    private int? ReadPhase(EntityReference reference)
    {
        var enemy = _enemyOf(reference);
        if (enemy == null) return null;
        var reading = ReadLivePhase(enemy);
        return ReferenceEquals(_enemyOf(reference), enemy) ? reading : null;
    }

    /// <summary>One life's phase, through the shared reader: the enemy is judged first, and its own behaviour
    /// machine is the one the read is about — a machine the life does not have, or one that is not a boss, answers
    /// nothing rather than a phase of zero.</summary>
    private static int? ReadLivePhase(EnemyAgent enemy)
    {
        if (enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        return EnemyPhaseReader.TryRead(enemy.AI?.m_behaviour, out int phase, out _) ? phase : null;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy value reads require the runtime's own simulation thread.");
    }
}
