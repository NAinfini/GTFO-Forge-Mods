using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The turret surface other packages use. A package registers turret looks (<see cref="RegisterLook"/>) and its <c>Gear</c>
/// call-ins name one with <c>"Look": "Pack:item"</c>; the platform then dresses every sentry that call-in stands, on every peer,
/// in that model (the game's sentry keeps aiming, firing and hitting; only its body meshes are hidden). The events let the
/// package add its own sounds and effects at the moments of a turret's life. Register looks from the plugin's <c>Load</c>
/// (definition files are read after it).
/// </summary>
public static class OrdnanceTurrets
{
    private static readonly TurretLookBook Book = new();
    private static readonly Dictionary<string, ModelHandle> LookModels = new(StringComparer.Ordinal);

    /// <summary>Every peer: a turret shell's petals begin to open (the deploy moment). The first argument is the look id of the call-in
    /// (empty when it names none), then the shell. Not raised for a shell that is replayed to a late joiner before this moment.</summary>
    public static event Action<string, Transform>? Deployed;

    /// <summary>Every peer: an Ordnance sentry exists (the game's own spawn hook ran). The first argument is its look id (empty for the
    /// game's own look). Use <see cref="TryLook"/> as the keep-alive test of a sound that follows it.</summary>
    public static event Action<string, SentryGunInstance, Transform>? Online;

    /// <summary>Every peer: a timed sentry's lifetime ran out (the host removes it right after). Look id, the sentry, its position.</summary>
    public static event Action<string, SentryGunInstance, Vector3>? Expiring;

    /// <summary>Registers a turret look. <paramref name="lookId"/> is the model's <c>Pack:item</c> name in ForgeVisual; its prefab must
    /// hold the four parts <paramref name="spec"/> names. Returns false, with one error in the log, when the id is not written
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
            Plugin.Error("Turret look '" + lookId + "' refused: " + why + ".");
            return false;
        }
        LookModels.Add(lookId, model);
        return true;
    }

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

    internal static void RaiseDeployed(string lookId, Transform shell) => Raise(() => Deployed?.Invoke(lookId, shell), "Deployed");

    internal static void RaiseOnline(string lookId, SentryGunInstance sentry) => Raise(() => Online?.Invoke(lookId, sentry, sentry.transform), "Online");

    internal static void RaiseExpiring(string lookId, SentryGunInstance sentry, Vector3 at) => Raise(() => Expiring?.Invoke(lookId, sentry, at), "Expiring");

    /// <summary>A handler's failure is logged and never reaches the sentry's own work.</summary>
    private static void Raise(Action raise, string what)
    {
        try { raise(); }
        catch (Exception error) { Plugin.Error("A turret " + what + " handler failed: " + error); }
    }
}
