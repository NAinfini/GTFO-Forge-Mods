using System;
using System.Collections.Generic;
using GameData;
using Gear;

namespace ForgeOrdnance.Frames;

/// <summary>The suit steadies the gun in the pilot's hands: whichever gun the local pilot holds (the suit gun or the pilot's own primary
/// or special) is given a copy of its own recoil data with the view kick (power, so up and sideways alike), the crosshair bloom and the
/// weapon's own kick scaled by <see cref="GunHandling.Recoil"/>; the game's recoil path (<c>Weapon.ApplyRecoil</c> into
/// <c>FPSCamera.AddRecoil</c>) then runs unchanged on it. The game's recoil block, every other player and the pilot's guns once out of
/// the suit or out of the hands keep their own data. A copy is made once per source block and multiplier.</summary>
internal static class GunSteady
{
    private static readonly Dictionary<(IntPtr, float), RecoilDataBlock> Copies = new();
    private static BulletWeapon? _weapon;
    private static RecoilDataBlock? _original;
    private static RecoilDataBlock? _applied;
    private static float _multiplier = -1f;

    /// <summary>No gun is being steadied.</summary>
    internal static bool Idle => _weapon == null;

    /// <summary>Keeps <paramref name="weapon"/> (the held gun, or null for none) at <paramref name="multiplier"/>; the gun held before gets
    /// its own data back. Called every frame; it only writes on a change.</summary>
    internal static void Keep(BulletWeapon? weapon, float multiplier)
    {
        if (weapon == null && _weapon == null) return;
        if (weapon != null && (weapon.WasCollected || multiplier >= 1f)) weapon = null;
        if (weapon == null || _weapon == null || weapon.Pointer != _weapon.Pointer)
        {
            Restore();
            if (weapon == null) return;
            _weapon = weapon;
            _original = weapon.m_archeType != null && weapon.m_archeType.m_recoilData != null ? weapon.m_archeType.m_recoilData : ((ItemEquippable)weapon).RecoilData;
        }
        if (_original == null || multiplier == _multiplier) return;
        var key = (_original.Pointer, multiplier);
        if (!Copies.TryGetValue(key, out var copy))
        {
            copy = ForgeCore.Gear.ScaledRecoil(_original, multiplier);
            Copies[key] = copy;
        }
        ((ItemEquippable)weapon).RecoilData = copy;
        if (weapon.m_archeType != null) weapon.m_archeType.m_recoilData = copy;
        _applied = copy;
        _multiplier = multiplier;
    }

    /// <summary>The gun held so far gets its own data back (when it still lives).</summary>
    private static void Restore()
    {
        var weapon = _weapon;
        if (weapon != null && !weapon.WasCollected && _original != null && _applied != null)
        {
            ((ItemEquippable)weapon).RecoilData = _original;
            if (weapon.m_archeType != null) weapon.m_archeType.m_recoilData = _original;
        }
        _weapon = null;
        _original = _applied = null;
        _multiplier = -1f;
    }

}
