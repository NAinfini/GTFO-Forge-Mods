using System;

namespace ForgePerformance.LoadShedding;

// The culling-node graph of one level with fixed buffers: built once after the level build, then Compute runs only
// when the camera's node or a door changes, without allocating. No game types, so it runs offline.
//
// Compute marks which portals to close for one visibility pass so that what is seen through a closed window door
// stops after `limit` nodes. Depth 0 is every node the camera reaches through open portals without passing a
// closed window door; such nodes are never limited, so a room that another open route shows stays exactly vanilla.
// Behind a closed window door the first node has depth 1, and depth grows by one per open portal (window doors
// included). The portals between depth `limit` and depth `limit + 1` are closed: the game's propagation can only
// reach a deeper node through one of them, because along any path the depth rises by at most one per portal.
internal sealed class DoorDepthGraph
{
    internal readonly int NodeCount;
    internal readonly int EdgeCount;
    private readonly int[] _edgeA, _edgeB, _adjacencyStart, _adjacency, _depth, _queue, _close;

    internal DoorDepthGraph(int nodeCount, int[] edgeA, int[] edgeB)
    {
        if (edgeA is null) throw new ArgumentNullException(nameof(edgeA));
        if (edgeB is null) throw new ArgumentNullException(nameof(edgeB));
        if (nodeCount < 0 || edgeA.Length != edgeB.Length) throw new ArgumentException("Edge arrays differ in length.");
        NodeCount = nodeCount; EdgeCount = edgeA.Length;
        _edgeA = edgeA; _edgeB = edgeB;
        _adjacencyStart = new int[nodeCount + 1];
        for (var e = 0; e < EdgeCount; e++)
        {
            if ((uint)edgeA[e] >= (uint)nodeCount || (uint)edgeB[e] >= (uint)nodeCount)
                throw new ArgumentOutOfRangeException(nameof(edgeA), "Edge " + e + " names a node outside the graph.");
            _adjacencyStart[edgeA[e] + 1]++; _adjacencyStart[edgeB[e] + 1]++;
        }
        for (var n = 0; n < nodeCount; n++) _adjacencyStart[n + 1] += _adjacencyStart[n];
        _adjacency = new int[EdgeCount * 2];
        var fill = new int[nodeCount];
        for (var e = 0; e < EdgeCount; e++)
        {
            _adjacency[_adjacencyStart[edgeA[e]] + fill[edgeA[e]]++] = e;
            _adjacency[_adjacencyStart[edgeB[e]] + fill[edgeB[e]]++] = e;
        }
        _depth = new int[nodeCount];
        _queue = new int[nodeCount];
        _close = new int[EdgeCount];
    }

    internal int CloseCount { get; private set; }
    internal int Closed(int index) => _close[index];
    internal int Depth(int node) => _depth[node];

    // open[e]: the game would traverse portal e now. window[e]: a closed window door sits in portal e.
    // camera < 0 (the camera's node is not part of the graph) closes nothing.
    internal void Compute(int camera, bool[] open, bool[] window, int limit)
    {
        if (open is null) throw new ArgumentNullException(nameof(open));
        if (window is null) throw new ArgumentNullException(nameof(window));
        if (open.Length < EdgeCount || window.Length < EdgeCount) throw new ArgumentException("Edge state arrays are too short.");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        CloseCount = 0;
        Array.Fill(_depth, -1);
        if ((uint)camera >= (uint)NodeCount) return;

        // Depth 0: open portals, no closed window door.
        int head = 0, tail = 0;
        _depth[camera] = 0; _queue[tail++] = camera;
        while (head < tail)
        {
            var node = _queue[head++];
            for (var i = _adjacencyStart[node]; i < _adjacencyStart[node + 1]; i++)
            {
                var e = _adjacency[i];
                if (!open[e] || window[e]) continue;
                var other = _edgeA[e] == node ? _edgeB[e] : _edgeA[e];
                if (_depth[other] >= 0) continue;
                _depth[other] = 0; _queue[tail++] = other;
            }
        }
        // Depth 1: the far side of each closed window door seen from depth 0.
        var reached = tail;
        head = tail = 0;
        for (var r = 0; r < reached; r++)
        {
            var node = _queue[r];
            for (var i = _adjacencyStart[node]; i < _adjacencyStart[node + 1]; i++)
            {
                var e = _adjacency[i];
                if (!open[e] || !window[e]) continue;
                var other = _edgeA[e] == node ? _edgeB[e] : _edgeA[e];
                if (_depth[other] >= 0) continue;
                _depth[other] = 1;
                _queue[reached + tail++] = other;
            }
        }
        // Deeper: any open portal, one more per portal, up to limit + 1.
        head = reached; tail += reached;
        while (head < tail)
        {
            var node = _queue[head++];
            var next = _depth[node] + 1;
            if (next > limit + 1) continue;
            for (var i = _adjacencyStart[node]; i < _adjacencyStart[node + 1]; i++)
            {
                var e = _adjacency[i];
                if (!open[e]) continue;
                var other = _edgeA[e] == node ? _edgeB[e] : _edgeA[e];
                if (_depth[other] >= 0) continue;
                _depth[other] = next; _queue[tail++] = other;
            }
        }
        for (var e = 0; e < EdgeCount; e++)
        {
            if (!open[e]) continue;
            int a = _depth[_edgeA[e]], b = _depth[_edgeB[e]];
            if (a == limit && b == limit + 1 || b == limit && a == limit + 1) _close[CloseCount++] = e;
        }
    }
}
