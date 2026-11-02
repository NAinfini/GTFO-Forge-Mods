using ForgeOrdnance;

/// <summary>The level handshake's epoch admission. Message transport, queueing and version refusal are ForgeCore's channel
/// (tested in Mods/ForgeCore/tests).</summary>
internal static class NetworkChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var admission = new HelloAdmission();
        check(admission.Evaluate(7, 123, 0, 1) == HelloDecision.Challenge,
            "bootstrap hello cannot mutate compatibility or replay state before proving host epoch");
        check(admission.Evaluate(7, 123, 1, 1) == HelloDecision.Accept,
            "current-epoch handshake proof admits first synchronization");
        check(admission.Evaluate(7, 123, 1, 1) == HelloDecision.Ignore,
            "duplicate handshake proof cannot replay retained extension state twice");
        check(admission.Evaluate(7, 456, 1, 1) == HelloDecision.Accept,
            "restarted peer with a new greeting receives a fresh synchronization");
        check(admission.Evaluate(7, 123, 1, 1) == HelloDecision.Ignore,
            "delayed proof from an earlier connected process cannot supersede its replacement");
        admission.Clear();
        check(admission.Evaluate(7, 123, 1, 2) == HelloDecision.Ignore,
            "old-epoch hello proof remains stale even after recovery clears handshake history");
        check(admission.Evaluate(7, 123, 0, 2) == HelloDecision.Challenge
            && admission.Evaluate(7, 123, 2, 2) == HelloDecision.Accept,
            "old bootstrap can only receive a challenge, and only a new-epoch proof replays current state");
        check(admission.Evaluate(7, 0, 2, 2) == HelloDecision.Ignore,
            "host broadcast greeting cannot be admitted as a peer proof");
    }
}
