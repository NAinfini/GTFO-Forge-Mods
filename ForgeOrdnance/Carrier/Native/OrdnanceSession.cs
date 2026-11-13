using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using ForgeUI;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Everything the mod knows about the current level: slots, the calls waiting to arrive and what this peer has shown of them.
/// The host decides (<see cref="DecideLanding"/>) and writes the result into its tables (<see cref="OrdnanceNetwork"/>); every peer,
/// the host included, follows the rows. A peer acts on a row only while it is in the level and reads the rows it holds when it enters.</summary>
internal sealed class OrdnanceSession
{
    private readonly SlotBook _book = new();
    private readonly SummonScheduler _scheduler = new();
    private readonly List<PendingSummon> _due = new();
    private bool _deliveringDue;
    private IReadOnlyList<OrdnanceDefinition> _localLoadout = Array.Empty<OrdnanceDefinition>();
    private DefinitionSet? _loadoutDefinitions;
    private ulong _loadoutVersion, _loadoutPlayer;
    /// <summary>The highest throw number the host accepted from this player in this level; a player who joins again numbers its throws above it.</summary>
    internal uint ThrowFloor => OrdnanceNetwork.FloorRows.TryGet(Local, out var floor) ? floor : 0;
    /// <summary>Host: the next call number of a developer drop (thrower 0). Seeded from the clock each time this player becomes the
    /// host, so a new host's drops never take the number of a drop still in the level.</summary>
    private uint _developerCalls = 1;
    /// <summary>The calls this peer showed as they happened (their beacon or their arrival); a kept state of any other call is a replay here.</summary>
    private readonly HashSet<CallKey> _presented = new();
    private readonly List<(ulong Owner, ThrownBeacon Ball)> _hostBeacons = new();
    // Each end simulates the thrown ball itself, so the host's copy can roll several metres away from the thrower's.
    private const float ThrowTolerance = 8f;
    private const float BeaconLifetime = 30f;

    internal OrdnanceSession()
    {
        BeaconItem.Spawned += ObserveBeacon;
        LoadoutStore.Changed += _submission.Changed;
    }

    private void ObserveBeacon(BeaconSpawn spawn)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        var agent = ForgeCore.Level.PlayerByLookup(spawn.Owner);
        if (agent == null || agent.WasCollected) return;
        var origin = agent.transform.position;
        _hostBeacons.Add((spawn.Owner, new ThrownBeacon(0, 0, new Vec3(origin.x, origin.y, origin.z), spawn.Item, spawn.Replicator)));
    }
    private bool _inLevel;
    private ulong _master;
    private readonly LoadoutSubmission _submission = new();
    /// <summary>True while the local player is typing, holding or has a thrown ball not yet landed (set by the controller owner).</summary>
    internal Func<bool> CallInProgress { get; set; } = () => false;

    /// <summary>The last 16 arrivals this peer was told of (the driver's readback, <see cref="OrdnanceControl.Arrivals"/>).</summary>
    internal List<string> Arrivals { get; } = new();

    internal event Action<string>? Notice;
    /// <summary>The host refused the local player's throw of this number, or gave no answer.</summary>
    internal event Action<uint>? ThrowRejected;
    internal event Action<CallKey, CallRow, OrdnanceDefinition>? BeaconStarted;
    internal event Action<ArrivedMessage, OrdnanceDefinition>? BeaconArrived;
    internal event Action<ulong, uint>? BeaconFailed;
    /// <summary>Level entry, checkpoint recovery or a host change: every beacon and ball in flight is void. The argument is true for a host change, which
    /// keeps the arrivals still holding something to claim (<see cref="OrdnanceClaims.HostChanged"/>) instead of voiding them.</summary>
    internal event Action<bool>? PresentationReset;

    internal SlotBook Book => _book;
    internal static ulong Local => SNet.LocalPlayer?.Lookup ?? 0UL;
    private static double Now => Time.time;
    internal bool InLevel => _inLevel;

    /// <summary>That player's copy matches this one and is in this level. The host answers for any player; a client for itself and its host.</summary>
    internal bool IsPeerCompatible(ulong player)
        => _inLevel && (ForgeCore.Session.IsHost
            ? player == Local || OrdnanceNetwork.Channel.PeerReady(player)
            : (player == Local || player == ForgeCore.GameEvents.Master) && OrdnanceNetwork.Channel.HostReady);

    /// <summary>The host's rules for this level turned the players' call-ins off: no call list, call key or cooldowns. Developer drops still arrive.</summary>
    internal bool CallMenuOff => LobbyRules.TryCurrent(out var rules, out _) && !rules.CallMenu;

    /// <summary>Call-ins work for this peer: its host runs a matching copy and is in the level (the host itself: it is in the level).</summary>
    internal bool Enabled => _inLevel && BeaconItem.Ready && OrdnanceNetwork.Channel.HostReady;
    /// <summary>Why call-ins are off, as a language key; null while they work.</summary>
    internal string? DisabledReason => !_inLevel ? "disabled.notInLevel"
        : !BeaconItem.Ready ? "disabled.ball"
        : OrdnanceNetwork.Channel.HostReady ? null
        : OrdnanceNetwork.Channel.State(ForgeCore.GameEvents.Master) == ForgeCore.PeerState.Refused ? "disabled.filesDiffer" : "disabled.waitingHost";

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
        OrdnanceNetwork.Channel.Ready = true;
        // What the host wrote before this peer entered: applied now, as the tables hold it.
        foreach (var (player, row) in OrdnanceNetwork.LoadoutRows.Rows) OnLoadoutRow(player, row, true);
        foreach (var (slot, row) in OrdnanceNetwork.SlotRows.Rows) OnSlotRow(slot, row, true);
        foreach (var (call, row) in OrdnanceNetwork.CallRows.Rows) OnCallRow(call, row, true);
        foreach (var (delivery, row) in OrdnanceNetwork.DeliveryRows.Rows) OnDeliveryRow(delivery, row, true);
        foreach (var (sentry, row) in OrdnanceNetwork.SentryRows.Rows) OnSentryRow(sentry, row, true);
        foreach (var (state, bytes) in OrdnanceNetwork.StateRows.Rows) OnStateRow(state, bytes);
        if (!ForgeCore.Session.IsHost) return;
        // A reload before the level's first checkpoint goes back to the level as it was entered: no slot saved, nothing of the content parts.
        OrdnanceNetwork.Checkpoint.Publish(CheckpointCopy.Join(default, default));
        SeedDeveloperCalls();
        ApplyAuthoredLoadouts();
        ScheduleLevelStartDrops();
    }

    private void SeedDeveloperCalls() => _developerCalls = DeveloperDrops.SeedCalls((ulong)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond));

    internal void LeaveLevel() { Reset(); _inLevel = false; }

    private void Reset()
    {
        _submission.Reset();
        _book.Clear(); _scheduler.Clear(); _hostBeacons.Clear(); _presented.Clear();
        PresentationReset?.Invoke(false);
    }

    /// <summary>ForgeCore matched a player's copy and sent it the tables. The host gives that player the level's authored loadout, and
    /// writes again every row that counts seconds from when it was written, so the newcomer does not start those clocks at their old
    /// values. It can run more than once for one player; every write here is safe to repeat.</summary>
    internal void PeerSynced(ulong peer)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost) return;
        ApplyAuthoredLoadout(peer);
        foreach (var entry in _book.Capture(Now).Entries) WriteSlot(entry.Player, entry.Key, entry.Uses, entry.CooldownRemaining);
        Deliveries.WriteRows();
        GearPayload.WriteRows();
        // A refreshed state is written back into the table, so its keys are walked from a copy; the list is the same one every time.
        _refreshed.Clear();
        _refreshed.AddRange(OrdnanceNetwork.StateRows.Rows.Keys);
        foreach (var state in _refreshed)
        {
            if (!OrdnanceApi.TryGetType(state.Type, out var type) || !OrdnanceNetwork.StateRows.TryGet(state, out var kept)) continue;
            try { OrdnanceNetwork.StateRows.Set(state, type.Payload.RefreshState(state.Thrower, state.ThrowId, state.Tag, (byte[])kept.Clone())); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Payload state refresh failed; the kept state stays as it was: " + error.Message); }
        }
    }
    private readonly List<StateKey> _refreshed = new();

    /// <summary>The host's player will not answer for this mod: Ordnance (or ForgeCore) is not installed there.</summary>
    internal void PeerSilent(ulong peer)
    {
        if (!_inLevel || ForgeCore.Session.IsHost || peer != ForgeCore.GameEvents.Master) return;
        Plugin.Logger.ErrorOnce("The host does not answer for Forge Ordnance; call-ins stay off for this level.");
        Notice?.Invoke(OrdnanceText.T("notice.hostSilent"));
    }

    /// <summary>ForgeCore's host-changed event.</summary>
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
        _loadoutDefinitions = null;
        _scheduler.Clear();
        _hostBeacons.Clear();
        PresentationReset?.Invoke(true);
        if (!ForgeCore.Session.IsHost) return;
        // The tables go on from the rows this peer held. The calls still waiting went with their host, and a kept state is one only
        // its payload's code on this host sends again. The world did not roll back: a level-start drop that already arrived is still
        // there and is not dropped again; one still waiting is scheduled again.
        OrdnanceNetwork.CallRows.Clear();
        OrdnanceNetwork.StateRows.Clear();
        SeedDeveloperCalls();
        ApplyAuthoredLoadouts();
        ScheduleLevelStartDrops(skipArrived: true);
        GearPayload.TakeOver();
    }

    /// <summary>Host, at a checkpoint: publishes the mod's one checkpoint copy (<see cref="CheckpointCopy"/>), the slot book as it
    /// stands now and after it the content parts' own bytes.</summary>
    internal void PublishCheckpoint(byte[] content)
    {
        if (_inLevel && ForgeCore.Session.IsHost) OrdnanceNetwork.Checkpoint.Publish(CheckpointCopy.Join(_book.Capture(Now).Encode(), content));
    }

    /// <summary>The content parts' bytes of the checkpoint copy this peer holds; null when it holds none.</summary>
    internal static byte[]? CheckpointContent => CheckpointCopy.TrySplit(OrdnanceNetwork.Checkpoint.Value, out _, out var content) ? content : null;

    internal void CheckpointReloaded()
    {
        if (!_inLevel) return;
        _loadoutDefinitions = null;
        _scheduler.Clear();
        _hostBeacons.Clear();
        CheckpointCopy.TrySplit(OrdnanceNetwork.Checkpoint.Value, out var slots, out _);
        _book.Restore(SlotSnapshot.Decode(slots) ?? new SlotSnapshot(Array.Empty<SlotSnapshot.Entry>()), Now);
        _presented.Clear();
        PresentationReset?.Invoke(false);
        if (!ForgeCore.Session.IsHost) return;
        OrdnanceNetwork.CallRows.Clear();
        OrdnanceNetwork.StateRows.Clear();
        OrdnanceNetwork.StartedRows.Clear();
        ApplyAuthoredLoadouts();
        foreach (var player in _book.Players.ToList()) WritePlayer(player);
        ScheduleLevelStartDrops(true);
    }

    /// <summary>Host, every frame: delivers the calls whose time has come. <paramref name="now"/> is the frame's <c>Time.time</c>.</summary>
    internal void Tick(float now)
    {
        TrySubmitLoadout();
        if (!_inLevel || !ForgeCore.Session.IsHost || _deliveringDue) return;
        for (var i = _hostBeacons.Count - 1; i >= 0; i--)
            if (_hostBeacons[i].Ball.Gone || _hostBeacons[i].Ball.Age(now) > BeaconLifetime) _hostBeacons.RemoveAt(i);
        _scheduler.TakeDue(now, _due);
        if (_due.Count == 0) return;
        _deliveringDue = true;
        try { foreach (var due in _due) Arrive(due); }
        finally { _due.Clear(); _deliveringDue = false; }
    }

    /// <summary>ForgeCore refused a player's copy (other message formats or other call-in files; ForgeCore logged the details). The host is
    /// told which player; a client whose host differs is told its call-ins stay off.</summary>
    internal void Mismatch(ulong player)
    {
        string text;
        if (ForgeCore.Session.IsHost) text = OrdnanceText.T("notice.peerMismatch", SNet.TryGetPlayer(player, out var other) && other != null ? other.NickName : player.ToString());
        else if (SNet.Master != null && SNet.Master.Lookup == player) text = OrdnanceText.T("notice.selfMismatch");
        else return;
        UIKit.PostChatNotice(text);
        Notice?.Invoke(text);
    }

    // ---- loadout and slots ---------------------------------------------------------------------------------

    /// <summary>The host's one-byte answer to a loadout or landing request.</summary>
    private static Rejection ReasonOf(ForgeCore.NetAnswer answer) => answer.Body.Length == 1 ? (Rejection)answer.Body[0] : Rejection.None;

    /// <summary>Tells the local player the host refused; <paramref name="throwId"/> is the refused throw, 0 for a loadout.</summary>
    private void Refuse(Rejection reason, uint throwId = 0)
    {
        if (reason == Rejection.None) return;
        Notice?.Invoke(OrdnanceText.T("notice.refused", OrdnanceText.Reason(reason)));
        if (throwId != 0) ThrowRejected?.Invoke(throwId);
    }

    private void TrySubmitLoadout()
    {
        if (!_inLevel || !_submission.Due(OrdnanceNetwork.Channel.HostReady, CallInProgress())) return;
        if (!LobbyRules.TrySubmissionKeys(out var keys)) return;
        _submission.Sent();
        if (ForgeCore.Session.IsHost) { Refuse(HostAcceptLoadout(Local, keys)); return; }
        OrdnanceNetwork.Loadout.AskHost(ForgeCore.NetBody.Of(LoadoutRow.From(keys)), answer =>
        {
            if (answer.Status == ForgeCore.NetAnswerStatus.Answered) Refuse(ReasonOf(answer));
            // No answer (the host left, or the level moved on): the loadout goes again to whoever is the host then.
            else if (answer.Status != ForgeCore.NetAnswerStatus.AlreadyRan) _submission.Changed();
        });
    }

    internal void ServeLoadout(ForgeCore.NetAsk ask)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost || !OrdnanceNetwork.Channel.IsPlayer(ask.From)) return;
        ask.Answer(new[] { (byte)DecideLoadout(ask.From, ask.Body) });
    }

    private Rejection DecideLoadout(ulong sender, byte[] body)
    {
        if (!OrdnanceContent.Rules.LobbyPicker)
        {
            Plugin.Logger.ErrorOnce("Loadout from " + NameOf(sender) + " ignored: this level has LobbyPicker off; the level's Loadouts decide.");
            return Rejection.None;
        }
        if (!ForgeCore.NetBody.TryRead(body, out LoadoutRow row) || row.ToKeys() is not { } keys)
        {
            Plugin.Logger.ErrorOnce("Loadout from " + sender + " is malformed (the limit is " + Messages.MaxLoadout + " entries). Refused.");
            return Rejection.LoadoutInvalid;
        }
        return HostAcceptLoadout(sender, keys);
    }

    private Rejection HostAcceptLoadout(ulong player, IReadOnlyList<uint> keys)
    {
        var rules = OrdnanceContent.Rules;
        var result = LoadoutValidator.Validate(keys, OrdnanceContent.Set, rules);
        if (!result.Ok)
        {
            Plugin.Logger.ErrorOnce("Loadout of " + NameOf(player) + " refused (" + result.Reason + "): " + result.Detail);
            return result.Reason;
        }
        _book.SetLoadout(player, result.Accepted, rules.UsesMultiplier);
        WritePlayer(player);
        return Rejection.None;
    }

    /// <summary>Host: with the lobby choice off, gives <paramref name="player"/> the loadout the level's author wrote for their slot in the party
    /// (their own list, else <c>All</c>, else none), through the same acceptance a lobby pick gets: validation, the slot book (uses and cooldown
    /// kept when it is given again) and the host's tables. With the lobby choice on, the author's <c>Loadouts</c> are not used.</summary>
    private void ApplyAuthoredLoadout(ulong player)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost || OrdnanceContent.Rules.LobbyPicker) return;
        var slot = SNet.TryGetPlayer(player, out var who) && who != null ? who.PlayerSlotIndex() : -1;
        var keys = new List<uint>();
        foreach (var id in AuthoredLoadouts.For(OrdnanceContent.Rules.Loadouts, slot) ?? Array.Empty<string>())
            if (OrdnanceContent.Set.TryGetById(id, out var definition)) keys.Add(definition.Key);
        var reason = HostAcceptLoadout(player, keys);
        if (player == Local) Refuse(reason);
    }

    /// <summary>Host: <see cref="ApplyAuthoredLoadout"/> for every player in the session (a player who joins later gets theirs in <see cref="PeerSynced"/>).</summary>
    private void ApplyAuthoredLoadouts()
    {
        var players = SNet.SessionHub?.PlayersInSession;
        if (players == null) return;
        for (var i = 0; i < players.Count; i++)
            if (players[i] is { IsBot: false } player) ApplyAuthoredLoadout(player.Lookup);
    }

    internal void OnLoadoutRow(ulong player, LoadoutRow row, bool present)
    {
        // The host's own book is what the rows are written from.
        if (!_inLevel || !present || ForgeCore.Session.IsHost) return;
        var keys = row.ToKeys();
        if (keys == null) { Plugin.Logger.ErrorOnce("Loadout row from the host claims " + row.Count + " entries; ignored."); return; }
        var known = keys.Where(k => OrdnanceContent.Set.TryGet(k, out _)).ToList();
        if (known.Count != keys.Length) Plugin.Logger.ErrorOnce("The host's loadout for " + player + " names call-ins this install does not have; those are left out.");
        _book.ApplyLoadout(player, known);
    }

    internal void OnSlotRow(SlotKey slot, SlotRow row, bool present)
    {
        if (_inLevel && present && !ForgeCore.Session.IsHost) _book.Apply(slot.Player, slot.Key, row.UsesLeft, row.InitialUses, row.CooldownRemaining, Now);
    }

    internal void OnSentryRow(ushort replicator, SentryRow row, bool present)
    {
        if (_inLevel && present) GearPayload.ApplyExpiry(replicator, row);
    }

    internal void OnDeliveryRow(DeliveryKey delivery, DeliveryRow row, bool present)
    {
        if (_inLevel && present) Deliveries.Apply(delivery, row);
    }

    /// <summary>Every peer, the host included: a state a payload type keeps for one call (<see cref="OrdnanceApi.SendState(string, ulong, uint, byte, byte[])"/>).</summary>
    internal void OnStateRow(StateKey state, byte[]? bytes)
    {
        if (!_inLevel || bytes == null || !OrdnanceApi.TryGetType(state.Type, out var type)) return;
        OrdnanceApi.Deliver(type, new OrdnancePayloadData(state.Thrower, state.ThrowId, state.Tag, (byte[])bytes.Clone(),
            !_presented.Contains(new CallKey(state.Thrower, state.ThrowId))));
    }

    /// <summary>Host: the loadout row of <paramref name="player"/> and the row of every slot in it.</summary>
    private void WritePlayer(ulong player)
    {
        OrdnanceNetwork.LoadoutRows.Set(player, LoadoutRow.From(_book.LoadoutOf(player)));
        foreach (var key in _book.LoadoutOf(player)) WriteSlot(player, key);
    }

    internal void RefundCall(ulong player, uint key, double cooldownSeconds)
    {
        _book.Refund(player, key, Now, cooldownSeconds);
        WriteSlot(player, key);
    }

    internal void PostNotice(string text) => Notice?.Invoke(text);

    private void WriteSlot(ulong player, uint key)
    {
        if (_book.TryGet(player, key, Now, out var uses, out var cooldown)) WriteSlot(player, key, uses, cooldown);
    }

    private void WriteSlot(ulong player, uint key, int uses, double cooldown)
    {
        var slot = new SlotKey(player, key);
        OrdnanceNetwork.SlotRows.TryGet(slot, out var old);
        OrdnanceNetwork.SlotRows.Set(slot, new SlotRow { UsesLeft = uses, CooldownRemaining = (float)cooldown, InitialUses = _book.InitialOf(player, key), Write = old.Write + 1 });
    }

    // ---- throw, landing, decision --------------------------------------------------------------------------

    /// <summary>The local player's ball came to rest. A client asks the host and gets one answer: the refusal, or that none came
    /// (the host did not answer in time, or is gone). An accepted call shows in the host's tables like everyone's.</summary>
    internal void ReportLanding(LandingMessage message)
    {
        if (ForgeCore.Session.IsHost) { Refuse(DecideLanding(Local, message), message.ThrowId); return; }
        var throwId = message.ThrowId;
        OrdnanceNetwork.Landing.AskHost(ForgeCore.NetBody.Of(message), answer =>
        {
            if (answer.Status == ForgeCore.NetAnswerStatus.Answered) Refuse(ReasonOf(answer), throwId);
            // A level or checkpoint change voids the ball with everything else in flight; a request the host already ran is in its tables.
            else if (answer.Status is ForgeCore.NetAnswerStatus.TimedOut or ForgeCore.NetAnswerStatus.Left) Refuse(Rejection.NoAnswer, throwId);
        });
    }

    internal void ServeLanding(ForgeCore.NetAsk ask)
    {
        if (!_inLevel || !ForgeCore.Session.IsHost || !OrdnanceNetwork.Channel.IsPlayer(ask.From)) return;
        ask.Answer(new[] { (byte)(ForgeCore.NetBody.TryRead(ask.Body, out LandingMessage message) ? DecideLanding(ask.From, message) : Rejection.InvalidLanding) });
    }

    /// <summary>Host: judges one landing. An accepted call spends its use, is scheduled and gets its row, from which every peer raises the beacon.</summary>
    private Rejection DecideLanding(ulong sender, LandingMessage message)
    {
        if (!OrdnanceContent.Set.TryGet(message.Key, out var definition)) return Rejection.UnknownDefinition;
        var rules = OrdnanceContent.Rules;
        var snapped = default(Vector3);
        var ball = _hostBeacons.Where(b => b.Owner == sender && !b.Ball.Gone && b.Ball.Age(Time.time) <= BeaconLifetime)
            .Select(b => b.Ball).FirstOrDefault(b => message.Replicator != 0 && b.Replicator == message.Replicator && message.Landing.IsFinite &&
                Vector3.Distance(b.Position, ToVector(message.Landing)) <= ThrowTolerance);
        var origin = ball?.Origin ?? new Vec3(float.NaN, float.NaN, float.NaN);
        var point = ball?.Position ?? default;
        var landing = new Vec3(point.x, point.y, point.z);
        var reason = ThrowValidator.Evaluate(_book, rules.CallMenu, sender, message.Key, origin, landing, Now, rules.MaxThrowDistance,
            () => TryResolve(landing, out snapped, out _));
        if (reason != Rejection.None) return reason;
        _hostBeacons.RemoveAll(b => ReferenceEquals(b.Ball, ball));
        _book.Consume(sender, message.Key, Now, definition.Cooldown * rules.CooldownMultiplier);
        WriteSlot(sender, message.Key);
        _scheduler.Add(new PendingSummon(sender, message.ThrowId, message.Key, snapped.x, snapped.y, snapped.z, Now + definition.CallInDelay));
        if (message.ThrowId > (OrdnanceNetwork.FloorRows.TryGet(sender, out var floor) ? floor : 0)) OrdnanceNetwork.FloorRows.Set(sender, message.ThrowId);
        OrdnanceNetwork.CallRows.Set(new CallKey(sender, message.ThrowId), new CallRow { Key = message.Key, Landing = new Vec3(snapped.x, snapped.y, snapped.z), Delay = (float)definition.CallInDelay });
        return Rejection.None;
    }

    /// <summary>Every peer, the host included: the host accepted a call; its beacon stands until the arrival.</summary>
    internal void OnCallRow(CallKey call, CallRow row, bool present)
    {
        if (!_inLevel || !present) return;
        if (!row.Landing.IsFinite || !float.IsFinite(row.Delay) || row.Delay < 0f) { Plugin.Logger.ErrorOnce("Malformed accepted call from the host."); return; }
        if (!_presented.Add(call)) return;
        if (!OrdnanceContent.Set.TryGet(row.Key, out var definition)) { Plugin.Logger.ErrorOnce("Accepted call names unknown definition key " + row.Key.ToString("x8") + "."); return; }
        if (OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload))
        {
            try { payload.OnBeaconLanded(new OrdnancePresentation(definition, ToVector(row.Landing), Vec.Identity, call.Thrower, call.ThrowId, row.Delay, OrdnanceArrivalMode.Drop)); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Payload '" + definition.PayloadType + "' OnBeaconLanded failed: " + error); }
        }
        BeaconStarted?.Invoke(call, row, definition);
    }

    /// <summary>Host: the one delivery entry. Every call that arrives comes through here when its time is due, a player's throw and a
    /// level's developer drop alike: the host runs the payload and every peer (itself included) is told it arrived, with or without
    /// its vehicle.</summary>
    private void Arrive(PendingSummon summon)
    {
        var landing = new Vec3(summon.X, summon.Y, summon.Z);
        var rotation = Vec.Identity;
        var call = new CallKey(summon.Thrower, summon.ThrowId);
        OrdnanceNetwork.CallRows.Remove(call);
        OrdnanceContent.Set.TryGet(summon.Key, out var definition);
        if (definition == null) { Plugin.Logger.ErrorOnce("A due call names unknown definition key " + summon.Key.ToString("x8") + "."); return; }
        var what = summon.Developer ? "Developer drop '" + definition.Id + "' (call " + summon.ThrowId + ")" : "Call '" + definition.Id + "' (throw " + summon.ThrowId + ")";
        // Before the payload runs: a state it keeps while it executes belongs to a call this peer shows as it happens.
        _presented.Add(call);
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
                    if (Vec.SqrLength(flat) > 0.01f) rotation = Quaternion.LookRotation(flat.normalized);
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
        // A new host does not drop a level-start drop again that has arrived.
        if (summon.Developer && DeveloperDrops.IsLevelStartCall(summon.ThrowId)) OrdnanceNetwork.StartedRows.Set(summon.ThrowId, 1);
        var arrived = new ArrivedMessage { Thrower = summon.Thrower, ThrowId = summon.ThrowId, Key = summon.Key, Landing = landing, Yaw = rotation.eulerAngles.y, Arrival = (byte)summon.Arrival };
        OrdnanceNetwork.Arrived.Broadcast(arrived);
        ApplyArrived(arrived);
    }

    // ---- developer drops -----------------------------------------------------------------------------------

    /// <summary>Host: schedules the level's <c>LevelStart</c> drops: when the level is entered after their delay, after a checkpoint reload
    /// (<paramref name="reschedule"/>, the world was rolled back) at once, and after a host change (<paramref name="skipArrived"/>) only those that have not arrived.</summary>
    private void ScheduleLevelStartDrops(bool reschedule = false, bool skipArrived = false)
    {
        foreach (var drop in OrdnanceContent.Rules.Drops)
        {
            if (drop.When != DropWhen.LevelStart) continue;
            var call = DeveloperDrops.LevelStartCall(drop.Name);
            if (skipArrived && OrdnanceNetwork.StartedRows.Rows.ContainsKey(call)) continue;
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
                + (ForgeCore.Level.ActiveKey ?? "?") + "' has no event drop of that name (neither in Settings.forge nor from DeclareLevel); nothing is dropped.");
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
        try { problem = DropPlacement.Resolve(where, out point); }
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
        if (FromHost(sender)) ApplyArrived(message);
    }

    private void ApplyArrived(ArrivedMessage message)
    {
        if (message.Arrival > (byte)OrdnanceArrivalMode.Direct || !message.Landing.IsFinite) { Plugin.Logger.ErrorOnce("Malformed arrival from the host for call " + message.ThrowId + "."); return; }
        _presented.Add(new CallKey(message.Thrower, message.ThrowId));
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

    internal void OnFailed(ulong sender, CallKey call)
    {
        if (FromHost(sender)) DeliveryFailed(call.Thrower, call.ThrowId);
    }

    internal void DeliveryFailed(ulong thrower, uint throwId)
    {
        Deliveries.Failed(thrower, throwId);
        BeaconFailed?.Invoke(thrower, throwId);
        if (thrower == Local) Notice?.Invoke(OrdnanceText.T("notice.deliveryFailed"));
    }

    /// <summary>Every peer but the host: data a payload type sent once about one call (<see cref="OrdnanceApi.SendData(string, ulong, uint, byte, byte[])"/>).</summary>
    internal void OnPayloadData(ulong sender, byte[] body)
    {
        if (!FromHost(sender)) return;
        if (body.Length < OrdnanceApi.StateKeyBytes) { Plugin.Logger.ErrorOnce("Malformed payload data from the host (" + body.Length + " bytes)."); return; }
        var call = System.Runtime.InteropServices.MemoryMarshal.Read<StateKey>(body);
        if (!OrdnanceApi.TryGetType(call.Type, out var type)) return;
        OrdnanceApi.Deliver(type, new OrdnancePayloadData(call.Thrower, call.ThrowId, call.Tag, body.AsSpan(OrdnanceApi.StateKeyBytes).ToArray(), false));
    }

    internal void OnClaimRequest(ulong sender, ClaimMessage message)
    {
        if (_inLevel && ForgeCore.Session.IsHost && OrdnanceNetwork.Channel.IsPlayer(sender)) OrdnanceClaims.OnRequest(sender, message);
    }

    internal void OnClaimState(ulong sender, ClaimStateMessage message)
    {
        if (FromHost(sender)) OrdnanceClaims.OnState(message);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static Vector3 ToVector(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The message is the host's and this peer is in the level (ForgeCore has already dropped one from another level or checkpoint).</summary>
    private bool FromHost(ulong sender) => _inLevel && OrdnanceNetwork.Channel.FromHost(sender);

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
