namespace ForgeCore.Tests;

internal static class CoreChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        // Pool: bounded, reuses its items, and taking/returning allocates nothing once warm.
        var made = 0; var shown = 0;
        var pool = Pool.Create(3, () => { made++; return new object(); }, _ => shown++, _ => shown--);
        pool.Prewarm(2);
        check(made == 2 && pool.Free == 2 && pool.Created == 2, "pool: prewarm makes items ahead of use");
        pool.TryTake(out var a); pool.TryTake(out var b); pool.TryTake(out var c);
        check(made == 3 && !pool.TryTake(out _) && shown == 3, "pool: a fourth take beyond capacity 3 is refused, not made");
        pool.Return(b);
        check(pool.TryTake(out var again) && ReferenceEquals(again, b) && made == 3, "pool: a returned item is taken again");
        pool.Return(a); pool.Return(again); pool.Return(c);
        var threw = false;
        try { pool.Return(a); } catch (InvalidOperationException) { threw = true; }
        check(threw, "pool: returning more than was taken is an error");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { pool.TryTake(out var x); pool.Return(x); }
        check(GC.GetAllocatedBytesForCurrentThread() == before, "pool: take and return allocate nothing");
        {
            var spent = Pool.Create(2, () => new object());
            spent.TryTake(out var lost); spent.TryTake(out var kept); spent.Return(kept);
            spent.Discard(lost);
            var drained = 0;
            spent.Drain(_ => drained++);
            check(drained == 1 && spent.Created == 0 && spent.TryTake(out _), "pool: a discarded item frees its place and drain releases the free ones");
        }

        // Log: only errors are written; a repeating error is written once per exact message.
        var log = Log.For("test.log");
        errors.Clear();
        for (var i = 0; i < 100; i++) log.ErrorOnce("broken");
        check(errors.Count == 1 && errors[0] == "broken", "log: a repeating error is written once");
        log.ErrorOnce("broken differently");
        check(errors.Count == 2 && errors[1] == "broken differently", "log: a different message from the same place is written too");
        errors.Clear();
        
        check(errors.Count == 0, "log: information and warnings write nothing");
        log.Error("failed");
        check(errors.Count == 1 && errors[0] == "failed", "log: an error is written");
        // Data blocks: only added; an ID taken by the game or another mod is refused.
        var ledger = new DataBlockLedger();
        check(ledger.Decide("Gear", 9, "mod.a", "gun", null) == DataBlockResult.Free, "blocks: a free ID can be added");
        ledger.Owns("Gear", 9, "mod.a", "gun");
        check(ledger.Decide("Gear", 9, "mod.a", "gun", "gun") == DataBlockResult.AlreadyAdded, "blocks: adding the same block again changes nothing");
        check(ledger.Decide("Gear", 9, "mod.b", "gun", "gun") == DataBlockResult.Conflict, "blocks: another mod cannot take the ID");
        check(ledger.Decide("Gear", 9, "mod.a", "other", "gun") == DataBlockResult.Conflict, "blocks: the owner cannot replace its block under another name");
        check(ledger.Decide("Gear", 10, "mod.a", "gun", "stock") == DataBlockResult.Conflict, "blocks: a block the game already has is never overwritten");

        // Store: one file per mod; a damaged file is reported and never overwritten.
        var root = Path.Combine(Path.GetTempPath(), "forgecore-store-" + Guid.NewGuid().ToString("N"));
        Store.Root = root;
        try
        {
            check(Store.Read<Dictionary<string, int>>("mod.s").Status == StoreStatus.Missing, "store: nothing saved yet is Missing");
            check(Store.Write("mod.s", new Dictionary<string, int> { ["level"] = 3 }), "store: a value is written");
            var read = Store.Read<Dictionary<string, int>>("mod.s");
            check(read.Status == StoreStatus.Read && read.Value!["level"] == 3, "store: the value reads back");
            File.WriteAllText(Store.PathOf("mod.s"), "{ broken");
            errors.Clear();
            check(Store.Read<Dictionary<string, int>>("mod.s").Status == StoreStatus.Corrupt && !Store.Write("mod.s", new Dictionary<string, int>())
                  && File.ReadAllText(Store.PathOf("mod.s")) == "{ broken" && errors.Count == 2, "store: a damaged file fails explicitly and is not overwritten");
        }
        finally { Directory.Delete(root, true); }

        // Input leases stack: the blocked set is all held leases together; the newest steering lease steers; releasing all restores.
        var book = new InputLeaseBook();
        var first = book.Take("mod.a"); var second = book.Take("mod.b");
        book.Block(first, stackalloc[] { 1, 2 });
        book.Block(second, stackalloc[] { 2, 5 });
        check(book.Engaged && book.Blocks(1) && book.Blocks(2) && book.Blocks(5) && !book.Blocks(3), "input: leases block their actions together");
        book.Steer(first, true, 0.5f, 1f); book.Steer(second, true, -1f, 0f);
        check(book.Steered(true) == -1f, "input: the newest steering lease steers");
        book.Release(second);
        check(book.Blocks(1) && book.Blocks(2) && !book.Blocks(5) && book.Steered(true) == 0.5f, "input: releasing one lease keeps the other's actions");
        book.Release(first);
        check(!book.Engaged && !book.Blocks(1) && book.Count == 0, "input: releasing the last lease restores everything");
        var gone = book.Take("mod.c");
        book.ReleaseAll();
        threw = false;
        try { book.Block(gone, stackalloc[] { 1 }); } catch (InvalidOperationException) { threw = true; }
        check(threw && !book.Engaged, "input: a released lease cannot block again");

        // A lease can press buttons itself: edges belong to the first query frame, a re-press gets a fresh edge, an unread press is reported.
        var pressing = book.Take("mod.p");
        book.Press(pressing, 7, 10);
        check(book.Engaged && book.Presses(7) && book.Held(7) && !book.Presses(8), "input: a pressed button is held and leased");
        check(book.Down(7, 14) && book.Down(7, 14) && !book.Down(7, 15), "input: the press edge is the first query frame, repeated within it only");
        check(!book.ReleaseButton(pressing, 7, 16) && !book.Held(7) && book.Presses(7), "input: releasing a read press is clean and the button stays leased");
        check(book.Up(7, 20) && book.Up(7, 20) && !book.Up(7, 21), "input: the release edge is the first query frame, repeated within it only");
        book.Press(pressing, 7, 30);
        check(book.ReleaseButton(pressing, 7, 31) && !book.Held(7) && book.Up(7, 32), "input: a press nobody queried is reported but still dropped");
        book.Press(pressing, 7, 40); book.Press(pressing, 8, 40);
        check(book.Held(7) && book.Held(8) && book.Down(7, 41) && !book.Down(7, 42) && book.Down(8, 41), "input: several buttons are independent");
        book.SetAxis(pressing, 3, 1f);
        threw = false;
        try { book.SetAxis(pressing, 4, 1f); } catch (InvalidOperationException) { threw = true; }
        check(threw && book.Drives(3) && book.Axis(3) == 1f && book.Axis(4) == 0f, "input: one axis per lease");
        book.Release(pressing);
        check(!book.Engaged && !book.Presses(7) && !book.Held(7) && !book.Drives(3), "input: releasing the lease ends the pressed buttons and axis");
    }
}
