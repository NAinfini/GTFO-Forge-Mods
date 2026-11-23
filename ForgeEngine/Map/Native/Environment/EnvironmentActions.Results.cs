using ForgeEngine.Framework;

namespace ForgeMap.Native;

internal sealed partial class EnvironmentActions
{
    /// <summary>A zone recipient of an earlier world or of a life a zone never has: zone addresses are life 0.</summary>
    internal static bool StaleZone(CommandContext context, EntityReference reference)
        => reference.WorldEpoch != context.WorldEpoch || reference.LifeEpoch != 0;

    /// <summary>The step that gives every live zone the one answer a single native request earned (a level-wide
    /// event, the ambient light write); a zone of another life answers its own `stale-entity` row.</summary>
    internal static readonly TargetStep<Outcome> SameStep = Same;

    private static void Same(ref Outcome answer, EntityReference target, int index, RowWriter rows)
    {
        if (target.LifeEpoch != 0) rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity);
        else rows.End(answer.Status, answer.Commit, answer.Code);
    }
}
