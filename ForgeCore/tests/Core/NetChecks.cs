using System.Runtime.InteropServices;

namespace ForgeCore.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Ping { public uint Sequence; public float Value; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Wide { public uint Sequence; public float Value; public ulong Extra; }
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct SwapA { [FieldOffset(0)] public int A; [FieldOffset(4)] public int B; }
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct SwapB { [FieldOffset(4)] public int A; [FieldOffset(0)] public int B; }

/// <summary>An in-memory session: every endpoint is one player; a send is delivered to the receivers' registered handlers at
/// once, as GTFO-API calls them from its receive path.</summary>
internal sealed class Hub
{
    internal readonly List<Endpoint> Players = new();
    internal ulong HostLookup = 1;
    /// <summary>Messages on their way, in order, when a check models the wire: delivery then happens at <see cref="Flush"/>, never inside the send.</summary>
    internal Queue<Action>? Wire;
    internal void Flush() { while (Wire is { Count: > 0 }) Wire.Dequeue()(); }
    private void Carry(Action delivery) { if (Wire != null) Wire.Enqueue(delivery); else delivery(); }

    internal Endpoint Join(ulong lookup) { var e = new Endpoint(this, lookup); Players.Add(e); return e; }

    internal sealed class Endpoint : INetTransport
    {
        private readonly Hub _hub;
        private readonly Dictionary<string, Delegate> _handlers = new();
        internal Endpoint(Hub hub, ulong lookup) { _hub = hub; Local = lookup; }
        internal readonly List<string> Sent = new();
        internal readonly List<(string Name, NetDelivery Delivery)> SentWith = new();
        public ulong Local { get; }
        public ulong Host => _hub.HostLookup;
        public void Register<T>(string name, Action<ulong, T> receive) where T : unmanaged => _handlers.Add(name, receive);
        public void RegisterBytes(string name, Action<ulong, byte[]> receive) => _handlers.Add(name, receive);
        public void Send<T>(string name, T message, ulong to, NetDelivery delivery) where T : unmanaged
        {
            Sent.Add(name);
            SentWith.Add((name, delivery));
            foreach (var p in _hub.Players)
                if (p != this && (to == 0 || p.Local == to) && p._handlers.TryGetValue(name, out var h)) _hub.Carry(() => ((Action<ulong, T>)h)(Local, message));
        }
        public void SendBytes(string name, byte[] data, ulong to, NetDelivery delivery)
        {
            Sent.Add(name);
            SentWith.Add((name, delivery));
            foreach (var p in _hub.Players)
                if (p != this && (to == 0 || p.Local == to) && p._handlers.TryGetValue(name, out var h)) _hub.Carry(() => ((Action<ulong, byte[]>)h)(Local, (byte[])data.Clone()));
        }
        public bool IsHuman(ulong lookup) => _hub.Players.Exists(p => p.Local == lookup);
        /// <summary>Players this endpoint's session list does not show yet (a joiner's member list lags behind the host's).</summary>
        internal readonly HashSet<ulong> Unseen = new();
        public int Others(ulong[] into)
        {
            var n = 0;
            foreach (var p in _hub.Players) if (p != this && !Unseen.Contains(p.Local)) into[n++] = p.Local;
            return n;
        }
    }
}

/// <summary>The game ticks every channel with the session read once (<c>Net.Tick</c>); a test ticks one channel with its own endpoint's.</summary>
internal static class ChannelTicks
{
    private static readonly System.Reflection.FieldInfo TransportField = typeof(NetChannel).GetField("_transport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    internal static void Tick(this NetChannel channel, double now, bool scan)
    {
        var transport = (INetTransport)TransportField.GetValue(channel)!;
        var members = new ulong[8];
        channel.Tick(now, scan, transport.Host, transport.Local, members, transport.Host == 0 ? 0 : transport.Others(members));
    }
}

internal static class NetChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        // Round trip: a client asks the host, the host answers everyone; handlers run only inside Pump.
        var hub = new Hub();
        var host = new NetChannel(hub.Join(1), "test.mod", 4);
        var client = new NetChannel(hub.Join(2), "test.mod", 4);
        var atHost = new List<(ulong, Ping)>(); var atClient = new List<(ulong, Ping)>();
        host.On<Ping>("ping", (s, m) => { atHost.Add((s, m)); host.Broadcast("pong", new Ping { Sequence = m.Sequence, Value = m.Value * 2 }); });
        client.On<Ping>("pong", (s, m) => atClient.Add((s, m)));
        client.On<Ping>("ping", (_, _) => { }); host.On<Ping>("pong", (_, _) => { });
        for (var t = 0; t < 3; t++) { host.Tick(t, true); client.Tick(t, true); }
        check(host.State(2) == PeerState.Compatible && client.State(1) == PeerState.Compatible, "net: same message formats and content are compatible on both sides");
        client.ToHost("ping", new Ping { Sequence = 7, Value = 1.5f });
        check(atHost.Count == 0, "net: received messages wait for the mod's own pump");
        host.Pump(); client.Pump();
        check(atHost.Count == 1 && atHost[0].Item1 == 2 && atHost[0].Item2.Sequence == 7, "net: the host receives the client's message with its sender");
        check(atClient.Count == 1 && atClient[0].Item1 == 1 && atClient[0].Item2.Value == 3f, "net: the host's broadcast reaches the client");
        check(host.EventName("ping") == "test.mod/ping", "net: message events carry the mod ID");
        var threw = false;
        try { host.On<Ping>("ping", (_, _) => { }); } catch (InvalidOperationException) { threw = true; }
        check(threw, "net: the same message name cannot be handled twice");

        // Player state: a value and how long ago it began; a later answerer gets it with the seconds counted to then, a cleared one does not.
        var stateNow = 100f;
        var hostState = host.PlayerState<Ping>("doing", () => stateNow);
        var clientNow = 40f;
        var clientState = client.PlayerState<Ping>("doing", () => clientNow);
        for (var t = 3; t < 6; t++) { host.Tick(t, true); client.Tick(t, true); }
        hostState.Publish(new Ping { Sequence = 9 }, 95f);
        client.Pump();
        check(clientState.TryGet(1, out var heldState, out var began) && heldState.Sequence == 9 && Math.Abs(began - 35f) < 0.001f, "player state: the value arrives with its beginning on the receiver's clock");
        host.Epoch = 5; client.Epoch = 6;
        hostState.Publish(new Ping { Sequence = 10 }, 95f);
        client.Pump();
        check(clientState.TryGet(1, out heldState, out _) && heldState.Sequence == 9, "player state: a value from another world is dropped");
        host.Epoch = 0; client.Epoch = 0;
        clientState.ForgetAll();
        stateNow = 110f;
        hostState.Resend(2);
        client.Pump();
        check(clientState.TryGet(1, out heldState, out began) && heldState.Sequence == 10 && Math.Abs(began - 25f) < 0.001f, "player state: a repeat counts the seconds to then");
        hostState.Clear();
        client.Pump();
        check(!clientState.TryGet(1, out _, out _), "player state: clearing takes it from the others");
        clientState.ForgetAll();
        hostState.Resend(2);
        client.Pump();
        check(!clientState.TryGet(1, out _, out _), "player state: a cleared value is not sent again");
        check(ForgeCore.OrbitMath.Pulled(0.5f, 2.4f, 0.2f) == 0.5f && ForgeCore.OrbitMath.Pulled(50f, 2.4f, 0.2f) == 2.4f, "orbit: the arm stops where the sweep hit and never grows past its length");
        check(ForgeCore.OrbitMath.Blend(0f, true, 0.15f, 0.3f) == 0.5f && ForgeCore.OrbitMath.Blend(0.1f, false, 1f, 0.3f) == 0f, "orbit: the view moves out and back over the blend time");

        // Delivery: a message travels the way its registration says; the queue limit binds only lossy messages. Overflowing lossy
        // traffic is counted and reported at the next pump, never silent, and never pushes a reliable message out.
        var shotsAtHost = new List<uint>(); var vitalAtHost = new List<uint>();
        host.On<Ping>("shot", (_, m) => shotsAtHost.Add(m.Sequence), NetDelivery.Lossy);
        client.On<Ping>("shot", (_, _) => { }, NetDelivery.Lossy);
        host.On<Ping>("vital", (_, m) => vitalAtHost.Add(m.Sequence));
        client.On<Ping>("vital", (_, _) => { });
        var clientEnd = hub.Players[1];
        clientEnd.SentWith.Clear();
        client.ToHost("shot", new Ping()); client.ToHost("vital", new Ping());
        check(clientEnd.SentWith[0] == ("test.mod/shot", NetDelivery.Lossy) && clientEnd.SentWith[1] == ("test.mod/vital", NetDelivery.Reliable),
            "net: a lossy message is sent on the lossy channel, a reliable one on the reliable channel");
        host.Pump(); shotsAtHost.Clear(); vitalAtHost.Clear();
        errors.Clear();
        for (var i = 0; i < 6; i++) client.ToHost("shot", new Ping { Sequence = (uint)i });
        host.Pump();
        check(shotsAtHost.Count == 4 && errors.Any(e => e.Contains("overflowed") && e.Contains("2 messages")), "net: a full lossy queue drops the excess and says how many");
        shotsAtHost.Clear(); errors.Clear();
        for (var i = 0; i < 4; i++) client.ToHost("shot", new Ping { Sequence = (uint)i });
        host.Pump();
        check(shotsAtHost.Count == 4 && errors.Count == 0, "net: a drained queue takes lossy messages again");
        shotsAtHost.Clear(); errors.Clear();
        for (var i = 0; i < 20; i++)
        {
            client.ToHost("shot", new Ping { Sequence = (uint)i });
            client.ToHost("vital", new Ping { Sequence = (uint)i });
        }
        host.Pump();
        check(vitalAtHost.Count == 20 && vitalAtHost.SequenceEqual(Enumerable.Range(0, 20).Select(i => (uint)i)) && shotsAtHost.Count == 4 && errors.Count(e => e.Contains("overflowed") && e.Contains("16 messages")) == 1,
            "net: over the limit, reliable messages (checkpoints, state replays) are all delivered in order and only lossy ones are dropped");
        for (var i = 0; i < 6; i++) client.ToHost("shot", new Ping());
        host.Clear();
        shotsAtHost.Clear(); errors.Clear();
        for (var i = 0; i < 4; i++) client.ToHost("shot", new Ping());
        host.Pump();
        check(shotsAtHost.Count == 4, "net: clearing the queue also resets the lossy count");
        threw = false;
        try { client.Broadcast("never-registered", new Ping()); } catch (InvalidOperationException) { threw = true; }
        check(threw, "net: sending a message that was never registered is an error");
        // Message formats are fingerprinted from each channel's registrations (name, delivery, field layout): a player whose
        // registrations differ in any of those is refused on both sides, with the differing message named; identical ones pass.
        var changedRefusals = new List<string>();
        (NetChannel Host, NetChannel Other) Pair(Action<NetChannel> hostSide, Action<NetChannel> otherSide)
        {
            var pairHub = new Hub();
            var a = new NetChannel(pairHub.Join(1), "test.mod", 16);
            var b = new NetChannel(pairHub.Join(3), "test.mod", 16);
            a.Refused += (p, r) => changedRefusals.Add("a:" + p + ":" + r);
            b.Refused += (p, r) => changedRefusals.Add("b:" + p + ":" + r);
            hostSide(a); otherSide(b);
            errors.Clear(); changedRefusals.Clear();
            for (var t = 0; t < 3; t++) { a.Tick(t, true); b.Tick(t, true); }
            return (a, b);
        }
        void Base(NetChannel c) { c.On<Ping>("ping", (_, _) => { }); c.On<Ping>("shot", (_, _) => { }, NetDelivery.Lossy); c.On<Ping>("text", (_, _) => { }); }

        var same = Pair(Base, Base);
        check(same.Host.State(3) == PeerState.Compatible && same.Other.State(1) == PeerState.Compatible && changedRefusals.Count == 0 && same.Host.Format == same.Other.Format,
            "net: identical message formats pass the handshake");

        var layout = Pair(Base, c => { c.On<Wide>("ping", (_, _) => { }); c.On<Ping>("shot", (_, _) => { }, NetDelivery.Lossy); c.On<Ping>("text", (_, _) => { }); });
        check(layout.Host.State(3) == PeerState.Refused && layout.Other.State(1) == PeerState.Refused && layout.Host.Format != layout.Other.Format
            && changedRefusals.Count == 2 && changedRefusals.All(r => r.Contains("'ping'") && r.Contains("different field layout") && !r.Contains("'shot'") && !r.Contains("'text'")),
            "net: a changed field layout is refused on both sides and names the message");

        var delivery = Pair(Base, c => { c.On<Ping>("ping", (_, _) => { }); c.On<Ping>("shot", (_, _) => { }); c.On<Ping>("text", (_, _) => { }); });
        check(delivery.Host.State(3) == PeerState.Refused && delivery.Other.State(1) == PeerState.Refused
            && changedRefusals.Count == 2 && changedRefusals.All(r => r.Contains("'shot'") && r.Contains("travels") && !r.Contains("'ping'")),
            "net: a changed delivery is refused on both sides and names the message");

        var extra = Pair(Base, c => { Base(c); c.On<Ping>("bonus", (_, _) => { }); });
        check(extra.Host.State(3) == PeerState.Refused && extra.Other.State(1) == PeerState.Refused
            && changedRefusals.Any(r => r.StartsWith("a:") && r.Contains("'bonus' exists only there")) && changedRefusals.Any(r => r.StartsWith("b:") && r.Contains("'bonus' is missing there")),
            "net: an added message is refused on both sides and names the message");

        var removed = Pair(Base, c => { c.On<Ping>("ping", (_, _) => { }); c.On<Ping>("text", (_, _) => { }); });
        check(removed.Host.State(3) == PeerState.Refused && removed.Other.State(1) == PeerState.Refused
            && changedRefusals.Any(r => r.StartsWith("a:") && r.Contains("'shot' is missing there")) && changedRefusals.Any(r => r.StartsWith("b:") && r.Contains("'shot' exists only there")),
            "net: a removed message is refused on both sides and names the message");
        check(errors.Count(e => e.Contains("refused") && e.Contains("'shot'")) == 2, "net: each side logs the refusal once, naming the message");

        // A refused player's messages are dropped at the pump and never reach the handler.
        hub = new Hub();
        var hostGot = 0;
        host = new NetChannel(hub.Join(1), "test.mod", 16);
        var oldSide = new NetChannel(hub.Join(3), "test.mod", 16);
        host.On<Ping>("ping", (_, _) => hostGot++);
        oldSide.On<Ping>("ping", (_, _) => { });
        oldSide.On<Ping>("extra", (_, _) => { });
        for (var t = 0; t < 3; t++) { host.Tick(t, true); oldSide.Tick(t, true); }
        oldSide.ToHost("ping", new Ping());
        host.Pump();
        check(hostGot == 0, "net: a refused player's message never reaches the handler");

        // Same message formats, different content (for example other definitions): refused, and its messages are dropped at the pump.
        hub = new Hub();
        host = new NetChannel(hub.Join(1), "test.mod", 16) { Content = "a" };
        var other = new NetChannel(hub.Join(4), "test.mod", 16) { Content = "b" };
        hostGot = 0;
        host.On<Ping>("ping", (_, _) => hostGot++);
        other.On<Ping>("ping", (_, _) => { });
        errors.Clear();
        for (var t = 0; t < 3; t++) { host.Tick(t, true); other.Tick(t, true); }
        other.ToHost("ping", new Ping());
        host.Pump();
        check(host.State(4) == PeerState.Refused && hostGot == 0 && errors.Any(e => e.Contains("different content")),
            "net: different content is refused and its messages are dropped");
        other.Content = "a";
        for (var t = 3; t < 6; t++) { other.Tick(t, true); host.Tick(t, true); }
        check(host.State(4) == PeerState.Compatible && other.State(1) == PeerState.Compatible, "net: matching content again makes both sides compatible");

        // Lobby values: each player's own value reaches the others, a later joiner gets every value when its copy answers, a
        // leaver's value is forgotten, a cleared value is cleared everywhere, a refused player gets and keeps nothing.
        hub = new Hub();
        var hostSide = new NetChannel(hub.Join(1), "test.mod", 16);
        var clientSide = new NetChannel(hub.Join(2), "test.mod", 16);
        var hostPicks = hostSide.Lobby("picks");
        var clientPicks = clientSide.Lobby("picks");
        var changedAtHost = new List<ulong>();
        hostPicks.Changed += changedAtHost.Add;
        hostPicks.Set(new byte[] { 1, 2 });
        clientPicks.Set(new byte[] { 3 });
        void Settle(int from, params NetChannel[] channels)
        {
            for (var t = from; t < from + 3; t++) foreach (var c in channels) { c.Tick(t, true); c.Pump(); }
        }
        Settle(0, hostSide, clientSide);
        check(hostPicks.TryGet(2, out var got) && got.SequenceEqual(new byte[] { 3 }) && clientPicks.TryGet(1, out got) && got.SequenceEqual(new byte[] { 1, 2 }),
            "lobby: each player's value reaches the other");
        check(changedAtHost.Contains(2) && hostPicks.VersionOf(2) != 0 && hostPicks.LocalVersion != 0, "lobby: a received value raises Changed and has a version");
        var version = hostPicks.VersionOf(2);
        clientPicks.Set(new byte[] { 3 });
        hostSide.Pump();
        check(hostPicks.VersionOf(2) == version, "lobby: setting the same bytes again changes nothing");
        var lateSide = new NetChannel(hub.Join(6), "test.mod", 16);
        var latePicks = lateSide.Lobby("picks");
        Settle(10, hostSide, clientSide, lateSide);
        check(latePicks.TryGet(1, out got) && got.SequenceEqual(new byte[] { 1, 2 }) && latePicks.TryGet(2, out got) && got.SequenceEqual(new byte[] { 3 }),
            "lobby: a player who joins later receives every value once its copy answers");
        clientPicks.Set(null);
        Settle(20, hostSide, lateSide);
        check(!hostPicks.TryGet(2, out _) && !latePicks.TryGet(2, out _) && hostPicks.VersionOf(2) == 0, "lobby: a cleared value is cleared on every other player");
        clientPicks.Set(new byte[] { 4 });
        Settle(30, hostSide, lateSide);
        hub.Players.RemoveAll(p => p.Local == 2);
        changedAtHost.Clear();
        Settle(40, hostSide, lateSide);
        check(!hostPicks.TryGet(2, out _) && !latePicks.TryGet(2, out _) && changedAtHost.Contains(2), "lobby: a player who left is forgotten, with a Changed");
        var oversized = Net.Dropped(Net.DropOversized);
        latePicks.Set(new byte[NetChannel.MaxLobbyBytes + 2]);
        Settle(45, hostSide, lateSide);
        check(!hostPicks.TryGet(6, out _) && errors.Any(e => e.Contains("larger than ForgeCore accepts")), "lobby: a value over the size cap is dropped at the receiver and reported");
        check(Net.Dropped(Net.DropOversized) == oversized + 1, "network: an illegal message is counted once under its refusal code where it is dropped");
        latePicks.Set(null);
        var strangerSide = new NetChannel(hub.Join(7), "test.mod", 16) { Content = "other" };
        var strangerPicks = strangerSide.Lobby("picks");
        strangerPicks.Set(new byte[] { 9 });
        Settle(50, hostSide, lateSide, strangerSide);
        check(!hostPicks.TryGet(7, out _) && !strangerPicks.TryGet(1, out _), "lobby: a refused player's value is dropped and ours is not sent to it");
        hub.HostLookup = 0;
        Settle(60, hostSide, lateSide);
        check(!hostPicks.TryGet(6, out _) && !latePicks.TryGet(1, out _), "lobby: leaving the session forgets every other player's value");
        check(NetFormat.LayoutOf<SwapA>() != NetFormat.LayoutOf<SwapB>(), "network: two layouts with the same fields and size but other field offsets differ in their fingerprint");

        // Content lists: matched entry by entry, never refusing the channel. Shared with the same hash is usable; an entry one side
        // lacks is named on both sides; a different hash is refused for that entry only; a malformed list is dropped and counted.
        hub = new Hub();
        var listHost = new NetChannel(hub.Join(1), "test.mod", 16);
        var listClient = new NetChannel(hub.Join(2), "test.mod", 16);
        var hostList = listHost.Manifest("packs");
        var clientList = listClient.Manifest("packs");
        check(hostList.Match(2, 10) == ContentMatch.Unknown, "content list: nothing is usable before the other player's list arrives");
        hostList.Set(new (uint, ulong)[] { (10, 100), (11, 110), (12, 120) });
        clientList.Set(new (uint, ulong)[] { (10, 100), (12, 999), (13, 130) });
        Settle(0, listHost, listClient);
        var hostDiff = hostList.Differences(2);
        var clientDiff = clientList.Differences(1);
        check(listHost.State(2) == PeerState.Compatible && hostList.Usable(2, 10) && clientList.Usable(1, 10),
            "content list: different lists keep the channel compatible and the shared entry is usable on both sides");
        check(hostDiff != null && hostDiff.OnlyHere.SequenceEqual(new uint[] { 11 }) && hostDiff.OnlyThere.SequenceEqual(new uint[] { 13 }) && hostDiff.Mismatched.SequenceEqual(new uint[] { 12 })
            && clientDiff != null && clientDiff.OnlyHere.SequenceEqual(new uint[] { 13 }) && clientDiff.OnlyThere.SequenceEqual(new uint[] { 11 }),
            "content list: each side names what the other lacks, what it lacks itself and what differs");
        check(hostList.Match(2, 11) == ContentMatch.OnlyHere && hostList.Match(2, 13) == ContentMatch.OnlyThere && !hostList.Usable(2, 12) && clientList.Match(1, 12) == ContentMatch.Mismatched,
            "content list: an entry one side lacks is not usable, and a different hash is refused for that entry");
        var malformed = Net.Dropped(Net.DropManifestMalformed);
        errors.Clear();
        hub.Players[1].SendBytes(listClient.EventName(NetChannel.LobbyPrefix + "manifest/packs"), new byte[] { 1, 1, 2, 3 }, 1, NetDelivery.Reliable);
        listHost.Pump();
        check(!hostList.Known(2) && Net.Dropped(Net.DropManifestMalformed) == malformed + 1 && errors.Any(e => e.Contains("malformed")),
            "content list: a malformed list is dropped, counted and reported, and nothing of it is usable");

        // A joiner whose session list does not show the host yet drops the host's value on arrival and prunes its view of the host;
        // once the host is listed it greets again and must be sent the value again (the host is already compatible with it).
        hub = new Hub();
        var firstHost = new NetChannel(hub.Join(1), "test.mod", 16);
        var firstHostPicks = firstHost.Lobby("picks");
        firstHostPicks.Set(new byte[] { 1, 2 });
        var laggingEnd = hub.Join(8);
        laggingEnd.Unseen.Add(1);
        var laggingSide = new NetChannel(laggingEnd, "test.mod", 16);
        var laggingPicks = laggingSide.Lobby("picks");
        Settle(0, firstHost, laggingSide);
        check(!laggingPicks.TryGet(1, out _), "lobby: a joiner that does not list the host yet drops its value");
        laggingEnd.Unseen.Clear();
        Settle(10, firstHost, laggingSide);
        check(laggingPicks.TryGet(1, out got) && got.SequenceEqual(new byte[] { 1, 2 }), "lobby: the host sends its value again when the joiner greets once it lists the host");

        // The session's add call names the player although the member list does not hold them yet (Net.NotePlayerAdded): the joiner
        // lists and greets the host from the call's argument, and the host's value arrives; the remove call takes the host out again
        // although the list never held it.
        hub = new Hub();
        var hintHost = new NetChannel(hub.Join(1), "test.hint", 16);
        hintHost.Lobby("picks").Set(new byte[] { 5 });
        var hintEnd = hub.Join(9);
        hintEnd.Unseen.Add(1);
        Net.Transport = hintEnd;
        var hintChannel = Net.Open("test.hint", 16);
        var hintPicks = hintChannel.Lobby("picks");
        void NetSettle(int from)
        {
            for (var t = from; t < from + 3; t++) { Net.Tick(t); hintHost.Tick(t, false); hintHost.Pump(); hintChannel.Pump(); }
        }
        hintHost.Tick(0, true);
        NetSettle(0);
        check(!hintPicks.TryGet(1, out _), "lobby: a joiner whose session list is empty greets no one");
        Net.NotePlayerAdded(1);
        NetSettle(10);
        check(hintPicks.TryGet(1, out got) && got.SequenceEqual(new byte[] { 5 }) && hintChannel.State(1) == PeerState.Compatible,
            "lobby: the player named by the session's add call is listed and greeted although the member list does not hold them, and their value arrives");
        Net.NotePlayerRemoved(1);
        NetSettle(20);
        check(!hintPicks.TryGet(1, out _), "lobby: the player named by the session's remove call is dropped with their value");

        // A player without the mod never answers: greeted again each answer window, then reported once, PeerSilent raised once.
        hub = new Hub();
        var lonely = new NetChannel(hub.Join(1), "test.mod", 16);
        var silentEnd = hub.Join(5);
        var silent = new List<ulong>();
        lonely.PeerSilent += silent.Add;
        errors.Clear();
        lonely.Tick(0, true);
        var silentAfter = NetChannel.AnswerSeconds * (NetChannel.GreetingRetries + 1);
        for (var t = 1.0; t < silentAfter; t++) lonely.Tick(t, false);
        check(errors.Count == 0 && silent.Count == 0, "net: a greeted player gets every answer window before it counts as silent");
        for (var t = silentAfter; t < silentAfter + 30; t++) lonely.Tick(t, false);
        check(silentEnd.Sent.Count == 0 && hub.Players[0].Sent.Count == NetChannel.GreetingRetries + 1 && errors.Count == 0 && silent.SequenceEqual(new ulong[] { 5 }),
            "net: an unanswered player is greeted each window, then reported once through PeerSilent and not logged"); hub.Players.Remove(silentEnd);
        lonely.Tick(silentAfter + 31, true);
        check(lonely.State(5) == PeerState.Unknown, "net: a player who left is forgotten");

        // An inline channel: a handler runs at the moment of delivery, in arrival order, with no pump; a refused player's message is still dropped.
        var inlineHub = new Hub { Wire = new Queue<Action>() };
        var inlineA = new NetChannel(inlineHub.Join(1), "test.inline", 512, true);
        var inlineB = new NetChannel(inlineHub.Join(2), "test.inline", 512, true);
        var order = new List<byte>();
        inlineA.OnBytes("step", (_, bytes) => order.Add(bytes[0]));
        inlineB.OnBytes("step", (_, _) => { });
        var inlineTime = 0.0;
        void TickInline(Hub wire, NetChannel first, NetChannel second) { for (var t = 0; t < 3; t++) { first.Tick(inlineTime, true); second.Tick(inlineTime, true); wire.Flush(); inlineTime += 1; } }
        TickInline(inlineHub, inlineA, inlineB);
        inlineB.SendBytes("step", new byte[] { 1 }, 1);
        inlineB.SendBytes("step", new byte[] { 2 }, 1);
        check(order.Count == 0, "net: nothing runs before it is delivered");
        inlineHub.Flush();
        check(order.SequenceEqual(new byte[] { 1, 2 }), "net: an inline channel runs each message at delivery, in arrival order, with no pump");

        // Holding: while a side does not know its content nobody is judged; once it says, the difference is refused with each side's detail, which outlasts the refusal.
        var refusedHere = new List<(ulong Player, byte[]? Detail, bool Format)>();
        inlineA.Refused += (player, _) => refusedHere.Add((player, inlineA.ContentDetailOf(player), inlineA.FormatDiffers(player)));
        inlineA.Content = "same";
        inlineB.HoldContent();
        TickInline(inlineHub, inlineA, inlineB);
        check(refusedHere.Count == 0 && inlineA.State(2) == PeerState.Unknown && inlineB.State(1) == PeerState.Unknown, "net: a side that holds its content judges nobody and is judged by nobody");
        inlineB.ContentDetail = new byte[] { 9 };
        inlineA.ContentDetail = new byte[] { 8 };
        inlineB.Content = "other";
        TickInline(inlineHub, inlineA, inlineB);
        check(inlineA.State(2) == PeerState.Refused && refusedHere.Count == 1 && refusedHere[0].Detail is { Length: 1 } arrived && arrived[0] == 9 && !refusedHere[0].Format,
            "net: a content refusal waits for and carries the other side's detail, and is not a format difference");
        var droppedBefore = order.Count;
        inlineB.SendBytes("step", new byte[] { 3 }, 1);
        inlineHub.Flush();
        check(order.Count == droppedBefore, "net: an inline channel drops a refused player's message");
        var synced = new List<ulong>();
        inlineA.PeerSynced += synced.Add;
        inlineB.Content = "same";
        TickInline(inlineHub, inlineA, inlineB);
        check(inlineA.State(2) == PeerState.Compatible && synced.SequenceEqual(new ulong[] { 2 }), "net: the same content afterwards is compatible and reported once");

        // A format difference outranks content, for the message explaining it too.
        var formatHub = new Hub { Wire = new Queue<Action>() };
        var formatA = new NetChannel(formatHub.Join(1), "test.inline", 512, true) { Content = "a", ContentDetail = new byte[] { 1 } };
        var formatB = new NetChannel(formatHub.Join(2), "test.inline", 512, true) { Content = "b", ContentDetail = new byte[] { 2 } };
        formatA.OnBytes("extra", (_, _) => { });
        var formatRefusals = new List<bool>();
        formatA.Refused += (player, _) => formatRefusals.Add(formatA.FormatDiffers(player));
        TickInline(formatHub, formatA, formatB);
        check(formatRefusals.SequenceEqual(new[] { true }), "net: different message formats are told apart from different content");
    }
}
