using System;
using Agents;
using HarmonyLib;
using Player;

namespace ForgeCore;

/// <summary>What the damage observer opened in <see cref="IPlayerHitObserver.Before"/>, handed back to its <c>After</c> or <c>Faulted</c>
/// for the same call (a value, nothing allocated).</summary>
public readonly record struct HitState(bool Open, float Value);

/// <summary>Reads a hit on a player around the game's own damage accept path.</summary>
public interface IPlayerHitObserver
{
    /// <summary>Before the game's body, after every <see cref="PlayerHits.Redirect"/>; the damage may still be changed.</summary>
    HitState Before(Dam_PlayerDamageBase receiver, ref float damage, Agent? source);
    /// <summary>After the game's body returned.</summary>
    void After(Dam_PlayerDamageBase receiver, Agent? source, HitState state);
    /// <summary>The game's body threw.</summary>
    void Faulted(HitState state);
}

/// <summary>Reads an infection write on a player around the game's own entry.</summary>
public interface IInfectionObserver
{
    /// <summary>Before the write; the answer comes back to <see cref="After"/> (NaN = nothing read).</summary>
    float Before(Dam_PlayerDamageBase receiver);
    /// <summary>After the write.</summary>
    void After(Dam_PlayerDamageBase receiver, pInfection data, float before);
}

/// <summary>The one place that patches the game's player hit path: <c>Dam_PlayerDamageBase.OnIncomingDamage</c> (every damage entry
/// funnels through it, on whichever peer receives the packet) and <c>Dam_PlayerDamageBase.ModifyInfection</c>. A mod changes or reads a hit
/// through the registrations here instead of patching these methods itself. Main thread only; one registration per owner (null removes
/// it). The first registration puts the patches on.</summary>
public static class PlayerHits
{
    private static (string Owner, Func<Dam_PlayerDamageBase, float, Agent?, float> Redirect)[] _redirects = [];
    private static (string Owner, Func<Dam_PlayerDamageBase, pInfection, bool> Allow)[] _gates = [];
    private static (string Owner, IPlayerHitObserver Observer)? _hitObserver;
    private static (string Owner, IInfectionObserver Observer)? _infectionObserver;

    /// <summary>Changes the damage of a hit before anything else reads it: <paramref name="redirect"/> gets the receiver, the damage and
    /// the attacker and answers the damage to apply (0 = none). Redirects run in registration order, before the observer.</summary>
    public static void Redirect(string owner, Func<Dam_PlayerDamageBase, float, Agent?, float>? redirect)
    {
        Ensure();
        _redirects = Replace(_redirects, owner, redirect);
    }

    /// <summary>Refuses an infection write: <paramref name="allow"/> answers false to skip the game's write. A write any gate refuses is
    /// skipped; the infection observer still sees it.</summary>
    public static void GateInfection(string owner, Func<Dam_PlayerDamageBase, pInfection, bool>? allow)
    {
        Ensure();
        _gates = Replace(_gates, owner, allow);
    }

    /// <summary>The one reader of hits (runs last, after every redirect, so it sees the damage the game will apply). A second owner is refused.</summary>
    public static void Observe(string owner, IPlayerHitObserver? observer)
    {
        Ensure();
        _hitObserver = Slot(_hitObserver, owner, observer);
    }

    /// <summary>The one reader of infection writes. A second owner is refused.</summary>
    public static void ObserveInfection(string owner, IInfectionObserver? observer)
    {
        Ensure();
        _infectionObserver = Slot(_infectionObserver, owner, observer);
    }

    private static void Ensure() => Patches.Ensure(Modules.PlayerHits, typeof(PlayerHitHooks));

    private static (string, T)[] Replace<T>((string Owner, T Handler)[] list, string owner, T? handler) where T : class
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        var rest = Array.FindAll(list, entry => entry.Owner != owner);
        return handler == null ? rest : [.. rest, (owner, handler)];
    }

    private static (string, T)? Slot<T>((string Owner, T Observer)? current, string owner, T? observer) where T : class
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (current is { } held && held.Owner != owner && observer != null)
            throw new InvalidOperationException("PlayerHits already has an observer from " + held.Owner + ".");
        if (current is { } mine && mine.Owner != owner) return current;
        return observer == null ? null : (owner, observer);
    }

    internal static float RedirectDamage(Dam_PlayerDamageBase receiver, float damage, Agent? source)
    {
        foreach (var (_, redirect) in _redirects) damage = redirect(receiver, damage, source);
        return damage;
    }

    internal static bool AllowInfection(Dam_PlayerDamageBase receiver, pInfection data)
    {
        foreach (var (_, allow) in _gates)
            if (!allow(receiver, data)) return false;
        return true;
    }

    internal static IPlayerHitObserver? HitObserver => _hitObserver?.Observer;
    internal static IInfectionObserver? InfectionObserver => _infectionObserver?.Observer;
}

/// <summary>Put on by the first <see cref="PlayerHits"/> registration. Redirects run first (<see cref="Priority.First"/>); the observer
/// brackets the game's body last (<see cref="Priority.Last"/>), so it reads what the earlier patches left.</summary>
internal static class PlayerHitHooks
{
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Redirect(Dam_PlayerDamageBase __instance, ref float damage, Agent sourceAgent)
    {
        try { damage = PlayerHits.RedirectDamage(__instance, damage, sourceAgent); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A player hit redirect failed: " + error); }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Observe(Dam_PlayerDamageBase __instance, ref float damage, Agent sourceAgent, out HitState __state)
    {
        __state = default;
        var observer = PlayerHits.HitObserver;
        if (observer == null) return;
        try { __state = observer.Before(__instance, ref damage, sourceAgent); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The player hit observer failed: " + error); }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Observed(Dam_PlayerDamageBase __instance, Agent sourceAgent, HitState __state)
    {
        var observer = PlayerHits.HitObserver;
        if (observer == null) return;
        try { observer.After(__instance, sourceAgent, __state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The player hit observer failed: " + error); }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, HitState __state)
    {
        if (__exception == null) return null;
        try { PlayerHits.HitObserver?.Faulted(__state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The player hit observer failed: " + error); }
        return __exception;
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ModifyInfection))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool InfectionBefore(Dam_PlayerDamageBase __instance, pInfection data, out float __state)
    {
        __state = float.NaN;
        try
        {
            var observer = PlayerHits.InfectionObserver;
            if (observer != null) __state = observer.Before(__instance);
            return PlayerHits.AllowInfection(__instance, data);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("A player infection hook failed: " + error);
            return true;
        }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ModifyInfection))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void InfectionAfter(Dam_PlayerDamageBase __instance, pInfection data, float __state)
    {
        try { PlayerHits.InfectionObserver?.After(__instance, data, __state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The player infection observer failed: " + error); }
    }
}
