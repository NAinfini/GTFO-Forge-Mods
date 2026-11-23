using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The kernel's `presentation` tier. A presentation step is a real step of a real plan, but what it writes is the
/// game's own presentation — a camera effect, a HUD line, a fog volume — so the split of responsibility is
/// different from every other tier: the host owns the decision (which step runs, on which inputs, for which
/// players), the recipient owns the write. Nothing here commits world state, and no presentation step ever
/// produces a fact.
/// <para>
/// One execution path does both halves. The host's walk resolves the step and its recipients and records an
/// intent instead of invoking a handler; a recipient hands that same intent back as a command request — over the
/// network layer's own request channel — and <see cref="ExecutePresentationCommand"/> invokes the very same
/// registered handler through the very same result validation, with <see cref="CommandContext.IsHost"/> false
/// because the advance that decided the timing ran elsewhere.
/// </para>
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>Player sessions one presentation step may address. The set is a routing list, not a value frame,
    /// so it is bounded here: a plan that means "everyone" says so through its own recipient resolver.</summary>
    public const int MaximumPresentationRecipients = 64;
    /// <summary>The largest dynamic input frame one presentation command may carry on the wire: the payload slot the
    /// network layer reserves for a command request. A step whose inputs need more is refused rather than
    /// truncated.</summary>
    public const int MaximumPresentationInputBytes = 512;

    /// <summary>The host transport's fixed envelope checks. Run before queueing, so a transport budget refusal
    /// is an ordinary step result under the plan's failure policy, without affecting other plans or the world.</summary>
    internal Func<PresentationOutput, CommandResult?>? PresentationAdmission { get; set; }

    /// <summary>Whether this step is the `presentation` tier. The tier is a registration fact of the capability
    /// the step's binding implements, read once when the plan was loaded and never inferred from the node's ports
    /// or from its id; a module that unregisters takes its plans with it, so a live step's tier cannot change.</summary>
    private static bool IsPresentationStep(ResolvedStep step) => step.Execution == "presentation";

    /// <summary>
    /// The host's half of a presentation step: the recipient set, then the intent the network layer sends. The
    /// step's own inputs are already resolved by the walk and are what travels — a recipient presents exactly what
    /// the host decided, and the fact that the write lands on a replica is carried by the request itself.
    /// </summary>
    private string? EnterPresentation(LoadedPlan plan, ResolvedStep step, Pending pending, string commandId,
        StepInputs frame, in KernelLog.StepOrigin origin, JsonElement parameters, out CommandResult result)
    {
        result = null!;
        var provider = step.ProviderId;
        var failure = ResolvePresentationRecipients(provider, step, frame, out var recipients);
        if (failure != null)
        {
            result = CommandResult.Rejected(failure.Code, failure.Message);
            return failure.Code;
        }
        // Every recipient is a player with no screen (a bot): nobody needed the step and nothing failed, so it is sent
        // to nobody and finishes under its own code instead of passing for a step somebody saw.
        if (recipients.Count == 0)
        {
            result = NoViewer;
            return null;
        }
        // The request already references the exact loaded plan and step. Constants belong to that
        // pre-resolved plan, not to the per-command payload (including long tutorial text).
        var startsSound = step.TracksSound;
        var soundId = startsSound
            ? WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + plan.Plan.Id + "/"
                + pending.Event.ScopeId + "/" + step.Frames.Index + "/" + checked(++kernelEffects.soundSequence)
            : null;
        // One object written once: the carried inputs, then the envelopes the step's own shape needs. Only a step that
        // holds plays names one, so a sound or HUD row spends none of its budget on it.
        var inputs = RuntimeJson.Write(new PresentationEnvelope(frame, soundId, pending.Event.ScopeId, pending.Provider,
            step.HoldsPlay, step.HoldsPlay ? kernelFlow.PlayIdentity(step, pending) : 0, CurrentTick), PresentationEnvelope.Write);
        var payload = RawJson.Bytes(inputs);
        if (payload.Length > MaximumPresentationInputBytes)
        {
            result = CommandResult.Rejected(RuntimeAbiCodes.PresentationInputBudget, step.NodeId);
            return RuntimeAbiCodes.PresentationInputBudget;
        }
        // A fact a client reported about its own player names that player: the order that reaches it carries the
        // client's token back, so a step its machine already played is not played there twice.
        string? predictedBy = null;
        if (pending.Event.PredictionToken != 0 && pending.Event.Reporter is { } reporter)
        {
            // The kernel's own one-row list, read by the resolver during the call only: no array per order.
            predictionReporter[0] = reporter;
            if (registry.Tables.PresentationSessions[provider].Sessions!(predictionReporter) is { Count: 1 } own) predictedBy = own[0];
            predictionReporter[0] = null!;
        }
        var request = new PresentationOutput(provider, plan.Plan.Id, step.NodeId, commandId, step.BindingId,
            step.Frames.Index, recipients, pending.Event.EventId, pending.Event.ScopeId, WorldEpoch,
            pending.Event.SimulationTick, RawJson.Snapshot(inputs, payload))
        {
            Origin = origin,
            TraceInputs = TraceValue(TraceFrame(frame)),
            TraceParameters = behaviorTraceObservers.Count == 0 ? null : parameters.Clone(),
            RequestSequence = checked(++eventQueue.sequence),
            PredictedBy = predictedBy,
            PredictionToken = predictedBy == null ? 0 : pending.Event.PredictionToken
        };
        if (PresentationAdmission?.Invoke(request) is { } refusal)
        {
            result = refusal;
            // An envelope refusal is an action result: the ordinary receipt/row and failure-policy path
            // reports it, rather than treating it as an activation-wide dispatch fault.
            return null;
        }
        if (RecipientStateAdmission?.Invoke(request) is { } stateRefusal)
        {
            result = stateRefusal;
            return null;
        }
        if (soundId != null)
        {
            RuntimeJson.Require(kernelEffects.soundHistory.Count < MaximumEventHistory && !kernelEffects.soundHistory.ContainsKey(soundId),
                RuntimeAbiCodes.HandleBudget, "Sound history capacity reached.");
            var handle = kernelEffects.CreateHandle(provider, "sound", "encounter", out var address);
            kernelEffects.soundHistory.Add(soundId, false);
            kernelEffects.sounds.Add(address, new SoundInstance(handle, pending.Provider, request));
            result = CommandResult.Partial(RuntimeJson.From(new { sound = handle }), CommitStates.None);
        }
        else result = Presented;
        kernelScheduler.presentationOutputs.Add(request);
        return null;
    }

    /// <summary>The play every presentation command names, beside its inputs: <c>[identity, tick]</c>. The tick is the
    /// one the step ran on (<see cref="CurrentTick"/>), not the tick its event was raised on — a step resumed after a wait
    /// runs later than its event — and the identity says which play this is from one command to the next (see
    /// <see cref="PlayIdentity"/>). A recipient that keeps something per play (a held effect) keys it by these two numbers
    /// and compares nothing else.</summary>
    internal const string PlayEnvelope = "$play";

    /// <summary>What one presentation command carries, written once by <see cref="Write"/>: the step's carried inputs,
    /// the sound envelope of a tracked playback, and the play of a step that holds plays.</summary>
    internal readonly record struct PresentationEnvelope(StepInputs Frame, string? SoundId, string Scope, string Source,
        bool Plays, long Play, long Tick)
    {
        internal static readonly Action<Utf8JsonWriter, PresentationEnvelope> Write = static (writer, envelope) =>
        {
            writer.WriteStartObject();
            envelope.Frame.WritePresentationInputs(writer);
            if (envelope.SoundId != null)
            {
                writer.WriteStartObject(SoundEnvelope);
                writer.WriteString("id", envelope.SoundId);
                writer.WriteString("scope", envelope.Scope);
                writer.WriteString("source", envelope.Source);
                writer.WriteBoolean("stop", false);
                writer.WriteEndObject();
            }
            if (envelope.Plays)
            {
                writer.WriteStartArray(PlayEnvelope);
                writer.WriteNumberValue(envelope.Play);
                writer.WriteNumberValue(envelope.Tick);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        };
    }

    /// <summary>Where the plays of one activation start from, before the rounds of the loops a step runs in are folded in.
    /// <see cref="Root"/> is the trigger scope, folded with the instance of every interval `repeat` or `ramp` series the
    /// activation runs under; <see cref="Subject"/> is <see cref="Root"/> folded with the trigger's own subject entities (id
    /// and life epoch, in any order), or with the event's own id when the trigger names no subject — each such event is a
    /// play of its own, because the scope alone (a world-wide one, for most enemy events) says nothing about whom it is
    /// about. A step outside every loop starts from
    /// <see cref="Subject"/>; a step inside one starts from <see cref="Root"/>, because its loop's round already names whom
    /// the play is about, and the same target reached from two triggers' events is one play.</summary>
    internal readonly record struct PlayOrigin(long Root, long Subject)
    {
        /// <summary>The origin of the series one interval `repeat` or `ramp` entry starts: every pulse of the series runs
        /// under it unchanged, and two entries — the same ability triggered again before the first series ended — differ
        /// by their schedule instance.</summary>
        internal PlayOrigin Series(long instance) => new(Fnv(Root, instance), Fnv(Subject, instance));
    }

    internal static readonly byte[] EntityIdUtf8 = System.Text.Encoding.UTF8.GetBytes("id");
    internal static readonly byte[] EntityLifeUtf8 = System.Text.Encoding.UTF8.GetBytes("lifeEpoch");

    internal static long Fnv(long hash, string text)
    {
        foreach (var c in text) hash = unchecked((hash ^ c) * 1099511628211L);
        return unchecked((hash ^ 0xff) * 1099511628211L);
    }

    /// <summary><see cref="Fnv(long, string)"/> over a JSON string's text without making the string: the code units are
    /// read from its UTF-8 in place; only text the writer escaped is unescaped first.</summary>
    internal static long FnvText(long hash, JsonElement text)
    {
        var raw = RawJson.Bytes(text);
        var inner = raw.Slice(1, raw.Length - 2);
        var start = hash;
        foreach (var b in inner)
        {
            // An ASCII byte is its own code unit; anything else is read through the decoder.
            if (b >= 0x80 || b == (byte)'\\') return Fnv(start, text.GetString()!);
            hash = unchecked((hash ^ b) * 1099511628211L);
        }
        return unchecked((hash ^ 0xff) * 1099511628211L);
    }

    internal static long Fnv(long hash, long value)
    {
        for (var shift = 0; shift < 64; shift += 8) hash = unchecked((hash ^ ((value >> shift) & 0xff)) * 1099511628211L);
        return hash;
    }

    /// <summary>The host's receipt for every presentation step it dispatched: the work was handed on, nothing was
    /// committed here and no fact was published. The result is immutable, so every receipt shares it.</summary>
    private static readonly CommandResult Presented = CommandResult.Partial(RuntimeJson.EmptyObject, CommitStates.None, Array.Empty<RuntimeFact>());

    /// <summary>The answer to a step whose recipients have no screen: the shape of `no-targets` (succeeded, nothing
    /// to change) with its own code, because the set was not empty.</summary>
    private static readonly CommandResult NoViewer = CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed,
        RuntimeAbiCodes.PresentationNoViewer, "", RuntimeJson.EmptyObject);

    /// <summary>The dispatch above is not a terminal result. The network ledger returns the real recipient
    /// outcomes here once it has closed the exact request, including explicit missing/world-ended outcomes.
    /// Presentation never upgrades a handler's partial result or claims a world commitment.</summary>
    internal void ReceivePresentationResult(PresentationOutput request, CommandResult result)
    {
        ReadThread();
        RuntimeJson.Require(CommandResultRules.TryValidate(result, out _, presentation: true)
            && (result.CommitState == CommitStates.None || result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown)
            && result.Facts.Count == 0,
            RuntimeAbiCodes.PresentationCommit, "A presentation receipt must be a valid non-committing result.");
        TracePresentationResult(request, result);
    }

    /// <summary>
    /// Which player sessions a step is addressed to, answered by the module that owns the step's binding from the
    /// entity references the step's own `recipients` port carries. There is no implicit set: a provider that
    /// declares a presentation capability registers a resolver, a step no resolver answers for is refused by name,
    /// and a step whose recipients cannot be converted is refused rather than widened — the same rule the hosting
    /// side of a request follows. Narrowing here is what keeps one player's value frame off every other machine.
    /// A resolver answers null for recipients it cannot convert and an empty list for real players none of whom has
    /// a screen; the second comes back as no sessions and no refusal.
    /// </summary>
    private RuntimeContractException? ResolvePresentationRecipients(string provider, ResolvedStep step, StepInputs inputs,
        out IReadOnlyList<string> recipients)
    {
        recipients = Array.Empty<string>();
        if (!registry.Modules.ContainsKey(provider))
            return new RuntimeContractException("binding-lifecycle", step.BindingId);
        if (!registry.Tables.PresentationSessions.TryGetValue(provider, out var registered) || registered.Sessions == null)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "No player-session provider is registered for " + provider + " (registered: " + string.Join(",", registry.Tables.PresentationSessions.Keys) + ").");
        var claimed = registered.Sessions(AddressedRecipients(step, inputs));
        if (claimed == null)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "A presentation step must address at least one player: " + step.NodeId);
        if (claimed.Count == 0) return null;
        // The sessions in ordinal order, each once: the same set whatever order and repetition the provider answered in.
        var sessions = new string[claimed.Count];
        for (var i = 0; i < sessions.Length; i++)
        {
            var session = claimed[i];
            if (string.IsNullOrEmpty(session))
                return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient, step.NodeId);
            sessions[i] = session;
        }
        Array.Sort(sessions, StringComparer.Ordinal);
        var distinct = sessions.Length == 0 ? 0 : 1;
        for (var i = 1; i < sessions.Length; i++)
            if (!string.Equals(sessions[i], sessions[distinct - 1], StringComparison.Ordinal)) sessions[distinct++] = sessions[i];
        if (distinct > MaximumPresentationRecipients)
            return new RuntimeContractException(RuntimeAbiCodes.PresentationRecipient,
                "A presentation step addresses at most " + MaximumPresentationRecipients + " players.");
        if (distinct < sessions.Length) Array.Resize(ref sessions, distinct);
        recipients = sessions;
        return null;
    }

    /// <summary>
    /// The entity references one presentation step addresses: the resolved value of the port the capability's own
    /// `recipients` declaration names, which every presentation action row declares and no plan may leave unwired.
    /// Null means the step carries none this kernel can read, and the provider answers that with its own refusal —
    /// never with the whole session.
    /// </summary>
    private IReadOnlyList<EntityReference>? AddressedRecipients(ResolvedStep step, StepInputs inputs)
    {
        var value = inputs.InputAt(step.RecipientsInputSlot);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        // A reference always names a non-empty id (<see cref="RuntimeJson.EntityRow"/> refuses any other), so every
        // one read is a recipient.
        if (value.ValueKind == JsonValueKind.Array)
        {
            var references = new EntityReference[value.GetArrayLength()];
            var index = 0;
            foreach (var item in value.EnumerateArray()) references[index++] = RuntimeJson.EntityRow(item);
            return references;
        }
        return new[] { RuntimeJson.EntityRow(value) };
    }

    /// <summary>
    /// The recipient's half: execute one presentation command the host decided. The plan and step are named by
    /// identity and resolved in this kernel's own registry, so a replica runs the same handler on the same inputs
    /// the host resolved; the command is invoked with <see cref="CommandContext.IsHost"/> false, and a handler
    /// that reports anything but a non-committing result is refused rather than trusted. No fact is published and
    /// no world state is written by this path, whatever a handler returns.
    /// </summary>
    internal CommandResult ExecutePresentationCommand(string planId, int stepIndex, string commandId, string eventId,
        string scopeId, long worldEpoch, long simulationTick, JsonElement inputs)
    {
        CommandContext? invocationContext = null;
        ResolvedStep? invokedStep = null;
        StepInputs? leased = null;
        try
        {
            Thread();
            RuntimeJson.Require(worldEpoch == WorldEpoch, "stale-world", "Presentation belongs to another world.");
            // Plan identities use the loader's opaque text contract (the website emits
            // content digests), not the dotted identifier grammar for providers/bindings.
            RuntimeJson.Text(planId);
            RuntimeJson.CommandId(commandId); RuntimeJson.Text(scopeId); RuntimeJson.Text(eventId);
            RuntimeJson.Integer(simulationTick, 0);
            RuntimeJson.Require(world.worldStarted, "dispatch-lifecycle", "Presentation needs a started world.");
            RuntimeJson.Require(inputs.ValueKind == JsonValueKind.Object, "invalid-presentation-input",
                "Presentation inputs must be an object keyed by input port id.");
            string? soundId = null;
            string? soundSource = null;
            if (inputs.TryGetProperty(SoundEnvelope, out var sound))
            {
                soundId = RuntimeJson.Text(sound, "id");
                scopeId = RuntimeJson.Text(sound, "scope");
                soundSource = RuntimeJson.Text(sound, "source");
                if (sound.GetProperty("stop").GetBoolean()) return SoundStopped(kernelEffects.StopLocalSound(soundId));
                if (kernelEffects.soundHistory.TryGetValue(soundId, out var ended) && ended) return SoundStopped(false);
                if (kernelEffects.localSounds.ContainsKey(soundId)) return Presented;
            }
            RuntimeJson.Require(plans.Loaded.ContainsKey(planId), "plan-unloaded", planId);
            var plan = plans.Loaded[planId];
            var step = plan.Step(stepIndex)
                ?? throw new RuntimeContractException("presentation-step", planId + "." + stepIndex);
            invokedStep = step;
            RuntimeJson.Require(IsPresentationStep(step), "presentation-step", step.NodeId);
            RuntimeJson.Require(plans.ModulesLive(plan), "binding-lifecycle", step.BindingId);
            var handler = step.Handler!;
            var inputFrame = executor.ResolveBoundaryStepInputs(step, inputs, out var parameters, referenceConstants: true,
                into: leased = LeaseBoundaryInputs(step));
            // The host names a play exactly on the steps that hold plays — the same load-time mark on both sides — so a
            // request that carries one where none belongs, or lacks one where it does, was not decided by this walk.
            var named = inputs.TryGetProperty(PlayEnvelope, out var play);
            RuntimeJson.Require(named == step.HoldsPlay && (!named || play.ValueKind == JsonValueKind.Array && play.GetArrayLength() == 2),
                "invalid-presentation-input", step.HoldsPlay ? "This presentation command names no play." : "This presentation step holds no play.");
            var playIdentity = named ? RuntimeJson.Integer(play[0], long.MinValue, long.MaxValue) : 0;
            var playTick = named ? RuntimeJson.Integer(play[1], 0) : 0;
            var origin = new RuntimeEvent(eventId, step.BindingId, worldEpoch, simulationTick, scopeId, RuntimeJson.EmptyObject);
            var context = new CommandContext(origin, simulationTick, commandId, plan.Names,
                step.NodeId, parameters, inputFrame, false, entityInstance, ReportedTickRate);
            if (soundId != null)
            {
                RuntimeJson.Require(StartsSound(step), "presentation-step", step.NodeId);
                RuntimeJson.Require(kernelEffects.soundHistory.ContainsKey(soundId) || kernelEffects.soundHistory.Count < MaximumEventHistory,
                    RuntimeAbiCodes.HandleBudget, "Sound history capacity reached.");
                context.SoundInstanceId = soundId;
                context.SoundRegistration = stop =>
                {
                    if (kernelEffects.localSounds.Count >= Limits.MaxLiveHandles)
                    {
                        stop();
                        throw new RuntimeContractException(RuntimeAbiCodes.HandleBudget, "Local sound capacity reached.");
                    }
                    kernelEffects.soundHistory.TryAdd(soundId, false);
                    kernelEffects.localSounds.Add(soundId, new LocalSound(planId, scopeId, soundSource!, stop));
                };
            }
            BeginCommandInvocation(ref context, setCurrent: false); invocationContext = context;
            executor.Invocation.Step = step;
            executor.Invocation.Play = playIdentity;
            executor.Invocation.PlayTick = playTick;
            executor.ArmTypedWrite(step, inputFrame, isHost: false, boundary: true);
            var result = executor.RenderBoundaryRows(step, NormalizeInvokedResult(handler!(context), presentation: true));
            if (!CommandResultRules.TryValidate(result, out var violation, presentation: true))
                return CommandResult.FailedUnknown(result.Outputs, "invalid-presentation-result",
                    "A presentation handler returned an invalid result (" + violation + ").");
            if (result.Status != CommandStatuses.Rejected && result.Status != CommandStatuses.Failed
                && result.Status != CommandStatuses.Cancelled && result.Status != CommandStatuses.Expired
                && result.CommitState != CommitStates.None)
            {
                // The tier's own invariant, refused rather than softened: a presentation write commits nothing, so
                // a handler that reports a commit is claiming a world write this path never performs.
                return CommandResult.Rejected(RuntimeAbiCodes.PresentationCommit,
                    "A presentation handler reports a committed result; got " + result.Status + "/" + result.CommitState + ".");
            }
            if (result.Facts.Count > 0)
            {
                // A fact is a committed write's own report, so a presentation result that carries one claims the
                // same world write a non-`none` commit does: this path publishes no fact and refuses to pass one on.
                return CommandResult.Rejected(RuntimeAbiCodes.PresentationCommit,
                    "A presentation handler reports " + result.Facts.Count + " committed fact(s); the tier writes presentation only.");
            }
            // The handler's own result is the answer, unchanged: the non-committing result is part of the tier's
            // contract, and what the handler reported as done is what reached the screen.
            return result;
        }
        catch (RuntimeContractException error)
        {
            return CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message));
        }
        catch (Exception error)
        {
            var detail = CommandResult.TruncateDetail(ThrowSite(error));
            if (invokedStep != null && log.HasSink && log.LogGate(Identity.Id).IsEnabled(RuntimeLogLevel.Error))
                LogPresentationHandlerException(error, invokedStep, planId, commandId, eventId, detail);
            return CommandResult.FailedUnknown("handler-exception", detail);
        }
        finally
        {
            if (invocationContext is { } completed) EndCommandInvocation(in completed);
            if (leased != null) leased.Leased = false;
        }
    }

    /// <summary>The first stack frame of the innermost exception as a log-safe throw site, with the counter key hashed
    /// from the complete frame, not its display preview: long signatures and different throw sites must never share a
    /// counter just because their prefixes match. Text is composed here so the record point below composes none.</summary>
    private static string PresentationThrowSite(Exception root, out string siteKey)
    {
        var frame = (root.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? "<stack unavailable>";
        siteKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(frame)));
        var site = " @ " + frame;
        if (site.Length > KernelLog.MaxLogText) site = site[..KernelLog.MaxLogText];
        return RuntimeLogValue.AccountShaped(site) ? "account-shaped" : new string(site.Select(c => c < ' ' || c == '\u007f' ? ' ' : c).ToArray());
    }

    private void LogPresentationHandlerException(Exception error, ResolvedStep step, string planId, string commandId, string eventId, string detail)
    {
        var root = error.GetBaseException();
        var site = PresentationThrowSite(root, out var siteKey);
        var record = new RuntimeLogRecord
        {
            Level = RuntimeLogLevel.Error,
            Code = RuntimeLogCodes.PresentationHandlerException,
            Provider = Identity.Id,
            Tick = CurrentTick,
            WorldEpoch = WorldEpoch,
            Binding = step.BindingId,
            CommandId = commandId,
            EventId = eventId,
            Detail = detail,
            Values = new[] { RuntimeLogValue.Of("plan", planId), RuntimeLogValue.Of("step", step.NodeId),
                RuntimeLogValue.Quote("exceptionType", root.GetType().FullName ?? root.GetType().Name),
                RuntimeLogValue.Of("throwSite", site), RuntimeLogValue.Of("totalCount", 1L) }
        };
        log.WriteRepeatedRefusal(in record, new KernelLog.RefusalSite(Cause: siteKey, StepPlan: planId, Step: step.NodeId, Reason: "handler-exception"));
    }
    /// <summary>The exception type and message plus where the innermost exception was thrown (its first stack
    /// frames), so a collected-object or null failure names the method that touched it.</summary>
    internal static string ThrowSite(Exception error)
    {
        var text = error.GetType().Name + ": " + error.Message;
        var frames = (error.GetBaseException().StackTrace ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToArray();
        return frames.Length == 0 ? text : text + " @ " + string.Join(" | ", frames);
    }
}
