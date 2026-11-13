using System;
using ForgeAudio;
using ForgeVisual;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Presentation of native contacts and projectile phases; effects finish independently.</summary>
internal sealed class SpecialEffects : IDisposable
{
    private sealed class DiscView
    {
        internal Transform Root = null!;
        internal Transform Blade = null!;
        internal AudioVoice Audio = null!;
        internal Vector3 Lift;
    }

    /// <summary>The flying well is drawn this many times its body radius across (0.75 m for a 0.18 m body).</summary>
    private const float ShellShare = 4.17f;
    private const int ShellCapacity = BlastShells.Capacity;
    private const float FlameImpactInterval = 0.08f;
    private const int FlameStart = 0, FlameCrackle = 1, FlameImpactSound = 2, DiscLaunch = 3, DiscFlight = 4,
        DiscBounce = 5, HoleLaunchSound = 6, HoleFlight = 7, HoleOpen = 8, HoleClose = 9, BlastLaunchSound = 10, BlastCycle = 11,
        BlastImpact = 12, FlameLoop = 13, FlameStop = 14, DiscStop = 15, HoleField = 16;
    /// <summary>The game's own blade-into-flesh sound (melee_knife_hit_flesh_regular) for a disc cutting an enemy.</summary>
    private const uint DiscHitSound = 3633778169;

    private readonly GameObject _root;
    private readonly DiscView[] _discs = new DiscView[3];
    private readonly AudioVoice _flameAudio, _holeAudio, _crackleAudio;
    private readonly Transform _holeVoice, _crackleVoice;
    private readonly AudioVoice[] _launchVoices = new AudioVoice[3];
    /// <summary>The hits drawn through ForgeVisual's impact presets: a spark burst off a surface.</summary>
    private ImpactLook _sparks = null!;
    /// <summary>Each flying shell's trail (a ribbon along where it has been, thinning behind it) and its light.</summary>
    private readonly Trail?[] _shellTrails = new Trail?[ShellCapacity];
    private readonly int[] _shellLights = new int[ShellCapacity];
    private static readonly TrailStyle ShellTrail = new()
    {
        RibbonPoints = 16,
        RibbonStep = 0.012f,
        RibbonWidth = 0.14f,
        RibbonTaper = 0.05f,
        RibbonHead = new Color(1f, 0.86f, 0.6f, 1f),
        RibbonTail = new Color(1f, 0.4f, 0.1f, 0f),
        RibbonBlend = FxBlend.Additive,
    };
    private static readonly PointLightSpec ShellLight = new(new Color(1f, 0.62f, 0.28f), 3f, 1.5f, 0f, 0.2f);
    /// <summary>Hold keys of this weapon's effects: its flame, each disc's trail, and its black hole. Each is taken new when its
    /// thing starts (the jet lit, a shell or disc fired, the well shown again), so a slot used again never moves the last one's copy.</summary>
    private int _flameKey, _holeKey, _shellKey;
    private readonly int[] _discKeys = new int[3];
    private int _launchCursor;
    private float _nextFlameImpact, _nextFlameImpactSound;
    private bool _flaming, _disposed;
    private static readonly string[] Sounds = { "flame-start", "flame-crackle", "flame-hit", "disc-fire",
        "disc-fly-loop", "disc-bounce", "gravity-fire", "gravity-fly-loop", "gravity-open", "gravity-close", "blast-fire", "blast-cycle",
        "blast-explode", "flame-loop", "flame-stop", "disc-drop", "gravity-field-loop" };

    internal Quaternion ViewRotation { get; set; } = Quaternion.identity;

    private EnergyLook _look = EnergyLook.Default;

    /// <summary>The sounds and effects of the gun in the hand; the owner sets it when the gun changes. The two impact presets are drawn with its effects.</summary>
    internal EnergyLook Look
    {
        get => _look;
        set
        {
            _look = value;
            _sparks = new ImpactLook { Fx = value.Effect(EnergyVfx.SparksHit) };
        }
    }

    internal SpecialEffects()
    {
        Look = EnergyLook.Default;
        _root = new GameObject("Energy disc flame and gravity voices");
        Object.DontDestroyOnLoad(_root);
        try
        {
            var discPrefab = (EnergyVfx.Disc ?? throw new InvalidOperationException("its model was not prepared")).Prefab;
            for (var index = 0; index < _discs.Length; index++)
            {
                var instance = Object.Instantiate(discPrefab, _root.transform, false);
                instance.name = "ricochet-disc-" + index;
                // The prefab's own streak stays off: the slot energy.disc draws the disc's trail.
                var trail = instance.GetComponent<TrailRenderer>();
                if (trail != null) trail.emitting = false;
                var blade = instance.transform.Find("cast-metal-rotor") ?? throw new InvalidOperationException("the prefab is missing its blade");
                _discs[index] = new DiscView
                {
                    Root = instance.transform,
                    Blade = blade,
                    Audio = EnergyAudio.Library.Voice(instance.transform)
                };
                instance.SetActive(false);
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("energylab-disc-not-drawn", "the ricochet disc is not drawn; the gun still fires: " + error); }
        _holeVoice = Child("singularity-voice", _root.transform);
        _crackleVoice = Child("flame-crackle-voice", _root.transform);
        _holeAudio = EnergyAudio.Library.Voice(_holeVoice);
        _crackleAudio = EnergyAudio.Library.Voice(_crackleVoice);
        _flameAudio = EnergyAudio.Library.Voice(_root.transform);
        for (var index = 0; index < _launchVoices.Length; index++)
            _launchVoices[index] = EnergyAudio.Library.Voice(Child("launch-voice-" + index, _root.transform));
    }

    private static Transform Child(string name, Transform parent)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }

    private static bool Alive(Object? value) => value is not null && value != null;
    private bool Ready => !_disposed && Alive(_root);

    internal void Origin(Vector3 position)
    {
        if (Ready) _root.transform.position = position;
    }

    private void BurstSound(int clip, float volume = 0.8f)
    {
        if (!Ready) return;
        _launchVoices[_launchCursor++ % _launchVoices.Length].Play(Look.Cue(Sounds[clip]), volume);
    }

    /// <summary>Starts a loop cue on <paramref name="voice"/> unless it is already playing that cue.</summary>
    private void LoopSound(AudioVoice voice, int clip, float volume)
    {
        if (!Ready || voice.IsPlaying(Look.Cue(Sounds[clip]))) return;
        voice.Play(Look.Cue(Sounds[clip]), volume);
    }


    /// <summary>The held flame jet from the muzzle along <paramref name="direction"/>. The jet reaches the weapon's
    /// full range; its puffs collide with level geometry, so a wall closer than that stops them.</summary>
    internal void Flame(Vector3 start, Vector3 direction, float distance)
    {
        if (!Ready) return;
        Origin(start);
        if (direction.sqrMagnitude < 0.0001f || distance <= 0.12f) { StopFlame(); return; }
        if (!_flaming)
        {
            _flaming = true;
            _flameKey = Fx.NewKey();
            BurstSound(FlameStart, 0.8f);
        }
        LoopSound(_flameAudio, FlameLoop, 0.5f);
        if (Look.Effect(EnergyVfx.FlameLine) is { } jet) Fx.Hold(jet, _flameKey, start, direction, distance, Color.white);
    }

    internal void StopFlame()
    {
        if (!_flaming) return;
        if (!_disposed)
        {
            _flameAudio.Stop();
            BurstSound(FlameStop, 0.5f);
        }
        _flaming = false;
    }

    /// <summary>Burning fuel landing at <paramref name="point"/>: at most one impact every
    /// <see cref="FlameImpactInterval"/> seconds, so a held jet does not stack hundreds of them. Every contact sizzles
    /// with the game's FlamerBurnFlesh; fire on a <paramref name="wall"/> also crackles, one crackle at a time.</summary>
    internal void FlameImpact(Vector3 point, float now, bool wall)
    {
        if (!Ready) return;
        if (now >= _nextFlameImpact)
        {
            _nextFlameImpact = now + FlameImpactInterval;
            if (Look.Effect(EnergyVfx.FlameHit) is { } hit) Fx.Play(hit, point, Vector3.up);
        }
        if (now >= _nextFlameImpactSound)
        {
            _nextFlameImpactSound = now + 0.25f;
            EnergyAudio.PlayAt(Look.Cue(Sounds[FlameImpactSound]), point, 0.42f);
        }
        if (!wall) return;
        _crackleVoice.position = point;
        if (_crackleAudio.Playing) return;
        _crackleAudio.Play(Look.Cue(Sounds[FlameCrackle]), 0.5f);
    }

    internal void BlastLaunch()
    {
        BurstSound(BlastLaunchSound, 0.65f);
        BurstSound(BlastCycle, 0.6f);
    }

    /// <summary>The shell of slot <paramref name="index"/> at <paramref name="position"/>: the ribbon of where it has been, ending at the shell, and its light. Called every frame it flies.</summary>
    internal void Shell(int index, Vector3 position, float now)
    {
        if (!Ready) return;
        var trail = _shellTrails[index] ??= Trails.Rent(position, ShellTrail);
        trail?.Move(position, 0f);
        PointLights.Hold(ref _shellLights[index], position, ShellLight, now);
    }

    /// <summary>The shell ended: its ribbon gathers into the last point and goes, its light is let go.</summary>
    internal void EndShell(int index)
    {
        _shellTrails[index]?.End();
        _shellTrails[index] = null;
        PointLights.Release(ref _shellLights[index]);
    }

    /// <summary>The blast gun's explosion, the same shared grenade-launcher blast the Ordnance launcher draws.</summary>
    internal void GrenadeExplosion(Vector3 position)
    {
        if (!Ready) return;
        EnergyAudio.PlayAt(Look.Cue(Sounds[BlastImpact]), position, 1f);
        HeavyBlast.Play(position);
    }

    internal void Sparks(Vector3 point, Vector3 normal)
    {
        if (!Ready) return;
        Impacts.Play(_sparks, point, normal);
        EnergyAudio.PlayAt(Look.Cue(Sounds[DiscBounce]), point, 0.7f);
    }

    internal void DiscImpact(Vector3 point, Vector3 direction)
    {
        if (!Ready) return;
        EnergyVfx.Play(Look.Effect(EnergyVfx.DiscHit), point, -direction);
        AudioKit.PostGameEvent(DiscHitSound, point);
    }

    internal void Disc(int index, Vector3 position, Vector3 direction, float now, bool launch = false)
    {
        if (!Ready) return;
        var view = _discs[index];
        if (view == null || !Alive(view.Root) || direction.sqrMagnitude < 0.0001f) return;
        direction.Normalize();
        if (launch)
        {
            Origin(position);
            view.Lift = Vector3.ProjectOnPlane(Vector3.up, direction);
            if (view.Lift.sqrMagnitude < 0.0001f)
                view.Lift = Vector3.ProjectOnPlane(ViewRotation * Vector3.right, direction);
            if (view.Lift.sqrMagnitude < 0.0001f)
                view.Lift = Vector3.ProjectOnPlane(Vector3.forward, direction);
            view.Lift = view.Lift.normalized;
            view.Root.position = position;
            view.Root.gameObject.SetActive(true);
            EnergyVfx.Play(Look.Effect(EnergyVfx.DiscLaunch), position, direction);
            view.Audio.Play(Look.Cue(Sounds[DiscFlight]), 0.6f); BurstSound(DiscLaunch, 0.8f);
        }
        view.Root.position = position;
        if (launch || _discKeys[index] == 0) _discKeys[index] = Fx.NewKey();
        if (Look.Effect(EnergyVfx.DiscTrail) is { } trail) Fx.Hold(trail, _discKeys[index], position, direction, float.NaN, Color.white);
        var lift = Vector3.ProjectOnPlane(view.Lift, direction);
        if (lift.sqrMagnitude < 0.0001f) lift = Vector3.ProjectOnPlane(Vector3.up, direction);
        if (lift.sqrMagnitude < 0.0001f) lift = Vector3.ProjectOnPlane(Vector3.right, direction);
        view.Lift = lift.normalized;
        var normal = (lift.normalized - direction * 0.55f).normalized;
        view.Blade.rotation = Quaternion.LookRotation(normal, direction) * Quaternion.AngleAxis(now * 720f, Vector3.forward);
    }

    internal void HideDisc(int index)
    {
        var view = _discs[index];
        if (view == null) return;
        if (view.Audio.Playing)
        {
            view.Audio.Stop();
            if (Ready) EnergyAudio.PlayAt(Look.Cue(Sounds[DiscStop]), view.Root.position, 0.35f);
        }
        if (Alive(view.Root)) view.Root.gameObject.SetActive(false);
    }

    /// <summary>The open well at <paramref name="scale"/> of its full size (<paramref name="radius"/> across), its disc flat and on <paramref name="position"/>, the point the field
    /// pulls and damages from; opening (<paramref name="activate"/>) takes the flying shell away at once.</summary>
    internal void Hole(Vector3 position, float scale, float radius, bool activate)
    {
        if (!Ready) return;
        var field = Look.Effect(EnergyVfx.HoleField);
        if (activate)
        {
            if (Look.Effect(EnergyVfx.HoleShell) is { } shell) Fx.Release(shell, _shellKey);
            _shellKey = 0;
            _holeKey = 0;
        }
        if (_holeKey == 0) _holeKey = Fx.NewKey();
        _holeVoice.position = position;
        if (field != null) Fx.Hold(field, _holeKey, position, Vector3.up, Mathf.Max(0.02f, scale) * radius, Color.white);
        if (!activate) return;
        EnergyVfx.Play(Look.Effect(EnergyVfx.HoleOpen), position, Vector3.up);
        // The well opens with one sound over its pulling hum, which lasts until it collapses.
        _holeAudio.Stop();
        EnergyAudio.PlayAt(Look.Cue(Sounds[HoleOpen]), position, 0.8f);
        LoopSound(_holeAudio, HoleField, 0.5f);
    }

    internal void HoleLaunch(Vector3 position, Vector3 direction)
    {
        if (!Ready) return;
        Origin(position);
        EnergyVfx.Play(Look.Effect(EnergyVfx.HoleLaunch), position, direction);
        BurstSound(HoleLaunchSound, 0.8f);
    }

    /// <summary>The flying well: its own effect (slot energy.hole.shell), <see cref="ShellShare"/> of its body radius across.</summary>
    internal void HoleProjectile(Vector3 position, float radius)
    {
        if (!Ready) return;
        _holeVoice.position = position;
        if (_shellKey == 0) _shellKey = Fx.NewKey();
        if (Look.Effect(EnergyVfx.HoleShell) is { } shell) Fx.Hold(shell, _shellKey, position, Vector3.up, radius * ShellShare, Color.white);
        LoopSound(_holeAudio, HoleFlight, 0.55f);
    }

    internal void CollapseHole()
    {
        _holeAudio.Stop();
        if (!Ready) return;
        EnergyAudio.PlayAt(Look.Cue(Sounds[HoleClose]), _holeVoice.position, 0.7f);
        EnergyVfx.Play(Look.Effect(EnergyVfx.HoleCollapse), _holeVoice.position, Vector3.up);
    }

    internal void HideHole()
    {
        if (Look.Effect(EnergyVfx.HoleShell) is { } shell) Fx.Release(shell, _shellKey);
        _holeKey = _shellKey = 0;
        _holeAudio.Stop();
    }

    internal void Clear()
    {
        if (_disposed) return;
        StopFlame();
        _crackleAudio.Stop();
        for (var index = 0; index < _discs.Length; index++) HideDisc(index);
        HideHole();
        for (var index = 0; index < ShellCapacity; index++) EndShell(index);
        foreach (var voice in _launchVoices) voice.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _flameAudio.Stop();
        _holeAudio.Stop();
        _crackleAudio.Stop();
        foreach (var voice in _launchVoices) voice.Stop();
        foreach (var view in _discs) view?.Audio.Stop();
        if (Alive(_root)) Object.Destroy(_root);
    }
}
