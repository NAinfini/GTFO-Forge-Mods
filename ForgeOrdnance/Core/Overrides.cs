using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeOrdnance;

/// <summary>One number a payload type lets a placement set (<c>Overrides</c> of a level drop, <see cref="OrdnanceApi.Place"/>): its
/// name, the range it must lie in and whether it must be whole. The payload's own numbers keep their own ranges; the framework only
/// checks this declaration and hands the values to <see cref="OrdnanceExecution.Overrides"/>.</summary>
public sealed record OrdnanceOverrideField(string Name, double Min, double Max, bool Whole = false);

/// <summary>The framework's half of override checking: names and ranges against what the payload type declared.</summary>
internal static class OverrideCheck
{
    internal static readonly IReadOnlyDictionary<string, double> None = new Dictionary<string, double>();

    /// <summary>Null when every value is a declared field inside its range; otherwise the first problem, naming the field.</summary>
    internal static string? Check(IReadOnlyList<OrdnanceOverrideField> fields, IReadOnlyDictionary<string, double> values, string payloadType)
    {
        foreach (var (name, value) in values)
        {
            OrdnanceOverrideField? field = null;
            foreach (var candidate in fields) if (candidate.Name == name) { field = candidate; break; }
            if (field == null)
                return name + ": payload " + payloadType + " does not accept this field" + (fields.Count == 0 ? " (it accepts no Overrides)" : " (it accepts " + string.Join(", ", Names(fields)) + ")");
            if (!double.IsFinite(value) || value < field.Min || value > field.Max || field.Whole && value != Math.Floor(value))
                return name + ": must be " + (field.Whole ? "a whole number " : "a number ") + field.Min.ToString(CultureInfo.InvariantCulture) + ".." + field.Max.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static IEnumerable<string> Names(IReadOnlyList<OrdnanceOverrideField> fields)
    {
        foreach (var field in fields) yield return field.Name;
    }
}
