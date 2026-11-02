using System;
using System.Collections.Generic;
using ForgeUI;

/// <summary>The confirmation queue's state machine and the tooltip's placement.</summary>
internal static class ConfirmChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Queue(check);
        Tooltip(check);
    }

    private static void Queue(Action<bool, string> check)
    {
        var ended = new List<(string, ConfirmAnswer)>();
        var queue = new ConfirmQueue<string>((q, a) => ended.Add((q, a)));
        check(!queue.Showing && !queue.Answer(ConfirmAnswer.Confirm), "confirm: nothing shows before a question");
        var a = queue.Ask("modA", "a");
        var b = queue.Ask("modB", "b");
        check(a > 0 && b > a && queue.Showing && queue.Current == "a", "confirm: one question shows, the next waits");
        var version = queue.Version;
        check(queue.Answer(ConfirmAnswer.Confirm) && ended.Count == 1 && ended[0] == ("a", ConfirmAnswer.Confirm), "confirm: the answer reaches its question once");
        check(queue.Current == "b" && queue.Version != version, "confirm: the next question shows");
        check(queue.Answer(ConfirmAnswer.Cancel) && ended[1] == ("b", ConfirmAnswer.Cancel) && !queue.Showing, "confirm: cancel is an answer of its own");

        ended.Clear();
        for (var i = 0; i <= ConfirmQueue<string>.Pending; i++) queue.Ask("modA", "q" + i);
        check(queue.Count == ConfirmQueue<string>.Pending + 1 && ended.Count == 0, "confirm: one shown and the pending ones wait");
        check(queue.Ask("modA", "late") == 0 && ended.Count == 1 && ended[0] == ("late", ConfirmAnswer.Dismissed), "confirm: a full queue refuses at once, never silently");

        ended.Clear();
        queue.WithdrawOwner("modA");
        var x = queue.Ask("modA", "x");
        var y = queue.Ask("modB", "y");
        var z = queue.Ask("modA", "z");
        ended.Clear();
        check(queue.Withdraw(y) && !queue.Withdraw(y) && ended.Count == 1 && ended[0] == ("y", ConfirmAnswer.Dismissed) && queue.Current == "x",
            "confirm: a waiting question is withdrawn once by its ticket");
        queue.WithdrawOwner("modA");
        check(!queue.Showing && ended.Count == 3, "confirm: an owner withdraws all of its questions");
        check(!queue.Withdraw(x) && !queue.Withdraw(z), "confirm: an ended ticket finds nothing");

        ended.Clear();
        ConfirmQueue<string>? self = null;
        self = new ConfirmQueue<string>((q, answer) => { ended.Add((q, answer)); if (q == "first") self!.Ask("m", "again"); });
        self.Ask("m", "first");
        self.Ask("m", "second");
        self.Answer(ConfirmAnswer.Confirm);
        check(self.Current == "second" && self.Count == 2, "confirm: a question asked from an answer waits behind the rest");
        self.WithdrawOwner("m");
        check(!self.Showing && ended.Count == 3, "confirm: withdrawing an owner ends each of its questions once");
    }

    private static void Tooltip(Action<bool, string> check)
    {
        // Bounds: 200 x 100 round the origin. Anchor 20 high at (0, 0).
        var (x, y) = TooltipLayout.Place(0f, 0f, 10f, 60f, 20f, 4f, 0f, 0f, 100f, 50f, out var below);
        check(!below && x == 0f && y == 24f, "tooltip: above the anchor when there is room");
        (x, y) = TooltipLayout.Place(0f, 35f, 10f, 60f, 20f, 4f, 0f, 0f, 100f, 50f, out below);
        check(below && y == 11f, "tooltip: flips under the anchor at the top edge");
        (x, _) = TooltipLayout.Place(95f, 0f, 10f, 60f, 20f, 4f, 0f, 0f, 100f, 50f, out _);
        check(x == 70f, "tooltip: kept inside the right edge");
        (x, _) = TooltipLayout.Place(-95f, 0f, 10f, 60f, 20f, 4f, 0f, 0f, 100f, 50f, out _);
        check(x == -70f, "tooltip: kept inside the left edge");
        (x, _) = TooltipLayout.Place(50f, 0f, 10f, 400f, 20f, 4f, 0f, 0f, 100f, 50f, out _);
        check(x == 0f, "tooltip: wider than the bounds it is centred");
        TooltipLayout.Place(0f, 0f, 49f, 60f, 20f, 4f, 0f, 0f, 100f, 50f, out below);
        check(!below, "tooltip: no room either side stays above");
    }
}
