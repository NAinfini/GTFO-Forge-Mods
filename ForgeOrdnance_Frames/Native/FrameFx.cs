using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The exosuits' particle effects. One prefab (<c>tools/fx/FrameFxBuild.cs</c>, embedded as <see cref="FxBundleBytes"/>) holds
/// twenty particle systems, one per kind of effect, authored in Unity: the game's interop strips the module setters, so the shape,
/// speed, size, colour and texture sheet of each kind are fixed in the bundle. The prefab is ForgeVisual's <see cref="ParticlePool"/>
/// and each system one of its <see cref="ParticleChannel"/>s, drawn with ForgeVisual's particle shader and the texture and blend listed
/// in <see cref="Table"/>; this class only emits into a channel (<c>Burst</c> from the system's position and rotation, or <c>Emit</c>
/// for rings, glows and cracks whose size, life and colour vary). The systems loop, play, have no emission of their own and simulate in
/// world space, so a system is its own pool: its <c>maxParticles</c> (given to the channel as the capacity) is the budget of everything
/// drawing into it, the oldest particle is recycled when it is full, and nothing allocates after the first use. Nothing here is
/// networked: each peer calls these from the state it already has.
///
/// Budgets per event (particles, all kinds together): landing 60 (24 dust, 10 column smoke, 14 stones, 10 sparks, 1 ring, 1 crack);
/// siege planted 17; arrival thrusters 2 to 4 per puff every 0.06 s for 1.06 s, about 14 alive; charge about 18 alive; rocket back-blast
/// 8 per rocket and a tail of about 5 flames and 9 smoke alive per rocket; push pulse 1 ring;
/// destruction 44 (1 fire flipbook, 6 fireball, 8 soft smoke, 18 sparks, 6 stones, 1 flash) and then about 10 alive while the wreck burns; ejection 2 steam
/// and 1 spark per back plate; boarding and leaving 2 small steam wisps per back plate as it opens and 1 as it latches; overheat about 6 alive.</summary>
internal static class FrameFx
{
    private enum Kind
    {
        BurstDust, ColumnSmoke, Chunks, Ember, Ring, PushRing, Thruster, FlameBlast, FlameSmall, Blast, JetCone, Fireball, WreckFlame, SmokeBlack,
        SmokeLight, Steam, HeatSteam, Glow, Crack, TrailDust,
    }

    /// <summary>Per kind, in <see cref="Kind"/> order: the system's name in the prefab, its texture in the bundle, how it blends, and its
    /// <c>maxParticles</c> (the three must match <c>tools/fx/FrameFxBuild.cs</c>).</summary>
    private static readonly (string Name, string Texture, FxBlend Blend, int Capacity)[] Table =
    {
        ("BurstDust", "fx-mist", FxBlend.Alpha, 80), ("ColumnSmoke", "fx-smoke", FxBlend.Alpha, 40), ("Chunks", "fx-disc", FxBlend.Alpha, 60),
        ("Ember", "fx-dot", FxBlend.Additive, 100), ("Ring", "fx-ring", FxBlend.Additive, 16), ("PushRing", "fx-ring", FxBlend.Additive, 8),
        ("Thruster", "fx-fireball-grey", FxBlend.Additive, 60), ("FlameBlast", "fx-fireball", FxBlend.Additive, 60),
        ("FlameSmall", "fx-fireball", FxBlend.Additive, 60), ("Blast", "fx-blast", FxBlend.Additive, 6), ("JetCone", "fx-jet", FxBlend.Additive, 120),
        ("Fireball", "fx-fireball", FxBlend.Additive, 40), ("WreckFlame", "fx-flame", FxBlend.Additive, 40), ("SmokeBlack", "fx-smoke", FxBlend.Alpha, 80),
        ("SmokeLight", "fx-smoke", FxBlend.Alpha, 80), ("Steam", "fx-smoke", FxBlend.Alpha, 60), ("HeatSteam", "fx-smoke", FxBlend.Additive, 24),
        ("Glow", "fx-glow", FxBlend.Additive, 40), ("Crack", "fx-crack", FxBlend.Alpha, 6), ("TrailDust", "fx-mist", FxBlend.Alpha, 40),
    };

    private static readonly Quaternion Ground = Quaternion.Euler(-90f, 0f, 0f);
    private static readonly Color Tan = new(0.92f, 0.8f, 0.62f, 0.8f);
    private static readonly Color CrackColour = new(0.1f, 0.08f, 0.06f, 0.85f);
    private static readonly Color Flash = new(1f, 0.82f, 0.5f, 0.9f);
    private static readonly Color VentColour = new(0.74f, 0.78f, 0.82f, 0.36f);
    private static readonly Color White = new(0.95f, 0.97f, 1f, 0.6f);

    /// <summary>Where the bundle build puts the textures (<c>tools/fx/FrameFxBuild.cs</c>).</summary>
    private const string TexturePath = "Assets/FrameFx/";
    private static ParticleChannel?[]? _channels;
    private static bool _failed;

    /// <summary>Loads the effects bundle and builds the systems now (the prewarm), so the first effect of a boarding creates nothing.</summary>
    internal static void Warm() => Ensure();

    // ---- the moments ----

    /// <summary>One frame of a thruster flame from one nozzle: a stretched cone and a soft hot core, both leaving at <paramref name="direction"/>
    /// at the jet speed on top of <paramref name="carry"/> (the suit's own velocity), so the flame stays in a column behind the moving suit.</summary>
    internal static void Jet(Vector3 nozzle, Vector3 carry, Vector3 direction, float strength)
    {
        var speed = FrameEffectsPlan.JetSpeed(strength);
        var size = FrameEffectsPlan.JetSize(strength);
        Shoot(Kind.JetCone, nozzle, carry + direction * speed, size, FrameEffectsPlan.JetLife(strength), Color.white);
        Shoot(Kind.Thruster, nozzle + direction * 0.15f, carry + direction * (speed * 0.7f), size * 0.9f, 0.14f, Color.white);
    }

    /// <summary>The last moment of a drop: the thrusters blow the dust off the ground below the suit.</summary>
    internal static void Kick(Vector3 ground)
    {
        Burst(Kind.BurstDust, ground + Vector3.up * 0.1f, Ground, 10);
        Place(Kind.Ring, ground + Vector3.up * 0.12f, 7f, 0.45f, Tan);
    }

    /// <summary>A suit touching down: ring of dust, rising column, stones, sparks, a shock ring and cracks in the floor.</summary>
    internal static void Landing(Vector3 ground)
    {
        var low = ground + Vector3.up * 0.1f;
        Burst(Kind.BurstDust, low, Ground, 24);
        Burst(Kind.ColumnSmoke, ground, Ground, 10);
        Burst(Kind.Chunks, low, Ground, 14);
        Burst(Kind.Ember, low, Ground, 10);
        Place(Kind.Ring, ground + Vector3.up * 0.15f, 14f, 0.7f, Tan);
        Place(Kind.Crack, ground + Vector3.up * 0.04f, 3.6f, 10f, CrackColour);
    }

    /// <summary>The siege supports planting.</summary>
    internal static void Siege(Vector3 ground)
    {
        var low = ground + Vector3.up * 0.1f;
        Burst(Kind.BurstDust, low, Ground, 12);
        Burst(Kind.Chunks, low, Ground, 4);
        Place(Kind.Ring, ground + Vector3.up * 0.15f, 6f, 0.5f, Tan);
    }

    /// <summary>One tick of a charge: blue flame from the back, and (every second tick) dust kicked up behind the feet. The flame is left out
    /// for the pilot's own first-person view (<paramref name="firstPerson"/>): it is world-space and stretched along its velocity, so a
    /// cone born a little behind the chest reached the camera and hung before the pilot's eyes while running; the pilot cannot see
    /// their own back, and everyone else still sees the flame.</summary>
    internal static void Charge(Vector3 back, Vector3 forward, Vector3 carry, Vector3 feet, bool dust, bool firstPerson)
    {
        if (!firstPerson) Jet(back, carry, -forward - Vector3.up * 0.1f, FrameEffectsPlan.ChargeJet);
        if (dust) Burst(Kind.TrailDust, feet + Vector3.up * 0.1f, Quaternion.LookRotation(-forward + Vector3.up * 0.15f), 1);
    }

    /// <summary>The barrier shoving enemies out: a white ring spreading from the dome's wall.</summary>
    internal static void Pulse(Vector3 centre, float radius)
        => Place(Kind.PushRing, centre, (radius + 2.2f) * 2f, 0.5f, White);

    /// <summary>A rocket leaving its shoulder: a flame cone and a smoke puff out of the back.</summary>
    internal static void Backblast(Vector3 at, Vector3 backward)
    {
        if (backward.sqrMagnitude < 1e-6f) return;
        Burst(Kind.FlameBlast, at, Quaternion.LookRotation(backward), 5);
        Burst(Kind.SmokeLight, at, Quaternion.identity, 3);
    }

    private static TrailStyle? _tailStyle;

    /// <summary>A rocket's tail in flight: a trail from <paramref name="start"/> that leaves small flames and, at half their rate, smoke
    /// along the rocket's path (<see cref="Trail.Move"/> every frame, <see cref="Trail.End"/> when it lands); null when the effects
    /// are not available. The flame and smoke are the systems' own look; only the rates, sizes, lives and drift are given here.</summary>
    internal static Trail? RocketTail(Vector3 start)
    {
        if (_tailStyle == null)
        {
            var flame = ChannelOf(Kind.FlameSmall);
            var smoke = ChannelOf(Kind.SmokeLight);
            if (flame == null || smoke == null) return null;
            _tailStyle = new TrailStyle
            {
                Puffs = new[]
                {
                    new TrailPuff
                    {
                        Channel = flame, PerSecond = FrameEffectsPlan.RocketTailFlamesPerSecond, Spread = 0.02f, Size = 0.19f, SizeJitter = 0.26f,
                        Life = 0.2f, LifeJitter = 0.3f, VelocityJitter = new Vector3(0.5f, 0.5f, 0.5f), Colour0 = Color.white, Priority = 1,
                    },
                    new TrailPuff
                    {
                        Channel = smoke, PerSecond = FrameEffectsPlan.RocketTailSmokePerSecond, Spread = 0.1f, Size = 0.575f, SizeJitter = 0.3f,
                        Life = 1.075f, LifeJitter = 0.25f, VelocityJitter = new Vector3(0.5f, 0.5f, 0.5f), Colour0 = Color.white, Priority = 1,
                    },
                },
            };
        }
        return Trails.Rent(start, _tailStyle);
    }

    /// <summary>The moment of destruction: fireball, black smoke, sparks, stones and a flash.</summary>
    internal static void Explosion(Vector3 feet)
    {
        var middle = feet + Vector3.up * 1.1f;
        Place(Kind.Blast, middle + Vector3.up * 0.9f, 8f, 1.1f, Color.white);
        Burst(Kind.Fireball, middle, Quaternion.identity, 6);
        Burst(Kind.SmokeBlack, middle, Quaternion.identity, 8);
        Burst(Kind.Ember, middle, Ground, 18);
        Burst(Kind.Chunks, feet + Vector3.up * 0.2f, Ground, 6);
        Place(Kind.Glow, middle, 7f, 0.25f, Flash);
    }

    /// <summary>One emission of a burning wreck: a flame and/or a curl of black smoke somewhere over its body.</summary>
    internal static void Wreck(Vector3 feet, bool flame, bool smoke)
    {
        var body = feet + Vector3.up * 1.0f;
        if (flame) Burst(Kind.WreckFlame, body, Quaternion.identity, 1);
        if (smoke) Burst(Kind.SmokeBlack, body + Vector3.up * 0.4f, Quaternion.identity, 1);
    }

    /// <summary>One back plate blowing open on ejection: a steam jet along <paramref name="outward"/> and a spark.</summary>
    internal static void Eject(Vector3 plate, Vector3 outward)
    {
        if (outward.sqrMagnitude < 1e-6f) return;
        Burst(Kind.Steam, plate, Quaternion.LookRotation(outward), 2);
        Burst(Kind.Ember, plate, Quaternion.LookRotation(outward), 1);
    }

    /// <summary>A thin wisp of steam from a seam opening or latching (boarding and leaving): a few small, translucent puffs of the
    /// smoke flipbook along <paramref name="direction"/>, so they never hide the suit or the view.</summary>
    internal static void Vent(Vector3 at, Vector3 direction, int count, float speed)
    {
        var along = direction.normalized;
        for (var i = 0; i < count; i++)
            Shoot(Kind.Steam, at + UnityEngine.Random.insideUnitSphere * 0.04f, (along + UnityEngine.Random.insideUnitSphere * 0.3f) * speed * UnityEngine.Random.Range(0.7f, 1.2f),
                UnityEngine.Random.Range(0.13f, 0.2f), UnityEngine.Random.Range(0.6f, 0.95f), VentColour);
    }

    /// <summary>A wisp of hot haze from a shoulder.</summary>
    internal static void Heat(Vector3 shoulder) => Burst(Kind.HeatSteam, shoulder, Ground, 1);

    // ---- the game's own effects (ForgeVisual's Game: catalogue) ----

    private static FxHandle? _flash, _explosion;

    /// <summary>The sentry gun's third-person muzzle flash as a copy under <paramref name="parent"/> (a gun hand or a tube's mouth), played
    /// with <see cref="AttachedFx.Play"/>; null (ForgeVisual logs why) when the game has not loaded it.</summary>
    internal static AttachedFx? MuzzleFlash(Transform parent)
    {
        _flash ??= Fx.Find("Game:Sentrygun_MuzzleFlash_3RD");
        return _flash == null ? null : Fx.Attach(_flash, parent);
    }

    /// <summary>A shot's tracer from the gun to where it lands (the game's own tracer).</summary>
    internal static void Tracer(Vector3 from, Vector3 to) => Fx.Tracer(from, to);

    /// <summary>The game's trip-mine explosion where a rocket lands, facing up.</summary>
    internal static void RocketExplosion(Vector3 point)
    {
        _explosion ??= Fx.Find("Game:FX_Tripmine");
        if (_explosion != null) Fx.Play(_explosion, point, Vector3.up);
    }

    // ---- plumbing ----

    private static void Burst(Kind kind, Vector3 at, Quaternion rotation, int count) => ChannelOf(kind)?.Burst(at, rotation, count);

    private static void Shoot(Kind kind, Vector3 at, Vector3 velocity, float size, float life, Color colour) => ChannelOf(kind)?.Emit(at, velocity, size, life, colour);

    private static void Place(Kind kind, Vector3 at, float size, float life, Color colour) => ChannelOf(kind)?.Emit(at, Vector3.zero, size, life, colour);

    private static ParticleChannel? ChannelOf(Kind kind) => _channels != null ? _channels[(int)kind] : Ensure() ? _channels![(int)kind] : null;

    private static bool Ensure()
    {
        if (_failed) return false;
        if (_channels != null) return true;
        _failed = true;
        var bundle = Bundles.RegisterBytes(Plugin.Id, "FramesFx", "FramesFx", FxBundleBytes.Read());
        var pool = bundle != null ? ParticlePool.Register(bundle, FxBundleBytes.PrefabName) : null;
        if (pool == null) { Plugin.Error("Exosuit effects could not be loaded (see ForgeVisual's error above); suits are shown without particle effects."); return false; }
        var channels = new ParticleChannel?[Table.Length];
        for (var i = 0; i < Table.Length; i++)
        {
            var (name, texture, blend, capacity) = Table[i];
            var channel = pool.Channel(name, capacity);
            if (channel == null || !bundle!.TryTexture(TexturePath + texture + ".png", out var image) || !channel.Look(image, blend))
            {
                Plugin.Error("Exosuit effects could not be set up: system or texture '" + name + "' / '" + texture + "' (see ForgeVisual's error above); suits are shown without particle effects.");
                return false;
            }
            channels[i] = channel;
        }
        if (!pool.Warm()) { Plugin.Error("Exosuit effects could not be made (see ForgeVisual's error above); suits are shown without particle effects."); return false; }
        _channels = channels;
        _failed = false;
        return true;
    }
}