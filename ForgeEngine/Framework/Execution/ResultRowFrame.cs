using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The one place every provider frames an action's result row set, and the one place the port id it goes under is
/// spelled.
///
/// The runtime reads a step's rows from the output port the capability's own `recipients` declaration names as its
/// result port, and that port is also what a later step's `fromStepSlot` read resolves. The primitive contract
/// declares that port as `result` for every action capability (`Docs/forge-contract/primitive/primitives.json`,
/// each action's `recipients.result`), so it is the port every provider writes under. A set written under any
/// other key is a set the row ABI never sees: it is neither validated nor published, so a later read of the step's
/// result resolves nothing and the command's own rows never reach a reader.
///
/// <see cref="Port"/> is the one spelling, and a focused case asserts it against every action capability a
/// registration declares or binds, so the single spelling and the declarations cannot drift: a capability that
/// declared a different result port fails that case rather than silently writing rows nothing reads.
///
/// A row set is one row per explicit recipient, in the recipient set's own order, and each row's `target` is the
/// recipient at its position. Nothing here inspects, re-spells or invents a target: the row carries the identity
/// its producer read, and the row ABI validates it against the recipient at the same index and refuses a row that
/// names another one.
/// </summary>
public static class ResultRows
{
    /// <summary>The result port id every action row set is written under: the port every action capability
    /// declares in its own `recipients` block, asserted against those declarations by a focused case rather than
    /// restated at a call site.</summary>
    public const string Port = "result";

    /// <summary>One row set under <see cref="Port"/>. <paramref name="rows"/> is the row itself or the rows
    /// themselves, exactly as the handler built them: a "one" recipient set is the one row, a "many" set is the
    /// array of rows in the recipients' own order. A value that is already a <see cref="JsonElement"/> is carried
    /// as it is rather than framed a second time.</summary>
    public static JsonElement Of(object rows)
        => RuntimeJson.WriteResultRows(rows);

    /// <summary>One adjacent output without allocating a params array or an intermediate row document.</summary>
    public static JsonElement Of(object rows, (string Port, JsonElement Value) beside)
        => RuntimeJson.WriteResultRows(rows, beside);

    /// <summary>One row set beside the other ports the same command publishes — a cancellation handle, a solution
    /// handle, a count, the solved points. Those are ports of the node and never columns of a row, so they travel
    /// beside the row set and in the order they are given.</summary>
    public static JsonElement Of(object rows, params (string Port, JsonElement Value)[] beside)
    {
        if (beside.Length == 0) return Of(rows);
        if (beside.Length == 1) return Of(rows, beside[0]);
        var value = rows is JsonElement element ? element : RuntimeJson.From(rows);
        var frame = new Dictionary<string, JsonElement>(1 + beside.Length, StringComparer.Ordinal)
        {
            [Port] = value
        };
        foreach (var (port, other) in beside) frame[port] = other;
        return RuntimeJson.From(frame);
    }
}
