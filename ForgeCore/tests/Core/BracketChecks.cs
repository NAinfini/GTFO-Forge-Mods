namespace ForgeCore.Tests;

internal static class BracketChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        var bracket = new Bracket<int>("GameHooks.Test", typeof(BracketChecks));
        var seen = new List<string>();
        bracket.Around(call => { seen.Add("b1:" + call); return call * 10; }, (call, state, ran) => seen.Add("a1:" + call + ":" + state + ":" + ran), (call, state, error) => seen.Add("f1:" + state + ":" + error.Message));
        bracket.Before(call => seen.Add("b2:" + call));
        bracket.After((call, ran) => seen.Add("a2:" + call + ":" + ran));
        bracket.Around<int>(_ => throw new InvalidOperationException("before failed"), (_, state, _) => seen.Add("a3:" + state));
        check(bracket.Active, "bracket: active once subscribed");

        bracket.RunBefore(3);
        bracket.RunAfter(3, true);
        check(string.Join(",", seen) == "b1:3,b2:3,a3:0,a2:3:True,a1:3:30:True", "bracket: before in subscription order, after in reverse, state handed back (" + string.Join(",", seen) + ")");
        check(errors.Exists(e => e.Contains("before failed")), "bracket: a failing handler is one logged error and the others still run");

        seen.Clear();
        bracket.RunBefore(4);
        bracket.RunFaulted(4, new InvalidOperationException("boom"));
        check(string.Join(",", seen) == "b1:4,b2:4,f1:40:boom", "bracket: a faulted call reaches the faulted end only (" + string.Join(",", seen) + ")");

        seen.Clear();
        bracket.RunAfter(5, true);
        check(seen.Count == 0, "bracket: an after with no open call is ignored");

        var gate = new Bracket<int>("GameHooks.TestSkip", typeof(BracketChecks));
        check(!gate.Active && !gate.Skipped(1), "bracket: without subscribers nothing is skipped");
        gate.Skip(call => call == 2);
        gate.Skip(_ => throw new InvalidOperationException("skip failed"));
        check(gate.Active && gate.Skipped(2) && !gate.Skipped(3), "bracket: a skip subscriber keeps the body from running for the calls it names");
        check(errors.Exists(e => e.Contains("skip failed")), "bracket: a failing skip subscriber is one logged error and does not skip");
    }
}
