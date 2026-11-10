using ForgeCore;

namespace ForgeOrdnanceArsenal;

/// <summary>This package's own messages, for what is not per-call host data: a borrower's loan notices, and grenades.
/// They go through ForgeCore's channel for this plugin, whose message format fingerprint refuses a player with other
/// Arsenal messages explicitly. Handlers run on the main thread from <see cref="Pump"/>; senders are checked there, because the
/// network layer does not authenticate them.</summary>
internal static class ArsenalNetwork
{
    private const int InboxLimit = 256;
    private static NetChannel? _channel;

    internal static void Register()
    {
        _channel = Net.Open(Plugin.Id, InboxLimit);
        _channel.On<LoanNotice>(LoanNotice.Name, HeavyWeapons.OnLoanNotice);
        _channel.On<GrenadeShot>(GrenadeShot.Name, Grenades.OnShot);
        _channel.On<GrenadeBurst>(GrenadeBurst.Name, Grenades.OnBurst);
        _channel.On<HomingShot>(HomingShot.Name, HomingFire.OnShot);
    }

    internal static void Clear() => _channel?.Clear();

    internal static void Pump() => _channel?.Pump();

    internal static void ToHost<T>(string name, T message) where T : unmanaged
    {
        Channel.TryToHost(name, message);

    }

    /// <summary>To every other peer.</summary>
    internal static void Broadcast<T>(string name, T message) where T : unmanaged => Channel.Broadcast(name, message);

    internal static NetChannel Channel => _channel ?? throw new System.InvalidOperationException("Arsenal's network channel is not open.");
}
