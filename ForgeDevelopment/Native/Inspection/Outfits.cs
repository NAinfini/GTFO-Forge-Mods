using System.Text;
using GameData;
using Player;

namespace ForgeDevelopment.Native;

/// <summary>Reads and changes the local player's outfit in its backpack (the vanity item IDs the game syncs and a recording keeps), so a recording
/// can be made in other clothes than the profile's. Changes the backpack only, not the body the game shows. Called by name from the console
/// (<c>call static:ForgeDevelopment.Native.Outfits Wear 1 &lt;id&gt;</c>).</summary>
internal static class Outfits
{
    /// <summary>Every vanity item this install has, as <c>id:type:name</c>, and what the local backpack wears.</summary>
    public static string List()
    {
        var text = new StringBuilder("wearing ").Append(Worn()).Append(" items");
        foreach (var block in VanityItemsTemplateDataBlock.GetAllBlocks())
            if (block != null) text.Append(' ').Append(block.persistentID).Append(':').Append(block.type).Append(':').Append(block.name);
        return text.ToString();
    }

    /// <summary>Puts vanity item <paramref name="id"/> in clothes slot <paramref name="type"/> (0 helmet, 1 torso, 2 legs, 3 backpack, 4 palette) of the local backpack.</summary>
    public static string Wear(int type, uint id)
    {
        var backpack = PlayerBackpackManager.LocalBackpack ?? throw new System.InvalidOperationException("no local backpack");
        backpack.EquipVanityItem((ClothesType)type, id);
        return "wearing " + Worn();
    }

    private static string Worn()
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null) return "-";
        var text = new StringBuilder();
        for (var type = 0; type <= (int)ClothesType.Palette; type++) text.Append(type == 0 ? "" : " ").Append(backpack.GetBackpackVanityItem((ClothesType)type));
        return text.ToString();
    }
}
