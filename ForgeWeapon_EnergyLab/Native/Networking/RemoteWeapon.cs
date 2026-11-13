using System;
using ForgeWeaponEnergyLab.Presentation;
using Gear;
using Player;
using ForgeCore;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

// Delayed modes are host simulations. Instant modes only deal hits from a host bot's native Fire callback.
internal sealed class RemoteWeapon : IDisposable
{
    private readonly bool _authoritative;
    private readonly RemotePeerState _state;
    private readonly EnergyDischarge _discharge = new();
    private readonly Vector3[] _starts = new Vector3[EnergyLimits.ArcTargets];
    private readonly Vector3[] _ends = new Vector3[EnergyLimits.ArcTargets];
    private readonly bool[] _hits = new bool[EnergyLimits.ArcTargets];
    private EnergyEffects? _effects;
    private EnergyVisuals? _visuals;
    private EnergyProjectiles? _plasma;
    private SpecialWeapons? _special;
    private int _preparedModes;
    private PlayerAgent? _chargeOwner;
    private EnergyGear? _chargeGear;
    /// <summary>The charge point as the shooter reported it, in the shooter's yaw frame, so it turns with the shooter.</summary>
    private Vector3 _chargeLocal;
    private float _chargeStartedAt, _chargeUntil;
    private bool _charging, _chargedFull;
    private bool _disposed;
    internal bool Faulted { get; private set; }
    private float _beamUntil, _flameUntil, _arcHoldUntil;
    /// <summary>The held beam or flame between two shots: it stays on the gun this machine shows, as the shooter moves; its far end and aim are the last shot's.</summary>
    private PlayerAgent? _streamOwner;
    private BulletWeapon? _streamWeapon;
    private EnergyGear? _streamGear;
    private Vector3 _streamLocal, _streamEnd, _streamDirection;
    private float _streamReach;
    private bool _streamHit;
    private Quaternion _look = Quaternion.identity;
    private EnergyLook _gearLook = EnergyLook.Default;
    private const float MaximumShownOffset = 3f;

    internal RemoteWeapon(bool authoritative, RemotePeerState state)
    {
        _authoritative = authoritative;
        _state = state;
    }

    internal void SetModel(PlayerAgent owner, BulletWeapon? weapon, EnergyGear? gear)
    {
        if (_disposed || gear == null || !owner.Alive || weapon == null || weapon.Owner == null ||
            weapon.Owner.Pointer != owner.Pointer || EnergyGearRegistry.DefinitionOf(weapon) != gear ||
            weapon.GearPartHolder == null) return;
        WeaponModels.Ensure(weapon.GearPartHolder);
        // The shooter's armed row arrives when it takes the gun up; the voices and buffers its shots need are made then, not mid-fight on its first shot.
        if (PresentationPreload.Ready) PreparePresentation(gear);
    }

    internal bool CanLaunch(EnergyGear gear)
    {
        if (_disposed) return false;
        PreparePresentation(gear);
        return gear.Mode switch
        {
            EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc => Plasma().CanLaunch(gear),
            EnergyMode.Disc => Special().CanLaunchDisc(gear),
            EnergyMode.BlackHole => Special().CanLaunchHole,
            EnergyMode.Blast => Special().CanLaunchShell,
            _ => true
        };
    }

    // Native Fire already spent the shooter's round. The host accepts a launch, never bills or replays Fire.
    internal bool TryLaunch(PlayerAgent owner, BulletWeapon? weapon, FireCommand command, float now, out FireVisual visual)
    {
        visual = default;
        if (!_authoritative || _disposed || EnergyGears.Match(command.CategoryId, command.Mode) is not { } gear ||
            !EnergyNativeFire.Delayed(gear.Mode) || !owner.Alive || command.Dimension != (int)owner.DimensionIndex ||
            !ForgeCore.Vec.Finite(owner.Position) ||
            !ForgeCore.Vec.Finite(command.Start) || !ForgeCore.Vec.Finite(command.Direction) || command.Direction.sqrMagnitude < 0.25f ||
            command.Direction.sqrMagnitude > 2.25f || !float.IsFinite(command.Power) || command.Power < 0f || command.Power > 1f ||
            !float.IsFinite(command.Damage) || command.Damage < 0f ||
            (command.Start - owner.Position).sqrMagnitude > EnergyProtocol.MaximumMuzzleDistance * EnergyProtocol.MaximumMuzzleDistance ||
            !_state.BeginLaunch(command.Sequence)) return false;
        // Consume the watermark before any side effect. A fault cannot replay a partially launched projectile.
        // The shooter may have switched guns before arrival; the current gun only binds presentation.
        PreparePresentation(gear);
        SetModel(owner, weapon, gear);
        visual = FireVisual.From(command, owner.Owner.Lookup);
        Present(owner, weapon, visual, now);
        return true;
    }

    internal FireVisual NativeDischarge(BulletWeapon weapon, EnergyGear gear, Vector3 origin, Vector3 direction, float now)
    {
        if (!_authoritative || _disposed) throw new InvalidOperationException("The bot's energy discharge is unavailable.");
        PreparePresentation(gear);
        SetModel(weapon.Owner, weapon, gear);
        var visual = _discharge.Resolve(weapon, gear, origin, direction, Special(), now);
        Present(weapon.Owner, weapon, visual, now);
        return visual;
    }

    internal bool PresentAccepted(PlayerAgent owner, FireVisual visual, float now)
    {
        if (_disposed) return false;
        if (visual.Sequence == 0 || (_state.LastVisualSequence != 0 &&
            unchecked((int)(visual.Sequence - _state.LastVisualSequence)) <= 0)) return false;
        if (EnergyGears.Match(visual.CategoryId, visual.Mode) is not { } gear ||
            !ValidVisual(visual, gear)) return false;
        _state.LastVisualSequence = visual.Sequence;
        PreparePresentation(gear);
        Present(owner, null, visual, now);
        return true;
    }

    private static bool ValidVisual(FireVisual visual, EnergyGear gear)
    {
        if (!ForgeCore.Vec.Finite(visual.Start) || !ForgeCore.Vec.Finite(visual.Direction) || visual.Direction.sqrMagnitude < 0.25f ||
            visual.Direction.sqrMagnitude > 2.25f || !float.IsFinite(visual.Power) || !float.IsFinite(visual.Damage) || visual.Damage < 0f) return false;
        if (EnergyNativeFire.Delayed(gear.Mode))
            return visual.Segments == 0 && visual.HitMask == 0 && visual.Power >= 0f && visual.Power <= 1f;
        var segments = gear.Mode == EnergyMode.ArcChain ? gear.Numbers.Targets : 1;
        if (visual.Segments < 1 || visual.Segments > segments || (visual.HitMask >> visual.Segments) != 0) return false;
        for (var index = 0; index < visual.Segments; index++) if (!ForgeCore.Vec.Finite(visual.End(index))) return false;
        return gear.Mode != EnergyMode.Flame || visual.Power >= 0f && visual.Power <= gear.Numbers.Shot.Range;
    }

    internal bool StartCharge(PlayerAgent owner, ChargePacket packet, float now)
    {
        if (_disposed || packet.Phase != 1 || packet.Mode is not ((int)EnergyMode.PlasmaBlast or (int)EnergyMode.PlasmaArc) ||
            EnergyGears.Match(packet.CategoryId, packet.Mode) is not { } gear || !ForgeCore.Vec.Finite(packet.Position) || !float.IsFinite(packet.Held) || packet.Held < 0f ||
            (_authoritative && (packet.Position - owner.Position).sqrMagnitude > EnergyProtocol.MaximumMuzzleDistance * EnergyProtocol.MaximumMuzzleDistance)) return false;
        _chargeLocal = GunPose.ToYawFrame(packet.Position - owner.Position, owner.TargetLookDir);
        _chargeUntil = now + EnergyProtocol.ChargeExpirySeconds;
        // The shooter repeating a charge this machine is drawing only keeps it alive.
        if (_charging && _chargeGear == gear && _chargeOwner == owner) return true;
        _effects?.StopCharge();
        _plasma?.Visuals.Hide(EnergyProjectiles.Capacity);
        _chargeOwner = owner;
        _chargeGear = gear;
        PreparePresentation(gear);
        UseLook(gear);
        SetModel(owner, owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>(), gear);
        _chargeStartedAt = now - packet.Held;
        _charging = true; _chargedFull = false;
        return true;
    }

    internal bool EndCharge(ChargePacket packet)
    {
        if (!_charging || _chargeGear == null || packet.Phase != 0 || packet.Mode != (int)_chargeGear.Mode || packet.CategoryId != _chargeGear.CategoryId) return false;
        return StopCharge();
    }

    /// <summary>The host's word on where this shooter's projectile ended (<see cref="FlightEnds"/>).</summary>
    internal void Finish(FlightKind kind, uint sequence, Vector3 at, float now)
    {
        if (_disposed) return;
        if (kind == FlightKind.Plasma) _plasma?.Finish(sequence, at, now);
        else _special?.Finish(kind, sequence, at, now);
    }

    private bool StopCharge()
    {
        if (!_charging) return false;
        _charging = false;
        _effects?.StopCharge();
        _plasma?.Visuals.Hide(EnergyProjectiles.Capacity);
        return true;
    }

    internal void Present(PlayerAgent owner, BulletWeapon? weapon, FireVisual visual, float now)
    {
        if (_disposed || EnergyGears.Match(visual.CategoryId, visual.Mode) is not { } gear || !ForgeCore.Vec.Finite(visual.Start) || !ForgeCore.Vec.Finite(visual.Direction) ||
            visual.Direction.sqrMagnitude < 0.25f || visual.Direction.sqrMagnitude > 2.25f ||
            !float.IsFinite(visual.Power)) return;
        var mode = gear.Mode;
        UseLook(gear);
        weapon ??= owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        SetModel(owner, weapon, gear);
        // The shooter's start is where its gun was on its own screen. What starts at the gun is drawn from where this machine shows that gun;
        // what flies is simulated from the reported start on every machine, so it lands where the host deals it.
        var start = ShownStart(owner, weapon, gear, visual.Start);
        var reported = visual.Start;
        var direction = visual.Direction.normalized;
        _look = Quaternion.LookRotation(direction);
        switch (mode)
        {
            case EnergyMode.Beam:
                if (visual.Segments != 1 || !ForgeCore.Vec.Finite(visual.End(0))) return;
                Effects().Beam(start, visual.End(0), visual.Hit(0), now);
                Visuals().Beam(start, visual.End(0), visual.Hit(0));
                _beamUntil = now + 0.3f;
                HoldStream(owner, weapon, gear, reported);
                _streamEnd = visual.End(0);
                _streamHit = visual.Hit(0);
                break;
            case EnergyMode.ArcChain:
                if (visual.Segments < 1 || visual.Segments > gear.Numbers.Targets) return;
                for (var index = 0; index < visual.Segments; index++)
                {
                    _starts[index] = index == 0 ? start : _ends[index - 1];
                    _ends[index] = visual.End(index);
                    if (!ForgeCore.Vec.Finite(_ends[index])) return;
                    _hits[index] = visual.Hit(index);
                }
                Effects().Chain(_starts.AsSpan(0, visual.Segments), _ends.AsSpan(0, visual.Segments),
                    _hits.AsSpan(0, visual.Segments), now, gear.Numbers.ArcLifetime, gear.Numbers.HopDelay);
                Effects().ArcHold(true);
                _arcHoldUntil = now + 0.8f;
                Visuals().Chain(_starts.AsSpan(0, visual.Segments), _ends.AsSpan(0, visual.Segments),
                    _hits.AsSpan(0, visual.Segments), now, gear.Numbers.HopDelay);
                break;
            case EnergyMode.PlasmaBlast:
            case EnergyMode.PlasmaArc:
                if (visual.Power < 0f || visual.Power > 1f) return;
                StopCharge();
                // The shooter's own gate let this shot go and the host accepted it: when no slot is free here the oldest flight gives way (admitted).
                Plasma().Launch(owner, reported, direction, visual.Power, gear, now, visual.Sequence, FlightEnds.Scale(gear, visual.Damage), admitted: true);
                Effects().Discharge(mode == EnergyMode.PlasmaArc, start);
                break;
            case EnergyMode.Blast:
                Effects().Hide();
                Visuals().Blast(start, direction);
                Special().Visuals.Origin(start);
                Special().Visuals.BlastLaunch();
                Special().LaunchShell(owner, gear, reported, direction, MathF.Min(visual.Damage, gear.Numbers.Shot.Damage * EnergyProtocol.MaximumDamageBoost), now, visual.Sequence);
                break;
            case EnergyMode.Disc:
                Special().LaunchDisc(owner, gear, reported, direction, now, visual.Sequence, FlightEnds.Scale(gear, visual.Damage), admitted: true);
                break;
            case EnergyMode.Flame:
                if (visual.Power < 0f || visual.Power > gear.Numbers.Shot.Range) return;
                Special().Visuals.Flame(start, direction, visual.Power);
                _flameUntil = now + 0.3f;
                HoldStream(owner, weapon, gear, reported);
                _streamDirection = direction;
                _streamReach = visual.Power;
                break;
            case EnergyMode.BlackHole:
                Special().LaunchHole(owner, gear, reported, direction, now, visual.Sequence, FlightEnds.Scale(gear, visual.Damage), admitted: true);
                break;
        }
    }

    private void HoldStream(PlayerAgent owner, BulletWeapon? weapon, EnergyGear gear, Vector3 reported)
    {
        _streamOwner = owner;
        _streamWeapon = weapon;
        _streamGear = gear;
        _streamLocal = GunPose.ToYawFrame(reported - owner.Position, owner.TargetLookDir);
    }

    // Where this machine shows the gun's muzzle: the shooter's third-person gun follows its aim, and the reported point (kept in the
    // shooter's yaw frame) stands in only while that gun is not shown.
    private static Vector3 ShownPoint(PlayerAgent owner, BulletWeapon? weapon, EnergyGear gear, Vector3 local)
        => ShownStart(owner, weapon, gear, owner.Position + GunPose.FromYawFrame(local, owner.TargetLookDir));

    private static Vector3 ShownStart(PlayerAgent owner, BulletWeapon? weapon, EnergyGear gear, Vector3 reported)
    {
        if (weapon == null || weapon.Owner == null || weapon.Owner.Pointer != owner.Pointer ||
            EnergyGearRegistry.DefinitionOf(weapon) != gear || weapon.MuzzleAlign == null) return reported;
        var shown = weapon.MuzzleAlign.position;
        // A gun far from its own shooter is not in the hands (a switch in progress). The report is no measure of that: on a lagging link it runs ahead of the moving shooter this machine shows.
        return ForgeCore.Vec.Finite(shown) && (shown - owner.Position).sqrMagnitude <= MaximumShownOffset * MaximumShownOffset ? shown : reported;
    }

    internal void Tick(float delta, float now)
    {
        if (_disposed) return;
        // No shot or repeat for a while means the shooter stopped or the link did: neither leaves the charge standing.
        if (_charging && (_chargeOwner == null || !_chargeOwner.Alive || now >= _chargeUntil)) StopCharge();
        if (_streamOwner != null && _streamOwner.Alive && now <= _beamUntil)
        {
            var from = ShownPoint(_streamOwner, _streamWeapon, _streamGear!, _streamLocal);
            Effects().Beam(from, _streamEnd, _streamHit, now);
            Visuals().Beam(from, _streamEnd, _streamHit);
        }
        if (_streamOwner != null && _streamOwner.Alive && now <= _flameUntil)
            Special().Visuals.Flame(ShownPoint(_streamOwner, _streamWeapon, _streamGear!, _streamLocal), _streamDirection, _streamReach);
        if (_charging && _chargeOwner != null)
        {
            var position = ShownPoint(_chargeOwner, _chargeOwner.Inventory?.WieldedItem?.TryCast<BulletWeapon>(), _chargeGear!, _chargeLocal);
            var power = (float)Math.Clamp((now - _chargeStartedAt - _chargeGear!.Numbers.MinimumCharge) /
                (_chargeGear!.Numbers.FullCharge - _chargeGear!.Numbers.MinimumCharge), 0d, 1d);
            Effects().Charge(power, position);
            var orbs = Plasma().Visuals;
            orbs.ChargingOrb(EnergyProjectiles.Capacity, position,
                PlasmaPayload.FromCharge(power, _chargeGear!.Numbers).BodyRadius, _chargeGear!.Mode == EnergyMode.PlasmaArc, firstPerson: false, sight: null);
            if (power >= 1f && !_chargedFull) { _chargedFull = true; orbs.Charged(EnergyProjectiles.Capacity); }
        }
        _effects?.Tick(now);
        _visuals?.Tick(now);
        _plasma?.Tick(delta, now);
        _special?.Tick(delta, now, _look);
        if (_beamUntil != 0f && now > _beamUntil)
        { _beamUntil = 0f; _effects?.Hide(); _visuals?.Hide(); }
        if (_flameUntil != 0f && now > _flameUntil)
        { _flameUntil = 0f; _special?.Visuals.StopFlame(); }
        if (_arcHoldUntil != 0f && now > _arcHoldUntil)
        { _arcHoldUntil = 0f; _effects?.ArcHold(false); }
    }

    private EnergyEffects Effects() => _effects ??= new EnergyEffects { Look = _gearLook };
    private EnergyVisuals Visuals() => _visuals ??= new EnergyVisuals { Look = _gearLook };
    private EnergyProjectiles Plasma()
    {
        if (_plasma != null) return _plasma;
        _plasma = new EnergyProjectiles(_authoritative);
        _plasma.Visuals.Look = _gearLook;
        return _plasma;
    }
    private SpecialWeapons Special()
    {
        if (_special != null) return _special;
        _special = new SpecialWeapons(_authoritative);
        _special.Visuals.Look = _gearLook;
        return _special;
    }

    // What this player's gun plays and draws is that gun's own (its definition's Look). A beam or chain held under the other look is let go first.
    private void UseLook(EnergyGear gear)
    {
        var look = EnergyLook.Of(gear);
        if (look == _gearLook) return;
        _effects?.Hide();
        _visuals?.Hide();
        _gearLook = look;
        if (_effects != null) _effects.Look = look;
        if (_visuals != null) _visuals.Look = look;
        if (_plasma != null) _plasma.Visuals.Look = look;
        if (_special != null) _special.Visuals.Look = look;
    }

    private void PreparePresentation(EnergyGear gear)
    {
        if (!PresentationPreload.Ready) throw new InvalidOperationException("Remote presentation is not ready.");
        var mode = gear.Mode;
        var bit = 1 << (int)mode;
        if ((_preparedModes & bit) != 0) return;
        Effects();
        if (mode is EnergyMode.Beam or EnergyMode.ArcChain or EnergyMode.Blast) Visuals();
        if (mode is EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc) Plasma();
        if (mode is EnergyMode.Disc or EnergyMode.Flame or EnergyMode.BlackHole or EnergyMode.Blast) Special();
        _preparedModes |= bit;
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _charging = false;
        _effects?.Dispose(); _visuals?.Dispose(); _plasma?.Dispose(); _special?.Dispose();
    }

    internal void StopAfterFault()
    {
        Faulted = true;
        Dispose();
    }
}
