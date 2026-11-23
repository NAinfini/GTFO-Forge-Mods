using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>What one step's execution writes and pays: the typed result-row regions and their renderings, the row
/// and output writers armed for the invocation in progress, the step's cost, the compiled resource reads and the
/// event text buffer. It reads the registry and the clock below it and knows nothing of the queue, the walk or the
/// effects above it.</summary>
internal sealed partial class KernelExecutor
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly EntityLives entityLives;
    private long WorldEpoch => world.WorldEpoch;

    internal KernelExecutor(KernelWorld world, KernelRegistry registry, EntityLives entityLives)
    {
        this.world = world; this.registry = registry; this.entityLives = entityLives;
        renderWriter = new Utf8JsonWriter(renderBuffer);
    }

    /// <summary>The state of the handler invocation in progress: its row writer, output sheet and deferred results.</summary>
    internal readonly CommandInvocationState Invocation = new();
    /// <summary>The set for one descriptor, count and shape, in this kernel's arena. A set is nothing but those
    /// three, so the descriptor keeps the one it registered last and a step that writes the same number of rows on
    /// every dispatch registers the same object.</summary>
    internal ResultRowSet RowSet(ResultRowDescriptor row, int count, bool set)
    {
        if (row.LastSet is { } last && last.Count == count && last.Set == set && ReferenceEquals(last.Space, resultArena)) return last;
        return row.LastSet = new ResultRowSet(row, count, set) { Space = resultArena };
    }

    /// <summary>The row and output writing armed for the invocation in progress.</summary>
    internal RowWriteState TypedWrite => Invocation.Rows;

    /// <summary>The rows of a holder-side command (an owner or presentation step run for another machine's walk):
    /// they are rendered for the network as soon as the handler returns, so they are written into a region of their
    /// own and never into the activation's.</summary>
    internal FrameSpace? boundaryRows;

    /// <summary>Whether the typed rows of the step in progress stay in their region only: set by the walk for a step
    /// nothing outside the kernel will read a document of, so no receipt rendering is made for it.</summary>
    internal bool elideRowDocument;
    /// <summary>Whether the step the walk just finished kept its rows in their region only: its log level is then
    /// read from the region, because its result carries no row document.</summary>
    internal bool rowDocumentElided;

    /// <summary>Arms the typed writers for one handler invocation of <paramref name="step"/>, right after the
    /// invocation began. <paramref name="boundary"/> writes into the holder-side region instead of the activation's.</summary>
    internal void ArmTypedWrite(ResolvedStep step, StepInputs inputs, bool isHost, bool boundary = false, RowWriteState? detached = null)
    {
        var row = step.Frames.Row;
        FrameSpace space; var baseSlot = 0;
        if (boundary)
        {
            // The region sits at the descriptor's own slot, as it does in the activation's arena, so one renderer
            // reads either space. A detached read (a retained presentation replayed outside any dispatch) writes
            // into a region of its own, because it may run while a holder-side command's rows are still being read.
            space = detached != null ? detachedRows ??= new FrameSpace(64, 64 * 1024) : boundaryRows ??= new FrameSpace(64, 64 * 1024);
            if (row != null) { if (row.Slot + row.Span > space.SlotCapacity) space.Grow(row.Slot + row.Span); baseSlot = row.Slot; }
            space.Strings.Reset();
        }
        else
        {
            space = resultArena;
            if (row != null) { EnsureResultArena(row); baseSlot = row.Slot; }
        }
        var recipients = 0;
        if (row != null && inputs.TryRecipientCount(step.ResultRecipientInput, out var count, out _)) recipients = count;
        (detached ?? TypedWrite).Arm(space, baseSlot, row, inputs, step.ResultRecipientInput, recipients, step.ManyRecipients,
            // The resolved tier, not the declaration: a host-declared action an owner-session adapter promoted runs on its holder.
            step.Execution == "host" && !step.OwnerTier, isHost, WorldEpoch, step.OutputSheet ??= OutputSheetOf(step));
    }

    /// <summary>The rows of a detached recipient read (<see cref="RebuildRecipientContext"/>): written so the handler
    /// runs as it does for a recipient command, and read by nobody afterwards.</summary>
    internal FrameSpace? detachedRows;

    internal static EvaluationOutputs OutputSheetOf(ResolvedStep step)
    {
        if (step.Contract.ValueKind != JsonValueKind.Object || !step.Contract.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Array) return new EvaluationOutputs(Array.Empty<string>(), null);
        var ids = new string[outputs.GetArrayLength()];
        var at = 0;
        foreach (var port in outputs.EnumerateArray()) ids[at++] = RuntimeJson.Text(port, "id");
        return new EvaluationOutputs(ids, null);
    }

    /// <summary>
    /// What a handler wrote through the typed writers, taken once it has returned. The side outputs become the
    /// result's own output object. The rows are already in their region: they are checked for the one thing only the
    /// end can know — one ended row per explicit recipient — and registered as the step's rows; a violation the
    /// writer kept, or a count that is not the recipient count, is the command's `failed + unknown` with the row
    /// ABI's code and leaves nothing registered. Returns false where the handler used neither writer.
    /// </summary>
    internal bool TakeTypedWrite(ResolvedStep step, ref CommandResult result, out ResultRowSet? rows, bool register)
    {
        rows = null;
        var typed = TypedWrite;
        if (!typed.Armed || !(typed.Used || typed.OutputsUsed || typed.Facts.Count > 0 || typed.FactErrorCode != null)) return false;
        if (typed.OutputsUsed && result.Outputs.ValueKind == JsonValueKind.Object) result = WithTypedOutputs(result, typed.Sheet!);
        // The facts the handler wrote by position join the ones it returned; a result that may not carry facts is
        // refused exactly as one that returned them would be, and a refused row set below publishes none of them.
        if (typed.FactErrorCode != null)
            result = CommandResult.FailedUnknown(typed.FactErrorCode, CommandResult.TruncateDetail(typed.FactErrorDetail));
        else if (typed.Facts.Count > 0)
        {
            try { result = NormalizeInvokedResult(CommandResult.WithFacts(result, typed.Facts), presentation: true); }
            catch (RuntimeContractException error) { result = CommandResult.FailedUnknown(error.Code, CommandResult.TruncateDetail(error.Message)); }
        }
        if (!typed.Used) { typed.Disarm(); return false; }
        var row = typed.Row;
        var code = typed.ErrorCode; var detail = typed.ErrorDetail;
        if (code == null && row == null)
        { code = RuntimeAbiCodes.ResultRowIncomplete; detail = "The step declares no result rows to write."; }
        if (code == null && typed.Open >= 0)
        { code = RuntimeAbiCodes.ResultRowIncomplete; detail = row!.Port + " row " + typed.Open + " was begun and never ended."; }
        if (code == null && typed.Count != typed.Recipients)
        { code = RuntimeAbiCodes.ResultRowIncomplete; detail = row!.Port + " carries " + typed.Count + " rows for " + typed.Recipients + " explicit recipients."; }
        if (code != null)
        {
            typed.Disarm();
            result = CommandResult.FailedUnknown(code, CommandResult.TruncateDetail(detail));
            return true;
        }
        rows = register ? RowSet(row!, typed.Count, typed.Set) : new ResultRowSet(row!, typed.Count, typed.Set) { Space = typed.Space! };
        if (register) resultRows[step.Frames.Index] = rows;
        typed.Disarm();
        return true;
    }

    /// <summary>One result with the outputs a handler wrote on its answer sheet (<see cref="CommandContext.Outputs"/>)
    /// added to its output object, under their contract ids and in contract order.</summary>
    internal CommandResult WithTypedOutputs(CommandResult result, EvaluationOutputs sheet)
    {
        renderBuffer.Clear();
        renderWriter.Reset(renderBuffer);
        renderWriter.WriteStartObject();
        foreach (var field in result.Outputs.EnumerateObject()) field.WriteTo(renderWriter);
        for (var index = 0; index < sheet.Cells.Length; index++)
        {
            if (!sheet.Cells[index].Carried) continue;
            renderWriter.WritePropertyName(sheet.Ids[index]);
            sheet.Cells[index].WriteValue(renderWriter, wire: true);
        }
        return RenderedResult(result);
    }

    /// <summary>A copy of <paramref name="result"/> whose outputs are the object open in the render buffer, which
    /// this closes.</summary>
    private CommandResult RenderedResult(CommandResult result)
    {
        renderWriter.WriteEndObject();
        renderWriter.Flush();
        var rendered = CommandResult.Create(result.Status, result.CommitState, result.Code, result.Detail,
            RuntimeJson.ParseWritten(renderBuffer.WrittenMemory), result.Facts.ToArray());
        rendered.CostSettlementLogDetail = result.CostSettlementLogDetail;
        return rendered;
    }

    /// <summary>A holder-side command's result with the rows its handler wrote through the typed writer rendered into
    /// its outputs: the document the network carries back to the host. A result whose handler returned its rows as
    /// JSON is already that document and is returned as it is.</summary>
    internal CommandResult RenderBoundaryRows(ResolvedStep step, CommandResult result)
    {
        if (!TakeTypedWrite(step, ref result, out var rows, register: false) || rows == null) return result;
        return WithRows(result, rows, settled: false);
    }

    /// <summary>The activation's typed result rows by step: what a later step reads when its own input is wired to a
    /// result-row output. Cleared with the activation, so a step can only read rows this activation wrote, and
    /// written only by a row set the ABI accepted — a refused set leaves nothing here to read.</summary>
    internal readonly ActivationSlots<ResultRowSet> resultRows = new();

    /// <summary>The activation's result rows: the typed region every row of this activation is written into. Its
    /// string area is rewound with the activation and every row write clears its own slots, so a later dispatch can
    /// never read an earlier one's row.</summary>
    internal readonly FrameSpace resultArena = new(1024, 256 * 1024);

    /// <summary>The buffer and writer one JSON rendering runs in. A receipt, a trace and the one read a downstream
    /// step makes all render through them, and one rendering finishes before another starts: the kernel walks one
    /// activation on one thread and no render calls back into the walk.</summary>
    internal readonly ArrayBufferWriter<byte> renderBuffer = new(1024);
    internal readonly Utf8JsonWriter renderWriter;

    /// <summary>Grows the result arena to the region a row was assigned at load. A plan's regions are absolute slots
    /// of its own descriptor, and one activation belongs to one plan, so the arena only ever has to hold the widest
    /// plan loaded.</summary>
    internal void EnsureResultArena(ResultRowDescriptor row)
    {
        var needed = row.Slot + row.Span;
        if (needed > resultArena.SlotCapacity) resultArena.Grow(needed);
    }

    /// <summary>The explicit recipients one step resolved, in the order its own input carried them: an array for a
    /// "many" port and the single value for a "one" port. A step whose inputs never resolved — a stale reference, a
    /// refused copy — reads them from the payload the step's own wiring names, because a refusal still has to report
    /// the recipients it refused for and those are the values the step would have received. Nothing is invented: a
    /// recipient the dispatch's own frame does not carry anywhere is no recipient, and no row is written for one.</summary>
    internal static RecipientSet Recipients(ResultRowDescriptor row, ResolvedStep step, InputLookup inputs, InputLookup payload)
    {
        if (row.RecipientPort.Length > 0 && inputs.TryRecipient(step, row, out var carried)) return new RecipientSet(carried);
        for (var i = 0; i < step.Inputs.Count; i++)
        {
            var input = step.Inputs[i];
            if (input.Name == row.RecipientPort && input.EventPort is { } origin && payload.TryGet(origin, out var declared))
                return new RecipientSet(declared);
        }
        return default;
    }

    /// <summary>Whether a many-recipient action's set resolved to nobody, which the walk answers `no-targets` without
    /// invoking the step. A capability that declares recipients always declares their result port, so every such
    /// action has a row descriptor. An empty set is known only after this action's inputs were fully resolved: a
    /// missing recipient, an event fallback or a step that never ran is not an empty set and cannot make count mode
    /// answer zero.</summary>
    internal static bool ResolvedEmptyRecipients(ResolvedStep step, StepInputs inputs)
        => step.ManyRecipients && inputs.TryRecipientCount(step.ResultRecipientInput, out var count, out var set) && set && count == 0;

    internal ResultRowSet RecordEmptyRows(ResolvedStep step, ResultRowDescriptor row)
    {
        EnsureResultArena(row);
        var empty = RowSet(row, 0, true);
        resultRows[step.Frames.Index] = empty;
        return empty;
    }

    /// <summary>A step's values by name, whether the walk still holds them as resolved values or a caller holds
    /// them as one JSON object; a value that is not an object carries no field.</summary>
    internal readonly struct InputLookup
    {
        private readonly StepInputs? values;
        private readonly JsonElement frame;
        private InputLookup(StepInputs? values, JsonElement frame) { this.values = values; this.frame = frame; }
        public static implicit operator InputLookup(StepInputs? values) => new(values, default);
        public static implicit operator InputLookup(JsonElement frame) => new(null, frame);
        internal bool TryGet(string name, out JsonElement value)
        {
            if (values is { } inputs) return inputs.TryGet(name, out value);
            if (frame.ValueKind == JsonValueKind.Object) return frame.TryGetProperty(name, out value);
            value = default;
            return false;
        }
        internal bool TryRecipient(ResolvedStep step, ResultRowDescriptor row, out JsonElement value)
        {
            if (values is { } inputs)
            {
                value = inputs.InputAt(step.ResultRecipientInput);
                return value.ValueKind != JsonValueKind.Undefined;
            }
            return TryGet(row.RecipientPort, out value);
        }
    }

    /// <summary>The row ABI's own code for a refusal the writer raised: the three registered codes pass through, a
    /// frame-string overflow is the row budget it belongs to, and every other shape failure is the row's own
    /// incompleteness. A caller reads one of the three codes and never a storage layer's.</summary>
    internal static string RowCode(string code) => code switch
    {
        RuntimeAbiCodes.ResultRowBudget or RuntimeAbiCodes.ResultRowStatus or RuntimeAbiCodes.ResultRowIncomplete => code,
        "frame-string-budget" => RuntimeAbiCodes.ResultRowBudget,
        _ => RuntimeAbiCodes.ResultRowIncomplete,
    };

    /// <summary>
    /// The action boundary's one row write. The handler's rows are validated against the step's compiled descriptor
    /// and written once into the activation's result arena — one row per explicit recipient, in recipient order,
    /// with each row's target the recipient at that position. The set is then the step's rows for the rest of the
    /// activation, and the receipt is rendered from the typed region, so a value the descriptor refused is not
    /// published as an output either.
    ///
    /// A handler that wrote nothing for the result port leaves the step with no rows: the port is the row's own
    /// address and a value under another name is not this ABI's row set. A malformed row set is the command's own
    /// failure — `failed + unknown` with the first exact result-row code — and publishes no facts, which is why this
    /// runs before the effect is finalized, the charge is settled and the facts are published.
    /// </summary>
    internal ResultRowSet? PublishRows(ResolvedStep step, InputLookup inputs, ref CommandResult result)
    {
        resultRows.Remove(step.Frames.Index);
        // A handler that wrote its rows through the typed writer has already written this region: there is no
        // document to parse, and the receipt's rendering is made only where something outside the kernel reads it.
        if (TakeTypedWrite(step, ref result, out var typed, register: true))
        {
            if (typed != null) result = WithRows(result, typed, settled: false);
            return typed;
        }
        if (step.Frames.Row is not { } row) return null;
        if (result.Outputs.ValueKind != JsonValueKind.Object) return null;
        if (!result.Outputs.TryGetProperty(row.Port, out var carried)) return null;
        try
        {
            // A handler that wrote a row set for a step whose explicit recipients did not resolve has written rows
            // for nobody: the raw value is refused rather than published, because a row's target is the recipient at
            // its position and a set no recipient could be isomorphic with is not a result. A resolved empty set never
            // reaches a handler (the walk answers it `no-targets`), so a handler's rows always have recipients.
            var recipients = Recipients(row, step, inputs, inputs);
            if (!(recipients.Count > 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " carries rows but the step resolved no explicit recipient to write them for.");
            EnsureResultArena(row);
            var set = carried.ValueKind == JsonValueKind.Array;
            // The row count is the recipient count, and neither the rows nor the recipients are copied out of the
            // boundary's own value: the set is read at the index a row's position names.
            var count = set ? carried.GetArrayLength() : carried.ValueKind == JsonValueKind.Object ? 1 : -1;
            if (!(count >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " carries no row set.");
            if (!(count == recipients.Count)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " carries " + count + " rows for " + recipients.Count + " explicit recipients.");
            var writer = new FrameWriter(resultArena.Slots, row.Slot, row.Span, resultArena.Strings);
            for (var index = 0; index < count; index++)
            {
                var value = set ? carried[index] : carried;
                ResultRowAbi.Write(ref writer, row, index, value, recipients.At(index));
                ResultRowAbi.ValidatePairing(row, index, value);
            }
            var written = RowSet(row, count, set);
            resultRows[step.Frames.Index] = written;
            result = WithRows(result, written, settled: false);
            return written;
        }
        catch (RuntimeContractException error)
        {
            resultRows.Remove(step.Frames.Index);
            // Nothing of the refused row set survives: the region may hold a partially written row, no set is
            // registered for the step, and the command reports the ABI's own code instead of publishing what the
            // descriptor refused.
            result = CommandResult.FailedUnknown(RowCode(error.Code), CommandResult.TruncateDetail(error.Message));
            return null;
        }
        catch
        {
            resultRows.Remove(step.Frames.Index);
            throw;
        }
    }

    /// <summary>
    /// One written row set as JSON: the ABI's own renderer over the activation's typed region, which is the one
    /// conversion a row makes on its way out of the kernel. The buffer is the kernel's, and the element returned is
    /// an independent copy of what it wrote.
    /// </summary>
    internal JsonElement RenderRows(ResultRowSet set, bool settled)
    {
        renderBuffer.Clear();
        renderWriter.Reset(renderBuffer);
        ResultRowAbi.WriteSet(renderWriter, RowFrame(set), set.Row, set.Count, set.Set, settled);
        renderWriter.Flush();
        return RuntimeJson.ParseWritten(renderBuffer.WrittenMemory);
    }

    /// <summary>The region one set's rows are in, opened for reading.</summary>
    internal Frame RowFrame(ResultRowSet set)
    {
        var space = set.Space ?? resultArena;
        return new Frame(space.Slots, set.Row.Slot, space.Strings.Memory);
    }

    /// <summary>
    /// The rows one step published, as the JSON the handler reading them takes. They are read positionally from the
    /// activation's typed region through the descriptor the plan compiled — one row per recipient, in recipient
    /// order — and never from the receipt, which is the boundary's rendering and not an execution frame. A step that
    /// wrote no rows (refused before it ran, or a handler that published none) has none to read, and the read is
    /// refused by name rather than answered with another step's or an earlier activation's rows.
    ///
    /// A row is the record of what the source command did with the recipients the plan already validated, not a live
    /// reference: the entities and handles it names were checked when it was written and are not resolved again
    /// here, so a consumer reads the identity the command acted on even after a later step of the same activation
    /// ended it.
    /// </summary>
    internal JsonElement StepRows(int stepIndex, string nodeId, string inputName)
    {
        if (!resultRows.TryGetValue(stepIndex, out var set)) throw new RuntimeContractException("from-step-value", nodeId + "." + inputName);
        return RenderRows(set, settled: true);
    }

    /// <summary>
    /// One command's outputs with its result port replaced by the rows the ABI wrote: the receipt, the trace and the
    /// network boundary's own rendering, written from the typed region so a value the writer refused is not
    /// published either. The rendering is the boundary's: it is what a caller outside the kernel reads, and the
    /// activation's internal frame carries the step's other outputs instead of it.
    /// </summary>
    internal CommandResult WithRows(CommandResult result, ResultRowSet set, bool settled)
    {
        if (elideRowDocument) return result;
        renderBuffer.Clear();
        renderWriter.Reset(renderBuffer);
        renderWriter.WriteStartObject();
        foreach (var field in result.Outputs.EnumerateObject())
        {
            if (field.NameEquals(set.Row.PortUtf8)) continue;
            field.WriteTo(renderWriter);
        }
        renderWriter.WritePropertyName(set.Row.PortUtf8);
        ResultRowAbi.WriteSet(renderWriter, RowFrame(set), set.Row, set.Count, set.Set, settled);
        return RenderedResult(result);
    }

    /// <summary>
    /// One action step's internal value frame: its outputs without the result-row port. A row is not a value — after
    /// the handler boundary it lives in the activation's typed region and a later step reads it through the
    /// descriptor — so the receipt's rendering of it never enters the frame a step reads. An action whose only
    /// output is its row publishes the shared empty frame instead of rebuilding one, and an action that wrote no
    /// row publishes its outputs as they are: they already are that frame.
    /// </summary>
    internal JsonElement ValueFrame(CommandResult result, ResultRowDescriptor? row)
    {
        if (row is not { } descriptor || result.Outputs.ValueKind != JsonValueKind.Object) return result.Outputs;
        var others = 0; var carriesRow = false;
        foreach (var field in result.Outputs.EnumerateObject())
            if (field.NameEquals(descriptor.PortUtf8)) carriesRow = true; else others++;
        if (!carriesRow) return result.Outputs;
        if (others == 0) return RuntimeJson.EmptyObject;
        // The other fields, written as the serializer writes a name-to-value map: each name through the writer's
        // own encoder, each value as its element writes itself.
        renderBuffer.Clear();
        renderWriter.Reset(renderBuffer);
        renderWriter.WriteStartObject();
        foreach (var field in result.Outputs.EnumerateObject())
        {
            if (field.NameEquals(descriptor.PortUtf8)) continue;
            renderWriter.WritePropertyName(field.Name);
            field.Value.WriteTo(renderWriter);
        }
        renderWriter.WriteEndObject();
        renderWriter.Flush();
        return RuntimeJson.ParseWritten(renderBuffer.WrittenMemory);
    }

    /// <summary>Whether a reference of this kind may name a resource its owner no longer holds (<see cref="RuntimeGraphContracts.MissingTolerantResourceKinds"/>):
    /// only while an owner is registered, so a kind nobody serves is still refused.</summary>
    internal bool ToleratesMissing(string kind)
        => RuntimeGraphContracts.MissingTolerantResourceKinds.Contains(kind) && registry.Tables.ResourceProviders.ContainsKey(kind);

    /// <summary>
    /// One compiled resource reference, resolved through the provider that owns its kind. The answer is the
    /// provider's own, so a provider that does not have the resource — including one that answers nothing yet,
    /// because its world has not been built — leaves the reference unresolved rather than invented. Load-time
    /// resolution reads this as the registry's static declaration; dispatch-time validation reads the same answer
    /// and turns a null into `stale-resource`.
    /// </summary>
    internal ResourceRef? ResolveCompiledResource(string kind, string resourceId)
    {
        if (kind == null || resourceId == null) return null;
        if (!Wire.ResourceKinds.Contains(kind)) return null;
        if (!registry.Tables.ResourceProviders.TryGetValue(kind, out var provider)) return null;
        try { return provider.Provider.Resolve(resourceId); }
        catch (Exception error) { throw new RuntimeContractException("resource-provider-failed", kind + " " + resourceId + ": " + error.GetType().Name); }
    }

    /// <summary>
    /// A compiled reference names `{id, revision}`. The owner that resolved the id must hold that same revision,
    /// otherwise the plan was compiled against another version of the resource and is `stale-resource`. A kind that
    /// carries no authored revision is listed in <see cref="RuntimeGraphContracts.RevisionExemptResourceKinds"/>;
    /// any other kind whose owner cannot answer a revision is refused instead of passed.
    /// </summary>
    internal void RequireResourceRevision(string kind, string resourceId, string revision, string at)
    {
        var provider = registry.Tables.ResourceProviders[kind].Provider;
        if (provider.Revision == null)
        {
            if (!(RuntimeGraphContracts.RevisionExemptResourceKinds.Contains(kind))) throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, at + " " + kind + " owner reports no revision");
            return;
        }
        string? held;
        try { held = provider.Revision(resourceId); }
        catch (Exception error) { throw new RuntimeContractException("resource-provider-failed", kind + " " + resourceId + ": " + error.GetType().Name); }
        if (!(string.Equals(held, revision, StringComparison.Ordinal))) throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, at + " " + kind + " " + resourceId + " revision " + revision + " is not the installed revision");
    }

    /// <summary>The buffer an event's payload text is written into where only the text is asked for — its size and
    /// its replay fingerprint — and the cells whose text it currently holds.</summary>
    internal readonly ArrayBufferWriter<byte> eventBuffer = new(256);
    internal Utf8JsonWriter? eventWriter;
    internal CommandValue[]? eventBufferCells;
    /// <summary>Moves with every text written into the buffer, so a caller can tell whether the text it wrote is
    /// still the one the buffer holds.</summary>
    internal long eventTextStamp;

    /// <summary>The payload's UTF-8 text: the document's own bytes where the event has one, and otherwise the text
    /// its cells render to, written into the kernel's buffer without making a document of it.</summary>
    internal ReadOnlySpan<byte> EventPayloadText(RuntimeEvent value)
    {
        if (value.RenderedOutputs.ValueKind != JsonValueKind.Undefined || value.Cells is not { } cells) return RawJson.Bytes(value.Outputs);
        if (!ReferenceEquals(eventBufferCells, cells))
        {
            var writer = BeginEventText();
            value.CellBinding!.WriteEvent(writer, cells);
            writer.Flush();
            eventBufferCells = cells;
        }
        return eventBuffer.WrittenSpan;
    }

    /// <summary>The text one written sheet renders to, before its event is admitted: what the payload budget judges.</summary>
    internal ReadOnlySpan<byte> SheetText(EvaluationOutputs sheet)
    {
        var writer = BeginEventText();
        sheet.Write(writer);
        writer.Flush();
        return eventBuffer.WrittenSpan;
    }

    internal Utf8JsonWriter BeginEventText()
    {
        eventBufferCells = null;
        eventTextStamp++;
        eventBuffer.Clear();
        var writer = eventWriter ??= new Utf8JsonWriter(eventBuffer);
        writer.Reset(eventBuffer);
        return writer;
    }

    internal ActionFrameSnapshot[] CaptureActionFrames(ResolvedEntry entry)
    {
        var frames = new List<ActionFrameSnapshot>();
        for (var index = 0; index < entry.Steps.Count; index++)
            if (entry.Steps[index].NodeKind == "action" && stepFrames.TryGetValue(index, out var frame)
                && frame.ValueKind == JsonValueKind.Object)
                frames.Add(new ActionFrameSnapshot(index, frame.Clone()));
        return frames.ToArray();
    }

    internal void RestoreActionFrames(ActionFrameSnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots) stepFrames[snapshot.Step] = snapshot.Frame;
    }

    internal readonly ActivationSlots<JsonElement> stepFrames = new();

    /// <summary>The slots of this activation that hold a `pure`/`query` frame: a frame validated against its own
    /// contract when it was evaluated, which no later step writes again inside the activation.</summary>
    internal readonly ActivationSlots<bool> validatedSlots = new();

    /// <summary>The entities each validated slot's port carries, with the resolver that answered for their kind.
    /// A repeated read of the same frame re-asks these resolvers instead of parsing the frame, and the values are
    /// re-read here rather than only at the frame's own validation because a handler of this activation can still
    /// end an entity's life.</summary>
    internal readonly ActivationSlots<EntityCheck[]> memoEntities = new();

    /// <summary>The references one evaluated entity port carried, collected before they are kept as the memo.</summary>
    internal readonly List<EntityCheck> evaluatedEntities = new();

    /// <summary>Writes a loop control step's value outputs for the round it is about to run: `index` counts rounds,
    /// and `item` is the candidate — or `position` the point — the round is about. A cycled walk whose round is past
    /// the end of its list uses the list from the top, so a team larger than its authored point list is placed on the
    /// list again instead of walking fewer rounds than the budget asked for.</summary>
    internal void WriteLoopFrame(in ControlFrame frame)
    {
        if (!frame.ForEach) { stepFrames[frame.Step] = RuntimeJson.From(new { index = frame.Round }); return; }
        var at = frame.Round % frame.Items.GetArrayLength();
        stepFrames[frame.Step] = frame.Positions
            ? RuntimeJson.From(new { position = frame.Items[at], index = frame.Round })
            : RuntimeJson.From(new { item = frame.Items[at], index = frame.Round });
    }

    /// <summary>
    /// One written output checked against its port, the same rules the JSON boundary applies and with the same codes:
    /// a decoded value is checked as it is, and kept decoded; a value handed over as JSON, or one whose kind the port
    /// does not take as it was written, is checked by the port's JSON rule and kept as JSON. An entity port's live
    /// references are kept as the activation's memo, so a later read of the value re-asks their resolvers only.
    /// </summary>
    internal CommandValue CheckOutput(CommandValue cell, PortView port, ResolvedStep step, int index)
    {
        var live = evaluatedEntities;
        if (cell.Typed)
        {
            if (cell.Kind == ValueKind.Null)
            {
                RuntimeJson.Require(port.Nullable, "null-input", port.Id);
                if (port.Type == "entity") { live.Clear(); KeepEntities(step, index, live); }
                return cell;
            }
            switch (port.Kind)
            {
                case PortView.Kinds.Boolean when cell.Kind == ValueKind.Boolean:
                    return cell;
                case PortView.Kinds.Integer when cell.Kind == ValueKind.Integer:
                    RuntimeJson.Require(cell.Integer >= -RuntimeJson.MaxSafeInteger && cell.Integer <= RuntimeJson.MaxSafeInteger,
                        "invalid-integer", "Expected bounded safe integer.");
                    return cell;
                case PortView.Kinds.Integer when cell.Kind == ValueKind.Number && double.IsFinite(cell.Number):
                    RuntimeJson.Require(cell.Number == Math.Truncate(cell.Number) && Math.Abs(cell.Number) <= RuntimeJson.MaxSafeInteger,
                        "invalid-integer", "Expected bounded safe integer.");
                    return CommandValue.OfInteger((long)cell.Number);
                case PortView.Kinds.Number when cell.Kind == ValueKind.Number:
                    RuntimeJson.Require(double.IsFinite(cell.Number), "invalid-number", "Expected finite number.");
                    return cell;
                case PortView.Kinds.Number when cell.Kind == ValueKind.Integer:
                    return CommandValue.OfNumber(cell.Integer);
                case PortView.Kinds.String when cell.Kind == ValueKind.String:
                    RuntimeJson.Require(((string)cell.Reference!).Length <= 4096, "invalid-string", "Expected bounded string.");
                    return cell;
                case PortView.Kinds.Enum when cell.Kind is ValueKind.String or ValueKind.Integer:
                    {
                        var names = port.EnumNames!;
                        var member = cell.Kind == ValueKind.Integer
                            ? cell.Integer >= 0 && cell.Integer < names.Length ? (int)cell.Integer : -1
                            : Array.IndexOf(names, (string)cell.Reference!);
                        RuntimeJson.Require(member >= 0, "invalid-enum", "Expected enum member index.");
                        return CommandValue.OfEnum(names[member], member, port.EnumMembers![member], port.EnumIndices![member]);
                    }
                case PortView.Kinds.Entity when !port.Many && cell.Reference is EntityReference single:
                    live.Clear();
                    entityLives.CheckEntity(single, live);
                    KeepEntities(step, index, live);
                    return cell;
                case PortView.Kinds.Entity when port.Many && cell.Reference is EntityReference[] many:
                    RuntimeJson.Require(many.Length <= 256, "invalid-array", "entity many");
                    RuntimeJson.RequireDistinct(many);
                    live.Clear();
                    for (var i = 0; i < many.Length; i++) entityLives.CheckEntity(many[i], live);
                    KeepEntities(step, index, live);
                    return cell;
                case PortView.Kinds.Other when port.Type == "vector3" && !port.Many && cell.Kind == ValueKind.Vector3
                    && double.IsFinite(cell.Number) && double.IsFinite(cell.Y) && double.IsFinite(cell.Z):
                    return cell;
            }
            cell = CommandValue.OfJson(cell.Rendered().WireJson);
        }
        // An entity port's references are parsed once here, checked and kept: every later read of this frame in this
        // activation re-asks their resolvers instead of parsing the list again.
        if (port.Type != "entity") { ResolveInput(cell.Json, port, null, frame: true); return cell; }
        live.Clear();
        ResolveInput(cell.Json, port, live, frame: true);
        KeepEntities(step, index, live);
        return cell;
    }

    /// <summary>Keeps one entity output's live references as the activation's memo. An evaluation that answered the
    /// same references as the last one keeps the array it kept then.</summary>
    internal void KeepEntities(ResolvedStep step, int index, List<EntityCheck> live)
    {
        var kept = step.EvaluatedEntities![index];
        var same = kept != null && kept.Length == live.Count;
        for (var i = 0; same && i < live.Count; i++)
            same = ReferenceEquals(kept![i].Kind, live[i].Kind) && (ReferenceEquals(kept[i].Entity, live[i].Entity)
                || kept[i].Entity.WorldEpoch == live[i].Entity.WorldEpoch && kept[i].Entity.LifeEpoch == live[i].Entity.LifeEpoch
                && string.Equals(kept[i].Entity.Id, live[i].Entity.Id, StringComparison.Ordinal));
        if (!same) step.EvaluatedEntities[index] = kept = live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();
        memoEntities[step.EntityMemoBase + index] = kept!;
    }

    /// <summary>One event against its trigger's value outputs, and its cells with it: the values a publisher wrote on
    /// the gate's sheet, or the document it handed over.</summary>
    internal EventSubject AdmitEvent(RuntimeEvent value, BindingView binding, RuntimeSubscriptionGate? written)
        => written != null ? AdmitWritten(value, binding, written.EventValues!) : AdmitDocument(value, binding);

    internal void ReserveActivationSlots(ResolvedPlan plan)
    {
        this.resultRows.Reserve(plan.Frames.Steps.Length);
        BindEventCells(plan);
        foreach (var entry in plan.Entries)
        {
            stepFrames.Reserve(entry.Steps.Count);
            validatedSlots.Reserve(entry.Steps.Count);
            var memo = 0;
            foreach (var step in entry.Steps)
            {
                if (step.Frames.Row is { } row)
                {
                    step.ResultRecipientInput = Array.IndexOf(step.HandlerInputs, row.RecipientPort);
                    step.ManyRecipients = row.RecipientPort.Length > 0 && step.Contract.ValueKind == JsonValueKind.Object
                        && step.Contract.TryGetProperty("recipients", out var recipients)
                        && RuntimeJson.Text(recipients, "cardinality") == "many";
                    this.EnsureResultArena(row);
                }
                step.EntityMemoBase = memo;
                memo += step.OutputViews?.Length ?? 0;
            }
            memoEntities.Reserve(memo);
        }
    }

    /// <summary>One actual action arrival owns a fresh result, even when a sequence reaches the same node twice
    /// without changing activation. Remove only frames derived from this result; do not reset unrelated pure or
    /// world-query memoization and do not re-run any action or control as part of invalidation.</summary>
    internal void BeginActionResult(ResolvedEntry entry, int stepIndex, ResolvedStep step)
    {
        this.resultRows.Remove(step.Frames.Index);
        stepFrames.Remove(stepIndex);
        foreach (var dependent in step.ResultDerivedSteps)
        {
            entry.Steps[dependent].OutputActivation = -1;
            stepFrames.Remove(dependent);
            validatedSlots.Remove(dependent);
            if (entry.Steps[dependent].OutputViews is not { } outputs) continue;
            for (var port = 0; port < outputs.Length; port++)
                memoEntities.Remove(entry.Steps[dependent].EntityMemoBase + port);
        }
    }

    /// <summary>The same reading for a step that was refused before it ran, whose fallback is the dispatch's own
    /// event: the cell the step's recipient input is wired to.</summary>
    internal RecipientSet RefusedRecipients(ResultRowDescriptor row, ResolvedStep step, KernelExecutor.InputLookup inputs, RuntimeEvent origin)
    {
        if (row.RecipientPort.Length > 0 && inputs.TryRecipient(step, row, out var carried)) return new RecipientSet(carried);
        for (var i = 0; i < step.Inputs.Count; i++)
        {
            var input = step.Inputs[i];
            if (input.Name == row.RecipientPort && input.EventPort != null && EventCell(origin, input.EventCell).Carried)
                return new RecipientSet(EventCellJson(origin, input.EventCell));
        }
        return default;
    }

    /// <summary>
    /// The rows a kernel refusal writes: one deterministic row per explicit recipient, through the same descriptor
    /// and the same region the handler's rows go through, carrying the command's own conclusion so that conclusion
    /// stays derivable from the complete row set. The target is the recipient's own identity and never an invented
    /// or coerced one, and a refusal whose recipients are nowhere in the dispatch — or whose set is wider than the
    /// contract allows — writes no rows at all rather than a row nobody could be isomorphic with.
    /// </summary>
    internal CommandResult RefuseWithRows(ResolvedStep step, StepInputs? inputs, RuntimeEvent origin, CommandResult refused)
    {
        this.resultRows.Remove(step.Frames.Index);
        if (step.Frames.Row is not { } row) return refused;
        var recipients = RefusedRecipients(row, step, inputs, origin);
        if (recipients.Count == 0)
        {
            if (inputs != null && KernelExecutor.ResolvedEmptyRecipients(step, inputs)) this.RecordEmptyRows(step, row);
            return refused;
        }
        if (recipients.Count > row.Capacity) return refused;
        try
        {
            this.EnsureResultArena(row);
            var writer = new FrameWriter(this.resultArena.Slots, row.Slot, row.Span, this.resultArena.Strings);
            for (var index = 0; index < recipients.Count; index++)
                ResultRowAbi.WriteRefusal(ref writer, row, index, recipients.At(index), refused.Status, refused.CommitState, refused.Code);
            var set = this.RowSet(row, recipients.Count, true);
            this.resultRows[step.Frames.Index] = set;
            return this.WithRows(refused, set, settled: false);
        }
        catch (RuntimeContractException)
        {
            this.resultRows.Remove(step.Frames.Index);
            return refused;
        }
        catch
        {
            this.resultRows.Remove(step.Frames.Index);
            throw;
        }
    }

    /// <summary>Resolves, once per loaded plan, where each of its readers finds its value in an event's cells: the
    /// step inputs wired to an event output, the entry's own trigger ports, its structural enum filters and the ports
    /// its mounts read.</summary>
    internal void BindEventCells(ResolvedPlan plan)
    {
        foreach (var entry in plan.Entries)
        {
            if (!registry.BindingViews.TryGetValue(entry.BindingId, out var binding)) continue;
            var ports = entry.TriggerOutputs;
            var cells = new int[ports.Length];
            for (var i = 0; i < ports.Length; i++) cells[i] = binding.CellOf(ports[i].Id);
            entry.TriggerCells = cells;
            entry.GateAmountCell = entry.Gate?.AccumulateAmount is { } amount ? binding.CellOf(amount) : -1;
            if (entry.EnumFilters is { } filters)
                foreach (var filter in filters) filter.Cell = binding.CellOf(filter.Port);
            foreach (var attachment in entry.Attachments)
            {
                attachment.PortCells = Array.ConvertAll(attachment.Ports, port => binding.CellOf(port.Id));
                if (attachment.EquipmentAttachment is not { } roles) continue;
                attachment.HolderCell = binding.CellOf(roles.Holder.Id);
                attachment.EquipmentCell = binding.CellOf(roles.Equipment.Id);
            }
            foreach (var step in entry.Steps)
                foreach (var input in step.Inputs)
                {
                    if (input.EventPort is not { } port) continue;
                    input.EventCell = binding.CellOf(port);
                    input.EventCellDecoded = input.EventCell >= 0 && DecodedEventPort(binding.Outputs[input.EventCell])
                        && SameValueRule(binding.Outputs[input.EventCell], input.View);
                }
        }
    }

    /// <summary>The cells of one event. An event the kernel admitted carries them; one that reached a queue without
    /// an admission of this kernel — a migration image's — is read here the first time something asks, by the same
    /// rule and without asking whether its entities still live: the importing host answers that where they are read.</summary>
    internal CommandValue[] CellsOf(RuntimeEvent value)
    {
        if (value.Cells is { } cells) return cells;
        var payload = value.RenderedOutputs;
        if (payload.ValueKind != JsonValueKind.Object || !registry.BindingViews.TryGetValue(value.BindingId, out var binding))
            return value.Cells = Array.Empty<CommandValue>();
        var outputs = binding.Outputs;
        cells = new CommandValue[outputs.Length];
        for (var i = 0; i < outputs.Length; i++)
            if (payload.TryGetProperty(outputs[i].IdUtf8, out var data)) cells[i] = ReadEventCell(data, outputs[i], live: false);
        value.CellBinding = binding;
        return value.Cells = cells;
    }

    /// <summary>One cell of an event, or an empty cell where the event carries nothing there.</summary>
    internal CommandValue EventCell(RuntimeEvent value, int cell)
    {
        var cells = CellsOf(value);
        return (uint)cell < (uint)cells.Length ? cells[cell] : default;
    }

    /// <summary>One event's document payload against its trigger's value outputs: the shape, then every carried
    /// port's value and entities, port by port in contract order. What it read is kept as the event's cells.</summary>
    internal EventSubject AdmitDocument(RuntimeEvent value, BindingView binding)
    {
        RuntimeJson.Require(binding.IsTrigger, "not-trigger", value.BindingId);
        var outputs = binding.Outputs;
        // A written event revalidated here (a delay's captured template) is rendered once from its cells.
        var payload = value.Outputs;
        ShapePorts(payload, outputs, optionalAllowed: true);
        var cells = new CommandValue[outputs.Length];
        for (var i = 0; i < outputs.Length; i++)
            if (payload.TryGetProperty(outputs[i].IdUtf8, out var data)) cells[i] = ReadEventCell(data, outputs[i], live: true);
        value.Cells = cells; value.CellBinding = binding;
        return SubjectOf(cells, binding);
    }

    /// <summary>One event written by output position against the same outputs, by the same rules and with the same
    /// codes: a position that is no value output is the unknown field, an unwritten required output the missing one,
    /// and each written value is checked as it is where its kind is the port's own, and as the document it renders
    /// to otherwise. The sheet's cells are copied: the sheet is the binding's and takes the next event.</summary>
    /// <param name="partial">A client's own copy of a fact it reported (<see cref="RuntimeKernel.Predict"/>): an output only
    /// the host can name is left unwritten, and its cell stays not carried instead of refusing the event.</param>
    internal EventSubject AdmitWritten(RuntimeEvent value, BindingView binding, EvaluationOutputs sheet, bool partial = false)
    {
        var outputs = binding.Outputs;
        var written = sheet.Cells;
        for (var position = 0; position < written.Length; position++)
            if (written[position].Carried && binding.SheetCells[position] < 0)
                throw new RuntimeContractException("unknown-field", binding.OutputIds[position]);
        for (var i = 0; i < outputs.Length; i++)
            if (!partial && !outputs[i].Optional && !written[binding.CellSheet[i]].Carried)
                throw new RuntimeContractException("missing-field", outputs[i].Id);
        var cells = new CommandValue[outputs.Length];
        for (var i = 0; i < outputs.Length; i++)
        {
            var cell = written[binding.CellSheet[i]];
            if (cell.Carried) cells[i] = AdmitWrittenCell(cell, outputs[i]);
        }
        value.Cells = cells; value.CellBinding = binding;
        return SubjectOf(cells, binding);
    }

    internal CommandValue AdmitWrittenCell(CommandValue cell, PortView port)
    {
        if (!cell.Typed) return ReadEventCell(cell.Json, port, live: true);
        if (cell.Kind == ValueKind.Null)
        {
            RuntimeJson.Require(port.Nullable, "null-input", port.Id);
            return cell;
        }
        switch (port.Kind)
        {
            case PortView.Kinds.Boolean when cell.Kind == ValueKind.Boolean:
                return cell;
            case PortView.Kinds.Integer when cell.Kind == ValueKind.Integer:
                RuntimeJson.Integer(cell.Integer, -RuntimeJson.MaxSafeInteger);
                return cell;
            case PortView.Kinds.Number when cell.Kind == ValueKind.Number && double.IsFinite(cell.Number):
                return cell;
            case PortView.Kinds.String when cell.Kind == ValueKind.String:
                RuntimeJson.Require(((string)cell.Reference!).Length <= Wire.PlanJson.MaxStringValueLength, "invalid-string", "Expected bounded string.");
                return cell;
            case PortView.Kinds.Enum when cell.Kind == ValueKind.Integer:
                RuntimeJson.Require(cell.Integer >= 0 && cell.Integer < port.EnumNames!.Length, "invalid-enum", "Expected enum member index.");
                var member = (int)cell.Integer;
                return CommandValue.OfEnum(port.EnumNames![member], member, port.EnumMembers![member], port.EnumIndices![member]);
            case PortView.Kinds.Entity when !port.Many && cell.Reference is EntityReference { Id: not null } single:
                entityLives.CheckEntity(single);
                return cell;
            case PortView.Kinds.Entity when port.Many && cell.Reference is EntityReference[] many && Array.TrueForAll(many, static entity => entity.Id != null):
                // A value arriving at a port refuses a malformed reference anywhere in the list first, then a
                // repeated one, then one that no longer lives: the order the document form is read in.
                RuntimeJson.Require(many.Length <= Wire.MaxSetWidth, "invalid-array", "entity many");
                for (var i = 0; i < many.Length; i++)
                { RuntimeJson.Text(many[i].Id); RuntimeJson.Integer(many[i].WorldEpoch); RuntimeJson.Integer(many[i].LifeEpoch); }
                for (var i = 1; i < many.Length; i++) RequireDistinct(many, i, many[i]);
                for (var i = 0; i < many.Length; i++) entityLives.CheckEntity(many[i]);
                return cell;
            case PortView.Kinds.Other when port.Type == "vector3" && !port.Many && cell.Kind == ValueKind.Vector3
                && double.IsFinite(cell.Number) && double.IsFinite(cell.Y) && double.IsFinite(cell.Z):
                return cell;
        }
        return ReadEventCell(cell.Rendered().WireJson, port, live: true);
    }

    /// <summary>One port's document value as the event's cell: checked against the port (and, where
    /// <paramref name="live"/>, against the providers that answer for its entities), then decoded exactly as the step
    /// that reads it would decode it. A kind with no decoded form keeps the value itself.</summary>
    internal CommandValue ReadEventCell(JsonElement data, PortView port, bool live)
    {
        var decoded = ResolveInput(data, port, validateEntityLifetimes: live);
        if (data.ValueKind == JsonValueKind.Null) return CommandValue.Read(data, port.HandlerKind, false);
        switch (port.Kind)
        {
            case PortView.Kinds.Boolean:
            case PortView.Kinds.Integer:
            case PortView.Kinds.Number:
            case PortView.Kinds.String:
                return CommandValue.Read(data, port.HandlerKind, false);
            case PortView.Kinds.Enum:
                var member = (int)data.GetDouble();
                return CommandValue.OfEnum(port.EnumNames![member], member, port.EnumMembers![member], data);
            case PortView.Kinds.Entity:
                return CommandValue.Read(data, ValueKind.Entity, port.Many, decoded);
            case PortView.Kinds.Other when port.Type == "vector3" && !port.Many:
                return CommandValue.Read(data, ValueKind.Vector3, false);
            case PortView.Kinds.Other when port.Type == "entity":
                // A contract the typed view could not read still names entities the claim filters ask about.
                var many = RuntimeGraphContracts.Many(port.Contract);
                if (!many) return CommandValue.Read(data, ValueKind.Entity, false, entityLives.ReadEntity(data));
                var references = new EntityReference[data.GetArrayLength()];
                var index = 0;
                foreach (var item in data.EnumerateArray()) references[index++] = entityLives.ReadEntity(item);
                return CommandValue.Read(data, ValueKind.Entity, true, references);
            default:
                return CommandValue.OfJson(data);
        }
    }

    /// <summary>One event cell as the value a frame and the wire carry, written the first time a reader that takes
    /// documents asks for it and kept with the event.</summary>
    internal JsonElement EventCellJson(RuntimeEvent value, int cell)
    {
        var cells = CellsOf(value);
        if (cells[cell].WireJson.ValueKind == JsonValueKind.Undefined) cells[cell] = cells[cell].Rendered();
        return cells[cell].WireJson;
    }

    internal void WriteGroupFrame(int stepIndex, FlowGroup group)
        => stepFrames[stepIndex] = RuntimeJson.From(new { group = group.Job.Resume!.HandleValue, attempt = group.Attempt });

    /// <summary>
    /// Resolves one resource reference at the kernel boundary, for a value an event payload or a plan's own
    /// compile-time reference carries. A resource the owner no longer answers for was compiled for a world that has
    /// ended: that is `stale-resource`, and it is a rejection here rather than a value that travels on and is
    /// refused later at whichever step happens to read it.
    /// </summary>
    internal ResourceRef? ValidateResourceValue(JsonElement value, JsonElement port)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (RuntimeJson.Text(port, "type") != "resource") return null;
        // The shape is read first, so a malformed reference is refused as one wherever it arrived from; whether
        // the resource is still there is asked second and only about kind and id.
        var reference = RuntimeJson.ResourceRefOf(value);
        RuntimeJson.Require(reference.ResourceKind == RuntimeJson.Text(port, "resourceKind"),
            RuntimeAbiCodes.ResourceKind, reference.ResourceId);
        if (world.StartupState != RuntimeStartupState.Ready || !world.worldStarted) return reference;
        if (!(this.ToleratesMissing(reference.ResourceKind) || this.ResolveCompiledResource(reference.ResourceKind, reference.ResourceId) != null)) throw new RuntimeContractException(RuntimeAbiCodes.StaleResource, reference.ResourceKind + " " + reference.ResourceId);
        return reference;
    }

    /// <summary>The entities one captured template names, with the resolver that answers for each kind. The capture
    /// is a snapshot and its ports are fixed by the capability it was validated against, so this list is the whole
    /// time-dependent part of the capture and the only thing a later frame re-asks.</summary>
    internal EntityCheck[] LivenessOf(RuntimeEvent template, BindingView binding)
    {
        var live = new List<EntityCheck>();
        var cells = CellsOf(template);
        for (var i = 0; i < cells.Length; i++)
        {
            if (binding.Outputs[i].Type != "entity") continue;
            if (cells[i].Reference is EntityReference single) Collect(single);
            else if (cells[i].Reference is EntityReference[] many) foreach (var item in many) Collect(item);
        }
        return live.Count == 0 ? Array.Empty<EntityCheck>() : live.ToArray();
        // The reference was validated and its resolver proved present by the check that accepted the capture, so
        // this reads the resolver the entity will be re-asked of and resolves nothing now.
        void Collect(EntityReference entity)
        {
            if (entityLives.TryEntityCheck(entity, out var check)) live.Add(check);
        }
    }

    /// <summary>
    /// One value against the typed port that carries it, and every entity reference it names against the provider
    /// that answers for the reference's kind: the value's form, a resource's owner, an event's row, and the liveness
    /// of its entities, in that order. A reference is parsed once for both halves. <paramref name="live"/> collects
    /// each reference with the resolver that answered for it, for a later read of the same value.
    /// </summary>
    /// <remarks>A <paramref name="frame"/> value is one a `pure`/`query` step produced: its entity port is read by
    /// its type alone and a repeated reference is refused as soon as it is read, which is the order that frame's
    /// check has always had; a value arriving at a port refuses a malformed reference anywhere in the list first.</remarks>
    internal object? ResolveInput(JsonElement value, PortView port, List<EntityCheck>? live = null, bool frame = false, bool validateEntityLifetimes = true)
    {
        if (port.Type == "entity" && (frame || port.Kind == PortView.Kinds.Entity))
        {
            if (value.ValueKind == JsonValueKind.Null) { RuntimeJson.Require(port.Nullable, "null-input", port.Id); return null; }
            var many = port.Kind == PortView.Kinds.Other ? RuntimeGraphContracts.Many(port.Contract) : port.Many;
            if (!many) { var entity = entityLives.ReadEntity(value); if (validateEntityLifetimes) entityLives.CheckEntity(entity, live); return entity; }
            RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 256, "invalid-array", "entity many");
            var count = value.GetArrayLength();
            if (count == 0) return Array.Empty<EntityReference>();
            var references = new EntityReference[count];
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var reference = entityLives.ReadEntity(item);
                if (frame) RequireDistinct(references, index, reference);
                references[index++] = reference;
            }
            if (!frame) for (var i = 1; i < count; i++) RequireDistinct(references, i, references[i]);
            if (validateEntityLifetimes) for (var i = 0; i < count; i++) entityLives.CheckEntity(references[i], live);
            return references;
        }
        // Only an event value is read against the dispatch's own event table, so only one builds it.
        port.Validate(value, port.Type == "event" ? EventRows : null);
        ResourceRef? resource = null;
        if (port.Type == "resource" && value.ValueKind != JsonValueKind.Null)
        {
            if (!port.Many) resource = ValidateResourceValue(value, port.Contract);
            else foreach (var member in value.EnumerateArray()) ValidateResourceValue(member, port.Contract);
        }
        // A contract the typed view could not read is checked by its own fields, entities included.
        if (validateEntityLifetimes && port.Kind == PortView.Kinds.Other && port.Type == "entity" && value.ValueKind != JsonValueKind.Null)
        {
            if (!RuntimeGraphContracts.Many(port.Contract)) entityLives.CheckEntity(entityLives.ReadEntity(value), live);
            else foreach (var item in value.EnumerateArray()) entityLives.CheckEntity(entityLives.ReadEntity(item), live);
        }
        return resource;
    }

    /// <summary>Decodes the JSON form used only across the owner/presentation network and migration boundaries into
    /// the same physical cells a local walk produces. Name reads end here; the handler receives a resolved frame.</summary>
    internal StepInputs ResolveBoundaryStepInputs(ResolvedStep step, JsonElement inputs, out JsonElement parameters,
        bool referenceConstants = false, JsonElement wireParameters = default, bool validateEntityLifetimes = true,
        StepInputs? into = null)
    {
        var frame = into?.Reset() ?? new StepInputs(step);
        for (var index = 0; index < step.Inputs.Count; index++)
        {
            var input = step.Inputs[index];
            var slot = step.InputSlots[index];
            if (slot < 0)
            {
                if (wireParameters.ValueKind == JsonValueKind.Undefined) continue;
                var parameter = -1 - slot;
                var rule = step.PromotedParameterRules[step.ParameterOverrideMap[parameter]];
                if (!wireParameters.TryGetProperty(input.Name, out var promoted))
                {
                    rule.Validate(default);
                    continue;
                }
                input.View.Validate(promoted, null);
                rule.Validate(promoted);
                frame.SetParameter(parameter, input.View.HandlerValue(promoted), promoted,
                    step.HandlerParameterKinds[parameter], resourceResolver: input.ResourceResolver);
                continue;
            }
            if (referenceConstants && input.HandlerLiteral is { } literal)
            {
                if (!(!inputs.TryGetProperty(input.Name, out _))) throw new RuntimeContractException("invalid-presentation-input", step.NodeId + "." + input.Name + " must reference the loaded plan constant.");
                frame.Set(slot, literal, input.View, null, input.ResourceResolver, step.InputConstantValues[index],
                    input.Wrap);
                continue;
            }
            if (!inputs.TryGetProperty(input.Name, out var value))
            {
                if (!(input.View.Optional)) throw new RuntimeContractException("missing-input", step.NodeId + "." + input.Name);
                continue;
            }
            var decodedValue = value;
            if (input.Wrap)
            {
                if (!(value.ValueKind == JsonValueKind.Array)) throw new RuntimeContractException("array-required", step.NodeId + "." + input.Name + " must carry the compiled singleton collection.");
                if (!(value.GetArrayLength() == 1)) throw new RuntimeContractException("port-cardinality", step.NodeId + "." + input.Name + " must carry exactly one wrapped value.");
                decodedValue = value[0];
            }
            var decoded = ResolveInput(decodedValue, input.View, validateEntityLifetimes: validateEntityLifetimes);
            frame.Set(slot, input.View.HandlerValue(value), input.View, decoded, input.ResourceResolver,
                wrapped: input.Wrap, wireValue: value);
        }
        if (wireParameters.ValueKind != JsonValueKind.Undefined)
        {
            RuntimeJson.Parameters(wireParameters, step.Capability);
            parameters = RuntimeJson.ResolveEnumParameters(wireParameters, step.Capability);
            frame.SetParameterDocuments(parameters, wireParameters);
        }
        else parameters = step.HandlerParameters.ValueKind != JsonValueKind.Undefined ? step.HandlerParameters
            : RuntimeJson.ResolveEnumParameters(step.Parameters, step.Capability);
        if (wireParameters.ValueKind == JsonValueKind.Undefined)
            frame.SetParameterDocuments(parameters, step.Parameters);
        return frame;
    }

    /// <summary>The dispatch currently being walked, and its rows: the only table an event value is ever resolved
    /// against. The table is written from the dispatch the first time the walk reads it — a dispatch is walked once
    /// — so a dispatch whose steps never read an event row builds none. Both are null outside a dispatch, where an
    /// event value has nothing to name.</summary>
    internal Pending? currentPending;

    internal RuntimeEventRows? currentRows;

    /// <summary>The rows of the dispatch being walked, or null outside one.</summary>
    internal RuntimeEventRows? EventRows => currentPending is { Tabled: true } pending
        ? currentRows ??= EventRowsOf(pending.Work, pending.Event, pending.Sequence) : null;
}
