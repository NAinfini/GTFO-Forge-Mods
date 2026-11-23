using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ForgeEngine.Framework;
using ForgeEngine.Network;
using SNetwork;
// The kernel's own PlanIdentity is a different type with the same name: this file is the boundary that maps one
// onto the other, so the network's type is the one named explicitly.
using PlanIdentity = ForgeEngine.Network.PlanIdentity;
using ForgeCore;

namespace ForgeEngine.GameBindings;

/// <summary>
/// The network layer's game-side binding. ForgeCore's channel carries everything (transport, members, handshake, the world
/// number, requests, the frame budget) and <see cref="EngineNet"/> says what the engine sends through it; this half reads the
/// game and the runtime for both: who the players are, which content this machine holds, and where a received world, state body
/// or owner message may enter the kernel (the bridge's safe point).
/// <para>
/// The channel is opened once per process at the first <see cref="Start"/>, before any host packet can arrive, and its
/// handlers answer only while the binding is started.
/// </para>
/// </summary>
internal static class NetworkBinding
{
    private static NetChannel? _channel;
    private static EngineNet? _net;
    private static NetworkHandshake? _handshake;
    private static bool _started, _suspended, _attached;
    private static (ulong Local, bool Host, ulong Master) _session;
    // Keep the converted IL2CPP delegate: converting the managed Action again creates a new native target.
    private static Il2CppSystem.Action? _sessionHandler, _authorityHandler;
    private static Il2CppSystem.Action<SNet_Player, SNet_PlayerEvent, SNet_PlayerEventReason>? _playerHandler;
    private static NetWorld? _receivedWorld;
    private static long? _receivedStep;
    private static readonly Queue<(bool Complete, long Epoch, ArraySegment<byte> Body)> ReceivedState = new();
    private static readonly Queue<(OwnerOutput Request, CommandResult Result)> DisconnectedOwnerReplay = new();
    private static bool _flushingReceivedState, _ownerDisconnectPending, _ownerReceiptReplayPending;
    private const int MaximumResourceRows = 64;

    internal static NetworkLogPoints? LogPoints { get; private set; }

    /// <summary>The engine's one channel. The domains claim their own messages on it under their domain's prefix
    /// (map/, player/, weapon/); it exists once <see cref="Start"/> has run, which is before any domain starts.</summary>
    internal static NetChannel Channel => _channel ?? throw new InvalidOperationException("The Forge network binding has not started.");

    /// <summary>The kernel epoch ForgeCore's current world number begins at; 0 while this machine is in no session.</summary>
    internal static long WorldBase => EngineNet.WorldBase(Net.World);

    /// <summary>
    /// Starts the binding: the engine's messages are claimed on its channel (once per process), the content this machine
    /// compares is held back until plan discovery adopts a plan set, and the game's session events are followed. A suspended
    /// host never reaches this call.
    /// </summary>
    internal static void Start()
    {
        if (_started) throw new InvalidOperationException("The Forge network binding is already started.");
        var kernel = GameRuntimeBridge.Kernel ?? throw new InvalidOperationException("The network binding starts after the runtime kernel.");
        kernel.PresentationAdmission = EngineNet.AdmitPresentation;
        if (_channel == null)
        {
            // Every message is claimed before any session exists: a host greets a joining client as soon as the session lists it.
            _channel = Net.Open(NetworkProtocol.ChannelId);
            _net = new EngineNet(_channel)
            {
                Local = LocalIdentity,
                Host = MasterSession,
                LocalAddress = LocalAddress,
                SessionOf = SessionOf,
                Guard = GameRuntimeBridge.Guard,
                // Native presentation handlers can synchronously receive a new world. Its identities and state wait until
                // this command's outermost boundary completes.
                Boundary = static run =>
                {
                    using var boundary = GameRuntimeBridge.BeginDispatchBoundary();
                    var result = run();
                    boundary.Complete();
                    return result;
                },
                PresentationSettled = static (request, result) => GameRuntimeBridge.Guard(() => GameRuntimeBridge.Kernel?.ReceivePresentationResult(request, result)),
                StateReceived = static (complete, epoch, body) => Receive(() => ReceivedState.Enqueue((complete, epoch, body))),
                StepReceived = static epoch => Receive(() => _receivedStep = epoch)
            };
            _channel.Refused += PeerRefused;
            _channel.PeerSynced += PeerSynced;
            Net.WorldChanged += WorldChanged;
        }
        _started = true;
        _suspended = false;
        _channel.HoldContent();
        _handshake = new NetworkHandshake(new RuntimeIdentitySummary(kernel.Identity.Id, kernel.Identity.Version, kernel.Identity.ApiVersion, kernel.Identity.GameBuild));
        LogPoints = new NetworkLogPoints(kernel);
        _net!.Kernel = kernel;
        _net.Log = LogPoints;
        EngineNet.Current = _net;
        OwnerCommandChannel.Attach(_net, DeliverOwner);
        GameRuntimeBridge.NetworkSafePoint = () => GameRuntimeBridge.Guard(FlushReceivedState);
        // The game's events are IL2CPP delegates: a method group does not convert to one directly, so the system
        // delegate is materialized first and the game's implicit conversion takes it from there.
        _sessionHandler = (Action)SessionChanged;
        SNet_Events.OnPlayerJoin += _sessionHandler;
        SNet_Events.OnPlayerLeave += _sessionHandler;
        // The host's role is sampled by the bridge only when one of these says the session may have changed.
        _authorityHandler = (Action)GameRuntimeBridge.AuthorityChanged;
        SNet_Events.OnMasterChanged += _authorityHandler;
        SNet_Events.OnResetSessionEvent += _authorityHandler;
        _playerHandler = (Action<SNet_Player, SNet_PlayerEvent, SNet_PlayerEventReason>)PlayerChanged;
        SNet_Events.OnPlayerEvent += _playerHandler;
        global::ForgeCore.GameEvents.ExpeditionSelected += FollowRundown;
        // The generations of this kernel (the host resets them before this start): a new one re-announces this side's content.
        _generations = GameRuntimeBridge.Generations;
        _generations.Changed += ContentGenerationChanged;
    }

    /// <summary>Releases the game subscriptions and takes the kernel off the channel. Idempotent: startup cleanup runs it whether
    /// or not the binding ever started, and a second call must not throw.</summary>
    internal static void Stop()
    {
        GameRuntimeBridge.Kernel?.DisconnectOwnerReceiver();
        _ownerDisconnectPending = _ownerReceiptReplayPending = false;
        if (_sessionHandler != null)
        {
            SNet_Events.OnPlayerJoin -= _sessionHandler;
            SNet_Events.OnPlayerLeave -= _sessionHandler;
            _sessionHandler = null;
        }
        if (_authorityHandler != null)
        {
            SNet_Events.OnMasterChanged -= _authorityHandler;
            SNet_Events.OnResetSessionEvent -= _authorityHandler;
            _authorityHandler = null;
        }
        if (_playerHandler != null)
        {
            SNet_Events.OnPlayerEvent -= _playerHandler;
            _playerHandler = null;
        }
        global::ForgeCore.GameEvents.ExpeditionSelected -= FollowRundown;
        if (_generations != null) _generations.Changed -= ContentGenerationChanged;
        _generations = null;
        _installedRundowns = null;
        GameRuntimeBridge.NetworkSafePoint = null;
        ClearReceived();
        if (_attached) LogPoints?.Session("detached", _session.Host ? SessionRole.Host : SessionRole.Client);
        _attached = false;
        _session = default;
        OwnerCommandChannel.Detach();
        EngineNet.Current = null;
        if (_net != null) { _net.Kernel = null; _net.Log = null; }
        // A stopped engine compares nothing: its channel holds until a later start adopts a plan set again.
        if (_started) _channel?.HoldContent();
        _started = false;
        _handshake = null;
        LogPoints = null;
        _content = null;
        _contentRead = null;
        _adoptPending = false;
        _installedPlans = null;
        _packagesOfPlan = null;
        GameRuntimeBridge.Kernel?.ClearSessionSounds();
    }

    /// <summary>
    /// Re-reads the SNet session: who this machine's player is, whether it hosts, and who the host is. The membership events
    /// drive it, the level entry does too, and so does every world ForgeCore announces. A changed reading closes the owner
    /// receiver of the session that ended and replays the owner endings the new one is owed, both at the bridge's safe point.
    /// </summary>
    internal static void RefreshSession()
    {
        if (!_started) return;
        // A machine that cannot name the address its player is reached at is in no usable session.
        var local = SNet.HasLocalPlayer && LocalAddress().Length > 0 ? LocalIdentity() : 0;
        var session = local == 0 ? default : (local, Session.IsHost, MasterSession());
        if (session == _session) return;
        var kernel = GameRuntimeBridge.Kernel;
        if (_attached) LogPoints?.Session("detached", _session.Host ? SessionRole.Host : SessionRole.Client);
        _session = session;
        _attached = local != 0;
        if (!_attached)
        {
            // SNet may detach from a read-only lifecycle callback. Cleanup consumes the receiver ledger only at
            // the same safe boundary as received state.
            _ownerDisconnectPending = true;
            ClearReceived();
            GameRuntimeBridge.Guard(FlushReceivedState);
            kernel?.ClearSessionSounds();
            return;
        }
        LogPoints?.Session("attached", session.Item2 ? SessionRole.Host : SessionRole.Client);
        if (session.Item2 && kernel != null) OwnerCommandChannel.RestoreMigration(kernel.OwnerMigrationRequests, kernel.WorldEpoch);
        _ownerReceiptReplayPending = true;
        GameRuntimeBridge.Guard(FlushReceivedState);
    }

    /// <summary>The kernel restored the image of the host that left. The game may have named the promoted host before that:
    /// the owner steps the image holds are the ones its holders may still answer.</summary>
    internal static void CompleteHostMigration()
    {
        RefreshSession();
        if (_attached && _session.Host && GameRuntimeBridge.Kernel is { } kernel)
            OwnerCommandChannel.RestoreMigration(kernel.OwnerMigrationRequests, kernel.WorldEpoch);
    }

    /// <summary>
    /// The game's own identity for the local player: SNet's <c>Lookup</c>, the account value GTFO-API reports to a
    /// receiver as the sender of everything this machine sends. A machine with no local player has no identity.
    /// </summary>
    private static ulong LocalIdentity()
    {
        if (!SNet.HasLocalPlayer || SNet.LocalPlayer == null) return 0;
        try { return SNet.LocalPlayer.Lookup; }
        catch (Exception error)
        {
            // Native player getter; 0 is the documented "no identity" answer the callers refuse on.
            NativeReadFailures.Report("NetworkBinding.LocalIdentity " + error.GetType().Name, error.Message);
            return 0;
        }
    }

    /// <summary>
    /// The address this machine's player is reached at, in the one spelling <see cref="PlayerAddress"/> owns. It is
    /// deliberately not the session identity's account id — that value is what the transport attributes a send to,
    /// while this one is the address a requested step names to reach a player. A machine whose slot the game cannot
    /// answer for has no address, and <see cref="RefreshSession"/> reads that as no usable session.
    /// </summary>
    private static string LocalAddress()
        => SNet.HasLocalPlayer ? PlayerAddress.Of(SNet.LocalPlayer) ?? "" : "";

    private static ulong MasterSession() => SNet.HasMaster && SNet.Master != null ? SNet.Master.Lookup : 0;

    /// <summary>The player a slot address names; 0 when nobody holds it. An ambiguous slot is never widened to a different player.</summary>
    private static ulong SessionOf(string address)
    {
        var players = SNet.SessionHub?.PlayersInSession;
        ulong session = 0;
        if (players == null) return session;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (PlayerAddress.Of(player) != address) continue;
            if (session != 0) return 0;
            session = player.Lookup;
        }
        return session;
    }

    // Installed Rundown packs are the current content generation's (contract I-RELOAD): one read serves every session until a new
    // generation is committed, which reads the install again.
    private static IReadOnlyList<RundownPack>? _installedRundowns;
    private static IReadOnlyList<RundownPack> InstalledRundowns =>
        _installedRundowns ??= ContentPackages.ReadRundowns(BepInEx.Paths.BepInExRootPath, "ForgeEngine.Network", _ => { });
    private static ContentGenerations? _generations;
    // The level this side compares: the packs of the Rundown the process is set to play, read once per Rundown.
    private static NetworkContentSnapshot? _content;
    private static uint _contentRundown;
    private static IReadOnlyList<PlanIndexEntry>? _installedPlans;
    private static ILookup<string, string>? _packagesOfPlan; // plan content digest -> packs it is installed in

    // The snapshot reads and hashes every file of the packs a level names (about 0.6 s for the 43 MB V1 pack on a loaded
    // development machine), so it is read on a worker thread. It touches only the file system, never the game or the kernel.
    private static Task<NetworkContentSnapshot>? _contentRead;
    private static bool _adoptPending; // AdoptLevel started the read; its plans are announced when it lands (PumpContent)

    private static void StartContentRead()
    {
        _contentRundown = Globals.Global.RundownIdToLoad;
        var root = BepInEx.Paths.BepInExRootPath;
        var packages = NetworkContentSnapshot.NeededPackages(InstalledRundowns, _contentRundown);
        var enemies = EnemyTable(); // game data: read here, on the game thread, before the worker starts
        _contentRead = Task.Run(() => NetworkContentSnapshot.Capture(root, packages, enemies));
    }

    /// <summary>The loaded enemy table in table order. The game's spawn packet names an enemy only by its position in a prefab
    /// list built from this table, so the handshake compares all of it, whatever pack each row came from. Null when the table
    /// cannot be read; the handshake then reports it as unreadable instead of comparing nothing.</summary>
    private static IReadOnlyList<(uint Id, string Name)>? EnemyTable()
    {
        try
        {
            var blocks = GameData.EnemyDataBlock.GetAllBlocks();
            var rows = new (uint, string)[blocks.Length];
            for (var index = 0; index < rows.Length; index++) rows[index] = (blocks[index].persistentID, blocks[index].name ?? "");
            return rows;
        }
        catch (Exception error)
        {
            NativeReadFailures.Report("NetworkBinding.EnemyTable " + error.GetType().Name, error.Message);
            return null;
        }
    }

    internal static bool ContentPending => _adoptPending;

    /// <summary>Announces the level's plans once the content read has landed: the channel's content fingerprint and the detail
    /// a refused player reads are set, and the channel's handshake judges every player again. The bridge calls it every frame
    /// while <see cref="ContentPending"/>.</summary>
    internal static void PumpContent()
    {
        if (!_adoptPending) return;
        if (!_started || _installedPlans == null) { _adoptPending = false; return; }
        if (_contentRead != null)
        {
            if (!_contentRead.IsCompleted) return;
            // The read counts a file it cannot open as unreadable content, so a throw here is a defect: the caller's guard
            // pauses Forge once, and the finished task is dropped first so the next frame does not throw it again.
            var read = _contentRead;
            _contentRead = null;
            _adoptPending = false;
            _content = read.GetAwaiter().GetResult();
        }
        _adoptPending = false;
        var installed = _installedPlans.GroupBy(entry => entry.PlanId!, StringComparer.Ordinal)
            .Where(copies => copies.All(entry => entry.Digest == copies.First().Digest)).Select(copies => copies.First())
            .Select(entry => new PlanIdentity(entry.PlanId!, entry.ResourceId ?? "", entry.ResourceRevision ?? "", "", "", entry.Digest));
        // A mismatch ruling is about the plans and content that were announced when it was reached. A different
        // announcement (the level this side plays moved) is judged afresh, not by the earlier ruling.
        if (_handshake!.AdoptPlans(_content!.OfLevel(installed, _packagesOfPlan!), _content)) _suspended = false;
        _channel!.ContentDetail = _handshake.Local().Write();
        _channel.Content = _handshake.ContentText();
    }

    /// <summary>The plan set this process compares and advertises: the installed plans of startup's index, each by its
    /// identity and the digest of its bytes, and the pack each file is installed in. A level loads only its own part
    /// of them, so the comparison is made on the installed files rather than on what the kernel has parsed: two sides
    /// with the same bytes have the same bindings and mounts, and the lobby can compare before any level is chosen.</summary>
    internal static void AdoptPlans(PlanIndex index)
    {
        if (!_started || index == null) return;
        _installedPlans = index.Entries.Where(entry => entry.PlanId != null).ToArray();
        _packagesOfPlan = _installedPlans.Where(entry => NetworkContentSnapshot.PackOf(entry.Path) != null)
            .ToLookup(entry => entry.Digest, entry => NetworkContentSnapshot.PackOf(entry.Path)!);
        AdoptLevel();
    }

    /// <summary>「内容已换」 after startup: the session is told this side's content again through ForgeCore's channel. The new
    /// generation's Rundown packs are read, the level's packs hashed again, and the channel's content fingerprint set once the read lands
    /// (<see cref="PumpContent"/>), so every player is judged again against the new content (a lobby reload has no other human; a client
    /// that joins later compares the new content).</summary>
    private static void ContentGenerationChanged(ContentChanged change)
    {
        if (!_started || change.From == 0) return;
        _installedRundowns = null;
        if (_installedPlans != null) AdoptLevel();
    }

    /// <summary>Follows the level this side is set to play: the Rundown is chosen in the menu or lobby, at no moment
    /// the session events announce, so it is read when the game stores an expedition choice
    /// (<c>GameEvents.ExpeditionSelected</c>). A change reads the new level's packs and plans and announces them.</summary>
    private static void FollowRundown(uint? rundown, int tier, int index)
    {
        if (_started && _installedPlans != null && Globals.Global.RundownIdToLoad != _contentRundown) AdoptLevel();
    }

    /// <summary>The plan set is the level's too: only plans installed in a pack this level reads are compared, so a
    /// Forge pack of another level never changes the verdict. Copies of one file in two packs are one plan; copies of
    /// one identity with different bytes are refused by every loader, so they are compared as absent.</summary>
    private static void AdoptLevel()
    {
        StartContentRead();
        _adoptPending = true;
    }

    /// <summary>The channel refused a player: its message layouts or its content differ. The verdict names the first real
    /// difference; when the player is this client's host, Forge is suspended for the session and the base game goes on.</summary>
    private static void PeerRefused(ulong player, string why)
    {
        if (!_started || _handshake is not { } handshake || _channel is not { } channel) return;
        var role = _net!.Role;
        var isMaster = player == MasterSession();
        var verdict = handshake.PlansAdopted
            ? handshake.Refuse(player, role, channel.FormatDiffers(player), why, channel.ContentDetailOf(player))
            : new HandshakeEvaluation(false, NetworkCodes.ProtocolMismatch, why);
        LogPoints?.Handshake(role, isMaster, false, verdict.Code, verdict.Detail);
        if (verdict.Code.StartsWith("content-audio-", StringComparison.Ordinal))
        {
            // The digests say only that the packed resources differ; each side lists its own files so the missing hash can be named.
            var files = handshake.Content.ResourceFiles;
            for (var index = 0; index < files.Count && index < MaximumResourceRows; index++)
            {
                var name = files[index];
                var dot = name.IndexOf('.');
                LogPoints?.HandshakeResource(role, verdict.Code, dot < 0 ? "" : name.Substring(dot), dot < 0 ? name : name.Substring(0, dot),
                    index, files.Count);
            }
        }
        // A host only refuses the player it just decided about, and a verdict about any other player changes nothing here,
        // so the vanilla game keeps running in every case. A repeated verdict repeats the ruling rather than the warning.
        if (role != SessionRole.Client || !isMaster || _suspended) return;
        _suspended = true;
        var reason = handshake.PlansAdopted ? handshake.Explain(player, verdict.Code) : "";
        if (reason.Length == 0) reason = verdict.Code == NetworkCodes.PlanSetMismatch ? verdict.Detail
            : "The installed game additions do not match the host. Install the same packs and rejoin.";
        var detail = reason + " Forge is disabled for this session; the base game can continue.";
        Fail("network-suspended-by-host", detail);
        GameRuntimeBridge.Guard(() => GameRuntimeBridge.Suspend(verdict.Code, detail, notice: SuspendNotice.HostMismatch));
    }

    private static void PeerSynced(ulong player)
    {
        if (!_started) return;
        var isMaster = player == MasterSession();
        if (isMaster) _suspended = false;
        LogPoints?.Handshake(_net!.Role, isMaster, true, NetworkCodes.Accepted, "");
    }

    /// <summary>ForgeCore's world moved: the state stream starts over, the session is read again, and the kernel follows at the
    /// bridge's safe point.</summary>
    private static void WorldChanged(NetWorld world)
    {
        if (!_started) return;
        _net!.WorldChanged();
        // Only the latest world is kept for the safe point, so the session's end is told as it happens.
        if (world.Number == 0) GameRuntimeBridge.NetworkSessionEnded();
        Receive(() => _receivedWorld = world);
    }

    private static void Receive(Action remember)
    {
        if (!_started) return;
        remember();
        GameRuntimeBridge.Guard(FlushReceivedState);
    }

    /// <summary>
    /// The presentation steps the advance just decided, sent to the players their provider named. The binding
    /// owns the sending because it owns the session; the kernel owns the decision because it owns the plan.
    /// </summary>
    internal static void Present(IReadOnlyList<PresentationOutput> presentations)
    {
        if (presentations.Count != 0 && _attached) _net!.Present(presentations);
    }

    /// <summary>
    /// The owner steps the advance just decided, each sent to the one session that holds what it changes. A process
    /// with no session executes nothing: an owner write reaches a world through the session that holds it.
    /// </summary>
    internal static void Own(IReadOnlyList<OwnerOutput> ownerCommands)
    {
        if (ownerCommands.Count == 0 || !_attached || !_net!.IsHost) return;
        foreach (var output in ownerCommands) OwnerCommandChannel.Send(output);
    }

    /// <summary>The state this advance owes the session: what changed to everyone, the whole document to a player owed it.
    /// A client reaches this call too and sends nothing but a request for a document that did not come.</summary>
    internal static void SyncVariables()
    {
        GameRuntimeBridge.Kernel?.FlushOwnerEndResults();
        if (_attached) _net!.SyncState();
    }

    /// <summary>The host stepped its kernel inside the current world number (it suspended itself): its clients follow.</summary>
    internal static void AnnounceStep(long epoch) => _net?.AnnounceStep(epoch);

    /// <summary>Keeps Forge's half of one native Migration_A/B buffer where every player holds it.</summary>
    internal static void PublishMigration(byte buffer, string runtime, string? checkpoint)
    {
        if (_attached && _net!.IsHost) _net.PublishMigration(buffer, runtime, checkpoint);
    }

    internal static void DropMigration(byte buffer) => _net?.DropMigration(buffer);

    internal static bool HoldsMigration => _net is { HoldsMigration: true };

    /// <summary>Forge's half of one native migration buffer as this machine holds it.</summary>
    internal static bool TryReadMigration(byte buffer, out string runtime, out string? checkpoint)
    {
        if (_net != null) return _net.TryReadMigration(buffer, out runtime, out checkpoint);
        runtime = "";
        checkpoint = null;
        return false;
    }

    private static void SessionChanged() => GameRuntimeBridge.Guard(() =>
    {
        GameRuntimeBridge.AuthorityChanged();
        RefreshSession();
    });

    private static void PlayerChanged(SNet_Player player, SNet_PlayerEvent playerEvent, SNet_PlayerEventReason reason)
    {
        // A joining client is told it is in the session (PlayerInSession, right after the master assigned its slot) only
        // after the membership events above fired, while it had no slot and so no address. Without this reading it stays
        // unattached until its own level entry.
        if (player != null && player.IsLocal && playerEvent is SNet_PlayerEvent.PlayerInSession or SNet_PlayerEvent.PlayerIsSynced)
            SessionChanged();
    }

    /// <summary>An owner message addressed to this machine: the host's step to apply here, or (on the host) a holder's result
    /// or its notice that the step's effect ended.</summary>
    private static void DeliverOwner(OwnerChannelMessage message) => GameRuntimeBridge.Guard(() =>
    {
        var kernel = GameRuntimeBridge.Kernel;
        if (kernel == null) return;
        // A native holder handler can synchronously receive a new world; it waits until this boundary completes.
        using var boundary = GameRuntimeBridge.BeginDispatchBoundary();
        kernel.OwnerEndReporter = OwnerCommandChannel.TryReply;
        if (message.Kind == "apply")
        {
            var applied = kernel.ExecuteOwnerOutput(message.Request, LocalAddress());
            // The holder's own record of what it did with the host's step: without it a refused owner step leaves no
            // line on the machine that refused it.
            LogPoints?.Request(_net!.Role, "owner", applied.Status, applied.Code);
            // A command that answers later sends its one result when it settles; a replay of it in the
            // meantime gets no reply, because the host would take "pending" for the command's outcome.
            if (!kernel.OwnerSettlementPending(message.Request.CommandId))
                OwnerCommandChannel.Reply(message.Request, applied);
            // A lost disconnect notice is replayed whenever the original application is retried. Its
            // terminal receipt is durable in the receiver ledger, so this performs no native cleanup.
            if (kernel.DisconnectedOwnerReceipt(message.Request.CommandId) is { } receipt)
                OwnerCommandChannel.Reply(receipt.Request, receipt.Result, EffectEndReasons.Reclaimed);
            kernel.ReplayOwnerEndings(message.Request.CommandId);
        }
        else if (message.Kind == "ended" && message.Request.Operation == "apply" && message.Reason == EffectEndReasons.Reclaimed
            && !OwnerCommandLedger.IsEnding(message.Request))
            kernel.ReceiveDisconnectedOwnerEnd(message.Request, message.Result!);
        else if (message.Kind == "ended" || message.Request.Operation != "apply")
            kernel.ReceiveOwnerEnd(message.Request, message.Reason, message.Result!);
        else kernel.ReceiveOwnerResult(message.Request.CommandId, message.Request.Holder, message.Request.WorldEpoch,
            message.Result!);
        boundary.Complete();
    });

    private static void ClearReceived()
    {
        _receivedWorld = null;
        _receivedStep = null;
        ReceivedState.Clear();
        DisconnectedOwnerReplay.Clear();
    }

    /// <summary>The kernel's world count starts over at the host's number. What an earlier session left queued names
    /// epochs that may come round again, and a state body above the new epoch would wait at the head of the queue
    /// for good: both queues are emptied and the host is asked for the whole document again. A received step stays:
    /// it is of this host, and <see cref="GameRuntimeBridge.NetworkStep"/> takes it only inside the current number.</summary>
    internal static void RestartWorldCount()
    {
        ReceivedState.Clear();
        DisconnectedOwnerReplay.Clear();
        _net?.StateRefused();
    }

    /// <summary>What the network handed this machine enters the kernel here, outside every dispatch: the owner receiver's
    /// session change, then the world ForgeCore announced, then a step inside it, then the state bodies in the order they came.</summary>
    private static void FlushReceivedState()
    {
        if (GameRuntimeBridge.IsDispatching || _flushingReceivedState) return;
        _flushingReceivedState = true;
        try
        {
            // A world is also when the session is read again: what that reading owes the owner receiver is settled first.
            var world = _receivedWorld;
            _receivedWorld = null;
            if (world != null) RefreshSession();
            var kernel = GameRuntimeBridge.Kernel;
            if (_ownerDisconnectPending)
            {
                _ownerDisconnectPending = false;
                kernel?.DisconnectOwnerReceiver();
            }
            if (_ownerReceiptReplayPending && _attached && kernel != null)
            {
                _ownerReceiptReplayPending = false;
                kernel.OwnerEndReporter = OwnerCommandChannel.TryReply;
                foreach (var receipt in kernel.DisconnectedOwnerReceipts()) DisconnectedOwnerReplay.Enqueue(receipt);
                // Register the cached notices even if the earlier disconnect receipts cannot be sent yet.
                kernel.ReplayOwnerEndings();
            }
            if (world is { } moved) GameRuntimeBridge.NetworkWorldChanged(moved);
            if (_receivedStep is { } step)
            {
                _receivedStep = null;
                GameRuntimeBridge.NetworkStep(step);
            }
            kernel = GameRuntimeBridge.Kernel;
            if (kernel == null || !_attached) { ReceivedState.Clear(); DisconnectedOwnerReplay.Clear(); return; }
            while (DisconnectedOwnerReplay.TryPeek(out var receipt))
            {
                if (receipt.Request.WorldEpoch == kernel.WorldEpoch
                    && !OwnerCommandChannel.TryReply(receipt.Request, receipt.Result, EffectEndReasons.Reclaimed)) break;
                DisconnectedOwnerReplay.Dequeue();
            }
            // A body of a world this kernel has not reached waits for it; one of a world it left is dropped.
            while (ReceivedState.TryPeek(out var state) && state.Epoch <= kernel.WorldEpoch)
            {
                ReceivedState.Dequeue();
                try
                {
                    if (state.Epoch != kernel.WorldEpoch)
                        throw new RuntimeContractException(NetworkCodes.StaleWorld, "A state body belongs to another world.");
                    EngineNet.ApplyState(kernel, state.Complete, state.Epoch, state.Body);
                    LogPoints?.Variables("applied", state.Complete ? "document" : "delta", state.Epoch, state.Body.Count);
                }
                catch (Exception error)
                {
                    // A body that could not be restored leaves this side where it was (application is atomic). A change of
                    // this world is written against the state before it, so this side cannot take the changes that follow
                    // a refused one either: it asks the host for the whole document again, as it does for a refused
                    // document. Only a failed rollback leaves the world unknown.
                    if (state.Complete || state.Epoch == kernel.WorldEpoch) _net!.StateRefused();
                    if (error is RuntimeContractException { Code: "native-state-rollback-failed" }) throw;
                    Fail("network-state-refused", "World " + state.Epoch + ", " + (state.Complete ? "document" : "delta") + ", "
                        + error.GetType().Name + ": " + error.Message);
                }
            }
        }
        finally { _flushingReceivedState = false; }
    }

    /// <summary>An error with its registered code, in the host's own log (a refused state means this machine and the host no longer agree).</summary>
    private static void Fail(string code, string detail) => ForgeCore.Log.For(Plugin.PluginGuid).Error(code, detail);
}
