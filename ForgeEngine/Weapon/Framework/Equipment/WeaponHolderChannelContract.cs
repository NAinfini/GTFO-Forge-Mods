using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The holder-client command channel: the one thing this slice has to add to the runtime for
/// <see cref="WeaponHolderActionsContract"/>'s two rows to be executable at all.
///
/// The kernel dispatches a command on the machine that owns the world, and until now the only tier that ran a
/// handler somewhere else was `presentation` — and that tier refuses a committing result by design, because it
/// writes a screen. A reload is the opposite case: it is a real world write whose correct machine is the holder's
/// client, not the host. The channel is therefore one tier, `owner`, with three parts:
///
/// <list type="number">
/// <item>the host walks an `owner` step the way it walks a presentation step — it resolves the step's inputs,
/// decides the timing, and records an intent instead of invoking a handler;</item>
/// <item>the intent travels as the same `CommandRequest` message the presentation tier already uses, marked with
/// its own node index, addressed to the holder's session;</item>
/// <item>the holder executes it through a kernel entry point that is <see cref="RuntimeKernel"/>'s own handler
/// invocation, with <c>IsHost</c> false, but <b>without</b> the presentation tier's no-commit rule: the write
/// really happens there, and the facts it publishes travel back through the ordinary fact channel.</item>
/// </list>
///
/// Who the holder is, is the provider's own answer, not the kernel's guess: this module registers one session
/// resolver per `owner` capability of its own (the runtime's <c>OwnerSessions</c> table, the same ownership rule
/// the presentation tier's recipient resolver follows), and a step whose holder cannot be named is refused with
/// <see cref="NoHolderCode"/> rather than sent to an implicit everyone.
///
/// This file is the contract half: the provider identity, the row ids and the refusal codes the two halves share.
/// The native half (<c>Native/Equipment/WeaponHolderChannel.cs</c>) is the one that reads a session out of a player
/// reference and performs the write.
/// </summary>
internal static class WeaponHolderChannelContract
{
    /// <summary>The provider the two holder rows belong to. It is its own provider rather than a row on
    /// <see cref="ModuleDefinition.ProviderId"/> because the owner-session resolver and the execution tier are
    /// registration facts of the module that owns the binding, and this module answers for exactly the two rows
    /// whose write happens on somebody else's machine.</summary>
    public const string ProviderId = "forge.module.gtfo.weapon.holder";

    /// <summary>The refusal for an `owner` step whose holder cannot be named. The tier refuses it by name, the
    /// same way the presentation tier refuses a step with no recipients: an action addressed to nobody is not a
    /// broadcast, and a silently accepted one would report a reload that never happened.</summary>
    public const string NoHolderCode = RuntimeKernel.OwnerHolderCode;

    /// <summary>The three capability ids this provider answers for, in declaration order. The resolver table is
    /// keyed by them, so a registration and its resolvers cannot drift apart.</summary>
    public static IReadOnlyList<string> Capabilities { get; } = new[]
    {
        WeaponHolderActionsContract.ReloadCapability,
        WeaponHolderActionsContract.ClipSetCapability,
        WeaponHolderActionsContract.AutoFireCapability
    };
}
