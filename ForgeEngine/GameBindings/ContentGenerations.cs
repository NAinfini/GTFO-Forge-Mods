using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.GameBindings;

/// <summary>One discovery of the installed content (contract I-RELOAD, 发现): every participant's <see cref="IContentParticipant.Read"/>
/// puts what it read from disk here, once, and admission, preparation and commit only use these reads. Startup's snapshot holds
/// the reads ForgeEngine made at its Load and at startup; a reload's snapshot is a new discovery of the install.</summary>
internal sealed class ContentDiskSnapshot
{
    private readonly Dictionary<string, object> _reads = new(StringComparer.Ordinal);
    internal ContentDiskSnapshot(string root, bool startup) { Root = root; Startup = startup; }
    /// <summary>The BepInEx root the install was discovered under.</summary>
    internal string Root { get; }
    /// <summary>True for the first generation's snapshot (empty generation to generation 1).</summary>
    internal bool Startup { get; }
    internal void Put(string participant, object read) => _reads[participant] = read ?? throw new ArgumentNullException(nameof(read));
    internal T Get<T>(string participant) where T : class
        => _reads.TryGetValue(participant, out var read) ? (T)read
            : throw new InvalidOperationException("The discovery snapshot holds no read of " + participant + ".");
    internal bool Has(string participant) => _reads.ContainsKey(participant);
    /// <summary>The same reads, admitted again as a later generation (a commit of unchanged input after the game lost it).</summary>
    internal ContentDiskSnapshot Again()
    {
        var again = new ContentDiskSnapshot(Root, startup: false);
        foreach (var read in _reads) again._reads.Add(read.Key, read.Value);
        return again;
    }
}

/// <summary>A participant's admitted slice of one snapshot: its digest (the same input gives the same digest) and the value
/// admission produced for it, which preparation builds from.</summary>
internal sealed record ContentPart(string Participant, string Digest, object? Value);

/// <summary>A new generation refused before anything was written (or rolled back after a failed commit): the code names the
/// reason, the participant names who refused. The previous generation stays.</summary>
internal sealed class ContentGenerationRefusal : Exception
{
    internal ContentGenerationRefusal(string code, string participant, string detail, Exception? inner = null)
        : base(code + " (" + participant + "): " + detail, inner) { Code = code; Participant = participant; }
    internal string Code { get; }
    internal string Participant { get; }
    /// <summary>The participant cannot yet replace its content in a running game; its lane implements the swap.</summary>
    internal const string Unsupported = "reload-participant-unsupported";
    internal const string NotClean = "reload-lobby-not-clean";
    internal const string AuthoringRequired = "reload-authoring-required";
    internal const string Stopped = "reload-content-stopped";
    internal const string CommitFailed = "reload-commit-failed";
}

/// <summary>What a commit hands to the participants: the generation being built, this participant's admitted part, the part and
/// leases the current generation holds for it (null at startup), and whether the input is unchanged but the game lost the
/// content (a data table re-initialization), so it must be put in again.</summary>
internal readonly record struct ContentChange(ContentGeneration Next, ContentPart Part, ContentPart? Current, ContentLeases? CurrentLeases, bool Redo)
{
    /// <summary>The input is the one the current generation committed and the game still holds it: nothing to replace.</summary>
    internal bool Unchanged => !Redo && Current != null && Current.Digest == Part.Digest;
}

/// <summary>A participant: one kind of content (contract I-RELOAD 档位) that goes into the game per generation.
/// <see cref="Read"/> is discovery (disk, once per generation); <see cref="Admit"/> is pure (no game state, no disk), the same
/// rules the website and workshop run; <see cref="Prepare"/> builds every native object the new content needs without publishing
/// any of it, and any failure there leaves the current generation untouched.</summary>
internal interface IContentParticipant
{
    string Id { get; }
    void Read(ContentDiskSnapshot snapshot);
    ContentPart Admit(ContentDiskSnapshot snapshot, ContentGeneration current);
    PreparedContent Prepare(ContentChange change);
}

/// <summary>One participant's prepared content. <see cref="Commit"/> is the short native write with readback and records a lease for
/// everything it put in; <see cref="Release"/> confirms (by default the leases of what this commit replaced are revoked);
/// <see cref="Revert"/> undoes a commit another participant's failure rolled back (by default this commit's leases are revoked);
/// <see cref="Discard"/> drops prepared objects that were never committed.</summary>
internal abstract class PreparedContent
{
    internal ContentLeases Leases { get; private protected set; } = new();
    internal abstract void Commit(ContentGeneration next);
    internal virtual void Release(ContentLeases? previous) => previous?.RevokeAll();
    internal virtual void Revert() => Leases.RevokeAll();
    internal virtual void Discard() { }

    /// <summary>The participant keeps the current generation's content as it is (its input did not change): its leases move to the
    /// new generation untouched.</summary>
    internal static PreparedContent Carry(ContentLeases current) => new Carried(current);

    /// <summary>A commit that runs <paramref name="commit"/> and records its leases; release and revert as the defaults.</summary>
    internal static PreparedContent Of(Action<ContentGeneration, ContentLeases> commit) => new Delegated(commit);

    private sealed class Carried : PreparedContent
    {
        internal Carried(ContentLeases current) => Leases = current;
        internal override void Commit(ContentGeneration next) { }
        internal override void Release(ContentLeases? previous) { }
        internal override void Revert() { }
    }

    private sealed class Delegated : PreparedContent
    {
        private readonly Action<ContentGeneration, ContentLeases> _commit;
        internal Delegated(Action<ContentGeneration, ContentLeases> commit) => _commit = commit;
        internal override void Commit(ContentGeneration next) => _commit(next, Leases);
    }
}

/// <summary>What a lease stands for (contract I-RELOAD 租约).</summary>
internal enum ContentLeaseKind
{
    TableRows, NativePatch, NativeCache, ModelRegistration, SidecarRows, Menu, KernelContent, UnityObject, GenerationPlan, PlanIndex
}

/// <summary>One thing a generation put into the game, revocable exactly once: a second <see cref="Revoke"/> does nothing.</summary>
internal sealed class ContentLease
{
    private Action? _revoke;
    internal ContentLease(ContentLeaseKind kind, string owner, Action revoke)
    {
        if (string.IsNullOrEmpty(owner)) throw new ArgumentException("Name the lease owner.", nameof(owner));
        Kind = kind; Owner = owner; _revoke = revoke ?? throw new ArgumentNullException(nameof(revoke));
    }
    internal ContentLeaseKind Kind { get; }
    internal string Owner { get; }
    internal bool Revoked => _revoke == null;
    internal void Revoke()
    {
        var revoke = _revoke;
        _revoke = null;
        revoke?.Invoke();
    }

    /// <summary>A write to a native value that existed before Forge: the baseline is read now, before <paramref name="write"/> puts
    /// <paramref name="value"/> in, and revoking writes the baseline back.</summary>
    internal static ContentLease Overwrite<T>(string owner, Func<T> read, Action<T> write, T value)
    {
        var baseline = read();
        write(value);
        return new(ContentLeaseKind.NativePatch, owner, () => write(baseline));
    }
}

/// <summary>The leases one participant holds in one generation, revoked newest first.</summary>
internal sealed class ContentLeases
{
    private readonly List<ContentLease> _leases = new();
    internal int Count => _leases.Count;
    internal IReadOnlyList<ContentLease> Items => _leases;
    internal ContentLease Add(ContentLease lease) { _leases.Add(lease ?? throw new ArgumentNullException(nameof(lease))); return lease; }
    internal ContentLease Add(ContentLeaseKind kind, string owner, Action revoke) => Add(new ContentLease(kind, owner, revoke));
    /// <summary>Revokes every lease newest first; every lease is tried and the failures are thrown together.</summary>
    internal void RevokeAll()
    {
        List<Exception>? failures = null;
        for (int index = _leases.Count - 1; index >= 0; index--)
        {
            try { _leases[index].Revoke(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("Content lease revocation failed.", failures);
    }
}

/// <summary>One committed content generation: its number (0 is empty, startup commits 1), the snapshot it was read from, each
/// participant's admitted part and leases, and the values commits handed on to the participants after them.</summary>
internal sealed class ContentGeneration
{
    private readonly Dictionary<string, ContentPart> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentLeases> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _results = new(StringComparer.Ordinal);
    internal ContentGeneration(long number, ContentDiskSnapshot? snapshot) { Number = number; Snapshot = snapshot; }
    internal static ContentGeneration Empty() => new(0, null);
    internal long Number { get; }
    internal ContentDiskSnapshot? Snapshot { get; }
    internal string Digest { get; private set; } = "";
    internal IReadOnlyDictionary<string, ContentPart> Parts => _parts;
    internal ContentPart? Part(string participant) => _parts.TryGetValue(participant, out var part) ? part : null;
    internal ContentLeases? Leases(string participant) => _leases.TryGetValue(participant, out var leases) ? leases : null;
    internal int LeaseCount => _leases.Values.Sum(leases => leases.Count);
    /// <summary>A value a participant's commit hands on (startup: the accepted content snapshot the host callbacks read).</summary>
    internal void Publish(string participant, object value) => _results[participant] = value;
    internal T Result<T>(string participant) where T : class
        => _results.TryGetValue(participant, out var value) ? (T)value
            : throw new InvalidOperationException("Generation " + Number + " holds no committed result of " + participant + ".");
    internal T? TryResult<T>(string participant) where T : class => _results.TryGetValue(participant, out var value) ? value as T : null;
    internal void Inherit(ContentGeneration previous, string participant)
    {
        if (previous._results.TryGetValue(participant, out var value)) _results[participant] = value;
    }
    internal void AddPart(ContentPart part) => _parts.Add(part.Participant, part);
    internal void SetLeases(string participant, ContentLeases leases) => _leases[participant] = leases;
    internal void Seal() => Digest = ForgeCore.Hash.Sha256Hex(string.Join("\n", _parts.Values.OrderBy(part => part.Participant, StringComparer.Ordinal)
        .Select(part => part.Participant + "=" + part.Digest)));
}

/// <summary>The 「内容已换」 event: generation <see cref="From"/> was replaced by <see cref="To"/>; <see cref="Changed"/> names the
/// participants whose content changed (a carried participant is not named).</summary>
internal sealed record ContentChanged(long From, long To, string Digest, IReadOnlyList<string> Changed, bool Redo);

/// <summary>The content generation orchestrator (contract I-RELOAD): startup commits the empty generation into generation 1 and a
/// reload commits generation N into N+1, through <see cref="Commit"/>, the one path. Participants run in registration order:
/// all of them admit, then all of them prepare (any failure: the new generation is dropped, the current one stays as it was),
/// then all of them commit; a failed commit reverts the committed participants newest first and the current generation stays.
/// Game thread only.</summary>
internal sealed class ContentGenerations
{
    private readonly List<IContentParticipant> _participants = new();

    internal ContentGeneration Current { get; private set; } = ContentGeneration.Empty();
    internal IReadOnlyList<IContentParticipant> Participants => _participants;
    /// <summary>「内容已换」: raised after a generation was committed and confirmed.</summary>
    internal event Action<ContentChanged>? Changed;
    /// <summary>A commit in progress: participants must not install hooks or start another commit.</summary>
    internal bool Committing { get; private set; }

    internal void Register(IContentParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (Current.Number != 0) throw new InvalidOperationException("Content participants register before the first generation.");
        if (_participants.Any(existing => existing.Id == participant.Id))
            throw new InvalidOperationException("Content participant " + participant.Id + " is already registered.");
        _participants.Add(participant);
    }

    /// <summary>Discovery: a new snapshot with every participant's read.</summary>
    internal ContentDiskSnapshot Discover(string root, bool startup)
    {
        var snapshot = new ContentDiskSnapshot(root, startup);
        foreach (var participant in _participants) participant.Read(snapshot);
        return snapshot;
    }

    /// <summary>Admit, prepare and commit <paramref name="snapshot"/> as the next generation. Throws
    /// <see cref="ContentGenerationRefusal"/> with the current generation unchanged when admission or preparation refuses or a
    /// commit failed and was reverted; any other exception from a revert means the rollback itself failed.</summary>
    internal ContentGeneration Commit(ContentDiskSnapshot snapshot, bool redo = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Committing) throw new InvalidOperationException("A content generation is already being committed.");
        Committing = true;
        try { return CommitCore(snapshot, redo); }
        finally { Committing = false; }
    }

    private ContentGeneration CommitCore(ContentDiskSnapshot snapshot, bool redo)
    {
        var current = Current;
        var next = new ContentGeneration(current.Number + 1, snapshot);
        foreach (var participant in _participants)
        {
            ContentPart part;
            try { part = participant.Admit(snapshot, current); }
            catch (ContentGenerationRefusal) { throw; }
            catch (Exception error) { throw new ContentGenerationRefusal("reload-admission-failed", participant.Id, error.Message, error); }
            if (part.Participant != participant.Id) throw new InvalidOperationException(participant.Id + " admitted a part of " + part.Participant + ".");
            next.AddPart(part);
        }
        var prepared = new List<(IContentParticipant Participant, PreparedContent Content)>(_participants.Count);
        try
        {
            foreach (var participant in _participants)
            {
                var change = new ContentChange(next, next.Part(participant.Id)!, current.Part(participant.Id), current.Leases(participant.Id), redo);
                prepared.Add((participant, participant.Prepare(change)));
            }
        }
        catch (Exception error)
        {
            Discard(prepared, 0);
            if (error is ContentGenerationRefusal) throw;
            throw new ContentGenerationRefusal("reload-prepare-failed", _participants[prepared.Count].Id, error.Message, error);
        }
        int committed = 0;
        try
        {
            for (; committed < prepared.Count; committed++)
            {
                var (participant, content) = prepared[committed];
                content.Commit(next);
                next.SetLeases(participant.Id, content.Leases);
                if (next.TryResult<object>(participant.Id) == null) next.Inherit(current, participant.Id);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            // The failed participant may have written part of its content: its own revert runs first.
            for (int index = Math.Min(committed, prepared.Count - 1); index >= 0; index--)
            {
                try { prepared[index].Content.Revert(); }
                catch (Exception revert) { failures.Add(revert); }
            }
            Discard(prepared, committed + 1);
            if (failures.Count != 0) throw new AggregateException("Content generation rollback failed.", new[] { error }.Concat(failures));
            throw new ContentGenerationRefusal(error is ContentGenerationRefusal refusal ? refusal.Code : ContentGenerationRefusal.CommitFailed,
                prepared[committed].Participant.Id, error.Message, error);
        }
        next.Seal();
        Current = next;
        var changed = new List<string>();
        List<Exception>? releaseFailures = null;
        foreach (var (participant, content) in prepared)
        {
            var before = current.Leases(participant.Id);
            if (!ReferenceEquals(before, content.Leases)) changed.Add(participant.Id);
            try { content.Release(before); }
            catch (Exception error) { (releaseFailures ??= new()).Add(error); }
        }
        Changed?.Invoke(new ContentChanged(current.Number, next.Number, next.Digest, changed, redo));
        if (releaseFailures != null) throw new AggregateException("Generation " + next.Number + " is committed; releasing the previous content failed.", releaseFailures);
        return next;
    }

    private static void Discard(List<(IContentParticipant Participant, PreparedContent Content)> prepared, int from)
    {
        for (int index = from; index < prepared.Count; index++)
        {
            try { prepared[index].Content.Discard(); }
            catch (Exception error) { ForgeEngine.Framework.NativeFault.Note("content-generation.discard", error); }
        }
    }
}
