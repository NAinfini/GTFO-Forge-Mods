namespace ForgeWeaponEnergyLab;

internal static class EnergyProtocol
{
    // Received packets queued between two frames; a link recovering from a stall delivers its backlog at once.
    internal const int InboxLimit = 1024;
    internal const float MaximumMuzzleDistance = 3.5f;
}
