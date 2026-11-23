using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// Where an evaluator writes its answer: one typed cell per output port, addressed by the output's position in the
/// handler shape's own <see cref="HandlerShape.Outputs"/> list (a shape that declares no outputs addresses the
/// capability's outputs in contract order). The kernel owns one of these per `pure`/`query` step and hands the same
/// one to every evaluation of that step, so a value written here is not a document: nothing is serialized unless a
/// boundary — a trace, a presentation packet, a handler that reads its inputs as JSON — asks for it.
///
/// Every output of the contract must be written; the kernel checks each written value against its port after the
/// evaluator returns (kind, bounds, enum membership, entity liveness and distinctness, nullability) with the codes the
/// JSON boundary always used. A kind with no typed setter here (a handle, a resource, a collection of scalars) is
/// written with <see cref="Json"/> and checked by the port's own rule. The object is valid only during the call: an
/// evaluator must not keep it.
/// </summary>
public sealed class EvaluationOutputs
{
    private readonly int[]? map;
    private readonly string[] ids;
    internal string[] Ids => ids;
    internal readonly CommandValue[] Cells;
    /// <summary>The reference arrays last published per output. An evaluation that answers the same references keeps
    /// the array it published before; one that answers differently publishes a new one, so a frame that kept the old
    /// array still holds the value it was given.</summary>
    private readonly EntityReference[]?[] published;

    /// <summary>A standalone answer sheet for the given output ids, in the order they are addressed: what a test or a
    /// tool that calls an evaluator directly hands it, read back with <see cref="ToJson"/>.</summary>
    public EvaluationOutputs(params string[] outputIds)
    {
        ids = outputIds ?? throw new ArgumentNullException(nameof(outputIds));
        Cells = new CommandValue[ids.Length];
        published = new EntityReference[]?[ids.Length];
    }

    internal EvaluationOutputs(string[] contractIds, int[]? declaredToContract)
    {
        ids = contractIds;
        map = declaredToContract;
        Cells = new CommandValue[ids.Length];
        published = new EntityReference[]?[ids.Length];
    }

    internal void Begin() => Array.Clear(Cells, 0, Cells.Length);

    /// <summary>The contract index of a declared output.</summary>
    private int Index(int output)
    {
        var index = map == null ? output : (uint)output < (uint)map.Length ? map[output] : -1;
        if ((uint)index >= (uint)Cells.Length) throw new RuntimeContractException("shape-port", "output " + output);
        return index;
    }

    public void Boolean(int output, bool value) => Cells[Index(output)] = CommandValue.OfBoolean(value);
    public void Integer(int output, long value) => Cells[Index(output)] = CommandValue.OfInteger(value);
    public void Number(int output, double value) => Cells[Index(output)] = CommandValue.OfNumber(value);
    /// <summary>A string, or an enum member by name.</summary>
    public void Text(int output, string value)
        => Cells[Index(output)] = CommandValue.OfText(value ?? throw new RuntimeContractException("invalid-string", "Expected bounded string."));
    /// <summary>An enum member by its index in the port's set.</summary>
    public void Enum(int output, int member) => Cells[Index(output)] = CommandValue.OfInteger(member);
    public void Vector(int output, double x, double y, double z) => Cells[Index(output)] = CommandValue.OfVector(x, y, z);
    public void Vector(int output, (double X, double Y, double Z) value) => Vector(output, value.X, value.Y, value.Z);
    /// <summary>One reference; null writes the port's null.</summary>
    public void Entity(int output, EntityReference? value)
        => Cells[Index(output)] = value == null ? CommandValue.OfNull() : CommandValue.OfEntity(value);
    /// <summary>A collection of references, copied: the evaluator may reuse its list as soon as this returns.</summary>
    public void Entities(int output, IReadOnlyList<EntityReference> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var index = Index(output);
        var previous = published[index];
        if (previous == null || previous.Length != values.Count || !Same(previous, values))
        {
            previous = values.Count == 0 ? Array.Empty<EntityReference>() : new EntityReference[values.Count];
            for (var i = 0; i < previous.Length; i++)
                previous[i] = values[i] ?? throw new RuntimeContractException("invalid-array", "entity many");
            published[index] = previous;
        }
        Cells[index] = CommandValue.OfEntities(previous);

        static bool Same(EntityReference[] previous, IReadOnlyList<EntityReference> values)
        {
            for (var i = 0; i < previous.Length; i++)
            {
                var value = values[i];
                if (ReferenceEquals(previous[i], value)) continue;
                if (value == null || previous[i].WorldEpoch != value.WorldEpoch || previous[i].LifeEpoch != value.LifeEpoch
                    || !string.Equals(previous[i].Id, value.Id, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
    /// <summary>One resource reference; null writes the port's null.</summary>
    public void Resource(int output, ResourceRef? value)
        => Cells[Index(output)] = value == null ? CommandValue.OfNull() : CommandValue.OfResource(value);
    public void Null(int output) => Cells[Index(output)] = CommandValue.OfNull();
    /// <summary>A value of a kind with no typed setter, in the port's JSON form.</summary>
    public void Json(int output, JsonElement value) => Cells[Index(output)] = CommandValue.OfJson(value);
    /// <summary>A handle output from its value; the wire form a handle port carries is written here, once.</summary>
    public void Handle(int output, RuntimeHandle value) => Json(output, value.ToJson());
    /// <summary>A handle collection output from its values, each at its own position; a position that carries no
    /// handle writes the port's null. The array is written once, here.</summary>
    public void Handles(int output, IReadOnlyList<RuntimeHandle?> values)
        => Json(output, RuntimeJson.Write(values ?? throw new ArgumentNullException(nameof(values)), static (writer, handles) =>
        {
            writer.WriteStartArray();
            for (var index = 0; index < handles.Count; index++)
                if (handles[index] is { } handle) handle.Write(writer);
                else writer.WriteNullValue();
            writer.WriteEndArray();
        }));

    /// <summary>The written outputs as the object a frame carries: every written output under its id, in order, enum
    /// members as their index where the kernel resolved them.</summary>
    public JsonElement ToJson() => RuntimeJson.Write(this, static (writer, outputs) => outputs.Write(writer));

    /// <summary>The same object, written token by token into a writer the caller owns.</summary>
    internal void Write(Utf8JsonWriter writer) => Write(writer, ids, Cells);

    private static void Write(Utf8JsonWriter writer, string[] ids, CommandValue[] cells)
    {
        writer.WriteStartObject();
        for (var i = 0; i < cells.Length; i++)
        {
            if (!cells[i].Carried) continue;
            writer.WritePropertyName(ids[i]);
            cells[i].WriteValue(writer, wire: true);
        }
        writer.WriteEndObject();
    }

    /// <summary>The object a kept copy of a sheet's cells renders to: the text <see cref="ToJson"/> gave the sheet.</summary>
    internal static JsonElement Render(string[] ids, CommandValue[] cells)
        => RuntimeJson.Write((Ids: ids, Cells: cells), static (writer, kept) => Write(writer, kept.Ids, kept.Cells));

    /// <summary>The written cells as a copy the sheet's next use cannot change. A value written as a document is
    /// cloned, so the copy does not depend on a document its writer may dispose.</summary>
    internal CommandValue[] Snapshot()
    {
        var copy = (CommandValue[])Cells.Clone();
        for (var i = 0; i < copy.Length; i++)
            if (!copy[i].Typed && copy[i].Json.ValueKind != JsonValueKind.Undefined) copy[i] = CommandValue.OfJson(copy[i].Json.Clone());
        return copy;
    }
}
