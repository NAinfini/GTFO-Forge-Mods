using GameData;
using Player;

namespace ForgeOrdnanceFrames;

/// <summary>Host: what enemies hear of a suit. Its pilot is registered with ForgeCore's <c>PlayerNoise.Scale</c> for the time the pilot
/// wears it (<see cref="FrameNoise.PilotMultiplier"/>); the game's own noise for that player's walking, running, crouching, jumping and
/// landing is untouched and only heard that many times as far. The landing of the suit itself has no pilot to hear, so the touchdown
/// impact makes the game's world noise (ForgeOrdnance's <c>OrdnanceImpact.NoiseRadius</c>) with the radius below.</summary>
internal static class Noise
{
    private const string DefaultDetection = "DefaultDetection";

    /// <summary>Starts scaling <paramref name="pilot"/>'s noise for as long as the returned lease is held.</summary>
    internal static ForgeCore.PlayerNoiseLease Scale(PlayerAgent pilot)
    {
        var lease = ForgeCore.PlayerNoise.Scale(pilot, FrameNoise.PilotMultiplier, Plugin.Id);
        return lease;
    }

    /// <summary>The landing noise radius: twice the game's default walking and running detection distance. 0 (and an error line) when
    /// the game has no such detection block.</summary>
    internal static float LandingRadius()
    {
        var block = EnemyDetectionDataBlock.GetBlock(DefaultDetection);
        if (block == null)
        {
            Plugin.Logger.ErrorOnce("Exosuit landing noise: the game has no " + DefaultDetection + " enemy detection block, so the landing makes no noise.");
            return 0f;
        }
        var radius = FrameNoise.LandingRadius(block.movementDetectionDistance);
        return radius;
    }
}
