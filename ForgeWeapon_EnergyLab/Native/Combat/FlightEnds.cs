using System;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Where a projectile ends is the host's: its simulation deals the damage, so on every other machine the picture waits for the
/// host's word (<see cref="FlightEnd"/>) and moves there. A machine that hears nothing ends the projectile where it stopped itself.</summary>
internal static class FlightEnds
{
    /// <summary>Host: tells the other machines where <paramref name="owner"/>'s projectile number <paramref name="sequence"/> ended.</summary>
    internal static void Announce(PlayerAgent owner, FlightKind kind, uint sequence, Vector3 at)
    {
        if (sequence == 0 || owner.Owner == null) return;
        EnergyNetwork.BroadcastFlightEnd(FlightEnd.Create(owner.Owner.Lookup, kind, sequence, at));
    }

    /// <summary>The factor the shooter's native hit put on the gun's base damage (a booster's), taken from the damage the shot carried; the host
    /// takes at most <see cref="EnergyProtocol.MaximumDamageBoost"/> times the base from any peer. 1 when the shot carried none.</summary>
    internal static float Scale(EnergyGear gear, float damage)
        => damage > 0f && float.IsFinite(damage) && gear.Numbers.Shot.Damage > 0f
            ? Math.Clamp(damage / gear.Numbers.Shot.Damage, 0f, EnergyProtocol.MaximumDamageBoost) : 1f;
}
