using System;
using ForgeCore;

namespace ForgeOrdnance.Frames;

/// <summary>Which of the two guns sharing the special weapon slot while piloting.</summary>
internal enum LoanGun : byte { Own, Suit }

/// <summary>The special slot while a suit is worn: the suit gun and the pilot's own special weapon take turns in it (the game's own
/// gear swap, which replicates), each keeping its magazine and reserve while the other is in the slot. Boarding takes ForgeCore's
/// shared slot lease (<see cref="SpecialSlot"/>), so a heavy weapon lent by another lender is put away by its owner first, and the suit
/// gun is asked for once that lease is <see cref="SpecialSlotLease.Ready"/>; the own weapon is the one <see cref="SpecialSlot.Own"/>
/// keeps (the player's own, never a lent heavy weapon). Boarding brings the suit gun in when it has rounds, otherwise the own weapon
/// when something else stands in the slot; the special weapon's select key, pressed while that slot is held, swaps; an empty suit gun
/// goes out for good (no swapping back to it); leaving the suit, by any path, ends with the own weapon in the slot, and only then is the
/// lease given back (the older lease then takes its heavy weapon back). A swap is pending until the new gun is in the slot; no other
/// swap starts meanwhile. The suit gun is never refilled: while the own weapon is away, ForgeCore sends special ammunition into its
/// kept pool.</summary>
internal sealed class SuitGunLoan
{
    /// <summary>The lease's owner in the slot. Arsenal's heavy weapons lend under their own name: a checkpoint remembers whose gun was in the slot.</summary>
    public const string Owner = Messages.FramesId;

    private readonly SpecialSlot _slot;
    private readonly Func<string> _refusal;
    private SpecialSlotLease? _lease;

    /// <summary>A loan over <paramref name="slot"/>; <paramref name="refusal"/> says, in the player's language, why nothing else can be
    /// lent into the slot while piloting.</summary>
    public SuitGunLoan(SpecialSlot slot, Func<string> refusal)
    {
        _slot = slot;
        _refusal = refusal;
    }

    /// <summary>A pilot session is open (from boarding until leaving by any path).</summary>
    public bool Piloting { get; private set; }
    /// <summary>The gun in the slot (the last swap that arrived).</summary>
    public LoanGun InSlot { get; private set; } = LoanGun.Own;
    /// <summary>The gun asked for and not yet in the slot.</summary>
    public LoanGun? Pending { get; private set; }
    /// <summary>The suit gun ran dry while held; it does not come back this session.</summary>
    public bool SuitSpent { get; private set; }
    /// <summary>The suit gun's rounds kept while it is out of the slot (clip, reserve).</summary>
    public int SuitClip { get; private set; }
    public int SuitReserve { get; private set; }

    /// <summary>The slot's lease is held (from boarding until the own weapon is back after leaving).</summary>
    public bool Leased => _lease is { Held: true };

    /// <summary>The slot may be changed now: every older lease has put its gun aside.</summary>
    public bool SlotReady => _lease is { Held: true, Ready: true };

    /// <summary>The pilot's own special weapon as the shared slot keeps it.</summary>
    public OwnSpecialWeapon? Own => _slot.Own;

    /// <summary>The pilot closed in with the suit's stock; <paramref name="slotGearJson"/> and <paramref name="slotChecksum"/> are the gear
    /// in the slot now (the own weapon unless something else stands in it; null when the slot could not be read). The suit gun is asked
    /// for when it has rounds, otherwise the own weapon when it is away (the caller keeps the own stock with <c>LoanedGun.OwnLeaves</c> when
    /// the own weapon leaves, and sends the swap once <see cref="SlotReady"/>).</summary>
    public LoanGun? Board(int suitClip, int suitReserve, string? slotGearJson, uint slotChecksum)
    {
        if (!Leased) _lease = _slot.Take(Owner, _refusal);
        if (slotGearJson != null) _slot.Remember(slotGearJson, slotChecksum);
        Piloting = true;
        SuitClip = suitClip;
        SuitReserve = suitReserve;
        SuitSpent = suitClip + suitReserve <= 0;
        var ask = Ask(LoanGun.Suit);
        if (ask != null || !_slot.OwnAway || _slot.Own == null) return ask;
        // No suit gun, and the slot holds another lender's gun: the own weapon comes in.
        Pending = LoanGun.Own;
        return LoanGun.Own;
    }

    /// <summary>The special weapon's select key while that slot is held: the other gun, or null when it cannot come (a swap is pending,
    /// the suit gun is spent, no session is open, or another lease holds the slot).</summary>
    public LoanGun? Toggle()
    {
        if (!Piloting || Pending != null || _lease is not { Held: true, Suspended: false }) return null;
        return Ask(InSlot == LoanGun.Suit ? LoanGun.Own : LoanGun.Suit);
    }

    /// <summary>The suit gun in the slot is out of rounds: it is spent and the own weapon is asked for.</summary>
    public LoanGun? SuitEmpty()
    {
        SuitClip = SuitReserve = 0;
        SuitSpent = true;
        return InSlot == LoanGun.Suit && Pending == null ? Ask(LoanGun.Own) : null;
    }

    /// <summary>The pilot left (any path): the own weapon is asked for when the suit gun is in the slot or on its way, or a swap back is
    /// already pending (asked again). With the own weapon settled in the slot the lease is given back at once.</summary>
    public LoanGun? Leave()
    {
        Piloting = false;
        if (InSlot == LoanGun.Own && Pending == null) { Release(); return null; }
        return ReturnOwn();
    }

    /// <summary>The pilot left before the swap asked at boarding was sent to the game (an older lease had not parked yet): nothing
    /// changed in the slot, the session ends and the lease is given back.</summary>
    public void Withdraw()
    {
        Piloting = false;
        Pending = null;
        InSlot = LoanGun.Own;
        Release();
    }

    /// <summary>The own weapon is asked for whatever the slot is thought to hold (a checkpoint put a suit gun back), and the session ends.</summary>
    public LoanGun ReturnOwn()
    {
        if (!Leased) _lease = _slot.Take(Owner, _refusal);
        Piloting = false;
        Pending = LoanGun.Own;
        return LoanGun.Own;
    }

    /// <summary>A pending swap did not arrive in time: the suit gun is given up and the own weapon asked for (again).</summary>
    public LoanGun? TimedOut()
    {
        if (Pending == null) return null;
        Pending = LoanGun.Own;
        return LoanGun.Own;
    }

    /// <summary>The asked-for gun is in the slot (with the own weapon, its kept stock already written back). Out of the suit with the
    /// own weapon back, the lease is given back.</summary>
    public void Arrived(LoanGun gun)
    {
        if (Pending != gun) return;
        InSlot = gun;
        Pending = null;
        if (gun != LoanGun.Own) return;
        _slot.OwnReturned();
        if (!Piloting) Release();
    }

    /// <summary>The slot holds something this loan did not bring (a checkpoint, another lender): the suit gun is no longer in it and no
    /// swap is awaited. <paramref name="ownInSlot"/>: the slot holds the own weapon (pickups go through the game again). Outside a
    /// session the lease is given back.</summary>
    public void Lost(bool ownInSlot)
    {
        InSlot = LoanGun.Own;
        Pending = null;
        if (ownInSlot) _slot.OwnReturned();
        if (!Piloting) Release();
    }

    /// <summary>The suit gun's rounds as it leaves the slot (they only go down).</summary>
    public void KeepSuit(int clip, int reserve)
    {
        SuitClip = clip;
        SuitReserve = reserve;
    }

    /// <summary>Level end or a fresh start: nothing is kept and the lease is given back.</summary>
    public void Clear()
    {
        Piloting = SuitSpent = false;
        InSlot = LoanGun.Own;
        Pending = null;
        SuitClip = SuitReserve = 0;
        Release();
    }

    private void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private LoanGun? Ask(LoanGun gun)
    {
        if (gun == LoanGun.Suit && (!Piloting || SuitSpent || SuitClip + SuitReserve <= 0)) return null;
        if (gun == InSlot && Pending == null) return null;
        Pending = gun;
        return gun;
    }
}
