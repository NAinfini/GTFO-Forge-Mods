using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>A pod's or shell's landing dust: a ring of dust and a few debris puffs (no fire or sparks), emitted into the two systems of the
/// <c>Ordnance:pod-impact</c> prefab on ForgeVisual's <see cref="ParticlePool"/>. Each system's budget is the capacity the prefab was built
/// with (eight landings' worth); a landing that does not fit in the free room is skipped whole and never takes a live particle. Unity simulates
/// the particles; there are no coroutines, collision callbacks or per-frame managed allocations.</summary>
internal static class PodImpactEffects
{
    internal const int DustCapacity = 384, DebrisCapacity = 96;
    private const int DustCount = 48, DebrisCount = 12;
    private static ParticleChannel? _dust, _debris;

    internal static void Load(BundleHandle bundle)
    {
        if (_dust != null) return;
        var pool = ParticlePool.Register(bundle, "pod-impact") ?? throw new InvalidOperationException("the bundle has no pod-impact prefab");
        var dust = OrdnanceFx.Channel(pool, "dust-ring", DustCapacity);
        var debris = OrdnanceFx.Channel(pool, "debris-puffs", DebrisCapacity);
        OrdnanceFx.Look(bundle, dust, "vfx-mist", FxBlend.Alpha);
        OrdnanceFx.Look(bundle, debris, "vfx-mist", FxBlend.Alpha);
        if (!pool.Warm()) throw new InvalidOperationException("the impact systems could not be made");
        _dust = dust;
        _debris = debris;
    }

    internal static void Play(Vector3 ground, Vector3 podSize)
    {
        if (_dust == null || _debris == null || !OrdnanceFx.Room(_dust, DustCount) || !OrdnanceFx.Room(_debris, DebrisCount)) return;
        var scale = Math.Clamp(Math.Max(podSize.x, podSize.z), .3f, 6f);
        Emit(_dust, ground, scale, DustCount, .25f, 2.4f, 1.5f, .5f, new Color(.52f, .46f, .37f, .42f));
        Emit(_debris, ground, scale, DebrisCount, 2.2f, 3.2f, .75f, .14f, new Color(.36f, .32f, .26f, .7f));
    }

    private static void Emit(ParticleChannel particles, Vector3 ground, float scale, int count, float up, float speed,
        float lifetime, float size, Color colour)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = i * (Mathf.PI * 2f / count) + UnityEngine.Random.Range(-.06f, .06f);
            var radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            particles.Emit(ground + radial * (scale * .45f) + Vector3.up * .05f,
                (radial * speed + Vector3.up * up) * scale, size * scale * UnityEngine.Random.Range(.8f, 1.2f),
                lifetime * UnityEngine.Random.Range(.8f, 1.15f), colour);
        }
    }

    /// <summary>The size of a spawned instance (prefab assets do not report renderer bounds reliably).</summary>
    internal static Vector3 ModelSize(GameObject instance)
    {
        var bounds = new Bounds();
        var first = true;
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (first) { bounds = renderer.bounds; first = false; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return bounds.size;
    }
}