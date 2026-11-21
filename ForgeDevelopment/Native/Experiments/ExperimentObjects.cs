using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>
/// Bookkeeping for the objects an experiment creates. A command can only address one value at a time, so
/// everything a run spawns is registered here: the panel shows the count, the runner destroys it when the
/// command ends, and a command can call these methods itself when it needs a clean slate mid-run.
/// </summary>
internal static class ExperimentObjects
{
    private static readonly List<NavMarker> Markers = new();
    private static readonly List<GameObject> Clones = new();
    private static readonly object Gate = new();

    internal static int Count { get { lock (Gate) return Markers.Count + Clones.Count; } }

    internal static void Track(NavMarker? marker)
    {
        if (marker == null) return;
        lock (Gate) Markers.Add(marker);
    }

    internal static void Track(GameObject? clone)
    {
        if (clone == null) return;
        lock (Gate) Clones.Add(clone);
    }

    internal static NavMarker? TakeLastMarker()
    {
        lock (Gate)
        {
            for (var index = Markers.Count - 1; index >= 0; index--)
            {
                var marker = Markers[index];
                Markers.RemoveAt(index);
                if (marker != null) return marker;
            }
        }
        return null;
    }

    internal static void Forget(Object? value)
    {
        if (value == null) return;
        var identity = value.GetHashCode();
        lock (Gate)
        {
            for (var index = Markers.Count - 1; index >= 0; index--)
                if (Markers[index] == null || Markers[index].GetHashCode() == identity) Markers.RemoveAt(index);
            for (var index = Clones.Count - 1; index >= 0; index--)
                if (Clones[index] == null || Clones[index].GetHashCode() == identity) Clones.RemoveAt(index);
        }
    }

    /// <summary>Destroys everything this module created. Called when a command finishes, and on demand.</summary>
    internal static string DestroyAll()
    {
        NavMarker[] markers;
        GameObject[] clones;
        lock (Gate)
        {
            markers = Markers.ToArray();
            clones = Clones.ToArray();
            Markers.Clear();
            Clones.Clear();
        }
        var removed = 0;
        var failures = new List<string>();
        var layer = TryLayer();
        foreach (var marker in markers)
        {
            try
            {
                if (marker == null) { removed++; continue; }
                if (layer != null) ForgeCore.NavMarkers.Remove(marker);
                else Object.Destroy(marker.gameObject);
                removed++;
            }
            catch (Exception e)
            {
                failures.Add(e.GetType().Name + ": " + e.Message);
            }
        }
        foreach (var clone in clones)
        {
            try
            {
                if (clone == null) { removed++; continue; }
                Object.Destroy(clone);
                removed++;
            }
            catch (Exception e)
            {
                failures.Add(e.GetType().Name + ": " + e.Message);
            }
        }
        var report = "destroyed " + removed.ToString(CultureInfo.InvariantCulture) + " experiment object(s)";
        if (failures.Count != 0) report += "; failures: " + string.Join("; ", failures.Take(4));
        return report;
    }

    private static NavMarkerLayer? TryLayer()
    {
        try { return GuiManager.NavMarkerLayer; }
        catch (Exception) { return null; } // Il2Cpp read guard: no nav marker layer yet is the null the callers already handle.
    }
}

/// <summary>
/// The methods a command calls on this module itself. They exist so an experiment can arm the damage
/// rewrite, clean up its own objects or place and remove markers without inventing a step type for each
/// case. Every method here only touches the local presentation, never game state.
/// </summary>
internal static class ExperimentHelper
{
    internal static string ArmDamage(float factor) => ExperimentDamage.Arm(factor);
    internal static string DisarmDamage() => ExperimentDamage.Disarm();
    internal static string RemoveAllExperimentMarkers() => ExperimentObjects.DestroyAll();

    /// <summary>The generic placement entry point, with the tracking object taken from the previous result.</summary>
    internal static string PlaceOnResult(GameObject trackingObj, string option, string title, float destroyDelay)
    {
        var layer = GuiManager.NavMarkerLayer;
        if (layer == null) return "GuiManager.NavMarkerLayer is unavailable";
        if (!ExperimentConvert.TryEnum(typeof(NavMarkerOption), option, out var parsed, out var error)) return error;
        var marker = ForgeCore.NavMarkers.Place((NavMarkerOption)parsed!, trackingObj, title, destroyDelay);
        ExperimentObjects.Track(marker);
        return marker == null ? "PlaceCustomMarker returned null" : "marker placed on " + trackingObj.name;
    }

    /// <summary>The one placement that takes a colour. <c>plain: true</c> passes no colour at all, so the
    /// difference between "no colour" and a colour is recorded rather than guessed at.</summary>
    internal static string SelectOnResult(GameObject trackingObj, bool plain, float red, float green, float blue, float alpha)
    {
        var layer = GuiManager.NavMarkerLayer;
        if (layer == null) return "GuiManager.NavMarkerLayer is unavailable";
        NavMarker? marker;
        if (plain)
        {
            marker = layer.PlaceSelectionMarker(trackingObj, new Il2CppSystem.Nullable<Color>());
            ExperimentObjects.Track(marker);
            return marker == null ? "PlaceSelectionMarker returned null" : "selection marker placed on " + trackingObj.name + " without a colour";
        }
        var color = new Color(red, green, blue, alpha);
        marker = layer.PlaceSelectionMarker(trackingObj, new Il2CppSystem.Nullable<Color>(color));
        ExperimentObjects.Track(marker);
        return marker == null ? "PlaceSelectionMarker returned null" : "selection marker placed on " + trackingObj.name + " with " + color;
    }

    internal static string RemoveMarker(NavMarker marker)
    {
        if (marker == null) return "no marker to remove";
        var layer = GuiManager.NavMarkerLayer;
        if (layer == null) return "GuiManager.NavMarkerLayer is unavailable";
        ForgeCore.NavMarkers.Remove(marker);
        ExperimentObjects.Forget(marker);
        return "marker removed";
    }

    internal static string RemoveLastMarker()
    {
        var layer = GuiManager.NavMarkerLayer;
        if (layer == null) return "GuiManager.NavMarkerLayer is unavailable";
        var marker = ExperimentObjects.TakeLastMarker();
        if (marker == null) return "no experiment marker was placed";
        ForgeCore.NavMarkers.Remove(marker);
        return "last marker removed";
    }
}
