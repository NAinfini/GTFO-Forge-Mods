using System;
using System.Collections.Generic;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceFrames;

/// <summary>Firepower shoulder rockets. The host grants a salvo and sends its seed; every peer flies the same rockets
/// (<see cref="FrameRules.RocketShot"/>) and plays the game's trip-mine explosion effect (the preloaded <c>FX_Tripmine</c>
/// pool) and the frag grenade's explosion sound where each lands; the current host applies the game's explosion through ForgeCore's <c>Combat.Explode</c>,
/// <c>DamageUtil.DoExplosionDamage(point, radius, damage, MASK_EXPLOSION_TARGETS, MASK_EXPLOSION_BLOCKERS, false, 0)</c>, so
/// damage falls off to the edge, level geometry blocks it, and players are hit as by any explosive (the pilot's own hits land
/// on the suit's armour).
///
/// Each rocket leaves its tube of the suit's launcher (<see cref="FrameRocketPod"/>) with the tube's muzzle flash and a
/// back-blast, flies as the definition's rocket model (<see cref="RocketLauncherSpec.Round"/>, pooled after landing) with its
/// tail of flame and smoke, and leaves its tube empty until it is reloaded.</summary>
internal static class FrameRockets
{

    private struct Rocket
    {
        internal float Start, Duration, Arc, Damage, Radius, Speed;
        internal int Index;
        internal ForgeVisual.Trail? Tail;
        internal bool Fired;
        internal Vector3 From, To;
        internal FrameUnit Unit;
        internal string Round;
        internal GameObject? Body;
    }

    private static readonly FrameRocketFlights<Rocket> Flying = new();
    /// <summary>One pool of rocket bodies per round. No more rockets than the flight table holds are ever in the air, so that is
    /// each pool's capacity; a take beyond it is refused and logged, never dropped silently.</summary>
    private static readonly Dictionary<string, ForgeCore.Pool<GameObject>> Spare = new(StringComparer.Ordinal);

    /// <summary>Every peer: a granted salvo of <paramref name="unit"/> at the message's point.</summary>
    internal static void Launch(FrameUnit unit, FrameRocketMessage m, float now)
    {
        var spec = unit.Record.Spec.Rockets;
        if (spec == null) { Plugin.Error("Exosuit rockets: suit " + unit.Record.ThrowId + " has no rockets; launch ignored."); return; }
        if (!float.IsFinite(m.TargetX) || !float.IsFinite(m.TargetY) || !float.IsFinite(m.TargetZ))
        {
            Plugin.Error("Exosuit rockets: suit " + unit.Record.ThrowId + " received a non-finite target; launch ignored.");
            return;
        }
        var target = new Vector3(m.TargetX, m.TargetY, m.TargetZ);
        var pod = unit.Visual?.RocketPod;
        pod?.Salvo(now);
        var soundAt = Origin(unit, 0);
        FrameAudio.PlayAt(FrameSound.Rocket, FrameAudio.Identity(unit), soundAt, ForgeAudio.AudioKit.DimensionAt(soundAt, true),
            unit.Record.Pilot != 0 && unit.Record.Pilot == SNet.LocalPlayer?.Lookup);
        for (var i = 0; i < spec.Salvo; i++)
        {
            FrameRules.RocketShot(m.Seed, i, spec.Spread, out var dx, out var dz, out var delay);
            var rocket = new Rocket
            {
                Start = now + delay, Damage = spec.Damage, Radius = spec.Radius, Speed = spec.Speed, Index = i, Unit = unit,
                Round = spec.Launcher.Round, To = target + new Vector3(dx, 0f, dz),
            };
            Aim(ref rocket, Origin(unit, i));
            Flying.Add(rocket);
        }
    }

    /// <summary>Every frame on every peer.</summary>
    internal static void Tick(float now)
    {
        if (Flying.Count == 0) return;
        for (var i = 0; i < Flying.Capacity; i++)
        {
            if (!Flying.IsLive(i)) continue;
            ref var r = ref Flying.At(i);
            if (now < r.Start) continue;
            var t = (now - r.Start) / r.Duration;
            if (t >= 1f)
            {
                Land(i, ref r);
                continue;
            }
            if (!r.Fired)
            {
                // It leaves now: from its tube where the suit stands this frame, with the tube's flash and a back-blast.
                r.Fired = true;
                var pod = r.Unit.Visual?.RocketPod;
                Aim(ref r, Origin(r.Unit, r.Index));
                r.Start = now;
                t = 0f;
                pod?.Fire(r.Index);
                FrameFx.Backblast(r.From, -(pod?.Facing(r.Index) ?? (r.To - r.From).normalized));
            }
            if (r.Body == null || r.Body.WasCollected) r.Body = Take(r.Round, r.From);
            if (r.Body == null) continue;
            var position = Point(in r, t);
            var ahead = Point(in r, Math.Min(1f, t + 0.02f)) - position;
            r.Tail ??= FrameFx.RocketTail(position);
            r.Tail?.Move(position, t);
            r.Body.transform.SetPositionAndRotation(position, ahead.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(ahead) : r.Body.transform.rotation);
        }
    }

    /// <summary>Where rocket <paramref name="index"/> of <paramref name="unit"/>'s salvo leaves: its tube's mouth, or above the
    /// suit's shoulders when the suit is not shown (its art is missing): the rocket still flies and explodes.</summary>
    private static Vector3 Origin(FrameUnit unit, int index)
        => unit.Visual?.RocketPod?.Origin(index) ?? new Vector3(unit.Record.X, unit.Record.Y + 2.2f, unit.Record.Z);

    /// <summary>Flies <paramref name="r"/> from <paramref name="from"/> to its landing point.</summary>
    private static void Aim(ref Rocket r, Vector3 from)
    {
        var distance = Vector3.Distance(from, r.To);
        r.From = from;
        r.Duration = Math.Max(0.05f, distance / r.Speed);
        r.Arc = Math.Min(3f, distance * 0.08f);
    }

    private static Vector3 Point(in Rocket r, float t) => Vector3.Lerp(r.From, r.To, t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * r.Arc);

    private static void Land(int slot, ref Rocket r)
    {
        var ownsDamage = Flying.Settle(slot, SNet.IsMaster);
        try
        {
            r.Tail?.End();
            r.Tail = null;
            if (r.Body != null && !r.Body.WasCollected) Give(r.Round, r.Body);
            r.Body = null;
            PlayExplosion(r.To);
        }
        catch (Exception error) { Plugin.Error("Exosuit rocket landing presentation failed: " + error); }
        finally
        {
            if (ownsDamage) Explode(r.To, r.Radius, r.Damage);
        }
    }

    private static void Explode(Vector3 point, float radius, float damage)
    {
        try { ForgeCore.Combat.Explode(point, radius, damage, 0f); }
        catch (Exception error) { Plugin.Error("Exosuit rocket explosion failed: " + error); }
    }

    private static void PlayExplosion(Vector3 point)
    {
        // The package's own blast: the game's grenade and mine explosion events start no voice when posted this way (Wwise ends
        // them within 0.1 s without ever reporting a duration).
        FrameAudio.PlayAlone(FrameSound.RocketBlast, point);
        FrameFx.RocketExplosion(point);
    }

    private static GameObject? Take(string round, Vector3 at)
    {
        if (!Spare.TryGetValue(round, out var pool)) Spare[round] = pool = ForgeCore.Pool.Create(Flying.Capacity, () => Make(round), body => body.SetActive(true), body => body.SetActive(false));
        try
        {
            while (pool.TryTake(out var body))
            {
                if (body.WasCollected) { pool.Discard(body); continue; }
                body.transform.position = at;
                return body;
            }
            Plugin.Error("Exosuit rockets: all " + pool.Capacity + " bodies of round '" + round + "' are in the air; this rocket is not drawn.");
        }
        catch (Exception error) { Plugin.Error("Exosuit rockets: a body of round '" + round + "' could not be made: " + error); }
        return null;
    }

    private static GameObject Make(string round)
    {
        var body = FrameAssets.SpawnProp(round, Vector3.zero, Quaternion.identity, null)
            ?? throw new InvalidOperationException("the prop is missing");
        body.name = "ForgeOrdnanceFrames.Rocket";
        foreach (var renderer in body.GetComponentsInChildren<Renderer>(true)) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return body;
    }

    private static void Give(string round, GameObject body)
    {
        if (Spare.TryGetValue(round, out var pool)) pool.Return(body);
    }

    /// <summary>Level left: drop every rocket (its explosion included) and the pools tied to the level.</summary>
    internal static void Clear()
    {
        for (var i = 0; i < Flying.Capacity; i++)
        {
            if (!Flying.IsLive(i)) continue;
            ref var rocket = ref Flying.At(i);
            rocket.Tail?.End();
            rocket.Tail = null;
            if (rocket.Body != null && !rocket.Body.WasCollected) Object.Destroy(rocket.Body);
        }
        foreach (var pool in Spare.Values) pool.Drain(spare => { if (spare != null && !spare.WasCollected) Object.Destroy(spare); });
        Spare.Clear();
        Flying.Reset();
    }
}
