using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using ForgeCore;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>A content domain whose entries the host delivers once per content generation (Enemy, Weapon; contract I-RELOAD D1,
/// D2). <see cref="BeginEntries"/> runs before the generation's entries arrive: the domain starts again from its own base content
/// (what it read at its Start, never the previous generation's entries) and registers on the generation how to undo this
/// generation and how to let go of the previous one. <see cref="EndEntries"/> runs after every entry arrived and the rows the
/// generation no longer has left the tables.</summary>
internal interface IContentEntryDomain
{
    void BeginEntries(ContentEntryGeneration generation);
    void EndEntries(ContentEntryGeneration generation);
}

/// <summary>One generation of content entries being written into the game. Each accepted entry's rows go into the tables in one
/// table transaction of their own: a row the previous generation published (same table and ID) is replaced in its slot, any other
/// row is added. After the entries, the previous generation's rows this one did not publish are removed in one more transaction.
/// The generation holds every transaction until <see cref="Release"/> (the new content generation stands: the replaced and removed
/// objects are let go and the domains' release work runs) or <see cref="Revert"/> (it does not: every transaction is undone
/// newest first, then every domain's undo work newest first).</summary>
internal sealed class ContentEntryGeneration
{
    /// <summary>The ledger owner of every entry row (the owner <see cref="NativeDataBlockTables.Register"/> has always used).</summary>
    internal const string TableOwner = "Forge content";
    private static readonly List<IContentEntryDomain> Domains = new();

    /// <summary>The generation whose entries are being delivered; null outside a delivery.</summary>
    internal static ContentEntryGeneration? Current { get; private set; }
    /// <summary>The delivered generation that stands (the last one released); null before the first.</summary>
    internal static ContentEntryGeneration? Standing { get; private set; }

    /// <summary>A domain registers once, at its Start (before the first generation).</summary>
    internal static void RegisterDomain(IContentEntryDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (!Domains.Contains(domain)) Domains.Add(domain);
    }

    internal static void UnregisterDomain(IContentEntryDomain domain) => Domains.Remove(domain);

    private readonly HashSet<(string Type, uint Id)> _previousRows;
    private readonly HashSet<(string Type, uint Id)> _rows = new();
    private readonly List<(string Type, uint Id)> _published = new();
    private readonly List<NativeDataBlockTables.Transaction> _transactions = new();
    private readonly List<Action> _undo = new(), _release = new();
    private bool _handedOver;

    internal ContentEntryGeneration(long number, ContentEntryGeneration? previous, bool redo)
    {
        Number = number; Redo = redo;
        _previousRows = previous == null ? new() : new(previous._rows);
    }

    /// <summary>The content generation number this delivery builds.</summary>
    internal long Number { get; }
    /// <summary>The same input again because the game lost Forge's rows (a data table re-initialization).</summary>
    internal bool Redo { get; }
    /// <summary>The rows this generation published.</summary>
    internal IReadOnlyCollection<(string Type, uint Id)> Rows => _rows;
    /// <summary>The rows this generation published, in publication order: the order a cold start with this content appends them
    /// to the tables, and so the order the game's own setup reads them in.</summary>
    internal IReadOnlyList<(string Type, uint Id)> Published => _published;
    /// <summary>Whether the previous generation published this row.</summary>
    internal bool Previously(string type, uint id) => _previousRows.Contains((type, id));
    /// <summary>The previous generation's rows this one did not publish (complete after the entries arrived).</summary>
    internal IEnumerable<(string Type, uint Id)> Withdrawn => _previousRows.Where(row => !_rows.Contains(row));

    /// <summary>Undo work for this generation's change, run newest first after the table transactions are undone.</summary>
    internal void OnUndo(Action undo) => _undo.Add(undo ?? throw new ArgumentNullException(nameof(undo)));
    /// <summary>Work that lets go of what the previous generation held, run once this generation stands.</summary>
    internal void OnRelease(Action release) => _release.Add(release ?? throw new ArgumentNullException(nameof(release)));

    /// <summary>Publish one entry's rows: replaced in place where the previous generation published them (and, after a table
    /// re-initialization, the table still holds them), added otherwise. Outside a delivery (a domain driven directly) the rows are
    /// only added.</summary>
    internal static void Publish(IReadOnlyList<NativeDataBlockRegistration> definitions, bool strictFields)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (Current is not { } generation) { NativeDataBlockTables.Register(definitions, strictFields, TableOwner); return; }
        var transaction = new NativeDataBlockTables.Transaction(TableOwner);
        foreach (var definition in definitions)
        {
            if (generation.Previously(definition.Type, definition.PersistentId) && (!generation.Redo || InTable(definition.Type, definition.PersistentId)))
                transaction.Replace(definition, strictFields);
            else transaction.Add(definition, strictFields);
        }
        transaction.Commit();
        generation._transactions.Add(transaction);
        foreach (var definition in definitions)
            if (generation._rows.Add((definition.Type, definition.PersistentId))) generation._published.Add((definition.Type, definition.PersistentId));
    }

    private static bool InTable(string type, uint id)
    {
        try { NativeDataBlockTables.RequireBlock(type, id); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Deliver this generation: every domain begins, <paramref name="dispatch"/> hands the entries to the consumers, the
    /// withdrawn rows leave the tables and every domain ends. A throw leaves what was written for <see cref="Revert"/>.</summary>
    internal void Deliver(Action dispatch)
    {
        if (Current != null) throw new InvalidOperationException("Content entries are already being delivered.");
        Current = this;
        try
        {
            foreach (var domain in Domains) domain.BeginEntries(this);
            dispatch();
            // After a table re-initialization a withdrawn row the table lost has nothing left to remove.
            var withdrawn = Withdrawn.Where(row => !Redo || InTable(row.Type, row.Id)).ToArray();
            if (withdrawn.Length != 0)
            {
                var transaction = new NativeDataBlockTables.Transaction(TableOwner);
                foreach (var (type, id) in withdrawn) transaction.Remove(type, id);
                transaction.Commit();
                _transactions.Add(transaction);
            }
            foreach (var domain in Domains) domain.EndEntries(this);
        }
        finally { Current = null; }
    }

    /// <summary>A lease for the rows this generation published: revoking it removes them, until a later generation took them over.</summary>
    internal ContentLease Lease() => new(ContentLeaseKind.TableRows, EntriesParticipantName, () =>
    {
        if (_handedOver) return;
        var owned = new HashSet<(string, string, uint)>(NativeDataBlockTables.OwnedRows());
        var transaction = new NativeDataBlockTables.Transaction(TableOwner);
        int staged = 0;
        foreach (var (type, id) in _rows)
            if (owned.Contains((TableOwner, type, id))) { transaction.Remove(type, id); staged++; }
        if (staged == 0) return;
        transaction.Commit();
        transaction.Release();
    });

    internal const string EntriesParticipantName = "entries";

    /// <summary>This generation stands: the transactions let go of the objects they replaced or removed, the domains' release
    /// work runs, and the previous generation's rows are now this one's.</summary>
    internal void Release(ContentEntryGeneration? previous)
    {
        if (previous != null) previous._handedOver = true;
        Standing = this;
        List<Exception>? failures = null;
        foreach (var transaction in _transactions)
        {
            try { transaction.Release(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        foreach (var release in _release)
        {
            try { release(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        _transactions.Clear(); _release.Clear(); _undo.Clear();
        if (failures != null) throw new AggregateException("Releasing the previous content entries failed.", failures);
    }

    /// <summary>This generation does not stand: every table transaction is undone newest first, then every undo work newest first.
    /// Every step is tried; the failures are thrown together.</summary>
    internal void Revert()
    {
        List<Exception>? failures = null;
        for (int index = _transactions.Count - 1; index >= 0; index--)
        {
            try { _transactions[index].Revert(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        for (int index = _undo.Count - 1; index >= 0; index--)
        {
            try { _undo[index](); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        _transactions.Clear(); _release.Clear(); _undo.Clear();
        if (failures != null) throw new AggregateException("Reverting the content entries failed.", failures);
    }
}

internal static partial class GameRuntimeBridge
{
    /// <summary>The weapon, enemy and behaviour entries (tiers D1 and D2 and behaviour sources). Every generation whose entry files
    /// changed, and every redo after a table re-initialization, delivers the whole entry set again to the kernel's domain consumers
    /// as one <see cref="ContentEntryGeneration"/>; unchanged files carry the current generation's content.</summary>
    private sealed class EntriesParticipant : IContentParticipant
    {
        internal const string Name = ContentEntryGeneration.EntriesParticipantName;
        private ContentEntryGeneration? _committed;
        public string Id => Name;
        public void Read(ContentDiskSnapshot snapshot) => snapshot.Put(Name, ContentPackStartup.Scan(snapshot.Root));
        public ContentPart Admit(ContentDiskSnapshot snapshot, ContentGeneration current)
        {
            var catalog = snapshot.Get<ContentCatalog>(Name);
            string digest = Hash.Sha256Hex(string.Join("\n", catalog.SourceHashes.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Key + "=" + row.Value)
                .Concat(catalog.Rejections.Select(row => row.Code + " " + row.Entry + " " + row.Path).OrderBy(row => row, StringComparer.Ordinal))));
            // The package identity preflight compares the Rundowns the host callbacks captured; it is part of startup admission.
            return new(Name, digest, snapshot.Startup ? ContentPackStartup.Admit(Kernel!, catalog) : catalog);
        }
        public PreparedContent Prepare(ContentChange change)
        {
            if (change.Unchanged) return PreparedContent.Carry(change.CurrentLeases!);
            return new Delivery(this, (ContentCatalog)change.Part.Value!, change.Redo);
        }

        private sealed class Delivery : PreparedContent
        {
            private readonly EntriesParticipant _owner;
            private readonly ContentCatalog _catalog;
            private readonly bool _redo;
            private ContentEntryGeneration? _generation, _previous;
            private ContentPlanAdmission? _previousPlans;
            internal Delivery(EntriesParticipant owner, ContentCatalog catalog, bool redo) { _owner = owner; _catalog = catalog; _redo = redo; }

            internal override void Commit(ContentGeneration next)
            {
                _previous = _owner._committed;
                _previousPlans = _contentPlans;
                var generation = _generation = new ContentEntryGeneration(next.Number, _previous, _redo);
                ContentPlanAdmission? admission = null;
                AdmittedContentSnapshot? admitted = null;
                generation.Deliver(() => (admission, admitted) = ContentPackStartup.Dispatch(Kernel!, Paths.BepInExRootPath, _catalog));
                _owner._committed = generation;
                _contentPlans = admission;
                next.Publish(Name, admitted!);
                Leases.Add(generation.Lease());
            }

            // The rows were replaced in place: the previous generation's lease is handed over, never revoked.
            internal override void Release(ContentLeases? previous) => _generation!.Release(_previous);

            internal override void Revert()
            {
                if (_generation == null) return;
                _owner._committed = _previous;
                _contentPlans = _previousPlans;
                _generation.Revert();
            }
        }
    }
}
