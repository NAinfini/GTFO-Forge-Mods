using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using IRF;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The author's colour, glow and visibility of a room part (<see cref="RoomBuildPart.Appearance"/>) on the object native built
/// for it. A spawner part's object only exists once native expanded it, so this runs after the factory, like RoomRecipeAttachments. Every
/// renderer below the part's object is painted through <see cref="NativeModelRuntime.PaintSurfaces"/>, the one surface path weapons and
/// placed models share: one material per (source material, look) in the level, so parts painted alike share their materials and the draw
/// count stays the vanilla count. Every peer builds the same recipe, so nothing is sent. A part the look cannot reach is one Error naming
/// the part and why; the level goes on.</summary>
internal static class RoomRecipeLooks
{
    internal const string Code = "room.build-part-look";
    private const string Owner = "map:room-part-look";

    /// <summary>Never throws: a look is appearance only and must not refuse the room.</summary>
    internal static void Paint(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        try { PaintUnchecked(room, recipe); }
        catch (Exception error) { MapLog.Contained("room-recipe-looks.paint", error); }
    }

    private static void PaintUnchecked(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        var looked = recipe.Parts.Where(part => part.Appearance != null && !RoomRecipeSpatialReadback.IsRemoved(room, part.Id)).ToArray();
        if (looked.Length == 0) return;
        var objects = RoomRecipeSpatialReadback.InstancesOf(room, recipe);
        string roomKey = GeneratedRoomRegistry.LocatorFor(room)?.InstanceId ?? room.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture);
        int painted = 0;
        foreach (var part in looked)
        {
            // A part native never built is already the room's structural failure (RoomRecipeSpatialReadback.CheckParts).
            if (!objects.TryGetValue(part.Id, out var built) || built == null) continue;
            var renderers = built.GetComponentsInChildren<Renderer>(true).ToArray();
            bool together = built.GetComponentsInChildren<InstancedRenderFeature>(true).Any(feature => feature != null && feature.enabled);
            if (renderers.Length == 0) { Refused(recipe, part, together ? "room-part-look-drawn-together" : "room-part-look-nothing-drawn"); continue; }
            ForgeVisual.SurfaceAppearance look;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(part.Appearance!);
                look = NativeModelContent.ReadAppearance(document.RootElement);
            }
            catch (Exception error) when (error is System.IO.InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            { Refused(recipe, part, "room-part-look-unreadable:" + error.Message); continue; }
            var refusal = NativeModelRuntime.PaintSurfaces(Owner, roomKey + "/" + part.Id, renderers.Select(renderer => (renderer, look)).ToArray());
            if (refusal != null) { Refused(recipe, part, refusal); continue; }
            painted++;
            if (together) Refused(recipe, part, "room-part-look-drawn-together");
            if (MapLog.Logs(RuntimeLogLevel.Info)) Readback(recipe, part, renderers);
        }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, Code, "Room parts given the author's colour, glow or visibility.",
                RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("roomInstance", roomKey),
                RuntimeLogValue.Of("looked", looked.Length), RuntimeLogValue.Of("painted", painted));
    }

    /// <summary>What the first painted material of the part reads back as: the author's sRGB colour and the raw glow colour.</summary>
    private static void Readback(RoomBuildRecipe recipe, RoomBuildPart part, Renderer[] renderers)
    {
        var material = renderers.Where(renderer => renderer != null).SelectMany(renderer => renderer.sharedMaterials).FirstOrDefault(material => material != null);
        string tint = material != null && ForgeVisual.SurfaceLook.ReadTint(material, out var srgb, out _) ? Triple(srgb[0], srgb[1], srgb[2]) : "none";
        string glow = "none";
        if (material != null && material.HasProperty(ForgeVisual.Glow.EmissiveColorName))
        {
            var colour = material.GetColor(ForgeVisual.Glow.EmissiveColorName);
            glow = Triple(colour.r, colour.g, colour.b);
        }
        MapLog.Record(RuntimeLogLevel.Info, Code, "A room part's look read back from its material.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Quote("part", part.Id), RuntimeLogValue.Of("renderers", renderers.Length),
            RuntimeLogValue.Quote("shader", material != null && material.shader != null ? material.shader.name : "none"),
            RuntimeLogValue.Of("tint", tint), RuntimeLogValue.Of("glow", glow),
            RuntimeLogValue.Of("shadowOnly", renderers.Count(renderer => renderer != null && renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)));
    }

    private static void Refused(RoomBuildRecipe recipe, RoomBuildPart part, string reason)
        => MapLog.Record(RuntimeLogLevel.Error, Code, Sentence(reason) + " The level goes on.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Quote("part", part.Id), RuntimeLogValue.Of("reason", reason.Length > 200 ? reason[..200] : reason));

    /// <summary>The author's words for each reason the look did not reach the part.</summary>
    private static string Sentence(string reason)
    {
        string code = reason.StartsWith("model-surface-apply-failed:", StringComparison.Ordinal) ? reason["model-surface-apply-failed:".Length..] : reason;
        if (code.StartsWith("room-part-look-unreadable:", StringComparison.Ordinal))
            return "This piece's look is not written the way the game reads it, so it keeps its own surface. Export the room again from the website.";
        return code switch
        {
            "room-part-look-drawn-together" => "The game draws this piece together with every other copy of it, so it cannot take its own colour, glow or visibility.",
            "room-part-look-nothing-drawn" => "This piece draws nothing (a light, a sound or a placement point), so colour, glow and visibility do not apply to it.",
            "model-glow-unsupported" => "This piece's material cannot glow, so its glow is not shown.",
            "model-surface-shader-unavailable" or "model-surface-colour-unavailable" => "The game's plain colour material is not available, so this piece keeps its own colour.",
            "model-surface-emission-unavailable" => "This piece's own lights could not be kept under the new colour, so it keeps its own surface.",
            "model-appearance-colour-range" => "This colour is too bright for the game to draw, so this piece keeps its own surface.",
            _ => "This piece's colour, glow or visibility could not be applied, so it keeps its own surface.",
        };
    }

    private static string Triple(float r, float g, float b) => FormattableString.Invariant($"({r:0.###},{g:0.###},{b:0.###})");
}
