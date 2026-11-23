using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Session-wide actions owned by the Map package. Their typed graph is generated from the reviewed
/// Primitive SSOT; this file owns only provider identity, binding identity, permission and handler shape.</summary>
internal static class SessionActionContract
{
    public const string CheckpointSaveBinding = ModuleDefinition.ProviderId + ".binding.session.checkpoint_save";
    public const string CheckpointSaveHandler = "gtfo.map.session.checkpoint_save";
    public const string CheckpointPermission = "session.checkpoint";

    public static readonly HandlerShape CheckpointSaveShape =
        new HandlerShape().Inputs("participants", "anchor").Outputs("result");
    public static readonly HandlerSlot Participants = CheckpointSaveShape.InputSlot("participants");
    public static readonly HandlerSlot Anchor = CheckpointSaveShape.InputSlot("anchor");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(CheckpointSaveBinding, Blocks.ActionSessionCheckpointSave.Id, CheckpointSaveHandler, "execute", new[] { CheckpointPermission }, CheckpointSaveShape)
    };
}
