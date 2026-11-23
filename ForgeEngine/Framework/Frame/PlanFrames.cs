using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Where one step input's value comes from at dispatch. <see cref="Constant"/> also covers a literal
/// the input itself carries: both are read from the plan's constant pool.</summary>
internal enum PortSource { Constant = 0, Event = 1, Pure = 2 }

/// <summary>
/// One port's address inside a frame: the slot the port starts at, its physical kind, the number of slots it
/// may occupy, and — for a step input — where the value is copied from. Name plus this descriptor is what makes
/// a port name a load-time spelling: dispatch moves values between the offsets recorded here, never by name.
/// </summary>
internal readonly struct PortSlot
{
    internal PortSlot(int slot, ValueKind kind, int width, PortSource source, int sourceSlot)
    { Slot = slot; Kind = kind; Width = width; Source = source; SourceSlot = sourceSlot; }

    /// <summary>First slot of the port; -1 for the structural <c>execution</c> port and for a parameter the plan
    /// authored no value for, neither of which carries a value. A parameter's slot is a constant-pool slot when its
    /// source is <see cref="PortSource.Constant"/> and a plan frame slot otherwise.</summary>
    internal int Slot { get; }
    internal ValueKind Kind { get; }
    /// <summary>Slots this port reserves: 1 for a single value, 3 for a vector3, 2 for a resource, the row's own
    /// width for a result, and one head slot plus <c>Wire.MaxSetWidth</c> elements for a collection.</summary>
    internal int Width { get; }
    /// <summary>Inputs only: where the dispatch copy reads from.</summary>
    internal PortSource Source { get; }
    /// <summary>Inputs only: the constant slot, the event-frame slot, or the pure output's port index; -1 when
    /// nothing writes the slot at all, which is how an optional input the plan left unwired stays Missing.</summary>
    internal int SourceSlot { get; }
    /// <summary>Inputs only: the absolute step index a <see cref="PortSource.Pure"/> input reads, else -1.</summary>
    internal int SourceStep { get; init; }
    /// <summary>A one-element value wired into a "many" input (D-006②): the copy writes element 0 and Count = 1.</summary>
    internal bool WrapsAsSet { get; init; }
    /// <summary>True for a collection port. It is the port's declared cardinality, never inferred from
    /// <see cref="Width"/>: a collection's reserved width is its own kind's segment, which is not a value's width.</summary>
    internal bool Many { get; init; }
}

/// <summary>
/// One step's frame regions. <see cref="InputBase"/>/<see cref="OutputBase"/>/<see cref="PureMemoBase"/> are
/// absolute slots of the plan's frame space; the regions of a step are contiguous, so a step's whole footprint
/// is one span (value slots and string bytes are counted separately). Steps in one entry never share a slot;
/// different entries reuse the arena because the kernel walks one activation at a time.
/// </summary>
internal sealed class StepFrames
{
    internal StepFrames(int index, int inputBase, int outputBase, int pureMemoBase, int span, int valueSlots,
        PortSlot[] inputs, PortSlot[] parameters, PortSlot[] outputs, int[] successors, int bindingIndex, int handlerIndex)
    {
        Index = index; InputBase = inputBase; OutputBase = outputBase; PureMemoBase = pureMemoBase; Span = span; ValueSlots = valueSlots;
        Inputs = inputs; Parameters = parameters; Outputs = outputs; Successors = successors; BindingIndex = bindingIndex; HandlerIndex = handlerIndex;
    }

    /// <summary>Position in the plan-wide step array, which is what a `fromStepSlot` step index addresses.</summary>
    internal int Index { get; }
    internal int InputBase { get; }
    /// <summary>Result-row region; -1 on a `pure` step, whose values live in <see cref="PureMemoBase"/>.</summary>
    internal int OutputBase { get; }
    /// <summary>Memo region of a `pure` step; -1 elsewhere. "Not evaluated yet" is its first slot being Missing.</summary>
    internal int PureMemoBase { get; }
    /// <summary>Slots this step may touch, from <see cref="InputBase"/> to the end of its last region. A collection
    /// port reserves its whole declared width here, so this is the footprint an arena has to hold.</summary>
    internal int Span { get; }
    /// <summary>Slots this step is charged for: everything but a collection port's reserved width, which is
    /// budgeted separately. This is what <see cref="RuntimeLimits.MaxValueSlotsPerStep"/> bounds.</summary>
    internal int ValueSlots { get; }
    /// <summary>Same order as the resolved contract's inputs; the index is the plan's dense input slot.</summary>
    internal PortSlot[] Inputs { get; }
    /// <summary>The capability's parameters in declaration order — a constant pool slot, or the input port a
    /// promoted parameter's value arrives through, or no slot at all when the plan authored no value. A handler
    /// shape addresses this table by the parameter's declaration index, so no parameter is read by name.</summary>
    internal PortSlot[] Parameters { get; }
    /// <summary>Same order as the resolved contract's outputs.</summary>
    internal PortSlot[] Outputs { get; }
    /// <summary>Step indices in the plan-wide array; -1 marks a null successor.</summary>
    internal int[] Successors { get; }
    /// <summary>Index into the plan's pin table — the binding this step executes, by position, not by string.</summary>
    internal int BindingIndex { get; }
    /// <summary>Handler table index, or -1 for a step the kernel walks itself (`control`) and for `pure` steps.</summary>
    internal int HandlerIndex { get; }
    /// <summary>The step's own result row as the plan compiled it, or null on a step that declares no result port.
    /// It is the one descriptor the action boundary writes through: the recipient it is about, the row's stride and
    /// column offsets, the slot its region starts at, and the codes its port declares.</summary>
    internal ResultRowDescriptor? Row { get; init; }
}

/// <summary>
/// One step as the frame builder needs it: its capability graph (whose parameters are the literals and whose
/// ports are the resolved contract), the positionally compiled constants of those parameters, the promoted set
/// that tells a literal from a promoted value, the node's kind, and the plan-side wiring it was resolved to.
/// All of it is already validated when the builder runs, apart from the value parameters, whose own definition
/// is what the builder is the first and only place to check them against.
/// </summary>
internal sealed class StepContract
{
    internal StepContract(string nodeId, string nodeKind, JsonElement graph, JsonElement contract, JsonElement[] constants,
        IReadOnlySet<string> promoted, IReadOnlyList<StepInput> inputs, IReadOnlyList<int?> successors, int bindingIndex, int handlerIndex)
    {
        NodeId = nodeId; NodeKind = nodeKind; Graph = graph; Contract = contract; Constants = constants; Promoted = promoted;
        Inputs = inputs; Successors = successors; BindingIndex = bindingIndex; HandlerIndex = handlerIndex;
    }

    internal string NodeId { get; }
    internal string NodeKind { get; }
    internal JsonElement Graph { get; }
    internal JsonElement Contract { get; }
    /// <summary>`layout.constants`, one entry per declared parameter, null where the plan promoted or omitted it.</summary>
    internal JsonElement[] Constants { get; }
    internal IReadOnlySet<string> Promoted { get; }
    internal IReadOnlyList<StepInput> Inputs { get; }
    internal IReadOnlyList<int?> Successors { get; }
    internal int BindingIndex { get; }
    internal int HandlerIndex { get; }
}

/// <summary>
/// One entrypoint's frame root: the trigger's event payload frame, the result row frame the trigger capability
/// declares, and where the entry's steps start in the plan-wide step array.
/// </summary>
internal sealed class EntryFrames
{
    internal EntryFrames(string nodeId, int start, int stepCount, int baseSlot, PortSlot[] eventPorts, PortSlot[] resultPorts)
    { NodeId = nodeId; Start = start; StepCount = stepCount; Base = baseSlot; EventPorts = eventPorts; ResultPorts = resultPorts; }

    internal string NodeId { get; }
    /// <summary>Absolute step index the entrypoint's walk starts at.</summary>
    internal int Start { get; }
    internal int StepCount { get; }
    /// <summary>First slot of the event payload frame; its ports use the event's own reserved widths.</summary>
    internal int Base { get; }
    /// <summary>Slots this entrypoint's single dispatch may occupy, the event frame and result row included.</summary>
    internal int Span { get; init; }
    /// <summary>The trigger's declared outputs, one entry per declared port so `fromEventSlot` addresses this
    /// table. A result row keeps its entry but reserves no slot here: its own region is <see cref="ResultPorts"/>.</summary>
    internal PortSlot[] EventPorts { get; }
    /// <summary>The trigger capability's `result` outputs: one region per port, as wide as that row's declared
    /// fields. The region stays reserved because the plan's own layout declares it, but nothing writes it: an
    /// entrypoint's row is not a command's result and has no recipients to be isomorphic with, so it owns no
    /// descriptor here.</summary>
    internal PortSlot[] ResultPorts { get; }
}

/// <summary>One entrypoint as the frame builder needs it: the trigger's resolved contract (whose outputs are the
/// event payload and result row ports) and every step, in the entry's own step order.</summary>
internal readonly struct EntryContract
{
    internal EntryContract(string nodeId, JsonElement trigger, IReadOnlyList<StepContract> steps)
    { NodeId = nodeId; Trigger = trigger; Steps = steps; }
    internal string NodeId { get; }
    internal JsonElement Trigger { get; }
    internal IReadOnlyList<StepContract> Steps { get; }
}

/// <summary>
/// A plan's load-time frame descriptor: every step's absolute frame regions and port slot table, the event and
/// result row frames of every entrypoint, and the constant pool its literals were folded into. It is built once
/// inside <see cref="RuntimePlan.Parse"/> and lives with the resolved plan. Nothing here is consulted by name,
/// and nothing here allocates per tick.
/// </summary>
internal sealed class PlanFrames
{
    private PlanFrames(int start, int stepCount, StepFrames[] steps, EntryFrames[] entries, FrameSpace constants,
        int maxValueSlotsPerStep, int stepSlots, int slots)
    {
        Start = start; StepCount = stepCount; Steps = steps; Entries = entries; Constants = constants;
        MaxValueSlotsPerStep = maxValueSlotsPerStep; StepSlots = stepSlots; Slots = slots;
    }

    /// <summary>Absolute index of the plan's first step; every step index in a plan is offset by this.</summary>
    internal int Start { get; }
    internal int StepCount { get; }
    /// <summary>Plan-wide step array: an entry's steps are contiguous and keep their order inside the entry.</summary>
    internal StepFrames[] Steps { get; }
    internal EntryFrames[] Entries { get; }
    /// <summary>Plan-static constant pool; its string region is written once at load and never rewritten.</summary>
    internal FrameSpace Constants { get; }
    internal int MaxValueSlotsPerStep { get; }
    /// <summary>The widest single step footprint, against which every step was checked.</summary>
    internal int StepSlots { get; }
    /// <summary>Slots the widest entrypoint of this plan needs in the dispatch arena.</summary>
    internal int Slots { get; }
    internal long ByteCount => (long)Slots * RuntimeFrames.SlotBytes + Constants.ByteCount;

    /// <summary>Read-only view of one constant slot. The pool is written once at load and never rewritten, so the
    /// same bytes stay valid for as long as the plan is loaded.</summary>
    internal Frame Constant(int slot) => new(Constants.Slots, slot, Constants.Strings.Memory);

    /// <summary>
    /// Lays out every frame of one resolved plan. Called once per accepted plan file, after the plan's own wiring
    /// has been validated, so no slot table built here can still disagree with the contract it came from.
    /// </summary>
    internal static PlanFrames Build(IReadOnlyList<EntryContract> entries, RuntimeLimits limits)
    {
        RuntimeFrames.ValidateTables();
        var starts = new int[entries.Count];
        var stepCount = 0;
        for (var entry = 0; entry < entries.Count; entry++)
        { starts[entry] = stepCount; stepCount += entries[entry].Steps.Count; }

        // The constant pool: every authored parameter value and every literal input of every step, encoded once.
        // A promoted parameter has no constant at all — its value arrives through an input slot after the
        // resolved inputs — so the pool never holds a value the plan did not compile.
        var constants = new FrameSpace(Math.Max(16, stepCount * 2), ConstantStringBytes(entries, limits));

        // Region layout: entry by entry, then step by step, each step's inputs, memo region and result row in one
        // contiguous span. Step indices remain plan-wide; physical slots are entry-local. BeginActivation clears
        // the current rows before each entry, and continuations/owner replies capture their own activation state.
        var steps = new StepFrames[stepCount];
        var entriesByFrame = new EntryFrames[entries.Count];
        var stepSlots = 0;
        var slots = 0;
        for (var entry = 0; entry < entries.Count; entry++)
        {
            var cursor = 0;
            var baseSlot = cursor;
            var triggerPorts = RuntimeJson.Rows(entries[entry].Trigger, "outputs");
            // `fromEventSlot` addresses the trigger's declared output order, so the event table keeps one entry per
            // declared port; a result row, however, is not a payload value: it reserves its own region and owns no
            // slot in the event frame.
            var eventPorts = Layout(triggerPorts, ref cursor, RuntimeGraphContracts.IsResult);
            var resultPorts = Layout(triggerPorts.Where(RuntimeGraphContracts.IsResult).ToArray(), ref cursor);
            for (var step = 0; step < entries[entry].Steps.Count; step++)
            {
                var index = starts[entry] + step;
                var contract = entries[entry].Steps[step];
                // Each step's regions are contiguous: inputs, then — for a `pure` or `query` step — the memo
                // region its consumers read, or the result row region every other step owns. `OutputBase` is -1
                // on a value step and `PureMemoBase` is -1 elsewhere, so neither region is ever read where it
                // does not exist. A control step's value outputs live in its ordinary result region: the kernel
                // writes them when it enters the step, and a later step reads them like any other output.
                var pure = contract.NodeKind is "pure" or "query";
                var inputBase = cursor;
                var inputs = InputSlots(contract, triggerPorts, constants, ref cursor);
                var pureMemoBase = pure ? cursor : -1;
                var outputBase = pure ? -1 : cursor;
                var outputRows = RuntimeJson.Rows(contract.Contract, "outputs");
                // A result port reserves one row per explicit recipient the contract allows, so the writer's own
                // bound is the recipient bound: an action can never construct more rows than its contract admits.
                var rowCapacity = RowCapacity(contract.Contract);
                var outputs = Layout(outputRows, ref cursor, null, rowCapacity);
                var stepRow = StepRow(outputRows, outputs, contract.Contract);
                var span = cursor - inputBase;
                // A collection reserves its whole declared element width; a handle, a resource and a result row are
                // references or rows. All of them are budgeted against the frame's bytes, never against the
                // per-step value-slot budget, which counts the values a step decodes.
                var valueSlots = inputs.Sum(Budgeted) + outputs.Sum(Budgeted);
                if (!(valueSlots <= limits.MaxValueSlotsPerStep)) throw new RuntimeContractException("frame-slot-budget", contract.NodeId + " needs " + valueSlots + " value slots, above the " + limits.MaxValueSlotsPerStep + " per-step budget.");
                stepSlots = Math.Max(stepSlots, span);
                var successors = new int[contract.Successors.Count];
                for (var i = 0; i < successors.Length; i++) successors[i] = contract.Successors[i] ?? -1;
                var parameters = ParameterSlots(contract, inputs, constants);
                steps[index] = new StepFrames(index, inputBase, outputBase, pureMemoBase, span, valueSlots, inputs, parameters, outputs,
                    successors, contract.BindingIndex, contract.HandlerIndex)
                { Row = stepRow };
            }
            entriesByFrame[entry] = new EntryFrames(entries[entry].NodeId, starts[entry], entries[entry].Steps.Count, baseSlot, eventPorts, resultPorts)
            { Span = cursor - baseSlot };
            slots = Math.Max(slots, cursor);
        }
        var frames = new PlanFrames(0, stepCount, steps, entriesByFrame, constants, limits.MaxValueSlotsPerStep, stepSlots, slots);
        if (!(frames.ByteCount <= limits.MaxDispatchFrameBytes)) throw new RuntimeContractException("frame-bytes-budget", "The plan's frames need " + frames.ByteCount + " bytes, above the " + limits.MaxDispatchFrameBytes + " byte budget.");
        return frames;
    }

    private static int ConstantStringBytes(IReadOnlyList<EntryContract> entries, RuntimeLimits limits)
    {
        var strings = new HashSet<string>(StringComparer.Ordinal);
        var bytes = 0;
        void Collect(JsonElement value, JsonElement port, bool parameter)
        {
            if (value.ValueKind == JsonValueKind.Null) return;
            if (RuntimeGraphContracts.Many(port) && value.ValueKind == JsonValueKind.Array)
            { foreach (var element in value.EnumerateArray()) Collect(element, port, parameter); return; }
            var kind = RuntimeFrames.KindOf(RuntimeJson.Text(port, "type"));
            var text = kind == ValueKind.String ? value.GetString()
                : kind == ValueKind.Resource ? RuntimeJson.Text(value, parameter ? "resourceId" : "id") : null;
            if (text == null || !strings.Add(text)) return;
            var encoded = Encoding.UTF8.GetByteCount(text);
            RuntimeJson.Require(encoded <= RuntimeFrames.MaximumStringBytes, "frame-string-budget", "A frame string exceeds 4096 bytes.");
            bytes = checked(bytes + encoded);
            RuntimeJson.Require(bytes <= limits.MaxDispatchFrameBytes, "frame-bytes-budget", "The constant strings exceed the plan frame byte budget.");
        }
        foreach (var entry in entries)
            foreach (var step in entry.Steps)
            {
                foreach (var input in step.Inputs)
                    if (input.Literal is { } literal) Collect(literal, input.Port, false);
                var definitions = RuntimeJson.Rows(step.Graph, "parameters");
                for (var index = 0; index < definitions.Length; index++)
                    if (!step.Promoted.Contains(RuntimeJson.Text(definitions[index], "id")))
                        Collect(step.Constants[index], definitions[index], true);
            }
        return Math.Max(4096, bytes);
    }

    /// <summary>Slots one port is charged for. A collection's whole reserved element width, a handle, a resource
    /// and a result row are budgeted against the plan's frame bytes as a whole rather than counted slot by slot
    /// against the per-step value budget: what a step is charged for is the values it decodes, not the references
    /// and rows it may pass on.</summary>
    private static int Budgeted(PortSlot port)
        => port.Many || port.Kind is ValueKind.Handle or ValueKind.Resource or ValueKind.Result ? 0 : port.Width;

    /// <summary>
    /// One step's parameters, in the capability's declaration order. A promoted parameter reads the input port
    /// `Resolve` appended for it; an authored one is validated against its own definition and written into the
    /// plan's constant pool; an omitted one owns no slot, because no value can ever reach it.
    /// </summary>
    private static PortSlot[] ParameterSlots(StepContract step, PortSlot[] inputs, FrameSpace constants)
    {
        var definitions = RuntimeJson.Rows(step.Graph, "parameters");
        var parameters = new PortSlot[definitions.Length];
        // `Resolve` appends promoted parameters after every declared input, in declaration order.
        var promotedBase = RuntimeJson.Rows(step.Graph, "inputs").Length;
        var promoted = 0;
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            if (step.Promoted.Contains(RuntimeJson.Text(definition, "id"))) { parameters[index] = inputs[promotedBase + promoted++]; continue; }
            if (step.Constants[index].ValueKind == JsonValueKind.Null)
            {
                parameters[index] = new PortSlot(-1, ValueKind.Missing, 0, PortSource.Constant, -1);
                continue;
            }
            RuntimeJson.ValidateParameter(step.Constants[index], definition);
            var slot = ConstantSlot(constants, step.Constants[index], definition, RuntimeJson.Text(definition, "id"), true);
            var type = RuntimeJson.Text(definition, "type");
            parameters[index] = new PortSlot(slot, RuntimeFrames.KindOf(type), RuntimeFrames.ValueWidth(type), PortSource.Constant, slot);
        }
        return parameters;
    }

    /// <summary>
    /// One step's input table, in the contract's input order, each entry carrying its copy source. Every value
    /// port keeps its declared width in the frame, wired or not: the slot addresses are a function of the contract
    /// alone, which is what lets a handler shape resolve an offset once at registration.
    /// </summary>
    private static PortSlot[] InputSlots(StepContract step, JsonElement[] triggerPorts, FrameSpace constants, ref int cursor)
    {
        var targets = RuntimeJson.Rows(step.Contract, "inputs");
        var inputs = new PortSlot[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            var port = targets[index];
            var (kind, width) = RuntimeGraphContracts.FramePort(port);
            var slot = -1;
            var source = PortSource.Constant;
            var sourceSlot = -1;
            var sourceStep = -1;
            var wraps = false;
            if (width > 0)
            {
                slot = cursor; cursor += width;
                var name = RuntimeJson.Text(port, "id");
                var input = step.Inputs.FirstOrDefault(i => i.Name == name);
                // Only an optional port may be left unwired, and then nothing is ever copied into its slot: it
                // stays the frame's Missing initial value, which is how the handler boundary reads it too.
                if (!(input != null || RuntimeJson.Flag(port, "optional"))) throw new RuntimeContractException("missing-input", step.NodeId + "." + name);
                if (input != null)
                {
                    if (input.Literal is { } literal) sourceSlot = ConstantSlot(constants, literal, port, input.Name, false);
                    else if (input.FromStep is { } from)
                    {
                        // D-017 R4-a: a pure step's output frame, addressed by its absolute step index and port order.
                        source = PortSource.Pure;
                        sourceStep = from.Step;
                        sourceSlot = from.Port;
                    }
                    else
                    {
                        source = PortSource.Event;
                        sourceSlot = Array.FindIndex(triggerPorts, origin => RuntimeJson.Text(origin, "id") == input.EventPort);
                        if (!(sourceSlot >= 0)) throw new RuntimeContractException("event-port-missing", step.NodeId + "." + input.Name);
                    }
                    wraps = input.Wrap;
                }
            }
            inputs[index] = new PortSlot(slot, kind, width, source, sourceSlot)
            { WrapsAsSet = wraps, SourceStep = sourceStep, Many = RuntimeGraphContracts.Many(port) };
        }
        return inputs;
    }

    /// <summary>One value written into the constant pool, and the number of slots it consumed — one for every
    /// implemented kind but a vector3, which is three. The pool grows on demand: literals are rare and small, so
    /// sizing it for a worst case that never occurs would waste the plan's byte budget. A parameter carries the
    /// reference a frame carries, written out; a step's input literal carries the plan's own compiled reference,
    /// which the loader resolved before this point. A collection literal is a segment exactly like a collection
    /// in any frame: the head slot carries the element kind and count, and each element follows at its kind's own
    /// width, encoded by the same single-value branch a one-value literal takes.</summary>
    private static int ConstantSlot(FrameSpace constants, JsonElement value, JsonElement port, string name, bool parameterConstant)
    {
        var slot = constants.SlotCount;
        var width = EncodedWidth(value, port);
        if (slot + width > constants.SlotCapacity) constants.Grow(Math.Max(slot * 2 + 16, slot + width));
        constants.SlotCount = slot + Encode(constants, slot, value, port, name, parameterConstant);
        return slot;
    }

    /// <summary>The slots a literal occupies once encoded, so the pool is grown before a single slot is written.</summary>
    private static int EncodedWidth(JsonElement value, JsonElement port)
    {
        var type = RuntimeJson.Text(port, "type");
        var single = RuntimeFrames.KindOf(type) == ValueKind.Resource ? RuntimeFrames.ResourceWidth : RuntimeFrames.ValueWidth(type);
        if (!RuntimeGraphContracts.Many(port) || value.ValueKind != JsonValueKind.Array) return single;
        return 1 + value.GetArrayLength() * single;
    }

    private static int Encode(FrameSpace space, int slot, JsonElement value, JsonElement port, string name, bool parameterConstant)
    {
        if (value.ValueKind == JsonValueKind.Null || !RuntimeGraphContracts.Many(port))
            return EncodeValue(space, slot, value, port, name, parameterConstant);
        var kind = RuntimeFrames.KindOf(RuntimeJson.Text(port, "type"));
        RuntimeJson.Require(RuntimeFrames.Segmented(kind), "unsupported-port", RuntimeJson.Text(port, "type"));
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Array, "literal-wrong-type", name);
        var count = value.GetArrayLength();
        RuntimeJson.Require(count <= Wire.MaxSetWidth, "literal-wrong-type", name);
        var stride = RuntimeFrames.ElementWidth(kind);
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            RuntimeJson.Require(element.ValueKind != JsonValueKind.Null, "null-literal", name);
            EncodeValue(space, slot + 1 + index++ * stride, element, port, name, parameterConstant);
        }
        space.Slots[slot] = new FrameValue { Kind = kind, Count = (ushort)count };
        return 1 + count * stride;
    }

    private static int EncodeValue(FrameSpace space, int slot, JsonElement value, JsonElement port, string name, bool parameterConstant)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            RuntimeJson.Require(RuntimeJson.Flag(port, "nullable"), "null-literal", name);
            space.Slots[slot] = FrameValue.Null();
            return 1;
        }
        switch (RuntimeFrames.KindOf(RuntimeJson.Text(port, "type")))
        {
            case ValueKind.Boolean: space.Slots[slot] = FrameValue.Of(value.GetBoolean()); return 1;
            case ValueKind.Integer: space.Slots[slot] = FrameValue.Of((long)value.GetDouble()); return 1;
            case ValueKind.Number: space.Slots[slot] = FrameValue.Of(value.GetDouble()); return 1;
            case ValueKind.Enum:
                // A port indexes the whole member set it names; a structural parameter may inline its own list.
                var member = (int)value.GetDouble();
                RuntimeJson.Require(member >= 0 && member < RuntimeGraphContracts.EnumMembers(port).Length, "invalid-enum", name);
                space.Slots[slot] = FrameValue.Of(member);
                return 1;
            case ValueKind.String:
                var text = value.GetString()!;
                var (offset, length) = space.Strings.Add(text);
                space.Slots[slot] = FrameValue.Of(offset, length);
                return 1;
            case ValueKind.Vector3:
                // Three slots like any other vector3: the head carries x, its two neighbours y and z.
                space.Slots[slot + 1] = FrameValue.Of(value[1].GetDouble());
                space.Slots[slot + 2] = FrameValue.Of(value[2].GetDouble());
                space.Slots[slot] = new FrameValue { Kind = ValueKind.Vector3, Count = RuntimeFrames.VectorWidth, Number = value[0].GetDouble() };
                return RuntimeFrames.VectorWidth;
            case ValueKind.Resource:
                // The one reference a plan may compile: the port names the kind, the value names the instance.
                // Two slots, exactly like every other resource anywhere in a frame. A parameter is the frame's own
                // reference written out (`resourceKind`/`resourceId`); a step's input literal is the document's
                // `{id, revision}` form, already resolved through the kind's owner by the loader.
                var kindIndex = port.TryGetProperty("resourceKind", out var declaredKind)
                    ? Array.IndexOf(Wire.ResourceKinds, declaredKind.GetString()) : -1;
                RuntimeJson.Require(kindIndex >= 0, RuntimeAbiCodes.ResourceKind, name);
                var resourceId = RuntimeJson.Text(value, parameterConstant ? "resourceId" : "id");
                var (idOffset, idLength) = space.Strings.Add(resourceId);
                space.Slots[slot + 1] = FrameValue.Of(idOffset, idLength);
                space.Slots[slot] = FrameValue.OfResource(kindIndex);
                return RuntimeFrames.ResourceWidth;
            case ValueKind.Handle:
                // Unreachable by construction: the plan loader refuses a handle literal before a step's frame is
                // built, because a handle exists only once a step or an event produced it. If it ever arrives
                // here, the loader lost that check — and a constant pool holding a handle would be a lie.
                throw new RuntimeContractException(RuntimeAbiCodes.HandleLiteral, name);
            default:
                // An event is the dispatch's own row and a result row is defined by its schema; neither is a value
                // a plan can compile, and `policy` has no frame form at all.
                throw new RuntimeContractException("unsupported-port", RuntimeJson.Text(port, "type"));
        }
    }

    /// <summary>Appends one port's reserved span to a frame region and returns the table the region addresses.
    /// A port <paramref name="reservedElsewhere"/> reports keeps its entry — the table stays aligned with the
    /// contract's own declaration order — but reserves nothing here: its region belongs to another table. A result
    /// port reserves <paramref name="rowCapacity"/> rows of its own stride, because a row region is as wide as the
    /// recipient set it is written for and never one row whose neighbours the writer would have to be trusted with.</summary>
    private static PortSlot[] Layout(JsonElement[] ports, ref int cursor, Func<JsonElement, bool>? reservedElsewhere = null, int rowCapacity = 1)
    {
        var slots = new PortSlot[ports.Length];
        for (var index = 0; index < ports.Length; index++)
        {
            var (kind, width) = RuntimeGraphContracts.FramePort(ports[index]);
            if (kind == ValueKind.Result) width *= rowCapacity;
            if (width == 0 || (reservedElsewhere != null && reservedElsewhere(ports[index])))
            { slots[index] = new PortSlot(-1, kind, 0, PortSource.Constant, -1); continue; }
            slots[index] = new PortSlot(cursor, kind, width, PortSource.Constant, -1)
            { Many = RuntimeGraphContracts.Many(ports[index]) };
            cursor += width;
        }
        return slots;
    }

    /// <summary>The rows an action's result port holds: one per explicit recipient the contract allows, which is
    /// the cardinality its own recipients declaration states — a "one" recipient is one row and a "many" one is the
    /// same 256-element bound every collection port carries.</summary>
    private static int RowCapacity(JsonElement contract)
    {
        if (contract.ValueKind != JsonValueKind.Object || !contract.TryGetProperty("recipients", out var recipients)) return 1;
        return RuntimeJson.Text(recipients, "cardinality") == "many" ? Wire.MaxSetWidth : 1;
    }

    /// <summary>
    /// The one result row an action step writes through: the port its own recipients declaration names. A step whose
    /// contract declares no recipient set owns no descriptor, so a result port nothing writes rows for is never
    /// mistaken for the row set of an action, and every other declared output keeps its slot table entry.
    /// </summary>
    private static ResultRowDescriptor? StepRow(JsonElement[] ports, PortSlot[] slots, JsonElement contract)
    {
        var named = RuntimeGraphContracts.ResultPortName(contract);
        if (named.Length == 0) return null;
        for (var index = 0; index < ports.Length; index++)
        {
            if (slots[index].Kind != ValueKind.Result) continue;
            if (RuntimeJson.Text(ports[index], "id") != named) continue;
            return ResultRowAbi.Compile(ports[index], contract, slots[index].Slot, slots[index].Width / RuntimeGraphContracts.ResultRowWidth(ports[index]));
        }
        return null;
    }
}
