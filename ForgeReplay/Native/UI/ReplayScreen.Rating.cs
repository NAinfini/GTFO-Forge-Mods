using System;
using System.Collections.Generic;
using ForgeUI;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The rating page of the statistics (PLAN 6): a grade card for each of the four players on top, a big radar under them with the picked player
/// filled and the others as thin outlines, and a table of the scores beside it (under it when the panel is narrow). A small grade on each player card.
/// Worked out once per analysis (<see cref="BuildRating"/>); picking a player only changes what is drawn.</summary>
internal static partial class ReplayScreen
{
    private static readonly MemberRating?[] Ratings = new MemberRating?[4];
    private static readonly float[][] RatingScores = { Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>() };
    private static readonly string[] CardGrades = { "", "", "", "" };
    private static readonly Color[] CardGradeColours = new Color[4];
    private static readonly RadarChart[] CardRadars = { new(), new(), new(), new() };
    private static readonly RadarChart BigRadar = new();
    private static readonly HudTable CompareTable = new();
    private static string[] _dimensionLabels = Array.Empty<string>();
    private static string _ratingLegend = "", _compareFor = "";
    private static int _ratingPick = -1, _ratingDrawn = -2;
    private static float _ratingBody = 640f;

    private const float RatingPad = 18f, RatingGap = 14f, RatingCardHeight = 150f, RatingLabel = 34f, RatingSide = 150f, RatingRadiusMax = 230f, RatingRadiusMin = 70f, RatingWideMin = 120f;
    private const float CardRadarRadius = 48f, SelectedLine = 3.6f, OtherLine = 1.4f;

    private readonly record struct RatingBox(float Width, float Height, bool Wide, float Radius, float CardWidth);

    // The scores, grades and charts of the four players from the whole read; every player's rating is placed by the same fixed anchors.
    private static void BuildRating(Analysis analysis)
    {
        ClearRating();
        var table = RatingStore.Table;
        _dimensionLabels = new string[RatingMetrics.Dimensions.Length];
        for (var d = 0; d < _dimensionLabels.Length; d++) _dimensionLabels[d] = _ui.Text("rating.dim." + RatingMetrics.Dimensions[d]);
        for (var slot = 0; slot < 4; slot++)
        {
            if (!EverHeld[slot]) continue;
            var rating = Ratings[slot] = RatingStore.Rate(analysis, slot);
            var scores = RatingScores[slot] = new float[RatingMetrics.Dimensions.Length];
            for (var d = 0; d < scores.Length; d++) scores[d] = rating?.Dimensions[d].Score is { } s ? (float)s : float.NaN;
            CardRadars[slot].Set(0, scores, SlotColour(slot), true, 1.8f);
            if (rating == null || table == null || rating.Grade < 0) continue;
            CardGrades[slot] = table.Grades[rating.Grade].Name;
            CardGradeColours[slot] = UIKitColors.Level((float)rating.Score!.Value);
        }
        _ratingLegend = _ui.Text("rating.legend");
        _ratingDrawn = -2;
        BuildCompare();
    }

    private static void ClearRating()
    {
        for (var slot = 0; slot < 4; slot++) { Ratings[slot] = null; CardGrades[slot] = ""; CardRadars[slot].Clear(); RatingScores[slot] = Array.Empty<float>(); }
        BigRadar.Clear();
        _ratingPick = -1; _ratingDrawn = -2; _compareFor = "";
    }

    // The player whose radar is filled: the one picked on a card, else the one being watched, else the first in the level.
    private static int RatingSelected()
    {
        if (_ratingPick is >= 0 and < 4 && EverHeld[_ratingPick]) return _ratingPick;
        var follow = Replayer.Follow;
        if (follow is >= 0 and < 4 && EverHeld[follow]) return follow;
        for (var slot = 0; slot < 4; slot++) if (EverHeld[slot]) return slot;
        return -1;
    }

    // Picking a player: that polygon filled and bold, the others thin and faint; the shapes themselves are kept, so nothing is worked out again.
    private static void ApplyRatingSelection(int selected)
    {
        _ratingDrawn = selected;
        for (var slot = 0; slot < 4; slot++)
        {
            if (!EverHeld[slot]) continue;
            var picked = slot == selected;
            BigRadar.Set(slot, RatingScores[slot], picked ? SlotColour(slot) : Fade(SlotColour(slot), 0.6f), picked, picked ? SelectedLine : OtherLine);
        }
    }

    // ---- Tables -----------------------------------------------------------------------------------------------------------------

    // Rows are the dimensions and the overall grade, columns the players; the highest of each row is green. Made again only when a name changes.
    private static void BuildCompare()
    {
        var slots = new List<int>();
        for (var s = 0; s < 4; s++) if (EverHeld[s]) slots.Add(s);
        var key = string.Join("\u0001", slots.ConvertAll(s => Names[s]));
        if (key == _compareFor) return;
        _compareFor = key;
        var heads = new string[slots.Count + 1];
        heads[0] = "";
        for (var i = 0; i < slots.Count; i++) heads[i + 1] = Names[slots[i]];
        CompareTable.Clear();
        CompareTable.SetColumns(heads, Head);
        for (var i = 0; i < slots.Count; i++) CompareTable.SetHead(i + 1, NameColour(slots[i], UIKitColors.MenuOver), SlotColour(slots[i]));
        for (var d = 0; d <= RatingMetrics.Dimensions.Length; d++)
        {
            var total = d == RatingMetrics.Dimensions.Length;
            var scores = new double?[slots.Count];
            for (var i = 0; i < slots.Count; i++) scores[i] = total ? Ratings[slots[i]]?.Score : Ratings[slots[i]]?.Dimensions[d].Score;
            double best = double.NegativeInfinity; var known = 0;
            foreach (var s in scores) if (s is { } v) { best = Math.Max(best, v); known++; }
            var cells = new TableCell[slots.Count + 1];
            cells[0] = new TableCell(total ? _ui.Text("rating.total") : _dimensionLabels[d], total ? UIKitColors.GameText : Dim, total);
            for (var i = 0; i < slots.Count; i++)
            {
                var rating = Ratings[slots[i]];
                var cell = scores[i] is not { } score ? None
                    : total ? Cell(!Array.Exists(rating!.Dimensions, x => x.Estimated), CardGrades[slots[i]].Length > 0 ? CardGrades[slots[i]] : "—")
                    : Cell(!rating!.Dimensions[d].Estimated, Whole((float)(score * 100.0)));
                if (total && rating != null && rating.Grade < 0) cell = None;
                var green = known > 1 && scores[i] is { } mine && mine >= best && cell.Quality != StatQuality.Empty;
                // A value is HUD text, a guess the warning colour and an empty cell dimmed; the best of the row stays green.
                var shade = cell.Quality switch { StatQuality.Estimate => UIKitColors.Warn, StatQuality.Empty => Fade(UIKitColors.GameText, 0.55f), _ => UIKitColors.GameText };
                cells[i + 1] = new TableCell(cell.Text, green ? UIKitColors.Good : shade, cell.Quality == StatQuality.Exact || green || total);
            }
            CompareTable.AddRow(cells);
        }
    }

    // ---- The page ---------------------------------------------------------------------------------------------------------------

    // How big the rating page is in <paramref name="maxWidth"/> x <paramref name="maxHeight"/>: the radar and the table side by side when they fit with a radar of a fair size, else the table under the radar.
    private static RatingBox RatingLayout(HudCanvas c, float maxWidth, float maxHeight, float minWidth)
    {
        var inner = maxWidth - 2f * RatingPad;
        CompareTable.Layout(c, inner, 600f);
        float tw = CompareTable.Width, th = CompareTable.Height;
        var fixedHeight = 2f * RatingPad + RatingCardHeight + RatingGap;
        var radius = Math.Min(RatingRadiusMax, (inner - tw - RatingGap) / 2f - RatingSide);
        radius = Math.Min(radius, (maxHeight - fixedHeight - 2f * RatingLabel) / 2f);
        var wide = radius >= RatingWideMin;
        if (!wide)
        {
            radius = Math.Min(RatingRadiusMax, inner / 2f - RatingSide);
            radius = Math.Min(radius, (maxHeight - fixedHeight - th - RatingGap - 2f * RatingLabel) / 2f);
            radius = Math.Max(radius, RatingRadiusMin);
        }
        var radarWidth = 2f * (radius + RatingSide); var radarHeight = 2f * (radius + RatingLabel);
        var body = fixedHeight + (wide ? Math.Max(radarHeight, th) : radarHeight + RatingGap + th);
        var width = Math.Max(minWidth, 2f * RatingPad + (wide ? radarWidth + RatingGap + tw : Math.Max(radarWidth, tw)));
        width = Math.Max(width, 2f * RatingPad + 4f * 170f + 3f * RatingGap);
        width = Math.Min(width, maxWidth);
        return new RatingBox(width, Math.Min(body, maxHeight), wide, radius, (width - 2f * RatingPad - 3f * RatingGap) / 4f);
    }

    private static void RatingBody(HudElement hud, HudCanvas c, float left, float top, RatingBox box)
    {
        var selected = RatingSelected();
        if (selected != _ratingDrawn) ApplyRatingSelection(selected);
        var x = left + RatingPad;
        var cardTop = top - RatingPad;
        for (var slot = 0; slot < 4; slot++)
            RatingCard(hud, c, slot, x + slot * (box.CardWidth + RatingGap), cardTop, box.CardWidth, slot == selected);
        var radarTop = cardTop - RatingCardHeight - RatingGap;
        var radarWidth = 2f * (box.Radius + RatingSide);
        var grid = Fade(UIKitColors.GameText, 0.4f);
        BigRadar.Draw(c, x + radarWidth / 2f, radarTop - RatingLabel - box.Radius, box.Radius, grid, _dimensionLabels, 17f, Fade(UIKitColors.GameText, 0.9f));
        if (box.Wide) CompareTable.Draw(c, x + radarWidth + RatingGap - CompareTable.Pad, radarTop);
        else CompareTable.Draw(c, x - CompareTable.Pad, radarTop - 2f * (box.Radius + RatingLabel) - RatingGap);
    }

    // One player's card: the name in the player's colour, the grade in big letters, a small radar of the same shape; a click picks the player.
    private static void RatingCard(HudElement hud, HudCanvas c, int slot, float left, float top, float width, bool selected)
    {
        var cx = left + width / 2f; var cy = top - RatingCardHeight / 2f;
        var held = EverHeld[slot];
        var colour = SlotColour(slot);
        var over = held && HudPointer.Hover(hud, cx, cy, width, RatingCardHeight);
        c.Plate(cx, cy, width, RatingCardHeight, Cut, Plate());
        c.Pointed(cx, cy, width, RatingCardHeight, Cut, UIKitColors.MenuOver, over, over && HudPointer.PressedOn(hud, cx, cy, width, RatingCardHeight), selected, rest: false);
        c.Rect(left + 4f, cy, 5f, RatingCardHeight - 12f, held ? colour : Fade(colour, 0.35f));
        if (!held) { c.Text(_ui.Text("player.empty"), left + 22f, cy, 18f, Fade(UIKitColors.GameText, 0.55f), HudAlign.Left, bold: true); return; }
        if (HudPointer.Click(hud, cx, cy, width, RatingCardHeight)) _ratingPick = slot;
        var rating = Ratings[slot];
        c.Text(c.FitText(Names[slot], 18f, true, width - 40f), left + 20f, top - 22f, 18f, colour, HudAlign.Left, bold: true);
        var textRoom = width - 2f * CardRadarRadius - 50f;
        if (CardGrades[slot].Length > 0) c.Text(CardGrades[slot], left + 20f, cy - 18f, 46f, CardGradeColours[slot], HudAlign.Left, bold: true);
        else if (rating?.State == RatingState.TooFewDimensions) c.Text(c.FitText(_ui.Text("rating.fewdims"), 18f, true, textRoom), left + 20f, cy - 18f, 18f, UIKitColors.Warn, HudAlign.Left, bold: true);
        else c.Text("—", left + 20f, cy - 18f, 30f, Fade(UIKitColors.GameText, 0.55f), HudAlign.Left, bold: true);
        CardRadars[slot].Draw(c, left + width - CardRadarRadius - 22f, cy - 18f, CardRadarRadius, Fade(UIKitColors.GameText, 0.35f), null, 16f, null, 2);
    }
}
