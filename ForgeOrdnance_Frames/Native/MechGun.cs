using System;
using GameData;
using Gear;
using Player;
using UnityEngine;
using LoanedGun = ForgeCore.LoanedGun;

namespace ForgeOrdnanceFrames;

/// <summary>The suit gun and the pilot's own guns, on the pilot's own machine.
/// <para>The suit gun is the model's game gear (<see cref="WeaponSpec.Gear"/>, an offline gear block of a special-slot weapon) put into
/// the special slot through the game's own inventory path (<c>PlayerBackpackManager.EquipLocalGear</c>, which replicates to every peer),
/// under the game weapon's own name. It and the pilot's own special weapon take turns in that slot (<see cref="SuitGunLoan"/>): boarding
/// brings the suit gun in, the special weapon's select key pressed while that slot is held swaps them, an empty suit gun goes out for
/// good, and every way of leaving the suit ends with the pilot's own weapon back. Each keeps its magazine and reserve while the other is
/// in the slot. The suit gun is told apart by its instance in the slot (not by its name or gear, which may equal the pilot's own).
/// The suit's stock lives in its record (host-owned; reports may only lower it): the gun is loaded with at most the gear's clip, the
/// rest goes to the reserve (rounds beyond the game's pack capacity wait here and top the pack up as it drains).</para>
/// <para>The slot is shared through ForgeCore's <see cref="ForgeCore.Gear.SpecialSlot"/>: boarding with a lent heavy weapon (another
/// plugin's lease) waits until its owner has put it away, the pilot's own weapon is the one kept there, and leaving gives the lease
/// back once the own weapon is in the slot, so the heavy weapon's owner takes it back.</para>
/// <para>Ammunition pickups: the primary and the tool fill through the game as always; special ammunition goes to the pilot's own
/// weapon, through the game while it is in the slot, into its kept stock otherwise (ForgeCore's one pickup redirect). The suit gun
/// is never refilled. A medical pack heals the pilot's own health as always and never the suit's armour.</para>
/// <para>Handling (<see cref="GunHandling"/>): while piloting, whatever gun the pilot holds kicks less and reloads faster; the suit gun
/// alone hits harder and, planted, fires faster.</para>
/// <para>The game assembles a new gear instance asynchronously after <c>EquipLocalGear</c> while the slot already names the new
/// gear, so a swap is done only once the slot holds a different instance of the asked-for gear: ammunition written into the old
/// instance would be lost. The instance stands in the slot a frame after the ask, but its parts come one per frame from the game's
/// part spawner coroutine (<see cref="ForgeCore.GearAssembly"/>), which kept the new gun unloaded, unheld and empty for a dozen frames (0.27 to
/// 0.49 s measured). The spawner of the new gun's own part holder is finished at once in the frame the instance stands in the slot, and
/// the rounds are written in that same frame. The slot's gear, weapon and model, and the swap's timing, are logged after each swap.</para></summary>
internal static class MechGun
{
    private const float SwapTimeout = 8f, SettleSeconds = 1.5f, SlotWait = 3f;

    private static readonly SuitGunLoan Loan = new(ForgeCore.Gear.SpecialSlot, () => LocalPilot.Words.Piloting);
    private static FrameUnit? _unit;
    private static GearIDRange? _suitRange;
    private static uint _suitChecksum;
    // The checksum of the range the slot held last frame: read again only when the slot holds another range.
    private static GearIDRange? _summed;
    private static uint _sum;
    // The swap asked at boarding waits for an older lease (a heavy weapon) to be put away first.
    private static bool _waiting;
    // The special slot was not assembled at boarding (a fresh gear, a level start): boarding is tried again each frame until SlotWait ends.
    private static bool _retry;
    private static float _slotWaitUntil;
    // The suit gun's instance while it is settled in the slot (its identity), and its live stock.
    private static IntPtr _gun;
    private static int _overflow, _clip, _pack;
    private static bool _reloading;
    private static float _settleUntil;
    // The swap in flight: when it was asked, whether the game was asked, and the slot's instance at that moment.
    private static float _swapStarted;
    private static bool _sent;
    private static IntPtr _swappedFrom;
    // An orderly leave (<see cref="End"/>): the pilot cannot act until the landing, so the own weapon's parts come at the game's own pace,
    // one per frame, instead of every part spawned in the frame its instance stands in the slot.
    private static bool _paced;
    // The swap key was named this ride.
    private static bool _swapHinted;

    /// <summary>The suit gun is settled in the special slot.</summary>
    internal static bool SuitInSlot => _gun != IntPtr.Zero;

    /// <summary>Nothing to advance: no suit gun, no swap in flight, no suit worn and no recoil copy held (so <see cref="Tick"/> would only return).</summary>
    internal static bool Idle => !_waiting && !_retry && _gun == IntPtr.Zero && Loan.Pending == null && !Loan.Piloting && GunSteady.Idle;

    /// <summary>The suit gun is in the slot (for the cockpit's magazine arc).</summary>
    internal static bool Active => SuitInSlot;

    internal static FrameGunReading Reading => new(SuitInSlot, _clip, _pack + _overflow, _reloading, ReloadProgress(Time.time));

    /// <summary>The suit gun's stock right now (clip, reserve including rounds waiting beyond the pack), for the host report.</summary>
    internal static int Clip => _clip;
    internal static int Reserve => _pack + _overflow;
    /// <summary>The suit gun's magazine size (the game's own), for the cockpit's magazine arc; 0 before the first loan.</summary>
    internal static int ClipSize { get; private set; }

    /// <summary>The rounds of the gun on its way into the special slot while a swap is pending (the cockpit shows them from the first frame,
    /// not the empty gun the game assembles first), or null when none is.</summary>
    internal static SlotRounds? Incoming(int suitStock)
    {
        {
            if (Loan.Pending == null) return null;
            var cost = LoanedGun.TryLocalSpecial(out var backpack, out _) ? backpack!.AmmoStorage.SpecialAmmo.CostOfBullet : 0f;
            return SuitGunStock.Incoming(Loan, ClipSize, cost, suitStock);
        }
    }

    /// <summary>Whether <paramref name="item"/> is the suit gun settled in the local player's special slot.</summary>
    internal static bool IsSuitGun(Item? item)
        => _gun != IntPtr.Zero && item != null && !item.WasCollected && item.Pointer == _gun;

    /// <summary>Whether the suit's handling applies to <paramref name="item"/>: a suit is worn and it is one of the local player's guns.</summary>
    private static bool Handled(ItemEquippable? item)
        => Loan.Piloting && _unit != null && item != null && !item.WasCollected && item.Owner != null && item.Owner.IsLocallyOwned;

    /// <summary>Damage factor for hits of the local player's weapon <paramref name="item"/>: the suit gun's (<see cref="GunHandling.Damage"/>), 1 for every other gun.</summary>
    internal static float DamageMultiplier(ItemEquippable? item)
    {
        if (_unit == null || !IsSuitGun(item)) return 1f;
        var r = _unit.Record;
        return GunHandling.Damage(true, r.Spec.Weapon, r.Spec.Siege, r.Sieged);
    }

    /// <summary>The fire-rate factor for <paramref name="weapon"/>: the planted suit's for the suit gun, 1 otherwise.</summary>
    internal static float FireRate(BulletWeapon? weapon)
        => _unit != null && IsSuitGun(weapon) ? GunHandling.FireRate(true, _unit.Record.Spec.Siege, _unit.Record.Sieged) : 1f;

    /// <summary>The reload-time factor for <paramref name="weapon"/>: the suit's handling for any of the local pilot's guns, 1 otherwise.</summary>
    internal static float Reload(BulletWeapon? weapon)
        => Handled(weapon) ? GunHandling.Reload(_unit!.Record.Spec.Weapon, _unit.Record.Spec.Siege, _unit.Record.Sieged) : 1f;

    /// <summary>The recoil factor for any of the local pilot's guns while a suit is worn, 1 otherwise.</summary>
    private static float Recoil => _unit != null && Loan.Piloting ? GunHandling.Recoil(_unit.Record.Spec.Weapon, _unit.Record.Spec.Siege, _unit.Record.Sieged) : 1f;

    // The planted suit's faster fire: the clock that keeps the ideal time of the next shot, the archetype whose timers were last set
    // and the timer values left there (so a timer the game did not rewrite since is not moved twice), and the guard while this class
    // fires the extra shots through the game's own entry.
    private static readonly FireClock Clock = new();
    private static IntPtr _timedArchetype;
    private static float _shotTimerSeen, _burstTimerSeen;
    private static bool _firingExtra;
    // The faster reload, measured: the game's own reload time and the factor applied the last time the game asked, and when the
    // current reload started.
    private static float _reloadNative, _reloadFactor = 1f, _reloadStarted;

    /// <summary>After the game fired a shot of <paramref name="archetype"/> and set its next-shot (and next-burst) timer from the gun's own
    /// shot delay: while planted, <see cref="FireClock"/> keeps the ideal time of the next shot of the suit gun at the gun's delay divided
    /// by the fire-rate factor. Shots already due in this frame are fired at once through the game's own entry (its pre-fire check, its
    /// shot and its post-fire check: ammunition, recoil, sound and replication as for any shot), and the game's timer is set to the ideal
    /// time of the next one, so the rate is the target at any frame rate instead of rounding up to whole frames. A timer the game has not
    /// rewritten since the last call is left alone. The first second of each planted stream is measured and logged once per siege.</summary>
    internal static void AfterShot(BulletWeaponArchetype archetype)
    {
        if (_firingExtra) return;
        var rate = FireRate(archetype.m_weapon);
        if (rate <= 1f) { Clock.Stop(); return; }
        var same = archetype.Pointer == _timedArchetype;
        var shot = archetype.m_nextShotTimer;
        var burst = archetype.m_nextBurstTimer;
        if (!same || shot != _shotTimerSeen)
        {
            var delay = archetype.ShotDelay();
            // The game has just set its timer to its own time plus the gun's delay.
            var gameNow = shot - delay;
            var extra = Clock.AfterShot(gameNow, delay / rate, out var next);
            for (var i = 0; i < extra && CanFireAgain(archetype); i++)
            {
                _firingExtra = true;
                try { archetype.OnFireShot(); archetype.PostFireCheck(); }
                finally { _firingExtra = false; }
            }
            shot = next;
            archetype.m_nextShotTimer = shot;
        }
        if (!same || burst != _burstTimerSeen) { burst -= archetype.BurstDelay() * (1f - 1f / rate); archetype.m_nextBurstTimer = burst; }
        _timedArchetype = archetype.Pointer;
        _shotTimerSeen = shot;
        _burstTimerSeen = archetype.m_nextBurstTimer;
        MechGunSound.SiegeShot();
    }

    /// <summary>Whether the game would fire another shot now: still firing, rounds in the clip and its own pre-fire check passes.</summary>
    private static bool CanFireAgain(BulletWeaponArchetype archetype)
    {
        var weapon = archetype.m_weapon;
        return archetype.m_firing && weapon != null && !weapon.WasCollected && weapon.GetCurrentClip() > 0 && archetype.PreFireCheck();
    }

    /// <summary>The game asked for the reload time of the local pilot's gun <paramref name="weapon"/>: kept to log the reload when it ends.</summary>
    internal static void ReloadAsked(BulletWeapon weapon, float native, float factor)
    {
        _reloadNative = native;
        _reloadFactor = factor;
    }

    private static void ReloadChanged(bool reloading, float now)
    {
        if (reloading) { _reloadStarted = now; return; }
        if (_reloadFactor >= 1f || _reloadStarted <= 0f) return;
        _reloadStarted = 0f;
    }

    /// <summary>The current reload's progress 0..1 and whether one runs (for the HUD).</summary>
    internal static float ReloadProgress(float now)
    {
        if (!_reloading || _reloadStarted <= 0f) return -1f;
        var total = _reloadNative * Math.Min(1f, _reloadFactor);
        return total > 0f ? Math.Clamp((now - _reloadStarted) / total, 0f, 1f) : -1f;
    }

    /// <summary>The special weapon's select key was pressed while that slot is held: the other gun comes in (if it can).</summary>
    internal static void ToggleKey(float now)
    {
        if (!Loan.Piloting) return;
        var agent = ForgeCore.Players.Local;
        if (agent != null && FrameWorld.IsSeized(agent)) return;
        var ask = Loan.Toggle();
        if (ask == null)
        {
            if (Loan.SuitSpent && Loan.Pending == null) LocalPilot.Notice(LocalPilot.Words.GunEmpty);
            return;
        }
        Send(ask, now);
    }

    /// <summary>The pilot is in (piloted): the session opens and takes the special slot's lease; the suit gun comes in when it has rounds
    /// (otherwise the own weapon, if a lent heavy weapon stands in the slot), once an older lease has put its gun away.</summary>
    internal static void Begin(FrameUnit unit, float now)
    {
        if (Loan.Piloting) return;
        _paced = false;
        _unit = unit;
        _swapHinted = false;
        var r = unit.Record;
        var slotReadable = LoanedGun.TryLocalSpecial(out _, out var item) && item!.GearIDRange != null && item.IsLoaded;
        if (!slotReadable)
        {
            // Not assembled yet: wait for it (Tick tries again) and give up only when it never becomes readable.
            if (_slotWaitUntil == 0f) _slotWaitUntil = now + SlotWait;
            _retry = now < _slotWaitUntil;
            if (_retry) return;
        }
        _retry = false;
        _slotWaitUntil = 0f;
        // The slot's gear: the pilot's own weapon unless something else stands in it (the shared slot keeps the own weapon then).
        var slotJson = slotReadable ? item!.GearIDRange!.ToJSON() : null;
        var slotChecksum = slotReadable ? item!.GearIDRange!.GetChecksum() : 0u;
        string? failure = null;
        GearIDRange? range = null;
        var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(r.Spec.Weapon.Gear);
        if (!slotReadable) failure = "the special weapon slot is not ready";
        else if (block == null) failure = "offline gear block " + r.Spec.Weapon.Gear + " does not exist";
        else
        {
            range = new GearIDRange(block.GearJSON);
            var reading = ForgeCore.Gear.Prepare(range);
            if (reading.Status != ForgeCore.GearAssetStatus.Ready) failure = "gear " + r.Spec.Weapon.Gear + " cannot be assembled (" + reading.Detail + ")";
        }
        if (failure != null)
        {
            Plugin.Logger.ErrorOnce("Exosuit gun: " + failure + "; the suit gun was not lent.");
            Dispatch(Loan.Board(0, 0, slotJson, slotChecksum), now);
            return;
        }
        _suitRange = range;
        _suitChecksum = range!.GetChecksum();
        var ask = Loan.Board(r.Magazine, r.Reserve, slotJson, slotChecksum);
        if (ask == null) return;
        Dispatch(ask, now);
    }

    /// <summary>Sends the swap asked at boarding now, or once every older lease has put its gun away.</summary>
    private static void Dispatch(LoanGun? ask, float now)
    {
        if (ask == null) return;
        if (Loan.SlotReady) { Send(ask, now); return; }
        _waiting = true;
    }

    private static uint SlotChecksum(GearIDRange range)
    {
        if (_summed == null || _summed.Pointer != range.Pointer) { _summed = range; _sum = range.GetChecksum(); }
        return _sum;
    }

    /// <summary>Every frame on the local player's machine.</summary>
    internal static void Tick(float now)
    {
        GunSteady.Keep(Loan.Piloting ? Wielded() : null, Recoil);
        if (_retry && _unit != null) Begin(_unit, now);
        if (_waiting)
        {
            if (!Loan.SlotReady) return;
            _waiting = false;
            Send(Loan.Pending, now);
        }
        if (Loan.Pending == null && _gun == IntPtr.Zero) return;
        if (Loan.Pending != null && now - _swapStarted > SwapTimeout)
        {
            Plugin.Logger.ErrorOnce("Exosuit gun: the special slot did not finish taking " + (Loan.Pending == LoanGun.Suit ? "the suit gun" : "back the pilot's own weapon") + "; retrying with the pilot's own weapon.");
            Send(Loan.TimedOut(), now);
        }
        if (Loan.Pending != null && !_sent) Send(Loan.Pending, now);
        if (!LoanedGun.TryLocalSpecial(out var backpack, out var item)) return;
        if (Loan.Pending != null && !_paced) LoanedGun.FinishParts(item!);
        var ready = LoanedGun.Settled(item!, out var weapon);
        if (!ready) return;
        var ammo = backpack!.AmmoStorage.SpecialAmmo;
        var checksum = SlotChecksum(item!.GearIDRange!);
        var instance = item.Instance!.Pointer;
        switch (Loan.Pending)
        {
            case LoanGun.Suit:
                {
                    if (checksum != _suitChecksum || instance == _swappedFrom) return;
                    var (clip, pack, overflow) = SuitGunStock.Split(Loan.SuitClip, Loan.SuitReserve, weapon!.GetMaxClip(), ammo.BulletsMaxCap);
                    _overflow = overflow;
                    if (!ForgeCore.Gear.WriteLocalAmmo(AmmoType.Special, weapon!, pack * ammo.CostOfBullet, clip)) Plugin.Logger.ErrorOnce("Exosuit gun: the suit gun's ammunition did not read back as written.");
                    ForgeCore.Players.Local?.Sync.WantsToWieldSlot(InventorySlot.GearSpecial);
                    Loan.Arrived(LoanGun.Suit);
                    _gun = instance;
                    MechGunSound.Claim(item.Instance!);
                    _clip = clip;
                    _pack = pack;
                    _reloading = false;
                    ClipSize = weapon.GetMaxClip();
                    _settleUntil = now + SettleSeconds;
                    LoanedGun.EndSwap();
                    if (!_swapHinted && Loan.Own != null)
                    {
                        _swapHinted = true;
                        LocalPilot.Notice(FrameWords.Format(LocalPilot.Words.GunSwap, ForgeUI.UIKitKeys.Binding(InputAction.SelectSpecial, "3")));
                    }
                    return;
                }
            case LoanGun.Own:
                {
                    var own = Loan.Own;
                    if (own == null || checksum != own.Checksum || instance == _swappedFrom) return;
                    if (!ForgeCore.Gear.WriteLocalAmmo(AmmoType.Special, weapon!, own.Pool, own.Clip)) Plugin.Logger.ErrorOnce("Exosuit gun: the pilot's own weapon's ammunition did not read back as written.");
                    if (Loan.Piloting) ForgeCore.Players.Local?.Sync.WantsToWieldSlot(InventorySlot.GearSpecial);
                    LoanedGun.EndSwap();
                    Loan.Arrived(LoanGun.Own);
                    return;
                }
        }
        // The suit gun settled in the slot.
        if (instance != _gun)
        {
            // Something else (a checkpoint, another mod) replaced the slot; the own weapon's stock stays kept unless it is the own weapon.
            Forget();
            Loan.Lost(checksum == Loan.Own?.Checksum);
            return;
        }
        _clip = weapon!.GetCurrentClip();
        _pack = ammo.BulletsInPack;
        var reloading = weapon.IsReloading;
        if (reloading != _reloading) ReloadChanged(reloading, now);
        _reloading = reloading;
        if (_overflow > 0 && _pack < ammo.BulletsMaxCap)
        {
            var moved = Math.Min(_overflow, ammo.BulletsMaxCap - _pack);
            _overflow -= moved;
            _pack += moved;
            ammo.AmmoInPack = _pack * ammo.CostOfBullet;
            backpack.AmmoStorage.UpdateSlotAmmoUI(InventorySlot.GearSpecial);
            PlayerBackpackManager.ForceLocalAmmoStorageUpdate();
        }
        if (now >= _settleUntil && _clip + _pack + _overflow <= 0)
        {
            LocalPilot.ReportAmmo(0, 0);
            LocalPilot.Notice(LocalPilot.Words.GunEmpty);
            Send(Loan.SuitEmpty(), now);
        }
    }

    /// <summary>The pilot is released (left, ejected, wrecked, seized out, reload, level end): the own special weapon comes back.
    /// <paramref name="paced"/>: an orderly leave, where the leaving motion holds the pilot well past the game's own assembly of the
    /// weapon, so its parts are not all spawned in one frame (a forced release finishes them at once: the pilot may need the gun now).</summary>
    internal static void End(float now, bool paced = false)
    {
        _paced = paced;
        _retry = false;
        _slotWaitUntil = 0f;
        if (_waiting)
        {
            // Nothing was asked of the game yet: the slot is left as it is and the lease goes back.
            _waiting = false;
            Loan.Withdraw();
        }
        if (Loan.Piloting || Loan.Pending != null) Send(Loan.Leave(), now);
        _unit = null;
        GunSteady.Keep(null, 1f);
    }

    /// <summary>After a checkpoint reload (the shared slot already put back as it was at the checkpoint): a suit gun the game brought back
    /// goes back for the pilot's own weapon as it was at the checkpoint.</summary>
    internal static void AfterReload(float now)
    {
        var own = Loan.Own;
        if (own == null || !Loan.Leased && ForgeCore.Gear.SpecialSlot.CheckpointHolder != SuitGunLoan.Owner) return;
        Forget();
        if (LoanedGun.TryLocalSpecial(out _, out var item) && item!.GearIDRange != null && item.GearIDRange.GetChecksum() == _suitChecksum && _suitChecksum != own.Checksum)
        {
            Send(Loan.ReturnOwn(), now);
            return;
        }
        // The game put the pilot's own weapon (or another plugin's gun) back with its checkpoint stock: nothing is awaited here.
        Loan.Lost(item?.GearIDRange != null && item.GearIDRange.GetChecksum() == own.Checksum);
    }

    /// <summary>Level end: forget everything (the game rebuilds the inventory for the next level).</summary>
    internal static void Clear()
    {
        GunSteady.Keep(null, 1f);
        Loan.Clear();
        Forget();
        LoanedGun.EndSwap();
        _waiting = false;
        _retry = false;
        _slotWaitUntil = 0f;
        _paced = false;
        _unit = null;
        _suitRange = null;
        _summed = null;
    }

    /// <summary>Asks the game for <paramref name="gun"/> in the special slot, keeping the stock of the gun leaving it.</summary>
    private static void Send(LoanGun? gun, float now)
    {
        if (gun == null) return;
        var own = Loan.Own;
        // No own weapon is kept when the level ended under the pilot (ForgeCore clears the shared slot before this plugin's cleanup, and the game rebuilds the inventory): nothing to swap back.
        if (gun == LoanGun.Own && own == null) { Loan.Lost(false); return; }
        var settled = LoanedGun.TryLocalSpecial(out var backpack, out var current) && current!.Instance != null;
        // Leaving the slot: the suit gun's live stock (reported to the host), or the pilot's own weapon's.
        if (_gun != IntPtr.Zero)
        {
            Loan.KeepSuit(_clip, _pack + _overflow);
            LocalPilot.ReportAmmo(_clip, _pack + _overflow);
            Forget();
        }
        else if (gun == LoanGun.Suit && settled && Loan.InSlot == LoanGun.Own && current!.GearIDRange != null && current.GearIDRange.GetChecksum() == own?.Checksum)
            LoanedGun.OwnLeaves();
        // The game's pack for the gun coming in is written now, so its reserve is right from the moment the slot names it (the magazine lives in the new instance and is written the frame it exists).
        if (backpack != null) PrepareReserve(gun.Value, backpack);
        _swappedFrom = settled ? current!.Instance!.Pointer : IntPtr.Zero;
        _swapStarted = now;
        _sent = true;
        try
        {
            if (gun == LoanGun.Suit) LoanedGun.Equip(_suitRange!);
            else LoanedGun.EquipOwn();
        }
        catch (Exception error)
        {
            _sent = false;
            Plugin.Logger.ErrorOnce("Exosuit gun: asking the game for " + (gun == LoanGun.Suit ? "the suit gun" : "the pilot's own special weapon") + " failed (retrying): " + error);
        }
    }

    /// <summary>Writes the pack of the gun coming into the special slot into the game's ammunition storage before the game is asked for it: the own weapon's kept pool, or the suit gun's reserve (once its magazine size is known).</summary>
    private static void PrepareReserve(LoanGun gun, PlayerBackpack backpack)
    {
        var storage = backpack.AmmoStorage;
        var ammo = storage.SpecialAmmo;
        if (gun == LoanGun.Own)
        {
            if (Loan.Own is not { } own) return;
            ammo.AmmoInPack = own.Pool;
        }
        else
        {
            if (ClipSize <= 0) return;
            var (_, pack, _) = SuitGunStock.Split(Loan.SuitClip, Loan.SuitReserve, ClipSize, ammo.BulletsMaxCap);
            ammo.AmmoInPack = pack * ammo.CostOfBullet;
        }
        storage.UpdateSlotAmmoUI(InventorySlot.GearSpecial);
        PlayerBackpackManager.ForceLocalAmmoStorageUpdate();
    }

    /// <summary>The suit gun is out of the slot: its live readings and recoil hold go.</summary>
    private static void Forget()
    {
        _gun = IntPtr.Zero;
        // The next gun (or the next level's archetype at a reused address) starts its fire timing fresh.
        _timedArchetype = IntPtr.Zero;
        Clock.Stop();
        MechGunSound.Release();
        _clip = _pack = _overflow = 0;
        _reloading = false;
    }

    /// <summary>The local player's held gun, if it is a gun.</summary>
    private static BulletWeapon? Wielded()
    {
        var held = ForgeCore.Players.Local?.Inventory?.WieldedItem;
        return held != null && !held.WasCollected ? held.TryCast<BulletWeapon>() : null;
    }

}
