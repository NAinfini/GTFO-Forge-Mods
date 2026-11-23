using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The three host-authoritative writes of a placed model's root transform. Each row is independently
/// addressable while the native half shares receiver validation, interpolation and replication.</summary>
internal static class PlacedModelTransformContract
{
    public const string PositionHandler = "gtfo.map.set_model_position";
    public const string RotationHandler = "gtfo.map.set_model_rotation";
    public const string ScaleHandler = "gtfo.map.set_model_scale";
    public const string PositionPermission = "map.placed_model.position";
    public const string RotationPermission = "map.placed_model.rotation";
    public const string ScalePermission = "map.placed_model.scale";

    public static readonly HandlerShape PositionShape = Shape();
    public static readonly HandlerShape RotationShape = Shape();
    public static readonly HandlerShape ScaleShape = Shape();
    public static readonly HandlerSlot PositionModels = PositionShape.InputSlot("models");
    public static readonly HandlerSlot PositionX = PositionShape.InputSlot("x");
    public static readonly HandlerSlot PositionY = PositionShape.InputSlot("y");
    public static readonly HandlerSlot PositionZ = PositionShape.InputSlot("z");
    public static readonly HandlerSlot PositionDuration = PositionShape.InputSlot("duration");
    public static readonly HandlerSlot PositionMode = PositionShape.ParameterSlot("mode");
    public static readonly HandlerSlot RotationModels = RotationShape.InputSlot("models");
    public static readonly HandlerSlot RotationX = RotationShape.InputSlot("x");
    public static readonly HandlerSlot RotationY = RotationShape.InputSlot("y");
    public static readonly HandlerSlot RotationZ = RotationShape.InputSlot("z");
    public static readonly HandlerSlot RotationDuration = RotationShape.InputSlot("duration");
    public static readonly HandlerSlot RotationMode = RotationShape.ParameterSlot("mode");
    public static readonly HandlerSlot ScaleModels = ScaleShape.InputSlot("models");
    public static readonly HandlerSlot ScaleX = ScaleShape.InputSlot("x");
    public static readonly HandlerSlot ScaleY = ScaleShape.InputSlot("y");
    public static readonly HandlerSlot ScaleZ = ScaleShape.InputSlot("z");
    public static readonly HandlerSlot ScaleDuration = ScaleShape.InputSlot("duration");
    public static readonly HandlerSlot ScaleMode = ScaleShape.ParameterSlot("mode");

    private static HandlerShape Shape()
        => new HandlerShape().Inputs("models", "x", "y", "z", "duration").Outputs("result").Parameters("mode");

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.action.map.set_model_position",
            Blocks.ActionMapSetModelPosition.Id, PositionHandler, "execute", new[] { PositionPermission }, PositionShape),
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.action.map.set_model_rotation",
            Blocks.ActionMapSetModelRotation.Id, RotationHandler, "execute", new[] { RotationPermission }, RotationShape),
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.action.map.set_model_scale",
            Blocks.ActionMapSetModelScale.Id, ScaleHandler, "execute", new[] { ScalePermission }, ScaleShape)
    };
}
