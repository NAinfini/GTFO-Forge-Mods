using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation.Native;

/// <summary>One validated effect request: where it plays, which way it faces, the far end of a line member and the
/// size a Forge member draws at. A member that takes no end or size never receives one; the row refuses first.</summary>
internal readonly struct EffectRequest
{
    internal EffectRequest(UnityEngine.Vector3 position, UnityEngine.Vector3? direction, UnityEngine.Vector3? end, float size,
        EffectStep step, long world, long play, long tick)
    {
        Position = position;
        Direction = direction;
        End = end;
        Size = size;
        Step = step;
        World = world;
        Play = play;
        Tick = tick;
    }

    internal UnityEngine.Vector3 Position { get; }
    internal UnityEngine.Vector3? Direction { get; }
    internal UnityEngine.Vector3? End { get; }
    /// <summary>The size in metres, already defaulted from the member when the plan named none; NaN for a member
    /// without a size.</summary>
    internal float Size { get; }
    /// <summary>The loaded step this play belongs to, with its held-play keys.</summary>
    internal EffectStep Step { get; }
    /// <summary>The world the play belongs to; a new world keeps only the rows still held when the old one ended.</summary>
    internal long World { get; }
    /// <summary>Which play this is, as the host named it (<see cref="CommandContext.Play"/>): the trigger scope and the
    /// target (or round) of every loop the step runs in.</summary>
    internal long Play { get; }
    /// <summary>The host tick the step ran on (<see cref="CommandContext.PlayTick"/>).</summary>
    internal long Tick { get; }
}

/// <summary>
/// The `forge.action.presentation.effect_play` command: one member of <see cref="EffectMembers"/> played on each
/// addressed client at the position the plan named. Every port a member cannot honour is refused by name before
/// anything plays: a line member without an end, an end or a size handed to a member that has none, a size outside
/// the member's own bound, a flame without a direction. What is left is handed to the effect player, which takes an
/// original effect through the game's own entry and draws a Forge member from ForgeVisual's effect bundle; a
/// member whose resources this world has not loaded refuses by name rather than playing nothing.
///
/// The row is a `presentation` step, so this body runs on each viewer's own machine: the effect is spawned locally,
/// nothing about it is replicated, and it never damages anything.
/// </summary>
internal sealed class EffectActions
{

    private readonly VisualEffectPlayer _effects;
    private readonly Action<string> _report;

    internal EffectActions(VisualEffectPlayer effects, Action<string> report)
    {
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }


    internal CommandResult Handle(CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationContract.EffectViewers, out var refusal))
            return refusal;
        if (!EffectMembers.TryGet(context.Text(PresentationContract.EffectPreset), out var member))
            return Outcome.Rejected(ResultCode.EffectPresetRequired);
        if (!context.Present(PresentationContract.EffectPosition)
            || !PresentationRows.UnityVector(context.Vector(PresentationContract.EffectPosition), out var position))
            return Outcome.Rejected(ResultCode.EffectPositionRequired);
        // The direction is the effect's orientation (a surface normal for an impact, the aim for a flame). A plan
        // that names none gets the member's own resting orientation, which is a real one and not a guessed normal.
        bool hasDirection = context.Present(PresentationContract.EffectDirection);
        var direction = default(UnityEngine.Vector3);
        if (hasDirection && (!PresentationRows.UnityVector(context.Vector(PresentationContract.EffectDirection), out direction)
            || direction.sqrMagnitude <= 0f)) return Outcome.Rejected(ResultCode.EffectDirectionInvalid);
        if (member.NeedsDirection && !hasDirection) return Outcome.Rejected(ResultCode.EffectDirectionRequired);
        bool hasEnd = context.Present(PresentationContract.EffectEnd);
        var end = default(UnityEngine.Vector3);
        if (hasEnd && !PresentationRows.UnityVector(context.Vector(PresentationContract.EffectEnd), out end))
            return Outcome.Rejected(ResultCode.EffectEndInvalid);
        if (hasEnd != member.NeedsEnd) return Outcome.Rejected(hasEnd ? ResultCode.EffectEndUnsupported : ResultCode.EffectEndRequired);
        float size = member.DefaultSize;
        if (context.Present(PresentationContract.EffectSize))
        {
            if (!member.TakesSize) return Outcome.Rejected(ResultCode.EffectSizeUnsupported);
            double named = context.Number(PresentationContract.EffectSize) ?? double.NaN;
            if (!double.IsFinite(named) || named <= 0d || named > member.MaximumSize) return Outcome.Rejected(ResultCode.EffectSizeInvalid);
            size = (float)named;
        }
        var step = context.StepState as EffectStep;
        if (step == null) context.StepState = step = new EffectStep();
        var request = new EffectRequest(position, hasDirection ? direction : null, hasEnd ? end : null, size,
            step, context.WorldEpoch, context.Play, context.PlayTick);
        try
        {
            if (_effects.Play(member, request) is { } missing) return Outcome.Rejected(missing);
        }
        catch (Exception error)
        {
            Report("presentation.effect-play-failed: " + error.GetType().Name + ": " + error.Message);
            return PresentationRows.Failed(context, ResultCode.NativeCommitException);
        }
        // The tier commits no world state: the effect ran on this machine and nothing was replicated, so each
        // viewer's row is the tier's own `partial + none` and the row declares the four fixed columns alone.
        return PresentationRows.Presented(context, ResultCode.EffectPlayed);
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { PresentationLog.Contained("effect-actions.report", reportError); }
    }
}
