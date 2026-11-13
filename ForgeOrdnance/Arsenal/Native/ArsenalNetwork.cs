using ForgeCore;

namespace ForgeOrdnance.Arsenal;

/// <summary>Arsenal's own messages, for what is not per-call host data: a borrower's loan notices, grenades and the tracking
/// rifle's tracer. They go on the mod's one ForgeCore channel (<see cref="OrdnanceNetwork"/> opens it), whose format fingerprint
/// refuses a player with other messages, and which drops a message sent in another level or before a checkpoint reload. Handlers
/// run on the main thread; senders are checked there, because the network layer does not authenticate them.</summary>
internal static class ArsenalNetwork
{
    internal static NetEvent<LoanNotice> Loan { get; private set; } = null!;
    internal static NetEvent<GrenadeShot> Shot { get; private set; } = null!;
    internal static NetEvent<GrenadeBurst> Burst { get; private set; } = null!;
    /// <summary>Shown only: a tracer that arrives late or not at all changes nothing.</summary>
    internal static NetEvent<HomingShot> Homing { get; private set; } = null!;

    internal static void Register()
    {
        var channel = OrdnanceNetwork.Channel;
        Loan = channel.Event<LoanNotice>(LoanNotice.Name, HeavyWeapons.OnLoanNotice);
        Shot = channel.Event<GrenadeShot>(GrenadeShot.Name, Grenades.OnShot);
        Burst = channel.Event<GrenadeBurst>(GrenadeBurst.Name, Grenades.OnBurst);
        Homing = channel.Event<HomingShot>(HomingShot.Name, HomingFire.OnShot, NetDelivery.Lossy);
    }
}
