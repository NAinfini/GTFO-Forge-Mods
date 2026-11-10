using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AIGraph;
using CullingSystem;
using Il2CppInterop.Runtime;
using LevelGeneration;

namespace ForgePerformance.LoadShedding;

// ClosedDoorDepthLimit. The game's propagation (C_Camera.PropagateFrustra, a breadth-first walk over the C_Portals
// whose IsOpen is true, started only by FPSCamera.OnPreCull through C_Camera.RunVisibilityOnPreCull) never treats a
// closed door as closed: IsOpen is written once, to true, in C_Portal.Setup. Weak doors have gaps and security, Apex
// and bulkhead doors a window, so the room right behind a closed door stays drawn as in vanilla; what this cuts is
// everything after the first ClosedDoorDepth rooms behind it. IsOpen is global, so the portals DoorDepthGraph picks
// are closed in the prefix of the visibility call and opened again in its finalizer; nothing else ever sees them
// closed. Every node the camera reaches without passing a closed door stays vanilla.
//
// Door state comes from the game's own events, no patch: the door sync's state changes and the weak door
// animation's closed event. A door counts as closed while it covers the doorway; Opening, Open, Destroyed or any
// damage ends that in the state callback, before the next visibility pass, and a damaged or destroyed door is left
// vanilla for the rest of the level. Closing after an open waits for the closing animation to end; a joined or
// restored state and the first state of a door that was never opened count at once.
//
// Per frame: two pointer reads and a compare, then one byte write and one restore per selected portal. The graph and
// all buffers are built once after the level build; the selection is recomputed only when the camera's culling node
// or a door changes, without allocating.
internal sealed class ClosedDoorDepth
{
    // C_Portal.<IsOpen>k__BackingField, C_Camera.m_cullAgent and C_MovingCuller.CurrentNode, checked by
    // SheddingHooks.VerifyLayout before anything is read or written.
    internal const int PortalIsOpen = 0x128;
    internal const int CameraCullAgent = 0x18;
    internal const int CullerCurrentNode = 0x30;

    private readonly int _depth;
    private readonly List<Door> _doors = new();
    private int _version;

    private IntPtr _cameraField;
    private IntPtr[] _nodePointers = Array.Empty<IntPtr>();
    private IntPtr[] _edgePointers = Array.Empty<IntPtr>();
    private int[] _edgeDoor = Array.Empty<int>();
    private bool[] _open = Array.Empty<bool>(), _closedDoor = Array.Empty<bool>();
    private IntPtr[] _flipped = Array.Empty<IntPtr>();
    private DoorDepthGraph? _graph;
    private IntPtr _lastNode;
    private int _lastVersion = -1, _flippedCount;
    private bool _failed;
    private long _noSync, _shared, _released, _recomputes, _framesLimited, _closesTotal, _maximumClosed, _failures;

    internal ClosedDoorDepth(int depth) => _depth = depth;

    internal (long Doors, long NoSync, long Shared, long Released, long Nodes, long Portals, long Recomputes,
        long FramesLimited, long ClosesTotal, long MaximumClosed, long Failures) Counts
        => (_doors.Count, _noSync, _shared, _released, _nodePointers.Length, _edgePointers.Length, _recomputes,
            _framesLimited, _closesTotal, _maximumClosed, _failures);

    internal void BuildDone()
    {
        EndLevel(); // A second build of the same level starts over.
        var courseNodes = AIG_CourseNode.s_allNodes;
        if (courseNodes == null) return;
        var visited = new HashSet<IntPtr>();
        var portalDoor = new Dictionary<IntPtr, int>();
        var doorIndex = new Dictionary<IntPtr, int>();
        var shared = new HashSet<IntPtr>();
        for (var n = 0; n < courseNodes.Count; n++)
        {
            var portals = courseNodes[n]?.m_portals;
            if (portals == null) continue;
            for (var p = 0; p < portals.Count; p++)
            {
                var course = portals[p];
                if (course == null || !visited.Add(course.Pointer)) continue;
                var core = course.m_door;
                var cull = course.m_cullPortal;
                if (core == null || cull == null) continue;
                if (!doorIndex.TryGetValue(core.Pointer, out var index))
                {
                    index = Track(core);
                    doorIndex.Add(core.Pointer, index);
                }
                if (index < 0) continue;
                // One culling portal named by two doors cannot follow either: it stays vanilla.
                if (portalDoor.TryGetValue(cull.Pointer, out var owner) && owner != index) { shared.Add(cull.Pointer); _shared++; }
                portalDoor[cull.Pointer] = index;
            }
        }
        foreach (var pointer in shared) portalDoor.Remove(pointer);
        if (_doors.Count != 0) BuildGraph(courseNodes, portalDoor);
    }

    private int Track(iLG_Door_Core core)
    {
        var weak = core.TryCast<LG_WeakDoor>();
        var security = weak == null ? core.TryCast<LG_SecurityDoor>() : null;
        var component = weak != null ? weak.m_syncComp : security?.m_syncComp;
        var sync = component == null ? null : component.TryCast<LG_Door_Sync>();
        // Without the door's own state events an opened door could still count as closed: leave it vanilla.
        if (sync == null) { _noSync++; return -1; }
        var anim = weak?.m_animComp?.TryCast<LG_WeakDoor_Anim>();
        var door = new Door(sync, anim);
        door.OnState = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<pDoorState, bool>>(
            (Action<pDoorState, bool>)((state, dropIn) => Changed(door, state, dropIn)));
        if (anim != null)
            door.OnClosed = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => AnimationClosed(door)));
        _doors.Add(door);
        sync.add_OnDoorStateChange(door.OnState);
        if (anim != null) anim.add_OnDoorIsClosed(door.OnClosed);
        var current = sync.GetCurrentSyncState();
        var status = (byte)current.status;
        door.Status = status;
        Apply(door, DoorCullingPolicy.OnState(status, status, current.damageTaken > 0f, true));
        return _doors.Count - 1;
    }

    private void Changed(Door door, pDoorState state, bool dropIn)
    {
        try
        {
            if (door.Released) return;
            var status = (byte)state.status;
            var decision = DoorCullingPolicy.OnState(door.Status, status, state.damageTaken > 0f, dropIn);
            door.Status = status;
            Apply(door, decision);
        }
        catch (Exception error) { Fail(error); }
    }

    private void AnimationClosed(Door door)
    {
        try
        {
            if (door.Released || !DoorCullingPolicy.Covers(door.Status) || door.Covering) return;
            door.Covering = true; _version++;
        }
        catch (Exception error) { Fail(error); }
    }

    private void Apply(Door door, DoorCullingPolicy.Portal decision)
    {
        var covering = decision == DoorCullingPolicy.Portal.Close;
        if (decision == DoorCullingPolicy.Portal.Release) { door.Released = true; _released++; }
        // CloseWhenAnimationEnds keeps the door counted as open until the closed event.
        if (door.Covering == covering && decision != DoorCullingPolicy.Portal.Release) return;
        door.Covering = covering; _version++;
    }

    // Cold: the culling nodes of the level and every portal between them, found from the course nodes and followed
    // through the portals' own endpoints, so a node no course node names is still part of the graph.
    private void BuildGraph(Il2CppSystem.Collections.Generic.List<AIG_CourseNode> courseNodes, Dictionary<IntPtr, int> portalDoor)
    {
        var index = new Dictionary<IntPtr, int>();
        var nodes = new List<C_Node>();
        var pending = new Queue<C_Node>();
        void Add(C_Node? node)
        {
            if (node == null || index.ContainsKey(node.Pointer)) return;
            index.Add(node.Pointer, nodes.Count); nodes.Add(node); pending.Enqueue(node);
        }
        for (var n = 0; n < courseNodes.Count; n++) Add(courseNodes[n]?.m_cullNode);
        var portals = new List<C_Portal>();
        var seen = new HashSet<IntPtr>();
        while (pending.Count != 0)
        {
            var list = pending.Dequeue().m_portals;
            if (list == null) continue;
            for (var p = 0; p < list.Count; p++)
            {
                var portal = list[p];
                if (portal == null || !seen.Add(portal.Pointer)) continue;
                Add(portal.m_backwardsNode); Add(portal.m_forwardsNode);
                if (portal.m_backwardsNode != null && portal.m_forwardsNode != null) portals.Add(portal);
            }
        }
        var a = new int[portals.Count];
        var b = new int[portals.Count];
        _edgePointers = new IntPtr[portals.Count];
        _edgeDoor = new int[portals.Count];
        for (var e = 0; e < portals.Count; e++)
        {
            a[e] = index[portals[e].m_backwardsNode.Pointer]; b[e] = index[portals[e].m_forwardsNode.Pointer];
            _edgePointers[e] = portals[e].Pointer;
            _edgeDoor[e] = portalDoor.TryGetValue(portals[e].Pointer, out var door) ? door : -1;
        }
        _nodePointers = new IntPtr[nodes.Count];
        for (var n = 0; n < nodes.Count; n++) _nodePointers[n] = nodes[n].Pointer;
        _open = new bool[portals.Count]; _closedDoor = new bool[portals.Count]; _flipped = new IntPtr[portals.Count];
        _graph = new DoorDepthGraph(nodes.Count, a, b);
        _cameraField = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<C_Camera>.NativeClassPtr, "Current");
        if (_cameraField == IntPtr.Zero) throw new MissingFieldException("C_Camera", "Current");
        _lastNode = IntPtr.Zero; _lastVersion = -1;
    }

    // Prefix of C_Camera.RunVisibilityOnPreCull. No allocation, no lookup by name or key.
    internal unsafe void BeforeVisibility()
    {
        if (_graph == null || _failed) return;
        try
        {
            var camera = IntPtr.Zero;
            IL2CPP.il2cpp_field_static_get_value(_cameraField, &camera);
            if (camera == IntPtr.Zero) return;
            var agent = Marshal.ReadIntPtr(camera, CameraCullAgent);
            if (agent == IntPtr.Zero) return;
            var node = Marshal.ReadIntPtr(agent, CullerCurrentNode);
            if (node != _lastNode || _version != _lastVersion) Recompute(node);
            for (var i = 0; i < _graph.CloseCount; i++)
            {
                var pointer = _edgePointers[_graph.Closed(i)];
                if (Marshal.ReadByte(pointer, PortalIsOpen) != 1) continue;
                Marshal.WriteByte(pointer, PortalIsOpen, 0);
                _flipped[_flippedCount++] = pointer;
            }
            if (_flippedCount != 0)
            {
                _framesLimited++; _closesTotal += _flippedCount;
                if (_flippedCount > _maximumClosed) _maximumClosed = _flippedCount;
            }
        }
        catch (Exception error) { AfterVisibility(); Fail(error); }
    }

    // Finalizer of the same call: every portal closed for this pass is open again before anything else runs.
    internal void AfterVisibility()
    {
        for (var i = 0; i < _flippedCount; i++)
            if (Marshal.ReadByte(_flipped[i], PortalIsOpen) == 0) Marshal.WriteByte(_flipped[i], PortalIsOpen, 1);
        _flippedCount = 0;
    }

    private void Recompute(IntPtr node)
    {
        _lastNode = node; _lastVersion = _version; _recomputes++;
        // Only when the camera changed node: a linear scan of the node array, no dictionary on the frame path.
        var camera = Array.IndexOf(_nodePointers, node);
        for (var e = 0; e < _edgePointers.Length; e++)
        {
            // Read with nothing closed: the previous pass was restored by its finalizer.
            _open[e] = Marshal.ReadByte(_edgePointers[e], PortalIsOpen) == 1;
            var door = _edgeDoor[e];
            _closedDoor[e] = door >= 0 && _doors[door].Covering;
        }
        _graph!.Compute(camera, _open, _closedDoor, _depth);
    }

    // Any failure leaves the level vanilla: nothing is closed again until the next level.
    private void Fail(Exception error)
    {
        _failed = true;
        if (_failures++ == 0)
            LoadSheddingController.Current?.LogExtraFailure("closed-door-depth", error.GetType().Name + ": " + error.Message);
    }

    // Restores every portal, leaves the game's door events and drops the graph. Runs when the level ends.
    internal void EndLevel()
    {
        Exception? first = null;
        try { AfterVisibility(); } catch (Exception error) { first ??= error; }
        foreach (var door in _doors)
        {
            try { if (door.Sync != null && door.OnState != null) door.Sync.remove_OnDoorStateChange(door.OnState); }
            catch (Exception error) { first ??= error; }
            try { if (door.Anim != null && door.OnClosed != null) door.Anim.remove_OnDoorIsClosed(door.OnClosed); }
            catch (Exception error) { first ??= error; }
        }
        _doors.Clear();
        _graph = null; _failed = false;
        _nodePointers = Array.Empty<IntPtr>(); _edgePointers = Array.Empty<IntPtr>();
        _edgeDoor = Array.Empty<int>(); _open = _closedDoor = Array.Empty<bool>(); _flipped = Array.Empty<IntPtr>();
        _noSync = _shared = _released = _recomputes = _framesLimited = _closesTotal = _maximumClosed = _failures = 0;
        if (first != null) throw first;
    }

    private sealed class Door
    {
        internal readonly LG_Door_Sync Sync;
        internal readonly LG_WeakDoor_Anim? Anim;
        internal Il2CppSystem.Action<pDoorState, bool>? OnState;
        internal Il2CppSystem.Action? OnClosed;
        internal byte Status;
        internal bool Covering, Released;
        internal Door(LG_Door_Sync sync, LG_WeakDoor_Anim? anim) { Sync = sync; Anim = anim; }
    }
}
