using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>The few host reads the exit-scan code needs. They sit in one file so the offline suite can replace exactly
/// these and compile the rest of the production sources unchanged.</summary>
internal static class ExitScanEnvironment
{
    internal static long WorldEpoch => RecTime.WorldEpoch;
    internal static int Frame { get { try { return Time.frameCount; } catch (Exception) { return -1; /* Unity frame counter unreadable: -1 is the scan record's explicit "no frame" value. */ } } }
    internal static void Info(string message) => Plugin.PluginLog.LogInfo("[ExitScan] " + message);

    /// <summary>The sibling-indexed transform path, so two objects with the same name stay distinguishable.</summary>
    internal static string PathOf(Transform? transform)
    {
        if (transform == null) return "unavailable";
        var names = new List<string>();
        for (var i = 0; transform != null && i < 32; i++, transform = transform.parent) names.Add(transform.name + "[" + transform.GetSiblingIndex() + "]");
        names.Reverse(); return string.Join("/", names);
    }
}
