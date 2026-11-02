using UnityEngine;

namespace ForgeOrdnance;

/// <summary>What each presentation moment emits into the effect channels: positions, speeds, sizes, lifetimes and tints, and nothing else.
/// Fade, growth, flipbook and spin are baked into the prefab; pooling, budgets and triggers live in <c>OrdnanceFx</c>.
/// Particle budgets per moment are the counts written beside each recipe.</summary>
internal static class FxRecipes
{
    /// <summary>The beacon shock ring: full diameter in metres and seconds to run out and fade.</summary>
    internal const float RingDiameter = 7.2f, RingLife = 0.55f;

    private static float R(float a, float b) => Random.Range(a, b);
    private static Vector3 Spread(float radius) { var c = Random.insideUnitCircle * radius; return new Vector3(c.x, 0f, c.y); }

    /// <summary>Landing: a heavy thing hits the ground and the ground breaks open (no fire, no sparks, no flash): 18 concrete and earth
    /// chunks thrown out and up (8 big, 10 small), a grey-brown skirt of dust racing along the ground (10) and light soft dust rolling out and
    /// up from the pod's base (10); 38 particles. The small gravel that keeps spraying for a moment is <see cref="Gravel"/>.</summary>
    internal static void LandingBreak(FxSystems s, Vector3 ground, float scale)
    {
        const int chunks = 18, skirt = 10, soft = 10;
        for (var i = 0; i < chunks; i++)
        {
            var angle = i * (Mathf.PI * 2f / chunks) + R(-0.2f, 0.2f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            var big = i % 9 < 4;
            s.Rock.Emit(ground + radial * (scale * 0.5f) + Vector3.up * 0.08f,
                (radial * R(1.6f, 3.4f) + Vector3.up * R(3f, 5.8f)) * Mathf.Sqrt(scale),
                scale * (big ? R(0.15f, 0.24f) : R(0.06f, 0.1f)), R(1.8f, 2.8f), ChunkTint());
        }
        for (var i = 0; i < skirt; i++)
        {
            var angle = i * (Mathf.PI * 2f / skirt) + R(-0.3f, 0.3f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            s.Smoke.Emit(ground + radial * (scale * 0.55f) + Vector3.up * 0.12f, (radial * R(2.2f, 3.6f) + Vector3.up * R(0.1f, 0.4f)) * scale,
                scale * 0.8f * R(0.8f, 1.2f), R(1.1f, 1.6f), new Color(0.56f, 0.5f, 0.43f, 0.75f));
        }
        for (var i = 0; i < soft; i++)
        {
            var angle = i * (Mathf.PI * 2f / soft) + R(-0.3f, 0.3f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            s.Smoke.Emit(ground + radial * (scale * 0.5f) + Vector3.up * 0.1f, (radial * R(0.8f, 1.4f) + Vector3.up * R(0.2f, 0.5f)) * scale,
                scale * 1.0f * R(0.8f, 1.2f), R(1.8f, 2.4f), new Color(0.86f, 0.83f, 0.78f, 0.85f));
        }
    }

    /// <summary>Gravel sprayed out of the ground around the pod's base while it settles: <paramref name="count"/> small chunks, flung outward.</summary>
    internal static void Gravel(FxSystems s, Vector3 ground, float scale, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = R(0f, Mathf.PI * 2f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            s.Rock.Emit(ground + radial * (scale * R(0.45f, 0.6f)) + Vector3.up * 0.06f, (radial * R(1.2f, 3.2f) + Vector3.up * R(1.5f, 3.2f)) * Mathf.Sqrt(scale),
                scale * R(0.025f, 0.05f), R(0.9f, 1.4f), ChunkTint(), 0);
        }
    }

    /// <summary>The atlas chunks carry their own colour (concrete or earth); the tint only varies their brightness a little.</summary>
    private static Color ChunkTint() { var v = R(0.82f, 1f); return new Color(v, v, v, 1f); }
    /// <summary>Doors springing open: five white steam puffs out of one hinge side (four sides: 20 smoke).</summary>
    internal static void DoorVent(FxSystems s, Vector3 centre, Vector3 hinge, float scale)
    {
        var outward = hinge - centre;
        outward.y = 0f;
        outward = outward.sqrMagnitude < 1e-4f ? Vector3.right : outward.normalized;
        for (var i = 0; i < 5; i++)
            s.Smoke.Emit(hinge + Spread(scale * 0.05f) + Vector3.up * (scale * 0.2f), (outward * R(1.3f, 2.1f) + Spread(0.5f) + Vector3.up * R(0.3f, 0.8f)) * scale,
                scale * 0.55f * R(0.85f, 1.15f), R(0.9f, 1.2f), new Color(1f, 1f, 1f, 1f));
    }

    /// <summary>The warm light inside the pod as the doors give way (1 flare).</summary>
    internal static void DoorGlow(FxSystems s, Vector3 centre, float scale)
        => s.Flare.Emit(centre, Vector3.zero, scale * 2.2f, 0.5f, new Color(1f, 0.7f, 0.35f, 0.8f));

    /// <summary>The sinking pod stirs the dust at its feet (12 smoke).</summary>
    internal static void LeaveDust(FxSystems s, Vector3 ground, float scale)
    {
        const int puffs = 12;
        for (var i = 0; i < puffs; i++)
        {
            var angle = i * (Mathf.PI * 2f / puffs) + R(-0.1f, 0.1f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            s.Smoke.Emit(ground + radial * (scale * 0.55f) + Vector3.up * 0.05f, (radial * 0.35f + Vector3.up * 0.25f) * scale,
                scale * 0.8f * R(0.8f, 1.2f), R(1.3f, 1.6f), new Color(0.78f, 0.7f, 0.58f, 0.9f));
        }
    }

    /// <summary>Beacon touchdown: a ring of ground sparks and a flash and a shock ring lying on the ground in the beam's colour (12 sparks, 1 flare and 1 ring).</summary>
    internal static void BeaconImpact(FxSystems s, Vector3 ground, Color hot)
    {
        const int sparks = 12;
        for (var i = 0; i < sparks; i++)
        {
            var angle = i * (Mathf.PI * 2f / sparks) + R(-0.15f, 0.15f);
            var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            s.Spark.Emit(ground + radial * 0.15f + Vector3.up * 0.05f, radial * R(2.2f, 3.5f) + Vector3.up * R(2.5f, 4.5f),
                0.09f * R(0.8f, 1.2f), R(0.45f, 0.7f), new Color(hot.r, hot.g, hot.b, 0.95f));
        }
        s.Flare.Emit(ground + Vector3.up * 0.1f, Vector3.zero, 1.4f, 0.3f, new Color(hot.r, hot.g, hot.b, 0.9f));
        s.Ring.Emit(ground + Vector3.up * 0.06f, Vector3.zero, RingDiameter, RingLife, new Color(hot.r, hot.g, hot.b, 1f));
    }

    /// <summary>A turret coming online: a blue-white flash and a wisp of smoke (1 flare and 6 smoke).</summary>
    internal static void SentryOnline(FxSystems s, Vector3 at)
    {
        s.Flare.Emit(at, Vector3.zero, 1.4f, 0.3f, new Color(0.6f, 0.8f, 1f, 1f));
        for (var i = 0; i < 6; i++)
            s.Smoke.Emit(at + Spread(0.08f) + Vector3.up * 0.1f, Spread(0.15f) + Vector3.up * R(0.5f, 1f), 0.25f * R(0.8f, 1.2f), R(1.2f, 1.8f),
                new Color(1f, 1f, 1f, 1f));
    }

    /// <summary>A turret running out: a thread of black smoke (8 smoke).</summary>
    internal static void SentryExpired(FxSystems s, Vector3 at)
    {
        for (var i = 0; i < 8; i++)
            s.Smoke.Emit(at + Vector3.up * 0.3f + Spread(0.06f), Spread(0.2f) + Vector3.up * R(0.6f, 1.1f), 0.3f * R(0.8f, 1.2f), R(2.6f, 3.2f),
                new Color(0.3f, 0.3f, 0.3f, 1f));
    }
}
