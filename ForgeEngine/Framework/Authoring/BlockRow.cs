using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>One block of the generated table: its id, the provider that registers it, the registry kind, the control
/// step the kernel walks for it, its execution tier, the permissions its recipients require and the result codes it
/// may answer. The table is generated from the reviewed Primitives and Behavior Operators
/// (`Tools/Forge/build-primitive-runtime.ts`), so nothing here is written a second time in C#.</summary>
public sealed record BlockRow(string Id, string Provider, string Kind, ControlOp Op, string Execution,
    IReadOnlyList<string> Requires, IReadOnlyList<string> Codes);

public static partial class Blocks
{
    public static ReadOnlySpan<BlockRow> Rows => Table;

    /// <summary>The position of the block called <paramref name="id"/>, or -1 when the table has none.</summary>
    public static int IndexOf(string id)
    {
        var low = 0;
        var high = Count - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var order = string.CompareOrdinal(Table[middle].Id, id);
            if (order == 0) return middle;
            if (order < 0) low = middle + 1; else high = middle - 1;
        }
        return -1;
    }

    /// <summary>One block as the registry reads it (id, owner, kind, label, version, parameters, graph), parsed from
    /// the text the table stores. Registration-time use only.</summary>
    public static JsonElement Capability(int index)
    {
        var reader = new Utf8JsonReader(CapabilityJson(index));
        return JsonElement.ParseValue(ref reader);
    }
}
