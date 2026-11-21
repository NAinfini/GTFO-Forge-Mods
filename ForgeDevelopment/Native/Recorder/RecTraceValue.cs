using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>Freezes a value through the existing bounded reader, then validates its JSON before it can enter
/// a paired observation. A cap/error/cycle never becomes a complete snapshot. Each value has the same byte,
/// element, depth and time limits as the existing recorder; the enclosing session applies its own record cap.</summary>
internal readonly record struct RecTraceValue(string Status, string? Error = null)
{
    internal bool Complete => Status is "observed" or "explicit-null";

    /// <summary>Projects the value through the bounded reader into this thread's scratch, then copies the finished bytes
    /// into the record as <paramref name="name"/>. A value over its budget or failing to serialize is not written; its
    /// outcome says why. Nothing is parsed or cloned: the scratch is validated JSON already.</summary>
    internal static RecTraceValue Write(Utf8JsonWriter json, string name, object? value)
    {
        Scratch scratch;
        string status;
        try
        {
            var projection = Projection.Take();
            var projected = projection.Read(value, 0);
            // One buffer and writer per thread are reused for every value instead of a fresh 64 KB buffer each time.
            // A capture started while this thread's scratch is in use (a getter that re-enters) gets its own.
            scratch = _scratch ??= new Scratch();
            if (scratch.InUse) scratch = new Scratch();
            scratch.InUse = true;
            try
            {
                scratch.Buffer.Reset();
                if (scratch.Writer == null) scratch.Writer = new Utf8JsonWriter(scratch.Buffer);
                else scratch.Writer.Reset(scratch.Buffer);
                RecReflect.WriteValue(scratch.Writer, projected, 0);
                scratch.Writer.Flush();
                status = projection.Problem ?? Problem(scratch.Buffer.WrittenSpan) ?? (value == null ? "explicit-null" : "observed");
                projection.Release();
            }
            catch { scratch.InUse = false; throw; }
        }
        catch (RecValueTooLargeException) { return new("budget-exceeded"); }
        catch (Exception error) { return new("serialization-error", error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200)); }
        // Outside the catch: the record's own budget failing here belongs to the record, not to this value.
        try
        {
            json.WritePropertyName(name);
            json.WriteRawValue(scratch.Buffer.WrittenSpan, skipInputValidation: true);
        }
        finally { scratch.InUse = false; }
        return new(status);
    }
    private sealed class Scratch
    {
        internal readonly RecJsonBuffer Buffer = new(RecSession.DefaultRecordBudget);
        internal Utf8JsonWriter? Writer;
        internal bool InUse;
    }
    [ThreadStatic] private static Scratch? _scratch;
    // Native List<T> does not implement the managed IEnumerable contract. Read only its
    // live Count/Item range, rather than serializing unused backing storage or wrapper plumbing.
    // Native fields use the existing evidence reader so a failed getter is distinct from null.
    private sealed class Projection
    {
        // One per thread, reused: a value's path set empties itself as the read unwinds, so only the deadline starts over.
        [ThreadStatic] private static Projection? _free;
        private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);
        private long _deadline;
        internal static Projection Take()
        {
            var projection = _free ?? new Projection();
            _free = null;
            projection._deadline = Environment.TickCount64 + RecReflect.BudgetMilliseconds;
            projection.Problem = null;
            return projection;
        }
        internal void Release() { _path.Clear(); _free = this; }
        internal string? Problem { get; private set; }
        private object? Incomplete(string problem) { Problem ??= problem; return null; }
        internal object? Read(object? value, int depth)
        {
            if (Environment.TickCount64 > _deadline) return Incomplete("time-budget-exceeded");
            if (value != null && RecReflect.Probe.NativeReadUnavailable(value) != null)
                return Incomplete("native-read-skipped");
            if (value == null || RecReflect.IsScalar(value)) return value;
            if (RecReflect.Probe.TryScalar(value, out var scalar)) return Read(scalar, depth);
            if (depth >= RecReflect.MaxDepth) return Incomplete("depth-capped");
            if (!_path.Add(value)) return Incomplete("cycle");
            try
            {
                var type = value.GetType();
                if (value is IDictionary map)
                {
                    var entries = new Dictionary<string, object?>();
                    foreach (DictionaryEntry pair in map)
                    {
                        if (entries.Count >= RecReflect.MaxElements) { Incomplete("element-capped"); break; }
                        if (Environment.TickCount64 > _deadline) { Incomplete("time-budget-exceeded"); break; }
                        if (pair.Key is not string key) { Incomplete("unsupported-map-key"); continue; }
                        entries[key] = Read(pair.Value, depth + 1);
                    }
                    return entries;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Il2CppSystem.Collections.Generic.List`1")
                {
                    var count = VanillaValueReading.ReadMember(value, "Count");
                    var item = type.GetProperty("Item", new[] { typeof(int) });
                    if (!count.Available || count.Value is not int length || length < 0 || item?.GetGetMethod() == null)
                        return Incomplete("collection-unavailable");
                    var items = new List<object?>();
                    for (var index = 0; index < length; index++)
                    {
                        if (index >= RecReflect.MaxElements) { Incomplete("element-capped"); break; }
                        if (Environment.TickCount64 > _deadline) { Incomplete("time-budget-exceeded"); break; }
                        items.Add(Read(item.GetValue(value, new object[] { index }), depth + 1));
                    }
                    return items;
                }
                if (value is IEnumerable sequence && value is not IDictionary)
                {
                    var items = new List<object?>();
                    foreach (var item in sequence)
                    {
                        if (items.Count >= RecReflect.MaxElements) { Incomplete("element-capped"); break; }
                        if (Environment.TickCount64 > _deadline) { Incomplete("time-budget-exceeded"); break; }
                        items.Add(Read(item, depth + 1));
                    }
                    return items;
                }
                // These four candidate fields are verified in the interop source. An empty or partial
                // metadata list must not turn an opaque HeatType wrapper into a complete weight snapshot.
                var fields = type.FullName == "EnemyPopulationManager+HeatType"
                    ? new[] { "m_enemyType", "m_heat", "m_weight", "m_grabCountRef" }
                    : RecReflect.Probe.NativeFieldNames(value);
                if (fields == null) { Incomplete("unsupported-object"); return value; }
                var observed = new Dictionary<string, object?>();
                foreach (var field in fields)
                {
                    if (observed.Count >= RecReflect.MaxElements) { Incomplete("element-capped"); break; }
                    if (Environment.TickCount64 > _deadline) { Incomplete("time-budget-exceeded"); break; }
                    var read = VanillaValueReading.ReadMember(value, field);
                    if (!read.Available) { Incomplete(read.Outcome); continue; }
                    observed[field] = Read(read.Value, depth + 1);
                }
                if (observed.Count == 0) Incomplete("opaque-object");
                return observed;
            }
            catch (Exception) { return Incomplete("getter-error"); } // The read is recorded as the explicit incomplete outcome "getter-error".
            finally { _path.Remove(value); }
        }
    }

    // A cap or failure marker as a member name at any depth makes the value incomplete.
    private static string? Problem(ReadOnlySpan<byte> value)
    {
        var reader = new Utf8JsonReader(value);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && (reader.ValueTextEquals("$truncatedAt") || reader.ValueTextEquals("$depthCapped")
                    || reader.ValueTextEquals("$error") || reader.ValueTextEquals("$ref") || reader.ValueTextEquals("$nativeRead")))
                return "incomplete-value";
        return null;
    }
    internal void WriteStatus(Utf8JsonWriter json, string name)
    {
        json.WriteStartObject(name);
        json.WriteString("status", Status);
        if (Error != null) json.WriteString("error", Error);
        json.WriteEndObject();
    }
}
