using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

internal static class ShutdownSequence
{
    // Preserve original exceptions for callers even when the reporting sink itself fails.
    internal static IReadOnlyList<(string Stage, Exception Error)> Run(
        (string Stage, Action Action)[] steps, Action<string, Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(failed);
        var failures = new List<(string Stage, Exception Error)>();
        foreach (var step in steps)
        {
            try { step.Action(); }
            catch (Exception error)
            {
                failures.Add((step.Stage, error));
                try { failed(step.Stage, error); }
                catch (Exception reportingError)
                {
                    // Never recursively report through a failing sink or skip later cleanup.
                    failures.Add((step.Stage + "/error_report", reportingError));
                }
            }
        }
        return failures.AsReadOnly();
    }
}
