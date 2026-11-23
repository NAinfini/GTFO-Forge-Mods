using System;

namespace ForgeEngine.Framework;

/// <summary>The one error record for an exception a package deliberately contained. The counting that decides
/// which occurrences are written (the 1st, 10th, 100th ... of one site and exception type) is ForgeCore's
/// `OnceReporter`, kept by the package; the record's shape and the rule that a log write never changes the contained
/// site's answer live here once, for every package.</summary>
internal static class ContainedFailure
{
    /// <summary>The counting key of one containment: the site and the exception type.</summary>
    public static string Key(string site, Exception error) => site + "|" + error.GetType().Name;

    /// <summary>Writes one `code` error record through <paramref name="handle"/>: detail `site: description`, values
    /// site, exception type and the running <paramref name="count"/>. Nothing is written without a registered handle or
    /// an open error level, and a writer failure is swallowed: the log is diagnostic.</summary>
    public static void Write(RuntimeModuleHandle? handle, string code, string site, Exception error, long count)
    {
        if (handle is not { IsRegistered: true } registered || !registered.Logs(RuntimeLogLevel.Error)) return;
        try
        {
            var facts = new RuntimeLogFacts
            {
                Detail = site + ": " + RuntimeDiagnostics.Describe(error),
                Values = new[] { RuntimeLogValue.Of("site", site), RuntimeLogValue.Of("exception", error.GetType().Name), RuntimeLogValue.Of("count", count) }
            };
            registered.Log(RuntimeLogLevel.Error, code, in facts);
        }
        catch (Exception) { /* The log is diagnostic: a writer failure must not change the contained site's answer. */ }
    }
}
