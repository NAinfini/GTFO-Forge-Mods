namespace ForgeAudio.Tests;

/// <summary>The claim table behind body sounds and weapon actions: who answers a game sound, and that answering costs nothing per play.</summary>
internal static class ClaimChecks
{
    private static class K
    {
        internal const int Footstep = (int)BodySound.Footstep, Movement = (int)BodySound.Movement;
        internal const int ReloadStart = (int)WeaponAction.ReloadStart, ReloadDone = (int)WeaponAction.ReloadDone, DryFire = (int)WeaponAction.DryFire;
    }

    private sealed class Owner { internal readonly string Name; internal Owner(string name) { Name = name; } }

    private static IntPtr[] Pointers(params long[] values) => Array.ConvertAll(values, v => new IntPtr(v));

    internal static void Run(Action<bool, string> check)
    {
        Weapons(check);
        var book = new ClaimBook<Owner>();
        var suit = new Owner("suit");
        var low = new Owner("low");
        var high = new Owner("high");

        check(book.Find(new IntPtr(100), K.Footstep) == null, "an unclaimed body plays its own sounds (empty table)");

        var suitEntry = book.Add(suit, Pointers(100, 101, 102), K.Footstep | K.Movement, 0);
        check(book.Find(new IntPtr(101), K.Footstep) == suit && book.Find(new IntPtr(102), K.Movement) == suit,
            "every transform of a claimed body answers to its claim");
        check(book.Find(new IntPtr(200), K.Footstep) == null && book.Find(IntPtr.Zero, K.Footstep) == null,
            "another player's sounds (and an unrooted feedback player) are not claimed");

        var lowEntry = book.Add(low, Pointers(101), K.Footstep, -5);
        var highEntry = book.Add(high, Pointers(101), K.Footstep, 10);
        check(book.Find(new IntPtr(101), K.Footstep) == high, "the highest priority claim answers");
        check(book.Find(new IntPtr(101), K.Movement) == suit, "a claim only answers the kinds it claimed");
        var newer = new Owner("newer");
        var newerEntry = book.Add(newer, Pointers(101), K.Footstep, 10);
        check(book.Find(new IntPtr(101), K.Footstep) == newer, "at equal priority the newer claim answers");

        check(book.Remove(newerEntry) && !book.Remove(newerEntry), "a claim ends once");
        check(book.Find(new IntPtr(101), K.Footstep) == high, "an ended claim hands the body back to the next one");
        book.Remove(highEntry);
        book.Remove(suitEntry);
        check(book.Find(new IntPtr(100), K.Footstep) == null && book.Find(new IntPtr(101), K.Footstep) == low,
            "ending a claim removes only its own transforms");
        check(!suitEntry.Live && lowEntry.Live && book.Count == 1, "ended claims report inactive");

        book.Reset(lowEntry, Pointers(300));
        check(book.Find(new IntPtr(101), K.Footstep) == null && book.Find(new IntPtr(300), K.Footstep) == low,
            "a refreshed claim answers for its new transforms only");

        book.Add(suit, Pointers(100, 101), K.Footstep, 0);
        book.Clear();
        check(book.Count == 0 && !lowEntry.Live && book.Find(new IntPtr(100), K.Footstep) == null
            && book.Find(new IntPtr(300), K.Footstep) == null, "the level ending ends every claim");
        check(!book.Remove(lowEntry), "disposing a claim the level already ended does nothing");

        // Zero allocation per play, claimed or not: thousands of transforms, lookups that hit, miss on kind and miss on pointer.
        var many = new long[2000];
        for (var i = 0; i < many.Length; i++) many[i] = 10_000 + i;
        book.Add(suit, Pointers(many), K.Footstep, 0);
        book.Add(high, Pointers(10_000, 10_001), K.Footstep | K.Movement, 5);
        Owner? sink = null;
        for (var i = 0; i < 100; i++) sink = book.Find(new IntPtr(10_000 + i), K.Movement);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 100_000; i++)
        {
            sink = book.Find(new IntPtr(10_000 + i % 2000), K.Footstep);
            if (sink != null) hits++;
            sink = book.Find(new IntPtr(10_000 + i % 2000), K.Movement);
            sink = book.Find(new IntPtr(500 + i % 7), K.Footstep);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(hits == 100_000 && allocated == 0, "a body sound lookup allocates nothing (" + allocated + " bytes over 300000 lookups)");
    }

    /// <summary>Weapon actions: one pointer per claim (the weapon instance), only the claimed actions answered, the rest of the game's
    /// weapons untouched.</summary>
    private static void Weapons(Action<bool, string> check)
    {
        var book = new ClaimBook<Owner>();
        var suitGun = new Owner("suit gun");
        var other = new Owner("other mod");
        check(book.Find(new IntPtr(7), K.DryFire) == null, "a weapon nobody claimed keeps the game's own action sounds");

        var suitEntry = book.Add(suitGun, Pointers(7), K.ReloadStart | K.ReloadDone | K.DryFire, 0);
        check(book.Find(new IntPtr(7), K.ReloadStart) == suitGun && book.Find(new IntPtr(7), K.ReloadDone) == suitGun && book.Find(new IntPtr(7), K.DryFire) == suitGun,
            "a claimed weapon answers every action it claimed");
        check(book.Find(new IntPtr(8), K.ReloadStart) == null, "another weapon (the pilot's own gun) is not claimed");

        var partialEntry = book.Add(other, Pointers(7), K.DryFire, 3);
        check(book.Find(new IntPtr(7), K.DryFire) == other && book.Find(new IntPtr(7), K.ReloadDone) == suitGun,
            "a higher priority claim answers only the actions it claimed");

        check(book.Remove(partialEntry) && book.Find(new IntPtr(7), K.DryFire) == suitGun, "ending a claim hands the action to the next one");
        book.Remove(suitEntry);
        check(book.Find(new IntPtr(7), K.ReloadStart) == null && book.Count == 0, "a released weapon plays the game's sounds again");

        book.Add(suitGun, Pointers(7), K.ReloadStart | K.ReloadDone | K.DryFire, 0);
        Owner? sink = null;
        for (var i = 0; i < 100; i++) sink = book.Find(new IntPtr(7), K.DryFire);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hits = 0;
        for (var i = 0; i < 100_000; i++)
        {
            sink = book.Find(new IntPtr(7), K.ReloadStart);
            if (sink != null) hits++;
            sink = book.Find(new IntPtr(1000 + i % 9), K.DryFire);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(hits == 100_000 && allocated == 0, "a weapon action lookup allocates nothing (" + allocated + " bytes over 200000 lookups)");
    }}
