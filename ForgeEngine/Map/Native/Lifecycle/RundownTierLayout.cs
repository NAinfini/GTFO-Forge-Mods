using System;
using System.Collections.Generic;
using CellMenu;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Rundown page: a tier with more expeditions than its native spacing can show side by side.
/// Build 20403457 `CM_PageRundown_New.PlaceTier` (RVA 0x1265B10) puts icon i of a tier's n at tier-local
/// ((t − ½)·1500·s, −2·min(t, 1 − t)·450·s·ScaleYModifier), t = i/(n − 1), s = TierVisualData.Scale, under the tier transform
/// scaled s, each icon scaled 1/s; so on the page icons keep their size and stand 1500·s²/(n − 1) apart, and nothing else
/// spaces them. Tiers stand `m_tierSpacing` apart. When a tier's spacing is narrower than any shipped Rundown uses, the
/// PlaceRundown postfix lays the same icons out again after the native placement: rows at the spacing and width of the
/// widest shipped tier, the next row one icon block lower, using the band down to the next tier that has icons; only when
/// that band cannot hold the rows are the icons, spacing and rows scaled down together. A tier at or above the shipped
/// spacing (every original Rundown) keeps its native positions and scale untouched.</summary>
internal static class RundownTierLayout
{
    private const float OvalWidth = 1500f, OvalDepth = 450f;
    // Page units (the tier transforms' parent frame). The narrowest spacing a shipped Rundown uses: Rundown 7 tier A,
    // 4 icons at Scale 0.65 (1500·0.65²/3). The widest shipped tier: 4 icons at Scale 0.75, 281.25 apart over 843.75.
    internal const float NarrowestShippedPitch = 211.25f, RowPitch = 281.25f, RowSpan = 843.75f;
    // An icon with its name and two status lines is about 87 units tall on the page (read in game); a row is that plus a gap.
    internal const float BlockHeight = 87f, RowStep = 110f;
    private const float SmallestScale = .4f;

    internal static void Arrange(CM_PageRundown_New page)
    {
        var visuals = page.m_currentRundownData?.StorytellingData?.Visuals;
        if (visuals == null) return;
        var tiers = new[] { page.m_expIconsTier1, page.m_expIconsTier2, page.m_expIconsTier3, page.m_expIconsTier4, page.m_expIconsTier5 };
        var data = new[] { visuals.TierAVisuals, visuals.TierBVisuals, visuals.TierCVisuals, visuals.TierDVisuals, visuals.TierEVisuals };
        var icons = new List<CM_ExpeditionIcon_New>[tiers.Length];
        for (int tier = 0; tier < tiers.Length; tier++)
        {
            icons[tier] = new List<CM_ExpeditionIcon_New>();
            if (tiers[tier] == null) continue;
            foreach (var icon in tiers[tier])
                if (icon != null && icon.DataBlock is { PutIconAboveTier: false }) icons[tier].Add(icon);
        }
        for (int tier = 0; tier < tiers.Length; tier++)
        {
            // The band this tier may fill: down to the next tier that shows icons (the last tier keeps one spacing).
            int bands = 1;
            while (tier + bands < tiers.Length && icons[tier + bands].Count == 0) bands++;
            if (tier + bands == tiers.Length) bands = Math.Max(1, bands - 1);
            if (data[tier] != null) Tier(icons[tier], data[tier], bands * page.m_tierSpacing);
        }
    }

    private static void Tier(List<CM_ExpeditionIcon_New> icons, GameData.TierVisualData visual, float band)
    {
        float scale = visual.Scale;
        int count = icons.Count;
        if (count < 2 || scale <= 0 || OvalWidth * scale * scale / (count - 1) >= NarrowestShippedPitch - 1e-3f) return;
        float size = 1f;
        int rows, perRow;
        while (true)
        {
            int capacity = (int)Math.Floor(RowSpan / (RowPitch * size) + 1e-4) + 1;
            int fit = Math.Max(1, (int)Math.Floor((band - BlockHeight * size) / (RowStep * size) + 1e-4) + 1);
            rows = (count + capacity - 1) / capacity;
            if (rows <= fit || size <= SmallestScale) break;
            size = Math.Max(SmallestScale, size - .05f);
        }
        perRow = (count + rows - 1) / rows;
        // The tier's own y axis is the oval's depth (it runs into the page, not down it): one row keeps the native depth V,
        // stacked rows stay level, and a row goes down the page along the page's y axis carried into the tier frame.
        float depth = rows == 1 ? OvalDepth * scale * visual.ScaleYModifier : 0f;
        for (int index = 0; index < count; index++)
        {
            int row = index / perRow, column = index % perRow, inRow = Math.Min(perRow, count - row * perRow);
            float t = inRow == 1 ? .5f : column / (float)(inRow - 1);
            var transform = icons[index].transform;
            var tier = transform.parent;
            var onPage = new Vector3((column - (inRow - 1) * .5f) * RowPitch * size, -row * RowStep * size, 0f);
            var inTier = tier.parent != null ? tier.InverseTransformVector(tier.parent.TransformVector(onPage)) : onPage / scale;
            transform.localPosition = new Vector3(0f, -2f * Math.Min(t, 1f - t) * depth, transform.localPosition.z) + inTier;
            if (size < 1f) transform.localScale = Vector3.one * (size / scale);
        }
    }
}
