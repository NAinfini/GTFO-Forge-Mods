using ForgeEngine.GameBindings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgePlayer.Native;

/// <summary>MAP5a player entity identity. One life is one PlayerAgent instance observed for one SNet_Player
/// in one Runtime world. Downed, revive, heal and warp never allocate or end a life here; respawn, landing
/// and checkpoint semantics belong to the rest of MAP5 and are not modelled. Only the host allocates lives.</summary>
internal sealed class PlayerIdentityModule : IDisposable
{
    private const string Prefix = EntityKinds.Player + ":";
    // Lookup is only a native/transport mapping key. It never appears in an entity ID, log or diagnostic.
    private sealed record Entry(ulong Key, SNet_Player Player, IntPtr PlayerPointer, PlayerAgent Agent, IntPtr AgentPointer,
        EntityReference Reference)
    {
        internal bool Active = true;
    }
    private readonly record struct Observed(SNet_Player Player, PlayerAgent Agent);
    private readonly Dictionary<long, Entry> _players = new();
    // Per-world entity number for a player key, kept across that player's lives until the world changes.
    private readonly Dictionary<ulong, long> _numbers = new();
    private readonly HashSet<IntPtr> _unresolvedReported = new();
    private readonly HashSet<ulong> _conflictReported = new();
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canObserve;
    private readonly Action<string> _log, _warn;
    private readonly RuntimeModuleHandle _registration;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _nextLife, _nextNumber;
    private readonly HashSet<EntityReference> _retiredMappings = new();
    private readonly Dictionary<long, long> _remoteLives = new();
    /// <summary>The host's recorded lives as session state. A broadcast presentation resolves the entities its
    /// players are addressed by on the host, so a client has to hold the same identities; the owner path only
    /// imports them for one command.</summary>
    private DomainStateChannel.NativeStateSource? _identityState;
    /// <summary>The last state table that named a life this machine could not bind yet: its agent may not exist
    /// here when the body arrives, so the table waits for the next native life change instead of leaving that
    /// player unresolved until the next whole document.</summary>
    private JsonElement? _identityPending;
    // Resolve interop metadata once. A build without this reviewed native surface explicitly refuses import.
    private static readonly System.Reflection.MethodInfo? ReplicatorMethod = typeof(PlayerAgent).GetMethod("GetReplicator", Type.EmptyTypes);
    private static readonly System.Reflection.PropertyInfo? ReplicatorKey = ReplicatorMethod?.ReturnType.GetProperty("Key");
    internal Func<PlayerAgent, ushort?> NativeLifeKey { get; set; } = agent =>
        ReplicatorMethod?.Invoke(agent, null) is { } replicator && ReplicatorKey?.GetValue(replicator) is ushort key ? key : null;
    private bool _disposed;
    public bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>Whether this process may read a recorded life at all right now: the registration is live, the
    /// runtime is ready and this peer is the authority. It is the same gate every readback in this class already
    /// applies, exposed because the life half asks before it reads rather than deriving a second gate.</summary>
    public bool Authoritative => CanObserve;

    /// <summary>Attaches this domain's half to the one Player registration. The registration itself is owned by
    /// the session, because the runtime accepts exactly one provider of an identity namespace and `gtfo.player` has
    /// to be declared by that one provider. The registration is already in place when this constructor runs, so a
    /// rejected registration leaves no module and this half is simply not attached.</summary>
    internal PlayerIdentityModule(RuntimeModuleHandle registration, IForgeEngine kernel, Func<bool> canObserve, Action<string> log, Action<string> warn)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        _registration.RegisterOwnerIdentities((holder, _) => ExportMappings(holder), ImportMappings, OwnerSession);
        _identityState = DomainStateChannel.RegisterNativeState(kernel, "forge.player.identity",
            CaptureIdentityState, RestoreIdentityState);
        // The resolver, the instance lookup, the read-only observer and the `forge.selector.target.players`
        // query binding are this half's whole runtime surface. The observer reads one named life through the
        // kernel's budgeted query; the binding answers with the lives this module records, so a selector never
        // enumerates native state on its own. Every one of them is handed this instance by the session that
        // built it; there is no process-wide slot another half could read a stale module from.
        // Checkpoint reload, level cleanup and authority loss reach Player as a host world change; no local copy.
        _lifecycle = _registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged
                || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            {
                ClearWorld();
                // The game spawns the host's agent in the lobby, before the level's world begins, and raises no second
                // spawn for it. The agent list is the truth, so the new world reads it once on its first ticks.
                _rescan = value.Current.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped);
            }
            else if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
            {
                if (_rescan && CanObserve) { _rescan = false; Reconcile(); }
                if (_endScans > 0)
                {
                    _endScans--;
                    InvalidateEndedMappings();
                }
            }
        });
    }

    // How many more ticks look for lives that ended. A death, a spawn or a despawn arms it; the game frees a
    // destroyed agent at the end of its frame, so the look runs a few ticks after the event, and keeps running while
    // a life is dead but its downed state has not left yet.
    private const int EndScanTicks = 3;
    private int _endScans;
    private bool _rescan;

    /// <summary>Raised after a recorded life ended and left the table: death, despawn, replacement or key conflict.
    /// World changes and runtime stops are not announced here; their holders end with the world.</summary>
    internal event Action<EntityReference>? LifeEnded;

    /// <summary>A native event that can end a life happened (a player set dead, a player spawned or despawned). The
    /// next ticks check every recorded life once; no tick looks while nothing happened. A client also retries the
    /// last state table there, because the agent it could not bind when the body arrived may exist now.</summary>
    internal void NoteLifeChange()
    {
        _endScans = EndScanTicks;
        if (Session.IsHost || _identityPending is not { } pending) return;
        _identityPending = null;
        ImportMappings(pending, replace: true);
    }

    private void EndLife(Entry entry)
    {
        if (!entry.Active) return;
        entry.Active = false;
        IdentityRevision++;
        IdentityChanged();
        ReportEnded(entry.Reference);
    }

    /// <summary>The recorded lives changed: a host's next state body carries them.</summary>
    private void IdentityChanged()
    {
        if (Session.IsHost) _identityState?.Changed();
    }

    // The player kind declares EntityEndReports, so every way a life stops being current reports it exactly here.
    private void ReportEnded(EntityReference reference)
    {
        if (_registration.IsRegistered) _registration.EntityEnded(reference);
    }

    internal int Count { get { CheckThread(); return _players.Count; } }
    // An invalidation watermark, never a substitute for Resolve's native checks before publishing a fact.
    internal long IdentityRevision { get; private set; }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player identity requires the owning simulation thread.");
    }

    // Not the InLevel gameplay gate: agents are spawned during the elevator states of the same world epoch,
    // before InLevel, and a gated first readback would leave them unrecorded.
    private bool CanObserve
        => CanRead && Session.IsHost;
    private bool CanRead => IsRegistered && _kernel.StartupState == RuntimeStartupState.Ready && _canObserve();

    internal void ClearWorld()
    {
        CheckThread();
        IdentityRevision++;
        _endScans = 0;
        foreach (var entry in _players.Values)
            if (entry.Active) { entry.Active = false; ReportEnded(entry.Reference); }
        _players.Clear(); _numbers.Clear(); _unresolvedReported.Clear(); _conflictReported.Clear(); _nextNumber = 0;
        _retiredMappings.Clear(); _remoteLives.Clear(); _identityPending = null;
        IdentityChanged();
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _lifecycle.Dispose(); ClearWorld(); _identityState?.Dispose(); _identityState = null; _disposed = true;
    }

    /// <summary>Reads the in-level agent list after a native spawn or despawn body and reconciles lives.
    /// A changed agent or player pointer for a player key is a new life under the same per-world number.</summary>
    public void Reconcile()
    {
        CheckThread();
        if (!CanObserve) return;
        var observed = new Dictionary<ulong, Observed>();
        var conflicts = new HashSet<ulong>();
        int count = Players.Count;
        for (int i = 0; i < count; i++)
        {
            var agent = Players.At(i);
            if (agent == null) continue; // Unity equality: destroyed agents are not in the level.
            var player = agent.Owner;
            if (player == null || !Linked(player, agent))
            {
                if (_unresolvedReported.Add(agent.Pointer))
                    _warn("player.owner-unresolved: an in-level agent without a linked SNet_Player is not recorded.");
                continue;
            }
            ulong key = player.Lookup;
            if (observed.TryGetValue(key, out var prior))
            {
                if (prior.Agent.Pointer != agent.Pointer || prior.Player.Pointer != player.Pointer) conflicts.Add(key);
                continue;
            }
            observed.Add(key, new Observed(player, agent));
        }
        foreach (var entry in new List<Entry>(_players.Values))
        {
            string? reason = !observed.TryGetValue(entry.Key, out var now) ? "despawned"
                : conflicts.Contains(entry.Key) ? "key-conflict"
                : entry.Reference.WorldEpoch != _kernel.WorldEpoch || now.Agent.Pointer != entry.AgentPointer
                    || now.Player.Pointer != entry.PlayerPointer ? "replaced" : null;
            if (reason == null) continue;
            _players.Remove(_numbers[entry.Key]);
            EndLife(entry);
            _log("player.life-ended " + Describe(entry.Reference) + " reason=" + reason);
            LifeEnded?.Invoke(entry.Reference);
        }
        foreach (var item in observed)
        {
            if (!item.Value.Agent.Alive && PlayerObservation.DownedState(item.Value.Agent) == null) continue;
            if (conflicts.Contains(item.Key))
            {
                if (_conflictReported.Add(item.Key))
                    _warn("player.key-conflict: two in-level agents claim one SNet_Player; neither is recorded.");
                continue;
            }
            if (!_numbers.TryGetValue(item.Key, out var number)) _numbers.Add(item.Key, number = checked(++_nextNumber));
            if (_players.ContainsKey(number)) continue;
            var reference = new EntityReference(Prefix + number.ToString(CultureInfo.InvariantCulture),
                _kernel.WorldEpoch, checked(++_nextLife));
            var now = item.Value;
            _players.Add(number, new Entry(item.Key, now.Player, now.Player.Pointer, now.Agent, now.Agent.Pointer, reference));
            IdentityRevision++;
            IdentityChanged();
            _log("player.life-started " + Describe(reference) + " bot=" + (now.Player.IsBot ? "true" : "false"));
        }
    }

    private Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return null;
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(Prefix, StringComparison.Ordinal)
            || !long.TryParse(reference.Id.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !_players.TryGetValue(number, out var entry) || entry.Reference != reference) return null;
        if (_retiredMappings.Contains(reference)) return null;
        return BoundCurrent(entry) ? entry : null;
    }

    internal Func<bool>? BindCurrent(EntityReference reference)
        => Resolve(reference) is { } entry ? () => BoundCurrent(entry) : null;

    private bool BoundCurrent(Entry entry)
    {
        CheckThread();
        if (!entry.Active || _disposed || _kernel.StartupState != RuntimeStartupState.Ready || !_canObserve()
            || entry.Reference.WorldEpoch != _kernel.WorldEpoch) return false;
        var agent = entry.Agent;
        var player = entry.Player;
        if (agent == null || agent.Pointer != entry.AgentPointer || player == null || player.Pointer != entry.PlayerPointer
            || player.Lookup != entry.Key) return false;
        var owner = agent.Owner;
        return owner != null && owner.Pointer == entry.PlayerPointer && Linked(player, agent);
    }

    internal bool IsCurrent(EntityReference reference) => Resolve(reference) != null;

    /// <summary>Whether a life was ever recorded for this player in the current world. The per-world number is kept
    /// across that player's lives until the world changes, so a recorded life that has ended still counts.</summary>
    public bool HadLife(SNet_Player? player)
    {
        CheckThread();
        return player != null && CanRead && _numbers.ContainsKey(player.Lookup);
    }

    /// <summary>The entity the replication data of a spawn names, or null while the identity tracks no life for
    /// that player. The player is matched by its native pointer, exactly as the instance lookup does, and the
    /// reference that answers is the one <see cref="Resolve"/> already verified.</summary>
    public EntityReference? ReferenceOf(SNet_Player? player)
    {
        CheckThread();
        if (player == null || !CanRead) return null;
        var pointer = player.Pointer;
        foreach (var entry in _players.Values)
            if (entry.PlayerPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    /// <summary>The entity of a native agent a callback carried, or null when this module tracks no life for it.
    /// It is the same pointer comparison the instance lookup performs, over the agents the module recorded rather
    /// than over the player objects.</summary>
    public EntityReference? ReferenceOf(object? instance)
    {
        CheckThread();
        if (instance is not PlayerAgent agent || agent == null || !CanRead) return null;
        var pointer = agent.Pointer;
        foreach (var entry in _players.Values)
            if (entry.AgentPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    /// <summary>The transport key an owner command addressed to this session address runs under, or null when the address
    /// does not name exactly one session member with a current life. Owner commands can arrive before any equipment has
    /// been observed, so the answer comes from the player directory alone. A host-side bot has no peer of its own: its
    /// commands run on the host, under the host's key.</summary>
    internal ulong? OwnerSession(string address)
    {
        CheckThread();
        var members = SNet.SessionHub?.PlayersInSession;
        if (members == null || string.IsNullOrEmpty(address)) return null;
        ulong? lookup = null;
        for (var index = 0; index < members.Count; index++)
        {
            var player = members[index];
            if (player == null || PlayerAddress.Of(player) != address) continue;
            var transport = player.IsBot ? SNet.LocalPlayer?.Lookup ?? 0 : player.Lookup;
            if (ReferenceOf(player) == null || transport == 0 || lookup.HasValue) return null;
            lookup = transport;
        }
        return lookup;
    }

    /// <summary>One read of a recorded life right now: alive from the agent's own flag, downed from the
    /// locomotion machine's current state, and whether that state already ran its revive. A state machine that
    /// is not there to read answers "not revived" rather than guessing. Null means the identity no longer holds
    /// the reference, which is not the same as a life that is simply not downed.</summary>
    public RuntimePlayerLife? LifeOf(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (Resolve(reference) is not { } entry || entry.Agent is not { } agent) return null;
        var state = PlayerObservation.DownedState(agent);
        return new RuntimePlayerLife(agent.Alive, state != null, state is { m_isRevived: true });
    }

    /// <summary>The actor the game recorded on a downed life's revive interaction. The interop surface of this
    /// build does not expose that actor: the interact base keeps its per-interactor records in a nested info type
    /// whose `Agent` member the interaction itself does not carry, so there is no read here to make. The answer is
    /// null, and the `revived` fact's rescuer is the one the revive interaction's own callback named, exactly as a
    /// `revive_started` row's is — the caller keeps that actor and this read adds none.</summary>
    public EntityReference? ReviverOf(EntityReference reference)
    {
        CheckThread();
        return null;
    }

    /// <summary>The position of a recorded life in metres, or null when it cannot be read. Only finite
    /// coordinates answer: a position the game could not produce is not a position.</summary>
    public double[]? PositionOf(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (Resolve(reference) is not { } entry || entry.Agent is not { } agent) return null;
        var position = agent.Position;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new double[] { position.x, position.y, position.z } : null;
    }

    /// <summary>The position a teleport argument names, in metres, or null when it is not a finite position.
    /// The argument is the game's own location struct, so the read is the same finite-coordinate rule the
    /// recorded life's own position goes through.</summary>
    public double[]? Position(object? locationData)
    {
        CheckThread();
        if (!CanObserve) return null;
        if (locationData is not pPlayerLocationData location) return null;
        var position = location.goodPosition;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new double[] { position.x, position.y, position.z } : null;
    }

    /// <summary>The position of a recorded life in metres, or null when it cannot be read. It is the same read
    /// <see cref="PositionOf"/> performs, under the name the life-world contract declares.</summary>
    public double[]? Position(EntityReference reference) => PositionOf(reference);

    /// <summary>Drops this world's recorded lives. The kernel's world epoch is what invalidates the references
    /// already handed out; this releases the table they were read from.</summary>
    public void BeginWorld() => ClearWorld();

    /// <summary>Whether a native change to a recorded life is legal right now: the same readiness, authority,
    /// ownership and fault gate every readback goes through. The action layer asks this instead of deriving a
    /// second gate from the kernel and the network flag, so a commit can never be attempted in a state the
    /// module would refuse to read in.</summary>
    public bool CanCommit => CanObserve;

    /// <summary>The same gate for a change that runs on the machine that simulates the player, which is any peer and
    /// not only the host: the registration is live, the runtime is ready and the session is not faulted. Holding
    /// the player is not decided here; the `owner` handler proves it with the agent's own ownership flag, and the
    /// kernel only routes a step to the session of the player it names.</summary>
    public bool CanCommitOwned => CanRead;

    /// <summary>The current agent behind a recorded life, for the half that reads or commits native state on it;
    /// null when the reference is not this module's current life. The instance is handed out only after the same
    /// verification a snapshot read performs, and the caller re-reads and re-compares the pointers it captured
    /// before it commits anything, so a life replaced mid-command is refused rather than written through.</summary>
    public PlayerAgent? CurrentAgent(EntityReference reference)
    {
        CheckThread();
        return Resolve(reference)?.Agent;
    }

    /// <summary>Read-only snapshot for a current player life: position, health, alive/downed/dead and the
    /// host, local, bot and slot facts. A reference whose identity the module no longer holds is refused, and
    /// a life replaced while its native state was being read yields no snapshot at all.</summary>
    internal RuntimeEntitySnapshot? Observe(EntityReference reference)
    {
        CheckThread();
        if (!CanObserve) return null;
        var entry = Resolve(reference);
        if (entry == null) return null;
        var agent = entry.Agent;
        return agent == null || agent.Pointer != entry.AgentPointer
            ? null : PlayerObservation.Read(agent, entry.Player, entry.Reference);
    }

    /// <summary>The position and life state <see cref="Observe"/> would publish for the same life, without the
    /// snapshot (<see cref="PlayerObservation.ReadPose"/>).</summary>
    internal bool ObservePose(EntityReference reference, out EntityPose pose)
    {
        pose = default;
        CheckThread();
        if (!CanObserve) return false;
        var entry = Resolve(reference);
        var agent = entry?.Agent;
        return agent != null && agent.Pointer == entry!.AgentPointer && PlayerObservation.ReadPose(agent, entry.Player, out pose);
    }

    /// <summary>The recorded lives that still resolve right now, in first-recorded order. The caller is the Player
    /// candidate source, which the kernel reads under its query budget: this enumeration reads the
    /// module's own registry and resolves each entry as it lists it, and that one read is what a selector's
    /// `query` step spends. The order it answers in is the registry's, so the candidate source decides the
    /// order the kind publishes. A client answers from the table the host sent it: that is what its kernel names
    /// "every player" by when it plays a step ahead of the host's order.</summary>
    internal IReadOnlyList<EntityReference> CurrentPlayers()
    {
        CheckThread();
        if (!CanRead) return Array.Empty<EntityReference>();
        var current = new List<EntityReference>(_players.Count);
        foreach (var entry in _players.Values)
            if (Resolve(entry.Reference) is { } live) current.Add(live.Reference);
        return current;
    }

    /// <summary>SDK instance lookup: a player or native agent maps to its recorded current life, anything else to null.
    /// It never allocates, so a player seen before the spawn readback has no reference yet rather than a guessed one.</summary>
    internal EntityReference? ResolveInstance(object instance)
    {
        CheckThread();
        if (instance is PlayerAgent) return ReferenceOf(instance);
        if (instance is not SNet_Player player || player == null || !CanObserve) return null;
        var pointer = player.Pointer;
        foreach (var entry in _players.Values)
            if (entry.PlayerPointer == pointer) return Resolve(entry.Reference)?.Reference;
        return null;
    }

    private static bool Linked(SNet_Player player, PlayerAgent agent)
    {
        var linked = player.PlayerAgent?.TryCast<PlayerAgent>();
        return linked != null && linked.Pointer == agent.Pointer;
    }

    private void InvalidateEndedMappings()
    {
        // .NET 6 Dictionary.Remove preserves enumerators; retirement only removes entries, so no snapshot is needed.
        foreach (var pair in _players)
        {
            var agent = pair.Value.Agent;
            bool dead = !agent.Alive;
            var downed = dead ? PlayerObservation.DownedState(agent) : null;
            if (Resolve(pair.Value.Reference) == null || dead && downed == null)
            {
                var entry = pair.Value;
                _retiredMappings.Add(entry.Reference);
                _players.Remove(pair.Key);
                EndLife(entry);
                LifeEnded?.Invoke(entry.Reference);
            }
            else if (dead) _endScans = Math.Max(_endScans, 1);
        }
    }

    internal JsonElement ExportMappings(ulong? holder = null)
    {
        CheckThread();
        if (!CanObserve) return RuntimeJson.From(Array.Empty<object>());
        InvalidateEndedMappings();
        return RuntimeJson.From(_players.Values.Where(entry => holder == null || entry.Key == holder).Select(entry => new
        { reference = entry.Reference, session = entry.Key, replica = NativeLifeKey(entry.Agent) }).ToArray());
    }

    internal void ImportMappings(JsonElement mappings) => ImportMappings(mappings, replace: false);

    /// <summary>The host's recorded lives, as the session state. A body replaces this client's whole table: a life
    /// the host no longer names must stop resolving here, while a row this machine cannot bind (its agent has not
    /// spawned yet, or its replica key is unavailable) is left out instead of refusing the session's whole state.
    /// <para>The host allocates the lives, so a table put back on it (a checkpoint reload, or the image a machine takes
    /// over as the new host) never replaces a life it recorded itself: only a row for a player this host has not
    /// numbered in this world is adopted, with the number and the life the table gives it, and the host's own counters
    /// move past every number and life the table names. On the host that saved the checkpoint that is no row at all;
    /// on a machine that just became the host, whose table the world change emptied, it is the whole table.</para></summary>
    private void ImportMappings(JsonElement mappings, bool replace)
    {
        CheckThread();
        var host = Session.IsHost;
        if (!CanRead || host && !replace) throw new RuntimeContractException("owner-not-client", "Identity imports require a ready client.");
        InvalidateEndedMappings();
        var imported = new Dictionary<long, Entry>();
        var named = new HashSet<long>();
        var waiting = false;
        foreach (var row in mappings.EnumerateArray())
        {
            var reference = RuntimeJson.Entity(row.GetProperty("reference"));
            if (reference.WorldEpoch != _kernel.WorldEpoch)
            {
                if (replace) continue;
                throw new RuntimeContractException("stale-world", "Player mapping world changed.");
            }
            if (!reference.Id.StartsWith(Prefix, StringComparison.Ordinal)
                || !long.TryParse(reference.Id.AsSpan(Prefix.Length), out var number))
            {
                if (replace) continue;
                throw new RuntimeContractException("owner-identity-invalid", "Invalid host player reference.");
            }
            named.Add(number);
            var session = row.GetProperty("session").GetUInt64();
            if (host)
            {
                // No later life of this host may reuse a number or a life the table names, bound here or not.
                _nextNumber = Math.Max(_nextNumber, number); _nextLife = Math.Max(_nextLife, reference.LifeEpoch);
                if (_numbers.ContainsKey(session) || _numbers.ContainsValue(number)) continue;
            }
            if (_retiredMappings.Contains(reference) || _remoteLives.TryGetValue(number, out var last) && reference.LifeEpoch < last)
            {
                if (replace) continue;
                throw new RuntimeContractException("stale-entity", "Player mapping life ended.");
            }
            var replica = row.GetProperty("replica");
            if (replica.ValueKind != JsonValueKind.Number)
            {
                if (replace) continue;
                throw new RuntimeContractException(RuntimeKernel.OwnerUnsupportedCode, "Player replica is unavailable.");
            }
            PlayerAgent? match = null;
            for (int c = 0, total = Players.Count; c < total; c++)
            {
                var candidate = Players.At(c);
                if (candidate != null && (candidate.Alive || PlayerObservation.DownedState(candidate) != null)
                    && candidate.Owner is { } player && player.Lookup == session && Linked(player, candidate)
                    && NativeLifeKey(candidate) == replica.GetUInt16())
                {
                    if (match != null) throw new RuntimeContractException("owner-identity-conflict", "Ambiguous native player mapping.");
                    match = candidate;
                }
            }
            // Missing remote players do not replace a local player. Their references remain unresolved, and a
            // whole-table body waits for the native life that will let this machine bind them.
            if (match == null) { waiting |= replace; continue; }
            if (_players.TryGetValue(number, out var previous) && previous.Reference == reference && previous.AgentPointer != match.Pointer)
            {
                // The life ends completely before the refusal: leaving it in the table would end it again on every retry.
                _retiredMappings.Add(reference); _players.Remove(number); EndLife(previous); LifeEnded?.Invoke(previous.Reference);
                if (!replace) throw new RuntimeContractException("stale-entity", "Native player instance changed.");
            }
            var matchedPlayer = match.Owner!;
            imported[number] = new Entry(session, matchedPlayer, matchedPlayer.Pointer, match, match.Pointer, reference);
            _remoteLives[number] = reference.LifeEpoch;
        }
        if (replace && !host)
            foreach (var pair in new List<KeyValuePair<long, Entry>>(_players))
            {
                if (named.Contains(pair.Key)) continue;
                _retiredMappings.Add(pair.Value.Reference);
                _players.Remove(pair.Key);
                EndLife(pair.Value);
                LifeEnded?.Invoke(pair.Value.Reference);
            }
        foreach (var entry in imported)
        {
            if (_players.TryGetValue(entry.Key, out var previous))
            {
                if (previous.Reference == entry.Value.Reference && previous.AgentPointer == entry.Value.AgentPointer
                    && previous.PlayerPointer == entry.Value.PlayerPointer) continue;
                _retiredMappings.Add(previous.Reference);
                _players.Remove(entry.Key);
                EndLife(previous);
                LifeEnded?.Invoke(previous.Reference);
            }
            _players[entry.Key] = entry.Value;
            if (host) _numbers[entry.Value.Key] = entry.Key;
            IdentityRevision++;
            IdentityChanged();
        }
        // A row the host could not bind is not retried: the next spawn readback records that player under a new number.
        if (replace) _identityPending = waiting && !host ? mappings.Clone() : null;
    }

    /// <summary>The lives the host records, for the session state: one row per life, the same body the owner path
    /// sends. A host's state body carries this whenever a life starts or ends, so a client resolves the player
    /// entities a broadcast presentation names.</summary>
    private JsonElement CaptureIdentityState() => ExportMappings();

    /// <summary>A state body replaces a client's whole identity table; the host keeps the lives it recorded and adopts
    /// only the players it has not numbered (see the import's replace form).</summary>
    private void RestoreIdentityState(JsonElement mappings) => ImportMappings(mappings, replace: true);

    private static string Describe(EntityReference reference)
        => "id=" + reference.Id + " world=" + reference.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + " life=" + reference.LifeEpoch.ToString(CultureInfo.InvariantCulture);
}
