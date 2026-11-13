using ForgeVisual;

namespace ForgeOrdnance.Arsenal;

/// <summary>The strike moments whose effect comes from the effect library (the bundle <c>forge-arsenal-fx.bundle</c> and its slot table
/// <c>fx-slots.forge</c> in the plugin's <c>Arsenal</c> folder, made by <c>Tools/Vfx/FxLibrary/fx_slots.py</c> from the owner's selection). Which prefab plays
/// for a slot is data in that table, not code here; a slot the table does not list, or an install without the files, logs its error and draws
/// nothing. Playing is ForgeVisual's <see cref="Fx"/>. The handles are null until <see cref="Load"/>, which the plugin runs when the first
/// level has been built: every reader takes its handle at the moment it plays.</summary>
internal static class ArsenalFxSlots
{
    internal static FxHandle? BigBlast, SmallBlast, GasBurst, StasisPulse, StrafeHit, ShellTrail, HunterTrail, GasCloud, StasisField, FireZone, BeamBody, SlowAura, Floodlight, ScannerSweep,
        FoamSpray, MineBlast, MineRise, FoamRise, MineArm, MineScatter, RocketMuzzle, RocketBackblast, RocketBlast, GrenadeMuzzle, AmrMuzzle, MgMuzzle,
        Cluster, Incendiary, BeamCharge, BeamBurn, StrikeReturn;

    /// <summary>Plays a slot's effect found at load, at the slot's own size; a slot the table lacked (reported then) is null and draws nothing.</summary>
    internal static void Play(FxHandle? fx, UnityEngine.Vector3 at, UnityEngine.Vector3 direction)
    {
        if (fx != null) Fx.Play(fx, at, direction);
    }

    /// <summary>The same, <paramref name="size"/> metres across.</summary>
    internal static void Play(FxHandle? fx, UnityEngine.Vector3 at, UnityEngine.Vector3 direction, float size)
    {
        if (fx != null) Fx.Play(fx, at, direction, size, UnityEngine.Color.white);
    }

    /// <summary>The library's effect for an area of this kind (gas cloud, stasis field, burning ground), or null.</summary>
    internal static FxHandle? AreaOf(StrikeKind kind) => kind switch { StrikeKind.Gas => GasCloud, StrikeKind.Stasis => StasisField, _ => FireZone };
    /// <summary>The library's effect standing over this area, sized to the whole radius. A slot with a spacing lays copies over its floor (none behind a wall, the same wall that stops its damage),
    /// gas rolling out from the canister and fire catching outward; a slot without one (gas and fire now) is one copy at the area's diameter, which a wall does not cut.</summary>
    internal static FxField? FieldOf(StrikeKind kind, UnityEngine.Vector3 centre, float radius)
    {
        if (AreaOf(kind) is not { } fx) return null;
        // A point Fx.Field sees moved off the floor is left out.
        var field = Fx.Field(fx, centre, Vec.Up, radius,
            point => ForgeCore.Level.HasSight(centre, point + Vec.Up) ? Strikes.Ground(point) : point + Vec.Up * 100f);
        field.GrowSeconds = kind switch { StrikeKind.Gas => 1.5f, StrikeKind.Incendiary => 0.9f, _ => 0f };
        return field;
    }
    /// <summary>One held beam copy: only one strike beam draws its line at a time per route, and each keeps this key.</summary>
    internal static void LineBeam(int key, UnityEngine.Vector3 from, UnityEngine.Vector3 to) { if (BeamBody != null) Fx.Line(BeamBody, key, from, to, true, UnityEngine.Color.white); }

    internal static void Load()
    {
        var set = FxSlots.Load(Plugin.Id, "ArsenalFx", "Arsenal/forge-arsenal-fx.bundle", "Arsenal/fx-slots.forge");
        ShellTrail = set.Find("strike.shell");
        HunterTrail = set.Find("strike.hunter");
        GasCloud = set.Find("strike.gas.cloud"); StasisField = set.Find("strike.stasis.field"); FireZone = set.Find("strike.fire");
        BeamBody = set.Find("strike.beam"); SlowAura = set.Find("strike.stasis.slow");
        Floodlight = set.Find("light.tower"); ScannerSweep = set.Find("scan.sweep");
        FoamSpray = set.Find("foam.spray"); MineBlast = set.Find("mine.blast"); MineRise = set.Find("mine.rise"); FoamRise = set.Find("foam.rise");
        MineArm = set.Find("mine.arm"); MineScatter = set.Find("mine.scatter");
        RocketMuzzle = set.Find("rocket.muzzle"); RocketBackblast = set.Find("rocket.backblast"); RocketBlast = set.Find("rocket.blast");
        GrenadeMuzzle = set.Find("heavy.gl.muzzle");
        AmrMuzzle = set.Find("heavy.amr.muzzle"); MgMuzzle = set.Find("heavy.mg.muzzle");
        Cluster = set.Find("strike.cluster"); Incendiary = set.Find("strike.incendiary"); BeamCharge = set.Find("strike.beam.charge");
        BeamBurn = set.Find("strike.beam.burn"); StrikeReturn = set.Find("strike.return");
        // A turret's muzzle flash: the carrier plays it for the sentries wearing the look, on each shot.
        ArsenalTurrets.BurstShow.Muzzle = set.Find("turret.rapid.muzzle");
        ArsenalTurrets.SniperShow.Muzzle = set.Find("turret.sniper.muzzle");
        ArsenalTurrets.ShotgunShow.Muzzle = set.Find("turret.shotgun.muzzle");
        BigBlast = set.Find("strike.big");
        SmallBlast = set.Find("strike.small");
        GasBurst = set.Find("strike.gas.burst");
        StasisPulse = set.Find("strike.stasis.pulse");
        StrafeHit = set.Find("strike.strafe");
    }
}
