using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using Player;

namespace ForgePlayer.Native;

/// <summary>The publication side of the player's own events: a supply use and a ping. Each one is one
/// game event about the player who did it, so each row publishes one fact about one life, under the same rules the
/// player-life and player-state halves publish under (host only, one transition per event id, a required port that
/// cannot be read publishes nothing).
///
/// Two properties of these two rows are worth stating where the code is:
/// <list type="bullet">
/// <item>Both are observed from the game's own event funnel, which every machine runs for the events it
/// produced. Publishing is host-only: a client sends its own player's event through <see cref="PlayerInputReport"/>, and the
/// host's hook stays silent for a client's player, so one event is one fact whichever entries also reach the host.</item>
/// <item>A ping is observed from two entries — the game's own `player_ping` event and the marker call the game
/// makes with the pinging agent and the world position — and the two are de-duplicated per life, per tick and per
/// position, so a machine that sees both publishes one fact.</item>
/// </list></summary>
internal sealed class PlayerEventFacts
{
    private const string Prefix = EntityKinds.Player + ":";

    /// <summary>The distance under which two pings of one life in one tick are the same ping. The two entries the
    /// game reaches for one ping carry the same world position bit for bit, so this only guards against a
    /// re-encoded position; it is not a smoothing radius.</summary>
    internal const double PingDeduplicationRadius = 0.001;

    private sealed class LifeRecord
    {
        internal readonly Dictionary<string, long> Transitions = new(StringComparer.Ordinal);
        internal long PingTick = -1;
        internal double[]? PingPosition;
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

    internal static PlayerEventFacts? Installed { get; private set; }

    internal PlayerEventFacts(RuntimeModuleHandle registration, IForgeEngine kernel, NativePlayerVitals source,
        Action<string> log, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }

    internal static PlayerEventFacts Attach(RuntimeModuleHandle registration, IForgeEngine kernel,
        NativePlayerVitals source, Action<string> log, Action<string> report)
    {
        var facts = new PlayerEventFacts(registration, kernel, source, log, report);
        facts.BeginWorld();
        Installed = facts;
        return facts;
    }

    internal static void Detach(PlayerEventFacts facts)
    {
        if (ReferenceEquals(Installed, facts)) Installed = null;
    }

    // The subscription gates of the rows this half publishes, resolved once through the same binding mapping the
    // publish uses and kept as fields rather than looked up again on every publish.
    private RuntimeSubscriptionGate? _supplyUsedGate, _pingGate, _movementStateChangedGate, _inputHeldGate, _keyPressedGate, _firePressedGate,
        _staminaReadGate;
    private bool _gatesResolved;

    private void ResolveGates()
    {
        if (_gatesResolved) return;
        _staminaReadGate = _gates.TryGetValue(PlayerStaminaContract.ValueBindingId, out var stamina) ? stamina : null;
        _supplyUsedGate = GateOf("supply_used");
        _pingGate = GateOf("ping");
        _movementStateChangedGate = GateOf("movement_state_changed");
        _inputHeldGate = GateOf("input_held");
        _keyPressedGate = GateOf("key_pressed");
        _firePressedGate = GateOf("fire_pressed");
        _gatesResolved = true;
    }

    private RuntimeSubscriptionGate? GateOf(string fact)
        => _gates.TryGetValue(PlayerEventContract.BindingOf(fact), out var gate) ? gate : null;

    internal bool PingObserved { get { ResolveGates(); return _pingGate is { HasSubscribers: true }; } }
    internal bool MovementObserved { get { ResolveGates(); return _movementStateChangedGate is { HasSubscribers: true }; } }
    internal bool HoldObserved { get { ResolveGates(); return _inputHeldGate is { HasSubscribers: true }; } }
    /// <summary>A loaded plan reads a player's stamina (`forge.query.player.stamina`); every machine loads the same plans.</summary>
    internal bool StaminaRead { get { ResolveGates(); return _staminaReadGate is { HasSubscribers: true }; } }

    public bool SupplyUsed(object? agent, string kind)
    {
        CheckThread();
        ResolveGates();
        if (_supplyUsedGate is not { HasSubscribers: true } || !Ready("supply_used")) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        string member;
        try { member = PlayerEventContract.SupplyKind(kind); }
        catch (RuntimeContractException error) { ReportOnce("supply:" + kind, error.Message); return false; }
        var e = _registration.BeginEvent(_supplyUsedGate);
        PlayerEventContract.WriteSupplyUsed(e.Values, life, member);
        return Publish(life, "supply_used", e);
    }

    /// <summary>One ping. The position is required, so a ping whose position could not be read publishes nothing.</summary>
    public bool Ping(object? agent, double[]? position)
    {
        CheckThread();
        if (!PingObserved || !Ready("ping")) return false;
        if (position == null || position.Length != 3 || !Finite(position)) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        if (IsRepeat(life, position)) return false;
        var e = _registration.BeginEvent(_pingGate!);
        PlayerEventContract.WritePing(e.Values, life, position);
        return Publish(life, "ping", e);
    }

    /// <summary>One posture change. The two members are read from the machine itself, before and after its own
    /// state entry ran, so a call that changed nothing publishes nothing and the pair is never derived from the
    /// argument the caller was handed. A member outside the declared set refuses the fact by name rather than
    /// being published as the nearest posture.</summary>
    public bool MovementStateChanged(object? agent, int state, int previous)
    {
        CheckThread();
        if (!MovementObserved || !Ready("movement_state_changed")) return false;
        if (state == previous) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        var e = _registration.BeginEvent(_movementStateChangedGate!);
        try { PlayerEventContract.WriteMovementStateChanged(e.Values, life, state, previous); }
        catch (RuntimeContractException error)
        {
            ReportOnce("movement:" + state + ":" + previous, error.Message);
            return false;
        }
        return Publish(life, "movement_state_changed", e);
    }

    /// <summary>One timed interaction that reached its trigger. The duration is the interaction's own, in
    /// seconds, and it is converted to the runtime's ticks once, here, where the payload is built, at the rate the
    /// running world reported (contract §3.2 时间单位). A duration that is not a finite, non-negative number
    /// publishes nothing: a held time reported as zero reads exactly like an interaction with no hold at all.</summary>
    public bool InputHeld(object? agent, double durationSeconds)
    {
        CheckThread();
        if (!HoldObserved || !Ready("input_held")) return false;
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        long ticks = (long)Math.Round(durationSeconds * _kernel.TicksPerSecond, MidpointRounding.AwayFromZero);
        var e = _registration.BeginEvent(_inputHeldGate!);
        PlayerEventContract.WriteInputHeld(e.Values, life, ticks);
        return Publish(life, "input_held", e);
    }

    /// <summary>One edge of one key the local player pressed or released. The caller is the poller that read
    /// `UnityEngine.Input.GetKeyDown` / `GetKeyUp` for the keys the loaded plans subscribe to, and it hands the
    /// native local agent, the `key_code` member index of the key it read and the `key_phase` member index of the
    /// edge. This half keeps the same host-only gate every other fact of the family carries: a client's edge reaches it
    /// on the host, through <see cref="PlayerInputReport"/>, for that client's own player.</summary>
    public bool FirePressed(object? agent)
    {
        CheckThread();
        ResolveGates();
        if (_firePressedGate is not { HasSubscribers: true } || !Ready("fire_pressed")) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        var e = _registration.BeginEvent(_firePressedGate);
        PlayerEventContract.WriteFirePressed(e.Values, life);
        return Publish(life, "fire_pressed", e);
    }

    public bool KeyPressed(object? agent, int key, int phase)
    {
        CheckThread();
        ResolveGates();
        if (_keyPressedGate is not { HasSubscribers: true } || !Ready("key_pressed")) return false;
        if (!ReferenceOf(agent, out var life)) return false;
        var e = _registration.BeginEvent(_keyPressedGate);
        try { PlayerEventContract.WriteKeyPressed(e.Values, life, key, phase); }
        catch (RuntimeContractException error) { ReportOnce("key:" + key + ":" + phase, error.Message); return false; }
        return Publish(life, "key_pressed", e);
    }

    private bool IsRepeat(EntityReference life, double[] position)
    {
        var record = Record(life);
        long tick = Math.Max(0, _kernel.CurrentTick);
        bool repeat = record.PingTick == tick && record.PingPosition is { } prior
            && Distance(prior, position) <= PingDeduplicationRadius;
        record.PingTick = tick;
        record.PingPosition = position;
        return repeat;
    }

    private static bool Finite(double[] position)
        => double.IsFinite(position[0]) && double.IsFinite(position[1]) && double.IsFinite(position[2]);

    private static double Distance(double[] left, double[] right)
    {
        double dx = left[0] - right[0], dy = left[1] - right[1], dz = left[2] - right[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private bool Ready(string fact)
    {
        if (_disposed || !_registration.IsRegistered) return false;
        if (_source.Authoritative) return true;
        ReportOnce("authority:" + fact, "player-event-host-observation-unavailable: " + fact + "; only an observed host entry can publish this fact.");
        return false;
    }

    private bool ReferenceOf(object? instance, [NotNullWhen(true)] out EntityReference? reference)
    {
        reference = default;
        if (instance == null) return false;
        _source.Reconcile();
        if (_source.ReferenceOf(instance) is not { } found) return false;
        if (_source.Read(found) == null) return false;
        reference = found;
        return true;
    }

    private LifeRecord Record(EntityReference reference)
    {
        if (!_lives.TryGetValue(reference.Id, out var record))
        {
            record = new LifeRecord();
            _lives[reference.Id] = record;
        }
        return record;
    }

    /// <summary>Raises one written event. The row's own gate was checked before native identity reads, deduplication
    /// and writing, and the event was begun on that gate, so it publishes through the binding the gate names.</summary>
    private bool Publish(EntityReference reference, string fact, EventWriter e)
    {
        var record = Record(reference);
        record.Transitions.TryGetValue(fact, out long transition);
        record.Transitions[fact] = transition + 1;
        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + transition.ToString(CultureInfo.InvariantCulture);
        var answer = e.Raise(eventId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_kernel.WorldEpoch));
        if (answer.Status == "queued")
        {
            // The line is composed only when the session's info gate would write it.
            if (_registration.Logs(RuntimeLogLevel.Info)) Record("player." + fact + " id=" + reference.Id + " transition=" + transition.ToString(CultureInfo.InvariantCulture));
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
        _once.Report(key, "Player event observation: " + message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player event observation requires the runtime's own simulation thread.");
    }

    public void BeginWorld()
    {
        CheckThread();
        _lives.Clear();
        _once.Reset();
        Record("player-health-observation-coverage-incomplete: direct health writes outside the observed native health entries do not produce a health-change fact; a later observed write that finds a different health reports player-health-bypass-observed.");
    }

    public void Dispose()
    {
        CheckThread();
        Detach(this);
        _disposed = true;
        _lives.Clear();
    }
}

/// <summary>The seam the funnel hook runs through, with one typed entry per row so a hook body names the fact it
/// publishes rather than shaping a payload. Every entry runs on the session that owns the registration the fact
/// is published through: a session that failed before it created the half runs none of them, and a callback that
/// fails disables the session exactly as the other readbacks do.</summary>
internal static class PlayerEventHooks
{
    internal static void LowHealth(PlayerStateFacts? state, PlayerAgent player)
    {
        if (state == null || PlayerInputReport.Current?.LowHealth(player) == true) return;
        _ = state.LowHealth(player);
    }

    internal static void SupplyUsed(PlayerEventFacts? events, PlayerAgent player, string kind)
    {
        if (events == null || PlayerInputReport.Current?.SupplyUsed(player, kind) == true) return;
        _ = events.SupplyUsed(player, kind);
    }

    internal static void Ping(PlayerEventFacts? events, PlayerAgent player, double[]? position)
    {
        if (events == null || PlayerInputReport.Current?.Ping(player, position) == true) return;
        _ = events.Ping(player, position);
    }

    internal static void MovementStateChanged(PlayerEventFacts? events, PlayerAgent player, int state, int previous)
    {
        if (events == null || PlayerInputReport.Current?.Movement(player, state, previous) == true) return;
        _ = events.MovementStateChanged(player, state, previous);
    }

    internal static void InputHeld(PlayerEventFacts? events, PlayerAgent player, double durationSeconds)
    {
        if (events == null || PlayerInputReport.Current?.Held(player, durationSeconds) == true) return;
        _ = events.InputHeld(player, durationSeconds);
    }

    internal static void FirePressed(PlayerEventFacts? events, PlayerAgent? player)
    {
        if (events != null && player != null) _ = events.FirePressed(player);
    }

    internal static void KeyPressed(PlayerEventFacts? events, PlayerAgent player, int key, int phase)
    {
        if (events == null) return;
        _ = events.KeyPressed(player, key, phase);
    }
}
