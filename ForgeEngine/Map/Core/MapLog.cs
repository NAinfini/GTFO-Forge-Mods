using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The Map provider's diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG). Each code has one fixed
/// level, written beside it. A fact the kernel accepted or refused is never logged here: the kernel already writes
/// `trigger.fired` for every queued event and `event.rejected` for every refusal, under this provider's binding. What
/// is left is what only this package sees: the map field documents it accepted or refused, the level it is in, and
/// what a level build wrote.
///
/// Every record carries its sentence as the detail (message only), so the line a reader sees is the text the package
/// used to print; the values are what a reader filters on. The refusals of the other authored tables (fixed
/// placements, wave anchors, spawn placement, room transforms) stay BepInEx warnings with their own
/// codes: a refusal is the one line allowed to stay a warning.
/// Scan coverage also records its typed refusal in forge.log so the author can locate its exact row.
/// </summary>
internal static partial class MapLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/MapLogCodes.g.cs).
}

/// <summary>One diagnostic a pure reader of this package met: the code, the sentence that says why, and the document it
/// came from. The reader hands it on and never decides where it is written, so a focused test reads the same values
/// the host logs. The sentence reaches the console line only; the document is the value a log reader filters on.</summary>
internal readonly record struct MapDiagnostic(string Code, string Detail, string? File = null)
{
    /// <summary>The line as a person reads it: the code, then the sentence.</summary>
    public override string ToString() => Code + " " + Detail;
}

/// <summary>
/// The Map package's one way into the Forge Engine log: the registration's own <see cref="RuntimeModuleHandle.Log"/>,
/// shared by every part of the package. It is attached once the registration exists and detached with it; nothing is
/// written before that, and every record point asks <see cref="Logs"/> before it builds a value array, so a closed
/// level allocates nothing.
///
/// A record point sits inside level-build callbacks, and a line that cannot be written must not abort one:
/// <see cref="Write"/> keeps the failure's type in <see cref="LastFailure"/> instead of throwing it into the game.
/// </summary>
internal static class MapLog
{
    private static RuntimeModuleHandle? _handle;

    /// <summary>Hands the registration over. Called once, right after the registration exists.</summary>
    public static void Attach(RuntimeModuleHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        ResetContained();
    }

    /// <summary>Releases the registration this log writes through, if it is still the attached one.</summary>
    public static void Detach(RuntimeModuleHandle handle)
    {
        if (ReferenceEquals(_handle, handle)) _handle = null;
    }

    /// <summary>Whether a registration is attached, whatever level it logs: what a sentence reported earlier waits for.</summary>
    public static bool IsAttached => _handle is { IsRegistered: true };

    /// <summary>Whether a record at <paramref name="level"/> would be written: one comparison once attached.</summary>
    public static bool Logs(RuntimeLogLevel level) => _handle is { IsRegistered: true } handle && handle.Logs(level);

    /// <summary>The type name of the last write the kernel or its sink refused, or null.</summary>
    public static string? LastFailure { get; private set; }

    /// <summary>One record with the facts already built. Callers that build a value array ask <see cref="Logs"/>
    /// first. Answers false when the write failed; the failure is kept in <see cref="LastFailure"/> and written once per
    /// code and exception type as a contained failure, which carries three values and so is not refused the same way.</summary>
    public static bool Write(RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        if (_handle is not { IsRegistered: true } handle) return true;
        try { handle.Log(level, code, in facts); return true; }
        catch (Exception error) { LastFailure = error.GetType().Name; Contained("map-log.write " + code, error); return false; }
    }

    /// <summary>The common shape: a code, its sentence as the detail, and the values a reader filters on. Answers whether
    /// the record was written (true also when the level is closed and nothing was asked for).</summary>
    public static bool Record(RuntimeLogLevel level, string code, string detail, params RuntimeLogValue[] values)
    {
        if (!Logs(level)) return true;
        return Write(level, code, new RuntimeLogFacts { Detail = SafeDetail(detail), Values = values.Length == 0 ? null : values });
    }

    private static readonly ForgeCore.OnceReporter ContainedCounts = new();

    /// <summary>A deliberate catch site records the exception it contained: the site name, the exception type and its
    /// message, and how often that site has caught that type. A site that fails every frame writes at the 1st, 10th,
    /// 100th ... occurrence instead of flooding the log (ForgeCore's `OnceReporter` counts, the Engine's
    /// <see cref="ContainedFailure"/> writes). Never throws.</summary>
    public static void Contained(string site, Exception error)
    {
        var key = ContainedFailure.Key(site, error);
        if (ContainedCounts.ShouldReport(key))
            ContainedFailure.Write(_handle, MapLogCodes.ExceptionContained, site, error, ContainedCounts.Count(key));
    }

    /// <summary>Forgets the occurrence counts so a new session starts at the 1st again.</summary>
    public static void ResetContained() => ContainedCounts.Reset();
    /// <summary>One reader diagnostic, as an error record under its own code, carrying the document it came from.</summary>
    public static void Refused(MapDiagnostic diagnostic)
    {
        if (diagnostic.File is { } file) Record(RuntimeLogLevel.Error, diagnostic.Code, diagnostic.Detail, RuntimeLogValue.Quote("file", file));
        else Record(RuntimeLogLevel.Error, diagnostic.Code, diagnostic.Detail);
    }

    /// <summary>One package-atomic admission failure. Identity comes from the actual rejected package and
    /// document; the registration and kernel stamp provider, tick and world epoch. Authored text uses the SDK's
    /// safe preview policy, so a malformed package name or path cannot suppress its own rejection record.</summary>
    public static void PackageRejected(string package, string document, string path, string reason, string detail, int? slot = null)
    {
        if (!Logs(RuntimeLogLevel.Error)) return;
        // 1 to 8 is a menu position; 0 is the single-Rundown package, which has no position.
        if (slot is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(slot));
        var values = new System.Collections.Generic.List<RuntimeLogValue> { RuntimeLogValue.Quote("package", package), RuntimeLogValue.Quote("document", document),
            RuntimeLogValue.Quote("path", path), RuntimeLogValue.Quote("reason", reason) };
        // An absent slot means no valid canonical Rundown established one. Never infer it from a bad document.
        if (slot is { } known) values.Add(RuntimeLogValue.Of("slot", known));
        if (reason == "gen-hook-unavailable" && detail.Contains("native-hook-", StringComparison.Ordinal))
        {
            // I-DIAG's message is a 256-character preview and each scalar also has a 256-character limit.
            // Retain the complete exception message (including declaration/target) as ordered scalar parts.
            // The BepInEx rejection still carries the stack. No changes to the shared log wire format.
            int end = detail.IndexOfAny(new[] { '\r', '\n' });
            string message = SafeDetail(end < 0 ? detail : detail[..end]);
            const int maximum = 10 * 256;
            bool truncated = message.Length > maximum;
            if (truncated) message = message[..maximum];
            for (int at = 0, part = 0; at < message.Length; at += 256, part++)
                values.Add(RuntimeLogValue.Of("detail" + part, SafeDetail(message.Substring(at, Math.Min(256, message.Length - at)))));
            if (truncated) values.Add(RuntimeLogValue.Of("detailTruncated", true));
        }
        // log-values-max: 16 -- package, document, path, reason, slot, at most ten 256-character detail parts and detailTruncated.
        Record(RuntimeLogLevel.Error, MapLogCodes.PackageRejected, detail, values.ToArray());
    }

    /// <summary>A text for a record's detail or a values chunk: a text holding an account number (Runtime's own Steam64
    /// judgement, asked through <see cref="RuntimeLogValue.Quote"/>, which checks the whole text) is replaced by
    /// `account-shaped` whole before it is cut into chunks, and a control character becomes a space.</summary>
    public static string SafeDetail(string text)
    {
        if (RuntimeLogValue.Quote("detail", text).Text == "account-shaped") return "account-shaped";
        var control = false;
        foreach (var character in text)
            if (character < ' ' || character == '\u007f') { control = true; break; }
        if (!control) return text;
        var chars = text.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
            if (chars[index] < ' ' || chars[index] == '\u007f') chars[index] = ' ';
        return new string(chars);
    }
}
