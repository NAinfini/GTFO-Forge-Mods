using System;
using System.Collections.Generic;
using System.Linq;
using IRF;
using LevelGeneration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>Original object prototypes (spawners, marker producers, baked meshes, lights, colliders and any other vanilla
/// object a recipe part names), captured before native resource expansion changes them.</summary>
internal static class RoomRecipeParts
{
    private static readonly Dictionary<string, Dictionary<string, GameObject>> Sources = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, HashSet<MapLevelReference>>> PlacedModelRequests = new(StringComparer.Ordinal);
    private static GameObject? staging;

    /// <summary>Each source prefab with the spawner paths the placed models of <paramref name="level"/> use. A pack holds the
    /// models of every level, but a level only loads its own rooms, so a level asks for the paths its own models use; an
    /// unreadable level (null) asks for everything. A source none of the level's models use is left out.</summary>
    internal static IEnumerable<KeyValuePair<string, string[]>> RequestsFor(MapLevelReference? level)
    {
        foreach (var requested in PlacedModelRequests)
        {
            var paths = requested.Value.Where(path => level is not { } own || path.Value.Contains(own)).Select(path => path.Key).ToArray();
            if (paths.Length != 0) yield return new(requested.Key, paths);
        }
    }

    /// <summary>The generic placed object reuses the room builder's one table of exact native spawner
    /// prototypes. These requests add paths the current package will place; they do not create another model
    /// catalog or accept preview-only asset ids.</summary>
    internal static void ConfigurePlacedModels(IReadOnlyList<PlacedModel> models)
    {
        PlacedModelRequests.Clear();
        foreach (var model in models)
        {
            if (model.Model is not { } prototype) continue;
            if (!PlacedModelRequests.TryGetValue(prototype.SourcePrefab, out var paths))
                PlacedModelRequests.Add(prototype.SourcePrefab, paths = new(StringComparer.Ordinal));
            if (!paths.TryGetValue(prototype.SourcePath, out var levels)) paths.Add(prototype.SourcePath, levels = new());
            levels.Add(model.Level);
        }
    }

    /// <summary>Capture one original spawner at its exact hierarchy path inside <paramref name="sourcePrefab"/>.</summary>
    internal static bool CaptureSpawner(string sourcePrefab, string path, LG_PrefabSpawner original)
    {
        var prefab = original.m_prefab;
        if (prefab == null) return false;
        string node = System.IO.Path.GetFileName(path), prefix = "LG_PrefabSpawner_" + prefab.name;
        if (node != prefix && !node.StartsWith(prefix + " (", StringComparison.Ordinal))
            throw Refusal("recipe-part-prefab-name-mismatch", sourcePrefab, path);
        if (!Sources.TryGetValue(sourcePrefab, out var parts)) Sources.Add(sourcePrefab, parts = new(StringComparer.Ordinal));
        if (parts.TryGetValue(path, out var cached))
        {
            var previous = cached == null ? null : cached.GetComponent<LG_PrefabSpawner>();
            if (previous == null || !Equivalent(previous, original))
            {
                parts[path] = null!; // conflicting evidence must not leave the first candidate usable
                throw Refusal("recipe-part-ambiguous", sourcePrefab, path);
            }
            return false;
        }
        EnsureStaging();
        parts.Add(path, Object.Instantiate<GameObject>(original.gameObject, staging!.transform));
        return true;
    }
    private static void EnsureStaging()
    {
        if (staging != null) return;
        staging = new GameObject("Forge original room parts"); staging.SetActive(false);
        Object.DontDestroyOnLoad(staging);
    }
    internal static bool Capture(RoomBuildDonor donor, GameObject source) => Capture(donor.Prefab, donor.Nodes, source);
    internal static bool Capture(string sourcePrefab, string path, GameObject source)
        => Capture(sourcePrefab, new[] { path }, source);
    internal static bool Captured(RoomBuildDonor donor)
        => Sources.TryGetValue(donor.Prefab, out var parts) && donor.Nodes.All(parts.ContainsKey);

    /// <summary>All requested paths of one donor are captured together, or none are.</summary>
    private static bool Capture(string sourcePrefab, IEnumerable<string> requestedPaths, GameObject source)
    {
        Sources.TryGetValue(sourcePrefab, out var existing);
        bool complete = existing != null;
        foreach (var path in requestedPaths) complete &= existing != null && existing.ContainsKey(path);
        if (complete) return false;
        EnsureStaging();
        var holder = new GameObject(sourcePrefab); holder.transform.SetParent(staging!.transform, false);
        var parts = existing == null ? new Dictionary<string, GameObject>(StringComparer.Ordinal) : new(existing, StringComparer.Ordinal);
        try
        {
            foreach (var path in requestedPaths)
            {
                if (parts.ContainsKey(path)) continue;
                parts.Add(path, Prototype(OriginalNode(source.transform, sourcePrefab, path), holder.transform, sourcePrefab, path));
            }
            Sources[sourcePrefab] = parts; return true;
        }
        catch { Object.DestroyImmediate(holder); throw; }
    }
    internal static GameObject Get(string source, string path)
        => Sources.TryGetValue(source, out var parts) && parts.TryGetValue(path, out var part)
            ? part ?? throw Refusal("recipe-part-ambiguous", source, path)
            : throw Refusal("recipe-part-not-captured-before-expansion", source, path);
    internal static void Clear()
    {
        Sources.Clear(); PlacedModelRequests.Clear();
        if (staging != null) Object.DestroyImmediate(staging); staging = null;
    }
    /// <summary>The clone of <paramref name="selected"/> a part of a split prefab is built from: the node's modifiers
    /// (<c>~</c> no children, <c>+^k.i</c> support objects) applied, and every instanced draw parented outside the clone
    /// moved onto it, keeping where it stands relative to the node.</summary>
    private static GameObject Prototype(Transform selected, Transform holder, string sourcePrefab, string path)
    {
        var clone = Object.Instantiate<GameObject>(selected.gameObject, holder);
        var parts = path.Split('/');
        var last = parts[^1];
        var support = last.StartsWith('+') ? last[1..].Split(',') : Array.Empty<string>();
        if (Array.IndexOf(parts, WithoutChildren) >= 0)
            for (int i = clone.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(clone.transform.GetChild(i).gameObject);
        foreach (var row in support)
        {
            // ^k.i: child i of the k-th ancestor of the node.
            var dot = row.IndexOf('.');
            var at = selected;
            if (row[0] != '^' || dot < 2 || !int.TryParse(row[1..dot], out var up) || !int.TryParse(row[(dot + 1)..], out var child)) throw Refusal("recipe-part-missing", sourcePrefab, path);
            for (; up > 0 && at != null; up--) at = at.parent;
            if (at == null || child < 0 || child >= at.childCount) throw Refusal("recipe-part-missing", sourcePrefab, path);
            var original = at.GetChild(child);
            var added = Object.Instantiate<GameObject>(original.gameObject, clone.transform).transform;
            added.localPosition = selected.InverseTransformPoint(original.position);
            added.localRotation = Quaternion.Inverse(selected.rotation) * original.rotation;
            Vector3 own = selected.lossyScale, wanted = original.lossyScale;
            added.localScale = new(wanted.x / own.x, wanted.y / own.y, wanted.z / own.z);
        }
        foreach (var feature in clone.GetComponentsInChildren<InstancedRenderFeature>(true))
        {
            var instances = feature.Instances;
            if (instances == null) continue;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance?.Parent == null || instance.Parent.IsChildOf(clone.transform)) continue;
                var parent = instance.Parent;
                instance.LocalPosition = selected.InverseTransformPoint(parent.TransformPoint(instance.LocalPosition));
                instance.LocalForward = selected.InverseTransformDirection(parent.TransformDirection(instance.LocalForward));
                instance.LocalUp = selected.InverseTransformDirection(parent.TransformDirection(instance.LocalUp));
                instance.Parent = clone.transform;
            }
        }
        return clone;
    }
    private const string WithoutChildren = "~", KeepFlags = "!", ThroughSpawner = ">";
    /// <summary>Whether this part keeps its spawner's own collision and navigation flags instead of the recipe default.</summary>
    internal static bool KeepsSpawnerFlags(string path) => Array.IndexOf(path.Split('/'), KeepFlags) >= 0;

    /// <summary>The object at <paramref name="path"/> below <paramref name="root"/>, or the root itself for
    /// <see cref="RoomBuildRecipe.PrefabRootNode"/>. A segment <c>name#k</c> picks the k-th sibling of that name; a plain
    /// segment matching several siblings is accepted only for equivalent spawners (the native tutorial has duplicate
    /// names), never by choosing between different objects. A <c>&gt;</c> segment steps through the spawner on the node into
    /// the prefab it spawns, under the same rule for same-named spawners. The grammar's modifiers (<see cref="Prototype"/>) follow the object path and do not address anything.</summary>
    internal static Transform OriginalNode(Transform root, string sourcePrefab, string path)
    {
        if (path == RoomBuildRecipe.PrefabRootNode) return root;
        var matches = new List<Transform> { root };
        foreach (var segment in path.Split('/'))
        {
            if (segment is RoomBuildRecipe.PrefabRootNode or WithoutChildren or KeepFlags || segment.StartsWith('+')) continue;
            if (segment == ThroughSpawner)
            {
                if (One(matches, sourcePrefab, path).GetComponent<LG_PrefabSpawner>() is not { m_prefab: not null } crossed) throw Refusal("recipe-part-missing", sourcePrefab, path);
                matches = new List<Transform> { crossed.m_prefab.transform };
                continue;
            }
            int cut = segment.LastIndexOf('#'), index = -1;
            string name = segment;
            if (cut > 0 && int.TryParse(segment.AsSpan(cut + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k))
            { name = segment[..cut]; index = k; }
            var next = new List<Transform>();
            foreach (var parent in matches)
            {
                var named = new List<Transform>();
                for (int i = 0; i < parent.childCount; i++) { var child = parent.GetChild(i); if (child.name == name) named.Add(child); }
                if (index < 0) next.AddRange(named);
                else if (index < named.Count) next.Add(named[index]);
            }
            if (next.Count == 0) throw Refusal("recipe-part-missing", sourcePrefab, path);
            matches = next;
        }
        return One(matches, sourcePrefab, path);
    }
    /// <summary>The one node a name path reaches, at its end or where <c>&gt;</c> crosses a spawner: several same-named
    /// siblings stand for one node only when they are equivalent spawners, and then the first in child order is taken.</summary>
    private static Transform One(List<Transform> matches, string sourcePrefab, string path)
    {
        if (matches.Count == 1) return matches[0];
        var selected = matches[0].GetComponent<LG_PrefabSpawner>();
        foreach (var match in matches)
        {
            var other = match.GetComponent<LG_PrefabSpawner>();
            if (selected == null || other == null || selected.m_prefab == null || !Equivalent(selected, other))
                throw Refusal("recipe-part-ambiguous", sourcePrefab, path, "several same-named nodes with different content");
        }
        return matches[0];
    }
    /// <summary>A capture refusal names the donor and the exact spawner path; both also ride in <c>Data</c> for the I-DIAG record.</summary>
    private static InvalidOperationException Refusal(string reason, string sourcePrefab, string path, string? detail = null)
    {
        var error = new InvalidOperationException(reason + ": " + (detail == null ? "" : detail + ": ") + sourcePrefab + "#" + path);
        error.Data["sourcePrefab"] = sourcePrefab; error.Data["sourcePath"] = path;
        return error;
    }
    internal static string? FailedPath(Exception error) => error.GetBaseException().Data["sourcePath"] as string;
    internal static string? FailedPrefab(Exception error) => error.GetBaseException().Data["sourcePrefab"] as string;
    private static bool Equivalent(LG_PrefabSpawner left, LG_PrefabSpawner right)
        => left.m_prefab != null && right.m_prefab != null && left.m_prefab.Pointer == right.m_prefab.Pointer
            && left.m_forceShadowsOff == right.m_forceShadowsOff
            && left.m_useLightCategoryOverride == right.m_useLightCategoryOverride
            && left.m_lightCategoryOverride == right.m_lightCategoryOverride;
}
