using System;
using Gear;
using HarmonyLib;
using Player;
using UnityEngine;

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

    internal static void ThrowStarted(ThrowingWeapon weapon)
    {
        if (!_active || Attempted || !IsBall(weapon) || weapon.Owner == null || weapon.Owner != PlayerManager.GetLocalPlayerAgent()) return;
        Attempted = true;
        OrdnanceCues.Local(OrdnanceSound.BeaconThrow);
    }

    internal static void Tick(PlayerAgent agent)
    {
        var weapon = agent.Inventory?.WieldedItem?.TryCast<ThrowingWeapon>();
        if (weapon != null && IsBall(weapon)) SilenceNativeThrow(weapon);
    }

    internal static void End(bool thrown) => _active = false;

    /// <summary>The game's own throw sound (<c>ThrowingWeapon.m_throwSoundEvent</c>, a Wwise event name) would play on top of
    /// <c>beacon.throw</c>: silence it on the ball only. Glow sticks and other throwables keep theirs.</summary>
    internal static void SilenceNativeThrow(ThrowingWeapon weapon)
    {
        if (string.IsNullOrEmpty(weapon.m_throwSoundEvent)) return;
        weapon.m_throwSoundEvent = "";
    }

    internal static bool IsBall(ThrowingWeapon weapon) => weapon.ItemDataBlock != null && weapon.ItemDataBlock.persistentID == BeaconItem.BlockId;
}

/// <summary>Proves the game's own throw ran for the ball (the item replication and slot clean-up follow from it).</summary>
[HarmonyPatch(typeof(ThrowingWeapon), "Throw")]
internal static class BallThrowHook
{
    [HarmonyPrefix]
    private static void Prefix(ThrowingWeapon __instance, float strength)
    {
        try
        {
            if (ThrowWatch.IsBall(__instance))
            {
                ThrowWatch.SilenceNativeThrow(__instance);
                ThrowWatch.ThrowStarted(__instance);
            }
        }
        catch (Exception error) { Plugin.Error("Ball throw hook failed: " + error); }
    }
}
