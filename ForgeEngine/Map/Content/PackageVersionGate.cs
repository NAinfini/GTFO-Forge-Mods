using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Version rule of an exported package. Formats only grow: older content is a valid subset of the current
/// format and is read directly, so the only version question is whether the package needs a newer base package.
/// A package whose optional top-level rundown.forge field <c>minBasePackageVersion</c> ("major.minor.patch") is not met by this base
/// package under <see cref="ForgeVersion"/>'s rule (same major, installed minor at or above the stated one, patch ignored) is refused with the fixed code <see cref="NeedsUpdateCode"/>.</summary>
internal static class PackageVersionGate
{
    internal const string NeedsUpdateCode = "package-needs-base-update";
    internal const string MinimumBaseField = "minBasePackageVersion";

    internal static Version BaseVersion { get; } = Normalize(typeof(PackageVersionGate).Assembly.GetName().Version ?? new Version(1, 0, 0));

    /// <summary>Returns null when the package is readable, otherwise the refusal as (code, detail).</summary>
    internal static (string Code, string Detail)? Check(string rundownText, Version? baseVersion = null)
    {
        baseVersion ??= BaseVersion;
        JsonNode? node;
        try { node = JsonNode.Parse(rundownText); }
        catch (JsonException) { return null; } // malformed JSON is the Rundown reader's refusal, not a version question
        if (node is not JsonObject root || root[MinimumBaseField] is not { } minimum) return null;
        string text = minimum is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
        if (!Version.TryParse(text, out var required) || required.Revision > 0 || required.Build < 0)
            return ("document-invalid", MinimumBaseField + ": expected major.minor.patch");
        return !ForgeVersion.Meets(baseVersion.Major, baseVersion.Minor, required.Major, required.Minor) ? (NeedsUpdateCode, MinimumBaseField + "=" + required + " base=" + baseVersion) : null;
    }

    private static Version Normalize(Version version) => new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
}
