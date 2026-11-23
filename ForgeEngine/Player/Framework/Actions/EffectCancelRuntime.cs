using System;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The canonical effect cancellation body. Handles belong to the shared kernel, not to a native
/// modifier ledger. The producing provider restores its own write through that handle's callback.</summary>
internal static class EffectCancelRuntime
{
    private static readonly HandlePort Port = PortOf(AgentModifierContract.CancelCapability.GetProperty("graph")
        .GetProperty("inputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "handles"));

    private static HandlePort PortOf(JsonElement port)
        => new(port.GetProperty("handleKind").GetString()!, port.GetProperty("lifetime").GetString()!);

    public static CommandResult Execute(RuntimeModuleHandle registration, CommandContext context,
        Func<bool> canCommit, Func<RuntimeHandle, bool> holdsUnrestoredGroup)
    {
        if (!canCommit()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        int count = context.Count(AgentModifierContract.CancelHandlesSlot);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField).WithDetail("handles");
        if (count == 0)
        {
            context.Outputs.Integer(Blocks.ActionEffectCancel.OutCancelled, 0);
            return Outcome.NoTargets();
        }
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        // A handle that does not read is that handle's own refused row; the other handles are still cancelled.
        var keys = new (RuntimeHandle Handle, bool Read, Outcome Answer)[count];
        for (int i = 0; i < count; i++)
            keys[i].Read = context.TryRuntimeHandle(AgentModifierContract.CancelHandlesSlot, i, out keys[i].Handle);
        var rows = context.Rows;
        int cancelled = 0;
        for (int i = 0; i < count; i++)
        {
            // A handle named twice is cancelled once; its later rows repeat the first answer.
            int first = 0;
            while (first < i && !(keys[i].Read && keys[first].Read && keys[first].Handle == keys[i].Handle)) first++;
            if (first == i)
            {
                Status status = Status.Rejected; Commit state = Commit.None; string code;
                if (!keys[i].Read) code = ResultCode.StaleHandle;
                else if (!canCommit()) code = ResultCode.AuthorityOrPhase;
                else
                {
                    try
                    {
                        int ended = registration.CancelHandle(keys[i].Handle, Port, Blocks.ActionEffectCancel.Id);
                        // A handle whose effect already ended has nothing left to cancel: the wanted state holds.
                        if (ended == 0) { status = Status.Succeeded; state = Commit.Confirmed; code = ResultCode.AlreadyEnded; }
                        else if (holdsUnrestoredGroup(keys[i].Handle))
                        { status = Status.Failed; state = Commit.Unknown; code = ResultCode.NativeClearException; }
                        else
                        { status = Status.Succeeded; state = Commit.Confirmed; code = ResultCode.Committed; cancelled += ended; }
                    }
                    catch (RuntimeContractException error)
                    {
                        code = error.Code;
                        if (code is ResultCode.HandleCancelFailed or ResultCode.OwnerCancelPending or ResultCode.OwnerCancelFailed)
                        { status = Status.Failed; state = Commit.Unknown; }
                    }
                }
                keys[i].Answer = new Outcome(status, state, code);
            }
            var answer = keys[first].Answer;
            rows.Begin(i).Integer(Blocks.ActionEffectCancel.RowTargetCount, count).End(answer.Status, answer.Commit, answer.Code);
        }
        context.Outputs.Integer(Blocks.ActionEffectCancel.OutCancelled, cancelled);
        return rows.Conclude();
    }
}
