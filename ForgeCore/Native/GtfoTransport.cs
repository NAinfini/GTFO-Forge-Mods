using System;
using GTFO.API;
using SNetwork;

namespace ForgeCore;

/// <summary>Channels over GTFO-API's NetworkAPI: <see cref="NetDelivery.Reliable"/> on the reliable ordered channel the game uses for
/// orders, <see cref="NetDelivery.Lossy"/> on its non-critical (unreliable) channel.</summary>
internal sealed class GtfoTransport : INetTransport
{
    private static SNet_ChannelType Channel(NetDelivery delivery)
        => delivery == NetDelivery.Lossy ? SNet_ChannelType.GameNonCritical : SNet_ChannelType.GameOrderCritical;

    public void Register<T>(string name, Action<ulong, T> receive) where T : unmanaged => NetworkAPI.RegisterEvent(name, receive);

    public void RegisterBytes(string name, Action<ulong, byte[]> receive) => NetworkAPI.RegisterFreeSizedEvent(name, receive);

    public unsafe void Send<T>(string name, T message, ulong to, NetDelivery delivery) where T : unmanaged
    {
        if (to == 0) NetworkAPI.InvokeEvent(name, message, Channel(delivery));
        else if (SNet.TryGetPlayer(to, out var player) && player != null) NetworkAPI.InvokeEvent(name, message, player, Channel(delivery));
        else return;
        Net.CountSent(sizeof(T), delivery);
    }

    public void SendBytes(string name, byte[] data, ulong to, NetDelivery delivery)
    {
        if (to == 0) NetworkAPI.InvokeFreeSizedEvent(name, data, Channel(delivery));
        else if (SNet.TryGetPlayer(to, out var player) && player != null) NetworkAPI.InvokeFreeSizedEvent(name, data, player, Channel(delivery));
        else return;
        Net.CountSent(data.Length, delivery);
    }

    public ulong Local => SNet.LocalPlayer?.Lookup ?? 0;

    public ulong Host => SNet.Master?.Lookup ?? 0;

    /// <summary>The session's members, not the Steam lobby's: the lobby also holds players the session hub has not accepted (a
    /// different game revision, still joining), who are not part of the game and would be greeted for nothing.</summary>
    public int Others(ulong[] into)
    {
        var players = SNet.SessionHub?.PlayersInSession;
        if (players == null) return 0;
        var local = Local;
        var count = 0;
        for (var i = 0; i < players.Count && count < into.Length; i++)
        {
            var player = players[i];
            if (player == null || player.IsBot || player.Lookup == local) continue;
            into[count++] = player.Lookup;
        }
        return count;
    }

    public bool IsHuman(ulong lookup) => SNet.TryGetPlayer(lookup, out var player) && player != null && !player.IsBot;
}
