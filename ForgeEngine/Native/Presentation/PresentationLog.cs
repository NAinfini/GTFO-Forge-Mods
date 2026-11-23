using System;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation.Native;

/// <summary>The presentation provider's diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG). Each has one
/// fixed level.</summary>
internal static partial class PresentationLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/PresentationLogCodes.g.cs).
}

/// <summary>
/// The presentation provider's way into the Forge Engine log: its registration's own <see cref="RuntimeModuleHandle.Log"/>.
/// The host hands the registration over once it exists; with none attached (every offline suite) nothing is written.
/// </summary>
internal static class PresentationLog
{
    private static readonly Regex Source = new(@"^([a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+)[: ]", RegexOptions.CultureInvariant);
    private static readonly ForgeCore.OnceReporter ContainedCounts = new();
    private static RuntimeModuleHandle? _handle;

    internal static void Attach(RuntimeModuleHandle handle) => _handle = handle ?? throw new ArgumentNullException(nameof(handle));

    internal static void Detach(RuntimeModuleHandle handle)
    {
        if (ReferenceEquals(_handle, handle)) _handle = null;
        ContainedCounts.Reset();
    }

    private static bool Logs(RuntimeLogLevel level) => _handle is { } handle && handle.Logs(level);

    private static void Record(string code, string detail, params RuntimeLogValue[] values)
    {
        if (_handle is not { } handle || !handle.Logs(RuntimeLogLevel.Error)) return;
        var facts = new RuntimeLogFacts { Detail = detail, Values = values };
        handle.Log(RuntimeLogLevel.Error, code, in facts);
    }

    /// <summary>One refusal sentence, as an error record. Never throws: a report is written from inside handlers.</summary>
    internal static void Report(string message)
    {
        if (message == null || !Logs(RuntimeLogLevel.Error)) return;
        try
        {
            var match = Source.Match(message);
            if (match.Success)
                Record(PresentationLogCodes.NativeReport, message, RuntimeLogValue.Quote("source", match.Groups[1].Value));
            else Record(PresentationLogCodes.NativeReport, message);
        }
        catch (Exception) { /* The log is diagnostic; a writer failure must not change the row's result. */ }
    }

    /// <summary>An exception a body deliberately contained. One site and type is written at its 1st, 10th, 100th…
    /// occurrence, so a repeating fault stays visible without flooding the log.</summary>
    internal static void Contained(string site, Exception error)
    {
        var key = ContainedFailure.Key(site, error);
        if (ContainedCounts.ShouldReport(key))
            ContainedFailure.Write(_handle, PresentationLogCodes.ExceptionContained, site, error, ContainedCounts.Count(key));
    }
}
