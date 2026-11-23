using System;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// An event's payload as the kernel reads it: one cell per value output of its trigger, in the registered binding's
/// order. The cells are made once, where the event is admitted — from the values a publisher wrote by position, or
/// from the document a publisher handed over — and every later reader (the claim filters, the waits, the actor
/// roles, a step that takes an event output as its input) reads a cell by an index resolved when the plan or the
/// binding was loaded. The payload is a document only for the readers that need one: the replay ledger mixes its
/// text, a migration image and a trace carry it.
/// </summary>
public sealed partial class RuntimeKernel
{
    /// <summary>The single entity one cell carries, or null for an absent cell, a null and a collection.</summary>
    internal static EntityReference? CellEntity(CommandValue cell)
        => cell.Kind == ValueKind.Entity ? cell.Reference as EntityReference : null;

    /// <summary>The text one cell carries, or null for anything that is not a single string.</summary>
    internal static string? CellText(CommandValue cell)
        => cell.Kind == ValueKind.String && cell.Count < 0 ? cell.Reference as string
            : cell.Json.ValueKind == JsonValueKind.String ? cell.Json.GetString() : null;

    /// <summary>The number one cell carries, or null for anything that is not a single number.</summary>
    internal static double? CellNumber(CommandValue cell)
        => cell.Count >= 0 ? null : cell.Kind == ValueKind.Number ? cell.Number : cell.Kind == ValueKind.Integer ? cell.Integer
            : cell.Json.ValueKind == JsonValueKind.Number && cell.Json.TryGetDouble(out var number) ? number : null;

    /// <summary>Whether a port's event cell is the cell a handler is handed: a kind whose value is decoded when the
    /// event is admitted. Every other kind (a handle, a resource, an event row, a collection of scalars) stays the
    /// port's JSON value and is checked where it is read.</summary>
    internal static bool DecodedEventPort(PortView port) => port.Kind switch
    {
        PortView.Kinds.Boolean or PortView.Kinds.Integer or PortView.Kinds.Number or PortView.Kinds.String
            or PortView.Kinds.Enum or PortView.Kinds.Entity => true,
        PortView.Kinds.Other => port.Type == "vector3" && !port.Many,
        _ => false
    };

    /// <summary>Whether a value that passed one port's check passes the other's and reads to the same handler value:
    /// what lets a step whose input names an event output take the admitted cell instead of checking the value again.
    /// A plan resolves its trigger contract from its own parameters, so the two views are compared, not assumed equal.</summary>
    internal static bool SameValueRule(PortView registered, PortView wired)
        => registered.Kind == wired.Kind && registered.Type == wired.Type && registered.Many == wired.Many
            && registered.Nullable == wired.Nullable && registered.HandlerKind == wired.HandlerKind
            && (registered.Kind != PortView.Kinds.Enum || registered.EnumNames.AsSpan().SequenceEqual(wired.EnumNames));

    internal static EventSubject SubjectOf(CommandValue[] cells, BindingView binding)
        => binding.EndSubjectIndex < 0 ? default
            : new EventSubject(cells[binding.EndSubjectIndex].Kind == ValueKind.Entity ? cells[binding.EndSubjectIndex].Reference : null);

}
