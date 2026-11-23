using System;
using ForgeEngine.Framework;
using ForgeMap.Generation;
using LevelGeneration;
using UnityEngine;
using static ForgeMap.Generation.GenerationProgram;

namespace ForgeMap.Native;

/// <summary>Every connector of a plan is built by the native LG_BuildPlugJob.Build (0x12780B0): pose, flip, gate binding, gate job, divider and
/// culling jobs are the game's own. The plan only answers which prefab the native selector returns, and reads the result back.</summary>
internal static partial class CompiledMapGenerator
{
    /// <summary>The plan row the running native plug job answers for.</summary>
    private sealed class PlugBuild(LG_Plug plug, int connection, int exit, int link)
    {
        internal readonly LG_Plug Plug = plug;
        internal readonly int Connection = connection, Exit = exit, Link = link;
        internal GameObject? Prefab, Instance;
        internal string Id(Run run) => Connection >= 0 ? run.Program.Connections[Connection].Id
            : Exit >= 0 ? run.Program.UnpairedExits[Exit].Id : run.RoomLinks[Link].Id;
    }

    /// <summary>False when the native job must not run: the plan refused, or the plan leaves this exit open (the one thing the game has no job for).</summary>
    internal static bool PlugBuildStarting(LG_Plug plug, ref bool result)
    {
        if (!Live(out var run)) return true;
        if (run.Failed) { result = false; return false; }
        try
        {
            if (plug.m_wasProcessed) return true;
            long pointer = plug.Pointer.ToInt64();
            if (FindPlugBinding(run, pointer) is { } binding)
            {
                if (binding.Connection >= 0)
                {
                    var row = run.Program.Connections[binding.Connection];
                    var from = Plug(run, row.FromRoom, row.FromExit); var to = Plug(run, row.ToRoom, row.ToExit);
                    if (from.m_pariedWith?.Pointer != to.Pointer || to.m_pariedWith?.Pointer != from.Pointer)
                        throw new GenerationRefusal("gen-exit-mismatch", row.Id);
                    if (row.Edge == EdgeKind.ZoneEntry && row.NoDoorProbability.HasValue)
                        throw new GenerationRefusal("gen-source-door-required", row.Id + "/noDoorProbability");
                    run.Building = new(plug, binding.Connection, -1, -1);
                    return true;
                }
                var exit = run.Program.UnpairedExits[binding.Exit];
                if (plug.m_pariedWith != null) throw new GenerationRefusal("gen-exit-occupied", exit.Id);
                if (exit.Treatment == ExitTreatment.Open)
                {
                    RoomRecipeBuilder.LeavePortOpen(plug);
                    plug.m_wasProcessed = true; run.BuiltExits[binding.Exit] = true;
                    run.Record("exits", exit.Id, new { nativePlug = pointer, treatment = "open", paired = false, processed = true });
                    result = true; return false;
                }
                run.Building = new(plug, -1, binding.Exit, -1);
                return true;
            }
            int end = Array.IndexOf(run.LinkEnds, pointer);
            if (end >= 0) run.Building = new(plug, -1, -1, end / 2);
            return true;
        }
        catch (Exception error) { Refuse(run, error); result = false; return false; }
    }

    /// <summary>The prefab for the native Get*Drop/GetPlugStraight call of the running job. False keeps the caller's own selection.</summary>
    internal static bool ConnectorOverride(string family, ref GameObject result)
    {
        if (!Live(out var run) || run.Building is not { } build || build.Exit >= 0) return false;
        try
        {
            build.Prefab ??= build.Connection >= 0 ? ChooseConnection(run, build)
                : ChooseLink(run, build, family switch { "straight" => 0, "single" => 1, _ => 2 });
            result = build.Prefab;
            return true;
        }
        catch (Exception error) { Refuse(run, error); return false; }
    }

    /// <summary>The prefab for the native GetPlugCap call of the running job: the exit's own row. A paired connection or loop that reaches
    /// the cap path was left Blocked by PrepZone, which the plan does not allow.</summary>
    internal static bool CapOverride(ref GameObject result)
    {
        if (!Live(out var run) || run.Building is not { } build) return false;
        try
        {
            if (build.Exit < 0) throw new GenerationRefusal("gen-connection-mismatch", build.Id(run) + "/capped", "status " + build.Plug.ExpanderStatus);
            var row = run.Program.UnpairedExits[build.Exit];
            result = build.Prefab = Asset(run, row.Resource ?? throw new GenerationRefusal("gen-resource-missing", row.Id));
            RoomRecipeBuilder.KeepAuthoredCap(build.Plug);
            return true;
        }
        catch (Exception error) { Refuse(run, error); return false; }
    }

    private static GameObject ChooseConnection(Run run, PlugBuild build)
    {
        int index = build.Connection; var row = run.Program.Connections[index];
        var from = Plug(run, row.FromRoom, row.FromExit); var to = Plug(run, row.ToRoom, row.ToExit);
        bool? gatePolicy = run.Program.Door(index, run.Seed);
        if (gatePolicy == null && (from.m_isZoneSource || to.m_isZoneSource)) gatePolicy = true;
        float rise = Math.Abs(from.transform.position.y - to.transform.position.y); int family = (int)row.HeightFamily;
        uint seed = run.Program.Random(row.ToRoom, DecisionKind.Connection, run.Seed, (uint)index);
        GameObject prefab;
        if (row.Resource is { } resource) prefab = Asset(run, resource);
        else
        {
            var set = ConnectionSet(run, from, row.FromRoom, row.ToRoom, row.Id);
            int sub = (int)(from.m_subComplex != to.m_subComplex ? Expedition.SubComplex.Plug_SubComplex_Transition : from.m_subComplex);
            var gates = family switch { 0 => set.m_straightPlugsWithGates, 1 => set.m_singleDropPlugsWithGates, _ => set.m_doubleDropPlugsWithGates };
            var noGates = family switch { 0 => set.m_straightPlugsNoGates, 1 => set.m_singleDropPlugsNoGates, _ => set.m_doubleDropPlugsNoGates };
            var gatePool = ConnectorPool(gates, sub);
            var noGatePool = ConnectorPool(noGates, sub);
            int gateCount = gatePool?.Length ?? 0, noGateCount = noGatePool?.Length ?? 0;
            var sized = RoomRecipeDoorSelection.SelectForDoor(from, to, set, gatePolicy == false ? null : gates, gatePolicy == true ? null : noGates, (Expedition.SubComplex)sub, seed);
            if (sized != null) prefab = sized;
            else if (gatePolicy is { } required)
            {
                var pool = required ? gatePool : noGatePool; int count = required ? gateCount : noGateCount;
                if (pool == null || count == 0) throw new GenerationRefusal("gen-resource-missing", row.Id + "/connector-pool");
                prefab = pool[(int)(seed % (uint)count)];
            }
            else
            {
                int count = checked(gateCount + noGateCount);
                if (count == 0) throw new GenerationRefusal("gen-resource-missing", row.Id + "/connector-pool");
                int selected = (int)(seed % (uint)count);
                prefab = selected < gateCount ? gatePool![selected] : noGatePool![selected - gateCount];
            }
        }
        bool withGate = run.PrefabHasGate(prefab, row.Id);
        if (gatePolicy is { } requestedGate && requestedGate != withGate)
            throw new GenerationRefusal("gen-connection-incompatible", row.Id + "/door");
        if (row.Resource is { } fixedResource)
            RequireFixedConnectorHeight(run, row.Id, run.ConnectorFamilies[fixedResource], run.ConnectorRises[fixedResource], family, rise);
        LogConnector(row.Id, prefab, withGate, family, rise, from, to);
        return prefab;
    }

    private static GameObject ChooseLink(Run run, PlugBuild build, int family)
    {
        var link = run.RoomLinks[build.Link]; var id = link.Id;
        var plug = build.Plug; var pair = plug.m_pariedWith ?? throw new GenerationRefusal("gen-exit-mismatch", id + "/unpaired");
        uint seed = run.Program.Random(link.ToRoom, DecisionKind.Connection, run.Seed, (uint)(run.Program.Connections.Length + build.Link));
        GameObject prefab;
        if (link.Resource is { } resource) prefab = Asset(run, resource);
        else
        {
            var set = ConnectionSet(run, plug, link.FromRoom, link.ToRoom, id);
            int sub = (int)(plug.m_subComplex != pair.m_subComplex ? Expedition.SubComplex.Plug_SubComplex_Transition : plug.m_subComplex);
            var pools = link.Weak
                ? family switch { 0 => set.m_straightPlugsWithGates, 1 => set.m_singleDropPlugsWithGates, _ => set.m_doubleDropPlugsWithGates }
                : family switch { 0 => set.m_straightPlugsNoGates, 1 => set.m_singleDropPlugsNoGates, _ => set.m_doubleDropPlugsNoGates };
            var sized = RoomRecipeDoorSelection.SelectForDoor(plug, pair, set, link.Weak ? pools : null, link.Weak ? null : pools, (Expedition.SubComplex)sub, seed);
            if (sized != null) prefab = sized;
            else
            {
                var pool = ConnectorPool(pools, sub);
                if (pool == null || pool.Length == 0) throw new GenerationRefusal("gen-resource-missing", id + "/connector-pool");
                prefab = pool[(int)(seed % (uint)pool.Length)];
            }
        }
        bool withGate = run.PrefabHasGate(prefab, id);
        if (withGate != link.Weak) throw new GenerationRefusal("gen-connection-incompatible", id + "/door");
        LogConnector(id, prefab, withGate, family, Math.Abs(plug.transform.position.y - pair.transform.position.y), plug, pair);
        return prefab;
    }

    /// <summary>The instance the native SpawnPlug made for a plan's connection. False leaves a job outside the plan to the recipe checks.</summary>
    internal static bool PlugSpawned(GameObject instance)
    {
        if (!Live(out var run) || run.Building is not { } build) return false;
        build.Instance = instance;
        return true;
    }

    /// <summary>True when an exception of the native job was taken over as this run's refusal.</summary>
    internal static bool PlugBuildFinished(Exception? error)
    {
        if (!Live(out var run) || run.Building is not { } build) return false;
        run.Building = null;
        if (run.Failed) return error != null;
        try
        {
            if (error != null) throw new GenerationRefusal("gen-native-exception", build.Id(run) + "/native-build", error.ToString());
            if (build.Connection >= 0) FinishConnection(run, build);
            else if (build.Link >= 0) FinishLink(run, build);
            else FinishExit(run, build);
        }
        catch (Exception failure) { Refuse(run, failure); }
        return error != null;
    }

    private static void FinishConnection(Run run, PlugBuild build)
    {
        if (build.Instance is not { } instance) return; // The native job of the pair's other end, or of a zone source's pair, builds nothing.
        int index = build.Connection; var row = run.Program.Connections[index]; var prefab = build.Prefab!;
        var from = Plug(run, row.FromRoom, row.FromExit); var to = Plug(run, row.ToRoom, row.ToExit);
        bool withGate = run.PrefabHasGate(prefab, row.Id);
        var gate = ConnectorGate(build.Plug);
        if (withGate && gate == null) throw new GenerationRefusal("gen-connection-gate-missing", row.Id);
        if (!from.m_wasProcessed || !to.m_wasProcessed) throw new GenerationRefusal("gen-connection-mismatch", row.Id + "/processed");
        run.BuiltConnections[index] = true;
        var targetRoom = run.Rooms[row.ToRoom] ?? throw new GenerationRefusal("gen-reference-missing", row.Id + "/targetRoom");
        var targetLocator = run.Locators[row.ToRoom] ?? throw new GenerationRefusal("gen-reference-missing", row.Id + "/targetLocator");
        var targetZone = targetRoom.Root.m_zone ?? throw new GenerationRefusal("gen-area-identity", row.Id + "/targetZone");
        GeneratedRoomRegistry.CompleteConnection(targetZone, targetLocator, row.Id, instance, gate);
        var receipt = GeneratedRoomRegistry.GeneratedConnection(run.Epoch, run.LevelReference, row.Id, out var receiptRefusal);
        if (receipt == null || receipt.Instance?.Pointer != instance.Pointer)
            throw new GenerationRefusal("gen-connection-mismatch", row.Id + "/identity-readback:" + receiptRefusal);
        run.Record("connections", row.Id, new
        {
            fromRoom = run.Program.Rooms[row.FromRoom].Id,
            toRoom = run.Program.Rooms[row.ToRoom].Id,
            authorConnectionId = receipt.Id,
            authorFromRoomId = receipt.From.RoomId,
            authorFromRoomRevision = receipt.From.Revision,
            authorFromRoomInstance = receipt.From.InstanceId,
            authorToRoomId = receipt.To.RoomId,
            authorToRoomRevision = receipt.To.Revision,
            authorToRoomInstance = receipt.To.InstanceId,
            prefab = prefab.name,
            withGate,
            rise = Math.Abs(from.transform.position.y - to.transform.position.y),
            family = (int)row.HeightFamily,
            paired = true,
            spawned = true,
            status = build.Plug.ExpanderStatus.ToString(),
            dividerCount = instance.GetComponentsInChildren<LG_PortalDivider>(true).Length,
            nativePlug = from.Pointer.ToInt64(),
            nativePair = to.Pointer.ToInt64(),
            nativeParent = build.Plug.Pointer.ToInt64(),
            nativeInstance = instance.Pointer.ToInt64(),
            nativeGate = receipt.Gate?.Pointer.ToInt64(),
            gate = gate?.name
        });
    }

    private static void FinishLink(Run run, PlugBuild build)
    {
        if (build.Instance is not { } instance) return;
        var link = run.RoomLinks[build.Link]; var id = link.Id; var plug = build.Plug; var pair = plug.m_pariedWith!;
        var from = plug.m_linksFrom ?? throw new GenerationRefusal("gen-area-identity", id + "/from");
        var to = plug.m_linksTo ?? throw new GenerationRefusal("gen-area-identity", id + "/to");
        // Readback: both sockets taken, and the door (or the free passage) sits in both areas' gate lists, which is what the
        // navigation link job and the cluster scan walk.
        LG_Gate? joined = ConnectorGate(plug) ?? instance.GetComponentInChildren<LG_FreePassageGate>(true);
        bool linked = joined != null && from.m_gates.Contains(joined) && to.m_gates.Contains(joined);
        if (!plug.m_wasProcessed || !pair.m_wasProcessed || !linked) throw new GenerationRefusal("gen-connection-mismatch", id + "/readback");
        run.BuiltLinks[build.Link] = true;
        run.Record("links", id, new
        {
            fromRoom = run.Program.Rooms[link.FromRoom].Id,
            toRoom = run.Program.Rooms[link.ToRoom].Id,
            weak = link.Weak,
            prefab = build.Prefab!.name,
            status = plug.ExpanderStatus.ToString(),
            fromArea = from.Pointer.ToInt64(),
            toArea = to.Pointer.ToInt64(),
            nativeInstance = instance.Pointer.ToInt64(),
            gate = joined?.name,
            linked
        });
    }

    /// <summary>A connection with a custom plug at one end (LG_PlugCustom.Awake, RVA 0x1363CD0, sets m_isCustom) is built by the native
    /// LG_BuildCustomPlugJob (0x14816F0), not by LG_BuildPlugJob: it marks both plugs processed and puts the plug's own built-in gate into
    /// both areas' gate lists; no connector is spawned and the plan's connector or door choice has nothing to apply to. The other end's
    /// LG_BuildPlugJob yields to a custom partner (0x12781DA). Read back after the Plugs batch.</summary>
    private static void FinishCustomConnections(Run run)
    {
        foreach (int index in run.LevelProgram.Connections)
        {
            var row = run.Program.Connections[index];
            if (run.BuiltConnections[index] || run.Rooms[row.FromRoom] == null || run.Rooms[row.ToRoom] == null) continue;
            var from = Plug(run, row.FromRoom, row.FromExit); var to = Plug(run, row.ToRoom, row.ToExit);
            var custom = from.m_isCustom ? from : to.m_isCustom ? to : null;
            if (custom == null) continue; // An ordinary connection left unbuilt is refused at the AIGraph check.
            var gate = custom.GetComponentInChildren<LG_Gate>();
            var fromArea = custom.m_linksFrom; var toArea = custom.m_linksTo;
            bool linked = gate != null && fromArea != null && toArea != null && fromArea.m_gates.Contains(gate) && toArea.m_gates.Contains(gate);
            if (!from.m_wasProcessed || !to.m_wasProcessed || !linked) throw new GenerationRefusal("gen-connection-mismatch", row.Id + "/custom-plug");
            var targetRoom = run.Rooms[row.ToRoom]!; var targetLocator = run.Locators[row.ToRoom] ?? throw new GenerationRefusal("gen-reference-missing", row.Id + "/targetLocator");
            var targetZone = targetRoom.Root.m_zone ?? throw new GenerationRefusal("gen-area-identity", row.Id + "/targetZone");
            // The custom job never writes LG_Plug.m_gate, the field a receipt's gate identity is checked against, so the receipt holds the plug itself.
            GeneratedRoomRegistry.CompleteConnection(targetZone, targetLocator, row.Id, custom.gameObject, null);
            run.BuiltConnections[index] = true;
            bool planChoice = row.Resource != null || row.Door != DoorKind.Native || row.NoDoorProbability.HasValue;
            run.Record("connections", row.Id, new
            {
                fromRoom = run.Program.Rooms[row.FromRoom].Id,
                toRoom = run.Program.Rooms[row.ToRoom].Id,
                builder = "custom-plug",
                gateType = gate!.Type.ToString(),
                zoneSource = gate.m_isZoneSource,
                planChoiceUnused = planChoice,
                nativePlug = custom.Pointer.ToInt64(),
                nativeGate = gate.Pointer.ToInt64()
            });
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationCustomPlug, "Connection built by the native custom plug job with the plug's own gate.",
                RuntimeLogValue.Quote("connection", row.Id), RuntimeLogValue.Quote("plug", custom.gameObject.name),
                RuntimeLogValue.Of("gateType", (long)gate.Type), RuntimeLogValue.Of("gateZoneSource", gate.m_isZoneSource),
                RuntimeLogValue.Of("planChoiceUnused", planChoice));
        }
    }

    private static void FinishExit(Run run, PlugBuild build)
    {
        var row = run.Program.UnpairedExits[build.Exit]; var plug = build.Plug;
        if (!plug.m_wasProcessed || build.Prefab == null) throw new GenerationRefusal("gen-connection-mismatch", row.Id + "/cap");
        run.BuiltExits[build.Exit] = true;
        run.Record("exits", row.Id, new
        {
            nativePlug = plug.Pointer.ToInt64(),
            prefab = build.Prefab.name,
            treatment = row.Treatment.ToString().ToLowerInvariant(),
            paired = false,
            processed = true
        });
    }
}
