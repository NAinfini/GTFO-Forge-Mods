using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// A client's own copy of the presentation steps its own reported fact starts. The host stays the one executor: it
/// admits the fact, walks the plan and orders every recipient as before. What is predicted is only the part of that
/// order this machine can already name: the leading presentation steps of an entry whose inputs are plan constants,
/// values of the reported event, or the unfiltered all-players set as recipients. Each such step is played here at
/// once, kept under the reporter's token, and the host's order for the same (token, plan, step) is answered with the
/// kept result instead of playing the step a second time. A prediction the host never orders is reported once it is
/// <see cref="PredictionConfirmMilliseconds"/> old; nothing here writes world state.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>Predicted steps that wait for the host's order at one time. A step past it is not predicted and plays
    /// when its order arrives.</summary>
    internal const int MaximumOpenPredictions = 64;
    internal const long PredictionConfirmMilliseconds = 3000;

    /// <summary>The address a presentation recipient list names this machine's player by; null where no network
    /// layer is attached, and then nothing is predicted.</summary>
    internal Func<string>? LocalSession { get; set; }
    internal Func<long> PredictionClock { get; set; } = static () => Environment.TickCount64;

    private struct Prediction
    {
        public long Token, Born;
        public LoadedPlan Plan;
        public ResolvedStep Step;
        public CommandResult Result;
    }

    private readonly Prediction[] predictions = new Prediction[MaximumOpenPredictions];
    private int predictionCount;
    private readonly List<EntityReference> predictionPlayers = new();
    // The reporter of the event being ordered, handed to the session resolver as a one-row list (EnterPresentation).
    private readonly EntityReference[] predictionReporter = new EntityReference[1];
    private readonly List<(LoadedPlan Plan, ResolvedStep Step, JsonElement Inputs)> predictionSteps = new();

    /// <summary>The steps of one entry a client may play ahead of the host's order, as indexes into the entry's own
    /// steps: the run of presentation actions from its start, ended by the first step that holds anything the host
    /// alone decides (a duration, a cost, a sound or play it tracks, a handle, a promoted parameter, a value another
    /// step computed). An entry with trigger options, a message name or a mount that is asked about a subject or a
    /// holder has none: those are judged from host state.</summary>
    private static int[] PredictableSteps(ResolvedEntry entry)
    {
        if (entry.Gate != null || entry.MessageName != null) return Array.Empty<int>();
        foreach (var attachment in entry.Attachments)
            if (attachment.Matcher.Scope == null || attachment.Holder != null) return Array.Empty<int>();
        List<int>? run = null;
        for (var index = entry.Start; (uint)index < (uint)entry.Steps.Count && (run?.Count ?? 0) < entry.Steps.Count;)
        {
            var step = entry.Steps[index];
            if (!step.PresentationTier || step.TracksSound || step.HoldsPlay || step.StopsSoundInstance || step.Effect != null
                || step.Cost != null || step.Promoted.Count != 0 || step.ReturnsHandle || step.HasResultDecision
                || step.RecipientsInput == null || !PredictableInputs(entry, step)) break;
            (run ??= new List<int>()).Add(index);
            index = step.Successors.Count > 0 ? step.Successors[0] ?? -1 : -1;
        }
        return run?.ToArray() ?? Array.Empty<int>();
    }

    private static bool PredictableInputs(ResolvedEntry entry, ResolvedStep step)
    {
        foreach (var input in step.Inputs)
        {
            if (input.HandlerLiteral != null) continue;
            if (input.ResultPort) return false;
            if (input.FromStep is { } source)
            {
                // The one computed value a client can name: every player, unfiltered. A filter is another step
                // between the selector and this input, so the input would not name the selector itself.
                if (input.Name != step.RecipientsInput || (uint)source.Step >= (uint)entry.Steps.Count
                    || entry.Steps[source.Step].CapabilityId != Blocks.SelectorTargetPlayers.Id) return false;
                continue;
            }
            if (input.EventPort != null && input.EventCell < 0) return false;
        }
        return true;
    }

    /// <summary>Plays, on a client, the predictable steps the event written on <paramref name="gate"/>'s sheet starts
    /// here, and keeps each under <paramref name="token"/> until the host's order for it arrives. The event is admitted
    /// exactly as a published one is and claimed by the same scope, enum and mount filters; nothing is queued. Answers
    /// how many steps were played; zero on the host, with no token, or when this kernel does not admit the event (the
    /// host's own order then plays whatever the host decides).</summary>
    internal int Predict(RuntimeModuleHandle module, RuntimeSubscriptionGate gate, long token, long worldEpoch,
        long simulationTick, string scopeId)
    {
        ReadThread();
        if (world.worldHost != false || token == 0 || !world.worldStarted || worldEpoch != WorldEpoch
            || LocalSession?.Invoke() is not { Length: > 0 } local || gate.Subscription is not Subscription mounted) return 0;
        var binding = GateTrigger(module, gate);
        var value = new RuntimeEvent("predicted", gate.BindingId, worldEpoch, simulationTick, scopeId, default);
        var walkEntities = entityLives.SaveEntityMemo();
        predictionSteps.Clear();
        try
        {
            entityLives.OpenEntityMemo(value.RenderedOutputs, -1, null);
            // What only the host can name (the equipment of a shot) is left unwritten; a step that reads it is not predicted.
            var subject = executor.AdmitWritten(value, binding, gate.EventValues!, partial: true);
            var work = mounted.ForEvent(value).Work;
            for (var i = 0; i < work.Length; i++)
            {
                var entry = work[i].Entry;
                var steps = entry.Predictable ??= PredictableSteps(entry);
                if (steps.Length == 0 || !plans.ModulesLive(work[i].Plan) || !EventScopeMatches(entry, value)
                    || !EnumFilterMatches(entry, value) || !MatchesAttachments(value, entry, subject, out _)) continue;
                foreach (var index in steps)
                    if (PredictedInputs(entry.Steps[index], value, local) is { } inputs)
                        predictionSteps.Add((work[i].Plan, entry.Steps[index], inputs));
            }
        }
        // Not admitted or not claimable here: nothing is predicted and the host's own order plays the step.
        catch (RuntimeContractException) { predictionSteps.Clear(); }
        finally { entityLives.RestoreEntityMemo(walkEntities); }
        var played = 0;
        foreach (var (plan, step, inputs) in predictionSteps)
        {
            if (predictionCount == predictions.Length)
            {
                log.LogBudgetExceeded(step.BindingId, value.EventId, "prediction-capacity");
                break;
            }
            var result = ExecutePresentationCommand(plan.Plan.Id, step.Frames.Index, value.EventId, value.EventId, local,
                worldEpoch, Math.Max(0, simulationTick), inputs);
            predictions[predictionCount++] = new Prediction { Token = token, Born = PredictionClock(), Plan = plan, Step = step, Result = result };
            played++;
        }
        predictionSteps.Clear();
        return played;
    }

    /// <summary>The inputs the host's order for this step will carry, written from the event as this machine reported
    /// it, or null when this machine's player is not among the step's recipients (or they cannot be named here).</summary>
    private JsonElement? PredictedInputs(ResolvedStep step, RuntimeEvent value, string local)
    {
        predictionPlayers.Clear();
        foreach (var input in step.Inputs)
        {
            if (input.HandlerLiteral != null) continue;
            if (input.FromStep != null) { if (!TryEnumerateEntityCandidates(EntityKinds.Player, predictionPlayers, out _)) return null; }
            // A value this machine did not write is one it does not know: the host's order carries it, so the step waits for it.
            else if (input.EventPort != null && !executor.EventCell(value, input.EventCell).Carried) return null;
        }
        var inputs = RuntimeJson.Write((Kernel: this, Step: step, Event: value), static (writer, state) =>
        {
            writer.WriteStartObject();
            foreach (var input in state.Step.Inputs)
            {
                if (input.HandlerLiteral != null) continue;
                if (input.FromStep != null)
                {
                    writer.WritePropertyName(input.Name);
                    writer.WriteStartArray();
                    foreach (var player in state.Kernel.predictionPlayers) CommandValue.WriteEntity(writer, player);
                    writer.WriteEndArray();
                    continue;
                }
                if (input.EventPort == null) continue;
                var cell = state.Kernel.executor.EventCell(state.Event, input.EventCell);
                if (!cell.Carried) continue;
                writer.WritePropertyName(input.Name);
                if (input.Wrap) writer.WriteStartArray();
                cell.WriteValue(writer, wire: true);
                if (input.Wrap) writer.WriteEndArray();
            }
            writer.WriteEndObject();
        });
        if (!inputs.TryGetProperty(step.RecipientsInput!, out var addressed)
            || !registry.Tables.PresentationSessions.TryGetValue(step.ProviderId, out var registered) || registered.Sessions == null) return null;
        EntityReference[] recipients;
        if (addressed.ValueKind == JsonValueKind.Array)
        {
            recipients = new EntityReference[addressed.GetArrayLength()];
            var index = 0;
            foreach (var item in addressed.EnumerateArray()) recipients[index++] = RuntimeJson.EntityRow(item);
        }
        else recipients = new[] { RuntimeJson.EntityRow(addressed) };
        if (registered.Sessions(recipients) is not { } sessions) return null;
        foreach (var session in sessions)
            if (string.Equals(session, local, StringComparison.Ordinal)) return inputs;
        return null;
    }

    /// <summary>The kept result of the step the host now orders, if this machine played it ahead under the same
    /// token; the prediction is closed by the read. Null when it did not: the order is then executed as usual.</summary>
    internal CommandResult? TakePrediction(long token, string planId, int stepIndex)
    {
        for (var i = 0; i < predictionCount; i++)
        {
            ref var held = ref predictions[i];
            if (held.Token != token || held.Step.Frames.Index != stepIndex
                || !string.Equals(held.Plan.Plan.Id, planId, StringComparison.Ordinal)) continue;
            var result = held.Result;
            predictions[i] = predictions[--predictionCount];
            predictions[predictionCount] = default;
            return result;
        }
        return null;
    }

    /// <summary>Closes the predictions the host did not order in time: each was shown here and nowhere else, which is
    /// recorded once.</summary>
    private void ExpirePredictions()
    {
        if (predictionCount == 0) return;
        var now = PredictionClock();
        for (var i = predictionCount - 1; i >= 0; i--)
        {
            if (now - predictions[i].Born < PredictionConfirmMilliseconds) continue;
            log.LogEventRejected(predictions[i].Step.BindingId, predictions[i].Plan.Plan.Id + "." + predictions[i].Step.NodeId, "prediction-unconfirmed");
            predictions[i] = predictions[--predictionCount];
            predictions[predictionCount] = default;
        }
    }

    private readonly HashSet<string> predictionFailures = new(StringComparer.Ordinal);

    /// <summary>A prediction is optional: whatever goes wrong while a publisher writes or plays one must not reach the
    /// report it follows or the game method it runs in. The publisher catches it and hands it here; it is recorded
    /// once per trigger and world with its reason, and the event is simply shown when the host orders it.</summary>
    internal void PredictionFailed(string bindingId, Exception error)
    {
        if (!predictionFailures.Add(bindingId)) return;
        log.LogEventRejected(bindingId, error is RuntimeContractException contract ? contract.Code : error.GetType().Name, "prediction-failed");
    }

    private void ClearPredictions()
    {
        predictionFailures.Clear();
        Array.Clear(predictions, 0, predictionCount);
        predictionCount = 0;
    }
}
