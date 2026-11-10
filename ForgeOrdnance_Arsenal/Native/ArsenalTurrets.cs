using System;
using System.Collections.Generic;
using ForgeAudio;
using ForgeOrdnance;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>
/// Arsenal's turrets: the three sentry looks (<c>turret-burst</c>, <c>turret-sniper</c>, <c>turret-shotgun</c> of the <c>Arsenal</c> pack),
/// registered with ForgeOrdnance, which dresses every sentry whose <c>Gear</c> call-in names one (<c>"Look": "Arsenal:turret-burst"</c>
/// in <c>arsenal.forge</c>) on every peer; and the turrets' sounds, which only sentries wearing an Arsenal look get: the deploy sound as the
/// head starts to rise, the idle hum while the sentry stands, the shutdown sound when its time runs out.
/// </summary>
internal static class ArsenalTurrets
{
    internal const string Burst = "Arsenal:turret-burst";
    internal const string Sniper = "Arsenal:turret-sniper";
    internal const string Shotgun = "Arsenal:turret-shotgun";
    private const string Prefix = Pack + ":";
    private const string Pack = ArsenalArt.Pack;

    /// <summary>World scale of each look about its floor point (the look is built at this size whatever scale the game's sentry object has,
    /// which is scaled 1.5): a little above the game's own sentry (muzzle 0.90 m up), about 1 m to the muzzle. ForgeOrdnance puts the game's
    /// muzzle point, where shots start, on the look's muzzle and aims it at the target from there, so the muzzle height does not change where
    /// shots land. <c>BaseScale</c> sizes the drop base so the look fits its opening barrel up; <c>RiseHeight</c> is the look's height barrel up,
    /// how deep it hides in the base. The other burrow numbers are ForgeOrdnance's defaults for a turret.</summary>
    private static readonly (string Id, string Model, float Scale, float BaseScale, float RiseHeight, TurretMotionSpec Motion)[] Looks =
    {
        // The rotary gun: little kick, its barrels spin. A steady wide scan.
        (Burst, "turret-burst", 1.5f, 3.1f, 2f, new TurretMotionSpec { ScanDegrees = 28f, ScanSeconds = 7f, Recoil = 0.012f, Kick = 0.3f, MaxRecoil = 0.03f, RecoilReturn = 18f, BarrelSpin = 1500f }),
        // The rifle: a hard single kick, the long barrel slides, a slow narrow watch.
        (Sniper, "turret-sniper", 1.75f, 2.55f, 2.2f, new TurretMotionSpec { ScanDegrees = 14f, ScanSeconds = 10f, Recoil = 0.05f, Kick = 3.5f, MaxRecoil = 0.06f, RecoilReturn = 7f, BarrelRecoil = 0.07f }),
        // The shotgun: the biggest tip, close work, a quick wide look around.
        (Shotgun, "turret-shotgun", 1.5f, 2.35f, 1.94f, new TurretMotionSpec { ScanDegrees = 38f, ScanSeconds = 5f, Recoil = 0.045f, Kick = 5f, MaxRecoil = 0.07f, RecoilReturn = 9f, BarrelRecoil = 0.025f }),
    };

    private static readonly Dictionary<IntPtr, AudioSustain> Humming = new();

    /// <summary>Plugin load, before the definition files are read: registers the looks and starts listening for the turrets' moments.</summary>
    internal static void Register(Func<string, ModelHandle> model)
    {
        foreach (var (id, name, scale, baseScale, riseHeight, motion) in Looks)
            OrdnanceTurrets.RegisterLook(id, model(name), new TurretLookSpec { WorldScale = scale, Burrow = new BurrowSpec { BaseScale = baseScale, RiseHeight = riseHeight, TurnSeconds = 0.8f }, Motion = motion });
        OrdnanceTurrets.Deployed += OnDeployed;
        OrdnanceTurrets.Online += OnOnline;
        OrdnanceTurrets.Expiring += OnExpiring;
    }

    private static bool Ours(string lookId) => lookId.StartsWith(Prefix, StringComparison.Ordinal);

    private static void OnDeployed(string lookId, Transform shell)
    {
        if (Ours(lookId)) ArsenalAudio.PlayOn(ArsenalAudio.Cue.TurretDeploy, shell.gameObject);
    }

    /// <summary>The idle hum follows the sentry while it stands; a second call for the same sentry changes nothing.</summary>
    private static void OnOnline(string lookId, SentryGunInstance sentry, Transform at)
    {
        if (!Ours(lookId)) return;
        if (Humming.TryGetValue(sentry.Pointer, out var known) && known.IsActive) return;
        var sustain = ArsenalAudio.Sounds?.Sustain(ArsenalAudio.Cue.TurretIdle, sentry.gameObject, null, () => OrdnanceTurrets.TryLook(sentry, out _));
        if (sustain is not { IsActive: true }) return;
        foreach (var pointer in new List<IntPtr>(Humming.Keys))
            if (!Humming[pointer].IsActive) Humming.Remove(pointer);
        Humming[sentry.Pointer] = sustain;
    }

    private static void OnExpiring(string lookId, SentryGunInstance sentry, Vector3 at)
    {
        if (Ours(lookId)) ArsenalAudio.PlayAt(ArsenalAudio.Cue.TurretExpire, at);
    }

    /// <summary>One shot of a sentry of this package's looks: the library's muzzle flash of that turret, at the muzzle, along the barrel.</summary>
    internal static void Fired(SentryGunInstance_Firing_Bullets firing)
    {
        var muzzle = firing.MuzzleAlign;
        var sentry = firing.m_core?.TryCast<SentryGunInstance>();
        if (muzzle == null || muzzle.WasCollected || sentry == null || !OrdnanceTurrets.TryLook(sentry, out var lookId) || !Ours(lookId)) return;
        ArsenalFxSlots.Play(lookId == Sniper ? ArsenalFxSlots.TurretSniper : lookId == Shotgun ? ArsenalFxSlots.TurretShotgun : ArsenalFxSlots.TurretRapid, muzzle.position, muzzle.forward);
    }

    internal static void Clear() => Humming.Clear();
}
