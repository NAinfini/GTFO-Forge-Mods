using Player;

namespace ForgeCore;

public static partial class Combat
{
    /// <summary>Whether the player's own gun is lowered at idle (the relaxed pose). A shot or
    /// effect anchored on the muzzle must wait for it, or start from <see cref="GunPose.RaisedMuzzle"/>.</summary>
    public static bool GunLowered(PlayerAgent owner)
    {
        var holder = owner.FPItemHolder;
        if (holder == null) return false;
        return holder.ItemRelaxedTrigger ||
            holder.CurrentItemState?.TryCast<FirstPersonItem.FPIS_Relaxed>() != null;
    }

    /// <summary>The player's fire input asks for the gun: the game's own idle-lowering timer restarts, which brings a relaxed gun back up.</summary>
    public static void RaiseGun(PlayerAgent owner) => owner.FPItemHolder?.DontRelax();
}
