using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    /// <summary>The recipient-state source observes only requests the transport admission already accepted. It is
    /// a separate slot so network startup and teardown cannot overwrite the domain observer.</summary>
    internal Func<PresentationOutput, CommandResult?>? RecipientStateAdmission { get; set; }

    /// <summary>Read a newly admitted presentation request through the exact resolved frame its recipient handler
    /// will receive. This does not invoke the handler or open a second dispatch.</summary>
    internal void WithRecipientContext(PresentationOutput request, Action<CommandContext> read)
        => WithRecipientContext(request, request.Inputs, default, referenceConstants: true, read);

    /// <summary>Read a retained presentation after its complete input document was rebound to the current world.
    /// The request must still name the same loaded presentation step.</summary>
    internal void RebuildRecipientContext(PresentationOutput request, JsonElement inputs, JsonElement parameters,
        Action<CommandContext> read)
        => WithRecipientContext(request, inputs, parameters, referenceConstants: false, read);

    private void WithRecipientContext(PresentationOutput request, JsonElement inputs, JsonElement parameters,
        bool referenceConstants, Action<CommandContext> read)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(read);
        ReadThread();
        RuntimeJson.Require(request.WorldEpoch == WorldEpoch, "stale-world",
            "The retained presentation belongs to another world.");
        RuntimeJson.Require(plans.Loaded.TryGetValue(request.PlanId, out var plan), "plan-unloaded",
            request.PlanId);
        var step = plan!.Step(request.StepIndex);
        if (!(step != null && step.Frames.Index == request.StepIndex
            && step.ProviderId == request.Provider && step.BindingId == request.BindingId
            && step.NodeId == request.NodeId && IsPresentationStep(step))) throw new RuntimeContractException("presentation-step", request.PlanId + "." + request.StepIndex);
        RuntimeJson.Require(plans.ModulesLive(plan), "binding-lifecycle", request.BindingId);
        if (!referenceConstants)
            RuntimeJson.Require(parameters.ValueKind == JsonValueKind.Object, "invalid-presentation-input",
                "Retained presentation parameters must be an object.");
        var wireInputs = referenceConstants ? inputs : RecipientWireInputs(step!, inputs);
        var wireParameters = referenceConstants ? default : RecipientWireParameters(step!, parameters);
        // The read is synchronous, so the step's boundary frame is lent for it and taken back when it returns.
        var leased = LeaseBoundaryInputs(step!);
        try
        {
            var frame = executor.ResolveBoundaryStepInputs(step!, wireInputs, out var resolvedParameters,
                referenceConstants: referenceConstants, wireParameters: wireParameters, into: leased);
            var origin = new RuntimeEvent(request.EventId, request.BindingId, request.WorldEpoch,
                request.SimulationTick, request.ScopeId, RuntimeJson.EmptyObject);
            var context = new CommandContext(origin, request.SimulationTick, request.CommandId, plan.Names,
                request.NodeId, resolvedParameters, frame, false, entityInstance, ReportedTickRate,
                detachedDeferredContext: true);
            // The reader may hand the context to the step's own handler (a retained presentation replayed for a
            // player who rejoined), so the context has the row region and the recipients a recipient command has:
            // the handler writes one row per recipient and concludes as usual, and the rows end with the read.
            var written = context.DetachedRows!;
            executor.ArmTypedWrite(step!, frame, isHost: false, boundary: true, detached: written);
            try { read(context); }
            finally { written.Disarm(); }
        }
        finally { if (leased != null) leased.Leased = false; }
    }

    /// <summary>The presentation step's second frame, preallocated at load, for one synchronous boundary read (the
    /// recipient command or a recipient-state read); null while it is already lent, and the caller then decodes into
    /// a frame of its own. The step's dispatch frame is never lent: a recipient-state read runs inside its dispatch.</summary>
    private static StepInputs? LeaseBoundaryInputs(ResolvedStep step)
    {
        if (step.BoundaryInputs is not { Leased: false } frame) return null;
        frame.Leased = true;
        return frame;
    }

    /// <summary>A complete retained input document is handler-facing. Scalar enum cells therefore carry member
    /// names while the boundary decoder takes their compiled indices. Resource and other input values already use
    /// their canonical handler/wire object and pass through unchanged.</summary>
    private static JsonElement RecipientWireInputs(ResolvedStep step, JsonElement inputs)
        => RecipientWireInputs(step.Inputs, step.InputSlots, inputs);

    internal static JsonElement RecipientWireInputs(IReadOnlyList<StepInput> stepInputs, int[] inputSlots,
        JsonElement inputs)
    {
        RuntimeJson.Require(inputs.ValueKind == JsonValueKind.Object, "invalid-presentation-input",
            "Retained presentation inputs must be an object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = 0; index < stepInputs.Count; index++)
        {
            var input = stepInputs[index];
            if (inputSlots[index] < 0 || !inputs.TryGetProperty(input.Name, out var value)) continue;
            if (!input.Wrap && input.View.Kind == PortView.Kinds.Enum && value.ValueKind == JsonValueKind.String)
            {
                var member = Array.FindIndex(input.View.EnumMembers!, item => item.GetString() == value.GetString());
                RuntimeJson.Require(member >= 0, "invalid-enum", input.Name);
                fields.Add(input.Name, RuntimeJson.From(member));
            }
            else fields.Add(input.Name, value.Clone());
        }
        return RuntimeJson.From(fields);
    }

    /// <summary>The retained document is handler-facing, so enum parameters carry member names. The boundary
    /// decoder consumes their compiled member indices; convert only that representation and let the ordinary
    /// parameter validator check the complete shape and every remaining value.</summary>
    private static JsonElement RecipientWireParameters(ResolvedStep step, JsonElement parameters)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in step.ParameterDefinitions)
        {
            var name = RuntimeJson.Text(definition, "id");
            if (!parameters.TryGetProperty(name, out var value)) continue;
            if (RuntimeJson.Text(definition, "type") != "enum")
            { fields.Add(name, value.Clone()); continue; }
            RuntimeJson.Require(value.ValueKind == JsonValueKind.String, "invalid-enum", name);
            var index = Array.IndexOf(RuntimeGraphContracts.EnumMembers(definition), value.GetString());
            RuntimeJson.Require(index >= 0, "invalid-enum", name);
            fields.Add(name, RuntimeJson.From(index));
        }
        return RuntimeJson.From(fields);
    }
}
