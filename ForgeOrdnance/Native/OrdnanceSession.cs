using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using ForgeUI;
using Player;
using SNetwork;
using UnityEngine;
using UnityEngine.AI;

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
    private ulong _greeting = (ulong)DateTime.UtcNow.Ticks, _minimumEpoch;
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
        ForgeCore.GameEvents.MasterChanged += HostChanged;
        LoadoutStore.Changed += _submission.Changed;
        OrdnanceNetwork.Refused += Mismatch;
    }

    private void ObserveBeacon(BeaconSpawn spawn)
    {
        if (!_inLevel || !SNet.IsMaster) return;
        var agent = AgentOf(spawn.Owner);
        if (agent == null || agent.WasCollected) return;
        var origin = agent.transform.position;
        _hostBeacons.Add((spawn.Owner, new ThrownBeacon(0, 0, new Vec3(origin.x, origin.y, origin.z), spawn.Item, spawn.Replicator)));
    }
    private bool _inLevel, _hostHelloSeen;
    private ulong _master;
    private readonly LoadoutSubmission _submission = new();
    /// <summary>True while the local player is typing, holding or has a thrown ball not yet landed (set by the controller owner).</summary>
    internal Func<bool> CallInProgress { get; set; } = () => false;

    internal event Action<string>? Notice;
    internal event Action<RejectMessage>? ThrowRejected;
    internal event Action<AcceptedMessage, OrdnanceDefinition>? BeaconStarted;
    internal event Action<ArrivedMessage, OrdnanceDefinition>? BeaconArrived;
    internal event Action<ulong, uint>? BeaconFailed;
    /// <summary>Checkpoint recovery or a host change: every beacon and ball in flight is void.</summary>
    internal event Action? PresentationReset;

    internal SlotBook Book => _book;
    internal static ulong Local => SNet.LocalPlayer?.Lookup ?? 0UL;
    private static double Now => Time.time;
    internal bool InLevel => _inLevel;

    internal bool IsPeerCompatible(ulong player)
        => _inLevel && (SNet.IsMaster
            ? player == Local || _admitted.Contains(player)
            : _hostHelloSeen && (player == Local || player == _master) && FromMaster(_master));

    /// <summary>The host's rules for this level turned the players' call-ins off: no call list, call key or cooldowns. Developer drops still arrive.</summary>
    internal bool CallMenuOff => LobbyRules.TryCurrent(out var rules, out _) && !rules.CallMenu;

    /// <summary>Call-ins work for this peer: it is the host, or the host has answered (only a compatible copy's answer arrives).</summary>
    internal bool Enabled => _inLevel && BeaconItem.Ready && (SNet.IsMaster || _hostHelloSeen);
    /// <summary>Why call-ins are off, as a language key; null while they work.</summary>
    internal string? DisabledReason => !_inLevel ? "disabled.notInLevel"
        : !BeaconItem.Ready ? "disabled.ball"
        : SNet.IsMaster || _hostHelloSeen ? null
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
        OrdnanceNetwork.Epoch = SNet.IsMaster ? (ulong)DateTime.UtcNow.Ticks : 0;
        _minimumEpoch = 0;
        _greeting++;
        Announce();
        if (!SNet.IsMaster) return;
        SeedDeveloperCalls();
        if (OrdnanceContent.Settings.Unmatched(OrdnanceContent.SelectedLevelKey()) is { } unmatched && _warned.Add(unmatched)) Plugin.Report(unmatched);
        ScheduleLevelStartDrops();
    }

    /// <summary>Warnings already written since the level was entered; the same full message is written once per level.</summary>
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private void SeedDeveloperCalls() => _developerCalls = DeveloperDrops.SeedCalls((ulong)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));

    internal void LeaveLevel() { Reset(); _inLevel = false; _warned.Clear(); }

    private void Reset()
    {
        _submission.Reset();
        _book.Clear(); _scheduler.Clear(); _admitted.Clear(); _seenThrows.Clear();
        _checkpoint = null; _incomingCheckpoint = null; _hostBeacons.Clear(); _hostHelloSeen = false;
        OrdnanceApi.Retained.Clear();
        OrdnanceNetwork.Reset();
        _accepted.Clear(); _hellos.Clear();
        PresentationReset?.Invoke();
    }

    private HelloMessage MyHello(ulong greeting = 0, ulong peer = 0) => new() { Greeting = greeting, ThrowFloor = _seenThrows.Floor(peer) };

    /// <summary>Starts (or repeats) the handshake: the host announces itself, a client greets the host.</summary>
    internal void Announce()
    {
        if (!_inLevel) return;
        if (SNet.IsMaster)
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
        if (SNet.IsMaster)
        {
            if (Known(peer, out var player) && !player.IsLocal) OrdnanceNetwork.To(Messages.HelloEvent, MyHello(), player);
        }
        else if (peer == _master && !_hostHelloSeen) Announce();
    }

    /// <summary>The host's player never answered ForgeCore's greeting: Ordnance (or ForgeCore) is not installed there.</summary>
    internal void PeerSilent(ulong peer)
    {
        if (!_inLevel || SNet.IsMaster || peer != _master || _hostHelloSeen) return;
        Plugin.Error("Call-in handshake: the host did not answer; call-ins stay off for this level.");
        Notice?.Invoke(OrdnanceText.T("notice.hostSilent"));
    }
    /// <summary>ForgeCore's host-changed event: re-handshakes with the new host.</summary>
    private void HostChanged() => TakeOverHost(false);

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
        OrdnanceNetwork.Epoch = SNet.IsMaster ? (ulong)DateTime.UtcNow.Ticks : 0;
        _minimumEpoch = 0; _greeting++;
        _loadoutDefinitions = null;
        var dropped = _scheduler.Clear();
        _hostBeacons.Clear(); _incomingCheckpoint = null;
        _admitted.Clear(); _seenThrows.Clear(); _hostHelloSeen = false;
        OrdnanceApi.Retained.Clear();
        _accepted.Clear(); _hellos.Clear();
        PresentationReset?.Invoke();
        if (SNet.IsMaster) { SeedDeveloperCalls(); ScheduleLevelStartDrops(true); }
        Announce();
    }

    internal void CheckpointReached()
    {
        if (!_inLevel || !SNet.IsMaster) return;
        _checkpoint = _book.Capture(Now);
        foreach (var message in CheckpointState()) OrdnanceNetwork.Broadcast(Messages.CheckpointEvent, message);
    }

    internal void CheckpointReloaded()
    {
        if (!_inLevel) return;
        OrdnanceNetwork.Reset();
        _admitted.Clear();
        _minimumEpoch = OrdnanceNetwork.Epoch + 1;
        if (SNet.IsMaster) OrdnanceNetwork.Epoch = _minimumEpoch;
        else { _hostHelloSeen = false; _greeting++; }
        _loadoutDefinitions = null;
        var dropped = _scheduler.Clear();
        _hostBeacons.Clear(); _incomingCheckpoint = null;
        _book.Restore(_checkpoint ?? new SlotSnapshot(Array.Empty<SlotSnapshot.Entry>()), Now);
        OrdnanceApi.Retained.Clear();
        _accepted.Clear(); _hellos.Clear();
        PresentationReset?.Invoke();
        if (SNet.IsMaster) { foreach (var player in _book.Players.ToList()) BroadcastPlayer(player); ScheduleLevelStartDrops(true); }
        else Announce();
    }

    /// <summary>Host, every frame: delivers the calls whose time has come.</summary>
    internal void Tick()
    {
        TrySubmitLoadout();
        if (!_inLevel || !SNet.IsMaster || _deliveringDue) return;
        if (_hostBeacons.Count != 0) _hostBeacons.RemoveAll(b => b.Ball.Gone || b.Ball.Age > BeaconLifetime);
        _scheduler.TakeDue(Now, _due);
        if (_due.Count == 0) return;
        _deliveringDue = true;
        try { foreach (var due in _due) Arrive(due); }
        finally { _due.Clear(); _deliveringDue = false; }
    }

    // ---- handshake -----------------------------------------------------------------------------------------

    internal void OnHello(ulong sender, HelloMessage message)
    {
        if (!_inLevel) return;
        // A refused copy's messages never get here; one ForgeCore has not answered for yet waits for the next greeting.
        if (OrdnanceNetwork.State(sender) != ForgeCore.PeerState.Compatible)
        {
            return;
        }
        if (SNet.IsMaster)
        {
            if (!Known(sender, out var player) || player.IsLocal) return;
            var decision = _hellos.Evaluate(sender, message.Greeting, message.Epoch, OrdnanceNetwork.Epoch);
            if (decision == HelloDecision.Ignore) return;
            if (decision == HelloDecision.Challenge)
            {
                OrdnanceNetwork.To(Messages.HelloEvent, MyHello(message.Greeting, sender), player);
                return;
            }
            _admitted.Add(sender);
            SyncTo(player);
            return;
        }
        if (!FromMaster(sender)) return;
        // Recovery requires the answer to our new greeting, not an old queued hello.
        if (message.Greeting == 0) { if (!_hostHelloSeen) Announce(); return; }
        if (_hostHelloSeen || message.Greeting != _greeting || message.Epoch < _minimumEpoch) return;
        OrdnanceNetwork.Epoch = message.Epoch;
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
        if (SNet.IsMaster) text = OrdnanceText.T("notice.peerMismatch", SNet.TryGetPlayer(player, out var other) && other != null ? other.NickName : player.ToString());
        else if (SNet.Master != null && SNet.Master.Lookup == player) text = OrdnanceText.T("notice.selfMismatch");
        else return;
        UIKit.PostChatNotice(text);
        Notice?.Invoke(text);
    }

    // ---- loadout and slots ---------------------------------------------------------------------------------

    private void TrySubmitLoadout()
    {
        if (!_inLevel || !_submission.Due(SNet.IsMaster || _hostHelloSeen, CallInProgress())) return;
        if (!LobbyRules.TrySubmissionKeys(out var keys)) return;
        if (SNet.IsMaster) HostAcceptLoadout(Local, keys);
        else
        {
            OrdnanceNetwork.ToHost(Messages.LoadoutEvent, LoadoutMessage.From(Local, keys));
        }
        _submission.Sent();
    }

    internal void OnLoadoutSubmitted(ulong sender, LoadoutMessage message)
    {
        if (!_inLevel || !SNet.IsMaster || !Known(sender, out _)) return;
        if (!_admitted.Contains(sender))
        {
            RejectTo(sender, 0, 0, Rejection.DefinitionMismatch);
            return;
        }
        var keys = message.ToKeys();
        if (keys == null)
        {
            Plugin.Error("Loadout from " + sender + " claims " + message.Count + " entries; the limit is " + Messages.MaxLoadout + ". Refused.");
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
            Plugin.Error("Loadout of " + NameOf(player) + " refused (" + result.Reason + "): " + result.Detail);
            RejectTo(player, 0, 0, result.Reason);
            return;
        }
        _book.SetLoadout(player, result.Accepted, rules.UsesMultiplier);
        BroadcastPlayer(player);
    }

    private string SlotSummary(ulong player)
    {
        var parts = new List<string>();
        foreach (var key in _book.LoadoutOf(player))
        {
            var name = OrdnanceContent.Set.TryGet(key, out var d) ? d.Id : key.ToString("x8");
            parts.Add(_book.TryGet(player, key, Now, out var uses, out var cooldown)
                ? name + " (" + (uses < 0 ? "unlimited" : uses + " use(s)") + (cooldown > 0 ? ", cooling " + Math.Ceiling(cooldown) + " s" : "") + ")"
                : name + " (no slot)");
        }
        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    private static string KeyNames(IEnumerable<uint> keys)
    {
        var names = new List<string>();
        foreach (var key in keys) names.Add(OrdnanceContent.Set.TryGet(key, out var d) ? d.Id : key.ToString("x8"));
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    internal void OnLoadoutState(ulong sender, LoadoutMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        var keys = message.ToKeys();
        if (keys == null) { Plugin.Error("Loadout state from the host claims " + message.Count + " entries; ignored."); return; }
        var known = keys.Where(k => OrdnanceContent.Set.TryGet(k, out _)).ToList();
        if (known.Count != keys.Length) Plugin.Error("The host's loadout for " + message.Player + " names call-ins this install does not have; those are left out.");
        _book.ApplyLoadout(message.Player, known);
    }

    internal void OnSlot(ulong sender, SlotMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        _book.Apply(message.Player, message.Key, message.UsesLeft, message.CooldownRemaining, Now);
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
                    Plugin.Error("Malformed checkpoint cooldown from the host; recovery image ignored.");
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
                Kind = CheckpointMessage.Entry, Player = entry.Player, Key = entry.Key,
                UsesLeft = entry.Uses, CooldownRemaining = entry.CooldownRemaining
            };
        yield return new CheckpointMessage { Kind = CheckpointMessage.Commit };
    }

    private void BroadcastPlayer(ulong player)
    {
        OrdnanceNetwork.Broadcast(Messages.LoadoutStateEvent, LoadoutMessage.From(player, _book.LoadoutOf(player)));
        foreach (var key in _book.LoadoutOf(player)) BroadcastSlot(player, key);
    }

    private void BroadcastSlot(ulong player, uint key)
    {
        if (_book.TryGet(player, key, Now, out var uses, out var cooldown))
            OrdnanceNetwork.Broadcast(Messages.SlotEvent, new SlotMessage { Player = player, Key = key, UsesLeft = uses, CooldownRemaining = (float)cooldown });
    }

    /// <summary>Everything the host knows, to one peer that just completed the handshake.</summary>
    private void SyncTo(SNet_Player peer)
    {
        foreach (var player in _book.Players.ToList())
        {
            OrdnanceNetwork.To(Messages.LoadoutStateEvent, LoadoutMessage.From(player, _book.LoadoutOf(player)), peer);
        }
        // Retained slots outside the current loadout must survive host migration as well.
        foreach (var entry in _book.Capture(Now).Entries)
            OrdnanceNetwork.To(Messages.SlotEvent, new SlotMessage { Player = entry.Player, Key = entry.Key, UsesLeft = entry.Uses, CooldownRemaining = (float)entry.CooldownRemaining }, peer);
        foreach (var message in CheckpointState()) OrdnanceNetwork.To(Messages.CheckpointEvent, message, peer);
        foreach (var message in GearPayload.ExpiryState()) OrdnanceNetwork.To(Messages.SentryExpiryEvent, message, peer);
        foreach (var pending in _scheduler.Beacons())
            OrdnanceNetwork.To(Messages.AcceptedEvent, new AcceptedMessage
            {
                Thrower = pending.Thrower, ThrowId = pending.ThrowId, Key = pending.Key,
                Landing = new Vec3(pending.X, pending.Y, pending.Z), Delay = (float)Math.Max(0, pending.ArriveAt - Now)
            }, peer);
        var arrivals = Deliveries.ReplayState();
        foreach (var arrival in arrivals) OrdnanceNetwork.To(Messages.DeliveryReplayEvent, arrival, peer);
        foreach (var state in OrdnanceApi.Retained.InOrder()) OrdnanceNetwork.To(Messages.PayloadReplayEvent, state, peer);
    }

    // ---- throw, landing, decision --------------------------------------------------------------------------

    internal void ReportLanding(LandingMessage message)
    {
        if (SNet.IsMaster) OnLanding(Local, message);
        else OrdnanceNetwork.ToHost(Messages.LandingEvent, message);
    }

    internal void OnLanding(ulong sender, LandingMessage message)
    {
        if (!_inLevel || !SNet.IsMaster) return;
        if (sender != Local && !Known(sender, out _)) return;
        var compatible = sender == Local || _admitted.Contains(sender);
        if (!compatible) { RejectTo(sender, message.ThrowId, message.Key, Rejection.DefinitionMismatch); return; }
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition))
        {
            RejectTo(sender, message.ThrowId, message.Key, Rejection.UnknownDefinition);
            return;
        }
        if (!_seenThrows.Add(sender, message.ThrowId))
        {
            return;
        }
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
            Thrower = sender, ThrowId = message.ThrowId, Key = message.Key,
            Landing = new Vec3(snapped.x, snapped.y, snapped.z), Delay = (float)definition.CallInDelay
        };
        OrdnanceNetwork.Broadcast(Messages.AcceptedEvent, accepted);
        ApplyAccepted(accepted);
    }

    private void RejectTo(ulong player, uint throwId, uint key, Rejection reason)
    {
        var message = new RejectMessage { ThrowId = throwId, Key = key, Reason = (byte)reason };
        if (player == Local) ApplyReject(message);
        else if (SNet.TryGetPlayer(player, out var target) && target != null) OrdnanceNetwork.To(Messages.RejectEvent, message, target);
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
        if (!message.Landing.IsFinite || !float.IsFinite(message.Delay) || message.Delay < 0f) { Plugin.Error("Malformed accepted call from the host."); return; }
        if (!_accepted.Add((message.Thrower, message.ThrowId))) return;
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition)) { Plugin.Error("Accepted call names unknown definition key " + message.Key.ToString("x8") + "."); return; }
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
        {
            try { payload.OnBeaconLanded(new OrdnancePresentation(definition, ToVector(message.Landing), Quaternion.identity, message.Thrower, message.ThrowId, message.Delay, OrdnanceArrivalMode.Drop)); }
            catch (Exception error) { Plugin.Error("Payload '" + definition.PayloadType + "' OnBeaconLanded failed: " + error); }
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
        if (definition == null) { Plugin.Error("A due call names unknown definition key " + summon.Key.ToString("x8") + "."); return; }
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
                summoner = AgentOf(summon.Thrower);
                if (summoner != null)
                {
                    var flat = position - summoner.transform.position;
                    flat.y = 0f;
                    if (flat.sqrMagnitude > 0.01f) rotation = Quaternion.LookRotation(flat.normalized);
                }
            }
            payload.Execute(new OrdnanceExecution(definition, position, rotation, node!, summoner, summon.Thrower, summon.ThrowId, summon.Arrival));
        }
        catch (Exception error)
        {
            Plugin.Error(what + (summon.Developer ? "" : " from " + NameOf(summon.Thrower)) + " failed on arrival" + (summon.Developer ? "" : " (the use stays spent)") + ": " + error);
            Deliveries.ReportFailure(summon.Thrower, summon.ThrowId);
            return;
        }
        var arrived = new ArrivedMessage { Thrower = summon.Thrower, ThrowId = summon.ThrowId, Key = summon.Key, Landing = landing, Yaw = rotation.eulerAngles.y, Arrival = (byte)summon.Arrival };
        OrdnanceNetwork.Broadcast(Messages.ArrivedEvent, arrived);
        ApplyArrived(arrived);
    }

    // ---- developer drops -----------------------------------------------------------------------------------

    /// <summary>Host, entering the level: schedules the level's <c>LevelStart</c> drops.</summary>
    private void ScheduleLevelStartDrops(bool reschedule = false)
    {
        foreach (var drop in OrdnanceContent.Rules.Drops)
            if (drop.When == DropWhen.LevelStart) ScheduleDrop(drop, drop.Where!, reschedule ? 0 : drop.Delay, reschedule ? "level start, again" : "level start", DeveloperDrops.LevelStartCall(drop.Name));
    }

    /// <summary>Every peer, from the level event (<see cref="DeveloperDropEvents"/>); the host places the drop named
    /// <paramref name="name"/> after <paramref name="wait"/> seconds. <paramref name="eventPlace"/> is where the event says, used when the
    /// drop has no <c>Where</c> of its own.</summary>
    internal void FireEventDrop(string name, DropWhere eventPlace, float wait)
    {
        if (!_inLevel || !SNet.IsMaster) return;
        OrdnanceLevelDrop? drop = null;
        foreach (var candidate in OrdnanceContent.Rules.Drops)
            if (candidate.When == DropWhen.Event && candidate.Name == name) { drop = candidate; break; }
        if (drop == null)
        {
            Plugin.Error("Ordnance drop event (type " + DeveloperDrops.EventType + ") names drop '" + name + "' in WorldEventObjectFilter, but level '"
                + (OrdnanceContent.SelectedLevelKey() ?? "?") + "' has no event drop of that name (neither in Settings.json nor from DeclareLevel); nothing is dropped.");
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
            Plugin.Error(label + " not placed: '" + drop.Ordnance + "' is not a loaded call-in.");
            return;
        }
        Vec3 point;
        string? problem;
        try { problem = DropPlacement.Resolve(where, BuiltLevel.Instance, out point); }
        catch (InvalidOperationException error) { problem = error.Message; point = default; }
        if (problem != null)
        {
            Plugin.Error(label + " not placed at " + where + ": " + problem + ".");
            return;
        }
        var position = ToVector(point);
        if (!ScheduleAt(definition, position, where.Dimension, drop.Arrival, drop.Yaw, wait, out problem, call))
            Plugin.Error(label + " not placed at " + Plugin.Pos(position) + ": " + problem + ".");
    }

    /// <summary>Host: puts one developer call on the scheduler, from where it arrives through <see cref="Arrive"/> like every call.
    /// False, with <paramref name="problem"/> and no log line, when the point is not inside an area (of <paramref name="dimension"/>
    /// when given, else of any); the caller logs.</summary>
    private bool ScheduleAt(OrdnanceDefinition definition, Vector3 position, int dimension, OrdnanceArrivalMode arrival, float yaw, double wait, out string problem, uint call = 0)
    {
        if (!DeveloperNode(position, dimension, out _, out problem)) return false;
        _scheduler.Add(new PendingSummon(0, call != 0 ? call : DeveloperDrops.NextCall(ref _developerCalls), definition.Key, position.x, position.y, position.z, Now + wait, true, arrival, yaw, dimension));
        return true;
    }

    /// <summary>Host, in a level: brings <paramref name="ordnanceId"/> in at <paramref name="position"/>, as a developer drop that fires now
    /// (<see cref="OrdnanceApi.Place"/>). A client gets false and a reason, without a log line.</summary>
    internal bool Place(string ordnanceId, Vector3 position, float yaw, OrdnanceArrivalMode arrival, int dimension, out string reason)
    {
        reason = "";
        if (!SNet.IsMaster) { reason = "only the host places ordnance; check SNet.IsMaster first"; return false; }
        var label = "Place '" + ordnanceId + "'";
        if (!_inLevel) reason = "not in a level";
        else if (!OrdnanceContent.Set.TryGetById(ordnanceId, out var definition)) reason = "'" + ordnanceId + "' is not a loaded call-in";
        else if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z) || !float.IsFinite(yaw)) reason = "the position and yaw must be finite";
        else if (!Enum.IsDefined(arrival)) reason = "unknown arrival mode";
        else if (arrival == OrdnanceArrivalMode.Direct && !OrdnanceApi.SupportsDirect(definition.PayloadType)) reason = "payload " + definition.PayloadType + " cannot arrive Direct";
        else if (ScheduleAt(definition, position, dimension, arrival, yaw, 0, out reason)) return true;
        // Every refused call is written (a caller's mistake is its own line); only the checkpoint replay of the same drop is throttled.
        Plugin.Report(label + " refused: " + reason + ".");
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
        if (message.Arrival > (byte)OrdnanceArrivalMode.Direct || !message.Landing.IsFinite) { Plugin.Error("Malformed arrival from the host for call " + message.ThrowId + "."); return; }
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition)) return;
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
        {
            try { payload.OnArrived(new OrdnancePresentation(definition, ToVector(message.Landing), Quaternion.Euler(0f, message.Yaw, 0f), message.Thrower, message.ThrowId, 0f, (OrdnanceArrivalMode)message.Arrival)); }
            catch (Exception error) { Plugin.Error("Payload '" + definition.PayloadType + "' OnArrived failed: " + error); }
        }
        BeaconArrived?.Invoke(message, definition);
    }

    internal void OnDelivery(ulong sender, DeliveryMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        if (message.State == DeliveryMessage.Emptied) Deliveries.Emptied(message.Thrower, message.ThrowId);
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
        if (!FromCompatibleMaster(sender)) return;
        var bytes = message.ToBytes();
        if (bytes == null) { Plugin.Error("Malformed payload data from the host (length " + message.Length + ")."); return; }
        if (!OrdnanceApi.TryGetPayload(message.Type, out var payload))
        {
            return;
        }
        OrdnanceApi.Deliver(payload, "key " + message.Type, new OrdnancePayloadData(message.Thrower, message.ThrowId, message.Tag, bytes, false));
    }

    internal void OnClaimRequest(ulong sender, ClaimMessage message)
    {
        if (_inLevel && SNet.IsMaster && Known(sender, out _)) OrdnanceClaims.OnRequest(sender, message);
    }

    internal void OnClaimState(ulong sender, ClaimStateMessage message)
    {
        if (FromCompatibleMaster(sender)) OrdnanceClaims.OnState(message);
    }

    internal void OnDeliveryReplay(ulong sender, DeliveryReplayMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        Deliveries.Replay(message);
    }

    internal void OnPayloadReplay(ulong sender, PayloadDataMessage message)
    {
        if (!FromCompatibleMaster(sender)) return;
        var bytes = message.ToBytes();
        if (bytes == null) { Plugin.Error("Malformed replayed payload state from the host (length " + message.Length + ")."); return; }
        if (!OrdnanceApi.TryGetPayload(message.Type, out var payload))
        {
            return;
        }
        OrdnanceApi.Deliver(payload, "key " + message.Type, new OrdnancePayloadData(message.Thrower, message.ThrowId, message.Tag, bytes, true));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static Vector3 ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    private static bool Known(ulong lookup, out SNet_Player player)
    {
        player = null!;
        if (!SNet.TryGetPlayer(lookup, out var found) || found == null || found.IsBot) return false;
        player = found;
        return true;
    }

    private bool FromCompatibleMaster(ulong sender) => _inLevel && _hostHelloSeen && _master == sender && FromMaster(sender);

    private static bool FromMaster(ulong sender) => !SNet.IsMaster && SNet.Master != null && SNet.Master.Lookup == sender;

    private static string NameOf(ulong lookup) => SNet.TryGetPlayer(lookup, out var p) && p != null ? p.NickName + " (" + lookup + ")" : lookup.ToString();

    internal static PlayerAgent? AgentOf(ulong lookup) => SNet.TryGetPlayer(lookup, out var p) && p != null ? p.PlayerAgent?.TryCast<PlayerAgent>() : null;

    /// <summary>The landing point must be standable (navigation mesh within two metres) and inside a course node of some dimension.</summary>
    private static bool TryResolve(Vec3 landing, out Vector3 snapped, out AIG_CourseNode? node)
    {
        var point = ToVector(landing);
        snapped = point;
        node = null;
        if (!NavMesh.SamplePosition(point, out var hit, 2f, -1)) return false;
        snapped = hit.position;
        node = CourseNodeAt(snapped);
        return node != null;
    }

    private static AIG_CourseNode? CourseNodeAt(Vector3 position)
        => ForgeCore.Level.CourseNodeAt(position, 1f, out var node, out _) ? node : null;
}
