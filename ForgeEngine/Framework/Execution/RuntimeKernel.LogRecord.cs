using System;
using System.Globalization;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    /// <summary>The fixed code of the author record `forge.control.log.record` writes. Its tick sits in the record
    /// envelope; `values.room`, `values.item`, `values.result` and `values.wall` carry the rest.</summary>
    internal const string AuthorRecordCode = "author.record";
    private const int WallValueSlot = 3;
    private const string WallFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The record's fields, built once when the plan loads: room and item as the author typed them (bounded
    /// like every quoted text) and the result member. The last slot is the wall-clock time a step fills when it runs;
    /// it exists only in the log and is never read back by any game logic.</summary>
    internal static RuntimeLogValue[] AuthorRecordValues(JsonElement parameters) => new[]
    {
        RuntimeLogValue.Quote("room", RuntimeJson.Text(parameters, "room")),
        RuntimeLogValue.Quote("item", RuntimeJson.Text(parameters, "item")),
        RuntimeLogValue.Of("result", RuntimeJson.Text(parameters, "result")),
        default
    };

    /// <summary>One `log.record` step: writes the pre-resolved record at Info through the Runtime's own record point and
    /// continues at `next`. A kernel without a sink or a provider level below Info writes nothing and still continues.</summary>
    private string? EnterLogRecord(ResolvedStep step, out int? cursor)
    {
        var values = step.RecordValues!;
        if (log.RuntimeLogs(RuntimeLogLevel.Info))
        {
            values[WallValueSlot] = RuntimeLogValue.Of("wall", DateTime.UtcNow.ToString(WallFormat, CultureInfo.InvariantCulture));
            // log-values-max: 4 -- the pre-resolved record array holds room, item, result and the wall time.
            log.LogRuntime(RuntimeLogLevel.Info, AuthorRecordCode, new RuntimeLogFacts { Values = values });
        }
        cursor = Successor(step, 0);
        return null;
    }
}
