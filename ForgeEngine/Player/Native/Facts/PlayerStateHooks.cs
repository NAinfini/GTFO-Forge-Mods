using System;
using Agents;
using HarmonyLib;

namespace ForgePlayer.Native;

/// <summary>Player-state callbacks declared to the sole Runtime installer, plus the facade each one calls.
///
/// The damage path is read in two layers, because the game splits it that way:
/// <list type="bullet">
/// <item>Each receive entry (`ReceiveBulletDamage` and its siblings) knows the kind of damage it carries but not
/// whether it lands; its prefix records the kind for the duration of the call, and its postfix restores the outer kind.</item>
/// <item>`Dam_PlayerDamageBase.OnIncomingDamage` is the one accept path every receive entry funnels through. Its
/// prefix applies the already installed rule. Its postfix publishes the health actually deducted: the native bool
/// means lethal damage, so it cannot decide whether a nonlethal application occurred.</item>
/// </list>
/// The precedence on the two halves is fixed: `Prefix` runs first and the accept path's postfix runs inside it,
/// so the kind a fact carries is the kind of the entry that is running right now.
///
/// The health write is read from the three entries that write a player's health — the accept path above, the
/// add-health receive and the set-health receive — and only the outermost of them publishes, so one change is one
/// fact however the game nests the writes.
///
/// The infection write is a prefix/postfix pair on the one write entry: the prefix reads the value the receiver
/// holds before the write, the postfix publishes the value it holds after, and the delta between them is the write
/// that really happened.
///
/// The low-health event is not patched here: it arrives through the game-event funnel, which the player-event
/// observation owns because three other rows are read from the same call.</summary>
/// <summary>The kind of the damage receive entry currently running on this thread, as the index the `damage_kind`
/// set declares, and the enemy attack it carries, as the index the `player_attack_kind` set declares; either is null
/// when the running entry does not name one. Restored by the same entry's postfix/finalizer, so a nested call cannot leak a kind
/// into an outer one. The member names live in one place — `PlayerStateContract.DamageKindName` and
/// `PlayerStateContract.AttackKindName` — and the journal spells them through it.</summary>
internal readonly record struct PlayerDamageKindState(int? Kind, int? Attack, bool Entered);

internal static class PlayerDamageKindToken
{
    [ThreadStatic] private static int? current;
    [ThreadStatic] private static int? attack;

    internal static int? Current => current;

    internal static int? Attack => attack;

    /// <summary>Records the running entry's kinds while a plan listens to damage or a standing rule needs them: an unsubscribed
    /// hit reads one field and sets nothing.</summary>
    internal static PlayerDamageKindState Enter(int? kind, int? attackKind)
    {
        if (PlayerStateFacts.Installed is not { DamageObserved: true }
            && PlayerIncomingDamageRules.Installed is not { Active: true }) return default;
        var previous = new PlayerDamageKindState(current, attack, true);
        current = kind;
        attack = attackKind;
        return previous;
    }

    internal static void Leave(PlayerDamageKindState previous)
    {
        if (!previous.Entered) return;
        current = previous.Kind;
        attack = previous.Attack;
    }

    /// <summary>The finalizer of a kind entry: a throwing body still restores the outer kinds, and the error goes on unchanged.</summary>
    internal static Exception? Fault(Exception? error, PlayerDamageKindState previous)
    {
        if (error != null) Leave(previous);
        return error;
    }
}

/// <summary>One health write on a player receiver, as the outermost of the three entries that write it — the damage
/// accept path, the add-health receive and the set-health receive — sees it. A write that runs another inside it is
/// one change: only the outermost entry records the value before and publishes the value after, so a hit that the
/// game applies through a set-health is never two facts. Nothing is recorded, and nothing allocated, while no plan
/// listens to the health row. A value: the entry keeps it in its own `__state`.</summary>
internal readonly struct PlayerHealthWrite
{
    [ThreadStatic] private static bool open;

    private readonly bool _owns;
    private readonly float _before;

    private PlayerHealthWrite(float before)
    {
        _owns = true;
        _before = before;
    }

    /// <summary>Opens the write when it is the outermost one and the health row is listened to.</summary>
    internal static PlayerHealthWrite Enter(Dam_PlayerDamageBase damage)
    {
        if (open || PlayerStateFacts.Installed is not { } facts || !(facts.HealthObserved || facts.DamageObserved)) return default;
        open = true;
        return new PlayerHealthWrite(Health(damage));
    }

    /// <summary>The value the receiver held when this write was opened.</summary>
    internal float Before => _before;

    internal ForgeCore.HitState ToState() => new(_owns, _before);

    internal static PlayerHealthWrite From(ForgeCore.HitState state) => state.Open ? new PlayerHealthWrite(state.Value) : default;

    /// <summary>Closes the write this entry opened and answers whether it did, which is whether this entry is the
    /// one that publishes. The write is closed before the publish, so whatever the publish leads to is a write of its
    /// own. The postfix closes it on the path that completed; the finalizer closes it on the path that threw.</summary>
    internal bool Close()
    {
        if (!_owns) return false;
        open = false;
        return true;
    }

    private static float Health(Dam_PlayerDamageBase damage)
    {
        try { return damage == null ? float.NaN : damage.Health; }
        catch (Exception error) { PlayerCaughtFailures.Capture("health-read", error); return float.NaN; }
    }
}

/// <summary>The player's native deduction path. Its bool return indicates lethal damage, not acceptance.
/// Facts carry the observed health loss, including nonlethal damage, after the native body completes.
/// The same entry is the outermost health write of a hit: its prefix opens the write, its postfix publishes the change
/// with the attacker as its source, and its finalizer closes a write whose body threw. ForgeCore patches the method once
/// (<see cref="ForgeCore.PlayerHits"/>); this class is the observer it calls.</summary>
internal static class PlayerDamageAccepted
{
    internal static void Observe(string owner) => ForgeCore.PlayerHits.Observe(owner, Before, After, Faulted);

    private static ForgeCore.HitState Before(Dam_PlayerDamageBase receiver, ref float damage, Agent? source)
    {
        Prefix(receiver, ref damage, source!, out var state);
        return state.ToState();
    }

    private static void After(Dam_PlayerDamageBase receiver, Agent? source, ForgeCore.HitState state)
        => Postfix(receiver, source!, PlayerHealthWrite.From(state));

    private static void Faulted(ForgeCore.HitState state) => PlayerHealthWrite.From(state).Close();

    // No public native event/field provides per-receiver source/kind filtering before lethal deduction.
    // Retain this call-scoped argument adjustment; never skip/replay the native body, rewrite source or
    // originalDamage, restore shared state, or compensate with a second health write. Last priority lets
    // earlier damage modifiers participate; another Last-priority patch still has unspecified relative order.
    internal static void Prefix(Dam_PlayerDamageBase __instance, ref float damage, Agent sourceAgent, out PlayerHealthWrite __state)
    {
        __state = PlayerHealthWrite.Enter(__instance);
        damage = PlayerHost.Session?.GuardIncomingDamageRule(__instance, damage, sourceAgent) ?? damage;
    }

    internal static void Postfix(Dam_PlayerDamageBase __instance, Agent sourceAgent,
        PlayerHealthWrite __state)
    {
        if (!__state.Close()) return;
        float before = __state.Before;
        float applied = before - __instance.Health;
        if (float.IsFinite(applied) && applied > 0 && PlayerStateFacts.Installed is { DamageObserved: true })
        {
            int? kind = PlayerDamageKindToken.Current, attack = PlayerDamageKindToken.Attack;
            PlayerHost.Session?.GuardState(static (facts, call) =>
                facts.DamageApplied(call.Receiver, call.Applied, call.Source, call.Kind, call.Attack),
                (Receiver: __instance, Applied: applied, Source: sourceAgent, Kind: kind, Attack: attack));
        }
        PlayerHost.Session?.GuardState(static (facts, call) => facts.HealthChanged(call.Receiver, call.Before, call.Source, true),
            (Receiver: __instance, Before: before, Source: sourceAgent));
    }
}

/// <summary>The add-health receive a heal arrives through: an outermost health write whose source is the healer the
/// packet names. The healer is only read when this entry publishes, because reading it builds a managed wrapper.</summary>
[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveAddHealth))]
internal static class PlayerHealthAdded
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_PlayerDamageBase __instance, out PlayerHealthWrite __state)
        => __state = PlayerHealthWrite.Enter(__instance);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_PlayerDamageBase __instance, pAddHealthData data, PlayerHealthWrite __state)
    {
        if (!__state.Close()) return;
        float before = __state.Before;
        var healer = Healer(data);
        PlayerHost.Session?.GuardState(static (facts, call) => facts.HealthChanged(call.Receiver, call.Before, call.Source, true),
            (Receiver: __instance, Before: before, Source: healer));
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception, PlayerHealthWrite __state)
    {
        if (__exception != null) __state.Close();
        return __exception;
    }

    private static Agent? Healer(pAddHealthData data)
    {
        try { return data.source.TryGet(out var agent) ? agent : null; }
        catch (Exception error) { PlayerCaughtFailures.Capture("healer-read", error); return null; }
    }
}

/// <summary>The set-health receive a revive and a synced health value arrive through: an outermost health write
/// that carries no source, so its fact publishes the `source` port absent. ForgeCore patches the method once for enemies and players
/// (<see cref="ForgeCore.GameHooks.HealthSetReceived"/>); this is its player observer.</summary>
internal static class PlayerHealthSet
{
    private static bool subscribed;

    /// <summary>Once per process: a bracket has no unsubscribe, and the handlers reach the current session themselves.</summary>
    internal static void Subscribe()
    {
        if (subscribed) return;
        ForgeCore.GameHooks.HealthSetReceived.Around(Enter, Leave, Faulted);
        subscribed = true;
    }

    internal static PlayerHealthWrite Enter(Dam_SyncedDamageBase receiver)
        => receiver.TryCast<Dam_PlayerDamageBase>() is { } player ? PlayerHealthWrite.Enter(player) : default;

    internal static void Leave(Dam_SyncedDamageBase receiver, PlayerHealthWrite state, bool ran)
    {
        if (!state.Close() || receiver.TryCast<Dam_PlayerDamageBase>() is not { } player) return;
        float before = state.Before;
        PlayerHost.Session?.GuardState(static (facts, call) => facts.HealthChanged(call.Receiver, call.Before, null, false),
            (Receiver: player, Before: before));
    }

    private static void Faulted(Dam_SyncedDamageBase receiver, PlayerHealthWrite state, Exception error) => state.Close();
}

/// <summary>The infection write entry the action layer also submits through: the prefix reads the value before
/// the write, the postfix publishes what changed together with the write's own effect. Both halves read the
/// infection row's gate first: an unsubscribed write reads nothing and publishes nothing. ForgeCore patches the
/// method once (<see cref="ForgeCore.PlayerHits"/>); this class is the observer it calls.</summary>
internal static class PlayerInfectionWritten
{
    internal static void Observe(string owner) => ForgeCore.PlayerHits.ObserveInfection(owner, Before, Postfix);

    private static float Before(Dam_PlayerDamageBase receiver)
        => PlayerStateFacts.Installed is { InfectionObserved: true } ? Infection(receiver) : float.NaN;

    private static void Postfix(Dam_PlayerDamageBase __instance, pInfection data, float __state)
    {
        if (float.IsNaN(__state)) return;
        float before = __state;
        int cause = (int)data.effect;
        PlayerHost.Session?.GuardState(static (facts, call) => facts.InfectionChanged(call.Receiver, call.Before, call.Cause),
            (Receiver: __instance, Before: before, Cause: cause));
    }

    private static float Infection(Dam_PlayerDamageBase damage)
    {
        try { return damage == null ? float.NaN : damage.Infection; }
        catch (Exception error) { PlayerCaughtFailures.Capture("infection-read", error); return float.NaN; }
    }
}

// The receive entries and the `damage_kind` member each one names. A kind this half cannot name honestly is left
// unnamed: the entry sets no token, so its fact carries no `damage_kind` port instead of a guess. The mapping
// table and the entries left out are frozen in evidence/player-state-facts.json.

/// <summary>The six receive entries ForgeCore patches once (<see cref="ForgeCore.GameHooks.PlayerReceived"/>): each names its kind for the
/// duration of the call and restores the outer one after it, however the body ends.</summary>
internal static class PlayerDamageKindReceive
{
    private static bool subscribed;

    /// <summary>Once per process: a bracket has no unsubscribe, and the handlers reach the current session themselves.</summary>
    internal static void Subscribe()
    {
        if (subscribed) return;
        ForgeCore.GameHooks.PlayerReceived.Around(Enter, Leave, Faulted);
        subscribed = true;
    }

    internal static PlayerDamageKindState Enter(ForgeCore.PlayerReceive call) => call.Kind switch
    {
        ForgeCore.PlayerReceiveKind.Bullet => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDirect, PlayerStateContract.AttackKindBullet),
        ForgeCore.PlayerReceiveKind.ShooterProjectile => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindProjectile, PlayerStateContract.AttackKindShooterProjectile),
        ForgeCore.PlayerReceiveKind.Melee => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindMelee, PlayerStateContract.AttackKindMelee),
        ForgeCore.PlayerReceiveKind.Explosion => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindExplosion, PlayerStateContract.AttackKindExplosion),
        ForgeCore.PlayerReceiveKind.Fall => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindFall, null),
        ForgeCore.PlayerReceiveKind.TentacleAttack => PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindTentacle, PlayerStateContract.AttackKindTentacle),
        _ => default
    };

    internal static void Leave(ForgeCore.PlayerReceive call, PlayerDamageKindState state, bool ran) => PlayerDamageKindToken.Leave(state);

    private static void Faulted(ForgeCore.PlayerReceive call, PlayerDamageKindState state, Exception error) => PlayerDamageKindToken.Leave(state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFireDamage))]
internal static class PlayerFireDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot, PlayerStateContract.AttackKindFire);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveStickyDamage))]
internal static class PlayerStickyDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot, PlayerStateContract.AttackKindSticky);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveParasiteDamage))]
internal static class PlayerParasiteDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot, null);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

// The local player's Dam_PlayerDamageLocal overrides both receives with bodies of their own (0x161ABA0 parasite,
// 0x161AAB0 no-air; neither shared with another method, neither calls the base), so each needs its own entry with
// the same kind as the base hook. Both still reach OnIncomingDamage, where the kind is read.
[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveParasiteDamage))]
internal static class PlayerLocalParasiteDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot, null);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveNoAirDamage))]
internal static class PlayerLocalNoAirDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindDot, null);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceivePushDamage))]
internal static class PlayerPushDamageKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindCollision, null);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

// The tank and trap grab receive bodies are distinct native bodies that retain both the damage kind and the attack kind.

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveTentacleTankGrabDamage))]
internal static class PlayerTankGrabAttackKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindTankGrab, PlayerStateContract.AttackKindTankGrab);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

[HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveTentacleTrapGrabDamage))]
internal static class PlayerTrapGrabAttackKind
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(out PlayerDamageKindState __state) => __state = PlayerDamageKindToken.Enter(PlayerStateContract.DamageKindTrapGrab, PlayerStateContract.AttackKindTrapGrab);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerDamageKindState __state) => PlayerDamageKindToken.Leave(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, PlayerDamageKindState __state) => PlayerDamageKindToken.Fault(__exception, __state);
}

// Parasite trap and grab stop have no reviewed kind in either set. On the base class no-air shares its folded native
// body with ReceiveParasiteDamage (evidence/player-hooks.json), so one parasite hook names their common damage
// kind as dot but cannot distinguish suffocation or name a separate enemy attack; no second base patch is installed.
