using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeDevelopment.Native;

/// <summary>A packaged trace profile that could not be listed or read; it is reported, never dropped silently.</summary>
internal readonly record struct RecTraceFailure(string Relative, string Code, string Reason);

internal static class RecTraceProfiles
{
    /// <summary>The package's own <c>probes/trace/*.forge</c> files, in ordinal order.</summary>
    internal static IReadOnlyList<string> Discover(string packageDirectory, Action<RecTraceFailure> rejected)
    {
        ArgumentNullException.ThrowIfNull(packageDirectory);
        ArgumentNullException.ThrowIfNull(rejected);
        try
        {
            if (Directory.Exists(packageDirectory))
                return Directory.GetFiles(packageDirectory, "*.forge").OrderBy(path => path, StringComparer.Ordinal).ToArray();
            rejected(new("probes/trace", "development-trace-path", "The packaged trace directory does not exist: " + packageDirectory));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException
            or UnauthorizedAccessException)
        {
            rejected(new("probes/trace", "development-trace-unreadable", error.Message));
        }
        return Array.Empty<string>();
    }

    internal static bool Enabled(RecTraceProfile profile, bool enableAllProfiles,
        Func<string, bool, bool> configured)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configured);
        if (profile.Error != null) return false;
        var selected = configured(profile.Name, profile.EnabledByDefault);
        return enableAllProfiles || selected;
    }
}
