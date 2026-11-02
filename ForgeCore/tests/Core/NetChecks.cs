using System.Runtime.InteropServices;

namespace ForgeCore.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Ping { public uint Sequence; public float Value; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Wide { public uint Sequence; public float Value; public ulong Extra; }

/// <summary>An in-memory session: every endpoint is one player; a send is delivered to the receivers' registered handlers at
/// once, as GTFO-API calls them from its receive path.</summary>
internal sealed class Hub
{
    internal readonly List<Endpoint> Players = new();
    internal ulong HostLookup = 1;

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
                if (p != this && (to == 0 || p.Local == to) && p._handlers.TryGetValue(name, out var h)) ((Action<ulong, T>)h)(Local, message);
        }
        public void SendBytes(string name, byte[] data, ulong to, NetDelivery delivery)
        {
            Sent.Add(name);
            SentWith.Add((name, delivery));
            foreach (var p in _hub.Players)
                if (p != this && (to == 0 || p.Local == to) && p._handlers.TryGetValue(name, out var h)) ((Action<ulong, byte[]>)h)(Local, (byte[])data.Clone());
        }
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
        var strangerSide = new NetChannel(hub.Join(7), "test.mod", 16) { Content = "other" };
        var strangerPicks = strangerSide.Lobby("picks");
        strangerPicks.Set(new byte[] { 9 });
        Settle(50, hostSide, lateSide, strangerSide);
        check(!hostPicks.TryGet(7, out _) && !strangerPicks.TryGet(1, out _), "lobby: a refused player's value is dropped and ours is not sent to it");
        hub.HostLookup = 0;
        Settle(60, hostSide, lateSide);
        check(!hostPicks.TryGet(6, out _) && !latePicks.TryGet(1, out _), "lobby: leaving the session forgets every other player's value");

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

        // The same, but the game gives no further session event once the joiner lists the host: the hook on AddPlayerToSession
        // (simulated by one session read) is what makes it read its members.
        hub = new Hub();
        firstHost = new NetChannel(hub.Join(1), "test.mod", 16);
        firstHostPicks = firstHost.Lobby("picks");
        firstHostPicks.Set(new byte[] { 1, 2 });
        laggingEnd = hub.Join(8);
        laggingEnd.Unseen.Add(1);
        laggingSide = new NetChannel(laggingEnd, "test.mod", 16);
        laggingPicks = laggingSide.Lobby("picks");
        void EventsOnly(int from, params NetChannel[] channels)
        {
            for (var t = from; t < from + 3; t++) foreach (var c in channels) { c.Tick(t, false); c.Pump(); }
        }
        firstHost.Tick(0, true); laggingSide.Tick(0, true); // the first session read, as at start
        EventsOnly(1, firstHost, laggingSide);
        check(!laggingPicks.TryGet(1, out _), "lobby: with no session event, a joiner that lists the host late first drops its value");
        errors.Clear();
        laggingSide.Tick(10, false);
        check(errors.Any(e => e.Contains("never listed")), "lobby: a sender the session still does not list after the wait is reported once");
        laggingEnd.Unseen.Clear();
        laggingSide.Tick(11, true); // the AddPlayerToSession hook: the only session event
        EventsOnly(12, firstHost, laggingSide);
        check(laggingPicks.TryGet(1, out got) && got.SequenceEqual(new byte[] { 1, 2 }), "lobby: the joiner reads its members when the session lists the host (AddPlayerToSession, no other session event) and receives the host's value");

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

        // A player without the mod never answers: greeted once, reported once after the answer window, PeerSilent raised once.
        hub = new Hub();
        var lonely = new NetChannel(hub.Join(1), "test.mod", 16);
        var silentEnd = hub.Join(5);
        var silent = new List<ulong>();
        lonely.PeerSilent += silent.Add;
        errors.Clear();
        lonely.Tick(0, true);
        lonely.Tick(NetChannel.AnswerSeconds - 1, false);
        check(errors.Count == 0 && silent.Count == 0, "net: a greeted player gets the answer window before it counts as silent");
        for (var t = 0; t < 30; t++) lonely.Tick(NetChannel.AnswerSeconds + t, t == 0);
        check(silentEnd.Sent.Count == 0 && hub.Players[0].Sent.Count == 1 && errors.Count == 0 && silent.SequenceEqual(new ulong[] { 5 }),
            "net: an unanswered player is greeted once, then reported once through PeerSilent and not logged");        hub.Players.Remove(silentEnd);
        lonely.Tick(31, true);
        check(lonely.State(5) == PeerState.Unknown, "net: a player who left is forgotten");
    }
}
