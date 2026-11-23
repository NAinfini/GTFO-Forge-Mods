using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>The sourced attribute apply handler and the canonical `forge.action.effect.cancel` handler for its effect
/// handles: native modifications written per recipient through `AgentModifierManager.AddSyncedModifierValue`, then
/// released through the kernel's one handle-cancellation path.
/// The provider owns the `AgentModifier` table, so this adapter is the one place that writes it and the one place
/// that keeps the modification ids it must later release. One apply may name several members of that table (the
/// row's `modifiers` port group, e.g. a movement preset of speed and acceleration); every member it wrote is filed
/// under the one effect handle the kernel cast for the step, so the effect's end restores all of them.
///
/// What the native entry can and cannot say is why the shape is what it is. `AddSyncedModifierValue(Agent,
/// AgentModifier, float value, float deltaPerSec)` takes one signed contribution and a decay rate and returns the
/// modification id it registered; there is no setter for an absolute aggregate, no ordering and no rate channel
/// on this row's ports, so `add` and `subtract` land as one signed contribution (`subtract` submits the
/// negated amount) and the row offers no `set`. A returned id is checked in that agent's native instance before it confirms a contribution. A zero return means no
/// modification was registered, which is a refusal by name — never a retried or invented id.
///
/// The id, not the handle, is what the native clear takes, so the ledger is the provider's own: modification id →
/// the life it was written on, the attribute member, the submission and the effect handle it is filed under. The handle a
/// command's modifications are filed under is the kernel's own effect handle — the step's `effect` block is what
/// gives it duration and cancellation — so canonical `effect.cancel` addresses exactly what `attribute_apply`
/// handed out, and the kernel calls `RestoreApply` when that effect ends.
///
/// Native limits can reduce a submission. Only the per-id native value confirms its actual contribution;
/// the provider's ledger tracks ownership for cleanup and never substitutes for that readback.
///
/// Cleanup is driven by the kernel's own lifecycle because nothing else tells a provider that a life ended or a
/// world did: `WorldChanged` clears the ledger and the ids it still holds, `TickAdvanced` drops what belongs to a
/// life the player identity no longer resolves, and the kernel's restore callback revokes a single effect's
/// modifications. A lifecycle callback may not mutate the kernel, so the sweep asks the player identity whether a
/// life is current instead of the kernel's entity check: both answer about the same table, and the identity is
/// this package's own half of it.</summary>
internal sealed partial class AgentModifierAdapter : IDisposable
{
    /// <summary>The design's own pace for native modification writes per host tick, shared by every command that
    /// applies one. It only spreads the writes: a recipient the budget has not reached waits in a queue and is
    /// written on a later tick, never refused. Each tick and each command write at least one recipient, so a
    /// queue always drains.</summary>
    internal const int MaximumModifierWritesPerTick = 32;
    /// <summary>The largest magnitude one submission may carry. The native entry takes a float and applies no
    /// useful universal bound, so a value no modifier table would ever hold is refused here instead of being written.</summary>
    internal const double MaximumAmount = 1000000;
    /// <summary>The recipient kind this adapter resolves locally; enemy recipients use their shared receiver.</summary>
    internal const string PlayerKind = EntityKinds.Player;


    internal const string AmountSlot = "amount";

    // The ledger is keyed by the kernel's own handle value. It is matched against the one the kernel cast, which is
    // the only key a provider has: `TryNative` answers only for a handle that carries a native object, while a
    // handle registered here would be dropped by the kernel's own lifetime sweep — no provider in this package
    // registers the native object resolver that sweep asks, so a `PlayerAgent` would answer "no current life" and
    // release a live handle. The default value is the "no handle" marker.

    /// <summary>One live modification: what the native entry returned, the life it was written on, and the effect
    /// handle the command's own card filed it under. It carries no clock: its duration is the kernel's effect
    /// lifecycle, which ends it through <see cref="RestoreApply"/>.</summary>
    private sealed class Applied
    {
        internal uint Id;
        internal EntityReference Target = new("", 0, 0);
        /// <summary>The member this contribution was written to, kept so the provider's own per-recipient total
        /// can be unwound when the entry is released.</summary>
        internal AgentModifier Modifier;
        /// <summary>The signed contribution the native entry accepted, which is exactly what the provider's own
        /// total has to give back when the entry goes.</summary>
        internal double Submitted;
        internal bool ContributionConfirmed;
        internal ForgeCore.ModifierTable? NativeInstance;
        internal RuntimeHandle Handle;
        /// <summary>Whether a native clear failure for this id was already reported, so a failing clear is one
        /// diagnostic instead of one per tick while the id stays in the ledger.</summary>
        internal bool Reported;
    }

    /// <summary>The modifications one command wrote, under the effect handle the kernel cast for that step: every
    /// member of its `modifiers` group on every recipient that committed.</summary>
    private sealed class Group
    {
        internal RuntimeHandle Key;
        internal readonly List<uint> Ids = new();
    }

    private readonly RuntimeModuleHandle _registration;
    /// <summary>The identity half the session this adapter belongs to built; read per command, because the half is
    /// built after the registration this adapter subscribes on.</summary>
    private readonly Func<PlayerIdentityModule?> _players;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<uint, Applied> _entries = new();
    private readonly Dictionary<RuntimeHandle, Group> _groups = new();
    // The provider's own live contribution per recipient and member, so the optional `cap` port can clamp what
    // this card has stacked without reading the native table's total (which includes every other source). It is
    // maintained with the ledger: a contribution joins on a confirmed write and leaves when its entry is released.
    private readonly Dictionary<(EntityReference Target, AgentModifier Modifier), double> _totals = new();
    // Reused scratch for the ids one release names.
    private readonly List<uint> _due = new();
    private RuntimeLifecycleSubscription? _lifecycle;
    // Recipients the write budget has not reached, oldest command first. A world change or the end of their
    // effect empties them, so nothing is written to an old world's objects or after its effect ended.
    private readonly Queue<Request> _pending = new();
    // Reused scratch for one recipient's write.
    private readonly List<Applied> _taken = new();
    private readonly List<uint> _revoke = new();
    private readonly List<Applied> _written = new();
    private long _writeTick = -1;
    private int _writesThisTick;
    private long _clearFailures;
    private bool _disposed;

    /// <summary>The one adapter attached to the one Player registration, or null while none is. The kernel's handler
    /// table is part of the registration, which is built before the registration handle — and therefore before the
    /// adapter that mints handles on it — exists, so the table holds the two static entry points below and they
    /// answer from here. One package registers at most one provider of one identity namespace.</summary>
    internal static AgentModifierAdapter? Current { get; private set; }

    /// <summary>The `attribute_apply` entry point the registration's handler table holds.</summary>
    internal static CommandResult ApplyHandler(CommandContext context)
        => Current is { } adapter ? adapter.Apply(context) : Outcome.Rejected(ResultCode.AuthorityOrPhase);

    /// <summary>The canonical `forge.action.effect.cancel` entry point held by the registration.</summary>
    internal static CommandResult CancelHandler(CommandContext context)
        => Current is { } adapter ? adapter.Cancel(context) : Outcome.Rejected(ResultCode.AuthorityOrPhase);

    /// <summary>The `attribute_apply` row's own restore callback, which the kernel calls when the effect its card
    /// asked for ends — by its duration, by a cancellation, by a released plan or by the world. What ends is every
    /// modification that command filed under that handle; the kernel owns that one cancellation path, so a repeated
    /// restore after the group is already gone is the no-op it has to be.</summary>
    internal static void RestoreApply(RuntimeEffectContext context)
    {
        var key = context.HandleId;
        if (!key.IsCast) return;
        var adapter = Current;
        long failures = adapter?.ClearFailures ?? 0;
        try
        {
            if (adapter != null)
            {
                adapter.DropPending(key);
                adapter.ReleaseGroup(key, "effect-ended");
                if (adapter.ClearFailures != failures)
                    throw new RuntimeContractException(ResultCode.NativeClearException, "Native modifier restoration was not confirmed.");
            }
        }
        finally
        {
            if (ForgeEnemy.Native.EnemyHost.Session?.Module is { } enemies)
            {
                try { enemies.RestoreEnemyAttributes(key); }
                catch (Exception error)
                {
                    adapter?._report("player.attribute-modifier-enemy-clear-failed: " + error.GetType().Name);
                    throw new RuntimeContractException(ResultCode.NativeClearException, "Native enemy modifier restoration was not confirmed.");
                }
            }
        }
    }

    /// <summary>The adapter subscribes on the registration it is handed, because a lifecycle observer is the one
    /// per-tick and per-world notification a provider gets without the kernel calling into it. The subscription
    /// needs a registration that exists, so the adapter is created after `RegisterModule` returned — the kernel
    /// accepts a subscription outside the registration window, though not inside a dispatch.</summary>
    internal AgentModifierAdapter(RuntimeModuleHandle registration, Func<PlayerIdentityModule?> players, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _lifecycle = _registration.ObserveLifecycle(OnLifecycle);
        Current = this;
    }

    /// <summary>Native cleanup attempts whose removal could not be confirmed, including clear exceptions.</summary>
    internal long ClearFailures => _clearFailures;

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player attribute modifiers require the owning simulation thread.");
    }
}
