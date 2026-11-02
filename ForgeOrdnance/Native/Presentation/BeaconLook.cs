using System;
using System.Collections.Generic;
using ForgeVisual;
using HarmonyLib;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>
/// The summoning ball's own look (M01, prefab <c>beacon</c>) on every instance of the ball item, on every peer: the first
/// person item in the hand, the third person item other players see, and the thrown world item until it is gone. The glow
/// stick underneath stays: the game's throwing weapon, replication, physics and light all work as before; only its meshes are
/// hidden and M01 sits where the stick was, following the same transforms. The thrown stick's light turns amber to match the
/// ring. A mesh is hidden by scaling its (childless) transform to zero, so the game can still switch its renderer on and off
/// and M01 follows that switch.
/// </summary>
internal static class BeaconLook
{
    /// <summary>M01's height as modelled (metres, origin at its centre) and the height it has in the world. Third person and thrown
    /// copies are a compact 11 cm. In the first person hand the ball is 7.2 cm and sits on the stick's upper end, its centre
    /// <see cref="HandHold"/> radii under the tip, so it shows outside the fist.</summary>
    internal const float ModelHeight = 0.0817f;
    internal const float WorldHeight = 0.11f;
    internal const float HandWorldHeight = 0.072f;
    private const float HandHold = 0.5f;
    /// <summary>The hand bones are parented a few frames after the spawn; look for them this long, then report.</summary>
    private const float GripSeconds = 4f;
    private const float RescanSeconds = 1.5f;
    private static readonly Color Amber = new(1f, 0.58f, 0.12f, 1f);
    /// <summary>The amber ribbon a thrown ball drags behind it: 20 world-space points, one every 1/60 s (about a third of a second of flight),
    /// fading from the ball to nothing. It follows the world item the game replicates, so every peer draws its own from what it already sees,
    /// and it collapses to nothing a moment after the ball stops.</summary>
    private static readonly TrailStyle Ribbon = new()
    {
        RibbonPoints = 20, RibbonStep = 1f / 60f, RibbonWidth = 0.09f, RibbonBlend = FxBlend.Alpha,
        RibbonHead = new Color(1f, 0.7f, 0.25f, 0.9f), RibbonTail = new Color(1f, 0.5f, 0.1f, 0f),
    };
    private static readonly List<Entry> Entries = new();

    private sealed class Entry
    {
        internal Item Item = null!;
        internal ItemMode Mode;
        internal Renderer? Source;
        internal MeshRenderer[] Ours = Array.Empty<MeshRenderer>();
        internal readonly List<IntPtr> Handled = new();
        internal float RescanUntil;
        internal float NextScan;
        /// <summary>The thrown ball (world item) drags an amber ribbon; the held copies do not.</summary>
        internal Trail? Trail;
        /// <summary>First person only: set until the hand's bones have been found and the ball sits on the real grip point.</summary>
        internal Grip? Grip;
    }

    private sealed class Grip
    {
        internal Transform Holder = null!, Look = null!;
        internal Vector3 Axis, Center;
        /// <summary>Stick length and the ball's height, in the holder's units; the ball's half height in world metres.</summary>
        internal float Length, Along, Radius;
        internal float Until;
    }

    /// <summary>Every peer, from the game's item spawn: dresses one ball instance.</summary>
    internal static void Apply(Item item, ItemMode mode)
    {
        var model = OrdnanceAssets.Beacon;
        if (model == null)
        {
            return;
        }
        var entry = new Entry { Item = item, Mode = mode, RescanUntil = Time.time + RescanSeconds };
        Entries.Add(entry);
        if (mode == ItemMode.Instance) entry.Trail = Trails.Rent(item.transform.position, Ribbon);
        Dress(entry, model);
    }

    private static void Dress(Entry entry, ModelHandle model)
    {
        Renderer? anchor = null;
        var anchorSize = 0f;
        var anchorScale = Vector3.one;
        var hidden = false;
        foreach (var renderer in entry.Item.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || entry.Handled.Contains(renderer.Pointer)) continue;
            var mesh = MeshOf(renderer);
            if (mesh == null || renderer.name.StartsWith("ForgeOrdnance", StringComparison.Ordinal) || UnderLook(renderer.transform)) continue;
            entry.Handled.Add(renderer.Pointer);
            var size = mesh.bounds.size;
            var longest = Math.Max(size.x, Math.Max(size.y, size.z));
            if (longest > anchorSize) { anchorSize = longest; anchor = renderer; anchorScale = renderer.transform.localScale; }
            Hide(renderer);
            hidden = true;
        }
        if (!hidden) return;
        if (entry.Source == null && anchor != null)
        {
            entry.Source = anchor;
            entry.Ours = Mount(anchor, anchorScale, model, entry.Mode, out entry.Grip);
        }
        if (entry.Mode == ItemMode.Instance)
        {
            var stick = entry.Item.TryCast<GlowstickInstance>();
            if (stick != null) stick.m_LightColorTarget = Amber;
        }
    }

    private static Mesh? MeshOf(Renderer renderer)
    {
        var skinned = renderer.TryCast<SkinnedMeshRenderer>();
        if (skinned != null) return skinned.sharedMesh;
        if (renderer.TryCast<MeshRenderer>() == null) return null;
        var filter = renderer.GetComponent<MeshFilter>();
        return filter == null ? null : filter.sharedMesh;
    }

    private static bool UnderLook(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
            if (p.name == "ForgeOrdnance.BeaconLook") return true;
        return false;
    }

    private static void Hide(Renderer renderer)
    {
        var t = renderer.transform;
        if (t.childCount == 0) t.localScale = Vector3.zero;
        else renderer.enabled = false;
    }

    /// <summary>M01 in the anchor mesh's frame: its up axis along the stick's long axis (the end that points up now), centred on
    /// the stick's centre, at its real world size whatever scale the stick's hierarchy has.</summary>
    private static MeshRenderer[] Mount(Renderer anchor, Vector3 anchorScale, ModelHandle model, ItemMode mode, out Grip? grip)
    {
        grip = null;
        var mesh = MeshOf(anchor)!;
        var t = anchor.transform;
        var holder = new GameObject("ForgeOrdnance.BeaconLook");
        var parent = t.parent;
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = t.localPosition;
        holder.transform.localRotation = t.localRotation;
        // The anchor's own scale was zeroed to hide it; the holder takes its original scale so mesh units match.
        holder.transform.localScale = anchorScale;
        var bounds = mesh.bounds;
        var size = bounds.size;
        var axis = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
        var length = Vector3.Dot(size, axis);
        if (holder.transform.TransformDirection(axis).y < 0f) axis = -axis;
        var hand = mode == ItemMode.FirstPerson;
        var worldHeight = hand ? HandWorldHeight : WorldHeight;
        // The ball's own glow stick meshes are hidden by scale above (the game keeps switching their renderer), so nothing is hidden here.
        var dressed = Models.Dress(holder, model, new DressOptions { HideNative = false, Layer = false, Name = "ForgeOrdnance.BeaconModel" });
        if (dressed == null)
        {
            Object.Destroy(holder);
            return Array.Empty<MeshRenderer>();
        }
        var look = dressed.Look;
        look.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        var lossy = holder.transform.lossyScale;
        var along = Math.Abs(Vector3.Dot(lossy, axis));
        var scale = along > 1e-4f ? worldHeight / ModelHeight / along : 1f;
        look.transform.localScale = Vector3.one * scale;
        // In the hand the stick's centre is inside the fist: the ball sits on the stick's upper end, its centre HandHold radii under
        // the tip, so it shows outside the fingers (SettleGrip only logs where it ended up).
        var radius = worldHeight * 0.5f;
        var lift = hand && along > 1e-4f ? Math.Max(0f, length * 0.5f - HandHold * radius / along) : 0f;
        look.transform.localPosition = bounds.center + axis * lift;
        if (hand && along > 1e-4f)
            grip = new Grip { Holder = holder.transform, Look = look.transform, Axis = axis, Center = bounds.center, Length = length, Along = along, Radius = radius, Until = Time.time + GripSeconds };
        Models.SetLayer(holder.transform, anchor.gameObject.layer);
        var ours = look.GetComponentsInChildren<MeshRenderer>(true);
        foreach (var r in ours) r.enabled = anchor.enabled;
        return ours;
    }

    /// <summary>Every frame: M01 shows exactly when the game shows the stick; meshes the game adds shortly after spawn are hidden too.</summary>
    internal static void Tick()
    {
        if (Entries.Count == 0) return;
        var now = Time.time;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Item == null || e.Item.WasCollected) { e.Trail?.End(); Entries.RemoveAt(i); continue; }
            e.Trail?.Move(e.Item.transform.position, 0f);
            if (now < e.RescanUntil && now >= e.NextScan && OrdnanceAssets.Beacon != null)
            {
                e.NextScan = now + 0.25f;
                Dress(e, OrdnanceAssets.Beacon);
            }
            if (e.Grip != null) SettleGrip(e, now);
            if (e.Source == null || e.Source.WasCollected) continue;
            var shown = e.Source.enabled;
            foreach (var r in e.Ours)
                if (r != null && !r.WasCollected && r.enabled != shown) r.enabled = shown;
        }
    }

    private static string Chain(Transform item)
    {
        var names = new List<string>();
        for (var p = item; p != null && names.Count < 16; p = p.parent) names.Add(p.name);
        return string.Join(" < ", names);
    }

    private static bool IsFinger(string n) => n.Contains("finger") || n.Contains("thumb") || n.Contains("index") || n.Contains("middle") || n.Contains("pinky");

    /// <summary>Bones of the first person arm (names with hand, palm, wrist, finger, thumb, index, middle or pinky) under the item's
    /// root (the camera holder) and under the local player; the arm skeleton is not an ancestor of the item.</summary>
    private static List<Transform> FindBones(Transform item)
    {
        var roots = new List<Transform> { item.root };
        var player = PlayerManager.GetLocalPlayerAgent();
        if (player != null && player.transform.root.Pointer != item.root.Pointer) roots.Add(player.transform.root);
        var found = new List<Transform>();
        var stack = new Stack<Transform>(roots);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            var n = t.name.ToLowerInvariant();
            if (IsFinger(n) || (n.Contains("hand") && !n.Contains("handle")) || n.Contains("palm") || n.Contains("wrist")) found.Add(t);
            for (var i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
        }
        return found;
    }

    /// <summary>First person: the ball already sits on the stick's upper end (centre half a radius under the tip), where it shows
    /// outside the fingers. Once the arm's bones are found, log their names and how far the ball centre is from the nearest finger
    /// bone and from the camera.</summary>
    private static void SettleGrip(Entry e, float now)
    {
        var g = e.Grip!;
        if (g.Look == null || g.Look.WasCollected) { e.Grip = null; return; }
        var bones = FindBones(e.Item.transform);
        var fingers = bones.FindAll(b => IsFinger(b.name.ToLowerInvariant()));
        if (fingers.Count == 0)
        {
            if (now < g.Until) return;
            e.Grip = null;
            var roots = new List<string>();
            for (var i = 0; i < e.Item.transform.root.childCount && i < 12; i++) roots.Add(e.Item.transform.root.GetChild(i).name);
            Plugin.Error("Summoning ball in hand: no finger bone found under the item's root or the local player after " + GripSeconds + " s. Item chain: " + Chain(e.Item.transform)
                + "; children of its root: " + string.Join(", ", roots) + ".");
            return;
        }
        e.Grip = null;
        var centre = g.Look.position;
        Transform nearest = fingers[0];
        foreach (var f in fingers) if ((f.position - centre).sqrMagnitude < (nearest.position - centre).sqrMagnitude) nearest = f;
        var names = new List<string>();
        foreach (var b in bones) if (names.Count < 24) names.Add(b.name);
        var camera = Camera.main;
        var text = "Summoning ball in hand: " + bones.Count + " arm bone(s) [" + string.Join(", ", names) + "]; ball centre " + Math.Round(Vector3.Distance(centre, nearest.position), 3)
            + " m from the nearest finger bone '" + nearest.name + "'";
        if (camera != null) text += ", " + ForgeCore.Log.Pos(camera.transform.InverseTransformPoint(centre)) + " in the camera frame (x right, y up, z forward, m)";
    }
    internal static void Clear()
    {
        Entries.Clear();
    }
}

/// <summary>Every peer: each ball instance the game spawns (first person, third person, thrown) gets the M01 look.</summary>
internal static class BeaconLookHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.ItemSpawned.After(static (call, _) => Spawned(call));

    private static void Spawned(ForgeCore.ItemSpawn call)
    {
        if (call.Id != BeaconItem.BlockId || call.Item is not { } item) return;
        var mode = call.Mode;
        try { BeaconLook.Apply(item, mode); }
        catch (Exception error) { Plugin.Error("Dressing the summoning ball (" + mode + ") failed; it keeps the glow stick look: " + error); }
    }
}
