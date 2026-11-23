using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeMap.Generation;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;
using static ForgeMap.Generation.GenerationProgram;

namespace ForgeMap.Native;

/// <summary>The compiled-plan adapter. All physical installations belong to Runtime. Nothing registers a frame callback.</summary>
[HarmonyPatch(typeof(LG_ZoneJob_CreateExpandFromData), nameof(LG_ZoneJob_CreateExpandFromData.ExpandZone))]
// patch-targets: LG_ZoneJob_CreateExpandFromData.CleanupFailedZoneBuilds, LG_Floor.InjectJobs, LG_Area.Setup, LG_FactoryBatch.Activate, LG_CustomGeomorphBuildJob.Build, LG_WardenObjective_HSUActivator_Room.OnLateBuildJob
internal static partial class CompiledMapGenerator
{
    private sealed record Placed(int Resource, LG_Geomorph Root, CellCoordinate Anchor, double Rotation, CellCoordinate[] Cells, LG_Area[] OriginalAreas);
    private readonly record struct PlugBinding(long Pointer, int Connection, int Exit);
    private sealed partial class Run
    {
        internal readonly GenerationProgram Program;
        internal readonly LevelProgram LevelProgram;
        internal readonly MapLevelReference LevelReference;
        internal readonly string Level;
        internal long Epoch;
        internal readonly uint Seed;
        internal readonly Placed?[] Rooms;
        internal readonly RoomLocator?[] Locators;
        internal readonly LG_Zone?[] Zones;
        internal readonly bool[] RegisteredZones;
        internal readonly int[] EntryRooms;
        internal readonly int[] ExpansionCalls;
        internal int? SettingUpRoom;
        internal readonly bool[] ReadDimensions;
        internal readonly bool[] BuiltConnections;
        internal readonly bool[] BuiltExits;
        internal readonly LinkProgram[] RoomLinks;
        internal readonly bool[] BuiltLinks;
        internal readonly GameObject?[] Assets;
        internal readonly int[][][] ExitAddresses;
        internal readonly int[][] PlugOrdinals;
        internal readonly nint[] PlugRoots;
        internal readonly int[] ConnectorFamilies;
        internal readonly float[] ConnectorRises;
        internal bool AssetsResolved;
        internal readonly PlugBinding[] PlugBindings;
        internal int PlugBindingCount;
        internal bool PlugsSorted;
        internal PlugBuild? Building;
        internal long[] LinkEnds = Array.Empty<long>();
        internal readonly AuthorAreaReadbackRow[][] AuthorAreaReadbacks;
        internal readonly List<object> Readbacks = new();
        internal readonly List<Action> RestoreInputs = new();
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly ForgeCore.GenerationAttemptTiming Attempt = ForgeCore.GenerationAttempt.Timing;
        internal bool Complete, Failed;
        internal int JobsDequeued, BatchAdvances;
        internal bool QueueStopped;
        internal Run(GenerationProgram program, LevelProgram level, long epoch, uint seed)
        {
            Program = program; LevelProgram = level; Level = level.Reference; Epoch = epoch; Seed = seed;
            LevelReference = MapLevelReference.TryParse(level.Reference) ?? throw new GenerationRefusal("gen-reference-missing", level.Id + "/reference");
            RoomLinks = level.Links.Select(index => program.Links[index]).ToArray(); BuiltLinks = new bool[RoomLinks.Length];
            Rooms = new Placed?[program.Rooms.Length]; Locators = new RoomLocator?[program.Rooms.Length]; Zones = new LG_Zone?[program.Zones.Length];
            RegisteredZones = new bool[program.Zones.Length]; EntryRooms = new int[program.Zones.Length];
            ExpansionCalls = new int[program.Zones.Length]; Array.Fill(EntryRooms, -1); Array.Fill(ExpansionCalls, -1);
            ReadDimensions = new bool[program.Dimensions.Length]; BuiltConnections = new bool[program.Connections.Length];
            BuiltExits = new bool[program.UnpairedExits.Length]; Assets = new GameObject?[program.Resources.Length];
            ExitAddresses = new int[program.Resources.Length][][];
            PlugOrdinals = new int[program.Rooms.Length][];
            PlugRoots = new nint[program.Rooms.Length];
            ConnectorFamilies = new int[program.Resources.Length]; Array.Fill(ConnectorFamilies, -1);
            ConnectorRises = new float[program.Resources.Length];
            PlugBindings = new PlugBinding[checked(level.Connections.Length * 2 + level.UnpairedExits.Length)];
            AuthorAreaReadbacks = new AuthorAreaReadbackRow[program.Rooms.Length][];
            for (int index = 0; index < AuthorAreaReadbacks.Length; index++) AuthorAreaReadbacks[index] = Array.Empty<AuthorAreaReadbackRow>();
        }
        internal void Record(string phase, string id, object actual)
            => Readbacks.Add(new { phase, id, actual, elapsedMs = (Stopwatch.GetTimestamp() - Started) * 1000d / Stopwatch.Frequency });
    }
    private static IReadOnlyList<GenerationProgram> Programs = Array.Empty<GenerationProgram>();
    private static Run? Current;
    private static RuntimeWorkLease? FailureExit;
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static void RequireSupported(GenerationProgram program)
    {
        program.RequireSupportedRequests();
        ValidateStaticRequests(program);
    }
    internal static void PrepareProgram(GenerationProgram program) { RequireSupported(program); Prepare(); }
    /// <summary>A reload's plan (contract I-RELOAD tier A): a reload installs no hook, so a plan is admitted only when the generator
    /// hooks are already on (Authoring puts them on with the first generation, <see cref="EnsureHooks"/>).</summary>
    internal static void RequireInstalled(GenerationProgram program)
    {
        RequireSupported(program);
        if (!_hooksDeclared) throw new GenerationRefusal("gen-hook-unavailable", "the generator hooks are not on in this process; restart the game to build a new plan");
    }
    /// <summary>Authoring's optional hook group: the generator hooks go on with the first generation even when no package has a plan
    /// yet, so a reload can add one.</summary>
    internal static IReadOnlyList<string> EnsureHooks()
    {
        try { Prepare(); return Array.Empty<string>(); }
        catch (Exception error) when (error is NotSupportedException or MissingMethodException or ArgumentException) { return new[] { error.Message }; }
    }
    private static bool _hooksDeclared;
    internal static void Prepare()
    {
        if (_hooksDeclared) return;

        // ExpandZone is this type's own [HarmonyPatch] target, declared in MapNativeHooks with the rest: one target, one Map writer.
        if (!MapFieldHost.Hooks.Offers(MapHookFeatures.CompiledZoneExpansionFeature))
            throw new NotSupportedException(MapFieldHost.Hooks.Refused[typeof(CompiledMapGenerator)]);
        void Declare(Type type, string method, string? prefix = null, string? postfix = null)
            => NativeHookRegistration.Declare(new("ForgeMap.Generation." + type.Name + "." + method,
                type.GetMethod(method) ?? throw new MissingMethodException(type.FullName, method),
                prefix == null ? null : Method(typeof(CompiledMapGenerator), prefix),
                postfix == null ? null : Method(typeof(CompiledMapGenerator), postfix)));
        Declare(typeof(LG_ZoneJob_CreateExpandFromData), nameof(LG_ZoneJob_CreateExpandFromData.CleanupFailedZoneBuilds), postfix: nameof(RootZoneCleanup));
        Declare(typeof(LG_Floor), nameof(LG_Floor.InjectJobs), postfix: nameof(StaticZonesInjected));
        Declare(typeof(LG_Area), nameof(LG_Area.Setup), nameof(AreaSeedBeforeSetup));
        Declare(typeof(LG_FactoryBatch), nameof(LG_FactoryBatch.Activate), postfix: nameof(BatchActivated));
        Declare(typeof(LG_CustomGeomorphBuildJob), nameof(LG_CustomGeomorphBuildJob.Build), prefix: nameof(HsuBuildStarting));
        Declare(typeof(LG_WardenObjective_HSUActivator_Room), nameof(LG_WardenObjective_HSUActivator_Room.OnLateBuildJob),
            prefix: nameof(HsuLateBuildStarting), postfix: nameof(HsuLateBuildReturned));
        FactoryJobs.DeclareGenerator("ForgeMap.Generation.LG_Factory.GetNewJob", GetCompiledJob);
        _hooksDeclared = true;
    }
    /// <summary>Puts in the generation plans of content generation <paramref name="generation"/> (contract I-RELOAD): every later
    /// build reads these plans, and the run records which generation it built from.</summary>
    internal static void Activate(IReadOnlyList<GenerationProgram> programs, long generation)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var program in programs)
            foreach (var level in program.Levels)
                if (!claimed.Add(level.Reference)) throw new GenerationRefusal("gen-plan-conflict", level.Id);
        Programs = programs; ContentGeneration = generation; Clear(); Ended = null;
    }
    /// <summary>The plans every build reads now, and the content generation they belong to.</summary>
    internal static IReadOnlyList<GenerationProgram> ActivePrograms => Programs;
    internal static long ContentGeneration { get; private set; }
    /// <summary>The last run that ended in this world, kept so a reader that comes after the run was cleared still gets
    /// its real outcome (complete, interrupted or refused) with the progress it had, and not the "no options" answer
    /// of a world that never had a plan. It is dropped when the world changes, a plan set is activated or stopped, or
    /// a new run begins.</summary>
    private static (long Epoch, string Json)? Ended;
    internal static void Clear()
    {
        FailureExit?.Dispose(); FailureExit = null;
        var previous = Current; Current = null;
        if (previous != null)
        {
            var failures = new List<Exception>();
            try
            {
                try { Ended = (previous.Epoch, Describe(previous, ended: true)); }
                catch (Exception error) { failures.Add(error); }
                for (int i = previous.RestoreInputs.Count - 1; i >= 0; i--) { try { previous.RestoreInputs[i](); } catch (Exception error) { failures.Add(error); } }
            }
            finally { previous.RestoreInputs.Clear(); RestoreLoadingPriority(previous); GeneratedRoomRegistry.Clear(); RoomGenerationFailure.Reset(); }
            if (failures.Count != 0) throw new AggregateException("gen-input-restore-failed", failures);
        }
    }
    internal static void Stop() { Programs = Array.Empty<GenerationProgram>(); ContentGeneration = 0; Clear(); Ended = null; }
    /// <summary>The level stays and only its world number moves (a host migration, or a client that built before it heard its
    /// host's number): the run and its room receipts move to that number instead of being cleared under a level that stays.
    /// False: there is no such run.</summary>
    internal static bool FollowWorld(long from, long to)
    {
        if (Current is not { } run || run.Epoch != from || run.Level != LevelIdentity.Read()?.ToString()) return false;
        run.Epoch = to;
        GeneratedRoomRegistry.FollowWorld(from, to);
        // A refusal's way out is frame work of the world it was armed in; it goes on in the new one.
        if (FailureExit != null) { FailureExit.Dispose(); FailureExit = null; ArmFailureExit(to); }
        return true;
    }
    /// <summary>A world began that is not a migration. When the level its build placed stays (<paramref name="keptFrom"/>,
    /// see <see cref="GameRuntimeBridge.KeepsBuiltLevel"/>), the run goes on under the new number with the same plan, so a
    /// build still in progress places the rest of its rooms. Any other new world clears the run. A run that cannot follow
    /// (another world or level than the one kept) and has not finished is refused, which leaves the level: it is never
    /// left half placed.</summary>
    internal static void WorldBegan(long? keptFrom, long to)
    {
        if (keptFrom is not { } from || Current is not { } run) { Clear(); return; }
        var level = LevelIdentity.Read()?.ToString();
        if (FollowWorld(from, to))
        {
            run.Record("world-followed", run.Level, new { from, to });
            return;
        }
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.GenerationWorldUnfollowed,
            "The level stays under a new world, but the generation run belongs to world " + run.Epoch + " of " + run.Level
            + ", not world " + from + " of " + (level ?? "no level") + ".",
            RuntimeLogValue.Of("from", from), RuntimeLogValue.Of("to", to), RuntimeLogValue.Of("runEpoch", run.Epoch),
            RuntimeLogValue.Quote("runLevel", run.Level), RuntimeLogValue.Quote("level", level ?? ""),
            RuntimeLogValue.Of("complete", run.Complete));
        // A finished build left a whole level; only its Forge records go with the old world.
        if (run.Complete) { Clear(); return; }
        run.Epoch = to;
        GeneratedRoomRegistry.Clear();
        if (FailureExit != null) { FailureExit.Dispose(); FailureExit = null; ArmFailureExit(to); }
        Refuse(run, new GenerationRefusal("gen-stale-generation", run.Level + "/world-follow"));
    }
    /// <summary>Whether an active plan generates <paramref name="level"/>.</summary>
    internal static bool Generates(string level)
    {
        foreach (var program in Programs) if (program.Level(level) != null) return true;
        return false;
    }
    private static bool EnabledDimension(Run run, int dimension)
        => run.Program.Dimensions[dimension].Enabled && run.Program.Dimensions[dimension].Level == run.LevelProgram.Index;
    private static bool EnabledZone(Run run, int zone) => EnabledDimension(run, run.Program.Zones[zone].Dimension);
    private static bool EnabledRoom(Run run, int room) => EnabledZone(run, run.Program.Rooms[room].Zone);
    private static int Count<T>(T[] values, Func<T, bool> predicate)
    { int count = 0; foreach (var value in values) if (predicate(value)) count++; return count; }
    internal static string Readback(long epoch)
    {
        var run = Current;
        if (epoch != NativeHookRegistration.WorldEpoch)
            return JsonSerializer.Serialize(new { status = "unavailable", reason = "gen-stale-generation" });
        if (run == null)
        {
            if (Ended is { } ended && ended.Epoch == epoch) return ended.Json;
            return JsonSerializer.Serialize(new
            {
                status = "vanilla",
                reason = "no-generation-options",
                factoryDone = LG_Factory.BuildDone,
                level = LevelIdentity.Read()?.ToString(),
                seed = Builder.BuildSeed,
                worldEpoch = epoch
            });
        }
        if (run.Epoch != epoch) return JsonSerializer.Serialize(new { status = "unavailable", reason = "gen-stale-generation" });
        return Describe(run, ended: false);
    }
    /// <summary>One run as the readback reports it. A run that is still going is "incomplete"; once it has ended without
    /// finishing it is "interrupted", so a reader never mistakes a cut-off generation for one still in flight.</summary>
    private static string Describe(Run run, bool ended)
    {
        bool factoryDone = LG_Factory.BuildDone;
        return JsonSerializer.Serialize(new
        {
            status = run.Failed ? "refused" : run.Complete && factoryDone ? "complete" : ended ? "interrupted" : "incomplete",
            factoryDone,
            plan = run.Program.Hash,
            level = run.Level,
            worldEpoch = run.Epoch,
            seed = run.Seed,
            expectedRooms = Count(run.LevelProgram.Rooms, room => EnabledRoom(run, room)),
            actualRooms = Count(run.Rooms, room => room != null),
            expectedDimensions = Count(run.LevelProgram.Dimensions, dimension => EnabledDimension(run, dimension)),
            actualDimensions = Count(run.ReadDimensions, value => value),
            expectedZones = Count(run.LevelProgram.Zones, zone => EnabledZone(run, zone)),
            actualZones = Count(run.RegisteredZones, value => value),
            expectedConnections = Count(run.LevelProgram.Connections, index => EnabledRoom(run, run.Program.Connections[index].ToRoom)),
            actualConnections = Count(run.BuiltConnections, value => value),
            expectedExits = Count(run.LevelProgram.UnpairedExits, index => EnabledRoom(run, run.Program.UnpairedExits[index].Room)),
            actualExits = Count(run.BuiltExits, value => value),
            expectedExpansions = Count(run.LevelProgram.Rooms, index => EnabledRoom(run, index) && run.Program.Rooms[index].RootRole is not (RootKind.Elevator or RootKind.Dimension)),
            actualExpansions = run.ExpansionCalls.Where(value => value >= 0).Sum(),
            zoneFacilities = ReadZoneFacilities(run, factoryDone),
            readbacks = run.Readbacks
        });
    }
    private static bool Live(out Run run)
    {
        run = Current!;
        if (run == null) return false;
        if (run.Epoch != NativeHookRegistration.WorldEpoch)
        { Clear(); return false; }
        return true;
    }
    private static void Refuse(Run run, Exception error)
    {
        if (run.Failed) return;
        run.Failed = true;
        var refusal = error as GenerationRefusal;
        run.Record("refused", refusal?.Path ?? run.Level, new { code = refusal?.Code ?? "gen-native-exception", detail = error.ToString() });
        RoomGenerationFailure.Request();
        if (FailureExit == null) ArmFailureExit(run.Epoch);
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.RoomConnectionRefused, error.Message,
            RuntimeLogValue.Of("reason", refusal?.Code ?? "gen-native-exception"),
            RuntimeLogValue.Of("path", refusal?.Path ?? run.Level));
        RoomGenerationFailure.RecordException(error);
    }
    /// <summary>Unwind the native call before leaving the world. Healthy generations register no frame work.</summary>
    private static void ArmFailureExit(long epoch)
        => FailureExit = RuntimeWorkPhases.Native("ForgeMap.Generation.FailureExit", epoch, -100, _ =>
        {
            RoomGenerationFailure.ContinueFactory();
            if (GameStateManager.CurrentStateName == eGameStateName.AfterLevel)
            { FailureExit?.Dispose(); FailureExit = null; }
        });
    private static void Begin()
    {
        string? level = LevelIdentity.Read()?.ToString();
        if (level == null) { Clear(); return; }
        GenerationProgram? selected = null; LevelProgram? selectedLevel = null;
        foreach (var program in Programs)
            if (program.Level(level) is { } candidate)
            { if (selected != null) throw new GenerationRefusal("gen-plan-conflict", candidate.Id); selected = program; selectedLevel = candidate; }
        if (selected == null || selectedLevel == null) { Clear(); return; } // No options: no native pool, seed, grid or job is changed.
        var run = new Run(selected, selectedLevel, NativeHookRegistration.WorldEpoch, Builder.BuildSeed);
        try
        {
            Clear(); Ended = null; Current = run;
            // Which content generation's plan this build reads: a reload is visible in the run's own readback.
            run.Record("content-generation", run.Level, new { generation = ContentGeneration, programHash = run.Program.Hash });
            RequireSupported(run.Program);
            ApplyInputs(run); // Before SetupFloor (batch 3) constructs any zone job or HashRandom.
            ApplyStaticInputs(run);
        }
        catch (Exception error) { Current = run; Refuse(run, error); }
    }
    private static int? ZoneIndex(Run run, LG_Zone zone)
    {
        int found = -1;
        foreach (int index in run.LevelProgram.Zones)
        {
            var row = run.Program.Zones[index]; var dimension = run.Program.Dimensions[row.Dimension];
            if (row.LocalIndex != (int)zone.LocalIndex || row.Layer != (int)zone.Layer.m_type || dimension.NativeAddress != (int)zone.DimensionIndex) continue;
            if (found >= 0) throw new GenerationRefusal("gen-layout-owner", row.Id + "/native-zone"); found = index;
        }
        return found < 0 ? null : found;
    }
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool ExpandZone(LG_ZoneJob_CreateExpandFromData __instance, LG_Zone __0, LG_Area __1,
        LG_ZoneExpander __2, uint __3, ref LG_Area? __4, ref LG_ZoneJob_CreateExpandFromData.CoverageResult __result)
    {
        if (!Live(out var run)) return true;
        try
        {
            if (ZoneIndex(run, __0) is not { } index) return true;
            __4 = null; __result = LG_ZoneJob_CreateExpandFromData.CoverageResult.Failed;
            if (run.Failed) return false;
            var zoneProgram = run.Program.Zones[index];
            if (run.Zones[index] != null) throw new GenerationRefusal("gen-stage-invalid", zoneProgram.Id + "/repeated-expansion");
            LG_Plug source = __2.TryCast<LG_Plug>() ?? throw new GenerationRefusal("gen-source-expander", zoneProgram.Id);
            LG_Area? sourceArea = __1;
            var root = run.Program.Rooms[zoneProgram.RootRoom];
            if (zoneProgram.Source is { } declared)
            {
                int from = declared.Room; var parent = run.Rooms[from] ?? throw new GenerationRefusal("gen-source-unavailable", root.Id + "/source");
                source = Plug(run, from, declared.Exit);
                sourceArea = parent.OriginalAreas[run.Program.Rooms[from].AreaMap[declared.Area]];
            }
            else if (root.ParentConnection is { } parentConnection)
            {
                var link = run.Program.Connections[parentConnection]; int from = link.FromRoom;
                if (run.Rooms[from] == null) throw new GenerationRefusal("gen-source-unavailable", root.Id + "/parentConnection");
                source = Plug(run, from, link.FromExit); sourceArea = source.OriginalParentArea;
            }
            else if (root.RootRole is RootKind.Elevator or RootKind.Dimension)
            {
                int rootIndex = root.Index, first = -1;
                foreach (int connection in run.LevelProgram.Connections)
                    if (run.Program.Connections[connection].FromRoom == rootIndex && run.Program.Rooms[run.Program.Connections[connection].ToRoom].Zone == index)
                    { first = connection; break; }
                if (first >= 0)
                {
                    var native = __0.Dimension.m_startTile?.m_geoRoot ?? __0.Dimension.StaticLevel
                        ?? throw new GenerationRefusal("gen-root-invalid", root.Id);
                    source = PlugOnRoot(run, rootIndex, root.Resource, native, run.Program.Connections[first].FromExit);
                    sourceArea = source.OriginalParentArea;
                }
            }
            if (sourceArea == null || source.OriginalParentArea?.Pointer != sourceArea.Pointer || !__instance.IsExpanderFree(source))
                throw new GenerationRefusal("gen-source-unavailable", root.Id + "/source-area-exit");
            // Keep native Init and Done. Only this compiled zone's placement/registration is owned here. PlaceZone stores the plan's
            // source in zone.m_sourceExpander, so Build does not fall back to its own candidate exit, and Done marks the plan's source.
            __instance.m_buildFromExpander = source; __instance.m_buildFromArea = sourceArea; __instance.m_buildFromZone = sourceArea.m_zone;
            run.ExpansionCalls[index] = 0;
            var floor = Builder.CurrentFloor ?? throw new GenerationRefusal("gen-world-unavailable", root.Id + "/floor");
            PlaceZone(run, index, floor, __0, source, sourceArea, __3, __instance);
            RegisterZone(run, index, __instance, __0);
            __4 = (run.Rooms[run.EntryRooms[index]] ?? throw new GenerationRefusal("gen-reference-missing", root.Id + "/entry")).Root.m_areas[0];
            // This is explicit custom-plan completion, never a fabricated CoverageStatus.Enough.
            // RegisterZone has independently checked both actual Area count and real GetCoverage sum.
            __result = LG_ZoneJob_CreateExpandFromData.CoverageResult.PlacedCustomGeomorph;
            RecordExpandZoneExit(run, index, __instance, __0, __2, source);
        }
        catch (Exception error) { __4 = null; __result = LG_ZoneJob_CreateExpandFromData.CoverageResult.Failed; Refuse(run, error); }
        return false;
    }
    private static void RegisterZone(Run run, int index, LG_ZoneJob_CreateExpandFromData __instance, LG_Zone __0)
    {
        if (run.RegisteredZones[index]) throw new GenerationRefusal("gen-stage-invalid", run.Program.Zones[index].Id + "/duplicate-area-registration");
        var zone = run.Program.Zones[index];
        var areas = __0.m_areas ?? throw new GenerationRefusal("gen-area-identity", zone.Id);
        var settings = __0.m_settings ?? throw new GenerationRefusal("gen-stage-invalid", zone.Id);
        if (__0.m_subSeed != zone.ZoneSeed.Apply(__0.m_subSeed)
            || __instance.m_subSeed != __0.m_subSeed || __0.m_markerSubSeed != zone.MarkerSeed.Apply(__0.m_markerSubSeed)
            || __instance.m_markerSubSeed != __0.m_markerSubSeed || __0.m_lightsSubSeed != zone.LightSeed.Apply(__0.m_lightsSubSeed)
            || __instance.m_lightsSubSeed != __0.m_lightsSubSeed)
            throw new GenerationRefusal("gen-pin-mismatch", zone.Id + "/constructor-seed-readback");
        var unique = new HashSet<IntPtr>();
        foreach (int roomIndex in zone.Rooms)
        {
            var room = (run.Rooms[roomIndex] ?? throw new GenerationRefusal("gen-reference-missing", run.Program.Rooms[roomIndex].Id)).Root;
            foreach (var area in room.m_areas)
            {
                if (!unique.Add(area.Pointer) || area.m_zone?.Pointer != __0.Pointer || area.m_geomorph?.Pointer != room.Pointer)
                    throw new GenerationRefusal("gen-area-identity", run.Program.Rooms[roomIndex].Id,
                        GeneratedRoomRegistry.AreaOwnerDetail(__0, room, area));
            }
        }
        var registered = new HashSet<IntPtr>();
        foreach (var area in areas)
            if (area == null || !registered.Add(area.Pointer) || !unique.Contains(area.Pointer))
                throw new GenerationRefusal("gen-area-identity", zone.Id + "/zone-area-membership");
        if (!registered.SetEquals(unique)) throw new GenerationRefusal("gen-area-identity", zone.Id + "/zone-area-membership");
        float actual = 0;
        foreach (var area in areas) actual += area.GetCoverage();
        if (actual != __0.m_coverage) throw new GenerationRefusal("gen-coverage-unsatisfied", zone.Id + "/native-coverage-readback");
        var bounds = run.Program.Coverage(index);
        if (settings.m_minCoverage != bounds.Minimum || settings.m_maxCoverage != bounds.Maximum)
            throw new GenerationRefusal("gen-pin-mismatch", zone.Id + "/native-coverage-bounds");
        run.Program.VerifyStop(index, areas.Count, actual, bounds.Minimum, bounds.Maximum,
            run.ExpansionCalls[index]);
        run.RegisteredZones[index] = true;
        run.Record("areas", zone.Id, new
        {
            dimension = (int)__0.DimensionIndex,
            layer = (int)__0.Layer.m_type,
            localZone = (int)__0.LocalIndex,
            areas = areas.Count,
            coverage = actual,
            expansions = run.ExpansionCalls[index],
            minimum = settings.m_minCoverage,
            maximum = settings.m_maxCoverage,
            zoneSeed = __0.m_subSeed,
            markerSeed = __0.m_markerSubSeed,
            lightSeed = __0.m_lightsSubSeed
        });
    }
    private static void BatchStarted(LG_Factory.BatchName batch, bool succeeded)
    {
        if (!succeeded || !Live(out var run) || run.Failed) return;
        try
        {
            if (batch == LG_Factory.BatchName.Plugs)
            {
                Array.Sort(run.PlugBindings, 0, run.PlugBindingCount, Comparer<PlugBinding>.Create((a, b) => a.Pointer.CompareTo(b.Pointer)));
                run.PlugsSorted = true; return;
            }
            if (batch == LG_Factory.BatchName.Gates) { FinishCustomConnections(run); return; }
            if (batch != LG_Factory.BatchName.AIGraph_UnityAIGraph) return;
            if (Count(run.LevelProgram.Rooms, index => run.Rooms[index] != null) != Count(run.LevelProgram.Rooms, index => EnabledRoom(run, index)))
                throw new GenerationRefusal("gen-reference-missing", "rooms/unbuilt");
            if (Count(run.LevelProgram.Dimensions, index => run.ReadDimensions[index]) != Count(run.LevelProgram.Dimensions, index => EnabledDimension(run, index)))
                throw new GenerationRefusal("gen-reference-missing", "dimensions/unbuilt");
            int expectedZones = Count(run.LevelProgram.Zones, index => run.Zones[index] != null);
            if (Count(run.LevelProgram.Zones, index => run.RegisteredZones[index]) != expectedZones
                || expectedZones != Count(run.LevelProgram.Zones, index => EnabledZone(run, index)))
                throw new GenerationRefusal("gen-reference-missing", "zones/unregistered");
            foreach (int index in run.LevelProgram.Rooms) if (run.Rooms[index] is { } room) VerifyCells(run, index, room);
            foreach (int index in run.LevelProgram.Connections)
                if (run.Rooms[run.Program.Connections[index].ToRoom] != null && !run.BuiltConnections[index])
                    throw new GenerationRefusal("gen-reference-missing", run.Program.Connections[index].Id + "/connector-not-built");
            foreach (int index in run.LevelProgram.UnpairedExits)
                if (run.Rooms[run.Program.UnpairedExits[index].Room] != null && !run.BuiltExits[index])
                    throw new GenerationRefusal("gen-reference-missing", run.Program.UnpairedExits[index].Id + "/exit-not-built");
            for (int index = 0; index < run.RoomLinks.Length; index++)
                if (!run.BuiltLinks[index] && LinkPlaced(run, index)) throw new GenerationRefusal("gen-reference-missing", run.RoomLinks[index].Id + "/link-not-built");
            var authorAreas = FinalizeAuthorAreaReadbacks(run);
            run.Record("author-course-nodes", run.Level, new
            {
                rooms = authorAreas.Rooms,
                areas = authorAreas.Areas,
                createdIn = "PrepareZones",
                verifiedAt = batch.ToString()
            });
            run.Complete = true;
            run.Record("before-navigation", run.Level, new
            {
                rooms = Count(run.Rooms, value => value != null),
                connections = Count(run.BuiltConnections, value => value),
                exits = Count(run.BuiltExits, value => value),
                links = Count(run.BuiltLinks, value => value)
            });
        }
        catch (Exception error) { Refuse(run, error); }
    }
}
