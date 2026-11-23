using ForgeCore;
using System;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The Map provider's session half: the one trigger this provider publishes about the expedition itself
/// rather than about a map object. A map object is addressed and observed per instance; an expedition ends once
/// per world, and the fact carries nothing but which of the game's three ends it was.
///
/// Publication is deliberately narrow. Only the host publishes, only a world the kernel already started accepts
/// the event, and the event id is the trigger's own id plus the world epoch and the state, so a repeated native
/// report of one end is the kernel's own `duplicate` answer instead of a second dispatch. An end state this
/// provider has no outcome for is reported once and publishes nothing rather than being folded into a state it
/// is not.
///
/// This half publishes through the registration the Map provider already owns: one provider, one registration,
/// so the session half is composed on it instead of opening a second one.</summary>
internal sealed class ExpeditionModule : IDisposable
{
    private readonly RuntimeModuleHandle _registration;
    /// <summary>The row's subscription gate, resolved once here; the kernel refreshes it in place. A registration
    /// that does not declare the binding has no gate and nothing is published for it (a binding withdrawn with its feature is not declared).</summary>
    private readonly RuntimeSubscriptionGate? _endedGate;
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _authority;
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public ExpeditionModule(RuntimeModuleHandle registration, IForgeEngine kernel, Func<bool> authority, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _endedGate = registration.SubscriptionGates().TryGetValue(ExpeditionContract.EndedBinding, out var gate) ? gate : null;
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }

    /// <summary>One end of the expedition the native side reported, as the game's own `ExpeditionEndState`
    /// value. The answer is the kernel's own: `queued` when a plan's mounts claimed the event, `duplicate` when
    /// this same end was already published in this world, `ignored` when no plan is mounted on this world, and
    /// null when this half did not publish at all (a client, an unregistered provider, or a state that names no
    /// outcome).</summary>
    public DispatchResult? Ended(int endState)
    {
        CheckThread();
        if (_disposed || !_registration.IsRegistered) return null;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return null;
        // Nobody subscribes to the expedition's own row: the kernel would answer `no-consumer` for the event this
        // call would build, so it is never built.
        if (_endedGate is not { HasSubscribers: true }) return null;
        if (!_authority())
        {
            ReportOnce("client", "expedition end observed on a non-authoritative peer: the host publishes the "
                + "expedition's own facts, a client does not.");
            return null;
        }
        if (ExpeditionContract.Outcome(endState) is not { } outcome)
        {
            ReportOnce("end-state:" + endState.ToString(CultureInfo.InvariantCulture),
                "expedition end state " + endState.ToString(CultureInfo.InvariantCulture) + " names no execution "
                + "outcome this provider publishes; the event is not sent.");
            return null;
        }
        // The one value output carries the outcome's member index, the wire form of an enum port.
        var raised = _registration.BeginEvent(_endedGate);
        raised.Values.Enum(Blocks.TriggerSessionExpeditionEnded.OutOutcome, outcome);
        var result = raised.Raise(ExpeditionContract.EventId(_kernel.WorldEpoch, endState), _kernel.WorldEpoch,
            Math.Max(0, _kernel.CurrentTick), Scope());
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return result;
    }

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "The expedition trigger requires the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        _disposed = true;
        _once.Reset();
    }
}
