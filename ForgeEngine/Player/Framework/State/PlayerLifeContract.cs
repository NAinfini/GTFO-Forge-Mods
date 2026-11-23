using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The player-life trigger bindings this provider implements: the six catalog rows that describe what
/// happens to one player's life — downed, revive started, revive cancelled, revived, died and teleported — plus
/// `respawned`, whose fact is published by the replication spawn path rather than by a life transition. Every
/// one of them is an observation of native state, so each is registered with role `observe`, declares no
/// handler and no result row, and publishes the fact instead of acting on it.
///
/// The capability ids, their output ports and their `host` execution tier belong to the runtime's
/// `TriggerContracts` provider, which declares the catalog rows field for field; this provider declares only
/// the rows a plan binds to, in <see cref="Bindings"/>, and the one fact each of them carries. The row for a
/// capability this file binds is never restated here, so the shape and the implementation cannot drift into
/// two descriptions of one trigger.
///
/// The player entity kind these facts carry is the one identity the Player provider already owns (`gtfo.player`,
/// declared and resolved by `Native/PlayerIdentityModule`); nothing here mints an entity of its own.</summary>
internal static class PlayerLifeContract
{
    /// <summary>The Player package's provider id. It is written here rather than read from
    /// <see cref="ModuleDefinition"/> because this file is also compiled by the focused test project, which must
    /// not drag the whole provider declaration in to build one binding id. The identity table's own contract test
    /// asserts the two agree, so the literal cannot drift unnoticed.</summary>
    public const string ProviderId = "forge.module.gtfo.player";


    /// <summary>Every row this provider implements, in the order the registration declares them: the wire name
    /// of the fact and the capability it is the observation of. One fact has exactly one capability, and a
    /// native hook names the fact rather than an index into this list.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Facts = Array.AsReadOnly(new[]
    {
        ("downed", Blocks.TriggerPlayerDowned.Id),
        ("revive_started", Blocks.TriggerPlayerReviveStarted.Id),
        ("revive_cancelled", Blocks.TriggerPlayerReviveCancelled.Id),
        ("revived", Blocks.TriggerPlayerRevived.Id),
        ("died", Blocks.TriggerPlayerDied.Id),
        ("teleported", Blocks.TriggerPlayerTeleported.Id),
        ("respawned", Blocks.TriggerPlayerRespawned.Id)
    });

    /// <summary>The capability one fact is published through. A fact name outside this set is a programming
    /// error in this package, not a native condition, so it is refused by name instead of publishing nothing.</summary>
    internal static string CapabilityOf(string fact)
    {
        foreach (var (name, capability) in Facts) if (string.Equals(name, fact, StringComparison.Ordinal)) return capability;
        throw new RuntimeContractException("player-life-fact", "Unknown player-life fact: " + fact);
    }

    /// <summary>The binding a native life transition publishes through: the capability's own suffix under the
    /// Player provider, so either side names the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.trigger.".Length..];

    /// <summary>The binding of one fact, for the hooks and the module that publish it.</summary>
    internal static string BindingOf(string fact) => Binding(CapabilityOf(fact));

    /// <summary>The handler name that travels with a binding row. The observation is native and has no handler
    /// the runtime dispatches to, so the name is the row's own identity text: provider, domain and fact.</summary>
    internal static string HandlerOf(string fact) => "gtfo.player." + fact;

    // ------------------------------------------------------------------ payloads
    //
    // One builder per fact. Each writes exactly the ports the catalog declares for its row, in the catalog's
    // order, and nothing else: a port the native side could not read has no builder argument and is left out of
    // the object, while a nullable port the game genuinely answered with nobody is passed as null and is
    // written as JSON null. The distinction is the whole reason these are built here rather than at each hook:
    // "not observable" and "observably nobody" are different facts and a reader must be able to tell them apart.
    //
    // They live beside the row declarations because the ports and the payload are two halves of one shape, and
    // because the package's own tests can then read the payload the kernel would dispatch without driving a
    // whole plan.

    /// <summary>`downed`: the player. The game reports no downs source of its own — the attacker list is a record
    /// of everyone who ever hit the player, not a cause — so the row declares no source.</summary>
    public static void WriteDowned(EvaluationOutputs values, EntityReference player)
        => values.Entity(Blocks.TriggerPlayerDowned.OutPlayer, player);

    /// <summary>`revive_started`: the downed player and the rescuer who is reviving them. The rescuer port is
    /// required, so a rescue whose actor cannot be named has no event at all and is not published.</summary>
    public static void WriteReviveStarted(EvaluationOutputs values, EntityReference player, EntityReference rescuer)
    {
        values.Entity(Blocks.TriggerPlayerReviveStarted.OutPlayer, player);
        values.Entity(Blocks.TriggerPlayerReviveStarted.OutRescuer, rescuer);
    }

    /// <summary>`revive_cancelled`: the downed player and the nullable rescuer whose rescue ended. The game reports
    /// no reason of its own (the one native entry is the reviver leaving the interaction), so the row carries none.</summary>
    public static void WriteReviveCancelled(EvaluationOutputs values, EntityReference player, EntityReference? rescuer)
    {
        values.Entity(Blocks.TriggerPlayerReviveCancelled.OutPlayer, player);
        values.Entity(Blocks.TriggerPlayerReviveCancelled.OutRescuer, rescuer);
    }

    /// <summary>`revived`: the player who came back and the nullable actor of the revive. A revive can run with
    /// no interaction actor this process can name, so the rescuer is allowed to be nobody.</summary>
    public static void WriteRevived(EvaluationOutputs values, EntityReference player, EntityReference? rescuer)
    {
        values.Entity(Blocks.TriggerPlayerRevived.OutPlayer, player);
        values.Entity(Blocks.TriggerPlayerRevived.OutRescuer, rescuer);
    }

    /// <summary>`died`: the player who died. No native read names the kill that ended a life, so the row declares
    /// no source.</summary>
    public static void WriteDied(EvaluationOutputs values, EntityReference player)
        => values.Entity(Blocks.TriggerPlayerDied.OutPlayer, player);

    /// <summary>`teleported`: the player and both ends of the move, in metres. Both positions are required, so a
    /// move whose origin or destination could not be read is not published.</summary>
    public static void WriteTeleported(EvaluationOutputs values, EntityReference player, double[] from, double[] to)
    {
        values.Entity(Blocks.TriggerPlayerTeleported.OutPlayer, player);
        values.Vector(Blocks.TriggerPlayerTeleported.OutFrom, from[0], from[1], from[2]);
        values.Vector(Blocks.TriggerPlayerTeleported.OutTo, to[0], to[1], to[2]);
    }

    /// <summary>`respawned`: the player and the position the game spawned them at, in metres.</summary>
    public static void WriteRespawned(EvaluationOutputs values, EntityReference player, double[] position)
    {
        values.Entity(Blocks.TriggerPlayerRespawned.OutPlayer, player);
        values.Vector(Blocks.TriggerPlayerRespawned.OutPosition, position[0], position[1], position[2]);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.player.downed", Blocks.TriggerPlayerDowned.Id, "gtfo.player.downed", "observe", Array.Empty<string>()) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.player.binding.player.revive_started", Blocks.TriggerPlayerReviveStarted.Id, "gtfo.player.revive_started", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.player.revive_cancelled", Blocks.TriggerPlayerReviveCancelled.Id, "gtfo.player.revive_cancelled", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.player.revived", Blocks.TriggerPlayerRevived.Id, "gtfo.player.revived", "observe", Array.Empty<string>()) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.player.binding.player.died", Blocks.TriggerPlayerDied.Id, "gtfo.player.died", "observe", Array.Empty<string>()) { Waitable = true },
        new ProviderBinding("forge.module.gtfo.player.binding.player.teleported", Blocks.TriggerPlayerTeleported.Id, "gtfo.player.teleported", "observe", Array.Empty<string>()),
        new ProviderBinding("forge.module.gtfo.player.binding.player.respawned", Blocks.TriggerPlayerRespawned.Id, "gtfo.player.respawned", "observe", Array.Empty<string>()) { Waitable = true }
    };
}
