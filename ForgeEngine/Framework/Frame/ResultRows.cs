using System;
using System.Text;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One compiled column of a result row: the field's slot offset inside the row, its physical kind and width, whether
/// it may be null, and — for an enum column — the member names its values index. The names are the resolved array
/// itself, not its set's name: a row value is validated and written by reading this table, so neither the boundary
/// nor a reader looks a set up by string. <see cref="Settlement"/> marks the two columns a step's cost settlement
/// owns (`consumed`/`released`): a handler neither writes them nor is refused for leaving them out.
/// </summary>
internal readonly struct ResultColumn
{
    internal ResultColumn(string id, byte[] nameUtf8, ValueKind kind, int offset, int width, bool nullable, string[]? members, bool settlement, string code)
    {
        Id = id; NameUtf8 = nameUtf8; Kind = kind; Offset = offset; Width = width; Nullable = nullable; Members = members; Settlement = settlement; Code = code;
    }

    internal string Id { get; }
    /// <summary>The column's name compiled to the UTF-8 bytes a JSON writer writes and a row property is matched by.
    /// It is what keeps the per-row path free of strings: a property is compared against it byte for byte and a
    /// rendered column is named with it, so neither direction builds a string or scans the column list for one.</summary>
    internal byte[] NameUtf8 { get; }
    internal ValueKind Kind { get; }
    /// <summary>Slot offset of the column inside its row; the four fixed columns start the row.</summary>
    internal int Offset { get; }
    /// <summary>Slots the column occupies: 3 for a vector3, <see cref="RuntimeFrames.ResourceWidth"/> for a resource
    /// target, 1 for every other implemented kind.</summary>
    internal int Width { get; }
    internal bool Nullable { get; }
    /// <summary>The enum member names in declaration order, or null on every other column.</summary>
    internal string[]? Members { get; }
    /// <summary>True for `consumed`/`released`: written by the step's charge, not by the handler.</summary>
    internal bool Settlement { get; }
    /// <summary>The ABI code a value this column does not admit is refused with: the shared status/commit columns
    /// raise the status code, because an illegal word there is the row's vocabulary and not a missing field.</summary>
    internal string Code { get; }
}

/// <summary>
/// The recipients one row set is about, exactly as the boundary carried them: the array for a "many" port and the
/// single value for a "one" port. The value is the frame's own and is never copied — a row is read at the index its
/// position names — so a command allocates nothing for the set it writes rows for. It is a write-time view only: the
/// row's target is written into the row's own slots, so nothing renders from the boundary value afterwards.
/// </summary>
internal readonly struct RecipientSet
{
    private readonly JsonElement carried;

    internal RecipientSet(JsonElement carried)
    {
        this.carried = carried;
        Count = carried.ValueKind == JsonValueKind.Array ? carried.GetArrayLength()
            : carried.ValueKind == JsonValueKind.Undefined ? 0 : 1;
    }

    /// <summary>Recipients the boundary carried; 0 for a step that resolved none.</summary>
    internal int Count { get; }
    /// <summary>The recipient at one position: the row written at that position is about it.</summary>
    internal JsonElement At(int index) => carried.ValueKind == JsonValueKind.Array ? carried[index] : carried;
}

/// <summary>
/// One result port as the plan compiled it: the recipient the rows are about, the row's own stride and column
/// offsets, the slot its region starts at in the activation's result buffer, and the domain codes the port declares.
///
/// The <c>target</c> column is typed by the catalog's own recipient declaration and not by the row: the kind comes
/// from the action's <c>recipients.target</c> and the identity metadata from the recipient input port
/// (<c>entityKinds</c>, <c>resourceKind</c>/<c>schema</c>, <c>handleKind</c>/<c>lifetime</c>). A row's target is
/// therefore the recipient the row sits at, which is what makes one row per recipient in recipient order the only
/// shape a step can publish. Nothing here is consulted by name at dispatch: the offsets, the slot and the member
/// tables are fixed at load.
/// </summary>
internal sealed class ResultRowDescriptor
{
    internal ResultRowDescriptor(string port, byte[] portUtf8, string recipientPort, ValueKind targetKind, string[] targetEntityKinds,
        int targetResourceKind, string targetSchema, int targetHandleKind, int targetLifetime,
        ResultColumn[] columns, int stride, int capacity, int slot, string[] codes)
    {
        Port = port; PortUtf8 = portUtf8; RecipientPort = recipientPort; TargetKind = targetKind; TargetEntityKinds = targetEntityKinds;
        TargetResourceKind = targetResourceKind; TargetSchema = targetSchema; TargetHandleKind = targetHandleKind;
        TargetLifetime = targetLifetime; Columns = columns; Stride = stride; Capacity = capacity; Slot = slot; Codes = codes;
        Fields = new JsonElement[columns.Length];
        // The typed writer's own tables, fixed with the descriptor: where each status and commit word sits in its
        // column's member list, and which columns a handler's row has to write.
        StatusMembers = MembersOf(columns, 1, Outcomes.StatusNames);
        CommitMembers = MembersOf(columns, 2, Outcomes.CommitNames);
        for (var column = 4; column < columns.Length && column < 64; column++)
            if (!columns[column].Settlement) RequiredColumns |= 1UL << column;
    }

    private static int[] MembersOf(ResultColumn[] columns, int column, string[] words)
    {
        var members = new int[words.Length];
        for (var index = 0; index < words.Length; index++)
            members[index] = column < columns.Length && columns[column].Members is { } declared ? Array.IndexOf(declared, words[index]) : -1;
        return members;
    }

    /// <summary>The member index of each <see cref="Status"/> in the status column's own set, or -1 where the column
    /// does not carry that word.</summary>
    internal int[] StatusMembers { get; }
    /// <summary>The member index of each <see cref="Commit"/> in the commit column's own set, or -1.</summary>
    internal int[] CommitMembers { get; }
    /// <summary>The columns after the four fixed ones that a handler's row must write, as a bit per column: every
    /// declared column but the two the step's charge writes.</summary>
    internal ulong RequiredColumns { get; }
    /// <summary>The codes this port has already admitted, found by reference: a code is a compile-time constant, so
    /// the row a handler writes on every call carries the same string and is admitted without comparing text.</summary>
    private readonly string?[] admitted = new string?[8];
    private int admittedNext;
    internal bool Admitted(string code)
    {
        for (var index = 0; index < admitted.Length; index++) if (ReferenceEquals(admitted[index], code)) return true;
        return false;
    }
    internal void Admit(string code) { admitted[admittedNext] = code; admittedNext = (admittedNext + 1) % admitted.Length; }
    /// <summary>The row set last registered for this descriptor: a set is its descriptor, a count and a shape, so a
    /// step that writes the same number of rows again registers the same one.</summary>
    internal RuntimeKernel.ResultRowSet? LastSet { get; set; }

    /// <summary>The result port's own id: the property a `fromStepSlot` reader names and the receipt renders under.</summary>
    internal string Port { get; }
    /// <summary>The port id's own UTF-8 bytes: the property name the receipt writes it under and the name a step's
    /// output frame is stripped of, both without building a string.</summary>
    internal byte[] PortUtf8 { get; }
    /// <summary>The input port the explicit recipients arrive on; empty for a row that is about no recipient set.</summary>
    internal string RecipientPort { get; }
    /// <summary>The declared target kind: <see cref="ValueKind.Entity"/>, <see cref="ValueKind.Resource"/> or
    /// <see cref="ValueKind.Handle"/>.</summary>
    internal ValueKind TargetKind { get; }
    /// <summary>The kinds an id of the recipient input carries — the published kind of every declared kind, so a
    /// subkind-only input (`gtfo.map_object.door`) reads as its parent (`gtfo.map_object`); empty means any known
    /// kind.</summary>
    internal string[] TargetEntityKinds { get; }
    /// <summary>The resource kind's index in <see cref="Wire.ResourceKinds"/>, or -1.</summary>
    internal int TargetResourceKind { get; }
    /// <summary>The resource kind's schema, or empty.</summary>
    internal string TargetSchema { get; }
    /// <summary>The handle kind's index in <see cref="Wire.HandleKinds"/>, or -1.</summary>
    internal int TargetHandleKind { get; }
    /// <summary>The handle lifetime's index in <see cref="Wire.HandleLifetimes"/>, or -1.</summary>
    internal int TargetLifetime { get; }
    internal ResultColumn[] Columns { get; }
    /// <summary>Slots one row occupies.</summary>
    internal int Stride { get; }
    /// <summary>Rows the port's region holds: one per explicit recipient the contract allows.</summary>
    internal int Capacity { get; }
    /// <summary>First slot of the row region in the activation's result buffer.</summary>
    internal int Slot { get; }
    /// <summary>The domain codes the port declares; an empty list means the port cannot fail, not that rows go unchecked. A row's `code` outside this list is refused unless it
    /// is the shared word the runtime writes for the row's own pairing or a registered ABI code.</summary>
    internal string[] Codes { get; }
    /// <summary>The values one row's properties carried, indexed by column: the write's own scratch. A descriptor
    /// belongs to one plan and one activation writes it at a time, so the buffer is reused for every row instead of
    /// an array per row; a column the row left out holds <see cref="JsonValueKind.Undefined"/>.</summary>
    internal JsonElement[] Fields { get; }

    /// <summary>The first slot of one row, relative to the port's own region: the writer and the reader are opened at <see cref="Slot" />, so every offset inside a row is the descriptor's own and never the plan's.</summary>
    internal int RowSlot(int row) => row * Stride;
    /// <summary>Slots the whole row region reserves; the writer's own bound, so an over-wide row is refused before
    /// it can reach a neighbour.</summary>
    internal int Span => Stride * Capacity;
}

/// <summary>
/// The typed result-row writer, validator and reader: one place writes a row into the activation's result buffer,
/// one place validates it, and one place decodes a written set back to JSON. A row is written exactly once, in
/// recipient order, and a value that is not the complete identity of the declared kind at that position is refused
/// by the ABI's own code rather than stored.
///
/// The handler's JSON row is the boundary's input and the typed region is the only representation afterwards. The
/// rows a step reads and the rows a receipt renders are that same region: <see cref="WriteSet"/> is the one
/// conversion back to JSON, it runs at the boundaries that take JSON (a handler's input frame, a receipt, a trace,
/// the network), and nothing inside the kernel reads a rendered row in place of the typed one.
/// </summary>
internal static class ResultRowAbi
{
    /// <summary>The words every action's rows share and no port lists: `committed` and `partial` name the outcome
    /// the runtime's own result helpers write, and `stale-entity` is the one code for a recipient whose life is
    /// over, which the kernel's own refusal rows and every handler write alike.</summary>
    private static readonly string[] SharedCodes = { ResultCode.Committed, ResultCode.Partial, ResultCode.StaleEntity };

    /// <summary>The canonical row statuses, in the order <see cref="CommandStatuses"/> declares them.</summary>
    private static readonly string[] Statuses = { CommandStatuses.Succeeded, CommandStatuses.Partial,
        CommandStatuses.Rejected, CommandStatuses.Failed, CommandStatuses.Cancelled, CommandStatuses.Expired };

    /// <summary>
    /// Compiles one result port against the contract it is declared on. The target's kind and identity come from
    /// the contract's `recipients` declaration and the recipient input port it names; a row that declares no
    /// recipient set (a trigger's own result row) types its target from the column itself.
    /// </summary>
    internal static ResultRowDescriptor Compile(JsonElement port, JsonElement contract, int slot, int capacity)
    {
        var fields = RuntimeGraphContracts.ResultFields(port);
        var columns = new ResultColumn[fields.Length];
        var offset = 0;
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var type = RuntimeJson.Text(field, "type");
            var id = RuntimeJson.Text(field, "id");
            var kind = index == 0 ? RuntimeGraphContracts.TargetKindOf(type) : RuntimeFrames.KindOf(type);
            // The width is the row's own rule, shared with the plan's layout: an entity column is three slots — the id
            // it names and its two epochs — which is what keeps an admitted identity exact.
            var width = RuntimeGraphContracts.ResultColumnWidth(field, index);
            columns[index] = new ResultColumn(id, Encoding.UTF8.GetBytes(id), kind, offset, width, RuntimeJson.Flag(field, "nullable"),
                type == "enum" ? RuntimeGraphContracts.EnumMembers(field) : null,
                id is "consumed" or "released" && index > 3,
                index is 1 or 2 ? RuntimeAbiCodes.ResultRowStatus : RuntimeAbiCodes.ResultRowIncomplete);
            offset += width;
        }

        // The identity authority is the recipient declaration: the target column's own keys are only read where the
        // contract declares no recipient set at all, which is the shape a trigger's own result row has.
        var identity = fields[0];
        var recipientPort = "";
        var targetKind = columns[0].Kind;
        if (contract.ValueKind == JsonValueKind.Object && contract.TryGetProperty("recipients", out var recipients))
        {
            recipientPort = RuntimeJson.Text(recipients, "input");
            targetKind = RuntimeGraphContracts.TargetKindOf(RuntimeJson.Text(recipients, "target"));
            if (RuntimeGraphContracts.PortOf(contract, "inputs", recipientPort) is { } input) identity = input;
        }
        var codes = port.TryGetProperty("codes", out _) ? RuntimeJson.Strings(port.GetProperty("codes")) : Array.Empty<string>();
        var portId = RuntimeJson.Text(port, "id");
        return new ResultRowDescriptor(portId, Encoding.UTF8.GetBytes(portId), recipientPort, targetKind,
            RuntimeGraphContracts.PublishedEntityKinds(RuntimeGraphContracts.DeclaredEntityKinds(identity)),
            RuntimeGraphContracts.ResourceKindIndex(identity),
            RuntimeGraphContracts.OptionalText(identity, "schema") ?? "", RuntimeGraphContracts.HandleKindIndex(identity),
            RuntimeGraphContracts.LifetimeIndex(identity), columns, offset, capacity, slot, codes);
    }

    /// <summary>
    /// Writes one row into the result region. <paramref name="value"/> is the handler's row for this recipient and
    /// <paramref name="recipient"/> the recipient at the same position, whose complete identity the row's target is:
    /// the value is validated against the descriptor's own columns and offsets, and anything the descriptor does not
    /// declare — a missing column, an unknown one, a wrong kind, a value outside its set — is refused by the ABI's
    /// code instead of being stored or coerced. The row's own slots are cleared before it is written, so a column the
    /// row does not carry is one it never had, not the value a previous row left in the region it reuses.
    /// </summary>
    internal static void Write(ref FrameWriter frame, ResultRowDescriptor row, int index, JsonElement value, JsonElement recipient)
    {
        try { WriteRow(ref frame, row, index, value, recipient); }
        catch (RuntimeContractException error) { throw Row(error); }
    }

    /// <summary>
    /// The ABI's own code for a refusal the writer raised. The three registered row codes pass through; the frame's
    /// string region running out is the row budget it belongs to, and every other shape failure — a value that is
    /// not an object, a missing component, a number outside the safe range — is the row's own incompleteness. A
    /// caller reads one of the three codes and never a JSON reader's.
    /// </summary>
    internal static RuntimeContractException Row(RuntimeContractException error)
    {
        if (error.Code is RuntimeAbiCodes.ResultRowBudget or RuntimeAbiCodes.ResultRowStatus or RuntimeAbiCodes.ResultRowIncomplete)
            return error;
        return new RuntimeContractException(error.Code == "frame-string-budget" ? RuntimeAbiCodes.ResultRowBudget : RuntimeAbiCodes.ResultRowIncomplete, error.Message);
    }

    private static void WriteRow(ref FrameWriter frame, ResultRowDescriptor row, int index, JsonElement value, JsonElement recipient)
    {
        if (!(index >= 0 && index < row.Capacity)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowBudget, row.Port + " row " + index + " is outside the port's declared recipient count.");
        if (!(value.ValueKind == JsonValueKind.Object)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " is not a row object.");
        // The row's properties are matched against the compiled names — the descriptor's own UTF-8 bytes — and land
        // in the descriptor's scratch by column, so the column loop below reads its own values without a lookup and
        // the hot path builds no string, no array and no dictionary.
        var fields = row.Fields;
        Array.Clear(fields, 0, fields.Length);
        foreach (var property in value.EnumerateObject())
        {
            var column = ColumnOf(row, property);
            if (!(column >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " carries the unknown column " + property.Name + ".");
            fields[column] = property.Value;
        }
        var baseSlot = row.RowSlot(index);
        // The row's own slots start unwritten: the region is reused by the next dispatch, so a column this row does
        // not carry has to read as one it never had rather than as the value a previous row left in the same slots.
        frame.Clear(baseSlot, row.Stride);
        for (var column = 0; column < row.Columns.Length; column++)
        {
            var declared = row.Columns[column];
            var field = fields[column];
            if (field.ValueKind == JsonValueKind.Undefined)
            {
                // A settlement column is written by the step's charge, never by the handler; every other column is
                // the row's own and a row without it is incomplete rather than a row with a missing value.
                if (!(declared.Settlement)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " is missing the column " + declared.Id + ".");
                continue;
            }
            if (field.ValueKind == JsonValueKind.Null)
            {
                if (!(declared.Nullable)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " declares " + declared.Id + " null.");
                frame.SetNull(baseSlot + declared.Offset);
                continue;
            }
            if (column == 0) WriteTarget(ref frame, row, index, baseSlot, field, recipient);
            else WriteColumn(ref frame, row, index, baseSlot, declared, field);
        }
    }

    /// <summary>The column one row property names, or -1: the compiled UTF-8 name compared byte for byte, which is
    /// what keeps the per-row path from building the property's name as a string.</summary>
    private static int ColumnOf(ResultRowDescriptor row, JsonProperty property)
    {
        for (var column = 0; column < row.Columns.Length; column++)
            if (property.NameEquals(row.Columns[column].NameUtf8)) return column;
        return -1;
    }

    /// <summary>One JSON component of a vector3 column: a finite number and nothing else, read without the enumerator
    /// a sequence helper would box.</summary>
    private static bool Finite(JsonElement component)
        => component.ValueKind == JsonValueKind.Number && component.TryGetDouble(out var value) && double.IsFinite(value);

    /// <summary>
    /// One row's target: the complete identity of the declared kind, and positionally isomorphic with the recipient
    /// the row sits at. The row's own value is what is read and validated — a null, a bare id, a reference of
    /// another kind, another world or another recipient is refused by name — and the frame is written from the
    /// recipient, because the row's target <em>is</em> that recipient and one identity may not have two spellings.
    /// A handler that names somebody else is a retarget, not a result.
    /// </summary>
    internal static void WriteTarget(ref FrameWriter frame, ResultRowDescriptor row, int index, int baseSlot, JsonElement value, JsonElement recipient)
    {
        switch (row.TargetKind)
        {
            case ValueKind.Entity:
                {
                    var target = RuntimeJson.EntityRow(value);
                    if (!(target.WorldEpoch >= 0 && target.LifeEpoch >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " carries an incomplete entity identity.");
                    if (!(target.Id.IndexOf(':') > 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names no entity kind.");
                    var expected = RuntimeJson.EntityRow(recipient);
                    if (!(target == expected)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names " + target.Id + " instead of the recipient at its position.");
                    if (!(row.TargetEntityKinds.Length == 0
                        || Array.IndexOf(row.TargetEntityKinds, RuntimeJson.KindOf(target.Id)) >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names " + RuntimeJson.KindOf(target.Id) + ", which the recipient input does not accept.");
                    WriteEntity(ref frame, baseSlot, expected);
                    return;
                }
            case ValueKind.Resource:
                {
                    var reference = RuntimeJson.ResourceRefOf(value);
                    if (!(Array.IndexOf(Wire.ResourceKinds, reference.ResourceKind) == row.TargetResourceKind)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names the resource kind " + reference.ResourceKind + ".");
                    if (!(reference.ResourceId.Length > 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names no resource instance.");
                    if (!(reference == RuntimeJson.ResourceRefOf(recipient))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names " + reference.ResourceId + " instead of the recipient at its position.");
                    frame.SetResource(baseSlot, row.TargetResourceKind, reference.ResourceId);
                    return;
                }
            case ValueKind.Handle:
                {
                    RuntimeJson.Handle(value);
                    var world = (int)RuntimeJson.Integer(value.GetProperty("worldEpoch"), 0, int.MaxValue);
                    var generation = (int)RuntimeJson.Integer(value.GetProperty("lifeEpoch"), 0, int.MaxValue);
                    var local = (int)RuntimeJson.Integer(value.GetProperty("local"), 0, int.MaxValue);
                    var provider = (int)RuntimeJson.Integer(value.GetProperty("provider"), 0, int.MaxValue);
                    RuntimeJson.Handle(recipient);
                    if (!(world == (int)RuntimeJson.Integer(recipient.GetProperty("worldEpoch"), 0, int.MaxValue)
                        && generation == (int)RuntimeJson.Integer(recipient.GetProperty("lifeEpoch"), 0, int.MaxValue)
                        && local == (int)RuntimeJson.Integer(recipient.GetProperty("local"), 0, int.MaxValue)
                        && provider == (int)RuntimeJson.Integer(recipient.GetProperty("provider"), 0, int.MaxValue))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " names a handle that is not the recipient at its position.");
                    frame.SetHandle(baseSlot, new FrameHandle(world, generation, local), provider);
                    return;
                }
            default:
                throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " declares no target kind.");
        }
    }

    /// <summary>
    /// One entity value's own slots: the reference's id text, then its two epochs as the safe integers the contract
    /// admits. A row keeps the identity the handler named — the frame's three-component entity form carries a local
    /// index instead of the id text and two 32-bit epochs, so it could only report a different reference — and both
    /// epochs are written as the full-width integers they were validated as, with no narrowing anywhere.
    /// </summary>
    internal static void WriteEntity(ref FrameWriter frame, int slot, EntityReference entity)
    {
        frame.SetString(slot, entity.Id);
        frame.Set(slot + 1, entity.WorldEpoch);
        frame.Set(slot + 2, entity.LifeEpoch);
    }

    /// <summary>
    /// One non-target column, validated against its declared kind and value domain: the exact JSON kind the column
    /// admits, the enum member's declaration index, and the bounded string. A value outside the column's own domain
    /// is the row's failure and never a coerced one.
    /// </summary>
    private static void WriteColumn(ref FrameWriter frame, ResultRowDescriptor row, int index, int baseSlot, ResultColumn column, JsonElement value)
    {
        var slot = baseSlot + column.Offset;
        switch (column.Kind)
        {
            case ValueKind.Boolean:
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, column.Code, Column(row, index, column));
                frame.Set(slot, value.GetBoolean());
                return;
            case ValueKind.Integer:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var integer)
                    && double.IsFinite(integer) && integer == Math.Truncate(integer)
                    && Math.Abs(integer) <= RuntimeJson.MaxSafeInteger, column.Code, Column(row, index, column));
                frame.Set(slot, (long)value.GetDouble());
                return;
            case ValueKind.Number:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
                    column.Code, Column(row, index, column));
                frame.Set(slot, value.GetDouble());
                return;
            case ValueKind.String:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.String, column.Code, Column(row, index, column));
                frame.SetString(slot, value.GetString()!);
                return;
            case ValueKind.Vector3:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3
                    && Finite(value[0]) && Finite(value[1]) && Finite(value[2]), column.Code, Column(row, index, column));
                frame.SetVector3(slot, value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
                return;
            case ValueKind.Entity:
                {
                    // The complete reference the handler named, admitted by the contract's own bounds and stored as it
                    // is: a nullable later column is the same value form as the target, so neither can be re-spelled.
                    WriteEntity(ref frame, slot, RuntimeJson.EntityRow(value));
                    return;
                }
            case ValueKind.Enum:
                {
                    RuntimeJson.Require(value.ValueKind == JsonValueKind.String, column.Code, Column(row, index, column));
                    var member = Array.IndexOf(column.Members!, value.GetString());
                    RuntimeJson.Require(member >= 0, column.Code, Column(row, index, column));
                    frame.SetEnum(slot, member, column.Members!.Length);
                    return;
                }
            default:
                throw new RuntimeContractException(column.Code, Column(row, index, column));
        }
    }

    /// <summary>
    /// Whether one status/commit pair is one the canonical vocabulary admits. It is the command result's own pairing
    /// applied to a row, because a command's conclusion is derived from its rows: `succeeded` commits, `partial` may
    /// be confirmed, unknown or none, `failed` may be none or unknown, and the three terminal statuses are none. The
    /// one pairing a tier narrows — `partial + none` belongs to `presentation` alone — is the invocation boundary's
    /// rule, not the row vocabulary's.
    /// </summary>
    internal static bool CanonicalPairing(string status, string commit) => status switch
    {
        CommandStatuses.Succeeded => commit == CommitStates.Confirmed,
        CommandStatuses.Partial => commit is CommitStates.Confirmed or CommitStates.Unknown or CommitStates.None,
        CommandStatuses.Failed => commit is CommitStates.None or CommitStates.Unknown,
        CommandStatuses.Rejected or CommandStatuses.Cancelled or CommandStatuses.Expired => commit == CommitStates.None,
        _ => false,
    };

    /// <summary>
    /// The four fixed columns' own rule, applied once per row: the status and commit are the canonical vocabulary in
    /// the canonical pairing, and the code is the row's own reason. This is the one check that cannot be made column
    /// by column, because the pairing is a fact about the row and not about either value.
    /// </summary>
    internal static void ValidatePairing(ResultRowDescriptor row, int index, JsonElement value)
    {
        var status = RuntimeJson.Text(value, "status");
        var commit = RuntimeJson.Text(value, "committed");
        if (!(Array.IndexOf(Statuses, status) >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " carries the status " + status + ".");
        if (!(CanonicalPairing(status, commit))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " pairs " + status + " with " + commit + ".");
        ValidateCode(row, index, RuntimeJson.Text(value, "code"));
    }

    /// <summary>A row's code is a reason word, and one its port declares, every action shares or the kernel
    /// registers. Both writers ask this one rule, so a code one of them refuses the other refuses by the same code.</summary>
    internal static void ValidateCode(ResultRowDescriptor row, int index, string code)
    {
        if (!(code.Length is > 0 and <= RuntimeGraphContracts.DomainReasonCodeMaxLength
            && RuntimeGraphContracts.IsDomainReasonCode(code))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " carries the code " + code + ".");
        if (!(Array.IndexOf(row.Codes, code) >= 0 || Array.IndexOf(SharedCodes, code) >= 0
            || RuntimeAbiCodes.IsRegistered(code))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " carries the undeclared code " + code + ".");
    }

    /// <summary>
    /// One kernel-synthesized row: the recipient's own identity and the command's own conclusion, so the command's
    /// outcome stays derivable from the complete row set. The words are the command's status and commit when that
    /// pair is one the canonical vocabulary admits; a conclusion the row vocabulary does not admit is written as the
    /// conservative <c>rejected + none</c> instead of an illegal pairing, because a row is a value a reader branches
    /// on and not a place to restate a conclusion the rows cannot describe. It carries the four fixed columns and
    /// nothing else — a step that never ran has no domain value to report, and a fabricated one would be exactly the
    /// invented target this ABI forbids. The columns it leaves unwritten stay <see cref="ValueKind.Missing"/> and are
    /// omitted when the row is rendered.
    /// </summary>
    internal static void WriteRefusal(ref FrameWriter frame, ResultRowDescriptor row, int index, JsonElement recipient,
        string status, string commit, string code)
    {
        try { WriteRefusalRow(ref frame, row, index, recipient, status, commit, code); }
        catch (RuntimeContractException error) { throw Row(error); }
    }

    private static void WriteRefusalRow(ref FrameWriter frame, ResultRowDescriptor row, int index, JsonElement recipient,
        string status, string commit, string code)
    {
        if (!(index >= 0 && index < row.Capacity)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowBudget, row.Port + " row " + index + " is outside the port's declared recipient count.");
        var baseSlot = row.RowSlot(index);
        // A synthesized row's own slots start unwritten for the same reason the handler's do: it carries the four
        // fixed columns and nothing else, and a domain column of a previous dispatch must not survive in them.
        frame.Clear(baseSlot, row.Stride);
        // A synthesized row's target is the recipient itself: there is no handler value to compare against, and the
        // row's identity is exactly the recipient the kernel refused for.
        WriteTarget(ref frame, row, index, baseSlot, recipient, recipient);
        var canonical = CanonicalPairing(status, commit);
        for (var column = 1; column < 4; column++)
        {
            var declared = row.Columns[column];
            if (declared.Kind == ValueKind.String) { frame.SetString(baseSlot + declared.Offset, code); continue; }
            // Column 1 is the status and column 2 the commit; column 3 is the code string written above.
            var word = column == 1 ? (canonical ? status : CommandStatuses.Rejected) : (canonical ? commit : CommitStates.None);
            var member = Array.IndexOf(declared.Members!, word);
            if (!(member >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " cannot report " + word + " on " + declared.Id + ".");
            frame.SetEnum(baseSlot + declared.Offset, member, declared.Members!.Length);
        }
    }

    /// <summary>Writes one row's two settlement columns; the amounts are the charge's own, never a handler's.</summary>
    internal static void SetCost(ref FrameWriter frame, ResultRowDescriptor row, int index, long consumed, long released)
    {
        for (var column = 0; column < row.Columns.Length; column++)
        {
            var declared = row.Columns[column];
            if (!declared.Settlement) continue;
            frame.Set(row.RowSlot(index) + declared.Offset, declared.Id == "consumed" ? consumed : released);
        }
    }

    /// <summary>Replaces a row's conclusion when the kernel's later cost settlement fails. The target and the actual
    /// action readings stay intact; the fee is unknown even if the action itself had already committed.</summary>
    internal static void SetCostFailure(ref FrameWriter frame, ResultRowDescriptor row, int index, string code)
    {
        var baseSlot = row.RowSlot(index);
        for (var column = 1; column < 4; column++)
        {
            var declared = row.Columns[column];
            if (column == 3)
            {
                frame.SetString(baseSlot + declared.Offset, code);
                continue;
            }
            var value = column == 1 ? CommandStatuses.Failed : CommitStates.Unknown;
            var member = Array.IndexOf(declared.Members!, value);
            if (!(member >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " cannot report a cost settlement failure in " + declared.Id + ".");
            frame.SetEnum(baseSlot + declared.Offset, member, declared.Members!.Length);
        }
    }

    /// <summary>
    /// Writes one row set — one row per recipient in recipient order, or the single row a "one" set carried — into a
    /// JSON writer. This is the ABI's only row-to-JSON conversion: the receipt, the trace and the network boundary
    /// call it, and so does the one read a downstream step makes, because the handler it feeds takes JSON. Every
    /// column is decoded from the slots the writer filled, the target included: an entity column reports the id it
    /// named and its two epochs, a resource target its kind index and id, a handle target its triple and creating
    /// provider. Nothing here reads the boundary value a row was written for.
    /// </summary>
    internal static void WriteSet(Utf8JsonWriter writer, Frame frame, ResultRowDescriptor row, int count, bool set, bool settled)
    {
        // One row written as an object and several as an array, which is the shape the handler's own set carried.
        var asSet = set || count != 1;
        if (asSet) writer.WriteStartArray();
        for (var index = 0; index < count; index++) WriteRowJson(writer, frame, row, index, settled);
        if (asSet) writer.WriteEndArray();
    }

    private static void WriteRowJson(Utf8JsonWriter writer, Frame frame, ResultRowDescriptor row, int index, bool settled)
    {
        writer.WriteStartObject();
        WriteTargetJson(writer, frame, row, index);
        for (var column = 1; column < row.Columns.Length; column++)
        {
            var declared = row.Columns[column];
            var slot = row.RowSlot(index) + declared.Offset;
            var kind = frame.Kind(slot);
            // A column nothing wrote is one the row never had a value for — a refusal row's domain columns — and a
            // settlement column the charge has not written yet is the same. Neither is a null the row declared.
            if (kind == ValueKind.Missing) continue;
            if (declared.Settlement && !settled) continue;
            writer.WritePropertyName(declared.NameUtf8);
            if (kind == ValueKind.Null) { writer.WriteNullValue(); continue; }
            WriteColumnJson(writer, row, declared, slot, frame);
        }
        writer.WriteEndObject();
    }

    /// <summary>One row's target as the JSON a reader sees: the stored identity of the declared kind, written from
    /// the row's own slots and from nothing else.</summary>
    private static void WriteTargetJson(Utf8JsonWriter writer, Frame frame, ResultRowDescriptor row, int index)
    {
        writer.WritePropertyName(row.Columns[0].NameUtf8);
        var slot = row.RowSlot(index);
        switch (row.TargetKind)
        {
            case ValueKind.Entity: WriteEntityJson(writer, frame, slot); return;
            case ValueKind.Resource: WriteResourceJson(writer, frame, slot); return;
            case ValueKind.Handle: WriteHandleJson(writer, frame, slot); return;
            default: throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " renders no target kind.");
        }
    }

    private static void WriteColumnJson(Utf8JsonWriter writer, ResultRowDescriptor row, ResultColumn column, int slot, Frame frame)
    {
        switch (column.Kind)
        {
            case ValueKind.Entity: WriteEntityJson(writer, frame, slot); return;
            case ValueKind.Resource: WriteResourceJson(writer, frame, slot); return;
            case ValueKind.Handle: WriteHandleJson(writer, frame, slot); return;
            case ValueKind.Boolean: writer.WriteBooleanValue(frame.Boolean(slot)); return;
            case ValueKind.Integer: writer.WriteNumberValue(frame.Integer(slot)); return;
            case ValueKind.Number: writer.WriteNumberValue(frame.Number(slot)); return;
            case ValueKind.String: writer.WriteStringValue(frame.String(slot)); return;
            case ValueKind.Enum: writer.WriteStringValue(column.Members![frame.EnumIndex(slot)]); return;
            case ValueKind.Vector3:
                {
                    frame.Vector3(slot, out var x, out var y, out var z);
                    writer.WriteStartArray();
                    writer.WriteNumberValue(x); writer.WriteNumberValue(y); writer.WriteNumberValue(z);
                    writer.WriteEndArray();
                    return;
                }
            default: throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " renders no " + column.Kind + ".");
        }
    }

    /// <summary>One resource value: its kind and id, read back from the slot the writer filled.</summary>
    private static void WriteResourceJson(Utf8JsonWriter writer, Frame frame, int slot)
    {
        var resource = frame.Resource(slot);
        writer.WriteStartObject();
        writer.WriteString("resourceKind"u8, Wire.ResourceKinds[resource.KindIndex]);
        writer.WriteString("resourceId"u8, resource.Id);
        writer.WriteEndObject();
    }

    /// <summary>One entity value: the id it names and its two epochs, read back from the three slots the writer
    /// filled. The id is the reference's own text — never a spelling composed from the epoch components — and both
    /// epochs are written as the safe integers they were validated as, so the value a reader sees is the value the
    /// handler returned.</summary>
    private static void WriteEntityJson(Utf8JsonWriter writer, Frame frame, int slot)
    {
        writer.WriteStartObject();
        writer.WriteString("id"u8, frame.String(slot));
        writer.WriteNumber("worldEpoch"u8, frame.Integer(slot + 1));
        writer.WriteNumber("lifeEpoch"u8, frame.Integer(slot + 2));
        writer.WriteEndObject();
    }

    /// <summary>One handle value: the identity triple the slot holds plus the registration index of the provider that
    /// created it, which is the same four components the boundary writes out.</summary>
    private static void WriteHandleJson(Utf8JsonWriter writer, Frame frame, int slot)
    {
        var handle = frame.Handle(slot, out var provider);
        writer.WriteStartObject();
        writer.WriteNumber("worldEpoch"u8, handle.WorldEpoch);
        writer.WriteNumber("lifeEpoch"u8, handle.LifeEpoch);
        writer.WriteNumber("local"u8, handle.Local);
        writer.WriteNumber("provider"u8, provider);
        writer.WriteEndObject();
    }

    private static string Column(ResultRowDescriptor row, int index, ResultColumn column)
        => row.Port + " row " + index + " column " + column.Id + " does not match its declared " + column.Kind + ".";
}
