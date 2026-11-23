using ForgeEngine.GameBindings;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using HarmonyLib;
using Player;
using SNetwork;

namespace ForgePlayer.Native;

/// <summary>The player-life observation half of the Player provider: `forge.trigger.player.*` for one life's
/// transitions. It publishes through the Player registration the session already owns, one fact per native
/// transition, and it publishes nothing it did not read.
///
/// Publication rules, shared with the map-object half of this provider:
/// <list type="bullet">
/// <item>Only the host publishes. A client's copy of a life transition is a second publisher of a fact the
/// kernel answers per host, so a client read is refused and reported once per reason.</item>
/// <item>One native transition is one event id: the fact, the world epoch, the life the fact is about and a
/// per-life transition number. The kernel's own ledger answers a repeated report with `duplicate` instead of
/// dispatching twice, and the transition number is claimed before the publish, so a game callback that runs
/// twice for one transition (a host body and its sync body, a state entered through two entries) costs nothing
/// and can never reuse an id the kernel has already seen.</item>
/// <item>A fact whose required port cannot be read is not published at all. `player` is required on every row
/// here, so an unresolvable player publishes nothing; a port the catalog marks nullable carries JSON null when
/// the native side genuinely names nobody. A port that is merely unreadable is left out of the payload, which
/// is the framework's own "not observable" and never a claim that the port was written with nothing.</item>
/// <item>Every id and every diagnostic is built from the recorded entity id, the fact name and the reason word.
/// The account key a life is keyed by internally is never formatted, never logged and never placed in an id.</item>
/// </list>
///
/// The native entry points this half hooks are frozen in `evidence/player-life-facts.json`; a hook decides only
/// when to read, never what a fact says.</summary>
internal sealed class PlayerLifeFacts
{
    private const string Prefix = EntityKinds.Player + ":";

    /// <summary>What this half already published or already decided about one life in one world. One life is
    /// one subject: the entity id carries the per-world player number, so a replaced life is a new subject with
    /// its own transitions, and a world change clears the table.</summary>
    private sealed class LifeRecord
    {
        /// <summary>A downed transition was reported for this life and no revive has ended it yet.</summary>
        internal bool Downed;
        /// <summary>A rescue is running right now, with the actor that started it.</summary>
        internal EntityReference? Rescuer;
        /// <summary>The revive of the running rescue already ran, so its end is a revive and not a cancellation.</summary>
        internal bool ReviveRan;
        /// <summary>The life's death was reported, so the same state is not a second death.</summary>
        internal bool Dead;
        internal long Transitions;
        /// <summary>The life epoch this record belongs to: a respawn keeps the player id and takes a new epoch, so
        /// the next life starts from a clean record instead of the previous life's flags.</summary>
        internal long LifeEpoch;
    }

    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _authority;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LifeRecord> _lives = new(StringComparer.Ordinal);
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private RuntimeLifecycleSubscription? _lifecycle;
    private bool _disposed;

    /// <summary>The player identity every read goes through: the module the Player provider registered, the one place
    /// the recorded lives and their native agents are known. A half with no identity reads nothing.</summary>
    internal PlayerIdentityModule? World { get; init; }

    private PlayerIdentityModule? Lives => World;

    /// <summary>Attaches this half to the Player registration the session already owns. The subscription that drops
    /// the per-world tables is taken here, and a failure to take it is a failure to start: a half that kept a
    /// dead world's transitions would report a teleport from a level that no longer exists.</summary>
    internal PlayerLifeFacts(RuntimeModuleHandle registration, IForgeEngine kernel, Func<bool> authority,
        Action<string> report, Action<string> log)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _lifecycle = _registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged
                || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) BeginWorld();
        });
    }

    // The subscription gates of the rows this half publishes, resolved once through the same binding mapping the
    // publish uses and kept as fields rather than looked up again on every publish.
    private RuntimeSubscriptionGate? _revivedGate, _reviveStartedGate, _reviveCancelledGate, _diedGate, _teleportedGate, _respawnedGate, _downedGate;
    private bool _gatesResolved;

    private void ResolveGates()
    {
        if (_gatesResolved) return;
        _revivedGate = GateOf("revived");
        _reviveStartedGate = GateOf("revive_started");
        _reviveCancelledGate = GateOf("revive_cancelled");
        _diedGate = GateOf("died");
        _teleportedGate = GateOf("teleported");
        _respawnedGate = GateOf("respawned");
        _downedGate = GateOf("downed");
        _gatesResolved = true;
    }

    private RuntimeSubscriptionGate? GateOf(string fact)
        => _gates.TryGetValue(PlayerLifeContract.BindingOf(fact), out var gate) ? gate : null;

    /// <summary>The gate resolved for one fact this half publishes, read from the fields above instead of the
    /// registration's own dictionary. A fact this switch does not name falls back to the dictionary. A
    /// binding with no gate is one this registration does not hold, because the native target its feature stands on
    /// was refused; it is never published.</summary>
    private RuntimeSubscriptionGate? CachedGateOf(string fact)
    {
        ResolveGates();
        return fact switch
        {
            "revived" => _revivedGate,
            "revive_started" => _reviveStartedGate,
            "revive_cancelled" => _reviveCancelledGate,
            "died" => _diedGate,
            "teleported" => _teleportedGate,
            "respawned" => _respawnedGate,
            "downed" => _downedGate,
            _ => GateOf(fact)
        };
    }

    public bool Authoritative => !_disposed && _registration.IsRegistered
        && _kernel.StartupState == RuntimeStartupState.Ready && _authority();

    // ----------------------------------------------------------------- reading

    /// <summary>The identity records the level's lives; nothing else about this half's own reads changes. It is
    /// what the world contract asks of an identity, and it is refused on a peer that may not read.</summary>
    public void Reconcile()
    {
        CheckThread();
        if (!Authoritative) return;
        Lives?.Reconcile();
    }

    /// <summary>Whether one reference is a life this process may read right now. Only the host reads, so a
    /// client's callback reads nothing at all, and a life the identity no longer holds answers null exactly as a
    /// life the world never held does.</summary>
    private bool Current(EntityReference reference)
    {
        CheckThread();
        return Authoritative && LifeOf(reference) != null;
    }

    public double[]? Position(EntityReference reference)
    {
        CheckThread();
        if (!Authoritative) return null;
        return Lives?.Position(reference);
    }

    public double[]? Position(object? locationData)
        => locationData is pPlayerLocationData location ? PlayerNativeValues.Coordinates(location.goodPosition) : null;

    public EntityReference? ReferenceOf(object? agent)
    {
        CheckThread();
        if (!Authoritative) return null;
        return Lives?.ReferenceOf(agent);
    }

    public EntityReference? ReferenceOf(SNet_Player? player)
    {
        CheckThread();
        if (!Authoritative) return null;
        return Lives?.ReferenceOf(player);
    }

    public bool HadLife(SNet_Player? player)
    {
        CheckThread();
        return Authoritative && Lives is { } identity && identity.HadLife(player);
    }

    public RuntimePlayerLife? LifeOf(EntityReference reference)
    {
        CheckThread();
        if (!Authoritative) return null;
        return Lives?.LifeOf(reference);
    }

    public EntityReference? ReviverOf(EntityReference reference)
    {
        CheckThread();
        if (!Authoritative) return null;
        return Lives?.ReviverOf(reference);
    }

    // ------------------------------------------------------------- transitions

    /// <summary>A life entered the game's downed state. The state's own enter bodies differ between the host and
    /// a client, so the reading is taken from the locomotion machine's current state rather than from which body
    /// ran: the fact is published once per downed episode, whichever body reported it.</summary>
    public bool Downed(object? downedState)
    {
        CheckThread();
        return downedState is PLOC_Downed downed && Note(downed.m_owner);
    }

    /// <summary>The downed state ran its revive. The row is published from the revive itself and not from the
    /// state leaving, so a revive whose state transition is deferred still reports the moment the game restored
    /// the player, with the actor the rescue was started by.</summary>
    public bool Revived(object? downedState)
    {
        CheckThread();
        if (downedState is not PLOC_Downed downed) return false;
        if (downed.m_owner is not PlayerAgent player) return false;
        var reference = ReferenceOf(player);
        if (reference is not { } life || !Current(life)) return false;
        Track(life);
        var entry = _lives[life.Id];
        // A revive is the end of a downed episode: a life that was never reported downed has no episode to end.
        if (!entry.Downed || entry.ReviveRan) return false;
        var actor = ReviverOf(life) ?? entry.Rescuer;
        // The lifecycle record follows the game whether or not anyone subscribes to this fact: a later `died` or
        // `downed` is judged against it, and a plan that only listens to `revive_cancelled` must still work.
        var published = false;
        if (Begin("revived", entry, out var e))
        {
            PlayerLifeContract.WriteRevived(e.Values, life, actor);
            published = Raise(e, life, "revived", entry);
        }
        entry.ReviveRan = true; entry.Downed = false; entry.Rescuer = null; entry.Dead = false;
        return published;
    }

    /// <summary>A rescue began. The rescuer port is required on this row, so a rescue whose actor this process
    /// cannot name publishes nothing: a fact that says "someone" would be a fact about nobody.</summary>
    public bool ReviveStarted(object? rescuer, object? target)
    {
        CheckThread();
        if (!Subject(target, out var life, out var entry)) return false;
        if (entry.Rescuer != null || entry.ReviveRan) return false;
        var actor = ReferenceOf(rescuer);
        if (actor == null)
        {
            ReportOnce("revive-rescuer", "a revive interaction named a rescuer this process tracks no life for; "
                + "no revive_started fact was published.");
            return false;
        }
        var published = false;
        if (Begin("revive_started", entry, out var e))
        {
            PlayerLifeContract.WriteReviveStarted(e.Values, life, actor);
            published = Raise(e, life, "revive_started", entry);
        }
        entry.Rescuer = actor;
        return published;
    }

    /// <summary>A running rescue ended without the revive. Only a rescue this half saw start can be cancelled:
    /// an interaction that ends without ever having started one is not a rescue the players watched happen.</summary>
    public bool ReviveCancelled(object? rescuer, object? target)
    {
        CheckThread();
        if (!Subject(target, out var life, out var entry)) return false;
        if (entry.Rescuer == null || entry.ReviveRan) return false;
        var actor = ReferenceOf(rescuer) ?? entry.Rescuer;
        var published = false;
        if (Begin("revive_cancelled", entry, out var e))
        {
            PlayerLifeContract.WriteReviveCancelled(e.Values, life, actor);
            published = Raise(e, life, "revive_cancelled", entry);
        }
        entry.Rescuer = null;
        return published;
    }

    /// <summary>A life's alive flag was written false. The transition is judged from the write and from the
    /// identity's own read, so a write to an agent this provider does not track publishes nothing.</summary>
    public bool Died(bool alive, object? agent)
    {
        CheckThread();
        if (alive) return false;
        if (agent is not PlayerAgent player) return false;
        var reference = ReferenceOf(player);
        if (reference is not { } life || !Current(life)) return false;
        Track(life);
        var entry = _lives[life.Id];
        if (entry.Dead) return false;
        if (LifeOf(life) is not { Alive: false, Downed: false }) return false;
        var published = false;
        if (Begin("died", entry, out var e))
        {
            PlayerLifeContract.WriteDied(e.Values, life);
            published = Raise(e, life, "died", entry);
        }
        entry.Dead = true; entry.Downed = false; entry.Rescuer = null; entry.ReviveRan = false;
        return published;
    }

    /// <summary>A life was warped. The origin is the position the hook read before the native call and the
    /// destination is the position the life holds after it; a move whose either end cannot be read publishes
    /// nothing rather than an invented end of the move. A player another machine simulates is warped there, not
    /// here: that machine reports both ends (<see cref="PlayerInputReport.Teleported"/>) and
    /// <paramref name="reported"/> is the destination it read, used instead of the host's own copy of the position,
    /// which only follows later with that machine's position sync.</summary>
    public bool Teleported(object? agent, double[]? from, double[]? reported = null)
    {
        CheckThread();
        if (agent is not PlayerAgent player || from == null) return false;
        var reference = ReferenceOf(player);
        if (reference is not { } life || !Current(life)) return false;
        Track(life);
        var entry = _lives[life.Id];
        if ((reported ?? Position(life)) is not { } to) return false;
        if (!Begin("teleported", entry, out var e)) return false;
        PlayerLifeContract.WriteTeleported(e.Values, life, from, to);
        return Raise(e, life, "teleported", entry);
    }

    /// <summary>The replication path spawned a player. That path creates every player in a level — the first
    /// landing, a late join and a later life alike — so the spawn is a respawn only when the identity already
    /// recorded a life for that player in this world. The first spawn of a player is recorded and publishes nothing.</summary>
    public bool Respawned(object? spawnData)
    {
        CheckThread();
        if (spawnData is not pPlayerSpawnData spawn) return false;
        if (Lives is not { } identity) return false;
        // Asked before the identity records the life this spawn just created, or every spawn would look like a second one.
        var named = SpawnedPlayer(spawn.snetPlayer);
        bool hadLife = identity.HadLife(named);
        identity.Reconcile();
        if (!hadLife) return false;
        if (identity.ReferenceOf(named) is not { } life) return false;
        Track(life);
        var entry = _lives[life.Id];
        // The position the game handed the spawn is the one position this fact can report: the agent the spawn
        // creates may not exist yet at this point in the native call.
        if (Position(spawn.locationData) is not { } to) return false;
        if (!Begin("respawned", entry, out var e)) return false;
        PlayerLifeContract.WriteRespawned(e.Values, life, to);
        return Raise(e, life, "respawned", entry);
    }

    /// <summary>The player a spawn's replication data names. The game carries it as a wrapper struct that resolves
    /// to the player object, so the value is taken as an object and resolved by what it is.</summary>
    private static SNet_Player? SpawnedPlayer(object? named) => named switch
    {
        SNet_Player player => player,
        SNetStructs.pPlayer wrapped => wrapped.TryGetPlayer(out var player) ? player : null,
        _ => null
    };

    // ------------------------------------------------------------ the one pass

    /// <summary>The downed transition of one read. The reading is taken once per native callback and judged
    /// against what this half already reported for that life, so one transition is one fact and a repeated
    /// callback for the same state is nothing.</summary>
    private bool Note(object? agent)
    {
        CheckThread();
        if (agent is not PlayerAgent player) return false;
        var reference = ReferenceOf(player);
        if (reference is not { } life || !Current(life)) return false;
        Track(life);
        var entry = _lives[life.Id];
        if (entry.Downed) return false;
        // Downed is the locomotion machine's own state, and the state's enter bodies differ between the host and
        // a synced peer: a rescuable Downed state is independent of the native Alive flag. The native revive
        // request requires that flag to be false; an ended life without this state is not a downing.
        if (LifeOf(life) is not { Downed: true, Revived: false }) return false;
        var published = false;
        if (Begin("downed", entry, out var e))
        {
            PlayerLifeContract.WriteDowned(e.Values, life);
            published = Raise(e, life, "downed", entry);
        }
        entry.Downed = true; entry.ReviveRan = false; entry.Rescuer = null; entry.Dead = false;
        return published;
    }

    private bool Subject(object? target, [NotNullWhen(true)] out EntityReference? reference, out LifeRecord entry)
    {
        entry = null!; reference = default;
        var life = ReferenceOf(target);
        if (life is not { } named || !Current(named)) return false;
        Track(named);
        reference = named; entry = _lives[named.Id];
        return true;
    }

    /// <summary>The one subject per life. A life the identity no longer holds is never tracked.</summary>
    private void Track(EntityReference reference)
    {
        _lives.TryGetValue(reference.Id, out var previous);
        if (previous != null && previous.LifeEpoch == reference.LifeEpoch) return;
        // The transition number carries over: the event id is world + life id + transition, and a restart at zero
        // would reuse an id the kernel already saw for the previous life.
        _lives[reference.Id] = new LifeRecord { LifeEpoch = reference.LifeEpoch, Transitions = previous?.Transitions ?? 0 };
    }

    /// <summary>Claims one transition of one life and begins its event when anyone listens. The transition number
    /// is claimed before the publish: a refused publish must not let the next report reuse an id the kernel may
    /// already have seen. Nothing listening on this fact's binding means the kernel would answer `no-consumer`, so
    /// the event is never begun; the transition is still claimed, because a report seen while nobody listened is
    /// one this half has already made. The gate is the field resolved once above, never a fresh dictionary lookup,
    /// and the event publishes through the binding it names.</summary>
    private bool Begin(string fact, LifeRecord entry, out EventWriter e)
    {
        entry.Transitions += 1;
        e = default;
        if (CachedGateOf(fact) is not { HasSubscribers: true } gate) return false;
        e = _registration.BeginEvent(gate);
        return true;
    }

    /// <summary>Publishes one written fact of one life. The event id is the fact's own identity — the fact, the
    /// world, the life and the transition number <see cref="Begin"/> claimed — so a repeated native report of one
    /// transition is the kernel's own `duplicate` answer and a different transition of the same fact in the same
    /// world is its own id.</summary>
    private bool Raise(EventWriter e, EntityReference reference, string fact, LifeRecord entry)
    {
        string transition = (entry.Transitions - 1).ToString(CultureInfo.InvariantCulture);
        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + transition;
        var answer = e.Raise(eventId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_kernel.WorldEpoch));
        if (answer.Status == "queued")
        {
            // The line is composed only when the session's info gate would write it.
            if (_registration.Logs(RuntimeLogLevel.Info)) Record("player." + fact + " id=" + reference.Id + " transition=" + transition);
            return true;
        }
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return false;
    }

    private void Record(string line)
    {
        _log("player." + line);
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, "Player life observation: " + message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player life observation requires the runtime's own simulation thread.");
    }

    /// <summary>Drops this half's per-world tables. The registration belongs to the session, which disposes it
    /// for every half of this provider; this half only releases what it read in the world that just ended.</summary>
    public void BeginWorld()
    {
        CheckThread();
        _lives.Clear();
        _once.Reset();
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
        _lives.Clear();
    }
}

[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.Enter))]
internal static class DownedStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PLOC_Downed __instance)
        => PlayerHost.Session?.GuardLifeFacts(facts => facts.Downed(__instance));
}

[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.SyncEnter))]
internal static class SyncedDownedStateReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PLOC_Downed __instance)
        => PlayerHost.Session?.GuardLifeFacts(facts => facts.Downed(__instance));
}

// ForgeCore patches PLOC_Downed.OnPlayerRevived once; this reads the life after the body and every other postfix ran.
internal static class RevivedStateReadback
{
    private static bool subscribed;

    /// <summary>Once per process: a bracket has no unsubscribe, and the handler reaches the current session itself.</summary>
    internal static void Subscribe()
    {
        if (subscribed) return;
        ForgeCore.GameHooks.PlayerRevived.After(static (instance, _) => Revived(instance));
        subscribed = true;
    }

    private static void Revived(PLOC_Downed instance) => PlayerHost.Session?.GuardLifeFacts(facts => facts.Revived(instance));
}

[HarmonyPatch(typeof(Interact_Revive), nameof(Interact_Revive.OnInteractorStateChanged))]
internal static class ReviveInteractionReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Interact_Revive __instance, PlayerAgent sourceAgent, bool state)
        => PlayerHost.Session?.GuardLifeFacts(facts =>
        {
            // The argument is the interacting player; the instance's own target agent is the downed player whose
            // revive interaction this is. Both are handed to the half as native agents and resolved back through
            // the identity there, never off the argument's own fields.
            var target = __instance.m_interactTargetAgent;
            _ = state
                ? facts.ReviveStarted(sourceAgent, target)
                : facts.ReviveCancelled(sourceAgent, target);
        });
}

// A player's death is the replicated set-dead the damage base receives. The alive setters are no entry: the base
// one is the empty stub 2102 dump entries share and the player override is a plain bool setter folded with two
// unrelated setters, so a patch on either runs for code that has nothing to do with a death. The local player's
// damage base overrides the receive, so both are read (the local one through ForgeCore); `Died` rereads the life and publishes a death once.
[HarmonyPatch(typeof(Dam_PlayerDamageBase), "ReceiveSetDead")]
internal static class PlayerDiedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_PlayerDamageBase __instance)
    {
        PlayerHost.Session?.GuardLifeFacts(facts => facts.Died(false, __instance.Owner));
        PlayerHost.Session?.Guard(static module => module.NoteLifeChange());
    }
}

// ForgeCore patches Dam_PlayerDamageLocal.ReceiveSetDead once for every package; this reads the life after the body and every other postfix ran.
internal static class LocalPlayerDiedReadback
{
    private static bool subscribed;

    /// <summary>Once per process: a bracket has no unsubscribe, and the handler reaches the current session itself.</summary>
    internal static void Subscribe()
    {
        if (subscribed) return;
        ForgeCore.GameHooks.LocalPlayerDied.After(static (instance, _) => Died(instance));
        subscribed = true;
    }

    private static void Died(Dam_PlayerDamageLocal instance)
    {
        PlayerHost.Session?.GuardLifeFacts(facts => facts.Died(false, instance.Owner));
        PlayerHost.Session?.Guard(static module => module.NoteLifeChange());
    }
}

[HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.WarpTo))]
internal static class PlayerWarpedReadback
{
    /// <summary>The origin of the move is the position before the native call; the postfix can only see the new one.</summary>
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(PlayerAgent __instance, out double[]? __state)
    {
        double[]? from = null;
        PlayerHost.Session?.GuardLifeFacts(_ => from = PlayerNativeValues.Coordinates(__instance.Position));
        __state = from;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerAgent __instance, double[]? __state)
        => PlayerHost.Session?.GuardLifeFacts(facts =>
        {
            // A client's own player: its machine is the one the game warps it on, so it reports the move to the host.
            if (__state != null && PlayerInputReport.Current?.Teleported(__instance, __state,
                    PlayerNativeValues.Coordinates(__instance.Position)) == true) return;
            _ = facts.Teleported(__instance, __state);
        });
}
