using System.IO;

namespace ForgeDevelopment.Native;

// Pure capture policy. A missing native read is never a false boolean, an unlocked door,
// an empty lock inventory, or a guessed direction.
internal sealed class LevelGuideDoorFact
{
    public string Status { get; }
    public object? Value { get; }
    public string Source { get; }
    public string? MissingReason { get; }

    internal LevelGuideDoorFact(string status, object? value, string source, string? missingReason)
    {
        if (string.IsNullOrWhiteSpace(source)
            || (status != "observed" && status != "unknown")
            || (status == "observed" && missingReason != null)
            || (status == "unknown" && (value != null || string.IsNullOrWhiteSpace(missingReason))))
            throw new InvalidDataException("A door fact requires explicit native evidence or an unknown reason.");
        Status = status; Value = value; Source = source; MissingReason = missingReason;
    }
}

internal static class LevelGuideCaptureDoorFacts
{
    internal static LevelGuideDoorFact Observed(string source, object? value)
        => new("observed", value, source, null);

    internal static LevelGuideDoorFact Unknown(string source, string reason)
        => new("unknown", null, source, reason);

    internal static string? Readiness(bool setupObserved, bool syncPresent, bool replicatedStatePresent)
        => !setupObserved ? "door-setup-uninitialized"
            : !syncPresent ? "door-sync-implementation-unavailable"
            : !replicatedStatePresent ? "door-sync-state-uninitialized" : null;
}
