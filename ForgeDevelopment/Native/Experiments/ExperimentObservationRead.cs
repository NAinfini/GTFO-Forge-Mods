using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>Read-only command evidence. A real terminal null is a value; a missing member, failed getter,
/// or null before the requested leaf is not. Collections retain every index and fail if any read fails.</summary>
internal static class ExperimentObservationRead
{
    internal readonly record struct Result(bool Ok, object? Value, string Error);

    internal static Result Read(object? target, string path)
    {
        if (target == null) return new(false, null, "the target resolved to nothing");
        if (path.Length == 0) return new(true, target, "");
        var result = VanillaValueReading.ReadMember(target, path);
        if (result.Available && (result.Outcome != "explicit-null" || result.NullTarget == path))
            return new(true, result.Value, "");
        return new(false, null, "'" + path + "': " + (result.Outcome == "explicit-null"
            ? "null-before-leaf at " + result.NullTarget : result.Outcome + ": " + result.Error));
    }

    internal static Result ReadMany(IReadOnlyList<object?> targets, string path, Func<object?, string> format)
    {
        if (targets.Count == 0) return new(false, null, "the target collection is empty");
        var values = new string[targets.Count];
        var errors = new List<string>();
        for (var index = 0; index < targets.Count; index++)
        {
            var result = Read(targets[index], path);
            var label = "#" + index.ToString(CultureInfo.InvariantCulture);
            values[index] = label + "=" + (result.Ok ? result.Value == null ? "null" : format(result.Value) : "[unavailable]");
            if (!result.Ok) errors.Add(label + ": " + result.Error);
        }
        return new(errors.Count == 0, string.Join(" | ", values), string.Join(" | ", errors));
    }
}
