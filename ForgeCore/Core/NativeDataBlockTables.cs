using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace ForgeCore;

/// <summary>The answer of <c>DataBlocks.Check</c> and <c>DataBlocks.AddAll</c>.</summary>
public enum DataBlockResult
{
    /// <summary>The ID is free (<c>Check</c> only).</summary>
    Free,
    /// <summary>Added and read back.</summary>
    Added,
    /// <summary>This mod added the same block (same ID and name) before; nothing changed.</summary>
    AlreadyAdded,
    /// <summary>The ID or name belongs to the game or another mod (or this mod under another name); nothing changed.</summary>
    Conflict,
    /// <summary>Publishing or reading the block back failed; every block of the call was removed again.</summary>
    ReadbackFailed,
    /// <summary>A write failed and the undo could not bring the tables back in line with the ledger (the audit after the rollback
    /// failed; logged once as <c>core-data-blocks-audit-failed</c>). Stop using this mod's content until the game restarts; the game
    /// itself goes on.</summary>
    AuditFailed,
}

/// <summary>Which mod owns which data block, and the exact object it put in the tables: an ID taken by anyone else is refused, and
/// only the owner may replace or remove its row. One ledger for the process, written by every committed
/// <see cref="NativeDataBlockTables.Transaction"/>.</summary>
internal sealed class DataBlockLedger
{
    internal static readonly DataBlockLedger Shared = new();

    internal readonly record struct Row(string Mod, string Name, Type Table, object Value);

    private readonly Dictionary<(string Table, uint Id), Row> _owners = new();

    /// <summary>Whether <paramref name="modId"/> may add block <paramref name="id"/> named <paramref name="name"/> to
    /// <paramref name="table"/>, given the name of the block the table already has under that ID (null: none).</summary>
    internal DataBlockResult Decide(string table, uint id, string modId, string name, string? existingName)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id), "Data block ID 0 is never valid.");
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name the data block.", nameof(name));
        if (existingName == null) return DataBlockResult.Free;
        return _owners.TryGetValue((table, id), out var owner) && owner.Mod == modId && owner.Name == name && existingName == name
            ? DataBlockResult.AlreadyAdded
            : DataBlockResult.Conflict;
    }

    internal void Owns(Type table, uint id, string modId, string name, object value) => _owners[(table.Name, id)] = new(modId, name, table, value);

    internal void Forget(Type table, uint id) => _owners.Remove((table.Name, id));

    internal bool TryGet(Type table, uint id, out Row row) => _owners.TryGetValue((table.Name, id), out row);

    internal string? OwnerOf(string table, uint id) => _owners.TryGetValue((table, id), out var owner) ? owner.Mod : null;

    internal IEnumerable<(uint Id, Row Row)> Rows => _owners.Select(pair => (pair.Key.Id, pair.Value));
}

/// <summary>One already-resolved native definition, published through <see cref="NativeDataBlockTables.Transaction"/> (or
/// <see cref="NativeDataBlockTables.Register"/>).</summary>
public readonly record struct NativeDataBlockRegistration(string Type, string Name, uint PersistentId, string Json,
    uint? SourceId = null);

/// <summary>A table transaction failed and its undo did not bring every table with ledger rows back in line with the ledger. The
/// caller stops its own content until the game restarts; the game goes on. <see cref="Exception.InnerException"/> is the failure
/// that started the rollback.</summary>
public sealed class NativeDataBlockAuditException : Exception
{
    internal NativeDataBlockAuditException(Exception error, IReadOnlyList<string> problems)
        : base("Native tables do not match the data block ledger after a rollback: " + string.Join("; ", problems), error)
        => Problems = problems;

    /// <summary>What the audit found, one line per inconsistent row or table.</summary>
    public IReadOnlyList<string> Problems { get; }
}

public sealed class NativeDataBlockConflictException : Exception
{
    internal NativeDataBlockConflictException(string message) : base(message) { }
}

/// <summary>The native table transaction used by Rundowns, domain content and code-built blocks (<see cref="Transaction"/>). CellJSON
/// creates native objects; managed JSON deserialization cannot create a usable IL2CPP DataBlock. No tick path calls this type.</summary>
public static partial class NativeDataBlockTables
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Verify an original native row and the unchanged links a compiled clone claims it came from.
    /// Zero links are valid only when the source itself has zero; referenced nonzero rows are checked separately.</summary>
    public static void RequireSourceLinks(string type, uint sourceId, IReadOnlyDictionary<string, uint> expected)
    {
        var source = RequireBlock(type, sourceId);
        foreach (var link in expected)
            if (Get(source, link.Key) is not uint value || value != link.Value)
                throw new InvalidOperationException("Native source link differs: " + type + "/" + sourceId + "." + link.Key);
    }

    /// <summary>Whether a Forge publication (<see cref="Register"/> or a code-built add) put row <paramref name="id"/> into the
    /// <paramref name="type"/> table: a row is found by its number, and this tells a package's own row from a vanilla or foreign
    /// one without relying on its name.</summary>
    public static bool IsPublished(string type, uint id) => DataBlockLedger.Shared.OwnerOf(type + "DataBlock", id) != null;

    /// <summary>Every row the ledger holds: its owner, its table (<c>LevelLayout</c>) and its ID, ordered by table and ID. A caller
    /// compares two reads to learn which rows a step of its own published, and removes them again with <see cref="Transaction.Remove"/>.</summary>
    public static IReadOnlyList<(string Owner, string Type, uint Id)> OwnedRows()
        => DataBlockLedger.Shared.Rows.Select(row => (row.Row.Mod, Label(row.Row.Table), row.Id))
            .OrderBy(row => row.Item2, StringComparer.Ordinal).ThenBy(row => row.Id).ToArray();

    /// <summary>Whether row <paramref name="id"/> of the <paramref name="type"/> table (<c>LevelLayout</c>) is <paramref name="owner"/>'s
    /// and the table still holds the very object that owner published under that ID: then the owner may
    /// <see cref="Transaction.Replace(object)"/> or <see cref="Transaction.Remove"/> it. False after the game rebuilt its tables
    /// (the ledger still names the row, the table no longer holds it): the owner adds the row again.</summary>
    public static bool Holds(string owner, string type, uint id)
    {
        var table = NativeType("GameData." + type + "DataBlock");
        if (!DataBlockLedger.Shared.TryGet(table, id, out var row) || row.Mod != owner) return false;
        var byId = Get(table, "s_blockByID")!;
        return Has(byId, id) && SameObject(Call(byId, "get_Item", id), row.Value);
    }

    public static object RequireBlock(string type, uint id)
    {
        var reader = BlockReader(NativeType("GameData." + type + "DataBlock"));
        var source = reader.Get(id);
        if (source == null || reader.PersistentId(source) != id)
            throw new InvalidOperationException("Missing native block " + type + "/" + id);
        return source;
    }

    /// <summary>Adds every definition in one <see cref="Transaction"/> and commits it: nothing changes unless all rows are free and
    /// read back. Never idempotent: a row any owner published before is a collision too.</summary>
    public static void Register(IReadOnlyList<NativeDataBlockRegistration> definitions, bool strictFields = false,
        string owner = "Forge content")
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var transaction = new Transaction(owner);
        foreach (var definition in definitions) transaction.Add(definition, strictFields);
        transaction.Commit();
        transaction.Release();
    }

    /// <summary>Adds blocks built in code (a donor <c>DataBlocks.Clone</c> with changed fields) in one <see cref="Transaction"/>. A
    /// block <paramref name="owner"/> published before (same table, ID and name) is skipped; any other occupant of its ID or name is a
    /// <see cref="NativeDataBlockConflictException"/> and nothing changes. Returns how many blocks were written.</summary>
    internal static int Add(string owner, IReadOnlyList<object> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var transaction = new Transaction(owner);
        int written = 0;
        foreach (var block in blocks)
        {
            var (table, id, name) = Identity(block);
            var decision = Classify(table, owner, id, name, out var reason);
            if (decision == DataBlockResult.Conflict) throw new NativeDataBlockConflictException(reason);
            if (decision == DataBlockResult.Free) { transaction.Add(block); written++; }
        }
        if (written != 0) { transaction.Commit(); transaction.Release(); }
        return written;
    }

    private static (Type Table, uint Id, string Name) Identity(object block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return (block.GetType(), (uint)Get(block, "persistentID")!, (string?)Get(block, "name") ?? "");
    }

    private static string Label(Type table) => table.Name.EndsWith("DataBlock", StringComparison.Ordinal) ? table.Name[..^9] : table.Name;

    /// <summary>The one occupancy check of every way into the tables. Free: the ID and name are unused (and, for an item, no prefab
    /// table holds the ID, unless the ledger already names <paramref name="owner"/> for it). AlreadyAdded: <paramref name="owner"/> published this very block. Conflict: anything else, with the
    /// occupant, and its owner when the ledger knows it, in <paramref name="reason"/>. A name <paramref name="released"/> accepts is taken
    /// out of the table by an earlier step of the same transaction and counts as unused.</summary>
    internal static DataBlockResult Classify(Type table, string owner, uint id, string name, out string reason,
        Func<string, bool>? released = null)
    {
        reason = "";
        var byId = Get(table, "s_blockByID")!; var byName = Get(table, "s_blockIDByName")!;
        var occupant = Has(byId, id) ? (string?)Get(Call(byId, "get_Item", id)!, "name") ?? "" : null;
        var decision = DataBlockLedger.Shared.Decide(table.Name, id, owner, name, occupant);
        if (decision != DataBlockResult.Free)
        {
            var holder = DataBlockLedger.Shared.OwnerOf(table.Name, id);
            reason = "Native ID collision: " + name + " and loaded:" + Label(table) + "/" + occupant + " / " + id
                + (holder == null ? "" : " (added by " + holder + ")");
            return decision;
        }
        if (Has(byName, name) && released?.Invoke(name) != true)
        {
            reason = "Native name collision: " + name + " / " + Call(byName, "get_Item", name);
            return DataBlockResult.Conflict;
        }
        // The ledger still names this owner when the game re-initialised its tables and dropped the row: the prefab the item tables
        // hold under the ID was built from this owner's own row, and the same input adds it back.
        if (DataBlockLedger.Shared.OwnerOf(table.Name, id) != owner && PrefabCollision(table, id) is { } mode)
        {
            reason = "Native item prefab collision: " + name + " / " + id + " in mode " + mode;
            return DataBlockResult.Conflict;
        }
        return DataBlockResult.Free;
    }

    // The game builds its item prefab tables from the item blocks once; an item ID a built table already holds belongs to another
    // block and would hand this item that block's prefabs. No such table (not built yet, or no such game class): nothing collides.
    private static int? PrefabCollision(Type table, uint id)
    {
        if (table.Name != "ItemDataBlock" || FindNativeType("ItemSpawnManager") is not { } manager) return null;
        var member = (object?)manager.GetProperty("m_loadedPrefabsPerItemMode", Members) ?? manager.GetField("m_loadedPrefabsPerItemMode", Members);
        if (member == null || Get(manager, "m_loadedPrefabsPerItemMode") is not { } modes) return null;
        int count = modes is Array array ? array.Length : (int)Get(modes, "Length")!;
        for (int mode = 0; mode < count; mode++)
        {
            var prefabs = modes is Array plain ? plain.GetValue(mode) : Call(modes, "get_Item", mode);
            if (prefabs != null && Has(prefabs, id)) return mode;
        }
        return null;
    }

    /// <summary>A game cache built from a table learns a row that is now in the table (Native: Text).</summary>
    static partial void Published(object block);

    /// <summary>A game cache built from a table forgets a row that left the table (Native: Text).</summary>
    static partial void Withdrawn(object block);

    private static bool Has(object map, object key) => (bool)Call(map, "ContainsKey", key)!;
    private static int Count(object container) => (int)Get(container, "Count")!;

    // IL2CPP may hand out distinct managed wrappers for one native object. Only the actual interop base's Pointer is an identity;
    // arbitrary content fields named Pointer are not. A managed object (the test tables) is its own identity.
    private static PropertyInfo? _pointer;
    private static object ObjectKey(object value)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase")
                return (_pointer ??= type.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)!).GetValue(value)!;
        return value;
    }

    private static bool SameObject(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;
        var pointer = ObjectKey(left);
        return pointer is IntPtr native && native != IntPtr.Zero && pointer.Equals(ObjectKey(right));
    }

    // This deliberately follows actual native member types, including nested structures and collection elements.
    // Strict callers also compare supplied values before publication and through the native indexes: a writable
    // member alone does not prove that CellJSON applied it. Float comparisons use the declared native precision,
    // not JSON text equality or an epsilon. Non-strict Rundown materialization retains its established behavior.
    private static void Validate(JsonElement value, Type type, string path, object? actual = null, bool compare = false)
    {
        void Mismatch() => throw new InvalidOperationException("Native field value changed: " + path);
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null)
        {
            if (value.ValueKind == JsonValueKind.Null) { if (compare && actual != null) Mismatch(); return; }
            type = nullable;
        }
        bool valid; object? expected;
        // CellJSON's LocalizedText converter reads a Text ID or inline text, not an object.
        // Check the converted native value as well: successful deserialization alone is insufficient.
        if (type.FullName == "Localization.LocalizedText")
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint textId))
            {
                if (compare && (actual == null || !Equals(Get(actual, "Id"), textId))) Mismatch();
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (compare && (actual == null || !Equals(Get(actual, "UntranslatedText"), value.GetString()))) Mismatch();
            }
            else throw new InvalidOperationException("Invalid native localized text: " + path);
            return;
        }
        if (type == typeof(string))
        { valid = value.ValueKind is JsonValueKind.String or JsonValueKind.Null; expected = valid ? value.GetString() : null; }
        else if (type == typeof(bool))
        { valid = value.ValueKind is JsonValueKind.True or JsonValueKind.False; expected = valid && value.GetBoolean(); }
        else if (type.IsEnum)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                string name = value.GetString()!;
                if (!Enum.GetNames(type).Contains(name, StringComparer.Ordinal))
                    throw new InvalidOperationException("Invalid native enum name: " + path);
                expected = Enum.Parse(type, name);
                if (compare && !Equals(expected, actual)) Mismatch();
                return;
            }
            var underlying = Enum.GetUnderlyingType(type);
            Validate(value, underlying, path, compare && actual != null ? Convert.ChangeType(actual, underlying, CultureInfo.InvariantCulture) : actual, compare);
            return;
        }
        else if (type == typeof(byte)) { valid = value.TryGetByte(out var number); expected = number; }
        else if (type == typeof(sbyte)) { valid = value.TryGetSByte(out var number); expected = number; }
        else if (type == typeof(short)) { valid = value.TryGetInt16(out var number); expected = number; }
        else if (type == typeof(ushort)) { valid = value.TryGetUInt16(out var number); expected = number; }
        else if (type == typeof(int)) { valid = value.TryGetInt32(out var number); expected = number; }
        else if (type == typeof(uint)) { valid = value.TryGetUInt32(out var number); expected = number; }
        else if (type == typeof(long)) { valid = value.TryGetInt64(out var number); expected = number; }
        else if (type == typeof(ulong)) { valid = value.TryGetUInt64(out var number); expected = number; }
        else if (type == typeof(float)) { valid = value.TryGetSingle(out float number) && float.IsFinite(number); expected = number; }
        else if (type == typeof(double)) { valid = value.TryGetDouble(out double number) && double.IsFinite(number); expected = number; }
        else
        {
            var element = ListElementType(type);
            if (element != null)
            {
                if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Expected native list: " + path);
                var item = type.IsArray ? null : type.GetProperty("Item", Fields, null, element, new[] { typeof(int) }, null);
                if (compare)
                {
                    if (actual == null) Mismatch();
                    int count = type.IsArray ? ((Array)actual!).Length
                        : (int)Get(actual!, type.GetProperty("Count", Fields) != null ? "Count" : "Length")!;
                    if (count != value.GetArrayLength()) Mismatch();
                }
                int index = 0;
                foreach (var child in value.EnumerateArray())
                {
                    object? observed = !compare ? null : type.IsArray ? ((Array)actual!).GetValue(index) : item!.GetValue(actual, new object[] { index });
                    Validate(child, element, path + "[" + index++ + "]", observed, compare);
                }
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected native object: " + path);
            if (compare && actual == null) Mismatch();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in value.EnumerateObject())
            {
                if (!seen.Add(child.Name)) throw new InvalidOperationException("Duplicate native field: " + path + "." + child.Name);
                var property = type.GetProperty(child.Name, Fields);
                var field = type.GetField(child.Name, Fields);
                Type? memberType = property is { CanRead: true, CanWrite: true } && property.GetIndexParameters().Length == 0
                    ? property.PropertyType : field is { IsInitOnly: false } ? field.FieldType : null;
                if (memberType == null) throw new InvalidOperationException("Unknown or unwritable native field: " + path + "." + child.Name);
                object? observed = !compare ? null : property != null ? property.GetValue(actual) : field!.GetValue(actual);
                Validate(child.Value, memberType, path + "." + child.Name, observed, compare);
            }
            return;
        }
        if (!valid) throw new InvalidOperationException("Invalid native field type: " + path + " (" + type.Name + ")");
        if (compare && !Equals(expected, actual)) Mismatch();
    }

    // Unity vectors, colors and matrices also have integer indexers. Only actual list/array types
    // use the collection path; value types are checked through their native writable fields.
    private static Type? ListElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        for (var collection = type; collection != null; collection = collection.BaseType)
            if (collection.IsGenericType && collection.GetGenericTypeDefinition().FullName is
                "System.Collections.Generic.List`1" or "Il2CppSystem.Collections.Generic.List`1"
                or "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1")
                return collection.GetGenericArguments()[0];
        return null;
    }

    // A loaded type never changes within the process, so each name is searched across every loaded assembly once
    // per process instead of once per published row. A missing type is not remembered: it is searched and refused again.
    private static readonly Dictionary<string, Type> NativeTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, MethodInfo> Deserializers = new();
    private sealed record NativeBlockReader(Func<uint, object?> Get, Func<object, uint> PersistentId);
    private static readonly Dictionary<Type, NativeBlockReader> BlockReaders = new();
    private static NativeBlockReader BlockReader(Type type)
    {
        if (BlockReaders.TryGetValue(type, out var cached)) return cached;
        var method = type.GetMethod("GetBlock", Members, null, new[] { typeof(uint) }, null)
            ?? throw new MissingMethodException(type.FullName, "GetBlock(uint)");
        if (!method.IsStatic) throw new MissingMethodException(type.FullName, "static GetBlock(uint)");
        var property = type.GetProperty("persistentID", Members);
        var field = type.GetField("persistentID", Members);
        if (property == null && field == null) throw new MissingMemberException(type.FullName, "persistentID");
        var id = Expression.Parameter(typeof(uint), "id");
        var value = Expression.Parameter(typeof(object), "value");
        var typed = Expression.Convert(value, type);
        var member = property != null ? Expression.Property(typed, property) : Expression.Field(typed, field!);
        if (member.Type != typeof(uint)) throw new InvalidOperationException("Native persistentID is not UInt32: " + type.FullName);
        var reader = new NativeBlockReader(
            Expression.Lambda<Func<uint, object?>>(Expression.Convert(Expression.Call(method, id), typeof(object)), id).Compile(),
            Expression.Lambda<Func<object, uint>>(Expression.Convert(member, typeof(uint)), value).Compile());
        BlockReaders.Add(type, reader);
        return reader;
    }
    private static Type NativeType(string name) => FindNativeType(name) ?? throw new InvalidOperationException("Native type unavailable: " + name);
    private static Type? FindNativeType(string name)
    {
        if (NativeTypes.TryGetValue(name, out var cached)) return cached;
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
        if (type != null) NativeTypes.Add(name, type);
        return type;
    }
    private static object? Get(object target, string name)
    {
        var type = target as Type ?? target.GetType(); var instance = target is Type ? null : target;
        var property = type.GetProperty(name, Members);
        return property != null ? property.GetValue(instance) : (type.GetField(name, Members)
            ?? throw new MissingMemberException(type.FullName, name)).GetValue(instance);
    }
    private static void Set(object target, string name, object? value)
    {
        var type = target.GetType(); var property = type.GetProperty(name, Members);
        if (property != null) property.SetValue(target, value);
        else (type.GetField(name, Members) ?? throw new MissingMemberException(type.FullName, name)).SetValue(target, value);
    }
    // The audit walks whole tables, so a method is looked up once per container type, name and argument types, not per call.
    private static readonly Dictionary<(Type, string, string), MethodInfo> Methods = new();
    private static object? Call(object target, string name, params object?[] args)
    {
        var type = target as Type ?? target.GetType();
        var key = (type, name, string.Join(",", args.Select(arg => arg?.GetType().FullName ?? "null")));
        if (!Methods.TryGetValue(key, out var method))
            Methods.Add(key, method = type.GetMethods(Members).Single(candidate => candidate.Name == name && !candidate.IsGenericMethodDefinition
                && candidate.GetParameters().Length == args.Length && candidate.GetParameters().Select((parameter, index) =>
                    args[index] == null || parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match)));
        return method.Invoke(target is Type ? null : target, args);
    }
    private static object Deserialize(Type type, string json)
    {
        var cellJson = NativeType("CellJSON");
        if (!Deserializers.TryGetValue(type, out var method))
            Deserializers.Add(type, method = cellJson.GetMethods(Members).Single(candidate => candidate.Name == "Deserialize"
                && candidate.IsGenericMethodDefinition).MakeGenericMethod(type));
        return method.Invoke(null, new[] { (object)json, Get(cellJson, "JSONSettings_GameData") })
            ?? throw new InvalidOperationException("Native block deserialization returned null.");
    }
}
