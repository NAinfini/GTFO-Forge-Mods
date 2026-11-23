using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// The scope a variable lives in, as the plan header declares it and as a step names it at runtime. Six kinds
/// exist and each one has exactly one lifecycle: <c>level</c> is restored on checkpoint reload, <c>player</c> survives a down but hands a late joiner its initial value,
/// <c>weapon</c> is one entry per player-and-slot pair, <c>object</c> is cleared when the object is retired (a
/// deployed item returning to its owner) and restored otherwise, <c>enemy</c> disappears with the enemy, and
/// <c>module</c> is one entry per mount point.
/// </summary>
internal static class VariableScopeKinds
{
    public const string Level = "level";
    public const string Player = "player";
    public const string Weapon = "weapon";
    public const string Object = "object";
    public const string Enemy = "enemy";
    public const string Module = "module";
    public static readonly IReadOnlyList<string> All = new[] { Level, Player, Weapon, Object, Enemy, Module };
}

/// <summary>The declared value types. The wire spelling is the type's own name: a variable's declared type is the
/// only thing that says how its payload is read, so the payload itself never carries a tag.
///
/// <see cref="PortType"/> is the translation into the wire port vocabulary a plan's frame is built from: a
/// `g-var` node declares its own value port through the step's `value_type` structural parameter, and the port type
/// a variable compiles to has to be the same one its declared type reads as. The two tables are one rule in two
/// spellings, which is why the mapping lives here rather than in either the loader or the dispatcher.</summary>
internal static class VariableValueTypes
{
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Flag = "flag";
    public const string Text = "text";
    public const string Entity = "entity";
    public const string Handle = "handle";
    public const string SoundHandle = "handle:sound";
    internal const string EntityLifeEffectHandle = "handle:effect:entity_life";
    public static readonly IReadOnlyList<string> All = new[] { Number, Flag, Text, Entity, Handle, SoundHandle, EntityLifeEffectHandle, Integer };
    /// <summary>A chosen resource: the declared type is `resource:&lt;kind&gt;` for a kind in <see cref="ResourceKinds"/>,
    /// and the value is one reference of exactly that kind, `{resourceKind, resourceId}` — the frame's own spelling of
    /// a resource, so a value moves between a variable and a resource port with no translation. The kind is part of
    /// the type, so a variable of one kind can never hold another, and nothing converts between two kinds.</summary>
    internal const string ResourcePrefix = "resource:";
    /// <summary>The resource kinds a variable may hold, which are also the members of a read or write step's
    /// `value_type` after the plain ones. A kind joins this list together with its `resource:&lt;kind&gt;` members in the
    /// reviewed variable rows.</summary>
    internal static readonly IReadOnlyList<string> ResourceKinds = new[] { "enemy", "weapon", "tool" };
    /// <summary>Whether a declared type or a step's `value_type` member names a resource, and which kind.</summary>
    internal static bool IsResource(string valueType, out string kind)
    {
        kind = "";
        if (!valueType.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return false;
        kind = valueType.Substring(ResourcePrefix.Length);
        return ResourceKinds.Contains(kind);
    }
    /// <summary>Whether a header names one of the declared value types, resource members included.</summary>
    internal static bool IsDeclared(string valueType) => All.Contains(valueType) || IsResource(valueType, out _);
    /// <summary>The plain port types a variable node may compile to, in declaration order. A `g-message` node's
    /// `value_type` parameter offers exactly these members.</summary>
    public static readonly IReadOnlyList<string> PortTypes = new[] { "number", "boolean", "string", "entity", "integer" };
    /// <summary>The encounter effect member, spelled the way a structural member names a guarded handle:
    /// the `effect` kind with the `encounter` lifetime the wave and alarm rows publish. A handle port has to name its
    /// kind and lifetime, and this member is what carries both, so a declaration of type <see cref="Handle"/> is
    /// read and written through it.</summary>
    public const string EffectHandlePort = "handle:effect";

    internal static string PortType(string valueType) => valueType switch
    {
        Integer => "integer",
        Number => "number",
        Flag => "boolean",
        Text => "string",
        Entity => "entity",
        Handle or SoundHandle or EntityLifeEffectHandle => "handle",
        _ when IsResource(valueType, out _) => "resource",
        _ => throw new RuntimeContractException("variable-type", valueType)
    };

    /// <summary>Whether a step's `value_type` member moves the class a declaration holds. A guarded handle member
    /// names its class before the colon, so `handle:effect` moves a `handle` declaration.</summary>
    internal static bool IsPortTypeOf(string portType, string valueType)
    {
        if (valueType == Handle) return portType == EffectHandlePort;
        if (valueType == SoundHandle) return portType == SoundHandle;
        if (valueType == EntityLifeEffectHandle) return portType == EntityLifeEffectHandle;
        // A resource member carries its kind, and the kind has to be the declared one: `resource:weapon` never moves a
        // `resource:enemy` variable.
        if (IsResource(valueType, out _)) return portType == valueType;
        return portType == PortType(valueType);
    }
}

/// <summary>
/// One variable declaration from the plan header. <see cref="Name"/> is the author's own id and is the only name a
/// step uses; <see cref="Scope"/> is a member of <see cref="VariableScopeKinds"/>; <see cref="Type"/> is a member of
/// <see cref="VariableValueTypes"/>; and <see cref="Initial"/> is the value a fresh entry gets — validated against
/// the declared type at load, so a write can never put a value of the wrong type into the entry.
/// </summary>
internal sealed record VariableDeclaration(string Name, string Scope, string Type, JsonElement Initial)
{
    internal int Index { get; set; } = -1;
    /// <summary>The resource kind a `resource:&lt;kind&gt;` declaration holds, decided once here, or null for every other type.</summary>
    internal string? ResourceKind { get; } = VariableValueTypes.IsResource(Type, out var kind) ? kind : null;
    /// <summary>The owner's answer for the declared initial reference, cached on the first read in a world so a read of
    /// an unwritten address resolves nothing again. Dropped with the values when the world ends.</summary>
    internal ResourceRef? InitialResolved { get; set; }
}

/// <summary>
/// One variable's address at runtime: which declaration it is, which scope kind it lives in, and the subject that
/// scope instance belongs to. <see cref="Subject"/> is the entity id for
/// <c>player</c>/<c>enemy</c>/<c>object</c>, empty for <c>level</c>, and the serialized plan and complete host identity
/// for <c>module</c>; <see cref="Slot"/> is the weapon slot for <c>weapon</c> and -1 everywhere else.
/// The pairs that belong together are fixed by the scope kind, so a scope with a subject it cannot use is refused
/// instead of quietly keyed under something else.
/// </summary>
internal sealed record RuntimeVariableScope(string Kind, string Subject, int Slot)
{
    public static RuntimeVariableScope Level() => new(VariableScopeKinds.Level, "", -1);
    public static RuntimeVariableScope Player(string entityId) => new(VariableScopeKinds.Player, entityId, -1);
    public static RuntimeVariableScope Weapon(string entityId, int slot) => new(VariableScopeKinds.Weapon, entityId, slot);
    public static RuntimeVariableScope Object(string entityId) => new(VariableScopeKinds.Object, entityId, -1);
    public static RuntimeVariableScope Enemy(string entityId) => new(VariableScopeKinds.Enemy, entityId, -1);
    public static RuntimeVariableScope Module(string mount) => new(VariableScopeKinds.Module, mount, -1);
}

/// <summary>The execution address; public scopes and snapshot text are converted only at their boundary.</summary>
internal readonly record struct VariableScopeAddress(string Kind, string Subject, int Slot)
{
    internal string? Plan { get; init; }
    internal EntityReference? Host { get; init; }
    internal static VariableScopeAddress Module(string plan, EntityReference? host)
        => new(VariableScopeKinds.Module, host?.Id ?? "", -1) { Plan = plan, Host = host };
    internal string WireSubject => Kind == VariableScopeKinds.Module ? MountKey(Plan!, Host) : Subject;
    internal static string MountKey(string plan, EntityReference? host)
        => RuntimeJson.StableText(RuntimeJson.From(new object?[] { plan, host?.Id, host?.WorldEpoch, host?.LifeEpoch }));
    internal static VariableScopeAddress FromWire(string kind, string subject, int slot)
    {
        if (kind != VariableScopeKinds.Module) return new(kind, subject, slot);
        var tuple = RuntimeJson.Parse(subject);
        RuntimeJson.Require(tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() == 4 && slot == -1,
            "variable-subject", subject);
        var plan = RuntimeJson.Text(tuple[0]);
        EntityReference? host = null;
        if (tuple[1].ValueKind != JsonValueKind.Null)
            host = new EntityReference(RuntimeJson.Text(tuple[1]), RuntimeJson.Integer(tuple[2]), RuntimeJson.Integer(tuple[3]));
        else RuntimeJson.Require(tuple[2].ValueKind == JsonValueKind.Null && tuple[3].ValueKind == JsonValueKind.Null,
            "variable-subject", subject);
        return Module(plan, host);
    }
}

/// <summary>
/// One stored variable value: the scope it lives in, its declaration, its own address, and the payload. The
/// payload travels as JSON because a variable is read by a later step's port, and the port type is what decodes it;
/// <see cref="RuntimeVariableStore"/> is the only writer, so the payload's shape always agrees with the declared
/// type.
/// </summary>
public sealed record RuntimeVariableEntry(string Name, string ScopeKind, string Subject, int Slot, string Type, JsonElement Value)
{
    /// <summary>The address as the wire spells it — the touch set, the cleared set and a delta's `removed` rows all
    /// carry this text. It is built once, when the entry is: an entry is only made by a write that changed something.</summary>
    internal string Key { get; } = KeyOf(Name, ScopeKind, Subject, Slot);
    internal VariableAddress Address => new(Name, ScopeKind, Subject, Slot);
    internal static string KeyOf(string name, string kind, string subject, int slot)
        => name + "\u0000" + kind + "\u0000" + subject + "\u0000" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One address as the store looks it up: the same four parts <see cref="RuntimeVariableEntry.KeyOf"/>
/// joins, held apart so a read or a write that finds its entry builds no text.</summary>
internal readonly record struct VariableAddress(string Name, string Kind, string Subject, int Slot)
{
    internal static VariableAddress Of(string name, RuntimeVariableScope scope) => new(name, scope.Kind, scope.Subject, scope.Slot);
}

/// <summary>
/// The variable store: one table of values keyed by declaration, scope kind, subject and slot, with the scope
/// lifecycle rules applied by the kernel. It owns no world state and never asks a provider a question — what a
/// scope's subject is comes from the step or from the engine event the kernel already dispatched.
///
/// A value only changes through <see cref="Set"/> or <see cref="ClearScope"/>, and both mark the cell changed (or
/// record the removed key), so the host can send exactly what changed without comparing the whole store. What is
/// marked is what no body has carried yet rather than what one advance wrote: it is emptied by the <see cref="Settle"/>
/// of the export that framed a body, so a change made while a complete document is still travelling stays owed and leaves as one delta
/// after that document instead of making the document be framed again.
/// </summary>
internal sealed class RuntimeVariableStore
{
    private struct Cell
    {
        internal VariableDeclaration? Declaration;
        internal VariableScopeAddress Scope;
        internal JsonElement Value;
        /// <summary>For a resource value: the owner's answer for it, kept so a read hands it on without a lookup. Never
        /// replicated or checkpointed; a restored value is resolved on its first read.</summary>
        internal ResourceRef? Resolved;
        internal bool Changed;
        internal RuntimeVariableEntry Entry => new(Declaration!.Name, Scope.Kind, Scope.WireSubject,
            Scope.Slot, Declaration.Type, Value);
    }
    // The declared entry budget is also the physical table size. Dispatch addresses cells by a declaration
    // index and a complete scope identity; JSON names and replication keys are rendered only at boundaries.
    private readonly Cell[] cells = new Cell[RuntimeVariableContract.MaximumEntries];
    private readonly List<string> identities = new();
    // `high` is one past the last occupied cell: a read of an address nothing wrote, a settle and a scope clear walk the
    // cells in use and not the whole declared table.
    private int count, changed, high;
    private readonly Dictionary<string, Dictionary<string, VariableDeclaration>> declarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> cleared = new(StringComparer.Ordinal);
    private readonly HashSet<string> once = new(StringComparer.Ordinal);

    internal int Count => count;

    /// <summary>
    /// Whether anything is owed to the session: the wire decides with this whether an advance owes a body at all, and a body carries every address
    /// it framed and is what settles them. A change no body has carried therefore keeps this true across the
    /// advances that follow it, and a tick that wrote nothing new owes nothing, so the host sends no empty message
    /// per tick.
    /// </summary>
    internal bool Dirty => changed > 0 || cleared.Count > 0;

    /// <summary>A detached working image used to validate and assemble a restore before the live store is swapped.</summary>
    internal RuntimeVariableStore Fork()
    {
        var copy = new RuntimeVariableStore { count = count, changed = changed, high = high };
        Array.Copy(cells, copy.cells, cells.Length);
        copy.identities.AddRange(identities);
        foreach (var table in declarations)
            copy.declarations.Add(table.Key,
                new Dictionary<string, VariableDeclaration>(table.Value, StringComparer.Ordinal));
        copy.cleared.UnionWith(cleared);
        copy.once.UnionWith(once);
        return copy;
    }

    /// <summary>
    /// Marks what is owed as carried by the body an export just framed. Called by the export that built that body —
    /// the whole document, which contains every value, or the delta, which contains the touched and cleared
    /// addresses — so the next body's writes are what makes the set non-empty again. An address written after the
    /// framing is not in that body and stays owed.
    /// </summary>
    internal void Settle()
    {
        cleared.Clear();
        for (var i = 0; i < high; i++) cells[i].Changed = false;
        changed = 0;
    }

    /// <summary>The declarations of one plan, by name. Each plan header declares its own set and a variable is read
    /// or written by the plan that declared it, so the sets are kept apart rather than merged into one namespace two
    /// plans could collide in. A step whose plan declared nothing reads no variable at all.</summary>
    internal IReadOnlyDictionary<string, VariableDeclaration> Declarations(string planId)
        => declarations.TryGetValue(planId, out var rows) ? rows : Empty;

    private static readonly IReadOnlyDictionary<string, VariableDeclaration> Empty =
        new Dictionary<string, VariableDeclaration>(StringComparer.Ordinal);

    /// <summary>
    /// Replaces one plan's whole set, which is what a load or a reload of that plan does. The values are untouched:
    /// a reload restores the plan's behaviour, and the checkpoint, not the file, owns the values.
    ///
    /// A variable name is one address in the world, so two loaded plans may share it only by declaring it
    /// identically (same scope, type and initial value); a second declaration that disagrees is refused by name
    /// instead of silently re-keying a value the first plan is still reading. The refusal is the loader's, which is
    /// why a conflicting plan is rejected whole.
    ///
    /// A value outlives the plan that wrote it, but only under a declaration that can hold it: a name declared here
    /// with another type or another scope drops the payload the previous declaration left behind, so a later plan
    /// starts from its own declared initial value instead of reading the previous world's bytes as a value its
    /// contract never described. The same name and shape keeps its value, which is what a reload of one behaviour
    /// and a checkpoint restore both rely on.
    /// </summary>
    internal void Declare(string planId, IReadOnlyList<VariableDeclaration> rows)
    {
        var table = new Dictionary<string, VariableDeclaration>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            table[row.Name] = row;
        }
        foreach (var row in table.Values)
            foreach (var existing in declarations)
            {
                if (existing.Key == planId || !existing.Value.TryGetValue(row.Name, out var other)) continue;
                RuntimeJson.Require(other.Scope == row.Scope && other.Type == row.Type && Same(other.Initial, row.Initial),
                    "variable-conflict", row.Name);
            }
        // Admission is read-only until every conflict has been checked, including the caller's indices.
        foreach (var row in table.Values)
        {
            row.Index = identities.IndexOf(row.Name);
            if (row.Index < 0) { row.Index = identities.Count; identities.Add(row.Name); }
        }
        DropIncompatible(table);
        if (table.Count == 0) declarations.Remove(planId); else declarations[planId] = table;
    }

    /// <summary>
    /// Drops every stored value whose name this plan declares with another type or another scope than the value was
    /// written under. The declaration is the contract a reader is handed, and an address is (name, scope, subject,
    /// slot): a payload of another type under the same address would be read as the new type, so it goes — and it
    /// goes through the touch set, because a client holding it has to be told it is gone rather than left with bytes
    /// no declaration owns. One pass over the table, whatever the plan declares.
    /// </summary>
    private void DropIncompatible(Dictionary<string, VariableDeclaration> declared)
    {
        var dropped = Entries().Where(entry => declared.TryGetValue(entry.Name, out var declaration)
            && (!string.Equals(entry.Type, declaration.Type, StringComparison.Ordinal)
                || !string.Equals(entry.ScopeKind, declaration.Scope, StringComparison.Ordinal))).ToArray();
        Remove(dropped);
    }

    /// <summary>Removes entries the table holds and owes each removal to the next body.</summary>
    private void Remove(RuntimeVariableEntry[] removed)
    {
        foreach (var entry in removed)
        {
            var declaration = Declared(entry.Name);
            var index = Find(declaration?.Index ?? identities.IndexOf(entry.Name), VariableScopeAddress.FromWire(entry.ScopeKind, entry.Subject, entry.Slot));
            if (index < 0) continue;
            if (cells[index].Changed) changed--;
            cells[index] = default; count--;
            while (high > 0 && cells[high - 1].Declaration == null) high--;
            cleared.Add(entry.Key);
        }
    }

    /// <summary>Every loaded plan's declarations, merged by name. Names are unique per world (see
    /// <see cref="Declare"/>), so this is the one table a step, and a checkpoint restore all
    /// resolve a variable through.</summary>
    internal IEnumerable<VariableDeclaration> Declarations()
        => declarations.Values.SelectMany(table => table.Values).OrderBy(row => row.Name, StringComparer.Ordinal);

    /// <summary>The declaration a name resolves to in this world, or null when no loaded plan declares it.</summary>
    internal VariableDeclaration? Declared(string name)
    {
        VariableDeclaration? found = null;
        foreach (var entry in declarations.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (entry.Value.TryGetValue(name, out var row)) { if (found != null) return found; found = row; }
        return found;
    }

    /// <summary>Forgets a plan's declarations, together with the latches keyed by that plan's own nodes. Its values
    /// stay: a variable whose plan unloaded is still a value of the world, and a plan loaded back under the same id
    /// reads what the world holds.
    ///
    /// The latch key's plan half is compared, not a prefix of the whole key: a plan whose id begins with this one's
    /// id plus the separator is another plan, and its latches are not this unload's to drop.</summary>
    internal void ForgetPlan(string planId)
    {
        declarations.Remove(planId);
        foreach (var key in once.Where(key => LatchPlanOf(key) == planId).ToArray()) once.Remove(key);
    }

    /// <summary>
    /// The plan half of one latch key, or null where the text is not a key this store wrote. The key is read from
    /// the right because that is the end the node-id grammar makes unambiguous: a step node is
    /// <c>[A-Za-z][A-Za-z0-9_-]*</c> and can never contain the separator, so the last separator is always the
    /// key's own and everything before it is the plan id — dots, colons and all.
    /// </summary>
    internal static string? LatchPlanOf(string key)
    {
        var step = key.LastIndexOf(LatchSeparator);
        return step <= 0 ? null : key[..step];
    }

    /// <summary>One latch key, written the one way <see cref="LatchPlanOf"/> reads it: the plan and the `once`
    /// card's own node. The entrypoint is not part of it, so a card reached from several triggers latches once.</summary>
    internal static string LatchKeyOf(string planId, string stepNode) => planId + LatchSeparator + stepNode;

    private const char LatchSeparator = ':';

    internal VariableDeclaration? Declared(string planId, string name)
        => declarations.TryGetValue(planId, out var rows) && rows.TryGetValue(name, out var row) ? row : null;

    /// <summary>Every entry of the current world, ordered so two stores holding the same values encode the same
    /// bytes. Deterministic order is what makes the snapshot comparable across peers and across a checkpoint.</summary>
    internal IReadOnlyList<RuntimeVariableEntry> Snapshot()
    {
        var rows = Entries().ToArray();
        Array.Sort(rows, EntryOrder);
        return rows;
    }

    /// <summary>The one order a document lists entries in: name, scope kind and subject ordinal, then slot.</summary>
    private static readonly Comparison<RuntimeVariableEntry> EntryOrder = static (left, right) =>
    {
        var order = string.CompareOrdinal(left.Name, right.Name);
        if (order == 0) order = string.CompareOrdinal(left.ScopeKind, right.ScopeKind);
        if (order == 0) order = string.CompareOrdinal(left.Subject, right.Subject);
        return order != 0 ? order : left.Slot.CompareTo(right.Slot);
    };

    /// <summary>The entries this table wrote and no body has carried yet, for the delta a client applies. Reading the
    /// touch set does not clear it: one host advance may be handed to several recipients, and the delta belongs to
    /// the addresses rather than to the advance. The addresses the same advance cleared are not entries any more, so
    /// they travel separately — see <see cref="ClearedKeys"/>. Only the owed entries are sorted, in the order
    /// <see cref="Snapshot"/> lists them.</summary>
    internal IReadOnlyList<RuntimeVariableEntry> Touched()
    {
        var rows = new List<RuntimeVariableEntry>(changed);
        for (var i = 0; i < high; i++)
            if (cells[i].Declaration != null && cells[i].Changed) rows.Add(cells[i].Entry);
        rows.Sort(EntryOrder);
        return rows;
    }

    /// <summary>The addresses removed and not yet carried by a body, as the keys they were removed under. A removal
    /// has to travel explicitly: a client that simply does not hear about an address goes on holding the value, and
    /// leaving the removal out of the delta would be indistinguishable from an advance that never mentioned it. The
    /// set is long-lived the same way the touched entries are: a removal nothing has carried stays in it.</summary>
    internal IReadOnlyList<string> ClearedKeys
    {
        get
        {
            var keys = new string[cleared.Count];
            cleared.CopyTo(keys);
            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }
    }

    /// <summary>Whether one address currently holds a value. A read of an address that holds none is answered from
    /// the declaration's initial value, which is why the store keeps the declaration and not just the value.</summary>
    internal bool Has(string name, RuntimeVariableScope scope) => Find(identities.IndexOf(name), VariableScopeAddress.FromWire(scope.Kind, scope.Subject, scope.Slot)) >= 0;

    /// <summary>The value at one address, or the declaration's initial value when the address holds none yet. The
    /// caller has already checked the declaration exists, so the initial value it hands back is the declared one and
    /// never a guess.</summary>
    internal JsonElement Read(VariableDeclaration declaration, RuntimeVariableScope scope)
        => Read(declaration, VariableScopeAddress.FromWire(scope.Kind, scope.Subject, scope.Slot));

    internal JsonElement Read(VariableDeclaration declaration, VariableScopeAddress scope)
    {
        var index = Find(declaration.Index, scope);
        return index >= 0 ? cells[index].Value : declaration.Initial;
    }

    /// <summary>The same read for a resource variable: the value and the owner's answer for it, which the cell (or the
    /// declaration, for an address nothing has written) holds once it has been resolved, and null before.</summary>
    internal JsonElement Read(VariableDeclaration declaration, VariableScopeAddress scope, out ResourceRef? resolved)
    {
        var index = Find(declaration.Index, scope);
        resolved = index >= 0 ? cells[index].Resolved : declaration.InitialResolved;
        return index >= 0 ? cells[index].Value : declaration.Initial;
    }

    /// <summary>Keeps the owner's answer for what an address currently holds, so the next read resolves nothing.</summary>
    internal void Remember(VariableDeclaration declaration, VariableScopeAddress scope, ResourceRef resolved)
    {
        var index = Find(declaration.Index, scope);
        if (index >= 0) cells[index].Resolved = resolved; else declaration.InitialResolved = resolved;
    }

    private int Find(int declaration, VariableScopeAddress scope)
    {
        for (var i = 0; i < high; i++)
            if (cells[i].Declaration is { } row && row.Index == declaration && cells[i].Scope == scope) return i;
        return -1;
    }

    private IEnumerable<RuntimeVariableEntry> Entries(string? kind = null)
    {
        for (var i = 0; i < high; i++)
            if (cells[i].Declaration != null && (kind == null || cells[i].Scope.Kind == kind)) yield return cells[i].Entry;
    }

    /// <summary>
    /// Writes one address, returning whether the value actually changed. Writing the value an address already holds
    /// is not a change, so it is neither touched nor replicated: that is what keeps a per-tick counter from
    /// producing an identical delta every tick.
    ///
    /// A write that would open a <em>new</em> address past the table's cap is refused by name; an update to an
    /// address the table already holds is always allowed, because the cap bounds how many addresses a world may
    /// hold, not how often one of them may change.
    /// </summary>
    internal bool Set(VariableDeclaration declaration, RuntimeVariableScope scope, JsonElement value)
        => Set(declaration, VariableScopeAddress.FromWire(scope.Kind, scope.Subject, scope.Slot), value.Clone());

    internal bool Set(VariableDeclaration declaration, VariableScopeAddress scope, JsonElement value, ResourceRef? resolved = null)
    {
        RuntimeJson.Require(declaration.Index >= 0, "variable-undeclared", declaration.Name);
        // A resource variable declared with a default always holds a resource, which is what lets the compiler read it
        // without a when-present step: a write of nothing is refused by name instead of clearing it.
        RuntimeJson.Require(declaration.ResourceKind == null || declaration.Initial.ValueKind == JsonValueKind.Null || value.ValueKind != JsonValueKind.Null,
            "variable-value", declaration.Name);
        var index = Find(declaration.Index, scope);
        if (index >= 0 && Same(cells[index].Value, value)) { cells[index].Resolved ??= resolved; return false; }
        if (index < 0)
        {
            RequireRoom(count, 1, declaration.Name);
            for (index = 0; cells[index].Declaration != null; index++) { }
            cells[index].Declaration = declaration; cells[index].Scope = scope; count++;
            if (index >= high) high = index + 1;
        }
        cells[index].Value = value;
        cells[index].Resolved = resolved;
        if (!cells[index].Changed) { cells[index].Changed = true; changed++; }
        return true;
    }

    /// <summary>
    /// The one budget question the store asks: may <paramref name="added"/> more addresses join a table that already
    /// holds <paramref name="held"/>? The cap is the declared one and the refusal is explicit, so a snapshot, a
    /// delta or a write cannot grow the table past it — and a caller that arrives with more rows than the cap holds
    /// is refused before any of them is allocated.
    /// </summary>
    private static void RequireRoom(int held, int added, string at)
        => RuntimeJson.Require(added <= RuntimeVariableContract.MaximumEntries && held + added <= RuntimeVariableContract.MaximumEntries,
            RuntimeVariableContract.BudgetCode, at);

    /// <summary>
    /// The rows of one document field, bounded by the table's own budget before any of them is read: a body that
    /// carries more addresses than the table may hold is refused by name here, so the loop that would allocate them
    /// never starts. The generic row reader's own cap is deliberately not the one asked about — this table's cap is
    /// its own number, and a legal body at the cap is not the wrong shape.
    /// </summary>
    private static JsonElement.ArrayEnumerator RowsWithinCap(JsonElement document, string field, string at)
    {
        var rows = document.GetProperty(field);
        RuntimeJson.Require(rows.ValueKind == JsonValueKind.Array, "array-required", field);
        RequireRoom(0, rows.GetArrayLength(), at);
        return rows.EnumerateArray();
    }

    /// <summary>
    /// The row count of one delta list, bounded by the table's own budget before any row is read, as
    /// <see cref="RowsWithinCap"/> bounds a document's. A removed address is its key text: the declaration name, the
    /// scope kind, the subject and the slot joined by a separator, and a key nothing holds is simply not removed.
    /// </summary>
    private static int Rows(System.IO.BinaryReader reader)
    {
        var count = reader.Read7BitEncodedInt();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("row count out of range");
        RequireRoom(0, count, "delta");
        return count;
    }

    /// <summary>Clears one address. Clearing an address that holds nothing is a no-op rather than a touch, so a
    /// deployment returning twice does not replicate twice.</summary>
    internal bool Clear(string name, RuntimeVariableScope scope)
    {
        var index = Find(identities.IndexOf(name), VariableScopeAddress.FromWire(scope.Kind, scope.Subject, scope.Slot));
        if (index < 0) return false;
        Remove(new[] { cells[index].Entry });
        return true;
    }

    /// <summary>
    /// Clears every entry of one scope kind, or of one subject inside one kind, and reports how many entries went
    /// away. The cleared keys are touched because a client has to learn that they are gone; a snapshot omitting them
    /// is the same answer, and both are delivered.
    /// </summary>
    internal int ClearScope(string kind, string? subject = null)
    {
        // The scope is compared on the cell, so an entry (and its key text) is only built for the ones that go.
        var removed = new List<RuntimeVariableEntry>();
        for (var i = 0; i < high; i++)
            if (cells[i].Declaration != null && cells[i].Scope.Kind == kind
                && (subject == null || cells[i].Scope.WireSubject == subject)) removed.Add(cells[i].Entry);
        Remove(removed.ToArray());
        return removed.Count;
    }

    /// <summary>
    /// Clears every module-scope entry of one plan. A module value's subject is its mount point, and the mount point
    /// is <see cref="MountKey"/>: the plan and complete host identity. The plan half is compared exactly, read
    /// back by <see cref="MountPlanOf"/> — never a prefix of the joined text, because a plan id is arbitrary author
    /// text and may itself contain the separator, so unloading `forge` must leave `forge/other`'s values alone.
    /// </summary>
    internal int ClearModuleScope(string planId)
    {
        var removed = Entries(VariableScopeKinds.Module)
            .Where(entry => string.Equals(MountPlanOf(entry.Subject), planId, StringComparison.Ordinal)).ToArray();
        Remove(removed);
        return removed.Length;
    }

    /// <summary>
    /// The one canonical spelling of a module variable's mount point, and the one place it is written: the plan that
    /// owns the behaviour and the host's id, world epoch and life epoch. Serialization belongs to snapshot and
    /// replication boundaries; dispatch uses the resolved scope struct directly.
    /// </summary>
    internal static string MountKey(string planId, EntityReference? host) => VariableScopeAddress.MountKey(planId, host);

    /// <summary>The exact plan identity in one serialized module address; malformed addresses are refused.</summary>
    internal static string? MountPlanOf(string mount)
    {
        return VariableScopeAddress.FromWire(VariableScopeKinds.Module, mount, -1).Plan;
    }

    /// <summary>Drops every value and every latch without touching the declarations: a world ended, and a value from
    /// the previous world names entities that no longer exist. The touch set is left holding the keys so the removal
    /// is what the next delta carries.</summary>
    internal void ClearValues()
    {
        foreach (var entry in Entries()) cleared.Add(entry.Key);
        Array.Clear(cells, 0, cells.Length); count = changed = high = 0;
        // The owner's answers belong to the world they were given in.
        foreach (var table in declarations.Values)
            foreach (var declaration in table.Values) declaration.InitialResolved = null;
        once.Clear();
    }

    /// <summary>The latch <c>once</c> keeps: a `(plan, step)` pair is one card's own guard, shared by every
    /// entrypoint that reaches the card. The method is the whole check-and-set in one call, so two dispatches of
    /// the same latched step in one tick cannot both see it unset.
    /// </summary>
    internal bool ClaimOnce(string key)
    {
        return once.Add(key);
    }

    /// <summary>The `once` latches of the whole world, in one stable order: the checkpoint writer carries them and
    /// nothing else reads them.</summary>
    internal IReadOnlyList<string> Latches => once.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    /// <summary>The latches and values one checkpoint has to carry. A checkpoint restores the same world, so the
    /// same world epoch comes back with it and the snapshot's own epoch is what proves the entries belong to it.
    /// <paramref name="gates"/> is the trigger gate table, already serialized by the store that owns it: the two
    /// tables are written as properties of one checkpoint but neither is a variable of the other's store.</summary>
    internal string Save(long worldEpoch, string gates)
    {
        // The gate table arrives as text from the store that owns it; it is read and written back in the document's
        // stable spelling, so a gate text in any key order frames to the same bytes.
        var stableGates = RuntimeJson.StableText(RuntimeJson.Parse(gates));
        var writer = BeginDocument();
        writer.WriteStartObject();
        WriteEntries(writer, Snapshot());
        writer.WritePropertyName("gates");
        writer.WriteRawValue(stableGates, skipInputValidation: true);
        writer.WriteStartArray("once");
        foreach (var latch in Latches) writer.WriteStringValue(latch);
        writer.WriteEndArray();
        writer.WriteNumber("worldEpoch", worldEpoch);
        writer.WriteEndObject();
        return EndDocument(writer);
    }

    /// <summary>The strict UTF-8 a delta's texts are read in: a text that is not UTF-8 refuses the body.</summary>
    internal static readonly System.Text.UTF8Encoding Text = new(false, true);
    private const byte NamedType = byte.MaxValue;

    /// <summary>
    /// One advance's own delta, in one stable order: the addresses it removed (each its key text), then the entries it
    /// wrote. An entry is its name, its scope kind as its place in <see cref="VariableScopeKinds.All"/>, its subject, its
    /// slot + 1, its type as its place in <see cref="VariableValueTypes.All"/> (or 255 and the type's text) and its value as
    /// the JSON text a document writes for it. Counts and lengths are 7-bit encoded, texts length-prefixed UTF-8. The world
    /// travels in the body's header.
    /// </summary>
    internal void WriteDelta(System.IO.BinaryWriter writer)
    {
        var removed = ClearedKeys;
        writer.Write7BitEncodedInt(removed.Count);
        foreach (var key in removed) writer.Write(key);
        var rows = Touched();
        writer.Write7BitEncodedInt(rows.Count);
        foreach (var entry in rows)
        {
            writer.Write(entry.Name);
            writer.Write((byte)IndexOf(VariableScopeKinds.All, entry.ScopeKind));
            writer.Write(entry.Subject);
            writer.Write7BitEncodedInt(entry.Slot + 1);
            var type = IndexOf(VariableValueTypes.All, entry.Type);
            writer.Write(type < 0 ? NamedType : (byte)type);
            if (type < 0) writer.Write(entry.Type);
            var value = entry.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? RuntimeJson.StableBytes(entry.Value) : RawJson.Bytes(entry.Value);
            writer.Write7BitEncodedInt(value.Length);
            writer.Write(value);
        }
    }

    private static int IndexOf(IReadOnlyList<string> members, string member)
    {
        for (var index = 0; index < members.Count; index++) if (members[index] == member) return index;
        return -1;
    }

    // The two documents are written directly in the stable spelling — properties in ordinal order at every level,
    // compact, the default encoder — which is byte for byte what serializing the same rows and sorting them wrote
    // before, without building the rows as objects, serializing them and reading them back to sort them.
    private readonly ArrayBufferWriter<byte> documentBuffer = new(1024);
    private Utf8JsonWriter? documentWriter;

    private Utf8JsonWriter BeginDocument()
    {
        documentBuffer.Clear();
        if (documentWriter == null) documentWriter = new Utf8JsonWriter(documentBuffer);
        else documentWriter.Reset(documentBuffer);
        return documentWriter;
    }

    private string EndDocument(Utf8JsonWriter writer)
    {
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(documentBuffer.WrittenSpan);
    }

    /// <summary>The `entries` rows, each with its fields in ordinal order. A scalar value is written as the reader
    /// holds it, which is what the stable spelling writes for one; an entity or handle object goes through the one
    /// stable writer so its own keys are ordered by the same rule.</summary>
    private static void WriteEntries(Utf8JsonWriter writer, IReadOnlyList<RuntimeVariableEntry> rows)
    {
        writer.WriteStartArray("entries");
        foreach (var entry in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("name", entry.Name);
            writer.WriteString("scope", entry.ScopeKind);
            writer.WriteNumber("slot", entry.Slot);
            writer.WriteString("subject", entry.Subject);
            writer.WriteString("type", entry.Type);
            writer.WritePropertyName("value");
            if (entry.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                writer.WriteRawValue(RuntimeJson.StableText(entry.Value), skipInputValidation: true);
            else entry.Value.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Restores a checkpoint over the current values. A restore replaces the whole table rather than merging: the
    /// checkpoint is the authoritative state of the world it was taken in, and an entry it does not name is one the
    /// world did not have. The declarations are untouched — they come from the plan, which is reloaded beside this.
    ///
    /// An entry is restored only where the loaded plans declare it, and only where the declaration's own type and
    /// scope agree with the entry: a checkpoint names values, and a value whose address no longer exists (or exists
    /// as another kind) is not restored into an address a step would read as something else.
    ///
    /// The document is the whole table, so it settles the touch set: an address it put back is not a local write
    /// waiting for a body, and a body framed from this table already carries every value in it.
    /// </summary>
    internal void Restore(string json)
    {
        var value = RuntimeJson.Parse(json);
        RuntimeJson.Shape(value, "worldEpoch entries once gates");
        // The document's own row count is bounded before anything is allocated: a body that carries more addresses
        // than the table may hold is refused whole, not filled up to the cap.
        var restored = new Dictionary<VariableAddress, RuntimeVariableEntry>();
        foreach (var row in RowsWithinCap(value, "entries", "snapshot"))
        {
            RuntimeJson.Shape(row, "name scope subject slot type value");
            var name = RuntimeJson.Text(row, "name"); var kind = RuntimeJson.Text(row, "scope");
            RuntimeJson.Require(VariableScopeKinds.All.Contains(kind), "variable-scope", kind);
            var subject = row.GetProperty("subject").GetString() ?? "";
            var slot = (int)RuntimeJson.Integer(row.GetProperty("slot"), -1);
            var type = RuntimeJson.Text(row, "type");
            RuntimeJson.Require(VariableValueTypes.IsDeclared(type), "variable-type", name);
            var declaration = Declared(name);
            if (declaration == null || declaration.Type != type || declaration.Scope != kind) continue;
            RuntimeVariableContract.Validate(row.GetProperty("value"), type, name);
            VariableScopeAddress.FromWire(kind, subject, slot);
            var entry = new RuntimeVariableEntry(name, kind, subject, slot, type, row.GetProperty("value").Clone());
            restored[entry.Address] = entry;
        }
        // Rows the loaded plans do not declare are dropped rather than restored, so the table a document can really
        // produce is the deduplicated one — never larger than the rows that were read.
        Array.Clear(cells, 0, cells.Length); count = changed = high = 0;
        foreach (var entry in restored.Values) RestoreEntry(entry);
        once.Clear();
        foreach (var key in RuntimeJson.Strings(value.GetProperty("once"))) once.Add(key);
        Settle();
    }

    /// <summary>One validated delta, staged: the entries it removes from this table and the entries it writes.</summary>
    internal readonly record struct PreparedDelta(RuntimeVariableEntry[] Removed, List<RuntimeVariableEntry> Incoming);

    /// <summary>
    /// Validates one advance's delta against the table a client already holds, without changing it: the entries the
    /// host wrote, and the addresses it cleared, which a delta names by carrying no value for them. Addresses the
    /// host's plans no longer declare are ignored, exactly as a checkpoint ignores them, so a delta can never invent
    /// an entry a step would then read as something else.
    ///
    /// The whole document is validated here: the rows are parsed into the entries it would write, the removals are
    /// staged, and the capacity question is asked about the table the delta would leave behind — removals count as
    /// room its own replacements may use. A body that is malformed, or that would still grow the table past the cap,
    /// is refused whole, and <see cref="ApplyDelta"/> of what this returns cannot be refused, so a refusal never
    /// leaves the table half advanced. The cost is the delta's: the table is only walked for a delta that removes.
    /// </summary>
    internal PreparedDelta PrepareDelta(System.IO.BinaryReader reader)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        for (var count = Rows(reader); count > 0; count--) removed.Add(reader.ReadString());
        // A removal names an address by its wire key, so the table's own keys are read into that spelling.
        var gone = removed.Count == 0 ? Array.Empty<RuntimeVariableEntry>() : Entries().Where(entry => removed.Contains(entry.Key)).ToArray();
        var incoming = new List<RuntimeVariableEntry>();
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        for (var count = Rows(reader); count > 0; count--)
        {
            var name = reader.ReadString();
            var scope = reader.ReadByte();
            var subject = reader.ReadString();
            var slot = reader.Read7BitEncodedInt() - 1;
            var typeIndex = reader.ReadByte();
            var type = typeIndex == NamedType ? reader.ReadString() : typeIndex < VariableValueTypes.All.Count ? VariableValueTypes.All[typeIndex] : "";
            var length = reader.Read7BitEncodedInt();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new FormatException("value length out of range");
            var text = reader.ReadBytes(length);
            // A row of a scope kind or a type this build does not know, or that the loaded plans do not declare, is passed over.
            if (scope >= VariableScopeKinds.All.Count || !VariableValueTypes.IsDeclared(type)) continue;
            var kind = VariableScopeKinds.All[scope];
            var declaration = Declared(name);
            if (declaration == null || declaration.Type != type || declaration.Scope != kind) continue;
            var payload = RuntimeJson.Parse(Text.GetString(text));
            RuntimeVariableContract.Validate(payload, type, name);
            RuntimeJson.Require(declaration.ResourceKind == null || declaration.Initial.ValueKind == JsonValueKind.Null || payload.ValueKind != JsonValueKind.Null,
                "variable-value", name);
            var entry = new RuntimeVariableEntry(name, kind, subject, slot, type, payload);
            incoming.Add(entry);
            // Parsed before the removal test, so a bad address is refused here and never reaches ApplyDelta.
            var address = VariableScopeAddress.FromWire(kind, subject, slot);
            if (removed.Contains(entry.Key) || Find(declaration.Index, address) < 0) fresh.Add(entry.Key);
        }
        RequireRoom(count - gone.Length, fresh.Count, "delta");
        return new PreparedDelta(gone, incoming);
    }

    /// <summary>Applies a delta <see cref="PrepareDelta"/> validated against this table. It came from a body, so it
    /// settles the touch set for the reason a restore does.</summary>
    internal void ApplyDelta(in PreparedDelta delta)
    {
        Remove(delta.Removed);
        foreach (var entry in delta.Incoming) RestoreEntry(entry);
        Settle();
    }
    private void RestoreEntry(RuntimeVariableEntry entry)
    {
        var declaration = Declared(entry.Name)!;
        RuntimeVariableContract.Validate(entry.Value, declaration.Type, declaration.Name);
        Set(declaration, VariableScopeAddress.FromWire(entry.ScopeKind, entry.Subject, entry.Slot), entry.Value);
    }

    /// <summary>
    /// Whether two payloads are the same value in the spelling a document gives them: the same kind, the same
    /// number text, the same decoded string, and for an entity or handle the same fields holding the same values
    /// whatever order they were written in. A write that changes nothing is decided here and is the common case of a
    /// per-tick counter, so the decision reads the two values and builds nothing: a scalar is compared as the
    /// writer spells it, into two buffers this store keeps, and an identity object field by field. An object of any
    /// other shape — never one a declared type holds — falls back to comparing the two stable spellings.
    /// </summary>
    internal bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null:
                return true;
            case JsonValueKind.Number or JsonValueKind.String:
                return SameScalar(left, right);
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength()) return false;
                using (var l = left.EnumerateArray().GetEnumerator())
                using (var r = right.EnumerateArray().GetEnumerator())
                    while (l.MoveNext() && r.MoveNext())
                        if (!Same(l.Current, r.Current)) return false;
                return true;
            case JsonValueKind.Object:
                var fields = Fields(left, right);
                if (fields == null) return RuntimeJson.StableText(left) == RuntimeJson.StableText(right);
                foreach (var field in fields)
                    if (!Same(left.GetProperty(field), right.GetProperty(field))) return false;
                return true;
            default:
                return RuntimeJson.StableText(left) == RuntimeJson.StableText(right);
        }
    }

    /// <summary>The identity shapes a declared type holds (see <see cref="RuntimeVariableContract.Validate"/>).</summary>
    private static readonly string[][] IdentityFields =
    {
        new[] { "id", "worldEpoch", "lifeEpoch" },
        new[] { "worldEpoch", "lifeEpoch", "local", "provider" },
        new[] { "resourceKind", "resourceId" }
    };

    /// <summary>The identity shape both objects have exactly — each field once and nothing else — or null.</summary>
    private static string[]? Fields(JsonElement left, JsonElement right)
    {
        var count = Count(left);
        if (count != Count(right)) return null;
        foreach (var fields in IdentityFields)
        {
            if (fields.Length != count) continue;
            var all = true;
            foreach (var field in fields)
                if (!left.TryGetProperty(field, out _) || !right.TryGetProperty(field, out _)) { all = false; break; }
            if (all) return fields;
        }
        return null;

        static int Count(JsonElement value)
        {
            var count = 0;
            foreach (var _ in value.EnumerateObject()) count++;
            return count;
        }
    }

    private readonly ArrayBufferWriter<byte> leftScalar = new(64), rightScalar = new(64);
    private Utf8JsonWriter? leftWriter, rightWriter;

    /// <summary>Two scalars as the stable spelling writes them — a number's own text, a string re-encoded by the
    /// default encoder — compared byte for byte.</summary>
    private bool SameScalar(JsonElement left, JsonElement right)
        => Spelled(left, leftScalar, ref leftWriter).SequenceEqual(Spelled(right, rightScalar, ref rightWriter));

    private static ReadOnlySpan<byte> Spelled(JsonElement value, ArrayBufferWriter<byte> buffer, ref Utf8JsonWriter? writer)
    {
        buffer.Clear();
        if (writer == null) writer = new Utf8JsonWriter(buffer);
        else writer.Reset(buffer);
        value.WriteTo(writer);
        writer.Flush();
        return buffer.WrittenSpan;
    }
}

/// <summary>
/// The variable declarations and the value checks behind them. Both the plan header and a `g-var` write go through
/// these, so "a variable is a declared name of a declared type" is one rule with one implementation rather than a
/// loader check and a runtime check that can drift apart.
/// </summary>
internal static class RuntimeVariableContract
{
    private static readonly string[] EntityFields = { "id", "worldEpoch", "lifeEpoch" };
    private static readonly string[] HandleFields = { "worldEpoch", "lifeEpoch", "local", "provider" };
    private static readonly string[] ResourceFields = { "resourceKind", "resourceId" };
    internal const int MaximumDeclarations = 512;
    /// <summary>Addresses one world's variable table may hold. It is the cap the store enforces on a new address, on
    /// a snapshot and on a delta — one number, asked in one place — rather than a declaration nothing reads.</summary>
    internal const int MaximumEntries = 4096;
    /// <summary>The one refusal the cap produces: a new address, a snapshot or a delta that would grow the table past
    /// <see cref="MaximumEntries"/>. It belongs to the `variable-*` family the store's other refusals use, and it is
    /// stated once here so a write, a restore and a delta all name the same budget.</summary>
    internal const string BudgetCode = "variable-budget";
    internal const int MaximumTextBytes = 512;

    /// <summary>Reads the plan header's `variables[]`, rejecting the first declaration that is not a name, a scope,
    /// a type and an initial value of that type.</summary>
    internal static IReadOnlyList<VariableDeclaration> ParsePlanVariables(JsonElement plan)
    {
        var rows = plan.TryGetProperty("variables", out var declared) ? RuntimeJson.Rows(plan, "variables", MaximumDeclarations) : Array.Empty<JsonElement>();
        var result = new List<VariableDeclaration>(rows.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "id scope type initial");
            var name = RuntimeJson.Id(row, "id");
            RuntimeJson.Require(seen.Add(name), "duplicate-variable", name);
            var scope = RuntimeJson.Text(row, "scope");
            RuntimeJson.Require(VariableScopeKinds.All.Contains(scope), "variable-scope", name);
            var type = RuntimeJson.Text(row, "type");
            RuntimeJson.Require(VariableValueTypes.IsDeclared(type), "variable-type", name);
            var initial = row.GetProperty("initial");
            // A resource variable's header value is the document's `{id, revision}`; the declaration keeps the
            // runtime spelling, built once here. Whether the owner has it is the loader's question (see RuntimePlan).
            if (VariableValueTypes.IsResource(type, out var resourceKind) && initial.ValueKind != JsonValueKind.Null)
                initial = RuntimeJson.From(new { resourceKind, resourceId = RuntimeJson.ResourceLiteral(initial) });
            Validate(initial, type, name);
            result.Add(new VariableDeclaration(name, scope, type, initial.Clone()));
        }
        return result.AsReadOnly();
    }

    /// <summary>Checks one payload against a declared type. The type is the whole contract: a number variable holds
    /// a finite number, a flag holds a boolean, text holds a bounded string, and an entity or handle holds the
    /// identity object of its own kind. Null is allowed for entity and handle only, because "this variable holds no
    /// entity" is a fact a behaviour can act on and needs no second representation.</summary>
    internal static void Validate(JsonElement value, string type, string name)
    {
        if (value.ValueKind == JsonValueKind.Null && (type is VariableValueTypes.Entity or VariableValueTypes.Handle or VariableValueTypes.SoundHandle or VariableValueTypes.EntityLifeEffectHandle
            || VariableValueTypes.IsResource(type, out _))) return;
        switch (type)
        {
            case VariableValueTypes.Integer:
                try { RuntimeJson.Integer(value, -RuntimeJson.MaxSafeInteger); }
                catch (RuntimeContractException) { throw new RuntimeContractException("variable-value", name); }
                return;
            case VariableValueTypes.Number:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
                    "variable-value", name);
                return;
            case VariableValueTypes.Flag:
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "variable-value", name);
                return;
            case VariableValueTypes.Text:
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
                RuntimeJson.Require(text != null && System.Text.Encoding.UTF8.GetByteCount(text) <= MaximumTextBytes, "variable-value", name);
                // Display text may break lines: the line feed is the one control character a text value carries.
                foreach (var character in text!) RuntimeJson.Require(character == '\n' || (character > 31 && character != 127), "variable-value", name);
                return;
            case VariableValueTypes.Entity:
                RuntimeJson.Shape(value, EntityFields);
                RuntimeJson.Text(value, "id"); RuntimeJson.Integer(value.GetProperty("worldEpoch")); RuntimeJson.Integer(value.GetProperty("lifeEpoch"));
                return;
            case VariableValueTypes.Handle:
            case VariableValueTypes.SoundHandle:
            case VariableValueTypes.EntityLifeEffectHandle:
                RuntimeJson.Shape(value, HandleFields);
                RuntimeJson.Integer(value.GetProperty("worldEpoch")); RuntimeJson.Integer(value.GetProperty("lifeEpoch"));
                RuntimeJson.Integer(value.GetProperty("local")); RuntimeJson.Integer(value.GetProperty("provider"));
                return;
            default:
                if (VariableValueTypes.IsResource(type, out var resourceKind))
                {
                    // One reference of exactly the declared kind. A reference of another kind is refused by the
                    // resource-kind code, never coerced and never stored as the declared kind.
                    RuntimeJson.Shape(value, ResourceFields);
                    RuntimeJson.Require(RuntimeJson.Text(value, "resourceKind") == resourceKind, RuntimeAbiCodes.ResourceKind, name);
                    RuntimeJson.Text(value, "resourceId");
                    return;
                }
                throw new RuntimeContractException("variable-type", name);
        }
    }
}
