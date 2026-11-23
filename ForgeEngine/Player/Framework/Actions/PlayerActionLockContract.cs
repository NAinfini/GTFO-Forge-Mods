using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>
/// The one action-lock row this package owns: `forge.action.player.action_lock`, the write that blocks a player's
/// own input for a while and lets it go again when the duration runs out or the handle is cancelled.
///
/// Native evidence: `catalog/native/native-runtime.json`, types PlayerAgent / PlayerSync / pPlayerControlsToggle;
/// dump.cs:650974 `RequestToggleControlsEnabled(bool)` RVA 0x1305C40, :657676 `SendToggleControlsEnabled(bool)`
/// RVA 0x18DBDF0, :657694 `IncomingSyncControlsToggle(pPlayerControlsToggle)` RVA 0x18D94C0.
/// The packet (:657481) has only `bool Value`, with no action mask. The two complete PlayerSync bodies send that
/// bool and, on the peer where their ownership checks admit the local write, call `PlayerLocomotion.ChangeState`
/// with `StandStill` (17) for false or `Stand` (0) for true. The locomotion state is readable, but it is a shared
/// vanilla state and carries no writer, command, receipt or prior-state identity. This binding therefore carries
/// only `all`, and a later recovery cannot reconstruct one Forge lock from `StandStill`.
///
/// InputMapper exposes per-action readers, not a replicated per-action lock. `PlayerAgent.InputFilter` is an
/// eFocusState (focus context, not InputAction bits). The inventory's settable AllowScrollInput / AllowedToWieldItem
/// are local gates, not a synchronized swap+sprint command; the held item's WeaponSwitchAllowed / RunAllowed /
/// FireButton / CanReload are getter-only properties. Their existence does not prove such a command impossible
/// to implement with new hooks, nor prove that those hooks would enforce it correctly in multiplayer.
///
/// The Primitive's required structural enum exposes only `all`. Unsupported direct requests remain explicit
/// refusals; the authoring and plan gates reject them before execution.
/// </summary>
internal static class PlayerActionLockContract
{
    /// <summary>The row's canonical id, declared in the reviewed Primitive set.</summary>
    public const string CapabilityId = Blocks.ActionPlayerActionLock.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.action.player.action_lock";

    /// <summary>The handler name, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.player.action_lock";

    /// <summary>The permission the row's own recipient contract names (`recipients.requires`).</summary>
    public const string InputPermission = "player.input";

    /// <summary>The lifetime the row's `handle` output declares: the lock belongs to the player it was written on.</summary>
    public const string HandleLifetime = "entity_life";

    /// <summary>The port the row publishes its cancellation handle on.</summary>
    public const string HandlePort = "handle";

    /// <summary>The member supported by the native synchronized toggle described above.</summary>
    public const string AllMember = "all";

    /// <summary>The four unsupported slot names. They are recognised so the refusal can name the
    /// slot the plan asked for instead of reporting an unknown token.</summary>
    public static readonly IReadOnlyList<string> SlotMembers = Array.AsReadOnly(new[]
    {
        "swap", "sprint", "fire", "reload"
    });

    /// <summary>The ports the handler really reads and writes.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "duration").Outputs("result", "handle").Parameters("actions");

    public static readonly HandlerSlot TargetsSlot = Shape.InputSlot("targets");
    public static readonly HandlerSlot DurationSlot = Shape.InputSlot("duration");
    public static readonly HandlerSlot ActionsSlot = Shape.ParameterSlot("actions");

    /// <summary>The member one `actions` token names, or null when nothing names it. The comparison is ordinal and
    /// case-insensitive on a trimmed token, which is the same reading every other structural string of this
    /// package gets.</summary>
    public static string? Member(string token)
    {
        string trimmed = token.Trim();
        if (string.Equals(trimmed, AllMember, StringComparison.OrdinalIgnoreCase)) return AllMember;
        foreach (var slot in SlotMembers)
            if (string.Equals(trimmed, slot, StringComparison.OrdinalIgnoreCase)) return slot;
        return null;
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPlayerActionLock.Id, HandlerName, "execute", new[] { InputPermission }, Shape)
    };
}
