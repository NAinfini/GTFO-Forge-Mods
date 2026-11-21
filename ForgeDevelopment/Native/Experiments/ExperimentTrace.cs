using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

/// <summary>
/// Records which traced methods have fired, so a <c>wait.trace</c> step can block until one of them is
/// observed. The tracer's generated patch is the only writer; a wait that never sees its method fails on its
/// own timeout instead of hanging the run.
/// </summary>
internal static class ExperimentTrace
{
    private const int MaximumKeys = 512;

    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static volatile bool _capturing;

    internal static bool Capturing => _capturing;

    internal static void Start()
    {
        lock (Gate) Seen.Clear();
        _capturing = true;
    }

    internal static void Stop()
    {
        _capturing = false;
        lock (Gate) Seen.Clear();
    }

    /// <summary>Called by the tracer patch on the way back out of a traced method.</summary>
    internal static void Observe(string type, string method)
    {
        if (!_capturing) return;
        var key = type + "." + method;
        lock (Gate)
        {
            if (Seen.Count < MaximumKeys || Seen.Contains(key)) Seen.Add(key);
        }
    }

    /// <summary>Matches on the full type name, the short type name, or the method name alone.</summary>
    internal static bool HasSeen(string typeName, string methodName)
    {
        lock (Gate)
        {
            if (Seen.Count == 0) return false;
            foreach (var key in Seen)
            {
                var dot = key.LastIndexOf('.');
                if (dot <= 0) continue;
                if (!string.Equals(key[(dot + 1)..], methodName, StringComparison.Ordinal)) continue;
                var type = key[..dot];
                if (string.Equals(type, typeName, StringComparison.Ordinal) || string.Equals(ShortName(type), typeName, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    private static string ShortName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName[(dot + 1)..];
    }
}
