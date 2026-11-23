using ForgeEngine.Framework;

namespace ForgeMap.Native;

internal static partial class ObjectivePresentationLog
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/ObjectivePresentationLog.g.cs).
    internal static void State(string phase, string? level, int entries)
        => MapLog.Record(RuntimeLogLevel.Info, Recorded, "Objective display state retained; no objective progress changed.",
            RuntimeLogValue.Quote("phase", phase), RuntimeLogValue.Quote("level", level ?? "unavailable"), RuntimeLogValue.Of("entries", entries));
    internal static void Presented(string? level, int entries, int panels)
        => MapLog.Record(RuntimeLogLevel.Info, PresentedCode, "Local native panel writes returned; visual acceptance remains untested.",
            RuntimeLogValue.Quote("level", level ?? "unavailable"), RuntimeLogValue.Of("entries", entries), RuntimeLogValue.Of("panels", panels));
    internal static void Pending(string? level, int entries, string reason, string? site, long world, string phase)
        => MapLog.Record(RuntimeLogLevel.Info, PendingCode,
            phase == "ending" ? "World ended before native objective display withdrawal completed; no retry is retained."
                : phase == "skipped" ? "Panel has never received a native main getter; its main override is skipped until native initialization. Other presentation writes continue."
                : phase == "installed" ? "A skipped panel received its native main getter; the authored main text replaced it in the same native write."
                : "Native GUI or original main getter is not ready; Map clock retains projection work.",
            RuntimeLogValue.Quote("level", level ?? "unavailable"), RuntimeLogValue.Of("entries", entries),
            RuntimeLogValue.Quote("reason", reason), RuntimeLogValue.Quote("site", site ?? "none"),
            RuntimeLogValue.Of("world", world), RuntimeLogValue.Quote("phase", phase));
    internal static void Refused(string? level, string reason, string detail)
        => MapLog.Record(RuntimeLogLevel.Error, RefusedCode, detail,
            RuntimeLogValue.Quote("level", level ?? "unavailable"), RuntimeLogValue.Quote("reason", reason));
}
