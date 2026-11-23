using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The player's own input events: using a supply and the local ping, plus the posture, held-input and
/// key rows below. Each is a thing the game reports about the player who did it, which is why they are one family —
/// each is one game event with a player and a small vocabulary, not a state of an item or of a life.
///
/// Two rules from the framework are what shape these rows:
/// <list type="bullet">
/// <item>The game announces a supply use on the machine that performed it. The host is the only
/// publisher, so the row declares `execution: host` like every other trigger and the observation half publishes
/// only where it is authoritative; a client's local event is not a second publisher of a host fact. What the host
/// genuinely lacks (a client's own input) is recorded in `evidence/player-event-facts.json` and in the report as
/// the one open channel, rather than being faked with a derived host fact.</item>
/// <item>`supply_kind` is an `enum` port whose members are the vocabulary below, spelled
/// in the order the framework's shared enum-set table declares: an enum port's wire value is the member's index
/// and never its name, which is the same rule the player-state family's `damage_kind` publication follows. The
/// member name is what the provider validates and what the journal carries — a kind outside the list is refused
/// by name, never passed through — and the index is what the payload writes. Every other port is a member of a
/// closed vocabulary read from the game's own event, so no port is invented.</item>
/// </list>
///
/// The supply and ping rows are trigger-contract rows (the text below is what the integration batch moves into
/// `TriggerContracts`); this provider binds them to its own observation. `e-p-equip` needs nothing here: the
/// canonical `forge.trigger.input.equipped` / `unequipped` rows already exist and are already bound, so the node
/// list row lands on them unchanged.</summary>
internal static class PlayerEventContract
{
    public const string ProviderId = "forge.module.gtfo.player";




    /// <summary>The local player pressing or releasing one key: `forge.trigger.input.key`, this provider's own
    /// row and in this family because it is a player's own input event. Its `key`
    /// parameter is a structural member of the framework's `key_code` set, so the loaded plans decide which keys
    /// the observation half reads; the `key` output carries that member's index, which is what the kernel filters
    /// an entry mounted for one key on, and the `phase` output carries `key_phase`'s member index.</summary>
    public const string KeyCapability = Blocks.TriggerInputKey.Id;


    /// <summary>The structural parameter whose members are the keys the loaded plans subscribe to.</summary>
    public const string KeyParameter = "key";

    /// <summary>The framework enum set the `key` parameter narrows, and the one the observation half maps each
    /// member back to a native `UnityEngine.KeyCode` through.</summary>
    public const string KeySet = "key_code";

    /// <summary>The two members of `key_phase`, in the declaration order that is the compiled value of the
    /// `phase` output, and their member indices resolved once here so the poll path never looks a name up.</summary>
    public const string PressedPhase = "pressed";
    public const string ReleasedPhase = "released";
    public static readonly int PressedPhaseIndex = PhaseIndex(PressedPhase);
    public static readonly int ReleasedPhaseIndex = PhaseIndex(ReleasedPhase);

    /// <summary>Every observed row this family binds, in registration order.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("supply_used", Blocks.TriggerPlayerSupplyUsed.Id),
        ("ping", Blocks.TriggerInputPing.Id),
        ("movement_state_changed", Blocks.TriggerPlayerMovementStateChanged.Id),
        ("input_held", Blocks.TriggerInputHeld.Id),
        ("key_pressed", KeyCapability),
        ("fire_pressed", Blocks.TriggerInputFire.Id)
    });

    /// <summary>The `supply_used.supply_kind` vocabulary, in the declaration order of the framework's
    /// `supply_kind` enum set: the game posts one event per supply it knows, these four are the supplies a player
    /// can apply, and a payload carries a member's position in this list. The member name is what the journal and
    /// the evidence carry.</summary>
    public static readonly IReadOnlyList<string> SupplyKinds = Array.AsReadOnly(new[]
    {
        "medikit", "ammokit", "disinfection", "tool_refill"
    });

    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-event-fact", "Unknown player-event fact: " + fact);
    }

    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    internal static string HandlerOf(string fact) => "gtfo.player." + fact;

    /// <summary>One fact's kind word as the port carries it: the caller passes the game's own event member and
    /// gets the catalog's vocabulary member, or a refusal by name when the event is not one of this family's. A
    /// member outside the list is a programming error, never a silent pass-through of a game enum name.</summary>
    public static string SupplyKind(string member) => Member(SupplyKinds, member, "supply_kind");

    private static string Member(IReadOnlyList<string> vocabulary, string member, string set)
    {
        foreach (var candidate in vocabulary) if (string.Equals(candidate, member, StringComparison.Ordinal)) return candidate;
        throw new RuntimeContractException("player-event-kind", "Not a " + set + " member: " + member);
    }

    // ---------------------------------------------------------------- payloads

    /// <summary>The position one member of a kind list holds, which is the value its enum port carries. A member
    /// outside the list is a programming error here and not a native condition: the caller resolved the name
    /// through <see cref="SupplyKind"/> first.</summary>
    private static int Position(IReadOnlyList<string> members, string kind, string set)
    {
        for (var position = 0; position < members.Count; position++)
            if (string.Equals(members[position], kind, StringComparison.Ordinal)) return position;
        throw new RuntimeContractException("player-event-kind", "Not a " + set + " member: " + kind);
    }

    /// <summary>`supply_used`: the player who applied a supply and which supply it was, on the shipped row's own
    /// `supply_kind` port. The port carries the member's index in the set the framework declares and never its
    /// name, which is why the list above is spelled in that set's order.</summary>
    public static void WriteSupplyUsed(EvaluationOutputs values, EntityReference player, string kind)
    {
        var member = Position(SupplyKinds, kind, "supply_kind");
        values.Entity(Blocks.TriggerPlayerSupplyUsed.OutPlayer, player);
        values.Enum(Blocks.TriggerPlayerSupplyUsed.OutSupplyKind, member);
    }

    /// <summary>`ping`: the player who pinged and where, in metres.</summary>
    public static void WritePing(EvaluationOutputs values, EntityReference player, double[] position)
    {
        values.Entity(Blocks.TriggerInputPing.OutPlayer, player);
        values.Vector(Blocks.TriggerInputPing.OutPosition, position[0], position[1], position[2]);
    }

    /// <summary>`movement_state_changed`: the player whose locomotion machine changed state, the member it is in
    /// now and the member it left. Both enum ports carry the member's index in the framework's own
    /// `player_movement_state` set, which is the order <see cref="RuntimeEnumSets"/> spells, and that order is the
    /// native `PLOC_State` enum's own - so an index here is the native value and no mapping table stands between
    /// them. A native value outside the declared members is a programming error here: the
    /// publisher refuses it before building a payload, because reporting it as a neighbouring posture would be an
    /// invented state.</summary>
    public static void WriteMovementStateChanged(EvaluationOutputs values, EntityReference player, int state, int previous)
    {
        Member(state); Member(previous);
        values.Entity(Blocks.TriggerPlayerMovementStateChanged.OutPlayer, player);
        values.Enum(Blocks.TriggerPlayerMovementStateChanged.OutState, state);
        values.Enum(Blocks.TriggerPlayerMovementStateChanged.OutPrevious, previous);
    }

    /// <summary>One `player_movement_state` index, or a refusal by name. The members are the framework's own set
    /// - the one place that enum is declared for every package - and not a copy kept here, so an index this
    /// payload accepts is exactly an index the kernel's own port validation accepts. The set is closed, so an
    /// index nothing names is not published under the nearest one.</summary>
    private static int Member(int index)
        => index >= 0 && index < RuntimeEnumSets.Members(MovementStateSet).Count ? index
            : throw new RuntimeContractException("player-movement-state", "Not a player_movement_state member: " + index);

    /// <summary>The framework enum set both posture ports carry.</summary>
    private const string MovementStateSet = "player_movement_state";

    /// <summary>`input_held`: the player who held a timed interaction long enough, and how long that interaction
    /// runs for, in ticks. The row's `action` parameter is fixed to `interact` - the only member of the
    /// `equipment_action` set with a "held long enough" reading in this game.</summary>
    public static void WriteInputHeld(EvaluationOutputs values, EntityReference actor, long elapsedTicks)
    {
        values.Entity(Blocks.TriggerInputHeld.OutActor, actor);
        values.Integer(Blocks.TriggerInputHeld.OutElapsed, elapsedTicks);
    }

    /// <summary>`key_pressed`: the local player who pressed or released a key, which key it was, and which edge
    /// it was, on the shipped row's own `key` and `phase` ports. Both enum ports carry the member's index in the
    /// framework's own set and never its name, so the observation half resolves each member once — through
    /// <see cref="KeyIndex"/> and <see cref="PhaseIndex"/> — and the payload writes the number. `key` is also the
    /// trigger's structural parameter: an entry mounted for one key is dispatched only when this port carries the
    /// same member, which is what keeps two plans on the same binding apart. A key or phase outside the declared
    /// set is a programming error here, not a native condition, and is refused rather than published as the
    /// neighbouring member.</summary>
    public static void WriteFirePressed(EvaluationOutputs values, EntityReference player)
        => values.Entity(Blocks.TriggerInputFire.OutPlayer, player);

    public static void WriteKeyPressed(EvaluationOutputs values, EntityReference player, int key, int phase)
    {
        if (key < 0 || key >= RuntimeEnumSets.Members(KeySet).Count)
            throw new RuntimeContractException("player-key-code", "Not a key_code member: " + key);
        if (phase < 0 || phase >= RuntimeEnumSets.Members(KeyPhaseSet).Count)
            throw new RuntimeContractException("player-key-phase", "Not a key_phase member: " + phase);
        values.Entity(Blocks.TriggerInputKey.OutPlayer, player);
        values.Enum(Blocks.TriggerInputKey.OutKey, key);
        values.Enum(Blocks.TriggerInputKey.OutPhase, phase);
    }

    /// <summary>The `key_code` member index one kebab-cased member name carries, or a refusal by name. The members
    /// are the framework's own set — the one place `key_code` is declared — so an index this returns is exactly an
    /// index the kernel's own port validation accepts.</summary>
    public static int KeyIndex(string member)
    {
        var members = RuntimeEnumSets.Members(KeySet);
        for (var position = 0; position < members.Count; position++)
            if (string.Equals(members[position], member, StringComparison.Ordinal)) return position;
        throw new RuntimeContractException("player-key-code", "Not a key_code member: " + member);
    }

    /// <summary>The `key_phase` member index one of the two edge names carries, or a refusal by name. The members
    /// are the framework's own set — the one place that pair is declared — so an index this payload accepts is
    /// exactly an index the kernel's own port validation accepts.</summary>
    private static int PhaseIndex(string member)
    {
        var members = RuntimeEnumSets.Members(KeyPhaseSet);
        for (var position = 0; position < members.Count; position++)
            if (string.Equals(members[position], member, StringComparison.Ordinal)) return position;
        throw new RuntimeContractException("player-key-phase", "Not a key_phase member: " + member);
    }

    /// <summary>The framework enum set the `phase` output carries.</summary>
    private const string KeyPhaseSet = "key_phase";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.player.supply_used", Blocks.TriggerPlayerSupplyUsed.Id, "gtfo.player.supply_used", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.input.ping", Blocks.TriggerInputPing.Id, "gtfo.player.ping", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.player.movement_state_changed", Blocks.TriggerPlayerMovementStateChanged.Id, "gtfo.player.movement_state_changed", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.input.held", Blocks.TriggerInputHeld.Id, "gtfo.player.input_held", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.input.fire", Blocks.TriggerInputFire.Id, "gtfo.player.fire_pressed", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.trigger.input.key", Blocks.TriggerInputKey.Id, "gtfo.player.key_pressed", "observe", Array.Empty<string>())
    };
}
