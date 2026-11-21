using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Drives and reads the objects declared to ForgeCore's recording interface (<c>Recording.Track</c>, <c>State</c>) the way any mod
/// would, so a recording can be checked against what was done while it ran. Called by name from the console
/// (<c>call static:ForgeDevelopment.Native.TrackedObjects Move "ForgeEngine/model/&lt;id&gt;" 0 1 0 1.5 true</c>).</summary>
internal static class TrackedObjects
{
    private static readonly List<IDisposable> Loaded = new();
    private static readonly List<GameObject> LoadBodies = new();
    private static readonly List<(Transform At, Vector3 Home)> LoadPlaces = new();
    private static bool _loadMoves, _hooked;
    private static int _loadFrame;

    /// <summary><c>key at=x,y,z scale=x,y,z shown=</c> of the object the recording interface finds for the key, else <c>missing</c>.</summary>
    public static string Read(string key)
    {
        var body = ForgeCore.Recording.Find(key);
        if (body == null) return key + " missing";
        var t = body.transform;
        return key + " at=" + V(t.position) + " scale=" + V(t.localScale) + " shown=" + body.activeSelf;
    }

    /// <summary>Moves the object by the offset, multiplies its scale and shows or hides it, then reads it back.</summary>
    public static string Move(string key, float dx, float dy, float dz, float scale, bool shown)
    {
        var body = ForgeCore.Recording.Find(key);
        if (body == null) return key + " missing";
        var t = body.transform;
        t.position += new Vector3(dx, dy, dz);
        t.localScale *= scale;
        body.SetActive(shown);
        return Read(key);
    }

    /// <summary>Tells the key's state as its owner would (<c>Recording.State</c>; "null" sends null). Only the recording hears it.</summary>
    public static string Tell(string key, string text)
    {
        ForgeCore.Recording.State(key, text == "null" ? null : text);
        return "told " + key;
    }

    /// <summary>Declares <paramref name="count"/> empty objects under <c>ForgeDevelopment/load/&lt;n&gt;</c> (0 takes them all back), moving every frame
    /// when <paramref name="moving"/>: the load a recording's per-frame cost is measured against.</summary>
    public static string Load(int count, bool moving)
    {
        foreach (var held in Loaded) held.Dispose();
        foreach (var body in LoadBodies) if (body != null && !body.WasCollected) UnityEngine.Object.Destroy(body);
        Loaded.Clear(); LoadBodies.Clear(); LoadPlaces.Clear();
        _loadMoves = moving;
        for (var i = 0; i < count; i++)
        {
            var body = new GameObject("ForgeDevelopment load " + i);
            var home = new Vector3(i % 50, 0f, i / 50);
            var at = body.transform;
            at.position = home;
            LoadBodies.Add(body);
            LoadPlaces.Add((at, home));
            Loaded.Add(ForgeCore.Recording.Track("ForgeDevelopment/load/" + i, body));
        }
        if (!_hooked) { _hooked = true; ForgeCore.Frame.OnUpdate("NAinfini.ForgeDevelopment.tracked-load", MoveLoad); }
        return "declared " + count + (moving ? " moving" : " still");
    }

    /// <summary><c>pickups=;placed=;shown=;items=</c>: the level's pickups (those under the floor, switched off ones included, and the active
    /// scene's), how many lie placed in the level, how many of those are shown, and the item ids with their count (<c>id x n</c>), to compare
    /// a level with its replay.</summary>
    public static string GroundItems()
    {
        var seen = new HashSet<IntPtr>();
        var all = new List<LevelGeneration.LG_PickupItem_Sync>();
        var floor = LevelGeneration.LG_LevelBuilder.Current?.m_currentFloor;
        if (floor != null) foreach (var p in floor.transform.GetComponentsInChildren<LevelGeneration.LG_PickupItem_Sync>(true)) if (seen.Add(p.Pointer)) all.Add(p);
        foreach (var p in UnityEngine.Object.FindObjectsOfType<LevelGeneration.LG_PickupItem_Sync>()) if (seen.Add(p.Pointer)) all.Add(p);
        int placed = 0, shown = 0;
        var ids = new SortedDictionary<uint, int>();
        foreach (var p in all)
        {
            if (p.GetCurrentState().status != LevelGeneration.ePickupItemStatus.PlacedInLevel) continue;
            placed++;
            if (p.gameObject.activeInHierarchy) shown++;
            var id = p.item == null ? 0u : p.item.pItemData.itemID_gearCRC;
            ids[id] = ids.TryGetValue(id, out var n) ? n + 1 : 1;
        }
        var text = new System.Text.StringBuilder("pickups=" + all.Count + ";placed=" + placed + ";shown=" + shown + ";items=");
        foreach (var (id, n) in ids) text.Append(id).Append(" x").Append(n).Append(',');
        return text.ToString();
    }

    // Moves each declared object every frame from where it was put, through cached transforms, so the load reads nothing from the game.
    private static void MoveLoad()
    {
        if (!_loadMoves) return;
        var step = new Vector3(0f, Mathf.Sin(++_loadFrame * 0.05f), 0f);
        foreach (var (at, home) in LoadPlaces) if (!at.WasCollected) at.position = home + step;
    }

    private static string V(Vector3 v) => v.x.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.###", CultureInfo.InvariantCulture);
}
