using System;
using System.Collections;
using ForgeAudio;
using ForgeVisual;
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
        internal TrailRenderer Trail = null!;
        internal AudioVoice Audio = null!;
        internal Vector3 Lift;
    }

    /// <summary>The members' default sizes: flame reach and explosion radius.</summary>
    private const float FlameSize = 8f, ExplosionSize = 2.2f;
    /// <summary>The gravity well while it flies, as a fraction of the open well.</summary>
    private const float ProjectileScale = 0.15f;
    /// <summary>The well's collapse flash comes when the `collapse` state throws its debris out.</summary>
    private const float CollapseFlashDelay = 0.28f;
    private const string FlameIgnite = "ignite", HoleOpenState = "open", HoleCollapseState = "collapse";
    private static readonly PointLightSpec FlameLight = new(new Color(1f, 0.45f, 0.12f), 6f, 4f, flicker: 0.35f);
    private static readonly PointLightSpec FlameImpactLight = new(new Color(1f, 0.45f, 0.12f), 2.5f, 2f, flicker: 0.4f);
    private static readonly PointLightSpec ExplosionLight = new(new Color(1f, 0.62f, 0.28f), 8f, 3f, 0.5f, 0.25f);
    private static readonly PointLightSpec SparksLight = new(new Color(1f, 0.7f, 0.4f), 2.5f, 2f, 0.12f, 0.3f);
    private static readonly PointLightSpec HoleLight = new(new Color(0.55f, 0.15f, 1f), 6f, 2f, flicker: 0.2f);
    private static readonly PointLightSpec HoleOpenLight = new(new Color(0.6f, 0.3f, 1f), 9f, 5f, 0.4f);
    private static readonly PointLightSpec HoleCollapseLight = new(new Color(0.75f, 0.5f, 1f), 8f, 4f, 0.35f);
    /// <summary>The Blast Gun shell: the plasma orb of the bundle, tinted to the blast's orange and drawn small.</summary>
    private static readonly Color ShellTint = new(1f, 0.6f, 0.25f);
    private static readonly PointLightSpec ShellLight = new(new Color(1f, 0.62f, 0.28f), 3f, 1.5f, flicker: 0.2f);
    private const float ShellSize = 0.09f, OrbSize = 0.3f;
    private const int ShellCapacity = 64;
    private const float FlameImpactInterval = 0.08f;
    private const int FlameStart = 0, FlameCrackle = 1, FlameImpactSound = 2, DiscLaunch = 3, DiscFlight = 4,
        DiscBounce = 5, HoleLaunchSound = 6, HoleFlight = 7, HoleOpen = 8, HoleClose = 9, BlastLaunchSound = 10, BlastCycle = 11,
        BlastImpact = 12, FlameLoop = 13, FlameStop = 14, DiscStop = 15, HoleField = 16;
    /// <summary>The game's own blade-into-flesh sound (melee_knife_hit_flesh_regular) for a disc cutting an enemy.</summary>
    private const uint DiscHitSound = 3633778169;

    private readonly GameObject _root;
    private readonly FxInstancePool _pool = new("Energy disc flame and gravity effects");
    private readonly DiscView[] _discs = new DiscView[3];
    private readonly AudioVoice _flameAudio, _holeAudio, _crackleAudio;
    private readonly Transform _holeVoice, _crackleVoice;
    private readonly AudioVoice[] _launchVoices = new AudioVoice[3];
    /// <summary>The hits drawn through ForgeVisual's impact presets: a spark burst off a surface, a disc biting in, a blast.</summary>
    private readonly ImpactLook _sparks, _discHit, _explosion;
    private readonly FxInstance?[] _shells = new FxInstance?[ShellCapacity];
    private readonly int[] _shellLights = new int[ShellCapacity];
    private FxInstance? _flame, _hole;
    private int _flameLight, _flameImpactLight, _holeLight;
    private int _launchCursor;
    private float _nextFlameImpact, _nextFlameImpactSound;
    private bool _flaming, _disposed, _prepared;
    private static readonly string[] Sounds = { "flame-start", "flame-crackle", "flame-hit", "disc-fire",
        "disc-fly-loop", "disc-bounce", "gravity-fire", "gravity-fly-loop", "gravity-open", "gravity-close", "blast-fire", "blast-cycle",
        "blast-explode", "flame-loop", "flame-stop", "disc-drop", "gravity-field-loop" };

    internal Quaternion ViewRotation { get; set; } = Quaternion.identity;

    internal SpecialEffects()
    {
        _sparks = new ImpactLook { Model = EnergyVfx.Sparks, Light = SparksLight, LightLift = 0.1f };
        _discHit = new ImpactLook { Model = EnergyVfx.Sparks, ModelScale = 0.7f, Light = SparksLight };
        _explosion = new ImpactLook { Model = EnergyVfx.Explosion, Light = ExplosionLight };
        _root = new GameObject("Energy disc flame and gravity voices");
        Object.DontDestroyOnLoad(_root);
        try
        {
            var trailMaterial = TrailMaterial();
            var discPrefab = EnergyVfx.Disc.Prefab;
            for (var index = 0; index < _discs.Length; index++)
            {
                var instance = Object.Instantiate(discPrefab, _root.transform, false);
                instance.name = "ricochet-disc-" + index;
                var trail = instance.GetComponent<TrailRenderer>();
                var blade = instance.transform.Find("cast-metal-rotor");
                if (trail == null || blade == null) throw new InvalidOperationException("Disc prefab is missing its blade or trail.");
                trail.sharedMaterial = trailMaterial;
                _discs[index] = new DiscView { Root = instance.transform, Blade = blade,
                    Audio = EnergyAudio.Library.Voice(instance.transform), Trail = trail };
                instance.SetActive(false);
            }
        }
        catch { Object.Destroy(_root); _pool.Dispose(); throw; }
        _holeVoice = Child("singularity-voice", _root.transform);
        _crackleVoice = Child("flame-crackle-voice", _root.transform);
        _holeAudio = EnergyAudio.Library.Voice(_holeVoice);
        _crackleAudio = EnergyAudio.Library.Voice(_crackleVoice);
        _flameAudio = EnergyAudio.Library.Voice(_root.transform);
        for (var index = 0; index < _launchVoices.Length; index++)
            _launchVoices[index] = EnergyAudio.Library.Voice(Child("launch-voice-" + index, _root.transform));
    }

    internal IEnumerator Prepare()
    {
        if (_prepared) yield break;
        foreach (var step in _pool.Prepare(EnergyVfx.Flame, 2)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.FlameImpact, 8)) yield return step;
        foreach (var step in Impacts.Prepare(_explosion, 4)) yield return step;
        foreach (var step in Impacts.Prepare(_sparks, 8)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.BlackHole, 2)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.Orb, 4, ShellTint)) yield return step;
        _prepared = true;
    }

    private static Transform Child(string name, Transform parent)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }

    /// <summary>The bundle's trail material, read from `forge_plasma_orb`'s world-space trail layer, so the disc's
    /// streak is drawn by the same shader and texture as the orb's.</summary>
    private static Material TrailMaterial()
    {
        var layer = EnergyVfx.Orb.Prefab.transform.Find("trail")
            ?? throw new InvalidOperationException(EnergyVfx.Orb.Name + " has no trail layer for the disc streak.");
        var renderer = layer.GetComponent<ParticleSystemRenderer>();
        var material = Alive(renderer) ? renderer.trailMaterial : null;
        return Alive(material) ? material! : throw new InvalidOperationException(EnergyVfx.Orb.Name + "'s trail layer has no trail material.");
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
        _launchVoices[_launchCursor++ % _launchVoices.Length].Play(Sounds[clip], volume);
    }

    /// <summary>Starts a loop cue on <paramref name="voice"/> unless it is already playing that cue.</summary>
    private void LoopSound(AudioVoice voice, int clip, float volume)
    {
        if (!Ready || voice.IsPlaying(Sounds[clip])) return;
        voice.Play(Sounds[clip], volume);
    }

    private void OneShot(ModelHandle model, Vector3 position, Quaternion rotation, float scale = 1f)
    {
        var effect = _pool.Take(model);
        effect.Place(position, rotation, scale);
        effect.Start();
    }

    private static Quaternion Facing(Vector3 direction)
        => direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Quaternion.LookRotation(Vector3.up);

    /// <summary>The held flame jet from the muzzle along <paramref name="direction"/>. The jet reaches the weapon's
    /// full range; its puffs collide with level geometry, so a wall closer than that stops them.</summary>
    internal void Flame(Vector3 start, Vector3 direction, float distance, float now)
    {
        if (!Ready) return;
        Origin(start);
        if (direction.sqrMagnitude < 0.0001f || distance <= 0.12f) { StopFlame(); return; }
        if (!_flaming)
        {
            _flaming = true;
            BurstSound(FlameStart, 0.8f);
        }
        LoopSound(_flameAudio, FlameLoop, 0.5f);
        var ignite = _flame == null;
        _flame ??= _pool.Take(EnergyVfx.Flame).Hold();
        _flame.Place(start, Facing(direction), distance / FlameSize);
        _flame.Keep();
        if (ignite) _flame.PlayState(FlameIgnite);
        // The burning cloud's light sits a little ahead of the nozzle, where the prefab's glow layer is.
        PointLights.Hold(ref _flameLight, start + direction.normalized * Mathf.Min(1.2f, distance), FlameLight, now);
    }

    internal void StopFlame()
    {
        if (!_flaming && _flame == null && _flameLight == 0 && _flameImpactLight == 0) return;
        if (_flaming && !_disposed)
        {
            _flameAudio.Stop();
            BurstSound(FlameStop, 0.5f);
        }
        _flaming = false;
        _flame?.Release();
        _flame = null;
        PointLights.Release(ref _flameLight);
        PointLights.Release(ref _flameImpactLight);
    }

    /// <summary>Burning fuel landing at <paramref name="point"/>: at most one impact every
    /// <see cref="FlameImpactInterval"/> seconds, so a held jet does not stack hundreds of them. Every contact sizzles
    /// with the game's FlamerBurnFlesh; fire on a <paramref name="wall"/> also crackles, one crackle at a time.</summary>
    internal void FlameImpact(Vector3 point, float now, bool wall)
    {
        if (!Ready) return;
        PointLights.Hold(ref _flameImpactLight, point, FlameImpactLight, now);
        if (now >= _nextFlameImpact)
        {
            _nextFlameImpact = now + FlameImpactInterval;
            OneShot(EnergyVfx.FlameImpact, point, Quaternion.LookRotation(Vector3.up));
        }
        if (now >= _nextFlameImpactSound)
        {
            _nextFlameImpactSound = now + 0.25f;
            EnergyAudio.PlayAt(Sounds[FlameImpactSound], point, 0.42f);
        }
        if (!wall) return;
        _crackleVoice.position = point;
        if (_crackleAudio.Playing) return;
        _crackleAudio.Play(Sounds[FlameCrackle], 0.5f);
    }

    internal void BlastLaunch()
    {
        BurstSound(BlastLaunchSound, 0.65f);
        BurstSound(BlastCycle, 0.6f);
    }

    /// <summary>The shell of slot <paramref name="index"/> at <paramref name="position"/>: a small glowing orb with its streak and light.</summary>
    internal void Shell(int index, Vector3 position, float now)
    {
        if (!Ready) return;
        var appeared = _shells[index] == null;
        var shell = _shells[index] ??= _pool.Take(EnergyVfx.Orb, ShellTint).Hold();
        shell.Place(position, Quaternion.identity, ShellSize / OrbSize);
        shell.Keep();
        if (appeared)
        {
            shell.Emit("shell", false);
            shell.PlayState("launch");
        }
        PointLights.Hold(ref _shellLights[index], position, ShellLight, now);
    }

    internal void EndShell(int index)
    {
        _shells[index]?.Release();
        _shells[index] = null;
        PointLights.Release(ref _shellLights[index]);
    }

    internal void GrenadeExplosion(Vector3 position, float radius, float now)
    {
        if (!Ready || !float.IsFinite(radius)) return;
        radius = Math.Clamp(radius, 0.35f, 8f);
        EnergyAudio.PlayAt(Sounds[BlastImpact], position, 1f);
        Impacts.Play(_explosion, position, Vector3.zero, radius / ExplosionSize, Mathf.Clamp(radius / ExplosionSize, 0.6f, 1.4f));
    }

    internal void Sparks(Vector3 point, Vector3 normal, float now)
    {
        if (!Ready) return;
        Impacts.Play(_sparks, point, normal);
        EnergyAudio.PlayAt(Sounds[DiscBounce], point, 0.7f);
    }

    internal void DiscImpact(Vector3 point, Vector3 direction, float now)
    {
        if (!Ready) return;
        Impacts.Play(_discHit, point, -direction, 1f, 0.7f);
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
            view.Trail.Clear();
            view.Trail.emitting = true;
            view.Audio.Play(Sounds[DiscFlight], 0.6f); BurstSound(DiscLaunch, 0.8f);
        }
        view.Root.position = position;
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
        if (Alive(view.Trail)) view.Trail.emitting = false;
        if (view.Audio.Playing)
        {
            view.Audio.Stop();
            if (Ready) EnergyAudio.PlayAt(Sounds[DiscStop], view.Root.position, 0.35f);
        }
        if (Alive(view.Root)) view.Root.gameObject.SetActive(false);
    }

    internal void Hole(Vector3 position, float scale, float radius, bool activate)
    {
        if (!Ready) return;
        ShowHole(position, Mathf.Max(0.02f, scale) * radius / 5f, faceCamera: true);
        if (!activate) return;
        _hole!.PlayState(HoleOpenState);
        PointLights.Flash(position, HoleOpenLight, Time.time);
        // The well opens with one sound over its pulling hum, which lasts until it collapses.
        _holeAudio.Stop();
        EnergyAudio.PlayAt(Sounds[HoleOpen], position, 0.8f);
        LoopSound(_holeAudio, HoleField, 0.5f);
    }

    internal void HoleLaunch(Vector3 position)
    {
        if (!Ready) return;
        Origin(position);
        BurstSound(HoleLaunchSound, 0.8f);
    }

    internal void HoleProjectile(Vector3 position, float radius)
    {
        if (!Ready) return;
        ShowHole(position, ProjectileScale * radius / 0.18f);
        LoopSound(_holeAudio, HoleFlight, 0.55f);
    }

    private void ShowHole(Vector3 position, float scale, bool faceCamera = false)
    {
        _holeVoice.position = position;
        _hole ??= _pool.Take(EnergyVfx.BlackHole).Hold();
        var shown = position;
        var camera = faceCamera ? Player.PlayerManager.GetLocalPlayerAgent()?.FPItemHolder?.m_LookCamera : null;
        if (camera != null)
        {
            // The field is a camera-facing sheet up to 5 m across (5 m at scale 1); centred on a floor or wall it is cut
            // along that surface. Draw it nearer the camera, shrunk to keep its on-screen size, so the surface stays behind.
            var toward = camera.transform.position - position;
            var distance = toward.magnitude;
            if (distance > 0.5f)
            {
                var pull = Mathf.Min(5f * scale, distance * 0.5f);
                shown += toward * (pull / distance);
                scale *= (distance - pull) / distance;
            }
        }
        _hole.Place(shown, Quaternion.identity, scale);
        _hole.Keep();
        PointLights.Hold(ref _holeLight, position, HoleLight, Time.time, Mathf.Clamp01(scale));
    }

    internal void CollapseHole()
    {
        _holeAudio.Stop();
        if (!Ready) return;
        EnergyAudio.PlayAt(Sounds[HoleClose], _holeVoice.position, 0.7f);
        _hole?.PlayState(HoleCollapseState);
        PointLights.Flash(_holeVoice.position, HoleCollapseLight, Time.time, delay: CollapseFlashDelay);
    }

    internal void HideHole()
    {
        _holeAudio.Stop();
        _hole?.Release();
        _hole = null;
        PointLights.Release(ref _holeLight);
    }

    internal void Clear()
    {
        if (_disposed) return;
        StopFlame();
        _crackleAudio.Stop();
        for (var index = 0; index < _discs.Length; index++) HideDisc(index);
        HideHole();
        for (var index = 0; index < ShellCapacity; index++) EndShell(index);
        _pool.Clear();
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
        _pool.Dispose();
        if (Alive(_root)) Object.Destroy(_root);
    }
}
