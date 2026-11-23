using System;
using System.Runtime.CompilerServices;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEnemy;

/// <summary>
/// The enemy provider's diagnostic codes in the Forge Engine log (contract §3.2 I-DIAG, 包内部原生诊断码). Each code
/// has one fixed level, written beside it. A fact the kernel accepted or refused is never logged here: the kernel
/// already writes `trigger.fired` for every queued event and `event.rejected` / `budget.exceeded` for every refusal,
/// under this provider's binding, so a line of this package's own for the same fact would record it twice. What is
/// left is what only this package sees: a failure it met before the kernel was reached, what an enemy profile could
/// and could not write, and the per-life diagnostics that exist at trace only.
/// </summary>
internal static partial class EnemyLogCodes
{
    // The constants are generated from catalog/authoring/diagnostic-codes.json (artifacts/generated/diagnostics/EnemyLogCodes.g.cs).
}

/// <summary>
/// The enemy package's one way into the Forge Engine log: the registration's own <see cref="RuntimeModuleHandle.Log"/>,
/// shared by every part of the session. It is attached once the registration exists; nothing is written before that,
/// and every caller asks <see cref="Logs"/> before it builds a value array, so a closed level allocates nothing.
///
/// A record point sits inside native callbacks (a spawn, a prefab build, a damage window), and a line that cannot be
/// written must not abort one: <see cref="Write"/> keeps the failure's type in <see cref="LastFailure"/> instead of
/// throwing it into the game.
/// </summary>
internal sealed class EnemyLog
{
    private RuntimeModuleHandle? _handle;

    /// <summary>Hands the registration over. Called once, right after the registration exists.</summary>
    public void Attach(RuntimeModuleHandle handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _current = this;
        ContainedSites.Reset();
    }

    private static readonly OnceReporter ContainedSites = new();
    private static EnemyLog? _current;

    /// <summary>A deliberate catch site records the exception it contained: where it was caught, the exception type and
    /// its message, and how often that place has caught that type; the 1st, 10th, 100th ... occurrence is written
    /// (ForgeCore\'s <see cref="OnceReporter"/>). The caller's own answer is unchanged. Never throws; before the session's log is
    /// attached nothing is written.</summary>
    public static void Contained(Exception error, [CallerMemberName] string member = "", [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var site = member + "@" + file[(file.LastIndexOfAny(new[] { '\\', '/' }) + 1)..] + ":" + line;
        var key = ContainedFailure.Key(site, error);
        if (ContainedSites.ShouldReport(key))
            ContainedFailure.Write(_current?._handle, EnemyLogCodes.ExceptionContained, site, error, ContainedSites.Count(key));
    }
    /// <summary>Whether a record at <paramref name="level"/> would be written: one comparison once attached.</summary>
    public bool Logs(RuntimeLogLevel level) => _handle != null && _handle.Logs(level);

    /// <summary>The type name of the last write the kernel or its sink refused, or null.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>One record with the facts already built. Callers that build a value array ask <see cref="Logs"/> first.
    /// Answers false when the write failed; the failure is kept in <see cref="LastFailure"/>.</summary>
    public bool Write(RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        if (_handle == null) return true;
        // The type is kept in LastFailure for the caller, and the refusal is written once per code and exception type as a
        // contained failure: that record carries three values, so a record refused for its shape still leaves a line.
        try { _handle.Log(level, code, in facts); return true; }
        catch (Exception error) { LastFailure = error.GetType().Name; Contained(error, "write " + code); return false; }
    }

    /// <summary>The common shape: a code, the reason it happened, and the entity and binding it is about. Nothing is
    /// allocated unless the level is open.</summary>
    public void Reason(RuntimeLogLevel level, string code, string reason, string? entity = null, string? binding = null)
    {
        if (!Logs(level)) return;
        Write(level, code, new RuntimeLogFacts
        {
            Entity = entity,
            Binding = binding,
            Values = new[] { RuntimeLogValue.Of("reason", reason) }
        });
    }

    /// <summary>A failure: the exception type as a value and its message as the detail (values.exception).</summary>
    public void Exception(RuntimeLogLevel level, string code, Exception error)
    {
        if (!Logs(level)) return;
        Write(level, code, new RuntimeLogFacts
        {
            Detail = RuntimeDiagnostics.Describe(error),
            Values = new[] { RuntimeLogValue.Of("exception", error.GetType().Name) }
        });
    }
}
