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
/// in <c>arsenal.json</c>) on every peer; and the turrets' sounds, which only sentries wearing an Arsenal look get: the deploy sound as the
/// shell's petals open, the idle hum while the sentry stands, the shutdown sound when its time runs out.
/// </summary>
internal static class ArsenalTurrets
{
    internal const string Burst = "Arsenal:turret-burst";
    internal const string Sniper = "Arsenal:turret-sniper";
    internal const string Shotgun = "Arsenal:turret-shotgun";
    private const string Prefix = Pack + ":";
    private const string Pack = ArsenalArt.Pack;

    /// <summary>World scale of each look about its floor point (the look is built at this size whatever scale the game's sentry object has,
    /// which is scaled 1.5). ForgeOrdnance puts the game's muzzle point, where shots start, on the look's muzzle and aims it at the target
    /// from there, so the muzzle height does not change where shots land. The burst and sniper looks at 1.24 and 1.14 carry it 0.91 and
    /// 1.13 m up; the shotgun look is 1.05, the game's own sentry size (muzzle 0.90 m up, level with the burst look's).</summary>
    private static readonly (string Id, string Model, float Scale)[] Looks =
    {
        (Burst, "turret-burst", 1.24f), (Sniper, "turret-sniper", 1.14f), (Shotgun, "turret-shotgun", 1.05f),
    };

    private static readonly Dictionary<IntPtr, AudioSustain> Humming = new();

    /// <summary>Plugin load, before the definition files are read: registers the looks and starts listening for the turrets' moments.</summary>
    internal static void Register(Func<string, ModelHandle> model)
    {
        foreach (var (id, name, scale) in Looks)
            OrdnanceTurrets.RegisterLook(id, model(name), new TurretLookSpec { WorldScale = scale });
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

    internal static void Clear() => Humming.Clear();
}
