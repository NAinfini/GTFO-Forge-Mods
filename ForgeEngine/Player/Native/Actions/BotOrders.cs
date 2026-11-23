using System;
using System.Collections.Generic;
using Agents;
using ForgeEngine.Framework;
using Gear;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>The `forge.action.bot.order` command handler, on build 20403457: one native action request per named
/// bot, submitted through the game's own command entry `PlayerAIBot.RequestAction(PlayerBotActionBase.Descriptor)`.
/// The bot's own `IsActionAllowed` is asked first, so a request the game's rules forbid is reported as
/// `action-forbidden-by-native-rules` rather than submitted and dropped; a request that returns false is the same
/// refusal, because the entry point's own answer is what says the action was taken into the queue.
///
/// What the row's inputs can name, and what this half resolves them to:
/// <list type="bullet">
/// <item>`bots` — the recipient collection, resolved through this package's own player table; a reference that is
/// not a live player agent of this world is `stale-entity`, and an agent without the game's own
/// `PlayerAIBot` component is `bot-not-a-bot`, which is the row's own definition of a bot.</item>
/// <item>`anchor` — the entity an order is about: the player a bot follows or revives, the agent it attacks or
/// strikes, the object it uses a firearm at or unlocks. A player anchor is resolved through this package's own
/// table; every other kind goes through the kernel's published instance door
/// (`CommandContext.EntityInstance`), which answers only for a kind whose owning package registered its own
/// instance. An anchor that cannot be resolved to what the order needs is `anchor-missing`, never a request aimed
/// at whatever else the reference happened to name.</item>
/// <item>`position` — the destination a `travel` names. Without one, the anchor's own position is the destination
/// and the anchor is therefore required.</item>
/// </list>
///
/// The descriptor each order builds carries only what the order's own inputs name. The order's own enums are the
/// exception, because a descriptor built by hand has to say what the request means: `attack` asks for every means
/// the game declares while engaging, `use_firearm` asks the bot to open fire on a target it can see, `melee` asks
/// for a strike, and `unlock` asks for whatever method fits the lock. The optional `priority` parameter, when set,
/// is written to the descriptor's own `Prio` before the game's gate weighs it. Everything else stays at the value
/// the game's own constructor gave it, so this row invents no tuning of its own.
///
/// A submitted request is a committed one: the native entry point took it into the bot's action queue, and this row
/// does not claim the bot carried it out — what the bot then does is its own AI's business, which is exactly what
/// the row's own irreducibility says.</summary>
internal static class BotOrders
{

    /// <summary>The handler reads the offsets resolved from <see cref="BotOrderContract.OrderShape"/> at
    /// registration, plus the host flag and the kernel's entity-instance door.</summary>
    internal static CommandResult Order(PlayerIdentityModule? players, CommandContext context)
    {
        // The row is host-executed: a replica that somehow reached this body refuses by name rather than driving
        // another machine's bots.
        if (!context.IsHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (players == null || !players.CanCommit)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var order = context.Text(BotOrderContract.OrderSlot);
        if (order == null || Array.IndexOf(BotOrderContract.Orders, order) < 0)
            return Outcome.Rejected(ResultCode.OrderUnsupported);
        var command = new OrderCommand
        {
            Players = players,
            Order = order,
            Context = context,
            Position = context.Vector(BotOrderContract.PositionSlot),
            Anchor = context.TryEntity(BotOrderContract.AnchorSlot, out var reference) ? reference : null,
            Priority = context.Integer(BotOrderContract.PrioritySlot)
        };
        // Any reference that is not a player's is answered `bot-not-a-bot` by the step itself, so the skeleton's
        // kind check is not asked for; a refused request writes nothing, so no commit is ever unknown here.
        return context.EachTarget(ref command, OrderStep, null, CommandResult.MaximumFacts, ResultCode.ActionForbiddenByNativeRules,
            countColumn: Blocks.ActionBotOrder.RowTargetCount);
    }

    private struct OrderCommand
    {
        internal PlayerIdentityModule Players;
        internal string Order;
        internal EntityReference? Anchor;
        internal (double X, double Y, double Z)? Position;
        internal long? Priority;
        internal CommandContext Context;
    }

    private static readonly TargetStep<OrderCommand> OrderStep = OrderTarget;

    private static void OrderTarget(ref OrderCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = OrderOne(command.Players, target, command.Order, command.Anchor, command.Position, command.Priority, command.Context);
        rows.End(row.Status, row.Commit, row.Code);
    }

    /// <summary>One recipient's request. Every step before the native call is a read the caller could re-check,
    /// and the bot's own component is what decides whether this reference is a bot at all.</summary>
    private static Outcome OrderOne(PlayerIdentityModule players, EntityReference target, string order,
        EntityReference? anchor, (double X, double Y, double Z)? position, long? priority, CommandContext context)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.BotNotABot);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        bool alive;
        try { alive = agent.Alive; }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        if (!alive) return Outcome.Rejected(ResultCode.NotAlive);
        PlayerAIBot? bot;
        try { bot = agent.GetComponent<PlayerAIBot>(); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        if (bot == null) return Outcome.Rejected(ResultCode.BotNotABot);
        if (!TryDescriptor(bot, order, anchor, position, players, context, out var descriptor, out var code))
            return Outcome.Rejected(code);
        try
        {
            // The one tuning the row names: an unset priority keeps the constructor's own value.
            if (priority is { } prio) descriptor!.Prio = prio;
            // A bot that already follows this very player holds the state the request asks for: the game's own
            // forbidden registry refuses a second follow of the same client, so the request is answered by name
            // instead of being submitted into that refusal.
            if (FollowsAlready(bot, descriptor!)) return AlreadyActive;
            // The game's own gate first: a request its rules forbid is refused by name instead of submitted and
            // dropped by the action queue.
            if (!bot.IsActionAllowed(descriptor!)) return Forbidden(order, bot, descriptor!, "gate");
            return bot.RequestAction(descriptor!) ? Committed : Forbidden(order, bot, descriptor!, "entry");
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ActionForbiddenByNativeRules, error); return Outcome.Rejected(ResultCode.ActionForbiddenByNativeRules); }
    }

    /// <summary>True when the request is a follow of a player and the bot's running actions already hold a follow of
    /// that same player in the Active status. A queued, finished or aborted follow does not count: only an Active one
    /// is the state the order asks for.</summary>
    private static bool FollowsAlready(PlayerAIBot bot, PlayerBotActionBase.Descriptor requested)
    {
        var follow = requested.TryCast<PlayerBotActionFollow.Descriptor>();
        if (follow?.Client == null) return false;
        var actions = bot.Actions;
        for (var index = 0; actions != null && index < actions.Count; index++)
        {
            var running = actions[index]?.DescBase?.TryCast<PlayerBotActionFollow.Descriptor>();
            if (running is { Status: PlayerBotActionBase.Descriptor.StatusType.Active, Client: { } client }
                && client.Pointer == follow.Client.Pointer) return true;
        }
        return false;
    }

    /// <summary>The state the order asks for already holds in the bot's native action list, so nothing is submitted;
    /// the row reports it as confirmed under its own code.</summary>
    private static Outcome AlreadyActive => Outcome.Succeeded(ResultCode.AlreadyActive);

    /// <summary>The refusal the game's own rules gave, with what they were weighed against. The row's code says only
    /// that the native gate said no; the game's reasons (a layer or priority clash with a running action, or its
    /// registry of recently failed actions) are invisible from the result, so the bot's queue and the request's
    /// priority, layers and forbidden-registry answer go to the Player error log.</summary>
    private static Outcome Forbidden(string order, PlayerAIBot bot, PlayerBotActionBase.Descriptor requested, string stage)
    {
        PlayerCaughtFailures.Note("bot-order-forbidden", "stage=" + stage + " " + RefusalDetail(order, bot, requested));
        return Outcome.Rejected(ResultCode.ActionForbiddenByNativeRules);
    }

    /// <summary>`order=… requested=kind(prio,layers,status) forbidden=… running=[…] queued=[…]`. A queue that cannot
    /// be read says so instead of hiding the refusal.</summary>
    internal static string RefusalDetail(string order, PlayerAIBot bot, PlayerBotActionBase.Descriptor requested)
    {
        try
        {
            var forbidden = bot.IsActionForbidden(requested);
            var running = new List<string>();
            var actions = bot.Actions;
            for (var index = 0; actions != null && index < actions.Count && index < 16; index++)
                running.Add(actions[index]?.DescBase is { } desc ? Entry(desc) : "null");
            var queued = new List<string>();
            var waiting = bot.m_queuedActions;
            for (var index = 0; waiting != null && index < waiting.Count && index < 16; index++)
                queued.Add(waiting[index] is { } desc ? Entry(desc) : "null");
            return "order=" + order + " requested=" + Entry(requested) + " forbidden=" + forbidden
                + " running=[" + string.Join(",", running) + "] queued=[" + string.Join(",", queued) + "]";
        }
        catch (Exception error) { return "order=" + order + " detail-unreadable=" + error.GetType().Name; }
    }

    private static string Entry(PlayerBotActionBase.Descriptor descriptor)
        => Kind(descriptor) + "(prio=" + descriptor.Prio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ",layers=" + descriptor.RequiredLayers + ",status=" + descriptor.Status + ")";

    private static string Kind(PlayerBotActionBase.Descriptor descriptor)
        => descriptor.TryCast<PlayerBotActionFollow.Descriptor>() != null ? "follow"
            : descriptor.TryCast<PlayerBotActionTravel.Descriptor>() != null ? "travel"
            : descriptor.TryCast<PlayerBotActionAttack.Descriptor>() != null ? "attack"
            : descriptor.TryCast<PlayerBotActionUseFirearm.Descriptor>() != null ? "use_firearm"
            : descriptor.TryCast<PlayerBotActionMelee.Descriptor>() != null ? "melee"
            : descriptor.TryCast<PlayerBotActionRevive.Descriptor>() != null ? "revive"
            : descriptor.TryCast<PlayerBotActionUnlock.Descriptor>() != null ? "unlock" : "other";

    /// <summary>
    /// The one native descriptor an order is about, or the code that says why this half cannot build it. Only the
    /// fields the order's own inputs name are written; the constructor's own values carry everything else.
    /// </summary>
    private static bool TryDescriptor(PlayerAIBot bot, string order, EntityReference? anchor, (double X, double Y, double Z)? position,
        PlayerIdentityModule players, CommandContext context, out PlayerBotActionBase.Descriptor? descriptor, out string code)
    {
        descriptor = null; code = ResultCode.AnchorMissing;
        switch (order)
        {
            case "follow":
                {
                    // The bot follows a player: the anchor is that player's own agent, and nothing else can be a
                    // follow target.
                    if (!TryPlayerAgent(anchor, players, out var client)) return false;
                    descriptor = new PlayerBotActionFollow.Descriptor(bot) { Client = client };
                    return true;
                }
            case "travel":
                {
                    // A destination is either the position the request carries or the anchor's own position; a request
                    // that names neither has nothing to travel to.
                    var travel = new PlayerBotActionTravel.Descriptor(bot)
                    { DestinationType = PlayerBotActionTravel.Descriptor.DestinationEnum.Position };
                    if (position is { } destination)
                        travel.DestinationPos = new Vector3((float)destination.X, (float)destination.Y, (float)destination.Z);
                    else if (TryAgent(anchor, players, context, out var at) && TryPosition(at!, out var where)) travel.DestinationPos = where;
                    else return false;
                    descriptor = travel;
                    return true;
                }
            case "attack":
                {
                    if (!TryAgent(anchor, players, context, out var target)) return false;
                    descriptor = new PlayerBotActionAttack.Descriptor(bot)
                    {
                        TargetAgent = target,
                        // The request says "attack this target": every means the game declares, engaging it.
                        Means = PlayerBotActionAttack.AttackMeansEnum.All,
                        Stance = PlayerBotActionAttack.StanceEnum.Engage
                    };
                    return true;
                }
            case "use_firearm":
                {
                    if (!TryAgent(anchor, players, context, out var target)) return false;
                    // The weapon is the bot's own: the request asks it to use a firearm, not to be handed one, and a
                    // bot whose wielded item is not a bullet weapon cannot carry the request out.
                    var weapon = bot.Inventory?.WieldedItem?.TryCast<BulletWeaponSynced>();
                    if (weapon == null) { code = ResultCode.ActionForbiddenByNativeRules; return false; }
                    var transform = target!.transform;
                    if (transform == null) return false;
                    descriptor = new PlayerBotActionUseFirearm.Descriptor(bot)
                    { TargetObj = transform, Weapon = weapon, OpenFire = true, requireVisibility = true };
                    return true;
                }
            case "melee":
                {
                    if (!TryAgent(anchor, players, context, out var target)) return false;
                    var gameObject = target!.gameObject;
                    descriptor = new PlayerBotActionMelee.Descriptor(bot)
                    {
                        TargetAgent = target,
                        TargetGameObject = gameObject,
                        // The request is a strike at the named target, not a swing in the air.
                        Strike = true
                    };
                    return true;
                }
            case "revive":
                {
                    if (!TryPlayerAgent(anchor, players, out var client)) return false;
                    descriptor = new PlayerBotActionRevive.Descriptor(bot) { Client = client };
                    return true;
                }
            default:
                {
                    // Unlock: the anchor is the object to open, and the lock is the one the game's own component tree
                    // carries for it. A target without a weak lock has nothing this order can act on.
                    if (anchor is not { } reference || context.EntityInstance(reference) is not { } native) return false;
                    var gameObject = (native as Component)?.gameObject;
                    var lockInstance = gameObject?.GetComponentInChildren<LG_WeakLock>();
                    if (gameObject == null || lockInstance == null) return false;
                    // The descriptor names what the lock is on: the game's `TargetTypeEnum` has Door and Container,
                    // and a weak-locked resource container is the second.
                    var onContainer = lockInstance.GetComponentInParent<LG_WeakResourceContainer>() != null;
                    descriptor = new PlayerBotActionUnlock.Descriptor(bot)
                    {
                        TargetType = onContainer ? PlayerBotActionUnlock.Descriptor.TargetTypeEnum.Container
                            : PlayerBotActionUnlock.Descriptor.TargetTypeEnum.Door,
                        // Whatever method fits the lock is the game's own decision, which is the descriptor's own
                        // `Evaluate` entry's answer for the same lock.
                        Method = PlayerBotActionUnlock.Descriptor.MethodEnum.Any,
                        Lock = lockInstance,
                        TargetGO = gameObject
                    };
                    return true;
                }
        }
    }

    /// <summary>The agent an anchor names, when this half can resolve one: a player through this package's own
    /// table, every other kind through the kernel's instance door. A kind whose owner publishes no instance, and
    /// an object that is not an agent, both answer false — which is what keeps a request from being aimed at an
    /// object the order cannot name.</summary>
    private static bool TryAgent(EntityReference? anchor, PlayerIdentityModule players, CommandContext context, out Agent? agent)
    {
        agent = null;
        if (anchor is not { } reference) return false;
        if (players.CurrentAgent(reference) is { } player) { agent = player; return true; }
        try { agent = context.EntityInstance(reference) as Agent; }
        catch (Exception error) { PlayerCaughtFailures.Capture("bot-anchor-instance", error); agent = null; }
        return agent != null;
    }

    /// <summary>The player agent an anchor names, for the two orders whose subject is a person.</summary>
    private static bool TryPlayerAgent(EntityReference? anchor, PlayerIdentityModule players, out PlayerAgent? client)
    {
        client = anchor is { } reference ? players.CurrentAgent(reference) : null;
        return client != null;
    }

    private static bool TryPosition(Agent agent, out Vector3 position)
    {
        position = default;
        try
        {
            position = agent.Position;
            return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("bot-position-read", error); return false; }
    }

    private static Outcome Committed => Outcome.Succeeded(ResultCode.OrderRequested);
}
