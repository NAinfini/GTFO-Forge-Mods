using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of `forge.action.inventory.recall_sentry`. The request is
/// `SentryGunInstance.m_sync.WantItemAction(owner, SyncedItemAction_New.PickUp)`, the replicated action a player's own
/// pickup ends in: the master validates it and every machine runs `SyncedPickup(owner)`, which clears the owner's
/// deployed tool slot, puts the sentry's remaining ammo back into the pack on the placer's machine (or on the master
/// for a bot) and despawns the instance (GameAssembly 0x141b2b0, build 20403457). Nothing here writes ammo or the slot.
///
/// `SyncedPickup` settles the agent the action names, so the agent is the sentry's own owner and never whoever the
/// plan happens to run for. Level sentries and sentries a mod stood off the tool slot have no placement in the adapter's
/// table, so they are not recalled. A tool slot marked deployed with no placement is refused by name: the sentry
/// exists and this request cannot reach it, which must not read as "nothing placed".
/// </summary>
internal sealed class SentryRecallActions : ISentryRecallSource, IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly IPlayerDirectory _players;
    private readonly Func<EntityReference, IReadOnlyList<SentryGunInstance>> _placedBy;
    private readonly Func<bool> _authoritative;
    private readonly Action<string> _report;
    private bool _disposed;

    internal SentryRecallActions(IForgeEngine kernel, IPlayerDirectory players,
        Func<EntityReference, IReadOnlyList<SentryGunInstance>> placedBy, Func<bool> authoritative, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _placedBy = placedBy ?? throw new ArgumentNullException(nameof(placedBy));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public DetonateOutcome Recall(EntityReference player, out string code)
    {
        code = "";
        if (_disposed || !_authoritative()) { code = ResultCode.AuthorityOrPhase; return DetonateOutcome.Rejected; }
        if (player == null || !EntityKinds.Is(player, EntityKinds.Player))
        { code = ResultCode.UnsupportedRecipient; return DetonateOutcome.Rejected; }
        if (!_kernel.IsEntityCurrent(player) || _players.AgentOf(player) is not { } agent)
        { code = ResultCode.StaleEntity; return DetonateOutcome.Rejected; }
        try
        {
            var sentries = _placedBy(player);
            PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack);
            if (sentries.Count == 0)
            {
                if (backpack != null && backpack.IsDeployed(InventorySlot.GearClass))
                { code = ResultCode.RecallSentryUnresolved; return DetonateOutcome.Rejected; }
                code = ResultCode.RecallSentryNonePlaced;
                return DetonateOutcome.Committed;
            }
            foreach (var sentry in sentries) sentry.m_sync.WantItemAction(agent, SyncedItemAction_New.PickUp);
            if (_placedBy(player).Count == 0 && (backpack == null || !backpack.IsDeployed(InventorySlot.GearClass)))
                return DetonateOutcome.Committed;
            code = ResultCode.RecallSentryUnconfirmed;
            return DetonateOutcome.Unknown;
        }
        catch (Exception error)
        {
            try { _report("weapon.recall-sentry-exception: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception failure) { NativeCallFailure.Note("SentryRecallActions.Report", failure); }
            code = ResultCode.NativeCommitException;
            return DetonateOutcome.Unknown;
        }
    }

    public void Dispose() => _disposed = true;
}
