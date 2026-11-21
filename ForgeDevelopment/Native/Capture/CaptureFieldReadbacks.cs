using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeDevelopment.Native;

/// <summary>Reads only reviewed member paths and freezes each result as managed typed data.</summary>
internal static class CaptureFieldReadbacks
{
    internal sealed record Entry(string Id, string Source, string Type, string[] Identity, string[] Paths);
    internal sealed record PathRead(string Status, RecProjectionValue Value, string? Error, string? NativeType = null);

    internal static Entry[] Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("nativeReadbacks", out var plan)) return Array.Empty<Entry>();
        var entries = new List<Entry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in plan.EnumerateArray())
        {
            string Required(string name) => row.GetProperty(name).GetString() ?? throw new FormatException(name);

            string[] ReadPaths(string name)
            {
                var paths = new List<string>();
                foreach (var value in row.GetProperty(name).EnumerateArray())
                {
                    var path = value.GetString() ?? throw new FormatException(name);
                    if (!Regex.IsMatch(path, @"^(?:[A-Za-z_]\w*(?:\[\])?|@[A-Za-z_]\w*)(?:\.(?:[A-Za-z_]\w*(?:\[\])?|@[A-Za-z_]\w*))*$"))
                        throw new FormatException("Invalid readback path: " + path);
                    paths.Add(path);
                }
                if (paths.Count != paths.Distinct(StringComparer.Ordinal).Count()) throw new FormatException("Duplicate readback path: " + name);
                return paths.ToArray();
            }

            var entry = new Entry(Required("id"), Required("source"), Required("type"), ReadPaths("identity"), ReadPaths("paths"));
            // Behavior-binding ("binding", "input", source "entity") and map-model ("provider") rows read Forge Engine's kernel
            // and left with it; such a row is refused by name instead of being parsed and never read.
            if (!ids.Add(entry.Id) || entry.Id.Length == 0 || entry.Type.Length == 0 || entry.Paths.Length == 0
                || entry.Source is not ("components" or "blocks" or "singleton" or "static")
                || row.TryGetProperty("binding", out _) || row.TryGetProperty("input", out _) || row.TryGetProperty("provider", out _))
                throw new FormatException("Invalid readback entry: " + entry.Id);
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    internal static IEnumerable<int> Collect(CaptureSnapshot snapshot, IEnumerable<Entry> entries,
        Func<Entry, IEnumerable<object>> roots, Func<object, string, object?> cast, Func<long> frame,
        Func<object, string?>? unavailable = null, Func<Entry, object, string>? rootIdentity = null)
    {
        var section = snapshot.Section("nativeReadbacks", "capture-readbacks.forge/nativeReadbacks");
        foreach (var group in entries.GroupBy(entry => (entry.Source, entry.Type)))
        {
            var plan = group.ToArray();
            yield return 0;
            IEnumerator<object>? cursor = null;
            string? failure = null;
            try { cursor = roots(plan[0]).GetEnumerator(); }
            catch (Exception error) { failure = Failure(error); }
            if (cursor == null || failure != null)
            {
                foreach (var entry in plan) Add(snapshot, section, entry, "root", "", "root-unavailable",
                    RecProjectionValue.Error("unknown", failure ?? "root-enumerator-unavailable"), failure, frame());
                continue;
            }

            var count = 0;
            try
            {
                while (true)
                {
                    yield return 0;
                    object? root = null;
                    bool next;
                    try { next = cursor.MoveNext(); if (next) root = cursor.Current; }
                    catch (Exception error) { failure = Failure(error); next = false; }
                    if (failure != null)
                    {
                        foreach (var entry in plan) Add(snapshot, section, entry, "root", "", "root-read-failed",
                            RecProjectionValue.Error("unknown", failure), failure, frame());
                        break;
                    }
                    if (!next) break;
                    count++;
                    foreach (var entry in plan)
                    {
                        var identity = new List<string>();
                        if (rootIdentity != null)
                        {
                            try { identity.Add(rootIdentity(entry, root!)); }
                            catch (Exception error) { failure = Failure(error); }
                            if (failure != null)
                            {
                                Add(snapshot, section, entry, "root", "", "root-read-failed",
                                    RecProjectionValue.Error("unknown", failure), failure, frame());
                                break;
                            }
                        }
                        foreach (var path in entry.Identity)
                            foreach (var step in Walk(snapshot, section, entry, root, path.Split('.'), 0, path,
                                "identity", cast, frame, identity, unavailable)) yield return step;
                        var rootId = entry.Source == "static" ? "static=" + entry.Type : string.Join(";", identity);
                        foreach (var path in entry.Paths)
                            foreach (var step in Walk(snapshot, section, entry, root, path.Split('.'), 0, path,
                                rootId, cast, frame, null, unavailable)) yield return step;
                    }
                }
            }
            finally
            {
                try { cursor.Dispose(); }
                catch (Exception error)
                {
                    failure = Failure(error);
                    foreach (var entry in plan) Add(snapshot, section, entry, "root", "", "root-dispose-failed",
                        RecProjectionValue.Error("collection", failure), failure, frame());
                }
            }
            foreach (var entry in plan) Add(snapshot, section, entry, "root-count", "",
                failure == null ? "root-count" : "collection-incomplete",
                failure == null ? RecProjectionValue.Count(count) : RecProjectionValue.Error("collection", failure), failure, frame());
        }
    }

    private static IEnumerable<int> Walk(CaptureSnapshot snapshot, CaptureSection section, Entry entry, object? value,
        string[] segments, int at, string path, string rootId, Func<object, string, object?> cast, Func<long> frame,
        List<string>? identity, Func<object, string?>? unavailable, Type? declaredType = null)
    {
        yield return 0;
        if (value == null)
        {
            var terminal = at == segments.Length;
            var status = terminal ? "explicit-null" : "null-before-leaf";
            var frozen = terminal ? RecProjectionValue.Null(ValueType(declaredType))
                : RecProjectionValue.Error(ValueType(declaredType), "null-before-leaf");
            Add(snapshot, section, entry, path, rootId, status, frozen, terminal ? null : "null-before-leaf", frame());
            identity?.Add(path + "=unavailable");
            yield break;
        }
        if (unavailable?.Invoke(value) is { } unavailableReason)
        {
            Add(snapshot, section, entry, path, rootId, "native-unavailable",
                RecProjectionValue.Error(ValueType(declaredType ?? value.GetType()), unavailableReason), unavailableReason, frame());
            identity?.Add(path + "=unavailable");
            yield break;
        }
        if (at == segments.Length)
        {
            var scalar = Scalar(value);
            Add(snapshot, section, entry, path, rootId, scalar.Status == "value" ? "value"
                    : scalar.Status == "truncated" ? "truncated" : "unsupported-leaf",
                scalar, scalar.Status == "value" ? null : scalar.Reason, frame());
            identity?.Add(path + "=" + (scalar.Status == "value" ? ScalarIdentity(scalar) : "unavailable"));
            yield break;
        }

        var segment = segments[at];
        if (segment.StartsWith('@'))
        {
            object? converted = null;
            string? error = null;
            try { converted = cast(value, segment[1..]); }
            catch (Exception exception) { error = Failure(exception); }
            if (error != null)
                Add(snapshot, section, entry, path, rootId, "cast-failed", RecProjectionValue.Error("unknown", error), error, frame());
            else if (converted == null)
                Add(snapshot, section, entry, path, rootId, "type-not-applicable", RecProjectionValue.Unsupported("unknown", "type-not-applicable"), null, frame());
            else
                foreach (var step in Walk(snapshot, section, entry, converted, segments, at + 1, path, rootId,
                    cast, frame, identity, unavailable)) yield return step;
            yield break;
        }

        var each = segment.EndsWith("[]", StringComparison.Ordinal);
        var memberName = each ? segment[..^2] : segment;
        object? selected = null;
        Type? memberType = null;
        string? failure = null;
        try
        {
            var staticType = value as Type;
            var flags = (staticType == null ? BindingFlags.Instance : BindingFlags.Static)
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var type = staticType ?? value.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            { memberType = property.PropertyType; selected = property.GetValue(staticType == null ? value : null); }
            else if (type.GetField(memberName, flags) is { } field)
            { memberType = field.FieldType; selected = field.GetValue(staticType == null ? value : null); }
            else failure = "Missing reviewed accessor: " + type.FullName + "." + memberName;
        }
        catch (Exception error) { failure = Failure(error); }
        if (failure != null)
        {
            Add(snapshot, section, entry, path, rootId, "getter-error", RecProjectionValue.Error("unknown", failure), failure, frame());
            yield break;
        }
        if (!each || selected == null)
        {
            foreach (var step in Walk(snapshot, section, entry, selected, segments, at + 1, path, rootId,
                cast, frame, identity, unavailable, memberType)) yield return step;
            yield break;
        }

        IEnumerator? enumeration = null;
        PropertyInfo? indexer = null, size = null;
        object? nativeEnumeration = null;
        MethodInfo? moveNext = null;
        PropertyInfo? current = null;
        var length = 0;
        try
        {
            if (unavailable?.Invoke(selected) is { } reason) throw new InvalidOperationException(reason);
            var type = selected.GetType();
            size = type.GetProperty("Count") ?? type.GetProperty("Length");
            indexer = type.GetProperty("Item", new[] { typeof(int) });
            if (size != null && indexer != null) length = Convert.ToInt32(size.GetValue(selected), CultureInfo.InvariantCulture);
            else if (selected is IEnumerable values) enumeration = values.GetEnumerator();
            else
            {
                nativeEnumeration = type.GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(selected, null);
                var iteratorType = nativeEnumeration?.GetType();
                moveNext = iteratorType?.GetMethod("MoveNext", Type.EmptyTypes);
                current = iteratorType?.GetProperty("Current");
                if (moveNext == null || current == null) throw new InvalidOperationException("No reviewed collection accessors: " + type.FullName);
            }
            if (length < 0) throw new InvalidOperationException("Negative collection count");
        }
        catch (Exception error) { failure = Failure(error); }
        if (failure != null)
        {
            Add(snapshot, section, entry, path, rootId, "collection-error", RecProjectionValue.Error("collection", failure), failure, frame());
            yield break;
        }

        var observed = 0;
        try
        {
            while (true)
            {
                yield return 0;
                object? item = null;
                bool next;
                try
                {
                    if (unavailable?.Invoke(selected) is { } reason) throw new InvalidOperationException(reason);
                    if (indexer != null) { next = observed < length; if (next) item = indexer.GetValue(selected, new object[] { observed }); }
                    else if (enumeration != null) { next = enumeration.MoveNext(); if (next) item = enumeration.Current; }
                    else { next = (bool)moveNext!.Invoke(nativeEnumeration, null)!; if (next) item = current!.GetValue(nativeEnumeration); }
                }
                catch (Exception error) { failure = Failure(error); next = false; }
                if (failure != null)
                {
                    Add(snapshot, section, entry, path, rootId, "collection-error", RecProjectionValue.Error("collection", failure), failure, frame());
                    break;
                }
                if (!next)
                {
                    try
                    {
                        if (indexer != null && Convert.ToInt32(size!.GetValue(selected), CultureInfo.InvariantCulture) != length)
                            failure = "Collection size changed during acquisition";
                    }
                    catch (Exception error) { failure = Failure(error); }
                    break;
                }
                var address = rootId + "/" + memberName + "[" + observed.ToString(CultureInfo.InvariantCulture) + "]";
                observed++;
                foreach (var step in Walk(snapshot, section, entry, item, segments, at + 1, path, address,
                    cast, frame, identity, unavailable)) yield return step;
            }
        }
        finally
        {
            if (enumeration is IDisposable disposable)
                try { disposable.Dispose(); }
                catch (Exception error)
                {
                    failure = Failure(error);
                    Add(snapshot, section, entry, path, rootId, "collection-dispose-failed",
                        RecProjectionValue.Error("collection", failure), failure, frame());
                }
            if (nativeEnumeration != null)
                try { nativeEnumeration.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(nativeEnumeration, null); }
                catch (Exception error)
                {
                    failure = Failure(error);
                    Add(snapshot, section, entry, path, rootId, "collection-dispose-failed",
                        RecProjectionValue.Error("collection", failure), failure, frame());
                }
        }
        Add(snapshot, section, entry, path, rootId, failure == null ? "collection-complete" : "collection-incomplete",
            failure == null ? RecProjectionValue.Count(observed) : RecProjectionValue.Error("collection", failure), failure, frame());
    }

    internal static RecProjectionValue Scalar(object value)
    {
        var type = value.GetType();
        if (value is IntPtr pointer) return RecProjectionValue.Reference(pointer.ToInt64().ToString(CultureInfo.InvariantCulture));
        if (type.IsEnum) return new("enum", "value", JsonSerializer.SerializeToElement(new
        { name = value.ToString(), number = Convert.ToInt64(value, CultureInfo.InvariantCulture) }), "");
        if (value is float single && !float.IsFinite(single) || value is double real && !double.IsFinite(real))
            return RecProjectionValue.Unsupported("number", "non-finite");
        if (value is bool boolean) return RecProjectionValue.Boolean(boolean);
        // A single getter cannot bypass the capture output budget with an arbitrarily large managed string.
        if (value is string text) return text.Length <= 4096 ? RecProjectionValue.Text(text)
            : RecProjectionValue.Truncated("string", "scalar-output-limit");
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return new("number", "value", JsonSerializer.SerializeToElement(value, type), "");
        return RecProjectionValue.Unsupported(ValueType(type), "unsupported-leaf:" + type.FullName);
    }

    /// <summary>A path read is one lease-local microstep: no native root survives the return.</summary>
    internal static PathRead ReadScalarPath(object root, string path, Func<object, string, object?> cast,
        Func<object, string?>? unavailable = null, Action? beforeRead = null)
    {
        object? value = root;
        Type? declared = root as Type ?? root.GetType();
        foreach (var segment in path.Split('.'))
        {
            beforeRead?.Invoke();
            if (value == null)
                return new("null-before-leaf", RecProjectionValue.Error(ValueType(declared), "null-before-leaf"), "null-before-leaf", declared?.FullName);
            if (unavailable?.Invoke(value) is { } unavailableReason)
                return new("native-unavailable", RecProjectionValue.Error(ValueType(declared), unavailableReason), unavailableReason, declared?.FullName);
            if (segment.EndsWith("[]", StringComparison.Ordinal))
                return new("unsupported-leaf", RecProjectionValue.Unsupported("collection", "binding-collection-not-admitted"),
                    "binding-collection-not-admitted", declared?.FullName);
            if (segment.StartsWith('@'))
            {
                try { value = cast(value, segment[1..]); declared = value?.GetType(); }
                catch (RecObservationTerminated) { throw; }
                catch (TargetInvocationException error) when (error.InnerException is RecObservationTerminated)
                { throw error.InnerException; }
                catch (Exception error)
                {
                    var failure = Failure(error);
                    return new("cast-failed", RecProjectionValue.Error("unknown", failure), failure, declared?.FullName);
                }
                if (value == null) return new("type-not-applicable", RecProjectionValue.Unsupported("unknown", "type-not-applicable"), null, declared?.FullName);
                continue;
            }
            try
            {
                var staticType = value as Type;
                var flags = (staticType == null ? BindingFlags.Instance : BindingFlags.Static)
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                var type = staticType ?? value.GetType();
                var property = type.GetProperty(segment, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                { declared = property.PropertyType; value = property.GetValue(staticType == null ? value : null); }
                else if (type.GetField(segment, flags) is { } field)
                { declared = field.FieldType; value = field.GetValue(staticType == null ? value : null); }
                else throw new MissingMemberException(type.FullName, segment);
            }
            catch (RecObservationTerminated) { throw; }
            catch (TargetInvocationException error) when (error.InnerException is RecObservationTerminated)
            { throw error.InnerException; }
            catch (Exception error)
            {
                var failure = Failure(error);
                return new("getter-error", RecProjectionValue.Error(ValueType(declared), failure), failure, declared?.FullName);
            }
        }
        if (value == null) return new("explicit-null", RecProjectionValue.Null(ValueType(declared)), null, declared?.FullName);
        if (unavailable?.Invoke(value) is { } reason)
            return new("native-unavailable", RecProjectionValue.Error(ValueType(declared), reason), reason, declared?.FullName);
        var scalar = Scalar(value);
        return scalar.Status == "value" ? new("value", scalar, null, declared?.FullName)
            : scalar.Status == "truncated" ? new("truncated", scalar, scalar.Reason, declared?.FullName)
            : new("unsupported-leaf", scalar, scalar.Reason, declared?.FullName);
    }

    internal static string Failure(Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        return error.GetType().FullName + ": " + error.Message;
    }

    internal static string ValueType(Type? type)
    {
        type = Nullable.GetUnderlyingType(type ?? typeof(object)) ?? type;
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(string)) return "string";
        if (type == typeof(IntPtr)) return "reference";
        if (type?.IsEnum == true) return "enum";
        if (type != null && (type.IsPrimitive || type == typeof(decimal))) return "number";
        return type == null || type == typeof(object) ? "unknown" : "reference";
    }

    private static string ScalarIdentity(RecProjectionValue value)
        => value.Data.ValueKind == JsonValueKind.String ? value.Data.GetString() ?? "" : value.Data.GetRawText();

    internal static void Add(CaptureSnapshot snapshot, CaptureSection section, Entry entry, string path, string rootId,
        string status, RecProjectionValue value, string? error, long frame)
    {
        if (status is not ("value" or "explicit-null" or "collection-complete" or "root-count" or "type-not-applicable"))
        {
            snapshot.AcquisitionComplete = false;
            snapshot.FieldCompleteness = "incomplete";
            if (status.StartsWith("collection", StringComparison.Ordinal) || status.StartsWith("root", StringComparison.Ordinal))
                snapshot.CoverageCompleteness = "incomplete";
        }
        var row = new CaptureRow("nativeReadback", entry.Id + "/" + rootId + "/" + path, entry.Type);
        row.Field("readbackId", entry.Id).Field("source", entry.Source).Field("root", rootId).Field("path", path)
            .Field("status", status).Field("error", error).Field("observedFrame", frame.ToString(CultureInfo.InvariantCulture))
            .Value("value", value);
        section.Rows.Add(row);
    }
}
