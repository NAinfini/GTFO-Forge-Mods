using ForgeCore;
using System;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The half behind `forge.trigger.interaction.terminal_log_read`: it reads which terminal and which file a native
/// callback was about and hands the three ports to <see cref="TerminalLogPublisher"/>. This half holds the
/// reading only — the file lookup is <see cref="TerminalLogReading"/>, the address is the map-object half's own
/// terminal reader, and the player identity is the player domain's — so a focused test drives the same body the
/// Harmony patches call without a level.
///
/// The instance is owned by the Map plugin session, which owns the registration and the world epoch. `Current`
/// is how a native callback reaches it, because a Harmony patch is static and the session is not; a callback on a
/// peer with no session, or after the session faulted or the world ended, publishes nothing.
///
/// Both of the terminal command interpreter's entries land here, and they are two halves of one read rather than
/// two facts: the machine that received the `READLOG` command runs `ReadLog`, every other machine runs
/// `OnReceiveLogRead` when that command's packet arrives, and the host is the one peer that publishes either
/// way.</summary>
internal sealed class TerminalLogFacts
{
    /// <summary>The live half, or null before the session created it and after it was released.</summary>
    internal static TerminalLogFacts? Current { get; set; }

    /// <summary>The address a terminal reads as right now, or null when it has none. It is a delegate because
    /// the session hands over the map-object half's own terminal reader while a test hands over the terminal's
    /// coordinates, so the mapping from a read to a fact is exercised without a level.</summary>
    internal delegate MapObjectReference? TerminalAddress(LG_ComputerTerminal terminal);

    /// <summary>The player entity one native player instance names, or null when this process tracks no life for
    /// it. The terminal's own synced interaction source is the acting player; the identity module is what turns
    /// that instance into the entity a port may carry.</summary>
    internal delegate EntityReference? PlayerEntity(object? instance);

    private readonly TerminalLogPublisher _publisher;
    private readonly Action<string> _report;
    private readonly TerminalAddress _address;
    private readonly PlayerEntity _players;
    private readonly OnceReporter _once;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly IForgeEngine _kernel;
    private readonly ForgeEngine.GameBindings.WorldFault _fault = new();

    internal TerminalLogFacts(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> ready,
        Action<string> report, TerminalAddress address, PlayerEntity players)
    {
        _ = ready ?? throw new ArgumentNullException(nameof(ready));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _report = report ?? throw new ArgumentNullException(nameof(report)); _once = new OnceReporter(_report);
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        // The terminal's own log table is replicated level state, so the host is the peer that publishes: a
        // client runs the same callbacks and reports one diagnostic instead of a second fact.
        _publisher = new TerminalLogPublisher(kernel, registration, () => ready() && Session.IsHost, report);
        // The read counters are per world: a terminal address of a level that no longer exists names nothing, so
        // the table is dropped when the world changes instead of being carried into the next one.
        _lifecycle = registration.ObserveLifecycle(change =>
        {
            if (change.Kind == RuntimeLifecycleKind.WorldChanged && !change.IsMigration) BeginWorld();
        });
    }


    /// <summary>Whether this half stopped producing after a callback failed.</summary>
    internal bool Faulted => _fault.Holds(_kernel.WorldEpoch);

    /// <summary>Drops the per-world read counters. The session calls this on a world transition, where no
    /// address a counter holds can name anything any more.</summary>
    internal void BeginWorld() => _publisher.BeginWorld();

    /// <summary>The machine that received the `READLOG` command read one file. The argument is the name the
    /// player typed; the file this row reports is the table's own record of it, so a read of a log whose name
    /// differs only in case reports the authored name.</summary>
    internal void Read(LG_ComputerTerminalCommandInterpreter interpreter, string? argument)
        => Publish(interpreter, TerminalLogReading.ByCommandArgument(Terminal(interpreter), argument));

    /// <summary>The machine that received the read packet read one file: the packet carries the file's content
    /// id and nothing else, so this is the same lookup the local entry ends in.</summary>
    internal void Received(LG_ComputerTerminalCommandInterpreter interpreter, uint id)
        => Publish(interpreter, TerminalLogReading.ById(Terminal(interpreter), id), id);

    /// <summary>One read, from the file it named to the fact about it. A read whose terminal has no address, or
    /// whose file did not resolve, publishes nothing and reports the cause once instead of publishing a fact
    /// about an object or a file this provider did not read.</summary>
    private void Publish(LG_ComputerTerminalCommandInterpreter interpreter,
        (string? Name, string? Refusal) reading, uint? contentId = null)
    {
        var terminal = Terminal(interpreter);
        if (reading.Refusal is { } refusal)
        {
            if (_once.Count("reading:" + refusal) == 0)
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.TerminalLogRefused, refusal,
                    RuntimeLogValue.Quote("reason", refusal),
                    RuntimeLogValue.Of("contentId", (long)(contentId ?? 0)));
            ReportOnce("reading:" + refusal, "terminal log read not published: " + refusal + ".");
            return;
        }
        if (terminal == null || _address(terminal) is not { } address)
        {
            ReportOnce("address", "terminal log read not published: the terminal reports none of the address keys "
                + "its own reader builds an address from.");
            return;
        }
        _publisher.LogRead(address, reading.Name!, _players(TerminalLogReading.Actor(terminal)));
    }

    /// <summary>The terminal one command interpreter belongs to. A callback whose interpreter is gone reads no
    /// terminal at all, which every lookup refuses by name.</summary>
    private static LG_ComputerTerminal? Terminal(LG_ComputerTerminalCommandInterpreter? interpreter)
        => interpreter == null || interpreter.WasCollected ? null : interpreter.m_terminal;

    /// <summary>Runs one callback body. A body that throws stops this half instead of letting the exception
    /// escape into the game's own command path, which is what the package's other fact halves do.</summary>
    internal void Guard(Action<TerminalLogFacts> callback)
    {
        if (Faulted) return;
        try { callback(this); }
        catch (Exception error)
        {
            _fault.Mark(_kernel.WorldEpoch);
            _report("terminal-log-fact-exception (disabled for this level): " + error.GetType().Name);
        }
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    /// <summary>Releases the lifecycle subscription the read counters are dropped through. The session owns this
    /// half and disposes it before the registration it published on goes away.</summary>
    public void Dispose()
    {
        _lifecycle.Dispose();
        _once.Reset();
    }
}
