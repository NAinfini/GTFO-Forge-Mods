using System;
using System.Collections.Generic;
using AIGraph;
using ChainedPuzzles;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Verifies and claims every authored task object before native culling, on every peer. Zone terminals, zone,
/// door-lock and objective generators, HSUs and generator clusters were built by the native builder at their authored
/// points (<see cref="FixedFunctionPoints"/>); here each row's native identity (zone terminal ordinal, generator request
/// slot, door lock, objective collection ordinal) must be the object built at that row's point, and a mismatch is
/// refused as an error. A row whose point built nothing is reported once and its native object is moved to the authored
/// point as before. Objects no marker request builds are moved too: uplink and download terminals (the uplink receiver
/// is picked later by the linker job) and central-cluster objective generators, which their cluster builds. Positional
/// state is absent from their replicators; only interaction/progress state remains native. See
/// fixed-object-placement-20403457.json. Generated scans of a moved object retain their original world pose and navigation path.</summary>
internal static class TaskObjectPlacement
{
    private static (long Epoch, IntPtr Builder)? preparedWorld;
    private static int preparedCount;

    internal static int Place(long epoch, IReadOnlyList<FixedItem> rows, Action<string> report)
    {
        var world = (epoch, LG_LevelBuilder.Current?.Pointer ?? IntPtr.Zero);
        if (preparedWorld == world) return preparedCount;
        if (LG_Factory.BuildDone)
        {
            foreach (var row in rows)
                if (row.TaskObject != null) Refuse(epoch, row, "fixed-object-culling-stage-missed", report);
            return 0;
        }
        preparedWorld = world;
        preparedCount = 0;
        var claimed = new HashSet<int>();
        // Every task object exists before native culling collection. Later FactoryDone only reads this receipt.
        foreach (var kind in new[] { FixedTaskObjectKind.GeneratorCluster, FixedTaskObjectKind.Terminal, FixedTaskObjectKind.Generator, FixedTaskObjectKind.Hsu, FixedTaskObjectKind.UplinkTerminal, FixedTaskObjectKind.DownloadTerminal })
            foreach (var row in rows)
                if (row.TaskObject?.Kind == kind)
                    try { PlaceRow(epoch, row, claimed, report); }
                    catch (Exception error) { Refuse(epoch, row, "fixed-object-move-exception:" + error.GetType().Name, report); }
        return preparedCount = claimed.Count;
    }

    private static void PlaceRow(long epoch, FixedItem row, HashSet<int> claimed, Action<string> report)
    {
        var role = row.TaskObject!;
        if (!RoomResolver.TryPlace(epoch, row.Locator, out var room, out var refusal))
        { Refuse(epoch, row, refusal!, report); return; }
        if (role.Kind == FixedTaskObjectKind.GeneratorCluster) { PlaceCluster(epoch, row, claimed, report); return; }
        var world = row.WorldPose(room!.Pose);
        if (!RoomResolver.TryCourseNode(row.Locator.Dimension, world.Position, room.AreaCourseNode, out var node)
            || node == null || !InZone(node, row.Locator))
        { Refuse(epoch, row, "fixed-object-course-node", report); return; }
        var area = node.m_area;
        if (area == null || area.WasCollected || area.transform == null || area.transform.WasCollected)
        { Refuse(epoch, row, "fixed-object-course-node", report); return; }
        Component component;
        if (role.Kind == FixedTaskObjectKind.Terminal)
        {
            var zone = node.m_zone!;
            if (ZoneIndex.SpecificTerminalPlacements(zone) is not 0)
            { Refuse(epoch, row, "fixed-object-terminal-ambiguous", report); return; }
            var objects = zone.TerminalsSpawnedInZone;
            var candidates = new List<TaskObjectCandidate>();
            if (objects != null)
                for (var i = 0; i < objects.Count; i++)
                    if (objects[i] != null && !objects[i].WasCollected) candidates.Add(new(i, i, objects[i].GetInstanceID()));
            var match = FixedTaskObjectMatching.Match(candidates, role.Index, role.Count, shortList: true);
            if (match.Kind != TaskObjectMatchKind.Matched)
            {
                Refuse(epoch, row, match.Kind switch
                {
                    TaskObjectMatchKind.Ambiguous => "fixed-object-terminal-ambiguous",
                    TaskObjectMatchKind.NotSpawned => "fixed-object-terminal-not-spawned",
                    _ => "fixed-object-terminal-unmatched"
                }, report, "the zone spawned " + candidates.Count + " of the " + role.Count + " terminals the level asked for");
                return;
            }
            component = objects![match.NativeIndex];
        }
        else if (role.Scope == FixedTaskObjectScope.Zone)
        {
            var zone = node.m_zone!;
            if (!GeneratorPlacementBuild.Resolve(epoch, zone, role, out var core, out var cause))
            { Refuse(epoch, row, cause, report); return; }
            component = core!;
        }
        else
        {
            var objects = Candidates(row);
            var candidates = new List<TaskObjectCandidate>();
            for (var i = 0; i < objects.Count; i++) candidates.Add(new(i, i, objects[i].GetInstanceID()));
            var match = FixedTaskObjectMatching.Match(candidates, role.Index, role.Count);
            if (match.Kind != TaskObjectMatchKind.Matched)
            { Refuse(epoch, row, match.Kind == TaskObjectMatchKind.Ambiguous ? "fixed-object-ambiguous" : "fixed-object-unmatched", report); return; }
            component = objects[match.NativeIndex];
        }
        if (!Moves(role, component) && Verify(epoch, row, component, claimed, report)) return;
        if (role.Receiver)
        {
            var source = component.TryCast<LG_ComputerTerminal>();
            var receiver = source?.CorruptedUplinkReceiver;
            if (receiver == null || receiver.WasCollected || receiver.CorruptedUplinkReceiver?.Pointer != source!.Pointer)
            { Refuse(epoch, row, "fixed-object-receiver-unmatched", report); return; }
            component = receiver;
        }
        var identity = component.GetInstanceID();
        if (claimed.Contains(identity)) { Refuse(epoch, row, "fixed-object-ambiguous", report); return; }
        FixedItemRules.Apply(component.transform, row);
        var generator = component.TryCast<LG_PowerGenerator_Core>();
        var hsu = component.TryCast<LG_HSU>();
        var terminal = component.TryCast<LG_ComputerTerminal>();
        var item = generator?.m_terminalItem ?? hsu?.m_terminalItem ?? terminal?.m_terminalItem;
        if (item == null || item.WasCollected)
        { Refuse(epoch, row, "fixed-object-terminal-missing", report); return; }
        // Central-cluster Setup assigns the core's SpawnNode before Setup(), but does not assign the
        // core's terminal inventory node. The core owns its generated location; relocation writes both.
        var oldNode = generator != null ? generator.SpawnNode : item.SpawnNode;
        if (oldNode == null || oldNode.WasCollected)
        { Refuse(epoch, row, "fixed-object-generated-node-missing", report); return; }
        // Preserve native zone registration, objective text fragments and generated puzzle space. A candidate
        // generated in another candidate zone cannot silently become this zone's object.
        if (!InZone(oldNode, row.Locator) || item.SpawnNode is { } inventoryNode && !InZone(inventoryNode, row.Locator))
        { Refuse(epoch, row, "fixed-object-generated-zone", report); return; }
        var transform = component.transform;
        var position = new Vector3((float)world.Position[0], (float)world.Position[1], (float)world.Position[2]);
        var rotation = new Quaternion((float)world.Rotation[0], (float)world.Rotation[1], (float)world.Rotation[2], (float)world.Rotation[3]);
        if (transform == null || transform.WasCollected) { Refuse(epoch, row, "fixed-object-transform-missing", report); return; }
        var puzzles = Puzzles(hsu, terminal);
        if (puzzles.Count != 0 && (transform.parent == null || transform.parent.WasCollected))
        { Refuse(epoch, row, "fixed-object-scan-parent-missing", report); return; }
        if (generator != null && !GeneratorObservation.RegisterPlacement(epoch, generator, row))
        { Refuse(epoch, row, "fixed-object-identity-ambiguous", report); return; }
        Record(epoch, row, "before", transform, oldNode, puzzles.Count);
        PreserveScans(transform, puzzles);
        if (terminal != null) oldNode.UnregisterComputerTerminal(terminal);
        // C_BuildCullerNode gathers descendants of this Area, then native clusters their actual bounds.
        transform.SetParent(area.transform, true);
        transform.SetPositionAndRotation(position, rotation);
        item.SpawnNode = node;
        if (generator != null)
        {
            generator.SpawnNode = node;
            generator.m_sound?.UpdatePosition(position);
        }
        if (terminal != null)
        {
            terminal.m_position = position;
            if (terminal.UplinkPuzzle is { } uplink && !uplink.WasCollected) uplink.m_position = position;
            node.RegisterComputerTerminal(terminal);
            terminal.m_sound?.UpdatePosition(position);
        }
        claimed.Add(identity);
        Record(epoch, row, "after", transform, item.SpawnNode, puzzles.Count);
    }

    /// <summary>The cluster was built on its own point at the authored pose (<see cref="FixedFunctionPoints"/>); nothing moves it.
    /// This claims the zone's one cluster, records where it stands, and hides its body when the row says so: every renderer,
    /// collider and light the point built (the whole built object, not only the cluster's own node: base and floor plate
    /// included) that is not part of one of its generators is turned off, so the generators stay and keep working.</summary>
    private static void PlaceCluster(long epoch, FixedItem row, HashSet<int> claimed, Action<string> report)
    {
        LG_PowerGeneratorCluster? found = null;
        var count = 0;
        foreach (var cluster in UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>())
            if (cluster != null && !cluster.WasCollected && InZone(cluster.SpawnNode, row.Locator)) { found = cluster; count++; }
        if (found == null || count != 1)
        { Refuse(epoch, row, count == 0 ? "fixed-object-unmatched" : "fixed-object-ambiguous", report, "the zone built " + count + " generator clusters"); return; }
        var transform = found.transform;
        if (FixedFunctionPoints.Built(epoch, row, out var spawned) is { } reason) { Refuse(epoch, row, reason, report); return; }
        if (!transform.IsChildOf(spawned!.transform)) { Refuse(epoch, row, "fixed-object-identity-mismatch", report); return; }
        Record(epoch, row, "before", transform, found.SpawnNode, 0);
        if (row.HideBody)
        {
            var members = new List<Transform>();
            if (found.m_generators != null)
                foreach (var generator in found.m_generators)
                    if (generator != null && !generator.WasCollected) members.Add(generator.transform);
            bool Member(Transform part)
            {
                foreach (var member in members) if (part.IsChildOf(member)) return true;
                return false;
            }
            var body = spawned!.transform;
            FixedItemRules.HideBody(body, renderer => Member(renderer.transform));
            foreach (var collider in body.GetComponentsInChildren<Collider>(true))
                if (collider != null && !Member(collider.transform)) collider.enabled = false;
            foreach (var light in body.GetComponentsInChildren<Light>(true))
                if (light != null && !Member(light.transform)) light.enabled = false;
        }
        claimed.Add(found.GetInstanceID());
        Record(epoch, row, "after", transform, found.SpawnNode, 0);
    }

    /// <summary>Whether this row's object is moved: no marker request builds it at the authored point.</summary>
    private static bool Moves(FixedTaskObjectRole role, Component component)
    {
        if (role.Kind is FixedTaskObjectKind.UplinkTerminal or FixedTaskObjectKind.DownloadTerminal) return true;
        if (role.Kind != FixedTaskObjectKind.Generator || role.Scope != FixedTaskObjectScope.Objective) return false;
        foreach (var cluster in UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>())
            if (cluster != null && !cluster.WasCollected && component.transform.IsChildOf(cluster.transform)) return true;
        return false;
    }

    /// <summary>A row built at its authored point: its native identity must be the object the builder built there. A row
    /// whose point built nothing (no pinned request, prefab not loaded, point refused or unused) is reported once and
    /// answers false, so the caller moves the native object to the authored point as before; a mismatch is refused.</summary>
    private static bool Verify(long epoch, FixedItem row, Component component, HashSet<int> claimed, Action<string> report)
    {
        if (FixedFunctionPoints.Built(epoch, row, out var spawned) is { } reason)
        {
            // The engine log has no warning level; the one line goes through the refusal channel and names the fallback.
            report("fixed-placement-fallback id=" + row.Id + " code=" + reason + " role=" + row.TaskObject!.Key(row.Locator)
                + ": not built at the authored point; the native object is moved there instead");
            return false;
        }
        if (!component.transform.IsChildOf(spawned!.transform)) { Refuse(epoch, row, "fixed-object-identity-mismatch", report); return true; }
        var identity = component.GetInstanceID();
        if (!claimed.Add(identity)) { Refuse(epoch, row, "fixed-object-ambiguous", report); return true; }
        if (component.TryCast<LG_PowerGenerator_Core>() is { } generator && !GeneratorObservation.RegisterPlacement(epoch, generator, row))
        { claimed.Remove(identity); Refuse(epoch, row, "fixed-object-identity-ambiguous", report); return true; }
        FixedItemRules.Apply(component.transform, row);
        // Built in place: the before and after records are the same pose.
        Record(epoch, row, "before", component.transform, null, 0);
        Record(epoch, row, "after", component.transform, null, 0);
        return true;
    }

    private static List<Component> Candidates(FixedItem row)
    {
        var role = row.TaskObject!;
        var result = new List<Component>();
        if (role.Scope == FixedTaskObjectScope.Door)
        {
            var door = DoorObservation.ByAddress(role.TargetDoor!);
            var generator = door?.m_locks?.TryCast<LG_SecurityDoor_Locks>()?.m_powerGeneratorNeeded;
            if (generator != null && !generator.WasCollected) result.Add(generator);
        }
        else if (role.Scope == FixedTaskObjectScope.Objective)
        {
            var registry = WardenObjectiveManager.GetObjectiveItemCollection((LG_LayerType)role.Layer, role.ChainIndex);
            if (registry == null) return result;
            for (var i = 0; i < registry.Count; i++)
            {
                var item = registry[i];
                if (item == null || item.WasCollected) continue;
                Component? component = role.Kind switch
                {
                    FixedTaskObjectKind.Generator => item.TryCast<LG_PowerGenerator_Core>(),
                    FixedTaskObjectKind.Hsu => item.TryCast<LG_HSU>(),
                    _ => item.TryCast<LG_ComputerTerminal>()
                };
                if (component != null && !component.WasCollected) result.Add(component);
            }
        }
        return result;
    }

    private static bool InZone(AIG_CourseNode? node, RoomLocator locator)
        => node != null && !node.WasCollected && node.m_zone is { } zone && !zone.WasCollected
            && zone.m_layer is { } layer && !layer.WasCollected
            && (int)zone.m_dimensionIndex == locator.Dimension && (int)layer.m_type == locator.Layer
            && (int)zone.LocalIndex == locator.LocalIndex;

    private static List<ChainedPuzzleInstance> Puzzles(LG_HSU? hsu, LG_ComputerTerminal? terminal)
    {
        var puzzles = new List<ChainedPuzzleInstance>();
        void Add(ChainedPuzzleInstance? puzzle)
        {
            if (puzzle == null || puzzle.WasCollected) return;
            foreach (var existing in puzzles) if (existing.Pointer == puzzle.Pointer) return;
            puzzles.Add(puzzle);
        }
        Add(hsu?.m_puzzle);
        Add(terminal?.m_chainPuzzleForWardenObjective);
        Add(terminal?.m_chainPuzzleMidWardenObjective);
        if (terminal?.m_commandToChainPuzzleMap is { } commands)
            foreach (var puzzle in commands.Values) Add(puzzle);
        return puzzles;
    }

    /// <summary>Native Setup allocates cores, replicators and spline geometry. Re-running it is not a relocation
    /// API. Keep the existing cores' world positions and cached course nodes together, under the same level
    /// lifetime. Cluster children are kept by detaching their cluster root, never re-generated individually.</summary>
    private static void PreserveScans(Transform root, IReadOnlyList<ChainedPuzzleInstance> puzzles)
    {
        foreach (var puzzle in puzzles)
        {
            var parent = puzzle.m_parent;
            if (parent != null && !parent.WasCollected && (parent.Pointer == root.Pointer || parent.IsChildOf(root)))
            {
                var anchor = new GameObject("ForgeGeneratedScanOrigin").transform;
                anchor.SetParent(root.parent, false);
                anchor.SetPositionAndRotation(parent.position, parent.rotation);
                puzzle.m_parent = anchor;
            }
            for (var i = 0; i < puzzle.NRofPuzzles(); i++)
            {
                var component = puzzle.GetPuzzle(i)?.TryCast<Component>();
                if (component == null || component.WasCollected) continue;
                var transform = component.transform;
                if (transform.IsChildOf(root)) transform.SetParent(root.parent, true);
            }
        }
    }

    private static void Record(long epoch, FixedItem row, string phase, Transform transform, AIG_CourseNode? node, int scans)
    {
        TaskObjectMoveObservations.Observe(row.Id, transform.gameObject.GetInstanceID(), epoch, phase,
            new[] { transform.position.x, transform.position.y, transform.position.z },
            new[] { transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w });
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedItem, "id=" + row.Id + " phase=" + phase + " kind="
            + FixedTaskObjectRole.Wire(row.TaskObject!.Kind) + " pos=" + FixedPlacementSession.Show(transform.position)
            + " node=" + FixedPlacementSession.Node(node) + " retainedScans=" + scans,
            RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Of("route", "objective"),
            RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("kind", FixedTaskObjectRole.Wire(row.TaskObject.Kind)),
            RuntimeLogValue.Of("role", row.TaskObject.Key(row.Locator)), RuntimeLogValue.Of("retainedScans", (long)scans),
            RuntimeLogValue.Of("x", (double)transform.position.x), RuntimeLogValue.Of("y", (double)transform.position.y),
            RuntimeLogValue.Of("z", (double)transform.position.z), RuntimeLogValue.Of("node", FixedPlacementSession.Node(node)));
    }

    private static void Refuse(long epoch, FixedItem row, string code, Action<string> report, string? detail = null)
    {
        TaskObjectMoveObservations.Observe(row.Id, 0, epoch, "refused", null, null, code);
        report("fixed-placement-refused id=" + row.Id + " code=" + code + " role=" + row.TaskObject!.Key(row.Locator) + (detail == null ? "" : ": " + detail));
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedItem, "Task object placement refused: " + code,
            RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Of("route", "objective"),
            RuntimeLogValue.Of("phase", "refused"), RuntimeLogValue.Of("code", code),
            RuntimeLogValue.Of("role", row.TaskObject.Key(row.Locator)));
    }
}

/// <summary>Bounded production receipts for the authoring capture service. This is the task placement output,
/// not another runtime registry or scheduler.</summary>
internal static class TaskObjectMoveObservations
{
    private const int MaximumRows = 4096;
    private static readonly Dictionary<string, RuntimeMapTaskMove> Rows = new(StringComparer.Ordinal);
    private static long _epoch = long.MinValue;
    private static int _dropped;

    internal static void Clear()
    {
        lock (Rows) { Rows.Clear(); _epoch = long.MinValue; _dropped = 0; }
    }

    internal static void Observe(string id, int instance, long epoch, string phase, float[]? position,
        float[]? rotation, string? refusal = null)
    {
        lock (Rows)
        {
            if (_epoch != epoch) { Rows.Clear(); _epoch = epoch; _dropped = 0; }
            if (!Rows.TryGetValue(id, out var before) && Rows.Count >= MaximumRows) { _dropped++; return; }
            var paired = before?.WorldEpoch == epoch && before.NativeInstanceId == instance && before.Status == "before";
            Rows[id] = new RuntimeMapTaskMove(id, instance, epoch,
                phase == "before" ? Clone(position) : paired ? Clone(before!.BeforePosition) : null,
                phase == "after" ? Clone(position) : null, phase == "after" ? Clone(rotation) : null,
                phase == "after" ? paired ? "applied" : "unpaired" : phase, refusal);
        }
    }

    internal static RuntimeMapTaskMoveSnapshot Snapshot(long epoch)
    {
        lock (Rows)
        {
            if (_epoch != epoch) return new(epoch, Array.Empty<RuntimeMapTaskMove>(), 0);
            var rows = new List<RuntimeMapTaskMove>(Rows.Count);
            foreach (var row in Rows.Values) rows.Add(row with
            {
                BeforePosition = Clone(row.BeforePosition),
                AfterPosition = Clone(row.AfterPosition),
                AfterRotation = Clone(row.AfterRotation)
            });
            rows.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            return new(epoch, rows.AsReadOnly(), _dropped);
        }
    }

    private static float[]? Clone(float[]? values) => values == null ? null : (float[])values.Clone();
}
