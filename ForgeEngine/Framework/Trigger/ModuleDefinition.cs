using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger;

/// <summary>The production Trigger provider. Its node vocabulary is the declaration tables this class composes
/// — <see cref="PureModule"/> for the value-only rows, <see cref="ObservedQueryModule"/> for the observation rows,
/// <see cref="ObservedSpaceNodes"/> for the rows that select from explicit candidates and <see cref="AreaFieldNodes"/>
/// for the row that measures one against an authored area field — and every row is the generated block table's
/// (<see cref="Blocks"/>), bound to a handler in this assembly. This provider also owns the `area_field`
/// resource kind: the field definitions this install ships, read from the published runtime resources through the
/// session the native pump keeps in step with the world. Nothing here declares a node, and no `execute` binding
/// exists yet: this provider evaluates, it does not run commands.</summary>
internal static class ModuleDefinition
{
    public const string ProviderId = "forge.module.trigger";

    /// <summary>
    /// The production Trigger provider with no install in front of it: the honest empty field table, for a caller
    /// that has no BepInEx root to read (a test, or the release export). The host registers the same
    /// module with the session its own field read produced.
    /// </summary>
    public static RuntimeModule Create() => Create(AreaFieldSession.Empty, null);

    /// <summary>
    /// The production Trigger provider, with the field session this install loaded and the world's line-of-sight
    /// cast. The session is a parameter because the one caller that knows the BepInEx root is the host;
    /// every read of the `area_field` kind goes through the table the session currently holds, so a new world
    /// replaces one table instead of re-registering the module. The sight row is declared only when a probe is
    /// handed in, because only the native half can cast against the level.
    /// </summary>
    public static RuntimeModule Create(AreaFieldSession fields, LineOfSightProbe? sight)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var area = fields.Family;
        var world = ObservedFamily.Compose(area, sight == null ? ObservedFamily.Declare() : SightDeclarations.Family(sight));
        var nodes = PureModule.Nodes.Concat(ObservedQueryModule.Nodes).Concat(ObservedSpaceNodes.Nodes).Concat(world.Nodes).ToArray();
        var declaration = new ProviderDeclaration(ProviderId, EnginePackage.Version)
            .DeclareBlocks(sight == null ? new[] { SightDeclarations.CapabilityId } : Array.Empty<string>())
            .Bind(nodes.Select(node => new ProviderBinding(node.BindingId, node.CapabilityId, node.HandlerName, node.Role,
                Array.Empty<string>(), node.Shape)).ToArray());
        return declaration.Module(RuntimeApi.Fingerprint, evaluators: nodes.ToDictionary(node => node.HandlerName, node => node.Evaluate,
            StringComparer.Ordinal)) with
        {
            // The `area_field` kind is this provider's own: the definitions come from the runtime resources this
            // install ships, read through the session the pump keeps in step with the world. A kind nobody
            // registered would leave every read of it refused, and a kind registered by two packages would make
            // which fields exist depend on registration order.
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
            {
                [AreaFieldDocument.ResourceKind] = RuntimeResourceProvider.Of(
                    () => fields.Current.Enumerate(), id => fields.Current.Resolve(id),
                    revision: id => fields.Current.Revision(id))
            }
        };
    }

}
