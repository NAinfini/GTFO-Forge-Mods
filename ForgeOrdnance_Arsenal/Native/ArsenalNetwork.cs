using ForgeCore;
using SNetwork;

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
        _channel =Net.Open(Plugin.Id, InboxLimit);
        _channel.On<LoanNotice>(LoanNotice.Name, HeavyWeapons.OnLoanNotice);
        _channel.On<GrenadeShot>(GrenadeShot.Name, Grenades.OnShot);
        _channel.On<GrenadeBurst>(GrenadeBurst.Name, Grenades.OnBurst);
    }

    internal static void Clear() => _channel?.Clear();

    internal static void Pump() => _channel?.Pump();

    internal static void ToHost<T>(string name, T message) where T : unmanaged
    {
        Channel.TryToHost(name, message);

    }

    /// <summary>To every other peer.</summary>
    internal static void Broadcast<T>(string name, T message) where T : unmanaged => Channel.Broadcast(name, message);

    /// <summary>True when <paramref name="lookup"/> is a human player in this session.</summary>
    internal static bool IsPlayer(ulong lookup) => SNet.TryGetPlayer(lookup, out var p) && p != null && !p.IsBot;

    private static NetChannel Channel => _channel ?? throw new System.InvalidOperationException("Arsenal's network channel is not open.");
}
