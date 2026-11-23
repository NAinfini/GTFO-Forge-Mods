using System;
using System.Collections.Generic;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using GameData;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>Selected children remain native enemy groups and native survival waves. This adapter supplies a
/// group's private population and a wave's exact member/cost; placement, AI, replication and scheduling stay native.</summary>
internal sealed partial class EnemyModule
{
    internal sealed class ChildSource
    {
        internal EnemyGroupDataBlock Group = null!;
        internal EnemyPopulationDataBlock Population = null!;
        internal readonly List<ChildMember> Members = new();
        internal float Weight, MaximumRoleCost, MaximumCapCost;
        internal eEnemyRole? PrivateRole;
        internal eEnemyRoleDifficulty? PrivateDifficulty;
    }
    internal sealed class ChildMember
    {
        internal EnemyDataBlock Enemy = null!;
        internal SurvivalWavePopulationDataBlock WavePopulation = null!;
        internal float Weight, CapCost;
    }
    internal sealed class ChildAttack
    {
        internal EntityReference? Owner;
        internal string Body = "";
        internal EnemyAbility? Ability;
        internal EnemySpawnAttackValues Values = null!;
        internal ChildSource Source = null!;
        internal uint TypeId;
        internal string? File;
        internal long World;
        internal int Spawned;
    }
    private sealed class ChildWaveProfile
    {
        internal uint Enemy;
        internal string File = "";
        internal EnemySpawnAttackValues Values = null!;
    }
    private Dictionary<uint, ChildWaveProfile> _childWaveProfiles = new();
    private static Dictionary<uint, ChildWaveProfile> PrepareChildWaveProfiles(EnemyProfileStore profiles)
    {
        var result = new Dictionary<uint, ChildWaveProfile>();
        foreach (var enemy in profiles.Enemies)
            foreach (var values in enemy.Abilities?.Spawning ?? Array.Empty<EnemySpawnAttackValues>())
                if (values.WaveSettings is uint settings && !result.TryAdd(settings,
                    new ChildWaveProfile { Enemy = enemy.EnemyTypeId, File = enemy.Source, Values = values }))
                    throw new InvalidOperationException("Native child wave identity " + settings + " is claimed by multiple attacks: " + enemy.Source);
        return result;
    }
    internal sealed class ChildWave
    {
        internal ChildAttack Attack = null!;
        internal SurvivalWave Wave = null!;
        internal int? Remaining;
        internal int Spawned;
        internal ChildMember? Next;
        internal bool BudgetWaiting;
    }
    internal sealed class ChildGroupScope : IDisposable
    {
        private readonly EnemyModule _module;
        private readonly ChildGroupScope? _previous;
        internal readonly EnemyGroup Group;
        private readonly SurvivalWavePopulationDataBlock? _population;
        internal readonly ChildWave Wave;
        internal readonly float Cost;
        internal readonly int Before;
        private bool _closed;
        internal ChildGroupScope(EnemyModule module, EnemyGroup group, ChildWave wave, ChildMember member)
        {
            _module = module; Group = group; Wave = wave; Cost = member.CapCost;
            Before = group.Members?.Count ?? 0; _population = group.m_wavePopulationData;
            group.m_wavePopulationData = member.WavePopulation;
            _previous = module._childCostScope; module._childCostScope = this;
        }
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            // The population belongs to this call only, including an exceptional native return.
            Group.m_wavePopulationData = _population;
            if (ReferenceEquals(_module._childCostScope, this)) _module._childCostScope = _previous;
        }
    }
    private readonly Dictionary<IntPtr, List<ChildAttack>> _childAttacks = new();
    private readonly HashSet<IntPtr> _refusedChildAttacks = new();
    private readonly Dictionary<IntPtr, ChildWave> _childWaves = new();
    private ChildGroupScope? _childCostScope;
    private long _childWorld = -1;

    private void ForgetChildSpawning(EnemyAgent enemy)
    {
        if (_childAttacks.Count == 0 && _refusedChildAttacks.Count == 0) return;
        var comps = enemy.Abilities?.AllComps;
        if (comps == null) return;
        for (int index = 0; index < comps.Length; index++)
            if (comps[index] is { } ability) { _childAttacks.Remove(ability.Pointer); _refusedChildAttacks.Remove(ability.Pointer); }
    }
    private void ChildWorld()
    {
        if (_childWorld == _kernel.WorldEpoch) return;
        ClearChildSpawning();
    }
    private void ClearChildSpawning()
    {
        _childAttacks.Clear(); _refusedChildAttacks.Clear(); _childWaves.Clear();
        _childCostScope = null; _childWorld = _kernel.WorldEpoch;
    }
    private static EnemyAbility? ChildAbility(EnemyAgent enemy, int index)
    {
        var data = enemy.EnemyData?.AI_Abilities;
        if (data == null || index < 0 || index >= data.Count) return null;
        var selected = data[index];
        if (selected == null || !selected.Enabled) return null;
        int slot = 0;
        for (int at = 0; at < index; at++) if (data[at].Enabled && data[at].AbilityType == selected.AbilityType) slot++;
        var table = enemy.Abilities?.AbilityComps;
        int type = (int)selected.AbilityType;
        var components = table != null && type >= 0 && type < table.Length ? table[type]?.Comps : null;
        return components != null && slot < components.Length ? components[slot] : null;
    }
    private float ChildCapCost(EnemyDataBlock enemy)
    {
        if (_profiles?.Resolve(enemy.persistentID).Fields?.CostTowardsCap is float authored) return authored;
        var costs = EnemyCostManager.Current?.m_enemyTypeCosts;
        int type = (int)enemy.EnemyType;
        if (costs == null || type < 0 || type >= costs.Length) throw new InvalidOperationException(enemy.name + ": native cost table has no member");
        return costs[type];
    }
    private ChildSource ChildSourceFor(EnemySpawnAttackValues values)
    {
        if (values.Group is not uint groupId) throw new InvalidOperationException("The selected attack has no enemy group.");
        var group = EnemyGroupDataBlock.GetBlock(groupId);
        if (group == null) throw new InvalidOperationException((values.SourceName ?? "Enemy group") + " / " + groupId + ": missing EnemyGroupDataBlock");
        uint populationId = values.Population ?? RundownManager.ActiveExpedition.Expedition.EnemyPopulation;
        var population = EnemyPopulationDataBlock.GetBlock(populationId);
        if (population == null) throw new InvalidOperationException(group.name + ": missing enemy population " + populationId);
        var source = new ChildSource { Group = group, Population = population };
        var privateKeys = new HashSet<(eEnemyRole Role, eEnemyRoleDifficulty Difficulty)>();
        foreach (var role in group.Roles)
        {
            float roleWeight = role.Distribution == eEnemyRoleDistribution.Force_One ? 1f : EnemyGroupScoreSpawnData.GetPopMultiplier(role);
            if (!(roleWeight > 0f)) continue;
            foreach (var row in population.RoleDatas)
            {
                if (row.Role != role.Role || row.Difficulty != group.Difficulty || !(row.Weight > 0f)) continue;
                var child = EnemyDataBlock.GetBlock(row.Enemy);
                if (child == null) throw new InvalidOperationException(group.name + ": missing enemy " + row.Enemy);
                float cost = ChildCapCost(child);
                if (!(float.IsFinite(row.Cost) && row.Cost >= 0f && float.IsFinite(cost) && cost >= 0f))
                    throw new InvalidOperationException(child.name + ": native population/cap cost is invalid");
                float weight = roleWeight * row.Weight;
                if (!float.IsFinite(weight)) throw new InvalidOperationException(child.name + ": native spawn weight is invalid");
                // This is a private native object, not a globally registered alias or a narrowed packet ID.
                var wavePopulation = new SurvivalWavePopulationDataBlock
                {
                    WaveRoleWeakling = child.persistentID,
                    WaveRoleStandard = child.persistentID,
                    WaveRoleSpecial = child.persistentID,
                    WaveRoleMiniBoss = child.persistentID,
                    WaveRoleBoss = child.persistentID
                };
                source.Members.Add(new ChildMember { Enemy = child, WavePopulation = wavePopulation, Weight = weight, CapCost = cost });
                source.Weight += weight;
                source.MaximumRoleCost = Math.Max(source.MaximumRoleCost, row.Cost);
                source.MaximumCapCost = Math.Max(source.MaximumCapCost, cost);
                if (values.Population != null) privateKeys.Add((row.Role, row.Difficulty));
            }
        }
        if (source.Members.Count == 0 || !float.IsFinite(source.Weight))
            throw new InvalidOperationException(group.name + ": the chosen population has no loadable members for this group's roles/difficulty");
        if (values.Population != null)
        {
            if (privateKeys.Count != 1)
                throw new InvalidOperationException(group.name + ": a private birthing population must expose exactly one role/difficulty key");
            foreach (var key in privateKeys) { source.PrivateRole = key.Role; source.PrivateDifficulty = key.Difficulty; }
        }
        return source;
    }
    private void PrepareChildSpawning(EnemyAgent enemy, uint typeId, string? file, EnemySpawnAttackValues[] attacks)
    {
        ChildWorld();
        foreach (var values in attacks)
        {
            var ability = ChildAbility(enemy, values.AbilityIndex);
            string label = enemy.EnemyData?.name + " / attack " + values.AbilityIndex;
            try
            {
                if (ability == null) throw new InvalidOperationException(label + ": the selected native ability is absent or disabled");
                var birthing = ability.TryCast<EAB_Birthing>();
                var wave = ability.TryCast<EAB_SpawnChildrenWave>();
                if (birthing == null && wave == null) throw new InvalidOperationException(label + ": this native ability cannot spawn children");
                var source = ChildSourceFor(values);
                if (birthing != null)
                {
                    if (values.WaveIndex != null || values.WaveSettings != null || values.WavePopulation != null) throw new InvalidOperationException(label + ": EAB_Birthing has no survival-wave identity");
                    if (enemy.Locomotion?.BirtherGiveBirth == null || enemy.Locomotion.m_animator == null)
                        throw new InvalidOperationException(label + ": native EAB_Birthing.Trigger requires EnemyLocomotion.BirtherGiveBirth and its Animator");
                    birthing.m_groupData = source.Group.persistentID;
                    birthing.m_groupDataBlock = source.Group;
                    // Native DoTrigger multiplies this group's role-cost budget by the allowed child count.
                    if (_profiles?.Resolve(typeId).Abilities?.Birthing?.ChildrenCost == null) birthing.m_childrenCost = source.MaximumRoleCost;
                    if (values.Count is int count) { birthing.m_childrenPerBirth = count; birthing.m_childrenAllowedSpawn = count; }
                    if (values.Interval is float interval) birthing.m_minDelayUntilNextBirth = birthing.m_maxDelayUntilNextBirth = interval;
                    if (values.Delay is float delay) birthing.m_pauseTimer = Clock.Time + delay;
                }
                else
                {
                    int index = values.WaveIndex ?? 0;
                    if (wave!.waveData == null || index < 0 || index >= wave.waveData.Count)
                        throw new InvalidOperationException(label + ": native wave " + index + " does not exist");
                    if (values.WaveSettings is not uint settings || values.WavePopulation is not uint population
                        || SurvivalWaveSettingsDataBlock.GetBlock(settings) == null || SurvivalWavePopulationDataBlock.GetBlock(population) == null)
                        throw new InvalidOperationException(label + ": compiled native wave settings/population is missing");
                    var data = wave.waveData[index]; data.WaveSettings = settings; data.WavePopulation = population;
                    if (values.Delay is float delay) data.SpawnDelay = delay;
                    wave.waveData[index] = data;
                }
                if (!_entities.TryGetValue(enemy.GlobalID, out var owner) || owner.EnemyPointer != enemy.Pointer)
                    throw new InvalidOperationException(label + ": native parent life is not registered");
                var attack = new ChildAttack { Owner = owner.Reference, Body = enemy.EnemyData?.name ?? label, Ability = ability, Values = values, Source = source, TypeId = typeId, File = file, World = _kernel.WorldEpoch };
                if (!_childAttacks.TryGetValue(ability.Pointer, out var rows)) _childAttacks.Add(ability.Pointer, rows = new List<ChildAttack>());
                rows.RemoveAll(row => row.Values.WaveIndex == values.WaveIndex); rows.Add(attack);
                ChildRecord(attack, "prepared", source.Group.persistentID, 0, source.MaximumCapCost);
            }
            catch (Exception error)
            {
                if (ability != null) _refusedChildAttacks.Add(ability.Pointer);
                AbilityMissing(typeId, file, "child-spawning", label + ": " + error.GetBaseException().Message);
            }
        }
    }
    internal bool ChildAttackAllowed(EnemyAbility ability)
    {
        ChildWorld();
        if (_refusedChildAttacks.Contains(ability.Pointer)) return false;
        if (!_childAttacks.TryGetValue(ability.Pointer, out var attacks)) return true;
        if (ability.TryCast<EAB_Birthing>() is { m_childrenPerBirth: 0 }) return false;
        if (ability.TryCast<EAB_SpawnChildrenWave>() is { } wave)
            foreach (var attack in attacks)
            {
                int index = attack.Values.WaveIndex ?? 0;
                if (wave.waveData == null || index >= wave.waveData.Count
                    || wave.waveData[index].WaveSettings != attack.Values.WaveSettings || wave.waveData[index].WavePopulation != attack.Values.WavePopulation)
                {
                    AbilityMissing(attack.TypeId, attack.File, "child-spawning", attack.Body + ": native phase identity changed after preparation");
                    return false;
                }
            }
        return true;
    }

    internal void SelectChildPopulation(EnemyGroupScoreSpawnData data, ref eEnemyRole role, ref eEnemyRoleDifficulty difficulty)
    {
        ChildWorld();
        var ability = data.m_group?.BirthersAbility;
        if (ability == null || !_childAttacks.TryGetValue(ability.Pointer, out var rows)) return;
        if (rows.Count != 1) throw new InvalidOperationException("Birthing attack has ambiguous population.");
        var source = rows[0].Source;
        data.m_popData = source.Population;
        // EAB_Birthing asks AddEnemyData for vanilla BirtherChild/Easy even after its group is replaced.
        // Only a compiled single-enemy helper carries an explicit private Population; route that call to
        // the private table's exact isolated key. Group choices and unconfigured mothers stay native.
        if (source.PrivateRole is eEnemyRole selectedRole && source.PrivateDifficulty is eEnemyRoleDifficulty selectedDifficulty)
        {
            role = selectedRole;
            difficulty = selectedDifficulty;
        }
    }
    internal int? LimitChildGroup(EnemyGroupScoreSpawnData data)
    {
        ChildWorld();
        var ability = data.m_group?.BirthersAbility;
        if (ability == null || !_childAttacks.TryGetValue(ability.Pointer, out var attacks)) return null;
        int wanted = Math.Max(0, (int)ability.m_childrenAllowedSpawn);
        int forced = data.m_enemyData_Forced.Count;
        // Normalize the native selection before GeneratePlacements, not after the constructor has
        // already allocated placement slots. Keep its chosen IDs/modes and relative amounts.
        if (forced > wanted)
        {
            data.m_enemyData_Forced.RemoveRange(wanted, forced - wanted);
            data.m_enemyDataMode_Forced.RemoveRange(wanted, forced - wanted);
            forced = wanted;
        }
        long selected = 0;
        for (int at = 0; at < data.m_enemyAmount.Count; at++) selected += data.m_enemyAmount[at];
        int normalWanted = wanted - forced;
        if (selected > 0)
        {
            long cumulative = 0; int assigned = 0;
            for (int at = 0; at < data.m_enemyAmount.Count; at++)
            {
                cumulative += data.m_enemyAmount[at];
                int end = (int)(cumulative * normalWanted / selected);
                data.m_enemyAmount[at] = end - assigned; assigned = end;
            }
        }
        else if (forced > 0)
        {
            for (int at = forced; at < wanted; at++)
            {
                data.m_enemyData_Forced.Add(data.m_enemyData_Forced[at % forced]);
                data.m_enemyDataMode_Forced.Add(data.m_enemyDataMode_Forced[at % forced]);
            }
        }
        // The placements the native constructor asks for are its forced members plus its amounts; GetTotalEnemyCount sums
        // only the amounts (m_enemyAmount and the respawn amounts), so a group whose roles are all Force_One reads as empty there.
        int actual = data.m_enemyData_Forced.Count;
        for (int at = 0; at < data.m_enemyAmount.Count; at++) actual += data.m_enemyAmount[at];
        ChildRecord(attacks[0], actual == wanted ? "planned" : "native-selection-empty", 0, actual, ability.m_childrenCost);
        return actual;
    }
    internal void ChildWaveSpawned(SurvivalWave wave)
    {
        ChildWorld();
        // A pooled native pointer is not a wave life. An unconfigured wave must not inherit
        // the previous occupant's selected children within the same world.
        _childWaves.Remove(wave.Pointer);
        if (!Session.IsHost || wave.m_settings == null || !_childWaveProfiles.TryGetValue(wave.m_settings.persistentID, out var profile)) return;
        ChildAttack attack;
        try
        {
            if (wave.m_populationDataID != profile.Values.WavePopulation)
                throw new InvalidOperationException("Native wave population differs from compiled phase " + profile.Values.WaveIndex);
            var body = EnemyDataBlock.GetBlock(profile.Enemy);
            if (body == null) throw new InvalidOperationException("Missing parent enemy " + profile.Enemy);
            // The native packet has no parent-life identity. Resolve the compiled phase, never
            // infer an owner from call timing or the most recently spawned body.
            attack = new ChildAttack
            {
                Body = body.name,
                Values = profile.Values,
                Source = ChildSourceFor(profile.Values),
                TypeId = profile.Enemy,
                File = profile.File,
                World = _kernel.WorldEpoch
            };
        }
        catch (Exception error)
        {
            wave.StopEvent();
            AbilityMissing(profile.Enemy, profile.File, "child-spawning", error.GetBaseException().Message); return;
        }
        var copy = CopyChildWaveSettings(wave.m_settings);
        if (attack.Values.Count is int count)
        {
            float budget = attack.Source.MaximumCapCost * count;
            if (!float.IsFinite(budget)) { wave.StopEvent(); AbilityMissing(attack.TypeId, attack.File, "child-spawning", "Native wave cost overflow"); return; }
            // A zero-cost child still needs a live native wave. These are scheduling points,
            // not a charge to EnemyCostManager; Remaining stops it after the actual count.
            if (count > 0 && budget == 0f) budget = 1f;
            copy.m_populationPointsTotal = budget;
            copy.m_populationPointsPerWaveStart = copy.m_populationPointsPerWaveEnd = budget;
            copy.m_populationPointsPerGroupStart = copy.m_populationPointsPerGroupEnd = budget;
            copy.m_populationPointsMinPerGroup = 0f;
        }
        // Forge picks the member of each group itself (BeforeChildWaveGroup) and every role of a member's private wave
        // population names that same child (ChildSourceFor), so the copied wave is narrowed to a single role: the
        // native wave then has exactly one role slot to fill, never a second pick of its own over the source wave's
        // role mix. Removing this filter lets the source wave's own role weights choose again.
        copy.m_populationFilter = new Il2CppSystem.Collections.Generic.List<eEnemyType>(); copy.m_populationFilter.Add(eEnemyType.Weakling);
        copy.m_filterType = eEnemyFilterType.Include;
        if (attack.Values.Interval is float interval) copy.m_pauseBetweenGroups = interval;
        wave.SetSettings(copy);
        var state = new ChildWave { Attack = attack, Wave = wave, Remaining = attack.Values.Count };
        _childWaves[wave.Pointer] = state;
        ChildRecord(attack, "wave-started", attack.Source.Group.persistentID, 0, attack.Source.MaximumCapCost);
        if (state.Remaining == 0) wave.StopEvent();
    }
    internal bool BeforeChildWaveGroup(EnemyGroup group, out ChildGroupScope? scope)
    {
        scope = null; ChildWorld();
        if (!Session.IsHost || group.SurvivalWave == null || !_childWaves.TryGetValue(group.SurvivalWave.Pointer, out var wave)) return true;
        if (wave.Attack.World != _kernel.WorldEpoch || wave.Remaining == 0)
        { group.OnGroupSpawnCompleted(); return false; }
        if (Clock.Time < group.m_nextSpawn) return true;
        var source = wave.Attack.Source;
        var chosen = wave.Next;
        if (chosen == null)
        {
            // Drawn from the wave and its spawn count, not the game's global random, whose sequence the vanilla spawns read.
            float pick = (float)ForgeCore.DeterministicRandom.Unit((ulong)(long)group.SurvivalWave.Pointer, (ulong)wave.Spawned) * source.Weight;
            chosen = source.Members[source.Members.Count - 1];
            foreach (var member in source.Members) { pick -= member.Weight; if (pick < 0f) { chosen = member; break; } }
            wave.Next = chosen;
        }
        float available = EnemyCostManager.GetPointsLeft(group.CurrentCourseNode.m_dimension.DimensionIndex);
        if (chosen.CapCost > available)
        {
            if (!wave.BudgetWaiting) ChildRecord(wave.Attack, "budget-wait", chosen.Enemy.persistentID, wave.Spawned, chosen.CapCost);
            wave.BudgetWaiting = true; return false;
        }
        scope = new ChildGroupScope(this, group, wave, chosen);
        return true;
    }
    internal bool ChildGroupFinished(EnemyGroup group) => group.SurvivalWave != null
        && _childWaves.TryGetValue(group.SurvivalWave.Pointer, out var wave) && wave.Remaining == 0;
    internal bool AfterChildWaveGroup(ChildGroupScope? scope)
    {
        if (scope == null) return false;
        int actual = Math.Max(0, (scope.Group.Members?.Count ?? 0) - scope.Before);
        var wave = scope.Wave;
        if (actual > 0)
        {
            wave.Next = null; wave.BudgetWaiting = false;
            wave.Spawned += actual;
            if (wave.Remaining is int remaining) wave.Remaining = Math.Max(0, remaining - actual);
            if (wave.Attack.Values.Interval is float interval) scope.Group.m_nextSpawn = Clock.Time + interval;
            ChildRecord(wave.Attack, "spawned", scope.Group.Members![scope.Group.Members.Count - 1].EnemyData.persistentID, wave.Spawned, scope.Cost);
            if (wave.Remaining == 0) { scope.Group.OnGroupSpawnCompleted(); wave.Wave.StopEvent(); }
        }
        return wave.Remaining == 0;
    }
    internal void ChildBorn(EAB_Birthing ability, EnemyAgent child)
    {
        ChildWorld();
        if (!_childAttacks.TryGetValue(ability.Pointer, out var rows) || rows.Count != 1) return;
        var attack = rows[0];
        ChildRecord(attack, "born", child.EnemyData.persistentID, ++attack.Spawned, ChildCapCost(child.EnemyData));
    }
    internal bool ChildWaveCost(out float cost)
    { cost = _childCostScope?.Cost ?? 0f; return _childCostScope != null; }

    private void ChildRecord(ChildAttack attack, string result, uint child, int actual, float cost)
    {
        Log.Write(RuntimeLogLevel.Info, EnemyLogCodes.AbilityFieldsApplied, new RuntimeLogFacts
        {
            Entity = attack.Owner?.Id,
            Values = new[] { RuntimeLogValue.Of("enemyType", (long)attack.TypeId), EnemyLabel(attack.TypeId),
                RuntimeLogValue.Of("result", result),
                RuntimeLogValue.Of("parentLife", attack.Owner?.LifeEpoch ?? -1), RuntimeLogValue.Of("abilityIndex", (long)attack.Values.AbilityIndex),
                RuntimeLogValue.Of("waveIndex", (long)(attack.Values.WaveIndex ?? -1)),
                RuntimeLogValue.Of("waveSettings", (long)(attack.Values.WaveSettings ?? 0)), RuntimeLogValue.Of("wavePopulation", (long)(attack.Values.WavePopulation ?? 0)), RuntimeLogValue.Of("group", (long)attack.Source.Group.persistentID),
                RuntimeLogValue.Of("population", (long)attack.Source.Population.persistentID),
                RuntimeLogValue.Of("cachedGroup", (long)(attack.Ability?.TryCast<EAB_Birthing>()?.m_groupDataBlock?.persistentID ?? 0)),
                RuntimeLogValue.Of("child", (long)child),
                RuntimeLogValue.Of("requested", (long)(attack.Values.Count ?? -1)), RuntimeLogValue.Of("actual", (long)actual), RuntimeLogValue.Of("cost", (double)cost) }
        });
    }
    private static SurvivalWaveSettingsDataBlock CopyChildWaveSettings(SurvivalWaveSettingsDataBlock data) => new()
    {
        persistentID = data.persistentID,
        name = data.name,
        internalEnabled = data.internalEnabled,
        m_pauseBeforeStart = data.m_pauseBeforeStart,
        m_pauseBetweenGroups = data.m_pauseBetweenGroups,
        m_wavePauseMin_atCost = data.m_wavePauseMin_atCost,
        m_wavePauseMax_atCost = data.m_wavePauseMax_atCost,
        m_wavePauseMin = data.m_wavePauseMin,
        m_wavePauseMax = data.m_wavePauseMax,
        m_populationFilter = data.m_populationFilter,
        m_filterType = data.m_filterType,
        m_chanceToRandomizeSpawnDirectionPerWave = data.m_chanceToRandomizeSpawnDirectionPerWave,
        m_chanceToRandomizeSpawnDirectionPerGroup = data.m_chanceToRandomizeSpawnDirectionPerGroup,
        m_overrideWaveSpawnType = data.m_overrideWaveSpawnType,
        m_survivalWaveSpawnType = data.m_survivalWaveSpawnType,
        m_populationPointsTotal = data.m_populationPointsTotal,
        m_populationPointsPerWaveStart = data.m_populationPointsPerWaveStart,
        m_populationPointsPerWaveEnd = data.m_populationPointsPerWaveEnd,
        m_populationPointsMinPerGroup = data.m_populationPointsMinPerGroup,
        m_populationPointsPerGroupStart = data.m_populationPointsPerGroupStart,
        m_populationPointsPerGroupEnd = data.m_populationPointsPerGroupEnd,
        m_populationRampOverTime = data.m_populationRampOverTime
    };
}
