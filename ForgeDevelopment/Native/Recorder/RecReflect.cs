using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>
/// A bounded JSON sink. <see cref="System.Text.Json.Utf8JsonWriter"/> cannot be pointed at a cap, so every recorder
/// record is composed into one of these: the writer is handed the buffer's own memory and a write that would pass the
/// cap throws <see cref="RecValueTooLargeException"/>. That is what one over-long record costs — the session drops
/// that record, counts it and writes a small error record of the same channel and kind in its place, instead of letting a
/// single object tree grow without bound.
/// </summary>
internal sealed class RecJsonBuffer : IBufferWriter<byte>
{
    private readonly byte[] _buffer;
    private int _written;

    internal RecJsonBuffer(int limit) { _buffer = new byte[Math.Max(256, limit)]; }
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    internal int Length => _written;
    internal void Reset() => _written = 0;

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (_written + Math.Max(sizeHint, 256) > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (_written + Math.Max(sizeHint, 256) > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        return _buffer.AsSpan(_written);
    }
}

/// <summary>Raised when one record would not fit the per-record budget. The session counts the record as dropped.</summary>
internal sealed class RecValueTooLargeException : Exception
{
    internal RecValueTooLargeException(int limit) : base("A recorded value exceeded the " + limit + " byte record budget.") { }
}

/// <summary>
/// Turns managed values into JSON and records unread native observations explicitly.
/// Native access is refused before reflection when the probe cannot establish read safety;
/// a managed read failure writes <c>"$error:..."</c>, and a value already on the
/// current path is written as <c>{"$ref":id}</c>. Depth, element count and wall-clock budget are capped, and the
/// budget is tested before each member, so one hostile object graph costs a bounded amount of frame time.
/// </summary>
internal static class RecReflect
{
    internal const int MaxDepth = 6;
    internal const int MaxElements = 64;
    internal const int BudgetMilliseconds = 20;

    /// <summary>Types the reader answers itself: enums are written as name plus number, and the game's own value types
    /// are read through <see cref="IRecValueProbe"/> because interop exposes them as private native fields.</summary>
    private static IRecValueProbe _probe = RecInertProbe.Instance;
    private static readonly ConcurrentDictionary<(Type Type, IntPtr Class, string Name), Lazy<Func<object, object?>?>> Members = new();
    private static readonly ConcurrentDictionary<(Type Type, IntPtr Class, string Name), MemberDiagnostic> MemberDiagnostics = new();
    private static int _writingMemberDiagnostics;

    internal static IRecValueProbe Probe
    {
        get => _probe;
        set
        {
            _probe = value;
            Members.Clear();
            MemberDiagnostics.Clear();
        }
    }

    private sealed record MemberDiagnostic(string @class, string managedType, string nativeClass, string member, string reason, string? error);

    /// <summary>Writes one value under a property name. <paramref name="name"/> must be a JSON property name; an
    /// array element or a member inside a value type is written through the writer itself instead.</summary>
    internal static void WriteValue(Utf8JsonWriter w, object? value, int depth)
    {
        ArgumentNullException.ThrowIfNull(w);
        var writer = RecWriter.Take(w);
        try { writer.WriteValue(value, Math.Max(0, depth)); }
        finally { writer.Release(); }
    }

    /// <summary>Reads a dotted member path such as <c>m_tagMarker.m_title.text</c>. A step is a field or a property,
    /// public or not, on an interop or managed object; a step that cannot be read answers null rather than throwing,
    /// so the caller can record the path it asked for.</summary>
    internal static object? ReadPath(object? root, string path)
    {
        if (root == null) return null;
        if (string.IsNullOrEmpty(path)) return root;
        var current = root;
        var steps = PathSteps(path);
        for (var step = 0; ; step++)
        {
            if (Probe.NativeReadUnavailable(current) is { } reason)
                return new Dictionary<string, object?> { ["$nativeRead"] = reason };
            var name = steps[step];
            if (name.Length != 0) current = ReadMember(current, name);
            if (current == null || step == steps.Length - 1) return current;
        }
    }

    // A configured path is read on every traced call and its steps never change, so each path is split once.
    private static readonly ConcurrentDictionary<string, string[]> PathStepCache = new(StringComparer.Ordinal);
    internal static string[] PathSteps(string path)
        => PathStepCache.TryGetValue(path, out var steps) ? steps : PathStepCache.GetOrAdd(path, static p => p.Split('.'));

    /// <summary>Reads several paths as one row of name/value pairs. A path that reads to nothing keeps its row with a
    /// null value, so a trace record never silently loses the field it was configured for.</summary>
    internal static (string Path, object? Value)[] ReadPaths(object? root, IReadOnlyList<string> paths)
    {
        var values = new (string, object?)[paths.Count];
        for (var i = 0; i < paths.Count; i++) values[i] = (paths[i], ReadPath(root, paths[i]));
        return values;
    }

    /// <summary>Managed type plus pointer identity for interop objects; labelling an
    /// observation must not invoke an engine instance-ID or name getter.</summary>
    internal static string Describe(object? obj)
    {
        if (obj == null) return "null";
        if (obj is string text) return "string(" + text.Length.ToString(CultureInfo.InvariantCulture) + ")";
        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum) return type.Name + "(" + Convert.ToString(obj, CultureInfo.InvariantCulture) + ")";
        var id = InstanceId(obj, out var native);
        // Native identity is a pointer, not a Unity instance ID. Neither a wrapper nor
        // a nonzero pointer proves that the engine object is initialized or still alive.
        if (native) return type.FullName + "#" + id.ToString("X", CultureInfo.InvariantCulture) + "(pointer; native-name-not-read)";
        if (Probe.NativeReadUnavailable(obj) != null)
            return type.FullName + "#" + id.ToString(CultureInfo.InvariantCulture) + "(managed; native-name-not-read; pointer-unavailable)";
        var name = Probe.IsUnityObject(obj) ? ReadMember(obj, "name") as string : null;
        return type.FullName + "#" + id.ToString(CultureInfo.InvariantCulture) + (native ? "" : "(managed)")
            + (string.IsNullOrEmpty(name) ? "" : " \"" + Truncate(name!, 64) + "\"");
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Pointer identity without an engine call; otherwise CLR identity without
    /// invoking a wrapper's overridden hash code. This does not prove native lifetime.</summary>
    internal static long InstanceId(object obj, out bool native)
    {
        native = false;
        if (Probe.TryInstanceId(obj, out var id)) { native = true; return id; }
        return (obj.GetType().GetHashCode() * 1_000_003L) + RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>Reads one member by name: property first, then field, then the native field the interop type does not
    /// project. It answers null for a member that cannot be read, and never throws.</summary>
    internal static object? ReadMember(object target, string name)
    {
        if (Probe.NativeReadUnavailable(target) is { } reason)
            return new Dictionary<string, object?> { ["$nativeRead"] = reason };
        var key = (Type: target.GetType(), Class: Probe.NativeClass(target), Name: name);
        try
        {
            var read = Members.GetOrAdd(key, static k => new Lazy<Func<object, object?>?>(() => ResolveMember(k),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            if (read != null) return read(target);
        }
        catch (Exception error)
        {
            // A failed getter is not a missing field. Keep its accessor and report the read failure once.
            RecordMemberFailure(key, "member-unreadable", error);
        }
        return null;
    }

    private static Func<object, object?>? ResolveMember((Type Type, IntPtr Class, string Name) key)
    {
        try
        {
            var read = ResolveManagedMember(key.Type, key.Name) ?? Probe.ResolveNativeMember(key.Class, key.Name);
            if (read == null) RecordMemberFailure(key, "member-missing", null);
            return read;
        }
        catch (Exception error)
        {
            RecordMemberFailure(key, "member-resolution-failed", error);
            return null;
        }
    }

    // The answer for a loaded type never changes, including "no such member", so a repeated reader (the callback
    // readback of the two Y4 observers) resolves each (type, name) once.
    private static readonly ConcurrentDictionary<(Type Type, string Name, bool StaticOnly), Func<object, object?>?> ManagedMembers = new();
    internal static Func<object, object?>? ResolveManagedMemberCached(Type type, string name, bool staticOnly = false)
        => ManagedMembers.GetOrAdd((type, name, staticOnly), static key => ResolveManagedMember(key.Type, key.Name, key.StaticOnly));

    internal static Func<object, object?>? ResolveManagedMember(Type type, string name, bool staticOnly = false)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (!staticOnly) flags |= BindingFlags.Instance;
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(name, flags);
            var getter = property?.GetIndexParameters().Length == 0 ? property.GetGetMethod(true) : null;
            if (getter != null) return BindGetter(getter) ?? (target => Invoke(getter, target));
        }
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, flags);
            if (field != null) return target => field.GetValue(field.IsStatic ? null : target);
        }
        return null;
    }

    // An instance getter on a reference type is called through a typed delegate instead of MethodInfo.Invoke on every
    // read. Anything the delegate cannot express (static, value-type owner, by-ref or pointer result) keeps Invoke.
    private static readonly MethodInfo BindGetterDefinition = typeof(RecReflect).GetMethod(nameof(BindGetterOf), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Func<object, object?>? BindGetter(MethodInfo getter)
    {
        var owner = getter.DeclaringType;
        var result = getter.ReturnType;
        if (getter.IsStatic || owner == null || owner.IsValueType || owner.ContainsGenericParameters
            || result == typeof(void) || result.IsByRef || result.IsPointer || result.IsByRefLike) return null;
        try { return (Func<object, object?>)BindGetterDefinition.MakeGenericMethod(owner, result).Invoke(null, new object[] { getter })!; }
        catch (Exception) { return null; } // No bindable getter: null sends the caller to the reflected invoke path.
    }
    private static Func<object, object?> BindGetterOf<TOwner, TResult>(MethodInfo getter) where TOwner : class
    {
        var read = (Func<TOwner, TResult>)Delegate.CreateDelegate(typeof(Func<TOwner, TResult>), getter);
        return target => read((TOwner)target);
    }

    private static void RecordMemberFailure((Type Type, IntPtr Class, string Name) key, string reason, Exception? error)
    {
        var diagnosticKey = (Type: key.Class == IntPtr.Zero ? key.Type : typeof(IRecValueProbe), key.Class, key.Name);
        if (MemberDiagnostics.ContainsKey(diagnosticKey)) return;
        var managedType = key.Type.FullName ?? key.Type.Name;
        var diagnostic = new MemberDiagnostic(Probe.NativeClassName(key.Class) ?? managedType, managedType, key.Class.ToString("X"), key.Name,
            reason, error == null ? null : error.GetType().Name + ": " + Truncate(error.Message, 200));
        if (!MemberDiagnostics.TryAdd(diagnosticKey, diagnostic)) return;
        // A member read can run inside RecSession.Write's body. A separate frozen document avoids re-entering
        // that method and resetting its shared JSON buffer. Each class/member has exactly one diagnostic row.
        WriteMemberDiagnostics();
    }

    private static void WriteMemberDiagnostics()
    {
        while (true)
        {
            // Other readers never wait here while holding the session's write lock. One writer publishes a
            // cumulative snapshot, then catches any distinct failures added while it was serializing.
            if (Interlocked.CompareExchange(ref _writingMemberDiagnostics, 1, 0) != 0) return;
            var count = 0;
            try
            {
                var members = MemberDiagnostics.Values.ToArray();
                count = members.Length;
                RecSession.WriteDocument("recorder-member-diagnostics.forge", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "forge.rec.member-diagnostics",
                    members
                }));
            }
            finally { Volatile.Write(ref _writingMemberDiagnostics, 0); }
            if (count == MemberDiagnostics.Count) return;
        }
    }

    private static object? Invoke(MethodInfo method, object target, params object?[] arguments)
    {
        try { return method.Invoke(method.IsStatic ? null : target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    /// <summary>True when the value carries no state beyond itself: nulls, primitives, strings, enums and the game's
    /// value types. RecTracer uses the same test to decide that a `changes` diff has something to compare.</summary>
    internal static bool IsScalar(object? value)
    {
        if (value == null) return true;
        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        return IsScalarOf(type) || Probe.Handles(type);
    }

    private static bool IsScalarOf(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);

    /// <summary>Cycles, depth, element caps and per-reader error guards live here. Every value on the current path is
    /// held in <see cref="_ids"/> until its own object ends, so a cycle answers the id it was first written under.</summary>
    internal sealed class RecWriter
    {
        // One per thread, reused by the top-level WriteValue: its id table empties as each object ends, so only the writer and the deadline change.
        [ThreadStatic] private static RecWriter? _free;
        private Utf8JsonWriter _json;
        private Dictionary<object, int>? _ids;
        private long _deadline;
        private int _nextId = 1;

        private RecWriter(Utf8JsonWriter json) => _json = json;

        internal static RecWriter Take(Utf8JsonWriter json)
        {
            var writer = _free;
            _free = null;
            if (writer == null) writer = new RecWriter(json);
            writer._json = json;
            writer._nextId = 1;
            writer._deadline = Environment.TickCount64 + BudgetMilliseconds;
            return writer;
        }

        internal void Release()
        {
            _ids?.Clear();
            _free = this;
        }

        /// <summary>The writer a probe composes into when it answers a value type itself.</summary>
        internal Utf8JsonWriter Json => _json;

        internal void WriteNamed(object? value, string name, int depth)
        {
            _json.WritePropertyName(name);
            WriteValue(value, depth);
        }

        /// <summary>A value type the probe owns: Unity's own Vector3, Color and Quaternion, and their IL2CPP
        /// wrappers. The probe writes the members it reads; everything the reader does still applies to them.</summary>
        internal void WriteValue(object? value, int depth)
        {
            if (value == null) { _json.WriteNullValue(); return; }
            if (Probe.NativeReadUnavailable(value) is { } reason)
            {
                _json.WriteStartObject();
                _json.WriteString("$type", value.GetType().FullName);
                _json.WriteNumber("$pointer", InstanceId(value, out _));
                _json.WriteString("$nativeRead", reason);
                _json.WriteEndObject();
                return;
            }
            if (Probe.TryScalar(value, out var scalar)) { WriteValue(scalar, depth); return; }
            var type = value.GetType();
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) { WriteSimple(value, underlying); return; }
            if (type.IsEnum) { WriteEnum(value, type); return; }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid))
            { WriteSimple(value, type); return; }
            if (Probe.Handles(type)) { Probe.Write(this, value, depth); return; }
            if (value is IDictionary map) { WriteDictionary(map, depth); return; }
            if (value is IEnumerable sequence) { WriteSequence(sequence, depth); return; }
            WriteObject(value, depth);
        }

        private void WriteSimple(object value, Type type)
        {
            switch (value)
            {
                case bool b: _json.WriteBooleanValue(b); break;
                case string s: _json.WriteStringValue(s); break;
                case char c: _json.WriteStringValue(c.ToString()); break;
                // Numbers are written as numbers: a trace record that turned every id into a string would sort and
                // compare differently from the value it claims to record.
                case float f: WriteNumber(f); break;
                case double d: WriteNumber(d); break;
                case decimal m: _json.WriteNumberValue(m); break;
                case byte v: _json.WriteNumberValue(v); break;
                case sbyte v: _json.WriteNumberValue(v); break;
                case short v: _json.WriteNumberValue(v); break;
                case ushort v: _json.WriteNumberValue(v); break;
                case int v: _json.WriteNumberValue(v); break;
                case uint v: _json.WriteNumberValue(v); break;
                case long v: _json.WriteNumberValue(v); break;
                case ulong v: _json.WriteNumberValue(v); break;
                case DateTime dt: _json.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture)); break;
                case DateTimeOffset dto: _json.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture)); break;
                case TimeSpan ts: _json.WriteStringValue(ts.ToString()); break;
                case Guid g: _json.WriteStringValue(g.ToString()); break;
                default:
                    if (value is IFormattable formattable) _json.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                    else _json.WriteStringValue(value.ToString() ?? "");
                    break;
            }
        }

        private void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _json.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                return;
            }
            _json.WriteNumberValue(value);
        }

        internal void WriteEnum(object value, Type type)
        {
            _json.WriteStartObject();
            _json.WriteString("name", Enum.GetName(type, value) ?? value.ToString());
            try { _json.WriteNumber("value", Convert.ToInt64(value, CultureInfo.InvariantCulture)); }
            catch (Exception) { _json.WriteString("value", value.ToString() ?? ""); } // The numeric value does not fit; the text form of the same enum value is recorded instead.
            _json.WriteString("type", type.Name);
            _json.WriteEndObject();
        }

        private void WriteSequence(IEnumerable sequence, int depth)
        {
            if (!Enter(sequence, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartArray();
            var written = 0;
            var truncated = false;
            try
            {
                foreach (var item in sequence)
                {
                    if (written >= MaxElements || OverBudget()) { truncated = true; break; }
                    WriteValue(item, depth + 1);
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                WriteError(error);
            }
            finally { Exit(sequence); }
            _json.WriteEndArray();
            if (truncated) _json.WriteNumber("$truncatedAt", written);
        }

        private void WriteDictionary(IDictionary map, int depth)
        {
            if (!Enter(map, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartObject();
            var written = 0;
            var truncated = false;
            try
            {
                foreach (DictionaryEntry entry in map)
                {
                    if (written >= MaxElements || OverBudget()) { truncated = true; break; }
                    _json.WritePropertyName(Truncate(entry.Key?.ToString() ?? "null", 128));
                    WriteValue(entry.Value, depth + 1);
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            }
            finally { Exit(map); }
            _json.WriteEndObject();
            if (truncated) _json.WriteNumber("$truncatedAt", written);
        }

        private void WriteObject(object value, int depth)
        {
            if (!Enter(value, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartObject();
            var type = value.GetType();
            var nativeFields = Probe.NativeFieldNames(value);
            _json.WriteString("$type", Probe.NativeClassName(Probe.NativeClass(value)) ?? type.FullName);
            try
            {
                if (Probe.IsDestroyed(value)) _json.WriteBoolean("$destroyed", true);
                if (depth >= MaxDepth) { _json.WriteBoolean("$depthCapped", true); return; }
                var written = 0;
                if (nativeFields != null)
                {
                    // IL2CPP wrappers also expose reflection/ownership plumbing. Walking those
                    // computed getters can invoke stripped native reflection functions. Native
                    // state comes from the game's actual field metadata, not wrapper properties.
                    foreach (var name in nativeFields)
                    {
                        if (written >= MaxElements || OverBudget()) { _json.WriteNumber("$truncatedAt", written); break; }
                        _json.WritePropertyName(name);
                        WriteValue(ReadMember(value, name), depth + 1);
                        written++;
                    }
                    return;
                }
                foreach (var member in Members(type))
                {
                    if (written >= MaxElements || OverBudget()) { _json.WriteNumber("$truncatedAt", written); break; }
                    _json.WritePropertyName(member.Name);
                    try { WriteValue(ReadMemberOrNull(value, member), depth + 1); }
                    catch (RecValueTooLargeException) { throw; }
                    catch (Exception error) { WriteError(error); }
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            }
            finally
            {
                Exit(value);
                _json.WriteEndObject();
            }
        }

        private static object? ReadMemberOrNull(object target, MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo field: return field.GetValue(field.IsStatic ? null : target);
                case PropertyInfo property:
                    var getter = property.GetGetMethod(true);
                    return getter == null ? null : Invoke(getter, target);
                default: return null;
            }
        }

        /// <summary>The members worth reading: readable instance fields and indexer-free properties, declared and
        /// inherited. Interop assemblies mirror every native method and field as a <c>NativeMethodInfoPtr_*</c> or
        /// <c>NativeFieldInfoPtr_*</c> static pointer; those are addresses, not state, and are skipped by name.</summary>
        internal static IEnumerable<MemberInfo> Members(Type type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || IsInteropPointer(field.Name)) continue;
                    if (seen.Add(field.Name)) yield return field;
                }
                foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
                    if (seen.Add(property.Name)) yield return property;
                }
            }
        }

        private static bool IsInteropPointer(string name)
            => name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) || name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal);

        /// <summary>Registers a value on the current path. A repeated reference answers false with the id it was first
        /// written under, which is what turns a cycle into <c>{"$ref":id}</c> instead of a stack overflow.</summary>
        private bool Enter(object value, int depth, out int id)
        {
            _ids ??= new(ReferenceComparer.Instance);
            if (_ids.TryGetValue(value, out id)) return false;
            id = _nextId++;
            _ids[value] = id;
            return true;
        }

        private void Exit(object value) => _ids?.Remove(value);

        private void WriteReference(int id)
        {
            _json.WriteStartObject();
            _json.WriteNumber("$ref", id);
            _json.WriteEndObject();
        }

        private void WriteError(Exception error)
        {
            _json.WriteStartObject();
            _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            _json.WriteEndObject();
        }

        private bool OverBudget() => Environment.TickCount64 > _deadline;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>What the reader needs from the runtime it reads. The game build installs the IL2CPP implementation; a test
/// installs a managed double, so cycle, depth, element-cap and error handling are all exercised without Unity.</summary>
internal interface IRecValueProbe
{
    // Must use managed type/observation state only, before any native metadata,
    // getter, enumeration, scalar unboxing or member resolution is attempted.
    string? NativeReadUnavailable(object value) => null;
    bool TryInstanceId(object value, out long id);
    bool IsDestroyed(object value);
    bool IsUnityObject(object value);
    IntPtr NativeClass(object value);
    string? NativeClassName(IntPtr nativeClass) => null;
    IEnumerable<string>? NativeFieldNames(object value) => null;
    bool TryScalar(object value, out object? scalar) { scalar = null; return false; }
    Func<object, object?>? ResolveNativeMember(IntPtr nativeClass, string name);
    /// <summary>True for the game's own value types, whose data lives in native fields rather than managed ones.
    /// The probe owns both the test and the write for them, so the pure reader stays free of Unity references.</summary>
    bool Handles(Type type);
    /// <summary>Writes one of the probe's own value types. Members that are not simple are handed back to the
    /// writer, so the same depth, element and budget caps apply inside them.</summary>
    void Write(RecReflect.RecWriter writer, object value, int depth);
}

/// <summary>The default: no engine, no interop. Reading an interop object under this probe reports only its managed
/// view, which is what a test process and an early startup both have.</summary>
internal sealed class RecInertProbe : IRecValueProbe
{
    internal static readonly RecInertProbe Instance = new();
    public bool TryInstanceId(object value, out long id) { id = 0; return false; }
    public bool IsDestroyed(object value) => false;
    public bool IsUnityObject(object value) => false;
    public IntPtr NativeClass(object value) => IntPtr.Zero;
    public Func<object, object?>? ResolveNativeMember(IntPtr nativeClass, string name) => null;
    public bool Handles(Type type) => false;
    public void Write(RecReflect.RecWriter writer, object value, int depth) { }
}


