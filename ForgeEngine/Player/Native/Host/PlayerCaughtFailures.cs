using ForgeEngine.Framework;
using System;

namespace ForgePlayer.Native;

/// <summary>Where a caught native failure is written when the caller can only answer with a result code. A row
/// carries the code and not the exception, so the exception type and message go to the Player error log here, at the
/// moment they are caught; `site` names where. The session installs its error reporter for its lifetime; outside a session (tests that
/// drive an action directly) there is no log and the record is dropped.</summary>
internal static class PlayerCaughtFailures
{
    private static Action<string>? _report;

    internal static void Attach(Action<string> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Notes.Clear();
    }

    private const int NoteLimit = 20;
    private static readonly System.Collections.Generic.Dictionary<string, int> Notes = new(StringComparer.Ordinal);

    /// <summary>Writes `site: detail` for a refusal that has no exception: the code on the row is the whole answer, and
    /// the facts it was judged against go to the log. At most 20 notes per site and session.</summary>
    internal static void Note(string site, string detail)
    {
        var report = _report;
        if (report == null) return;
        Notes.TryGetValue(site, out var count);
        if (count >= NoteLimit) return;
        Notes[site] = count + 1;
        try { report(site + ": " + detail); }
        catch (Exception reporter) { LastNoteFailure = reporter; }
    }

    internal static Exception? LastNoteFailure { get; private set; }

    internal static void Detach(Action<string> report)
    {
        if (ReferenceEquals(_report, report)) _report = null;
    }

    /// <summary>Writes `site: ExceptionType: message`. A reporter that throws cannot replace the failure it reports,
    /// so its own exception rides on the original one's Data.</summary>
    internal static void Capture(string site, Exception error)
    {
        var report = _report;
        if (report == null) return;
        try { report(site + ": " + RuntimeDiagnostics.Describe(error)); }
        catch (Exception reporter) { error.Data["ForgePlayer.ReportFailure"] = reporter; }
    }
}
