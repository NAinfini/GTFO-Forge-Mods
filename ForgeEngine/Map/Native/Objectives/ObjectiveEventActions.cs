using ForgeCore;
using System;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Advances progression objectives through WorldEventManager.ExecuteEvent.</summary>
internal sealed class ObjectiveEventActions
{
    private readonly Func<bool> _canExecute;
    private readonly OnceReporter _once;

    private ObjectiveEventActions(Func<bool> canExecute, Action<string> report)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
    }

    internal static ObjectiveEventActions For(Func<bool> canExecute, Action<string> report)
        => new(canExecute, report);

    internal CommandResult HandleProgress(CommandContext context) => Progress(context);

    private CommandResult Progress(CommandContext context)
    {
        if (!ObjectiveEventContract.TryProgress(context, out var request, out string? code))
            return Outcome.Rejected(code!);
        var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.StepProgressionObjective };
        data.Layer = (LG_LayerType)request.Layer;
        data.Count = request.Steps;
        // The executor is a void request: submission is not the later replicated objective state, so the commit is
        // unknown, the same policy every objective write of this package keeps (ObjectiveActionHandler).
        return Commit(data, "progress") ?? ObjectiveEventContract.Issued(context, request);
    }

    /// <summary>Reads readiness for each request; a native exception may follow a replicated write. Null when the
    /// request was submitted.</summary>
    private Outcome? Commit(WardenObjectiveEventData data, string key)
    {
        if (!_canExecute() || WorldEventManager.Current == null)
            return Outcome.Rejected(ResultCode.ObjectiveUnavailable);
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
        }
        catch (Exception error)
        {
            ReportOnce(key, "objective " + key + " event threw: " + error.GetType().Name);
            return Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        return null;
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

}
