namespace ForgeWeaponEnergyLabExperimental;

internal static class EnergyProtocol
{
    internal const string Version = EnergyLab.Version;
    internal static readonly System.Version Number = new(Version);
    // Received packets queued between two frames; a link recovering from a stall delivers its backlog at once.
    internal const int InboxLimit = 1024;
    internal const float HelloInterval = 1f;
    internal const float HelloLifetime = 3f;
    internal const float MaximumMuzzleDistance = 3.5f;
}
