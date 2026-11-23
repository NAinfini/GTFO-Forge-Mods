using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Publishes the door and terminal interaction facts this slice adds, and nothing else. The publisher is
/// handed the one Map registration by the session and answers with whether the kernel queued the event, so the
/// native callbacks stay one line each and a test can drive the same body without a Harmony patch.
///
/// Every fact carries an event id built from the same three things the package's existing map-object facts use —
/// the world, the subject's own address and the subject's transition number — so two facts about one door are
/// two events and a repeated sync of one state is none. The state is what the transition counts: it is the native
/// values the fact reports, so a callback that fires twice for one state publishes once.</summary>
internal sealed class DoorTerminalPublisher
{
    /// <summary>The fact kind the terminal command row carries. It is the catalog's existing
    /// `forge.trigger.interaction.terminal_command` row, whose shape this provider already declares. The name is
    /// the map-object module's own fact kind rather than a second spelling of it — the module is a different
    /// assembly and names it `internal`, so this half states the same text beside the capability id it belongs
    /// to, and a test asserts the two agree.</summary>
    internal const string CommandFact = "terminal_command";

    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly DoorTerminalFactLedger _ledger = new();
    private readonly OnceReporter _once;
    private readonly Row _scan, _broken, _lock, _noise, _command;
    private long _commandSequence;

    /// <summary>One published row: its fact, its binding and its subscription gate, resolved once at attach. The
    /// kernel refreshes the gate in place wherever the subscription table changes, so a native callback with nothing
    /// to say to anyone reads one field instead of building an event the kernel would refuse as `no-consumer`. A
    /// registration that does not declare the binding has no gate and nothing is published for it (a binding withdrawn with its feature is not declared).</summary>
    private sealed class Row
    {
        internal Row(IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates, string fact)
        {
            Fact = fact;
            Binding = DoorTerminalEventContract.Binding(fact);
            Gate = gates.TryGetValue(Binding, out var gate) ? gate : null;
        }

        internal readonly string Fact;
        internal readonly string Binding;
        internal readonly RuntimeSubscriptionGate? Gate;
        internal bool Observed => Gate is { HasSubscribers: true };
    }

    internal DoorTerminalPublisher(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> authority,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report)); _once = new OnceReporter(_report);
        var gates = registration.SubscriptionGates();
        _scan = new Row(gates, DoorTerminalEventContract.DoorScanFact);
        _broken = new Row(gates, DoorTerminalEventContract.DoorBrokenFact);
        _lock = new Row(gates, DoorTerminalEventContract.LockBrokenFact);
        _noise = new Row(gates, DoorTerminalEventContract.DoorNoiseFact);
        _command = new Row(gates, CommandFact);
    }

    /// <summary>Drops the per-world state. A world transition invalidates every address a key could name, so
    /// the ledger is emptied rather than kept.</summary>
    internal void BeginWorld() { _ledger.Clear(); _commandSequence = 0; _repeats = 0; }

    /// <summary>Forgets the scan state published for one door. A scan put back to idle without a fact of its own (the
    /// test fixture deactivating the chain) would otherwise leave "started" remembered, and the next real start of the
    /// same scan would be taken for a repeat and publish nothing.</summary>
    internal void ForgetScan(MapObjectReference address) => _ledger.Forget(new DoorTerminalSubject(_scan.Fact, address, null));

    private long _repeats;

    /// <summary>A reading the ledger took for a repeat of the state it already published, on a row somebody listens
    /// to. Written at the 1st, 10th, 100th ... repeat of the world so a callback that fires every frame cannot flood.</summary>
    private void NoteRepeat(Row row, in DoorTerminalSubject subject)
    {
        var count = ++_repeats;
        var power = 1L;
        while (power < count) power *= 10;
        if (count != power || !MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.DoorTerminalFactRepeated,
            "A door or terminal fact reading equal to the one already published was not published again.",
            RuntimeLogValue.Of("fact", row.Fact), RuntimeLogValue.Of("subject", subject.Address?.ToString() ?? subject.Entity ?? ""),
            RuntimeLogValue.Of("count", count));
    }

    /// <summary>One scan transition. The phase is the transition and the status is the native value the phase
    /// was derived from, so a plan can read either.</summary>
    internal bool Scan(MapObjectReference address, int phase, int status, EntityReference? instigator = null, int? scanIndex = null)
    {
        var subject = new DoorTerminalSubject(_scan.Fact, address, null);
        // A door's status may be written after its first core activates. It is a reported value, not another
        // activation of the same whole-chain step.
        var started = phase == DoorTerminalDerivations.ScanPhase(DoorScanStage.Activated, false);
        if (!Admit(_scan, subject, new FactReading(null, phase, started ? 0 : status), out long transition, scanIndex)) return false;
        var raised = _registration.BeginEvent(_scan.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionDoorScan.OutDoor, Reference(address));
        values.Enum(Blocks.TriggerInteractionDoorScan.OutPhase, phase);
        values.Entity(Blocks.TriggerInteractionDoorScan.OutActor, instigator);
        values.Enum(Blocks.TriggerInteractionDoorScan.OutStatus, status);
        if (scanIndex is { } scan) values.Integer(Blocks.TriggerInteractionDoorScan.OutScanIndex, scan);
        else values.Null(Blocks.TriggerInteractionDoorScan.OutScanIndex);
        return Raise(raised, _scan, MapObjectText(address), transition).Status == "queued";
    }

    /// <summary>One stage of a weak door: the door's own runtime entity, the zone it stands in and its world
    /// position, plus the attacker when the replication callback named one. The zone is published as the `zone`
    /// resource reference every other zone reader of this package answers with, and the door entity's namespace
    /// is the weak door's own, because a weak door has no map address to be named by. `health` and `healthMax` are
    /// the door's own destruction component's durability after the callback (`LG_WeakDoor_Destruction.m_health` /
    /// `m_healthMax`); a door whose component does not read publishes both ports absent.</summary>
    internal bool WeakDoor(EntityReference door, string phase, ResourceRef? zone, double[]? position,
        EntityReference? attacker, double? health, double? healthMax)
    {
        int index = DoorTerminalEventContract.DoorPhaseIndex(phase);
        if (index < 0) return false;
        var subject = new DoorTerminalSubject(_broken.Fact, null, door.Id);
        if (!Admit(_broken, subject, new FactReading(phase), out long transition)) return false;
        // The optional ports a door does not read are left unwritten, so the event carries them absent.
        var raised = _registration.BeginEvent(_broken.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionDoorBroken.OutDoor, door);
        values.Enum(Blocks.TriggerInteractionDoorBroken.OutPhase, index);
        if (zone is { } resource) values.Resource(Blocks.TriggerInteractionDoorBroken.OutZone, resource);
        if (position is { Length: 3 } point) values.Vector(Blocks.TriggerInteractionDoorBroken.OutPosition, point[0], point[1], point[2]);
        if (attacker is { } player) values.Entity(Blocks.TriggerInteractionDoorBroken.OutAttacker, player);
        if (health is { } left) values.Number(Blocks.TriggerInteractionDoorBroken.OutHealth, left);
        if (healthMax is { } most) values.Number(Blocks.TriggerInteractionDoorBroken.OutHealthMax, most);
        return Raise(raised, _broken, door.Id, transition).Status == "queued";
    }

    /// <summary>One lock release. The cause names the lock kind too: a melee lock is smashed, a hackable one hacked.</summary>
    internal bool LockBroken(MapObjectReference address, string cause)
    {
        var subject = new DoorTerminalSubject(_lock.Fact, address, null);
        if (!Admit(_lock, subject, new FactReading(cause), out long transition)) return false;
        var raised = _registration.BeginEvent(_lock.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionLockBroken.OutDoor, Reference(address));
        values.Enum(Blocks.TriggerInteractionLockBroken.OutCause, DoorTerminalEventContract.LockCauseIndex(cause));
        return Raise(raised, _lock, MapObjectText(address), transition).Status == "queued";
    }

    /// <summary>One completed native noise submission from an authored weak door. This is an occurrence rather
    /// than persistent state, so two identical submissions remain two facts. The numeric noise kind is the
    /// native <c>NM_NoiseType</c> value carried by that call; no enemy-hearing result is inferred.</summary>
    internal bool DoorNoise(MapObjectReference address, double[] position, double radiusMin, double radiusMax,
        int noiseType)
    {
        var subject = new DoorTerminalSubject(_noise.Fact, address, null);
        if (!Admit(_noise, subject, new FactReading(noiseType.ToString(CultureInfo.InvariantCulture),
                radiusMin, radiusMax), out long transition)) return false;
        var raised = _registration.BeginEvent(_noise.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionDoorNoise.OutDoor, Reference(address));
        if (position is { Length: 3 }) values.Vector(Blocks.TriggerInteractionDoorNoise.OutPosition, position[0], position[1], position[2]);
        values.Number(Blocks.TriggerInteractionDoorNoise.OutRadiusMin, radiusMin);
        values.Number(Blocks.TriggerInteractionDoorNoise.OutRadiusMax, radiusMax);
        values.Enum(Blocks.TriggerInteractionDoorNoise.OutNoiseType, noiseType);
        return Raise(raised, _noise, MapObjectText(address), transition).Status == "queued";
    }

    /// <summary>One terminal command the terminal accepted. Unique commands use the interpreter's registered name.
    /// The actor is the current synchronized terminal user, or absent when that native source is unavailable.</summary>
    internal DispatchResult? TerminalCommand(MapObjectReference address, int command, string? registeredName,
        EntityReference? actor = null)
    {
        if (!_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready || !_authority()) return null;
        if (MapObjectTerminalCommand.IsUnique(command) && string.IsNullOrEmpty(registeredName))
        {
            _report("compat." + MapObjectTerminalCommand.NameUnavailable + ": " + address + "; command=" + command);
            return MapObjectTerminalCommand.RejectName(_registration, _command.Binding, address, command, _kernel.WorldEpoch);
        }
        string name = MapObjectTerminalCommand.Name(command, registeredName);
        var subject = new DoorTerminalSubject(_command.Fact, address, null);
        // Each received command is an occurrence, including two consecutive executions of the same name.
        if (!Admit(_command, subject, new FactReading(name + ":" + (++_commandSequence).ToString(CultureInfo.InvariantCulture), command), out long transition)) return null;
        var raised = _registration.BeginEvent(_command.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionTerminalCommand.OutTerminal, Reference(address));
        if (actor is { } player) values.Entity(Blocks.TriggerInteractionTerminalCommand.OutActor, player);
        values.Text(Blocks.TriggerInteractionTerminalCommand.OutCommand, name);
        return Raise(raised, _command, MapObjectText(address), transition);
    }

    /// <summary>Whether one state of one object is a fact to build. The authority check and the ledger come first:
    /// a repeated state is no fact, and a state seen while nobody listened is still remembered, because it is a
    /// fact this publisher has already reported. A row nobody listens to then stops before any event value is
    /// built. Nothing on this path allocates once the object has been seen.</summary>
    private bool Admit(Row row, in DoorTerminalSubject subject, in FactReading state, out long transition, int? scanIndex = null)
    {
        transition = 0;
        if (!_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client:" + row.Fact,
                "door or terminal fact observed on a non-authoritative peer: the host publishes map-object state.");
            return false;
        }
        if (!_ledger.Observe(subject, state, out transition, scanIndex))
        {
            if (row.Observed) NoteRepeat(row, subject);
            return false;
        }
        return row.Observed;
    }

    /// <summary>Publishes one admitted fact, its outputs already written, under the transition <see cref="Admit"/>
    /// gave it. The subject text is the entity id the object is named by — kind included — so the published event
    /// id and the reference a plan reads back are one spelling of one thing.</summary>
    private DispatchResult Raise(EventWriter raised, Row row, string subjectText, long transition)
    {
        var result = raised.Raise(EventId(row.Fact, subjectText, transition), _kernel.WorldEpoch,
            Math.Max(0, _kernel.CurrentTick), Scope());
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return result;
    }

    private static string MapObjectText(MapObjectReference address) => EntityKinds.MapObject + ":" + address;

    /// <summary>The event id is the fact's own identity: the kind of the subject, the fact, the world, the
    /// subject's own text and the subject's transition number, which is the same rule the package's other
    /// map-object facts follow. The kind is read off the subject rather than hard-coded, because a weak door
    /// belongs to its own namespace: an event id that spelled the map-object kind for a weak door would name a
    /// map object the world does not have.</summary>
    private string EventId(string fact, string subject, long transition)
        => KindOf(subject) + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + subject + ":" + transition.ToString(CultureInfo.InvariantCulture);

    /// <summary>The kind part of one subject's own text: everything before the first colon, which is how every
    /// entity id in this runtime spells its kind.</summary>
    private static string KindOf(string subject)
    {
        int colon = subject.IndexOf(':');
        return colon > 0 ? subject[..colon] : subject;
    }

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>The entity reference a port carries for one address in this world. It is built from the address
    /// and the world the fact was published in, exactly as the module's own map-object entity ids are, so a
    /// reference a plan reads back is the reference the kernel resolves.</summary>
    private EntityReference Reference(MapObjectReference address)
        => new(EntityKinds.MapObject + ":" + address, _kernel.WorldEpoch, 1);

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }
}
