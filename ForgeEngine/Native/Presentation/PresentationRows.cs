using ForgeEngine.Framework;

namespace ForgeEngine.Presentation.Native;

/// <summary>The rows of the shared presentation steps: one per viewer, in the order the request addressed them, the
/// target written by the kernel from the recipient at that position. A row carries the four fixed columns and, where
/// the row declares it, the viewer count; nothing here commits world state, so every row's commit is none and the
/// conclusion is the presentation tier's own rule (<see cref="RowWriter.ConcludePresented"/>).</summary>
internal static class PresentationRows
{
    /// <summary>The column a row with no viewer count passes.</summary>
    internal const int NoCount = -1;

    /// <summary>One answer for every viewer, written as each viewer's row, with the viewer count in
    /// <paramref name="countColumn"/> where the row declares one. A context with no row region — a recipient's
    /// retained presentation shown again outside a handler invocation — has no row to write and answers as it is.</summary>
    internal static Outcome Answer(CommandContext context, Status status, string code, int countColumn = NoCount)
    {
        var rows = context.Rows;
        var count = rows.Count;
        if (count == 0) return new Outcome(status, Commit.None, code);
        for (var index = 0; index < count; index++)
        {
            var row = rows.Begin(index);
            if (countColumn != NoCount) row.Integer(countColumn, count);
            row.End(status, Commit.None, code);
        }
        return rows.ConcludePresented();
    }

    /// <summary>One local display answered for every viewer: `partial + none`, because the display ran and the tier
    /// commits nothing.</summary>
    internal static Outcome Presented(CommandContext context, string code, int countColumn = NoCount)
        => Answer(context, Status.Partial, code, countColumn);

    /// <summary>A failure that is definite for every viewer the step addressed; each still gets its own row.</summary>
    internal static Outcome Failed(CommandContext context, string code, int countColumn = NoCount)
        => Answer(context, Status.Failed, code, countColumn);

    /// <summary>Three finite coordinates as a Unity vector; anything else is no point at all.</summary>
    internal static bool UnityVector((double X, double Y, double Z)? value, out UnityEngine.Vector3 vector)
        => ForgeEngine.GameBindings.GameVectors.TryUnity(value, out vector);
}
