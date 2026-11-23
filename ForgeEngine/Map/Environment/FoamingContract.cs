using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The canonical combat row this provider implements for `gtfo.map_object.door`: `forge.action.combat.foaming`
/// is declared and owned by the shared combat contract, and this package binds it to its own handler exactly as the
/// enemy package binds the same capability to its own. The capability is the authority for the ports, the result
/// schema and the codes; nothing here restates them, and no second capability is invented for a door.
///
/// The row lives in the game-independent assembly because the runtime module, the manifest and the website read it
/// there; the handler and the native write path are the native assembly's `DoorFoamActions`, which answers through
/// this same shape. A registration only declares what it answers, which is why this file is a declaration and not
/// part of the identity table.</summary>
internal static class FoamingContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.foaming";
    public const string HandlerName = "gtfo.map_object.foaming";

    /// <summary>The permission the row's recipient contract turns into a door write: the game's own glue spawn
    /// entry for a door the plan named. It is the map-object counterpart of the enemy half's glue write.</summary>
    public const string Permission = "gtfo.map_object.glue.write";

    /// <summary>The one shape of this handler, resolved at registration against the canonical capability: the
    /// recipient collection, the required source reference, the foam volume, the effect multiplier, the Forge-side
    /// duration and the optional expansion potential. The row's own `foam` handle is published by the kernel, so the
    /// handler reads it and never writes it.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "source", "volume", "strength", "duration", "bubble_amount").Outputs("result", "foam").Parameters();
    public static readonly HandlerSlot Targets = Shape.InputSlot("targets");
    public static readonly HandlerSlot Source = Shape.InputSlot("source");
    public static readonly HandlerSlot Volume = Shape.InputSlot("volume");
    public static readonly HandlerSlot Strength = Shape.InputSlot("strength");
    public static readonly HandlerSlot Duration = Shape.InputSlot("duration");
    public static readonly HandlerSlot BubbleAmount = Shape.InputSlot("bubble_amount");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionCombatFoaming.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
