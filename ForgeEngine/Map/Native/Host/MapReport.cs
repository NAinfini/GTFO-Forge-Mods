using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The one place a native part's untyped refusal sentence becomes a record in the Map provider's log. The
/// package writes no BepInEx line: a sentence reported before the registration exists waits here and is written
/// the moment <see cref="MapLog"/> is attached; one reported after it is written at once. A sentence that starts with
/// its own <c>map.*</c> or <c>rundown.*</c> code keeps that code as the value <c>source</c>.</summary>
internal static class MapReport
{
    private const int Capacity = 64;
    private static readonly Regex Source = new(@"^([a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+)[: ]", RegexOptions.CultureInvariant);
    private static readonly List<string> Pending = new();
    private static int _dropped;

    internal static void Write(string message)
    {
        if (message == null) return;
        if (!MapLog.IsAttached)
        {
            lock (Pending)
            {
                if (Pending.Count < Capacity) Pending.Add(message);
                else _dropped++;
            }
            return;
        }
        Record(message);
    }

    /// <summary>Writes what was reported before the registration existed, then how many reports did not fit. Called
    /// once the log is attached.</summary>
    internal static void Flush()
    {
        string[] waiting; int dropped;
        lock (Pending) { waiting = Pending.ToArray(); Pending.Clear(); dropped = _dropped; _dropped = 0; }
        foreach (var message in waiting) Record(message);
        if (dropped != 0)
            Record("map.report-dropped: " + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " more reports arrived before the log was attached and were not kept.");
    }

    private static void Record(string message)
    {
        // The per-tick query ceilings refuse a whole read by design and keep the previous state; that is a limit working.
        // A door or terminal the level's own layout leaves outside the addressable families (a vanilla zone with specific
        // terminal spawns, a gate of no known zone) is a readback fact, not a failure: nothing can name it, and a plan
        // that tries is refused on its own path. One whose members did not read says "cannot be addressed" and stays an error.
        var level = message.Contains("tick-budget", StringComparison.Ordinal) || message.Contains("query-budget", StringComparison.Ordinal)
            || message.Contains(" is not addressable: ", StringComparison.Ordinal)
            ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
        if (!MapLog.Logs(level)) return;
        var match = Source.Match(message);
        if (match.Success)
            MapLog.Record(level, MapLogCodes.NativeReport, message, RuntimeLogValue.Quote("source", match.Groups[1].Value));
        else MapLog.Record(level, MapLogCodes.NativeReport, message);
    }
}
