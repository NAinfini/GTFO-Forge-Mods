using System;
using Player;

namespace ForgeOrdnance;

/// <summary>The throw step while the ball is in hand: silences the game's own throw sound and plays <c>beacon.throw</c> once when the game's Throw runs.</summary>
internal static class ThrowWatch
{
    private static bool _active;
    internal static bool Attempted { get; private set; }

    internal static void Begin()
    {
        _active = true;
        Attempted = false;
    }

    /// <summary>The game's Throw ran for a ball (<see cref="BallThrowHook"/> checked that).</summary>
    internal static void ThrowStarted(ThrowingWeapon weapon)
    {
        if (!_active || Attempted || weapon.Owner == null || weapon.Owner != ForgeCore.Players.Local) return;
        Attempted = true;
        OrdnanceCues.Local(OrdnanceSound.BeaconThrow);
    }

    internal static void Tick(PlayerAgent agent)
    {
        var weapon = agent.Inventory?.WieldedItem?.TryCast<ThrowingWeapon>();
        if (weapon != null && IsBall(weapon)) SilenceNativeThrow(weapon);
    }

    internal static void End() => _active = false;

    /// <summary>The game's own throw sound (<c>ThrowingWeapon.m_throwSoundEvent</c>, a Wwise event name) would play on top of
    /// <c>beacon.throw</c>: silence it on the ball only. Glow sticks and other throwables keep theirs.</summary>
    internal static void SilenceNativeThrow(ThrowingWeapon weapon)
    {
        if (string.IsNullOrEmpty(weapon.m_throwSoundEvent)) return;
        weapon.m_throwSoundEvent = "";
    }

    internal static bool IsBall(ThrowingWeapon weapon)
    {
        var block = weapon.ItemDataBlock;
        return block != null && block.persistentID == BeaconItem.BlockId;
    }
}

/// <summary>Proves the game's own throw ran for the ball (the item replication and slot clean-up follow from it). ForgeCore patches
/// the throw once (<c>GameHooks.ThrowableThrown</c>); this runs ahead of the body, so the native throw sound is already silenced.</summary>
internal static class BallThrowHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.ThrowableThrown.Before(Throwing);

    private static void Throwing(ThrowingWeapon weapon)
    {
        try
        {
            if (ThrowWatch.IsBall(weapon))
            {
                ThrowWatch.SilenceNativeThrow(weapon);
                ThrowWatch.ThrowStarted(weapon);
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Ball throw hook failed: " + error); }
    }
}
