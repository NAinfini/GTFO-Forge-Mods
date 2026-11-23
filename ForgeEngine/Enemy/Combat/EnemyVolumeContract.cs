using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The one effect-volume row this package owns: `forge.action.combat.effect_volume`, the native volume
/// the game's own `EffectVolumeManager` runs against a sphere the game's own `EV_Sphere` describes.
///
/// The row is anchored at an entity rather than at a bare coordinate: the catalog has no `entity_or_position`
/// port type, and inventing one is the runtime's call rather than this module's, so the row takes the entity
/// collection every other action in this family takes and reads each anchor's own position. `follow_anchor`
/// moves the volume with the anchor it was placed on; without it the volume stays where the anchor stood.
///
/// The volume is the gameplay half: `contents` selects which of the target's own two values the volume writes
/// (`all`, `health`, `infection`), `modification` whether it inflicts or shields it, `scale` the amount the
/// volume's own `ComputeAmount` is multiplied by, and `radius_min` / `radius_max` the two distances the game
/// interpolates between, so a target at the centre receives the full amount and one at or beyond the outer
/// radius receives none. A plan that wants a status or a damage tick inside the volume composes it from the
/// generic damage and effect cards, exactly as the R2 ruling says: this row carries the volume, not a status
/// registry.
///
/// The volume's visible body is the game's own fog sphere (`FogSphereAllocator`), allocated at the same position
/// and radius so the volume can be seen where it is. That half is presentation: a fog-sphere budget with no room
/// left does not fail the submission, and the result row's own `code` column says which of the two happened
/// (`volume-placed` or `volume-placed-undrawn`), because the row's result schema has the four fixed columns and
/// the count and no sixth column of its own.
///
/// The row carries no lifetime port. Its clock is the step's own `effect` block (I-PLAN §3.4): the kernel casts
/// the handle, times the instance and calls the module's own restore callback when it ends, so the volumes one
/// dispatch placed are released together — by their duration, by a cancellation, by a released plan or by the
/// world. The catalog ports are `in / anchors` in and `next / result / volumes` out: `volumes` is that same
/// kernel handle, published into the port when the card asked for a cancel handle, and absent for a card that
/// asked for no effect at all, whose volumes live until the module releases them.</summary>
internal static class EnemyVolumeContract
{
    /// <summary>The provider every row here belongs to; the same string `ModuleDefinition.ProviderId` carries.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>`forge.action.combat.effect_volume`: register one native effect volume per anchor.</summary>
    public const string VolumeBinding = ProviderId + ".binding.effect_volume";
    public const string VolumeHandler = "gtfo.combat.effect_volume";

    /// <summary>The permission the binding writes the world through: a value written to every target standing
    /// inside the volume the game itself keeps registered.</summary>
    public const string VolumePermission = "gtfo.effect_volume.write";

    /// <summary>Native effect contents plus environmental fog. Fog creates no health/infection volume;
    /// scale is its native density, modification must be inflict and the inner radius must be zero.</summary>
    public static readonly string[] Contents = { "all", "health", "infection", "fog" };

    /// <summary>The handler's ports: `anchors` is the recipient collection and `volumes` the kernel's own effect
    /// handle, published into that port when the card asked for a cancel handle. The four numeric and boolean
    /// choices are the row's own structural parameters, so the shape declares only the ports.</summary>
    public static readonly HandlerShape Ports = new HandlerShape()
        .Inputs("anchors").Outputs("result", "volumes")
        .Parameters("contents", "modification", "scale", "radius_min", "radius_max", "follow_anchor");
    public static readonly HandlerSlot AnchorsSlot = Ports.InputSlot("anchors");
    public static readonly HandlerSlot ContentsSlot = Ports.ParameterSlot("contents");
    public static readonly HandlerSlot ModificationSlot = Ports.ParameterSlot("modification");
    public static readonly HandlerSlot ScaleSlot = Ports.ParameterSlot("scale");
    public static readonly HandlerSlot RadiusMinSlot = Ports.ParameterSlot("radius_min");
    public static readonly HandlerSlot RadiusMaxSlot = Ports.ParameterSlot("radius_max");
    public static readonly HandlerSlot FollowAnchorSlot = Ports.ParameterSlot("follow_anchor");

    public static HandlerShape Shape() => Ports;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(VolumeBinding, Blocks.ActionCombatEffectVolume.Id, VolumeHandler, "execute", new[] { VolumePermission }, Ports)
    };
}
