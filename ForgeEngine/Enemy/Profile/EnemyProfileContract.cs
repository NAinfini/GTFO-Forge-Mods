using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The game-independent half of `forge.enemy.profile`: the binding rows and handler shapes this provider's
/// execute handlers are registered with. `EnemyRegistration` composes them into the one declaration both the native
/// module and the release export read, so the registry spelling lives in one place instead of being repeated
/// inside `EnemyModule`. This contract belongs to the profile domain and keeps
/// its namespace unchanged; the project boundary keeps it available to release export without native code.
///
/// Five rows were surveyed against build 20403457. Only `phase_set` has a native write entry whose effect the
/// game itself replicates, so only that row has a block and a binding here; the other four have none rather
/// than being filled in with a shape the runtime cannot honour. Two of the
/// four are nevertheless served by this package without a graph row: `limb_profile` and `perception_profile` are
/// spawn-time static profiles, written on every peer from one document, which is why their reasons no longer say
/// the values cannot be delivered at all.</summary>
internal static class EnemyProfileContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.enemy.phase_set`: switch a SquidBoss to another phase.</summary>
    internal const string PhaseSetBinding = ProviderId + ".binding.phase_set";
    internal const string PhaseSetHandler = "gtfo.enemy.phase_set";

    /// <summary>The handler's own port set, resolved once at registration against the block's own row.
    /// `enemies` is the recipient collection; `phase` and `expected_phase` follow it in the declared order.</summary>
    internal static readonly HandlerShape PhaseSetPorts = new HandlerShape()
        .Inputs("enemies", "phase", "expected_phase").Outputs("result");
    internal static readonly HandlerSlot PhaseSlot = PhaseSetPorts.InputSlot("phase");
    internal static readonly HandlerSlot ExpectedPhaseSlot = PhaseSetPorts.InputSlot("expected_phase");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(PhaseSetBinding, Blocks.ActionEnemyPhaseSet.Id, PhaseSetHandler, "execute", new[] { "gtfo.enemy.behavior.write" }, PhaseSetPorts)
    };
}
