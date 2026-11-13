using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The turret surface other packages use. A package registers turret looks (<see cref="RegisterLook(string, ModelHandle, TurretLookSpec)"/>) and its <c>Gear</c>
/// call-ins name one with <c>"Look": "Pack:item"</c>; the platform then dresses every sentry that call-in stands, on every peer,
/// in that model (the game's sentry keeps aiming, firing and hitting; only its body meshes are hidden). The events let the
/// package add its own sounds and effects at the moments of a turret's life. Register looks from the plugin's <c>Load</c>
/// (definition files are read after it).
/// </summary>
public static class OrdnanceTurrets
{
    private static readonly TurretLookBook Book = new();
    private static readonly Dictionary<string, ModelHandle> LookModels = new(StringComparer.Ordinal);

    /// <summary>Every peer: a turret shell has thrown its lids and the turret begins to rise out of it (the deploy moment). The first argument is the look id of the
    /// call-in (empty when it names none), then the shell. Not raised for a shell that is replayed to a late joiner before this moment.</summary>
    public static event Action<string, Transform>? Deployed;

    /// <summary>Every peer: an Ordnance sentry exists (the game's own spawn hook ran). The first argument is its look id (empty for the
    /// game's own look). Use <see cref="TryLook"/> as the keep-alive test of a sound that follows it.</summary>
    public static event Action<string, SentryGunInstance, Transform>? Online;

    /// <summary>Every peer: a timed sentry's lifetime ran out (the host removes it right after). Look id, the sentry, its position.</summary>
    public static event Action<string, SentryGunInstance, Vector3>? Expiring;

    /// <summary>Registers a turret look. <paramref name="lookId"/> is the model's <c>Pack:item</c> name in ForgeVisual; its prefab must
    /// hold the four parts <paramref name="spec"/> names, and <see cref="TurretLookSpec.Burrow"/> says how it rides up on its base and
    /// turns its barrel down before the game's sentry takes over. Returns false, with one error in the log, when the id is not written
    /// <c>Pack:item</c>, is taken, the spec is unusable, or the model lacks a part; definitions naming a refused look are skipped
    /// with their own error. Not after definitions are loaded.</summary>
    public static bool RegisterLook(string lookId, ModelHandle model, TurretLookSpec spec)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        string? why = null;
        if (OrdnanceApi.DefinitionsLoaded) why = "definitions are already loaded; register from your plugin's Load";
        else if (spec == null) why = "no spec was given";
        else
        {
            var root = model.Prefab.transform;
            foreach (var part in new[] { spec.Base, spec.Yaw, spec.Head, spec.Muzzle })
                if (!string.IsNullOrEmpty(part) && root.Find(part) == null) { why = "the model " + model.Name + " has no part '" + part + "'"; break; }
            why ??= Book.Register(lookId, spec);
        }
        if (why != null)
        {
            Plugin.Logger.ErrorOnce("Turret look '" + lookId + "' refused: " + why + ".");
            return false;
        }
        LookModels.Add(lookId, model);
        return true;
    }

    /// <summary>A look of this mod's own parts, with its sounds and muzzle effect (<see cref="TurretLookShow"/>), which the carrier
    /// plays itself at the moments the events below report.</summary>
    internal static bool RegisterLook(string lookId, ModelHandle model, TurretLookSpec spec, TurretLookShow show)
    {
        if (!RegisterLook(lookId, model, spec)) return false;
        Shows.Add(lookId, show);
        return true;
    }

    private static readonly Dictionary<string, TurretLookShow> Shows = new(StringComparer.Ordinal);
    /// <summary>The idle hums that follow the standing sentries, by sentry: a second <see cref="RaiseOnline"/> for the same sentry starts no second hum.</summary>
    private static readonly Dictionary<IntPtr, ForgeAudio.AudioSustain> Humming = new();

    internal static TurretLookShow? ShowOf(string lookId) => Shows.TryGetValue(lookId, out var show) ? show : null;

    /// <summary>Level exit: the hums ended with their sentries.</summary>
    internal static void Clear() => Humming.Clear();

    /// <summary>The look id of an Ordnance sentry (empty when it has the game's own look). False for a sentry Ordnance did not stand.</summary>
    public static bool TryLook(SentryGunInstance sentry, out string lookId)
    {
        lookId = "";
        if (!GearPayload.TryIdentity(sentry, out _, out var key)) return false;
        if (key != 0 && Book.TryGetByKey(key, out var id)) lookId = id;
        return true;
    }

    internal static bool IsRegistered(string lookId) => Book.IsRegistered(lookId);

    internal static uint KeyOf(string lookId) => lookId.Length == 0 ? 0u : TurretLookBook.KeyOf(lookId);

    internal static string IdOf(uint key) => key != 0 && Book.TryGetByKey(key, out var id) ? id : "";

    internal static bool TryGet(string lookId, out ModelHandle model, out TurretLookSpec spec)
    {
        spec = null!;
        model = null!;
        return LookModels.TryGetValue(lookId, out model!) && Book.TryGet(lookId, out spec!);
    }

    internal static void RaiseDeployed(string lookId, Transform shell)
    {
        try
        {
            if (ShowOf(lookId) is { } show) show.Sounds()?.Play(show.Deploy, shell.gameObject);
            Deployed?.Invoke(lookId, shell);
        }
        catch (Exception error) { Failed("Deployed", error); }
    }

    internal static void RaiseOnline(string lookId, SentryGunInstance sentry)
    {
        try
        {
            if (ShowOf(lookId) is { } show) Hum(show, sentry);
            Online?.Invoke(lookId, sentry, sentry.transform);
        }
        catch (Exception error) { Failed("Online", error); }
    }

    internal static void RaiseExpiring(string lookId, SentryGunInstance sentry, Vector3 at)
    {
        try
        {
            if (ShowOf(lookId) is { } show) show.Sounds()?.Play(show.Expire, at);
            Expiring?.Invoke(lookId, sentry, at);
        }
        catch (Exception error) { Failed("Expiring", error); }
    }

    /// <summary>The idle hum follows the sentry while it stands.</summary>
    private static void Hum(TurretLookShow show, SentryGunInstance sentry)
    {
        if (Humming.TryGetValue(sentry.Pointer, out var known) && known.IsActive) return;
        var sustain = show.Sounds()?.Sustain(show.Idle, sentry.gameObject, null, () => TryLook(sentry, out _));
        if (sustain is not { IsActive: true }) return;
        // The hums that ended with their sentries are dropped here (a dictionary may lose entries while it is walked).
        foreach (var (pointer, hum) in Humming)
            if (!hum.IsActive) Humming.Remove(pointer);
        Humming[sentry.Pointer] = sustain;
    }

    /// <summary>A failure at one of a turret's moments (a handler's, or the look's own sound) is logged and never reaches the sentry's own work.</summary>
    private static void Failed(string what, Exception error) => Plugin.Logger.ErrorOnce("A turret " + what + " handler failed: " + error);
}

/// <summary>What one of this mod's own parts gives a look it registers, and the carrier plays at the turret's moments: the sounds of
/// its life, from the part's own sound set, and the effect at the muzzle on every shot. Another package's look gets the same moments
/// from the events of <see cref="OrdnanceTurrets"/>.</summary>
internal sealed class TurretLookShow
{
    /// <summary>The sound set the three cues are in, asked for at each moment: null while the part has none (it failed to load).</summary>
    internal Func<ForgeAudio.AudioLibrary?> Sounds = static () => null;
    /// <summary>As the head starts to rise out of the shell; while the sentry stands (a loop); when its time runs out.</summary>
    internal string Deploy = "", Idle = "", Expire = "";
    /// <summary>Played at the muzzle, along the barrel, on each shot; set when the part's effect library is loaded.</summary>
    internal FxHandle? Muzzle;
}
