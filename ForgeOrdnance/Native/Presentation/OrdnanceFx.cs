using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The six particle systems of the baked <c>Ordnance:ordnance-fx</c> prefab, as ForgeVisual channels.</summary>
internal sealed class FxSystems
{
    internal ParticleChannel Flame = null!, Smoke = null!, Spark = null!, Flare = null!, Ring = null!, Rock = null!;
}

/// <summary>
/// The particle effects of the call-in moments, on ForgeVisual's <see cref="ParticlePool"/>: one pool of the baked <c>ordnance-fx</c>
/// prefab whose six systems (flame, smoke, spark, flare, ring, rock) hold every effect of every call at once; each system's budget is
/// the capacity the prefab was built with (ten effects' worth). Every effect is triggered by each peer from state it already has (the
/// delivery timeline, the beacon's own countdown, the sentry's expiry timer), so nothing is sent over the network; a late joiner plays no
/// instant effect and only the continuous fall trail follows the current timeline. Unity simulates the particles; the managed side only
/// emits, without allocating per frame and without string lookups. An effect that does not fit in the free room of its systems is
/// skipped whole and never takes a live particle.
/// </summary>
internal static class OrdnanceFx
{
    /// <summary>The prefab's maxParticles per system: ten effects of the old per-effect budget (flame 16, smoke 24, spark 16, flare 4, ring 4, rock 72).</summary>
    internal const int FlameCapacity = 160, SmokeCapacity = 240, SparkCapacity = 160, FlareCapacity = 40, RingCapacity = 40, RockCapacity = 720;
    private const int Sprays = 10;
    /// <summary>The ground keeps spitting gravel for this long after a pod lands, at this rate (about 40 small chunks).</summary>
    private const float GravelSeconds = 1.8f, GravelPerSecond = 22f;
    /// <summary>The re-entry flame goes out this long before touchdown, so the arrival lands without fire: the longest flame (0.4 s) is gone on landing.</summary>
    private const float FlameCutoff = 0.4f;

    private struct Spray
    {
        internal float Until, Debt, Scale;
        internal Vector3 Ground;
    }

    private static readonly Spray[] Gravel = new Spray[Sprays];
    private static readonly Dictionary<(float, bool), TrailStyle> FallStyles = new();
    private static FxSystems? _fx;

    internal static void Load(BundleHandle bundle)
    {
        if (_fx != null) return;
        var pool = ParticlePool.Register(bundle, "ordnance-fx") ?? throw new InvalidOperationException("the bundle has no ordnance-fx prefab");
        FxSystems systems = new()
        {
            Flame = Channel(pool, "flame", FlameCapacity), Smoke = Channel(pool, "smoke", SmokeCapacity), Spark = Channel(pool, "spark", SparkCapacity),
            Flare = Channel(pool, "flare", FlareCapacity), Ring = Channel(pool, "ring", RingCapacity), Rock = Channel(pool, "rock", RockCapacity),
        };
        Look(bundle, systems.Flame, "vfx-flame", FxBlend.Additive);
        Look(bundle, systems.Smoke, "vfx-smoke", FxBlend.Alpha);
        Look(bundle, systems.Spark, "vfx-spark", FxBlend.Additive);
        Look(bundle, systems.Flare, "vfx-flare", FxBlend.Additive);
        Look(bundle, systems.Ring, "vfx-ring", FxBlend.Additive);
        Look(bundle, systems.Rock, "vfx-rock", FxBlend.Alpha);
        if (!pool.Warm()) throw new InvalidOperationException("the effect systems could not be made");
        _fx = systems;
    }

    internal static ParticleChannel Channel(ParticlePool pool, string child, int capacity)
        => pool.Channel(child, capacity) ?? throw new InvalidOperationException("The effect prefab " + pool.Name + " lacks particle system " + child + ".");

    internal static void Look(BundleHandle bundle, ParticleChannel channel, string texture, FxBlend blend)
    {
        if (!bundle.TryTexture(texture, out var sprite)) throw new InvalidOperationException("rebuild forge-ordnance.bundle with the texture " + texture);
        if (!channel.Look(sprite, blend)) throw new InvalidOperationException("ForgeVisual's particle shader is unavailable");
    }

    /// <summary>True when <paramref name="count"/> more particles fit in the channel's free room.</summary>
    internal static bool Room(ParticleChannel channel, int count) => channel.Capacity <= 0 || channel.Live + count <= channel.Capacity;

    /// <summary>True while any landed pod still sprays gravel.</summary>
    internal static bool Busy(float now)
    {
        for (var i = 0; i < Gravel.Length; i++) if (now < Gravel[i].Until) return true;
        return false;
    }

    /// <summary>Every frame while <see cref="Busy"/>: the gravel still spraying around landed pods.</summary>
    internal static void Tick(float now)
    {
        if (_fx == null) return;
        for (var i = 0; i < Gravel.Length; i++)
        {
            ref var spray = ref Gravel[i];
            if (now >= spray.Until) continue;
            spray.Debt += GravelPerSecond * Time.deltaTime;
            var count = (int)spray.Debt;
            if (count > 0) { spray.Debt -= count; FxRecipes.Gravel(_fx, spray.Ground, spray.Scale, count); }
        }
    }

    internal static void Clear()
    {
        for (var i = 0; i < Gravel.Length; i++) Gravel[i] = default;
    }

    /// <summary>The footprint scale the landing dust uses, bounded to 0.3 to 6 m.</summary>
    internal static float ScaleOf(Vector3 size) => Math.Clamp(Math.Max(size.x, size.z), 0.3f, 6f);

    // ---- one-shot moments ---------------------------------------------------------------------------------

    /// <summary>A pod touches down and the ground breaks open: chunks, dust and a moment of gravel (38 particles and about 40 gravel).</summary>
    internal static void PodLanded(Vector3 ground, Vector3 size)
    {
        if (_fx is not { } fx || !Room(fx.Rock, 18) || !Room(fx.Smoke, 20)) return;
        var scale = ScaleOf(size);
        FxRecipes.LandingBreak(fx, ground, scale);
        var now = Time.time;
        var free = 0;
        for (var i = 0; i < Gravel.Length; i++)
        {
            if (Gravel[i].Until < Gravel[free].Until) free = i;
        }
        Gravel[free] = new Spray { Ground = ground, Scale = scale, Debt = 0f, Until = now + GravelSeconds };
    }

    /// <summary>A pod's doors spring open: steam from each hinge side and a warm glow (21 particles).</summary>
    internal static void PodOpened(Vector3 centre, Transform[] hinges, Vector3 size)
    {
        if (_fx is not { } fx || !Room(fx.Smoke, 5 * hinges.Length) || !Room(fx.Flare, 1)) return;
        var scale = ScaleOf(size);
        foreach (var hinge in hinges)
            if (hinge != null && !hinge.WasCollected) FxRecipes.DoorVent(fx, centre, hinge.position, scale);
        FxRecipes.DoorGlow(fx, centre, scale);
    }

    /// <summary>A pod sinks away: dust at its feet (12 particles).</summary>
    internal static void PodLeaving(Vector3 ground, Vector3 size)
    {
        if (_fx is { } fx && Room(fx.Smoke, 12)) FxRecipes.LeaveDust(fx, ground, ScaleOf(size));
    }

    /// <summary>A beacon touches down: sparks, a flash and a shock ring (14 particles).</summary>
    internal static void BeaconImpact(Vector3 ground, Color hot)
    {
        if (_fx is { } fx && Room(fx.Spark, 12) && Room(fx.Flare, 1) && Room(fx.Ring, 1)) FxRecipes.BeaconImpact(fx, ground, hot);
    }

    internal static void SentryOnline(Vector3 at)
    {
        if (_fx is { } fx && Room(fx.Flare, 1) && Room(fx.Smoke, 6)) FxRecipes.SentryOnline(fx, at);
    }

    internal static void SentryExpired(Vector3 at)
    {
        if (_fx is { } fx && Room(fx.Smoke, 8)) FxRecipes.SentryExpired(fx, at);
    }

    // ---- the fall -----------------------------------------------------------------------------------------

    /// <summary>A falling arrival's tail, starting at <paramref name="start"/>: flames along the path while it falls (and, for the pod,
    /// smoke), moved every frame with <see cref="Trail.Move"/> (progress 0 to 1 from release to touchdown) and ended at touchdown or
    /// when the arrival leaves early; the puffs already left finish on their own.</summary>
    internal static Trail? BeginFall(Vector3 start, float scale, bool smoke)
    {
        if (_fx is not { } fx) return null;
        if (!FallStyles.TryGetValue((scale, smoke), out var style))
        {
            // Every puff of a full system is skipped (priority 0): the tail is cosmetic and never takes a live particle.
            var flame = new TrailPuff
            {
                Channel = fx.Flame, PerSecond = smoke ? 24f : 18f, StopAt = 1f - FlameCutoff / DeliveryTiming.FallSeconds,
                Spread = scale * 0.1f, LiftBySize = 0.4f, Velocity = new Vector3(0f, 2f, 0f), VelocityJitter = new Vector3(0f, 1f, 0f),
                Size = scale * 1.7f, SizeJitter = 0.15f, SizeGrowth = 1.5f, Life = 0.35f, LifeJitter = 0.15f, Colour0 = new Color(1f, 0.85f, 0.65f, 1f),
            };
            var puffs = new List<TrailPuff> { flame };
            if (smoke)
                puffs.Add(new TrailPuff
                {
                    Channel = fx.Smoke, PerSecond = 14f, Offset = new Vector3(0f, scale * 0.5f, 0f), Spread = scale * 0.25f,
                    Velocity = new Vector3(0f, 0.5f, 0f), VelocityJitter = new Vector3(scale * 0.45f, 0.3f, scale * 0.45f),
                    Size = scale * 1.7f, SizeJitter = 0.2f, Life = 1f, LifeJitter = 0.15f, Colour0 = new Color(0.92f, 0.87f, 0.82f, 1f),
                });
            style = new TrailStyle { Puffs = puffs.ToArray() };
            FallStyles.Add((scale, smoke), style);
        }
        return Trails.Rent(start, style);
    }
}