using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using AssetShards;
using Enemies;
using Il2CppInterop.Runtime;
using System.Text.Json;
using BepInEx;
using GameData;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>F3 authoring command: read the game's loaded Enemy DataBlocks for comparison with the offline catalog.
/// Loaded blocks can be changed by installed mods, so this is an observation, not a claim of vanilla provenance.</summary>
internal static class EnemyAbilityInventory
{
    private sealed record AbilityRow(int Index, Dictionary<string, object> Value, string Status);
    private sealed record EnemyRow(string EnemyId, string Name, List<AbilityRow> Abilities);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private const int MaxBlocks = 4096, MaxAbilities = 4096, MaxPrefabs = 256, MaxEnemies = 256;
    private const int MaxComponents = 32, MaxFields = 128, MaxAbilityTypes = 64, MaxSlots = 32;
    private static long _captureSequence;
    private static string _previousGeneration = "";
    private static Dictionary<string, EnemyAbilityEvidence.CooldownSample> _previous = new();
    private sealed record FieldComponent(int InstanceId, string NativeType, int AbilityType, int AbilityIndex,
        List<EnemyAbilityEvidence.FieldObservation> Fields, string Status);
    private sealed record PrefabObservation(string AbilityPrefab, string Status, List<FieldComponent> Components, string? Error = null);
    private sealed record LiveObservation(string Enemy, uint EnemyId, int EnemyInstanceId,
        int AbilityType, int AbilityIndex, int? ComponentInstanceId, string? ComponentType,
        int? ComponentDeclaredAbilityType, int? ComponentDeclaredAbilityIndex, FieldComponent? ComponentObservation,
        EnemyAbilityEvidence.FieldObservation Cooldown, EnemyAbilityEvidence.FieldObservation LastTriggerTime,
        EnemyAbilityEvidence.CooldownSample Sample, EnemyAbilityEvidence.CooldownInterval Interval);
    private sealed class Coverage
    {
        public string Status { get; set; } = "observed";
        public int Discovered { get; set; }
        public int Inspected { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public List<string> Errors { get; } = new();
        public int OmittedErrors { get; private set; }
        internal void Missing(string error) { Status = "partial"; if (Errors.Count < 64) Errors.Add(error); else OmittedErrors++; }
    }

    internal static void ExportCurrent()
    {
        try
        {
            var blocks = GameDataBlockBase<EnemyDataBlock>.GetAllBlocks();
            if (blocks == null || blocks.Count == 0) throw new InvalidOperationException("Enemy DataBlocks are not loaded.");
            var enemies = new List<EnemyRow>();
            var ids = new HashSet<uint>();
            var abilityCount = 0;
            if (blocks.Count > MaxBlocks) throw new InvalidOperationException("Enemy DataBlock count exceeds the bounded inventory limit.");
            var definitionBudget = new CaptureWorkBudget(32768, 500);
            foreach (var block in blocks)
            {
                if (!definitionBudget.TryStep()) throw new InvalidOperationException("Definition inventory exceeded its step/time budget.");
                if (block == null || !ids.Add(block.persistentID))
                    throw new InvalidOperationException("Enemy DataBlocks contain a null or duplicate block.");
                var values = block.AI_Abilities ?? throw new InvalidOperationException("Enemy " + block.persistentID + " has no ability list.");
                var abilities = new List<AbilityRow>();
                if (values.Count > MaxSlots || abilityCount + values.Count > MaxAbilities)
                    throw new InvalidOperationException("Enemy ability count exceeds the bounded inventory limit.");
                for (var index = 0; index < values.Count; index++)
                {
                    if (!definitionBudget.TryStep()) throw new InvalidOperationException("Ability definition inventory exceeded its step/time budget.");
                    var ability = values[index] ?? throw new InvalidOperationException("Enemy " + block.persistentID + " has a null ability.");
                    if (string.IsNullOrEmpty(ability.AbilityPrefab) || !float.IsFinite(ability.Cooldown))
                        throw new InvalidOperationException("Enemy " + block.persistentID + " has incomplete ability data.");
                    var value = new Dictionary<string, object>
                    {
                        ["Enabled"] = ability.Enabled,
                        ["AbilityPrefab"] = ability.AbilityPrefab,
                        ["AbilityType"] = (int)ability.AbilityType,
                        ["Cooldown"] = ability.Cooldown,
                    };
                    if (ability.AbilityConditions is { } conditions && conditions.Count > 0)
                    {
                        var idsForAbility = new List<int>();
                        if (conditions.Count > MaxSlots) throw new InvalidOperationException("Ability condition count exceeds the bounded inventory limit.");
                        foreach (var condition in conditions) idsForAbility.Add((int)condition);
                        value["AbilityConditions"] = idsForAbility;
                    }
                    abilities.Add(new AbilityRow(index, value, "game-observed"));
                    abilityCount++;
                }
                enemies.Add(new EnemyRow(block.persistentID.ToString(CultureInfo.InvariantCulture), block.name ?? "", abilities));
            }
            enemies.Sort((left, right) => uint.Parse(left.EnemyId, CultureInfo.InvariantCulture).CompareTo(uint.Parse(right.EnemyId, CultureInfo.InvariantCulture)));
            var captureId = RuntimeDiagnostics.SessionId + "-" + (++_captureSequence).ToString("D6", CultureInfo.InvariantCulture);
            var prefabCoverage = new Coverage();
            var liveCoverage = new Coverage();
            var prefabs = ReadPrefabs(enemies, prefabCoverage);
            var live = ReadCooldowns(captureId, liveCoverage, out var nextSamples);
            var revision = CellBuildData.GetRevision();
            var document = new
            {
                Format = "forge-enemy-ability-reference",
                SchemaVersion = 1,
                GameRevision = revision,
                Source = new
                {
                    Kind = "loaded-game-datablocks",
                    Api = "GameDataBlockBase<EnemyDataBlock>.GetAllBlocks()",
                    GameBuild = Application.version,
                    SessionId = RuntimeDiagnostics.SessionId,
                    CaptureId = captureId,
                    GenerationId = RuntimeDiagnostics.GenerationId,
                    WorldEpoch = RuntimeDiagnostics.WorldEpoch,
                    CapturedAtUtc = DateTimeOffset.UtcNow
                },
                FieldUnits = new Dictionary<string, object>
                {
                    ["Enabled"] = new { unit = "boolean", status = "schema-verified" },
                    ["AbilityPrefab"] = new { unit = "asset-path", status = "schema-verified" },
                    ["AbilityType"] = new { unit = "AgentAbility", status = "schema-verified" },
                    ["Cooldown"] = new { unit = "s", status = "pending-game", note = "Seconds follow the current editor convention; the DataBlock schema only says Single." },
                    ["AbilityConditions"] = new { unit = "AbilityCondition[]", status = "schema-verified" },
                },
                Pending = new[] {
                    new { field = "abilityPrefab.componentValues", status = "pending-game", note = "Only observed numeric EnemyAbility fields are captured; missing assets, unsupported fields and truncated sections remain unverified. Loaded values may be modified." },
                    new { field = "Cooldown.unit", status = "pending-game", note = "Raw native timer values and elapsed sample intervals do not establish cooldown units or readiness. Correlate real ability/readiness trace calls; no gameplay method is invoked here." } },
                PrefabObservations = prefabs,
                LiveCooldownObservations = live,
                ObservationCoverage = new
                {
                    Prefabs = prefabCoverage,
                    LiveCooldowns = liveCoverage,
                    Limits = new
                    {
                        MaxPrefabs,
                        MaxEnemies,
                        MaxComponents,
                        MaxFields,
                        MaxAbilityTypes,
                        MaxSlots,
                        MaxNumericArrayElements = EnemyAbilityEvidence.MaxNumericArrayElements,
                        BudgetMillisecondsPerSection = 100
                    },
                    Note = "Cooperative budgets include enumeration; an individual native call cannot be interrupted. Components are EnemyAbility-derived only; nested object graphs are not traversed."
                },
                Enemies = enemies,
            };
            // The capture ID (session and sequence) is new each time, so the file never replaces an earlier capture.
            var file = Path.Combine(Paths.BepInExRootPath, "ForgeReports", "enemy-abilities", revision.ToString(CultureInfo.InvariantCulture), captureId + ".forge");
            ForgeCore.Store.WriteBytes(file, JsonSerializer.SerializeToUtf8Bytes(document, Json));
            _previous = nextSamples; // Only published files may become an interval's previous capture.
            Plugin.PluginLog.LogInfo("Enemy ability inventory: " + file + " (enemies=" + enemies.Count + "; abilities=" + abilityCount + "; prefab samples=" + prefabs.Count + "; cooldown samples=" + live.Count + "; units pending)");
        }
        catch (Exception error)
        {
            RuntimeDiagnostics.Note("enemy_ability_inventory_failed", error.GetType().Name + ": " + error.Message);
            Plugin.PluginLog.LogError("Enemy ability inventory failed: " + error);
        }
    }

    private static List<PrefabObservation> ReadPrefabs(List<EnemyRow> enemies, Coverage coverage)
    {
        var result = new List<PrefabObservation>();
        var paths = enemies.SelectMany(e => e.Abilities).Select(a => (string)a.Value["AbilityPrefab"])
            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        coverage.Discovered = paths.Length;
        var budget = new CaptureWorkBudget(20000, 100);
        for (var index = 0; index < paths.Length; index++)
        {
            if (index >= MaxPrefabs || !budget.TryStep()) { coverage.Missing("prefab-limit-or-time-budget"); break; }
            var path = paths[index];
            var components = new List<FieldComponent>();
            try
            {
                // false means already loaded only. This command must never load or instantiate an asset.
                var prefab = AssetShardManager.GetLoadedAsset<GameObject>(path, false);
                if (prefab == null) { result.Add(new(path, "not-loaded", components)); coverage.Missing(path + ": not-loaded"); coverage.Inspected++; continue; }
                var found = prefab.GetComponentsInChildren<EnemyAbility>(true);
                var partial = found.Length > MaxComponents;
                if (found.Length == 0) coverage.Missing(path + ": no-ability-components");
                for (var componentIndex = 0; componentIndex < found.Length && componentIndex < MaxComponents; componentIndex++)
                {
                    if (!budget.TryStep()) { partial = true; break; }
                    if (found[componentIndex] is not { } component) { partial = true; continue; }
                    var read = ReadComponent(component, budget);
                    components.Add(read);
                    partial |= read.Status != "observed";
                }
                result.Add(new(path, partial ? "partial" : found.Length == 0 ? "no-ability-components" : "observed", components));
                if (partial) coverage.Missing(path + ": component-field-limit-or-unavailable");
            }
            catch (Exception error) { result.Add(new(path, "read-error", components, Error(error))); coverage.Missing(path + ": " + Error(error)); }
            coverage.Inspected++;
        }
        coverage.ElapsedMilliseconds = budget.ElapsedMilliseconds;
        if (result.Count == 0) coverage.Missing("no-prefab-observations");
        return result;
    }

    private static FieldComponent ReadComponent(EnemyAbility component, CaptureWorkBudget budget)
    {
        var fields = new List<EnemyAbilityEvidence.FieldObservation>();
        var klass = component.ObjectClass;
        var nativeType = RecIl2CppProbe.Instance.NativeClassName(klass) ?? "unavailable";
        var status = "observed";
        var count = 0;
        for (var depth = 0; klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass), depth++)
        {
            if (depth >= 16 || !budget.TryStep()) { status = "partial"; break; }
            var declaringType = RecIl2CppProbe.Instance.NativeClassName(klass) ?? "unavailable";
            var projectedType = typeof(EnemyAbility).Assembly.GetType(declaringType, false);
            object? projection = null;
            try { projection = projectedType?.GetConstructor(new[] { typeof(IntPtr) })?.Invoke(new object[] { component.Pointer }); }
            catch (Exception error) { fields.Add(new(declaringType, "unknown", "read-error", null, Error(error))); status = "partial"; }
            var iterator = IntPtr.Zero;
            while (true)
            {
                if (count >= MaxFields || !budget.TryStep()) { status = "partial"; break; }
                var field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator);
                if (field == IntPtr.Zero) break;
                count++;
                if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
                var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)) ?? "unavailable";
                var observed = EnemyAbilityEvidence.ReadProjectedField(projection, declaringType, name);
                fields.Add(observed);
                if (observed.Status != "observed" && observed.Status != "unsupported-type") status = "partial";
            }
            if (count >= MaxFields || !budget.HasTime || declaringType == typeof(EnemyAbility).FullName) break;
        }
        if (fields.Count == 0) status = "fields-unavailable";
        return new(component.GetInstanceID(), nativeType, (int)component.m_abilityType, component.AbilityIndex, fields, status);
    }

    private static List<LiveObservation> ReadCooldowns(string captureId, Coverage coverage, out Dictionary<string, EnemyAbilityEvidence.CooldownSample> next)
    {
        var result = new List<LiveObservation>();
        next = new Dictionary<string, EnemyAbilityEvidence.CooldownSample>();
        var generation = RuntimeDiagnostics.GenerationId + "/" + RuntimeDiagnostics.WorldEpoch;
        if (_previousGeneration != generation) _previous.Clear();
        _previousGeneration = generation;
        var budget = new CaptureWorkBudget(20000, 100);
        try
        {
            var enemies = UnityEngine.Object.FindObjectsOfType<EnemyAgent>();
            coverage.Discovered = enemies.Length;
            for (var enemyIndex = 0; enemyIndex < enemies.Length; enemyIndex++)
            {
                if (enemyIndex >= MaxEnemies || !budget.TryStep()) { coverage.Missing("enemy-limit-or-time-budget"); break; }
                var enemy = enemies[enemyIndex];
                if (enemy == null) { coverage.Missing("null-enemy-at/" + enemyIndex); continue; }
                try
                {
                    string? entityLife = null;
                    try
                    {
                        if (NativeLife.Of(enemy) is { } life) entityLife = "enemy:" + enemy.GlobalID.ToString(CultureInfo.InvariantCulture)
                            + "@" + life.WorldEpoch.ToString(CultureInfo.InvariantCulture) + "." + life.Instance.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception error) { coverage.Missing("enemy/" + enemy.GlobalID + ": entity-life-read: " + Error(error)); }
                    if (entityLife == null) coverage.Missing("enemy/" + enemy.GlobalID + ": entity-life-unproven; no elapsed comparison");
                    var abilities = enemy.Abilities;
                    if (abilities == null) { coverage.Missing("enemy/" + enemy.GlobalID + ": abilities-unavailable"); continue; }
                    var cooldowns = abilities.m_cooldowns;
                    var triggers = abilities.m_lastTriggerTimes;
                    var components = abilities.AbilityComps;
                    if (cooldowns == null || triggers == null || components == null)
                    { coverage.Missing("enemy/" + enemy.GlobalID + ": cooldown/trigger/component-table-unavailable"); continue; }
                    var typeCount = Math.Max(cooldowns.Length, Math.Max(triggers.Length, components.Length));
                    if (typeCount > MaxAbilityTypes) coverage.Missing("enemy/" + enemy.GlobalID + ": ability-type-limit");
                    for (var typeIndex = 0; typeIndex < typeCount && typeIndex < MaxAbilityTypes; typeIndex++)
                    {
                        if (!budget.TryStep()) { coverage.Missing("live-time-budget"); break; }
                        var cooldown = typeIndex < cooldowns.Length ? cooldowns[typeIndex] : null;
                        var trigger = typeIndex < triggers.Length ? triggers[typeIndex] : null;
                        var comps = typeIndex < components.Length ? components[typeIndex]?.Comps : null;
                        var slotCount = Math.Max(cooldown?.Count ?? 0, Math.Max(trigger?.Count ?? 0, comps?.Length ?? 0));
                        if (slotCount > MaxSlots) coverage.Missing("enemy/" + enemy.GlobalID + "/" + typeIndex + ": slot-limit");
                        for (var slot = 0; slot < slotCount && slot < MaxSlots; slot++)
                        {
                            if (result.Count >= MaxAbilities || !budget.TryStep()) { coverage.Missing("live-row-limit-or-time-budget"); break; }
                            EnemyAbilityEvidence.FieldObservation Read(string field, Il2CppSystem.Collections.Generic.List<float>? values)
                            {
                                try
                                {
                                    return values != null && slot < values.Count
                                    ? EnemyAbilityEvidence.Project(field, "System.Single", values[slot])
                                    : new(field, "System.Single", "field-unavailable", null, "Native table slot is absent.");
                                }
                                catch (Exception error) { return new(field, "System.Single", "read-error", null, Error(error)); }
                            }
                            var cooldownValue = Read("m_cooldowns[" + typeIndex + "][" + slot + "]", cooldown);
                            var triggerValue = Read("m_lastTriggerTimes[" + typeIndex + "][" + slot + "]", trigger);
                            var component = comps != null && slot < comps.Length ? comps[slot] : null;
                            var sample = new EnemyAbilityEvidence.CooldownSample(captureId, Time.time, Time.realtimeSinceStartup,
                                RuntimeDiagnostics.SimulationTick, triggerValue.Value is float last ? last : null, entityLife);
                            var key = entityLife + "/" + typeIndex + "/" + slot
                                + "/" + (component?.Pointer.ToString() ?? "unavailable");
                            FieldComponent? componentObservation = null;
                            if (component != null)
                            {
                                try { componentObservation = ReadComponent(component, budget); }
                                catch (Exception error) { coverage.Missing("enemy/" + enemy.GlobalID + "/" + typeIndex + "/" + slot + ": component-read: " + Error(error)); }
                            }
                            EnemyAbilityEvidence.CooldownSample? previous = null;
                            if (entityLife != null && component != null) _previous.TryGetValue(key, out previous);
                            result.Add(new("enemy/" + enemy.GlobalID, enemy.EnemyDataID, enemy.GetInstanceID(), typeIndex, slot,
                                component == null ? null : component.GetInstanceID(), component == null ? null : RecIl2CppProbe.Instance.NativeClassName(component.ObjectClass),
                                component == null ? null : (int)component.m_abilityType, component == null ? null : component.AbilityIndex, componentObservation,
                                cooldownValue, triggerValue, sample, EnemyAbilityEvidence.Interval(previous, sample)));
                            if (entityLife != null && component != null) next[key] = sample;
                            if (componentObservation?.Status != "observed" || cooldownValue.Status != "observed" || triggerValue.Status != "observed")
                                coverage.Missing("enemy/" + enemy.GlobalID + "/" + typeIndex + "/" + slot + ": partial-slot");
                        }
                    }
                    coverage.Inspected++;
                }
                catch (Exception error) { coverage.Missing("enemy-index/" + enemyIndex + ": " + Error(error)); }
            }
        }
        catch (Exception error) { coverage.Missing("live-enumeration: " + Error(error)); }
        coverage.ElapsedMilliseconds = budget.ElapsedMilliseconds;
        if (result.Count == 0) coverage.Missing("no-live-cooldown-samples");
        return result;
    }
    private static string Error(Exception error) => EnemyAbilityEvidence.Error(error);
}

// The projection is pure managed code so offline tests exercise the same value/error and interval rules.
// Only names obtained from native FIELD metadata reach ReadProjectedField; arbitrary getters are never enumerated.
internal static class EnemyAbilityEvidence
{
    internal const int MaxNumericArrayElements = 64;
    internal sealed record FieldObservation(string Field, string ValueType, string Status, object? Value, string? Error = null);
    internal sealed record CooldownSample(string CaptureId, float UnityTime, float RealtimeSinceStartup, long? SimulationTick, float? LastTriggerTimeRaw, string? EntityLife);
    internal sealed record CooldownInterval(string Status, string? PreviousCaptureId, double? ElapsedScaledSeconds,
        double? ElapsedRealtimeSeconds, long? ElapsedSimulationTicks, float? PreviousLastTriggerTimeRaw,
        double? LastTriggerDeltaRaw, string UnitStatus = "pending-game");

    internal static FieldObservation ReadProjectedField(object? projection, string declaringType, string name)
    {
        var field = declaringType + "." + name;
        if (projection == null) return new(field, "unknown", "projection-unavailable", null);
        try
        {
            var property = projection.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetGetMethod(true) == null || property.GetIndexParameters().Length != 0)
                return new(field, "unknown", "field-unavailable", null);
            return Project(field, property.PropertyType.FullName ?? property.PropertyType.Name, property.GetValue(projection));
        }
        catch (Exception error) { return new(field, "unknown", "read-error", null, Error(error)); }
    }
    internal static FieldObservation Project(string field, string valueType, object? value)
    {
        if (value == null) return new(field, valueType, "null-value", null);
        if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
            return new(field, valueType, "non-finite", null);
        var type = value.GetType();
        if (type.IsEnum) return new(field, valueType, "observed", Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return new(field, valueType, "observed", value);
        // Generated vectors expose plain managed scalar fields. No object graph or behavior getter is followed.
        var dimensions = valueType switch
        {
            "UnityEngine.Vector2" => "xy",
            "UnityEngine.Vector3" => "xyz",
            "UnityEngine.Vector4" or "UnityEngine.Quaternion" => "xyzw",
            "UnityEngine.Color" => "rgba",
            _ => ""
        };
        if (dimensions.Length != 0)
        {
            var values = new Dictionary<string, object>();
            foreach (var axis in dimensions)
            {
                var number = type.GetField(axis.ToString())?.GetValue(value);
                if (number is not float numberValue || !float.IsFinite(numberValue)) return new(field, valueType, "field-unavailable", null, "Vector member unavailable/non-finite.");
                values[axis.ToString()] = numberValue;
            }
            return new(field, valueType, "observed", values);
        }
        // Numeric arrays are intentionally narrow; arbitrary IEnumerable values may enumerate game objects.
        if (value is float[] numbers)
        {
            if (numbers.Length > MaxNumericArrayElements) return new(field, valueType, "element-limit", null);
            if (numbers.Any(number => !float.IsFinite(number))) return new(field, valueType, "non-finite", null);
            return new(field, valueType, "observed", numbers.ToArray());
        }
        if (valueType.StartsWith("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1[[System.Single,", StringComparison.Ordinal))
        {
            var count = type.GetProperty("Length")?.GetValue(value);
            var indexer = type.GetProperty("Item");
            if (count is not int length || indexer == null) return new(field, valueType, "field-unavailable", null);
            if (length < 0 || length > MaxNumericArrayElements) return new(field, valueType, "element-limit", null);
            var array = new float[length];
            for (var index = 0; index < length; index++)
            {
                var element = indexer.GetValue(value, new object[] { index });
                if (element is not float item || !float.IsFinite(item)) return new(field, valueType, "non-finite", null);
                array[index] = item;
            }
            return new(field, valueType, "observed", array);
        }
        return new(field, valueType, "unsupported-type", null);
    }
    internal static CooldownInterval Interval(CooldownSample? previous, CooldownSample current)
    {
        if (string.IsNullOrEmpty(current.EntityLife)) return new("identity-unproven", null, null, null, null, null, null);
        if (previous == null) return new("no-previous-sample", null, null, null, null, null, null);
        if (previous.EntityLife != current.EntityLife) return new("different-life", previous.CaptureId, null, null, null, null, null);
        if (!float.IsFinite(current.UnityTime) || !float.IsFinite(previous.UnityTime)
            || !float.IsFinite(current.RealtimeSinceStartup) || !float.IsFinite(previous.RealtimeSinceStartup)
            || current.UnityTime < previous.UnityTime || current.RealtimeSinceStartup < previous.RealtimeSinceStartup
            || current.SimulationTick < previous.SimulationTick)
            return new("clock-discontinuity", previous.CaptureId, null, null, null, previous.LastTriggerTimeRaw, null);
        return new("sample-interval-observed", previous.CaptureId, (double)current.UnityTime - previous.UnityTime,
            (double)current.RealtimeSinceStartup - previous.RealtimeSinceStartup, current.SimulationTick - previous.SimulationTick,
            previous.LastTriggerTimeRaw, current.LastTriggerTimeRaw.HasValue && previous.LastTriggerTimeRaw.HasValue
                ? (double)current.LastTriggerTimeRaw.Value - previous.LastTriggerTimeRaw.Value : null);
    }
    internal static string Error(Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        var text = error.GetType().Name + ": " + error.Message;
        return text.Length <= 240 ? text : text[..240];
    }
}
