using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The holder-client action rows: the two actions of the node list whose write is only correct on the machine
/// that holds the weapon.
///
/// `forge.action.weapon.reload` and `forge.action.weapon.clip_set` both change the magazine of one equipment
/// instance. That state is per-machine: a clip belongs to the inventory that owns the weapon, and the game's own
/// reload path (`PlayerInventoryLocal.TriggerReload`/`DoReload`) and clip write (`BulletWeapon.SetCurrentClip`)
/// act on whichever machine runs them. A host that executed either for a client-held weapon would write a
/// different player's inventory, so both rows declare the `owner` execution tier: the host decides *when* (which
/// plan, which step, which inputs), the holder's own client performs the write, and the game's own replication
/// carries the result back.
///
/// The rows are registered here, not by <see cref="ModuleDefinition"/>: they belong to the holder provider
/// (see <see cref="WeaponHolderChannelContract"/>), which has its own registration and its own owner-session
/// resolver. The rows themselves — ports, parameters, result schema, the `owner` tier — are the generated block
/// table's (<see cref="Blocks"/>).
/// </summary>
internal static class WeaponHolderActionsContract
{
    /// <summary>The reload row, at the identity the website catalog already carries.</summary>
    public const string ReloadCapability = Blocks.ActionWeaponReload.Id;
    /// <summary>The clip write row. The catalog has no separate magazine row — `forge.action.inventory.stack_set`
    /// is the backpack stack and `forge.action.weapon.ammo_*` the reserve pools — so this is a new canonical id of
    /// the weapon domain, named after what it sets.</summary>
    public const string ClipSetCapability = Blocks.ActionWeaponClipSet.Id;
    /// <summary>The fire row. Its canonical id is this package's own and its tier is the same `owner` one the
    /// magazine rows carry, because the same machine owns both: the weapon fires where the inventory that holds
    /// it lives — the game's own `Fire` writes the clip, the recoil and the replicated shot count of that
    /// machine's weapon — so a host that fired a client's weapon would be firing the wrong copy.</summary>
    public const string AutoFireCapability = Blocks.ActionWeaponAutoFire.Id;

    public const string ReloadBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_reload";
    public const string ClipSetBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_clip_set";
    public const string AutoFireBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_auto_fire";
    public const string ReloadHandler = "gtfo.weapon.holder.reload";
    public const string ClipSetHandler = "gtfo.weapon.holder.clip_set";
    public const string AutoFireHandler = "gtfo.weapon.holder.auto_fire";

    /// <summary>The permission all three rows write under: they change the magazine of one equipment instance or
    /// spend one of its rounds.</summary>
    public const string MagazineWritePermission = "gtfo.equipment.magazine.write";

    /// <summary>The three `required_state` members, so a body that checks one and a plan that asks for it spell
    /// the value once. `none` fires whatever is in hand; `aiming` and `charging` are the game's own weapon states
    /// and never a re-derivation of them.</summary>
    public const string StateNone = "none";
    public const string StateAiming = "aiming";
    public const string StateCharging = "charging";

    /// <summary>What one owner-tier action asks the holder's machine to do. The kind is a structural value of the
    /// step, not a port: one capability is one action, and the channel body switches on this constant rather than
    /// parsing an id out of a plan.</summary>
    public enum HolderAction
    {
        Reload,
        ClipSet,
        AutoFire
    }

    /// <summary>The shape of each handler, resolved at registration against its row. Every row reads the
    /// equipment it addresses and the plan's own request fields; the clip row
    /// additionally reads the amount it was asked for, and the fire row reads nothing else because the state it
    /// requires is a structural parameter the native check reads out of the weapon itself.</summary>
    public static readonly HandlerShape ReloadShape = new HandlerShape()
        .Inputs("equipment", "speed_multiplier", "cap").Outputs("result");
    public static readonly HandlerSlot ReloadEquipmentSlot = ReloadShape.InputSlot("equipment");
    public static readonly HandlerSlot ReloadSpeedMultiplierSlot = ReloadShape.InputSlot("speed_multiplier");
    public static readonly HandlerSlot ReloadCapSlot = ReloadShape.InputSlot("cap");

    public static readonly HandlerShape ClipSetShape = new HandlerShape()
        .Inputs("equipment", "amount").Outputs("result")
        .Parameters("clip_policy", "overflow_policy", "source_pool");
    public static readonly HandlerSlot ClipSetEquipmentSlot = ClipSetShape.InputSlot("equipment");
    public static readonly HandlerSlot ClipSetAmountSlot = ClipSetShape.InputSlot("amount");
    public static readonly HandlerSlot ClipSetPolicySlot = ClipSetShape.ParameterSlot("clip_policy");
    public static readonly HandlerSlot ClipSetOverflowPolicySlot = ClipSetShape.ParameterSlot("overflow_policy");
    public static readonly HandlerSlot ClipSetSourcePoolSlot = ClipSetShape.ParameterSlot("source_pool");

    public static readonly HandlerShape AutoFireShape = new HandlerShape()
        .Inputs("equipment").Outputs("result").Parameters("required_state");
    public static readonly HandlerSlot AutoFireEquipmentSlot = AutoFireShape.InputSlot("equipment");
    public static readonly HandlerSlot AutoFireRequiredStateSlot = AutoFireShape.ParameterSlot("required_state");

    public static ProviderDeclaration Describe() =>
        new ProviderDeclaration(WeaponHolderChannelContract.ProviderId, EnginePackage.Version, EnginePackage.Name)
            .DeclareBlocks().Bind(
                new ProviderBinding(ReloadBinding, ReloadCapability, ReloadHandler, "execute", new[] { MagazineWritePermission }, ReloadShape),
                new ProviderBinding(ClipSetBinding, ClipSetCapability, ClipSetHandler, "execute", new[] { MagazineWritePermission }, ClipSetShape),
                new ProviderBinding(AutoFireBinding, AutoFireCapability, AutoFireHandler, "execute", new[] { MagazineWritePermission }, AutoFireShape));

    /// <summary>Which row one capability id is, or null when this contract does not carry it. The channel's own
    /// dispatcher uses this instead of comparing ids at each call site.</summary>
    public static HolderAction? KindOf(string capabilityId)
    {
        if (string.Equals(capabilityId, ReloadCapability, StringComparison.Ordinal)) return HolderAction.Reload;
        if (string.Equals(capabilityId, ClipSetCapability, StringComparison.Ordinal)) return HolderAction.ClipSet;
        if (string.Equals(capabilityId, AutoFireCapability, StringComparison.Ordinal)) return HolderAction.AutoFire;
        return null;
    }

    /// <summary>
    /// The holder provider's whole registration, built here rather than in the native half so the focused test
    /// project can register exactly this module and exercise its two rows without a game.
    ///
    /// <paramref name="holders"/> is the owner-session resolver the host's `owner` dispatch asks for the session
    /// that holds the equipment a step names; the native half supplies the one that reads it out of the game, and
    /// a test supplies its own. The two handler bodies are the only part that touches the game, so they are handed
    /// in as well; a registration built without them declares both rows and answers neither, which is what the
    /// declaration-only shape is for. <paramref name="logLevel"/> is passed straight to the kernel by the caller.
    /// </summary>
    public static RuntimeModule Module(Func<EntityReference, string?>? holders, CommandHandler? reload = null,
        CommandHandler? clipSet = null, CommandHandler? autoFire = null)
    {
        var sessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (holders != null)
            foreach (var capability in WeaponHolderChannelContract.Capabilities) sessions[capability] = holders;
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (reload != null) handlers[ReloadHandler] = reload;
        if (clipSet != null) handlers[ClipSetHandler] = clipSet;
        if (autoFire != null) handlers[AutoFireHandler] = autoFire;
        return Describe().Module(RuntimeApi.Fingerprint, handlers) with { OwnerSessions = sessions };
    }
}
