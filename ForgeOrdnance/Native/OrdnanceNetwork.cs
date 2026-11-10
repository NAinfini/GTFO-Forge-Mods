using System;
using System.Globalization;
using ForgeCore;

namespace ForgeOrdnance;

/// <summary>This mod's ForgeCore channel (message formats fingerprinted by ForgeCore, content = the definition set and the registered
/// payload types), so a player with other message formats or other call-in files is refused on both sides by ForgeCore. Handlers run
/// on the main thread in <see cref="Pump"/>; every level message carries the host's recovery epoch and one from an earlier epoch is
/// dropped there. The network layer does not authenticate senders, so every handler checks the sender itself.</summary>
internal static class OrdnanceNetwork
{
    private const int InboxCapacity = 512;
    private static NetChannel? _channel;
    /// <summary>The world epoch, minted by the host and adopted by clients through ForgeCore (<see cref="NetChannel.FollowWorld"/>): stamped
    /// into every level message, and a message from another one is dropped by ForgeCore. Zero until the world is up.</summary>
    internal static ulong Epoch => _channel?.Epoch ?? 0;

    internal static NetChannel Channel => _channel ?? throw new InvalidOperationException("Ordnance's network channel is not open.");

    /// <summary>Raised when ForgeCore refuses a player's copy of this mod (other message formats or other call-in files).</summary>
    internal static event Action<ulong>? Refused;

    internal static void Register(OrdnanceSession session)
    {
        if (_channel != null) return;
        _channel = Net.Open(Plugin.Id, InboxCapacity);
        _channel.Refused += (player, _) => Refused?.Invoke(player);
        _channel.PeerSynced += session.PeerSynced;
        _channel.EpochChanged += session.EpochChanged;
        _channel.FollowWorld(() => session.InLevel);
        _channel.PeerSilent += session.PeerSilent;
        Register<HelloMessage>(Messages.HelloEvent, session.OnHello);
        Register<LoadoutMessage>(Messages.LoadoutEvent, session.OnLoadoutSubmitted);
        Register<LoadoutMessage>(Messages.LoadoutStateEvent, session.OnLoadoutState);
        Register<LandingMessage>(Messages.LandingEvent, session.OnLanding);
        Register<RejectMessage>(Messages.RejectEvent, session.OnReject);
        Register<AcceptedMessage>(Messages.AcceptedEvent, session.OnAccepted);
        Register<ArrivedMessage>(Messages.ArrivedEvent, session.OnArrived);
        Register<SlotMessage>(Messages.SlotEvent, session.OnSlot);
        Register<CheckpointMessage>(Messages.CheckpointEvent, session.OnCheckpoint);
        Register<SentryExpiryMessage>(Messages.SentryExpiryEvent, session.OnSentryExpiry);
        Register<DeliveryMessage>(Messages.DeliveryEvent, session.OnDelivery);
        Register<PayloadDataMessage>(Messages.PayloadDataEvent, session.OnPayloadData);
        Register<ClaimMessage>(Messages.ClaimEvent, session.OnClaimRequest);
        Register<ClaimStateMessage>(Messages.ClaimStateEvent, session.OnClaimState);
        Register<DeliveryReplayMessage>(Messages.DeliveryReplayEvent, session.OnDeliveryReplay);
        Register<PayloadDataMessage>(Messages.PayloadReplayEvent, session.OnPayloadReplay);
    }

    /// <summary>A per-player lobby value of this mod (ForgeCore delivers it to every player and to players who join later).</summary>
    internal static NetLobbyValue Lobby(string name) => Channel.Lobby(name);

    /// <summary>The fingerprint both sides must share besides the message formats: the definition set and the payload types.</summary>
    internal static void SetContent(ulong definitions, ulong payloadTypes)
        => Channel.Content = definitions.ToString("x16", CultureInfo.InvariantCulture) + "/" + payloadTypes.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>ForgeCore's answer for <paramref name="player"/>'s copy of this mod.</summary>
    internal static PeerState State(ulong player) => _channel?.State(player) ?? PeerState.Unknown;

    private static void Register<T>(string name, Action<ulong, T> handler) where T : unmanaged, INetEpoch
    {
        // The hello is how an epoch reaches a client, so it is the one message no epoch can gate.
        if (name == Messages.HelloEvent) Channel.On<T>(name, handler);
        else Channel.OnEpoch<T>(name, handler);
    }

    internal static void Pump() => _channel?.Pump();

    internal static void Reset() => _channel?.Clear();

    internal static void ToHost<T>(string name, T message) where T : unmanaged, INetEpoch
    {
        if (name != Messages.HelloEvent) message.Epoch = Epoch;
        Channel.TryToHost(name, message);
    }

    internal static void To<T>(string name, T message, ulong player) where T : unmanaged, INetEpoch { message.Epoch = Epoch; Channel.To(player, name, message); }

    internal static void Broadcast<T>(string name, T message) where T : unmanaged, INetEpoch { message.Epoch = Epoch; Channel.Broadcast(name, message); }
}
