using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using ForgeUI;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Everything the mod knows about the current level: slots, handshake state, accepted calls and the checkpoint image.
/// The host decides (<see cref="OnLanding"/>); every peer, the host included, mirrors the result. Network handlers all run on
/// the main thread through <see cref="OrdnanceNetwork.Pump"/>.</summary>
internal sealed class OrdnanceSession
{
    private readonly SlotBook _book = new();
    private readonly SummonScheduler _scheduler = new();
    private readonly List<PendingSummon> _due = new();
    private bool _deliveringDue;
    private IReadOnlyList<OrdnanceDefinition> _localLoadout = Array.Empty<OrdnanceDefinition>();
    private DefinitionSet? _loadoutDefinitions;
    private ulong _loadoutVersion, _loadoutPlayer;
    /// <summary>Host: players whose level handshake completed this epoch (their copy is compatible per ForgeCore).</summary>
    private readonly HashSet<ulong> _admitted = new();
    private readonly ThrowHistory _seenThrows = new();
    private ulong _greeting = (ulong)DateTime.UtcNow.Ticks;
    private readonly HelloAdmission _hellos = new();
    internal uint ThrowFloor { get; private set; }
    /// <summary>Host: the next call number of a developer drop (thrower 0). Seeded from the clock each time this player becomes the
    /// host, so a new host's drops never take the number of a drop still in the level.</summary>
    private uint _developerCalls = 1;
    private readonly HashSet<(ulong, uint)> _accepted = new();
    private SlotSnapshot? _checkpoint;
    private List<SlotSnapshot.Entry>? _incomingCheckpoint;
    private readonly List<(ulong Owner, ThrownBeacon Ball)> _hostBeacons = new();
    // Each end simulates the thrown ball itself, so the host's copy can roll several metres away from the thrower's.
    private const float ThrowTolerance = 8f;
    private const float BeaconLifetime = 30f;

    internal OrdnanceSession()
    {
        BeaconItem.Spawned += ObserveBeacon;
        LoadoutStore.Changed += _submission.Changed;
        OrdnanceNetwork.Refused += Mismatch;
    }

    private void ObserveBeacon(BeaconSpawn spawn)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        var agent = ForgeCore.Level.PlayerByLookup(spawn.Owner);
        if (agent == null || agent.WasCollected) return;
        var origin = agent.transform.position;
        _hostBeacons.Add((spawn.Owner, new ThrownBeacon(0, 0, new Vec3(origin.x, origin.y, origin.z), spawn.Item, spawn.Replicator)));
    }
    private bool _inLevel, _hostHelloSeen;
    private ulong _master;
    private readonly LoadoutSubmission _submission = new();
    /// <summary>True while the local player is typing, holding or has a thrown ball not yet landed (set by the controller owner).</summary>
    internal Func<bool> CallInProgress { get; set; } = () => false;

    /// <summary>The last 16 arrivals this peer was told of (the driver's readback, <see cref="OrdnanceControl.Arrivals"/>).</summary>
    internal List<string> Arrivals { get; } = new();

    internal event Action<string>? Notice;
    internal event Action<RejectMessage>? ThrowRejected;
    internal event Action<AcceptedMessage, OrdnanceDefinition>? BeaconStarted;
    internal event Action<ArrivedMessage, OrdnanceDefinition>? BeaconArrived;
    internal event Action<ulong, uint>? BeaconFailed;
    /// <summary>Level entry, checkpoint recovery or a host change: every beacon and ball in flight is void. The argument is true for a host change, which
    /// keeps the arrivals still holding something to claim (<see cref="OrdnanceClaims.HostChanged"/>) instead of voiding them.</summary>
    internal event Action<bool>? PresentationReset;

    internal SlotBook Book => _book;
    internal static ulong Local => SNet.LocalPlayer?.Lookup ?? 0UL;
    private static double Now => Time.time;
    internal bool InLevel => _inLevel;

    internal bool IsPeerCompatible(ulong player)
        => _inLevel && (ForgeCore.Session.IsHost
            ? player == Local || _admitted.Contains(player)
            : _hostHelloSeen && (player == Local || player == _master) && OrdnanceNetwork.Channel.FromHost(_master));

    /// <summary>The host's rules for this level turned the players' call-ins off: no call list, call key or cooldowns. Developer drops still arrive.</summary>
    internal bool CallMenuOff => LobbyRules.TryCurrent(out var rules, out _) && !rules.CallMenu;

    /// <summary>Call-ins work for this peer: it is the host, or the host has answered (only a compatible copy's answer arrives).</summary>
    internal bool Enabled => _inLevel && BeaconItem.Ready && (ForgeCore.Session.IsHost || _hostHelloSeen);
    /// <summary>Why call-ins are off, as a language key; null while they work.</summary>
    internal string? DisabledReason => !_inLevel ? "disabled.notInLevel"
        : !BeaconItem.Ready ? "disabled.ball"
        : ForgeCore.Session.IsHost || _hostHelloSeen ? null
        : OrdnanceNetwork.State(_master) == ForgeCore.PeerState.Refused ? "disabled.filesDiffer" : "disabled.waitingHost";

    internal IReadOnlyList<OrdnanceDefinition> LocalLoadout()
    {
        var definitions = OrdnanceContent.Set;
        var player = Local;
        if (ReferenceEquals(_loadoutDefinitions, definitions) && _loadoutVersion == _book.LoadoutVersion && _loadoutPlayer == player)
            return _localLoadout;
        var list = new List<OrdnanceDefinition>();
        foreach (var key in _book.LoadoutOf(player))
            if (definitions.TryGet(key, out var definition)) list.Add(definition);
        _loadoutDefinitions = definitions;
        _loadoutVersion = _book.LoadoutVersion;
        _loadoutPlayer = player;
        return _localLoadout = list;
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------

    internal void EnterLevel()
    {
        Reset();
        _inLevel = true;
        _master = SNet.Master?.Lookup ?? 0UL;
        _greeting++;
        RestartHandshakeWatch();
        Announce();
        if (!ForgeCore.Session.IsHost) return;
        SeedDeveloperCalls();
        ApplyAuthoredLoadout(Local);
        ScheduleLevelStartDrops();
    }

    private void SeedDeveloperCalls() => _developerCalls = DeveloperDrops.SeedCalls((ulong)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));

    internal void LeaveLevel() { Reset(); _inLevel = false; }

    private void Reset()
    {
        _submission.Reset();
        _book.Clear(); _scheduler.Clear(); _admitted.Clear(); _seenThrows.Clear();
        _checkpoint = null; _incomingCheckpoint = null; _hostBeacons.Clear(); _hostHelloSeen = false;
        OrdnanceApi.Retained.Clear();
        OrdnanceNetwork.Reset();
        _accepted.Clear(); _hellos.Clear(); _startedDrops.Clear();
        PresentationReset?.Invoke(false);
    }

    private HelloMessage MyHello(ulong greeting = 0, ulong peer = 0) => new() { Greeting = greeting, ThrowFloor = _seenThrows.Floor(peer) };

    /// <summary>Starts (or repeats) the handshake: the host announces itself, a client greets the host.</summary>
    internal void Announce()
    {
        if (!_inLevel) return;
        if (ForgeCore.Session.IsHost)
        {
            OrdnanceNetwork.Broadcast(Messages.HelloEvent, MyHello());
            TrySubmitLoadout();
        }
        else if (SNet.Master != null)
        {
            OrdnanceNetwork.ToHost(Messages.HelloEvent, MyHello(_greeting));
        }
    }

    /// <summary>ForgeCore matched another player's copy of Ordnance and it holds none of our state (it joined, or rebuilt its view of
    /// us): the host greets it so it greets back, a client whose host has not answered greets the host. This replaces a retry timer;
    /// a host that is not in the level yet greets everyone itself when it enters.</summary>
    internal void PeerSynced(ulong peer)
    {
        if (!_inLevel) return;
        if (ForgeCore.Session.IsHost)
        {
            if (OrdnanceNetwork.Channel.IsPlayer(peer) && peer != Local) OrdnanceNetwork.To(Messages.HelloEvent, MyHello(), peer);
        }
        else if (peer == _master && !_hostHelloSeen) Announce();
    }

    /// <summary>The world's epoch changed (the host minted it, or this client adopted the host's): the handshake runs again for the new epoch, so a
    /// greeting that found no epoch is answered once there is one and an answer given under an earlier epoch no longer counts.</summary>
    internal void EpochChanged()
    {
        if (!_inLevel) return;
        if (!ForgeCore.Session.IsHost) { _hostHelloSeen = false; _greeting++; }
        Announce();
    }

    /// <summary>The host's player never answered ForgeCore's greeting: Ordnance (or ForgeCore) is not installed there.</summary>
    internal void PeerSilent(ulong peer)
    {
        if (!_inLevel || ForgeCore.Session.IsHost || peer != _master || _hostHelloSeen) return;
        Plugin.Logger.ErrorOnce("Call-in handshake: the host did not answer; call-ins stay off for this level.");
        Notice?.Invoke(OrdnanceText.T("notice.hostSilent"));
    }
    /// <summary>ForgeCore's host-changed event: re-handshakes with the new host.</summary>
    internal void HostChanged() => TakeOverHost(false);

    /// <summary>A migration can end with the same master lookup, which ForgeCore does not report; the migration hook takes over anyway.</summary>
    internal void MigrationCompleted() => TakeOverHost(true);

    private void TakeOverHost(bool migration)
    {
        if (!_inLevel) return;
        var master = SNet.Master?.Lookup ?? 0UL;
        if (master == _master && !migration) return;
        _master = master;
        _submission.Reset();
        OrdnanceNetwork.Reset();
        _greeting++;
        RestartHandshakeWatch();
        _loadoutDefinitions = null;
        _scheduler.Clear();
        _hostBeacons.Clear(); _incomingCheckpoint = null;
        _admitted.Clear(); _seenThrows.Clear(); _hostHelloSeen = false;
        OrdnanceApi.Retained.Clear();
        _accepted.Clear(); _hellos.Clear();
        PresentationReset?.Invoke(true);
        // The world did not roll back: a level-start drop that already arrived is still there and is not dropped again; one still waiting is scheduled again.
        if (ForgeCore.Session.IsHost) { SeedDeveloperCalls(); ApplyAuthoredLoadout(Local); ScheduleLevelStartDrops(skipArrived: true); GearPayload.TakeOver(); }
        Announce();
    }

    internal void CheckpointReached()
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        _checkpoint = _book.Capture(Now);
        foreach (var message in CheckpointState()) OrdnanceNetwork.Broadcast(Messages.CheckpointEvent, message);
    }

    internal void CheckpointReloaded()
    {
        if (!_inLevel) return;
        OrdnanceNetwork.Reset();
        _admitted.Clear();
        RestartHandshakeWatch();
        if (!ForgeCore.Session.IsHost) { _hostHelloSeen = false; _greeting++; }
        _loadoutDefinitions = null;
        _scheduler.Clear();
        _hostBeacons.Clear(); _incomingCheckpoint = null;
        _book.Restore(_checkpoint ?? new SlotSnapshot(Array.Empty<SlotSnapshot.Entry>()), Now);
        OrdnanceApi.Retained.Clear();
        _accepted.Clear(); _hellos.Clear(); _startedDrops.Clear();
        PresentationReset?.Invoke(false);
        if (ForgeCore.Session.IsHost) { ApplyAuthoredLoadout(Local); foreach (var player in _book.Players.ToList()) BroadcastPlayer(player); ScheduleLevelStartDrops(true); }
        else Announce();
    }

    /// <summary>Host, every frame: delivers the calls whose time has come.</summary>
    internal void Tick()
    {
        TrySubmitLoadout();
        WatchHandshake();
        if (!_inLevel || !ForgeCore.Session.IsHost || _deliveringDue) return;
        if (_hostBeacons.Count != 0) _hostBeacons.RemoveAll(b => b.Ball.Gone || b.Ball.Age > BeaconLifetime);
        _scheduler.TakeDue(Now, _due);
        if (_due.Count == 0) return;
        _deliveringDue = true;
        try { foreach (var due in _due) Arrive(due); }
        finally { _due.Clear(); _deliveringDue = false; }
    }

    // ---- handshake -----------------------------------------------------------------------------------------

    /// <summary>How long a level handshake may take before the stall is written to the log once.</summary>
    private const double HandshakeStallSeconds = 15;
    private double _nextHandshakeCheck;
    private readonly HashSet<ulong> _stallReported = new();

    /// <summary>A new greeting round starts the stall clock again (level entry, host change, checkpoint reload).</summary>
    private void RestartHandshakeWatch()
    {
        _nextHandshakeCheck = Now + HandshakeStallSeconds;
        _stallReported.Clear();
    }

    /// <summary>Once a second after the stall time: a player whose level handshake has not completed is written to the log once,
    /// with ForgeCore's verdict on that player's copy, so a call-in list stuck on "waiting for the host" or a host that never sees
    /// a player's call-ins says why. Players are not refused here; the handshake keeps answering if it completes later.</summary>
    private void WatchHandshake()
    {
        if (!_inLevel || Now < _nextHandshakeCheck) return;
        _nextHandshakeCheck = Now + 1;
        if (!ForgeCore.Session.IsHost)
        {
            // While the session is torn down at the end of a level the game can report no master for a moment with this player still
            // named host; nobody waits on itself, so that is not a stall.
            if (_hostHelloSeen || _master == 0 || _master == Local || !_stallReported.Add(_master)) return;
            Plugin.Logger.ErrorOnce("Call-in handshake with the host (" + _master + ") is not complete after " + HandshakeStallSeconds
                + " s: ForgeCore channel " + OrdnanceNetwork.State(_master) + ", greeting " + _greeting + ", host epoch "
                + OrdnanceNetwork.Epoch + ". Call-ins stay off on this side, and the host's level messages are dropped, until the host answers.");
            return;
        }
        var players = SNet.SessionHub?.PlayersInSession;
        if (players == null) return;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || player.IsBot || player.IsLocal || _admitted.Contains(player.Lookup) || !_stallReported.Add(player.Lookup)) continue;
            Plugin.Logger.ErrorOnce("Call-in handshake with player " + player.Lookup + " is not complete after " + HandshakeStallSeconds
                + " s: ForgeCore channel " + OrdnanceNetwork.State(player.Lookup) + ". That player's loadout and calls are refused until it completes.");
        }
    }

    internal void OnHello(ulong sender, HelloMessage message)
    {
        if (!_inLevel) return;
        // A refused copy's messages never get here; one ForgeCore has not answered for yet waits for the next greeting.
        if (OrdnanceNetwork.State(sender) != ForgeCore.PeerState.Compatible) return;
        if (ForgeCore.Session.IsHost)
        {
            if (!OrdnanceNetwork.Channel.IsPlayer(sender) || sender == Local) return;
            var decision = _hellos.Evaluate(sender, message.Greeting, message.Epoch, OrdnanceNetwork.Epoch);
            if (decision == HelloDecision.Ignore) return;
            if (decision == HelloDecision.Challenge)
            {
                OrdnanceNetwork.To(Messages.HelloEvent, MyHello(message.Greeting, sender), sender);
                return;
            }
            _admitted.Add(sender);
            ApplyAuthoredLoadout(sender);
            SyncTo(sender);
            return;
        }
        if (!OrdnanceNetwork.Channel.FromHost(sender)) return;
        // Recovery requires the answer to our new greeting, not an old queued hello.
        if (message.Greeting == 0) { if (!_hostHelloSeen) Announce(); return; }
        // The epoch is the host's current one, adopted through ForgeCore; an answer from another epoch, or before it arrived, waits for the next one.
        if (_hostHelloSeen || message.Greeting != _greeting || message.Epoch == 0 || message.Epoch != OrdnanceNetwork.Epoch) return;
        ThrowFloor = message.ThrowFloor;
        _hostHelloSeen = true;
        var proof = MyHello(_greeting);
        proof.Epoch = OrdnanceNetwork.Epoch;
        OrdnanceNetwork.ToHost(Messages.HelloEvent, proof);
        TrySubmitLoadout();
    }

    /// <summary>ForgeCore refused a player's copy (other message formats or other call-in files; ForgeCore logged the details). The host is
    /// told which player; a client whose host differs is told its call-ins stay off.</summary>
    private void Mismatch(ulong player)
    {
        string text;
        if (ForgeCore.Session.IsHost) text = OrdnanceText.T("notice.peerMismatch", SNet.TryGetPlayer(player, out var other) && other != null ? other.NickName : player.ToString());
        else if (SNet.Master != null && SNet.Master.Lookup == player) text = OrdnanceText.T("notice.selfMismatch");
        else return;
        UIKit.PostChatNotice(text);
        Notice?.Invoke(text);
    }

    // ---- loadout and slots ---------------------------------------------------------------------------------

    private void TrySubmitLoadout()
    {
        if (!_inLevel || !_submission.Due(ForgeCore.Session.IsHost || _hostHelloSeen, CallInProgress())) return;
        if (!LobbyRules.TrySubmissionKeys(out var keys)) return;
        if (ForgeCore.Session.IsHost) HostAcceptLoadout(Local, keys);
        else
        {
            OrdnanceNetwork.ToHost(Messages.LoadoutEvent, LoadoutMessage.From(Local, keys));
        }
        _submission.Sent();
    }

    internal void OnLoadoutSubmitted(ulong sender, LoadoutMessage message)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost || !OrdnanceNetwork.Channel.IsPlayer(sender)) return;
        if (!_admitted.Contains(sender))
        {
            RejectTo(sender, 0, 0, Rejection.DefinitionMismatch);
            return;
        }
        if (!OrdnanceContent.Rules.LobbyPicker)
        {
            Plugin.Logger.ErrorOnce("Loadout from " + NameOf(sender) + " ignored: this level has LobbyPicker off; the level's Loadouts decide.");
            return;
        }
        var keys = message.ToKeys();
        if (keys == null)
        {
            Plugin.Logger.ErrorOnce("Loadout from " + sender + " claims " + message.Count + " entries; the limit is " + Messages.MaxLoadout + ". Refused.");
            RejectTo(sender, 0, 0, Rejection.LoadoutInvalid);
            return;
        }
        HostAcceptLoadout(sender, keys);
    }

    private void HostAcceptLoadout(ulong player, IReadOnlyList<uint> keys)
    {
        var rules = OrdnanceContent.Rules;
        var result = LoadoutValidator.Validate(keys, OrdnanceContent.Set, rules);
        if (!result.Ok)
        {
            Plugin.Logger.ErrorOnce("Loadout of " + NameOf(player) + " refused (" + result.Reason + "): " + result.Detail);
            RejectTo(player, 0, 0, result.Reason);
            return;
        }
        _book.SetLoadout(player, result.Accepted, rules.UsesMultiplier);
        BroadcastPlayer(player);
    }

    /// <summary>Host: with the lobby choice off, gives <paramref name="player"/> the loadout the level's author wrote for their slot in the party
    /// (their own list, else <c>All</c>, else none), through the same acceptance a lobby pick gets: validation, the slot book (uses and cooldown
    /// kept when it is given again), the broadcast, and the resend to a player who joins later (<see cref="SyncTo"/>). With the lobby choice on, the author's
    /// <c>Loadouts</c> are not used.</summary>
    private void ApplyAuthoredLoadout(ulong player)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost || OrdnanceContent.Rules.LobbyPicker) return;
        var slot = SNet.TryGetPlayer(player, out var who) && who != null ? who.PlayerSlotIndex() : -1;
        var keys = new List<uint>();
        foreach (var id in AuthoredLoadouts.For(OrdnanceContent.Rules.Loadouts, slot) ?? Array.Empty<string>())
            if (OrdnanceContent.Set.TryGetById(id, out var definition)) keys.Add(definition.Key);
        HostAcceptLoadout(player, keys);
    }

    internal void OnLoadoutState(ulong sender, LoadoutMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        var keys = message.ToKeys();
        if (keys == null) { Plugin.Logger.ErrorOnce("Loadout state from the host claims " + message.Count + " entries; ignored."); return; }
        var known = keys.Where(k => OrdnanceContent.Set.TryGet(k, out _)).ToList();
        if (known.Count != keys.Length) Plugin.Logger.ErrorOnce("The host's loadout for " + message.Player + " names call-ins this install does not have; those are left out.");
        _book.ApplyLoadout(message.Player, known);
    }

    internal void OnSlot(ulong sender, SlotMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        _book.Apply(message.Player, message.Key, message.UsesLeft, message.InitialUses, message.CooldownRemaining, Now);
    }

    internal void OnCheckpoint(ulong sender, CheckpointMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        switch (message.Kind)
        {
            case CheckpointMessage.Begin:
                _incomingCheckpoint = new List<SlotSnapshot.Entry>();
                break;
            case CheckpointMessage.Entry:
                if (_incomingCheckpoint == null) return;
                if (!double.IsFinite(message.CooldownRemaining) || message.CooldownRemaining < 0)
                {
                    _incomingCheckpoint = null;
                    Plugin.Logger.ErrorOnce("Malformed checkpoint cooldown from the host; recovery image ignored.");
                    return;
                }
                _incomingCheckpoint.Add(new SlotSnapshot.Entry(message.Player, message.Key, message.CooldownRemaining, message.UsesLeft));
                break;
            case CheckpointMessage.Commit:
                if (_incomingCheckpoint == null) return;
                _checkpoint = new SlotSnapshot(_incomingCheckpoint);
                _incomingCheckpoint = null;
                break;
        }
    }

    internal void OnSentryExpiry(ulong sender, SentryExpiryMessage message)
    {
        if (FromCompatibleMaster(sender)) GearPayload.ApplyExpiry(message);
    }

    private IEnumerable<CheckpointMessage> CheckpointState()
    {
        yield return new CheckpointMessage { Kind = CheckpointMessage.Begin };
        foreach (var entry in _book.RecoverySnapshot(_checkpoint).Entries)
            yield return new CheckpointMessage
            {
                Kind = CheckpointMessage.Entry,
                Player = entry.Player,
                Key = entry.Key,
                UsesLeft = entry.Uses,
                CooldownRemaining = entry.CooldownRemaining
            };
        yield return new CheckpointMessage { Kind = CheckpointMessage.Commit };
    }

    private void BroadcastPlayer(ulong player)
    {
        OrdnanceNetwork.Broadcast(Messages.LoadoutStateEvent, LoadoutMessage.From(player, _book.LoadoutOf(player)));
        foreach (var key in _book.LoadoutOf(player)) BroadcastSlot(player, key);
    }

    internal void RefundCall(ulong player, uint key, double cooldownSeconds)
    {
        _book.Refund(player, key, Now, cooldownSeconds);
        BroadcastSlot(player, key);
    }

    internal void PostNotice(string text) => Notice?.Invoke(text);

    private void BroadcastSlot(ulong player, uint key)
    {
        if (_book.TryGet(player, key, Now, out var uses, out var cooldown))
            OrdnanceNetwork.Broadcast(Messages.SlotEvent, new SlotMessage { Player = player, Key = key, UsesLeft = uses, CooldownRemaining = (float)cooldown, InitialUses = _book.InitialOf(player, key) });
    }

    /// <summary>Everything the host knows, to one peer that just completed the handshake.</summary>
    private void SyncTo(ulong peer)
    {
        foreach (var player in _book.Players.ToList())
        {
            OrdnanceNetwork.To(Messages.LoadoutStateEvent, LoadoutMessage.From(player, _book.LoadoutOf(player)), peer);
        }
        // Retained slots outside the current loadout must survive host migration as well.
        foreach (var entry in _book.Capture(Now).Entries)
            OrdnanceNetwork.To(Messages.SlotEvent, new SlotMessage { Player = entry.Player, Key = entry.Key, UsesLeft = entry.Uses, CooldownRemaining = (float)entry.CooldownRemaining, InitialUses = _book.InitialOf(entry.Player, entry.Key) }, peer);
        foreach (var message in CheckpointState()) OrdnanceNetwork.To(Messages.CheckpointEvent, message, peer);
        foreach (var message in GearPayload.ExpiryState()) OrdnanceNetwork.To(Messages.SentryExpiryEvent, message, peer);
        foreach (var pending in _scheduler.Beacons())
            OrdnanceNetwork.To(Messages.AcceptedEvent, new AcceptedMessage
            {
                Thrower = pending.Thrower,
                ThrowId = pending.ThrowId,
                Key = pending.Key,
                Landing = new Vec3(pending.X, pending.Y, pending.Z),
                Delay = (float)Math.Max(0, pending.ArriveAt - Now)
            }, peer);
        var arrivals = Deliveries.ReplayState();
        foreach (var arrival in arrivals) OrdnanceNetwork.To(Messages.DeliveryReplayEvent, arrival, peer);
        foreach (var state in OrdnanceApi.Retained.InOrder()) OrdnanceNetwork.To(Messages.PayloadReplayEvent, Refreshed(state), peer);
    }

    /// <summary>A kept state as it is replayed now: the payload type may rebuild its time-relative fields from its current record.</summary>
    private static PayloadDataMessage Refreshed(PayloadDataMessage state)
    {
        if (!OrdnanceApi.TryGetPayload(state.Type, out var payload) || state.ToBytes() is not { } kept) return state;
        try { return PayloadDataMessage.From(state.Thrower, state.ThrowId, state.Type, state.Tag, payload.RefreshState(state.Thrower, state.ThrowId, state.Tag, kept)); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Payload state refresh failed; the kept state is replayed as it was: " + error.Message); return state; }
    }

    // ---- throw, landing, decision --------------------------------------------------------------------------

    internal void ReportLanding(LandingMessage message)
    {
        if (ForgeCore.Session.IsHost) OnLanding(Local, message);
        else OrdnanceNetwork.ToHost(Messages.LandingEvent, message);
    }

    internal void OnLanding(ulong sender, LandingMessage message)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        if (sender != Local && !OrdnanceNetwork.Channel.IsPlayer(sender)) return;
        var compatible = sender == Local || _admitted.Contains(sender);
        if (!compatible) { RejectTo(sender, message.ThrowId, message.Key, Rejection.DefinitionMismatch); return; }
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition))
        {
            RejectTo(sender, message.ThrowId, message.Key, Rejection.UnknownDefinition);
            return;
        }
        if (!_seenThrows.Add(sender, message.ThrowId)) return;
        var name = NameOf(sender);
        var rules = OrdnanceContent.Rules;
        var snapped = default(Vector3);
        var ball = _hostBeacons.Where(b => b.Owner == sender && !b.Ball.Gone && b.Ball.Age <= BeaconLifetime)
            .Select(b => b.Ball).FirstOrDefault(b => message.Replicator != 0 && b.Replicator == message.Replicator && message.Landing.IsFinite &&
                Vector3.Distance(b.Position, ToVector(message.Landing)) <= ThrowTolerance);
        var origin = ball?.Origin ?? new Vec3(float.NaN, float.NaN, float.NaN);
        var point = ball?.Position ?? default;
        var landing = new Vec3(point.x, point.y, point.z);
        var reason = ThrowValidator.Evaluate(_book, compatible, rules.CallMenu, sender, message.Key, origin, landing, Now, rules.MaxThrowDistance,
            () => TryResolve(landing, out snapped, out _));
        if (reason != Rejection.None)
        {
            RejectTo(sender, message.ThrowId, message.Key, reason);
            return;
        }
        _hostBeacons.RemoveAll(b => ReferenceEquals(b.Ball, ball));
        _book.Consume(sender, message.Key, Now, definition.Cooldown * rules.CooldownMultiplier);
        BroadcastSlot(sender, message.Key);
        _scheduler.Add(new PendingSummon(sender, message.ThrowId, message.Key, snapped.x, snapped.y, snapped.z, Now + definition.CallInDelay));
        var accepted = new AcceptedMessage
        {
            Thrower = sender,
            ThrowId = message.ThrowId,
            Key = message.Key,
            Landing = new Vec3(snapped.x, snapped.y, snapped.z),
            Delay = (float)definition.CallInDelay
        };
        OrdnanceNetwork.Broadcast(Messages.AcceptedEvent, accepted);
        ApplyAccepted(accepted);
    }

    private void RejectTo(ulong player, uint throwId, uint key, Rejection reason)
    {
        var message = new RejectMessage { ThrowId = throwId, Key = key, Reason = (byte)reason };
        if (player == Local) ApplyReject(message);
        else if (OrdnanceNetwork.Channel.IsPlayer(player)) OrdnanceNetwork.To(Messages.RejectEvent, message, player);
    }

    internal void OnReject(ulong sender, RejectMessage message)
    {
        if (FromCompatibleMaster(sender)) ApplyReject(message);
    }

    private void ApplyReject(RejectMessage message)
    {
        var reason = (Rejection)message.Reason;
        Notice?.Invoke(OrdnanceText.T("notice.refused", OrdnanceText.Reason(reason)));
        if (message.ThrowId != 0) ThrowRejected?.Invoke(message);
    }

    internal void OnAccepted(ulong sender, AcceptedMessage message)
    {
        if (FromCompatibleMaster(sender)) ApplyAccepted(message);
    }

    private void ApplyAccepted(AcceptedMessage message)
    {
        if (!message.Landing.IsFinite || !float.IsFinite(message.Delay) || message.Delay < 0f) { Plugin.Logger.ErrorOnce("Malformed accepted call from the host."); return; }
        if (!_accepted.Add((message.Thrower, message.ThrowId))) return;
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition)) { Plugin.Logger.ErrorOnce("Accepted call names unknown definition key " + message.Key.ToString("x8") + "."); return; }
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
        {
            try { payload.OnBeaconLanded(new OrdnancePresentation(definition, ToVector(message.Landing), Quaternion.identity, message.Thrower, message.ThrowId, message.Delay, OrdnanceArrivalMode.Drop)); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Payload '" + definition.PayloadType + "' OnBeaconLanded failed: " + error); }
        }
        BeaconStarted?.Invoke(message, definition);
    }

    /// <summary>Host: the one delivery entry. Every call that arrives comes through here when its time is due, a player's throw and a
    /// level's developer drop alike: the host runs the payload and every peer (itself included) is told it arrived, with or without
    /// its vehicle.</summary>
    private void Arrive(PendingSummon summon)
    {
        var landing = new Vec3(summon.X, summon.Y, summon.Z);
        var rotation = Quaternion.identity;
        OrdnanceContent.Set.TryGet(summon.Key, out var definition);
        if (definition == null) { Plugin.Logger.ErrorOnce("A due call names unknown definition key " + summon.Key.ToString("x8") + "."); return; }
        var what = summon.Developer ? "Developer drop '" + definition.Id + "' (call " + summon.ThrowId + ")" : "Call '" + definition.Id + "' (throw " + summon.ThrowId + ")";
        try
        {
            if (!OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
                throw new InvalidOperationException("Payload type '" + definition.PayloadType + "' is not registered.");
            Vector3 position;
            AIG_CourseNode? node;
            PlayerAgent? summoner = null;
            if (summon.Developer)
            {
                position = ToVector(landing);
                if (!DeveloperNode(position, summon.Dimension, out node, out var problem)) throw new InvalidOperationException(problem);
                rotation = Quaternion.Euler(0f, summon.Yaw, 0f);
            }
            else
            {
                if (!TryResolve(landing, out position, out node) || node == null)
                    throw new InvalidOperationException("The landing point no longer resolves to a course node.");
                summoner = ForgeCore.Level.PlayerByLookup(summon.Thrower);
                if (summoner != null)
                {
                    var flat = position - summoner.transform.position;
                    flat.y = 0f;
                    if (flat.sqrMagnitude > 0.01f) rotation = Quaternion.LookRotation(flat.normalized);
                }
            }
            payload.Execute(new OrdnanceExecution(definition, position, rotation, node!, summoner, summon.Thrower, summon.ThrowId, summon.Arrival, summon.Overrides));
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce(what + (summon.Developer ? "" : " from " + NameOf(summon.Thrower)) + " failed on arrival" + (summon.Developer ? "" : " (the use stays spent)") + ": " + error);
            Deliveries.ReportFailure(summon.Thrower, summon.ThrowId);
            return;
        }
        var arrived = new ArrivedMessage { Thrower = summon.Thrower, ThrowId = summon.ThrowId, Key = summon.Key, Landing = landing, Yaw = rotation.eulerAngles.y, Arrival = (byte)summon.Arrival };
        OrdnanceNetwork.Broadcast(Messages.ArrivedEvent, arrived);
        ApplyArrived(arrived);
    }

    // ---- developer drops -----------------------------------------------------------------------------------

    /// <summary>Every peer: the call numbers of the level-start drops that have arrived (or been replayed to this peer), so a new host does not drop them again.</summary>
    private readonly HashSet<uint> _startedDrops = new();

    private void NoteStarted(ulong thrower, uint throwId)
    {
        if (thrower == 0 && DeveloperDrops.IsLevelStartCall(throwId)) _startedDrops.Add(throwId);
    }

    /// <summary>Host: schedules the level's <c>LevelStart</c> drops: when the level is entered after their delay, after a checkpoint reload
    /// (<paramref name="reschedule"/>, the world was rolled back) at once, and after a host change (<paramref name="skipArrived"/>) only those that have not arrived.</summary>
    private void ScheduleLevelStartDrops(bool reschedule = false, bool skipArrived = false)
    {
        foreach (var drop in OrdnanceContent.Rules.Drops)
        {
            if (drop.When != DropWhen.LevelStart) continue;
            var call = DeveloperDrops.LevelStartCall(drop.Name);
            if (skipArrived && _startedDrops.Contains(call)) continue;
            ScheduleDrop(drop, drop.Where!, reschedule ? 0 : drop.Delay, reschedule || skipArrived ? "level start, again" : "level start", call);
        }
    }

    /// <summary>Every peer, from the level event (<see cref="DeveloperDropEvents"/>); the host places the drop named
    /// <paramref name="name"/> after <paramref name="wait"/> seconds. <paramref name="eventPlace"/> is where the event says, used when the
    /// drop has no <c>Where</c> of its own.</summary>
    internal void FireEventDrop(string name, DropWhere eventPlace, float wait)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        OrdnanceLevelDrop? drop = null;
        foreach (var candidate in OrdnanceContent.Rules.Drops)
            if (candidate.When == DropWhen.Event && candidate.Name == name) { drop = candidate; break; }
        if (drop == null)
        {
            Plugin.Logger.ErrorOnce("Ordnance drop event (type " + DeveloperDrops.EventType + ") names drop '" + name + "' in WorldEventObjectFilter, but level '"
                + (OrdnanceContent.SelectedLevelKey() ?? "?") + "' has no event drop of that name (neither in Settings.forge nor from DeclareLevel); nothing is dropped.");
            return;
        }
        ScheduleDrop(drop, drop.Where ?? eventPlace, wait, "level event");
    }

    /// <summary>Host: resolves the drop's place in this level and schedules it. Each refusal is one error naming the drop and the reason.
    /// <paramref name="call"/> is the drop's call number; 0 takes the next developer call.</summary>
    private void ScheduleDrop(OrdnanceLevelDrop drop, DropWhere where, double wait, string cause, uint call = 0)
    {
        var label = "Developer drop '" + drop.Name + "' (" + drop.Ordnance + ", " + cause + ")";
        if (!OrdnanceContent.Set.TryGetById(drop.Ordnance, out var definition))
        {
            Plugin.Logger.ErrorOnce(label + " not placed: '" + drop.Ordnance + "' is not a loaded call-in.");
            return;
        }
        Vec3 point;
        string? problem;
        try { problem = DropPlacement.Resolve(where, BuiltLevel.Instance, out point); }
        catch (InvalidOperationException error) { problem = error.Message; point = default; }
        if (problem != null)
        {
            Plugin.Logger.ErrorOnce(label + " not placed at " + where + ": " + problem + ".");
            return;
        }
        var position = ToVector(point);
        if (!ScheduleAt(definition, position, where.Dimension, drop.Arrival, drop.Yaw, wait, drop.Overrides, out problem, call))
            Plugin.Logger.ErrorOnce(label + " not placed at " + ForgeCore.Log.Pos(position) + ": " + problem + ".");
    }

    /// <summary>Host: puts one developer call on the scheduler, from where it arrives through <see cref="Arrive"/> like every call.
    /// False, with <paramref name="problem"/> and no log line, when the point is not inside an area (of <paramref name="dimension"/>
    /// when given, else of any); the caller logs.</summary>
    private bool ScheduleAt(OrdnanceDefinition definition, Vector3 position, int dimension, OrdnanceArrivalMode arrival, float yaw, double wait,
        IReadOnlyDictionary<string, double>? overrides, out string problem, uint call = 0)
    {
        if (!DeveloperNode(position, dimension, out _, out problem)) return false;
        _scheduler.Add(new PendingSummon(0, call != 0 ? call : DeveloperDrops.NextCall(ref _developerCalls), definition.Key, position.x, position.y, position.z, Now + wait, true, arrival, yaw, dimension, overrides));
        return true;
    }

    /// <summary>Host, in a level: brings <paramref name="ordnanceId"/> in at <paramref name="position"/>, as a developer drop that fires now
    /// (<see cref="OrdnanceApi.Place"/>). A client gets false and a reason, without a log line.</summary>
    internal bool Place(string ordnanceId, Vector3 position, float yaw, OrdnanceArrivalMode arrival, int dimension, IReadOnlyDictionary<string, double>? overrides, out string reason)
    {
        reason = "";
        if (!ForgeCore.Session.IsHost) { reason = "only the host places ordnance; check ForgeCore.Session.IsHost first"; return false; }
        var label = "Place '" + ordnanceId + "'";
        if (!_inLevel) reason = "not in a level";
        else if (!OrdnanceContent.Set.TryGetById(ordnanceId, out var definition)) reason = "'" + ordnanceId + "' is not a loaded call-in";
        else if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z) || !float.IsFinite(yaw)) reason = "the position and yaw must be finite";
        else if (!Enum.IsDefined(arrival)) reason = "unknown arrival mode";
        else if (arrival == OrdnanceArrivalMode.Direct && !OrdnanceApi.SupportsDirect(definition.PayloadType)) reason = "payload " + definition.PayloadType + " cannot arrive Direct";
        else if (overrides != null && OrdnanceApi.CheckOverrides(definition, overrides) is { } bad) reason = "Overrides." + bad;
        else if (ScheduleAt(definition, position, dimension, arrival, yaw, 0, overrides == null ? null : new Dictionary<string, double>(overrides), out reason)) return true;
        // Every refused call is written (a caller's mistake is its own line); only the checkpoint replay of the same drop is throttled.
        Plugin.Logger.Error(label + " refused: " + reason + ".");
        return false;
    }

    /// <summary>The course node a developer drop lands in: the one at its exact point, in <paramref name="dimension"/> when it is not negative.</summary>
    private static bool DeveloperNode(Vector3 position, int dimension, out AIG_CourseNode? node, out string problem)
    {
        problem = "";
        if (ForgeCore.Level.CourseNodeAt(position, 2f, out node, out var found, dimension) && node != null && (dimension < 0 || found == dimension)) return true;
        problem = "the point is not inside any area" + (dimension < 0 ? "" : " of dimension " + dimension) + " (no course node within 2 m)";
        node = null;
        return false;
    }
    internal void OnArrived(ulong sender, ArrivedMessage message)
    {
        if (FromCompatibleMaster(sender)) ApplyArrived(message);
    }

    private void ApplyArrived(ArrivedMessage message)
    {
        if (message.Arrival > (byte)OrdnanceArrivalMode.Direct || !message.Landing.IsFinite) { Plugin.Logger.ErrorOnce("Malformed arrival from the host for call " + message.ThrowId + "."); return; }
        NoteStarted(message.Thrower, message.ThrowId);
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition)) return;
        if (Arrivals.Count == 16) Arrivals.RemoveAt(0);
        Arrivals.Add(definition.Id + " call " + message.ThrowId + " " + (OrdnanceArrivalMode)message.Arrival + " at " + ForgeCore.Log.Pos(ToVector(message.Landing)));
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
        {
            try { payload.OnArrived(new OrdnancePresentation(definition, ToVector(message.Landing), Quaternion.Euler(0f, message.Yaw, 0f), message.Thrower, message.ThrowId, 0f, (OrdnanceArrivalMode)message.Arrival)); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Payload '" + definition.PayloadType + "' OnArrived failed: " + error); }
        }
        BeaconArrived?.Invoke(message, definition);
    }

    internal void OnDelivery(ulong sender, DeliveryMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        if (message.State == DeliveryMessage.Emptied) Deliveries.Emptied(message.Thrower, message.ThrowId);
        else if (message.State == DeliveryMessage.Recalled) Deliveries.Recalled(message.Thrower, message.ThrowId);
        else if (message.State == DeliveryMessage.Failed) DeliveryFailed(message.Thrower, message.ThrowId);
    }

    internal void DeliveryFailed(ulong thrower, uint throwId)
    {
        Deliveries.Failed(thrower, throwId);
        BeaconFailed?.Invoke(thrower, throwId);
        if (thrower == Local) Notice?.Invoke(OrdnanceText.T("notice.deliveryFailed"));
    }

    internal void OnPayloadData(ulong sender, PayloadDataMessage message)
    {
        // The host's live strike display is one-way presentation, already gated by the ForgeCore channel's epoch and
        // this host's identity. A player's own call-in handshake gates what it may call, not what the host shows it.
        if (!_inLevel || _master != sender || !OrdnanceNetwork.Channel.FromHost(sender)) return;
        var bytes = message.ToBytes();
        if (bytes == null) { Plugin.Logger.ErrorOnce("Malformed payload data from the host (length " + message.Length + ")."); return; }
        if (!OrdnanceApi.TryGetPayload(message.Type, out var payload)) return;
        OrdnanceApi.Deliver(payload, "key " + message.Type, new OrdnancePayloadData(message.Thrower, message.ThrowId, message.Tag, bytes, false));
    }

    internal void OnClaimRequest(ulong sender, ClaimMessage message)
    {
        if (_inLevel && ForgeCore.Session.IsHost && OrdnanceNetwork.Channel.IsPlayer(sender)) OrdnanceClaims.OnRequest(sender, message);
    }

    internal void OnClaimState(ulong sender, ClaimStateMessage message)
    {
        if (FromCompatibleMaster(sender)) OrdnanceClaims.OnState(message);
    }

    internal void OnDeliveryReplay(ulong sender, DeliveryReplayMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        NoteStarted(message.Thrower, message.ThrowId);
        Deliveries.Replay(message);
    }

    internal void OnPayloadReplay(ulong sender, PayloadDataMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        var bytes = message.ToBytes();
        if (bytes == null) { Plugin.Logger.ErrorOnce("Malformed replayed payload state from the host (length " + message.Length + ")."); return; }
        NoteStarted(message.Thrower, message.ThrowId);
        if (!OrdnanceApi.TryGetPayload(message.Type, out var payload)) return;
        OrdnanceApi.Deliver(payload, "key " + message.Type, new OrdnancePayloadData(message.Thrower, message.ThrowId, message.Tag, bytes, true));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static Vector3 ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    private bool FromCompatibleMaster(ulong sender) => _inLevel && _hostHelloSeen && _master == sender && OrdnanceNetwork.Channel.FromHost(sender);

    private static string NameOf(ulong lookup) => SNet.TryGetPlayer(lookup, out var p) && p != null ? p.NickName + " (" + lookup + ")" : lookup.ToString();

    /// <summary>The landing point must be standable (navigation mesh within two metres) and inside a course node of some dimension.</summary>
    private static bool TryResolve(Vec3 landing, out Vector3 snapped, out AIG_CourseNode? node)
    {
        node = null;
        if (!ForgeCore.Level.TrySnap(ToVector(landing), 2f, out snapped)) return false;
        node = CourseNodeAt(snapped);
        return node != null;
    }

    private static AIG_CourseNode? CourseNodeAt(Vector3 position)
        => ForgeCore.Level.CourseNodeAt(position, 1f, out var node, out _) ? node : null;
}
