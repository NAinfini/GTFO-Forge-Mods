using System;
using System.Collections.Generic;
using AIGraph;
using Player;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>A call-in payload type. Register one with <see cref="OrdnanceApi.RegisterPayload(string, IOrdnancePayload)"/> from your plugin's
/// <c>Load</c>; definition files then name it in <c>Payload.Type</c>.</summary>
public interface IOrdnancePayload : IOrdnancePayloadParser
{
    /// <summary>Runs on the host when the call arrives. An exception is logged and the call is lost; no use is refunded.</summary>
    void Execute(OrdnanceExecution execution);

    /// <summary>Runs on every peer when the host accepts the call and the beacon starts counting down.</summary>
    void OnBeaconLanded(OrdnancePresentation presentation) { }

    /// <summary>Runs on every peer when the call arrives, after the host's <see cref="Execute"/> succeeds.</summary>
    void OnArrived(OrdnancePresentation presentation) { }

    /// <summary>True when <see cref="OnArrived"/> plays its own arrival with its own impact (for example
    /// <see cref="OrdnanceDelivery.StartPod"/>); the framework's generic arrival camera shake is then skipped.</summary>
    bool HasOwnArrival => false;

    /// <summary>True when this payload honours <see cref="OrdnanceArrivalMode.Direct"/>: for such an arrival
    /// (<see cref="OrdnanceExecution.Arrival"/>, <see cref="OrdnancePresentation.Arrival"/>) it plays no vehicle and its contents are
    /// there at once. A level's developer drop that asks for a direct arrival of a payload type without it is refused, with an
    /// error, when the definitions load.</summary>
    bool SupportsDirect => false;

    /// <summary>The numbers a placement may set for this payload type (<c>Overrides</c> of a level drop, <see cref="OrdnanceApi.Place"/>): name,
    /// range, whole or not. The framework refuses anything else with an error before the call is scheduled and hands the accepted values to
    /// <see cref="OrdnanceExecution.Overrides"/> on the host; the payload applies them and sends what the other peers need through its own
    /// per-call state (<see cref="OrdnanceApi.SendState"/>). Empty (the default): this type takes no overrides.</summary>
    IReadOnlyList<OrdnanceOverrideField> OverrideFields => Array.Empty<OrdnanceOverrideField>();

    /// <summary>The check only this payload can make on accepted <paramref name="overrides"/> against the definition's parsed
    /// <paramref name="parameters"/> (a value above what the definition allows): a reason, or null when they are fine. Runs when the
    /// settings load and again in <see cref="OrdnanceApi.Place"/>.</summary>
    string? CheckOverrides(object parameters, IReadOnlyDictionary<string, double> overrides) => null;

    /// <summary>Custom pod art for this definition, or null for the built-in pod. Called by StartPod and late-join
    /// replay. Return the same cached appearance on every peer; load its bundle before arrivals can be received.</summary>
    OrdnancePodAppearance? GetPodAppearance(OrdnanceDefinition definition) => null;

    /// <summary>The turret shell's burrow, head and lifetime for this definition, which <see cref="OrdnanceDelivery.StartShell"/> and
    /// late-join replay use. Null gives the plain shell: it bores in, throws its lids and sinks away again, nothing rises. Return the same on every peer.</summary>
    OrdnanceShellAppearance? GetShellAppearance(OrdnanceDefinition definition) => null;

    /// <summary>Runs on every peer (the host included) for data the host sent with <see cref="OrdnanceApi.SendData"/> for a call
    /// of this payload type. Recovery epochs reject earlier-level/recovery traffic; within the current epoch check
    /// <see cref="OrdnanceDelivery.FindPod"/> or your own state before updating a finished call.</summary>
    void OnData(OrdnancePayloadData data) { }

    /// <summary>Host: called for each kept <see cref="OrdnanceApi.SendState"/> state just before it is replayed to a player who joins
    /// later; returns the bytes to replay. A state that holds times relative to when it was sent (seconds left) must be rebuilt from the
    /// payload's current record here, or the late joiner starts those clocks over. The default replays <paramref name="data"/> as kept.</summary>
    byte[] RefreshState(ulong summonerLookup, uint throwId, byte tag, byte[] data) => data;
}

/// <summary>What <see cref="IOrdnancePayload.OnData"/> receives.</summary>
public sealed class OrdnancePayloadData
{
    internal OrdnancePayloadData(ulong summonerLookup, uint throwId, byte tag, byte[] data, bool replayed)
    {
        SummonerLookup = summonerLookup; ThrowId = throwId; Tag = tag; Data = data; Replayed = replayed;
    }

    public ulong SummonerLookup { get; }
    public uint ThrowId { get; }
    /// <summary>The sender's own message kind.</summary>
    public byte Tag { get; }
    /// <summary>0 to <see cref="OrdnanceApi.MaxDataBytes"/> bytes, as sent.</summary>
    public byte[] Data { get; }
    /// <summary>True on a player who joined after the host sent it with <see cref="OrdnanceApi.SendState"/>: the call's
    /// <see cref="IOrdnancePayload.OnBeaconLanded"/> and <see cref="IOrdnancePayload.OnArrived"/> never ran on this peer, so build
    /// the arrival's current look from the state alone.</summary>
    public bool Replayed { get; }
}

/// <summary>Holds the call key off while it exists (<see cref="OrdnanceApi.BlockCalls"/>).</summary>
public sealed class OrdnanceCallBlock : IDisposable
{
    internal OrdnanceCallBlock(string reason) => Reason = reason;

    /// <summary>Who holds the block and why, for whoever reads the code or a dump; a press while blocked is ignored without a log line.</summary>
    public string Reason { get; }
    public bool Active => OrdnanceApi.Blocks.Contains(this);

    /// <summary>Releases this block; calls work again once no block is left. Safe to call more than once.</summary>
    public void Dispose() => OrdnanceApi.Blocks.Remove(this);
}

/// <summary>Everything the host knows when a call arrives.</summary>
public sealed class OrdnanceExecution
{
    internal OrdnanceExecution(OrdnanceDefinition definition, Vector3 position, Quaternion rotation, AIG_CourseNode node, PlayerAgent? summoner, ulong summonerLookup, uint throwId,
        OrdnanceArrivalMode arrival, IReadOnlyDictionary<string, double>? overrides = null)
    {
        Definition = definition; Position = position; Rotation = rotation; CourseNode = node; Summoner = summoner; SummonerLookup = summonerLookup; ThrowId = throwId; Arrival = arrival;
        Overrides = overrides ?? OverrideCheck.None;
    }

    /// <summary>The numbers this placement set (<see cref="IOrdnancePayload.OverrideFields"/>), already checked; empty for a player's call and for a drop that sets none.
    /// Host only: other peers get what they need from the payload's own state.</summary>
    public IReadOnlyDictionary<string, double> Overrides { get; }

    public OrdnanceDefinition Definition { get; }
    /// <summary>What <see cref="IOrdnancePayloadParser.Parse"/> returned for this definition.</summary>
    public object Parameters => Definition.Parameters;
    /// <summary>The landing point: a throw's snapped onto the navigation mesh, a developer drop's exactly as the level places it.</summary>
    public Vector3 Position { get; }
    /// <summary>Flat rotation facing from the thrower toward the landing point; a developer drop's faces its level-given yaw.</summary>
    public Quaternion Rotation { get; }
    public AIG_CourseNode CourseNode { get; }
    /// <summary>The player who called it, or null when they have left since or it is a developer drop.</summary>
    public PlayerAgent? Summoner { get; }
    /// <summary>The caller's player lookup; 0 for a developer drop (the level placed it, no player called it).</summary>
    public ulong SummonerLookup { get; }
    /// <summary>With or without its vehicle: player call-ins always <see cref="OrdnanceArrivalMode.Drop"/>; only payloads with
    /// <see cref="IOrdnancePayload.SupportsDirect"/> receive <see cref="OrdnanceArrivalMode.Direct"/>.</summary>
    public OrdnanceArrivalMode Arrival { get; }
    /// <summary>With <see cref="SummonerLookup"/>, identifies this call on every peer (<see cref="OrdnancePresentation.ThrowId"/>).</summary>
    public uint ThrowId { get; }
}

/// <summary>What a presentation callback gets, identical on every peer.</summary>
public sealed class OrdnancePresentation
{
    internal OrdnancePresentation(OrdnanceDefinition definition, Vector3 position, Quaternion rotation, ulong summonerLookup, uint throwId, float delay,
        OrdnanceArrivalMode arrival)
    {
        Definition = definition; Position = position; Rotation = rotation; SummonerLookup = summonerLookup; ThrowId = throwId; Delay = delay; Arrival = arrival;
    }

    /// <summary>As <see cref="OrdnanceExecution.Arrival"/>; always <see cref="OrdnanceArrivalMode.Drop"/> in <see cref="IOrdnancePayload.OnBeaconLanded"/>
    /// (only throws have beacons).</summary>
    public OrdnanceArrivalMode Arrival { get; }

    public OrdnanceDefinition Definition { get; }
    public object Parameters => Definition.Parameters;
    public Vector3 Position { get; }
    /// <summary>On arrival: the host's <see cref="OrdnanceExecution.Rotation"/>, so every peer orients the arrival the same way.
    /// Identity in <see cref="IOrdnancePayload.OnBeaconLanded"/>.</summary>
    public Quaternion Rotation { get; }
    public ulong SummonerLookup { get; }
    public uint ThrowId { get; }
    /// <summary>Seconds until arrival at the time of <see cref="IOrdnancePayload.OnBeaconLanded"/>; 0 on arrival.</summary>
    public float Delay { get; }
}

/// <summary>The extension surface other plugins use; see DEVELOPERS.md, "Extension API". Compatibility between add-ons and this
/// framework is the public surface itself (a plugin that calls something missing fails to load) and, between players, ForgeCore's
/// message format fingerprint on each plugin's channel.</summary>
public static class OrdnanceApi
{
    /// <summary>Fills <paramref name="into"/> with the parsed parameters (<see cref="OrdnanceDefinition.Parameters"/>) of every call-in of
    /// payload type <paramref name="payloadType"/> that can arrive in the selected level, each once: what a player in the lobby picked
    /// (this player included, as their lobby picks were last shared; none when the level turned the call menu off) and what the level's
    /// developer drops bring. Read only; allocates nothing once the list has room. Lets an add-on prepare for what this session can call in.</summary>
    public static void PickedParameters(string payloadType, List<object> into) => LobbySync.PickedParameters(payloadType, into);

    /// <summary>The answer of <see cref="PickedParameters"/> may have changed: a player's picks changed (in the lobby or in a level, where the
    /// lobby loadout page can be opened again) or the call menu rule did. Raised on the main thread; ask again and prepare for what is new.</summary>
    public static event Action? PickedParametersChanged;

    internal static void RaisePickedParametersChanged()
    {
        if (PickedParametersChanged == null) return;
        foreach (Action handler in PickedParametersChanged.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A PickedParametersChanged handler failed: " + error); }
        }
    }

    /// <summary>True only after current-level/current-recovery admission. The host may query any peer; a client
    /// knows only its own admission and that of its current host. Use this to gate add-on-owned network channels as well.</summary>
    public static bool IsPeerCompatible(ulong playerLookup) => Plugin.Session.IsPeerCompatible(playerLookup);

    /// <summary>The loaded definition with this <see cref="OrdnanceDefinition.Key"/> (the value the network carries). False before
    /// definitions are loaded or when none has the key. Every peer loads the same definitions, so a client, a late joiner and a
    /// reloaded checkpoint all find it.</summary>
    public static bool TryGetDefinition(uint key, out OrdnanceDefinition definition) => OrdnanceContent.Set.TryGet(key, out definition);

    /// <summary>The loaded definition with this <see cref="OrdnanceDefinition.Id"/>; same answer as <see cref="TryGetDefinition(uint, out OrdnanceDefinition)"/> for its key.</summary>
    public static bool TryGetDefinition(string id, out OrdnanceDefinition definition) => OrdnanceContent.Set.TryGetById(id, out definition);

    /// <summary>Every loaded definition, ordered by <see cref="OrdnanceDefinition.Id"/> (the same on every peer); empty before definitions are loaded.</summary>
    public static IReadOnlyList<OrdnanceDefinition> Definitions => OrdnanceContent.Set.Definitions;

    internal static readonly PayloadRegistry Registry = new();
    internal static readonly CategoryRegistry Categories = new(id => () => OrdnanceText.T("category." + id));
    private static readonly Dictionary<string, IOrdnancePayload> Payloads = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, IOrdnancePayload> ByKey = new();

    /// <summary>Largest <see cref="SendData"/> payload in bytes.</summary>
    public const int MaxDataBytes = Messages.MaxPayloadData;

    /// <summary>True once definition files have been read; payload types must be registered before that.</summary>
    public static bool DefinitionsLoaded { get; internal set; }

    /// <summary>Raised once on the main thread when the definition files have been read: <see cref="Definitions"/> and
    /// <see cref="TryGetDefinition(string, out OrdnanceDefinition)"/> answer from then on. Subscribe from your plugin's <c>Load</c>.</summary>
    public static event Action? DefinitionsReady;

    internal static void RaiseDefinitionsReady()
    {
        if (DefinitionsReady == null) return;
        foreach (Action handler in DefinitionsReady.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A DefinitionsReady handler failed: " + error); }
        }
    }

    /// <summary>Adds a payload type. Call it from your plugin's <c>Load</c> (which runs before game data, and therefore before
    /// definition files, are read). Returns false, with one error in the log, when the name is not namespaced (<c>author.Type</c>),
    /// taken or collides, or definitions are already loaded; definitions naming a refused type are then skipped with their own
    /// error. Players are matched by ForgeCore: the registered type names are part of this mod's handshake content, and the
    /// add-on's own messages by the fingerprint of its own channel.</summary>
    public static bool RegisterPayload(string type, IOrdnancePayload payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        var owner = payload.GetType().Assembly.GetName().Name ?? payload.GetType().FullName ?? "unknown plugin";
        return Register(type, payload, owner, true);
    }
    /// <summary>The built-in types (<c>Item</c>, <c>Gear</c>) are the only names without an author namespace.</summary>
    internal static void RegisterBuiltIn(string type, IOrdnancePayload payload)
    {
        if (!Register(type, payload, "Forge Ordnance", false)) throw new InvalidOperationException("Built-in payload '" + type + "' could not be registered.");
    }

    private static bool Register(string type, IOrdnancePayload payload, string owner, bool namespaced)
    {
        string? refusal = null;
        if (DefinitionsLoaded) refusal = "definitions are already loaded; register from your plugin's Load";
        else if (string.IsNullOrEmpty(type) || namespaced && !DefinitionParser.IsNamespacedId(type)) refusal = "the type name must be namespaced like author.Type";
        else if (Payloads.ContainsKey(type)) refusal = "the name is already registered";
        else if (ByKey.ContainsKey(ForgeCore.Fnv.Hash32(type))) refusal = "the name has the same network hash as another registered type; choose another name";
        if (refusal != null)
        {
            Plugin.Logger.Error("Payload type '" + type + "' from '" + owner + "' refused: " + refusal + ".");
            return false;
        }
        Registry.Register(type, payload);
        Payloads.Add(type, payload);
        ByKey.Add(ForgeCore.Fnv.Hash32(type), payload);
        return true;
    }

    /// <summary>Adds a lobby category that definitions may name in <c>Category</c>: the platform itself keeps Supply, Defensive,
    /// Weapon and Offensive. Call it from your plugin's <c>Load</c>, before definition files are read. <paramref name="name"/> gives the
    /// tab text in the player's language (called when the lobby is drawn); <paramref name="order"/> places the tab (the built-ins sit at
    /// 0, 1, 2 and 4); <paramref name="beamColour"/> (0xRRGGBB) is the default beam of its beacons. Returns false, with one error in the
    /// log, when the id is not a plain name of letters and digits, is taken, or definitions are already loaded. A category present on
    /// only one peer changes that peer's definition hash, so the handshake keeps the players apart.</summary>
    public static bool RegisterCategory(string id, Func<string> name, int order, uint beamColour)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        try
        {
            if (DefinitionsLoaded) throw new InvalidOperationException("definitions are already loaded; register from your plugin's Load");
            Categories.Register(id, name, order, beamColour);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Plugin.Logger.Error("Category '" + id + "' refused: " + error.Message.TrimEnd('.') + ".");
            return false;
        }
        return true;
    }

    /// <summary>Host only: sends up to <see cref="MaxDataBytes"/> bytes about one call to every peer; each peer's (and the host's
    /// own) <see cref="IOrdnancePayload.OnData"/> of <paramref name="payloadType"/> receives it, in send order. Use it for what
    /// the presentation needs beyond <see cref="OrdnancePresentation"/>: a seed, a spawned object's id, a state change.
    /// Late joiners do not receive earlier data; use <see cref="SendState"/> for what they must see.</summary>
    public static void SendData(string payloadType, ulong summonerLookup, uint throwId, byte tag, byte[] data)
        => Send(payloadType, summonerLookup, throwId, tag, data, false);

    private static bool Send(string payloadType, ulong summonerLookup, uint throwId, byte tag, byte[] data, bool retain)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (!ForgeCore.Session.IsHost) throw new InvalidOperationException("SendData is host only.");
        if (!Payloads.TryGetValue(payloadType, out var payload)) throw new ArgumentException("Payload type '" + payloadType + "' is not registered.", nameof(payloadType));
        var message = PayloadDataMessage.From(summonerLookup, throwId, ForgeCore.Fnv.Hash32(payloadType), tag, data);
        var kept = !retain || Retained.Set(message);
        if (!kept)
        {
            Plugin.Logger.ErrorOnce("Payload '" + payloadType + "' state for throw " + throwId + " refused: the host already keeps " + RetainedData.Capacity + " states; clear finished calls with ClearState. Nothing was dispatched.");
            return false;
        }
        OrdnanceNetwork.Broadcast(Messages.PayloadDataEvent, message);
        Deliver(payload, payloadType, new OrdnancePayloadData(summonerLookup, throwId, tag, (byte[])data.Clone(), false));
        return kept;
    }

    /// <summary>Host only: <see cref="SendData"/>, keeping this message before dispatch so callbacks may replace or clear it.
    /// The host keeps it as the call's current state for
    /// <paramref name="tag"/> (replacing what was kept for that tag) and replays it to every player who joins later, whose
    /// <see cref="IOrdnancePayload.OnData"/> then sees <see cref="OrdnancePayloadData.Replayed"/>. Send the whole state each
    /// time, not a change. Call <see cref="ClearState"/> when the arrival is gone. Kept state ends with the level, a checkpoint
    /// reload or a host change (a new host replays only what its own payload code sends again). False, with one error in the
    /// log, when the host already keeps <see cref="MaxKeptStates"/> states: nothing is dispatched. The caller must undo or
    /// refuse the associated world change on false. Capacity failure is atomic.</summary>
    public static bool SendState(string payloadType, ulong summonerLookup, uint throwId, byte tag, byte[] data)
        => Send(payloadType, summonerLookup, throwId, tag, data, true);

    /// <summary>Host only: forgets every state kept with <see cref="SendState"/> for one call of <paramref name="payloadType"/>;
    /// players who join afterwards see nothing of it.</summary>
    public static void ClearState(string payloadType, ulong summonerLookup, uint throwId)
    {
        if (!ForgeCore.Session.IsHost) throw new InvalidOperationException("ClearState is host only.");
        Retained.Clear(ForgeCore.Fnv.Hash32(payloadType), summonerLookup, throwId);
    }

    /// <summary>Host only: a call that found nothing to do gives its use back and keeps only <paramref name="cooldownSeconds"/> of its cooldown (never more than it had);
    /// every peer's list shows the change.</summary>
    public static void RefundCall(ulong summonerLookup, OrdnanceDefinition definition, double cooldownSeconds)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (!ForgeCore.Session.IsHost) throw new InvalidOperationException("RefundCall is host only.");
        Plugin.Session.RefundCall(summonerLookup, definition.Key, cooldownSeconds);
    }

    /// <summary>Shows <paramref name="text"/> to this player on the call-in list, with the list's notice sound.</summary>
    public static void PostNotice(string text) => Plugin.Session.PostNotice(text);

    /// <summary>The most states <see cref="SendState"/> keeps at once on the host.</summary>
    public const int MaxKeptStates = RetainedData.Capacity;

    /// <summary>The world epoch ForgeOrdnance follows (the host mints one for each level start, checkpoint reload and host change; clients adopt it);
    /// 0 while no world is up. An add-on stamps its own request messages with it and drops those of another value, as ForgeOrdnance's own messages are.</summary>
    public static ulong WorldEpoch => OrdnanceNetwork.Epoch;

    /// <summary>The name of <paramref name="definition"/> in the player's current language: its folder's <c>&lt;Id&gt;.Name</c> text, else
    /// the file's <c>Name</c>. Use it wherever you show a call-in (a HUD line, a prompt); do not read <see cref="OrdnanceDefinition.Name"/>.</summary>
    public static string DisplayName(OrdnanceDefinition definition)
        => definition == null ? throw new ArgumentNullException(nameof(definition)) : OrdnanceContent.Name(definition);

    internal static readonly Dictionary<string, OrdnanceLevelRules> DeclaredLevels = new(StringComparer.Ordinal);

    /// <summary>Gives the level whose expedition key is <paramref name="levelKey"/> its ordnance rules from code, as an add-on's own
    /// level would write them in <c>Settings.forge</c> <c>Levels.&lt;key&gt;</c> (turn the call menu off, drop a call-in at level start).
    /// Call it from your plugin's <c>Load</c>, before definition files are read; it is merged with <c>Settings.forge</c> into one effective
    /// setting and checked the same way. <paramref name="rules"/> is the whole rule set of the level (fields you leave out have
    /// their defaults; nothing is inherited from the file's global rules). Returns false, with one error in the log, when
    /// the rules are invalid, the key is already declared by another call, or definitions are already loaded. A key that
    /// <c>Settings.forge</c> also sets is reported when definitions load and the file wins.</summary>
    public static bool DeclareLevel(string levelKey, OrdnanceLevelRules rules)
    {
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        // A copy, lists included: what the caller changes afterwards must not change the level (drops are immutable records).
        rules = rules with
        {
            Allowed = rules.Allowed == null ? null : System.Linq.Enumerable.ToArray(rules.Allowed),
            // A drop's Overrides dictionary is the caller's too: the scheduler keeps what was checked here, not the caller's later edits.
            Drops = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(rules.Drops ?? Array.Empty<OrdnanceLevelDrop>(),
                drop => drop.Overrides == null ? drop : drop with { Overrides = new Dictionary<string, double>(drop.Overrides, StringComparer.Ordinal) })),
            Loadouts = System.Linq.Enumerable.ToDictionary(rules.Loadouts ?? AuthoredLoadouts.None, e => e.Key, e => (IReadOnlyList<string>)System.Linq.Enumerable.ToArray(e.Value), StringComparer.Ordinal)
        };
        var errors = new List<string>();
        var where = "DeclareLevel " + levelKey;
        if (DefinitionsLoaded) errors.Add(where + ": definitions are already loaded; declare levels from your plugin's Load.");
        else if (string.IsNullOrWhiteSpace(levelKey)) errors.Add("DeclareLevel: the level key must not be empty.");
        else if (DeclaredLevels.ContainsKey(levelKey)) errors.Add(where + ": this level is already declared.");
        else OrdnanceSettings.Check(rules, where, errors);
        foreach (var error in errors) Plugin.Logger.Error(error);
        if (errors.Count != 0) return false;
        DeclaredLevels.Add(levelKey, rules);
        return true;
    }

    /// <summary>Host only, in a level: brings the call-in <paramref name="ordnanceId"/> in at <paramref name="position"/> now, facing
    /// <paramref name="yaw"/> degrees, as <paramref name="arrival"/> (<see cref="OrdnanceArrivalMode.Direct"/> needs a payload with
    /// <see cref="IOrdnancePayload.SupportsDirect"/>). It goes through the same delivery as the level's <c>Drops</c>, so every player
    /// sees it. Pass <paramref name="dimension"/> to require the point to be in that dimension; the default takes whichever contains
    /// it. A client gets false and a <paramref name="reason"/> without a log line (test <c>ForgeCore.Session.IsHost</c> first); any other refusal
    /// is false, a reason and one error in the log. What it places is not placed again after a checkpoint reload or a host change.
    /// <paramref name="overrides"/> sets numbers on this one placement: only the fields the payload type declares
    /// (<see cref="IOrdnancePayload.OverrideFields"/>: a heavy weapon's <c>AmmoRatio</c>, an exosuit's <c>Armor</c>), inside their ranges; anything else
    /// refuses the call with a reason naming the field.</summary>
    public static bool Place(string ordnanceId, Vector3 position, float yaw, OrdnanceArrivalMode arrival, out string reason, int dimension = -1,
        IReadOnlyDictionary<string, double>? overrides = null)
        => Plugin.Session.Place(ordnanceId, position, yaw, arrival, dimension, overrides, out reason);

    /// <summary>The problem with <paramref name="overrides"/> for <paramref name="definition"/>'s payload type (a field it does not declare,
    /// a value out of range, a payload-specific refusal), or null when they are acceptable. The same check settings loading and <see cref="Place"/> make.</summary>
    public static string? CheckOverrides(OrdnanceDefinition definition, IReadOnlyDictionary<string, double> overrides)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (overrides == null) throw new ArgumentNullException(nameof(overrides));
        if (!Payloads.TryGetValue(definition.PayloadType, out var payload)) return "payload type " + definition.PayloadType + " is not registered";
        return OverrideCheck.Check(payload.OverrideFields, overrides, definition.PayloadType) ?? payload.CheckOverrides(definition.Parameters, overrides);
    }

    /// <summary>Holds the local player's call key off until the returned block is disposed, e.g. while piloting an exosuit: a
    /// press is ignored (silently: a blocked key is no error; <paramref name="reason"/> names the holder), and a code being entered or a ball being held is cancelled with
    /// the inventory put back. Blocks from several plugins add up; calls work again when none is left. Blocks outlive levels,
    /// so dispose yours when its reason ends. Main thread only.</summary>
    public static OrdnanceCallBlock BlockCalls(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Give a reason: who blocks the calls.", nameof(reason));
        var block = new OrdnanceCallBlock(reason);
        Blocks.Add(block);
        return block;
    }

    /// <summary>True while any <see cref="BlockCalls"/> block exists.</summary>
    public static bool CallsBlocked => Blocks.Count != 0;

    internal static readonly HashSet<OrdnanceCallBlock> Blocks = new();
    internal static readonly RetainedData Retained = new();

    internal static bool TryGetPayload(string type, out IOrdnancePayload payload) => Payloads.TryGetValue(type, out payload!);

    internal static bool TryGetPayload(uint key, out IOrdnancePayload payload) => ByKey.TryGetValue(key, out payload!);

    internal static bool SupportsDirect(string type) => Payloads.TryGetValue(type, out var payload) && payload.SupportsDirect;

    internal static void Deliver(IOrdnancePayload payload, string what, OrdnancePayloadData data)
    {
        try { payload.OnData(data); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Payload '" + what + "' OnData (tag " + data.Tag + ") failed: " + error); }
    }
}
