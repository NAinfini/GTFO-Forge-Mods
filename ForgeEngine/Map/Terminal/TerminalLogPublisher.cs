using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Publishes the terminal log read fact and nothing else. It is the event-building half of
/// `forge.trigger.interaction.terminal_log_read`: the native callbacks report which terminal was read, which of
/// its files was read and which player read it, and everything that is a decision about publishing — the
/// authority gate, the event identity, the ports and the refusal reasons — lives here, where a focused test
/// drives it without a Harmony patch.
///
/// A read is an instantaneous event, so nothing here is deduplicated by state: reading one file twice is two
/// facts and each carries its own transition number. The number is the count of reads this world published for
/// that terminal, which makes the event id unique per read without this half remembering what was read — a
/// world transition drops the counters, and the kernel's own epoch is what makes an earlier world's ids
/// unresolvable anyway.</summary>
internal sealed class TerminalLogPublisher
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    /// <summary>The row's subscription gate, resolved once here; the kernel refreshes the gate in place.
    /// A registration that does not declare the binding has no gate and nothing is published for it (a binding withdrawn with its feature is not declared).</summary>
    private readonly RuntimeSubscriptionGate? _gate;
    private readonly Func<bool> _authority;
    private readonly Dictionary<string, long> _reads = new(StringComparer.Ordinal);
    private readonly OnceReporter _once;

    public TerminalLogPublisher(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> authority,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gate = registration.SubscriptionGates().TryGetValue(TerminalLogContract.BindingId, out var gate) ? gate : null;
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }


    /// <summary>How many terminals this world counted reads for, for a case to assert a world transition
    /// released them.</summary>
    internal int Tracked => _reads.Count;

    /// <summary>Drops the per-world counters. A world transition invalidates every address a count could name,
    /// so the counters are emptied rather than kept.</summary>
    public void BeginWorld()
    {
        if (_reads.Count != 0) _reads.Clear();
    }

    /// <summary>One log read: the terminal it happened at, the file's own authored name and the player who read
    /// it. The file name is the one the terminal's own log table carries and never a spelling derived from the
    /// command line, and the actor is required because the catalog's own row declares it: a read this provider
    /// cannot attribute publishes nothing and says why instead of publishing a fact a plan would read as an
    /// anonymous read.</summary>
    public bool LogRead(MapObjectReference terminal, string file, EntityReference? actor)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (string.IsNullOrEmpty(file))
        {
            ReportOnce("file-missing", "terminal log read not published: the read file's own name did not read, so "
                + "the fact has no file to report.");
            return false;
        }
        if (actor is not { } reader)
        {
            ReportOnce("actor-missing", "terminal log read not published: the terminal reports no synced "
                + "interacting player, so the read cannot be attributed and the row's required actor port would "
                + "be missing.");
            return false;
        }
        if (!_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client", "terminal log read observed on a non-authoritative peer: the host publishes "
                + "terminal state, a client does not.");
            return false;
        }
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this
        // call would build, so neither the address text nor the event value is built. The read counter only numbers
        // reads that were offered, so skipping it keeps every id unique.
        if (_gate is not { HasSubscribers: true } gate) return false;
        string address = terminal.ToString();
        long read = _reads.TryGetValue(address, out var previous) ? previous + 1 : 1;
        _reads[address] = read;
        var raised = _registration.BeginEvent(gate);
        raised.Values.Entity(Blocks.TriggerInteractionTerminalLogRead.OutTerminal, Reference(terminal));
        raised.Values.Text(Blocks.TriggerInteractionTerminalLogRead.OutFile, file);
        raised.Values.Entity(Blocks.TriggerInteractionTerminalLogRead.OutActor, reader);
        var result = raised.Raise(EventId(address, read), _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope());
        if (result.Status == "queued") return true;
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return false;
    }

    /// <summary>The event id of one read: the map-object namespace, the fact's own kind, the world, the
    /// terminal's own address and the read's transition number. The number is what makes two reads of one file
    /// two events.</summary>
    private string EventId(string address, long read)
        => EntityKinds.MapObject + "." + TerminalLogContract.Fact + ":"
            + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":" + address + ":"
            + read.ToString(CultureInfo.InvariantCulture);

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>The entity reference a port carries for one address in this world: the address text in the one
    /// `gtfo.map_object` namespace, carrying the world it was read in and the one life an addressed object has.
    /// It is the same reference the map-object module publishes for that address, so a plan reads one
    /// identity.</summary>
    private EntityReference Reference(MapObjectReference address)
        => new(EntityKinds.MapObject + ":" + address, _kernel.WorldEpoch, 1);

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }
}
