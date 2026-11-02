using System;
using System.Collections.Generic;
using ForgeAudio;
using ForgeOrdnance;
using Player;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceFrames;

/// <summary>One suit as every peer sees it, driven only by its record: falling, standing empty, being boarded, worn, being left,
/// wrecked. The suit never moves while it is boarded or left: its opening parts (<see cref="FrameSpec.Opening"/>) move out on the
/// model's "open" clip, timed by <see cref="FrameSequence"/>, with steam from the back's seams, the
/// pilot standing behind it vanishes as it stands open (boarding) or plays the leaving motion out of it (hops out backwards,
/// <see cref="PilotBody"/>, ForgeVisual's body clip), and the parts close. Every
/// peer derives this from the host's state start, so a late joiner sees the same point. While worn the suit follows its pilot:
/// the local pilot's suit follows the game's smoothed character position (the same one the camera follows, so the suit never
/// jumps against the view between physics steps), a remote pilot's suit the replicated position. The pilot's own person is not
/// drawn while inside (the game's switch for a remote pilot's model, then the never-written <c>forceRenderingOff</c> on its
/// renderers; for the local pilot only its third-person shadow model). The local pilot's own suit only casts its shadow from the
/// moment the pilot is inside (the camera may move anywhere in it, crouching included, without seeing its walls); the pilot sees
/// the suit on its own first-person body instead (ForgeVisual's <see cref="ForgeVisual.FirstPersonWear"/>, <see cref="LocalPilot"/>). A worn suit moves exactly as its pilot's own game body
/// does (walking, running, crouching, jumping, holding, aiming and reloading whatever is in hand), scaled to the suit, and holds a copy
/// of the pilot's own item in its right hand (<see cref="PoseFromPilot"/>); an empty suit (arriving, standing, being boarded or left,
/// wrecked) stands in its rest pose, completely still. The pilot's game footsteps, jump and landing sounds are claimed through ForgeAudio (<see cref="AudioKit.ClaimBodySounds"/>): the suit plays its own footsteps,
/// jump and landing for everyone. The pilot's flashlight becomes the suit's lamp (wider, longer, warm), with a glow on the suit
/// while it is on.</summary>
internal sealed partial class FrameVisual : IBodySoundHandler
{
    // This package's frame work, named in ForgeCore's slow-frame line.

    private const float FallHeight = 40f;
    internal const float FallSeconds = 2.0f;
    /// <summary>The whole arrival state: the fall, then the settle before the suit can be boarded.</summary>
    internal const float ArriveSeconds = FallSeconds + FrameSequence.LandSettleSeconds;
    // Boarding: when the boot sound (the power-on starting at the black, FrameSequence.Black) and, for the other players, the ready voice (the
    // power-on's NEURAL LINK OK) play, or -1; the pilot plays the voice with the power-on on their own clock (LocalPilot).
    private float _bootAt = -1f, _voiceAt = -1f;
    private const float HitboxScale = 1.3f;
    private const float ShakeRange = 40f;
    /// <summary>Metres the renderers' bounds grow on every side: the built bounds cover the opening clips only, a worn suit's limbs
    /// reach wherever its pilot's do.</summary>
    private const float LimbReach = 1f;

    private readonly FrameUnit _unit;
    private readonly GameObject _root;
    private readonly Animation? _animation;
    private readonly SkinnedMeshRenderer[] _renderers;
    private readonly Transform? _muzzle, _chest, _head, _model;
    // The opening: the model's "open" and "close" clips (every part's rigid path; the open clip's first frame and the close
    // clip's last are shut), their sounds (read off the clips), the back's parts (an ejection's steam) and the parts beside the
    // back's middle seam (the opening's steam).
    private readonly AnimationClip? _open, _close;
    private readonly PlateEvent[] _openEvents = Array.Empty<PlateEvent>(), _closeEvents = Array.Empty<PlateEvent>();
    private readonly Transform[] _backParts, _seamParts;
    /// <summary>When the shown state started on the game clock (moved back by the host's elapsed time for a state joined part-way).</summary>
    internal float StateStart => _stateStart;
    /// <summary>The helmet top above the model's head bone, metres. Starts as <see cref="HelmetEstimate"/> and is replaced once by the
    /// highest vertex of the posed meshes (<see cref="MeasureHelmet"/>) after the bones have settled: a skinned renderer's bounds are
    /// not the posed shape, neither on the spawn frame nor later.</summary>
    private float _helmetAboveHead = HelmetEstimate;
    private const float HelmetEstimate = 0.3f;
    /// <summary>Where the suit's name sits in the world: <see cref="NameGap"/> over the helmet top, which follows the head bone every
    /// frame; the one position for an idle suit's plate and a pilot's game marker.</summary>
    internal Vector3 NameAt
    {
        get
        {
            var head = _head != null ? _head.position : _root.transform.position + Vector3.up * 1.9f;
            return new Vector3(head.x, head.y + _helmetAboveHead + NameGap, head.z);
        }
    }
    private bool _plateOpen, _eventsClosing;
    private int _eventStep;
    private bool _ownHidden;
    private readonly List<Renderer> _forcedOff = new();
    private readonly ForgeVisual.AttachedFx? _flash;
    private int _tracerFrame, _tracersThisFrame;
    private readonly List<(Collider Collider, Vector3 Size, float Radius, float Height)> _hitboxes = new();

    private FrameState _shown = (FrameState)255;
    private ulong _shownPilot;
    private PlayerAgent? _pilot;
    private PilotBody? _body;
    private bool _pilotLocal, _hidden, _landed, _airborne, _silenced;
    private BodySoundClaim? _bodySounds;
    private float _arriveStart, _stateStart, _nextHideCheck;
    private OrdnanceDrop? _drop;
    private float _yaw, _seenBoost, _nextPilotTry;
    private bool _seenSiege;
    private readonly string _identity;
    private float _seenArmor;
    private bool _seenShieldUp = true;
    private float _nextShieldHitSound;
    private bool _armorAlarm;
    private AudioHandle? _servo, _boostLoop, _siegeDeploy;
    private bool _seenDeploy;
    private int _servoDimension = -1, _boostDimension = -1;
    private float _seenShield = float.PositiveInfinity;
    private readonly FrameRocketPod? _rocketPod;
    // The heavy object the pilot carries (CarryHeavy models): on the suit's back for everyone else, out of the pilot's own hands.
    private readonly FrameCarry _carry = new();
    // The pilot's flashlight while it is the suit's lamp.
    private Light? _light;
    private float _lightRange, _lightAngle, _lightIntensity, _appliedRange;
    private Color _lightColour;
    private ForgeVisual.GlowSpot? _lamp;
    private static readonly Color LampColour = new(1f, 0.85f, 0.6f, 0.85f);
    private const float LampSize = 0.09f;

    private FrameVisual(FrameUnit unit, GameObject root)
    {
        _unit = unit;
        _identity = FrameAudio.Identity(unit);
        _seenArmor = unit.Record.Armor;
        _root = root;
        _renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        _animation = root.GetComponentInChildren<Animation>(true);
        if (_animation == null) Plugin.Error("Exosuit model " + unit.Record.Spec.Model + " has no Animation; it does not visibly open.");
        _muzzle = Find(root.transform, "hand.R");
        _chest = Find(root.transform, "chest");
        _head = Find(root.transform, "head");
        var opening = unit.Record.Spec.Opening;
        var back = new List<Transform>();
        var nodes = new List<Transform>();
        var seams = new List<bool>();
        if (opening != null)
            foreach (var part in opening.Parts)
            {
                var node = Find(root.transform, part.Node);
                if (node == null)
                {
                    Plugin.Error("Exosuit model " + unit.Record.Spec.Model + " has no node '" + part.Node + "' (Opening); that part does not open.");
                    continue;
                }
                nodes.Add(node);
                seams.Add(part.Seam);
                if (part.Node.StartsWith("plate.back.", StringComparison.Ordinal)) back.Add(node);
            }
        var found = nodes.Count;
        _backParts = back.ToArray();
        var seamParts = new List<Transform>();
        for (var i = 0; i < nodes.Count; i++) if (seams[i]) seamParts.Add(nodes[i]);
        _seamParts = seamParts.ToArray();
        _open = _animation != null ? _animation.GetClip("open") : null;
        _close = _animation != null ? _animation.GetClip("close") : null;
        if (opening != null && _animation != null && (_open == null || _close == null))
            Plugin.Error("Exosuit model " + unit.Record.Spec.Model + " lacks its "
                + (_open == null ? "'open'" : "") + (_open == null && _close == null ? " and " : "") + (_close == null ? "'close'" : "") + " clip; it does not visibly open.");
        else if (_open != null && _close != null && _animation != null)
        {
            _openEvents = PlateTimeline.Opening(Offsets(_open, nodes, out var openDegrees, out var step), openDegrees, seams.ToArray(), step);
            _closeEvents = PlateTimeline.Closing(Offsets(_close, nodes, out var closeDegrees, out step), closeDegrees, step);
            _open.SampleAnimation(_animation.gameObject, 0f);
        }
        if (_head == null) Plugin.Error("Exosuit model " + unit.Record.Spec.Model + " has no 'head' node; its name sits at a fixed height.");
        foreach (var renderer in _renderers)
        {
            var bounds = renderer.localBounds;
            bounds.Expand(LimbReach * 2f);
            renderer.localBounds = bounds;
        }
        _model = _animation != null ? _animation.transform : root.transform.childCount > 0 ? root.transform.GetChild(0) : null;
        _pose = _renderers.Length > 0 ? ForgeVisual.Models.ReadPoseTarget(root.transform, _renderers[0], FrameBodyRig.PoseLinks, unit.Record.Spec.Model) : null;
        if (found == 0) Plugin.Error("Exosuit model " + unit.Record.Spec.Model + " has no opening parts; it does not visibly open.");
        if (_muzzle != null) _flash = FrameFx.MuzzleFlash(_muzzle);
        if (unit.Record.Spec.Rockets is { } rockets)
        {
            var now = Time.time;
            _rocketPod = FrameRocketPod.Create(root.transform, rockets, unit.Record.RocketsReadyAt > now ? unit.Record.RocketsReadyAt - rockets.Cooldown : float.NegativeInfinity);
        }
    }

    /// <summary>Each of <paramref name="nodes"/>' distance (metres) and turn (degrees) from the shut suit (the open clip's first
    /// frame), [frame][part], at every frame of <paramref name="clip"/>; <paramref name="step"/> the seconds between frames.</summary>
    private float[][] Offsets(AnimationClip clip, List<Transform> nodes, out float[][] degrees, out float step)
    {
        var go = _animation!.gameObject;
        _open!.SampleAnimation(go, 0f);
        var position = new Vector3[nodes.Count];
        var rotation = new Quaternion[nodes.Count];
        for (var p = 0; p < nodes.Count; p++) { position[p] = nodes[p].localPosition; rotation[p] = nodes[p].localRotation; }
        var rate = clip.frameRate > 0f ? clip.frameRate : 30f;
        var frames = Mathf.RoundToInt(clip.length * rate) + 1;
        step = 1f / rate;
        var metres = new float[frames][];
        degrees = new float[frames][];
        for (var k = 0; k < frames; k++)
        {
            clip.SampleAnimation(go, Math.Min(clip.length, k * step));
            metres[k] = new float[nodes.Count];
            degrees[k] = new float[nodes.Count];
            for (var p = 0; p < nodes.Count; p++)
            {
                metres[k][p] = (nodes[p].localPosition - position[p]).magnitude;
                degrees[k][p] = Quaternion.Angle(nodes[p].localRotation, rotation[p]);
            }
        }
        return metres;
    }

    /// <summary>The suit's glow colour while all is well, as a plain colour for its name.</summary>
    internal static Color NameColour(FrameSpec spec)
    {
        var l = spec.Light;
        var peak = Math.Max(1f, Math.Max(l.GlowR, Math.Max(l.GlowG, l.GlowB)));
        return new Color(l.GlowR / peak, l.GlowG / peak, l.GlowB / peak, 1f);
    }

    internal static FrameVisual? Create(FrameUnit unit, bool replayed = false)
    {
        var r = unit.Record;
        var root = FrameAssets.Spawn(r.Spec.Model, new Vector3(r.X, r.Y, r.Z), Quaternion.Euler(0f, r.Yaw, 0f));
        if (root == null) return null;
        root.name = "ForgeOrdnanceFrames.Frame." + r.ThrowId;
        var visual = new FrameVisual(unit, root);
        visual._armorAlarm = r.Armor <= r.Spec.Armor * 0.25f;
        if (replayed)
        {
            visual._seenBoost = r.BoostEndsAt;
            visual._seenSiege = r.Sieged;
            visual._seenShieldUp = FrameRules.BarrierUp(r);
            visual.Transition(r, Time.time, true);
            visual._landed = r.State != FrameState.Arriving || r.StateEndsAt - FrameSequence.LandSettleSeconds <= Time.time;
            visual.Tick(Time.time);
            visual.LateTick(Time.time);
        }
        return visual;
    }

    /// <summary>The suit's rocket launcher, or null when it has none (or its art is missing).</summary>
    internal FrameRocketPod? RocketPod => _rocketPod;

    internal Vector3 Muzzle => HeldMuzzle(out var held) ? held
        : _muzzle != null ? _muzzle.position + _root.transform.forward * 0.4f : _root.transform.position + Vector3.up * 1.5f;

    /// <summary>Every frame: follows the record's state.</summary>
    internal void Tick(float now)
    {
        var r = _unit.Record;
        if (r.State != _shown || r.Pilot != _shownPilot) Transition(r, now);
        if (r.HasPilot && _pilot == null && now >= _nextPilotTry) { _nextPilotTry = now + 1f; TakePilot(r.Pilot); }
        MoveMarker();
        TickServo(r);
        if (_bootAt >= 0f && now >= _bootAt)
        {
            _bootAt = -1f;
            if (r.State is FrameState.Entering or FrameState.Piloted) Sound(FrameSound.Boot);
        }
        if (_voiceAt >= 0f && now >= _voiceAt)
        {
            _voiceAt = -1f;
            if (r.State is FrameState.Entering or FrameState.Piloted && LocalPilot.Unit != _unit) Sound(FrameSound.VoiceReady);
        }
        var lowArmor = r.Armor <= r.Spec.Armor * 0.25f;
        if (r.State == FrameState.Piloted && lowArmor && !_armorAlarm) Sound(FrameSound.Alarm);
        _armorAlarm = lowArmor;
        if (r.State == FrameState.Piloted && r.Armor < _seenArmor) Sound(FrameSound.ArmorHit);
        _seenArmor = r.Armor;
        if (r.BoostEndsAt != _seenBoost)
        {
            _seenBoost = r.BoostEndsAt;
        }
        TickBoost(r, now);
        var deploying = r.SiegeDeployEndsAt > 0f;
        if (deploying != _seenDeploy)
        {
            _seenDeploy = deploying;
            if (deploying) _siegeDeploy = FrameAudio.PlayAttached(FrameSound.SiegeDeploy, _identity, _root.transform, Dimension, _pilotLocal);
            else if (_siegeDeploy != null && _siegeDeploy.State != PlaybackState.Ended) _siegeDeploy.Stop(0.12f);
        }
        if (r.Sieged != _seenSiege)
        {
            _seenSiege = r.Sieged;
            Sound(FrameSounds.Siege(r.Sieged));
            if (r.Sieged) SiegeFx();
        }
        if (r.Spec.Shield != null) TickBarrier(r, now);
        _rocketPod?.Tick(now);
        switch (r.State)
        {
            case FrameState.Idle:
                if (!_landed) Land(new Vector3(r.X, r.Y, r.Z));
                else if (!_helmetMeasured && now - _stateStart > 1f) MeasureHelmet(r);
                break;
            case FrameState.Destroyed:
            {
                var age = now - _stateStart;
                _root.transform.position = new Vector3(r.X, r.Y, r.Z) + Vector3.down * FrameEffectsPlan.WreckSink(age, FrameRules.WreckSeconds) * 2.5f;
                WreckFx(now, age);
                break;
            }
        }
        TickFxLight(r, now);
        TickGlow(r, now);
        if (_hidden && now >= _nextHideCheck) WatchHidden(now);
    }

    private bool _helmetMeasured;
    /// <summary>The shell's rearmost point in the suit's frame (z, metres), from the posed meshes (<see cref="MeasureHelmet"/>): a stowed
    /// heavy object hangs behind it. A skinned renderer's bounds are not it (the built bounds are grown by <see cref="LimbReach"/> on every
    /// side and are axis-aligned in the world).</summary>
    private float _backPlane = float.NaN;
    /// <summary>Metres around the head bone (horizontally) whose vertices count as the helmet, so a back-mounted part is not the top.</summary>
    private const float HelmetRadius = 0.5f;

    /// <summary>Once, a second after the suit stands idle (the bones are in place): the helmet top above the head bone, from the highest vertex
    /// of the posed meshes around the head. Reports once when that cannot be read or comes out implausible, and keeps the estimate then.</summary>
    private void MeasureHelmet(FrameRecord r)
    {
        _helmetMeasured = true;
        if (_head == null) return;
        var head = _head.position;
        var top = float.NegativeInfinity;
        var back = float.PositiveInfinity;
        var baked = new Mesh();
        try
        {
            foreach (var renderer in _renderers)
            {
                if (renderer == null || renderer.WasCollected) continue;
                renderer.BakeMesh(baked);
                var vertices = baked.vertices;
                var space = renderer.transform;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = space.TransformPoint(vertices[i]);
                    back = Math.Min(back, _root.transform.InverseTransformPoint(world).z);
                    var dx = world.x - head.x;
                    var dz = world.z - head.z;
                    if (dx * dx + dz * dz <= HelmetRadius * HelmetRadius) top = Math.Max(top, world.y);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Error("Name plate of " + r.Spec.Model + ": the meshes could not be read for the helmet top (" + e + "); it sits " + HelmetEstimate.ToString("F2") + " m over the head bone.");
            return;
        }
        finally { Object.Destroy(baked); }
        if (!float.IsPositiveInfinity(back)) _backPlane = back;
        var above = top - head.y;
        if (float.IsNegativeInfinity(top) || above < 0.05f || above > 1f)
        {
            Plugin.Error("Name plate of " + r.Spec.Model + ": the helmet top reads " + above.ToString("F2") + " m over the head bone; it sits " + HelmetEstimate.ToString("F2") + " m over it.");
            return;
        }
        _helmetAboveHead = above;
    }

    /// <summary>After the game moved the players: the suit's presentation, then the watch on where the game put its pilot.</summary>
    internal void LateTick(float now)
    {
        Present(now);
    }

    /// <summary>The back plates of a suit being boarded or left, or a worn suit following its pilot, walking and stepping. A worn suit
    /// stands on its pilot's feet every frame, so wherever the game puts the pilot (a snatcher's warp into its arena dimension and back, a
    /// dimension warp, a teleport, a tank's tentacle dragging it) the suit is there the same frame on every peer.</summary>
    private void Present(float now)
    {
        var r = _unit.Record;
        if (_pilot != null && _pilot.WasCollected) _pilot = null;
        // Boarding and leaving play from the host's state start alone, so a peer that has not resolved the pilot's agent yet
        // (a late joiner) still sees the back open and close at the same point.
        switch (r.State)
        {
            case FrameState.Entering:
            {
                var elapsed = now - _stateStart;
                Stand(r);
                var plates = FrameSequence.EnterPlates(elapsed, OpenLength, CloseLength);
                Open(plates);
                PlateSounds(plates);
                // The pilot stands where they boarded from until the black, then is inside: no motion plays.
                Conceal(!FrameSequence.PilotShownEntering(elapsed));
                _body?.Stop();
                Own(_pilotLocal && LocalPilot.Inside);
                TickLight(false);
                _carry.Release();
                Unpose();
                return;
            }
            case FrameState.Exiting:
            {
                var elapsed = now - _stateStart;
                Stand(r);
                var plates = FrameSequence.ExitPlates(elapsed, OpenLength);
                Open(plates);
                PlateSounds(plates);
                Conceal(!FrameSequence.PilotShownExiting(elapsed));
                var clip = elapsed - FrameSequence.ExitClipStart;
                if (clip >= 0f && clip < PilotMotion.ExitSeconds) PoseBody(r, clip);
                else _body?.Stop();
                Own(_pilotLocal && elapsed < FrameSequence.ExitClipStart);
                TickLight(false);
                _carry.Release();
                Unpose();
                return;
            }
        }
        IdlePlates(now);
        _body?.Stop();
        if (_pilot == null || !r.HasPilot || r.State != FrameState.Piloted)
        {
            Own(false);
            TickLight(false);
            Unpose();
            Silence(false);
            _carry.Release();
            return;
        }
        // Worn.
        Conceal(true);
        Silence(true);
        var feet = _pilotLocal && _pilot.PlayerCharacterController != null ? _pilot.PlayerCharacterController.SmoothPosition : _pilot.Position;
        var forward = _pilot.Rotation * Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 1e-4f) _yaw = Quaternion.LookRotation(forward).eulerAngles.y;
        var rotation = Quaternion.Euler(0f, _yaw, 0f);
        _root.transform.SetPositionAndRotation(feet - rotation * Vector3.forward * FrameSequence.CockpitBack, rotation);
        Own(_pilotLocal);
        TickLight(true);
        var state = _pilot.Locomotion != null ? _pilot.Locomotion.m_currentStateEnum : PlayerLocomotion.PLOC_State.Stand;
        var airborne = state is PlayerLocomotion.PLOC_State.Jump or PlayerLocomotion.PLOC_State.Fall;
        if (airborne != _airborne)
        {
            _airborne = airborne;
            if (airborne) { if (state == PlayerLocomotion.PLOC_State.Jump) Sound(FrameSound.Jump); }
            else
            {
                SoundAt(FrameSound.Land, feet);
            }
        }
        _wornFrame = Time.frameCount;
        FollowPilot(now);
    }

    /// <summary>A worn suit takes its pilot's body pose, its held item and, for a carrying model, the carried object.</summary>
    private void FollowPilot(float now)
    {
        PoseFromPilot(now);
        if (_nameAnchor != null) _nameAnchor.transform.position = NameAt;
        if (_pilot == null || _pilotLocal || _pose == null) return;
        var stows = _unit.Record.Spec.CarryHeavy;
        // A suit joined while worn was never idle here: measure its shell now.
        if (stows && !_helmetMeasured) MeasureHelmet(_unit.Record);
        var hands = (_pose.Bone(FrameBodyRig.LeftHandLink).position + _pose.Bone(FrameBodyRig.RightHandLink).position) * 0.5f;
        _carry.Show(_pilot, _root.transform, stows ? _backPlane : float.NaN, _chest, hands, stows);
    }

    /// <summary>The pilot's body <paramref name="time"/> seconds into the leaving motion, on the suit.</summary>
    private void PoseBody(FrameRecord r, float time) => _body?.Pose(time, new Vector3(r.X, r.Y, r.Z), Quaternion.Euler(0f, r.Yaw, 0f));

    /// <summary>The pilot motion's body for this suit's pilot (the local pilot's camera follows its head), or null.</summary>
    internal PilotBody? Body => _body;

    /// <summary>The suit stands where the record puts it (it never moves while being boarded or left).</summary>
    private void Stand(FrameRecord r)
        => _root.transform.SetPositionAndRotation(new Vector3(r.X, r.Y, r.Z), Quaternion.Euler(0f, r.Yaw, 0f));

    private float OpenLength => _open != null ? _open.length : 0f;
    internal float CloseLength => _close != null ? _close.length : 0f;

    /// <summary>Poses every opening part at <paramref name="pose"/> of the model's "open" or "close" clip, which hold only the
    /// parts' bones (rigid paths, never scaled); after the other clips were sampled this frame; a shut suit is written once and then
    /// left to its clips. The baked bounds cover the clips only, so while parts are open the renderers recompute their bounds (the
    /// suit is not culled while a plate still shows).</summary>
    private void Open(PlatePose pose)
    {
        var open = pose.Closing ? pose.Time < CloseLength : pose.Time > 0f;
        if (!open && !_plateOpen) return;
        if (open != _plateOpen)
            foreach (var renderer in _renderers)
                if (renderer != null && !renderer.WasCollected) renderer.updateWhenOffscreen = open;
        _plateOpen = open;
        var clip = pose.Closing ? _close : _open;
        if (clip != null && _animation != null) clip.SampleAnimation(_animation.gameObject, pose.Time);
    }

    /// <summary>The sounds (and the seam's steam) the clip has passed since the last frame; a change of clip starts its list over.</summary>
    private void PlateSounds(PlatePose pose)
    {
        if (pose.Closing != _eventsClosing) { _eventsClosing = pose.Closing; _eventStep = 0; }
        var events = pose.Closing ? _closeEvents : _openEvents;
        while (_eventStep < events.Length && events[_eventStep].At <= pose.Time)
        {
            var e = events[_eventStep++];
            if (e.Steam) VentSeam();
            if (e.Sound is { } sound)
            {
                // The first few plate sounds of a level with when they were asked for: a cue asked twice shows as two lines.
                Sound(sound);
            }
        }
    }

    /// <summary>A peer joining mid-sequence starts the opening's sounds after the moments already past.</summary>
    private void SkipPlateSounds(PlatePose pose)
    {
        _eventsClosing = pose.Closing;
        _eventStep = PlateTimeline.Passed(pose.Closing ? _closeEvents : _openEvents, pose.Time);
    }

    /// <summary>A short puff of steam from the back's middle seam as it first parts: between the parts beside it, out and up
    /// behind the suit.</summary>
    private void VentSeam()
    {
        var sum = Vector3.zero;
        var count = 0;
        foreach (var plate in _seamParts)
            if (plate != null && !plate.WasCollected) { sum += plate.position; count++; }
        if (count == 0) return;
        var t = _root.transform;
        var at = sum / count;
        at += t.right * Vector3.Dot(t.position - at, t.right);
        FrameFx.Vent(at - t.forward * 0.08f, -t.forward + Vector3.up * 0.45f, 2, 1.4f);
    }

    /// <summary>The local pilot's own suit once the pilot is inside it: drawn as its shadow only, so the camera never shows its walls
    /// (standing, crouching or looking down). Everyone else sees it whole.</summary>
    private void Own(bool hidden)
    {
        if (hidden == _ownHidden) return;
        _ownHidden = hidden;
        var mode = hidden ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
        foreach (var renderer in _renderers)
            if (renderer != null && !renderer.WasCollected) renderer.shadowCastingMode = mode;
        _rocketPod?.ShadowOnly(hidden);
        _heldCopy?.ShadowOnly(hidden);
    }

    private void Transition(FrameRecord r, float now, bool replayed = false)
    {
        var previous = _shown;
        _shown = r.State;
        _stateStart = now;
        if (r.State != FrameState.Arriving) { _drop?.Cancel(); _drop = null; }
        if (r.State != FrameState.Idle) _ejectStart = _closeStart = -1f;
        _bootAt = _voiceAt = -1f;
        if (replayed)
        {
            var duration = r.State switch
            {
                FrameState.Arriving => ArriveSeconds,
                FrameState.Entering => FrameSequence.EnterSeconds,
                FrameState.Exiting => FrameSequence.ExitSeconds,
                FrameState.Destroyed => FrameRules.WreckSeconds,
                _ => 0f,
            };
            if (duration > 0f) _stateStart -= Mathf.Clamp(duration - (r.StateEndsAt - now), 0f, duration);
        }
        var pilotChanged = r.Pilot != _shownPilot;
        _shownPilot = r.Pilot;
        if (pilotChanged || !r.HasPilot) ReleasePilot();
        if (r.HasPilot && _pilot == null) TakePilot(r.Pilot);
        var ground = new Vector3(r.X, r.Y, r.Z);
        switch (r.State)
        {
            case FrameState.Arriving:
                _arriveStart = now - Math.Max(0f, ArriveSeconds - Math.Max(0f, r.StateEndsAt - now));
                _kicked = false;
                _landed = false;
                // The fall is ForgeOrdnance's container-free drop: the supply pod's fall whistle now, its touchdown, rubble and camera shake
                // when it lands (a peer joining mid-fall hears only what is still to come).
                _drop?.Cancel();
                _drop = OrdnanceArrival.Drop(_root.transform, ground, FallSeconds, FallStyle(ground), now - _arriveStart, replayed);
                break;
            case FrameState.Idle:
                _root.transform.SetPositionAndRotation(ground, Quaternion.Euler(0f, r.Yaw, 0f));
                Unpose();
                // The pilot has landed: the empty suit powers down and closes on its own (IdlePlates), holding nobody.
                if (!replayed && previous == FrameState.Exiting)
                {
                    Sound(FrameSound.PowerDown);
                    _closeStart = now;
                }
                if (!replayed && previous == FrameState.Piloted) EjectFx(now);
                break;
            case FrameState.Entering:
                _landed = true;
                Stand(r);
                SkipPlateSounds(replayed ? FrameSequence.EnterPlates(now - _stateStart, OpenLength, CloseLength) : new PlatePose(false, -1f));
                // The power-on starts at the black: its boot sound then, the ready voice when its NEURAL LINK OK steadies (a late joiner
                // hears only what is still to come).
                var bootAt = _stateStart + FrameSequence.Black;
                if (bootAt >= now) _bootAt = bootAt;
                if (bootAt + BootPlan.Default.LinkSteady >= now) _voiceAt = bootAt + BootPlan.Default.LinkSteady;
                break;
            case FrameState.Piloted:
                _airborne = false;
                break;
            case FrameState.Exiting:
                Stand(r);
                SkipPlateSounds(replayed ? FrameSequence.ExitPlates(now - _stateStart, OpenLength) : new PlatePose(false, -1f));
                break;
            case FrameState.Destroyed:
                if (!replayed)
                {
                    SoundAt(FrameSound.Destroyed, _root.transform.position);
                    ExplosionFx();
                }
                break;
        }
    }

    /// <summary>How the suit lands. Bystanders feel it through ForgeOrdnance's one camera-shake setting; a player wearing a suit never gets shake.</summary>
    private static OrdnanceImpact LandImpactOf(float noiseRadius)
        => new() { Strength = 0.2f, Range = ShakeRange, Seconds = 0.6f, Frequency = 25f, ShakeIf = () => LocalPilot.Unit == null, NoiseRadius = noiseRadius };

    /// <summary>The touchdown of a fall this peer played: with the landing noise of a suit with no pilot (ForgeOrdnance makes it on the host only).</summary>
    private OrdnanceImpact? _fallImpact;

    /// <summary>The touchdown of a suit found already standing: the sounds and shake only. The noise belongs to the fall, which this peer
    /// did not play, and the host made it (or its successor will not repeat a landing long past).</summary>
    private static readonly OrdnanceImpact StandingImpact = LandImpactOf(0f);

    private OrdnanceDropStyle FallStyle(Vector3 ground)
        => new()
        {
            FallHeight = FallHeight, Impact = _fallImpact ??= LandImpactOf(Noise.LandingRadius()),
            OnFall = fall => ArrivalFx(fall, ground), OnLanded = () => Touchdown(ground),
        };

    /// <summary>The suit stands on the ground and the drop has played its impact: the suit's own landing sound and effects.</summary>
    private void Touchdown(Vector3 ground)
    {
        _landed = true;
        Sound(FrameSound.Landing);
        LandFx(ground);
    }

    /// <summary>The suit is already Idle without this peer having seen the fall: it lands where it is, with the impact the drop would have played.</summary>
    private void Land(Vector3 ground)
    {
        _root.transform.position = ground;
        OrdnanceArrival.Impact(_root.transform, ground, StandingImpact);
        Touchdown(ground);
    }

    /// <summary>Another peer's pilot fired: muzzle flash and a tracer from the muzzle of the gun the suit holds (the game plays the
    /// weapon's own sound); at most two tracers per suit per frame.</summary>
    internal void Shots(int count, Vector3 origin, Vector3 target)
    {
        if (count <= 0) return;
        // The pilot's own shot left from the gun the suit holds here.
        if (HeldMuzzle(out var held)) origin = held;
        if (_flash?.Transform is { } flash)
        {
            var aim = target - origin;
            flash.SetPositionAndRotation(origin, Quaternion.LookRotation(aim.sqrMagnitude > 0.01f ? aim : _root.transform.forward));
            _flash.Play();
        }
        var frame = Time.frameCount;
        if (frame != _tracerFrame) { _tracerFrame = frame; _tracersThisFrame = 0; }
        for (var s = 0; s < Math.Min(count, 2) && _tracersThisFrame < 2; s++, _tracersThisFrame++) FrameFx.Tracer(origin, target);
    }

    // ---- lamp ---------------------------------------------------------------------------------------------------

    /// <summary>While worn the pilot's own flashlight (its key and its replication stay the game's) is the suit's lamp: range,
    /// cone and intensity times the model's multipliers, tinted; for a remote pilot it shines from the suit's helmet. A value the
    /// game writes again (a weapon change sets the flashlight from the weapon's part) becomes the new base. A small warm glow on the
    /// suit's chest shows the lamp is on. Everything is put back when the pilot leaves.</summary>
    private void TickLight(bool worn)
    {
        var light = worn && _pilot != null ? _pilot.Inventory?.m_flashlight : null;
        if (light != null && light.WasCollected) light = null;
        if (_light != null && (light == null || light.Pointer != _light.Pointer)) RestoreLight();
        if (light == null) { Glow(false); return; }
        var spec = _unit.Record.Spec.Light;
        if (_light == null || Math.Abs(light.range - _appliedRange) > 0.01f)
        {
            _light = light;
            _lightRange = light.range;
            _lightAngle = light.spotAngle;
            _lightIntensity = light.intensity;
            _lightColour = light.color;
            light.range = _appliedRange = _lightRange * spec.RangeMultiplier;
            light.spotAngle = Math.Min(150f, _lightAngle * spec.AngleMultiplier);
            light.intensity = _lightIntensity * spec.IntensityMultiplier;
            light.color = new Color(spec.R, spec.G, spec.B, 1f);
        }
        var head = _head != null ? _head.position : _root.transform.position + Vector3.up * 2f;
        var lamp = head + _root.transform.forward * 0.28f - Vector3.up * 0.12f;
        if (!_pilotLocal) light.transform.position = lamp;
        // The local pilot never sees its own helmet; the glow is for everyone else.
        Glow(!_pilotLocal && _pilot!.Inventory!.FlashlightEnabled, lamp);
    }

    private void RestoreLight()
    {
        var light = _light;
        _light = null;
        if (light == null || light.WasCollected) return;
        light.range = _lightRange;
        light.spotAngle = _lightAngle;
        light.intensity = _lightIntensity;
        light.color = _lightColour;
    }

    private void Glow(bool on, Vector3 at = default)
    {
        if (!on)
        {
            if (_lamp != null && _lamp.Shown) _lamp.Hide();
            return;
        }
        if (_lamp == null || !_lamp.Alive)
        {
            _lamp = ForgeVisual.GlowSpot.Create(_root.transform, Vector3.zero);
            if (_lamp == null) return;
        }
        // A soft warm dot that always faces the camera, small enough never to hide the suit or the view; the local pilot never sees its own helmet.
        if (!_lamp.Shown) _lamp.Show(LampColour, LampSize);
        _lamp.Place(at);
    }

    // ---- pilot ------------------------------------------------------------------------------------------------

    private void TakePilot(ulong lookup)
    {
        var agent = ForgeCore.Level.PlayerByLookup(lookup);
        if (agent == null) return;
        _pilot = agent;
        _pilotLocal = agent.IsLocallyOwned && agent.Owner != null && agent.Owner.Lookup == SNet.LocalPlayer.Lookup;
        ScaleHitboxes(agent);
        _body = PilotBody.Bind(agent, _pilotLocal);
        BindPose(agent);
        MoveMarker();
    }

    /// <summary>How far over the shut suit's helmet top its name sits (the idle suit's plate and the pilot's game marker), metres.</summary>
    internal const float NameGap = 0.2f;
    private GameObject? _nameAnchor;
    private NavMarker? _marker;
    private GameObject? _markerOriginal;

    /// <summary>The pilot's own game marker (name, health, icon, as the game draws it) moves from the person's height to over the suit's
    /// helmet: the game draws it at its tracked object's position with no offset, so only the tracked object is swapped for an anchor
    /// that <see cref="FollowPilot"/> keeps at <see cref="NameAt"/>. The game makes the marker some time after the player appears, so
    /// each frame tries again until it exists.</summary>
    private void MoveMarker()
    {
        if (_pilot == null || _marker != null) return;
        var marker = _pilot.NavMarker?.m_marker;
        if (marker == null || marker.WasCollected) return;
        _nameAnchor = new GameObject("ForgeOrdnanceFrames.Name");
        _nameAnchor.transform.position = NameAt;
        _markerOriginal = marker.m_trackingObj;
        marker.SetTrackingObject(_nameAnchor);
        _marker = marker;
    }

    private void ReleaseMarker()
    {
        if (_marker != null && !_marker.WasCollected && _markerOriginal != null && !_markerOriginal.WasCollected)
            _marker.SetTrackingObject(_markerOriginal);
        if (_nameAnchor != null) Object.Destroy(_nameAnchor);
        _nameAnchor = null;
        _marker = null;
        _markerOriginal = null;
    }

    /// <summary>The pilot's own footsteps, jump and landing sounds are off exactly while the suit is worn (the walk in and out
    /// keeps the game's footsteps).</summary>
    private void Silence(bool on)
    {
        if (on == _silenced) return;
        _silenced = on;
        if (on)
        {
            if (_pilot != null && !_pilot.WasCollected)
                _bodySounds = AudioKit.ClaimBodySounds(Plugin.Id, _pilot.gameObject, BodySound.Footstep | BodySound.Movement, this);
        }
        else
        {
            _bodySounds?.Dispose();
            _bodySounds = null;
        }
    }

    private void ReleasePilot()
    {
        _carry.Release();
        Unpose();
        UnbindPose();
        if (_pilot == null) return;
        _body?.Stop();
        _body = null;
        ReleaseMarker();
        if (!_pilot.WasCollected)
        {
            Conceal(false);
            RestoreHitboxes();
        }
        RestoreLight();
        Glow(false);
        Own(false);
        Silence(false);
        _pilot = null;
        _pilotLocal = false;
    }

    /// <summary>Stops (or resumes) drawing the pilot as a person. A remote pilot: the game's own switch
    /// (<c>PlayerSyncModelData.SetAllVisible</c>) and then <c>forceRenderingOff</c> on every renderer of its agent (model and held
    /// item), which the game never writes, so a refresh by the game cannot bring the person back. The local pilot: only its
    /// third-person model, which draws its shadow; the first-person body is the worn shell's (<see cref="LocalPilot"/>). Only renderers turned off
    /// here are turned back on.</summary>
    private void Conceal(bool hide)
    {
        if (hide == _hidden || _pilot == null) return;
        var agent = _pilot;
        try
        {
            if (!_pilotLocal)
            {
                var model = agent.PlayerSyncModel;
                if (model != null) model.SetAllVisible(!hide);
            }
            if (hide) HideRenderers(agent);
            else
            {
                foreach (var renderer in _forcedOff)
                    if (renderer != null && !renderer.WasCollected) renderer.forceRenderingOff = false;
                _forcedOff.Clear();
            }
            _hidden = hide;
            _nextHideCheck = Time.time + 0.5f;
        }
        catch (Exception error) { Plugin.Error("Hiding the exosuit pilot failed: " + error); }
    }

    private void HideRenderers(PlayerAgent agent)
    {
        if (!_pilotLocal) { ForceOff(agent.gameObject); return; }
        var model = agent.PlayerSyncModel;
        if (model != null) ForceOff(model.gameObject);
    }

    private void ForceOff(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.forceRenderingOff || _carry.Shows(renderer)) continue;
            renderer.forceRenderingOff = true;
            _forcedOff.Add(renderer);
        }
    }

    /// <summary>Every 0.5 s while the pilot is inside: renderers the game created since (a newly drawn weapon of a remote pilot) are
    /// turned off too. Nothing the game switched is switched back here; the renderers already off stay off by themselves.</summary>
    private void WatchHidden(float now)
    {
        _nextHideCheck = now + 0.5f;
        if (_pilot == null || _pilot.WasCollected) return;
        HideRenderers(_pilot);
    }

    private void ScaleHitboxes(PlayerAgent agent)
    {
        _hitboxes.Clear();
        foreach (var limb in agent.GetComponentsInChildren<Dam_PlayerDamageLimb>(true))
        {
            var collider = limb.GetComponent<Collider>();
            if (collider == null) continue;
            var capsule = collider.TryCast<CapsuleCollider>();
            var sphere = collider.TryCast<SphereCollider>();
            var box = collider.TryCast<BoxCollider>();
            if (capsule != null) { _hitboxes.Add((collider, Vector3.zero, capsule.radius, capsule.height)); capsule.radius *= HitboxScale; capsule.height *= HitboxScale; }
            else if (sphere != null) { _hitboxes.Add((collider, Vector3.zero, sphere.radius, 0f)); sphere.radius *= HitboxScale; }
            else if (box != null) { _hitboxes.Add((collider, box.size, 0f, 0f)); box.size *= HitboxScale; }
        }
    }

    private void RestoreHitboxes()
    {
        foreach (var (collider, size, radius, height) in _hitboxes)
        {
            if (collider == null || collider.WasCollected) continue;
            var capsule = collider.TryCast<CapsuleCollider>();
            var sphere = collider.TryCast<SphereCollider>();
            var box = collider.TryCast<BoxCollider>();
            if (capsule != null) { capsule.radius = radius; capsule.height = height; }
            else if (sphere != null) sphere.radius = radius;
            else if (box != null) box.size = size;
        }
        _hitboxes.Clear();
    }

    /// <summary>A suit's barrier (the tank's, or the firepower's while planted) has no picture of its own: its raise, drop and hit sounds
    /// (a hit is the barrier's strength dropping since the last frame), the push pulse and the pilot's barrier line.</summary>
    private void TickBarrier(FrameRecord r, float now)
    {
        var lost = r.Shield < _seenShield;
        _seenShield = r.Shield;
        WatchPush(r, now);
        var up = FrameRules.BarrierUp(r);
        if (up != _seenShieldUp)
        {
            _seenShieldUp = up;
            if (r.State == FrameState.Piloted) Sound(up ? FrameSound.ShieldUp : FrameSound.ShieldDown);
        }
        else if (lost && up && now >= _nextShieldHitSound)
        {
            _nextShieldHitSound = now + 0.25f;
            Sound(FrameSound.ShieldHit);
        }
    }

    internal void Destroy()
    {
        // The suit's own objects go whatever the pilot's side does: a pilot object the game already destroyed (a level unload)
        // must not leave the suit standing into the next level.
        try
        {
            StopServo();
            StopBoost();
            ReleasePilot();
        }
        finally
        {
            try { if (_root != null) Object.Destroy(_root); }
            finally
            {
                DestroyFx();
                DestroyGlow();
                _lamp?.Destroy();
            }
        }
    }

    private int Dimension => _pilot != null && !_pilot.WasCollected
        ? (int)_pilot.DimensionIndex : ForgeAudio.AudioKit.DimensionAt(_root.transform.position, true);

    /// <summary>A sound that follows the suit (3D for everyone but its own pilot, who hears it flat).</summary>
    internal void Sound(FrameSound sound) => FrameAudio.PlayAttached(sound, _identity, _root.transform, Dimension, _pilotLocal);

    private StepGate _stepGate;

    /// <summary>One of the pilot's own game body sounds: a footstep becomes the suit's footstep at the same place, once per footfall,
    /// whatever the pilot's pace; the jump and landing are dropped (the suit plays its own). The game's sound never plays.</summary>
    bool IBodySoundHandler.Play(BodySound kind, Vector3 position)
    {
        if (kind == BodySound.Footstep && _stepGate.Admit(Time.time)) SoundAt(FrameSound.Footstep, position);
        return true;
    }

    private void SoundAt(FrameSound sound, Vector3 position)
        => FrameAudio.PlayAt(sound, _identity, position, Dimension, _pilotLocal);


    private void TickServo(FrameRecord record)
    {
        if (!record.HasPilot || record.State is FrameState.Idle or FrameState.Arriving or FrameState.Destroyed)
        {
            StopServo();
            return;
        }
        var dimension = Dimension;
        if (_servo != null && _servo.State != PlaybackState.Ended && dimension == _servoDimension) return;
        StopServo();
        _servoDimension = dimension;
        _servo = FrameAudio.PlayAttached(FrameSound.Servo, _identity, _root.transform, dimension, _pilotLocal);
    }

    /// <summary>The assault charge: its thruster loop, and steam jetting from the back every few hundredths of a second.</summary>
    private void TickBoost(FrameRecord record, float now)
    {
        if (!record.Boosting(now) || record.State == FrameState.Destroyed)
        {
            StopBoost();
            return;
        }
        ChargeFx(now);
        var dimension = Dimension;
        if (_boostLoop != null && _boostLoop.State != PlaybackState.Ended && dimension == _boostDimension) return;
        StopBoost();
        _boostDimension = dimension;
        _boostLoop = FrameAudio.PlayAttached(FrameSound.Boost, _identity, _root.transform, dimension, _pilotLocal);
    }

    private void StopBoost()
    {
        if (_boostLoop != null && _boostLoop.State != PlaybackState.Ended) _boostLoop.Stop(0.2f);
        _boostLoop = null;
        _boostDimension = -1;
    }

    private void StopServo()
    {
        if (_servo != null && _servo.State != PlaybackState.Ended) _servo.Stop(0.15f);
        _servo = null;
        _servoDimension = -1;
    }

    private static Transform? Find(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (var i = 0; i < parent.childCount; i++)
        {
            var found = Find(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
