using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Advances progression objectives through the native StepProgressionObjective event.</summary>
internal static class ObjectiveEventContract
{
    public const string ProviderId = "forge.module.gtfo.map";
    public const string ProgressHandlerName = "gtfo.map.objective_progress";
    public const string ProgressPermission = "objective.progress";
    internal static readonly string[] Layers = { "main", "secondary", "third" };
    public static string Binding(string capabilityId)
        => capabilityId.StartsWith("forge.", StringComparison.Ordinal)
            ? ProviderId + ".binding." + capabilityId["forge.".Length..].Replace('.', '_')
            : throw new RuntimeContractException("objective-event-capability", capabilityId);

    public readonly struct ProgressRequest
    {
        internal ProgressRequest(ResourceRef target, int layer, string layerName, int steps)
        {
            Target = target;
            Layer = layer;
            LayerName = layerName;
            Steps = steps;
        }
        public ResourceRef Target { get; }
        public int Layer { get; }
        public string LayerName { get; }
        public int Steps { get; }
    }

    public static bool TryProgress(CommandContext context, out ProgressRequest request, out string? code)
    {
        ArgumentNullException.ThrowIfNull(context);
        request = default;
        code = null;
        if (!context.IsHost) { code = ResultCode.AuthorityOrPhase; return false; }
        if (!context.TryResource(ProgressObjectives, out var target) || target.ResourceKind != ObjectiveLayerReference.Kind)
        { code = ResultCode.ObjectiveTargetUnsupported; return false; }
        // The layer is the one the objective reference names; the recipient is its only spelling.
        int layer = ObjectiveLayerReference.LayerIndex(target, Layers);
        if (layer < 0) { code = ResultCode.ObjectiveLayerUnknown; return false; }
        string layerName = Layers[layer];
        if (context.Integer(ProgressSteps) is not { } steps || steps is < 1 or > int.MaxValue)
        {
            code = ResultCode.ObjectiveStepsInvalid;
            return false;
        }
        request = new ProgressRequest(target, layer, layerName, (int)steps);
        return true;
    }

    /// <summary>The row of a submitted progression: the native executor is a void request with no readback, so the row
    /// carries the same failed / unknown conclusion as its command.</summary>
    public static Outcome Issued(CommandContext context, ProgressRequest request)
    {
        var rows = context.Rows;
        rows.Begin(0).Text(Blocks.ActionMapObjectiveProgress.RowLayer, request.LayerName)
            .Integer(Blocks.ActionMapObjectiveProgress.RowSteps, request.Steps)
            .End(Status.Failed, Commit.Unknown, ResultCode.ObjectiveProgressIssued);
        return rows.Conclude();
    }

    public static readonly HandlerShape ProgressShape = new HandlerShape()
        .Inputs("objectives", "steps").Outputs("result");
    public static readonly HandlerSlot ProgressObjectives = ProgressShape.InputSlot("objectives");
    public static readonly HandlerSlot ProgressSteps = ProgressShape.InputSlot("steps");

    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.action_map_objective_progress", Blocks.ActionMapObjectiveProgress.Id, ProgressHandlerName, "execute", new[] { ProgressPermission }, ProgressShape)
    };

}
