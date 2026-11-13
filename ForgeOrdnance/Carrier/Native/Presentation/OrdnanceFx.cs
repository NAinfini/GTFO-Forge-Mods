using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The effects of the call-in moments: each plays its slot of the effect library (<see cref="OrdnanceFxSlots"/>; a slot the table does not list logs
/// its error and draws nothing). Every effect is triggered by each peer from state it already has (the delivery timeline, the beacon's own countdown,
/// the sentry's expiry timer), so nothing is sent over the network; a late joiner plays no instant effect.
/// </summary>
internal static class OrdnanceFx
{
    internal static ParticleChannel Channel(ParticlePool pool, string child, int capacity)
        => pool.Channel(child, capacity) ?? throw new InvalidOperationException("The effect prefab " + pool.Name + " lacks particle system " + child + ".");

    internal static void Look(BundleHandle bundle, ParticleChannel channel, string texture, FxBlend blend)
    {
        if (!bundle.TryTexture(texture, out var sprite)) throw new InvalidOperationException("rebuild forge-ordnance.bundle with the texture " + texture);
        if (!channel.Look(sprite, blend)) throw new InvalidOperationException("ForgeVisual's particle shader is unavailable");
    }

    /// <summary>True when <paramref name="count"/> more particles fit in the channel's free room.</summary>
    internal static bool Room(ParticleChannel channel, int count) => channel.Capacity <= 0 || channel.Live + count <= channel.Capacity;

    /// <summary>The footprint scale the landing effects use, bounded to 0.3 to 6 m.</summary>
    internal static float ScaleOf(Vector3 size) => Math.Clamp(Math.Max(size.x, size.z), 0.3f, 6f);

    /// <summary>A pod touches down and the ground breaks open.</summary>
    internal static void PodLanded(Vector3 ground, Vector3 size)
    {
        var scale = ScaleOf(size);
        if (OrdnanceFxSlots.PodGravel is { } gravel) Fx.Play(gravel, ground, Vec.Up, scale, Color.white);
        if (OrdnanceFxSlots.PodLand is { } land) Fx.Play(land, ground, Vec.Up, scale, Color.white);
    }

    /// <summary>A pod's doors spring open: one puff of steam and a spray of sparks at each hinge, and a warm glow.</summary>
    internal static void PodOpened(Vector3 centre, Transform[] hinges, Vector3 size)
    {
        var scale = ScaleOf(size);
        if (OrdnanceFxSlots.PodGlow is { } glow) Fx.Play(glow, centre, Vec.Up, scale, Color.white);
        foreach (var hinge in hinges)
        {
            if (hinge == null || hinge.WasCollected) continue;
            if (OrdnanceFxSlots.PodVent is { } vent) Fx.Play(vent, hinge.position, Vec.Up, scale, Color.white);
            if (OrdnanceFxSlots.ServoSparks is { } sparks) Fx.Play(sparks, hinge.position, Vec.Up, scale, Color.white);
        }
    }

    /// <summary>A pod sinks away: dust at its feet and one small puff of steam.</summary>
    internal static void PodLeaving(Vector3 ground, Vector3 size)
    {
        if (OrdnanceFxSlots.PodVent is { } vent) Fx.Play(vent, ground + Vec.Up * 0.4f, Vec.Up, ScaleOf(size), Color.white);
        if (OrdnanceFxSlots.PodLeave is { } slot) Fx.Play(slot, ground, Vec.Up, ScaleOf(size), Color.white);
    }

    /// <summary>A beacon touches down.</summary>
    internal static void BeaconImpact(Vector3 ground, Color hot)
    {
        if (OrdnanceFxSlots.BeaconImpact is { } slot) Fx.Play(slot, ground, Vec.Up, 1f, hot);
    }

    /// <summary>The colour of a turret coming online (its slot's ring is white and takes this tint).</summary>
    private static readonly Color Online = new(0.35f, 0.85f, 1f, 1f);

    internal static void SentryOnline(Vector3 at)
    {
        if (OrdnanceFxSlots.SentryOnline is { } slot) Fx.Play(slot, at, Vec.Up, 1f, Online);
    }

    internal static void SentryExpired(Vector3 at)
    {
        if (OrdnanceFxSlots.SentryExpired is { } slot) Fx.Play(slot, at, Vec.Up, 1f, Color.white);
    }
}
