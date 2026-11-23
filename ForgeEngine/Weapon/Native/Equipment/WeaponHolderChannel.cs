using System;
using ForgeEngine.GameBindings;
using ForgeEngine.Framework;
using Gear;
using HarmonyLib;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of the holder-client command channel: who holds an equipment instance, and the two writes the
/// holder's own machine performs.
///
/// The magazine writes use the game's own entry points and nothing else. A reload is
/// <see cref="PlayerInventoryBase.TriggerReload"/>, the same body the player's own reload key reaches, so a
/// Forge-requested reload is indistinguishable from a pressed one and the clip transfer, the animation and the
/// replication are the game's. A clip write is <see cref="BulletWeapon.SetCurrentClip"/>, the setter the game's
/// own reload and pickup paths use, capped against the weapon's own <c>GetMaxClip</c> rather than a number this
/// package invents.
///
/// Build 20403457's external-fire method is empty. The fire adapter therefore supplies one exact instance's
/// input to <c>BulletWeaponArchetype.Update</c>; the native state machine owns its gates, charge, shot, cost and
/// replication. A delayed native request has an unknown result until a real shot is observed.
///
/// These bodies run on the machine that owns the inventory, which is exactly why the rows declare the `owner`
/// tier: the host sends the intent, the holder executes it here, and the game's replication carries the result.
/// <see cref="HolderOf"/> answers which player holds this instance; ForgePlayer's player directory, which every
/// write here is handed, answers which session that player is on and whether it sits at this machine. Neither one
/// guesses an answer it does not have, and the equipment comparison is the package's own identity table rather than
/// a pointer, so a request can only ever reach the instance it named.
/// </summary>
internal static class WeaponHolderChannel
{
    private static EquipmentNativeAdapter? _adapter;

    /// <summary>The live adapter, wired by <see cref="WeaponNativeSession.Start"/>. Null before the session is
    /// built, which is what keeps a resolver or a handler from running over a half-built package.</summary>
    internal static EquipmentNativeAdapter? Adapter
    {
        get => _adapter;
        set
        {
            if (!ReferenceEquals(_adapter, value)) StopFire(ResultCode.HolderSessionEnded);
            _adapter = value;
        }
    }

    /// <summary>Whether this machine is inside a running game this package may write to. It is the same gate the
    /// package's other writers use, handed in by the session rather than read from the kernel here.</summary>
    internal static Func<bool> CanExecute { get; set; } = () => false;

    /// <summary>
    /// Which native player holds one equipment instance. This is the package's own knowledge; which session that
    /// player is on — the answer the host's `owner` dispatch needs — and whether it is this machine's local player
    /// are ForgePlayer's, asked of its player directory by the caller.
    ///
    /// The reference is resolved against this package's own two tables. A deployed world instance carries its
    /// live native item in the placement table, and that item's own owner chain (item to agent to player) is the
    /// game's answer, not a cached one. A held equipment life resolves through the adapter's current equippable
    /// table and that instance's owner, without a second player table.
    ///
    /// Null is a real answer (a recalled or destroyed placement, a machine with no running game) and the tier
    /// turns it into a named refusal rather than sending the step somewhere. An identity nobody can answer for is
    /// never a fallback to "local": handing an unknown device to whichever machine received the request is exactly
    /// the wrong-inventory write this channel exists to prevent.
    /// </summary>
    internal static SNet_Player? HolderOf(EntityReference equipment)
    {
        var adapter = Adapter;
        if (adapter == null || equipment == null || !CanExecute()) return null;
        // A world instance this machine placed: the placement table answers the item it spawned, and that item's
        // own owner chain is the game's answer to who holds it.
        if (adapter.PlacedItemOf(equipment) is { } placed) return placed.Owner == null ? null : placed.Owner.Owner;
        // The other shape a plan addresses is the equipment life itself — the backpack slot the instance lives in.
        // The adapter's own identity and slot table is the authority for that: it answers only for a reference of
        // this world whose recorded handle, backpack pointer and slot still agree with the backpack, so a life that
        // ended, moved or was replaced in its slot answers null rather than the slot it used to be in.
        return adapter.EquippableOf(equipment) is { } equippable ? equippable.Backpack.Owner : null;
    }

    /// <summary>The local player's own inventory, or null when this machine has no local player. Every write in
    /// this file goes through it, so a machine that is not the holder writes nothing.</summary>
    private static PlayerInventoryBase? LocalInventory(IPlayerDirectory players)
    {
        var agent = players.LocalAgent;
        return agent == null ? null : agent.Inventory;
    }

    /// <summary>
    /// The reload write: the game's own reload entry point on this machine's inventory, reached only when the
    /// instance the request names is the item this inventory is holding and the inventory's own check accepts a
    /// reload. The original item's <c>IsReloading</c> flag is read back after the body ran, and the clip is read
    /// from the weapon at that moment, so the answer reports a reload the game actually opened and the magazine it
    /// held as the reload began (the refill lands later, when the game's reload time ends).
    ///
    /// `speed_multiplier` is the reload row's own modifier: absent means the reload takes the weapon's own time. It is
    /// written into the instance's own pending slot before the game's reload entry point runs, and the game's own
    /// `ReloadTime` getter is what divides it (<see cref="ReloadSpeedSlots"/>), so the scaled duration is what the whole
    /// reload runs under — no block is copied, no pointer is swapped, and nothing else reading the instance sees a
    /// substituted block. A reload the game did not open has no life whose ending would drop the multiplier, so it is
    /// dropped again here; the reload's own end, a weapon switch and the session end are the three points that drop it
    /// otherwise. A request that names no multiplier takes the path this row always had.
    /// </summary>
    internal static bool TryReload(IPlayerDirectory players, EntityReference equipment, bool hasSpeedMultiplier, double speedMultiplier,
        out int clip, out string code)
    {
        clip = 0;
        code = ResultCode.ReloadRefused;
        var inventory = LocalInventory(players);
        if (inventory == null) return false;
        var weapon = Wielded(inventory);
        if (weapon == null || !Matches(weapon, equipment)) return false;
        var equippable = weapon.TryCast<ItemEquippable>();
        if (equippable == null) { code = ResultCode.ClipPolicyUnsupported; return false; }
        // A true flag before this request is another reload's life, not evidence that this request opened one.
        if (equippable.IsReloading || !inventory.CanReloadCurrent()) return false;
        var shooter = weapon.TryCast<BulletWeapon>();
        if (!hasSpeedMultiplier)
        {
            try { inventory.TriggerReload(); }
            catch (Exception failure)
            {
                NativeCallFailure.Note("WeaponHolderChannel.TryReload", failure);
                code = ResultCode.NativeCommitException;
                return false;
            }
            clip = shooter?.GetCurrentClip() ?? 0;
            // TriggerReload has native early-return gates after CanReloadCurrent. Build 20403457's accepted
            // ItemEquippable animation path sets IsReloading synchronously before it returns, so this flag is the
            // readback that distinguishes a started reload from a refused request.
            if (!equippable.IsReloading) return false;
            code = "";
            return true;
        }
        if (shooter == null) { code = ResultCode.ClipPolicyUnsupported; return false; }
        // A multiplier the getter cannot divide by is not a reload this row can carry: zero or a negative value
        // would make the duration infinite or negative, and one no float holds is not a duration either.
        if (!double.IsFinite(speedMultiplier) || speedMultiplier <= 0d || speedMultiplier > float.MaxValue)
        {
            code = ResultCode.ClipPolicyUnsupported;
            return false;
        }
        if (!ReloadSpeedSlots.Arm(shooter, speedMultiplier))
        {
            // The table holds one pending multiplier per instance the process can have a reload in flight for;
            // a full one means this machine is already carrying more than that, so the request is refused rather
            // than served without the port it named.
            code = ResultCode.ReloadRefused;
            return false;
        }
        try { inventory.TriggerReload(); }
        catch (Exception failure)
        {
            // The entry point did not return, so no reload life was opened that could end and drop the
            // multiplier; it is dropped here rather than left armed for whatever reload comes next.
            ReloadSpeedSlots.Clear(shooter.Pointer);
            NativeCallFailure.Note("WeaponHolderChannel.TryReload#scaled", failure);
            code = ResultCode.NativeCommitException;
            return false;
        }
        // The game sets the item's own flag inside its own entry point, and the reload observer reads the same
        // flag to decide a reload is under way. A reload the game did not open is therefore dropped at once.
        if (!shooter.IsReloading)
        {
            ReloadSpeedSlots.Clear(shooter.Pointer);
            return false;
        }
        clip = shooter.GetCurrentClip();
        code = "";
        return true;
    }

    /// <summary>
    /// Supply one native press and held input until its first shot. All actual shooting remains inside Update.
    /// Charge continues through subsequent native updates; it is not fast-forwarded or simulated by Forge.
    /// A confirmed result requires both the original clip and native replication-counter readbacks.
    /// </summary>
    internal static bool TryFire(IPlayerDirectory players, EntityReference equipment, string requiredState, out string code,
        Action<string>? report = null)
    {
        code = ResultCode.FireRefused;
        if (!CanExecute()) return false;
        var agent = players.LocalAgent;
        var inventory = agent == null ? null : agent.Inventory;
        if (inventory == null) return false;
        var weapon = Wielded(inventory);
        var addressed = Adapter?.EquippableOf(equipment);
        if (addressed == null || WeaponNativeSession.Current is not { } session)
        { code = ResultCode.StaleEntity; return false; }
        if (weapon == null || weapon.Pointer != addressed.Value.Item.Pointer) return false;
        if (!session.Identity.IsCurrent(equipment))
        { code = ResultCode.StaleEntity; return false; }
        if (WeaponNativeSession.Offer(WeaponFeature.Fire) == null)
        { code = ResultCode.FireInputUnavailable; return false; }
        var shooter = weapon.TryCast<BulletWeapon>();
        if (shooter == null || shooter.TryCast<BulletWeaponSynced>() != null
            || shooter.m_archeType == null || shooter.m_archeType.Pointer == IntPtr.Zero
            || agent!.Sync == null || agent.FPItemHolder == null) return false;
        if (agent.FPItemHolder.ItemIsBusy) { code = ResultCode.FireRefusedBusy; return false; }
        if (!StateHolds(agent, shooter, requiredState)) return false;
        if (_fireRequest != null || _nativeUpdateDepth != 0) { code = ResultCode.FireRequestActive; return false; }
        // The input hooks run every frame the game reads a fire button, so they go on with the first request.
        if (WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.FireInput) != null
            || WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.ArchetypeUpdate) != null)
        { code = ResultCode.FireInputUnavailable; return false; }
        var request = new FireRequest(equipment, shooter, agent, inventory, report);
        _fireRequest = request;
        try { request.Archetype.Update(); }
        catch (Exception failure)
        {
            // An invoked native update cannot promise no commit: the caller answers failed/unknown with this code.
            NativeCallFailure.Note("WeaponHolderChannel.TryFire", failure);
            StopFire(ResultCode.FireNativeUpdateThrew);
            code = ResultCode.FireNativeUpdateThrew;
            return false;
        }
        code = request.Code;
        return code.Length == 0;
    }

    // One process has one local wielded weapon. The request and its native handles are resolved once;
    // its input/readback hooks do no dictionary or string lookup and allocate nothing per update.
    internal sealed class FireRequest
    {
        internal readonly EntityReference Equipment;
        internal readonly BulletWeapon Weapon;
        internal readonly BulletWeaponArchetype Archetype;
        internal readonly PlayerAgent Agent;
        internal readonly PlayerInventoryBase Inventory;
        internal readonly PlayerSync Sync;
        internal readonly int Clip, FireCount;
        internal readonly Action<string>? Report;
        internal bool Updating, Press = true;
        internal string Code = ResultCode.FireRequestPending;

        internal FireRequest(EntityReference equipment, BulletWeapon weapon, PlayerAgent agent,
            PlayerInventoryBase inventory, Action<string>? report)
        {
            Equipment = equipment; Weapon = weapon; Archetype = weapon.m_archeType!; Agent = agent;
            Inventory = inventory; Sync = agent.Sync; Report = report;
            Clip = weapon.GetCurrentClip(); FireCount = Sync.m_fireCountSyncRef;
        }
    }

    private static FireRequest? _fireRequest;

    /// <summary>The request a first native update left waiting (a charge before the shot); null once it settled or was stopped.</summary>
    internal static FireRequest? PendingFire => _fireRequest;
    private static int _nativeUpdateDepth;

    internal static FireRequest? BeginFireUpdate(BulletWeaponArchetype archetype)
    {
        _nativeUpdateDepth++;
        var request = _fireRequest;
        if (request == null || request.Archetype.Pointer != archetype.Pointer || request.Updating) return null;
        if (!CanExecute() || request.Inventory.WieldedItem == null
            || request.Inventory.WieldedItem.Pointer != request.Weapon.Pointer
            || request.Weapon.m_archeType == null || request.Weapon.m_archeType.Pointer != archetype.Pointer
            || request.Agent.Sync == null || request.Agent.Sync.Pointer != request.Sync.Pointer
            || request.Agent.FPItemHolder == null)
        { StopFire(ResultCode.FireHolderChanged); return null; }
        if (request.Agent.FPItemHolder.ItemIsBusy)
        { StopFire(ResultCode.FireRefusedBusy); return null; }
        request.Updating = true;
        return request;
    }

    internal static bool FireInput(IntPtr weapon, bool pressed)
    {
        var request = _fireRequest;
        if (request == null || !request.Updating || request.Weapon.Pointer != weapon) return false;
        if (!CanExecute()) { StopFire(ResultCode.ReadbackUnconfirmed); return false; }
        if (request.Agent.FPItemHolder == null) return false;
        if (request.Agent.FPItemHolder.ItemIsBusy) { StopFire(ResultCode.FireRefusedBusy); return false; }
        if (!pressed) return true;
        var value = request.Press;
        request.Press = false;
        return value;
    }

    internal static void EndFireUpdate(BulletWeaponArchetype archetype, FireRequest? request, Exception? exception)
    {
        try
        {
            if (request == null)
            {
                // Harmony also finalizes a prefix that threw before it could assign __state.
                if (exception != null && _fireRequest?.Archetype.Pointer == archetype.Pointer)
                    StopFire(ResultCode.FireNativeUpdateThrew);
                return;
            }
            request.Updating = false;
            if (!ReferenceEquals(_fireRequest, request)) return;
            if (exception != null) { StopFire(ResultCode.FireNativeUpdateThrew); return; }
            if (WeaponNativeSession.Current is not { Inert: false } || !CanExecute())
            { StopFire(ResultCode.ReadbackUnconfirmed); return; }
            try
            {
                var clip = request.Weapon.GetCurrentClip();
                var shots = unchecked(request.Sync.m_fireCountSyncRef - request.FireCount);
                if (shots == 1 && (clip == request.Clip - 1 || clip == request.Clip && Weapon.InfiniteAmmo))
                { request.Code = ""; _fireRequest = null; return; }
                // The observed numbers travel with the code: a shot the clip did not pay for and a second shot in one
                // update are different native behaviours, and only the readback itself can tell them apart.
                if (shots != 0 || clip != request.Clip)
                {
                    StopFire(ResultCode.ReadbackUnconfirmed, " shots=" + shots + " clipBefore=" + request.Clip + " clipAfter=" + clip);
                    return;
                }
                if (!request.Archetype.m_inChargeup && !request.Archetype.Firing)
                    StopFire(NativeFireRefusal(request.Agent, request.Weapon) ?? ResultCode.FireRefused);
            }
            catch (Exception failure) { NativeCallFailure.Note("WeaponHolderChannel.EndFireUpdate", failure); StopFire(ResultCode.ReadbackUnconfirmed); }
        }
        finally { if (_nativeUpdateDepth > 0) _nativeUpdateDepth--; }
    }

    internal static void StopFireFor(EntityReference equipment)
    {
        if (_fireRequest?.Equipment == equipment) StopFire(ResultCode.StaleEntity);
    }

    private static string? NativeFireRefusal(PlayerAgent agent, BulletWeapon weapon)
    {
        if (agent.FPItemHolder?.ItemIsBusy == true) return ResultCode.FireRefusedBusy;
        if (weapon.IsReloading) return ResultCode.FireRefusedReload;
        // An empty magazine is the weapon's own dry click: the native empty-clip path arms the next-shot deadline
        // itself, so the cooldown read afterwards is the click's effect and not what refused the shot.
        if (weapon.GetCurrentClip() <= 0 && !Weapon.InfiniteAmmo) return ResultCode.FireRefusedEmpty;
        return CoolingDown(weapon) ? ResultCode.FireRefusedCooldown : null;
    }

    /// <summary>Native Update admits a shot only after both of these Clock.Time deadlines.</summary>
    internal static bool CoolingDown(BulletWeapon weapon)
        => weapon.m_archeType is { } archetype
            && (Clock.Time <= archetype.m_nextShotTimer || Clock.Time <= archetype.m_nextBurstTimer);

    internal static void ObserveFireLifecycle(RuntimeLifecycleEvent value)
    {
        if (_fireRequest == null) return;
        if (value.Kind == RuntimeLifecycleKind.CheckpointRestored
            || value.Current.WorldEpoch != _fireRequest.Equipment.WorldEpoch
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            StopFire(ResultCode.FireLifetimeEnded);
    }

    internal static void StopFire(string code, string observed = "")
    {
        var request = _fireRequest;
        _fireRequest = null;
        if (request == null) return;
        request.Updating = false;
        request.Code = code;
        try { request.Report?.Invoke("weapon.holder-fire-ended equipment=" + request.Equipment.Id + " code=" + code + observed); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponHolderChannel.StopFire", failure); /* Diagnostics must not keep synthetic input held. */ }
    }

    /// <summary>Whether the weapon is in the state a fire request named. An unknown member is a state this build
    /// does not have, which is refused rather than treated as `none`.</summary>
    private static bool StateHolds(PlayerAgent? agent, BulletWeapon weapon, string requiredState)
    {
        if (string.Equals(requiredState, WeaponHolderActionsContract.StateNone, StringComparison.Ordinal)) return true;
        var holder = agent == null ? null : agent.FPItemHolder;
        if (string.Equals(requiredState, WeaponHolderActionsContract.StateAiming, StringComparison.Ordinal))
            return holder != null && holder.ItemAimTrigger;
        if (string.Equals(requiredState, WeaponHolderActionsContract.StateCharging, StringComparison.Ordinal))
            return weapon.m_archeType != null && weapon.m_archeType.m_inChargeup;
        return false;
    }

    /// <summary>
    /// The clip write: the weapon's own setter, on the instance the request names, with the cap the weapon itself
    /// reports. A count above that cap is refused instead of clamped, because a plan that asked for more bullets
    /// than the magazine holds asked for a state the weapon cannot be in; `fill` asks for the cap itself and needs
    /// no amount.
    ///
    /// `overflow_policy` decides what the capacity means: `clamp` writes the capacity where the request asked for
    /// more, `exceed` writes the count as asked and leaves the magazine above its own capacity, and an absent
    /// policy keeps this row's original refusal. `source_pool` decides where the rounds come from: `reserve`
    /// limits the gain to the rounds the player's own pack holds and deducts exactly those from the pack, which is
    /// the pool-to-magazine transfer the game's own reload performs; an absent policy or `none` writes the
    /// magazine alone. A reserve transfer is a write to this machine's own pack, so it is performed on the
    /// machine that holds the weapon — the tier the row already declares.
    /// </summary>
    internal static bool TrySetClip(IPlayerDirectory players, EntityReference equipment, string policy, bool hasAmount, int amount,
        string overflowPolicy, string sourcePool, out int clip, out string code, out bool written)
    {
        clip = 0;
        written = false;
        code = ResultCode.StaleEntity;
        var local = players.LocalAgent;
        var addressed = Adapter?.EquippableOf(equipment);
        if (local == null || addressed == null) return false;
        var owner = addressed.Value.Backpack.Owner;
        var localReference = players.ReferenceOf(local);
        if (localReference == null || players.ReferenceOf(owner) != localReference
            || !players.IsLocal(players.SessionOf(owner))) return false;
        var weapon = addressed.Value.Item.TryCast<BulletWeapon>();
        if (weapon == null) { code = ResultCode.ClipPolicyUnsupported; return false; }
        var capacity = weapon.GetMaxClip();
        int wanted;
        switch (policy)
        {
            case "set":
                if (!hasAmount) { code = ResultCode.ClipPolicyUnsupported; return false; }
                wanted = amount;
                break;
            case "fill":
                wanted = capacity;
                break;
            default:
                code = ResultCode.ClipPolicyUnsupported;
                return false;
        }
        if (wanted < 0) { code = ResultCode.ClipPolicyUnsupported; return false; }
        if (sourcePool is not ("" or "none" or "reserve"))
        { code = ResultCode.ClipPolicyUnsupported; return false; }
        switch (overflowPolicy)
        {
            case "clamp":
                wanted = Math.Min(wanted, capacity);
                break;
            case "exceed":
                break;
            case "":
            case null:
                if (wanted > capacity) { code = ResultCode.ClipOverCapacity; return false; }
                break;
            default:
                code = ResultCode.ClipPolicyUnsupported;
                return false;
        }
        var current = weapon.GetCurrentClip();
        var fromReserve = 0;
        SNet_Player? reserveOwner = null;
        PlayerAmmoStorage? reserveStorage = null;
        var reserveBefore = 0;
        if (string.Equals(sourcePool, "reserve", StringComparison.Ordinal))
        {
            // Capacity is settled before the source is billed. Computing the transfer from the pre-clamp request
            // would charge rounds the magazine never received.
            if (wanted < current) { code = ResultCode.ClipPolicyUnsupported; return false; }
            if (!TryReserve(players, weapon.AmmoType, out reserveOwner, out reserveStorage, out reserveBefore, out code))
                return false;
            fromReserve = Math.Min(wanted - current, reserveBefore);
            if (fromReserve <= 0) { code = ResultCode.ClipReserveEmpty; return false; }
            wanted = current + fromReserve;
        }
        // The ammo display refresh needs the weapon's own inventory slot, so the storage and slot are resolved before
        // anything is written. `SetClipAmmoInSlot` is not called: it is the reload's own move of rounds from the reserve
        // into the magazine, so calling it here would take a magazine from the reserve again.
        var slot = weapon.ItemDataBlock?.inventorySlot;
        var syncStorage = slot == null ? null : LocalAmmoStorage(players);
        if (slot == null || syncStorage == null) { code = ResultCode.StaleEntity; return false; }
        written = true;
        try { weapon.SetCurrentClip(wanted); }
        catch (Exception failure)
        {
            // The setter threw part-way; `written` is already true, so the caller answers failed/unknown.
            NativeCallFailure.Note("WeaponHolderChannel.TrySetClip", failure);
            code = ResultCode.NativeCommitException;
            return false;
        }
        clip = weapon.GetCurrentClip();
        if (clip != wanted)
        {
            // An unchanged magazine is a proven refusal. Any other value is an observed partial/foreign write: the
            // commit is unknown and the caller answers failed/unknown with this code.
            code = clip == current ? ResultCode.ClipReadback : ResultCode.ClipReadbackPartial;
            return false;
        }
        // The write is committed and verified; the game's remaining calls after a magazine write finish it. A
        // failure here leaves the commit unknown, like the reserve half below.
        try
        {
            syncStorage.UpdateSlotAmmoUI(slot.Value);
        }
        catch (Exception error)
        {
            NativeCallFailure.Note("WeaponHolderChannel.TrySetClip#sync", error);
            code = ResultCode.ClipSyncFailed;
            return false;
        }
        // The refresh must not have moved the magazine again.
        if (weapon.GetCurrentClip() != wanted)
        {
            code = ResultCode.ClipReadbackPartial;
            return false;
        }
        if (fromReserve > 0 && !SpendReserve(reserveOwner!, reserveStorage!, weapon.AmmoType, reserveBefore, fromReserve))
        {
            code = ResultCode.ClipReserveReadback;
            return false;
        }
        code = "";
        return true;
    }

    /// <summary>Whether a refusal of <see cref="TrySetClip"/> leaves the commit unknown (answered failed/unknown): only a
    /// failure after the magazine write, other than the unchanged magazine that proves the write was refused. A failure
    /// before the write (the reserve read included) wrote nothing and is a rejection.</summary>
    internal static bool IsCommitUnknown(string code, bool written)
        => written && code != ResultCode.ClipReadback;

    /// <summary>This machine's own player. The agent is a Unity object, so it is tested with == (which sees a
    /// destroyed one) rather than read through with ?..</summary>
    private static SNet_Player? LocalOwner(IPlayerDirectory players)
    {
        var agent = players.LocalAgent;
        return agent == null ? null : agent.Owner;
    }

    /// <summary>This machine's own player's ammunition storage, or null when its backpack cannot be read.</summary>
    private static PlayerAmmoStorage? LocalAmmoStorage(IPlayerDirectory players)
    {
        var player = LocalOwner(players);
        if (player == null) return null;
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(player, out var backpack) || backpack == null) return null;
            var storage = backpack.AmmoStorage;
            return storage == null || storage.Pointer == IntPtr.Zero ? null : storage;
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponHolderChannel.LocalAmmoStorage", failure); return null; }
    }

    /// <summary>
    /// Resolves and reads the reserve before either half of the transfer is submitted. Every false result is still
    /// a true refusal: no magazine or reserve write has happened yet.
    /// </summary>
    private static bool TryReserve(IPlayerDirectory players, AmmoType type, out SNet_Player? player,
        out PlayerAmmoStorage? storage, out int reserve, out string code)
    {
        player = LocalOwner(players);
        storage = null;
        reserve = 0;
        code = ResultCode.ClipReserveReadback;
        if (player == null) return false;
        PlayerBackpack? backpack;
        try { if (!PlayerBackpackManager.TryGetBackpack(player, out backpack) || backpack == null) return false; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponHolderChannel.TryReserve", failure); return false; }
        storage = backpack.AmmoStorage;
        if (storage == null || storage.Pointer == IntPtr.Zero) return false;
        try { reserve = ReserveBullets(storage, type, player); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponHolderChannel.TryReserve#2", failure); return false; }
        if (reserve < 0) return false;
        code = "";
        return true;
    }

    /// <summary>The reserve in whole bullets. The pool's raw value is divided by its own bullet cost with the same
    /// float tolerance the ammunition rows use, because the game's floored count reads N-1 for a pool that holds
    /// exactly N after float arithmetic; a pool that cannot be read raw falls back to the manager's count.</summary>
    private static int ReserveBullets(PlayerAmmoStorage storage, AmmoType type, SNet_Player player)
    {
        var pool = storage.GetInventorySlotAmmo(type);
        if (pool != null && pool.Pointer != IntPtr.Zero)
        {
            var raw = pool.AmmoInPack;
            var cost = pool.CostOfBullet;
            if (float.IsFinite(raw) && float.IsFinite(cost) && cost > 0f)
                return WeaponSupplyAdapter.WholeBullets(raw, cost);
        }
        return PlayerBackpackManager.GetBulletsInPack(type, player);
    }

    /// <summary>Commits the reserve half and reads it back by the same whole-bullet count it was priced in. False is
    /// an unknown commit, not a rejection: an exception or a count that is not exactly the spent rounds leaves the
    /// outcome unproven, and the caller answers failed/unknown with no success fact. There is no atomic native
    /// clip/reserve transaction in this build, so this never guesses that a failed read means the reserve stayed
    /// unchanged and never rolls the already written magazine back over a possibly completed deduction.</summary>
    private static bool SpendReserve(SNet_Player player, PlayerAmmoStorage storage, AmmoType type, int before, int rounds)
    {
        try
        {
            storage.UpdateBulletsInPack(type, -rounds);
            return ReserveBullets(storage, type, player) == before - rounds;
        }
        catch (Exception error) { NativeCallFailure.Note("WeaponHolderChannel.SpendReserve", error); return false; }
    }

    /// <summary>The item this inventory is holding, or null when nothing is wielded. Read through the inventory's
    /// own property rather than a scroll slot, so a request can never reach a weapon the holder does not have in
    /// hand.</summary>
    private static Item? Wielded(PlayerInventoryBase inventory)
    {
        var item = inventory.WieldedItem;
        return item == null ? null : item.TryCast<Item>();
    }

    /// <summary>Whether a native item is the equipment instance a request named. The comparison is the identity
    /// table's, not the pointer's: the reference was minted for one equipment life, and the item in hand has to be
    /// the instance that life currently points at.</summary>
    private static bool Matches(Item item, EntityReference equipment)
    {
        var adapter = Adapter;
        if (adapter == null) return false;
        var current = adapter.EntityOf(item);
        return current != null && current == equipment;
    }
}

[HarmonyPatch(typeof(ItemEquippable), "get_FireButton")]
internal static class HolderFireHeld
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ItemEquippable __instance, ref bool __result)
    {
        if (WeaponNativeSession.Current is { Inert: false })
            __result |= WeaponHolderChannel.FireInput(__instance.Pointer, pressed: false);
    }
}

[HarmonyPatch(typeof(ItemEquippable), "get_FireButtonPressed")]
internal static class HolderFirePressed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ItemEquippable __instance, ref bool __result)
    {
        if (WeaponNativeSession.Current is { Inert: false })
            __result |= WeaponHolderChannel.FireInput(__instance.Pointer, pressed: true);
    }
}

/// <summary>The archetype's per-frame update. The prefix opens the holder's fire update, then reads whether the
/// ranged charge was running; the postfix publishes the ranged charge (<c>CombatFactsObserver.RangedRan</c>),
/// and the finalizer closes the fire update. A Harmony patch body is named for the patch point it is, and the patch
/// point here is the body the game already runs — this package drives no loop of its own. The priorities keep the
/// charge reading after every other patch of the same body, so what is read is the state the game finished deciding.</summary>
[HarmonyPatch(typeof(BulletWeaponArchetype), nameof(BulletWeaponArchetype.Update))]
internal static class ArchetypeUpdateHooks
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(BulletWeaponArchetype __instance, out (WeaponHolderChannel.FireRequest? Fire, bool Charging) __state)
    {
        __state = default;
        // Always pair the update depth with the finalizer, including an inert session or a throwing prefix.
        if (WeaponNativeSession.Current is not { Inert: false }) WeaponHolderChannel.StopFire(ResultCode.ReadbackUnconfirmed);
        __state.Fire = WeaponHolderChannel.BeginFireUpdate(__instance);
        // WHY: F8, the flag before the update travels in Harmony's own __state, so no shared table is written per frame.
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponUpdate);
        __state.Charging = __instance != null && CombatFactsObserver.RangedCharging(__instance);
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(BulletWeaponArchetype __instance, (WeaponHolderChannel.FireRequest? Fire, bool Charging) __state)
    {
        using var measured = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponUpdate);
        if (__instance == null) return;
        // WHY: P5 - an archetype neither charging nor just stopped has nothing to publish; `RangedRan` returns at this same test.
        if (!__state.Charging && !__instance.m_inChargeup) return;
        WeaponNativeSession.Offer(WeaponFeature.Fire)?.Guard(static (_, call) => CombatFactsObserver.Current?.RangedRan(call.Item1, call.Item2),
            (__instance, __state.Charging));
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static void Finalizer(BulletWeaponArchetype __instance, Exception? __exception, (WeaponHolderChannel.FireRequest? Fire, bool Charging) __state)
        => WeaponHolderChannel.EndFireUpdate(__instance, __state.Fire, __exception);
}
