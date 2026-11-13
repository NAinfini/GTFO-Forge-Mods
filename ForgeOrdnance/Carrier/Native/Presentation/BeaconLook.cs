using System;
using System.Collections.Generic;
using ForgeVisual;
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
    private const float RescanSeconds = 1.5f;
    private static readonly Color Amber = new(1f, 0.58f, 0.12f, 1f);
    private static readonly List<Entry> Entries = new();

    private sealed class Entry
    {
        internal readonly int RibbonKey = Fx.NewKey();
        internal Item Item = null!;
        internal ItemMode Mode;
        internal Renderer? Source;
        internal MeshRenderer[] Ours = Array.Empty<MeshRenderer>();
        internal readonly List<IntPtr> Handled = new();
        internal float RescanUntil;
        internal float NextScan;
    }

    /// <summary>Every peer, from the game's item spawn: dresses one ball instance.</summary>
    internal static void Apply(Item item, ItemMode mode)
    {
        var model = OrdnanceAssets.Beacon;
        if (model == null) return;
        var entry = new Entry { Item = item, Mode = mode, RescanUntil = Time.time + RescanSeconds };
        Entries.Add(entry);
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
            if (mesh == null || renderer.name.StartsWith("ForgeOrdnance", StringComparison.Ordinal)) continue;
            var t = renderer.transform;
            if (UnderLook(t)) continue;
            entry.Handled.Add(renderer.Pointer);
            var size = mesh.bounds.size;
            var longest = Math.Max(size.x, Math.Max(size.y, size.z));
            if (longest > anchorSize) { anchorSize = longest; anchor = renderer; anchorScale = t.localScale; }
            Hide(renderer, t);
            hidden = true;
        }
        if (!hidden) return;
        if (entry.Source == null && anchor != null)
        {
            entry.Source = anchor;
            entry.Ours = Mount(anchor, anchorScale, model, entry.Mode);
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

    /// <summary><paramref name="t"/> is the renderer's own transform.</summary>
    private static void Hide(Renderer renderer, Transform t)
    {
        if (t.childCount == 0) t.localScale = Vec.Zero;
        else renderer.enabled = false;
    }

    /// <summary>M01 in the anchor mesh's frame: its up axis along the stick's long axis (the end that points up now), centred on
    /// the stick's centre, at its real world size whatever scale the stick's hierarchy has.</summary>
    private static MeshRenderer[] Mount(Renderer anchor, Vector3 anchorScale, ModelHandle model, ItemMode mode)
    {
        var mesh = MeshOf(anchor)!;
        var t = anchor.transform;
        var holder = new GameObject("ForgeOrdnance.BeaconLook");
        var frame = holder.transform;
        var parent = t.parent;
        frame.SetParent(parent, false);
        frame.localPosition = t.localPosition;
        frame.localRotation = t.localRotation;
        // The anchor's own scale was zeroed to hide it; the holder takes its original scale so mesh units match.
        frame.localScale = anchorScale;
        var bounds = mesh.bounds;
        var size = bounds.size;
        var axis = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vec.Up : Vec.Forward;
        var length = Vec.Dot(size, axis);
        if (frame.TransformDirection(axis).y < 0f) axis = -axis;
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
        var lookFrame = look.transform;
        lookFrame.localRotation = Quaternion.FromToRotation(Vec.Up, axis);
        var lossy = frame.lossyScale;
        var along = Math.Abs(Vec.Dot(lossy, axis));
        var scale = along > 1e-4f ? worldHeight / ModelHeight / along : 1f;
        lookFrame.localScale = Vector3.one * scale;
        // In the hand the stick's centre is inside the fist: the ball sits on the stick's upper end, its centre HandHold radii under
        // the tip, so it shows outside the fingers.
        var radius = worldHeight * 0.5f;
        var lift = hand && along > 1e-4f ? Math.Max(0f, length * 0.5f - HandHold * radius / along) : 0f;
        lookFrame.localPosition = bounds.center + axis * lift;
        Models.SetLayer(frame, anchor.gameObject.layer);
        var ours = look.GetComponentsInChildren<MeshRenderer>(true);
        var shown = anchor.enabled;
        foreach (var r in ours) r.enabled = shown;
        return ours;
    }

    /// <summary>Every frame: M01 shows exactly when the game shows the stick; meshes the game adds shortly after spawn are hidden too.
    /// <paramref name="now"/> is the frame's <c>Time.time</c>.</summary>
    internal static void Tick(float now)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Item == null || e.Item.WasCollected) { Entries.RemoveAt(i); continue; }
            if (e.Mode == ItemMode.Instance && OrdnanceFxSlots.Ribbon is { } ribbon) Fx.Hold(ribbon, e.RibbonKey, e.Item.transform.position, Vec.Up, float.NaN, Color.white);
            if (now < e.RescanUntil && now >= e.NextScan && OrdnanceAssets.Beacon is { } model)
            {
                e.NextScan = now + 0.25f;
                Dress(e, model);
            }
            if (e.Source == null || e.Source.WasCollected) continue;
            var shown = e.Source.enabled;
            foreach (var r in e.Ours)
                if (r != null && !r.WasCollected && r.enabled != shown) r.enabled = shown;
        }
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
        catch (Exception error) { Plugin.Logger.ErrorOnce("Dressing the summoning ball (" + mode + ") failed; it keeps the glow stick look: " + error); }
    }
}
