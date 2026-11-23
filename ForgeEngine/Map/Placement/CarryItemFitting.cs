using System;

namespace ForgeMap;

/// <summary>An axis-aligned box in one frame, as six numbers.</summary>
internal readonly record struct FitBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public double SizeX => MaxX - MinX;
    public double SizeY => MaxY - MinY;
    public double SizeZ => MaxZ - MinZ;
    public double CenterX => (MinX + MaxX) * .5;
    public double CenterY => (MinY + MaxY) * .5;
    public double CenterZ => (MinZ + MaxZ) * .5;
    public bool IsFinite => double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MinZ)
        && double.IsFinite(MaxX) && double.IsFinite(MaxY) && double.IsFinite(MaxZ) && SizeX >= 0 && SizeY >= 0 && SizeZ >= 0;
}

/// <summary>The geometry a custom mission item needs besides the model itself: how the model is scaled and centred into the
/// rendered bounds of the vanilla body it replaces, and how the body's interaction boxes grow to cover it. Pure numbers, so the
/// same result is reached on every peer and the rule is testable without the game.</summary>
internal static class CarryItemFitting
{
    private const double Epsilon = 1e-6;

    /// <summary>The uniform scale and the translation (applied after scaling) that put the model's centre on the target's
    /// centre with the model as large as fits inside the target. Axes the model has no extent on are ignored; a model with no
    /// extent at all, or a target with none, cannot be fitted.</summary>
    public static bool TryAutoFit(FitBox target, FitBox model, out double scale, out double offsetX, out double offsetY, out double offsetZ)
    {
        scale = 1; offsetX = offsetY = offsetZ = 0;
        if (!target.IsFinite || !model.IsFinite) return false;
        double best = double.PositiveInfinity;
        void Axis(double targetSize, double modelSize)
        {
            if (modelSize > Epsilon) best = Math.Min(best, targetSize / modelSize);
        }
        Axis(target.SizeX, model.SizeX); Axis(target.SizeY, model.SizeY); Axis(target.SizeZ, model.SizeZ);
        if (!double.IsFinite(best) || best <= Epsilon) return false;
        scale = best;
        offsetX = target.CenterX - scale * model.CenterX;
        offsetY = target.CenterY - scale * model.CenterY;
        offsetZ = target.CenterZ - scale * model.CenterZ;
        return true;
    }

    /// <summary>The smallest box holding both, grown by <paramref name="margin"/> around the required one. A vanilla interaction
    /// box is never made smaller: the base body's box may already be generous in a direction the model does not reach.</summary>
    public static FitBox Enlarge(FitBox existing, FitBox required, double margin)
    {
        var grown = new FitBox(required.MinX - margin, required.MinY - margin, required.MinZ - margin,
            required.MaxX + margin, required.MaxY + margin, required.MaxZ + margin);
        return new FitBox(Math.Min(existing.MinX, grown.MinX), Math.Min(existing.MinY, grown.MinY), Math.Min(existing.MinZ, grown.MinZ),
            Math.Max(existing.MaxX, grown.MaxX), Math.Max(existing.MaxY, grown.MaxY), Math.Max(existing.MaxZ, grown.MaxZ));
    }
}
