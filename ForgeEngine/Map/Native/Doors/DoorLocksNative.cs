using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using HarmonyLib;
using LevelGeneration;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Native construction/lock adapter and immutable execution observer. No scheduler or completion store.</summary>
internal sealed class DoorLocksNative : IDisposable
{
    internal static DoorLocksNative? Current { get; private set; }
    private readonly IForgeEngine _kernel;
    private readonly IReadOnlyList<DoorLockRow> _rows;
    private readonly IReadOnlyList<GeneratorSourceRow> _generators;
    private readonly RuntimeBehaviorTraceSubscription? _trace;
    // The remaining-conditions record: which conditions of each door are done. The host computes it from the behavior trace; it travels the
    // Runtime native-state path (join, reconnect, checkpoint, migration), so every machine writes the same prompt from the same record.
    private readonly DomainStateChannel.NativeStateSource? _state;
    private readonly Dictionary<string, bool[]> _remaining = new(StringComparer.Ordinal);
    private const string StateKey = "forge.map.door-lock-prompt";
    // One transient execution read frame for the prompt; discarded at the last read, never used to open a door.
    private readonly Dictionary<string, bool[]> _promptFrames = new(StringComparer.Ordinal);
    internal DoorLocksNative(IForgeEngine kernel, IReadOnlyList<DoorLockRow> rows, IReadOnlyList<GeneratorSourceRow>? generators = null)
    {
        _kernel = kernel; _rows = rows; _generators = generators ?? Array.Empty<GeneratorSourceRow>();
        if (rows.Any(row => row.Conditions.Count > 0))
        { _trace = kernel.ObserveBehaviorTrace(Observe); _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore); }
        Current = this;
    }
    private static string RemainingKey(DoorLockRow row) => row.Level + "/" + row.Door;
    private JsonElement Capture() => JsonSerializer.SerializeToElement(_remaining);
    private void Restore(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The door-lock prompt record is malformed.");
        var next = new Dictionary<string, bool[]>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
        {
            var row = _rows.FirstOrDefault(candidate => RemainingKey(candidate) == entry.Name);
            if (row == null || entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() != row.Conditions.Count
                || entry.Value.EnumerateArray().Any(flag => flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                throw new InvalidDataException("The door-lock prompt record is malformed.");
            next.Add(entry.Name, entry.Value.EnumerateArray().Select(flag => flag.GetBoolean()).ToArray());
        }
        _remaining.Clear();
        foreach (var pair in next) _remaining.Add(pair.Key, pair.Value);
        var level = LevelIdentity.Read();
        foreach (var row in _rows.Where(row => row.Level == level && _remaining.ContainsKey(RemainingKey(row)))) ApplyPrompt(row);
    }
    // Writes the recorded prompt into the door if it is built and still waiting; a door built later reads the record in Built.
    private void ApplyPrompt(DoorLockRow row)
    {
        var door = DoorObservation.ByAddress(row.Door); var locks = door?.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (door?.LastStatus != eDoorStatus.Closed_LockedWithNoKey || locks == null) return;
        locks.m_lockedWithNoKeyInteractionText = TextCardValue.FromRendered(Prompt(row, _remaining[RemainingKey(row)])); locks.UpdateText();
    }
    private DoorLockRow? Row(MapObjectReference address)
    {
        // The level is native state: read it once per lookup (and only when a row exists), not once per row.
        MapLevelReference? level = null; var read = false;
        foreach (var row in _rows)
        {
            if (!read) { level = LevelIdentity.Read(); read = true; }
            if (row.Level == level && row.Door == address) return row;
        }
        return null;
    }
    internal DoorLockRow? Row(LG_SecurityDoor door) => DoorObservation.Decide(door).Address is { } address ? Row(address) : null;
    internal List<(LG_Zone Zone, ProgressionPuzzleData Original)> Prepare(LG_Distribute_ProgressionPuzzles job)
    {
        var originals = new List<(LG_Zone, ProgressionPuzzleData)>();
        try
        {
            var level = _rows.Count == 0 ? null : LevelIdentity.Read();
            foreach (var row in _rows)
            {
                if (row.Level != level || !row.KeyPuzzle.HasValue) continue;
                var zone = ZoneOf(job.m_floor, row.Door);
                if (zone == null) { Refuse(row, "door-lock-zone-missing"); continue; }
                var data = zone.m_settings?.m_zoneData ?? throw new InvalidOperationException("door-lock-zone-data");
                var previous = data.ProgressionPuzzleToEnter;
                if (previous.PuzzleType != eProgressionPuzzleType.Locked_No_Key) { Refuse(row, "door-lock-not-no-key"); continue; }
                originals.Add((zone, previous));
                var puzzle = new ProgressionPuzzleData
                {
                    PuzzleType = eProgressionPuzzleType.Keycard_SecurityBox,
                    PlacementCount = 1,
                    CustomText = previous.CustomText,
                    ZonePlacementData = new Il2CppSystem.Collections.Generic.List<ZonePlacementData>()
                };
                foreach (var place in row.KeyPuzzle!.Value.GetProperty("ZonePlacementData").EnumerateArray())
                {
                    var weights = place.GetProperty("Weights");
                    puzzle.ZonePlacementData.Add(new ZonePlacementData
                    {
                        LocalIndex = (eLocalZoneIndex)place.GetProperty("LocalIndex").GetInt32(),
                        Weights = new ZonePlacementWeights { Start = weights.GetProperty("Start").GetSingle(), Middle = weights.GetProperty("Middle").GetSingle(), End = weights.GetProperty("End").GetSingle() }
                    });
                }
                // Only this authored zone participates in the unchanged vanilla key distribution job.
                data.ProgressionPuzzleToEnter = puzzle;
            }
        }
        catch (Exception error)
        {
            // The original failure is the one rethrown; a zone that could not be put back is attached to it.
            try { Restore(originals); }
            catch (Exception restoreError) { error.Data["door-lock-restore"] = restoreError.Message; }
            throw;
        }
        return originals;
    }
    /// <summary>The built zone an authored map address names on this floor, or null when the floor has none.</summary>
    private static LG_Zone? ZoneOf(LG_Floor? floor, MapObjectReference address)
    {
        var dimension = (eDimensionIndex)int.Parse(address.Dimension, CultureInfo.InvariantCulture);
        var layer = (LG_LayerType)int.Parse(address.Layer, CultureInfo.InvariantCulture);
        var index = (eLocalZoneIndex)int.Parse(address.Zone, CultureInfo.InvariantCulture);
        return floor != null && floor.TryGetZoneByLocalIndex(dimension, layer, index, out var zone) && zone != null ? zone : null;
    }
    internal void ResolveGenerators()
    {
        var generatorLevel = _generators.Count == 0 ? null : LevelIdentity.Read();
        foreach (var row in _generators.Where(row => row.Level == generatorLevel))
        {
            var placement = row.Placement;
            void Missing(string reason) => MapLog.Record(RuntimeLogLevel.Error, DoorLocksCodes.Refused, "Generator source could not be resolved.", RuntimeLogValue.Of("generator", row.Address.ToString()), RuntimeLogValue.Of("reason", reason));
            var address = row.Address;
            var zone = ZoneOf(LG_LevelBuilder.Current?.m_currentFloor, address);
            if (zone == null) { Missing("door-lock-generator-missing"); continue; }
            var dimension = int.Parse(address.Dimension, CultureInfo.InvariantCulture);
            var layer = int.Parse(address.Layer, CultureInfo.InvariantCulture);
            var role = new FixedTaskObjectRole(FixedTaskObjectKind.Generator, FixedTaskObjectScope.Zone, dimension, layer, placement.Index, placement.Count, false, null) { PlacementId = placement.PlacementId };
            if (!GeneratorPlacementBuild.Resolve(_kernel.WorldEpoch, zone, role, out var generator, out var reason))
            { Missing(reason); continue; }
            if (generator == null) { Missing("door-lock-generator-missing"); continue; }
            if (!GeneratorObservation.RegisterPlacement(_kernel.WorldEpoch, generator, address)) Missing("fixed-object-identity-ambiguous");
        }
    }
    /// <summary>Puts every changed zone back; a zone that cannot be written does not stop the others, and the failures
    /// are thrown together at the end.</summary>
    internal static void Restore(List<(LG_Zone Zone, ProgressionPuzzleData Original)> originals)
    {
        List<Exception>? errors = null;
        foreach (var row in originals)
        {
            try { (row.Zone.m_settings?.m_zoneData ?? throw new InvalidOperationException("door-lock-zone-data")).ProgressionPuzzleToEnter = row.Original; }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        if (errors != null) throw new AggregateException("Door-lock zones could not be restored.", errors);
    }
    internal void Built(LG_SecurityDoor door)
    {
        if (Row(door) is not { KeyPuzzle: not null } row) return;
        door.SetupAsLockedNoKey(TextCardValue.FromRendered(Prompt(row, _remaining.GetValueOrDefault(RemainingKey(row)) ?? new bool[row.Conditions.Count])));
    }
    internal MapActionOutcome? Unlock(LG_SecurityDoor door, MapObjectReference address)
    {
        var row = Row(address); if (row == null) return null;
        if (row.KeyPuzzle.HasValue)
        {
            if (door.LastStatus is eDoorStatus.Closed_LockedWithKeyItem or eDoorStatus.ChainedPuzzleActivated or eDoorStatus.Open or eDoorStatus.Opening or eDoorStatus.Unlocked
                or eDoorStatus.Closed_LockedWithChainedPuzzle or eDoorStatus.Closed_LockedWithChainedPuzzle_Alarm)
                return MapActionOutcome.AlreadyInState(ResultCode.DoorLockAlreadyReady);
            var key = door.m_keyItem; var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>(); var sync = door.m_sync?.TryCast<LG_Door_Sync>();
            if (key == null || locks == null || sync?.m_stateReplicator == null) { Refuse(row, ResultCode.DoorLockKeyMissing); return MapActionOutcome.Refused(ResultCode.DoorLockKeyMissing); }
            // SetupForGateKey initializes the native interactable; the native replicator publishes status 3.
            var state = sync.GetCurrentSyncState(); state.status = locks.SetupForGateKey(key);
            sync.m_stateReplicator.OnStateChange_Master(state);
            MapLog.Record(RuntimeLogLevel.Info, DoorLocksCodes.KeyEnabled, "Key insertion is available.", RuntimeLogValue.Of("door", row.Door.ToString()), RuntimeLogValue.Of("key", key.PublicName));
        }
        else door.UseChainedPuzzleOrUnlock(null);
        MapLog.Record(RuntimeLogLevel.Info, DoorLocksCodes.Ready, "Door opening conditions met.", RuntimeLogValue.Of("door", row.Door.ToString()), RuntimeLogValue.Of("rule", row.Rule));
        return MapActionOutcome.Issued(ResultCode.DoorUnlockIssued);
    }
    internal void Receive(LG_Door_Sync sync, pDoorState state)
    {
        if (state.status != eDoorStatus.Closed_LockedWithKeyItem) return;
        var level = _rows.Count == 0 ? null : LevelIdentity.Read();
        foreach (var row in _rows.Where(row => row.Level == level && row.KeyPuzzle.HasValue))
        {
            var door = DoorObservation.ByAddress(row.Door);
            if (door?.m_sync?.TryCast<LG_Door_Sync>()?.Pointer != sync.Pointer || door.m_keyItem == null) continue;
            door.m_locks?.TryCast<LG_SecurityDoor_Locks>()?.SetupForGateKey(door.m_keyItem);
        }
    }
    internal bool KeyPresent(LG_SecurityDoor door, SNet_Player source)
    {
        try { return Session.IsHost && Row(door)?.KeyConditionId != null && door.DoesPlayerHaveKey(source); }
        catch (Exception containedError) { MapLog.Contained("door-locks-native.key-present", containedError); return false; }
    }
    internal void KeyCompleted(LG_SecurityDoor door, SNet_Player source, bool wasPresent)
    {
        if (!wasPresent || !Session.IsHost || door.DoesPlayerHaveKey(source)) return;
        if (Row(door) is { KeyConditionId: not null } row)
            MapLog.Record(RuntimeLogLevel.Info, DoorLocksCodes.Completed, "Native key card insertion completed.", RuntimeLogValue.Of("door", row.Door.ToString()), RuntimeLogValue.Of("condition", row.KeyConditionId));
    }
    /// <summary>The door's prompt: the template with the conditions still open, then the key card when the door takes one.</summary>
    private static string Prompt(DoorLockRow row, bool[] values)
    {
        var open = new List<string>();
        for (int i = 0; i < row.Conditions.Count; i++) if (!values[i]) open.Add(row.Conditions[i].Label);
        if (row.KeyPuzzle.HasValue) open.Add(row.KeyLabel);
        return row.RemainingTemplate.Replace("{conditions}", string.Join(" / ", open), StringComparison.Ordinal);
    }
    private static void Refuse(DoorLockRow row, string reason) => MapLog.Record(RuntimeLogLevel.Error, DoorLocksCodes.Refused, "Door condition could not be applied.", RuntimeLogValue.Of("door", row.Door.ToString()), RuntimeLogValue.Of("reason", reason));
    private void Observe(RuntimeBehaviorTraceRecord trace)
    {
        try { ObserveReadback(trace); }
        catch (Exception error) { _promptFrames.Clear(); MapLog.Record(RuntimeLogLevel.Error, DoorLocksCodes.Refused, "Door prompt readback failed.", RuntimeLogValue.Of("reason", "door-lock-prompt-exception"), RuntimeLogValue.Of("exception", error.GetType().Name)); }
    }
    private void ObserveReadback(RuntimeBehaviorTraceRecord trace)
    {
        // Every step of every behavior passes here: no work unless a read frame is open or the plan is a door's.
        if (_promptFrames.Count != 0 && trace.Scope == "behavior-entry" && trace.Phase == "after" && trace.Plan is { } completed)
        {
            var prefix = trace.WorldEpoch + "/" + trace.EventId + "/" + completed.PlanId + "/";
            foreach (var frame in _promptFrames.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) _promptFrames.Remove(frame);
        }
        if (trace.Phase != "after" || trace.Plan is not { } plan || trace.Step is not { } step || trace.Outputs is not { } outputs) return;
        DoorLockRow? row = null;
        MapLevelReference? level = null; var levelRead = false;
        foreach (var candidate in _rows)
        {
            if (candidate.PlanId != plan.ResourceId) continue;
            if (!levelRead) { level = LevelIdentity.Read(); levelRead = true; }
            if (candidate.Level == level) { row = candidate; break; }
        }
        if (row == null) return;
        if (step.EndsWith("-complete", StringComparison.Ordinal) && outputs.TryGetProperty("written", out var written) && written.ValueKind == JsonValueKind.True
            && outputs.TryGetProperty("previous", out var previous) && previous.ValueKind == JsonValueKind.False)
        {
            if (int.TryParse(step.AsSpan(1, step.IndexOf('-') - 1), out var index) && index < row.Conditions.Count)
                MapLog.Record(RuntimeLogLevel.Info, DoorLocksCodes.Completed, "Door condition completed.", RuntimeLogValue.Of("door", row.Door.ToString()), RuntimeLogValue.Of("condition", row.Conditions[index].Id));
        }
        var split = step.LastIndexOf("-read-", StringComparison.Ordinal);
        if (split < 0 || !int.TryParse(step.AsSpan(split + 6), out var ordinal) || ordinal >= row.Conditions.Count || !outputs.TryGetProperty("value", out var value)) return;
        var key = trace.WorldEpoch + "/" + trace.EventId + "/" + plan.PlanId + "/" + step[..split];
        if (ordinal == 0) _promptFrames[key] = new bool[row.Conditions.Count];
        if (!_promptFrames.TryGetValue(key, out var flags)) return;
        flags[ordinal] = value.ValueKind == JsonValueKind.True;
        if (ordinal != flags.Length - 1) return;
        _promptFrames.Remove(key);
        _remaining[RemainingKey(row)] = flags; _state?.Changed();
        ApplyPrompt(row);
    }
    public void Dispose() { _trace?.Dispose(); _state?.Dispose(); _promptFrames.Clear(); _remaining.Clear(); if (ReferenceEquals(Current, this)) Current = null; }
}

[HarmonyPatch(typeof(LG_Distribute_ProgressionPuzzles), nameof(LG_Distribute_ProgressionPuzzles.Build))]
internal static class DoorLockKeyDistribution
{
    // One patch class for Build: the prefix opens the fixed door-generator pin scope (FixedDoorGeneratorNode reads it) and prepares the key distribution; the finalizer closes the scope and restores the zone data.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_Distribute_ProgressionPuzzles __instance, out List<(LG_Zone Zone, ProgressionPuzzleData Original)>? __state)
    { FixedFunctionPoints.Progression = true; __state = DoorLocksNative.Current?.Prepare(__instance); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Restore(List<(LG_Zone Zone, ProgressionPuzzleData Original)>? __state)
    { FixedFunctionPoints.Progression = false; if (__state != null) DoorLocksNative.Restore(__state); }
}
[HarmonyPatch(typeof(LG_SecurityDoor), nameof(LG_SecurityDoor.SetupKeyItemLock))]
internal static class DoorLockKeyBuilt
{ [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(LG_SecurityDoor __instance) => DoorLocksNative.Current?.Built(__instance); }
[HarmonyPatch(typeof(LG_Door_Sync), nameof(LG_Door_Sync.OnStateChange))]
internal static class DoorLockStateReceived
{ [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(LG_Door_Sync __instance, pDoorState newState) => DoorLocksNative.Current?.Receive(__instance, newState); }

[HarmonyPatch(typeof(LG_SecurityDoor), nameof(LG_SecurityDoor.RemoveKeyFromPlayer))]
internal static class DoorLockKeyCompleted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)] private static void Prefix(LG_SecurityDoor __instance, SNet_Player player, out bool __state) => __state = DoorLocksNative.Current?.KeyPresent(__instance, player) ?? false;
    [HarmonyPostfix, HarmonyPriority(Priority.Last)] private static void Postfix(LG_SecurityDoor __instance, SNet_Player player, bool __state) => DoorLocksNative.Current?.KeyCompleted(__instance, player, __state);
}
