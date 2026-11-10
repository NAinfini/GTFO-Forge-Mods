using System;

namespace ForgeReplay;

/// <summary>What a player carries besides the guns and the tool (those are in the loadout, the file header's or the newest Presence line's):
/// the resource pack and the consumable with what is left of them, what is carried in the hands, the flashlight and the pocket items. An item
/// is its <c>ItemDataBlock</c> id, 0 for none. <see cref="Known"/> is false until the recording has said.</summary>
internal readonly record struct PlayerKit(bool Known, uint PackItem, int PackCount, uint ConsumableItem, int ConsumableCount, uint CarryItem, bool Flashlight, uint[] Pockets)
{
    /// <summary>A PlayerKit event from after its slot (layout in DEVELOPERS.md).</summary>
    internal static PlayerKit Read(PayloadReader read)
    {
        var pack = (uint)read.U(); var packCount = (int)read.U();
        var consumable = (uint)read.U(); var consumableCount = (int)read.U();
        var carry = (uint)read.U(); var flashlight = read.U() == 1;
        var pockets = new uint[Math.Min((int)read.U(), 64)];
        for (var i = 0; i < pockets.Length; i++) pockets[i] = (uint)read.U();
        return new PlayerKit(true, pack, packCount, consumable, consumableCount, carry, flashlight, pockets);
    }
}
