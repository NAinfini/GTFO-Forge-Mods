using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The three sounds of a call-in's fall that the framework's own drop pod plays.</summary>
public enum OrdnanceArrivalSound
{
    /// <summary>The fall from the sky.</summary>
    Incoming,
    /// <summary>The touchdown.</summary>
    Impact,
    /// <summary>The rubble settling after the touchdown.</summary>
    Debris,
}

/// <summary>The drop pod's own sounds for a payload that plays its own arrival (<see cref="IOrdnancePayload.HasOwnArrival"/>) and
/// falls the way the pod does, so every call-in sounds alike and the sounds are chosen once. Play each at the moment the pod would:
/// <see cref="OrdnanceArrivalSound.Incoming"/> when the fall starts, the other two on touchdown.</summary>
public static class OrdnanceArrivalSounds
{
    private static readonly string[] Cues = { "drop.incoming", "drop.impact", "drop.debris" };

    /// <summary>Plays <paramref name="sound"/> following <paramref name="owner"/> (3D). <paramref name="dimension"/> is the game
    /// dimension of the landing point, or null to let the sound kit resolve it.</summary>
    public static void Play(OrdnanceArrivalSound sound, Transform owner, int? dimension = null)
        => OrdnanceCues.Attach(Cues[(int)sound], owner.gameObject, dimension);
}
