using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeEngine.Framework;
using ForgeVisual;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>What the registration record of a placed model says about its pose and look, as the native objects hold them
/// right after placement, next to the values the author wrote, so the two can be compared without a screenshot. Only
/// reads; a value the game does not hold is left out, never filled in. A record point carries a bounded number of values,
/// so each group is one value holding its numbers as text.</summary>
internal static class PlacedModelRecordValues
{
    private const int MaximumParts = 8, MaximumMaterials = 8, MaximumText = 600;

    /// <summary>One value for every placed model (the author pose, the world pose the room placement implies, the world pose the
    /// object holds) and one more for a composed model (the looks its parts were written with, the colours its materials hold now).</summary>
    internal static RuntimeLogValue[] Of(PlacedModel model, GameObject root, Vector3 expectedPosition, Quaternion expectedRotation,
        MapModelAssembly? placed)
    {
        var node = root.transform;
        var values = new List<RuntimeLogValue>
        {
            RuntimeLogValue.Of("pose", Bound("author:pos=" + Text(model.Position) + ",rot=" + Text(model.Rotation) + ",scale=" + Text(model.Scale)
                + "|expected:pos=" + Text(expectedPosition) + ",rot=" + Text(expectedRotation)
                + "|actual:pos=" + Text(node.position) + ",rot=" + Text(node.rotation) + ",scale=" + Text(node.lossyScale)))
        };
        if (placed == null) return values.ToArray();
        // The tints the author wrote (parts and their slots, vanilla and imported) next to what the painted materials hold:
        // the colour read back in the author's sRGB (SurfaceLook.ReadTint), so the two compare directly. Imported draws have no renderer,
        // so the materials come from the placement, not from the root's renderers.
        var wantedTints = new List<float[]>();
        var authored = new List<string>();
        var looks = placed.Assembly.Parts.Select(part => part.Appearance).Concat(placed.Assembly.ImportedParts.Select(part => part.Appearance)).ToList();
        for (var index = 0; index < looks.Count; index++)
        {
            if (looks[index] is not { } look) continue;
            foreach (var one in new[] { look }.Concat((look.Slots ?? Array.Empty<SurfaceSlot>()).Select(slot =>
                new SurfaceAppearance(slot.Tint, slot.GlowColor, slot.GlowBrightness, slot.Invisible))))
            {
                if (one.Tint != null) wantedTints.Add(one.Tint);
            }
            if (authored.Count < MaximumParts)
                authored.Add(index.ToString(CultureInfo.InvariantCulture) + ":tint=" + (look.Tint == null ? "none" : Text(look.Tint))
                    + ",glow=" + (look.GlowColor == null ? "none" : Text(look.GlowColor))
                    + ",brightness=" + Number(look.GlowBrightness) + ",invisible=" + (look.Invisible is { } invisible ? (invisible ? "yes" : "no") : "none"));
        }
        var read = placed.PaintedTints();
        values.Add(RuntimeLogValue.Of("look", Bound("author:" + (authored.Count == 0 ? "none" : string.Join(";", authored))
            + "|actual:" + (read.Length == 0 ? "none" : string.Join("|", read.Take(MaximumMaterials).Select((row, at) =>
                at.ToString(CultureInfo.InvariantCulture) + ":tint=" + Text(row.Srgb) + ",alpha=" + Number(row.Alpha)))))));
        values.Add(RuntimeLogValue.Of("lookRead", "tint=" + wantedTints.Count(wanted => read.Any(row => Near(row.Srgb, wanted))) + "/" + wantedTints.Count
            + ",materials=" + read.Length));
        return values.ToArray();
    }

    private static bool Near(float[] read, float[] wanted)
    {
        for (var channel = 0; channel < 3; channel++) if (Math.Abs(read[channel] - wanted[channel]) > 0.005f) return false;
        return true;
    }

    private static string Bound(string text) => text.Length <= MaximumText ? text : text.Substring(0, MaximumText);
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Text(IEnumerable<double> row) => "[" + string.Join(",", row.Select(Number)) + "]";
    private static string Text(IEnumerable<float> row) => "[" + string.Join(",", row.Select(value => Number(value))) + "]";
    private static string Text(Vector3 value) => Text(new double[] { value.x, value.y, value.z });
    private static string Text(Quaternion value) => Text(new double[] { value.x, value.y, value.z, value.w });
}
