using AIGraph;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore.Tests;

/// <summary>The position-to-course-node search (<c>Level.CourseNodeAt</c>) and the whole-array copies (<c>Il2CppArrays</c>).</summary>
internal static class LevelChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Globals.Global.RundownIdToLoad = 31; RundownManager.Active = new pActiveExpedition { tier = eRundownTier.TierC, expeditionIndex = 4 };
        check(Level.TryActiveExpedition(out var rundown, out var tier, out var index) && rundown == 31 && tier == 3 && index == 4,
            "level: the active expedition is the loaded rundown with the game's own tier value and zero-based index");
        RundownManager.ThrowOnRead = true;
        check(!Level.TryActiveExpedition(out rundown, out tier, out index) && rundown == 0 && tier == 0 && index == 0,
            "level: an expedition the game cannot read is no expedition, with empty outputs");
        RundownManager.ThrowOnRead = false;
        var nodes = AIG_CourseNode.Nodes;
        nodes.Clear(); AIG_CourseNode.Asked.Clear();
        Level.SetDimensions(0);
        check(!Level.CourseNodeAt(default, 1f, out var none, out var noDimension, preferredDimension: 0) && none == null && noDimension == -1 && AIG_CourseNode.Asked.Count == 0,
            "level: with no level built nothing is asked");
        Level.SetDimensions(0b0101);
        check(!Level.CourseNodeAt(default, 1f, out none, out noDimension, preferredDimension: 1) && noDimension == -1
            && AIG_CourseNode.Asked.Count == 2 && AIG_CourseNode.Asked.Contains(eDimensionIndex.Reality) && AIG_CourseNode.Asked.Contains(eDimensionIndex.Dimension2),
            "level: only the dimensions the level has are asked, a preferred one it lacks included");
        check(Level.HasDimension(2) && !Level.HasDimension(1) && !Level.HasDimension(-1) && !Level.HasDimension(64), "level: the dimensions the level has");
        Level.SetDimensions((1UL << (int)eDimensionIndex.MAX_COUNT) - 1);
        AIG_CourseNode.Asked.Clear();
        check(!Level.CourseNodeAt(default, 1f, out none, out noDimension) && none == null && noDimension == -1
            && AIG_CourseNode.Asked.Count == (int)eDimensionIndex.MAX_COUNT, "level: a point off every mesh is not found, after every dimension of the level was asked");

        var a = new AIG_CourseNode(); var b = new AIG_CourseNode();
        nodes[eDimensionIndex.Dimension1] = a; nodes[eDimensionIndex.Dimension3] = b;
        check(Level.CourseNodeAt(default, 1f, out var found, out var dimension) && found == a && dimension == 1, "level: the lowest dimension that has a node answers");
        AIG_CourseNode.Asked.Clear();
        check(Level.CourseNodeAt(default, 1f, out found, out dimension, preferredDimension: 3) && found == b && dimension == 3 && AIG_CourseNode.Asked.Count == 1,
            "level: the preferred dimension is asked first and ends the search when it answers");
        AIG_CourseNode.Asked.Clear();
        check(Level.CourseNodeAt(default, 1f, out found, out dimension, preferredDimension: 2) && found == a && dimension == 1
            && AIG_CourseNode.Asked.IndexOf(eDimensionIndex.Dimension2) == 0 && AIG_CourseNode.Asked.Count(d => d == eDimensionIndex.Dimension2) == 1,
            "level: a preferred dimension without a node falls back to the others, and is not asked twice");
        check(Level.CourseNodeAt(default, 1f, out found, out dimension, preferredDimension: 40) && dimension == 1, "level: a preferred dimension out of range is ignored");
        a.WasCollected = true;
        check(Level.CourseNodeAt(default, 1f, out found, out dimension) && found == b && dimension == 3, "level: a collected node is skipped");
        nodes.Clear();

        StandSpots.Offset(0, 0f, 2f, true, out var bx, out var bz);
        check(Math.Abs(bx) < 1e-4f && Math.Abs(bz + 2f) < 1e-4f, "stand spots: the first place is straight behind, one clearance away");
        StandSpots.Offset(0, 90f, 2f, true, out bx, out bz);
        check(Math.Abs(bx + 2f) < 1e-4f && Math.Abs(bz) < 1e-4f, "stand spots: facing +x (yaw 90) behind is -x");
        StandSpots.Offset(0, 0f, 2f, false, out bx, out bz);
        check(Math.Abs(bz - 2f) < 1e-4f, "stand spots: preferring the front mirrors the order");
        StandSpots.Offset(StandSpots.Count - 1, 0f, 1f, true, out bx, out bz);
        check(StandSpots.Count == 7 && Math.Abs(bz - 1.2f) < 1e-4f, "stand spots: the last place is in front, 1.2 clearances away");

        var triangles = new[] { 3, 1, 4, 1, 5, 9 };
        var game = Il2CppArrays.ToGame(triangles);
        check(game.Length == 6 && game.Items.ToArray().SequenceEqual(triangles), "arrays: a plain array is copied into a game array whole");
        check(Il2CppArrays.ToManaged(game).SequenceEqual(triangles) && !ReferenceEquals(Il2CppArrays.ToManaged(game), triangles), "arrays: a game array is copied out as a new plain array");
        check(Il2CppArrays.ToManaged(Il2CppArrays.ToGame(Array.Empty<int>())).Length == 0, "arrays: an empty array copies as empty");

        var pixels = new[] { new Color32 { r = 1, g = 2, b = 3, a = 4 }, new Color32 { r = 5, g = 6, b = 7, a = 8 } };
        var texture = new Texture2D();
        Il2CppArrays.Upload(texture, pixels);
        check(texture.Raw.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "arrays: pixels are uploaded as RGBA bytes in order");

        nodes.Clear(); AIG_CourseNode.Asked.Clear();
        nodes[eDimensionIndex.Dimension1] = new AIG_CourseNode();
        check(Level.CourseNodeIn(1, default, 1f, out var inside) && inside != null && AIG_CourseNode.Asked.Count == 1
            && !Level.CourseNodeIn(2, default, 1f, out var elsewhere) && elsewhere == null && AIG_CourseNode.Asked.Count == 2,
            "level: a node in a named dimension asks only that dimension");
        nodes[eDimensionIndex.Dimension1]!.WasCollected = true;
        check(!Level.CourseNodeIn(1, default, 1f, out var collected) && collected == null, "level: a collected node is not a node");

        Physics.FloorCollider = null;
        check(!Level.FloorBelow(default, 4f, out _) && Physics.LastRay == (4f, LayerManager.MASK_WORLD), "level: no floor below is false, asked against the world layers");
        Physics.FloorCollider = new Collider { isTrigger = true };
        check(!Level.FloorBelow(default, 4f, out _), "level: a trigger volume is not a floor");
        Physics.FloorCollider = new Collider();
        check(Level.FloorBelow(default, 4f, out var floorHit) && floorHit.collider == Physics.FloorCollider, "level: a solid collider below is the floor");
        check(Level.GroundRay(default, .4f, 7, out _) && Physics.LastRay == (.4f, 7), "level: the plain ground ray takes the caller's mask");
        Physics.FloorCollider = null;
    }
}
