namespace ForgeOrdnanceFrames;

/// <summary>How far enemies hear a suit. The suit's pilot is a player the game already makes noise for, and enemies hear that player
/// by the game's own rules (a walk, run, jump or landing inside the enemy's own detection distance, a crouch inside 1.9 m); the suit
/// makes every one of them <see cref="PilotMultiplier"/> times as far (ForgeCore's <c>PlayerNoise.Scale</c>). The suit's own landing,
/// which has no pilot, is heard as far as that player's run: the multiplier times the game's default detection distance.</summary>
public static class FrameNoise
{
    /// <summary>How many times as far as an ordinary player's noise a piloted suit is heard, in every movement state.</summary>
    public const float PilotMultiplier = 2f;

    /// <summary>The landing noise radius of a suit with no pilot: a run's, from the game's default <c>movementDetectionDistance</c>.</summary>
    public static float LandingRadius(float defaultMovementDetectionDistance) => defaultMovementDetectionDistance * PilotMultiplier;
}
