using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The two-point rows' stateless helpers: the distance between two positions and the unit direction from
/// one to the other. Both are functions of the plan's own ports, so a `pure` step stays re-evaluable on demand and
/// reads no world — a position reaches these rows as a value, never as an entity to look up.
///
/// The query tier measures world positions too, but its `spatial-overflow` refusal is part of that tier's own
/// contract and is asserted there, so the two measurements stay separate rather than sharing one code.</summary>
internal static class VectorNodes
{
    /// <summary>The Euclidean distance between two positions, in the unit both ports carry. The difference is taken
    /// component-wise before the length, and the length is scaled, so neither a pair of far-apart positions nor a
    /// pair of very close ones loses the answer to an overflowing intermediate square.</summary>
    public static double Distance(IReadOnlyList<double> from, IReadOnlyList<double> to)
        => Length(to[0] - from[0], to[1] - from[1], to[2] - from[2]);

    /// <summary>The unit vector pointing from `from` to `to`. Two positions that coincide have no direction, so the
    /// row refuses by name instead of answering a zero vector whose length a consumer would divide by.</summary>
    public static double[] Direction(IReadOnlyList<double> from, IReadOnlyList<double> to)
    {
        var x = to[0] - from[0];
        var y = to[1] - from[1];
        var z = to[2] - from[2];
        var length = Length(x, y, z);
        if (length == 0d) throw new RuntimeContractException("pure-zero-vector", "Two coincident positions have no direction.");
        return new[] { x / length, y / length, z / length };
    }

    /// <summary>The length of three components, scaled by the largest of them. A length that is still not finite
    /// has no value and is refused by name, never answered as infinity or NaN.</summary>
    private static double Length(double x, double y, double z)
    {
        var scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        if (scale == 0d) return 0d;
        x /= scale; y /= scale; z /= scale;
        var length = scale * Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length)) throw new RuntimeContractException("pure-nonfinite-result", "The vector length overflowed and has no value.");
        return length;
    }
}
