using System;
using System.Collections;
using ForgeWeaponEnergyLabExperimental.Presentation;
using Gear;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

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
    private Vector3 _chargeOffset;
    private float _chargeStartedAt;
    private bool _charging;
    private bool _disposed;
    internal bool Faulted { get; private set; }
    private float _beamUntil, _flameUntil, _arcHoldUntil;
    private Quaternion _look = Quaternion.identity;
    private const float MaximumShownOffset = 3f;

    internal RemoteWeapon(bool authoritative, RemotePeerState? state = null)
    {
        _authoritative = authoritative;
        _state = state ?? new RemotePeerState();
    }

    internal void SetModel(PlayerAgent owner, BulletWeapon? weapon, EnergyGear? gear)
    {
        if (_disposed || gear == null || !owner.Alive || weapon == null || weapon.Owner == null ||
            weapon.Owner.Pointer != owner.Pointer || EnergyGearRegistry.DefinitionOf(weapon) != gear ||
            weapon.GearPartHolder == null) return;
        WeaponModels.Ensure(weapon.GearPartHolder);
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
            _ => true
        };
    }

    // Native Fire already spent the shooter's round. The host accepts a launch, never bills or replays Fire.
    internal bool TryLaunch(PlayerAgent owner, BulletWeapon? weapon, FireCommand command, float now, out FireVisual visual)
    {
        visual = default;
        if (!_authoritative || _disposed || EnergyGears.Match(command.CategoryId, command.Mode) is not { } gear ||
            !EnergyNativeFire.Delayed(gear.Mode) || !owner.Alive || command.Dimension != (int)owner.DimensionIndex ||
            !Finite(owner.Position) ||
            !Finite(command.Start) || !Finite(command.Direction) || command.Direction.sqrMagnitude < 0.25f ||
            command.Direction.sqrMagnitude > 2.25f || !float.IsFinite(command.Power) || command.Power < 0f || command.Power > 1f ||
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
        if (_authoritative && gear.Mode == EnergyMode.Blast) EnergyBlastForces.AcceptVisual(visual);
        PreparePresentation(gear);
        Present(owner, null, visual, now);
        return true;
    }

    private static bool ValidVisual(FireVisual visual, EnergyGear gear)
    {
        if (!Finite(visual.Start) || !Finite(visual.Direction) || visual.Direction.sqrMagnitude < 0.25f ||
            visual.Direction.sqrMagnitude > 2.25f || !float.IsFinite(visual.Power)) return false;
        if (EnergyNativeFire.Delayed(gear.Mode))
            return visual.Segments == 0 && visual.HitMask == 0 && visual.Power >= 0f && visual.Power <= 1f;
        var segments = gear.Mode == EnergyMode.ArcChain ? gear.Numbers.Targets : 1;
        if (visual.Segments < 1 || visual.Segments > segments || (visual.HitMask >> visual.Segments) != 0) return false;
        for (var index = 0; index < visual.Segments; index++) if (!Finite(visual.End(index))) return false;
        return gear.Mode != EnergyMode.Flame || visual.Power >= 0f && visual.Power <= gear.Numbers.Shot.Range;
    }

    internal bool StartCharge(PlayerAgent owner, ChargePacket packet, float now)
    {
        if (_disposed || packet.Phase != 1 || packet.Mode is not ((int)EnergyMode.PlasmaBlast or (int)EnergyMode.PlasmaArc) ||
            EnergyGears.Match(packet.CategoryId, packet.Mode) is not { } gear || !Finite(packet.Position) || (_authoritative && (packet.Position - owner.Position).sqrMagnitude > 12.25f)) return false;
        _effects?.StopCharge();
        _plasma?.Visuals.Hide(EnergyProjectiles.Capacity);
        _chargeOwner = owner;
        _chargeGear = gear;
        PreparePresentation(gear);
        SetModel(owner, owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>(), gear);
        _chargeOffset = packet.Position - owner.Position;
        _chargeStartedAt = now;
        _charging = true;
        return true;
    }

    internal bool EndCharge(ChargePacket packet)
    {
        if (!_charging || _chargeGear == null || packet.Phase != 0 || packet.Mode != (int)_chargeGear.Mode || packet.CategoryId != _chargeGear.CategoryId) return false;
        return StopCharge();
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
        if (_disposed || EnergyGears.Match(visual.CategoryId, visual.Mode) is not { } gear || !Finite(visual.Start) || !Finite(visual.Direction) ||
            visual.Direction.sqrMagnitude < 0.25f || visual.Direction.sqrMagnitude > 2.25f ||
            !float.IsFinite(visual.Power)) return;
        var mode = gear.Mode;
        weapon ??= owner.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        SetModel(owner, weapon, gear);
        // The shooter's start is where its gun was on its own screen. Draw from where this machine shows that gun;
        // the host keeps simulating and damaging from the reported start.
        var start = ShownStart(owner, weapon, gear, visual.Start);
        var reported = visual.Start;
        var direction = visual.Direction.normalized;
        _look = Quaternion.LookRotation(direction);
        switch (mode)
        {
            case EnergyMode.Beam:
                if (visual.Segments != 1 || !Finite(visual.End(0))) return;
                Effects().Beam(start, visual.End(0), visual.Hit(0), now);
                Visuals().Beam(start, visual.End(0), visual.Hit(0), now);
                _beamUntil = now + 0.3f;
                break;
            case EnergyMode.ArcChain:
                if (visual.Segments < 1 || visual.Segments > gear.Numbers.Targets) return;
                for (var index = 0; index < visual.Segments; index++)
                {
                    _starts[index] = index == 0 ? start : _ends[index - 1];
                    _ends[index] = visual.End(index);
                    if (!Finite(_ends[index])) return;
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
                Plasma().Launch(owner, _authoritative ? reported : start, direction, visual.Power, gear, now);
                Effects().Discharge(mode == EnergyMode.PlasmaArc, start);
                break;
            case EnergyMode.Blast:
                if (!Finite(visual.End(0))) return;
                if (visual.Hit(0))
                {
                    var point = visual.End(0) - direction * 0.04f;
                    Special().Visuals.GrenadeExplosion(point, gear.Numbers.BlastRadius, now);
                    if (_authoritative && owner.Owner != null && !owner.Owner.IsBot)
                    {
                        _discharge.ApplyRemoteForce(gear, point);
                        WeaponNoise.Explosion(owner, gear, point);
                    }
                }
                Effects().Hide();
                Visuals().Blast(start, now);
                Special().Visuals.Origin(start);
                Special().Visuals.BlastLaunch();
                break;
            case EnergyMode.Disc:
                Special().LaunchDisc(owner, gear, _authoritative ? reported : start, direction, now);
                break;
            case EnergyMode.Flame:
                if (visual.Power < 0f || visual.Power > gear.Numbers.Shot.Range) return;
                Special().Visuals.Flame(start, direction, visual.Power, now);
                _flameUntil = now + 0.3f;
                break;
            case EnergyMode.BlackHole:
                Special().LaunchHole(owner, gear, _authoritative ? reported : start, direction, now);
                break;
        }
    }

    private static Vector3 ShownStart(PlayerAgent owner, BulletWeapon? weapon, EnergyGear gear, Vector3 reported)
    {
        if (weapon == null || weapon.Owner == null || weapon.Owner.Pointer != owner.Pointer ||
            EnergyGearRegistry.DefinitionOf(weapon) != gear || weapon.MuzzleAlign == null) return reported;
        var shown = weapon.MuzzleAlign.position;
        // A gun shown far from the report is mid-switch or teleporting; keep the reported start then.
        return Finite(shown) && (shown - reported).sqrMagnitude <= MaximumShownOffset * MaximumShownOffset ? shown : reported;
    }

    internal void Tick(float delta, float now)
    {
        if (_disposed) return;
        if (_charging && (_chargeOwner == null || !_chargeOwner.Alive)) StopCharge();
        if (_charging && _chargeOwner != null)
        {
            var position = _chargeOwner.Position + _chargeOffset;
            var power = (float)Math.Clamp((now - _chargeStartedAt - _chargeGear!.Numbers.MinimumCharge) /
                (_chargeGear!.Numbers.FullCharge - _chargeGear!.Numbers.MinimumCharge), 0d, 1d);
            Effects().Charge(power, position);
            Plasma().Visuals.ChargingOrb(EnergyProjectiles.Capacity, position,
                PlasmaPayload.FromCharge(power, _chargeGear!.Numbers).BodyRadius, now, _chargeGear!.Mode == EnergyMode.PlasmaArc);
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

    private EnergyEffects Effects() => _effects ??= new EnergyEffects();
    private EnergyVisuals Visuals() => _visuals ??= new EnergyVisuals();
    private EnergyProjectiles Plasma() => _plasma ??= new EnergyProjectiles(_authoritative);
    private SpecialWeapons Special() => _special ??= new SpecialWeapons(_authoritative);

    private void PreparePresentation(EnergyGear gear)
    {
        if (!PresentationPreload.Ready) throw new InvalidOperationException("Remote presentation is not ready.");
        var mode = gear.Mode;
        var bit = 1 << (int)mode;
        if ((_preparedModes & bit) != 0) return;
        Effects();
        if (mode is EnergyMode.Beam or EnergyMode.ArcChain or EnergyMode.Blast) Prepare(Visuals().Prepare());
        if (mode is EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc) Prepare(Plasma().Visuals.Prepare());
        if (mode is EnergyMode.Disc or EnergyMode.Flame or EnergyMode.BlackHole or EnergyMode.Blast) Prepare(Special().Visuals.Prepare());
        _preparedModes |= bit;
    }

    private static void Prepare(IEnumerator work)
    {
        using var cleanup = work as IDisposable;
        while (work.MoveNext()) { }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

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
