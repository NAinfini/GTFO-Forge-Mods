using ForgeCore;
using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The enemies' aimed projectiles in the air while a replay plays: each recorded flight (Projectile kinds 3 and 4) is drawn as the
/// game's own projectile of its recorded type (ForgeVisual <see cref="ProjectileLooks"/>: the prefab's meshes, particles, trail and light,
/// none of its behaviour), at its recorded places and carried on between two samples at the speed they give. It hits and hurts nothing;
/// the hit is the recorded hurt event. Main thread only.</summary>
internal static class Flights
{
    // A flight is carried on past its last sample for at most this long (the recorder samples every 50 to 100 ms).
    private const float CarrySeconds = 0.15f;

    private sealed class Flight
    {
        internal GameObject? Body;
        internal Vector3 At, Velocity;
        internal uint TimeMs;
    }

    private static readonly Dictionary<ulong, Flight> Live = new();
    private static readonly List<ulong> Gone = new();
    private static int _firedType = -1;

    /// <summary>A recorded aimed projectile fired (kind 0): its type stands for flights whose own type the recording could not name.</summary>
    internal static void Fired(int type) => _firedType = type;

    /// <summary>A flight sample: <paramref name="type"/> is the recorded ProjectileType + 1 (0 not known).</summary>
    internal static void Sample(ulong id, uint timeMs, Vector3 at, ulong type, Vector3 heading)
    {
        if (!Live.TryGetValue(id, out var flight))
        {
            Live[id] = flight = new Flight { At = at, TimeMs = timeMs };
            var kind = type > 0 ? (int)type - 1 : _firedType;
            if (kind >= 0)
            {
                try
                {
                    flight.Body = ProjectileLooks.Copy((ProjectileType)kind, at, Quaternion.LookRotation(Vec.SqrLength(heading) > 1e-6f ? heading : Vec.Forward), false, out _, out var reason);
                    if (flight.Body == null) Skipped.Note("a recorded enemy projectile could not be drawn", reason);
                }
                catch (Exception error) { Skipped.Note("a recorded enemy projectile could not be drawn", error.Message); }
            }
            return;
        }
        var seconds = (timeMs - flight.TimeMs) / 1000f;
        flight.Velocity = seconds > 0f ? (at - flight.At) / seconds : Vec.Zero;
        flight.At = at;
        flight.TimeMs = timeMs;
        Place(flight, 0f);
    }

    /// <summary>A flight ended (it hit something or its life ran out).</summary>
    internal static void Ended(ulong id)
    {
        if (!Live.TryGetValue(id, out var flight)) return;
        if (flight.Body != null) UnityEngine.Object.Destroy(flight.Body);
        Live.Remove(id);
    }

    /// <summary>Per frame: each flight is carried on from its last sample to the replay's moment <paramref name="nowMs"/>; one whose samples
    /// stopped without an end (a jump went past it) is taken away.</summary>
    internal static void Frame(uint nowMs)
    {
        if (Live.Count == 0) return;
        Gone.Clear();
        foreach (var (id, flight) in Live)
        {
            var seconds = nowMs >= flight.TimeMs ? (nowMs - flight.TimeMs) / 1000f : 0f;
            if (seconds > 1f) Gone.Add(id);
            else Place(flight, Mathf.Min(seconds, CarrySeconds));
        }
        foreach (var id in Gone) Ended(id);
    }

    private static void Place(Flight flight, float seconds)
    {
        if (flight.Body == null || flight.Body.WasCollected) return;
        var t = flight.Body.transform;
        t.position = flight.At + flight.Velocity * seconds;
        if (Vec.SqrLength(flight.Velocity) > 1e-4f) t.rotation = Quaternion.LookRotation(flight.Velocity);
    }

    /// <summary>A jump or the end of the replay: every flight in the air is taken away.</summary>
    internal static void Clear()
    {
        foreach (var flight in Live.Values) if (flight.Body != null) UnityEngine.Object.Destroy(flight.Body);
        Live.Clear();
        _firedType = -1;
    }
}
