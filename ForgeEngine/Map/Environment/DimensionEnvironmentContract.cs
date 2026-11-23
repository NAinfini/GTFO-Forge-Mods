using System;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The `forge.action.presentation.dimension_environment` row: one dimension's outside environment — whether it
/// counts as outside, and its light, atmosphere, cloud and sandstorm parameters.
///
/// <b>Why it is a presentation row.</b> The values it writes are the ones the game's own dimension record carries
/// (<c>DimensionData</c>) and they decide only what a player sees: the sky, the light and the weather. No gameplay
/// decision reads them, no state replicator carries them, and the read rows this provider has
/// (<c>forge.query.environment.state</c>) answer fog preset, fog height and zone lights and none of these members
/// — so the card writes each client's own view rather than a world state another machine would have to replicate.
///
/// <b>The address.</b> The dimension is the card's own `dimension` input, an integer, the same type
/// <see cref="EntityDimensionContract"/> answers with: a request names the dimension it changes instead of having
/// it inferred from the recipients' zones, which would write the same dimension once per zone standing in it.
///
/// <b>The entries.</b> <see cref="EnvironmentEntries"/> is the whole list the card declares, in the graph's own
/// order, each spelled exactly as the reviewed card spells it and mapped to the game's own member by the native
/// half. A member the request does not name is left exactly as the game had it: the card's rule is that an
/// unwired entry changes nothing, and there is no third state beside "named" and "not named".
///
/// This file is the game-independent half (the binding and its shape), and the native half is
/// <c>ForgeMap.Native.DimensionEnvironmentActions</c>. The recipient contract declares no permission for this row
/// — the reviewed card's own `requires` is empty, because the existing receiver vocabulary has no token for a
/// dimension's outside appearance and this package does not invent one.</summary>
internal static class DimensionEnvironmentContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.presentation.dimension_environment";
    public const string HandlerName = "gtfo.map.dimension_environment";

    /// <summary>The one port the row is addressed by: the integer dimension index the environment belongs to.
    /// It is the same member family <c>forge.query.entity.dimension</c> answers with, so a plan can feed one
    /// row's answer into this row's address.</summary>
    public const string DimensionPort = "dimension";

    /// <summary>
    /// Every environment entry the card declares, in the reviewed graph's own order. An index in this array is
    /// not a native value: the name is the address, and the native half maps it to the game's own
    /// <c>DimensionData</c> member. The two `integer` entries are data block ids (`AtmosphereData`,
    /// `CloudsData`); `is_outside` and `sandstorm` are booleans; every other entry is a number.
    /// </summary>
    public static readonly string[] EnvironmentEntries =
    {
        "is_outside",
        "light_azimuth", "light_elevation", "light_intensity",
        "ambient_intensity", "reflections_intensity", "godray_range", "godray_exponent",
        "atmosphere", "atmosphere_density", "exposure", "aerial_scale",
        "mie_scattering", "mie_g", "multiple_scattering",
        "clouds", "clouds_coverage", "clouds_density", "clouds_sharpness", "clouds_shadow_opacity",
        "clouds_timescale", "clouds_crawling", "clouds_fade",
        "sandstorm", "sandstorm_edge_a", "sandstorm_edge_b", "sandstorm_min_fog"
    };

    /// <summary>The row's port shape: the audience it is presented to, the dimension it addresses, every
    /// environment entry the card declares, and the result row. The entries are optional in the graph, so the
    /// shape names them all exactly as the capability does and the handler reads whichever the request filled.</summary>
    public static readonly HandlerShape Shape = BuildShape();
    public static readonly HandlerSlot Viewers = Shape.InputSlot("viewers");
    public static readonly HandlerSlot Dimension = Shape.InputSlot(DimensionPort);
    public static readonly HandlerSlot[] EntrySlots = EnvironmentEntries.Select(Shape.InputSlot).ToArray();

    private static HandlerShape BuildShape()
    {
        var shape = new HandlerShape().Inputs(new[] { "viewers", DimensionPort }.Concat(EnvironmentEntries).ToArray());
        return shape.Outputs("result");
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationDimensionEnvironment.Id, HandlerName, "execute", Array.Empty<string>(), Shape)
    };
}
