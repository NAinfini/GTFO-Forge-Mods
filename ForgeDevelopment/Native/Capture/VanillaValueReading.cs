using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeDevelopment.Native;

// Shared by the native reader and its offline record-format test. An absent value has no rawValue property.
internal static class VanillaValueReading
{
    internal const string Format = "forge.vanilla-value.reading.v1";
    internal sealed record ReadResult(string Outcome, object? Value, string? NullTarget = null, string? Error = null, object? SelectedBlock = null)
    {
        internal bool Available => Outcome is "value" or "explicit-null";
        internal object Evidence => new { status = Available ? "success" : Outcome, outcome = Outcome, nullTarget = NullTarget, error = Error };
    }

    // Diagnostic RecReflect.ReadPath intentionally conflates a failed getter with null. Baseline evidence must not.
    // A path is split once; the read runs for every field of every traced call, so it allocates nothing on the success path.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> Segments = new(StringComparer.Ordinal);
    private static string Walked(string[] names, int count) => string.Join(".", names, 0, count);

    internal static ReadResult ReadMember(object root, string path)
    {
        object? current = root;
        var names = Segments.GetOrAdd(path, static text => text.Split('.'));
        try
        {
            for (var at = 0; at < names.Length; at++)
            {
                var name = names[at];
                if (RecReflect.Probe.NativeReadUnavailable(current!) is { } reason)
                    return new("native-read-skipped", null, Error: reason + ": " + Walked(names, at + 1));
                var getter = RecReflect.ResolveManagedMember(current as Type ?? current!.GetType(), name, current is Type);
                var directAccessor = getter != null;
                if (current is not Type)
                    getter ??= RecReflect.Probe.ResolveNativeMember(RecReflect.Probe.NativeClass(current), name);
                if (getter == null) return new("missing-member", null, Error: Walked(names, at + 1));
                current = getter(current);
                // The generic native probe exposes no successful-null flag. Only the generated member getter can prove null.
                if (current == null) return directAccessor ? new("explicit-null", null, Walked(names, at + 1))
                    : new("field-unavailable", null, Error: "Native probe returned no value for " + Walked(names, at + 1));
            }
            return new("value", current);
        }
        catch (Exception error) { return Failed(error); }
    }
    internal static ReadResult Failed(Exception error)
    {
        if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        return new("getter-error", null, Error: error.GetType().FullName + ": " + error.Message);
    }

    internal static string InvocationKey(string context, long? callId)
        => callId.HasValue ? context + "/call/" + callId.Value : context;

    internal static bool ModelInitialized(string? modelFile, int? modelInstance)
        => !string.IsNullOrWhiteSpace(modelFile) && modelInstance.HasValue && modelInstance.Value != 0;

    internal static bool ModelPhaseReached(string? nativeMethod, string phase, bool initialized)
        => nativeMethod == "Enemies.EnemyPrefabManager.BuildEnemyPrefab"
            && phase == "after-native-before-forge" && initialized;

    internal static string ModelState(bool reached)
        => reached ? "model-initialized/native-build" : "phase-not-reached/native-build";

    internal static bool CostAllocationObserved(bool runOriginal, bool sameLease, int modeBefore, int modeAfter,
        float before, float after, int nativeReads, float? selectedCost)
    {
        if (!runOriginal || !sameLease || modeBefore != 1 || modeAfter != 1 || !float.IsFinite(before) || before > 0
            || !float.IsFinite(after) || after < 0) return false;
        // The reviewed native ModeChange branch writes the cap share. Its real field transition is sufficient
        // even if the cost-table read was inlined. An unchanged zero needs the additional native lookup receipt.
        if (nativeReads == 0) return after != before;
        return nativeReads == 1 && selectedCost.HasValue && float.IsFinite(selectedCost.Value)
            && selectedCost.Value >= 0 && after == selectedCost.Value;
    }

    internal sealed record SetupFailureChecks(bool Armed, bool RunOriginal, bool Completed, bool Initialized,
        bool NativeNullStack, bool ParserUnchanged, bool PatchOrderStable, bool LeaseCurrent, bool RuntimeReviewed,
        bool EntryQualified, bool FinalQualified, bool BuildMatches)
    {
        internal bool Qualified => Armed && RunOriginal && !Completed && !Initialized && NativeNullStack && ParserUnchanged
            && PatchOrderStable && LeaseCurrent && RuntimeReviewed && EntryQualified && FinalQualified && BuildMatches;
    }

    // Only the IL2CPP payload is inspected, never a managed StackTrace that merely names the patched wrapper.
    internal static string? NativeSetupStack(string message)
    {
        const string begin = "--- BEGIN IL2CPP STACK TRACE ---", end = "--- END IL2CPP STACK TRACE ---";
        var start = message.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0 || message.IndexOf(begin, start + begin.Length, StringComparison.Ordinal) >= 0
            || message.Contains("--->", StringComparison.Ordinal) || message.Contains("inner exception", StringComparison.OrdinalIgnoreCase)) return null;
        start += begin.Length;
        var stop = message.IndexOf(end, start, StringComparison.Ordinal);
        if (stop < 0 || message.IndexOf(end, StringComparison.Ordinal) != stop
            || message.IndexOf(end, stop + end.Length, StringComparison.Ordinal) >= 0) return null;
        var stack = message[start..stop].Trim();
        if (!stack.StartsWith("NullReferenceException:", StringComparison.Ordinal)
            && !stack.StartsWith("System.NullReferenceException:", StringComparison.Ordinal)) return null;
        var first = stack.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("at ", StringComparison.Ordinal));
        const string signature = @"^at Dam_EnemyDamageBase\.Setup ?\(Enemies\.EnemyAgent(?: [A-Za-z_]\w*)?,\s*System\.Single(?: [A-Za-z_]\w*)?,\s*System\.Single(?: [A-Za-z_]\w*)?\)(?:\s|$)";
        return first != null && Regex.IsMatch(first, signature, RegexOptions.CultureInvariant) ? stack : null;
    }
    internal static string Status(string phase, bool beforeForge, bool reached, bool available)
        => phase == "original-skipped" ? "original-skipped" : !reached ? "phase-not-reached"
            : !beforeForge ? "override-order-unverified" : !available ? "field-unavailable" : "observed";

    internal static void Identity(Utf8JsonWriter json, string request, string component, string field,
        string buildId, int revision, string build, string source, string? instance, string type, uint? id,
        string state, string phase, string status, bool qualified, bool beforeForge, string format = Format)
    {
        json.WriteString("format", format); json.WriteBoolean("gameExecuted", true);
        json.WriteString("request", request); json.WriteString("component", component); json.WriteString("field", field);
        json.WriteString("buildId", buildId); json.WriteNumber("gameRevision", revision); json.WriteString("gameBuild", build);
        json.WriteString("sourceSha256", source); json.WriteString("instance", instance); json.WriteString("dataBlockType", type);
        if (id.HasValue) json.WriteNumber("dataBlockId", id.Value); else json.WriteNull("dataBlockId");
        json.WriteString("state", state); json.WriteString("phase", phase); json.WriteString("status", status);
        json.WriteBoolean("phaseQualified", qualified); json.WriteBoolean("beforeForge", beforeForge);
    }
}
