using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The generator rows this provider implements, as one more `gtfo.map_object` category (ruling 148.4): a power
/// generator is an object the level places, so it is addressed by <see cref="MapObjectGeneratorAddress"/> and its
/// facts are published by <see cref="MapObjectModule"/> through the same registration, the same authority gate
/// and the same `map-object` attachment matcher as a door and a terminal. There is no `gtfo.level_object`
/// namespace any more and no second publisher for the two rows.
///
/// `forge.trigger.map.generator_cell` reports one cell going in or out of one generator together with the running
/// state the generator is left in, `forge.trigger.map.generator_cluster` reports a group whose every member is powered, and
/// `forge.condition.predicate.power` reads one generator's powered flag and its group's counts. The value row's
/// resource kind is `generator`, and the id it resolves is the generator's own address text, so the resource
/// table and the facts name one object with one text.
///
/// The group's own native type is `LevelGeneration.LG_PowerGeneratorCluster`; the row's `cluster` port carries
/// the group's address, which is the same category's `group&lt;serial&gt;` key form.
/// </summary>
internal static class GeneratorContract
{
    /// <summary>The Map provider these rows belong to. The spelling is the one `ModuleDefinition.ProviderId`
    /// carries, restated here so this file compiles against the framework alone, which is what lets the focused
    /// test project exercise it without the game-independent assembly's other halves.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The resource kind both the value row's input and the provider's resource table carry: one
    /// generator, spelled by its own address text.</summary>
    public const string GeneratorResourceKind = "generator";

    // ---- handler and binding names -------------------------------------------------------------------

    /// <summary>The handler names the native half supplies, one per row. A fact arrives as one whole frame, so
    /// an `observe` binding resolves no shape; the names are the rows' own discriminators and are what the
    /// native publisher spells when it reports.</summary>
    public const string GeneratorCellHandler = "gtfo.map.generator_cell";
    public const string GeneratorClusterHandler = "gtfo.map.generator_cluster";
    /// <summary>The evaluator name the value row carries. A `query` row is dispatched to an evaluator, which is
    /// handed the budgeted query session and answers with the row's output ports.</summary>
    public const string GeneratorStateHandler = "gtfo.map.generator_state";

    /// <summary>The permission every row here reads through: a plan that wants to know what a generator is doing
    /// reads the map object state and nothing else. The control side stays with the action rows, which declare
    /// their own `power.cell`.</summary>
    public const string ReadPermission = "gtfo.map_object.read";

    /// <summary>The binding id of one row: the capability's own suffix under the Map provider, so either side of
    /// a row names the counterpart of the other without a second table.</summary>
    public static string Binding(string capabilityId)
    {
        int kind = capabilityId.IndexOf('.', "forge.".Length);
        if (kind < 0) throw new ArgumentException("capability id has no kind segment", nameof(capabilityId));
        return ProviderId + ".binding." + capabilityId[(kind + 1)..];
    }

    public static readonly string GeneratorCellBinding = Binding(Blocks.TriggerMapGeneratorCell.Id);
    public static readonly string GeneratorClusterBinding = Binding(Blocks.TriggerMapGeneratorCluster.Id);
    /// <summary>The value row's own binding: a `condition` row is answered by an evaluator, so its binding
    /// carries role `evaluate` and names the reader the registration must hold.</summary>
    public static readonly string GeneratorStateBinding = Binding(Blocks.ConditionPredicatePower.Id);

    /// <summary>Every binding this family declares, in the order the registry rows list them.</summary>
    public static readonly string[] Bindings =
    {
        GeneratorCellBinding, GeneratorClusterBinding, GeneratorStateBinding
    };

    /// <summary>The value row's port shape: a value row has no execution ports at all — its input is the
    /// resource it reads and its outputs are its readings.</summary>
    public static readonly HandlerShape GeneratorStateShape = new HandlerShape()
        .Inputs("generator").Outputs("value", "connected", "total");

    /// <summary>The evaluator of the value row, built from the reader the session hands over. This assembly
    /// declares the row and holds no game type, so the reader is a delegate and the game-bound registration is
    /// the only caller that supplies it.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(EvaluatorHandler read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [GeneratorStateHandler] = read
        };
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.map.generator_cell", Blocks.TriggerMapGeneratorCell.Id, GeneratorCellHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.map.generator_cluster", Blocks.TriggerMapGeneratorCluster.Id, GeneratorClusterHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding("forge.module.gtfo.map.binding.predicate.power", Blocks.ConditionPredicatePower.Id, GeneratorStateHandler, "evaluate", new[] { ReadPermission }, GeneratorStateShape)
    };
}
