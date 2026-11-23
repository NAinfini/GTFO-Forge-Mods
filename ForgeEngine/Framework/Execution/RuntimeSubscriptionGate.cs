using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>
/// Whether anything loaded consumes one binding. For a trigger binding that is a plan mounted on it (or a `g-wait`
/// or an `end_on` naming it); for a `query` binding it is a loaded plan with a `query` step that reads it — which is
/// what lets a provider skip keeping the state such a read is answered from while no plan can ask for it. The answer
/// is precomputed: it changes only when plans or modules change, and the kernel refreshes it in the same place it
/// rebuilds the subscriptions. A provider that holds its own gate therefore answers "is anyone listening" with one
/// field read, before it builds the event value or records the state and before the kernel is reached at all.
/// </summary>
public sealed class RuntimeSubscriptionGate
{
    internal RuntimeSubscriptionGate(string bindingId) { BindingId = bindingId; }
    public string BindingId { get; }
    public bool HasSubscribers { get; internal set; }
    /// <summary>The kernel this gate was resolved in, the registered binding it names and the entries mounted on it:
    /// kept with the gate where the registry and the subscription table change, so writing and publishing an event
    /// of the binding looks nothing up by name. <see cref="Binding"/> is null once the binding left the registry.</summary>
    internal RuntimeKernel? Kernel;
    internal RuntimeKernel.BindingView? Binding;
    /// <summary>The kernel's own subscription object for the binding, or null while no plan is mounted on it.</summary>
    internal object? Subscription;
    /// <summary>The answer sheet an event or a committed fact of this binding is written on
    /// (<see cref="RuntimeModuleHandle.BeginEvent"/>, <see cref="CommandContext.BeginFact"/>): the registered
    /// trigger's own outputs, null for a binding that is no trigger.</summary>
    internal EvaluationOutputs? EventValues;
}

/// <summary>
/// One event being written by its trigger's output positions (the generated <c>Out…</c> constants of the trigger's
/// block) instead of as an object built per publish. <see cref="Values"/> takes the typed values; <see cref="Raise"/>
/// publishes the event. A binding has one sheet, so an event is written and raised before the next of the same
/// binding is begun.
/// </summary>
public readonly struct EventWriter
{
    private readonly RuntimeModuleHandle? module;
    private readonly RuntimeSubscriptionGate? gate;

    internal EventWriter(RuntimeModuleHandle module, RuntimeSubscriptionGate gate) { this.module = module; this.gate = gate; }

    /// <summary>Where the event's outputs are written, by output position.</summary>
    public EvaluationOutputs Values => gate?.EventValues ?? throw new RuntimeContractException("event-writer", "The event was not begun.");

    /// <summary>Publishes the event as written: the same admission <see cref="RuntimeModuleHandle.Publish"/> makes,
    /// with the binding and its mounted entries read off the gate.</summary>
    public DispatchResult Raise(string eventId, long worldEpoch, long simulationTick, string scopeId,
        string? causeId = null, string? rootEventId = null, int causalDepth = 0)
        => (module ?? throw new RuntimeContractException("event-writer", "The event was not begun."))
            .Raise(gate!, eventId, worldEpoch, simulationTick, scopeId, causeId, rootEventId, causalDepth);

    /// <summary>Publishes, on the host, a fact a client reported about its own player. <paramref name="token"/> is the
    /// number that client keeps its own early copy of the event's presentation steps under (<see cref="Predict"/>): the
    /// orders the host sends <paramref name="reporter"/> for this event carry it back, so each such step is shown on
    /// that machine once.</summary>
    internal DispatchResult Raise(string eventId, long worldEpoch, long simulationTick, string scopeId,
        EntityReference reporter, long token)
        => (module ?? throw new RuntimeContractException("event-writer", "The event was not begun."))
            .Raise(gate!, eventId, worldEpoch, simulationTick, scopeId, null, null, 0, reporter, token);

    /// <summary>On a client, plays at once the presentation steps this event starts on this machine that need nothing
    /// only the host knows, and keeps them under <paramref name="token"/> until the host's order for each arrives. The
    /// event is written exactly as the host will publish the reported fact; nothing is published here. Answers how
    /// many steps were played (always zero on the host).</summary>
    internal int Predict(long worldEpoch, long simulationTick, string scopeId, long token)
        => (module ?? throw new RuntimeContractException("event-writer", "The event was not begun."))
            .Predict(gate!, token, worldEpoch, simulationTick, scopeId);
}

/// <summary>
/// One committed fact of the command in progress, written by its trigger's output positions exactly as an event is
/// (<see cref="EventWriter"/>): <see cref="Values"/> takes the typed values and <see cref="Add"/> hands the fact to
/// the command. The kernel publishes the facts a command added only when its result is one that may carry them, in
/// the order they were added. A binding has one sheet, so a fact is written and added before the next of the same
/// binding is begun.
/// </summary>
public readonly struct FactWriter
{
    private readonly CommandInvocationState? invocation;
    private readonly long stamp;
    private readonly RuntimeSubscriptionGate? gate;

    internal FactWriter(CommandInvocationState invocation, long stamp, RuntimeSubscriptionGate gate)
    {
        this.invocation = invocation; this.stamp = stamp; this.gate = gate;
    }

    /// <summary>Where the fact's outputs are written, by output position.</summary>
    public EvaluationOutputs Values => gate?.EventValues ?? throw new RuntimeContractException("fact-writer", "The fact was not begun.");

    /// <summary>Adds the fact as written to the command's result.</summary>
    public void Add()
    {
        if (invocation == null || invocation.Stamp != stamp || stamp == 0)
            throw new RuntimeContractException("command-context-expired", "A fact writer is valid only for its handler invocation.");
        var rows = invocation.Rows;
        if (rows.Facts.Count >= CommandResult.MaximumFacts)
        { rows.FailFacts("fact-budget", CommandResult.FactBudgetDetail); return; }
        var sheet = Values;
        var kernel = gate!.Kernel ?? throw new RuntimeContractException("fact-writer", "The fact was not begun.");
        rows.Facts.Add(new RuntimeFact(gate, sheet.Ids, sheet.Snapshot(), kernel.SheetText(sheet).Length));
    }
}

/// <summary>A module can publish its own bindings and cancel its own scopes. This is ownership isolation, not an untrusted-code sandbox.</summary>
public sealed partial class RuntimeModuleHandle
{
    /// <summary>The gate of one of this module's own bindings. Resolved once — at registration, where the binding is
    /// already a fact of this module — and then read as one boolean before an event value is built.</summary>
    public RuntimeSubscriptionGate SubscriptionGate(string bindingId) => kernel.SubscriptionGate(ProviderId, bindingId);
    /// <summary>Begins one event of this module's own trigger binding, named by the gate the publisher already
    /// holds for it: the outputs are written by position and the event is published with <see cref="EventWriter.Raise"/>.</summary>
    public EventWriter BeginEvent(RuntimeSubscriptionGate gate) => kernel.BeginEvent(this, gate);
    internal DispatchResult Raise(RuntimeSubscriptionGate gate, string eventId, long worldEpoch, long simulationTick,
        string scopeId, string? causeId, string? rootEventId, int causalDepth, EntityReference? reporter = null, long token = 0)
        => kernel.Raise(this, gate, eventId, worldEpoch, simulationTick, scopeId, causeId, rootEventId, causalDepth, reporter, token);
    internal int Predict(RuntimeSubscriptionGate gate, long token, long worldEpoch, long simulationTick, string scopeId)
        => kernel.Predict(this, gate, token, worldEpoch, simulationTick, scopeId);
    /// <summary>What a publisher does with anything thrown while it predicts (see <see cref="RuntimeKernel.PredictionFailed"/>).</summary>
    internal void PredictionFailed(RuntimeSubscriptionGate gate, System.Exception error) => kernel.PredictionFailed(gate.BindingId, error);
    /// <summary>Whether any loaded plan consumes a trigger of any provider whose payload can name equipment. Only such a
    /// consumer can read a native attack's equipment attribution (<see cref="CombatShotAttribution"/>), so a producer
    /// that opens attribution windows reads this one precomputed boolean; with no such plan the window costs nothing.
    /// The set is a property of the registry and the loaded plans and is refreshed with the subscription table.</summary>
    public RuntimeSubscriptionGate EquipmentAttributionGate() => kernel.EquipmentAttributionGate();
    /// <summary>Every gate of every binding this module declares, resolved once here — the moment its bindings are
    /// already facts of the registry — so a publisher that writes to several bindings of its own keeps one set
    /// instead of re-asking the kernel per publish.</summary>
    public IReadOnlyDictionary<string, RuntimeSubscriptionGate> SubscriptionGates() => kernel.SubscriptionGates(ProviderId);
    /// <summary>The structural enum members the loaded plans mounted on one of this module's bindings actually
    /// name, ordinal ascending. A trigger whose parameter is a subscription key — `forge.trigger.input.key`'s
    /// `key` — reads this instead of polling every member of its set: the set is a property of the loaded plans,
    /// so it changes only where the subscription table is rebuilt. An empty answer means no loaded plan names a
    /// member, not that the binding is unknown; a binding this module does not own is refused by name.</summary>
    public IReadOnlyList<string> SubscribedEnumMembers(string bindingId, string parameterId)
        => kernel.SubscribedEnumMembers(ProviderId, bindingId, parameterId);
    /// <summary>The kernel's subscription-table generation, raised every time plans or modules change which
    /// bindings are consumed. A provider that caches a subscription-derived answer — the key set a poller reads —
    /// compares this one number per tick and rebuilds only when it moved, so no string lookup or allocation is
    /// paid on the tick path.</summary>
    public long SubscriptionGeneration => kernel.SubscriptionGeneration;
}
