namespace ForgeWeaponEnergyLab;

internal static class EnergyProtocol
{
    // Received packets queued between two frames; a link recovering from a stall delivers its backlog at once.
    internal const int InboxLimit = 1024;
    internal const float MaximumMuzzleDistance = 3.5f;
    // A shooter's reported shell damage is its base damage with boosters; the host takes at most this multiple of the base from any peer.
    internal const float MaximumDamageBoost = 5f;
    // A charge has no time limit and no shots to keep it alive: the shooter repeats it this often, and another machine ends it itself this long after the last one.
    internal const float ChargeRenewSeconds = 0.5f, ChargeExpirySeconds = 1.5f;
    // A machine that only draws a projectile holds it where it stopped for the host's word on where it ended; with none by then it ends there itself.
    internal const float FlightEndWaitSeconds = 0.6f;
}
