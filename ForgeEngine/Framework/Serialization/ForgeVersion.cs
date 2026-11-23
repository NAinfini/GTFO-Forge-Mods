using System;
using System.Globalization;

namespace ForgeEngine.Framework;

/// <summary>The one compatibility rule of every Forge package (contract 1.4): only a change of the leading major
/// number breaks. A requirement is a major plus the lowest minor; it is met by an installed version with the same
/// major and a minor at or above it. Patch numbers never matter. Written once here for the host and the plan loader.
/// Public so the base packages apply the very same rule to what they read (a plan's recorded runtime, the release they were built into) instead of copying it.</summary>
internal static class ForgeVersion
{
    internal static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text == null || !Version.TryParse(text, out var parsed) || parsed.Major < 0 || parsed.Minor < 0) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public static bool Meets(int installedMajor, int installedMinor, int requiredMajor, int requiredMinor)
        => installedMajor == requiredMajor && installedMinor >= requiredMinor;

    /// <summary>Whether <paramref name="installed"/> meets a requirement written as a full version (major and lowest
    /// minor are read, the patch is ignored). An unparseable text on either side does not meet it.</summary>
    public static bool Meets(string? installed, string? required)
        => TryParse(installed, out var have) && TryParse(required, out var need) && Meets(have.Major, have.Minor, need.Major, need.Minor);

    /// <summary>Whether <paramref name="installed"/> meets a requirement recorded as a major and its lowest minor, as plans and packs write it.</summary>
    public static bool Meets(string? installed, int requiredMajor, int requiredMinor)
        => TryParse(installed, out var have) && Meets(have.Major, have.Minor, requiredMajor, requiredMinor);

    /// <summary>Whether two installed versions are of the same major: two peers running together. A spelling that is not a version matches only itself.</summary>
    public static bool SameMajor(string? a, string? b)
        => TryParse(a, out var x) && TryParse(b, out var y) ? x.Major == y.Major : a == b;

    /// <summary>The part of a version two peers must share: its major, or the whole text when it is not a version.</summary>
    internal static string Major(string? text)
        => TryParse(text, out var version) ? version.Major.ToString(CultureInfo.InvariantCulture) : text ?? "";

    /// <summary>The sentence a player reads when a requirement is not met: which version is needed against which is installed.</summary>
    internal static string Describe(string installed, int requiredMajor, int requiredMinor)
        => "needs " + requiredMajor.ToString(CultureInfo.InvariantCulture) + "." + requiredMinor.ToString(CultureInfo.InvariantCulture)
            + " or newer of the same major version, installed is " + installed;
}
