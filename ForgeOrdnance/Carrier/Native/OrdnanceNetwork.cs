using ForgeCore;

namespace ForgeOrdnance;

/// <summary>This mod's ForgeCore channel and everything that travels on it. ForgeCore fingerprints the formats and the content (the
/// definition set and the registered payload types), so a player with other formats or other call-in files is refused on both
/// sides; it drops what belongs to another level or checkpoint, sends the tables and the checkpoint copy to a player who joins, and
/// runs every handler on the main thread. The network layer does not authenticate senders: a table and the checkpoint copy take
/// only the host's writes, an event's handler checks the sender itself.</summary>
internal static class OrdnanceNetwork
{
    internal static NetChannel Channel { get; private set; } = null!;

    /// <summary>Client to host, answered with one <see cref="Rejection"/> byte: this player's loadout (<see cref="LoadoutRow"/>), and
    /// the landing of a thrown ball (<see cref="LandingMessage"/>).</summary>
    internal static NetRequest Loadout { get; private set; } = null!;
    internal static NetRequest Landing { get; private set; } = null!;

    /// <summary>Host to every peer: a call arrived; a call failed on arrival.</summary>
    internal static NetEvent<ArrivedMessage> Arrived { get; private set; } = null!;
    internal static NetEvent<CallKey> Failed { get; private set; } = null!;
    /// <summary>Host to every peer: a <see cref="StateKey"/>, then the bytes of <c>OrdnanceApi.SendData</c>.</summary>
    internal static NetBytes PayloadData { get; private set; } = null!;
    /// <summary>Client to host and the host's word to every peer: taking a claimable is a broadcast state, not one answer to one player.</summary>
    internal static NetEvent<ClaimMessage> Claim { get; private set; } = null!;
    internal static NetEvent<ClaimStateMessage> ClaimState { get; private set; } = null!;

    /// <summary>The host's tables: each player's accepted loadout and slots, the highest throw number it accepted from each player,
    /// the calls waiting to arrive, the pods and shells standing, the sentries that end by themselves, the states payload types
    /// keep (<c>OrdnanceApi.SendState</c>) and the level-start drops that have arrived.</summary>
    internal static NetTable<ulong, LoadoutRow> LoadoutRows { get; private set; } = null!;
    internal static NetTable<SlotKey, SlotRow> SlotRows { get; private set; } = null!;
    internal static NetTable<ulong, uint> FloorRows { get; private set; } = null!;
    internal static NetTable<CallKey, CallRow> CallRows { get; private set; } = null!;
    internal static NetTable<DeliveryKey, DeliveryRow> DeliveryRows { get; private set; } = null!;
    internal static NetTable<ushort, SentryRow> SentryRows { get; private set; } = null!;
    internal static NetByteTable<StateKey> StateRows { get; private set; } = null!;
    internal static NetTable<uint, byte> StartedRows { get; private set; } = null!;

    /// <summary>The slot book at the last checkpoint (<see cref="SlotSnapshot.Encode"/>), restored by every peer when it is reloaded.</summary>
    internal static NetCheckpointCopy Checkpoint { get; private set; } = null!;

    internal static void Register(OrdnanceSession session)
    {
        Channel = Net.Open(Plugin.Id);
        Channel.Refused += (player, _) => session.Mismatch(player);
        Channel.PeerSynced += session.PeerSynced;
        Channel.PeerSilent += session.PeerSilent;
        Loadout = Channel.Request("Loadout", 1, session.ServeLoadout, 64);
        Landing = Channel.Request("Landing", 1, session.ServeLanding, 64);
        Arrived = Channel.Event<ArrivedMessage>("Arrived", session.OnArrived);
        Failed = Channel.Event<CallKey>("Failed", session.OnFailed);
        PayloadData = Channel.Bytes("PayloadData", 1, session.OnPayloadData, maxBytes: 256);
        Claim = Channel.Event<ClaimMessage>("Claim", session.OnClaimRequest);
        ClaimState = Channel.Event<ClaimStateMessage>("ClaimState", session.OnClaimState);
        LoadoutRows = Channel.Table<ulong, LoadoutRow>("Loadouts");
        LoadoutRows.Changed += session.OnLoadoutRow;
        SlotRows = Channel.Table<SlotKey, SlotRow>("Slots");
        SlotRows.Changed += session.OnSlotRow;
        FloorRows = Channel.Table<ulong, uint>("Floors");
        CallRows = Channel.Table<CallKey, CallRow>("Calls");
        CallRows.Changed += session.OnCallRow;
        DeliveryRows = Channel.Table<DeliveryKey, DeliveryRow>("Deliveries");
        DeliveryRows.Changed += session.OnDeliveryRow;
        SentryRows = Channel.Table<ushort, SentryRow>("Sentries");
        SentryRows.Changed += session.OnSentryRow;
        StateRows = Channel.Table<StateKey>("States", 1);
        StateRows.Changed += session.OnStateRow;
        StartedRows = Channel.Table<uint, byte>("Started");
        Checkpoint = Channel.CheckpointCopy("Checkpoint", 1);
    }
}
