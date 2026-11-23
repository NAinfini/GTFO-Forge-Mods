using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Author text receipts, not objective progress.</summary>
internal static class ObjectivePresentationContract
{
    public const string MainHandler = "gtfo.map.objective_main";
    public const string ListHandler = "gtfo.map.objective_list";
    public const string MainBinding = ModuleDefinition.ProviderId + ".binding.action_presentation_objective_main";
    public const string ListBinding = ModuleDefinition.ProviderId + ".binding.action_presentation_objective_list";
    public const string Permission = "objective.text";

    internal static readonly string[] Layers = { "main", "secondary", "third" };

    public static readonly HandlerShape MainShape = new HandlerShape()
        .Inputs("objectives", "text", "text_id").Outputs("result");
    public static readonly HandlerShape ListShape = new HandlerShape()
        .Inputs("objectives", "header", "body", "header_id", "body_id", "priority", "layer_independent", "visible")
        .Outputs("result").Parameters("entry");

    private static readonly HandlerSlot MainTarget = MainShape.InputSlot("objectives"),
        MainText = MainShape.InputSlot("text"), MainTextId = MainShape.InputSlot("text_id");
    private static readonly HandlerSlot ListTarget = ListShape.InputSlot("objectives"),
        EntryName = ListShape.ParameterSlot("entry"), Priority = ListShape.InputSlot("priority"), Independent = ListShape.InputSlot("layer_independent"),
        Header = ListShape.InputSlot("header"), HeaderId = ListShape.InputSlot("header_id"),
        Body = ListShape.InputSlot("body"), BodyId = ListShape.InputSlot("body_id"), Visible = ListShape.InputSlot("visible");
    public readonly record struct MainRequest(ResourceRef Target, int Layer, TextResourceContract.TextValue Text);
    public readonly record struct ListRequest(ResourceRef Target, int Layer, string Entry, int Priority,
        bool LayerIndependent, bool Visible, TextResourceContract.TextValue Header, TextResourceContract.TextValue Body);

    public static bool TryMain(CommandContext context, out MainRequest request, out string? code)
    {
        request = default;
        if (!TryTarget(context, MainTarget, out var target, out int layer, out code)
            || !TryText(context, MainText, MainTextId, out var text, out code)) return false;
        request = new(target!, layer, text);
        return true;
    }

    public static bool TryList(CommandContext context, out ListRequest request, out string? code)
    {
        request = default;
        if (!TryTarget(context, ListTarget, out var target, out int layer, out code)) return false;
        var entry = context.Text(EntryName);
        if (!ValidIdentity(entry)) { code = ResultCode.ObjectiveEntryInvalid; return false; }
        if (context.Integer(Priority) is not { } priority
            || priority < int.MinValue || priority > int.MaxValue)
        { code = ResultCode.ObjectivePriorityInvalid; return false; }
        if (context.Boolean(Independent) is not { } independent)
        { code = ResultCode.ObjectiveLayerScopeRequired; return false; }
        // `visible = false` withdraws the entry: only its identity is needed, so absent texts are not refused.
        bool visible = context.Boolean(Visible) ?? true;
        TextResourceContract.TextValue header = default, body = default;
        if (visible && (!TryText(context, Header, HeaderId, out header, out code)
            || !TryText(context, Body, BodyId, out body, out code))) return false;
        request = new(target!, layer, entry!, (int)priority, independent, visible, header, body);
        return true;
    }

    internal static bool ValidIdentity(string? entry)
        => !string.IsNullOrWhiteSpace(entry) && entry == entry.Trim()
            && Array.TrueForAll(entry.ToCharArray(), c => !char.IsControl(c));

    /// <summary>The objective reference and the layer it names (`layer:&lt;name&gt;`): the recipient is the only spelling of the layer.</summary>
    private static bool TryTarget(CommandContext context, HandlerSlot targetSlot, out ResourceRef? target, out int layer, out string? code)
    {
        target = null; layer = -1; code = null;
        if (!context.IsHost) { code = ResultCode.AuthorityOrPhase; return false; }
        if (!context.TryResource(targetSlot, out var resource)
            || resource.ResourceKind != ObjectiveLayerReference.Kind)
        { code = ResultCode.ObjectiveTargetUnsupported; return false; }
        layer = ObjectiveLayerReference.LayerIndex(resource, Layers);
        if (layer < 0) { code = ResultCode.ObjectiveLayerUnknown; return false; }
        target = resource;
        return true;
    }

    private static bool TryText(CommandContext context, HandlerSlot literalSlot, HandlerSlot storedSlot,
        out TextResourceContract.TextValue text, out string? code)
    {
        text = default; code = null;
        if (context.Present(literalSlot) && context.Present(storedSlot))
        { code = ResultCode.ObjectiveTextPairInvalid; return false; }
        if (!TextResourceContract.TryText(context, literalSlot, storedSlot, out text, out code)) return false;
        if (!text.HasText || !text.Stored && string.IsNullOrWhiteSpace(text.Literal))
        { code = ResultCode.ObjectiveTextRequired; return false; }
        return true;
    }

    /// <summary>The one row of a written main objective text, about the objective reference the request carried.</summary>
    public static Outcome MainWritten(CommandContext context, MainRequest request, string text)
    {
        var rows = context.Rows;
        rows.Begin(0).Text(Blocks.ActionPresentationObjectiveMain.RowLayer, Layers[request.Layer])
            .Text(Blocks.ActionPresentationObjectiveMain.RowText, text)
            .End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return rows.Conclude();
    }

    /// <summary>The one row of a written objective list entry, about the objective reference the request carried.</summary>
    public static Outcome ListWritten(CommandContext context, ListRequest request, string header, string body)
    {
        var rows = context.Rows;
        Columns(rows.Begin(0), Layers[request.Layer], header, body, request.Entry, request.Priority, request.LayerIndependent)
            .End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return rows.Conclude();
    }

    public static Outcome Refused(CommandContext context, bool list, string code)
    {
        // Preserve the exact resolved resource recipient even when a dynamic text check fails.
        if (!context.TryResource(list ? ListTarget : MainTarget, out var target)
            || target.ResourceKind != ObjectiveLayerReference.Kind) return Outcome.Rejected(code);
        int index = ObjectiveLayerReference.LayerIndex(target, Layers);
        string layer = index < 0 ? "" : Layers[index];
        var row = context.Rows.Begin(0);
        if (list)
            row = Columns(row, layer, context.Text(Header) ?? "", context.Text(Body) ?? "", context.Text(EntryName) ?? "",
                context.Integer(Priority) is { } value && value >= int.MinValue && value <= int.MaxValue ? (int)value : 0,
                context.Boolean(Independent) ?? false);
        else
            row = row.Text(Blocks.ActionPresentationObjectiveMain.RowLayer, layer)
                .Text(Blocks.ActionPresentationObjectiveMain.RowText, context.Text(MainText) ?? "");
        row.End(Status.Rejected, Commit.None, code);
        return Outcome.Rejected(code);
    }

    private static RowWriter Columns(RowWriter row, string layer, string header, string body, string entry, int priority, bool independent)
        => row.Text(Blocks.ActionPresentationObjectiveList.RowLayer, layer)
            .Text(Blocks.ActionPresentationObjectiveList.RowHeader, header)
            .Text(Blocks.ActionPresentationObjectiveList.RowBody, body)
            .Text(Blocks.ActionPresentationObjectiveList.RowEntry, entry)
            .Integer(Blocks.ActionPresentationObjectiveList.RowPriority, priority)
            .Boolean(Blocks.ActionPresentationObjectiveList.RowLayerIndependent, independent);

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        new ProviderBinding(MainBinding, Blocks.ActionPresentationObjectiveMain.Id, MainHandler, "execute", new[] { Permission }, MainShape),
        new ProviderBinding(ListBinding, Blocks.ActionPresentationObjectiveList.Id, ListHandler, "execute", new[] { Permission }, ListShape)
    };
}
