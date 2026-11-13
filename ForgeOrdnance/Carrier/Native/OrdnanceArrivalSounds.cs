using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The sounds of a call-in's fall and opening that the framework's own drop pod plays.</summary>
public enum OrdnanceArrivalSound
{
    /// <summary>The fall from the sky.</summary>
    Incoming,
    /// <summary>The touchdown.</summary>
    Impact,
    /// <summary>The rubble settling after the touchdown.</summary>
    Debris,
    /// <summary>The pod's doors, or any set of outer plates, swinging open.</summary>
    Open,
    /// <summary>The plates closing, or the pod leaving.</summary>
    Leave,
}

/// <summary>The drop pod's own sounds for a payload that plays its own arrival (<see cref="IOrdnancePayload.HasOwnArrival"/>) and
/// falls the way the pod does, so every call-in sounds alike and the sounds are chosen once. Play each at the moment the pod would:
/// <see cref="OrdnanceArrivalSound.Incoming"/> when the fall starts, Impact and Debris on touchdown, Open and Leave as plates open and close.</summary>
public static class OrdnanceArrivalSounds
{
    private static readonly string[] Cues = { "drop.incoming", "drop.impact", "drop.debris", "drop.open", "drop.leave" };

    /// <summary>Plays <paramref name="sound"/> following <paramref name="owner"/> (3D). <paramref name="dimension"/> is the game
    /// dimension of the landing point, or null to let the sound kit resolve it.</summary>
    public static void Play(OrdnanceArrivalSound sound, Transform owner, int? dimension = null)
        => OrdnanceCues.Attach(Cues[(int)sound], owner.gameObject, dimension);
}
