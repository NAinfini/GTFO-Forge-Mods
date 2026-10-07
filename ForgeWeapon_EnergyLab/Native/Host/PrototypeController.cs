using System;
using System.Collections;
using System.Collections.Generic;
using ForgeWeaponEnergyLabExperimental.Presentation;
using Gear;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

internal sealed class PrototypeController : MonoBehaviour
{
    private static PrototypeController? _instance;
    private readonly Dictionary<uint, double> _nextOrbShots = new();
    private float? _releasedPower;
    private readonly PlasmaCharge _charge = new();
    private readonly EnergyDischarge _discharge = new();
    private readonly NativeShot[] _shots = new NativeShot[1];
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
    private IEnumerator? _vfxPreparation;
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
    private bool _wielded;
    private float _nextModePublish;
    // Muzzle-anchored first-person effects are drawn as the look camera starts to render (PreCull).
    private bool _streamHeld, _chargeHeld;
    private float _chargePower;
    private Camera.CameraCallback? _preCull;

    public PrototypeController(IntPtr pointer) : base(pointer) { }

    private void Awake()
    {
        _instance = this;
        for (var index = 0; index < _shots.Length; index++) _shots[index] = new NativeShot();
        _preCull = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(PreCull));
        Camera.onPreCull = Camera.onPreCull == null ? _preCull
            : Il2CppSystem.Delegate.Combine(Camera.onPreCull, _preCull).Cast<Camera.CameraCallback>();
    }

    internal static bool Owns(BulletWeapon? weapon)
        => EnergyGearRegistry.ModeOf(weapon) != EnergyMode.Off;

    private static bool SessionReady()
        => EnergyNetwork.Ready && _instance?.PresentationReady == true;

    [HideFromIl2Cpp]
    internal bool PresentationReady => EnergyNetwork.WorldReady && EnergyGearRegistry.Ready && PresentationPreload.Ready &&
        _instancesReady && _simulationsReady && !_preparationFailed && !_faulted;

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
            else
            {
                _vfxPreparation ??= PrepareVfx();
                if (_vfxPreparation.MoveNext()) return;
                (_vfxPreparation as IDisposable)?.Dispose(); _vfxPreparation = null;
                _instancesReady = true;
            }
        }
        catch (Exception error)
        {
            _preparationFailed = true;
            (_vfxPreparation as IDisposable)?.Dispose(); _vfxPreparation = null;
            Plugin.Error("Energy instance preparation failed: " + error);
        }
    }

    [HideFromIl2Cpp]
    private IEnumerator PrepareVfx()
    {
        var phases = new[] { _warmVisuals!.Prepare(), _warmPlasma!.Prepare(), _warmSpecial!.Prepare() };
        foreach (var phase in phases)
        {
            using var cleanup = phase as IDisposable;
            while (phase.MoveNext()) yield return null;
        }
    }

    private void PrepareSimulations()
    {
        if (_simulationsReady || _preparationFailed || !EnergyNetwork.WorldReady || !_instancesReady) return;
        try
        {
            // Allocate physics/contacts buffers before first wield,
            // one simulator per frame; equip takes the prepared instances without new native arrays.
            if (_plasma == null)
            {
                _plasma = new EnergyProjectiles(SNet.IsMaster, _warmPlasma);
                _warmPlasma = null;
            }
            else if (_special == null)
            {
                _special = new SpecialWeapons(SNet.IsMaster, _warmSpecial);
                _warmSpecial = null;
                _simulationsReady = true;
            }
        }
        catch (Exception error)
        {
            _preparationFailed = true;
            Plugin.Error("Energy simulation preparation failed: " + error);
        }
    }

    private void LateUpdate()
    {
        try
        {
            PresentationPreload.Tick();
            EnergyIcons.Tick();
            WeaponModels.TickPending();
            WeaponModels.TickAnimations();
            PrepareVisuals();
            PrepareSimulations();
            Tick();
            // Energy lights follow this frame's effects (local and remote) and fade on their own.
            EnergyLights.Tick(Time.time);
            var fp = PlayerManager.GetLocalPlayerAgent()?.FPItemHolder;
            // Energy voices are placed against the player's camera every frame, after this frame's effects moved.
            var camera = fp?.m_LookCamera;
            EnergySound.Listen(camera != null ? camera.transform : null);
        }
        catch (Exception error) { Fault(error); }
    }

    // A damage call can have committed before throwing. Stop, never replay it or fire a stock shot.
    [HideFromIl2Cpp]
    private void Fault(Exception error)
    {
        Plugin.Error("Energy experiment stopped. Any native writes already attempted are not retried. " + error);
        _faulted = true;
        _streamHeld = _chargeHeld = false;
        HideBeam();
        _charge.Cancel();
        _chargeAnnounced = false;
        _plasma?.Clear();
        _special?.Clear();
        EnergyLights.Clear();
    }

    // Runs as the local look camera starts to render, after every update of this frame has moved the player, the camera
    // and the held gun. Drawing in any update left a strafing player's beam start one frame behind the drawn muzzle.
    private static void PreCull(Camera camera)
    {
        var controller = _instance;
        var holder = PlayerManager.GetLocalPlayerAgent()?.FPItemHolder;
        if (controller == null || controller._faulted || controller._weapon == null || holder == null ||
            holder.m_LookCamera == null || camera.Pointer != holder.m_LookCamera.Pointer ||
            holder.WieldedItem == null || holder.WieldedItem.Pointer != controller._weapon.Pointer) return;
        try { controller.PresentHeld(Time.time); }
        catch (Exception error) { controller.Fault(error); }
    }

    // These queries never deal damage or bill ammo.
    private void PresentHeld(float now)
    {
        if (!_streamHeld && !_chargeHeld) return;
        var weapon = _weapon!;
        var numbers = _gear!.Numbers;
        _shots[0].Trace(weapon, numbers.Shot);
        if (_chargeHeld)
        {
            _plasma!.Visuals.ChargingOrb(EnergyProjectiles.Capacity, _shots[0].Start,
                PlasmaPayload.FromCharge(_chargePower, numbers).BodyRadius, now, _mode == EnergyMode.PlasmaArc);
            _effects!.Charge(_chargePower, _shots[0].Start);
        }
        else if (_mode == EnergyMode.Beam)
        {
            _effects!.Beam(_shots[0].Start, _shots[0].End, _shots[0].HasHit, now);
            _visuals!.Beam(_shots[0].Start, _shots[0].End, _shots[0].HasHit, now);
        }
        else
        {
            var direction = (_shots[0].End - _shots[0].Start).normalized;
            _special!.Visuals.Flame(_shots[0].Start, direction, _special.FlameRange(_gear, _shots[0].Start, direction), now);
        }
    }

    private void Tick()
    {
        EnergyNetwork.Pump(this);
        if (!SessionReady())
        {
            if (_mode != EnergyMode.Off) Disarm();
            EnergyLights.Clear();
            return;
        }
        if (!EnergyGearRegistry.Ready) return;
        var now = Time.time;
        var player = PlayerManager.GetLocalPlayerAgent();
        // Released projectiles and a released beam's fading hold keep their own lifetime when switching guns or opening the map.
        if (!_faulted)
        {
            _effects?.Tick(now);
            _plasma?.Tick(Time.deltaTime, now);
            _special?.Tick(Time.deltaTime, now, player?.FPItemHolder?.m_LookCamera?.transform.rotation ?? Quaternion.identity);
        }
        foreach (var pair in _remotes)
        {
            if (!SNet.TryGetPlayer(pair.Key, out var member) || member == null)
            { _departed.Add(pair.Key); continue; }
            try { pair.Value.Tick(Time.deltaTime, now); }
            catch (Exception error)
            {
                Plugin.Error("Remote energy simulation stopped; committed damage will not be retried: " + error);
                pair.Value.StopAfterFault();
            }
        }
        foreach (var identity in _departed)
        { _remotes[identity].Dispose(); _remotes.Remove(identity); }
        _departed.Clear();
        if (player == null || !player.Alive)
        {
            if (_mode != EnergyMode.Off) Disarm();
            return;
        }
        if (_mode != EnergyMode.Off && _weapon == null) Disarm();
        var held = player.FPItemHolder?.WieldedItem?.TryCast<BulletWeapon>();
        if (held != null && (held.Owner == null || held.Owner.Pointer != player.Pointer)) held = null;
        // The armed gun keeps its mode while holstered; putting it away and drawing it again play its handling cues.
        if (_mode != EnergyMode.Off && !_faulted) Wield(held != null && held.Pointer == _weapon!.Pointer);
        if (_mode != EnergyMode.Off && _wielded && now >= _nextModePublish)
        {
            PublishMode(_mode);
            _nextModePublish = now + 2f;
        }
        if (held == null)
            return;
        var selectedGear = EnergyGearRegistry.DefinitionOf(held);
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
        if (_mode is not (EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc)) { NativeHeld(player, held, now); return; }
        if (!CanFire(player, held))
        {
            if (!_fireGateClosed) { CancelCharge(); StopHeldSounds(); _fireGateClosed = true; }
            return;
        }
        _fireGateClosed = false;
        Plasma(held, now);
    }

    [HideFromIl2Cpp]
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
        if (++_armSequence == 0) ++_armSequence;
        _weapon = weapon; _gear = gear; _mode = gear.Mode; _wielded = true;
        CancelCharge();
        _fireGateClosed = false;
        _lastNativeShot = float.NegativeInfinity;
        PublishMode(_mode);
        _nextModePublish = Time.time + 2f;
    }

    [HideFromIl2Cpp]
    internal static bool CanLaunch(BulletWeapon weapon, EnergyGear gear)
    {
        var controller = _instance;
        if (controller == null || !SessionReady()) return false;
        if (!EnergyNativeFire.Delayed(gear.Mode)) return true;
        if (weapon.Owner?.Owner?.IsBot == true)
            return SNet.IsMaster && controller.RemoteFor(weapon.Owner.Owner.Lookup, authoritative: true).CanLaunch(gear);
        return gear.Mode switch
        {
            EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc => controller._plasma!.CanLaunch(gear),
            EnergyMode.Disc => controller._special!.CanLaunchDisc(gear),
            EnergyMode.BlackHole => controller._special!.CanLaunchHole,
            _ => false
        };
    }

    [HideFromIl2Cpp]
    internal static float ReleasedPower(BulletWeapon weapon)
        => _instance?._weapon?.Pointer == weapon.Pointer ? _instance._releasedPower ?? 0f : 0f;

    [HideFromIl2Cpp]
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
        if (!SNet.IsMaster && EnergyNativeFire.Delayed(gear.Mode))
            EnergyNetwork.Send(FireCommand.Create(visual.Sequence, gear, visual.Start, visual.Direction,
                (int)owner.DimensionIndex, visual.Power));
        else EnergyNetwork.SendVisual(visual);
    }

    [HideFromIl2Cpp]
    internal static void StopNativeDischarge(BulletWeapon weapon)
    {
        var controller = _instance;
        if (controller == null) return;
        if (weapon.Owner?.Owner is { IsLocal: true, IsBot: false })
        { controller._faulted = true; controller.StopHeldSounds(); }
        else if (weapon.Owner?.Owner is { } owner && controller._remotes.TryGetValue(owner.Lookup, out var remote))
            remote.StopAfterFault();
    }

    [HideFromIl2Cpp]
    private void PresentNative(FireVisual visual)
    {
        var now = Time.time;
        switch (_mode)
        {
            case EnergyMode.Beam:
            case EnergyMode.Flame:
                // Fire runs before this frame's gun pose; PresentFirstPerson draws the stream from the posed muzzle.
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
                _plasma!.Launch(_weapon!.Owner, visual.Start, visual.Direction, visual.Power, _gear!, now);
                _effects!.Discharge(_mode == EnergyMode.PlasmaArc, visual.Start);
                break;
            case EnergyMode.Disc:
                _special!.LaunchDisc(_weapon!.Owner, _gear!, visual.Start, visual.Direction, now);
                break;
            case EnergyMode.BlackHole:
                _special!.LaunchHole(_weapon!.Owner, _gear!, visual.Start, visual.Direction, now);
                break;
            case EnergyMode.Blast:
                if (visual.Hit(0)) _special!.Visuals.GrenadeExplosion(visual.End(0) - visual.Direction * 0.04f,
                    _gear!.Numbers.BlastRadius, now);
                _effects!.Hide();
                _visuals!.Blast(visual.Start, now);
                _special!.Visuals.Origin(visual.Start); _special.Visuals.BlastLaunch();
                break;
        }
    }

    // Held beam and flame follow the aim between native discharges; PresentFirstPerson draws them.
    private void NativeHeld(PlayerAgent player, BulletWeapon weapon, float now)
    {
        var ammo = WeaponAmmo.Available(weapon);
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
        var ammo = WeaponAmmo.Available(weapon);
        var allowed = ammo >= 1 && _plasma!.CanLaunch(_gear!);
        var released = _charge.Update(now, weapon.FireButton, weapon.FireButtonPressed, allowed, numbers, out var power);
        if (_charge.Charging)
        {
            _chargePower = _charge.Power(now, numbers);
            _chargeHeld = true;
            if (!_chargeAnnounced)
            {
                _shots[0].Trace(weapon, numbers.Shot);
                _chargeAnnounced = true;
                _announcedGear = _gear;
                _announcedPosition = _shots[0].Start;
                PublishCharge(_gear!, true, _announcedPosition);
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
        var next = _nextOrbShots.TryGetValue(_gear.CategoryId, out var value) ? value : double.NegativeInfinity;
        if (now + 1e-7 < next) return;
        _nextOrbShots[_gear.CategoryId] = now - next >= numbers.Shot.Interval ? now + numbers.Shot.Interval : next + numbers.Shot.Interval;
        // Power belongs to this release's single native Fire, including a replacement body.
        _releasedPower = power;
        try { weapon.Fire(); }
        finally { _releasedPower = null; }
        weapon.FPItemHolder.DontRelax();
    }

    [HideFromIl2Cpp]
    private void PublishCharge(EnergyGear gear, bool start, Vector3 position)
    {
        var packet = ChargePacket.Create(0, gear, start, position);
        if (!SNet.IsMaster) { EnergyNetwork.SendCharge(packet); return; }
        if (SNet.LocalPlayer == null) throw new InvalidOperationException("Energy host has no local network player.");
        packet.Shooter = SNet.LocalPlayer.Lookup;
        EnergyNetwork.BroadcastCharge(packet);
    }

    [HideFromIl2Cpp]
    private void PublishMode(EnergyMode mode)
    {
        if (!SessionReady()) return;
        var packet = ModePacket.Create(mode == EnergyMode.Off ? null : _gear, _armSequence);
        if (!SNet.IsMaster) { EnergyNetwork.SendMode(packet); return; }
        if (SNet.LocalPlayer == null) throw new InvalidOperationException("Energy host has no local network player.");
        packet.Shooter = SNet.LocalPlayer.Lookup;
        EnergyNetwork.BroadcastMode(packet);
    }

    // CLR protocol structs/enums have no IL2CPP type metadata. Network dispatch calls these directly.
    [HideFromIl2Cpp]
    private RemotePeerState StateFor(ulong identity)
    {
        if (_remoteStates.TryGetValue(identity, out var state)) return state;
        if (_remoteStates.Count >= 16) throw new InvalidOperationException("Remote session identity budget exhausted.");
        state = new RemotePeerState(); _remoteStates.Add(identity, state); return state;
    }

    [HideFromIl2Cpp]
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

    [HideFromIl2Cpp]
    internal void ReceiveMode(ulong sender, ModePacket packet)
    {
        if (!EnergyGears.IsSelection(packet.CategoryId, packet.Mode) ||
            !SNet.TryGetPlayer(sender, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null || owner.Owner == null || owner.Owner.Lookup != sender) return;
        var mode = (EnergyMode)packet.Mode;
        var weapon = owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        if (mode != EnergyMode.Off && (!owner.Alive || weapon == null || weapon.Owner == null ||
            weapon.Owner.Pointer != owner.Pointer || !EnergyGearRegistry.Matches(weapon, packet.CategoryId, packet.Mode))) return;
        if (mode == EnergyMode.Off && !_remotes.ContainsKey(sender)) return;
        var remote = RemoteFor(sender, authoritative: true, packet.Arm);
        try
        {
            remote.SetModel(owner, weapon, EnergyGears.Match(packet.CategoryId, packet.Mode));
            packet.Shooter = sender;
            EnergyNetwork.BroadcastMode(packet);
        }
        catch (Exception error)
        {
            Plugin.Error("Remote energy model stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
    internal void ReceiveModeVisual(ModePacket packet)
    {
        if (SNet.LocalPlayer == null || packet.Shooter == SNet.LocalPlayer.Lookup ||
            !EnergyGears.IsSelection(packet.CategoryId, packet.Mode) ||
            !SNet.TryGetPlayer(packet.Shooter, out var peer) || peer == null) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null) return;
        var mode = (EnergyMode)packet.Mode;
        if (mode == EnergyMode.Off && !_remotes.ContainsKey(packet.Shooter)) return;
        var remote = RemoteFor(packet.Shooter, authoritative: false, packet.Arm);
        try { remote.SetModel(owner, owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>(), EnergyGears.Match(packet.CategoryId, packet.Mode)); }
        catch (Exception error)
        {
            Plugin.Error("Remote energy model visual stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
    internal void ReceiveCharge(ulong sender, ChargePacket packet)
    {
        if (!EnergyGearRegistry.Ready) return;
        // A charge from a player who is down, switching guns or leaving has nothing to charge.
        if (!SNet.TryGetPlayer(sender, out var peer) || peer == null || peer.IsLocal || peer.IsBot) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        var weapon = owner?.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        if (owner == null || !owner.Alive || owner.Owner == null || owner.Owner.Lookup != sender ||
            weapon == null || weapon.Owner == null || weapon.Owner.Pointer != owner.Pointer) return;
        if (!EnergyGearRegistry.Matches(weapon, packet.CategoryId, packet.Mode)) return;
        if (!_remotes.TryGetValue(sender, out var remote))
        {
            if (packet.Phase != 1) return;
            remote = RemoteFor(sender, authoritative: true);
        }
        try
        {
            // Charge packets only control presentation; a launch does not require a host-observed charge.
            var accepted = packet.Phase == 1 ? remote.StartCharge(owner, packet, Time.time)
                : remote.EndCharge(packet);
            if (!accepted) return;
            packet.Shooter = sender;
            EnergyNetwork.BroadcastCharge(packet);
        }
        catch (Exception error)
        {
            Plugin.Error("Remote energy charge stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
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
            Plugin.Error("Remote energy charge visual stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
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
        catch (Exception error)
        {
            Plugin.Error("Remote energy launch stopped; committed effects will not be replayed: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
    internal void ReceiveVisual(FireVisual visual)
    {
        if (SNet.LocalPlayer == null) return;
        if (visual.Shooter == SNet.LocalPlayer.Lookup) return;
        // The shot of a player who just left, or has no body yet, has nothing to be drawn from.
        if (!SNet.TryGetPlayer(visual.Shooter, out var peer) || peer == null) return;
        var owner = peer.PlayerAgent?.TryCast<PlayerAgent>();
        if (owner == null) return;
        if (!_remotes.TryGetValue(visual.Shooter, out var remote))
        {
            remote = RemoteFor(visual.Shooter, authoritative: false);
        }
        try { remote.PresentAccepted(owner, visual, Time.time); }
        catch (Exception error)
        {
            Plugin.Error("Remote energy visual stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
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
            Plugin.Error("Remote native energy presentation stopped: " + error);
            remote.StopAfterFault();
        }
    }

    [HideFromIl2Cpp]
    internal void SessionChanged()
    {
        _plasma?.Clear(); _special?.Clear();
        Disarm(true);
        ClearRemotes(); _remoteStates.Clear();
        _nextOrbShots.Clear();
        _preparationFailed = false;
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
        { if (++_armSequence == 0) ++_armSequence; PublishMode(_mode); return; }
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
            if (SessionReady()) PublishCharge(_announcedGear!, false, _announcedPosition);
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
        if (player.FPItemHolder.ItemDownTrigger || player.Locomotion == null) return false;
        var allowed = player.Locomotion.m_currentStateEnum is PlayerLocomotion.PLOC_State.Stand
            or PlayerLocomotion.PLOC_State.Crouch or PlayerLocomotion.PLOC_State.Jump
            or PlayerLocomotion.PLOC_State.Fall or PlayerLocomotion.PLOC_State.Land;
        return allowed;
    }

    private void Disarm(bool releaseResources = false)
    {
        if (_mode != EnergyMode.Off && SessionReady())
            try { PublishMode(EnergyMode.Off); }
            catch (Exception error) { Plugin.Error("Energy model disarm announcement failed: " + error); }
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
        _faulted = false;
    }

    private void OnDestroy()
    {
        if (_preCull != null)
        {
            Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, _preCull)?.Cast<Camera.CameraCallback>();
            _preCull = null;
        }
        WeaponBindings.Clear();
        _warmEffects?.Dispose(); _warmVisuals?.Dispose(); _warmPlasma?.Dispose(); _warmSpecial?.Dispose();
        _warmEffects = null; _warmVisuals = null; _warmPlasma = null; _warmSpecial = null;
        (_vfxPreparation as IDisposable)?.Dispose(); _vfxPreparation = null;
        Disarm(true);
        ClearRemotes();
        EnergyNetwork.Reset();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
