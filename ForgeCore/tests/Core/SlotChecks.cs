namespace ForgeCore.Tests;

/// <summary>The shared special slot: stacked leases (suspend, park, ready, release in any order), the one record of the own weapon,
/// where special pickups go, and the checkpoint image.</summary>
internal static class SlotChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // One lease: the own weapon leaves and comes back; pickups go to its kept pool only while it is away.
        var slot = new SpecialSlot();
        check(!slot.Taken && slot.Own == null && !slot.RedirectsRefill, "an untouched slot keeps nothing and redirects nothing");
        var heavy = slot.Take("heavy", () => "holding a heavy weapon");
        slot.Remember("own", 1);
        check(slot.Top == heavy && heavy.Ready && !heavy.Suspended && !slot.RedirectsRefill, "a lease alone is ready; pickups still go through the game until the own weapon leaves");
        slot.OwnLeaves(6, 40f);
        check(slot.RedirectsRefill && slot.Own == new OwnSpecialWeapon("own", 1, 6, 40f), "the own weapon leaves with its stock and pickups now go to its kept pool");
        slot.Refilled(55f);
        check(slot.Own!.Pool == 55f && slot.Own.Clip == 6, "a pickup changes only the kept pool");
        slot.Remember("heavy gear", 2);
        check(slot.Own.GearJson == "own", "the gear in the slot is not taken for the own weapon while the own weapon is away");

        // A second lease on top: the first is suspended, the second waits until it parks.
        var suit = slot.Take("suit", () => "Piloting an exosuit");
        check(heavy.Suspended && !suit.Suspended && slot.Top == suit && !suit.Ready, "a newer lease suspends the older one and waits for it");
        check(suit.Refusal == "Piloting an exosuit", "the top lease says why the slot is taken");
        heavy.Parked = true;
        check(suit.Ready && slot.RedirectsRefill, "once the older lease parked the newer one may change the slot; the own weapon is still away");
        slot.OwnReturned();
        check(!slot.RedirectsRefill && slot.Own!.Pool == 55f, "the own weapon back in the slot: pickups go through the game, its record stays while leases are held");
        slot.OwnLeaves(5, 50f);
        suit.Dispose();
        check(!heavy.Suspended && !suit.Held && slot.Top == heavy && slot.RedirectsRefill, "the newer lease gone, the older one is on top again; the own weapon is still away");
        suit.Dispose();
        check(slot.Top == heavy, "disposing twice changes nothing");
        slot.OwnReturned();
        heavy.Dispose();
        check(!slot.Taken && slot.Own == null && !slot.RedirectsRefill, "with the last lease gone and the own weapon home, nothing is kept");

        // Release out of order: the middle lease goes first.
        var a = slot.Take("a", () => "");
        var b = slot.Take("b", () => "");
        var c = slot.Take("c", () => "");
        a.Parked = true;
        b.Dispose();
        check(c.Ready && a.Suspended && slot.Top == c, "a lease released from the middle leaves the others in order");
        a.Parked = false;
        check(!c.Ready, "an older lease that resumes work holds the newer one back");
        c.Dispose(); a.Dispose();
        check(!a.Parked && !slot.Taken, "a released lease is no longer parked");

        // Checkpoints: what was away comes back away, with the stock it had then.
        var loan = slot.Take("heavy", () => "");
        slot.Remember("own", 1);
        slot.OwnLeaves(3, 20f);
        slot.CaptureCheckpoint();
        check(slot.CheckpointHolder == "heavy", "the checkpoint names the lease whose gun was in the slot");
        slot.Refilled(80f);
        slot.OwnReturned();
        loan.Dispose();
        slot.RecallCheckpoint();
        check(slot.RedirectsRefill && slot.Own == new OwnSpecialWeapon("own", 1, 3, 20f) && slot.CheckpointHolder == "heavy",
            "a reload brings the own weapon's checkpoint stock back as away, even after the loan ended");
        slot.RecallCheckpoint();
        check(slot.Own == new OwnSpecialWeapon("own", 1, 3, 20f), "repeated reloads keep the checkpoint image");
        slot.OwnReturned();
        slot.CaptureCheckpoint();
        check(slot.CheckpointHolder == null, "a checkpoint with the own weapon in the slot names nobody");
        var later = slot.Take("suit", () => "");
        slot.Remember("own", 1);
        slot.OwnLeaves(1, 2f);
        slot.RecallCheckpoint();
        check(!slot.RedirectsRefill && slot.Own != null, "a reload to a checkpoint with the own weapon in the slot puts it home; the record stays while a lease is held");
        later.Dispose();
        check(slot.Own == null, "and goes with the last lease");

        // Level end drops everything.
        var gone = slot.Take("heavy", () => "");
        slot.Remember("own", 1);
        slot.OwnLeaves(1, 1f);
        slot.CaptureCheckpoint();
        slot.Clear();
        check(!slot.Taken && !gone.Held && slot.Own == null && !slot.RedirectsRefill && slot.CheckpointHolder == null, "level end clears leases, the own weapon and the checkpoint");
        gone.Dispose();
        slot.RecallCheckpoint();
        check(!slot.RedirectsRefill && slot.Own == null, "a stale lease and a reload after the level end keep nothing");
        var threw = false;
        try { slot.OwnLeaves(1, 1f); } catch (InvalidOperationException) { threw = true; }
        check(threw, "the own weapon cannot leave before it is remembered");
    }
}
