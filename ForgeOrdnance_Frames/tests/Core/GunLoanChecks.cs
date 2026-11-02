using ForgeCore;
using ForgeOrdnanceFrames;

/// <summary>The special slot's two guns while piloting (swapping, the empty suit gun, every way out), the shared slot lease with a heavy
/// weapon lent by another plugin, where special ammunition goes, and which suit bonuses reach the pilot's own guns.</summary>
internal static class GunLoanChecks
{
    internal static void Run(Action<bool, string> check, IReadOnlyDictionary<string, FrameSpec> models)
    {
        // Boarding with rounds brings the suit gun; the own weapon's stock is kept as it leaves.
        var slot = new SpecialSlot();
        var loan = new SuitGunLoan(slot, () => "Piloting an exosuit");
        check(loan.Board(100, 300, "own", 1) == LoanGun.Suit && loan.SlotReady, "boarding with rounds asks for the suit gun; with nothing else lent the slot is ready");
        slot.OwnLeaves(7, 42f);
        check(loan.Toggle() == null, "no second swap starts while one is on its way");
        check(slot.RedirectsRefill, "while the suit gun is on its way, special ammunition goes to the kept own weapon");
        loan.Arrived(LoanGun.Suit);
        check(loan.InSlot == LoanGun.Suit && loan.Pending == null, "the suit gun is in the slot once it arrived");

        // Swapping back and forth keeps both stocks as they were.
        check(loan.Toggle() == LoanGun.Own, "the special key with the suit gun held asks for the own weapon");
        loan.KeepSuit(60, 280);
        check(loan.Own == new OwnSpecialWeapon("own", 1, 7, 42f), "the own weapon comes back with its clip and pool");
        loan.Arrived(LoanGun.Own);
        check(!slot.RedirectsRefill, "with the own weapon settled in the slot, the game's own pickup fills it");
        check(loan.Toggle() == LoanGun.Suit, "the special key with the own weapon held asks for the suit gun again");
        slot.OwnLeaves(3, 40f);
        loan.Arrived(LoanGun.Suit);
        check(loan.SuitClip == 60 && loan.SuitReserve == 280, "the suit gun comes back with the rounds it left with");

        // A pickup while the suit gun stands in goes to the own weapon's kept pool, never to the suit gun.
        check(slot.RedirectsRefill, "with the suit gun held, special ammunition goes to the own weapon's kept stock");
        slot.Refilled(55f);
        check(loan.Own!.Pool == 55f && loan.SuitClip == 60 && loan.SuitReserve == 280, "the refill lands on the own weapon; the suit gun's stock is unchanged");

        // While a swap is on its way the slot is worth the incoming gun's stock, so no frame of the swap shows an empty gun.
        var swapSlot = new SpecialSlot();
        var swap = new SuitGunLoan(swapSlot, () => "Piloting an exosuit");
        check(SuitGunStock.Incoming(swap, 60, 1f, 700) == null, "no swap pending: nothing is incoming");
        swap.Board(100, 300, "own", 1);
        var toSuit = SuitGunStock.Incoming(swap, 60, 1f, 700);
        check(toSuit is { Clip: 60, Reserve: 340, ClipSize: 60 } && toSuit.Value.ReserveMax >= toSuit.Value.Reserve, "the suit gun on its way is its stock: a full magazine of its size and the rest in reserve");
        check(SuitGunStock.Incoming(swap, 0, 1f, 700) is { Clip: 100, Reserve: 300 }, "before its magazine size is known the whole magazine stock is shown");
        swapSlot.OwnLeaves(7, 42f);
        swap.Arrived(LoanGun.Suit);
        swap.Toggle();
        swap.KeepSuit(33, 120);
        var toOwn = SuitGunStock.Incoming(swap, 60, 2f, 700);
        check(toOwn is { Clip: 7, Reserve: 21 }, "the own weapon on its way is its kept clip and pool, in rounds");
        swap.Arrived(LoanGun.Own);
        check(SuitGunStock.Incoming(swap, 60, 2f, 700) == null, "nothing is incoming once the swap arrived");
        foreach (var (clip, reserve, maxClip, cap) in new[] { (100, 300, 60, 260), (60, 0, 60, 260), (45, 10, 60, 260), (100, 600, 60, 260), (1, 0, 60, 260), (100, 300, 0, 260) })
        {
            var (loaded, pack, overflow) = SuitGunStock.Split(clip, reserve, maxClip, cap);
            check(loaded + pack + overflow == clip + reserve && loaded <= maxClip && pack <= cap && loaded >= 0 && overflow >= 0 && loaded + pack + overflow > 0,
                $"the suit stock {clip}+{reserve} splits into magazine, pack and overflow without losing or inventing a round");
        }
        // An empty suit gun goes out for good.
        check(loan.SuitEmpty() == LoanGun.Own, "an empty suit gun asks for the own weapon");
        loan.Arrived(LoanGun.Own);
        check(loan.SuitSpent && loan.SuitClip + loan.SuitReserve == 0, "the empty suit gun is spent");
        check(loan.Toggle() == null && loan.Pending == null, "the special key cannot bring an empty suit gun back");
        check(loan.Own!.Pool == 55f, "the own weapon came back with what it was refilled to");
        check(loan.Leave() == null && !loan.Piloting && !slot.Taken && slot.Own == null, "leaving with the own weapon in the slot needs no swap and gives the slot back");

        // Every way out (leave, eject, wreck, grab, warp all release the pilot) ends with the own weapon, from any state.
        var held = Fresh(out var heldSlot);
        check(held.Leave() == LoanGun.Own && held.Pending == LoanGun.Own && heldSlot.Taken, "leaving with the suit gun held asks for the own weapon and keeps the slot until it is back");
        held.Arrived(LoanGun.Own);
        check(held.InSlot == LoanGun.Own && !heldSlot.Taken && heldSlot.Own == null, "out of the suit with the own weapon back, nothing is kept any more");
        var comingSlot = new SpecialSlot();
        var coming = new SuitGunLoan(comingSlot, () => "");
        coming.Board(10, 0, "own", 1);
        comingSlot.OwnLeaves(1, 2f);
        check(coming.Leave() == LoanGun.Own && coming.Pending == LoanGun.Own, "leaving while the suit gun is on its way asks for the own weapon instead");
        var stuck = Fresh(out _);
        stuck.Toggle();
        check(stuck.TimedOut() == LoanGun.Own && stuck.Pending == LoanGun.Own, "a swap that does not arrive falls back to the own weapon");
        check(stuck.Leave() == LoanGun.Own, "leaving during a swap back asks for the own weapon again");
        var reload = Fresh(out var reloadSlot);
        reload.Leave();
        check(reload.ReturnOwn() == LoanGun.Own && !reload.Piloting && reloadSlot.Taken, "a checkpoint that brought a suit gun back asks for the own weapon");
        var spentSlot = new SpecialSlot();
        var spent = new SuitGunLoan(spentSlot, () => "");
        check(spent.Board(0, 0, "own", 1) == null && spent.Piloting && spent.Toggle() == null && spentSlot.Taken, "boarding a spent suit keeps the own weapon, never swaps, and still holds the slot");
        spent.Leave();
        check(!spentSlot.Taken, "leaving a spent suit gives the slot back");

        // Boarding with a heavy weapon lent by another plugin: it is put away first, the toggle is between the suit gun and the
        // player's own weapon (never the heavy weapon), every way out ends with the own weapon, and only then does the heavy weapon's
        // lease get the slot back.
        foreach (var way in new[] { "leave", "wreck", "timeout", "checkpoint" })
        {
            var shared = new SpecialSlot();
            var heavy = shared.Take("NAinfini.ForgeOrdnanceArsenal", () => "Heavy weapon in hand");
            shared.Remember("own", 1);
            shared.OwnLeaves(4, 30f);
            var suit = new SuitGunLoan(shared, () => "Piloting an exosuit");
            check(suit.Board(100, 300, "heavy gear", 2) == LoanGun.Suit && !suit.SlotReady && heavy.Suspended, way + ": boarding with a heavy weapon suspends its lease and waits");
            check(shared.Top!.Refusal == "Piloting an exosuit", way + ": while piloting the slot says why nothing else can be lent");
            heavy.Parked = true;
            check(suit.SlotReady && suit.Own == new OwnSpecialWeapon("own", 1, 4, 30f), way + ": once the heavy weapon is put away the suit gun is sent; the own weapon is the player's, not the heavy weapon");
            suit.Arrived(LoanGun.Suit);
            check(suit.Toggle() == LoanGun.Own, way + ": the special key swaps to the player's own weapon");
            suit.KeepSuit(90, 300);
            suit.Arrived(LoanGun.Own);
            check(!shared.RedirectsRefill && heavy.Suspended, way + ": the own weapon is in the slot; the heavy weapon stays put away");
            check(suit.Toggle() == LoanGun.Suit, way + ": and back to the suit gun");
            shared.OwnLeaves(2, 25f);
            suit.Arrived(LoanGun.Suit);
            shared.Refilled(45f);
            check(shared.Own!.Pool == 45f && suit.SuitReserve == 300, way + ": a pickup while piloting goes to the own weapon, never the suit gun or the heavy weapon");
            switch (way)
            {
                case "leave":
                case "wreck":
                    check(suit.Leave() == LoanGun.Own && heavy.Suspended, way + ": leaving asks for the own weapon; the heavy weapon waits for it");
                    break;
                case "timeout":
                    suit.Toggle();
                    suit.TimedOut();
                    check(suit.Leave() == LoanGun.Own && heavy.Suspended, way + ": leaving after a swap that never arrived asks for the own weapon again");
                    break;
                case "checkpoint":
                    suit.Leave();
                    check(suit.ReturnOwn() == LoanGun.Own && heavy.Suspended, way + ": a reload asks for the own weapon before the heavy weapon is handed back");
                    break;
            }
            suit.Arrived(LoanGun.Own);
            check(!heavy.Suspended && shared.Top == heavy && !shared.RedirectsRefill && shared.Own == new OwnSpecialWeapon("own", 1, 2, 45f),
                way + ": with the own weapon back and its ammunition intact, the heavy weapon's lease is on top again");
        }

        // Boarding a spent suit with a heavy weapon in the slot: the own weapon comes in instead, and leaving hands the slot back.
        {
            var shared = new SpecialSlot();
            var heavy = shared.Take("NAinfini.ForgeOrdnanceArsenal", () => "");
            shared.Remember("own", 1);
            shared.OwnLeaves(4, 30f);
            var suit = new SuitGunLoan(shared, () => "");
            check(suit.Board(0, 0, "heavy gear", 2) == LoanGun.Own, "a spent suit boarded with a heavy weapon in the slot asks for the own weapon");
            heavy.Parked = true;
            suit.Arrived(LoanGun.Own);
            check(suit.Toggle() == null && !shared.RedirectsRefill, "the own weapon is in the slot and there is nothing to swap to");
            check(suit.Leave() == null && !heavy.Suspended, "leaving hands the slot back at once");
        }

        // Leaving before the heavy weapon was put away: nothing was asked of the game, the slot goes back untouched.
        {
            var shared = new SpecialSlot();
            var heavy = shared.Take("NAinfini.ForgeOrdnanceArsenal", () => "");
            shared.Remember("own", 1);
            shared.OwnLeaves(4, 30f);
            var suit = new SuitGunLoan(shared, () => "");
            suit.Board(100, 0, "heavy gear", 2);
            suit.Withdraw();
            check(!suit.Piloting && suit.Pending == null && shared.Top == heavy && !heavy.Suspended && shared.RedirectsRefill,
                "a pilot gone before the swap was sent gives the slot back untouched; the heavy weapon is still the gun in it");
        }

        // Handling for every gun held, numbers for the suit gun only.
        var fp = models["frame-m17"];
        foreach (var spec in models.Values)
            check(spec.Weapon.ReloadMultiplier == 0.75f && spec.Weapon.RecoilMultiplier < 1f, spec.Model + ": any gun reloads in 0.75 of its time and kicks less while piloting");
        check(GunHandling.Reload(fp.Weapon, fp.Siege, true) == 0.75f * fp.Siege!.ReloadMultiplier && GunHandling.Reload(fp.Weapon, fp.Siege, false) == 0.75f,
            "planting halves the reload again for whatever gun is held");
        check(GunHandling.Recoil(fp.Weapon, fp.Siege, true) == fp.Weapon.RecoilMultiplier * fp.Siege.RecoilMultiplier, "planting lowers the recoil again for whatever gun is held");
        check(GunHandling.Damage(false, fp.Weapon, fp.Siege, true) == 1f && GunHandling.FireRate(false, fp.Siege, true) == 1f,
            "the pilot's own guns keep their damage and fire rate, planted or not");
        check(GunHandling.Damage(true, fp.Weapon, fp.Siege, true) == fp.Weapon.DamageMultiplier * fp.Siege.DamageMultiplier && GunHandling.FireRate(true, fp.Siege, true) == fp.Siege.FireRateMultiplier
            && GunHandling.FireRate(true, fp.Siege, false) == 1f, "the suit gun hits harder, and fires faster only while planted");

    }

    /// <summary>A session with the suit gun settled in the slot.</summary>
    private static SuitGunLoan Fresh(out SpecialSlot slot)
    {
        slot = new SpecialSlot();
        var loan = new SuitGunLoan(slot, () => "");
        loan.Board(30, 30, "own", 1);
        slot.OwnLeaves(5, 10f);
        loan.Arrived(LoanGun.Suit);
        return loan;
    }
}