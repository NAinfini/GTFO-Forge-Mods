using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>Read-only acoustic context for the existing admitted paired trace. This is neither a noise
/// simulator nor an event dispatcher. Roles and paths are explicit; spatial membership is never inferred.</summary>
internal static partial class RecAcousticObservation
{
    private const string LocationFormat = "forge.rec.acoustic-location.v1";
    private static long _epoch;
    [ThreadStatic] private static Scope? _top;

    // A location consists only of copied scalars/dictionaries, never live game references. The private type also
    // prevents an arbitrary dictionary in a game argument being mistaken for our reviewed location projection.
    private sealed class Location : Dictionary<string, object?> { }
    internal sealed class Scope
    {
        internal readonly long PatchId, Installation, CallId, Epoch;
        internal readonly int ThreadId;
        internal readonly Scope? Parent;
        internal readonly Func<bool>? IsInstallationCurrent;
        private readonly Location _source;
        internal readonly Dictionary<string, object?> BeforeFields;
        internal int Closed;
        private Scope(long patch, long installation, long call, long epoch, Scope? parent, Location source,
            Dictionary<string, object?> beforeFields, Func<bool>? current)
        {
            PatchId = patch; Installation = installation; CallId = call; Parent = parent;
            Epoch = epoch; ThreadId = Environment.CurrentManagedThreadId;
            _source = source; BeforeFields = beforeFields; IsInstallationCurrent = current;
        }
        internal static Scope Create(long patch, long installation, long call, long epoch, Scope? parent,
            Dictionary<string, object?> source, Dictionary<string, object?> beforeFields, Func<bool>? current)
            => new(patch, installation, call, epoch, parent, (Location)source, beforeFields, current);
        internal Dictionary<string, object?> SourceCopy() => CopyLocation(_source);
    }

    /// <summary>Call only after the existing gate admits a paired invocation and assigns its call ID.
    /// isCurrent should check the exact patch installation; Reset invalidates all threads on teardown.</summary>
    internal static Scope Enter(Config config, object? instance, object?[]? args, long patchId, long installation,
        long callId, Func<bool>? isCurrent = null)
    {
        if (callId <= 0) throw new ArgumentOutOfRangeException(nameof(callId), "Acoustic context requires an existing paired call ID.");
        var epoch = Volatile.Read(ref _epoch);
        var parent = Current();
        var source = EndpointLocation(config.Source, instance, args, parent);
        var scope = Scope.Create(patchId, installation, callId, epoch, parent, source, Fields(config, instance, args), isCurrent);
        _top = scope;
        return scope;
    }

    internal static Dictionary<string, object?> Capture(Config config, object? instance, object?[]? args,
        Scope? scope, string phase = "before")
    {
        var current = Valid(scope);
        var source = EndpointLocation(config.Source, instance, args, current ? scope!.Parent : null);
        var receiver = EndpointLocation(config.Receiver, instance, args, current ? scope!.Parent : null);
        var fields = Fields(config, instance, args);
        var distance = Distance(source, receiver);
        current = Valid(scope);
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (phase != "before")
        {
            foreach (var pair in config.Fields)
            {
                var leaf = pair.Value.Split('.').Last();
                if (leaf != "m_currentStateName" && !leaf.Contains("propagated", StringComparison.OrdinalIgnoreCase)) continue;
                var after = (Dictionary<string, object?>)fields[pair.Key]!;
                var before = current && scope!.BeforeFields.TryGetValue(pair.Key, out var observed)
                    ? (Dictionary<string, object?>)observed! : Unknown("before-observation-unavailable");
                changes[pair.Key] = ObservedValue(before, out var a) && ObservedValue(after, out var b)
                    ? new Dictionary<string, object?> { ["status"] = "observed", ["changed"] = !Equals(a, b), ["before"] = a, ["after"] = b }
                    : Unknown("field-transition-unavailable");
            }
        }
        return new Dictionary<string, object?>
        {
            ["layer"] = config.Layer,
            ["source"] = source,
            ["receiver"] = receiver,
            ["observationComplete"] = current && CompleteLocation(source) && CompleteLocation(receiver)
                && IsObserved(distance) && fields.Values.All(value => IsObserved((Dictionary<string, object?>)value!))
                && changes.Values.All(value => IsObserved((Dictionary<string, object?>)value!)),
            ["distance"] = distance,
            ["fields"] = fields,
            ["fieldChanges"] = changes,
            ["association"] = current ? Link(scope!) : Unknown("no-active-same-thread-paired-scope"),
            ["semantics"] = "read-only-observation; field changes do not prove successful wakeup"
        };
    }

    // Complete means both configured roles and all requested native spatial references/scalars were read.
    // An omitted endpoint is unknown, never N/A. A root invocation needs no observed parent; no claim about
    // untraced/asynchronous causality or successful gameplay is included in this snapshot-completeness flag.
    private static bool CompleteLocation(Location location)
    {
        if (!IsObserved((Dictionary<string, object?>)location["role"]!)
            || !IsObserved((Dictionary<string, object?>)location["identity"]!)
            || !IsObserved((Dictionary<string, object?>)location["position"]!)) return false;
        foreach (var key in new[] { "node", "area", "cluster" })
        {
            var membership = (Dictionary<string, object?>)location[key]!;
            if (!membership.TryGetValue("identity", out var identity) || !membership.TryGetValue("id", out var id)
                || !IsObserved((Dictionary<string, object?>)identity!) || !KnownId((Dictionary<string, object?>)id!)) return false;
        }
        return true;
    }
    private static bool KnownId(Dictionary<string, object?> value)
        => value.TryGetValue("status", out var status) && Equals(status, "observed")
            && value.TryGetValue("value", out var id) && id != null;
    private static bool IsObserved(Dictionary<string, object?> value)
        => value.TryGetValue("status", out var status) && status is "observed" or "explicit-null" or "configured";

    internal static void Exit(Scope? scope)
    {
        if (scope == null || Interlocked.Exchange(ref scope.Closed, 1) != 0) return;
        if (scope.ThreadId != Environment.CurrentManagedThreadId || scope.Epoch != Volatile.Read(ref _epoch)) return;
        // A late old finalizer must never pop a newly opened call. Out-of-order completion invalidates only
        // the abandoned descendant chain; it cannot leave a child pointing at a disposed source.
        var cursor = _top;
        while (cursor != null && !ReferenceEquals(cursor, scope)) cursor = cursor.Parent;
        if (cursor == null) return;
        for (cursor = _top; cursor != null && !ReferenceEquals(cursor, scope); cursor = cursor.Parent)
            Interlocked.Exchange(ref cursor.Closed, 1);
        _top = scope.Parent;
        Current();
    }
    internal static void Reset() { Interlocked.Increment(ref _epoch); _top = null; }
    private static bool Valid(Scope? scope)
    {
        if (scope == null || Volatile.Read(ref scope.Closed) != 0 || scope.ThreadId != Environment.CurrentManagedThreadId
            || scope.Epoch != Volatile.Read(ref _epoch)) return false;
        try { return scope.IsInstallationCurrent?.Invoke() ?? true; }
        catch (Exception) { return false; } // A scope whose installation check throws is not current; it is dropped as invalid.
    }
    private static Scope? Current()
    {
        while (_top != null && !Valid(_top)) _top = _top.Parent;
        return _top;
    }
    private static Dictionary<string, object?> Link(Scope scope)
    {
        var parent = Valid(scope.Parent) ? scope.Parent : null;
        return new Dictionary<string, object?>
        {
            ["status"] = "observed",
            ["kind"] = "same-thread-admitted-pair",
            ["patch"] = scope.PatchId,
            ["installation"] = scope.Installation,
            ["callId"] = scope.CallId,
            ["parent"] = parent == null ? Unknown("no-active-observed-ancestor") : new Dictionary<string, object?>
            { ["patch"] = parent.PatchId, ["installation"] = parent.Installation, ["callId"] = parent.CallId },
            ["coverage"] = "observed-ancestor-only; untraced or asynchronous causality unknown"
        };
    }

    internal static Dictionary<string, object?> ObserveLocation(object? actor, object? node = null, object? position = null)
        => LocationFrom(ReadOf(actor, "actor-unavailable"), node == null ? null : ReadOf(node, "node-unavailable"),
            position == null ? null : ReadOf(position, "position-unavailable"), null);

    private static Location EndpointLocation(Endpoint? endpoint, object? instance, object?[]? args, Scope? parent)
    {
        if (endpoint == null) return MissingLocation("endpoint-unconfigured");
        Location location;
        if (endpoint.From == "parent.source")
            location = Valid(parent) ? (Location)parent!.SourceCopy() : MissingLocation("no-active-same-thread-source");
        else if (endpoint.From != null)
        {
            var read = Select(instance, args, endpoint.From);
            location = read.Value is Location source ? CopyLocation(source)
                : MissingLocation(read.Available && read.Value != null ? "not-an-acoustic-location-projection" : Reason(read));
        }
        else
            location = LocationFrom(endpoint.Actor == null ? new("actor-unconfigured", null) : Select(instance, args, endpoint.Actor),
                endpoint.Node == null ? null : Select(instance, args, endpoint.Node),
                endpoint.Position == null ? null : Select(instance, args, endpoint.Position), endpoint.Role);
        if (endpoint.Role != null) location["role"] = Role(endpoint.Role);
        return location;
    }

    private static Location LocationFrom(VanillaValueReading.ReadResult actor, VanillaValueReading.ReadResult? node,
        VanillaValueReading.ReadResult? position, string? role)
    {
        node ??= ReadPath(actor, "CourseNode");
        position ??= ReadPath(actor, "Position");
        var area = ReadPath(node, "m_area");
        var cluster = ReadPath(node, "m_nodeCluster");
        return new Location
        {
            ["format"] = LocationFormat,
            ["role"] = Role(role),
            ["identity"] = Identity(actor),
            ["position"] = Position(position),
            ["node"] = Membership(node, "NodeID"),
            ["area"] = Membership(area, "UID"),
            ["cluster"] = Membership(cluster, "ID")
        };
    }
    private static Location MissingLocation(string reason)
    {
        var result = new Location { ["format"] = LocationFormat };
        foreach (var key in new[] { "role", "identity", "position", "node", "area", "cluster" }) result[key] = Unknown(reason);
        return result;
    }
    private static Dictionary<string, object?> Role(string? role)
        => role == null || role == "unknown" ? Unknown("actor-role-unverified")
            : new() { ["status"] = "configured", ["value"] = role };
    private static Dictionary<string, object?> Membership(VanillaValueReading.ReadResult read, string id)
        => new() { ["identity"] = Identity(read), ["id"] = Scalar(ReadPath(read, id)), ["membership"] = "native-reference-only" };
    private static Dictionary<string, object?> Identity(VanillaValueReading.ReadResult read)
    {
        if (!read.Available || read.Value == null) return Unknown(Reason(read), read.Error);
        try
        {
            // The probe's identity contract is wrapper-pointer-only and never calls an engine instance-ID getter.
            // A readable nonzero pointer labels the reference; it is explicitly not evidence of native liveness.
            if (RecReflect.Probe.TryInstanceId(read.Value, out var pointer))
                return pointer == 0 ? Unknown("null-native-pointer") : new()
                {
                    ["status"] = "observed",
                    ["kind"] = "native-pointer",
                    ["pointer"] = "0x" + pointer.ToString("X", CultureInfo.InvariantCulture),
                    ["managedType"] = read.Value.GetType().FullName,
                    ["lifetime"] = "unverified"
                };
            return Unknown("native-identity-unavailable", read.Value.GetType().FullName);
        }
        catch (Exception error) { return Unknown("identity-read-error", error.GetType().Name); }
    }
    private static Dictionary<string, object?> Position(VanillaValueReading.ReadResult read)
    {
        if (!read.Available || read.Value == null) return Unknown(Reason(read), read.Error);
        var values = new double[3]; var names = new[] { "x", "y", "z" };
        for (var i = 0; i < names.Length; i++)
        {
            var axis = ReadPath(read, names[i]);
            if (!axis.Available || !Number(axis.Value, out values[i]))
                return Unknown(axis.Available ? "position-component-not-finite-number" : Reason(axis), names[i]);
        }
        return new() { ["status"] = "observed", ["x"] = values[0], ["y"] = values[1], ["z"] = values[2] };
    }
    private static Dictionary<string, object?> Distance(Location source, Location receiver)
    {
        var a = (Dictionary<string, object?>)source["position"]!;
        var b = (Dictionary<string, object?>)receiver["position"]!;
        if (!Equals(a["status"], "observed") || !Equals(b["status"], "observed"))
            return Unknown("source-or-receiver-position-unavailable");
        var dx = (double)a["x"]! - (double)b["x"]!;
        var dy = (double)a["y"]! - (double)b["y"]!;
        var dz = (double)a["z"]! - (double)b["z"]!;
        // Scaling avoids overflow in squaring otherwise finite world coordinates.
        var scale = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
        var value = scale == 0 ? 0 : scale * Math.Sqrt(dx / scale * (dx / scale) + dy / scale * (dy / scale) + dz / scale * (dz / scale));
        return double.IsFinite(value) ? new() { ["status"] = "observed", ["metric"] = "euclidean-3d", ["value"] = value }
            : Unknown("distance-not-finite");
    }
    private static Dictionary<string, object?> Fields(Config config, object? instance, object?[]? args)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in config.Fields) values[field.Key] = Scalar(Select(instance, args, field.Value));
        return values;
    }
    private static VanillaValueReading.ReadResult Select(object? instance, object?[]? args, string selector)
    {
        var dot = selector.IndexOf('.'); var root = dot < 0 ? selector : selector[..dot];
        object? value;
        if (root == "instance") value = instance;
        else
        {
            var index = int.Parse(root[3..], CultureInfo.InvariantCulture);
            if (args == null || index >= args.Length) return new("argument-unavailable", null, Error: root);
            value = args[index];
        }
        var read = ReadOf(value, root + "-unavailable");
        return dot < 0 ? read : ReadPath(read, selector[(dot + 1)..]);
    }
    private static VanillaValueReading.ReadResult ReadOf(object? value, string missing)
        => value == null ? new(missing, null) : new("value", value);
    private static VanillaValueReading.ReadResult ReadPath(VanillaValueReading.ReadResult root, string path)
    {
        var read = root;
        foreach (var step in path.Split('.'))
        {
            if (!read.Available) return read;
            if (read.Value == null) return new("null-before-leaf", null, Error: read.NullTarget);
            try
            {
                if (RecReflect.Probe.NativeReadUnavailable(read.Value) is { } unavailable)
                    return new("native-read-skipped", null, Error: unavailable + ": " + step);
                if (RecReflect.Probe.TryInstanceId(read.Value, out var pointer) && pointer == 0)
                    return new("null-native-pointer", null, Error: step);
                if (read.Value is Dictionary<string, object?> map)
                {
                    if (map.TryGetValue("$nativeRead", out var refused)) return new("native-read-skipped", null, Error: refused as string);
                    if (map.TryGetValue("$error", out var error)) return new("getter-error", null, Error: error as string);
                    read = map.TryGetValue(step, out var value) ? value == null ? new("explicit-null", null, step) : new("value", value)
                        : new("missing-member", null, Error: step);
                }
                else read = VanillaValueReading.ReadMember(read.Value, step);
            }
            catch (Exception error) { return VanillaValueReading.Failed(error); }
        }
        return read;
    }
    private static Dictionary<string, object?> Scalar(VanillaValueReading.ReadResult read)
    {
        if (!read.Available) return Unknown(Reason(read), read.Error);
        if (read.Value == null) return new() { ["status"] = "explicit-null", ["value"] = null, ["nullTarget"] = read.NullTarget };
        object? value = read.Value;
        try
        {
            if (RecReflect.Probe.NativeReadUnavailable(value) is { } refused) return Unknown("native-read-skipped", refused);
            if (value is Enum) value = value.ToString();
            else if (value is char character) value = character.ToString();
            else if (value is not (string or bool) && !Number(value, out _))
            {
                if (!RecReflect.Probe.TryScalar(value, out var scalar) || scalar == null
                    || scalar is not (string or bool) && !Number(scalar, out _)) return Unknown("scalar-unavailable");
                value = scalar;
            }
            if (value is string text && text.Length > 256) return Unknown("scalar-string-too-long");
            return new() { ["status"] = "observed", ["value"] = value };
        }
        catch (Exception error) { return Unknown("scalar-read-error", error.GetType().Name); }
    }
    private static bool Number(object? value, out double number)
    {
        number = 0;
        if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)) return false;
        number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number);
    }
    private static bool ObservedValue(Dictionary<string, object?> field, out object? value)
    {
        value = null;
        return (Equals(field["status"], "observed") || Equals(field["status"], "explicit-null")) && field.TryGetValue("value", out value);
    }
    private static string Reason(VanillaValueReading.ReadResult read)
        => read.Outcome == "explicit-null" ? "null-reference" : read.Outcome;
    private static Dictionary<string, object?> Unknown(string reason, string? detail = null)
        => new() { ["status"] = "unknown", ["reason"] = reason, ["detail"] = detail };
    private static Location CopyLocation(Location source)
    {
        var copy = new Location();
        foreach (var pair in source) copy[pair.Key] = CopyValue(pair.Value);
        return copy;
    }
    private static object? CopyValue(object? value)
    {
        if (value is not Dictionary<string, object?> map) return value;
        var copy = new Dictionary<string, object?>();
        foreach (var pair in map) copy[pair.Key] = CopyValue(pair.Value);
        return copy;
    }
}
