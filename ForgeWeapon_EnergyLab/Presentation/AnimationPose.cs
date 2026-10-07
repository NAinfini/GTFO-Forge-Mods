using System;
using System.Numerics;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

internal readonly record struct AnimationFrame(Vector3 Position, Quaternion Rotation);

// Preserve the authored socket offset while the game's existing animation moves its donor socket.
internal static class AnimationPose
{
    internal static AnimationFrame Follow(in AnimationFrame restParent, in AnimationFrame currentParent, in AnimationFrame restChild)
    {
        Validate(restParent); Validate(currentParent); Validate(restChild);
        var inverse = Quaternion.Inverse(restParent.Rotation);
        var offset = Vector3.Transform(restChild.Position - restParent.Position, inverse);
        var rotation = Quaternion.Normalize(currentParent.Rotation * inverse * restChild.Rotation);
        return new AnimationFrame(currentParent.Position + Vector3.Transform(offset, currentParent.Rotation), rotation);
    }

    internal static AnimationFrame FollowHand(in AnimationFrame restGrip, in AnimationFrame currentGrip,
        in AnimationFrame restHand, in AnimationFrame currentHand, in AnimationFrame authoredHand)
    {
        Validate(currentHand);
        var donor = Follow(restGrip, currentGrip, restHand);
        var authored = Follow(restGrip, currentGrip, authoredHand);
        // The new foregrip offset belongs to the gun, not the rotating donor wrist.
        // Retain the donor hand's independent translation and rotation, including reloads.
        var rotation = Quaternion.Normalize(currentHand.Rotation * Quaternion.Inverse(donor.Rotation) * authored.Rotation);
        return new AnimationFrame(authored.Position + currentHand.Position - donor.Position, rotation);
    }

    // The authored foregrip sets longitudinal reach, not the wrist's lateral/vertical position or grip axes.
    // holdForward: how much further forward than the donor's own hold the whole item sits.
    internal static AnimationFrame LimitHandReach(in AnimationFrame modelRest, in AnimationFrame donorHand,
        in AnimationFrame authoredHand, float holdForward)
    {
        Validate(modelRest); Validate(donorHand); Validate(authoredHand);
        var forward = Vector3.Transform(Vector3.UnitZ, modelRest.Rotation);
        var shift = Vector3.Dot(authoredHand.Position - donorHand.Position, forward);
        // The arm reaches from the body, so the forward-shifted hold counts toward the support hand's reach.
        var excess = shift + holdForward;
        if (!float.IsFinite(excess)) throw new ArgumentException("Support-hand forward reach must be finite.");
        // Native IK drives the wrist. Keep its donor offset and rotation, and slide it only along the gun.
        return donorHand with { Position = donorHand.Position + forward * (shift - MathF.Max(0f, excess)) };
    }

    private static void Validate(in AnimationFrame frame)
    {
        if (!PresentationContract.Finite(frame.Position) || !float.IsFinite(frame.Rotation.LengthSquared()) ||
            MathF.Abs(frame.Rotation.LengthSquared() - 1f) > .001f)
            throw new ArgumentException("Animation socket pose must be finite with a unit rotation.");
    }
}
