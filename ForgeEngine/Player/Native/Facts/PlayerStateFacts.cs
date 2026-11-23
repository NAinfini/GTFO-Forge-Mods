using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>One read of one player's vital state, as the native receiver reports it right now. A value: the caller
/// never keeps it across a native call.</summary>
internal readonly record struct PlayerVitals(EntityReference Reference, float Health, float Maximum, float Infection, bool Alive);

/// <summary>The player-state observation half of the Player provider: the damage application, the health write, the
/// low-health event and the infection write, each published as one fact about one player life.
///
/// The rules this half publishes under are the ones the player-life half already follows, because two families of
/// facts about the same lives must not disagree about what a fact is:
/// <list type="bullet">
/// <item>Only the host publishes, and only while the runtime is ready. A read on a machine that may not publish
/// is refused and reported once per reason.</item>
/// <item>One native transition is one event id: the fact, the world epoch, the life and a per-life transition
/// number claimed before the publish, so a callback that runs twice for one transition costs nothing and can
/// never reuse an id the kernel has already seen.</item>
/// <item>A fact whose required port cannot be read is not published. `player`/`target` is required on every row
/// here; a port the native side genuinely answered with nobody is written as JSON null, and a port this half
/// cannot observe is left out of the payload entirely.</item>
/// </list>
///
/// The damage row is the canonical `forge.trigger.combat.damage_applied`, so this half reads its ports from
/// `PlayerStateContract` and reports the attacker through whichever domain can name it: a player attacker through
/// this package's own identity, an enemy attacker through the kind that owns enemies. `friendly_fire` is a
/// derivation and is written as one in the evidence: the game posts a friendly-fire event of its own, and the
/// attacker's own domain is what this half can read, so the port is true for a player attacker, false for an
/// attacker another domain named, and absent when no domain can name the attacker at all. The row's `equipment`
/// port is the one thing here that no receiver can read: it is the weapon provider's own shot correlation, asked
/// about the attacker, and it is absent for every application that no weapon's firing window caused.</summary>
internal sealed class PlayerStateFacts
{
    private const string Prefix = EntityKinds.Player + ":";

    /// <summary>What this half already decided about one life in one world. One life is one subject, and a world
    /// change clears the table.</summary>
    private sealed class LifeRecord
    {
        /// <summary>Transition numbers, one per fact, so a repeated native report of one transition is the
        /// kernel's own `duplicate` instead of a second event.</summary>
        internal readonly Dictionary<string, long> Transitions = new(StringComparer.Ordinal);

        /// <summary>The health this half last read back after a write it observed, and the subscription generation
        /// that observation belonged to. A health row nobody listened to in between leaves no baseline, so a gap in
        /// listening is never read as a bypass.</summary>
        internal double? LastHealth;
        internal long LastHealthGeneration = -1;
    }

    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly IForgeEngine _kernel;
    private readonly NativePlayerVitals _source;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LifeRecord> _lives = new(StringComparer.Ordinal);
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    /// <summary>The one attached half. A hook runs through <see cref="PlayerStateHooks"/>, which reads this
    /// late, so a session that failed before the half existed simply publishes nothing.</summary>
    internal static PlayerStateFacts? Installed { get; private set; }

    internal PlayerStateFacts(RuntimeModuleHandle registration, IForgeEngine kernel, NativePlayerVitals source,
        Action<string> log, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }

    /// <summary>Publishes through the Player registration the session already owns and becomes the half every hook
    /// reads. Called once per session, after that registration exists.</summary>
    internal static PlayerStateFacts Attach(RuntimeModuleHandle registration, IForgeEngine kernel,
        NativePlayerVitals source, Action<string> log, Action<string> report)
    {
        var facts = new PlayerStateFacts(registration, kernel, source, log, report);
        Installed = facts;
        return facts;
    }

    internal static void Detach(PlayerStateFacts facts)
    {
        if (ReferenceEquals(Installed, facts)) Installed = null;
    }

    // The subscription gates of the rows this half publishes, resolved once through the same binding mapping
    // the publish uses and kept as fields rather than looked up again on every publish. A hook reads one of
    // these before it builds anything, so a hit nobody listens for costs a field read and allocates nothing. A
    // binding with no gate is one this registration does not hold, because the native target its feature stands
    // on was refused; it is never published.
    private RuntimeSubscriptionGate? _damageGate, _healthGate, _infectionGate, _lowHealthGate;
    private bool _gatesResolved;

    /// <summary>Whether a plan listens to this half's damage row right now.</summary>
    internal bool DamageObserved { get { ResolveGates(); return _damageGate?.HasSubscribers ?? false; } }

    /// <summary>Whether a plan listens to this half's health row right now.</summary>
    internal bool HealthObserved { get { ResolveGates(); return _healthGate?.HasSubscribers ?? false; } }

    /// <summary>Whether a plan listens to this half's infection row right now.</summary>
    internal bool InfectionObserved { get { ResolveGates(); return _infectionGate?.HasSubscribers ?? false; } }

    private void ResolveGates()
    {
        if (_gatesResolved) return;
        _damageGate = GateOf("damage_applied");
        _healthGate = GateOf("health_changed");
        _infectionGate = GateOf("infection_changed");
        _lowHealthGate = GateOf("low_health");
        _gatesResolved = true;
    }

    private RuntimeSubscriptionGate? GateOf(string fact)
        => _gates.TryGetValue(PlayerStateContract.BindingOf(fact), out var gate) ? gate : null;

    /// <summary>The gate resolved for one fact this half publishes, read from the fields above instead of the
    /// registration's own dictionary: every publish asks this rather than repeating the dictionary lookup that
    /// built those fields. A fact this switch does not name (there is none among the ones this half publishes)
    /// falls back to the dictionary.</summary>
    private RuntimeSubscriptionGate? CachedGateOf(string fact)
    {
        ResolveGates();
        return fact switch
        {
            "damage_applied" => _damageGate,
            "health_changed" => _healthGate,
            "infection_changed" => _infectionGate,
            "low_health" => _lowHealthGate,
            _ => GateOf(fact)
        };
    }

    // ---------------------------------------------------------------- transition entries

    /// <summary>One native damage application: the receiver accepted `amount` from `sourceAgent` through the
    /// receive entry `kind` names. The damage is only published when the receiver's own accept path answered
    /// `true`, which is what the hook passes in: a refusal (ignored damage, a god-mode target) is not an
    /// application. `attackKind` is the `player_attack_kind` index of the receive entry, or null for an entry that
    /// set none.</summary>
    public bool DamageApplied(object? damageBase, float amount, object? sourceAgent, int? kind, int? attackKind)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(damageBase, out var life)) return false;
        if (!float.IsFinite(amount) || amount <= 0) return false;
        if (!Begin(life, "damage_applied", out var e, out var transition)) return false;
        var player = _source.ReferenceOf(sourceAgent);
        var attacker = Attacker(sourceAgent, player);
        bool? friendly = sourceAgent == null ? null : player != null ? true : attacker != null ? false : null;
        // Which gear the damage came from is not readable from the receiver at all: it is the weapon provider's
        // own correlation, published for the shooter whose shot is still in flight. A player attacker therefore
        // carries the equipment of that player's latest shot, and every other application — a fall, a fire tick,
        // an enemy's melee — carries none, because no shot of a weapon is behind it.
        var equipment = CombatShotAttribution.EquipmentOf(player);
        PlayerStateContract.WriteDamageApplied(e.Values, life, attacker, amount, kind, friendly, equipment, attackKind);
        var detail = !_registration.Logs(RuntimeLogLevel.Info) ? null
            : (kind is { } named ? "kind=" + PlayerStateContract.DamageKindName(named) : "kind=unnamed")
                + (attackKind is { } attack ? " attack=" + PlayerStateContract.AttackKindName(attack) : "");
        return Raise(e, life, "damage_applied", transition, detail);
    }

    /// <summary>One native health write on a player: `before` is the value the receiver held when the outermost
    /// write was entered, so a write that runs another inside it is one change, published once. `sourceApplies` says
    /// whether the write carries a source at all — a hit and a heal do, a set-health does not — and `sourceAgent` is
    /// that source, named through whichever domain can name it or written as nobody when none can. A write that did
    /// not change the value, or a receiver whose maximum is not positive, publishes nothing.</summary>
    public bool HealthChanged(object? damageBase, float before, object? sourceAgent, bool sourceApplies)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(damageBase, out var life)) return false;
        if (_source.Read(life) is not { } vitals) return false;
        if (!float.IsFinite(before) || !float.IsFinite(vitals.Health) || !float.IsFinite(vitals.Maximum)) return false;
        if (vitals.Maximum <= 0) return false;
        ObserveHealthContinuity(life, before, vitals.Health);
        double delta = vitals.Health - before;
        if (delta == 0) return false;
        var source = sourceApplies ? Attacker(sourceAgent, _source.ReferenceOf(sourceAgent)) : null;
        if (!Begin(life, "health_changed", out var e, out var transition)) return false;
        PlayerStateContract.WriteHealthChanged(e.Values, life, vitals.Health, delta, vitals.Maximum, sourceApplies, source);
        return Raise(e, life, "health_changed", transition);
    }

    /// <summary>How many times a write was entered holding a health this half did not leave it at: something moved the
    /// health between two observed writes, through no entry this half observes (a direct field write or another
    /// patch's own write). It is counted and reported, never published: the change itself was not seen, so no
    /// health-change fact is made up for it.</summary>
    internal long HealthBypassesObserved { get; private set; }

    /// <summary>Compares the health an observed write found with the health the previous observed write left. The
    /// baseline is only valid inside one subscription generation, so a stretch where the health row had no listener
    /// cannot look like a bypass.</summary>
    private void ObserveHealthContinuity(EntityReference life, double before, double after)
    {
        if (!_lives.TryGetValue(life.Id, out var record)) _lives[life.Id] = record = new LifeRecord();
        long generation = _kernel.SubscriptionGeneration;
        if (record.LastHealth is { } known && record.LastHealthGeneration == generation && Math.Abs(known - before) > HealthContinuityTolerance)
        {
            HealthBypassesObserved++;
            ReportOnce("health-bypass:" + life.Id,
                "player-health-bypass-observed: " + life.Id + " held " + before.ToString("R", CultureInfo.InvariantCulture)
                + " when a write was entered but was left at " + known.ToString("R", CultureInfo.InvariantCulture)
                + " by the previous observed write; the change in between came through no observed entry and is not published.");
        }
        record.LastHealth = after;
        record.LastHealthGeneration = generation;
    }

    private const double HealthContinuityTolerance = 1e-3;

    /// <summary>The source a native agent names: a player through this package's own identity, an enemy through
    /// the kind that owns enemies, and nobody when neither can.</summary>
    private EntityReference? Attacker(object? sourceAgent, EntityReference? player)
        => sourceAgent == null ? null : player ?? _kernel.ResolveEntityInstance(EntityKinds.Enemy, sourceAgent);

    /// <summary>One native low-health event. The game posts the event with the player it is about; this half only
    /// publishes it as a fact.</summary>
    public bool LowHealth(object? agent)
    {
        CheckThread();
        if (!Ready()) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        if (!Begin(life, "low_health", out var e, out var transition)) return false;
        PlayerStateContract.WriteLowHealth(e.Values, life);
        return Raise(e, life, "low_health", transition);
    }

    /// <summary>One native infection write: `before` is the value the receiver held when the write was entered,
    /// so the delta is the difference the write really made and never an amount the caller asked for. A write that
    /// did not change the value publishes nothing. `cause` is the write's own effect as its `infection_cause` index;
    /// a value the set does not name is refused and reported, never published as a guess.</summary>
    public bool InfectionChanged(object? damageBase, float before, int cause)
    {
        CheckThread();
        if (!Ready()) return false;
        if (cause < 0 || cause >= PlayerStateContract.InfectionCauseCount)
        {
            ReportOnce("infection-cause:" + cause.ToString(CultureInfo.InvariantCulture),
                "infection write carries an effect the cause set does not name: " + cause.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        if (!ReferenceOf(damageBase, out var life)) return false;
        if (_source.Read(life) is not { } vitals) return false;
        if (!float.IsFinite(before) || !float.IsFinite(vitals.Infection)) return false;
        double delta = vitals.Infection - before;
        if (delta == 0) return false;
        if (!Begin(life, "infection_changed", out var e, out var transition)) return false;
        PlayerStateContract.WriteInfectionChanged(e.Values, life, vitals.Infection, delta, cause);
        return Raise(e, life, "infection_changed", transition);
    }

    // ---------------------------------------------------------------- plumbing

    private bool Ready()
        => !_disposed && _registration.IsRegistered && _source.Authoritative;

    private bool ReferenceOf(object? instance, [NotNullWhen(true)] out EntityReference? reference)
    {
        reference = default;
        if (instance == null) return false;
        _source.Reconcile();
        if (_source.ReferenceOf(instance) is not { } found) return false;
        // The identity's own reconcile is what creates a life, and a reference is only handed out for a life the
        // identity still resolves right now.
        if (_source.Read(found) == null) return false;
        reference = found;
        return true;
    }

    /// <summary>Claims one transition of one fact of one life and begins its event when anyone listens. The
    /// transition number is claimed before the publish: a refused publish must not let the next report reuse an id
    /// the kernel may already have seen. Nothing listening on this fact's binding means the kernel would answer
    /// `no-consumer`, so the event is never begun; the transition is still claimed, because a report seen while
    /// nobody listened is one this half has already made. The gate is the field resolved once above, never a fresh
    /// dictionary lookup, and the event publishes through the binding it names.</summary>
    private bool Begin(EntityReference reference, string fact, out EventWriter e, out long transition)
    {
        if (!_lives.TryGetValue(reference.Id, out var record))
        {
            record = new LifeRecord();
            _lives[reference.Id] = record;
        }
        record.Transitions.TryGetValue(fact, out transition);
        record.Transitions[fact] = transition + 1;
        e = default;
        if (CachedGateOf(fact) is not { HasSubscribers: true } gate) return false;
        e = _registration.BeginEvent(gate);
        return true;
    }

    /// <summary>Publishes one written fact under its own identity: the fact, the world, the life and the
    /// transition <see cref="Begin"/> claimed.</summary>
    private bool Raise(EventWriter e, EntityReference reference, string fact, long transition, string? detail = null)
    {
        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + transition.ToString(CultureInfo.InvariantCulture);
        var answer = e.Raise(eventId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_kernel.WorldEpoch));
        if (answer.Status == "queued")
        {
            // The line is composed only when the session's info gate would write it.
            if (_registration.Logs(RuntimeLogLevel.Info)) Record("player." + fact + " id=" + reference.Id + " transition=" + transition.ToString(CultureInfo.InvariantCulture)
                + (detail == null ? "" : " " + detail));
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
        _once.Report(key, "Player state observation: " + message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player state observation requires the runtime's own simulation thread.");
    }

    /// <summary>Drops this half's per-world tables. The kernel's world epoch is what invalidates the events
    /// already published; this releases the memory of a world that no longer exists. Idempotent.</summary>
    public void BeginWorld()
    {
        CheckThread();
        _lives.Clear();
        _once.Reset();
    }

    public void Dispose()
    {
        CheckThread();
        Detach(this);
        _disposed = true;
        _lives.Clear();
    }
}
