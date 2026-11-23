using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The bindings and handler shapes of the enemy control rows this provider implements,
/// `forge.action.enemy.awaken`, `forge.action.enemy.sleep` and `forge.action.enemy.move_to`; the rows themselves
/// are the generated block table's (<see cref="Blocks"/>). The declaration half lives in the game-independent
/// package assembly, so the registration the game-side module builds and the one the release export builds read
/// the same bindings instead of two copies of them; the native handlers that answer the rows are the game-bound
/// assembly's own.
///
/// Three rows of the same family have no block and no binding, because a capability this provider cannot
/// answer would advertise a node that can never run: `forge.action.enemy.target_set` declares a `lease` handle
/// output, `forge.action.enemy.target_clear` and `forge.action.enemy.state_request` consume those handles as
/// their recipient input, and the runtime gives a provider no way to mint or read one
/// (`RuntimeKernel.CreateHandle` is private; `RuntimeModuleHandle` exposes Publish, CancelScope and Dispose).
/// They are reported in `ForgeEnemy/evidence/enemy-control-actions.json` rather than half-implemented.</summary>
internal static class EnemyControlContract
{
    /// <summary>The three binding ids and handler names, named rather than spelled at each call site: the bindings
    /// below declare them and the native handlers answer them.</summary>
    internal const string AwakenBinding = ModuleDefinition.ProviderId + ".binding.awaken";
    internal const string SleepBinding = ModuleDefinition.ProviderId + ".binding.sleep";
    internal const string MoveToBinding = ModuleDefinition.ProviderId + ".binding.move_to";
    internal const string AwakenHandler = "gtfo.enemy.awaken";
    internal const string SleepHandler = "gtfo.enemy.sleep";
    internal const string MoveToHandler = "gtfo.enemy.move_to";

    /// <summary>Each handler's own ports, declared once for both halves that register the rows and resolved at
    /// registration against the capability row the binding implements, so neither half can describe a different
    /// layout.</summary>
    internal static readonly HandlerShape AwakenPorts = new HandlerShape()
        .Inputs("enemies", "source", "reason").Outputs("result");
    internal static readonly HandlerShape SleepPorts = new HandlerShape()
        .Inputs("enemies").Outputs("result").Parameters("sleep_policy");
    internal static readonly HandlerShape MoveToPorts = new HandlerShape()
        .Inputs("enemies", "destination", "area", "speed", "arrival_tolerance").Outputs("result");

    internal static readonly HandlerSlot AwakenSourceSlot = AwakenPorts.InputSlot("source");
    internal static readonly HandlerSlot AwakenReasonSlot = AwakenPorts.InputSlot("reason");
    internal static readonly HandlerSlot SleepPolicySlot = SleepPorts.ParameterSlot("sleep_policy");
    internal static readonly HandlerSlot MoveDestinationSlot = MoveToPorts.InputSlot("destination");
    internal static readonly HandlerSlot MoveAreaSlot = MoveToPorts.InputSlot("area");
    internal static readonly HandlerSlot MoveSpeedSlot = MoveToPorts.InputSlot("speed");
    internal static readonly HandlerSlot MoveArrivalToleranceSlot = MoveToPorts.InputSlot("arrival_tolerance");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(AwakenBinding, Blocks.ActionEnemyAwaken.Id, AwakenHandler, "execute", new[] { "gtfo.enemy.behavior.write" }, AwakenPorts),
        new ProviderBinding(SleepBinding, Blocks.ActionEnemySleep.Id, SleepHandler, "execute", new[] { "gtfo.enemy.behavior.write" }, SleepPorts),
        new ProviderBinding(MoveToBinding, Blocks.ActionEnemyMoveTo.Id, MoveToHandler, "execute", new[] { "gtfo.enemy.navigation.write" }, MoveToPorts)
    };
}
