using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeCore;

namespace ForgeEngine.Framework;

/// <summary>
/// The one option block every trigger entry point may carry, and the state those options need at runtime.
///
/// These are the authoring panel's own trigger options (plan §3.4 统一选项): a card's trigger face is
/// the same face whatever event stands behind it, so the options belong to the entry point, not to a binding —
/// every trigger entry is gated in exactly one place, <see cref="RuntimeKernel"/>'s dispatch loop, and no trigger
/// provider implements a counter, a clock or a die of its own.
///
/// The block is written in the plan file as `entrypoints[].gate`, a structural object the loader resolves once and
/// refuses at load. Nothing in it is read from an event payload, so a plan without one keeps the behaviour it had:
/// an entry with no gate walks every dispatch it claims.
///
/// Host authority is structural here rather than a policy. The table below is only touched from a host advance,
/// and what a client sees is the effect of a decision the host already made, so the gate state never travels on
/// the wire: a client neither charges a threshold nor draws a random number, and never needs the table.
/// </summary>
internal static class TriggerGateContract
{
    /// <summary>The scope spellings the accounting may carry. `player` is the per-player reading: the threshold, the
    /// cap and the clock are charged against the player the event is about, so two players have two of each. `level`
    /// is the whole-card reading, one set for the level whatever the event names. `instance` is the per-host
    /// reading: one set per mounting entity, which is how "each enemy counts its own hits" is written. `shot` is
    /// the per-shot reading: one set per shot the triggering fact belongs to, which is how "at most N times inside
    /// one shot" is written.</summary>
    public const string PlayerScope = "player";
    public const string LevelScope = "level";
    public const string InstanceScope = "instance";
    public const string ShotScope = "shot";

    /// <summary>The scopes a gate may name, as the compiler spells them (plan §3.4 统一选项).</summary>
    internal static readonly string[] Scopes = { PlayerScope, LevelScope, InstanceScope, ShotScope };

    /// <summary>
    /// One entry point's `gate` block, or null where the entry declares none. The block is optional and its own
    /// fields are all optional, so a card that leaves an option alone writes nothing for it and the loader fills in
    /// the two defaults the authoring panel shows: no reset delay, a per-player cooldown scope.
    ///
    /// Each option refuses with its own code, because a rejected plan has to name the option that is wrong: one
    /// code per field is what makes a refusal actionable for whoever wrote the card. Anything else in the block is
    /// `unknown-field`, and a block that names no option at all is `gate-empty` rather than a no-op the loader keeps.
    /// </summary>
    internal static TriggerGateOptions? Parse(JsonElement entry, IReadOnlyList<PlanAttachment> attachments,
        JsonElement triggerContract)
    {
        if (!entry.TryGetProperty("gate", out var gate) || gate.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(gate.ValueKind == JsonValueKind.Object, "gate-shape", "gate");
        // Every option is optional, so the shape check is the unknown-field half alone; the at-least-one half is the
        // options record's own emptiness, which is also what the loader would have to answer anyway.
        RuntimeJson.Shape(gate, "", string.Join(' ', gateFields));
        var options = new TriggerGateOptions(
            Count(gate, "accumulate"), Amount(gate), Offset(gate), Delay(gate, "reset_delay"),
            Count(gate, "max_count"), Count(gate, "cooldown"), Debounce(gate), Scope(gate), Probability(gate), Once(gate))
        { SkipCount = Count(gate, "skip_count"), Source = Source(gate) };
        // A threshold below one is the option that is not a threshold: it would fire on the arrival that reached it
        // and on every arrival after, which is what leaving the option out already means. Each option's own value is
        // judged before anything is asked about the block as a whole, so a card that wrote a number wrong is told
        // that rather than told about a scope.
        RuntimeJson.Require(options.Accumulate is not { } threshold || threshold >= 1, "gate-count", "accumulate");
        // An amount source is only a source for a window it can fill, and a head start is only a head start inside
        // the window it starts: both are refused where they name no threshold, instead of being kept as fields
        // nothing would ever read.
        RuntimeJson.Require(options.AccumulateAmount == null || options.Accumulate != null, "gate-amount",
            "accumulate_amount needs accumulate: there is no window to fill without a threshold.");
        RuntimeJson.Require(options.ThresholdOffset == 0 || options.Accumulate != null, "gate-offset",
            "threshold_offset needs accumulate: a head start only means something inside a window.");
        RuntimeJson.Require(options.Accumulate is not { } window || options.ThresholdOffset < window, "gate-offset",
            "threshold_offset must be smaller than accumulate: a start at or above the threshold is a fire.");
        // The scope is the subject the entry count, threshold, cap and clock are booked against, so it is required by
        // exactly the options that are booked and forbidden to the ones that are not: a card that names no scoped
        // option and a card that names one without a scope are both refused, because the runtime does not guess
        // which subject an author meant (plan §3.4 统一选项: 编译器按卡种写默认值，运行时不猜). Asked before the
        // emptiness check, because a block that names the scope alone has named something and its own refusal is the
        // actionable one.
        var scoped = options.Accumulate != null || options.SkipCount != null || options.MaxCount != null || options.Cooldown != null;
        RuntimeJson.Require(scoped == (options.Scope != null), "gate-scope", scoped
            ? "A gate with accumulate, skip_count, max_count or cooldown must name the scope those options are booked against."
            : "scope needs accumulate, skip_count, max_count or cooldown: there is nothing to book against a subject.");
        // A cooldown of zero is kept as written: the clock check below never refuses an arrival with it (the card fires on
        // every event), which is what the author asked for; nothing in the gate needs a positive window.
        RuntimeJson.Require(!options.IsDefault || options.Source != null, "gate-empty", "A gate block must name at least one option.");
        // A per-instance gate is booked against the entity a mount target accepted, so a plan whose mounts take no
        // subject — `level` alone — has no instance to count and is refused rather than silently counting the level.
        RuntimeJson.Require(options.Scope != InstanceScope || attachments.Any(a => a.Kind != "level"), "gate-scope",
            "scope `instance` needs a mount target that accepts an entity; this plan mounts on the level alone.");
        // A per-shot gate is booked against the shot the triggering fact belongs to, and only a trigger whose own
        // fact carries that identity has one. A card that declares none is refused here, at load, by name: it is
        // not booked against the mount instance, because a shot is not the entity the card is mounted on.
        RuntimeJson.Require(options.Scope != ShotScope || DeclaresShotIdentity(triggerContract), "gate-scope",
            "scope `shot` needs a trigger whose fact carries a shot identity; this trigger's fact carries none.");
        return options;
    }

    /// <summary>Whether the trigger capability this entry point binds declares that its own fact belongs to one
    /// shot. The declaration is the trigger row's (`graph.factIdentity`), because the provider that publishes the
    /// fact is the only side that knows, and the plan carries the row it resolved.</summary>
    private static bool DeclaresShotIdentity(JsonElement triggerContract)
        => triggerContract.ValueKind == JsonValueKind.Object
           && triggerContract.TryGetProperty("factIdentity", out var identity)
           && identity.ValueKind == JsonValueKind.String
           && identity.GetString() == RuntimeGraphContracts.ShotFactIdentity;

    private static readonly string[] gateFields =
    {
        "accumulate", "accumulate_amount", "threshold_offset", "reset_delay", "skip_count", "max_count", "cooldown", "debounce",
        "scope", "probability", "once", "source"
    };

    /// <summary>The objects whose events this entry receives: a non-empty list of distinct map object addresses (the
    /// same spelling a trigger's own object address uses). It only narrows which events reach the entry, so it books
    /// nothing and needs no scope. The website expands a group into its members before export.</summary>
    private static string[]? Source(JsonElement gate)
    {
        if (!gate.TryGetProperty("source", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0, "gate-source",
            "source must be a non-empty list of object addresses.");
        var addresses = new string[value.GetArrayLength()];
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            RuntimeJson.Require(item.ValueKind == JsonValueKind.String && item.GetString()!.Split('/').All(part => part.Length > 0),
                "gate-source", "source names an object address that is not one.");
            addresses[index++] = item.GetString()!;
        }
        RuntimeJson.Require(addresses.Distinct(StringComparer.Ordinal).Count() == addresses.Length, "gate-source",
            "source names one object twice.");
        return addresses;
    }

    /// <summary>One non-negative count option. An explicit null is the same as leaving the field out, which is how
    /// an authoring panel writes a control it has not filled in.</summary>
    private static long? Count(JsonElement gate, string name)
    {
        if (!gate.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-count", name);
        return (long)value.GetDouble();
    }

    /// <summary>The event output port whose number one activation contributes, or null to count the activation
    /// itself. A port name is not a number, so a misspelled one is a load refusal here; a port the matched event
    /// does not carry is refused when the gate is charged, because only then is the event known.</summary>
    private static string? Amount(JsonElement gate)
    {
        if (!gate.TryGetProperty("accumulate_amount", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
            "gate-amount", "accumulate_amount must name an event output port.");
        return value.GetString();
    }

    /// <summary>The head start the first window gets: a non-negative amount that a window of this size still needs
    /// something more to reach.</summary>
    private static long Offset(JsonElement gate)
    {
        if (!gate.TryGetProperty("threshold_offset", out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-offset", "threshold_offset must be a whole number of at least zero.");
        return (long)value.GetDouble();
    }

    /// <summary>The reset delay, which is a duration rather than a count and so carries the delay's own code.</summary>
    private static long Delay(JsonElement gate, string name)
    {
        if (!gate.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 0 and <= RuntimeJson.MaxSafeInteger,
            "gate-reset-delay", name);
        return (long)value.GetDouble();
    }

    /// <summary>The quiet window a debounce release waits for. A window below one tick is not a window: it would
    /// release on the arrival itself, which is what leaving the option out already means. It carries its own code
    /// because it is a duration rather than a count.</summary>
    private static long? Debounce(JsonElement gate)
    {
        if (!gate.TryGetProperty("debounce", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number == Math.Truncate(number) && number is >= 1 and <= RuntimeJson.MaxSafeInteger,
            "gate-debounce", "debounce must be a whole number of ticks of at least one.");
        return (long)value.GetDouble();
    }

    /// <summary>The scope the gate's accounting is booked against, or null where the block names none. The spelling
    /// is the compiler's own; the runtime never fills in a default.</summary>
    private static string? Scope(JsonElement gate)
    {
        if (!gate.TryGetProperty("scope", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var scope = RuntimeJson.Text(value);
        RuntimeJson.Require(TriggerGateContract.Scopes.Contains(scope, StringComparer.Ordinal), "gate-scope", scope);
        return scope;
    }

    private static double? Probability(JsonElement gate)
    {
        if (!gate.TryGetProperty("probability", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsFinite(number) && number is > 0 and <= 1, "gate-probability", "probability");
        return value.GetDouble();
    }

    private static bool Once(JsonElement gate)
    {
        if (!gate.TryGetProperty("once", out var value) || value.ValueKind == JsonValueKind.Null) return false;
        RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "gate-once", "once");
        return value.ValueKind == JsonValueKind.True;
    }
}

/// <summary>
/// One entry point's resolved trigger options. Every option is absent by default, and an absent option is a check
/// that does not happen: an entry with no <see cref="Accumulate"/> fires on the first matching event, one with no
/// <see cref="MaxCount"/> never runs out, one with no <see cref="Cooldown"/> never waits, one with no
/// <see cref="Probability"/> always fires and one without <see cref="Once"/> does not latch.
///
/// <see cref="Accumulate"/> is the threshold one window has to reach and <see cref="ResetDelay"/> is the gap that
/// ends a window: an entry that needs three arrivals and allows no gap longer than ten ticks forgets what it had
/// after eleven quiet ticks and starts again from one. The threshold is charged in full for every window —
/// reaching it fires the entry, which empties what it had — so a window is the span between two activations.
///
/// <see cref="AccumulateAmount"/> says what one activation contributes: the number in the event output port it
/// names, or, without it, the activation itself, which is the "count or amount" the option offers. The amount is
/// read from the matched event's own outputs, so a card that accumulates damage accumulates the damage the event
/// carried rather than a number the plan restated.
///
/// <see cref="ThresholdOffset"/> is a head start, and the first window's alone: once a window ends, whether by
/// firing or by the reset delay, the next one starts at zero and needs the whole threshold.
/// <see cref="SkipCount"/> rejects the first arrivals booked to this subject, then stays open for every later
/// arrival. It counts at the entry point before the other checks, so a skipped arrival does not charge a threshold,
/// clock or random draw and changing the event source's subject filter is how a card chooses which entity kinds count.
/// <see cref="MaxCount"/> is a separate budget — how many times the entry may ever fire — and <see cref="Once"/>
/// is that budget being one, which is the whole of the option's difference from a cap of one.
///
/// <see cref="Scope"/> is which subject the four booked options are charged against, and it is required by
/// whichever of them is declared: `player` books one set per player, `level` one set for the whole level, and
/// `instance` one set per mounting entity. The runtime does not default it — the compiler writes the card's own
/// default — so a block that names a booked option without a scope never loads.
///
/// <see cref="Debounce"/> is the one option that delays instead of refusing: the arrival that reaches it starts a
/// quiet window of that many ticks and is refused with `gate-debounce`, and the card is released once — by the
/// kernel's own tick pass, never by the next event — when the window has passed without another arrival. A later
/// arrival inside the window moves the window's end to itself, which is what makes the release happen `debounce`
/// ticks after the last arrival. With <see cref="Accumulate"/> the threshold is judged first: a window that has not
/// reached it starts no timer (the arrival is still counted), and only an arrival that reaches the threshold — or
/// one that arrives while a release already waits — starts or moves the quiet clock.
/// </summary>
internal sealed record TriggerGateOptions(
    long? Accumulate = null, string? AccumulateAmount = null, long ThresholdOffset = 0, long ResetDelay = 0,
    long? MaxCount = null, long? Cooldown = null, long? Debounce = null, string? Scope = null,
    double? Probability = null, bool Once = false)
{
    /// <summary>How many arrivals this entry refuses before the rest of its gate is evaluated.</summary>
    public long? SkipCount { get; init; }

    /// <summary>The map objects whose events this entry receives, or null for every object. It is matched with the
    /// entry's own object address at dispatch, before anything is booked, so it is not part of <see cref="IsDefault"/>.</summary>
    public string[]? Source { get; init; }

    /// <summary>Whether this block asks for anything at all. A block that names no check is refused at load rather
    /// than kept as a no-op, so a plan never carries a gate object that means nothing. The two threshold parts
    /// count as naming something even where they stand alone: their refusal is then the actionable one — they
    /// belong to a window that is not there — instead of a block that is called empty for having named them.</summary>
    public bool IsDefault => Accumulate == null && AccumulateAmount == null && ThresholdOffset == 0 && SkipCount == null && MaxCount == null
        && Cooldown == null && Debounce == null && Probability == null && !Once;

    /// <summary>The number of times this entry may ever fire, or null while it may fire forever.</summary>
    internal long? Cap => Once ? 1 : MaxCount;
}

/// <summary>One entry point's refusal, as a receipt reports it: the entry that was refused, the check that refused
/// it, and how far that entry had got — the two numbers the "why did my trigger not fire" question is asked about.</summary>
public sealed record TriggerGateReceipt(string PlanId, string NodeId, string Code, long Accumulated, long Fired);

/// <summary>What one arrival at a gated entry point answered. <see cref="Admitted"/> is the card running now,
/// <see cref="Deferred"/> is a `debounce` window that was opened or moved — the arrival itself is refused, and the
/// release is the kernel's timed dispatch — and a verdict that is neither is an ordinary refusal, which the refusal
/// list names. <see cref="Generation"/> is how many times the deferred window has moved, which is what tells a
/// release already queued from the window the card now waits for.</summary>
internal readonly record struct TriggerGateVerdict(bool Admitted, bool Deferred, long Generation)
{
    internal static TriggerGateVerdict Ran() => new(true, false, 0);
    internal static TriggerGateVerdict Refused() => new(false, false, 0);
}

/// <summary>One quiet window that has passed: the entry point it belongs to, the subject set it was booked against,
/// which move of the window it is, and the arrival the kernel captured when it opened — which is null exactly when
/// <see cref="Refusal"/> says the window was dropped instead of released.</summary>
internal sealed record TriggerGateRelease(string PlanId, string NodeId, string Subject, long Generation,
    object? Payload, TriggerGateReceipt? Refusal);

/// <summary>
/// The host's gate state: one row per gated entry point, each carrying one booked set per scope subject — a player,
/// the level, or a mounting entity. This is a second table rather than another variable scope because nothing here
/// is authored, named or read by a plan — a plan's own variables are the author's, and a counter the framework
/// keeps for a trigger is not.
///
/// A row's lifecycle follows the entry point's: a released plan forgets its rows, a checkpoint keeps them (a
/// threshold half reached is progress the level made, not a fact of the machine that made it), and an entry with
/// no gate owns no row at all. A per-instance row's entries are dropped when the level's objects are rebuilt —
/// a restored checkpoint re-creates them — and an instance whose entity is already gone when it is next asked
/// about is dropped at that question rather than counted as a stranger's history.
/// </summary>
internal sealed class RuntimeTriggerGateStore
{
    private const int MaximumScopesPerGate = 1024;
    private const int MaximumRows = 65536;
    private const long MaximumCounter = 9007199254740991;

    /// <summary>One subject's booked state: the skipped count, the threshold, the cap and the clock of exactly one player, of the
    /// level, or of one mounting entity. Everything an option counts lives here, so the scope is not a property of
    /// the cooldown alone — a card whose threshold is per-enemy keeps one window per enemy as well.</summary>
    private sealed class Scope
    {
        /// <summary>Arrivals already refused by <c>skip_count</c>. It stops changing once that gate is open.</summary>
        internal long Skipped;
        /// <summary>Arrivals counted towards the current threshold, including the first window's head start.</summary>
        internal long Accumulated;
        /// <summary>How many times the entry has fired. Also the cap's counter, so `once` is a cap of one.</summary>
        internal long Fired;
        /// <summary>The tick of the last counted arrival, and whether there has ever been one: a gap longer than the
        /// reset delay forgets what was accumulated instead of adding to it. The flag is a lifetime latch that a
        /// reset never clears, so it is also what keeps the head start to the first window.</summary>
        internal long LastArrival;
        internal bool Started;
        /// <summary>The tick this subject's clock was charged at.</summary>
        internal long Charged = long.MinValue;
        /// <summary>How many draws the deterministic die has handed out, so a draw is a function of the world and
        /// the row's own history rather than of how the process was scheduled.</summary>
        internal long Rolls;
        /// <summary>For an instance scope, the entity this set is booked against: its identity is the key, and the
        /// reference is what says whether that entity is still alive.</summary>
        internal EntityReference? Entity;
        /// <summary>Whether a `debounce` release is waiting for this subject's quiet window to pass, the tick that
        /// window ends in, and how many times it has moved. The number is what tells a release the kernel has
        /// already queued from the current window: a later arrival moves the deadline and the queued entry names a
        /// generation that is no longer the set's.</summary>
        internal bool ReleaseArmed;
        internal long ReleaseAtTick;
        internal long ReleaseGeneration;
        /// <summary>Whether the kernel has already taken this window's release into its queue. The set stays its
        /// address until a new arrival opens the next window, which is what keeps that queued entry current.</summary>
        internal bool ReleaseTaken;
        /// <summary>What the armed release has to re-run: the arrival that opened the window, kept by the kernel as
        /// its own dispatch and never read here. It goes with the set, so a released plan, a rebuilt level and an
        /// ended world drop the release with the counter it belongs to.</summary>
        internal object? Release;
    }

    private sealed class Row
    {
        /// <summary>The booked sets by scope subject: the entity id of a player or an instance, or the empty
        /// string for the level.</summary>
        internal readonly Dictionary<string, Scope> Scopes = new(StringComparer.Ordinal);

        /// <summary>One booked set, or null where that subject has never been asked about.</summary>
        internal Scope? Find(string key) => Scopes.TryGetValue(key, out var scope) ? scope : null;
    }

    /// <summary>
    /// One gate row's identity: the plan that declares the entry point and the entry point's node. It is a tuple
    /// rather than one joined string because a plan id may contain any printable text — dots and colons included —
    /// so no delimiter can be assumed to separate the two halves. The only place the halves are joined is the wire
    /// document, and there they are joined and split by <see cref="KeyOf"/> and <see cref="SplitKey"/> alone.
    /// </summary>
    private readonly record struct GateKey(string Plan, string Node);
    private readonly Dictionary<GateKey, Row> rows = new();

    /// <summary>
    /// The one canonical spelling of a gate row's identity, used by every save and restore boundary. The separator
    /// is the node id grammar's own guarantee: a node id is <c>[A-Za-z][A-Za-z0-9_-]*</c> and can never contain a
    /// dot, so the last dot in the text is always the separator and a plan id that contains dots — or any other
    /// printable text — reads back exactly as it was written.
    /// </summary>
    private static string KeyOf(string planId, string nodeId) => planId + "." + nodeId;

    /// <summary>The halves of one canonical key, or null where the text is not one: the split is from the right,
    /// because that is the end the node id's own grammar makes unambiguous.</summary>
    private static GateKey? SplitKey(string key)
    {
        var separator = key.LastIndexOf('.');
        if (separator <= 0 || separator == key.Length - 1) return null;
        return new GateKey(key[..separator], key[(separator + 1)..]);
    }
    /// <summary>The plans whose entry points declare a gate at all, and by which node. A plan outside this table
    /// pays one dictionary miss per dispatch and nothing else, which is the whole cost of the feature for a card
    /// that does not use it.</summary>
    private readonly Dictionary<string, Dictionary<string, TriggerGateOptions>> gated = new(StringComparer.Ordinal);

    /// <summary>This world's die seed, drawn once by the host when the world begins and carried by every
    /// checkpoint of it. The host is the only machine that rolls — a client never evaluates a gate — so the seed
    /// is host state like the counters beside it and never travels on the wire; a checkpoint restored on the
    /// machine that rolled keeps handing out the same sequence, which is what makes a probability option
    /// reproducible across a reload. Zero means the host has not begun a world yet, and no draw can happen before
    /// it has: a roll is a dispatch, and a dispatch only runs in an advance of a world that started.</summary>
    private ulong sessionSeed;
    /// <summary>Begins a world: the counters go (the world ended, not the plan) and the die takes its seed. An
    /// armed quiet window goes with them: its release re-runs an arrival of the world that ended.</summary>
    internal void BeginWorld(long seed)
    {
        rows.Clear();
        HasReleases = false;
        sessionSeed = unchecked((ulong)seed);
    }

    internal int Count => rows.Count;

    /// <summary>
    /// Registers one plan's gated entry points, replacing whatever the plan declared before. Entry points with no
    /// gate are dropped here. Dispatch reads the gate the loader resolved onto the entry itself
    /// (<c>ResolvedEntry.Gate</c>); this table serves the checkpoint and migration paths. A reload keeps the
    /// rows: the checkpoint, not the file, owns the counters.
    /// </summary>
    internal void Declare(string planId, IReadOnlyList<ResolvedEntry> entries)
    {
        var table = new Dictionary<string, TriggerGateOptions>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry.Gate is { IsDefault: false } gate) table[entry.NodeId] = gate;
        if (table.Count == 0) gated.Remove(planId); else gated[planId] = table;
    }

    /// <summary>Drops every row of one released plan and stops gating its entries: a released entry point is not a
    /// counter the world kept.</summary>
    internal void ForgetPlan(string planId)
    {
        if (!gated.Remove(planId)) return;
        // The row's own plan half is compared, not a prefix of a joined string: a plan whose id begins with this
        // one's id plus the separator is another plan and keeps its counters.
        foreach (var key in rows.Keys.Where(key => string.Equals(key.Plan, planId, StringComparison.Ordinal)).ToArray())
            rows.Remove(key);
    }

    /// <summary>Drops every row without forgetting which entries are gated: the world, not the plan, ended.</summary>
    internal void Clear() { rows.Clear(); HasReleases = false; }

    /// <summary>
    /// The one gate check, called once per (plan, entry) a dispatch claims, in plan and entry order. A refused
    /// entry is reported through <paramref name="refusals"/> and its work items are dropped; an accepted one has
    /// already charged its threshold, its cap and its clocks by the time this returns true, so the walk that
    /// follows sees only entries allowed to run.
    ///
    /// <param name="player"/> is the entity a per-player gate is booked against — the event's instigator, or
    /// the entity the plan's mount accepted when the event names no instigator.
    ///
    /// <param name="instance"/> is the entity a per-instance gate is booked against: the mounting entity this very
    /// activation was matched on. A scope whose subject this event cannot name is refused as `gate-scope` rather
    /// than silently sharing one set between everyone.
    ///
    /// <param name="shot"/> is the identity a per-shot gate is booked against: the shot the matched event's own
    /// scope names, which the trigger row declared its fact carries. An activation that names no shot is refused
    /// as `gate-scope` for the same reason an instance scope with no instance is.
    ///
    /// <paramref name="carried"/> is the number the matched event carries in the port the gate accumulates (null
    /// where it carries none there), and the only place an amount threshold can be read from: the number a card
    /// accumulates is the number the event carried.
    ///
    /// <paramref name="release"/> is what a `debounce` release would have to re-run — the kernel's own dispatch for
    /// this arrival — and is kept by the booked set while the quiet window is open. It is null for a gate that
    /// declares no debounce, so nothing is allocated for the option a card did not use.
    /// </summary>
    internal TriggerGateVerdict Admit(string planId, string nodeId, TriggerGateOptions options, long tick, EntityReference? player,
        EntityReference? instance, string? shot, Func<EntityReference, bool>? resolve, double? carried, object? release,
        List<TriggerGateReceipt> refusals)
    {
        var row = RowOf(planId, nodeId);
        var key = ScopeKey(options.Scope, player, instance, shot);
        if (key == null)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-scope", 0, 0));
            return TriggerGateVerdict.Refused();
        }
        var scoped = row.Find(key);
        // An instance whose entity is already gone is not a subject this world has: its booked set is dropped and
        // this arrival starts a fresh one, rather than adding to a counter that belonged to a dead enemy.
        if (scoped != null && options.Scope == TriggerGateContract.InstanceScope
            && scoped.Entity is { Id.Length: > 0 } && resolve != null && !Current(resolve, scoped.Entity))
        { row.Scopes.Remove(key); scoped = null; }
        if (scoped == null)
        {
            // The booked set is what a new subject costs, so the cap is asked of the subjects that still exist:
            // an instance subject whose entity is gone is dropped before the cap is read, because a level that has
            // been running long enough to outlive a thousand enemies must not be told its card is out of room by
            // a thousand dead ones. The sweep is the same judgement the arrival above makes about its own subject.
            if (row.Scopes.Count >= MaximumScopesPerGate && options.Scope == TriggerGateContract.InstanceScope && resolve != null)
                foreach (var gone in row.Scopes.Where(pair => pair.Value.Entity is { Id.Length: > 0 } && !Current(resolve, pair.Value.Entity))
                    .Select(pair => pair.Key).ToArray())
                    row.Scopes.Remove(gone);
            if (row.Scopes.Count >= MaximumScopesPerGate)
            {
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-scope", 0, 0));
                return TriggerGateVerdict.Refused();
            }
            scoped = new Scope { Entity = instance ?? default };
            row.Scopes.Add(key, scoped);
        }
        // `skip_count` is the entry check: the first K arrivals stop before another option is charged. The counter
        // stops at K, so a maximum-safe-integer K still opens on the next arrival instead of saturating shut.
        if (options.SkipCount is { } skipCount && scoped.Skipped < skipCount)
        {
            scoped.Skipped++;
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-skip-count", scoped.Accumulated, scoped.Fired));
            return TriggerGateVerdict.Refused();
        }
        // The cap is the cheapest remaining check and the only one that can never be undone, so it is asked next:
        // a spent entry does no threshold, clock or random bookkeeping.
        if (options.Cap is { } cap && scoped.Fired >= cap)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-max-count", scoped.Accumulated, scoped.Fired));
            return TriggerGateVerdict.Refused();
        }
        if (options.Cooldown is { } cooldown && scoped.Charged != long.MinValue && tick - scoped.Charged < cooldown)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-cooldown", scoped.Accumulated, scoped.Fired));
            return TriggerGateVerdict.Refused();
        }
        var thresholdMet = true;
        if (options.Accumulate is { } threshold)
        {
            // A refused arrival is not an arrival: an event that does not carry the port the gate named leaves the
            // window exactly as it was, so a later event that does carry one can still reach the threshold.
            if (!Contribution(options, carried, out var amount))
            {
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-amount", scoped.Accumulated, scoped.Fired));
                return TriggerGateVerdict.Refused();
            }
            // A reset delay of zero is the absence of one: the window is only ever forgotten when the plan asked for
            // a delay and the arrivals waited longer than it. Reading zero as "forget immediately" would make every
            // threshold of a plan that never named a delay unreachable.
            if (scoped.Started && options.ResetDelay > 0 && tick - scoped.LastArrival > options.ResetDelay) scoped.Accumulated = 0;
            // The head start is the first window's alone, which is what the lifetime latch already remembers: both
            // ways a window ends — firing, and the reset delay — leave the next one needing the whole threshold.
            if (!scoped.Started) scoped.Accumulated = options.ThresholdOffset;
            scoped.Started = true;
            scoped.LastArrival = tick;
            scoped.Accumulated = Math.Min(MaximumCounter, scoped.Accumulated + amount);
            thresholdMet = scoped.Accumulated >= threshold;
            if (thresholdMet) scoped.Accumulated = 0;
        }
        // The debounce never releases on an arrival: it opens — or moves — the quiet window the kernel's own tick
        // pass releases on. An arrival that reaches the threshold opens it; one that does not is the accumulate miss
        // it always was, unless a release is already waiting, which the arrival in the same window moves to itself.
        // Either way the arrival is refused here, and the release is a dispatch of its own at the quiet tick.
        if (options.Debounce is { } quiet)
        {
            if (thresholdMet || scoped.ReleaseArmed)
            {
                scoped.ReleaseArmed = true;
                scoped.ReleaseTaken = false;
                scoped.ReleaseAtTick = tick + quiet;
                scoped.ReleaseGeneration++;
                scoped.Release = release;
                HasReleases = true;
                refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-debounce", scoped.Accumulated, scoped.Fired));
                return new TriggerGateVerdict(false, true, scoped.ReleaseGeneration);
            }
        }
        if (!thresholdMet)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-accumulate", scoped.Accumulated, scoped.Fired));
            return TriggerGateVerdict.Refused();
        }
        if (options.Probability is { } probability && NextDraw(planId, nodeId, scoped) >= probability)
        {
            refusals.Add(new TriggerGateReceipt(planId, nodeId, "gate-probability", scoped.Accumulated, scoped.Fired));
            return TriggerGateVerdict.Refused();
        }
        // Every check passed, so this activation happens: the two things that can only be counted once it does are
        // charged here and nowhere earlier. A refused activation costs an event, never a clock.
        scoped.Fired++;
        if (options.Cooldown != null) scoped.Charged = tick;
        return TriggerGateVerdict.Ran();
    }

    /// <summary>Whether any booked set is waiting for a quiet window to pass. The kernel's per-tick pass reads this
    /// once before it walks anything, so a world whose cards declare no debounce pays one boolean.</summary>
    internal bool HasReleases { get; private set; }

    /// <summary>
    /// Every quiet window that has passed by <paramref name="tick"/>, charged and handed back as the release the
    /// kernel dispatches. A release is the card's own activation, so it is charged here, at the quiet tick: the cap,
    /// the clock and the die are asked again because the world moved on while the window was open — and a release
    /// the cap, the clock or the die refuses is handed back with the refusal it earned rather than kept for a later
    /// tick, so the tick's own refusal list explains a card that waited and then did not fire.
    ///
    /// <paramref name="capacity"/> is how many releases the caller can still queue this tick; a window that comes
    /// due beyond it stays armed and is taken by a later advance, so a world full of debounce cards cannot overrun
    /// the queue it shares with every other event.
    /// </summary>
    internal List<TriggerGateRelease> DueReleases(long tick, int capacity)
    {
        var due = new List<TriggerGateRelease>();
        if (!HasReleases) return due;
        var waiting = false;
        foreach (var entry in rows)
        {
            var planId = entry.Key.Plan;
            var nodeId = entry.Key.Node;
            if (!gated.TryGetValue(planId, out var table) || !table.TryGetValue(nodeId, out var options)) continue;
            foreach (var pair in entry.Value.Scopes)
            {
                var scoped = pair.Value;
                if (!scoped.ReleaseArmed) continue;
                if (scoped.ReleaseAtTick > tick) { waiting = true; continue; }
                if (due.Count >= capacity) { waiting = true; continue; }
                scoped.ReleaseArmed = false;
                scoped.ReleaseTaken = true;
                var refusal = options.Cap is { } cap && scoped.Fired >= cap ? "gate-max-count"
                    : options.Cooldown is { } cooldown && scoped.Charged != long.MinValue && tick - scoped.Charged < cooldown ? "gate-cooldown"
                    : options.Probability is { } probability && NextDraw(planId, nodeId, scoped) >= probability ? "gate-probability"
                    : null;
                if (refusal != null)
                {
                    due.Add(new TriggerGateRelease(planId, nodeId, pair.Key, scoped.ReleaseGeneration, null,
                        new TriggerGateReceipt(planId, nodeId, refusal, scoped.Accumulated, scoped.Fired)));
                    scoped.Release = null;
                    continue;
                }
                scoped.Fired++;
                if (options.Cooldown != null) scoped.Charged = tick;
                // A window whose arrived dispatch the kernel never handed over has nothing to release: the set is
                // simply left disarmed rather than released with a null frame.
                if (scoped.Release is not { } payload) continue;
                due.Add(new TriggerGateRelease(planId, nodeId, pair.Key, scoped.ReleaseGeneration, payload, null));
                scoped.Release = null;
            }
        }
        HasReleases = waiting;
        return due;
    }

    /// <summary>Whether one queued release still belongs to the window it was taken from: an arrival that opened
    /// the next window left the queued entry behind, and that entry is dropped instead of released at a tick the
    /// card no longer waits for. The four names are the queued entry's own address, so the kernel hands back what
    /// it recorded without this store knowing the kernel's ticket type.</summary>
    internal bool ReleaseCurrent(string planId, string nodeId, string subject, long generation)
    {
        if (!rows.TryGetValue(new GateKey(planId, nodeId), out var row)) return false;
        var scoped = row.Find(subject);
        return scoped != null && scoped.ReleaseGeneration == generation
            && (scoped.ReleaseArmed || scoped.ReleaseTaken);
    }

    /// <summary>
    /// The subject one booking is keyed by: the entity id of the player or the mounting instance, the empty string
    /// for the level, the shot identity for a per-shot gate, and null where the scope names a subject this
    /// activation cannot. The level's own set is keyed by an empty string rather than by a sentinel entity so a
    /// checkpoint writes it the same way it writes a player's. A shot identity is not an entity — it is the scope
    /// text the fact itself carries — so it is compared as the text it is and never parsed into one.
    ///
    /// A gate that books none of the scoped options — a roll, or a plain `once` — still owns one set: the
    /// entry point's own row, keyed like the level's. The set is not the level's scope, it is the absence of a
    /// subject, and nothing else may be handed it.
    /// </summary>
    private static string? ScopeKey(string? scope, EntityReference? player, EntityReference? instance, string? shot) => scope switch
    {
        null or TriggerGateContract.LevelScope => "",
        TriggerGateContract.PlayerScope => player == null ? null : player.Id,
        TriggerGateContract.InstanceScope => instance == null ? null : instance.Id,
        TriggerGateContract.ShotScope => string.IsNullOrEmpty(shot) ? null : shot,
        _ => null
    };

    /// <summary>
    /// What one arrival adds to the window: one activation, or the number the event carries in the output port the
    /// gate named. A missing port, a value that is not a whole non-negative number, and one too large for the
    /// counters are the same refusal, because from the gate's side they are the same problem — the amount the card
    /// asked for cannot be read, so counting the arrival as one would fire on a number nobody chose.
    /// </summary>
    private static bool Contribution(TriggerGateOptions options, double? carried, out long amount)
    {
        amount = 1;
        if (options.AccumulateAmount == null) return true;
        if (carried is not { } number) return false;
        if (!double.IsFinite(number) || number != Math.Truncate(number) || number < 0) return false;
        amount = number > MaximumCounter ? MaximumCounter : (long)number;
        return true;
    }

    /// <summary>Whether one booked entity is still part of this world: the question is put to the resolver that
    /// owns the entity's kind and to nobody else. A kind nothing resolves counts as current, exactly as an effect's
    /// own subject check reads it — this kernel only drops what it can prove is gone. A resolver that throws is the
    /// kernel's to record: the delegate it hands to <c>Admit</c> catches, logs and answers false.</summary>
    private static bool Current(Func<EntityReference, bool> resolve, EntityReference entity) => resolve(entity);

    private Row RowOf(string planId, string nodeId)
    {
        var key = new GateKey(planId, nodeId);
        if (rows.TryGetValue(key, out var row)) return row;
        RuntimeJson.Require(rows.Count < MaximumRows, "gate-budget", "Gate row capacity reached.");
        row = new Row(); rows.Add(key, row); return row;
    }

    /// <summary>
    /// One deterministic draw. The die is a pure function of the row's address, how many draws it has already
    /// made and the world's own seed, so the same plan in the same world hands out the same sequence: a
    /// probability option is reproducible in a replay, and a checkpoint carries the seed that makes it so. It is
    /// deliberately not one shared generator, because then one entry's draws would shift another's.
    /// </summary>
    private double NextDraw(string planId, string nodeId, Scope scope)
    {
        var seed = DeterministicRandom.Mix(DeterministicRandom.Mix(DeterministicRandom.Mix((ulong)scope.Rolls + 1, Fnv.MixUtf8(Fnv.Offset64, planId)),
            Fnv.MixUtf8(Fnv.Offset64, nodeId)), sessionSeed);
        scope.Rolls++;
        return DeterministicRandom.Unit(seed);
    }
    /// <summary>The whole host table as one JSON row list. Written by the host and read back by the host: this is
    /// the checkpoint's half of the gate state, never a message a client applies. Every booked set travels with the
    /// subject it belongs to, so a checkpoint taken with half a threshold reached puts that half back for the same
    /// player, the same enemy or the level.</summary>
    internal string Save() => SaveRows(CounterRow);

    /// <summary>The fields a checkpoint and a migration image both carry for one booked set. The text is written
    /// key-sorted (<see cref="RuntimeJson.StableText"/>), so the row's own order is not part of the format.</summary>
    private static Dictionary<string, object?> CounterRow(string subject, Scope scope) => new(StringComparer.Ordinal)
    {
        ["subject"] = subject,
        ["skipped"] = scope.Skipped,
        ["accumulated"] = scope.Accumulated,
        ["fired"] = scope.Fired,
        ["lastArrival"] = scope.LastArrival,
        ["started"] = scope.Started,
        ["charged"] = scope.Charged,
        ["rolls"] = scope.Rolls
    };

    private string SaveRows(Func<string, Scope, Dictionary<string, object?>> scopeRow)
    {
        var entries = OrderedRows().Select(pair => new
        {
            key = KeyOf(pair.Key.Plan, pair.Key.Node),
            scopes = pair.Value.Scopes.OrderBy(scope => scope.Key, StringComparer.Ordinal)
                .Select(scope => scopeRow(scope.Key, scope.Value)).ToArray()
        }).ToArray();
        return RuntimeJson.StableText(RuntimeJson.From(new { seed = unchecked((long)sessionSeed), rows = entries }));
    }

    /// <summary>
    /// The exact gate image paired with a native migration buffer. Unlike a checkpoint it retains instance
    /// subjects and an armed/taken debounce window: vanilla is recalling the same world, not rebuilding it.
    /// Deadlines are stored as remaining ticks so the replacement host continues from the captured simulation tick.
    /// </summary>
    internal string SaveMigration(long tick, Func<object, JsonElement> serializeRelease) => SaveRows((subject, scope) =>
    {
        var row = CounterRow(subject, scope);
        row["entity"] = scope.Entity;
        row["releaseArmed"] = scope.ReleaseArmed;
        row["releaseTaken"] = scope.ReleaseTaken;
        row["releaseRemainingTicks"] = scope.ReleaseArmed ? Math.Max(0, scope.ReleaseAtTick - tick) : 0L;
        row["releaseGeneration"] = scope.ReleaseGeneration;
        row["release"] = scope.Release == null ? null : serializeRelease(scope.Release);
        return row;
    });

    /// <summary>
    /// Restores a checkpoint's gate rows over the declarations that are loaded. A row is restored only where a
    /// loaded plan declares that exact entry point with a gate: a checkpoint names counters, and a counter whose
    /// address no longer exists is not put back where a card's next option would charge a stranger's history. A row
    /// for an entry point that is declared now but was not when the checkpoint was taken is simply absent, which is
    /// the same as never having fired.
    ///
    /// A per-instance row is the one thing a checkpoint does not carry back: restoring one re-creates the level's
    /// own objects, so a saved instance subject names an entity that no longer exists, exactly as a saved per-enemy
    /// variable does. The counter is dropped with the object it counted.
    ///
    /// The seed comes back with the rows: a checkpoint is the world's own die, so the sequence a probability option
    /// was drawing from continues where the save left it instead of restarting from a fresh draw.
    /// </summary>
    internal void Restore(string json)
    {
        rows.Clear();
        // An armed quiet window is not part of a checkpoint: it releases the arrival of the world that was saved,
        // and that frame is gone with it. The counters come back; the window a restored card would have waited for
        // does not, which is the same "the world, not the card, ended" reading the other rows use.
        HasReleases = false;
        var document = RuntimeJson.Parse(json);
        if (document.ValueKind == JsonValueKind.Null) { sessionSeed = 0; return; }
        RuntimeJson.Require(document.ValueKind == JsonValueKind.Object, "gate-checkpoint",
            "Gate state must be an object carrying the seed and the rows.");
        RuntimeJson.Shape(document, "", "seed rows");
        sessionSeed = document.TryGetProperty("seed", out var seed)
            ? unchecked((ulong)RuntimeJson.Integer(seed, long.MinValue, long.MaxValue)) : 0UL;
        var entries = RuntimeJson.Rows(document, "rows", MaximumRows);
        RuntimeJson.Require(entries.Length <= MaximumRows, "gate-checkpoint", "Too many gate rows.");
        foreach (var entry in entries)
        {
            if (ReadKey(entry) is not { } key) continue;
            if (!gated.TryGetValue(key.Plan, out var table) || !table.TryGetValue(key.Node, out var options)) continue;
            RuntimeJson.Shape(entry, "key scopes");
            var row = new Row();
            foreach (var scoped in RuntimeJson.Rows(entry, "scopes", MaximumScopesPerGate))
            {
                if (row.Scopes.Count >= MaximumScopesPerGate) break;
                RuntimeJson.Shape(scoped, "subject accumulated fired lastArrival started charged rolls", "skipped");
                if (SavedSubject(scoped, options.Scope, "gate-checkpoint") is not { } subject) continue;
                row.Scopes[subject] = SavedCounters(scoped);
            }
            rows[key] = row;
        }
    }

    /// <summary>The key one saved set is booked under, or null when that key does not match the scope this load
    /// declares and the set is dropped rather than restored onto another subject. The empty subject is the level's
    /// own set and an unscoped gate's single set, so it is the one legitimately empty string and is read here rather
    /// than through the shared text rule; every other key is an entity id, validated as one, and only a per-player
    /// or a per-instance gate is booked against one.</summary>
    private static string? SavedSubject(JsonElement scoped, string? declaredScope, string code)
    {
        var subjectValue = scoped.GetProperty("subject");
        RuntimeJson.Require(subjectValue.ValueKind == JsonValueKind.String, code, "subject");
        var subject = subjectValue.GetString()!;
        if (subject.Length == 0) return declaredScope is null or TriggerGateContract.LevelScope ? subject : null;
        RuntimeJson.Text(subject);
        return declaredScope is TriggerGateContract.PlayerScope or TriggerGateContract.InstanceScope ? subject : null;
    }

    /// <summary>The counters a checkpoint and a migration image both carry for one booked set.</summary>
    private static Scope SavedCounters(JsonElement scoped) => new()
    {
        Skipped = scoped.TryGetProperty("skipped", out var skipped) ? RuntimeJson.Integer(skipped, 0, MaximumCounter) : 0,
        Accumulated = RuntimeJson.Integer(scoped.GetProperty("accumulated"), 0, MaximumCounter),
        Fired = RuntimeJson.Integer(scoped.GetProperty("fired"), 0, MaximumCounter),
        LastArrival = RuntimeJson.Integer(scoped.GetProperty("lastArrival"), long.MinValue, long.MaxValue),
        Started = RuntimeJson.Flag(scoped, "started"),
        Charged = RuntimeJson.Integer(scoped.GetProperty("charged"), long.MinValue, long.MaxValue),
        Rolls = RuntimeJson.Integer(scoped.GetProperty("rolls"), 0, MaximumCounter)
    };

    /// <summary>Restores the exact migration-only gate image over the unchanged loaded plan set.</summary>
    internal void RestoreMigration(string json, long tick, Func<JsonElement, object?> deserializeRelease)
    {
        rows.Clear();
        HasReleases = false;
        var document = RuntimeJson.Parse(json);
        RuntimeJson.Require(document.ValueKind == JsonValueKind.Object, "gate-migration",
            "Gate migration state must carry the seed and rows.");
        RuntimeJson.Shape(document, "seed rows");
        sessionSeed = unchecked((ulong)RuntimeJson.Integer(document.GetProperty("seed"), long.MinValue, long.MaxValue));
        foreach (var entry in RuntimeJson.Rows(document, "rows", MaximumRows))
        {
            RuntimeJson.Shape(entry, "key scopes");
            if (ReadKey(entry) is not { } key) continue;
            if (!gated.TryGetValue(key.Plan, out var table) || !table.TryGetValue(key.Node, out var options)) continue;
            var row = new Row();
            foreach (var scoped in RuntimeJson.Rows(entry, "scopes", MaximumScopesPerGate))
            {
                if (row.Scopes.Count >= MaximumScopesPerGate) break;
                RuntimeJson.Shape(scoped,
                    "subject accumulated fired lastArrival started charged rolls entity releaseArmed releaseTaken releaseRemainingTicks releaseGeneration release",
                    "skipped");
                if (SavedSubject(scoped, options.Scope, "gate-migration") is not { } subject) continue;
                var entityValue = scoped.GetProperty("entity");
                var releaseValue = scoped.GetProperty("release");
                var armed = RuntimeJson.Flag(scoped, "releaseArmed");
                var state = SavedCounters(scoped);
                state.Entity = entityValue.ValueKind == JsonValueKind.Null ? null : RuntimeJson.Entity(entityValue);
                state.ReleaseArmed = armed;
                state.ReleaseTaken = RuntimeJson.Flag(scoped, "releaseTaken");
                state.ReleaseAtTick = checked(tick + RuntimeJson.Integer(scoped.GetProperty("releaseRemainingTicks"), 0, RuntimeJson.MaxSafeInteger));
                state.ReleaseGeneration = RuntimeJson.Integer(scoped.GetProperty("releaseGeneration"), 0, MaximumCounter);
                state.Release = releaseValue.ValueKind == JsonValueKind.Null ? null : deserializeRelease(releaseValue);
                RuntimeJson.Require(!armed || state.Release != null, "gate-migration", "An armed debounce window has no captured release.");
                HasReleases |= armed;
                row.Scopes[subject] = state;
            }
            rows[key] = row;
        }
    }

    /// <summary>
    /// Drops the booked sets of every per-instance gate. Called at the one lifecycle boundary that rebuilds the
    /// level's own objects — restoring a checkpoint — because the entities those sets were keyed by are gone with
    /// them; a per-player set survives it, since the players are the same players.
    /// </summary>
    internal void PruneInstances(HashSet<string> instanceNodes)
    {
        if (instanceNodes.Count == 0) return;
        foreach (var pair in rows)
            if (instanceNodes.Contains(pair.Key.Node)) pair.Value.Scopes.Clear();
    }

    /// <summary>The canonical key halves of one text, or null where the text is not a key: the same pair
    /// <see cref="SplitKey"/> reads, with both halves checked as the texts they are, so a checkpoint or a migration
    /// document cannot introduce a plan or a node id the plan loader would never accept.</summary>
    private static GateKey? ReadKey(JsonElement entry)
    {
        var key = SplitKey(RuntimeJson.Text(entry, "key"));
        if (key is not { } halves) return null;
        RuntimeJson.Text(halves.Plan); RuntimeJson.Text(halves.Node);
        return halves;
    }

    /// <summary>The rows in the one canonical order the wire documents use: by plan, then by node, ordinal. It is
    /// the same order the joined key produced, read off the halves instead of off a concatenation.</summary>
    private IEnumerable<KeyValuePair<GateKey, Row>> OrderedRows()
        => rows.OrderBy(pair => pair.Key.Plan, StringComparer.Ordinal).ThenBy(pair => pair.Key.Node, StringComparer.Ordinal);

    /// <summary>The booked sets of the per-player and the per-instance gates, for a caller reporting what a
    /// checkpoint carried.</summary>
    internal int ScopeRows => rows.Values.Sum(row => row.Scopes.Count);

    /// <summary>What one gated entry point has fired and accumulated for one subject, or null where the plan
    /// declares no gate for that entry at all. A subject that has never been asked about reads as the window it
    /// would start: nothing accumulated, nothing fired.</summary>
    internal (long Fired, long Accumulated)? StateOf(string planId, string nodeId, string subject)
    {
        if (!rows.TryGetValue(new GateKey(planId, nodeId), out var row)) return null;
        var scoped = row.Find(subject);
        return scoped == null ? (0, 0) : (scoped.Fired, scoped.Accumulated);
    }
}
