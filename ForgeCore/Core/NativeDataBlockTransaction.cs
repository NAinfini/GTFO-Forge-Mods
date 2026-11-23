using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeCore;

public static partial class NativeDataBlockTables
{
    /// <summary>One reversible change to the game's data tables, across any number of tables, by one owner. Stage
    /// <see cref="Add(object)"/>, <see cref="Replace(object)"/> and <see cref="Remove"/> steps (staging builds and checks the new
    /// objects but writes nothing), then <see cref="Commit"/>: every step is written into the table's three containers
    /// (<c>Wrapper.Blocks</c>, <c>s_blockByID</c>, <c>s_blockIDByName</c>) and every row read back through them. Any failure undoes
    /// every written step in reverse and then audits every table with ledger rows; an audit that still fails throws
    /// <see cref="NativeDataBlockAuditException"/> (logged once) and the owner stops its content until restart, while a clean
    /// rollback rethrows the original error with the tables as they were. A committed transaction holds the replaced and removed
    /// objects until <see cref="Release"/> (the owner confirmed the new content) or <see cref="Revert"/> (it did not: every step is
    /// undone and read back). The native <c>RemoveBlock</c> deletes a file and is never called: a removal takes the row out of the
    /// three containers itself and renumbers <c>Index</c> from the removed position on.</summary>
    public sealed class Transaction
    {
        private enum Kind { Add, Replace, Remove }
        private enum Phase { Open, Committed, Released, Reverted, Failed }

        private sealed class Step
        {
            internal Kind Kind;
            internal Type Table = null!;
            internal uint Id;
            internal string Name = "";
            /// <summary>The object in the table after the step (Add, Replace).</summary>
            internal object? Value;
            /// <summary>The object in the table before the step (Replace, Remove); held until Release.</summary>
            internal object? Old;
            internal string OldName = "";
            internal JsonElement? Supplied;
            internal object Blocks = null!, ById = null!, ByName = null!;
            internal int Position = -1;
            /// <summary>Remove: the <c>Index</c> of every later row before renumbering, restored by the undo.</summary>
            internal int[] Shifted = Array.Empty<int>();
        }

        private readonly List<Step> _steps = new();
        private readonly HashSet<(Type, uint)> _ids = new();
        private readonly HashSet<(Type, string)> _names = new();
        private Phase _phase;

        public Transaction(string owner)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("Name the owner (a mod ID).", nameof(owner));
            Owner = owner;
        }

        /// <summary>The ledger owner of every row this transaction adds, replaces or removes.</summary>
        public string Owner { get; }

        /// <summary>Stage a new row from a resolved definition: its source row must exist, CellJSON builds the native object now, and
        /// with <paramref name="strictFields"/> every supplied value must survive deserialization exactly.</summary>
        public void Add(NativeDataBlockRegistration definition, bool strictFields = false) => Stage(Kind.Add, definition, strictFields);

        /// <summary>Stage a new row built in code (a <c>DataBlocks.Clone</c> with changed fields).</summary>
        public void Add(object block) => Stage(Kind.Add, block, null);

        /// <summary>Stage a new object for a row this owner published (same table and ID; the name may change to a free one), put into
        /// the same slot of all three containers.</summary>
        public void Replace(NativeDataBlockRegistration definition, bool strictFields = false) => Stage(Kind.Replace, definition, strictFields);

        /// <summary>Stage a new code-built object for a row this owner published; see <see cref="Replace(NativeDataBlockRegistration, bool)"/>.</summary>
        public void Replace(object block) => Stage(Kind.Replace, block, null);

        /// <summary>Stage the removal of row <paramref name="id"/> of the <paramref name="type"/> table (<c>LevelLayout</c>), which this
        /// owner published.</summary>
        public void Remove(string type, uint id)
        {
            RequirePhase(Phase.Open);
            var table = NativeType("GameData." + type + "DataBlock");
            if (id == 0 || !_ids.Add((table, id))) throw new InvalidOperationException("Duplicate or invalid native block identity: " + type + "/" + id);
            _steps.Add(new Step { Kind = Kind.Remove, Table = table, Id = id });
        }

        private void Stage(Kind kind, NativeDataBlockRegistration definition, bool strict)
        {
            RequirePhase(Phase.Open);
            var table = NativeType("GameData." + definition.Type + "DataBlock");
            if (definition.SourceId is { } sourceId)
            {
                var source = Call(table, "GetBlock", sourceId);
                if (source == null || (uint)Get(source, "persistentID")! != sourceId)
                    throw new InvalidOperationException("Missing native source " + definition.Type + "/" + sourceId);
            }
            using var document = JsonDocument.Parse(definition.Json);
            if (strict) Validate(document.RootElement, table, definition.Name);
            var value = Deserialize(table, definition.Json);
            if (strict) Validate(document.RootElement, table, definition.Name, value, compare: true);
            if ((uint)Get(value, "persistentID")! != definition.PersistentId || (string?)Get(value, "name") != definition.Name)
                throw new InvalidOperationException("Native block identity changed during deserialization: " + definition.Name);
            Stage(kind, value, strict ? document.RootElement.Clone() : null);
        }

        private void Stage(Kind kind, object block, JsonElement? supplied)
        {
            RequirePhase(Phase.Open);
            var (table, id, name) = Identity(block);
            if (id == 0 || name.Length == 0 || !_ids.Add((table, id)) || !_names.Add((table, name)))
                throw new InvalidOperationException("Duplicate or invalid native block identity: " + name);
            _steps.Add(new Step { Kind = kind, Table = table, Id = id, Name = name, Value = block, Supplied = supplied });
        }

        /// <summary>Check every step against the tables and the ledger, then write and read back all of them. Nothing changes when a
        /// check fails (<see cref="NativeDataBlockConflictException"/> for an occupied ID or name, or a row this owner does not
        /// own); a failed write or readback is undone (see the class).</summary>
        public void Commit()
        {
            RequirePhase(Phase.Open);
            _phase = Phase.Failed;
            // Removals are checked and written first, then replacements, then additions: a name a removal or a rename of this
            // transaction lets go of is free for a later step of it (an owner's row whose ID changed keeps its name).
            var ordered = _steps.OrderBy(step => step.Kind == Kind.Remove ? 0 : step.Kind == Kind.Replace ? 1 : 2).ToArray();
            _steps.Clear(); _steps.AddRange(ordered);
            var released = new HashSet<(Type, string)>();
            foreach (var step in _steps) Check(step, released);
            var written = new List<Step>();
            try
            {
                foreach (var step in _steps) { written.Add(step); Write(step); }
                foreach (var step in _steps) ReadBack(step, reverted: false);
            }
            catch (Exception error)
            {
                Rollback(written, error);
                throw;
            }
            foreach (var step in _steps)
            {
                if (step.Kind == Kind.Remove) DataBlockLedger.Shared.Forget(step.Table, step.Id);
                else DataBlockLedger.Shared.Owns(step.Table, step.Id, Owner, step.Name, step.Value!);
            }
            _phase = Phase.Committed;
            // The game's caches follow the committed tables; a throw here leaves the transaction committed and revertible.
            foreach (var step in _steps)
            {
                if (step.Kind == Kind.Remove) Withdrawn(step.Old!);
                else Published(step.Value!);
            }
        }

        /// <summary>The owner confirmed the committed content: the replaced and removed objects are let go.</summary>
        public void Release()
        {
            RequirePhase(Phase.Committed);
            foreach (var step in _steps) step.Old = null;
            _phase = Phase.Released;
        }

        /// <summary>Undo a committed transaction the owner did not confirm: added rows leave, replaced rows get their old object back
        /// in the same slot, removed rows return to their position, and every row is read back. A failure audits as
        /// <see cref="Commit"/> does, against the state before the commit.</summary>
        public void Revert()
        {
            RequirePhase(Phase.Committed);
            _phase = Phase.Failed;
            foreach (var step in _steps)
            {
                if (step.Kind == Kind.Add) DataBlockLedger.Shared.Forget(step.Table, step.Id);
                else DataBlockLedger.Shared.Owns(step.Table, step.Id, Owner, step.Kind == Kind.Replace ? step.OldName : step.Name, step.Old!);
            }
            try
            {
                for (int index = _steps.Count - 1; index >= 0; index--) Undo(_steps[index]);
                foreach (var step in _steps) ReadBack(step, reverted: true);
            }
            catch (Exception error)
            {
                Audited(error);
                throw;
            }
            _phase = Phase.Reverted;
            foreach (var step in _steps)
            {
                if (step.Kind == Kind.Add) Withdrawn(step.Value!);
                else Published(step.Old!);
            }
        }

        private void RequirePhase(Phase phase)
        {
            if (_phase != phase) throw new InvalidOperationException("Native table transaction is " + _phase + ", not " + phase + ".");
        }

        // <paramref name="released"/>: the names earlier steps of this transaction take out of their table (removals, renames).
        private void Check(Step step, HashSet<(Type, string)> released)
        {
            step.Blocks = Get(Get(step.Table, "Wrapper")!, "Blocks")!;
            step.ById = Get(step.Table, "s_blockByID")!;
            step.ByName = Get(step.Table, "s_blockIDByName")!;
            string reason;
            if (step.Kind == Kind.Add)
            {
                if (Classify(step.Table, Owner, step.Id, step.Name, out reason, name => released.Contains((step.Table, name))) != DataBlockResult.Free)
                    throw new NativeDataBlockConflictException(reason);
                return;
            }
            if (!DataBlockLedger.Shared.TryGet(step.Table, step.Id, out var row) || row.Mod != Owner
                || !Has(step.ById, step.Id) || !SameObject(Call(step.ById, "get_Item", step.Id), row.Value))
                throw new NativeDataBlockConflictException("Native row is not owned by " + Owner + ": " + Label(step.Table) + "/" + step.Id
                    + (row.Mod == null ? "" : " (added by " + row.Mod + ")"));
            step.Old = row.Value;
            step.OldName = row.Name;
            if (step.Kind == Kind.Remove) { step.Name = row.Name; released.Add((step.Table, step.Name)); return; }
            if (step.Name == row.Name) return;
            if (Has(step.ByName, step.Name) && !released.Contains((step.Table, step.Name)))
                throw new NativeDataBlockConflictException("Native name collision: " + step.Name + " / " + Call(step.ByName, "get_Item", step.Name));
            released.Add((step.Table, row.Name));
        }

        private static int Find(object blocks, object value)
        {
            int count = Count(blocks);
            for (int index = 0; index < count; index++)
                if (SameObject(Call(blocks, "get_Item", index), value)) return index;
            return -1;
        }

        private static void Renumber(object blocks, int from)
        {
            int count = Count(blocks);
            for (int index = from; index < count; index++) Set(Call(blocks, "get_Item", index)!, "Index", index);
        }

        private static void Write(Step step)
        {
            switch (step.Kind)
            {
                case Kind.Add:
                    step.Position = Count(step.Blocks);
                    Set(step.Value!, "Index", step.Position);
                    Call(step.Blocks, "Add", step.Value);
                    Call(step.ById, "Add", step.Id, step.Value);
                    Call(step.ByName, "Add", step.Name, step.Id);
                    break;
                case Kind.Replace:
                    step.Position = Find(step.Blocks, step.Old!);
                    if (step.Position < 0) throw new InvalidOperationException("Native row is missing from its wrapper: " + step.OldName);
                    Set(step.Value!, "Index", Get(step.Old!, "Index"));
                    Call(step.Blocks, "set_Item", step.Position, step.Value);
                    Call(step.ById, "set_Item", step.Id, step.Value);
                    if (step.Name != step.OldName)
                    {
                        Call(step.ByName, "Remove", step.OldName);
                        Call(step.ByName, "Add", step.Name, step.Id);
                    }
                    break;
                case Kind.Remove:
                    step.Position = Find(step.Blocks, step.Old!);
                    if (step.Position < 0) throw new InvalidOperationException("Native row is missing from its wrapper: " + step.Name);
                    int count = Count(step.Blocks);
                    step.Shifted = new int[count - step.Position - 1];
                    for (int index = 0; index < step.Shifted.Length; index++)
                        step.Shifted[index] = (int)Get(Call(step.Blocks, "get_Item", step.Position + 1 + index)!, "Index")!;
                    Call(step.Blocks, "RemoveAt", step.Position);
                    Call(step.ById, "Remove", step.Id);
                    Call(step.ByName, "Remove", step.Name);
                    Renumber(step.Blocks, step.Position);
                    break;
            }
        }

        // Each undo works from object identity, not from what the write claims to have done, so it also cleans up a step that
        // threw halfway and does nothing to a step that never reached the tables.
        private static void Undo(Step step)
        {
            switch (step.Kind)
            {
                case Kind.Add:
                    {
                        int position = Find(step.Blocks, step.Value!);
                        if (position >= 0) { Call(step.Blocks, "RemoveAt", position); Renumber(step.Blocks, position); }
                        if (Has(step.ById, step.Id) && SameObject(Call(step.ById, "get_Item", step.Id), step.Value)) Call(step.ById, "Remove", step.Id);
                        if (!Has(step.ById, step.Id) && Has(step.ByName, step.Name) && (uint)Call(step.ByName, "get_Item", step.Name)! == step.Id)
                            Call(step.ByName, "Remove", step.Name);
                        break;
                    }
                case Kind.Replace:
                    {
                        int position = Find(step.Blocks, step.Value!);
                        if (position >= 0) Call(step.Blocks, "set_Item", position, step.Old);
                        if (Has(step.ById, step.Id) && SameObject(Call(step.ById, "get_Item", step.Id), step.Value)) Call(step.ById, "set_Item", step.Id, step.Old);
                        if (step.Name != step.OldName)
                        {
                            if (Has(step.ByName, step.Name) && (uint)Call(step.ByName, "get_Item", step.Name)! == step.Id) Call(step.ByName, "Remove", step.Name);
                            if (!Has(step.ByName, step.OldName)) Call(step.ByName, "Add", step.OldName, step.Id);
                        }
                        break;
                    }
                case Kind.Remove:
                    {
                        if (step.Position >= 0 && Find(step.Blocks, step.Old!) < 0)
                        {
                            int count = Count(step.Blocks);
                            int position = Math.Min(step.Position, count);
                            Call(step.Blocks, "Insert", position, step.Old);
                            // The removed row kept its own Index. Later rows get back the Index they had; when the table changed in between, they take their position.
                            if (position == step.Position && count - position == step.Shifted.Length)
                                for (int index = 0; index < step.Shifted.Length; index++)
                                    Set(Call(step.Blocks, "get_Item", position + 1 + index)!, "Index", step.Shifted[index]);
                            else Renumber(step.Blocks, position);
                        }
                        if (!Has(step.ById, step.Id)) Call(step.ById, "Add", step.Id, step.Old);
                        if (!Has(step.ByName, step.Name)) Call(step.ByName, "Add", step.Name, step.Id);
                        break;
                    }
            }
        }

        // Read every row back through the table's three containers (and GetBlock) as they are now: the same containers the step
        // wrote, holding exactly the expected object under its ID, name and slot, with strict values intact; a removed row is gone.
        private static void ReadBack(Step step, bool reverted)
        {
            if (!SameObject(Get(Get(step.Table, "Wrapper")!, "Blocks"), step.Blocks) || !SameObject(Get(step.Table, "s_blockByID"), step.ById)
                || !SameObject(Get(step.Table, "s_blockIDByName"), step.ByName))
                throw new InvalidOperationException("Native table containers were replaced: " + Label(step.Table));
            bool present = reverted ? step.Kind != Kind.Add : step.Kind != Kind.Remove;
            var expected = reverted ? step.Old : step.Kind == Kind.Remove ? step.Old : step.Value;
            string name = reverted && step.Kind == Kind.Replace ? step.OldName : step.Name;
            if (!present)
            {
                if (Has(step.ById, step.Id) || (Has(step.ByName, name) && (uint)Call(step.ByName, "get_Item", name)! == step.Id)
                    || Find(step.Blocks, expected!) >= 0)
                    throw new InvalidOperationException("Native row was not removed: " + Label(step.Table) + "/" + name);
                return;
            }
            var byId = Has(step.ById, step.Id) ? Call(step.ById, "get_Item", step.Id) : null;
            int position = Find(step.Blocks, expected!);
            var resolved = Call(step.Table, "GetBlock", step.Id);
            if (!SameObject(byId, expected) || position < 0 || !SameObject(resolved, expected) || !Has(step.ByName, name)
                || (uint)Call(step.ByName, "get_Item", name)! != step.Id || (uint)Get(expected!, "persistentID")! != step.Id
                || (string?)Get(expected!, "name") != name)
                throw new InvalidOperationException("Native table readback failed: " + Label(step.Table) + "/" + name);
            if (step.Kind == Kind.Replace && step.OldName != step.Name)
            {
                string left = reverted ? step.Name : step.OldName;
                if (Has(step.ByName, left) && (uint)Call(step.ByName, "get_Item", left)! == step.Id)
                    throw new InvalidOperationException("Native table kept a replaced name: " + Label(step.Table) + "/" + left);
            }
            if (!reverted && step.Supplied is { } supplied)
            {
                Validate(supplied, step.Table, name + ".byID", byId, compare: true);
                Validate(supplied, step.Table, name + ".Wrapper", Call(step.Blocks, "get_Item", position), compare: true);
                Validate(supplied, step.Table, name + ".GetBlock", resolved, compare: true);
            }
        }

        private void Rollback(List<Step> written, Exception error)
        {
            var failures = new List<Exception>();
            for (int index = written.Count - 1; index >= 0; index--)
            {
                try { Undo(written[index]); }
                catch (Exception undo) { failures.Add(undo); }
            }
            Audited(failures.Count == 0 ? error : new AggregateException(new[] { error }.Concat(failures)));
        }

        // A rollback is complete when every table with ledger rows (and every table this transaction touched) matches the ledger
        // again; otherwise the owner hears it once, in the log and as the exception, and the original error stays inside.
        private void Audited(Exception error)
        {
            var problems = AuditOwnership(_steps.Select(step => step.Table));
            if (problems.Count == 0) return;
            var audit = new NativeDataBlockAuditException(error, problems);
            Log.For(Owner).Error("core-data-blocks-audit-failed", audit.Message + " Stop this mod's content until the game restarts. " + error);
            throw audit;
        }
    }

    /// <summary>Check every table that has ledger rows: each row the ledger holds is the very object under its ID, its name maps to
    /// that ID and it sits in the wrapper; and every wrapper row is the object its ID resolves to, under its own name, with the three
    /// containers the same size. Returns one line per problem (none: consistent). It reads only; the original <c>Index</c> is not
    /// required to equal the position, because the game's own rows already differ.</summary>
    public static IReadOnlyList<string> AuditOwnership() => AuditOwnership(Array.Empty<Type>());

    private static IReadOnlyList<string> AuditOwnership(IEnumerable<Type> touched)
    {
        var problems = new List<string>();
        var ledger = DataBlockLedger.Shared.Rows.ToList();
        foreach (var table in ledger.Select(entry => entry.Row.Table).Concat(touched).Distinct())
        {
            try
            {
                var blocks = Get(Get(table, "Wrapper")!, "Blocks")!;
                var byId = Get(table, "s_blockByID")!;
                var byName = Get(table, "s_blockIDByName")!;
                string label = Label(table);
                int count = Count(blocks);
                var rows = new HashSet<object>();
                for (int index = 0; index < count; index++)
                {
                    var row = Call(blocks, "get_Item", index);
                    if (row == null) { problems.Add(label + ": empty wrapper slot " + index); continue; }
                    rows.Add(ObjectKey(row));
                    uint id = (uint)Get(row, "persistentID")!; string name = (string?)Get(row, "name") ?? "";
                    if (!Has(byId, id) || !SameObject(Call(byId, "get_Item", id), row))
                        problems.Add(label + "/" + id + ": wrapper slot " + index + " is not the row its ID resolves to");
                    if (!Has(byName, name) || (uint)Call(byName, "get_Item", name)! != id)
                        problems.Add(label + "/" + id + ": name " + name + " does not map to its ID");
                }
                if (Count(byId) != count || Count(byName) != count)
                    problems.Add(label + ": " + count + " wrapper rows, " + Count(byId) + " IDs, " + Count(byName) + " names");
                foreach (var (id, row) in ledger.Where(entry => entry.Row.Table == table))
                {
                    if (!Has(byId, id) || !SameObject(Call(byId, "get_Item", id), row.Value) || !rows.Contains(ObjectKey(row.Value)))
                        problems.Add(label + "/" + id + ": not the object " + row.Mod + " published");
                    if (!Has(byName, row.Name) || (uint)Call(byName, "get_Item", row.Name)! != id)
                        problems.Add(label + "/" + id + ": " + row.Mod + "'s name " + row.Name + " does not map to it");
                }
            }
            catch (Exception error) { problems.Add(Label(table) + ": unreadable (" + error.GetBaseException().Message + ")"); }
        }
        return problems;
    }
}
