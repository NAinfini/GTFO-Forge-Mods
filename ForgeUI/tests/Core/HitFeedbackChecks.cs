using System;
using ForgeUI;

/// <summary>Hit feedback: the floating-number pool (merging, the cap with the oldest dropped, critical, life), the hit
/// marker's flash and the kill marks.</summary>
internal static class HitFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Numbers(check);
        Cap(check);
        Marker(check);
        Kills(check);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    private static int Used(FloatingNumbers numbers)
    {
        var used = 0;
        for (var i = 0; i < numbers.Capacity; i++) if (numbers[i].Used) used++;
        return used;
    }

    private static void Numbers(Action<bool, string> check)
    {
        var numbers = new FloatingNumbers(8);
        var slot = numbers.Add(7, 1.0f, 10f, HitKind.Hit, 1, 2, 3, true, 10);
        numbers.Add(7, 1.3f, 15f, HitKind.Hit, 9, 9, 9, true, 11);
        check(Used(numbers) == 1 && Near(numbers[slot].Amount, 25f) && Near(numbers[slot].X, 1f), "numbers: hits close together on one target add up where the first landed");
        numbers.MarkCritical(7, 11);
        check(numbers[slot].Kind == HitKind.Critical, "numbers: a weak-spot hit turns its number critical");
        numbers.Add(7, 2.0f, 5f, HitKind.Hit, 0, 0, 0, true, 12);
        check(Used(numbers) == 2, "numbers: a hit after the merge window starts a new number");
        numbers.Add(0, 2.0f, 5f, HitKind.Hit, 0, 0, 0, false, 12);
        numbers.Add(0, 2.0f, 5f, HitKind.Hit, 0, 0, 0, false, 12);
        check(Used(numbers) == 4, "numbers: key 0 never merges");
        var kill = numbers.Add(9, 3f, 4f, HitKind.Hit, 0, 0, 0, true, 13);
        numbers.Add(9, 3.1f, 4f, HitKind.Kill, 0, 0, 0, true, 14);
        check(numbers[kill].Kind == HitKind.Kill, "numbers: a merged number takes the strongest kind");
        numbers.Motion(numbers[kill], 3.1f + numbers.Seconds, out var rise, out var fade);
        check(Near(rise, 1f) && Near(fade, 1f), "numbers: risen and faded at the end of its life");
        numbers.Tick(3.1f + numbers.Seconds + 0.01f);
        check(Used(numbers) == 0, "numbers: freed after their life");
        check(numbers.Add(1, 0f, 1f, HitKind.Hit, 0, 0, 0, true, 0) >= 0 && Used(numbers) == 1, "numbers: a slot is free again");
        numbers.Clear();
        check(Used(numbers) == 0, "numbers: clear frees every number");
        numbers.MergeSeconds = 0f;
        numbers.Add(5, 0f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        numbers.Add(5, 0.01f, 1f, HitKind.Hit, 0, 0, 0, true, 1);
        check(Used(numbers) == 2, "numbers: a zero merge window never merges");
    }

    private static void Cap(Action<bool, string> check)
    {
        var numbers = new FloatingNumbers(3);
        var first = numbers.Add(1, 0f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        numbers.Add(2, 0.1f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        numbers.Add(3, 0.2f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        var fourth = numbers.Add(4, 0.3f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        check(fourth == first && numbers[fourth].Key == 4 && Used(numbers) == 3, "cap: past it the oldest gives way");
        check(new FloatingNumbers(0).Capacity == 1 && new FloatingNumbers(1000).Capacity == FloatingNumbers.Limit, "cap: the pool size is bounded");
    }

    private static void Marker(Action<bool, string> check)
    {
        var book = new HitFeedbackBook(4);
        check(book.PulseProgress(0f) >= 1f, "marker: nothing before the first hit");
        book.Pulse(HitKind.Kill, 1f, 5);
        book.Pulse(HitKind.Hit, 1f, 5);
        check(book.PulseKind == HitKind.Kill, "marker: a weaker hit in the same frame keeps the kill");
        check(Near(book.PulseProgress(1f + HitFeedbackBook.KillPulseSeconds / 2f), 0.5f), "marker: a kill flashes for its own length");
        book.Pulse(HitKind.Hit, 2f, 6);
        check(book.PulseKind == HitKind.Hit && book.PulseProgress(2f + HitFeedbackBook.HitSeconds) >= 1f, "marker: a later hit starts a new short flash");
        book.Pulse(HitKind.Critical, 2f, 6);
        check(book.PulseKind == HitKind.Critical, "marker: a weak spot in the same frame upgrades the flash");
    }

    private static void Kills(Action<bool, string> check)
    {
        var book = new HitFeedbackBook(4);
        book.Killed(10f, 4, 5, 6);
        check(Array.Exists(book.Marks, m => m.Used) && book.LastKill == 10f, "kill: a mark starts and the confirmation");
        book.Tick(10f + HitFeedbackBook.KillMarkSeconds + 0.01f);
        check(!Array.Exists(book.Marks, m => m.Used), "kill: the mark is short");
        for (var i = 0; i < HitFeedbackBook.Kills + 2; i++) book.Killed(20f, i, 0, 0);
        check(Array.TrueForAll(book.Marks, m => m.Used), "kill: a burst keeps the latest marks");
        book.Numbers.Add(1, 20f, 1f, HitKind.Hit, 0, 0, 0, true, 0);
        book.Clear();
        check(!Array.Exists(book.Marks, m => m.Used) && Used(book.Numbers) == 0 && float.IsNegativeInfinity(book.LastKill), "level change: everything clears");
    }
}
