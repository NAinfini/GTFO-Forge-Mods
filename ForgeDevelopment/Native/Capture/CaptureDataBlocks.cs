using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>Reads loaded block tables without initializing or repairing them. Every attempted table has an outcome;
/// a failed read is not evidence that a table is empty, absent or uninitialized.</summary>
internal static class CaptureDataBlocks
{
    private const string Where = "ForgeDevelopment.Native.CaptureDataBlocks";
    private static readonly List<(Type Type, MethodInfo? All, MethodInfo? Name, MethodInfo? PersistentId)> Resolved = new();
    private static bool _resolved;
    private static bool _discoveryComplete = true;

    internal static IReadOnlyList<(Type Type, MethodInfo? All, MethodInfo? Name, MethodInfo? PersistentId)> Types()
    {
        if (_resolved) return Resolved;
        _resolved = true;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (name is "ForgeDevelopment.Native" or "0Harmony" or "BepInEx.Core" or "Il2CppInterop.Runtime"
                || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name is "mscorlib" or "netstandard" or "Il2Cppmscorlib" or "SteamWorks_ASM"
                || name.StartsWith("UnityEngine.", StringComparison.Ordinal)) continue; // engine/platform wrappers hold no data blocks; their GetTypes() always partially fails
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error)
            {
                _discoveryComplete = false;
                CaptureReadDiagnostics.Report(Where, "discover-types", error, expression: assembly.FullName);
                var available = new List<Type>();
                foreach (var type in error.Types) if (type != null) available.Add(type);
                types = available.ToArray();
            }
            catch (Exception error)
            {
                _discoveryComplete = false;
                CaptureReadDiagnostics.Report(Where, "discover-types", error, expression: assembly.FullName);
                continue;
            }
            foreach (var type in types)
            {
                try
                {
                    if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    var baseType = BaseBlockType(type);
                    if (baseType == null) continue;
                    // Keep the discovered type even when its member metadata cannot be resolved. Collection will
                    // emit its unavailable outcome and contextual failure without hiding later tables.
                    MethodInfo? all = null;
                    try { all = ResolveAll(baseType); }
                    catch (Exception error) { CaptureReadDiagnostics.Report(Where, "resolve-member", error, type, expression: "GetAllBlocks()"); }
                    MethodInfo? blockName = null, persistentId = null;
                    try
                    {
                        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                        blockName = baseType.GetMethod("get_name", flags);
                        persistentId = baseType.GetMethod("get_persistentID", flags);
                    }
                    catch (Exception error) { CaptureReadDiagnostics.Report(Where, "resolve-identity-member", error, type); }
                    Resolved.Add((type, all, blockName, persistentId));
                }
                catch (Exception error) { _discoveryComplete = false; CaptureReadDiagnostics.Report(Where, "discover-base", error, type); }
            }
        }
        Resolved.Sort((left, right) => string.CompareOrdinal(left.Type.FullName, right.Type.FullName));
        return Resolved;
    }

    /// <summary>Null means unavailable, including unsupported or partially enumerated wrappers. An empty list is
    /// returned only after a successful complete enumeration. No partial list is mistaken for the full table.</summary>
    internal static IReadOnlyList<object>? Blocks(Type type)
    {
        var read = new TableRead(type, null);
        var blocks = new List<object>();
        try
        {
            while (read.Next(out var block)) if (block != null) blocks.Add(block);
        }
        finally { read.Dispose(); }
        return read.Complete ? blocks : null;
    }

    internal static string Name(object block) => ReadName(block, out _);
    internal static string PublicName(object block) => ReadPublicName(block, out _);
    internal static string PersistentId(object block) => ReadPersistentId(block, out _);

    private static string ReadName(object block, out string status)
        => Text(block, "get_name", out status) ?? block.GetType().Name;

    private static string ReadPublicName(object block, out string status)
    {
        var preferred = Text(block, "get_PublicName", out status);
        if (preferred != null) return preferred;
        var preferredStatus = status;
        var value = Text(block, "PublicName", out status);
        if (preferredStatus == "failed") status = "failed; fallback=" + status;
        return value ?? "";
    }

    private static string ReadPersistentId(object block, out string status)
    {
        var value = Member(block, "get_persistentID", out status);
        if (value == null)
        {
            var preferredStatus = status;
            value = Member(block, "persistentID", out status);
            if (preferredStatus == "failed") status = "failed; fallback=" + status;
        }
        return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static Type? BaseBlockType(Type type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "GameData.GameDataBlockBase`1") return current;
        return null;
    }

    // Select the evidenced static zero-argument method on the closed generic base. Never accidentally invoke a
    // concrete type's unrelated overload/instance method. This is resolution hardening, not a native-cause diagnosis.
    private static MethodInfo? ResolveAll(Type type)
        => type.GetMethod("GetAllBlocks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null, Type.EmptyTypes, null);

    private static MethodInfo? ResolveLoaded(Type type)
        => type.GetMethod("IsLoaded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null, Type.EmptyTypes, null);

    private static object? Member(object block, string name, out string status)
    {
        var type = block.GetType();
        PropertyInfo? property = null;
        var stage = "resolve-identity-member";
        try
        {
            property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (property == null) { status = "member-unavailable"; return null; }
            stage = "read-identity-member";
            var value = property.GetValue(block);
            status = value == null ? "returned-null" : "value";
            return value;
        }
        catch (Exception error)
        {
            CaptureReadDiagnostics.Report(Where, stage, error, type, property, name);
            status = "failed";
            return null;
        }
    }

    private static string? Text(object block, string name, out string status)
    {
        var value = Member(block, name, out status);
        if (value != null && value is not string) status = "unexpected-value-type";
        return value as string;
    }

    internal static IEnumerable<int> CollectSlices(CaptureSnapshot snapshot, int maxPerType)
        => CaptureReadDiagnostics.InSnapshot(snapshot, CollectDiscoveredTables(snapshot, maxPerType));

    private static IEnumerable<int> CollectDiscoveredTables(CaptureSnapshot snapshot, int maxPerType)
    {
        var types = Types();
        if (!_discoveryComplete)
        {
            snapshot.AcquisitionComplete = false;
            snapshot.Notes.Add("dataBlock type discovery incomplete; unvisited types are unknown, not absent");
        }
        foreach (var step in CollectTables(snapshot, maxPerType, types)) yield return step;
    }

    internal static IEnumerable<int> CollectTables(CaptureSnapshot snapshot, int maxPerType,
        IEnumerable<(Type Type, MethodInfo? All, MethodInfo? Name, MethodInfo? PersistentId)> types)
    {
        var section = snapshot.Section(CaptureSections.DataBlocks, "GameDataBlockBase<T>.GetAllBlocks()");
        foreach (var (type, all, _, _) in types)
        {
            yield return 0;
            var read = new TableRead(type, all);
            var recorded = 0;
            var rowFailures = 0;
            try
            {
                while (read.Next(out var block))
                {
                    yield return 0;
                    if (block == null || read.ObservedCount > maxPerType) continue;
                    try
                    {
                        var id = ReadPersistentId(block, out var idRead);
                        var name = ReadName(block, out var nameRead);
                        var publicName = ReadPublicName(block, out var publicNameRead);
                        var row = new CaptureRow("dataBlock", "datablock/" + type.Name + "/" + id, name);
                        row.Group = type.Name;
                        row.Field("type", type.Name);
                        row.Field("persistentId", id);
                        row.Field("publicName", publicName);
                        // Keep existing identity/fallback values, while making an unavailable/failed member read
                        // distinguishable from a successfully read empty string or null.
                        row.Field("nameRead", nameRead).Field("persistentIdRead", idRead).Field("publicNameRead", publicNameRead);
                        if (nameRead.StartsWith("failed", StringComparison.Ordinal) || idRead.StartsWith("failed", StringComparison.Ordinal)
                            || publicNameRead.StartsWith("failed", StringComparison.Ordinal)) rowFailures++;
                        section.Rows.Add(row);
                        recorded++;
                    }
                    catch (Exception error) { rowFailures++; read.Report("read-row", error, null); }
                }
            }
            finally
            {
                read.Dispose();
                var outcome = read.Outcome(recorded, rowFailures);
                section.Rows.Add(outcome); // Freeze only after enumeration and disposal, including partial failures.
                if (!read.Complete || rowFailures != 0)
                {
                    snapshot.AcquisitionComplete = false;
                    snapshot.Notes.Add(type.Name + ": " + (rowFailures == 0 ? read.Status : "row-read-failed")
                            + "; observed count is not a table total unless complete");
                }
                if (read.ObservedCount > maxPerType)
                    snapshot.Notes.Add(type.Name + ": " + read.ObservedCount.ToString(CultureInfo.InvariantCulture)
                        + " blocks observed, first " + Math.Max(0, maxPerType).ToString(CultureInfo.InvariantCulture) + " recorded");
            }
        }
    }

    /// <summary>All potentially throwing enumeration operations share one table boundary. Advancing reads at most
    /// one item so the caller still yields cooperatively; disposal failures cannot suppress the next table.</summary>
    internal sealed class TableRead : IDisposable
    {
        private readonly Type _type;
        private MethodInfo? _all;
        private MethodInfo? _loaded;
        private object? _wrapper;
        private IEnumerator? _enumerator;
        private PropertyInfo? _indexer;
        private int _length, _index;
        private bool _disposed;
        private string _resolution = "not-attempted", _invocation = "not-attempted", _returned = "not-observed";
        private string _initialization = "not-read", _initializationEvidence = "not-read";
        private string _enumeration = "not-started", _mechanism = "unknown", _disposal = "not-required";
        private readonly List<string> _diagnostics = new();
        private bool _diagnosticsQueued = true;
        internal string Status { get; private set; } = "not-started";
        internal long ObservedCount { get; private set; }
        internal bool Complete => Status == "complete" && _disposed;

        internal TableRead(Type type, MethodInfo? all)
        {
            _type = type;
            var stage = "resolve-member";
            MemberInfo? member = null;
            try
            {
                var baseType = BaseBlockType(type) ?? type;
                _all = all ?? ResolveAll(baseType);
                if (_all == null) { _resolution = "unavailable"; Status = "member-unavailable"; return; }
                _resolution = "resolved";
                member = _all;
                // Some declared block types never load in a play session; their native GetAllBlocks dereferences
                // an absent table. The game's own loaded-state API is the admission gate for that accessor.
                stage = "resolve-loaded-state";
                _loaded = ResolveLoaded(baseType);
                if (_loaded == null)
                {
                    _initialization = "unavailable";
                    _initializationEvidence = "IsLoaded() member unavailable";
                    Status = "loaded-state-unavailable";
                    return;
                }
                member = _loaded;
                stage = "read-loaded-state";
                var loaded = _loaded.Invoke(null, null);
                if (loaded is not bool isLoaded)
                    throw new InvalidOperationException("IsLoaded returned a non-boolean value.");
                _initialization = isLoaded ? "loaded" : "not-loaded";
                _initializationEvidence = "IsLoaded()=" + isLoaded.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
                if (!isLoaded) { Status = "not-loaded"; return; }
                member = _all;
                stage = "invoke";
                _wrapper = _all.Invoke(null, null);
                _invocation = "returned";
                if (_wrapper == null) { _returned = "null"; Status = "returned-null"; return; }
                _returned = "non-null";
                _enumeration = "opening";
                if (_wrapper is IEnumerable enumerable && _wrapper is not string)
                {
                    _mechanism = "IEnumerable";
                    stage = "get-enumerator";
                    member = typeof(IEnumerable).GetMethod("GetEnumerator");
                    _enumerator = enumerable.GetEnumerator();
                    if (_enumerator == null) throw new InvalidOperationException("GetEnumerator returned null.");
                    _disposal = _enumerator is IDisposable ? "pending" : "not-required";
                }
                else
                {
                    var wrapperType = _wrapper.GetType();
                    stage = "resolve-enumeration";
                    member = null;
                    var count = wrapperType.GetProperty("Count") ?? wrapperType.GetProperty("Length");
                    _indexer = wrapperType.GetProperty("Item", new[] { typeof(int) });
                    if (count == null || _indexer == null)
                    { _enumeration = "unsupported"; Status = "enumeration-unsupported"; return; }
                    _mechanism = count.Name + "/Item";
                    member = count;
                    stage = "read-count";
                    var value = count.GetValue(_wrapper);
                    if (value == null) throw new InvalidOperationException("Collection count returned null.");
                    stage = "convert-count";
                    _length = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    if (_length < 0) throw new InvalidOperationException("Collection count was negative.");
                }
                _enumeration = "in-progress";
                Status = "reading";
            }
            catch (Exception error) when (stage == "read-loaded-state" && error.InnerException?.Message.Contains("System.NullReferenceException") == true)
            {
                // A table the game never sets up has no backing dictionary, so its IsLoaded() dereferences null: that is "not loaded", not a read failure.
                _initialization = "not-loaded";
                _initializationEvidence = "IsLoaded() threw NullReferenceException (table never set up)";
                Status = "not-loaded";
            }
            catch (Exception error)
            {
                if (stage == "resolve-member") { _resolution = "failed"; Status = "resolution-failed"; }
                else if (stage == "resolve-loaded-state") { _initialization = "failed"; Status = "loaded-state-resolution-failed"; }
                else if (stage == "read-loaded-state") { _initialization = "failed"; Status = "loaded-state-read-failed"; }
                else if (stage == "invoke") { _invocation = "threw"; Status = "invocation-failed"; }
                else { _enumeration = "failed"; Status = "enumeration-failed"; }
                Report(stage, error, member);
            }
        }

        internal bool Next(out object? block)
        {
            block = null;
            if (Status != "reading") return false;
            var stage = "move-next";
            MemberInfo? member = typeof(IEnumerator).GetMethod("MoveNext");
            try
            {
                if (_enumerator != null)
                {
                    if (!_enumerator.MoveNext()) { Finish(); return false; }
                    stage = "current";
                    member = typeof(IEnumerator).GetProperty("Current");
                    block = _enumerator.Current;
                }
                else
                {
                    if (_index == _length) { Finish(); return false; }
                    stage = "indexer";
                    member = _indexer;
                    block = _indexer!.GetValue(_wrapper, new object[] { _index++ });
                }
                ObservedCount++;
                return true;
            }
            catch (Exception error)
            {
                _enumeration = "failed"; Status = "enumeration-failed";
                Report(stage, error, member);
                return false;
            }
        }

        private void Finish() { _enumeration = "complete"; Status = "complete"; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Status == "reading") { _enumeration = "cancelled"; Status = "cancelled"; }
            if (_enumerator is not IDisposable disposable) return;
            try { disposable.Dispose(); _disposal = "complete"; }
            catch (Exception error)
            {
                _disposal = "failed"; Status = "enumeration-failed";
                Report("dispose", error, typeof(IDisposable).GetMethod("Dispose"));
            }
        }

        internal void Report(string stage, Exception error, MemberInfo? member)
        {
            var receipt = CaptureReadDiagnostics.Report(Where, stage, error, _type, member,
                member == null ? (stage == "read-row" ? "PersistentId(block), Name(block), PublicName(block)" : "GetAllBlocks()") : null,
                target: _enumerator?.GetType() ?? _wrapper?.GetType() ?? _type);
            _diagnostics.Add(receipt.Id.ToString(CultureInfo.InvariantCulture));
            _diagnosticsQueued &= receipt.Queued;
        }

        internal CaptureRow Outcome(int recorded, int rowFailures)
        {
            var row = new CaptureRow("dataBlockType", "datablock/" + _type.FullName, _type.Name);
            row.Field("status", rowFailures == 0 ? Status : "row-read-failed").Field("type", _type.FullName).Field("assembly", _type.Assembly.FullName);
            row.Field("memberResolution", _resolution).Field("declaringType", _all?.DeclaringType?.FullName)
                .Field("memberAssembly", _all?.Module.Assembly.FullName).Field("member", _all?.ToString());
            row.Field("invocation", _invocation).Field("returned", _returned).Field("wrapperType", _wrapper?.GetType().AssemblyQualifiedName);
            row.Field("enumeratorType", _enumerator?.GetType().AssemblyQualifiedName);
            row.Field("enumerationMechanism", _mechanism).Field("enumeration", _enumeration).Field("disposal", _disposal);
            row.Field("initialized", _initialization).Field("initializationEvidence", _initializationEvidence);
            row.Field("observedCount", ObservedCount.ToString(CultureInfo.InvariantCulture));
            row.Field("recordedCount", recorded.ToString(CultureInfo.InvariantCulture));
            row.Field("rowFailures", rowFailures.ToString(CultureInfo.InvariantCulture));
            row.Field("countComplete", Complete ? "true" : "false");
            if (Complete) row.Field("count", ObservedCount.ToString(CultureInfo.InvariantCulture));
            if (_diagnostics.Count != 0)
                row.Field("diagnosticIds", string.Join(",", _diagnostics)).Field("diagnosticAdmission", _diagnosticsQueued ? "queued" : "refused-or-unavailable");
            return row;
        }
    }
}

/// <summary>Freezes read failures on the caller. The recorder sees only managed strings, never an exception,
/// reflected member or live game object. Its normal frozen-record path preserves oversized evidence as a budgeted
/// document. Admission is not a claim of persistence; the session index owns persistence/drop outcomes.</summary>
internal static class CaptureReadDiagnostics
{
    [ThreadStatic] private static CaptureSnapshot? _snapshot;
    private static long _nextId;

    internal readonly struct Receipt
    {
        internal Receipt(long id, bool queued) { Id = id; Queued = queued; }
        internal long Id { get; }
        internal bool Queued { get; }
    }

    /// <summary>A cooperative cursor must not leave its context installed between frames or after cancellation.</summary>
    internal static IEnumerable<int> InSnapshot(CaptureSnapshot snapshot, IEnumerable<int> work)
    {
        IEnumerator<int>? cursor = null;
        var complete = false;
        try
        {
            using (Enter(snapshot)) cursor = work.GetEnumerator();
            while (true)
            {
                bool more;
                int value = 0;
                using (Enter(snapshot))
                {
                    more = cursor.MoveNext();
                    if (more) value = cursor.Current;
                }
                if (!more) { complete = true; yield break; }
                yield return value;
            }
        }
        finally
        {
            if (!complete) snapshot.AcquisitionComplete = false;
            using (Enter(snapshot)) cursor?.Dispose();
        }
    }

    private static Scope Enter(CaptureSnapshot snapshot) => new(snapshot);
    private readonly struct Scope : IDisposable
    {
        private readonly CaptureSnapshot? _previous;
        internal Scope(CaptureSnapshot snapshot) { _previous = _snapshot; _snapshot = snapshot; }
        public void Dispose() => _snapshot = _previous;
    }

    internal static Receipt Report(string where, string stage, Exception error, Type? table = null,
        MemberInfo? member = null, string? expression = null, string? caller = null, int line = 0, Type? target = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        try
        {
            // Copy every value before enqueue. Exception.ToString also carries IL2CPP native stacks that do not
            // appear in CLR StackTrace/InnerException. Keep the wrapper and root as distinct facts.
            var snapshot = _snapshot;
            if (snapshot != null) snapshot.AcquisitionComplete = false;
            var reason = snapshot?.Reason;
            var scope = snapshot?.Scope;
            var tick = snapshot?.Tick;
            var epoch = snapshot?.WorldEpoch;
            var snapshotId = snapshot?.Identity;
            var tableName = table?.FullName;
            var assembly = table?.Assembly.FullName;
            var declaringType = member?.DeclaringType?.FullName;
            var signature = member?.ToString();
            var memberName = member?.Name;
            var memberAssembly = member?.Module.Assembly.FullName;
            var targetType = target?.AssemblyQualifiedName;
            var exceptions = new List<FrozenException>();
            for (var current = error; current != null; current = current.InnerException)
                exceptions.Add(new FrozenException(current));
            var chain = exceptions.ToArray();
            var fullText = Read(error.ToString);
            var queued = RecSession.WriteFrozen("log", "capture_error", json =>
            {
                json.WriteNumber("diagnosticId", id);
                json.WriteString("where", where);
                json.WriteString("stage", stage);
                json.WriteString("table", tableName);
                json.WriteString("assembly", assembly);
                json.WriteString("member", memberName);
                json.WriteString("declaringType", declaringType);
                json.WriteString("signature", signature);
                json.WriteString("memberAssembly", memberAssembly);
                json.WriteString("targetType", targetType);
                json.WriteString("expression", expression);
                json.WriteString("caller", caller);
                json.WriteNumber("line", line);
                json.WriteString("reason", reason);
                json.WriteString("scope", scope);
                Number(json, "snapshotId", snapshotId);
                Number(json, "snapshotTick", tick);
                Number(json, "worldEpoch", epoch);
                json.WriteString("error", chain[0].Type);
                json.WriteString("message", chain[0].Message);
                json.WriteString("stack", chain[0].Stack);
                json.WriteString("exceptionText", fullText);
                json.WriteString("rootError", chain[^1].Type);
                json.WriteString("rootMessage", chain[^1].Message);
                json.WriteString("rootStack", chain[^1].Stack);
                json.WriteStartArray("exceptionChain");
                foreach (var item in chain)
                {
                    json.WriteStartObject();
                    json.WriteString("type", item.Type);
                    json.WriteString("message", item.Message);
                    json.WriteString("stack", item.Stack);
                    json.WriteString("text", item.Text);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }, RecFrozenBytes.Measure(new object?[] { where, stage, tableName, assembly, memberName, declaringType,
                signature, memberAssembly, targetType, expression, caller, reason, scope, chain, fullText }));
            return new Receipt(id, queued);
        }
        catch (Exception) { return new Receipt(id, false); } // The diagnostic writer itself failed; Receipt.Queued=false is the explicit result its caller records.
    }

    internal static string? ReadWorld(Func<string?> read, string expression, string caller, int line)
    {
        try { return read(); }
        catch (Exception error)
        {
            Report("ForgeDevelopment.Native.CaptureWorld:read", "read", error,
                expression: expression, caller: caller, line: line);
            return null;
        }
    }

    /// <summary>The same guarded read for a static lambda: the object it reads is passed in, so the call builds no
    /// closure.</summary>
    internal static string? ReadWorld<TState>(TState state, Func<TState, string?> read, string expression, string caller, int line)
    {
        try { return read(state); }
        catch (Exception error)
        {
            Report("ForgeDevelopment.Native.CaptureWorld:read", "read", error,
                expression: expression, caller: caller, line: line);
            return null;
        }
    }

    private static void Number(Utf8JsonWriter json, string name, long? value)
    { if (value.HasValue) json.WriteNumber(name, value.Value); else json.WriteNull(name); }

    private static string? Read(Func<string?> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable: exception formatter threw " + error.GetType().FullName; }
    }

    private sealed class FrozenException
    {
        internal FrozenException(Exception error)
        {
            Type = error.GetType().FullName ?? error.GetType().Name;
            Message = Read(() => error.Message);
            Stack = Read(() => error.StackTrace);
            Text = Read(() => error.ToString());
        }
        internal string Type { get; }
        internal string? Message { get; }
        internal string? Stack { get; }
        internal string? Text { get; }
    }
}
