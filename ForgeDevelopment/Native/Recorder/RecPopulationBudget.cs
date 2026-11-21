using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Agents;
using Enemies;
using GameData;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Signature-specific, observation-only recorder. Never invokes selection, generation, spawn or cap checks.
internal static class RecPopulationBudget
{
    internal const string Channel = "population_budget";
    internal const int MaxRows = 128, MaxIdentities = 4096;
    private static readonly PopulationBudgetCalls Calls = new((kind, body) => RecSession.Write(Channel, kind, body));
    private static readonly Dictionary<string, long> Lives = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Placements = new(StringComparer.Ordinal);
    private static PopulationHookLifecycle? _lifecycle;
    private static int _requested;
    internal static bool HasHooks { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return _lifecycle?.HasHooks == true; } }
    internal static bool CleanupPending { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return _lifecycle?.CleanupPending == true; } }
    internal static int Requested { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return _requested; } }
    private static readonly object Gate = new();
    private const string Consumer = "NAinfini.ForgeDevelopment.PopulationBudget";
    private static bool _sessionActive;
    private static bool _hooksComplete;
    private static volatile bool _observing;
    internal static bool Enabled => _observing && Settings.RecorderPopulationBudget.Value;
    private static long _life, _identityEpoch;
    private static string Context => RecSession.SessionId + ":" + RecTime.Level;
    private static string Id(Il2CppObjectBase? value) => value == null || value.Pointer == IntPtr.Zero ? ""
        : value.GetType().FullName + ":" + value.Pointer.ToInt64().ToString("x", CultureInfo.InvariantCulture);
    private static void Sync()
    {
        Calls.EnsureContext(Context, RecTime.WorldEpoch);
        if (_identityEpoch == Calls.Epoch) return;
        Lives.Clear(); Placements.Clear(); _identityEpoch = Calls.Epoch;
    }
    internal static void Reset(string reason)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (!_sessionActive) return;
            Calls.Reset(Context, RecTime.WorldEpoch, reason, true);
            Lives.Clear(); Placements.Clear(); _identityEpoch = Calls.Epoch;
        }
    }
    internal static void Start()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) StartLocked();
    }
    private static void StartLocked()
    {
        if (_lifecycle?.CleanupPending == true || _lifecycle?.StartFailed == true)
            throw new InvalidOperationException("Population budget startup failed or cleanup is pending; native installation will not be retried.");
        if (_sessionActive) return;
        var leases = new Dictionary<MethodBase, IDisposable>();
        _sessionActive = true;
        _hooksComplete = false;
        // Runtime owns physical installation and rollback; retain every acquisition attempt for cleanup.
        var lifecycle = new PopulationHookLifecycle(
            method =>
            {
                if (leases.TryGetValue(method, out var lease)) { lease.Dispose(); leases.Remove(method); return; }
                var pending = DevelopmentNativeHooks.Shared.Inspect().FirstOrDefault(status => status.Target.Equals(method));
                if (pending is { CleanupPending: true, Consumers: 0 } && !DevelopmentNativeHooks.Shared.RetryCleanup(method))
                    throw new InvalidOperationException("Population native cleanup remains pending.", pending.Fault);
            });
        _lifecycle = lifecycle;
        _requested = 0;
        try
        {
            Reset("start");
            lifecycle.Start(() =>
            {
                void Hook(Type type, string name, Type[] arguments, string prefix, string postfix)
                {
                    _requested++;
                    var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, arguments, null);
                    var reason = method == null ? "signature-unavailable" : CaptureHarmonyGroup.RejectionReason(method);
                    try
                    {
                        if (reason == null)
                        {
                            var callbacks = new[]
                            {
                                new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Prefix, DevelopmentHookCallbacks.ResolveCallback(typeof(RecPopulationBudget), prefix)),
                                new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Postfix, DevelopmentHookCallbacks.ResolveCallback(typeof(RecPopulationBudget), postfix)),
                                new DevelopmentHookCallbacks.Callback(DevelopmentHookCallbacks.Phase.Finalizer, DevelopmentHookCallbacks.ResolveCallback(typeof(RecPopulationBudget), nameof(Finalizer))),
                            };
                            lifecycle.Install(method!, () => leases.Add(method!, DevelopmentNativeHooks.Shared.Subscribe(method!, Consumer,
                                callbacks, DevelopmentNativeHooks.Order.Shared, DevelopmentNativeHooks.Lane.FinalizerReturned, DevelopmentTypedHooks.For(method!))));
                        }
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException("Population budget patch failed: " + method, error);
                    }
                    RecSession.Write(Channel, "hook", json =>
                    {
                        json.WriteString("schema", PopulationBudgetCalls.Schema); json.WriteString("type", type.FullName);
                        json.WriteString("method", name); json.WriteString("signature", method?.ToString() ?? string.Join(",", arguments.Select(value => value.FullName)));
                        json.WriteString("assembly", type.Assembly.FullName); json.WriteString("moduleVersionId", type.Module.ModuleVersionId.ToString());
                        json.WriteString("status", reason == null ? "installed" : "unavailable"); json.WriteString("reason", reason);
                        json.WriteBoolean("nativeAdmissionChecked", method != null);
                        json.WriteString("observationPoint", "late-finalizer-after-all-postfixes");
                        json.WritePropertyName("consumerManifest");
                        JsonSerializer.Serialize(json, method == null ? null : CaptureHookProvenance.Describe(DevelopmentNativeHooks.Shared.TryGetManifest(method)));
                        var patches = method == null ? null : Harmony.GetPatchInfo(method);
                        json.WriteStartObject("patchOrderAtInstall");
                        void Owners(string phase, IEnumerable<Patch>? rows)
                        {
                            json.WriteStartArray(phase);
                            if (rows != null) foreach (var patch in rows)
                            {
                                json.WriteStartObject(); json.WriteString("owner", patch.owner); json.WriteNumber("priority", patch.priority);
                                json.WritePropertyName("before"); JsonSerializer.Serialize(json, patch.before);
                                json.WritePropertyName("after"); JsonSerializer.Serialize(json, patch.after); json.WriteEndObject();
                            }
                            json.WriteEndArray();
                        }
                        Owners("prefix", patches?.Prefixes); Owners("postfix", patches?.Postfixes); Owners("finalizer", patches?.Finalizers);
                        json.WriteEndObject();
                    });
                }
                Hook(typeof(LG_PopulateZone), "Build", Type.EmptyTypes, nameof(ZoneBefore), nameof(ZoneAfter));
                Hook(typeof(LG_PopulateZone), "TryPlaceGroup", new[] { typeof(EnemyGroupRandomizer), typeof(float).MakeByRefType(), typeof(bool) }, nameof(PlaceBefore), nameof(PlaceAfter));
                Hook(typeof(LG_PopulateArea), "Build", Type.EmptyTypes, nameof(AreaBefore), nameof(AreaAfter));
                Hook(typeof(EnemyGroup), "OnSpawn", new[] { typeof(pEnemyGroupSpawnData) }, nameof(GroupBefore), nameof(GroupAfter));
                Hook(typeof(EnemyGroup), "OnDespawn", Type.EmptyTypes, nameof(GroupBefore), nameof(GroupAfter));
                Hook(typeof(EnemyGroup), "TryMapGroupDataAndMaxPopScore", new[] { typeof(uint) }, nameof(MapBefore), nameof(MapAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "AddEnemyData", new[] { typeof(AgentMode), typeof(eEnemyRole), typeof(eEnemyRoleDifficulty), typeof(float), typeof(bool), typeof(int).MakeByRefType() }, nameof(AddBefore), nameof(AddAfter));
                Hook(typeof(EnemyPopulationDataBlock), "TryGetEnemyData", new[] { typeof(eEnemyRole), typeof(eEnemyRoleDifficulty), typeof(EnemyDataBlock).MakeByRefType(), typeof(float).MakeByRefType() }, nameof(SelectBefore), nameof(SelectAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "AddForcedEnemyDataDirect", new[] { typeof(uint), typeof(AgentMode) }, nameof(ForcedBefore), nameof(ScoreAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "AddEnemyDataDirect", new[] { typeof(uint), typeof(AgentMode), typeof(int) }, nameof(DirectBefore), nameof(ScoreAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "SpawnUpdate", Type.EmptyTypes, nameof(ScoreBefore), nameof(SpawnAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "SpawnMember", new[] { typeof(uint), typeof(AgentMode), typeof(int) }, nameof(MemberBefore), nameof(ScoreAfter));
                Hook(typeof(EnemyGroupScoreSpawnData), "SpawnMember", new[] { typeof(uint), typeof(AgentMode), typeof(Vector3), typeof(Quaternion) }, nameof(MemberPositionBefore), nameof(ScoreAfter));
                _hooksComplete = lifecycle.InstalledCount == _requested;
                RecSession.Write(Channel, "installation", json =>
                {
                    json.WriteString("schema", PopulationBudgetCalls.Schema); json.WriteNumber("requested", _requested);
                    json.WriteNumber("installed", lifecycle.InstalledCount); json.WriteBoolean("hooksComplete", _hooksComplete);
                    json.WriteString("status", _hooksComplete ? "installed" : "partial");
                    json.WriteBoolean("gameplayVerified", false);
                });
            });
            _observing = true;
        }
        catch
        {
            _observing = false;
            _hooksComplete = false;
            // Failed cleanup retains the exact backend instance for subsequent Stop.
            if (!lifecycle.HasHooks) _sessionActive = false;
            throw;
        }
    }
    internal static void Stop()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) StopLocked();
    }
    private static void StopLocked()
    {
        _observing = false; // Logical off is immediate even if native cleanup fails.
        var errors = new List<Exception>();
        try { Calls.Reset(Context, RecTime.WorldEpoch, "stop", false); }
        catch (Exception error) { errors.Add(error); }
        Lives.Clear(); Placements.Clear(); _hooksComplete = false;
        try { _lifecycle?.Stop(); }
        catch (Exception error) { errors.Add(error); }
        // Never discard a failed native uninstall or release its bridge early.
        if (_lifecycle?.HasHooks != true) _sessionActive = false;
        if (errors.Count != 0) throw new AggregateException("Population budget stop failed.", errors);
    }
    private static void Observe(Action action)
    {
        if (!Enabled || !_sessionActive || !RecSession.Active || !RecObservation.TryEnter()) return;
        try { using (RecGate.Enter(Gate, "recorder.lock-timeout")) { if (!Enabled || !_sessionActive || !RecSession.Active) return; Sync(); action(); } }
        catch (Exception) { Calls.Loss("observer-read-failed"); } // The failure is counted as the named loss "observer-read-failed".
        finally { RecObservation.Exit(); }
    }
    private static PopulationBudgetCalls.Frame? Begin(MethodBase method, Il2CppObjectBase instance,
        Func<PopulationBudgetFields> readFields, EnemyGroup? group = null, EnemyPopulationDataBlock? population = null, bool selection = false)
    {
        var groupId = Id(group);
        var life = groupId.Length == 0 ? 0 : Lives.GetValueOrDefault(groupId);
        return Calls.Begin(method.DeclaringType!.FullName + "." + method.Name, Id(instance), groupId, life, Id(population), () =>
        {
            var fields = readFields();
            if (!_hooksComplete) fields.Missing("hooks", "partial-installation");
            QualifyIdentity(fields);
            if (group != null && life == 0) fields.Missing("groupLife", "spawn-not-observed");
            fields.Read("signature", () => method.ToString());
            return fields;
        }, selection);
    }
    private static void QualifyIdentity(PopulationBudgetFields fields)
    {
        if (string.IsNullOrWhiteSpace(RecSession.SessionId)) fields.Missing("session", "unavailable");
        if (string.IsNullOrWhiteSpace(RecTime.Level) || RecTime.Level == "unavailable") fields.Missing("level", "unavailable");
        if (RecTime.WorldEpoch < 0) fields.Missing("worldEpoch", "unavailable");
    }
    private static void Returned(PopulationBudgetCalls.Frame? frame, Func<PopulationBudgetFields> read, object? result, bool ranOriginal)
    {
        if (frame == null || frame.Epoch != Calls.Epoch) return;
        Calls.Returned(frame, frame.Admitted && ranOriginal ? read() : new PopulationBudgetFields(), result, ranOriginal);
    }
    private static void Finalizer(Exception? __exception, PopulationBudgetCalls.Frame? __state)
    {
        Observe(() =>
        {
            Calls.Finish(__state, __exception);
            if (__state != null && __state.Epoch == Calls.Epoch && Lives.GetValueOrDefault(__state.Instance) == __state.GroupLife
                && (__state.Method == "Enemies.EnemyGroup.OnDespawn"
                || __state.Method == "Enemies.EnemyGroup.OnSpawn" && (__exception != null || !__state.RanOriginal))) Lives.Remove(__state.Instance);
        });
    }
    private static PopulationBudgetFields ZoneFields(LG_PopulateZone instance)
    {
        var fields = new PopulationBudgetFields();
        fields.Read("zone", () => Id(instance.m_zone));
        fields.Read("zoneAlias", () => instance.m_zone.Alias);
        fields.Read("dimension", () => (int)instance.m_zone.DimensionIndex);
        fields.Read("scoringPopScore", () => instance.m_scoringPopScore);
        ReadList(fields, "groupRandomizerPopScores", () => instance.m_groupRandomizerPopScores,
            value => new { randomizer = Id(value.randomizer), totalPopScore = Finite(value.totalPopScore) });
        ReadList(fields, "placedGroups", () => instance.m_placedGroups, value => Placement(value));
        return fields;
    }
    private static object Placement(EnemyGroupPlacement value) => new
    {
        placement = Id(value),
        area = Id(value.area),
        groupDefinition = value.groupData?.persistentID,
        populationPoints = Finite(value.populationPoints),
        observedPlacementCallId = Placements.GetValueOrDefault(Id(value)),
        placementLinkStatus = "same-native-reference-lifetime-unverified"
    };
    private static float Finite(float value) => float.IsFinite(value) ? value : throw new InvalidOperationException("Non-finite native value");
    private static void ReadList<T>(PopulationBudgetFields fields, string name,
        Func<Il2CppSystem.Collections.Generic.List<T>?> get, Func<T, object?> item)
    {
        try
        {
            var list = get(); if (list == null) { fields.Missing(name, "unavailable"); return; }
            var count = list.Count;
            var rows = new List<object?>();
            for (var i = 0; i < Math.Min(count, MaxRows); i++) rows.Add(item(list[i]));
            fields.Values[name] = rows; fields.Values[name + "Count"] = count;
            fields.Reads[name] = count > MaxRows ? "row-cap" : "read";
            if (list.Count != count) fields.Missing(name, "changed-during-read");
        }
        catch (Exception error) { fields.Missing(name, "read-error:" + error.GetType().Name); }
    }
    private static PopulationBudgetFields GroupFields(EnemyGroup group)
    {
        var fields = new PopulationBudgetFields();
        fields.Read("groupPopulationScore", () => group.Data.populationScore);
        fields.Read("groupDefinition", () => group.m_groupDataBlock?.persistentID);
        fields.Read("groupMaxScore", () => group.m_groupDataBlock == null ? null : group.m_groupDataBlock.MaxScore);
        return fields;
    }
    private static PopulationBudgetFields ScoreFields(EnemyGroupScoreSpawnData data)
    {
        var fields = data.m_group == null ? new PopulationBudgetFields() : GroupFields(data.m_group);
        fields.Read("group", () => Nonempty(Id(data.m_group))); fields.Read("population", () => Nonempty(Id(data.m_popData)));
        ReadList(fields, "selectedEnemyIds", () => data.m_enemyData, value => value);
        ReadList(fields, "selectedEnemyModes", () => data.m_enemyDataMode, value => (int)value);
        ReadList(fields, "selectedEnemyAmounts", () => data.m_enemyAmount, value => value);
        ReadList(fields, "forcedEnemyIds", () => data.m_enemyData_Forced, value => value);
        ReadList(fields, "forcedEnemyModes", () => data.m_enemyDataMode_Forced, value => (int)value);
        ReadList(fields, "spawnedEnemyIds", () => data.m_spawnedEnemyData, value => value);
        ReadList(fields, "spawnedEnemyModes", () => data.m_spawnedEnemyDataMode, value => (int)value);
        ReadList(fields, "spawnedEnemyAmounts", () => data.m_spawnedEnemyAmount, value => value);
        ReadList(fields, "spawnedForcedEnemyIds", () => data.m_spawnedEnemyData_Forced, value => value);
        ReadList(fields, "spawnedForcedEnemyModes", () => data.m_spawnedEnemyDataMode_Forced, value => (int)value);
        fields.Read("placementIndex", () => data.m_placementIndex); fields.Read("dataIndex", () => data.m_dataIndex);
        fields.Read("originalPlacementCount", () => data.m_orgPlacementCount);
        ReadList(fields, "freePlacements", () => data.m_freePlacements, value => value);
        Align(fields, "selectedEnemyIds", "selectedEnemyModes", "selectedEnemyAmounts");
        Align(fields, "forcedEnemyIds", "forcedEnemyModes");
        Align(fields, "spawnedEnemyIds", "spawnedEnemyModes", "spawnedEnemyAmounts");
        Align(fields, "spawnedForcedEnemyIds", "spawnedForcedEnemyModes");
        return fields;
    }
    private static void Align(PopulationBudgetFields fields, params string[] names)
    {
        var counts = names.Select(name => fields.Values.GetValueOrDefault(name + "Count")).ToArray();
        if (counts.Any(value => value == null) || counts.Distinct().Count() != 1)
            fields.Missing(string.Join("+", names), "cardinality-unavailable-or-mismatch");
    }
    private static string? Nonempty(string value) => value.Length == 0 ? null : value;
    private static void ZoneBefore(LG_PopulateZone __instance, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () => ZoneFields(__instance))); __state = state;
    }
    private static void ZoneAfter(LG_PopulateZone __instance, PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
        => Observe(() => Returned(__state, () => ZoneFields(__instance), __result, __runOriginal));
    private static void PlaceBefore(LG_PopulateZone __instance, EnemyGroupRandomizer __0, float __1, bool __2,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        var remaining = __1; PopulationBudgetCalls.Frame? state = null;
        Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = ZoneFields(__instance); fields.Read("remainingPopulation", () => remaining);
            fields.Read("forced", () => __2); fields.Read("randomizer", () => Nonempty(Id(__0)));
            var list = __instance.m_placedGroups;
            if (list != null) for (var i = 0; i < Math.Min(list.Count, MaxRows); i++) RememberPlacement(Id(list[i]), 0);
            return fields;
        })); __state = state;
    }
    private static void RememberPlacement(string id, long call)
    {
        if (id.Length == 0 || Placements.ContainsKey(id)) return;
        if (Placements.Count >= MaxIdentities) { Calls.Loss("placement-identity-cap"); return; }
        Placements[id] = call;
    }
    private static void PlaceAfter(LG_PopulateZone __instance, float __1, PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
    {
        var remaining = __1; Observe(() => Returned(__state, () =>
        {
            var list = __instance.m_placedGroups;
            if (__state != null && __state.BeforeComplete && list != null && list.Count <= MaxRows)
                for (var i = 0; i < list.Count; i++) RememberPlacement(Id(list[i]), __state.CallId);
            var fields = ZoneFields(__instance); fields.Read("remainingPopulation", () => remaining);
            return fields;
        }, __result, __runOriginal));
    }
    private static PopulationBudgetFields AreaFields(LG_PopulateArea area)
    {
        var fields = new PopulationBudgetFields(); fields.Read("placement", () => area.m_groupPlacement == null ? null : Placement(area.m_groupPlacement));
        fields.Missing("placementLifetime", "not-observed"); return fields;
    }
    private static void AreaBefore(LG_PopulateArea __instance, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () => AreaFields(__instance))); __state = state;
    }
    private static void AreaAfter(LG_PopulateArea __instance, PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
        => Observe(() => Returned(__state, () => AreaFields(__instance), __result, __runOriginal));
    private static void GroupBefore(EnemyGroup __instance, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() =>
        {
            if (__originalMethod.Name == "OnSpawn")
            {
                var id = Id(__instance);
                if (Lives.Count < MaxIdentities || Lives.ContainsKey(id)) Lives[id] = ++_life;
                else Calls.Loss("group-identity-cap");
            }
            state = Begin(__originalMethod, __instance, () => GroupFields(__instance), __instance);
        }); __state = state;
    }
    private static void GroupAfter(EnemyGroup __instance, PopulationBudgetCalls.Frame? __state, bool __runOriginal)
        => Observe(() => Returned(__state, () => GroupFields(__instance), null, __runOriginal));
    private static void MapBefore(EnemyGroup __instance, uint __0, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = GroupFields(__instance); fields.Read("requestedGroupDefinition", () => __0); return fields;
        }, __instance)); __state = state;
    }
    private static void MapAfter(EnemyGroup __instance, PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
        => Observe(() => Returned(__state, () => GroupFields(__instance), __result, __runOriginal));
    private static void AddBefore(EnemyGroupScoreSpawnData __instance, AgentMode __0, eEnemyRole __1, eEnemyRoleDifficulty __2,
        float __3, bool __4, int __5, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        var count = __5; PopulationBudgetCalls.Frame? state = null;
        Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = ScoreFields(__instance); fields.Read("mode", () => (int)__0);
            fields.Read("role", () => (int)__1); fields.Read("difficulty", () => (int)__2);
            fields.Read("populationScoreArgument", () => __3); fields.Read("forced", () => __4); fields.Read("placementCount", () => count);
            return fields;
        }, __instance.m_group, __instance.m_popData)); __state = state;
    }
    private static void AddAfter(EnemyGroupScoreSpawnData __instance, int __5, PopulationBudgetCalls.Frame? __state, bool __runOriginal)
    {
        var count = __5; Observe(() => Returned(__state, () =>
        { var fields = ScoreFields(__instance); fields.Read("placementCount", () => count); return fields; }, null, __runOriginal));
    }
    private static void SelectBefore(EnemyPopulationDataBlock __instance, eEnemyRole __0, eEnemyRoleDifficulty __1,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = new PopulationBudgetFields(); fields.Read("role", () => (int)__0); fields.Read("difficulty", () => (int)__1);
            fields.Read("populationDefinition", () => __instance.persistentID); return fields;
        }, population: __instance, selection: true)); __state = state;
    }
    private static void SelectAfter(EnemyPopulationDataBlock __instance, EnemyDataBlock __2, float __3,
        PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
    {
        // Out storage is never read in the prefix. False selections keep their actual outputs but are not expenditure.
        var enemy = __2; var cost = __3; Observe(() => Returned(__state, () =>
        {
            var fields = new PopulationBudgetFields(); fields.Read("returnedPopulationCost", () => cost);
            fields.Read("selectedEnemy", () => enemy == null ? null : enemy.persistentID);
            if (enemy == null) fields.Reads["selectedEnemy"] = __result ? "unexpected-null" : "explicit-null";
            if (enemy == null && !__result) fields.Values["selectedEnemy"] = null;
            fields.Read("selectionSucceeded", () => __result);
            if (__result && enemy != null) fields.Read("selectedPopulationCost", () => cost);
            else fields.Missing("selectedPopulationCost", "selection-failed-no-selected-expenditure");
            return fields;
        }, __result, __runOriginal));
    }
    private static void ScoreBefore(EnemyGroupScoreSpawnData __instance, MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance,
            () => ScoreFields(__instance), __instance.m_group, __instance.m_popData)); __state = state;
    }
    private static void ForcedBefore(EnemyGroupScoreSpawnData __instance, uint __0, AgentMode __1,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
        => Direct(__instance, __originalMethod, __0, __1, null, true, out __state);
    private static void DirectBefore(EnemyGroupScoreSpawnData __instance, uint __0, AgentMode __1, int __2,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
        => Direct(__instance, __originalMethod, __0, __1, __2, false, out __state);
    private static void Direct(EnemyGroupScoreSpawnData data, MethodBase method, uint enemy, AgentMode mode, int? amount, bool forced, out PopulationBudgetCalls.Frame? state)
    {
        PopulationBudgetCalls.Frame? frame = null; Observe(() => frame = Begin(method, data, () =>
        {
            var fields = ScoreFields(data); fields.Read("enemyIdArgument", () => enemy); fields.Read("modeArgument", () => (int)mode);
            fields.Read("forced", () => forced); if (amount.HasValue) fields.Read("amountArgument", () => amount.Value);
            fields.Missing("selectedPopulationCost", "not-provided-by-direct-api"); return fields;
        }, data.m_group, data.m_popData)); state = frame;
    }
    private static void MemberBefore(EnemyGroupScoreSpawnData __instance, uint __0, AgentMode __1, int __2,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = ScoreFields(__instance); fields.Read("enemyIdArgument", () => __0);
            fields.Read("modeArgument", () => (int)__1); fields.Read("placementIndexArgument", () => __2);
            return fields;
        }, __instance.m_group, __instance.m_popData)); __state = state;
    }
    private static void MemberPositionBefore(EnemyGroupScoreSpawnData __instance, uint __0, AgentMode __1, Vector3 __2, Quaternion __3,
        MethodBase __originalMethod, out PopulationBudgetCalls.Frame? __state)
    {
        PopulationBudgetCalls.Frame? state = null; Observe(() => state = Begin(__originalMethod, __instance, () =>
        {
            var fields = ScoreFields(__instance); fields.Read("enemyIdArgument", () => __0); fields.Read("modeArgument", () => (int)__1);
            fields.Read("position", () => new[] { Finite(__2.x), Finite(__2.y), Finite(__2.z) });
            fields.Read("rotation", () => new[] { Finite(__3.x), Finite(__3.y), Finite(__3.z), Finite(__3.w) }); return fields;
        }, __instance.m_group, __instance.m_popData)); __state = state;
    }
    private static void ScoreAfter(EnemyGroupScoreSpawnData __instance, PopulationBudgetCalls.Frame? __state, bool __runOriginal)
        => Observe(() => Returned(__state, () => ScoreFields(__instance), null, __runOriginal));
    private static void SpawnAfter(EnemyGroupScoreSpawnData __instance, PopulationBudgetCalls.Frame? __state, bool __result, bool __runOriginal)
        => Observe(() => Returned(__state, () => ScoreFields(__instance), __result, __runOriginal));

    // This uses CaptureRegistry's existing full-snapshot cadence, never a per-frame walker.
    internal static void Snapshot(string reason)
    {
        Observe(() =>
        {
            var fields = new PopulationBudgetFields();
            QualifyIdentity(fields);
            fields.Read("activeBalanceDefinition", () => RundownManager.ActiveExpedition?.Expedition?.ExpeditionBalance);
            fields.Read("activePopulationDefinition", () => RundownManager.ActiveExpedition?.Expedition?.EnemyPopulation);
            fields.Read("resolvedBalanceDefinition", () => RundownManager.ActiveExpeditionBalanceData?.persistentID);
            fields.Read("enemyPopulationPerZone", () => RundownManager.ActiveExpeditionBalanceData == null ? null : RundownManager.ActiveExpeditionBalanceData.EnemyPopulationPerZone);
            fields.Read("buildSeed", () => Builder.BuildSeed);
            fields.Read("hostSeed", () => Builder.HostIDSeedRandom == null ? null : Builder.HostIDSeedRandom.Seed);
            fields.Read("sessionSeed", () => Builder.SessionSeedRandom == null ? null : Builder.SessionSeedRandom.Seed);
            EnemyCostManager? manager = null;
            fields.Read("costManager", () => { manager = EnemyCostManager.Current; return Nonempty(Id(manager)); });
            fields.Read("allowedTotalCost", () => manager == null ? null : manager.m_allowedTotalCost);
            try
            {
                var map = manager?.m_dimensionEnemyCostMap;
                if (map == null) fields.Missing("dimensionEnemyCostMap", "unavailable");
                else
                {
                    var count = map.Count; var rows = new List<object>();
                    foreach (var row in map)
                    {
                        if (rows.Count >= MaxRows) break;
                        rows.Add(new { dimension = (int)row.Key, cost = Finite(row.Value) });
                    }
                    fields.Values["dimensionEnemyCostMap"] = rows; fields.Values["dimensionEnemyCostMapCount"] = count;
                    fields.Reads["dimensionEnemyCostMap"] = count > MaxRows ? "row-cap" : count != rows.Count || count != map.Count ? "changed-during-read" : "read";
                }
            }
            catch (Exception error) { fields.Missing("dimensionEnemyCostMap", "read-error:" + error.GetType().Name); }
            RecSession.Write(Channel, "snapshot", json =>
            {
                json.WriteString("schema", PopulationBudgetCalls.Schema); json.WriteString("reason", reason);
                json.WriteNumber("observationEpoch", Calls.Epoch); json.WriteNumber("worldEpoch", RecTime.WorldEpoch);
                json.WriteString("context", Context); json.WriteBoolean("hooksComplete", _hooksComplete);
                json.WriteNumber("recorderDropped", RecSession.Dropped); json.WriteBoolean("recorderBudgetReached", RecSession.BudgetReached);
                fields.Write(json); json.WriteBoolean("observationComplete", fields.Complete && _hooksComplete);
            });
            Calls.Status("snapshot");
        });
    }
}
