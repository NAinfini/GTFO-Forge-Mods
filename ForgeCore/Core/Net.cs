using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ForgeCore;

/// <summary>What a channel knows about another player's copy of the same mod.</summary>
public enum PeerState : byte
{
    /// <summary>No answer yet (just joined, or the mod is not installed there).</summary>
    Unknown,
    /// <summary>Same message formats and content: messages are exchanged.</summary>
    Compatible,
    /// <summary>Different message formats or content: both sides logged it and messages from that player are dropped.</summary>
    Refused,
}

/// <summary>How a message travels and what the receiving queue may do with it. A message's delivery is fixed where its handler is
/// registered (<see cref="NetChannel.On{T}(string, Action{ulong, T}, NetDelivery)"/>), and every send of that name follows it.</summary>
public enum NetDelivery : byte
{
    /// <summary>Reliable and ordered, and never dropped: requests, answers, checkpoints and state replays. It does not count toward
    /// the channel's queue capacity, so a burst of <see cref="Lossy"/> traffic cannot push it out.</summary>
    Reliable,
    /// <summary>Unreliable (the game's non-critical channel): the network may lose it and it may arrive out of order. For frequent
    /// presentation messages such as shots and hit directions. It waits in a queue of at most the channel's capacity; the excess
    /// is dropped and reported with an error in the mod's log.</summary>
    Lossy,
}

/// <summary>The network underneath channels: GTFO-API events in the game, an in-memory hub offline.</summary>
internal interface INetTransport
{
    void Register<T>(string name, Action<ulong, T> receive) where T : unmanaged;
    void RegisterBytes(string name, Action<ulong, byte[]> receive);
    /// <summary><paramref name="to"/> 0: every other player.</summary>
    void Send<T>(string name, T message, ulong to, NetDelivery delivery) where T : unmanaged;
    void SendBytes(string name, byte[] data, ulong to, NetDelivery delivery);
    ulong Local { get; }
    /// <summary>0 when there is no host (not in a session).</summary>
    ulong Host { get; }
    /// <summary>The other human players in the session, written into <paramref name="into"/>; returns how many.</summary>
    int Others(ulong[] into);
    /// <summary>True when <paramref name="lookup"/> is a human player this peer knows (the local player included), not a bot.</summary>
    bool IsHuman(ulong lookup);
}

/// <summary>The handshake every channel sends under one name per mod, so two different builds still meet and refuse each other
/// explicitly: the fingerprint of the channel's registered message formats, and the mod's content hash. Fixed 17-byte layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct NetHello
{
    internal ulong Format, Content;
    internal byte Flags;

    internal readonly bool Answer => (Flags & 1) != 0;
    /// <summary>The sender's content is not known yet (<see cref="NetChannel.HoldContent"/>): nothing can be compared until it greets again.</summary>
    internal readonly bool Held => (Flags & 2) != 0;

    internal static NetHello Of(ulong format, string content, bool answer, bool held)
        => new() { Format = format, Content = Fnv.Hash64(content), Flags = (byte)((answer ? 1 : 0) | (held ? 2 : 0)) };
}

/// <summary>The readiness message of <see cref="NetChannel.EnableReadiness"/>: the sender's world epoch and whether it is ready.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct NetReady : INetEpoch
{
    internal ulong Epoch;
    internal int Ready;
    ulong INetEpoch.Epoch { readonly get => Epoch; set => Epoch = value; }
}

/// <summary>What a channel's registered messages look like on the wire, as plain text that is the same on every machine: the
/// message name, how it travels and its field layout (each field's name and type in declaration order, plus its offset in explicit-layout structs, nested structs expanded,
/// fixed buffers with their length, plus the struct's byte size). The fingerprint is FNV-1a over that text, so any change to a
/// name, a delivery or a layout changes it, and no number has to be bumped by hand.</summary>
internal static class NetFormat
{
    internal const string BytesLayout = "bytes";

    internal static string LayoutOf<T>() where T : unmanaged
    {
        var text = new StringBuilder();
        Describe(typeof(T), text);
        return text.Append('#').Append(Unsafe.SizeOf<T>()).ToString();
    }

    private static void Describe(Type type, StringBuilder text)
    {
        if (type.IsEnum) { text.Append("enum:").Append(Enum.GetUnderlyingType(type).Name); return; }
        if (type.IsPrimitive) { text.Append(type.Name); return; }
        text.Append('{');
        var first = true;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken))
        {
            if (!first) text.Append(',');
            first = false;
            text.Append(field.Name);
            if (type.IsExplicitLayout) text.Append('@').Append(field.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? 0);
            text.Append(':');
            var buffer = field.GetCustomAttribute<FixedBufferAttribute>();
            if (buffer != null) { Describe(buffer.ElementType, text); text.Append('[').Append(buffer.Length).Append(']'); }
            else Describe(field.FieldType, text);
        }
        text.Append('}');
    }

    /// <summary>One message: its name, delivery and the hash of its layout. The channel's fingerprint covers these in name order.</summary>
    internal readonly struct Entry
    {
        internal readonly string Name;
        internal readonly NetDelivery Delivery;
        internal readonly ulong Layout;
        internal Entry(string name, NetDelivery delivery, ulong layout) { Name = name; Delivery = delivery; Layout = layout; }
    }

    internal static ulong Fingerprint(List<Entry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            text.Append(entry.Name).Append('|').Append((int)entry.Delivery).Append('|').Append(entry.Layout.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
        return Fnv.Hash64(text.ToString());
    }

    /// <summary>The per-message list, then the channel's <see cref="NetChannel.ContentDetail"/> (empty when it has none).</summary>
    internal static byte[] Write(List<Entry> entries, byte[]? detail)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(entries.Count);
            foreach (var entry in entries) { writer.Write(entry.Name); writer.Write((byte)entry.Delivery); writer.Write(entry.Layout); }
            writer.Write(detail?.Length ?? 0);
            if (detail != null) writer.Write(detail);
        }
        return stream.ToArray();
    }

    internal static List<Entry>? Read(byte[] data, out byte[] detail)
    {
        detail = Array.Empty<byte>();
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            var count = reader.ReadInt32();
            if (count < 0 || count > 4096) return null;
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++) entries.Add(new Entry(reader.ReadString(), (NetDelivery)reader.ReadByte(), reader.ReadUInt64()));
            var length = reader.ReadInt32();
            if (length < 0 || length > data.Length) return null;
            detail = reader.ReadBytes(length);
            return detail.Length == length ? entries : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The messages that differ between this side ("here") and the other ("there"), by name; empty when they agree.</summary>
    internal static string Differences(List<Entry> here, List<Entry> there)
    {
        var theirs = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in there) theirs[entry.Name] = entry;
        var found = new List<string>();
        foreach (var entry in here.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (!theirs.TryGetValue(entry.Name, out var other)) { found.Add("message '" + entry.Name + "' is missing there"); continue; }
            if (other.Delivery != entry.Delivery) found.Add("message '" + entry.Name + "' travels " + entry.Delivery + " here but " + other.Delivery + " there");
            if (other.Layout != entry.Layout) found.Add("message '" + entry.Name + "' has a different field layout");
        }
        var ours = new HashSet<string>(here.Select(e => e.Name), StringComparer.Ordinal);
        foreach (var entry in there.OrderBy(e => e.Name, StringComparer.Ordinal))
            if (!ours.Contains(entry.Name)) found.Add("message '" + entry.Name + "' exists only there");
        return string.Join("; ", found);
    }
}

/// <summary>One mod's network channel. Message events are named <c>modId/name</c>. A channel opened with <see cref="Net.OpenInline"/>
/// runs handlers and the handshake as each message arrives instead of queueing them for <see cref="Pump"/>. Every registered message adds its name, delivery
/// and field layout to the channel's format fingerprint (no protocol number is kept by hand); the handshake compares that
/// fingerprint and the mod's content hash, and when they differ both sides refuse each other (<see cref="Refused"/>, and an error
/// in the mod's log naming the messages that differ, from the format list the two sides then exchange). Received
/// messages wait in a queue until the mod calls <see cref="Pump"/> on its own frame: <see cref="NetDelivery.Reliable"/> messages
/// are never dropped, <see cref="NetDelivery.Lossy"/> ones are bounded by the channel's capacity. Senders are not
/// authenticated by the network: handlers check the sender themselves.</summary>
public sealed class NetChannel
{
    /// <summary>How long a greeted player may stay silent before <see cref="PeerSilent"/> is raised.</summary>
    internal const double AnswerSeconds = 10.0;

    /// <summary>How many more greetings an unanswered player gets, one per <see cref="AnswerSeconds"/>, before it counts as silent: a
    /// joining player drops a greeting from a player it does not list yet, and loading can take longer than one window.</summary>
    internal const int GreetingRetries = 2;

    private sealed class Peer
    {
        internal PeerState State;
        internal bool Greeted;
        internal double GreetedAt;
        internal bool ReportedSilent;
        internal bool Answered;
        internal int Retries;
        internal ulong RefusedFormat, RefusedContent;
        internal bool ContentDiffers, Reported;
        internal List<NetFormat.Entry>? There;
        internal byte[]? ThereDetail;
    }

    private readonly INetTransport _transport;
    private readonly ModLog _log;
    private readonly int _capacity;
    private readonly bool _inline;
    private readonly object _gate = new();
    private readonly Queue<(ulong Sender, Action Work, bool Lossy)> _inbox = new();
    private readonly Queue<(ulong Sender, NetHello Hello)> _hellos = new();
    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly List<ulong> _gone = new(4), _silent = new(4);
    private readonly Queue<(ulong Sender, byte[] Data)> _formats = new();
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private readonly List<NetFormat.Entry> _entries = new();
    private ulong _format;
    private bool _formatDirty = true;
    private readonly List<NetLobbyValue> _lobby = new();
    private readonly ulong[] _members = new ulong[8];
    private int _memberCount;
    private double _nextSilent = double.PositiveInfinity;
    private bool _greetAgain, _announce;
    private string _content = "";
    private ulong _host, _local;
    private int _dropped, _lossyQueued, _reliableDropped;
    /// <summary>Handshakes and format lists waiting for <see cref="Tick"/>; read without the lock so an idle frame takes none.</summary>
    private volatile int _control;
    private bool _readiness;
    private readonly Dictionary<ulong, bool> _ready = new();
    private readonly Dictionary<ulong, ulong> _highWater = new();
    private ulong _epochFrom;
    private bool _hostReady;
    private ulong _unlisted;
    private double _unlistedSince;
    private const double RosterRetrySeconds = 5;

    /// <summary>A registered message: its full event name and how it travels (name, delivery and layout make its format entry).</summary>
    private sealed class Route
    {
        internal string Event = "";
        internal NetDelivery Delivery;
    }

    internal NetChannel(INetTransport transport, string modId, int capacity, bool inline = false)
    {
        _inline = inline;
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _transport = transport;
        ModId = modId;
        _capacity = capacity;
        _log = Log.For(modId);
        transport.Register<NetHello>(HelloName(modId), (sender, hello) =>
        {
            if (_inline) { Inline(() => Receive(sender, hello)); return; }
            lock (_gate) if (_hellos.Count < 64) { _hellos.Enqueue((sender, hello)); _control++; return; }
            ControlOverflow();
        });
        transport.RegisterBytes(FormatsName(modId), (sender, data) =>
        {
            if (data == null || data.Length > MaxFormatsBytes) { OversizedBytes(sender); return; }
            if (_inline) { Inline(() => ReceiveFormats(sender, data)); return; }
            lock (_gate) if (_formats.Count < 64) { _formats.Enqueue((sender, data)); _control++; return; }
            ControlOverflow();
        });
    }

    /// <summary>The largest free-sized body ForgeCore accepts from a player, checked where it arrives so a handler never sees more:
    /// a format list (up to 4096 entries) and a lobby value. A larger or missing body is dropped and reported once per mod.</summary>
    internal const int MaxFormatsBytes = 512 * 1024, MaxLobbyBytes = 64 * 1024;

    /// <summary>Most reliable messages waiting for <see cref="Pump"/> at once. Reliable messages are never dropped for lack of room
    /// in a normal frame, so this only stops a player that floods them; the excess is dropped and reported.</summary>
    internal const int ReliableLimit = 8192;

    private void OversizedBytes(ulong sender)
    {
        Net.Drop(Net.DropOversized);
        _log.ErrorOnce(Net.DropOversized, "Player " + sender + " sent a " + ModId + " message that is missing or larger than ForgeCore accepts; it was dropped.");
    }

    /// <summary>A handshake or format list found its queue full (64) and was dropped; the sender's next greeting repeats it.</summary>
    private void ControlOverflow()
    {
        Net.Drop(Net.DropControlOverflow);
        _log.ErrorOnce(Net.DropControlOverflow, "The handshake queue of " + ModId + " overflowed (64 waiting); a handshake message was dropped, so that player may stay unanswered.");
    }

    public string ModId { get; }
    /// <summary>The fingerprint of every registered message's name, delivery and field layout, in hex; what the handshake
    /// compares. It changes with each registration, so read it after the mod has registered its messages.</summary>
    public string Format => Fingerprint().ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>An optional fingerprint of the content both sides must share besides the message formats (for example a hash of the
    /// definitions); changing it asks every player again. Setting it ends <see cref="HoldContent"/>.</summary>
    public string Content
    {
        get => _content;
        set
        {
            if (_content == (value ?? "") && !_held) return;
            _content = value ?? "";
            _held = false;
            AskAgain();
        }
    }

    /// <summary>This player does not know its content yet (its definitions are still loading, or it left the session's mod state):
    /// until <see cref="Content"/> is set no player is judged either way, so nobody is refused for a difference that is only a
    /// matter of time, and setting it asks every player again.</summary>
    public void HoldContent()
    {
        if (_held) return;
        _held = true;
        AskAgain();
    }
    private bool _held;

    /// <summary>What this player tells a player whose content differs, so each side can say what differs in its own words; sent once
    /// per refusal with the format list and read with <see cref="ContentDetailOf"/>. With a detail set, <see cref="Refused"/> waits for
    /// the other side's.</summary>
    public byte[]? ContentDetail { get; set; }

    /// <summary>The <see cref="ContentDetail"/> of a player this channel refused, or null while it has not arrived.</summary>
    public byte[]? ContentDetailOf(ulong player) => _peers.TryGetValue(player, out var peer) ? peer.ThereDetail : null;

    /// <summary>A refused player's message formats differ from this one's, which outranks any difference of content.</summary>
    public bool FormatDiffers(ulong player) => _peers.TryGetValue(player, out var peer) && peer.State == PeerState.Refused && peer.RefusedFormat != Fingerprint();

    /// <summary>Forgets every verdict and greets every player again at the next frame: this player restarted what it holds for the
    /// session, so every player's copy is judged afresh and <see cref="PeerSynced"/> follows for each that matches.</summary>
    private void AskAgain()
    {
        foreach (var peer in _peers.Values)
        {
            peer.State = PeerState.Unknown;
            peer.Greeted = false;
            peer.ReportedSilent = false;
            peer.Answered = false;
            peer.Retries = 0;
            peer.RefusedFormat = peer.RefusedContent = 0;
            peer.Reported = false;
            peer.There = null;
            peer.ThereDetail = null;
        }
        _greetAgain = true;
    }

    private ulong Fingerprint()
    {
        if (_formatDirty) { _format = NetFormat.Fingerprint(_entries); _formatDirty = false; }
        return _format;
    }

    /// <summary>Raised on the main thread when a player's copy is refused: the player's lookup and the reason (for logs; a mod
    /// shows the player its own words).</summary>
    public event Action<ulong, string>? Refused;

    /// <summary>The host's world epoch: the host sets it when a world starts, a client adopts it from the host's readiness message
    /// (<see cref="EnableReadiness"/>). Zero means no world yet. Stamp it into messages a mod sends and receive them with
    /// <see cref="OnEpoch{T}"/>, which drops what another world sent.</summary>
    public ulong Epoch { get => _epoch; set { if (_epoch == value) return; _epoch = value; _announce = true; } }
    private ulong _epoch;

    public PeerState State(ulong player) => _peers.TryGetValue(player, out var peer) ? peer.State : PeerState.Unknown;

    public bool IsHost => _transport.Host != 0 && _transport.Host == _transport.Local;

    /// <summary>True when this peer is a client and <paramref name="sender"/> is the host: the check a handler of a host-to-client message
    /// makes on the sender the network gives it (senders are not authenticated, so a client never acts on such a message from anyone else).
    /// False on the host itself and when there is no host.</summary>
    public bool FromHost(ulong sender) => !IsHost && _transport.Host != 0 && _transport.Host == sender;

    /// <summary>True when <paramref name="lookup"/> is a human player this peer knows (the local player included), not a bot: the check
    /// a handler of a player-to-host message makes before acting for that player.</summary>
    public bool IsPlayer(ulong lookup) => _transport.IsHuman(lookup);

    public string EventName(string name) => ModId + "/" + name;

    internal static string HelloName(string modId) => "ForgeCore/handshake/" + modId;
    internal static string FormatsName(string modId) => "ForgeCore/formats/" + modId;

    /// <summary>Handles fixed-size message <paramref name="name"/> as <see cref="NetDelivery.Reliable"/>; the handler runs inside <see cref="Pump"/>.</summary>
    public void On<T>(string name, Action<ulong, T> handler) where T : unmanaged => On(name, handler, NetDelivery.Reliable);

    /// <summary>Handles fixed-size message <paramref name="name"/> delivered as <paramref name="delivery"/>; the handler runs inside
    /// <see cref="Pump"/>. Every player that sends the message registers it too (with the same delivery): the send methods read how it
    /// travels from this registration.</summary>
    public void On<T>(string name, Action<ulong, T> handler, NetDelivery delivery) where T : unmanaged
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var route = Claim(name, delivery, NetFormat.LayoutOf<T>());
        _transport.Register<T>(route.Event, (sender, message) => Enqueue(sender, () => handler(sender, message), delivery));
    }

    /// <summary>Handles a byte body of any length up to <paramref name="maxBytes"/> as message <paramref name="name"/>: for a body whose
    /// length is part of its content. The format fingerprint covers its name and delivery, not what the bytes mean, so the mod puts
    /// that in its <see cref="Content"/>. A larger or missing body is dropped where it arrives and reported once.</summary>
    public void OnBytes(string name, Action<ulong, byte[]> handler, NetDelivery delivery = NetDelivery.Reliable, int maxBytes = MaxLobbyBytes)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var route = Claim(name, delivery, NetFormat.BytesLayout);
        _transport.RegisterBytes(route.Event, (sender, data) =>
        {
            if (data == null || data.Length > maxBytes) { OversizedBytes(sender); return; }
            Enqueue(sender, () => handler(sender, data), delivery);
        });
    }

    /// <summary>Sends byte message <paramref name="name"/> (registered with <see cref="OnBytes"/>) to <paramref name="to"/>, or to every other player when it is 0.</summary>
    public void SendBytes(string name, byte[] data, ulong to = 0)
    {
        var route = RouteOf(name);
        _transport.SendBytes(route.Event, data, to, route.Delivery);
    }

    /// <summary>As <see cref="On{T}(string, Action{ulong, T}, NetDelivery)"/> for a message stamped with its sender's
    /// <see cref="Epoch"/>: a message whose epoch is not this channel's current one, or any message while the epoch is zero, is
    /// dropped before the handler runs.</summary>
    public void OnEpoch<T>(string name, Action<ulong, T> handler, NetDelivery delivery = NetDelivery.Reliable) where T : unmanaged, INetEpoch
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        On<T>(name, (sender, message) =>
        {
            if (message.Epoch != 0 && message.Epoch == Epoch) handler(sender, message);
        }, delivery);
    }

    /// <summary>As <see cref="ToHost{T}"/>, but nothing is sent while there is no host (the session is ending or the host left).</summary>
    public bool TryToHost<T>(string name, T message) where T : unmanaged
    {
        if (_transport.Host == 0) return false;
        ToHost(name, message);
        return true;
    }

    public void ToHost<T>(string name, T message) where T : unmanaged
    {
        var route = RouteOf(name);
        _transport.Send(route.Event, message, _transport.Host != 0 ? _transport.Host : throw new InvalidOperationException("No host to send " + name + " to."), route.Delivery);
    }

    public void To<T>(ulong player, string name, T message) where T : unmanaged
    {
        var route = RouteOf(name);
        _transport.Send(route.Event, message, player != 0 ? player : throw new ArgumentException("Name the player.", nameof(player)), route.Delivery);
    }

    /// <summary>To every other player.</summary>
    public void Broadcast<T>(string name, T message) where T : unmanaged
    {
        var route = RouteOf(name);
        _transport.Send(route.Event, message, 0, route.Delivery);
    }

    /// <summary>A value every player of the session keeps for this mod under <paramref name="name"/> (lobby picks, the host's rules):
    /// each player sets only their own, and the channel delivers it to every other player, again to each player whose copy of the mod
    /// answers the handshake (a player who joins later, a reconnect, a content change), and forgets the value of a player who left,
    /// was refused or belongs to an earlier session. Received values are applied inside <see cref="Pump"/>.</summary>
    public NetLobbyValue Lobby(string name)
    {
        var route = Claim(LobbyPrefix + name, NetDelivery.Reliable, NetFormat.BytesLayout);
        var value = new NetLobbyValue(this, name, _log);
        _transport.RegisterBytes(route.Event, (sender, data) =>
        {
            if (data == null || data.Length > MaxLobbyBytes + 1) { OversizedBytes(sender); return; }
            Enqueue(sender, () => value.Receive(sender, data), NetDelivery.Reliable);
        });
        _lobby.Add(value);
        return value;
    }

    /// <summary>A (content number, content hash) list per player for this mod under <paramref name="name"/>, matched entry by entry: see <see cref="NetManifest"/>.</summary>
    public NetManifest Manifest(string name) => new(Lobby("manifest/" + name), ModId, _log);

    internal const string LobbyPrefix = "lobby/";
    private const string StatePrefix = "state/";
    private readonly List<IPlayerStateSlot> _states = new();

    /// <summary>A value per player with the moment it began, for this mod under <paramref name="name"/>: see <see cref="PlayerStateSlot{T}"/>.</summary>
    public PlayerStateSlot<T> PlayerState<T>(string name) where T : unmanaged => PlayerState<T>(name, Net.Clock);

    internal PlayerStateSlot<T> PlayerState<T>(string name, Func<float> clock) where T : unmanaged
    {
        var route = Claim(StatePrefix + name, NetDelivery.Reliable, NetFormat.LayoutOf<T>());
        var slot = new PlayerStateSlot<T>(this, route.Event, clock, _log);
        _transport.RegisterBytes(route.Event, (sender, data) => Enqueue(sender, () => { if (IsMember(sender)) slot.Receive(sender, data); }, NetDelivery.Reliable));
        _states.Add(slot);
        PeerSynced += slot.Resend;
        EpochChanged += slot.EpochTaken;
        GameEvents.LevelEnded += slot.ForgetAll;
        GameEvents.CheckpointReloaded += slot.ForgetAll;
        return slot;
    }

    internal ulong LocalPlayer => _transport.Local;

    /// <summary>To the players the session lists (<paramref name="to"/> 0) or one of them; nothing outside a session.</summary>
    internal void SendState(string eventName, byte[] data, ulong to)
    {
        if (_transport.Host == 0) return;
        if (to != 0)
        {
            if (IsMember(to)) _transport.SendBytes(eventName, data, to, NetDelivery.Reliable);
            return;
        }
        for (var i = 0; i < _memberCount; i++) _transport.SendBytes(eventName, data, _members[i], NetDelivery.Reliable);
    }

    /// <summary><paramref name="player"/> is another player of the current session (a value from someone who already left is stale).</summary>
    internal bool IsMember(ulong player)
    {
        return _host != 0 && Array.IndexOf(_members, player, 0, _memberCount) >= 0;
    }

    /// <summary>A handshake or lobby value came from a player the member list does not show. Only recorded: the list is read again
    /// when the session lists a player (<see cref="Net.NoteSession"/>), so the player is greeted then; if that has not happened after
    /// <see cref="RosterRetrySeconds"/> the value was lost for good and one error says so.</summary>
    internal void NoteUnlisted(ulong player)
    {
        if (_unlisted != 0) return;
        _unlisted = player;
        _unlistedSince = double.NaN;
    }

    private void CheckUnlisted(double now)
    {
        if (double.IsNaN(_unlistedSince)) _unlistedSince = now;
        if (IsMember(_unlisted)) { _unlisted = 0; return; }
        if (now - _unlistedSince < RosterRetrySeconds) return;
        _log.ErrorOnce("core-player-sent-data-but-session-never", "Player " + _unlisted + " sent " + ModId + " data but the session never listed them within " + RosterRetrySeconds + " s; their lobby values were dropped.");
        _unlisted = 0;
    }

    /// <summary>To every other player (<paramref name="to"/> 0) or one; nothing outside a session.</summary>
    internal void SendLobby(NetLobbyValue value, ulong to)
    {
        if (_transport.Host == 0) return;
        var name = EventName(LobbyPrefix + value.Name);
        if (to != 0)
        {
            if (IsMember(to)) _transport.SendBytes(name, value.Framed(), to, NetDelivery.Reliable);
            return;
        }
        // Only to players the session lists: a player not in the session hub yet would get a packet from someone it does not know.
        for (var i = 0; i < _memberCount; i++) _transport.SendBytes(name, value.Framed(), _members[i], NetDelivery.Reliable);
    }

    /// <summary>Runs every queued handler, oldest first, on the caller's thread. Messages from refused players are dropped;
    /// a handler that throws is logged and the rest still run. Lossy messages that found the queue full were dropped when they
    /// arrived; the count is reported here.</summary>
    public void Pump()
    {
        Settle();
        DrainControl();
        int dropped;
        int reliableDropped;
        lock (_gate) { dropped = _dropped; _dropped = 0; reliableDropped = _reliableDropped; _reliableDropped = 0; }
        if (reliableDropped != 0) _log.ErrorOnce("core-reliable-receive-queue-held-messages-more", "Reliable receive queue of " + ModId + " held " + ReliableLimit + " messages; " + reliableDropped + " more were dropped.");
        if (dropped != 0) _log.ErrorOnce("core-lossy-receive-queue-overflowed-capacity-messages", "Lossy receive queue of " + ModId + " overflowed (capacity " + _capacity + "); " + dropped + " messages were dropped.");
        while (true)
        {
            (ulong Sender, Action Work, bool Lossy) entry;
            lock (_gate)
            {
                if (_inbox.Count == 0) return;
                entry = _inbox.Dequeue();
                if (entry.Lossy) _lossyQueued--;
            }
            if (State(entry.Sender) == PeerState.Refused) { Net.Drop(Net.DropPeerRefused); continue; }
            try { entry.Work(); }
            catch (Exception error) { _log.ErrorOnce("core-message-handler-failed", "A " + ModId + " message handler failed: " + error); }
        }
    }

    private const string ReadyEvent = "ForgeCore/ready";

    /// <summary>True while the mod may take part in readiness: announcing and incoming readiness is accepted only
    /// then (for example once the world is up and the mod's definitions are loaded). The mod sets it before each <see cref="Pump"/>.</summary>
    public bool Live { get => _live; set { if (_live == value) return; _live = value; _announce = true; } }
    private bool _live;

    /// <summary>Whether this player's mod is ready (assets loaded, presentation up); sent to the host (or by the host to everyone) when it changes.</summary>
    public bool Ready { get => _ready0; set { if (_ready0 == value) return; _ready0 = value; _announce = true; } }
    private bool _ready0;

    /// <summary>Raised on the main thread when this player's <see cref="Epoch"/> became a new world's: on a client that adopted the host's (inside
    /// <see cref="Pump"/>), on the host that minted one in <see cref="FollowWorld"/>, after it did.</summary>
    public event Action? EpochChanged;

    /// <summary>Opts the channel into the readiness handshake: the host announces its <see cref="Epoch"/> and <see cref="Ready"/> state,
    /// a client adopts a newer epoch only from the host (an older one, or one from a world it already left, is refused) and answers
    /// with its own state, and the host records each compatible member's answer. Call it once, with the other registrations.</summary>
    public void EnableReadiness()
    {
        if (_readiness) return;
        _readiness = true;
        On<NetReady>(ReadyEvent, OnReady);
    }

    /// <summary>Raised inside <see cref="Net.Tick"/> when a player's copy matches this one and holds none of this mod's state: it
    /// answered a greeting for the first time, greeted again (it joined, rebuilt its view of this player) or the content changed.
    /// The handler sends that player (<see cref="To{T}"/>) the mod's current state; ForgeCore itself already sends the lobby values
    /// and, on the host, the readiness. It can fire more than once for one player, so what it sends must be safe to repeat.</summary>
    public event Action<ulong>? PeerSynced;

    /// <summary>Raised once when a greeted player has answered none of its greetings (<see cref="GreetingRetries"/>, each given
    /// <see cref="AnswerSeconds"/>): the mod or ForgeCore is not installed there, or is an older version.</summary>
    public event Action<ulong>? PeerSilent;

    /// <summary>The host announced that it is ready in the current world.</summary>
    public bool HostReady => _hostReady;

    /// <summary>On the host: <paramref name="player"/> is compatible and answered that it is ready in the current world.</summary>
    public bool PeerReady(ulong player)
        => State(player) == PeerState.Compatible && _ready.TryGetValue(player, out var ready) && ready;

    /// <summary>The host broadcasts, a client tells the host. Sent when <see cref="Epoch"/>, <see cref="Live"/> or <see cref="Ready"/>
    /// changes, to a player who just synced (the host), and as the answer to the host's message; never on a timer.</summary>
    private void SendReady(ulong to)
    {
        if (!Live || !_readiness || !IsHost && _epoch == 0) return;
        var message = new NetReady { Epoch = _epoch, Ready = Ready ? 1 : 0 };
        if (to != 0) To(to, ReadyEvent, message);
        else if (IsHost) Broadcast(ReadyEvent, message);
        else TryToHost(ReadyEvent, message);
    }
    /// <summary>The world ended: the epoch returns to zero (a client remembers it and takes no older one from that host) and
    /// every readiness answer is forgotten.</summary>
    public void ResetWorld()
    {
        if (_epochFrom != 0 && _epoch != 0) _highWater[_epochFrom] = _epoch;
        _epoch = 0;
        _epochFrom = 0;
        _hostReady = false;
        _ready.Clear();
    }

    private Func<bool>? _built;
    internal bool FollowsWorld => _built != null;
    private bool _up, _ended;
    private ulong _minted, _prevEpoch;

    /// <summary>Raised when <see cref="FollowWorld"/> dropped the world (the host changed or migrated, a checkpoint was reloaded, the
    /// level ended): everything the mod holds for that world is void. Raised on the main thread, from the game event itself.</summary>
    public event Action? WorldReset;

    /// <summary>The channel follows the game's world so the mod does not: the world is up once <see cref="Net.WorldReady"/> and
    /// <paramref name="ready"/> (the mod's own "built" test, read every frame) hold, and it stays up until the host changes or
    /// migrates, a checkpoint is reloaded or the level ends. While it is up the channel is <see cref="Live"/> as long as
    /// <paramref name="ready"/> holds (the mod still sets <see cref="Ready"/>); the host mints a fresh <see cref="Epoch"/> when it
    /// starts (raising <see cref="EpochChanged"/>), one above every epoch it minted before, and a client adopts it through <see cref="EnableReadiness"/> (called here).
    /// Dropping the world is <see cref="ResetWorld"/>, a <see cref="Clear"/> of the queue and <see cref="WorldReset"/>; after a host
    /// change or checkpoint reload a host still in a ready world mints again at once, so state sent from the game event is stamped
    /// with the new epoch. Call it once, with the other registrations; a handler the mod subscribes to the same
    /// <see cref="GameEvents"/> afterwards runs after the channel's.</summary>
    public void FollowWorld(Func<bool> ready)
    {
        if (_built != null) throw new InvalidOperationException(ModId + " already follows the world.");
        _built = ready;
        EnableReadiness();
        GameEvents.MasterChanged += Restart;
        GameEvents.MasterMigrated += Restart;
        GameEvents.CheckpointReloaded += Restart;
        GameEvents.LevelEnded += EndWorld;
    }

    /// <summary>A host change, migration or checkpoint reload. A client's reload event can come after it already adopted the epoch
    /// the host minted for the same reload, so the client refuses only what is at or below the epoch before the one it holds, then asks the
    /// host for its current epoch (a host answers a readiness message of another epoch with its own); an epoch above that is taken, the
    /// held one included, and an older one stays refused.</summary>
    internal void Restart()
    {
        Drop(true);
        Settle();
        if (_up && !IsHost && Live) AskHost();
    }

    /// <summary>A level cleanup for a world this channel never started is the previous level's, not this one's: arming
    /// the end here would only refuse the world about to start while the game reports the level it is already in.</summary>
    private void EndWorld() { if (!_up) return; _ended = true; Drop(false); }

    private void Drop(bool soft)
    {
        if (!_up) return;
        var from = _epochFrom;
        _up = false;
        Live = false;
        ResetWorld();
        if (soft && from != 0) _highWater[from] = _prevEpoch;
        Clear();
        WorldReset?.Invoke();
    }

    /// <summary>A client that holds no epoch tells the host so; the host answers with its readiness.</summary>
    private void AskHost() => TryToHost(ReadyEvent, new NetReady { Epoch = 0, Ready = Ready ? 1 : 0 });

    /// <summary>Starts the world when the game and the mod are both ready; every frame from <see cref="Pump"/> and after a restart.</summary>
    private void Settle()
    {
        if (_built == null) return;
        if (_ended) { if (Net.WorldReady) return; _ended = false; }
        if (_up) { Live = _built(); return; }
        if (!Net.WorldReady || !_built()) return;
        _up = true;
        Live = true;
        if (!IsHost)
        {
            // The host's announcement may have arrived before this side was live; ask once it can answer. A host that does
            // not hold this player as compatible yet answers through PeerSynced instead.
            if (State(_transport.Host) == PeerState.Compatible) AskHost();
            return;
        }
        Epoch = _minted = Math.Max(_minted + 1, (ulong)DateTime.UtcNow.Ticks);
        EpochChanged?.Invoke();
    }

    private void OnReady(ulong sender, NetReady message)
    {
        // A player still joining or leaving, a client's message reaching another client, or a copy that is not this one is no handshake.
        if (!Live || sender == 0 || sender == _transport.Local || State(sender) != PeerState.Compatible || !IsMember(sender)) return;
        if (IsHost)
        {
            if (message.Epoch == _epoch) _ready[sender] = message.Ready != 0;
            else if (_epoch != 0) SendReady(sender);
            return;
        }
        if (sender != _transport.Host) return;
        var highest = _highWater.TryGetValue(sender, out var value) ? value : 0;
        // From a world this client already left, or from no world.
        if (message.Epoch == 0 || message.Epoch < highest || Epoch == 0 && message.Epoch == highest) return;
        if (_epoch != message.Epoch)
        {
            _prevEpoch = Math.Max(_epoch, highest);
            _epoch = message.Epoch;
            _epochFrom = sender;
            _highWater[sender] = _epoch;
            EpochChanged?.Invoke();
        }
        _hostReady = message.Ready != 0;
        SendReady(sender);
    }

    /// <summary>Drops every queued message (a level ended, the host changed).</summary>
    public void Clear()
    {
        lock (_gate) { _inbox.Clear(); _dropped = 0; _reliableDropped = 0; _lossyQueued = 0; }
    }

    /// <summary>Answers handshakes, greets players who have not answered yet and forgets players who left. ForgeCore runs this
    /// every frame with the session's <paramref name="host"/> and <paramref name="local"/> player, read once for every channel; it
    /// allocates nothing and takes no lock unless a player joins, answers or leaves.</summary>
    internal void Tick(double now, bool rosterChanged, ulong host, ulong local, ulong[] members, int memberCount)
    {
        if (host != _host || local != _local)
        {
            _host = host;
            _local = local;
            _peers.Clear();
            _ready.Clear();
            _hostReady = false;
            foreach (var value in _lobby) value.ForgetAll();
            foreach (var state in _states) state.ForgetOthers();
        }
        if (rosterChanged) { Array.Copy(members, _members, memberCount); _memberCount = memberCount; }
        DrainControl();
        if (_host == 0) return;
        if (_unlisted != 0) CheckUnlisted(now);
        if (rosterChanged || _greetAgain) Greet(now);
        if (_announce) { _announce = false; SendReady(0); }
        if (now >= _nextSilent) CheckSilent(now);
    }

    /// <summary>Handles the handshakes and format lists that arrived since the last frame. <see cref="Pump"/> runs it first too, so a
    /// readiness message queued behind a handshake is never judged before that handshake was.</summary>
    private void DrainControl()
    {
        while (_control != 0)
        {
            (ulong Sender, NetHello Hello) entry;
            lock (_gate)
            {
                if (_hellos.Count == 0) break;
                entry = _hellos.Dequeue();
                _control--;
            }
            Receive(entry.Sender, entry.Hello);
        }
        while (_control != 0)
        {
            (ulong Sender, byte[] Data) entry;
            lock (_gate)
            {
                if (_formats.Count == 0) break;
                entry = _formats.Dequeue();
                _control--;
            }
            ReceiveFormats(entry.Sender, entry.Data);
        }
    }

    /// <summary>The members changed (or the content did): forget players who left and greet every member not greeted yet.</summary>
    private void Greet(double now)
    {
        _greetAgain = false;
        foreach (var lookup in _peers.Keys)
            if (Array.IndexOf(_members, lookup, 0, _memberCount) < 0) _gone.Add(lookup);
        foreach (var lookup in _gone)
        {
            _peers.Remove(lookup);
            _ready.Remove(lookup);
            foreach (var value in _lobby) value.Forget(lookup);
            foreach (var state in _states) state.Forget(lookup);
        }
        _gone.Clear();
        for (var i = 0; i < _memberCount; i++)
        {
            var lookup = _members[i];
            if (!_peers.TryGetValue(lookup, out var peer)) _peers[lookup] = peer = new Peer();
            if (peer.Greeted) continue;
            peer.Greeted = true;
            peer.GreetedAt = now;
            _nextSilent = Math.Min(_nextSilent, now + AnswerSeconds);
            _transport.Send(HelloName(ModId), NetHello.Of(Fingerprint(), _content, false, _held), lookup, NetDelivery.Reliable);
        }
    }

    private void CheckSilent(double now)
    {
        _nextSilent = double.PositiveInfinity;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (!peer.Greeted || peer.State != PeerState.Unknown || peer.ReportedSilent || peer.Answered) continue;
            if (now - peer.GreetedAt < AnswerSeconds) { _nextSilent = Math.Min(_nextSilent, peer.GreetedAt + AnswerSeconds); continue; }
            if (peer.Retries < GreetingRetries)
            {
                peer.Retries++;
                peer.GreetedAt = now;
                _nextSilent = Math.Min(_nextSilent, now + AnswerSeconds);
                _transport.Send(HelloName(ModId), NetHello.Of(Fingerprint(), _content, false, _held), pair.Key, NetDelivery.Reliable);
                continue;
            }
            peer.ReportedSilent = true;
            _silent.Add(pair.Key);
        }
        foreach (var lookup in _silent) Raise(PeerSilent, lookup);
        _silent.Clear();
    }

    private void Raise(Action<ulong>? handlers, ulong player)
    {
        try { handlers?.Invoke(player); }
        catch (Exception error) { _log.ErrorOnce("core-peer-handler-failed", "A " + ModId + " peer handler failed: " + error); }
    }
    private void Receive(ulong sender, NetHello hello)
    {
        if (sender == 0 || sender == _transport.Local) return;
        // The game closes the connection of a player that sends to someone who does not list it, so a greeting from a player the
        // session does not list yet is not answered: once it lists them this side greets them and the answer follows.
        if (!IsMember(sender)) { NoteUnlisted(sender); return; }
        if (!_peers.TryGetValue(sender, out var peer)) _peers[sender] = peer = new Peer();
        // Any greeting or answer proves the mod is there, whatever it decides next: a side that holds its content stays Unknown but is not silent.
        peer.Answered = true;
        var formatMatches = hello.Format == Fingerprint();
        var contentMatches = hello.Content == Fnv.Hash64(_content);
        // Answer a greeting (refused or not) so the other side decides too; an answer is never answered, so they cannot loop.
        if (!hello.Answer) _transport.Send(HelloName(ModId), NetHello.Of(Fingerprint(), _content, true, _held), sender, NetDelivery.Reliable);
        // Either side's content is still unknown: neither is judged. The side that holds greets again when it knows.
        if (_held || hello.Held)
        {
            peer.State = PeerState.Unknown;
            // The sender restarted what it holds (like AskAgain here): the verdict we remembered is void, so the next judgement sends the format list again.
            if (hello.Held) { peer.RefusedFormat = peer.RefusedContent = 0; peer.Reported = false; peer.There = null; peer.ThereDetail = null; }
            return;
        }
        if (formatMatches && contentMatches)
        {
            // A greeting or a first answer means the sender holds no state of ours (it just joined, rebuilt its view of us, or the
            // content changed), and what we sent before may have been dropped because it did not list us as a member yet: send it
            // all again, however compatible we already were. Receivers ignore identical bytes, so a repeat changes nothing.
            var sync = peer.State != PeerState.Compatible || !hello.Answer;
            peer.State = PeerState.Compatible;
            if (sync)
            {
                foreach (var value in _lobby) SendLobby(value, sender);
                if (IsHost) SendReady(sender);
                Raise(PeerSynced, sender);
            }
        }
        else
        {
            peer.State = PeerState.Refused;
            _ready.Remove(sender);
            if (sender == _host) _hostReady = false;
            foreach (var value in _lobby) value.Forget(sender);
            foreach (var state in _states) state.Forget(sender);
            if (peer.RefusedFormat != hello.Format || peer.RefusedContent != hello.Content)
            {
                peer.RefusedFormat = hello.Format;
                peer.RefusedContent = hello.Content;
                peer.ContentDiffers = !contentMatches;
                peer.Reported = false;
                peer.There = null;
                peer.ThereDetail = null;
                if (formatMatches && ContentDetail == null) { peer.Reported = true; Refuse(sender, "same message formats but different content"); }
                else _transport.SendBytes(FormatsName(ModId), NetFormat.Write(_entries, ContentDetail), sender, NetDelivery.Reliable);
            }
        }
    }

    private void ReceiveFormats(ulong sender, byte[] data)
    {
        var there = NetFormat.Read(data, out var detail);
        if (there == null) { Net.Drop(Net.DropFormatsUnreadable); _log.ErrorOnce(Net.DropFormatsUnreadable, "Player " + sender + " sent an unreadable " + ModId + " message format list."); return; }
        if (!_peers.TryGetValue(sender, out var peer)) _peers[sender] = peer = new Peer();
        peer.There = there;
        peer.ThereDetail = detail;
        peer.Reported = false;
        ReportFormats(sender, peer);
    }

    /// <summary>Once a player is refused and its list is here (a list that came first waits for the refusal, and the refusal for the
    /// list), say what differs, once: the messages by name, and the other side's detail is readable from then on.</summary>
    private void ReportFormats(ulong sender, Peer peer)
    {
        if (peer.Reported || peer.State != PeerState.Refused || peer.There == null) return;
        peer.Reported = true;
        if (peer.RefusedFormat == Fingerprint()) { Refuse(sender, "same message formats but different content"); return; }
        var differences = NetFormat.Differences(_entries, peer.There);
        Refuse(sender, "message formats differ (" + (differences.Length == 0 ? "no per-message difference found" : differences) + ")" + (peer.ContentDiffers ? " and content differs" : ""));
    }

    private void Refuse(ulong sender, string reason)
    {
        _log.Error("core-player-refused-messages-from-that-player", "Player " + sender + " refused for " + ModId + ": " + reason + ". Messages from that player are dropped.");
        try { Refused?.Invoke(sender, reason); }
        catch (Exception error) { _log.Error("core-refusal-handler-failed", "A " + ModId + " refusal handler failed: " + error); }
    }

    private Route Claim(string name, NetDelivery delivery, string layout)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name the message.", nameof(name));
        if (_routes.ContainsKey(name)) throw new InvalidOperationException(ModId + " already handles message " + name + ".");
        var route = new Route { Event = EventName(name), Delivery = delivery };
        _routes.Add(name, route);
        _entries.Add(new NetFormat.Entry(name, delivery, Fnv.Hash64(layout)));
        // A message registered after players answered changes what they must share: ask again.
        _formatDirty = true;
        AskAgain();
        return route;
    }

    private Route RouteOf(string name)
        => _routes.TryGetValue(name, out var route) ? route
            : throw new InvalidOperationException(ModId + " sends message " + name + " without handling it: register it with On or OnBytes first, which also fixes how it travels.");

    /// <summary>Runs one arrived message now, on the thread and at the moment the game delivers it (an inline channel).</summary>
    private void Inline(Action work)
    {
        try { work(); }
        catch (Exception error) { _log.ErrorOnce("core-message-handler-failed", "A " + ModId + " message handler failed: " + error); }
    }

    private void Enqueue(ulong sender, Action work, NetDelivery delivery)
    {
        if (_inline)
        {
            if (State(sender) == PeerState.Refused) Net.Drop(Net.DropPeerRefused);
            else Inline(work);
            return;
        }
        var lossy = delivery == NetDelivery.Lossy;
        lock (_gate)
        {
            if (lossy)
            {
                if (_lossyQueued >= _capacity) { _dropped++; return; }
                _lossyQueued++;
            }
            else if (_inbox.Count - _lossyQueued >= ReliableLimit) { _reliableDropped++; Net.Drop(Net.DropReliableOverflow); return; }
            _inbox.Enqueue((sender, work, lossy));
        }
    }
}

/// <summary>One per-player value of a mod's channel (<see cref="NetChannel.Lobby"/>). Values are bytes; each player sets only their
/// own. Main thread only.</summary>
public sealed class NetLobbyValue
{
    private readonly NetChannel _channel;
    private readonly ModLog _log;
    private readonly Dictionary<ulong, (byte[] Value, int Version)> _values = new();
    private readonly List<ulong> _forgotten = new(4);
    private int _nextVersion;

    internal NetLobbyValue(NetChannel channel, string name, ModLog log)
    {
        _channel = channel;
        Name = name;
        _log = log;
    }

    public string Name { get; }

    /// <summary>This player's own value, or null when it has none.</summary>
    public byte[]? Local { get; private set; }

    /// <summary>Changes whenever <see cref="Local"/> changes; 0 while there is none.</summary>
    public int LocalVersion { get; private set; }

    /// <summary>Raised inside <see cref="NetChannel.Pump"/> (a value arrived) or the frame update (a player left or was refused) with
    /// the player whose value changed.</summary>
    public event Action<ulong>? Changed;

    /// <summary>Sets this player's value (null clears it) and sends it to every other player; the same bytes again send nothing.</summary>
    public void Set(byte[]? value)
    {
        if (value == null ? Local == null : Local != null && value.AsSpan().SequenceEqual(Local)) return;
        Local = value == null ? null : (byte[])value.Clone();
        LocalVersion = Local == null ? 0 : ++_nextVersion;
        _channel.SendLobby(this, 0);
    }

    /// <summary>Another player's value as last received.</summary>
    public bool TryGet(ulong player, out byte[] value)
    {
        if (_values.TryGetValue(player, out var entry)) { value = entry.Value; return true; }
        value = Array.Empty<byte>();
        return false;
    }

    /// <summary>Changes whenever <paramref name="player"/>'s value changes; 0 while there is none.</summary>
    public int VersionOf(ulong player) => _values.TryGetValue(player, out var entry) ? entry.Version : 0;

    /// <summary>On the wire: 1 and the value, or 0 for none.</summary>
    internal byte[] Framed()
    {
        if (Local == null) return new byte[] { 0 };
        var framed = new byte[Local.Length + 1];
        framed[0] = 1;
        Local.CopyTo(framed, 1);
        return framed;
    }

    internal void Receive(ulong sender, byte[] framed)
    {
        if (sender == 0) return;
        if (!_channel.IsMember(sender)) { _channel.NoteUnlisted(sender); return; }
        if (framed == null || framed.Length == 0 || framed[0] > 1 || framed[0] == 0 && framed.Length != 1)
        {
            Net.Drop(Net.DropLobbyMalformed);
            _log.ErrorOnce(Net.DropLobbyMalformed, "A malformed " + _channel.ModId + " lobby value '" + Name + "' from player " + sender + " was ignored.");
            return;
        }
        if (framed[0] == 0) { Forget(sender); return; }
        var value = framed.AsSpan(1);
        if (_values.TryGetValue(sender, out var old) && value.SequenceEqual(old.Value)) return;
        _values[sender] = (value.ToArray(), ++_nextVersion);
        Raise(sender);
    }

    internal void Forget(ulong player)
    {
        if (_values.Remove(player)) Raise(player);
    }

    internal void ForgetAll()
    {
        if (_values.Count == 0) return;
        _forgotten.AddRange(_values.Keys);
        _values.Clear();
        foreach (var player in _forgotten) Raise(player);
        _forgotten.Clear();
    }

    private void Raise(ulong player)
    {
        try { Changed?.Invoke(player); }
        catch (Exception error) { _log.ErrorOnce("core-lobby-value-handler-failed", "A " + _channel.ModId + " lobby value '" + Name + "' handler failed: " + error); }
    }
}

/// <summary>Messages and payload bytes sent since the process started (<see cref="Net.Traffic"/>): all of them, and the
/// <see cref="NetDelivery.Lossy"/> share.</summary>
public readonly record struct NetTraffic(long Messages, long Bytes, long LossyMessages, long LossyBytes);

/// <summary>Network channels, one per mod (see <see cref="NetChannel"/>). Built on GTFO-API's NetworkAPI.</summary>
public static class Net
{
    private static readonly Dictionary<string, NetChannel> Channels = new(StringComparer.Ordinal);
    private static NetChannel[] _all = Array.Empty<NetChannel>();
    private static readonly ulong[] Roster = new ulong[8];
    private static int _rosterCount;
    private static ulong _rosterHost, _rosterLocal;
    private static bool _sessionChanged = true;
    // Players the session's own add / remove call named, whatever its member list shows at that moment: added ones are listed (and
    // greeted) even if the list does not hold them yet, removed ones are not even if it still does. Each entry goes once the list agrees.
    private static readonly ulong[] Added = new ulong[8], Removed = new ulong[8];
    private static int _addedCount, _removedCount;

    /// <summary>Set by ForgeCore's plugin (GTFO-API); offline tests build channels on their own hub.</summary>
    internal static INetTransport? Transport { get; set; }

    /// <summary>The clock a <see cref="PlayerStateSlot{T}"/> counts on (the game's <c>Time.time</c>, set by ForgeCore's plugin).</summary>
    internal static Func<float> Clock { get; set; } = static () => 0f;

    /// <summary>The game's world test, set by ForgeCore's plugin; offline tests set their own.</summary>
    internal static Func<bool> WorldProbe { get; set; } = static () => false;

    /// <summary>The game is in a level with a host and no migration in progress: the moment a mod's world may start (see <see cref="NetChannel.FollowWorld"/>).</summary>
    public static bool WorldReady => WorldProbe();

    /// <summary>Opens <paramref name="modId"/>'s channel; its message formats are fingerprinted as the mod registers them. A mod
    /// opens one channel; opening a second is refused. <paramref name="capacity"/> is how many received <see cref="NetDelivery.Lossy"/> messages may wait for
    /// <see cref="NetChannel.Pump"/>; <see cref="NetDelivery.Reliable"/> messages are not limited by it.</summary>
    public static NetChannel Open(string modId, int capacity = 512) => Add(modId, capacity, false);

    /// <summary>Opens <paramref name="modId"/>'s channel with no receive queue: handlers and the handshake run as each message arrives, on
    /// the thread and at the moment the game delivers it, so they keep their order against the game's own messages on the same
    /// network channel. For a mod whose decisions depend on that order; such a handler must be safe inside the game's network
    /// callback, and <see cref="NetChannel.Pump"/> has nothing to run.</summary>
    public static NetChannel OpenInline(string modId) => Add(modId, 512, true);

    private static NetChannel Add(string modId, int capacity, bool inline)
    {
        var transport = Transport ?? throw new InvalidOperationException("ForgeCore's network is not ready (GTFO-API missing?).");
        lock (Channels)
        {
            if (Channels.ContainsKey(modId)) throw new InvalidOperationException(modId + " already opened its channel.");
            var channel = new NetChannel(transport, modId, capacity, inline);
            Channels.Add(modId, channel);
            var all = new NetChannel[_all.Length + 1];
            _all.CopyTo(all, 0);
            all[_all.Length] = channel;
            _all = all;
            _sessionChanged = true;
            return channel;
        }
    }

    private static long _sentMessages, _sentBytes, _lossyMessages, _lossyBytes;

    /// <summary>What every Forge package sent since the process started: each send the game accepted is one message (a send to every
    /// player too), at its payload size without GTFO-API's event header. A reader takes two readings and divides by the time between
    /// them for messages and bytes per second. Reading and counting allocate nothing.</summary>
    public static NetTraffic Traffic => new(Interlocked.Read(ref _sentMessages), Interlocked.Read(ref _sentBytes),
        Interlocked.Read(ref _lossyMessages), Interlocked.Read(ref _lossyBytes));

    /// <summary>The transport counts one message it handed to the game.</summary>
    internal static void CountSent(int bytes, NetDelivery delivery)
    {
        Interlocked.Increment(ref _sentMessages);
        Interlocked.Add(ref _sentBytes, bytes);
        if (delivery != NetDelivery.Lossy) return;
        Interlocked.Increment(ref _lossyMessages);
        Interlocked.Add(ref _lossyBytes, bytes);
    }

    private static readonly Dictionary<string, long> Drops = new(StringComparer.Ordinal);

    /// <summary>Counts one received message dropped as illegal (malformed, oversized, flooding, from a refused or wrong sender, from
    /// another world) under its refusal <paramref name="code"/>. Every Forge package counts its network drops here, so one place
    /// answers how many of each kind arrived; the log still reports a kind once.</summary>
    public static void Drop(string code)
    {
        lock (Drops) Drops[code] = Drops.TryGetValue(code, out var count) ? count + 1 : 1;
    }

    /// <summary>How many received messages were dropped under <paramref name="code"/> since the process started.</summary>
    public static long Dropped(string code)
    {
        lock (Drops) return Drops.TryGetValue(code, out var count) ? count : 0;
    }

    // ForgeCore's own refusal codes for dropped messages.
    internal const string DropOversized = "net-body-oversized", DropControlOverflow = "net-control-overflow",
        DropReliableOverflow = "net-reliable-overflow", DropFormatsUnreadable = "net-formats-unreadable",
        DropLobbyMalformed = "net-lobby-malformed", DropStateSize = "net-state-size", DropStateInvalid = "net-state-invalid", DropPeerRefused = "net-peer-refused",
        DropManifestMalformed = "net-manifest-malformed";

    /// <summary>The game said the session may have changed (a player joined or left, the host changed, the session was reset): the
    /// next <see cref="Tick"/> reads the host, the local player and the members once for every channel.</summary>
    internal static void NoteSession() => _sessionChanged = true;

    /// <summary>The session added another player (<c>SNet_SessionHub.AddPlayerToSession</c>' argument): listed and greeted at the next <see cref="Tick"/>.</summary>
    internal static void NotePlayerAdded(ulong player)
    {
        if (player == 0) return;
        Take(Removed, ref _removedCount, player);
        if (Array.IndexOf(Added, player, 0, _addedCount) < 0 && _addedCount < Added.Length) Added[_addedCount++] = player;
        _sessionChanged = true;
    }

    /// <summary>The session removed another player (<c>SNet_SessionHub.RemovePlayerFromSession</c>' argument): left out at the next <see cref="Tick"/>.</summary>
    internal static void NotePlayerRemoved(ulong player)
    {
        if (player == 0) return;
        Take(Added, ref _addedCount, player);
        if (Array.IndexOf(Removed, player, 0, _removedCount) < 0 && _removedCount < Removed.Length) Removed[_removedCount++] = player;
        _sessionChanged = true;
    }

    private static void Take(ulong[] list, ref int count, ulong player)
    {
        var at = Array.IndexOf(list, player, 0, count);
        if (at >= 0) list[at] = list[--count];
    }

    /// <summary>The session's member list with the named adds and removes applied; a name the list already agrees with is dropped.</summary>
    private static int Apply(int count)
    {
        for (var i = 0; i < _removedCount;)
        {
            var at = Array.IndexOf(Roster, Removed[i], 0, count);
            if (at < 0) { Removed[i] = Removed[--_removedCount]; continue; }
            Roster[at] = Roster[--count];
            i++;
        }
        for (var i = 0; i < _addedCount;)
        {
            if (Array.IndexOf(Roster, Added[i], 0, count) >= 0) { Added[i] = Added[--_addedCount]; continue; }
            if (count < Roster.Length) Roster[count++] = Added[i];
            i++;
        }
        return count;
    }

    /// <summary>Handshakes are answered every frame; the session is read only after <see cref="NoteSession"/>.</summary>
    internal static void Tick(double now)
    {
        var all = _all;
        if (all.Length == 0) return;
        var transport = Transport;
        if (transport == null) return;
        var changed = _sessionChanged;
        if (changed)
        {
            _sessionChanged = false;
            _rosterHost = transport.Host;
            _rosterLocal = transport.Local;
            if (_rosterHost == 0) { _rosterCount = 0; _addedCount = _removedCount = 0; }
            else _rosterCount = Apply(transport.Others(Roster));
        }
        foreach (var channel in all)
        {
            try { channel.Tick(now, changed, _rosterHost, _rosterLocal, Roster, _rosterCount); }
            catch (Exception error) { Log.For(channel.ModId).ErrorOnce("core-handshake-failed", "ForgeCore handshake of " + channel.ModId + " failed: " + error); }
        }
    }
}
