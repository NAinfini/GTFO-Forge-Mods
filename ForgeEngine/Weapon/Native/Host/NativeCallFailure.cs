using System;

namespace ForgeWeapon.Native;

/// <summary>
/// Where a native call that threw is written down when its caller keeps going with a named refusal or a read it
/// marks unreadable. The caller's own answer is unchanged; what this adds is the exception type and message of the
/// first failure at each call site, so a refusal code is never the only trace of why the game's object could not be
/// read. A site that keeps failing the same way is written the 1st, 10th, 100th ... time, not once per frame.
/// </summary>
internal static class NativeCallFailure
{
    /// <summary>The Forge Engine log code, registered in the central diagnostic registry.</summary>
    internal const string Code = "weapon.native-call-failed";

    private static readonly ForgeCore.OnceReporter Written = new();

    /// <summary>The live session's error-level log writer, set while a session exists. Without one the failure is
    /// still kept in <see cref="Last"/>.</summary>
    internal static Action<string, string>? Sink { get; set; }

    /// <summary>The most recent failure noted, whether or not it reached the log. Offline suites read it; a later
    /// success never clears it.</summary>
    internal static string? Last { get; private set; }

    /// <summary>Notes one failure of <paramref name="site"/>. Never throws: a log that cannot be written leaves the
    /// failure in <see cref="Last"/> with the logging failure beside it.</summary>
    internal static void Note(string site, Exception error)
    {
        var text = "site=" + site + " " + Describe(error);
        Last = text;
        if (!Written.ShouldReport(site + "|" + error.GetType().Name)) return;
        try { Sink?.Invoke(Code, text); }
        catch (Exception logging) { Last = text + "; log write failed: " + Describe(logging); }
    }

    /// <summary>The type and a bounded message, with a message getter that throws reported rather than hiding the failure.</summary>
    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        if (message.Length > 1900) message = message[..1900];
        return error.GetType().Name + ": " + message;
    }
}
