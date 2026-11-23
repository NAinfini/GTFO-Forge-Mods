using System;
using ForgeEngine.Framework;
using ForgeVisual;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// The `forge.action.presentation.point_light` command: one temporary point light borrowed from the game's effect-light
/// pool on the machine that runs the step, at the world point the plan named, and given back when its duration ends
/// (ForgeVisual's <c>PointLights</c>). It is a `presentation` step, so it runs on each viewer's own machine, spawns
/// nothing that is replicated and commits no world state; the rows are the tier's own `partial + none`.
///
/// Every input is checked before a light is borrowed: a non-finite number, a colour channel outside 0-1, a reach or
/// intensity at or below zero, or a duration under one tick refuses the whole step. A pool with no free light fails
/// every viewer's row by name and takes nothing from the lights already lit.
/// </summary>
internal static class PointLightActions
{
    internal static CommandResult Handle(CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationContract.PointLightViewers, out var refusal))
            return refusal;
        if (!PresentationRows.UnityVector(context.Vector(PresentationContract.PointLightPosition), out var position)
            || context.Vector(PresentationContract.PointLightColor) is not { } color
            || !Channel(color.X) || !Channel(color.Y) || !Channel(color.Z)
            || !Positive(context.Number(PresentationContract.PointLightIntensity), out var intensity)
            || !Positive(context.Number(PresentationContract.PointLightRange), out var range)
            || context.Number(PresentationContract.PointLightDuration) is not { } ticks
            || !double.IsFinite(ticks) || ticks < 1d)
            return Outcome.Rejected(ResultCode.PointLightInvalidInput);
        float seconds = (float)(ticks / context.TicksPerSecond);
        if (!float.IsFinite(seconds) || seconds <= 0f) return Outcome.Rejected(ResultCode.PointLightInvalidInput);

        PointLightResult placement;
        try
        {
            placement = PointLights.Place(position, new PointLightSpec(new Color((float)color.X, (float)color.Y, (float)color.Z, 1f), range, intensity, flicker: 0f), seconds, Time.time);
        }
        catch (Exception error)
        {
            PresentationLog.Contained("point-light.place", error);
            return PresentationRows.Failed(context, ResultCode.NativeCommitException);
        }
        if (placement != PointLightResult.Placed) return PresentationRows.Failed(context, ResultCode.PointLightPoolFull);
        return PresentationRows.Presented(context, ResultCode.PointLightPlayed);
    }

    private static bool Channel(double value) => double.IsFinite(value) && value >= 0d && value <= 1d;

    private static bool Positive(double? value, out float result)
    {
        result = 0f;
        if (value is not { } number || !double.IsFinite(number) || number <= 0d) return false;
        result = (float)number;
        return float.IsFinite(result) && result > 0f;
    }
}
