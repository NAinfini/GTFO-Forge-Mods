using System;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The one native write path a damage row submits through, kept in its own type so the game assembly is
/// reached from exactly one audited member and the handler stays readable without it. The receiver probe compiles
/// this same source against a stand-in receiver, which is why the entry call needs no injection seam.</summary>
internal static class EnemyNativeWrite
{
    /// <summary>One bullet-damage submission with the exact argument list the frozen interop declares. The
    /// attacker is the exact enemy life this provider resolved from the instigator input. An external player
    /// this provider cannot resolve remains null; source, owner and recipient are never used as substitutes.
    /// The receiver tolerates the missing attacker — `ProcessReceivedDamage` passes it to
    /// `EnemyAgent.RegisterDamageInflictor`, whose body returns immediately for a null inflictor, and the
    /// explosion receive path reaches the same window from a packet that cannot name an attacker at all.
    /// The limb argument is the array position the declared id names (resolved by the caller); the two multipliers
    /// and the hit geometry are the row's own ports, and the gear category is the neutral value. The packet carries the limb as one byte, so a negative "no limb" index would arrive as 255 and the receiver's limb-array access past the end throws inside the game; index 0 is never routed to a limb.</summary>
    internal static void ApplyDamage(Dam_EnemyDamageBase damage, int limbIndex, float requested,
        float staggerMulti, float precisionMulti, Vector3 position, Vector3 direction, bool allowDirectionalBonus,
        global::Agents.Agent? instigator = null)
        => ForgeCore.Combat.BulletDamage(damage, requested, instigator, position, direction,
            Vector3.zero, allowDirectionalBonus, Math.Max(limbIndex, 0), staggerMulti, precisionMulti);

    /// <summary>Resolves the limb a hit should name. `-1` means no limb and needs no array at all; any other id
    /// must be one this receiver's limb array actually declares, and the index the entry point takes is that
    /// entry's position, which is not the id. An unreadable array or an id no limb declares answers false.</summary>
    internal static bool TryNameLimb(Dam_EnemyDamageBase damage, int limbId, out int limbIndex)
    {
        limbIndex = -1;
        try
        {
            if (limbId < 0) return true;
            var limbs = damage.DamageLimbs;
            if (limbs == null) return false;
            for (int index = 0; index < limbs.Length; index++)
            {
                var limb = limbs[index];
                if (limb == null || limb.m_limbID != limbId) continue;
                limbIndex = index;
                return true;
            }
            return false;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); limbIndex = -1; return false; }
    }
}
