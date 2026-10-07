using System;
using System.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Presentation of native contacts and projectile phases; effects finish independently.</summary>
internal sealed class SpecialEffects : IDisposable
{
    private sealed class DiscView
    {
        internal Transform Root = null!;
        internal Transform Blade = null!;
        internal TrailRenderer Trail = null!;
        internal AudioSource Audio = null!;
        internal Vector3 Lift;
    }

    private const string FlameMember = "forge_flame", FlameImpactMember = "forge_flame_impact", ExplosionMember = "forge_explosion",
        SparksMember = "forge_sparks", OrbMember = "forge_plasma_orb", HoleMember = "forge_black_hole";
    /// <summary>The members' default sizes: flame reach and explosion radius.</summary>
    private const float FlameSize = 8f, ExplosionSize = 2.2f;
    /// <summary>The gravity well while it flies, as a fraction of the open well.</summary>
    private const float ProjectileScale = 0.15f;
    /// <summary>The well's collapse flash comes when the `collapse` state throws its debris out.</summary>
    private const float CollapseFlashDelay = 0.28f;
    private const string FlameIgnite = "ignite", HoleOpenState = "open", HoleCollapseState = "collapse";
    private static readonly LightSpec FlameLight = new(new Color(1f, 0.45f, 0.12f), 6f, 4f, flicker: 0.35f);
    private static readonly LightSpec FlameImpactLight = new(new Color(1f, 0.45f, 0.12f), 2.5f, 2f, flicker: 0.4f);
    private static readonly LightSpec ExplosionLight = new(new Color(1f, 0.62f, 0.28f), 8f, 3f, 0.5f, 0.25f);
    private static readonly LightSpec SparksLight = new(new Color(1f, 0.7f, 0.4f), 2.5f, 2f, 0.12f, 0.3f);
    private static readonly LightSpec HoleLight = new(new Color(0.55f, 0.15f, 1f), 6f, 2f, flicker: 0.2f);
    private static readonly LightSpec HoleOpenLight = new(new Color(0.6f, 0.3f, 1f), 9f, 5f, 0.4f);
    private static readonly LightSpec HoleCollapseLight = new(new Color(0.75f, 0.5f, 1f), 8f, 4f, 0.35f);
    private const float FlameImpactInterval = 0.08f;
    private const int FlameStart = 0, FlameCrackle = 1, FlameImpactSound = 2, DiscLaunch = 3, DiscFlight = 4,
        DiscBounce = 5, HoleLaunchSound = 6, HoleFlight = 7, HoleOpen = 8, HoleClose = 9, BlastLaunchSound = 10, BlastCycle = 11,
        BlastImpact = 12, FlameLoop = 13, FlameStop = 14, DiscStop = 15, HoleField = 16;
    /// <summary>The game's own blade-into-flesh sound (melee_knife_hit_flesh_regular) for a disc cutting an enemy.</summary>
    private const uint DiscHitSound = 3633778169;

    private readonly GameObject _root;
    private readonly VfxPool _pool = new("Energy disc flame and gravity effects");
    private readonly DiscView[] _discs = new DiscView[3];
    private readonly AudioSource _flameAudio, _holeAudio, _crackleAudio;
    private readonly CellSoundPlayer[] _hitVoices = new CellSoundPlayer[3];
    private readonly Transform _holeVoice, _crackleVoice;
    private readonly AudioSource[] _launchVoices = new AudioSource[3];
    private readonly AudioSource[] _blastVoices = new AudioSource[6];
    private VfxInstance? _flame, _hole;
    private int _flameLight, _flameImpactLight, _holeLight;
    private int _blastCursor, _launchCursor, _hitCursor;
    private float _nextFlameImpact, _nextFlameImpactSound;
    private bool _flaming, _disposed, _prepared;
    private static readonly string[] Sounds = { "flame-start", "flame-crackle", "flame-hit", "disc-fire",
        "disc-fly-loop", "disc-bounce", "gravity-fire", "gravity-fly-loop", "gravity-open", "gravity-close", "blast-fire", "blast-cycle",
        "blast-explode", "flame-loop", "flame-stop", "disc-drop", "gravity-field-loop" };

    internal Quaternion ViewRotation { get; set; } = Quaternion.identity;

    internal SpecialEffects()
    {
        _root = new GameObject("Energy disc flame and gravity voices");
        try
        {
            Object.DontDestroyOnLoad(_root);
            // Load the prefabs up front, so a missing bundle fails the weapon mode when it is chosen.
            foreach (var member in new[] { FlameMember, FlameImpactMember, ExplosionMember, SparksMember, OrbMember, HoleMember })
                VfxLibrary.Prefab(member);
            var trailMaterial = TrailMaterial();
            var discPrefab = VfxLibrary.Prefab("forge_ricochet_disc");
            for (var index = 0; index < _discs.Length; index++)
            {
                var instance = Object.Instantiate(discPrefab, _root.transform, false);
                instance.name = "ricochet-disc-" + index;
                var trail = instance.GetComponent<TrailRenderer>();
                var blade = instance.transform.Find("cast-metal-rotor");
                if (trail == null || blade == null) throw new InvalidOperationException("Disc prefab is missing its blade or trail.");
                trail.sharedMaterial = trailMaterial;
                _discs[index] = new DiscView { Root = instance.transform, Blade = blade,
                    Audio = EnergySound.Voice(instance.transform), Trail = trail };
                instance.SetActive(false);
            }
            _holeVoice = Child("singularity-voice", _root.transform);
            _crackleVoice = Child("flame-crackle-voice", _root.transform);
            _holeAudio = EnergySound.Voice(_holeVoice);
            for (var index = 0; index < _hitVoices.Length; index++) _hitVoices[index] = new CellSoundPlayer(Vector3.zero);
            _crackleAudio = EnergySound.Voice(_crackleVoice);
            _flameAudio = EnergySound.Voice(_root.transform);
            for (var index = 0; index < _launchVoices.Length; index++)
                _launchVoices[index] = EnergySound.Voice(Child("launch-voice-" + index, _root.transform));
            for (var index = 0; index < _blastVoices.Length; index++)
                _blastVoices[index] = EnergySound.Voice(Child("blast-voice-" + index, _root.transform));
        }
        catch { Dispose(); throw; }
    }

    internal IEnumerator Prepare()
    {
        if (_prepared) yield break;
        foreach (var step in _pool.Prepare(FlameMember, 2)) yield return step;
        foreach (var step in _pool.Prepare(FlameImpactMember, 8)) yield return step;
        foreach (var step in _pool.Prepare(ExplosionMember, 4)) yield return step;
        foreach (var step in _pool.Prepare(SparksMember, 8)) yield return step;
        foreach (var step in _pool.Prepare(HoleMember, 2)) yield return step;
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
        var layer = VfxLibrary.Prefab(OrbMember).transform.Find("trail")
            ?? throw new InvalidOperationException(OrbMember + " has no trail layer for the disc streak.");
        var renderer = layer.GetComponent<ParticleSystemRenderer>();
        var material = Alive(renderer) ? renderer.trailMaterial : null;
        return Alive(material) ? material! : throw new InvalidOperationException(OrbMember + "'s trail layer has no trail material.");
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
        var voice = _launchVoices[_launchCursor++ % _launchVoices.Length];
        EnergySound.Stop(voice);
        EnergySound.PlayCue(voice, Sounds[clip], volume);
    }

    /// <summary>Starts a loop cue on <paramref name="voice"/> unless it is already playing that cue.</summary>
    private void LoopSound(AudioSource voice, int clip, float volume)
    {
        if (!Ready || !Alive(voice)) return;
        if (EnergySound.PlayingCue(voice, Sounds[clip])) return;
        EnergySound.Stop(voice);
        EnergySound.PlayCue(voice, Sounds[clip], volume);
    }

    private void OneShot(string member, Vector3 position, Quaternion rotation, float scale = 1f)
    {
        var effect = _pool.Take(member);
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
        _flame ??= _pool.Take(FlameMember).Hold();
        _flame.Place(start, Facing(direction), distance / FlameSize);
        _flame.Keep();
        if (ignite) _flame.PlayState(FlameIgnite);
        // The burning cloud's light sits a little ahead of the nozzle, where the prefab's glow layer is.
        EnergyLights.Hold(ref _flameLight, start + direction.normalized * Mathf.Min(1.2f, distance), FlameLight, now);
    }

    internal void StopFlame()
    {
        if (!_flaming && _flame == null && _flameLight == 0 && _flameImpactLight == 0) return;
        if (_flaming && !_disposed)
        {
            if (Alive(_flameAudio)) EnergySound.Stop(_flameAudio);
            BurstSound(FlameStop, 0.5f);
        }
        _flaming = false;
        _flame?.Release();
        _flame = null;
        EnergyLights.Release(ref _flameLight);
        EnergyLights.Release(ref _flameImpactLight);
    }

    /// <summary>Burning fuel landing at <paramref name="point"/>: at most one impact every
    /// <see cref="FlameImpactInterval"/> seconds, so a held jet does not stack hundreds of them. Every contact sizzles
    /// with the game's FlamerBurnFlesh; fire on a <paramref name="wall"/> also crackles, one crackle at a time.</summary>
    internal void FlameImpact(Vector3 point, float now, bool wall)
    {
        if (!Ready) return;
        EnergyLights.Hold(ref _flameImpactLight, point, FlameImpactLight, now);
        if (now >= _nextFlameImpact)
        {
            _nextFlameImpact = now + FlameImpactInterval;
            OneShot(FlameImpactMember, point, Quaternion.LookRotation(Vector3.up));
        }
        if (now >= _nextFlameImpactSound)
        {
            _nextFlameImpactSound = now + 0.25f;
            EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[FlameImpactSound], point, 0.42f);
        }
        if (!wall || !Alive(_crackleAudio)) return;
        _crackleVoice.position = point;
        if (_crackleAudio.isPlaying) return;
        EnergySound.PlayCue(_crackleAudio, Sounds[FlameCrackle], 0.5f);
    }

    internal void BlastLaunch()
    {
        BurstSound(BlastLaunchSound, 0.65f);
        BurstSound(BlastCycle, 0.6f);
    }

    internal void GrenadeExplosion(Vector3 position, float radius, float now)
    {
        if (!Ready || !float.IsFinite(radius)) return;
        radius = Math.Clamp(radius, 0.35f, 8f);
        EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[BlastImpact], position, 1f);
        OneShot(ExplosionMember, position, Quaternion.identity, radius / ExplosionSize);
        EnergyLights.Flash(position, ExplosionLight, now, Mathf.Clamp(radius / ExplosionSize, 0.6f, 1.4f));
    }

    internal void Sparks(Vector3 point, Vector3 normal, float now)
    {
        if (!Ready) return;
        OneShot(SparksMember, point, Facing(normal));
        EnergyLights.Flash(point + normal * 0.1f, SparksLight, now);
        EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[DiscBounce], point, 0.7f);
    }

    internal void DiscImpact(Vector3 point, Vector3 direction, float now)
    {
        if (!Ready) return;
        OneShot(SparksMember, point, Facing(-direction), 0.7f);
        EnergyLights.Flash(point, SparksLight, now, 0.7f);
        _hitVoices[_hitCursor].Post(DiscHitSound, point);
        _hitCursor = (_hitCursor + 1) % _hitVoices.Length;
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
            EnergySound.PlayCue(view.Audio, Sounds[DiscFlight], 0.6f); BurstSound(DiscLaunch, 0.8f);
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
        if (Alive(view.Audio) && view.Audio.isPlaying)
        {
            EnergySound.Stop(view.Audio);
            if (Ready) EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[DiscStop], view.Root.position, 0.35f);
        }
        if (Alive(view.Root)) view.Root.gameObject.SetActive(false);
    }

    internal void Hole(Vector3 position, float scale, float radius, bool activate)
    {
        if (!Ready) return;
        ShowHole(position, Mathf.Max(0.02f, scale) * radius / 5f, faceCamera: true);
        if (!activate) return;
        _hole!.PlayState(HoleOpenState);
        EnergyLights.Flash(position, HoleOpenLight, Time.time);
        // The well opens with one sound over its pulling hum, which lasts until it collapses.
        if (Alive(_holeAudio)) EnergySound.Stop(_holeAudio);
        EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[HoleOpen], position, 0.8f);
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
        _hole ??= _pool.Take(HoleMember).Hold();
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
        EnergyLights.Hold(ref _holeLight, position, HoleLight, Time.time, Mathf.Clamp01(scale));
    }

    internal void CollapseHole()
    {
        if (Alive(_holeAudio)) EnergySound.Stop(_holeAudio);
        if (!Ready) return;
        EnergySound.PlaySpatial(_blastVoices, ref _blastCursor, Sounds[HoleClose], _holeVoice.position, 0.7f);
        _hole?.PlayState(HoleCollapseState);
        EnergyLights.Flash(_holeVoice.position, HoleCollapseLight, Time.time, delay: CollapseFlashDelay);
    }

    internal void HideHole()
    {
        if (Alive(_holeAudio)) EnergySound.Stop(_holeAudio);
        _hole?.Release();
        _hole = null;
        EnergyLights.Release(ref _holeLight);
    }

    internal void Clear()
    {
        if (_disposed) return;
        StopFlame();
        if (Alive(_crackleAudio)) EnergySound.Stop(_crackleAudio);
        for (var index = 0; index < _discs.Length; index++) HideDisc(index);
        HideHole();
        _pool.Clear();
        foreach (var voice in _launchVoices) if (Alive(voice)) EnergySound.Stop(voice);
        foreach (var voice in _blastVoices) if (Alive(voice)) EnergySound.Stop(voice);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (Alive(_flameAudio)) EnergySound.Stop(_flameAudio);
        if (Alive(_holeAudio)) EnergySound.Stop(_holeAudio);
        foreach (var voice in _hitVoices) voice?.Recycle();
        if (Alive(_crackleAudio)) EnergySound.Stop(_crackleAudio);
        foreach (var voice in _launchVoices) if (Alive(voice)) EnergySound.Stop(voice);
        foreach (var voice in _blastVoices) if (Alive(voice)) EnergySound.Stop(voice);
        _pool.Dispose();
        if (Alive(_root)) Object.Destroy(_root);
    }
}
