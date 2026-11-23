using System;

namespace ForgeEngine.Framework;

/// <summary>
/// The variable, flow-glue and cross-module message vocabulary, owned by the kernel the same way
/// <see cref="ControlContracts"/> owns the first-step control words. Every row here is a `control` step except the
/// receive trigger: the kernel walks controls itself, which is what lets these rows write a frame and route to more
/// than one exit, and it is also why none of them needs a provider handler.
///
/// The rows are:
/// <list type="bullet">
/// <item><c>forge.variable.store.declare</c> — the declaration a read or a write names. The website lowers it into
/// the plan header's `variables[]` row, so no step ever runs it; its binding is registered so the row a plan was
/// authored against is one the registry holds.</item>
/// <item><c>forge.variable.store.read</c> / <c>.write</c> — read and write one declared variable (<c>g-var</c>).
/// A variable may hold a guarded effect handle — a wave or an alarm — as well as a number, a flag, text or an
/// entity, so a door, a terminal or a running wave kept for the whole level is an ordinary level variable.</item>
/// <item><c>forge.control.flow.once</c> — leave by `first` the first time this card is reached in the level, from
/// whichever entrypoint, and by `later` after that (<c>g-once</c>).</item>
/// <item><c>forge.control.flow.wait_event</c> — wait for a custom message or an engine event, with an optional
/// deadline, and leave by `received` or by `timeout` (<c>g-wait</c>).</item>
/// <item><c>forge.control.message.emit</c> — send a custom message another behaviour can receive (<c>g-message</c>).
/// </item>
/// <item><c>forge.trigger.status.event_received</c> — the entry point a behaviour hangs on to receive those
/// messages, and the row a `g-wait` names when it waits for one.</item>
/// <item><c>forge.trigger.status.variable_changed</c> — the entry point a behaviour hangs on to run when a write
/// changed one named variable's value.</item>
/// </list>
///
/// Nothing here is a second value channel: `g-var`, `g-message` and `g-wait` all read and write the one
/// store the kernel owns, publish through the one event queue, and resume the one dispatch walk. `g-io` is not a
/// runtime row at all: a module is an editor-only construct that is flattened into the plan at export, so there is
/// no `forge.control.flow.subgraph`/`.return` to dispatch and none is registered here.
/// </summary>
internal static class VariableContracts
{
    internal const string ProviderId = "forge.contract.variables";
    public const string DeclareCapability = Blocks.VariableStoreDeclare.Id;
    public const string ReadCapability = Blocks.VariableStoreRead.Id;
    public const string WriteCapability = Blocks.VariableStoreWrite.Id;
    public const string OnceCapability = Blocks.ControlFlowOnce.Id;
    public const string WaitCapability = Blocks.ControlFlowWaitEvent.Id;
    public const string EmitCapability = Blocks.ControlMessageEmit.Id;
    internal const string LogRecordCapability = Blocks.ControlLogRecord.Id;
    public const string MessageReceivedCapability = Blocks.TriggerStatusEventReceived.Id;
    public const string VariableChangedCapability = Blocks.TriggerStatusVariableChanged.Id;
    public const string DeclareBinding = ProviderId + ".binding.declare";
    public const string ReadBinding = ProviderId + ".binding.read";
    public const string WriteBinding = ProviderId + ".binding.write";
    public const string OnceBinding = ProviderId + ".binding.once";
    public const string WaitBinding = ProviderId + ".binding.wait";
    public const string EmitBinding = ProviderId + ".binding.emit";
    internal const string LogRecordBinding = ProviderId + ".binding.log_record";
    /// <summary>The port a custom message's one value travels on, on both sides: the send step's input and the
    /// receive trigger's output. The envelope already has a field called `payload`, so a trigger port of that name
    /// would be the one port that says nothing about what it carries; `value` is the same word the send row uses.
    /// </summary>
    internal const string MessageValuePort = "value";
    /// <summary>The one binding a custom message is received through. A `g-wait` names it as its `target`, a
    /// trigger step binds it as its entry point, and an `emit` step's own `message` parameter carries the name the
    /// sender chose.</summary>
    public const string MessageReceivedBinding = ProviderId + ".binding.message_received";
    /// <summary>The binding a variable change is published under. Its entry names one variable by its structural
    /// `name` parameter, and the event carries that name on its `name` port, so the same name index serves it.</summary>
    public const string VariableChangedBinding = ProviderId + ".binding.variable_changed";

    // What a `g-wait` may name is not listed here: a provider declares a trigger binding waitable
    // (`ProviderBinding.Waitable`) and the registry answers `IsWaitable`. The kernel looks a suspended wait up by
    // `RuntimeEvent.BindingId`, so the target is always a binding id; the custom-message binding below declares
    // itself waitable like any other.

    internal static bool IsMessageEvent(string bindingId) => bindingId == MessageReceivedBinding;
    /// <summary>The structural parameter an entry on a name-indexed binding names its one subject by, or null for a
    /// binding that is not indexed by name.</summary>
    internal static string? NameFilterParameter(string bindingId) => bindingId switch
    {
        MessageReceivedBinding => "message",
        VariableChangedBinding => "name",
        _ => null
    };
    /// <summary>The event port that carries the name a name-indexed entry is matched against.</summary>
    internal static string? NameFilterPort(string bindingId) => bindingId switch
    {
        MessageReceivedBinding => "message",
        VariableChangedBinding => "name",
        _ => null
    };
    /// <summary>A message name is one identifier, so the sender, the receiver's claim filter and a `g-wait` spell it
    /// the same way.</summary>
    internal static bool IsMessageName(string message) => message.Length is > 0 and <= 64 && RuntimeJson.IsId(message);
    /// <summary>The subject a `variable_changed` entry names is a compiled variable identity, not a message name: the
    /// registered level identity `var.g` plus a 64-digit digest is 69 characters, which the message limit would refuse.</summary>
    internal static bool IsVariableName(string name) => name.Length is > 0 and <= 80 && RuntimeJson.IsId(name);
    internal static bool IsNameFilter(string bindingId, string name)
        => bindingId == VariableChangedBinding ? IsVariableName(name) : IsMessageName(name);

    /// <summary>The kernel's own module. It is registered like any other built-in provider, so a plan pins these
    /// bindings by the same lock every other binding uses.</summary>
    public static RuntimeModule Module() => PrimitiveContracts.Declaration(ProviderId)
        .Bind(new ProviderBinding[]
        {
            // The declaration is lowered into the plan header and never dispatched. Its tier is `query`, whose
            // binding role is `observe`; the registry asks an evaluator only of a selector, condition or value row.
            Binding(DeclareBinding, DeclareCapability, "runtime.variable.declare", "observe"),
            Binding(ReadBinding, ReadCapability, "runtime.variable.read", "execute"),
            Binding(WriteBinding, WriteCapability, "runtime.variable.write", "execute"),
            Binding(OnceBinding, OnceCapability, "runtime.control.once", "execute"),
            Binding(WaitBinding, WaitCapability, "runtime.control.wait_event", "execute"),
            Binding(EmitBinding, EmitCapability, "runtime.message.emit", "execute"),
            Binding(LogRecordBinding, LogRecordCapability, "runtime.log.record", "execute"),
            Binding(MessageReceivedBinding, MessageReceivedCapability, "runtime.trigger.message_received", "observe", waitable: true),
            Binding(VariableChangedBinding, VariableChangedCapability, "runtime.trigger.variable_changed", "observe")
        }).Module(RuntimeApi.Fingerprint);

    private static ProviderBinding Binding(string id, string capabilityId, string handler, string role, bool waitable = false) =>
        new(id, capabilityId, handler, role, Array.Empty<string>()) { Waitable = waitable };
}
