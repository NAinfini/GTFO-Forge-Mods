using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>The name the game shows for an item id (<see cref="PlayerKit"/> holds ids): the data block's public name, "" when unknown.</summary>
internal static class ItemNames
{
    private static readonly Dictionary<uint, string> Known = new();

    internal static string Name(uint itemId)
    {
        if (itemId == 0) return "";
        if (Known.TryGetValue(itemId, out var name)) return name;
        try { name = GameData.ItemDataBlock.GetBlock(itemId)?.publicName ?? ""; }
        catch (Exception error) { Plugin.Logger.ErrorOnce("An item's name could not be read: " + error.Message); name = ""; }
        return Known[itemId] = name;
    }
}
