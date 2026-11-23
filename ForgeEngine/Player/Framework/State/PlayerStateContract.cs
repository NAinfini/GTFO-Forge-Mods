using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The player state this provider observes and the read-only value rows it answers: the "after the fact"
/// damage event, the low-health and infection events, and the values a plan reads out of one player's own state.
///
/// Three things about this family are deliberate:
/// <list type="bullet">
/// <item>The damage row is the canonical `forge.trigger.combat.damage_applied` — one capability, published by
/// whoever can observe an application — because a second "player damage" capability would be a second name for
/// one fact. This provider adds a binding for `gtfo.player`, never a capability: the runtime's trigger contract
/// owns that row's shape, the enemy half already binds the same id for its own subject, and the catalog is the
/// authority for the ports. One port this provider publishes has no home in the catalog's row yet and is
/// requested by the integration fragment: `friendly_fire`, optional and appended, because the game reports a
/// friendly-fire event of its own and the player row is the only place it can be read.</item>
/// <item>`forge.trigger.player.low_health` and `forge.trigger.player.infection_changed` are declared rows of the
/// trigger contract (the text below is what the integration batch moves there) and observed by this provider. The
/// game has a low-health event of its own; the infection row carries the value it changed to and the delta, both
/// read from the receiver rather than derived from a threshold crossing.</item>
/// <item>The value rows are this provider's own capabilities, declared from the block table. Each one reads
/// native state on the host through the kernel's budgeted query session and writes nothing; the read layer is
/// `Native/PlayerValueReads.cs` and the shapes below are the ports it answers.</item>
/// </list>
///
/// The player entity kind is the one identity this package already owns (`gtfo.player`); nothing here mints an
/// entity of its own, and no port is filled with a guess. A port the native side could not read is left out of a
/// payload (the framework's "not observable"), while a nullable port the game genuinely answered with nobody is
/// written as JSON null.</summary>
internal static class PlayerStateContract
{
    /// <summary>The Player package's provider id. Written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id.</summary>
    public const string ProviderId = "forge.module.gtfo.player";

    // ---------------------------------------------------------------- capabilities this provider observes


    // ---------------------------------------------------------------- capabilities this provider owns


    /// <summary>The value handler names: the evaluator this provider registers for each read-only row. The name
    /// is also the shape key, so the registration cannot supply an evaluator whose ports were never declared.</summary>
    public const string InfectionValueHandler = "gtfo.player.value.infection";
    public const string WieldedGearValueHandler = "gtfo.player.value.wielded_gear";
    public const string CarriedItemValueHandler = "gtfo.player.value.carried_item";
    public const string ToolValueHandler = "gtfo.player.value.tool";

    /// <summary>Every observed row this family binds, in registration order: the wire name of the fact and the
    /// capability it is the observation of. One fact has exactly one capability.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("damage_applied", Blocks.TriggerCombatDamageApplied.Id),
        ("health_changed", Blocks.TriggerCombatHealthChanged.Id),
        ("low_health", Blocks.TriggerPlayerLowHealth.Id),
        ("infection_changed", Blocks.TriggerPlayerInfectionChanged.Id)
    });

    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-state-fact", "Unknown player-state fact: " + fact);
    }

    /// <summary>The binding a native transition publishes through: the capability's own suffix under the Map
    /// provider, so either side names the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    /// <summary>The handler name that travels with an observe binding. The observation is native and no handler is
    /// dispatched to, so the text is the row's own identity: provider, domain and fact.</summary>
    internal static string HandlerOf(string fact) => "gtfo.player." + fact;

    // ---------------------------------------------------------------- payloads

    /// <summary>`damage_applied` for a player: the canonical row's ports in its own order, plus the one optional
    /// port this provider asks to add. `source` is nullable because a damage application whose attacker this
    /// process cannot name is still an application; `equipment` is left out when no shot of that attacker is
    /// correlated with this application, which is the case for every damage that did not come from a weapon's own
    /// firing window; `damage_kind` is left out when the receive entry that carried the application is not one of
    /// the kinds this half names; `limb` is never carried by this half, because a player's damage entries hand it
    /// no limb index it reads.
    ///
    /// An enum port's wire value is the member's index in the declared set and never its name — the same rule the
    /// enemy package's own `damage_kind` publication follows — so the kind travels as an index and the member
    /// name is what the journal and the evidence carry.</summary>
    public static void WriteDamageApplied(EvaluationOutputs values, EntityReference target, EntityReference? source, double amount,
        int? damageKind, bool? friendlyFire, EntityReference? equipment = null, int? attackKind = null)
    {
        values.Entity(Blocks.TriggerCombatDamageApplied.OutSource, source);
        if (equipment != null) values.Entity(Blocks.TriggerCombatDamageApplied.OutEquipment, equipment);
        values.Entity(Blocks.TriggerCombatDamageApplied.OutTarget, target);
        values.Number(Blocks.TriggerCombatDamageApplied.OutAmount, amount);
        if (damageKind is { } kind) values.Enum(Blocks.TriggerCombatDamageApplied.OutDamageKind, kind);
        if (friendlyFire is { } friendly) values.Boolean(Blocks.TriggerCombatDamageApplied.OutFriendlyFire, friendly);
        if (attackKind is { } attack) values.Enum(Blocks.TriggerCombatDamageApplied.OutAttackKind, attack);
    }

    /// <summary>The `player_attack_kind` set's declaration order, which is what the `attack_kind` port carries: one
    /// member per player receive entry whose native body is its own. The no-air and parasite entries share one folded
    /// body on this build (evidence/player-hooks.json, `rvaShared`), so neither can be told apart and neither has a
    /// member; push and fall are not enemy attacks. An entry without a member publishes the port absent.</summary>
    public const int AttackKindTentacle = 0, AttackKindShooterProjectile = 1, AttackKindTankGrab = 2, AttackKindTrapGrab = 3,
        AttackKindBullet = 4, AttackKindMelee = 5, AttackKindExplosion = 6, AttackKindFire = 7, AttackKindSticky = 8;

    /// <summary>The member name one declared attack index spells, for the journal and the evidence.</summary>
    public static string AttackKindName(int index) => index switch
    {
        AttackKindTentacle => "tentacle",
        AttackKindShooterProjectile => "shooter_projectile",
        AttackKindTankGrab => "tank_grab",
        AttackKindTrapGrab => "trap_grab",
        AttackKindBullet => "bullet",
        AttackKindMelee => "melee",
        AttackKindExplosion => "explosion",
        AttackKindFire => "fire",
        AttackKindSticky => "sticky",
        _ => throw new RuntimeContractException("player-state-kind", "Not a named attack kind index: " + index)
    };

    /// <summary>`health_changed` for a player: the canonical row's ports. `value` and `max` are the receiver's own
    /// `Health` / `HealthMax` after the write, `delta` the signed change the write made and `ratio` their quotient.
    /// `source` is written for the two writes that carry one — a hit's attacker and a heal's healer — as the entity
    /// when a domain names it and as JSON null when none does; a set-health write carries no source and publishes the
    /// port absent.</summary>
    public static void WriteHealthChanged(EvaluationOutputs values, EntityReference target, double value, double delta, double max,
        bool sourceApplies, EntityReference? source)
    {
        values.Entity(Blocks.TriggerCombatHealthChanged.OutTarget, target);
        values.Number(Blocks.TriggerCombatHealthChanged.OutValue, value);
        values.Number(Blocks.TriggerCombatHealthChanged.OutDelta, delta);
        values.Number(Blocks.TriggerCombatHealthChanged.OutMax, max);
        values.Number(Blocks.TriggerCombatHealthChanged.OutRatio, value / max);
        if (sourceApplies) values.Entity(Blocks.TriggerCombatHealthChanged.OutSource, source);
    }

    /// <summary>The `damage_kind` set's declaration order, which is what an enum port carries. Only the members
    /// this half can name are pinned; a receive entry whose kind this half cannot name publishes no port.</summary>
    public const int DamageKindDirect = 0, DamageKindMelee = 1, DamageKindExplosion = 2, DamageKindDot = 3,
        DamageKindCollision = 5, DamageKindFall = 6, DamageKindTentacle = 9, DamageKindTankGrab = 10, DamageKindTrapGrab = 11, DamageKindProjectile = 12;

    /// <summary>The member name one declared index spells, for the journal and the evidence. An index outside this
    /// package's own list is a programming error here, not a native condition.</summary>
    public static string DamageKindName(int index) => index switch
    {
        DamageKindDirect => "direct",
        DamageKindMelee => "melee",
        DamageKindExplosion => "explosion",
        DamageKindDot => "dot",
        DamageKindCollision => "collision",
        DamageKindFall => "fall",
        DamageKindTentacle => "tentacle",
        DamageKindTankGrab => "tank_grab",
        DamageKindTrapGrab => "trap_grab",
        DamageKindProjectile => "projectile",
        _ => throw new RuntimeContractException("player-state-kind", "Not a named damage kind index: " + index)
    };

    /// <summary>`low_health`: the player the game's own low-health event named.</summary>
    public static void WriteLowHealth(EvaluationOutputs values, EntityReference player)
        => values.Entity(Blocks.TriggerPlayerLowHealth.OutPlayer, player);

    /// <summary>`infection_changed`: the player, the value the receiver holds now, the signed change and what the
    /// write came with. Both numbers are the receiver's own unit; the game publishes no unit of its own and this row
    /// claims none. `cause` is the write's own `pInfection.effect` as its `infection_cause` index.</summary>
    public static void WriteInfectionChanged(EvaluationOutputs values, EntityReference player, double value, double delta, int cause)
    {
        values.Entity(Blocks.TriggerPlayerInfectionChanged.OutPlayer, player);
        values.Number(Blocks.TriggerPlayerInfectionChanged.OutValue, value);
        values.Number(Blocks.TriggerPlayerInfectionChanged.OutDelta, delta);
        values.Enum(Blocks.TriggerPlayerInfectionChanged.OutCause, cause);
    }

    /// <summary>How many members the `infection_cause` set has: native `pInfectionEffect` (`None`,
    /// `DisinfectionPack`, `DisinfectionStation`) in declaration order, so a member's index is the native value and a
    /// value outside it is not one this row can name.</summary>
    public const int InfectionCauseCount = 3;

    // ---------------------------------------------------------------- value answers

    /// <summary>`forge.query.player.infection`: the receiver's infection value.</summary>
    public static void InfectionAnswer(double value, EvaluationOutputs outputs) => outputs.Number(0, value);

    /// <summary>`forge.query.player.wielded_gear`: the equipment entity of the item the player is holding, or
    /// null when the player holds no item this process can name.</summary>
    public static void WieldedGearAnswer(EntityReference? equipment, int slot, EvaluationOutputs outputs)
    {
        outputs.Entity(0, equipment);
        outputs.Integer(1, slot);
    }

    /// <summary>`forge.query.player.carried_item`: the expedition item this player carries, or null when nobody
    /// in the backpack holds one. A read that happened and found nothing is an answer, not a missing port.</summary>
    public static void CarriedItemAnswer(EntityReference? item, EvaluationOutputs outputs) => outputs.Entity(0, item);

    /// <summary>`forge.query.player.tool`: the held item the two ammunition numbers belong to, the class-ammunition
    /// pool (`GetClassAmmoInPackAbs` / `GetClassAmmoMaxCap` — the tool energy the game itself spends), and the
    /// backpack's own consumable stacks: `count` is how many pocket items it holds in total and `stacks` how many
    /// separate stacks they are grouped into. The item is nullable for the same reason `wielded_gear` is: a player
    /// holding nothing has no pool, which this row refuses rather than answers with a zero.</summary>
    public static void ToolAnswer(EntityReference? item, int ammo, int ammoMaximum, int count, int stacks, EvaluationOutputs outputs)
    {
        outputs.Entity(0, item);
        outputs.Integer(1, ammo);
        outputs.Integer(2, ammoMaximum);
        outputs.Integer(3, count);
        outputs.Integer(4, stacks);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareValueBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.query.player.infection", Blocks.QueryPlayerInfection.Id, InfectionValueHandler, "observe", Array.Empty<string>(), new HandlerShape().Inputs("player").Outputs("value")),
        new ProviderBinding("forge.module.gtfo.player.binding.query.player.wielded_gear", Blocks.QueryPlayerWieldedGear.Id, WieldedGearValueHandler, "observe", Array.Empty<string>(), new HandlerShape().Inputs("player").Outputs("equipment", "slot")),
        new ProviderBinding("forge.module.gtfo.player.binding.query.player.carried_item", Blocks.QueryPlayerCarriedItem.Id, CarriedItemValueHandler, "observe", Array.Empty<string>(), new HandlerShape().Inputs("player").Outputs("item")),
        new ProviderBinding("forge.module.gtfo.player.binding.query.player.tool", Blocks.QueryPlayerTool.Id, ToolValueHandler, "observe", Array.Empty<string>(), new HandlerShape().Inputs("player").Outputs("item", "ammo", "ammo_max", "count", "stacks"))
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareFactBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.combat.damage_applied", Blocks.TriggerCombatDamageApplied.Id, "gtfo.player.damage_applied", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.combat.health_changed", Blocks.TriggerCombatHealthChanged.Id, "gtfo.player.health_changed", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.player.low_health", Blocks.TriggerPlayerLowHealth.Id, "gtfo.player.low_health", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.player.infection_changed", Blocks.TriggerPlayerInfectionChanged.Id, "gtfo.player.infection_changed", "observe", Array.Empty<string>())
    };
}
