using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>One heavy object a level lets the player carry on the back. <paramref name="HoldSeconds"/> null keeps the game's pick-up time.</summary>
internal sealed record CarryOnBackRule(uint ItemId, KeyCode Key, float MoveSpeed, float? HoldSeconds);

internal sealed record CarryOnBackLevel(LevelAddress Level, IReadOnlyList<CarryOnBackRule> Items);

/// <summary>The per-level carry-on-back rules of a Rundown package (`forge/carry-on-back.forge`). Heavy objects a level does not list keep the
/// game's behaviour. The file ships in the package every peer has, so nothing about the rules goes over the network.</summary>
internal static class CarryOnBackDocument
{
    internal const string FileName = "carry-on-back.forge";

    internal static IReadOnlyList<CarryOnBackLevel> Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!Fields(root, "schemaVersion", "levels") || !FormatVersion.Matches(root)
            || !root.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            throw new FormatException("Invalid carry-on-back document.");
        var accepted = new List<CarryOnBackLevel>();
        var seenLevels = new HashSet<LevelAddress>();
        foreach (var row in levels.EnumerateArray())
        {
            if (!Fields(row, "level", "items") || !row.TryGetProperty("level", out var name) || name.ValueKind != JsonValueKind.String
                || LevelAddress.TryParse(name.GetString()) is not { } level || !seenLevels.Add(level)
                || !row.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new FormatException("Invalid carry-on-back level.");
            accepted.Add(new(level, Array.AsReadOnly(items.EnumerateArray().Select(Item).ToArray())));
            if (accepted[^1].Items.Select(item => item.ItemId).Distinct().Count() != accepted[^1].Items.Count)
                throw new FormatException("A carry-on-back level lists one object twice.");
        }
        return Array.AsReadOnly(accepted.ToArray());
    }

    private static CarryOnBackRule Item(JsonElement row)
    {
        if (!Fields(row, "itemId", "key", "moveSpeed", "holdSeconds")
            || !row.TryGetProperty("itemId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var itemId) || itemId == 0)
            throw new FormatException("Invalid carry-on-back object.");
        if (!row.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String || key.GetString()!.Split('-').Any(string.IsNullOrEmpty)
            || !Enum.TryParse<KeyCode>(PlayerKeyFacts.UnityName(key.GetString()!), out var code) || code == default || !Enum.IsDefined(code))
            throw new FormatException("Invalid carry-on-back key.");
        if (!row.TryGetProperty("moveSpeed", out var speed) || speed.ValueKind != JsonValueKind.Number
            || !speed.TryGetDouble(out var moveSpeed) || moveSpeed is <= 0 or > float.MaxValue)
            throw new FormatException("A carry-on-back move speed is above 0 and fits a 32-bit float.");
        float? hold = null;
        if (row.TryGetProperty("holdSeconds", out var seconds))
        {
            if (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetDouble(out var value) || value is < 0 or > float.MaxValue)
                throw new FormatException("A carry-on-back hold time is 0 or more and fits a 32-bit float.");
            hold = (float)value;
        }
        return new(itemId, code, (float)moveSpeed, hold);
    }

    private static bool Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name)) return false;
        return true;
    }
}
