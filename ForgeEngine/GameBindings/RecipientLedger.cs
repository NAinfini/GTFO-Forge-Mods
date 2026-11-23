using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>A package's contribution to Runtime's recipient state: which private presentation lines each player
/// account was shown. Keys are account identities, never character slots: a reconnect may take another slot and a
/// different account must never inherit its private display. Native snapshots retain all recipients for migration;
/// only the exact local account applies a contribution, once per command. A package derives from this and says what
/// it handles, how a line is keyed, and how one retained line is shown again.</summary>
internal abstract class RecipientLedger : IDisposable
{
    public const int MaximumEntries = 256;
    public const string FullCode = "map-private-state-full";
    public const string InvalidCode = "map-private-state-invalid";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="StartTick">The host tick the line was issued on, which every machine reads from the same request.</param>
    /// <param name="Seconds">The line's declared lifetime; 0 for a line that lasts until replaced.</param>
    public sealed record Saved(PresentationOutput Request, JsonElement Inputs, JsonElement Parameters, string Surface,
        long StartTick = 0, double Seconds = 0);

    protected readonly IForgeEngine Kernel;
    private readonly IPlayerDirectory _players;
    private readonly DomainStateChannel.NativeStateSource _source;
    private Dictionary<ulong, Dictionary<string, Saved>> _recipients = new();
    private readonly Dictionary<string, string> _applied = new(StringComparer.Ordinal);
    private ulong? _localIdentity;
    // Reading the local account wraps several native objects and builds the session id on every call, so a frame reads it
    // only when something could have changed: a line was recorded, restored or reset, the owner called Wake (a player
    // arrived or left), a line is still pending, or the quiet-poll interval passed.
    private static readonly long QuietPollTicks = System.Diagnostics.Stopwatch.Frequency * 10;
    private bool _dirty = true;
    private long _lastRead = long.MinValue;
    /// <summary>Counts the pending reports so one reason is written at its 1st, 10th, 100th ... occurrence.</summary>
    protected readonly OnceReporter Once = new();
    protected EntityReference? LocalLife { get; private set; }

    protected RecipientLedger(IForgeEngine kernel, IPlayerDirectory players, string stateKey)
    {
        Kernel = kernel; _players = players;
        _source = DomainStateChannel.RegisterRecipientState(kernel, stateKey, Handles, Record, Capture, Restore);
    }

    protected abstract bool Handles(PresentationOutput request);

    /// <summary>Whether this admitted command leaves a line at all (a row with several placements may keep only one).</summary>
    protected virtual bool Keeps(CommandContext context) => true;

    /// <summary>The audience port of the request and the retained line's key.</summary>
    protected abstract (HandlerSlot Audience, string Surface) Locate(PresentationOutput request, CommandContext context);

    /// <summary>The lifetime a recorded line carries; none by default.</summary>
    protected virtual (long StartTick, double Seconds) Lifetime(PresentationOutput request) => (0, 0);

    /// <summary>A line that has nothing left to show, which only occupies room.</summary>
    protected virtual bool Expired(Saved saved) => false;

    /// <summary>Extra validation of a restored line, beyond handled request, matching surface and object inputs.</summary>
    protected virtual bool Valid(Saved saved) => true;

    /// <summary>Shows one retained line again to the local account: true with a code when it was applied (or has
    /// nothing left to apply), false with the reason it stays pending.</summary>
    protected abstract (bool Applied, string Code) Apply(Saved saved);

    /// <summary>Runs <paramref name="show"/> on the retained line's command with the local account as its only viewer:
    /// applied when the command presented, otherwise the reason it stays pending.</summary>
    protected (bool Applied, string Code) ShowToLocal(Saved saved, Func<CommandContext, CommandResult> show)
    {
        var inputs = saved.Inputs.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.Ordinal);
        inputs["viewers"] = RuntimeJson.From(new[] { LocalLife! });
        CommandResult? result = null;
        DomainStateChannel.RebuildRecipientContext(Kernel, saved.Request with { WorldEpoch = Kernel.WorldEpoch },
            RuntimeJson.From(inputs), saved.Parameters, context => result = show(context));
        return result?.Status is CommandStatuses.Succeeded or CommandStatuses.Partial
            ? (true, result.Code) : (false, result?.Code ?? InvalidCode);
    }

    protected virtual void Restored(string surface, string code) { }
    protected abstract void Pending(string surface, string code, Saved saved);

    /// <summary>The local account is gone or another one has taken its place.</summary>
    protected virtual void LocalReplaced() { }

    // Reused by every Record: the identities one request addresses and the lines a pruning pass drops.
    private readonly HashSet<ulong> _identities = new();
    private readonly List<(ulong Id, string Surface)> _expired = new();

    /// <summary>Records one admitted private line. Everything that can refuse it is checked before anything changes,
    /// so a refusal leaves the ledger as it was; a change is reported to the native-state channel here.</summary>
    public void Record(PresentationOutput request, CommandContext context)
    {
        if (!Handles(request) || !Keeps(context)) return;
        var (audience, surface) = Locate(request, context);
        var identities = _identities; identities.Clear();
        for (int i = 0; i < context.Count(audience); i++)
        {
            if (!context.TryEntity(audience, i, out var viewer) || _players.PlayerOf(viewer) is not { } player)
                throw new RuntimeContractException(InvalidCode, "Private display recipient is stale.");
            if (player.IsBot) continue;
            var session = _players.SessionOf(player);
            if (player.Lookup == 0 || session == null || !Addressed(request.Recipients, session))
                throw new RuntimeContractException(InvalidCode, "Private display recipient does not match the admitted route.");
            identities.Add(player.Lookup);
        }
        // Expired lines only occupy room: they are not counted, and they are dropped once the line is admitted.
        _expired.Clear();
        int live = 0;
        foreach (var (id, entries) in _recipients)
            foreach (var (key, line) in entries)
                if (Expired(line)) _expired.Add((id, key)); else live++;
        int added = 0;
        foreach (var id in identities)
            if (!_recipients.TryGetValue(id, out var entries) || !entries.TryGetValue(surface, out var held) || Expired(held)) added++;
        if (live + added > MaximumEntries)
            throw new RuntimeContractException(FullCode, "Private presentation state capacity reached.");
        if (identities.Count == 0 && _expired.Count == 0) return;
        foreach (var (id, key) in _expired)
        {
            var entries = _recipients[id];
            entries.Remove(key);
            if (entries.Count == 0) _recipients.Remove(id);
        }
        if (identities.Count > 0)
        {
            var (start, seconds) = Lifetime(request);
            var saved = new Saved(request with { Inputs = request.Inputs.Clone() }, context.Inputs.Clone(),
                context.Parameters.Clone(), surface, start, seconds);
            foreach (var id in identities)
            {
                if (!_recipients.TryGetValue(id, out var entries)) _recipients.Add(id, entries = new(StringComparer.Ordinal));
                entries[surface] = saved;
            }
        }
        _dirty = true;
        _source.Changed();
    }

    private static bool Addressed(IReadOnlyList<string> recipients, string session)
    {
        for (var i = 0; i < recipients.Count; i++) if (string.Equals(recipients[i], session, StringComparison.Ordinal)) return true;
        return false;
    }

    public JsonElement Capture()
    {
        var recipients = new Dictionary<ulong, Dictionary<string, Saved>>();
        foreach (var (id, entries) in _recipients)
            recipients.Add(id, new(entries, StringComparer.Ordinal));
        return JsonSerializer.SerializeToElement(new { recipients }, Json);
    }

    public void Restore(JsonElement state)
    {
        // Parse and validate the entire contribution before replacing any retained recipient state.
        var parsed = state.GetProperty("recipients").Deserialize<Dictionary<ulong, Dictionary<string, Saved>>>(Json)
            ?? throw new RuntimeContractException(InvalidCode, "Private state has no recipients.");
        if (parsed.Count > RuntimeKernel.MaximumPresentationRecipients || parsed.Values.Sum(entries => entries.Count) > MaximumEntries)
            throw new RuntimeContractException(FullCode, "Private presentation snapshot capacity reached.");
        var next = new Dictionary<ulong, Dictionary<string, Saved>>();
        foreach (var (id, entries) in parsed)
        {
            if (id == 0 || entries == null)
                throw new RuntimeContractException(InvalidCode, "Private state recipient is not an account identity.");
            var live = new Dictionary<string, Saved>(StringComparer.Ordinal);
            foreach (var (surface, saved) in entries)
            {
                if (saved == null || !Handles(saved.Request) || saved.Surface != surface
                    || saved.Inputs.ValueKind != JsonValueKind.Object || saved.Parameters.ValueKind != JsonValueKind.Object
                    || !Valid(saved))
                    throw new RuntimeContractException(InvalidCode, "Private state contribution is malformed.");
                live.Add(surface, saved);
            }
            next.Add(id, live);
        }
        _recipients = next;
        _dirty = true;
    }

    /// <summary>The handler ran: a line this account was recorded for and that this command carried is applied.</summary>
    public void Acknowledge(CommandContext context, CommandResult result)
    {
        if (result.Status is not (CommandStatuses.Succeeded or CommandStatuses.Partial)) return;
        if (_localIdentity == null) ReadLocal();
        if (_localIdentity == null || !_recipients.TryGetValue(_localIdentity.Value, out var entries)) return;
        foreach (var (surface, saved) in entries)
            if (saved.Request.CommandId == context.CommandId) _applied[surface] = context.CommandId;
    }

    private bool ReadLocal()
    {
        var agent = _players.LocalAgent;
        var player = agent?.Owner;
        var life = agent == null ? null : _players.ReferenceOf(agent);
        if (player == null || player.Lookup == 0 || life == null || !_players.IsLocal(_players.SessionOf(player)))
        {
            if (_localIdentity != null) LocalReplaced();
            LocalLife = null; _localIdentity = null; _applied.Clear(); return false;
        }
        ulong id = player.Lookup;
        if (_localIdentity != id || LocalLife != life)
        {
            if (_localIdentity != null) LocalReplaced();
            _applied.Clear(); Once.Reset();
        }
        _localIdentity = id; LocalLife = life;
        return true;
    }

    public void Frame()
    {
        // Nothing is addressed to anyone: the local player is not read. A local-player change is noticed by
        // ReadLocal on the first frame after a recipient is recorded, before any entry is applied.
        if (_recipients.Count == 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_dirty && now - _lastRead < QuietPollTicks) return;
        _lastRead = now;
        if (!ReadLocal()) { _dirty = true; return; } // no local account yet: ask again next frame
        _dirty = false; // set again below, or by a recording made meanwhile, while anything is left to show
        if (!_recipients.TryGetValue(_localIdentity!.Value, out var entries)) return;
        bool pending = false;
        foreach (var (surface, saved) in entries)
        {
            if (_applied.TryGetValue(surface, out var command) && command == saved.Request.CommandId) continue;
            try
            {
                var (applied, code) = Apply(saved);
                if (applied) { _applied[surface] = saved.Request.CommandId; Restored(surface, code); }
                else { pending = true; Pending(surface, code, saved); }
            }
            catch (Exception error)
            { pending = true; Pending(surface, error is RuntimeContractException contract ? contract.Code : error.GetType().Name, saved); }
        }
        if (pending) _dirty = true;
    }

    /// <summary>The owner learned the local account or its life may have changed (a player arrived or left): the next
    /// frame reads the local account instead of waiting for the quiet poll.</summary>
    public void Wake() => _dirty = true;

    public void CheckpointRestored()
    {
        _applied.Clear(); Once.Reset(); _dirty = true;
    }

    public void Clear()
    { _recipients.Clear(); _applied.Clear(); Once.Reset(); _localIdentity = null; LocalLife = null; _dirty = true; }

    public void Dispose() { _source.Dispose(); Clear(); }
}
