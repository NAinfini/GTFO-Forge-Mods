using System;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>
/// The foam half of the native execution layer: one method per glue operation, each taking the enemy instance the
/// caller resolved and answering with the one decision it made, plus the command handler that fans one plan step
/// out over the recipients and the ledger that honours a Forge-side duration.
///
/// Every write goes through the game's own glue entry point. `ProjectileManager.WantToSpawnGlueOnEnemyAgent` is the
/// call the C-foam launcher's own path makes for a hit enemy: it takes a sync id, the agent, the glue target's sub
/// index, a local position, the volume descriptor and the effect multiplier, and the game replicates it as its
/// `m_spawnGlueOnEnemy` broadcast. The descriptor the game itself builds carries exactly the two numbers an author
/// can meaningfully choose — `volume` and `expandVolume` — plus the projectile's own current scale, which belongs
/// to the projectile in flight and not to an author, so it is left at zero exactly as this package's other neutral
/// native arguments are.
///
/// The row only ever writes foam onto an enemy. A door and a bare surface both have their own spawn entry points in
/// the same manager, and neither is reachable from this provider: see `evidence/enemy-glue-hooks.json`.
///
/// What a reference currently names is the enemy provider's own answer, handed in as <c>resolve</c>; this family
/// never keeps a pointer it would have to trust later. `internal`, not private, so the focused suite can dispatch
/// the handler through a kernel-built command context; the registration still only ever reaches it through
/// `EnemyModule`'s handler table.
/// </summary>
internal sealed class GlueActions : IDisposable
{
    /// <summary>The smallest volume the game's own unit can carry and still be a request.</summary>
    internal const float MinimumVolume = 0.000001f;
    internal const float MaximumVolume = 1000000f;
    internal const float MinimumStrength = 0.000001f;
    internal const float MaximumStrength = 1000000f;

    private readonly Func<EntityReference, GlueTarget?> _resolve;
    private readonly Func<bool> _canExecute;
    private readonly GlueLedger _ledger;
    private bool _disposed;

    internal GlueActions(RuntimeModuleHandle registration, Func<EntityReference, GlueTarget?> resolve,
        Func<EntityReference, GlueTarget?> resolveCleanup, Func<bool> canExecute, IForgeEngine kernel)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _ledger = new GlueLedger(kernel ?? throw new ArgumentNullException(nameof(kernel)), registration,
            resolveCleanup ?? throw new ArgumentNullException(nameof(resolveCleanup)));
    }

    /// <summary>Whether this enemy life already carries foam this provider applied.</summary>
    internal bool IsFoamed(EntityReference target) => _ledger.IsFoamed(target);

    /// <summary>Retires cleanup state only when the module has proved the exact enemy life ended.</summary>
    internal void ForgetLife(EntityReference target) => _ledger.ForgetLife(target);

    /// <summary>Moves the foam this provider applied to the world a host migration made; see <see cref="GlueLedger.Follow"/>.</summary>
    internal void Follow(Func<IntPtr, EntityReference?> lifeOf, Func<RuntimeHandle, RuntimeHandle> handleOf) => _ledger.Follow(lifeOf, handleOf);

    /// <summary>Whether a volume the plan handed over is one this entry point can carry. A non-finite, zero or
    /// negative volume is not a small amount of foam; the game's own descriptor has no such value.</summary>
    internal static bool IsUsableVolume(double value)
        => double.IsFinite(value) && value >= MinimumVolume && value <= MaximumVolume;

    /// <summary>Whether an effect multiplier the plan handed over is one the entry point can carry. The game's own
    /// launcher passes its own multiplier here, which is a positive scale on the foam's effect, never zero or
    /// negative.</summary>
    internal static bool IsUsableStrength(double value)
        => double.IsFinite(value) && value >= MinimumStrength && value <= MaximumStrength;

    /// <summary>The enemy's own reading, taken once per target: the volume of foam currently attached to it. A
    /// receiver that does not read answers null, which is a refusal rather than a zero-volume reading.</summary>
    internal static float? ReadAttachedGlueVolume(Dam_EnemyDamageBase damage)
    {
        try
        {
            float volume = damage.AttachedGlueVolume;
            return float.IsFinite(volume) && volume >= 0f ? volume : null;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
    }

    /// <summary>The volume descriptor the game's own launcher builds for one hit: the amount of foam the shot
    /// carries and the potential it may expand by. `currentScale` stays zero because it is the projectile's own
    /// growing scale, not an author input.</summary>
    internal static GlueVolumeDesc Descriptor(double volume, double expandVolume)
        => new() { volume = (float)volume, expandVolume = (float)expandVolume };

    /// <summary>
    /// Applies one foam volume to one enemy through the game's own spawn entry point.
    ///
    /// The native broadcast indexes the damage receiver's glue-target array. Use an enabled target belonging to
    /// this exact enemy, and verify that its declared sub index resolves back to the same native pointer. There is
    /// no negative sentinel. The local position is the target's own origin because this action has no impact point.
    ///
    /// The readback is the whole evidence, and the caller performs it: this layer only enters the entry point.
    /// </summary>
    internal static bool Apply(Dam_EnemyDamageBase damage, double volume, double expandVolume, double strength, out float capacity)
    {
        capacity = 0;
        var enemy = damage.Owner;
        var targets = damage.m_glueTargets;
        if (enemy == null || enemy.Pointer == IntPtr.Zero || targets == null) return false;
        foreach (var target in targets)
        {
            if (target == null || target.Pointer == IntPtr.Zero || target.GlueTargetEnemyAgent?.Pointer != enemy.Pointer
                || target.IgnoreGlue || !float.IsFinite(target.GlueCapacity) || target.GlueCapacity <= 0) continue;
            int subIndex = target.GlueTargetSubIndex;
            if (subIndex < 0 || subIndex >= targets.Length || damage.GetGlueTarget(subIndex)?.Pointer != target.Pointer) continue;
            ProjectileManager.WantToSpawnGlueOnEnemyAgent(ProjectileManager.GetNextSyncID(), enemy, subIndex,
                UnityEngine.Vector3.zero, Descriptor(volume, expandVolume), (float)strength);
            capacity = target.GlueCapacity;
            return true;
        }
        return false;
    }
    /// <summary>
    /// Takes one volume of foam back off an enemy through the receiver's own removal entry point, and answers
    /// whether the volume it publishes actually fell.
    ///
    /// This is how a Forge-side duration ends: the native glue has no timer of its own, so the foam the plan asked
    /// for is removed by the same amount it added. Only a fall is a removal; a request the receiver did not act on
    /// answers false so the caller reports it instead of treating the foam as gone.
    /// </summary>
    internal static bool Remove(Dam_EnemyDamageBase damage, double volume, float before)
    {
        damage.RemoveGlueVolume((float)volume, 0f);
        float? after = ReadAttachedGlueVolume(damage);
        return after.HasValue && after.Value <= ExpectedAfterRemoval(before, volume);
    }

    internal static float ExpectedAfterRemoval(float before, double volume)
        => Math.Max(0f, before - (float)volume);

    /// <summary>
    /// Multi-target foam. One row is written per recipient in the plan's own order, and a spawn whose attached
    /// volume cannot be read back is reported as an unknown commit rather than as success: the game's spawn entry
    /// point returns void, so a spawn it dropped and one it applied are indistinguishable from this side, and
    /// neither may be claimed.
    ///
    /// The host is the only authority that may submit: the entry point is a static "want to" call whose replication
    /// is the game's own broadcast, so a client that reached it would ask the game to send a spawn the master never
    /// authorized. Two facts are asked, exactly as this package's other write rows ask them: the context's own
    /// authority fact, and the provider's session gate — the plugin is loaded, the runtime is in a phase that may
    /// write, and the game says this peer is the master.
    ///
    /// Every application in one dispatch shares one `foam` handle, minted on the first application that lands and
    /// registered with the enemy that row names; cancelling it takes that volume back off every enemy the handle
    /// covers. A dispatch that applied nothing mints no handle.
    /// </summary>
    internal CommandResult Foaming(CommandContext context)
    {
        if (_disposed || !context.IsHost || !_canExecute()) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // The source is a required, kernel-validated entity reference. This row has no use for it beyond that: the
        // game's own spawn entry point carries the effect multiplier, not a source agent, so no attacker is
        // invented for it.
        if (!context.TryEntity(GlueContract.SourceSlot, out _))
            throw new RuntimeContractException("shape-port", "foaming source");
        double volume = context.Number(GlueContract.VolumeSlot) ?? double.NaN;
        if (!IsUsableVolume(volume)) return CommandResult.Rejected(ResultCode.VolumeOutOfRange);
        // `bubble_amount` is the blob's own expansion potential, the second quantity the game's descriptor
        // carries beside the foam volume this row attaches. An unwired port keeps the volume's own value, which
        // is what the descriptor carried before the port existed.
        double bubbleAmount = volume;
        if (context.Present(GlueContract.BubbleAmountSlot))
        {
            double bubbleValue = context.Number(GlueContract.BubbleAmountSlot) ?? double.NaN;
            if (!IsUsableVolume(bubbleValue))
                return CommandResult.Rejected(ResultCode.BubbleAmountOutOfRange);
            bubbleAmount = bubbleValue;
        }
        double strength = context.Number(GlueContract.StrengthSlot) ?? double.NaN;
        if (!IsUsableStrength(strength)) return CommandResult.Rejected(ResultCode.StrengthOutOfRange);
        long duration = context.Integer(GlueContract.DurationSlot) ?? -1;
        // A duration of zero means "until the encounter ends", which is the game's own glue behaviour; a negative
        // duration is not a shorter one.
        if (duration < 0) return CommandResult.Rejected(ResultCode.DurationOutOfRange);
        long? expiresAtTick = null;
        if (duration > 0)
        {
            long now = Math.Max(0, context.SimulationTick);
            if (duration > long.MaxValue - now) return CommandResult.Rejected(ResultCode.DurationOutOfRange);
            expiresAtTick = now + duration;
        }
        // The handle every application of this dispatch is filed under is the kernel's own for the step: a card's
        // `effect` block is the clock, the kernel casts that handle before the handler runs, publishes it into this
        // row's declared `foam` port and calls `EnemyModule.RestoreFoam` back when the effect ends. A provider
        // cannot mint a handle while a command is being dispatched — the kernel refuses a registration change
        // mid-dispatch — so a provider-minted handle is a handle this row could never carry. A card with no `effect`
        // block names none, and its foam stands until the module releases it.
        var foam = context.EffectHandleId;
        var request = new FoamRequest(this, volume, bubbleAmount, strength, foam, expiresAtTick, Math.Max(0, context.SimulationTick));
        // The rows are the whole frame: the `foam` handle is the kernel's own and it publishes it into that port
        // itself when the step carries an effect, so a handler that wrote one would be a second writer of it.
        return context.EachTarget(ref request, FoamStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatFoaming.RowTargetCount);
    }

    private readonly record struct FoamRequest(GlueActions Glue, double Volume, double BubbleAmount, double Strength,
        RuntimeHandle? Foam, long? ExpiresAtTick, long Tick);

    /// <summary>The row's `amount` is the volume the plan asked for, on every row.</summary>
    private static readonly TargetStep<FoamRequest> FoamStep = static (ref FoamRequest request, EntityReference target, int _, RowWriter rows)
        => rows.Number(Blocks.ActionCombatFoaming.RowAmount, request.Volume)
            .End(request.Glue.FoamOne(request, target));

    /// <summary>One recipient. The row's own status is the ABI's `committed` column: only a volume that actually
    /// moved confirmed it, a refusal before the entry point confirmed nothing, and a spawn this side could not
    /// observe afterwards stays unknown.</summary>
    private BehaviorOutcome FoamOne(in FoamRequest request, EntityReference target)
    {
        var resolved = _resolve(target);
        if (resolved == null) return BehaviorOutcome.Refused(EnemyModule.UnresolvedCode(target));
        var glue = resolved.Value;
        if (!glue.Enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        var damage = glue.Damage;
        if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero)
            return BehaviorOutcome.Refused(ResultCode.MissingGlueReceiver);
        if (damage.Owner == null || damage.Owner.Pointer != glue.EnemyPointer)
            return BehaviorOutcome.Refused(ResultCode.GlueReceiverOwnerMismatch);
        float? before = ReadAttachedGlueVolume(damage);
        if (!before.HasValue) return BehaviorOutcome.Refused(ResultCode.MissingGlueReceiver);
        if (!_ledger.HasRoom) return BehaviorOutcome.Refused(ResultCode.GlueBudget);

        // Entering the native spawn entry point is the only place the world is written. It is one call and the
        // readback is its only proof, so a failure after it stays unknown rather than being retried.
        float? after;
        float capacity;
        try
        {
            if (!Apply(damage, request.Volume, request.BubbleAmount, request.Strength, out capacity))
                return BehaviorOutcome.Refused(ResultCode.MissingGlueReceiver);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        try
        {
            var current = _resolve(target);
            var currentDamage = current?.Damage;
            if (current == null || current.Value.EnemyPointer != glue.EnemyPointer
                || currentDamage == null || currentDamage.Pointer != damage.Pointer || !currentDamage.IsSetup
                || currentDamage.Owner == null || currentDamage.Owner.Pointer != glue.EnemyPointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            after = ReadAttachedGlueVolume(currentDamage);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        // A receiver that does not read afterwards, or whose published volume went backwards, is not a spawn
        // this side can vouch for.
        if (!after.HasValue || after.Value < before.Value) return BehaviorOutcome.Unseen(ResultCode.UnexpectedGlueReadback);
        // The volume did not move. The game applies the spawn on a later frame or when its broadcast arrives, so
        // the ledger keeps reading the receiver for a window and files the increment if it appears; this row stays
        // the unknown commit it is now. A receiver already at its cap takes nothing more and says so by its own code.
        if (after.Value == before.Value)
        {
            _ledger.AddPending(new PendingGlue(target, glue.EnemyPointer, damage.Pointer, before.Value, request.Foam,
                request.ExpiresAtTick, request.Tick));
            return before.Value >= capacity ? BehaviorOutcome.Refused(ResultCode.GlueAtCapacity)
                : BehaviorOutcome.Unseen(ResultCode.GlueUnseen);
        }

        float added = after.Value - before.Value;
        // Each observed increment keeps its own handle, expiry and exact receiver. A later application to the
        // same life is another obligation; merging it would let either cancellation consume the other.
        _ledger.Add(new GlueRecord(target, glue.EnemyPointer, damage.Pointer, added, request.Foam, request.ExpiresAtTick));
        return BehaviorOutcome.Committed();
    }

    /// <summary>The `foam` handle's own cancel: every application made through it is marked once and taken back off
    /// the exact receiver it was made on. A failed removal remains private cleanup work for a later safe tick; a
    /// repeated public callback does not submit it twice in the same tick.</summary>
    internal void ClearFoamed(RuntimeHandle handle)
    {
        if (!_ledger.RequestByHandle(handle))
            throw new InvalidOperationException("Enemy glue cancellation is awaiting native cleanup.");
    }

    public void Dispose()
    {
        bool retry = _disposed;
        _disposed = true;
        // Repeated Dispose calls retry a retained cleanup after an earlier native failure. The module keeps its
        // exact-life table until this succeeds, while `_disposed` continues to reject every new action.
        _ledger.Dispose(retry);
    }
}
