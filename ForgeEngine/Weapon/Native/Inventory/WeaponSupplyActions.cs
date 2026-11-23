using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using GameData;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>The ammunition row of `WeaponSupplyContract`, submitted through the game's own ammunition gift and
/// storage bodies. The evidence for every member named here is `evidence/weapon-supply.json`; nothing in this
/// file computes an amount the game does not itself apply, and every write is read back before it is reported.
///
/// <b>A positive amount</b> — the game's gift is a `pAmmoGive` of three relative amounts. Its receive half,
/// `PlayerBackpackManager.ReceiveAmmoGive`, applies them with `PlayerAmmoStorage.PickupAmmo` and, on the machine that owns
/// the pool (its own player, or a bot on the host), brings the owner's storage sync forward, which every other machine
/// takes as the pool's absolute value. The owner therefore settles the gift by calling that receive half itself: the
/// write and the readback happen in the same call on every machine, host or client. A pool this machine only mirrors
/// is submitted through `GiveAmmoToPlayer` (the host's authoritative action, as before); its landing is not observable here, so the row is an unobserved
/// write like any other (failed, commit unknown, `ammo-change-unobserved`). The row's
/// absolute round count is converted against the PlayerDataBlock resource-pack conversion base, read immediately
/// before the call. The bullets that really landed are read back from the same pool afterwards — the result's
/// `amount` is that readback, never the request.
///
/// <b>A negative amount</b> — the gift body's own `InventorySlotAmmo.AddAmmo` clamps only at the pool cap
/// (`Min(AmmoInPack + amount, AmmoMaxCap)`), so it cannot subtract safely; the body that clamps both ends is
/// `PlayerAmmoStorage.UpdateBulletsInPack(AmmoType, int)`, the one the resource pack itself calls on a backpack
/// this machine owns.
///
/// <b>Where it runs.</b> The row is an `owner` row: the kernel sends it to the machine of the player it names, so
/// both writes and the readback address the pool this machine owns, and a client's ammunition is never written
/// through the host's mirrored copy. The overflow policy decides a pool that holds fewer rounds than asked for: `reject` takes none, `clamp` takes what is there.
///
/// The recipient is resolved the way this package resolves every player: the reference is asked for the native
/// `SNet_Player` through the injected lookup, the game's own `TryGetBackpack` answers the backpack, and the
/// backpack's owner has to resolve back to the very reference that was asked about. A name, a slot or an agent is
/// never used to find a player.</summary>
internal sealed class WeaponSupplyAdapter
{

    /// <summary>What the runtime must answer for one `gtfo.player` reference: the native player it names, or null
    /// when this environment cannot name one. The signature is the same one the inventory actions take, so the
    /// integration forwards one lookup to both.</summary>
    internal delegate object? PlayerLookup(EntityReference reference);

    /// <summary>The other direction: the player life a native player is recorded under, or null. Both directions are
    /// ForgePlayer's player directory; the integration forwards the same two answers to the inventory actions.</summary>
    internal delegate EntityReference? OwnerLookup(SNet_Player player);

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly PlayerLookup _playerOf;
    private readonly OwnerLookup _referenceOf;
    private readonly Action<string> _report;
    private long _giftSequence;
    private int _giftTraces;
    internal const int GiftTraceBudget = 64;

    internal WeaponSupplyAdapter(IForgeEngine kernel, Func<bool> canExecute, PlayerLookup playerOf, OwnerLookup referenceOf,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _playerOf = playerOf ?? throw new ArgumentNullException(nameof(playerOf));
        _referenceOf = referenceOf ?? throw new ArgumentNullException(nameof(referenceOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>What one recipient's submission did: whether it wrote the pool, the code that says why, and the rounds
    /// the pool really gained as read back. The status follows from the commit: a confirmed write succeeded, an
    /// unobserved one (a gift handed to the host for a pool this machine does not own among them) failed with its commit
    /// unknown, and a write that was never made was rejected.</summary>
    internal readonly record struct SupplyOutcome(Commit Commit, string Code, int Amount);

    internal CommandResult HandleAdd(CommandContext context) => HandleAdd(this, context);


    /// <summary>The `forge.action.weapon.ammo_add` handler: one recipient, one pool, one signed amount, and the
    /// three structural parameters the catalog declares. Everything the native path cannot carry is refused before
    /// the first native call, and the overflow policy is applied against the pool's own bounds read for the
    /// decision: past the cap for a positive amount, below zero for a negative one.
    /// `unit` says what the amount counts: `rounds` is the bullet count the pools are read back in, and `raw` is
    /// the pool's own ammunition value, converted through the slot's own `CostOfBullet` before the write.</summary>
    internal static CommandResult HandleAdd(WeaponSupplyAdapter adapter, CommandContext context)
    {
        if (!adapter._canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!Recipient(context, out var recipient, out string code)) return Outcome.Rejected(code);
        if (!TryAmount(context.Integer(WeaponSupplyContract.AddAmountSlot), out int amount))
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        if (!TryAmmoType(context.Text(WeaponSupplyContract.AddAmmoTypeSlot), out AmmoType type, out code))
            return Outcome.Rejected(code);
        if (!TryPolicy(context.Text(WeaponSupplyContract.AddOverflowPolicySlot),
                WeaponSupplyContract.OverflowPolicies, out string policy, out code))
            return Outcome.Rejected(code);
        // `unit` is optional: an absent parameter is the round count this row always took, and a written one has
        // to be a member of the row's own list.
        var unit = context.Text(WeaponSupplyContract.AddUnitSlot) ?? "rounds";
        if (!WeaponSupplyContract.Units.Contains(unit)) return Outcome.Rejected(ResultCode.AmmoUnitUnknown);
        // The consumption entry takes a positive Int32 magnitude; Abs(Int32.MinValue) cannot be represented.
        if (amount == int.MinValue) return Outcome.Rejected(ResultCode.AmountOutOfRange);
        int sign = Math.Sign(amount), magnitude = Math.Abs(amount);
        if (unit == "raw" && !TryRounds(adapter, recipient, type, magnitude, out magnitude, out code))
            return Outcome.Rejected(code);
        if (sign < 0)
        {
            // The row's `amount` is what the pool gained, so a removal reports its rounds as a negative gain.
            var taken = adapter.Consume(recipient, type, magnitude, policy == "clamp");
            return Answer(context, taken with { Amount = -taken.Amount });
        }
        if (type is not (AmmoType.Standard or AmmoType.Special or AmmoType.Class))
            return Outcome.Rejected(ResultCode.AmmoPoolHasNoGift);
        var outcome = adapter.Add(recipient, type, magnitude, policy == "reject");
        return Answer(context, outcome);
    }

    /// <summary>Adds <paramref name="amount"/> rounds to one pool through the game's own gift body. The pool's cap and
    /// its current bullets are read for this request; the gift converts the rounds through the resource-pack base,
    /// and the bullets that really landed are the difference the pool reports afterwards.</summary>
    internal SupplyOutcome Add(EntityReference recipient, AmmoType type, int amount, bool rejectOverflow)
    {
        if (!TryPool(recipient, out var player, out var storage, out string refusal)) return Refuse(refusal);
        if (!TryRead(player, type, out int before, out _)) return Refuse(ResultCode.AmmoReadbackException);
        // The whole-bullet counts floor the pool's own value, so a pool holding less than one bullet's worth below its
        // cap reads one bullet short and a gift smaller than one bullet changes the count by nothing. The pool's raw
        // ammunition value is what the game clamps and adds to, so room and change are decided from it.
        if (!TryRaw(storage, type, out float rawBefore, out float rawMax, out float cost)) return Refuse(ResultCode.AmmoReadbackException);
        // The game's own whole-bullet read floors a raw value that float arithmetic left a hair below a whole bullet
        // (149.99997 reads 149 although the pool holds 150), so a request of 10 can read 11. The bullets the row reports
        // are counted from the raw values with that float error removed, before and after alike.
        before = WholeBullets(rawBefore, cost);
        // The reported bullet cap is not the raw capacity over the bullet cost (the A1 pool reports more bullets than
        // the raw capacity holds), so room is judged from the raw values.
        if (rejectOverflow && (double)rawBefore + (double)amount * cost > rawMax)
            return Refuse(ResultCode.AmmoPoolOverflow);
        // `clamp` takes what fits. A pool already at its cap has no room, so the game's gift cannot change it and
        // the answer is known from the read alone: nothing is submitted and the row reports the 0 rounds that
        // landed. A pool with room whose gift still lands nothing stays unobserved below.
        if (amount > 0 && rawBefore >= rawMax) return Confirmed(0);
        // Vanilla ResourcePackFirstPerson.ApplyPack -> PlayerAgent.GiveAmmoRel -> ReceiveAmmoGive (0x17589D0):
        // GetBlock<PlayerDataBlock>(1), then +0x178 / +0x188 / +0x198 times the relative gift (0x1758C4E..0x1758CB0).
        // Those are Ammo*ResourcePackMaxCap, not InventorySlotAmmo.AmmoMaxCap or BulletsMaxCap (which includes the clip).
        // AddAmmo (0x12F7D70) separately clamps the resulting raw delta to the reserve's AmmoMaxCap.
        int giftBase;
        try
        {
            var data = PlayerDataBlock.GetBlock(1u);
            if (data == null) return Refuse(ResultCode.AmmoReadbackException);
            giftBase = type switch
            {
                AmmoType.Standard => data.AmmoStandardResourcePackMaxCap,
                AmmoType.Special => data.AmmoSpecialResourcePackMaxCap,
                AmmoType.Class => data.AmmoClassResourcePackMaxCap,
                _ => 0
            };
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.Add", failure); return Refuse(ResultCode.AmmoReadbackException); }
        if (giftBase <= 0) return Refuse(ResultCode.AmmoReadbackException);
        float relative = (float)((double)amount * cost / giftBase);
        if (!float.IsFinite(relative) || relative < 0f) return Refuse(ResultCode.AmountOutOfRange);
        if (!_canExecute()) return Refuse(ResultCode.AuthorityOrPhase);
        // The machine that owns the pool settles the gift itself, through the gift's own receive half: the pool is written
        // here and now, and that body also brings forward the owner's storage sync, which every other machine takes as the
        // pool's absolute value. A pool this machine only mirrors still goes through the host's action, unobserved.
        bool owned = OwnsPool(player);
        long sequence = ++_giftSequence;
        var traceSession = WeaponNativeSession.Offer(WeaponFeature.Ammunition);
        var traceGift = _giftTraces < GiftTraceBudget && traceSession != null && ReferenceEquals(traceSession.Supply, this)
            && traceSession.Log.Logs(RuntimeLogLevel.Trace);
        if (traceGift) _giftTraces++;
        LogGift("request", rawBefore, before);
        try
        {
            if (owned)
            {
                var gift = new pAmmoGive
                {
                    ammoStandardRel = Standard(type, relative),
                    ammoSpecialRel = Special(type, relative),
                    ammoClassRel = Class(type, relative)
                };
                gift.targetPlayer.SetPlayer(player);
                PlayerBackpackManager.Current.ReceiveAmmoGive(gift);
            }
            else PlayerBackpackManager.GiveAmmoToPlayer(player, Standard(type, relative), Special(type, relative), Class(type, relative));
        }
        catch (Exception error)
        {
            LogGift("threw", null, null);
            // Whether the write landed before the throw is not observable from here; the commit stays unknown and is
            // never retried.
            _report("weapon.ammo-add-commit-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
        // The host applies a mirrored pool's gift and its owner reports the result in a later storage sync, so this
        // machine's copy proves nothing: the write is unobserved, summed up by the same rule as every other one.
        if (!owned) { LogGift("submitted-to-host", null, null); return Unknown(ResultCode.AmmoChangeUnobserved); }
        if (!TryRead(player, type, out int after, out _))
        { LogGift("readback-unavailable", null, null); return Unknown(ResultCode.AmmoReadbackException); }
        float rawAfter = 0f;
        bool rawAfterKnown = TryRaw(storage, type, out rawAfter, out _, out _);
        if (rawAfterKnown) after = WholeBullets(rawAfter, cost);
        LogGift("returned", rawAfterKnown ? rawAfter : null, after);
        int landed = after - before;
        if (landed < 0) return Unknown(ResultCode.AmmoChangeUnobserved);
        // No whole bullet landed: the commit is still observed when the pool's raw value moved (the gift was smaller
        // than one bullet's worth); a pool whose raw value did not move at all is the unobserved case.
        if (landed == 0 && amount != 0 && !(rawAfterKnown && rawAfter > rawBefore))
            return Unknown(ResultCode.AmmoChangeUnobserved);
        return Confirmed(landed);

        void LogGift(string stage, float? raw, int? bullets)
        {
            if (!traceGift) return;
            try
            {
                var session = WeaponNativeSession.Offer(WeaponFeature.Ammunition);
                if (session == null || !ReferenceEquals(session.Supply, this) || !session.Log.Logs(RuntimeLogLevel.Trace)) return;
                session.Log.Write(RuntimeLogLevel.Trace, "weapon.supply-trace", new RuntimeLogFacts
                {
                    Entity = recipient.Id,
                    Values = new[]
                    {
                        RuntimeLogValue.Of("phase", "ammo-gift-boundary"), RuntimeLogValue.Of("stage", stage),
                        RuntimeLogValue.Of("sequence", sequence), RuntimeLogValue.Of("worldEpoch", recipient.WorldEpoch),
                        RuntimeLogValue.Of("traceBudgetRemaining", (long)(GiftTraceBudget - _giftTraces)),
                        RuntimeLogValue.Of("lifeEpoch", recipient.LifeEpoch), RuntimeLogValue.Of("ammoType", (long)type),
                        RuntimeLogValue.Of("rounds", (long)amount), RuntimeLogValue.Of("giftBase", (long)giftBase),
                        RuntimeLogValue.Of("relative", (double)relative), RuntimeLogValue.Of("relativeBits", (long)BitConverter.SingleToInt32Bits(relative)),
                        RuntimeLogValue.Of("packetRaw", (double)(relative * giftBase)), RuntimeLogValue.Of("cost", (double)cost),
                        RuntimeLogValue.Of("rawBefore", (double)rawBefore),
                        RuntimeLogValue.Of("rawNow", raw?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable"),
                        RuntimeLogValue.Of("bulletsNow", (long)(bullets ?? -1))
                    }
                });
            }
            catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.GiftBoundary", failure); }
        }
    }

    /// <summary>The pool's whole bullets from its raw value. Float arithmetic leaves a pool that holds exactly N
    /// bullets a hair below N about as often as exact, and the game's floor then reads N-1; the float error of a pool
    /// value is about 3e-5 bullet, so 0.001 bullet absorbs it without ever counting a bullet the pool does not hold.</summary>
    internal static int WholeBullets(float raw, float cost) => (int)Math.Floor((double)raw / cost + 1e-3);

    /// <summary>The pool's raw ammunition value and cap, the values the game's own add clamps and writes. False when the
    /// pool cannot be read; callers refuse an additive request without a known raw value, capacity and bullet cost.</summary>
    private static bool TryRaw(PlayerAmmoStorage storage, AmmoType type, out float value, out float max, out float cost)
    {
        value = 0f;
        max = float.PositiveInfinity;
        cost = 0f;
        try
        {
            var pool = storage.GetInventorySlotAmmo(type);
            if (pool == null || pool.Pointer == IntPtr.Zero) return false;
            value = pool.AmmoInPack;
            max = pool.AmmoMaxCap;
            cost = pool.CostOfBullet;
            if (float.IsFinite(value) && float.IsFinite(max) && max > 0f && float.IsFinite(cost) && cost > 0f) return true;
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.TryRaw", failure); }
        value = 0f;
        max = float.PositiveInfinity;
        cost = 0f;
        return false;
    }

    /// <summary>Takes <paramref name="amount"/> rounds out of one pool through the storage body that clamps into
    /// the pool's own bounds; with <paramref name="partial"/> a pool holding fewer takes what it holds. The pool has to belong to this machine: a remote client owns and syncs its own
    /// ammunition, so a write to the host's mirrored copy is refused rather than silently reverted.</summary>
    internal SupplyOutcome Consume(EntityReference recipient, AmmoType type, int amount, bool partial)
    {
        if (!TryPool(recipient, out _, out var storage, out string refusal)) return Refuse(refusal);
        // The same raw-based whole-bullet count `Add` uses, so a pool holding N bullets never reads N-1.
        if (!TryRaw(storage, type, out float rawBefore, out _, out float cost)) return Refuse(ResultCode.AmmoReadbackException);
        int before = WholeBullets(rawBefore, cost);
        if (before <= 0) return Refuse(ResultCode.AmmoPoolEmpty);
        if (!partial && before < amount) return Refuse(ResultCode.AmmoPoolInsufficient);
        int spend = Math.Min(amount, before);
        if (!_canExecute()) return Refuse(ResultCode.AuthorityOrPhase);
        try { storage.UpdateBulletsInPack(type, -spend); }
        catch (Exception error)
        {
            _report("weapon.ammo-consume-commit-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
        if (!TryRaw(storage, type, out float rawAfter, out _, out _)) return Unknown(ResultCode.AmmoReadbackException);
        int landed = before - WholeBullets(rawAfter, cost);
        if (landed != spend) return Unknown(ResultCode.AmmoReadbackException);
        return Confirmed(landed);
    }

    /// <summary>The player and ammunition storage behind a recipient reference. The chain is the game's own and the
    /// identity is checked in both directions, exactly as the inventory writes check theirs.</summary>
    private bool TryPool(EntityReference recipient, out SNet_Player player, out PlayerAmmoStorage storage, out string code)
    {
        player = null!;
        storage = null!;
        if (recipient == null || !EntityKinds.Is(recipient, EntityKinds.Player)) { code = ResultCode.AmmoRecipientUnknown; return false; }
        // From here on the reference is a player's, so a reference that no longer resolves to that player is stale.
        if (!_kernel.IsEntityCurrent(recipient)) { code = ResultCode.StaleEntity; return false; }
        object? resolved;
        try { resolved = _playerOf(recipient); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.TryPool", failure); code = ResultCode.StaleEntity; return false; }
        if (resolved is not SNet_Player named) { code = ResultCode.StaleEntity; return false; }
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(named, out var backpack) || backpack == null)
            { code = ResultCode.AmmoPoolUnavailable; return false; }
            var owner = backpack.Owner;
            if (owner == null || _referenceOf(owner) != recipient)
            { code = ResultCode.StaleEntity; return false; }
            var found = backpack.AmmoStorage;
            if (found == null || found.Pointer == IntPtr.Zero) { code = ResultCode.AmmoPoolUnavailable; return false; }
            player = owner;
            storage = found;
            code = "";
            return true;
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.TryPool#2", failure); code = ResultCode.AmmoPoolUnavailable; return false; }
    }

    /// <summary>The pool's own counts, read through the manager's player-keyed wrappers so a pool is always read
    /// for the player the row named rather than for this machine's own backpack.</summary>
    private static bool TryRead(SNet_Player player, AmmoType type, out int bullets, out int cap)
    {
        bullets = 0;
        cap = 0;
        try
        {
            bullets = PlayerBackpackManager.GetBulletsInPack(type, player);
            cap = PlayerBackpackManager.GetAmmoMaxCap(type, player);
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.TryRead", failure); return false; }
        return bullets >= 0 && cap > 0 && bullets <= cap;
    }

    /// <summary>The pool slot's own ammunition cost, which is what a `unit=raw` amount is divided by. It is read
    /// from the same storage the add writes through and never from a weapon's archetype block, because the cost
    /// that converts a pool's ammunition value into bullets belongs to the pool's own slot.</summary>
    internal bool TryCostOfBullet(EntityReference recipient, AmmoType type, out float cost)
    {
        cost = 0f;
        if (!TryPool(recipient, out _, out var storage, out _)) return false;
        try
        {
            var slot = storage.GetInventorySlotAmmo(type);
            if (slot == null || slot.Pointer == IntPtr.Zero) return false;
            cost = slot.CostOfBullet;
            return float.IsFinite(cost) && cost > 0f;
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyActions.TryCostOfBullet", failure); return false; }
    }

    /// <summary>The one pool the row selected, as the game's gift spells its three amounts.</summary>
    private static float Standard(AmmoType type, float relative) => type == AmmoType.Standard ? relative : 0f;
    private static float Special(AmmoType type, float relative) => type == AmmoType.Special ? relative : 0f;
    private static float Class(AmmoType type, float relative) => type == AmmoType.Class ? relative : 0f;

    private static SupplyOutcome Confirmed(int amount) => new(Commit.Confirmed, ResultCode.Committed, amount);

    private static SupplyOutcome Refuse(string code) => new(Commit.None, code, 0);

    private static SupplyOutcome Unknown(string code)
        // The row's `amount` is a non-nullable integer. An unobserved write has proven no amount, so it reports 0 and
        // the `committed` column says unknown; a null here made the whole row invalid and replaced `code` with
        // `result-row-incomplete`.
        => new(Commit.Unknown, code, 0);

    /// <summary>Whether this machine owns <paramref name="player"/>'s pool: the machine that simulates the player.</summary>
    private static bool OwnsPool(SNet_Player player) => ForgeCore.Players.SimulatedHere(player);

    /// <summary>The one result row a single-recipient write answers with, and the command's own conclusion: the
    /// recipient's own outcome decides, because there is only ever one of them. The row's `amount` is a non-nullable
    /// integer, so an unobserved write reports 0 and its `committed` column says unknown.</summary>
    private static CommandResult Answer(CommandContext context, SupplyOutcome outcome)
    {
        var status = outcome.Commit switch { Commit.Confirmed => Status.Succeeded, Commit.Unknown => Status.Failed, _ => Status.Rejected };
        context.Rows.Begin(0).Integer(Blocks.ActionWeaponAmmoAdd.RowAmount, outcome.Amount).End(status, outcome.Commit, outcome.Code);
        return outcome.Commit switch
        {
            Commit.Confirmed => Outcome.Succeeded(),
            Commit.Unknown => Outcome.FailedUnknown(outcome.Code),
            _ => Outcome.Rejected(outcome.Code)
        };
    }

    /// <summary>The row's single recipient. The port is declared `one`, so a payload that carries an array is a
    /// plan the row was not written for and is refused by name.</summary>
    private static bool Recipient(CommandContext context, out EntityReference reference, out string code)
    {
        reference = null!;
        code = ResultCode.AmmoRecipientUnknown;
        return context.TryEntity(WeaponSupplyContract.AddPlayerSlot, out reference);
    }

    /// <summary>The requested signed native Int32 round count. A value outside the native type is refused.</summary>
    private static bool TryAmount(long? value, out int amount)
    {
        amount = 0;
        if (value is not { } parsed || parsed < int.MinValue || parsed > MaximumAmount)
            return false;
        amount = (int)parsed;
        return true;
    }

    /// <summary>The native positive magnitude accepted by the storage entry.</summary>
    internal const int MaximumAmount = int.MaxValue;

    /// <summary>The `ammo_type` parameter as the native enum. This table is the native `Player.AmmoType`
    /// declaration order, so the index is the native value and there is no second mapping table to drift. A
    /// member no row offers (`none`) still resolves here; the row's own policy is what refuses it.</summary>
    private static bool TryAmmoType(string? name, out AmmoType type, out string code)
    {
        type = AmmoType.None;
        code = ResultCode.AmmoTypeUnknown;
        if (name == null) return false;
        for (int index = 0; index < WeaponSupplyContract.AmmoTypes.Count; index++)
        {
            if (!string.Equals(WeaponSupplyContract.AmmoTypes[index], name, StringComparison.Ordinal)) continue;
            type = (AmmoType)index;
            code = "";
            return true;
        }
        return false;
    }

    /// <summary>One structural policy's member, checked against the row's own declared list.</summary>
    private static bool TryPolicy(string? name, IReadOnlyList<string> allowed, out string policy, out string code)
    {
        policy = "";
        code = ResultCode.AmmoPolicyUnknown;
        if (name == null) return false;
        foreach (string member in allowed) if (string.Equals(member, name, StringComparison.Ordinal)) { policy = name; code = ""; return true; }
        return false;
    }

    /// <summary>
    /// A `unit=raw` amount as the round count the pools are read and written in: the pool's own
    /// `InventorySlotAmmo.CostOfBullet` converts the raw ammunition value into bullets, which is the same
    /// conversion `PlayerAmmoStorage.UpdateBulletsInPack` performs on the way out. A pool whose slot this machine
    /// cannot read, a slot with no positive cost, and a raw amount that is not at least one whole bullet are all
    /// refusals — the amount is never rounded to a nearby count this package invented.
    /// </summary>
    private static bool TryRounds(WeaponSupplyAdapter adapter, EntityReference recipient, AmmoType type,
        int raw, out int rounds, out string code)
    {
        rounds = 0;
        code = ResultCode.AmmoUnitUnknown;
        if (!adapter.TryCostOfBullet(recipient, type, out var cost)) return false;
        var bullets = raw / cost;
        // Below one whole bullet the amount would floor to a zero-round write that still reports `committed`.
        if (!double.IsFinite(bullets) || bullets < 1d || bullets > MaximumAmount) return false;
        rounds = (int)Math.Floor(bullets);
        code = "";
        return true;
    }

}
