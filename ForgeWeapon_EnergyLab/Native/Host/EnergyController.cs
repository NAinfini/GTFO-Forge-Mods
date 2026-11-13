using System;
using System.Collections.Generic;
using ForgeVisual;
using ForgeWeaponEnergyLab.Presentation;
using Gear;
using Il2CppInterop.Runtime;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>The energy guns' per-frame work. A plain object driven by ForgeCore's <c>Frame</c> callbacks (late update, quit); the game camera's pre-cull
/// callback draws the muzzle-anchored effects.</summary>
internal sealed class EnergyController
{
    private static EnergyController? _instance;
    private readonly Dictionary<uint, double> _nextOrbShots = new();
    private float? _releasedPower;
    private readonly PlasmaCharge _charge = new();
    private readonly EnergyDischarge _discharge = new();
    private readonly NativeShot _shot = new();
    private readonly Vector3[] _starts = new Vector3[EnergyLimits.ArcTargets];
    private readonly Vector3[] _ends = new Vector3[EnergyLimits.ArcTargets];
    private readonly bool[] _hits = new bool[EnergyLimits.ArcTargets];
    private float _lastNativeShot = float.NegativeInfinity;
    private BulletWeapon? _weapon;
    private EnergyEffects? _effects;
    private EnergyVisuals? _visuals;
    private EnergyProjectiles? _plasma;
    private SpecialWeapons? _special;
    private EnergyEffects? _warmEffects;
    private EnergyVisuals? _warmVisuals;
    private PlasmaEffects? _warmPlasma;
    private SpecialEffects? _warmSpecial;
    private bool _preparationFailed;
    private bool _instancesReady, _simulationsReady, _fireGateClosed;
    private readonly Dictionary<ulong, RemoteWeapon> _remotes = new();
    private readonly Dictionary<ulong, RemotePeerState> _remoteStates = new();
    private uint _armSequence;
    private readonly List<ulong> _departed = new();
    private EnergyMode _mode;
    private EnergyGear? _gear;
    private bool _faulted;
    private bool _chargedFull;
    private bool _chargeAnnounced;
    private EnergyGear? _announcedGear;
    private Vector3 _announcedPosition;
    private float _chargeAnnouncedAt, _nextChargeRenew;
    private bool _wielded;
    // The local player's wielded bullet weapon, kept by the wield events (BulletWeapon.OnWield / OnUnWield) instead of read from the hand
    // every frame. Its energy definition is resolved once per wield.
    private BulletWeapon? _localWielded;
    // The local player, taken from the wield events instead of asked of the game every frame (a destroyed one reads as null).
    private PlayerAgent? _player;
    private EnergyGear? _wieldedGear;
    private bool _wieldedChecked;
    // The game is in a level (read when its state changes, not every frame).
    private bool _inLevel;
    // Muzzle-anchored first-person effects are drawn as the look camera starts to render (PreCull).
    private bool _streamHeld, _chargeHeld;
    private float _chargePower;
    private Camera.CameraCallback? _preCull;

    private EnergyController() { }

    /// <summary>Starts the controller and hands its frame work to ForgeCore.</summary>
    internal static EnergyController Start(string modId)
    {
        var controller = new EnergyController();
        controller.Awake();
        ForgeCore.Frame.OnLateUpdate(modId, controller.LateUpdate);
        ForgeCore.Frame.OnQuit(modId, controller.Shutdown);
        return controller;
    }

    private void Awake()
    {
        _instance = this;
        ForgeCore.GameEvents.LevelEnded += OnLevelEnded;
        ForgeCore.GameEvents.CheckpointReloaded += ForgetPlayer;
        ForgeCore.GameEvents.StateChanged += ReadState;
        ForgeCore.Net.WorldChanged += WorldChanged;
        _preCull = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(PreCull));
        Camera.onPreCull = Camera.onPreCull == null ? _preCull
            : Il2CppSystem.Delegate.Combine(Camera.onPreCull, _preCull).Cast<Camera.CameraCallback>();
    }

    // The level's weapons are destroyed with it; the next wield names the next gun.
    private void OnLevelEnded()
    {
        _localWielded = null; _wieldedGear = null; _wieldedChecked = false; _player = null;
    }

    private void ForgetPlayer() => _player = null;

    private void ReadState() => _inLevel = GameStateManager.CurrentStateName == eGameStateName.InLevel;

    private PlayerAgent? LocalPlayer()
    {
        if (_player == null) _player = ForgeCore.Players.Local;
        return _player;
    }

    /// <summary>A bullet weapon went into or out of a hand (the game's wield entries). Only the local player's gun is kept.</summary>
    internal static void NoteWield(BulletWeapon weapon, bool wielded)
    {
        var controller = _instance;
        if (controller == null || weapon.Owner?.Owner is not { IsLocal: true, IsBot: false }) return;
        if (wielded) { controller._localWielded = weapon; controller._player = weapon.Owner; controller._wieldedChecked = false; return; }
        if (controller._localWielded != null && controller._localWielded.Pointer == weapon.Pointer)
        { controller._localWielded = null; controller._wieldedGear = null; controller._wieldedChecked = false; }
    }

    private EnergyGear? WieldedGear()
    {
        if (!_wieldedChecked)
        {
            _wieldedGear = _localWielded == null ? null : EnergyGearRegistry.DefinitionOf(_localWielded);
            _wieldedChecked = true;
        }
        return _wieldedGear;
    }

    private static Quaternion LookRotation(PlayerAgent? player)
        => player?.FPItemHolder?.m_LookCamera?.transform.rotation ?? Quaternion.identity;

    // A local fault stops this player's own gun for the rest of the world (SessionReady); the channel and the other players' weapons go on (PresentationReady).
    private static bool SessionReady()
        => EnergyNetwork.Ready && _instance is { PresentationReady: true, _faulted: false };

    // In a level of the session's world: there is a host, and on a client it has said which world this is.
    private bool WorldUp => _inLevel && ForgeCore.Net.World.Number != 0;

    /// <summary>This machine can show and simulate the guns in the current world; ForgeCore tells the other players (<see cref="ForgeCore.NetChannel.Ready"/>).</summary>
    internal bool PresentationReady => WorldUp && EnergyGearRegistry.Ready && PresentationPreload.Ready &&
        _instancesReady && _simulationsReady && !_preparationFailed;

    private void PrepareVisuals()
    {
        if (!PresentationPreload.Ready || _preparationFailed || _instancesReady) return;
        try
        {
            // Voice/mesh allocation is staged before selection, not inside the equip path.
            if (_effects == null && _warmEffects == null) _warmEffects = new EnergyEffects();
            else if (_visuals == null && _warmVisuals == null) _warmVisuals = new EnergyVisuals();
            else if (_plasma == null && _warmPlasma == null) _warmPlasma = new PlasmaEffects(EnergyProjectiles.Capacity + 1);
            else if (_special == null && _warmSpecial == null) _warmSpecial = new SpecialEffects();
            else _instancesReady = true;
        }
        catch (Exception error)
        {
            _preparationFailed = true;
            Plugin.Logger.ErrorOnce("Energy instance preparation failed: " + error);
        }
    }

    private void PrepareSimulations()
    {
        if (_simulationsReady || _preparationFailed || !WorldUp || !_instancesReady) return;
        try
        {
            // Allocate physics/contacts buffers before first wield,
            // one simulator per frame; equip takes the prepared instances without new native arrays.
            if (_plasma == null)
            {
                _plasma = new EnergyProjectiles(ForgeCore.Session.IsHost, _warmPlasma);
                _warmPlasma = null;
            }
            else if (_special == null)
            {
                _special = new SpecialWeapons(ForgeCore.Session.IsHost, _warmSpecial);
                _warmSpecial = null;
                _simulationsReady = true;
            }
        }
        catch (Exception error)
        {
            _preparationFailed = true;
            Plugin.Logger.ErrorOnce("Energy simulation preparation failed: " + error);
        }
    }

    private void LateUpdate()
    {
        try
        {
            // Each stage runs only while it has work; the preload, the preparation and the model queue end by themselves.
            // Tick also picks up a gun whose own sounds finished decoding after the pass became ready.
            PresentationPreload.Tick();
            if (WeaponModels.HasWork) WeaponModels.TickPending();
            if (!_simulationsReady && !_preparationFailed) { PrepareVisuals(); PrepareSimulations(); }
            Tick();
            // Energy lights follow this frame's effects (local and remote) and fade on their own.
            PointLights.Tick(Time.time);
        }
        catch (Exception error) { Fault(error); }
    }

    // A damage call can have committed before throwing. Stop, never replay it or fire a stock shot.
    private void Fault(Exception error)
    {
        Plugin.Logger.ErrorOnce("An energy gun stopped. Any native writes already attempted are not retried. " + error);
        _faulted = true;
        _streamHeld = _chargeHeld = false;
        HideBeam();
        _charge.Cancel();
        _chargeAnnounced = false;
        _plasma?.Clear();
        _special?.Clear();
        PointLights.Clear();
    }

    // Runs as the local look camera starts to render, after every update of this frame has moved the player, the camera
    // and the held gun. Drawing in any update left a strafing player's beam start one frame behind the drawn muzzle.
    private static void PreCull(Camera camera)
    {
        var controller = _instance;
        // The managed flags decide first; the game's camera and holder are read only while a stream or charge is drawn.
        if (controller == null || !controller._streamHeld && !controller._chargeHeld) return;
        var holder = controller.LocalPlayer()?.FPItemHolder;
        if (controller._faulted || controller._weapon == null || holder == null ||
            holder.m_LookCamera == null || camera.Pointer != holder.m_LookCamera.Pointer ||
            holder.WieldedItem == null || holder.WieldedItem.Pointer != controller._weapon.Pointer) return;
        try { controller.PresentHeld(Time.time, holder.ItemAimTrigger ? new Ray(camera.transform.position, camera.transform.forward) : null); }
        catch (Exception error) { controller.Fault(error); }
    }

    // These queries never deal damage or bill ammo. The charge shows at its first-person size; aimed through an optic it also stays below the look camera's ray `sight`.
    private void PresentHeld(float now, Ray? sight)
    {
        if (!_streamHeld && !_chargeHeld) return;
        var weapon = _weapon!;
        var numbers = _gear!.Numbers;
        _shot.Trace(weapon, numbers.Shot);
        if (_chargeHeld)
        {
            _plasma!.Visuals.ChargingOrb(EnergyProjectiles.Capacity, _shot.Start,
                PlasmaPayload.FromCharge(_chargePower, numbers).BodyRadius, _mode == EnergyMode.PlasmaArc, firstPerson: true, sight);
            _effects!.Charge(_chargePower, _shot.Start);
        }
        else if (_mode == EnergyMode.Beam)
        {
            _effects!.Beam(_shot.Start, _shot.End, _shot.HasHit, now);
            _visuals!.Beam(_shot.Start, _shot.End, _shot.HasHit);
        }
        else
        {
            var direction = (_shot.End - _shot.Start).normalized;
            _special!.Visuals.Flame(_shot.Start, direction, _special.FlameRange(_gear, _shot.Start, direction));
        }
    }

    private void Tick()
    {
        EnergyNetwork.Channel.Ready = PresentationReady;
        if (_faulted && _mode != EnergyMode.Off) Disarm();
        if (!(EnergyNetwork.Ready && PresentationReady))
        {
            if (_mode != EnergyMode.Off) Disarm();
            // Other players' weapons stop with their ticks and messages: nothing they were showing may stay.
            ClearRemotes();
            PointLights.Clear();
            return;
        }
        if (!EnergyGearRegistry.Ready) return;
        var now = Time.time;
        // Released projectiles and a released beam's fading hold keep their own lifetime when switching guns or opening the map.
        if (!_faulted)
        {
            _effects?.Tick(now);
            _plasma?.Tick(Time.deltaTime, now);
            if (_special != null && (_mode != EnergyMode.Off || !_special.Idle))
                _special.Tick(Time.deltaTime, now, LookRotation(LocalPlayer()));
        }
        foreach (var pair in _remotes)
        {
            if (!SNet.TryGetPlayer(pair.Key, out var member) || member == null)
            { _departed.Add(pair.Key); continue; }
            try { pair.Value.Tick(Time.deltaTime, now); }
            catch (Exception error)
            {
                Plugin.Logger.ErrorOnce("Remote energy simulation stopped; committed damage will not be retried: " + error);
                pair.Value.StopAfterFault();
            }
        }
        foreach (var identity in _departed)
        { _remotes[identity].Dispose(); _remotes.Remove(identity); _remoteStates.Remove(identity); }
        _departed.Clear();
        // Nothing armed and no energy gun in the hand: only a wield event can change that.
        if (_mode == EnergyMode.Off && WieldedGear() == null) return;
        var player = LocalPlayer();
        if (player == null || !player.Alive)
        {
            if (_mode != EnergyMode.Off) Disarm();
            return;
        }
        if (_mode != EnergyMode.Off && _weapon == null) Disarm();
        var held = _localWielded;
        if (held != null && (held.Owner == null || held.Owner.Pointer != player.Pointer)) held = null;
        // The armed gun keeps its mode while holstered; putting it away and drawing it again play its handling cues.
        if (_mode != EnergyMode.Off && !_faulted) Wield(held != null && held.Pointer == _weapon!.Pointer);
        if (held == null)
            return;
        var selectedGear = WieldedGear();
        var selected = selectedGear?.Mode ?? EnergyMode.Off;
        if (_mode != EnergyMode.Off && (_weapon!.Pointer != held.Pointer || selectedGear != _gear))
            Disarm();
        if (selected != EnergyMode.Off && _mode == EnergyMode.Off && !_faulted)
        {
            if (_preparationFailed || !_simulationsReady) return;
            if (held.GearPartHolder == null || !WeaponModels.Ensure(held.GearPartHolder)) return;
            Arm(held, selectedGear!);
        }
        if (_mode == EnergyMode.Off || _faulted || _weapon!.Pointer != held.Pointer) return;
        _visuals!.Tick(now);
        // A lowered gun comes up on the fire input, as a stock gun's does; nothing is charged or drawn from the hip pose.
        if (held.FireButton && FocusStateManager.CurrentState == eFocusState.FPS && !held.IsReloading) ForgeCore.Combat.RaiseGun(player);
        if (_mode is not (EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc)) { NativeHeld(player, held, now); return; }
        if (!CanFire(player, held))
        {
            if (!_fireGateClosed) { CancelCharge(); StopHeldSounds(); _fireGateClosed = true; }
            return;
        }
        _fireGateClosed = false;
        Plasma(held, now);
    }

    private void Arm(BulletWeapon weapon, EnergyGear gear)
    {
        if (_effects == null) { _effects = _warmEffects; _warmEffects = null; }
        if (_visuals == null) { _visuals = _warmVisuals; _warmVisuals = null; }
        StopHeldSounds();
        if (gear.Mode is EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc)
        {
            weapon.m_archeType.OnStopFiring();
            weapon.m_archeType.OnStopChargeup();
        }
        _weapon = weapon; _gear = gear; _mode = gear.Mode; _wielded = true;
        ApplyLook(gear);
        // This frame's special-weapon tick ran before the arm; the flame is drawn from the current view at once.
        if (_special != null) _special.Visuals.ViewRotation = LookRotation(LocalPlayer());
        CancelCharge();
        _fireGateClosed = false;
        _lastNativeShot = float.NegativeInfinity;
        PublishArmed();
    }

    // Every arming, and every draw of the armed gun, is a new arm number in this player's row.
    private void PublishArmed()
    {
        if (++_armSequence == 0) ++_armSequence;
        EnergyNetwork.Armed.Publish(new ArmedGun { Arm = _armSequence, CategoryId = _gear!.CategoryId, Mode = (int)_mode }, Time.time);
    }

    // The sounds and effects every presentation object plays are the armed gun's own (its definition's Look). A beam or chain held under the
    // other gun's effects is let go first, so it never stays up under a handle the new look no longer releases.
    private void ApplyLook(EnergyGear gear)
    {
        var look = EnergyLook.Of(gear);
        if (_effects!.Look != look) { _effects.Hide(); _effects.Look = look; }
        if (_visuals!.Look != look) { _visuals.Hide(); _visuals.Look = look; }
        _plasma!.Visuals.Look = look;
        _special!.Visuals.Look = look;
    }

    internal static bool CanLaunch(BulletWeapon weapon, EnergyGear gear)
    {
        var controller = _instance;
        if (controller == null || !SessionReady()) return false;
        if (!EnergyNativeFire.Delayed(gear.Mode)) return true;
        if (weapon.Owner?.Owner?.IsBot == true)
            return ForgeCore.Session.IsHost && controller.RemoteFor(weapon.Owner.Owner.Lookup, authoritative: true).CanLaunch(gear);
        return gear.Mode switch
        {
            EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc => controller._plasma!.CanLaunch(gear),
            EnergyMode.Disc => controller._special!.CanLaunchDisc(gear),
            EnergyMode.BlackHole => controller._special!.CanLaunchHole,
            EnergyMode.Blast => controller._special!.CanLaunchShell,
            _ => false
        };
    }

    internal static float ReleasedPower(BulletWeapon weapon)
        => _instance?._weapon?.Pointer == weapon.Pointer ? _instance._releasedPower ?? 0f : 0f;

    internal static void NativeDischarge(BulletWeapon weapon, EnergyGear gear, Vector3 origin, Vector3 direction)
    {
        var controller = _instance;
        if (controller == null || !SessionReady()) throw new InvalidOperationException("Energy fire presentation/session is not ready.");
        var owner = weapon.Owner;
        FireVisual visual;
        if (owner.Owner.IsLocal && !owner.Owner.IsBot)
        {
            if (controller._weapon == null || controller._weapon.Pointer != weapon.Pointer)
            {
                controller.Disarm();
                controller.Arm(weapon, gear);
            }
            if (controller._faulted) throw new InvalidOperationException("This energy gun stopped after a native fault.");
            visual = controller._discharge.Resolve(weapon, gear, origin, direction, controller._special!, Time.time);
            controller._lastNativeShot = Time.time;
            controller.PresentNative(visual);
        }
        else
        {
            var remote = controller.RemoteFor(owner.Owner.Lookup, authoritative: true);
            visual = remote.NativeDischarge(weapon, gear, origin, direction, Time.time);
        }
        if (!ForgeCore.Session.IsHost && EnergyNativeFire.Delayed(gear.Mode))
            EnergyNetwork.Fire.ToHost(FireCommand.Create(visual.Sequence, gear, visual.Start, visual.Direction,
                (int)owner.DimensionIndex, visual.Power, visual.Damage));
        else EnergyNetwork.SendVisual(visual);
    }

    internal static void StopNativeDischarge(BulletWeapon weapon)
    {
        var controller = _instance;
        if (controller == null) return;
        if (weapon.Owner?.Owner is { IsLocal: true, IsBot: false })
        { controller._faulted = true; controller.StopHeldSounds(); }
        else if (weapon.Owner?.Owner is { } owner && controller._remotes.TryGetValue(owner.Lookup, out var remote))
            remote.StopAfterFault();
    }

    private void PresentNative(FireVisual visual)
    {
        var now = Time.time;
        switch (_mode)
        {
            case EnergyMode.Beam:
            case EnergyMode.Flame:
                // Fire runs before this frame's gun pose; PresentHeld draws the stream from the posed muzzle.
                _streamHeld = true;
                break;
            case EnergyMode.ArcChain:
                for (var index = 0; index < visual.Segments; index++)
                {
                    _starts[index] = index == 0 ? visual.Start : visual.End(index - 1);
                    _ends[index] = visual.End(index); _hits[index] = visual.Hit(index);
                }
                _effects!.Chain(_starts.AsSpan(0, visual.Segments), _ends.AsSpan(0, visual.Segments),
                    _hits.AsSpan(0, visual.Segments), now, _gear!.Numbers.ArcLifetime, _gear.Numbers.HopDelay);
                _visuals!.Chain(_starts.AsSpan(0, visual.Segments), _ends.AsSpan(0, visual.Segments),
                    _hits.AsSpan(0, visual.Segments), now, _gear.Numbers.HopDelay);
                break;
            case EnergyMode.PlasmaBlast:
            case EnergyMode.PlasmaArc:
                _plasma!.Launch(_weapon!.Owner, visual.Start, visual.Direction, visual.Power, _gear!, now, visual.Sequence, FlightEnds.Scale(_gear!, visual.Damage));
                _effects!.Discharge(_mode == EnergyMode.PlasmaArc, visual.Start);
                break;
            case EnergyMode.Disc:
                _special!.LaunchDisc(_weapon!.Owner, _gear!, visual.Start, visual.Direction, now, visual.Sequence, FlightEnds.Scale(_gear!, visual.Damage));
                break;
            case EnergyMode.BlackHole:
                _special!.LaunchHole(_weapon!.Owner, _gear!, visual.Start, visual.Direction, now, visual.Sequence, FlightEnds.Scale(_gear!, visual.Damage));
                break;
            case EnergyMode.Blast:
                _effects!.Hide();
                _visuals!.Blast(visual.Start, visual.Direction);
                _special!.Visuals.Origin(visual.Start); _special.Visuals.BlastLaunch();
                _special.LaunchShell(_weapon!.Owner, _gear!, visual.Start, visual.Direction, visual.Damage, now, visual.Sequence);
                break;
        }
    }

    // Held beam and flame follow the aim between native discharges; PresentHeld draws them.
    private static int Ammo(BulletWeapon weapon) => Weapon.InfiniteAmmo ? int.MaxValue : weapon.GetCurrentClip();

    private void NativeHeld(PlayerAgent player, BulletWeapon weapon, float now)
    {
        var ammo = Ammo(weapon);
        var held = weapon.FireButton && CanFire(player, weapon) &&
            (ammo >= 1 || now - _lastNativeShot < _gear!.Numbers.Shot.Interval);
        if (_mode == EnergyMode.ArcChain) { _effects!.ArcHold(held); return; }
        if (_mode is EnergyMode.Blast or EnergyMode.Disc or EnergyMode.BlackHole) return;
        if (!held) { StopHeldSounds(); return; }
        _streamHeld = true;
    }

    private void Plasma(BulletWeapon weapon, float now)
    {
        var numbers = _gear!.Numbers;
        var ammo = Ammo(weapon);
        var next = _nextOrbShots.TryGetValue(_gear!.CategoryId, out var value) ? value : double.NegativeInfinity;
        // No charge starts inside the cooldown, where its release would be dropped after the whole charge was spent.
        var allowed = ammo >= 1 && _plasma!.CanLaunch(_gear!) && now + 1e-7 >= next;
        var released = _charge.Update(now, weapon.FireButton, weapon.FireButtonPressed, allowed, numbers, out var power);
        if (_charge.Charging)
        {
            _chargePower = _charge.Power(now, numbers);
            _chargeHeld = true;
            if (!_chargeAnnounced || now >= _nextChargeRenew)
            {
                _shot.Trace(weapon, numbers.Shot);
                if (!_chargeAnnounced) { _chargeAnnounced = true; _chargeAnnouncedAt = now; _announcedGear = _gear; }
                _nextChargeRenew = now + EnergyProtocol.ChargeRenewSeconds;
                _announcedPosition = _shot.Start;
                PublishCharge(_gear!, true, _announcedPosition, now - _chargeAnnouncedAt);
            }
            if (_chargePower >= 1f && !_chargedFull)
            {
                _chargedFull = true;
                _plasma!.Visuals.Charged(EnergyProjectiles.Capacity);
            }
            return;
        }
        CancelCharge(audible: !released);
        if (!released) return;
        _nextOrbShots[_gear.CategoryId] = now - next >= numbers.Shot.Interval ? now + numbers.Shot.Interval : next + numbers.Shot.Interval;
        // Power belongs to this release's single native Fire, including a replacement body.
        _releasedPower = power;
        try { weapon.Fire(); }
        finally { _releasedPower = null; }
        weapon.FPItemHolder.DontRelax();
    }

    private void PublishCharge(EnergyGear gear, bool start, Vector3 position, float held)
    {
        var packet = ChargePacket.Create(0, gear, start, position, held);
        if (!ForgeCore.Session.IsHost) { EnergyNetwork.Charge.ToHost(packet); return; }
        if (SNet.LocalPlayer == null) throw new InvalidOperationException("Energy host has no local network player.");
        packet.Shooter = SNet.LocalPlayer.Lookup;
        EnergyNetwork.ChargeVisual.Broadcast(packet);
    }

    // CLR protocol structs/enums have no IL2CPP type metadata. Network dispatch calls these directly.
    private RemotePeerState StateFor(ulong identity)
    {
        if (_remoteStates.TryGetValue(identity, out var state)) return state;
        if (_remoteStates.Count >= 16) throw new InvalidOperationException("Remote session identity budget exhausted.");
        state = new RemotePeerState(); _remoteStates.Add(identity, state); return state;
    }

    private RemoteWeapon RemoteFor(ulong identity, bool authoritative, uint arm = 0)
    {
        var state = StateFor(identity);
        var rearmed = arm != 0 && (state.Arm == 0 || unchecked((int)(arm - state.Arm)) > 0);
        if (rearmed) state.Arm = arm;
        if (_remotes.TryGetValue(identity, out var remote))
        {
            if (!remote.Faulted || !rearmed) return remote;
            remote.Dispose(); _remotes.Remove(identity);
        }
        remote = new RemoteWeapon(authoritative, state); _remotes.Add(identity, remote); return remote;
    }

    /// <summary>Another player's armed gun (<see cref="EnergyNetwork.Armed"/>): its model, and what its shots need, are made now.</summary>
    internal void ReceiveArmed(ulong shooter, ArmedGun armed)
    {
        if (EnergyGears.Match(armed.CategoryId, armed.Mode) is not { } gear ||
            !SNet.TryGetPlayer(shooter, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null) return;
        var remote = RemoteFor(shooter, ForgeCore.Session.IsHost, armed.Arm);
        try { remote.SetModel(owner, owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>(), gear); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote energy model stopped: " + error);
            remote.StopAfterFault();
        }
    }

    internal void ReceiveCharge(ulong sender, ChargePacket packet)
    {
        if (!EnergyGearRegistry.Ready) return;
        // A charge from a player who is down, switching guns or leaving has nothing to charge.
        if (!SNet.TryGetPlayer(sender, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null || owner.Owner == null || owner.Owner.Lookup != sender) return;
        // A stop always counts: a shooter who just switched guns or went down still has a charge to end, here and on every other machine.
        if (packet.Phase == 1)
        {
            var weapon = owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
            if (!owner.Alive || weapon == null || weapon.Owner == null || weapon.Owner.Pointer != owner.Pointer ||
                !EnergyGearRegistry.Matches(weapon, packet.CategoryId, packet.Mode)) return;
        }
        if (!_remotes.TryGetValue(sender, out var remote))
        {
            if (packet.Phase != 1) return;
            remote = RemoteFor(sender, authoritative: true);
        }
        try
        {
            // Charge packets only control presentation; a launch does not require a host-observed charge.
            if (packet.Phase == 1) { if (!remote.StartCharge(owner, packet, Time.time)) return; }
            else remote.EndCharge(packet);
            packet.Shooter = sender;
            EnergyNetwork.ChargeVisual.Broadcast(packet);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote energy charge stopped: " + error);
            remote.StopAfterFault();
        }
    }

    internal void ReceiveChargeVisual(ChargePacket packet)
    {
        if (SNet.LocalPlayer == null || packet.Shooter == SNet.LocalPlayer.Lookup ||
            !SNet.TryGetPlayer(packet.Shooter, out var peer) || peer == null) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null) return;
        if (!_remotes.TryGetValue(packet.Shooter, out var remote))
        {
            if (packet.Phase != 1) return;
            remote = RemoteFor(packet.Shooter, authoritative: false);
        }
        try
        {
            if (packet.Phase == 1) remote.StartCharge(owner, packet, Time.time);
            else remote.EndCharge(packet);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote energy charge visual stopped: " + error);
            remote.StopAfterFault();
        }
    }

    internal void ReceiveCommand(ulong sender, FireCommand command)
    {
        if (!PresentationReady || !SNet.TryGetPlayer(sender, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        var weapon = owner?.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        if (owner == null || owner.Owner == null || owner.Owner.Lookup != sender) return;
        var remote = RemoteFor(sender, authoritative: true);
        try
        {
            if (remote.TryLaunch(owner, weapon, command, Time.time, out var visual)) EnergyNetwork.Broadcast(visual);
        }
        // Launches that a stalled link delivers together can find no free slot: this shot is dropped, the gun is fine.
        catch (BudgetExhausted error) { Plugin.Logger.ErrorOnce("energylab-remote-shot-dropped", "A remote energy shot was dropped: " + error.Message); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote energy launch stopped; committed effects will not be replayed: " + error);
            remote.StopAfterFault();
        }
    }

    internal void ReceiveVisual(FireVisual visual)
    {
        if (SNet.LocalPlayer == null) return;
        if (visual.Shooter == SNet.LocalPlayer.Lookup) return;
        // The shot of a player who just left, or has no body yet, has nothing to be drawn from.
        if (!SNet.TryGetPlayer(visual.Shooter, out var peer) || peer == null) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null) return;
        var remote = RemoteFor(visual.Shooter, authoritative: false);
        try { remote.PresentAccepted(owner, visual, Time.time); }
        catch (BudgetExhausted error) { Plugin.Logger.ErrorOnce("energylab-remote-shot-dropped", "A remote energy shot was dropped: " + error.Message); }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote energy visual stopped: " + error);
            remote.StopAfterFault();
        }
    }

    /// <summary>The host's word on where a projectile ended (<see cref="FlightEnds"/>): this player's own, or another's.</summary>
    internal void ReceiveFlightEnd(FlightEnd end)
    {
        if (SNet.LocalPlayer == null || !float.IsFinite(end.X + end.Y + end.Z) || !Enum.IsDefined((FlightKind)end.Kind)) return;
        var kind = (FlightKind)end.Kind;
        var now = Time.time;
        try
        {
            if (end.Shooter != SNet.LocalPlayer.Lookup)
            {
                if (_remotes.TryGetValue(end.Shooter, out var remote)) remote.Finish(kind, end.Sequence, end.Position, now);
            }
            else if (kind == FlightKind.Plasma) _plasma?.Finish(end.Sequence, end.Position, now);
            else _special?.Finish(kind, end.Sequence, end.Position, now);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A projectile's end could not be applied: " + error); }
    }

    internal void ReceiveNativeVisual(ulong sender, FireVisual visual)
    {
        if (EnergyNativeFire.Delayed((EnergyMode)visual.Mode) ||
            !SNet.TryGetPlayer(sender, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null || owner.Owner == null || owner.Owner.Lookup != sender) return;
        visual.Shooter = sender;
        var remote = RemoteFor(sender, authoritative: true);
        try
        {
            if (remote.PresentAccepted(owner, visual, Time.time)) EnergyNetwork.Broadcast(visual);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Remote native energy presentation stopped: " + error);
            remote.StopAfterFault();
        }
    }

    // ForgeCore moved to another world (a session or level began, the level ended, the host changed, a checkpoint was reloaded) and
    // dropped what belonged to the earlier one: so does this. A fault stops the gun of the world it happened in (a committed hit is
    // never replayed there), so it ends here too and the next world's guns fire.
    private void WorldChanged(ForgeCore.NetWorld world)
    {
        _plasma?.Clear(); _special?.Clear();
        Disarm(true);
        ClearRemotes(); _remoteStates.Clear();
        _nextOrbShots.Clear();
        _preparationFailed = false;
        _faulted = false;
    }

    private void ClearRemotes()
    {
        foreach (var remote in _remotes.Values) remote.Dispose();
        _remotes.Clear();
        _departed.Clear();
    }

    /// <summary>The armed gun going into or out of the hand; putting it away ends every held sound.</summary>
    private void Wield(bool wielding)
    {
        if (wielding == _wielded) return;
        _wielded = wielding;
        if (wielding)
        { PublishArmed(); return; }
        CancelCharge();
        StopHeldSounds();
    }

    /// <summary>Ends every sound that lasts while the trigger is held: beam, chain hum and flame.</summary>
    private void StopHeldSounds()
    {
        _streamHeld = false;
        HideBeam();
        _special?.Visuals.StopFlame();
    }

    private void HideBeam()
    {
        _effects?.Hide();
        _visuals?.Hide();
    }

    private void CancelCharge(bool audible = true)
    {
        if (_chargeAnnounced)
        {
            if (SessionReady()) PublishCharge(_announcedGear!, false, _announcedPosition, 0f);
            _chargeAnnounced = false;
        }
        _chargeHeld = false;
        if (audible) _effects?.CancelCharge();
        else _effects?.StopCharge();
        _charge.Cancel();
        _chargedFull = false;
        _plasma?.Visuals.Hide(EnergyProjectiles.Capacity);
    }

    private static bool CanFire(PlayerAgent player, BulletWeapon weapon)
    {
        if (FocusStateManager.CurrentState != eFocusState.FPS) return false;
        if (weapon.IsReloading) return false;
        if (player.FPItemHolder.ItemDownTrigger || player.Locomotion == null || ForgeCore.Combat.GunLowered(player)) return false;
        var allowed = player.Locomotion.m_currentStateEnum is PlayerLocomotion.PLOC_State.Stand
            or PlayerLocomotion.PLOC_State.Crouch or PlayerLocomotion.PLOC_State.Jump
            or PlayerLocomotion.PLOC_State.Fall or PlayerLocomotion.PLOC_State.Land;
        return allowed;
    }

    private void Disarm(bool releaseResources = false)
    {
        EnergyNetwork.Armed.Clear();
        _streamHeld = false;
        CancelCharge();
        if (releaseResources)
        {
            _instancesReady = _simulationsReady = false;
            _effects?.Dispose(); _effects = null;
            _visuals?.Dispose(); _visuals = null;
            _plasma?.Dispose(); _plasma = null;
            _special?.Dispose(); _special = null;
        }
        else
        {
            StopHeldSounds();
        }
        _weapon = null;
        _wielded = false;
        _mode = EnergyMode.Off; _gear = null;
        _lastNativeShot = float.NegativeInfinity;
        _fireGateClosed = false;
    }

    private void Shutdown()
    {
        ForgeCore.GameEvents.LevelEnded -= OnLevelEnded;
        ForgeCore.GameEvents.CheckpointReloaded -= ForgetPlayer;
        ForgeCore.GameEvents.StateChanged -= ReadState;
        ForgeCore.Net.WorldChanged -= WorldChanged;
        if (_preCull != null)
        {
            Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, _preCull)?.Cast<Camera.CameraCallback>();
            _preCull = null;
        }
        WeaponBindings.Clear();
        _warmEffects?.Dispose(); _warmVisuals?.Dispose(); _warmPlasma?.Dispose(); _warmSpecial?.Dispose();
        _warmEffects = null; _warmVisuals = null; _warmPlasma = null; _warmSpecial = null;
        Disarm(true);
        ClearRemotes();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
