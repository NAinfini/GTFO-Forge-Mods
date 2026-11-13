using System;
using System.Collections.Generic;
using GameData;
using Gear;

namespace ForgeReplay;

/// <summary>How many bullets a full pack of a recorded gear holds, by the game's own data: the player's cap for that ammunition over the cost of a
/// bullet of the gear's archetype (what <c>PlayerAmmoStorage.GetBulletMaxCap</c> works out in play). 0 when the data does not say, as for a tool
/// with no archetype; the caller then goes by the most this recording shows.</summary>
internal static class AmmoCaps
{
    /// <param name="gearJson">The gear's line of the recorded loadout.</param>
    /// <param name="line">0 main, 1 special, 3 tool: the loadout's line.</param>
    internal static uint Bullets(string gearJson, int line)
    {
        // Asked each time a number on a card moves; a gear's data does not change.
        if (Known.TryGetValue((gearJson, line), out var known)) return known;
        return Known[(gearJson, line)] = Read(gearJson, line);
    }

    private static readonly Dictionary<(string, int), uint> Known = new();

    private static uint Read(string gearJson, int line)
    {
        if (gearJson.Length == 0) return 0;
        try
        {
            var player = PlayerDataBlock.GetBlock(1U);
            var gear = new GearIDRange(gearJson);
            if (player == null) return 0;
            var cost = ArchetypeDataBlock.GetBlock(ForgeCore.Gear.ArchetypeId(gear))?.CostOfBullet ?? 0f;
            if (cost <= 0f) return 0;
            var cap = line == 0 ? player.AmmoStandardMaxCap : line == 1 ? player.AmmoSpecialMaxCap : player.AmmoClassMaxCap;
            return (uint)Math.Max(0f, cap / cost);
        }
        catch (Exception) { return 0; } // A line this game build cannot read: the recording's own most stands in.
    }
}
