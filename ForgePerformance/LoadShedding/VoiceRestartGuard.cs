using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ForgePerformance.LoadShedding;

// VoiceRestartGuard. Dissonance's CapturePipelineManager.Update calls RestartTransmissionPipeline (StopCapture, then Microphone.Start,
// each a round trip to the Windows audio service) when the microphone reports it stopped recording, after a device change, when the
// network mode changes and after a frame skip (its own FrameSkipDetector backs off for frame skips only). A microphone that never
// stays recording is restarted every frame. A restart that follows the previous one within WindowSeconds is held back until the delay
// has passed; the delay starts at FirstDelay and doubles up to MaxDelay; a restart after QuietSeconds without one starts over. The
// held-back request is not lost: Update asks again on the next frame (the pending flag stays set), so the restart happens as soon as
// the delay is over. A working microphone restarts only rarely and is never delayed.
// FrameSkipFilter: Update's frame skip test (FrameSkipDetector.IsFrameSkip, a limit of a few frame times, then its own breaker) asks for
// a restart after a single slow frame, and the restart (100 to 180 ms of StopCapture and Microphone.Start) is a second hitch. A frame
// shorter than FrameSkipSeconds (Update is called with Time.deltaTime) no longer restarts the pipeline; a longer one does as before.
// The detector's breaker has already counted the skip, exactly as it does for a restart that ran, so the next skip waits for it.
internal static class VoiceRestartGuard
{
    internal const string Code = "perf.voice-restart-held-back", AllowedCode = "perf.voice-restart-allowed";
    internal const double FirstDelay = 0.25, MaxDelay = 5, QuietSeconds = 10;
    internal static bool Enabled, FrameSkipFilter;
    internal static float FrameSkipSeconds = 0.5f;
    internal const string FrameSkipCode = "perf.voice-frame-skip-ignored";
    private const string FrameSkipReason = "Detected a frame skip";
    private static long _skipIgnored;
    private static bool _skipLogged;
    private static long _last;
    private static double _delay;
    private static long _heldBack, _ran;
    private static bool _logged;
    private static readonly Dictionary<string, int> _reasons = new();

    // True when a frame skip restart is not wanted: the frame (deltaTime) was shorter than FrameSkipSeconds. Other reasons never pass.
    internal static bool IgnoreFrameSkip(string? reason, float deltaTime, Action<string> log)
    {
        if (!FrameSkipFilter || reason == null || deltaTime >= FrameSkipSeconds
            || !reason.StartsWith(FrameSkipReason, StringComparison.Ordinal)) return false;
        _skipIgnored++;
        if (!_skipLogged)
        {
            _skipLogged = true;
            log("type=voice_restart_guard\tcode=" + FrameSkipCode + "\tstate=first\tframe_s=" + deltaTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + "\tlimit_s=" + FrameSkipSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        return true;
    }

    // True when this restart may run now.
    internal static bool Allow(string reason, Action<string> log)
    {
        var now = Stopwatch.GetTimestamp();
        var gap = _last == 0 ? double.MaxValue : (now - _last) / (double)Stopwatch.Frequency;
        if (gap > QuietSeconds) _delay = 0;
        else if (gap < _delay)
        {
            _heldBack++;
            if (!_logged)
            {
                _logged = true;
                log("type=voice_restart_guard\tcode=" + Code + "\tstate=first\tdelay_s=" + _delay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + "\treason=" + reason);
            }
            return false;
        }
        else _delay = _delay == 0 ? FirstDelay : Math.Min(MaxDelay, _delay * 2);
        _last = now; _ran++;
        // Restarts that ran, by Dissonance's reason (the text before a " (" detail such as the frame skip's delta time); a new reason is logged at once.
        var key = reason ?? "";
        var cut = key.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) key = key.Substring(0, cut);
        _reasons.TryGetValue(key, out var seen);
        _reasons[key] = seen + 1;
        if (seen == 0) log("type=voice_restart_guard\tcode=" + AllowedCode + "\tstate=reason\treason=" + reason);
        return true;
    }

    internal static string Summary()
    {
        var reasons = new List<string>();
        foreach (var pair in _reasons) reasons.Add(pair.Key + "=" + pair.Value);
        return "restarts_run=" + _ran + "\trestarts_held_back=" + _heldBack + "\tframe_skips_ignored=" + _skipIgnored + "\treasons=" + string.Join(";", reasons);
    }

    internal static void Reset() { Enabled = FrameSkipFilter = false; _skipIgnored = 0; _skipLogged = false; _last = 0; _delay = 0; _heldBack = _ran = 0; _logged = false; _reasons.Clear(); }
}
