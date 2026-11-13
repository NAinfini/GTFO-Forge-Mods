using System;
using ForgeVisual;

namespace ForgeOrdnance.Arsenal;

/// <summary>
/// Arsenal's turrets: the three sentry looks (<c>turret-burst</c>, <c>turret-sniper</c>, <c>turret-shotgun</c> of the <c>Arsenal</c> pack),
/// registered with the carrier, which dresses every sentry whose <c>Gear</c> call-in names one (<c>"Look": "Arsenal:turret-burst"</c>
/// in <c>arsenal.forge</c>) on every peer and plays what each look brings (<see cref="TurretLookShow"/>): the deploy sound as the head
/// starts to rise, the idle hum while the sentry stands, the shutdown sound when its time runs out, and the muzzle flash of every shot.
/// </summary>
internal static class ArsenalTurrets
{
    internal const string Burst = "Arsenal:turret-burst";
    internal const string Sniper = "Arsenal:turret-sniper";
    internal const string Shotgun = "Arsenal:turret-shotgun";

    /// <summary>What the carrier plays for each look: the same three sounds, and the look's own muzzle flash once the effect library is
    /// read (<see cref="ArsenalFxSlots.Load"/>).</summary>
    internal static readonly TurretLookShow BurstShow = Show(), SniperShow = Show(), ShotgunShow = Show();

    private static TurretLookShow Show() => new()
    {
        Sounds = static () => PartSounds.Arsenal,
        Deploy = ArsenalAudio.Cue.TurretDeploy,
        Idle = ArsenalAudio.Cue.TurretIdle,
        Expire = ArsenalAudio.Cue.TurretExpire,
    };

    /// <summary>World scale of each look about its floor point (the look is built at this size whatever scale the game's sentry object has,
    /// which is scaled 1.5): a little above the game's own sentry (muzzle 0.90 m up), about 1 m to the muzzle. The carrier puts the game's
    /// muzzle point, where shots start, on the look's muzzle and aims it at the target from there, so the muzzle height does not change where
    /// shots land. <c>BaseScale</c> sizes the drop base so the look fits its opening barrel up; <c>RiseHeight</c> is the look's height barrel up,
    /// how deep it hides in the base. The other burrow numbers are the carrier's defaults for a turret.</summary>
    private static readonly (string Id, string Model, float Scale, float BaseScale, float RiseHeight, TurretMotionSpec Motion, TurretLookShow Show)[] Looks =
    {
        // The rotary gun: little kick, its barrels spin. A steady wide scan.
        (Burst, "turret-burst", 1.5f, 3.1f, 2f, new TurretMotionSpec { ScanDegrees = 28f, ScanSeconds = 7f, Recoil = 0.012f, Kick = 0.3f, MaxRecoil = 0.03f, RecoilReturn = 18f, BarrelSpin = 1500f }, BurstShow),
        // The rifle: a hard single kick, the long barrel slides, a slow narrow watch.
        (Sniper, "turret-sniper", 1.75f, 2.55f, 2.2f, new TurretMotionSpec { ScanDegrees = 14f, ScanSeconds = 10f, Recoil = 0.05f, Kick = 3.5f, MaxRecoil = 0.06f, RecoilReturn = 7f, BarrelRecoil = 0.07f }, SniperShow),
        // The shotgun: the biggest tip, close work, a quick wide look around.
        (Shotgun, "turret-shotgun", 1.5f, 2.35f, 1.94f, new TurretMotionSpec { ScanDegrees = 38f, ScanSeconds = 5f, Recoil = 0.045f, Kick = 5f, MaxRecoil = 0.07f, RecoilReturn = 9f, BarrelRecoil = 0.025f }, ShotgunShow),
    };

    /// <summary>Plugin load, before the definition files are read: registers the looks, each with what the carrier plays for it.</summary>
    internal static void Register(Func<string, ModelHandle> model)
    {
        foreach (var (id, name, scale, baseScale, riseHeight, motion, show) in Looks)
            OrdnanceTurrets.RegisterLook(id, model(name), new TurretLookSpec { WorldScale = scale, Burrow = new BurrowSpec { BaseScale = baseScale, RiseHeight = riseHeight, TurnSeconds = 0.8f }, Motion = motion }, show);
    }
}
