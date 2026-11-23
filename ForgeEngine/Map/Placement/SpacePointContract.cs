using ForgeEngine.Framework;

namespace ForgeMap;

internal static class SpacePointContract
{
    public const string ContainsHandler = "gtfo.map.point_in_area", ForwardHandler = "gtfo.map.forward_positions";
    public const string ContainsBinding = ModuleDefinition.ProviderId + ".binding.point_in_area";
    public const string ContainsReadCode = "map.area-point-read", ContainsRefusedCode = "map.area-point-refused",
        ContainsUnresolvedCode = "area-point-unresolved";
    public static readonly HandlerShape ContainsShape = new HandlerShape().Inputs("field", "position").Outputs("inside");
    public static readonly HandlerSlot Field = ContainsShape.InputSlot("field"), Position = ContainsShape.InputSlot("position");
    public static readonly HandlerShape ForwardShape = new HandlerShape()
        .Inputs("dimension", "origin", "direction", "near", "far", "width", "angle", "count", "attempts", "seed")
        .Outputs("positions", "actual_count", "exhausted").Parameters("shape");
    public static readonly HandlerSlot Dimension = ForwardShape.InputSlot("dimension"), Origin = ForwardShape.InputSlot("origin"),
        Direction = ForwardShape.InputSlot("direction"), Near = ForwardShape.InputSlot("near"), Far = ForwardShape.InputSlot("far"),
        Width = ForwardShape.InputSlot("width"), Angle = ForwardShape.InputSlot("angle"), Count = ForwardShape.InputSlot("count"), Attempts = ForwardShape.InputSlot("attempts"),
        Seed = ForwardShape.InputSlot("seed"), Shape = ForwardShape.ParameterSlot("shape");
    public static ProviderBinding[] Bindings() => new[] {
        new ProviderBinding(ContainsBinding, Blocks.QuerySpacePointInArea.Id, ContainsHandler, "observe", System.Array.Empty<string>(), ContainsShape),
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.forward_positions", Blocks.QuerySpaceForwardPositions.Id, ForwardHandler, "observe", System.Array.Empty<string>(), ForwardShape) };
}
