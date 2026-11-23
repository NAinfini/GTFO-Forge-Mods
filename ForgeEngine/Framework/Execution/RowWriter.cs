using System;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// What one command invocation's typed row writing stands on: the row region the kernel armed for the step, the
/// step's own recipients, and how far the handler has written. The kernel owns one and re-arms it for every
/// invocation; <see cref="RowWriter"/> is the handler's view of it. Nothing here throws into the handler: the first
/// violation is kept, every later write is dropped, and the kernel turns the kept violation into the command's
/// `failed + unknown` with the row ABI's own code once the handler has returned — the same answer the JSON boundary
/// gives a row set it refuses.
/// </summary>
internal sealed class RowWriteState
{
    internal FrameSpace? Space;
    internal int Base;
    internal ResultRowDescriptor? Row;
    internal RuntimeKernel.StepInputs? Inputs;
    internal int RecipientInput;
    /// <summary>The explicit recipients the step resolved: the rows the handler owes, one per recipient.</summary>
    internal int Recipients;
    /// <summary>Whether the rows render as a set: the recipient port's own cardinality.</summary>
    internal bool Set;
    /// <summary>The step's contract tier allows a commit only on the authoritative machine.</summary>
    internal bool HostTier;
    internal bool IsHost;
    internal long WorldEpoch;
    /// <summary>Rows ended so far; rows are written in recipient order, so it is also the next row's index.</summary>
    internal int Count;
    internal int Open = -1;
    internal ulong Written;
    internal bool Used;
    internal string? ErrorCode, ErrorDetail;
    // The aggregation of the rows ended so far, kept as they are written so the conclusion reads no row back.
    internal int Confirmed, Succeeded;
    internal string? FirstCode, UnknownCode;
    internal PresentedTally Presented;
    /// <summary>The step's own answer sheet for the outputs it publishes beside its rows, by contract position, and
    /// whether this invocation asked for it (<see cref="CommandContext.Outputs"/>).</summary>
    internal EvaluationOutputs? Sheet;
    internal bool OutputsUsed;
    /// <summary>The committed facts this invocation added (<see cref="CommandContext.BeginFact"/>), in order, and the
    /// violation that refuses them all.</summary>
    internal readonly System.Collections.Generic.List<RuntimeFact> Facts = new();
    internal string? FactErrorCode, FactErrorDetail;
    /// <summary>The one recipient of a row written outside a step's own inputs: a deferred result's target.</summary>
    internal EntityReference? Single;
    /// <summary>The recipients of rows written after the step's own invocation ended: an owner command's, kept when
    /// its handler answered later.</summary>
    internal EntityReference[]? Targets;
    /// <summary>The conclusion of the row ended last.</summary>
    internal Status LastStatus;
    internal Commit LastCommit;
    internal string? LastCode;

    internal void Arm(FrameSpace space, int baseSlot, ResultRowDescriptor? row, RuntimeKernel.StepInputs inputs, int recipientInput,
        int recipients, bool set, bool hostTier, bool isHost, long worldEpoch, EvaluationOutputs sheet)
    {
        Disarm();
        Space = space; Base = baseSlot; Row = row; Inputs = inputs; RecipientInput = recipientInput; Recipients = recipients;
        Set = set; HostTier = hostTier; IsHost = isHost; WorldEpoch = worldEpoch; Sheet = sheet;
    }

    /// <summary>Arms the writer for the one row of a deferred result: its target is the recipient.</summary>
    internal void ArmSingle(FrameSpace space, int baseSlot, ResultRowDescriptor row, EntityReference target, long worldEpoch)
    {
        Disarm();
        Space = space; Base = baseSlot; Row = row; Single = target; Recipients = 1; IsHost = true; WorldEpoch = worldEpoch;
    }

    /// <summary>Arms the writer for the rows of a command whose handler answered later: one per kept recipient.</summary>
    internal void ArmTargets(FrameSpace space, int baseSlot, ResultRowDescriptor row, EntityReference[] targets, bool set,
        bool hostTier, long worldEpoch)
    {
        Disarm();
        Space = space; Base = baseSlot; Row = row; Targets = targets; Recipients = targets.Length; Set = set;
        HostTier = hostTier; WorldEpoch = worldEpoch;
    }

    internal void Disarm()
    {
        Space = null; Row = null; Inputs = null; Single = null; Targets = null; Recipients = 0; Count = 0; Open = -1; Written = 0; Used = false;
        ErrorCode = ErrorDetail = null; Confirmed = Succeeded = 0; Presented = default;
        FirstCode = UnknownCode = LastCode = null;
        Sheet = null; OutputsUsed = false;
        Facts.Clear(); FactErrorCode = FactErrorDetail = null;
    }

    internal void FailFacts(string code, string detail)
    {
        if (FactErrorCode != null) return;
        FactErrorCode = code; FactErrorDetail = detail;
    }

    internal bool Armed => Space != null;

    internal void Fail(string code, string detail)
    {
        if (ErrorCode != null) return;
        ErrorCode = code; ErrorDetail = detail;
    }

    internal void Fail(RuntimeContractException error)
    {
        var mapped = ResultRowAbi.Row(error);
        Fail(mapped.Code, mapped.Message);
    }

    internal FrameWriter Writer() => new(Space!.Slots, Base, Row!.Span, Space.Strings);

    /// <summary>The recipient one row is about, as the entity the step's own input carried; false where the
    /// recipient is not an entity the frame holds decoded.</summary>
    internal bool TryRecipient(int index, out EntityReference entity)
    {
        if (Inputs == null)
        {
            if (Targets is { } kept)
            {
                entity = (uint)index < (uint)kept.Length ? kept[index] : null!;
                return entity != null;
            }
            entity = Single!;
            return Single != null && index == 0;
        }
        var cell = Inputs.CellAt(RecipientInput);
        if (cell.Valid && cell.Kind == ValueKind.Entity)
        {
            if (cell.Count < 0 && index == 0 && cell.Reference is EntityReference single) { entity = single; return true; }
            if (cell.Reference is EntityReference[] set && (uint)index < (uint)set.Length) { entity = set[index]; return true; }
            if (cell.Reference is RuntimeKernel.EntityCheck[] live && (uint)index < (uint)live.Length) { entity = live[index].Entity; return true; }
        }
        entity = null!;
        return false;
    }
}

/// <summary>
/// The typed result-row writer a command handler writes through: one row per explicit recipient, in the recipients'
/// own order, straight into the activation's row region — no row object, no serialization, no parse. A row is
/// begun at its position (its target is the recipient at that position, written by the kernel), given its declared
/// columns by the block's generated <c>Row…</c> constants, and ended with its status, commit state and code.
///
/// The rules are the JSON boundary's own and are refused with the same codes: every declared column is written
/// (`result-row-incomplete`), the row count is the recipient count (`result-row-incomplete`), a target the handler
/// names is the recipient at that position (`result-row-incomplete`), and the status and commit are a canonical pair
/// with a declared code (`result-row-status`). A violation never throws into the handler; the command fails with the
/// first one after the handler returns. The writer is valid only inside its own handler invocation.
/// </summary>
public readonly struct RowWriter
{
    private readonly RowWriteState? state;
    private readonly CommandInvocationState? invocation;
    private readonly long stamp;

    internal RowWriter(CommandInvocationState invocation, long stamp)
    {
        this.invocation = invocation; this.stamp = stamp; state = invocation.Rows;
    }

    private RowWriteState State => invocation != null && invocation.Stamp == stamp && stamp != 0 ? state!
        : throw new RuntimeContractException("command-context-expired", "A row writer is valid only for its handler invocation.");

    /// <summary>The rows this command owes: one per explicit recipient the step resolved.</summary>
    public int Count => State.Recipients;

    /// <summary>The recipient at one position, where the recipients are entities.</summary>
    public bool TryTarget(int index, out EntityReference target) => State.TryRecipient(index, out target);

    /// <summary>Begins the row at <paramref name="index"/>; its target is the recipient at that position.</summary>
    public RowWriter Begin(int index)
    {
        var rows = State;
        rows.Used = true;
        if (rows.ErrorCode != null) return this;
        if (rows.Row is not { } row || !rows.Armed)
        { rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, "The step declares no result rows to write."); return this; }
        if (rows.Open >= 0)
        { rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + rows.Open + " was begun and never ended."); return this; }
        if (index != rows.Count || index >= rows.Recipients)
        {
            rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, index >= rows.Recipients
                ? row.Port + " row " + index + " is outside the step's " + rows.Recipients + " explicit recipients."
                : row.Port + " row " + index + " is written out of the recipients' order.");
            return this;
        }
        try
        {
            if (!(index < row.Capacity)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowBudget, row.Port + " row " + index + " is outside the port's declared recipient count.");
            var frame = rows.Writer();
            var baseSlot = row.RowSlot(index);
            frame.Clear(baseSlot, row.Stride);
            if (row.TargetKind == ValueKind.Entity && rows.TryRecipient(index, out var entity))
                ResultRowAbi.WriteEntity(ref frame, baseSlot, entity);
            else
            {
                // A resource or handle recipient, or an entity the frame holds only as its document: the target is
                // written from the recipient's own value by the boundary's writer.
                if (rows.Inputs == null) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " has no recipient to be about.");
                var carried = rows.Inputs.InputAt(rows.RecipientInput);
                var recipient = carried.ValueKind == JsonValueKind.Array ? carried[index] : carried;
                if (recipient.ValueKind != JsonValueKind.Object) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " has no recipient to be about.");
                ResultRowAbi.WriteTarget(ref frame, row, index, baseSlot, recipient, recipient);
            }
            rows.Open = index; rows.Written = 0;
        }
        catch (RuntimeContractException error) { rows.Fail(error); }
        return this;
    }

    /// <summary>Begins the row at <paramref name="index"/> for the target the handler acted on. The row's target is
    /// the recipient at that position; a handler that names another one has retargeted, and the row is refused.</summary>
    public RowWriter Begin(int index, EntityReference target)
    {
        var rows = State;
        Begin(index);
        if (rows.Open != index) return this;
        if (target == null || !rows.TryRecipient(index, out var recipient) || !(target == recipient))
        {
            rows.Open = -1;
            rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, rows.Row!.Port + " row " + index + " names " + (target?.Id ?? "nothing") + " instead of the recipient at its position.");
        }
        return this;
    }

    public RowWriter Boolean(int column, bool value)
    {
        if (Column(column, ValueKind.Boolean, ValueKind.Boolean, out var frame, out var slot)) frame.Set(slot, value);
        return this;
    }

    /// <summary>An integer column, or a number column given a whole number.</summary>
    public RowWriter Integer(int column, long value)
    {
        if (!Column(column, ValueKind.Integer, ValueKind.Number, out var frame, out var slot)) return this;
        if (Math.Abs(value) > RuntimeJson.MaxSafeInteger) { Refuse(column); return this; }
        if (state!.Row!.Columns[column].Kind == ValueKind.Number) frame.Set(slot, (double)value); else frame.Set(slot, value);
        return this;
    }

    public RowWriter Number(int column, double value)
    {
        if (!Column(column, ValueKind.Number, ValueKind.Number, out var frame, out var slot)) return this;
        if (!double.IsFinite(value)) { Refuse(column); return this; }
        frame.Set(slot, value);
        return this;
    }

    public RowWriter Text(int column, string value)
    {
        if (!Column(column, ValueKind.String, ValueKind.String, out var frame, out var slot)) return this;
        if (value == null) { Refuse(column); return this; }
        try { frame.SetString(slot, value); }
        catch (RuntimeContractException error) { state!.Fail(error); }
        return this;
    }

    /// <summary>An enum column by the member's index in the column's own set.</summary>
    public RowWriter Enum(int column, int member)
    {
        if (!Column(column, ValueKind.Enum, ValueKind.Enum, out var frame, out var slot)) return this;
        var members = state!.Row!.Columns[column].Members!;
        if ((uint)member >= (uint)members.Length) { Refuse(column); return this; }
        frame.SetEnum(slot, member, members.Length);
        return this;
    }

    public RowWriter Vector(int column, double x, double y, double z)
    {
        if (!Column(column, ValueKind.Vector3, ValueKind.Vector3, out var frame, out var slot)) return this;
        if (!(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))) { Refuse(column); return this; }
        frame.SetVector3(slot, x, y, z);
        return this;
    }

    /// <summary>A later entity column; null writes the column's declared null.</summary>
    public RowWriter Entity(int column, EntityReference? value)
    {
        if (value == null) return Null(column);
        if (!Column(column, ValueKind.Entity, ValueKind.Entity, out var frame, out var slot)) return this;
        if (string.IsNullOrEmpty(value.Id) || value.WorldEpoch < 0 || value.LifeEpoch < 0
            || value.WorldEpoch > RuntimeJson.MaxSafeInteger || value.LifeEpoch > RuntimeJson.MaxSafeInteger) { Refuse(column); return this; }
        try { ResultRowAbi.WriteEntity(ref frame, slot, value); }
        catch (RuntimeContractException error) { state!.Fail(error); }
        return this;
    }

    public RowWriter Null(int column)
    {
        if (!Column(column, ValueKind.Missing, ValueKind.Missing, out var frame, out var slot)) return this;
        if (!state!.Row!.Columns[column].Nullable)
        { state.Fail(RuntimeAbiCodes.ResultRowIncomplete, state.Row.Port + " row " + state.Open + " declares " + state.Row.Columns[column].Id + " null."); return this; }
        frame.SetNull(slot);
        return this;
    }

    /// <summary>Ends the open row with its conclusion. Every declared column must have been written.</summary>
    public void End(Status status, Commit commit, string code) => End(status, commit, code, synthesized: false, -1);

    /// <summary>Ends the open row of a presentation step for a recipient another machine presents (a player whose
    /// screen is drawn there): the row is written exactly as <see cref="End(Status, Commit, string)"/> writes it, and
    /// it does not decide this machine's conclusion (<see cref="ConcludePresented"/>), which that machine gives for
    /// its own write.</summary>
    internal void EndElsewhere(Status status, Commit commit, string code) => End(status, commit, code, synthesized: false, -1, elsewhere: true);

    /// <summary>Ends the open row as the action skeleton's own row: the four fixed columns, the shape a kernel
    /// refusal row has, and — where the action names one — its recipient-count column. Whatever else the step had
    /// written of the row before it stopped is cleared.</summary>
    internal void EndSynthesized(Status status, Commit commit, string code, int countColumn = -1)
        => End(status, commit, code, synthesized: true, countColumn);

    private void End(Status status, Commit commit, string code, bool synthesized, int countColumn, bool elsewhere = false)
    {
        var rows = State;
        if (rows.ErrorCode != null) return;
        if (rows.Open < 0) { rows.Used = true; rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, "A row was ended without being begun."); return; }
        var row = rows.Row!; var index = rows.Open;
        if (synthesized)
        {
            if (rows.Written != 0 && row.Columns.Length > 4)
            {
                var cleared = rows.Writer();
                cleared.Clear(row.RowSlot(index) + row.Columns[4].Offset, row.Stride - row.Columns[4].Offset);
            }
            if (countColumn >= 0) Integer(countColumn, rows.Recipients);
            if (rows.ErrorCode != null) { rows.Open = -1; return; }
        }
        rows.Open = -1;
        if (!synthesized && (rows.Written & row.RequiredColumns) != row.RequiredColumns)
        {
            for (var column = 4; column < row.Columns.Length; column++)
                if ((row.RequiredColumns & 1UL << column) != 0 && (rows.Written & 1UL << column) == 0)
                { rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " is missing the column " + row.Columns[column].Id + "."); return; }
        }
        try
        {
            var statusMember = (uint)status < (uint)row.StatusMembers.Length ? row.StatusMembers[(int)status] : -1;
            if (!(statusMember >= 0)) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " carries the status " + status + ".");
            var commitMember = (uint)commit < (uint)row.CommitMembers.Length ? row.CommitMembers[(int)commit] : -1;
            if (!(commitMember >= 0 && Outcomes.CanonicalPairing(status, commit))) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, row.Port + " row " + index + " pairs " + status + " with " + commit + ".");
            if (code == null) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + index + " is missing the column " + row.Columns[3].Id + ".");
            if (!row.Admitted(code)) { ResultRowAbi.ValidateCode(row, index, code); row.Admit(code); }
            var frame = rows.Writer();
            var baseSlot = row.RowSlot(index);
            frame.SetEnum(baseSlot + row.Columns[1].Offset, statusMember, row.Columns[1].Members!.Length);
            frame.SetEnum(baseSlot + row.Columns[2].Offset, commitMember, row.Columns[2].Members!.Length);
            frame.SetString(baseSlot + row.Columns[3].Offset, code);
        }
        catch (RuntimeContractException error) { rows.Fail(error); return; }
        rows.Count = index + 1;
        rows.LastStatus = status; rows.LastCommit = commit; rows.LastCode = code;
        if (index == 0) rows.FirstCode = code;
        if (commit == Commit.Confirmed) rows.Confirmed++;
        else if (commit == Commit.Unknown) rows.UnknownCode ??= code;
        if (status == Status.Succeeded) rows.Succeeded++;
        rows.Presented.Add(status, commit, code, elsewhere);
    }

    /// <summary>One whole row that carries no column of its own beyond the four fixed ones.</summary>
    public void Row(int index, Status status, Commit commit, string code) => Begin(index).End(status, commit, code);

    /// <summary>
    /// The command's conclusion from the rows written, by the one rule every host and owner action follows
    /// (<see cref="ResultAggregation"/>): no row is `no-targets`; every row done is `succeeded`; some row committed
    /// is `partial`, with an unknown commit when any row's is unknown; nothing committed and nothing unknown is a
    /// rejection with the first row's code; nothing committed and something unknown is `failed + unknown` with the
    /// first unknown row's code. A row is done when it committed; <paramref name="doneWhenSucceeded"/> asks for its
    /// status instead, for an action whose rows may be `partial + confirmed`.
    /// </summary>
    public Outcome Conclude(bool doneWhenSucceeded = false)
    {
        var rows = State;
        // Concluding is itself a use of the writer: a handler that concluded without a row still owes one per recipient.
        rows.Used = true;
        if (rows.ErrorCode != null) return Outcome.FailedUnknown(rows.ErrorCode);
        if (rows.Count == 0) return Outcome.NoTargets();
        if ((doneWhenSucceeded ? rows.Succeeded : rows.Confirmed) == rows.Count) return Outcome.Succeeded();
        if (rows.Confirmed > 0) return Outcome.Partial(rows.UnknownCode == null ? Commit.Confirmed : Commit.Unknown);
        return rows.UnknownCode == null ? Outcome.Rejected(rows.FirstCode!) : Outcome.FailedUnknown(rows.UnknownCode);
    }

    /// <summary>The conclusion of a presentation action's rows, which commit nothing, by the one presentation rule
    /// (<see cref="PresentedTally"/>).</summary>
    public Outcome ConcludePresented()
    {
        var rows = State;
        rows.Used = true;
        return rows.ErrorCode != null ? Outcome.FailedUnknown(rows.ErrorCode) : rows.Presented.Conclude();
    }

    private void Refuse(int column)
    {
        var rows = state!; var declared = rows.Row!.Columns[column];
        rows.Fail(declared.Code, rows.Row.Port + " row " + rows.Open + " column " + declared.Id + " does not match its declared " + declared.Kind + ".");
    }

    /// <summary>The open row's column as a place to write: false, with the violation kept, where no row is open,
    /// the column is not one a handler writes, it was written already or it is of another kind.
    /// <see cref="ValueKind.Missing"/> asks for any kind (a null).</summary>
    private bool Column(int column, ValueKind kind, ValueKind widened, out FrameWriter frame, out int slot)
    {
        var rows = State;
        frame = default; slot = 0;
        if (rows.ErrorCode != null) return false;
        if (rows.Open < 0) { rows.Used = true; rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, "A column was written outside a row."); return false; }
        var row = rows.Row!;
        if (column < 4 || column >= row.Columns.Length || column >= 64 || row.Columns[column].Settlement)
        { rows.Fail(RuntimeAbiCodes.ResultRowIncomplete, row.Port + " row " + rows.Open + " carries the unknown column " + column + "."); return false; }
        var declared = row.Columns[column];
        if (kind != ValueKind.Missing && declared.Kind != kind && declared.Kind != widened) { Refuse(column); return false; }
        rows.Written |= 1UL << column;
        frame = rows.Writer();
        slot = row.RowSlot(rows.Open) + declared.Offset;
        return true;
    }
}
