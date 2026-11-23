using System;
using ForgeCore;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeMap;

internal sealed record WaveNativeMember(ushort SettingsId, ushort PopulationId);
/// <summary>`random` is one native wave run by its own settings; `exact` is one native single-kind member per roster row.</summary>
internal sealed record WaveComposition(string Kind);
/// <summary>What starts a list entry that an earlier entry of the same host starts: that entry's own started / exhausted / cleared
/// fact, then <c>Seconds</c> later. Entries with no anchor start with the host.</summary>
internal sealed record WaveAfter(int Ordinal, string When, double Seconds);
internal sealed record WaveHostProgram(string Level, JsonElement Host, string Readiness, IReadOnlyList<WaveResource> Waves);
internal sealed record WaveResourceDocument(IReadOnlyList<WaveResource> Waves, IReadOnlyList<WaveHostProgram> Hosts);

/// <summary>Format 2 only. The field and number domains mirror readWaveResourceDocument in map-wave-program.ts.</summary>
internal static class WaveResourceDocuments
{
    public static WaveResourceDocument Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Fields(root, "$", "schemaVersion", "waves", "hosts");
            if (!FormatVersion.Matches(root))
                Fail("wave-resource-schema", "$.schemaVersion");
            var waves = new List<WaveResource>();
            var addresses = new Dictionary<string, WaveResource>(StringComparer.Ordinal);
            foreach (var row in Array(root, "waves", "$"))
            {
                Fields(row, "waves[]", "address", "level", "settingsId", "populationId", "revision", "composition", "members");
                var address = Text(row, "address", "waves[]");
                if (address.Length > 256 || address.Trim() != address || address.Any(c => c <= 31 || c == 127)
                    || !Regex.IsMatch(address, @"^forge\.wave/[^/]+/[^/]+/[0-9]+$", RegexOptions.CultureInvariant))
                    Fail("wave-resource-address", address);
                var level = Level(row, "waves[]");
                var settings = (ushort)UInt(row, "settingsId", "waves[]", ushort.MaxValue);
                var population = (ushort)UInt(row, "populationId", "waves[]", ushort.MaxValue);
                string? revision = null;
                WaveComposition? composition = null;
                IReadOnlyList<WaveNativeMember> members = System.Array.Empty<WaveNativeMember>();
                if (row.TryGetProperty("composition", out var value))
                {
                    revision = Revision(row, "revision", address);
                    composition = Composition(value, address);
                    var native = new List<WaveNativeMember>();
                    foreach (var member in Array(row, "members", address))
                    {
                        Fields(member, address + "/members", "settingsId", "populationId");
                        native.Add(new((ushort)UInt(member, "settingsId", address, ushort.MaxValue),
                            (ushort)UInt(member, "populationId", address, ushort.MaxValue)));
                    }
                    if (native.Count == 0 || native[0].SettingsId != settings || native[0].PopulationId != population)
                        Fail("wave-resource-members", address);
                    members = native.AsReadOnly();
                }
                else if (row.TryGetProperty("revision", out _) || row.TryGetProperty("members", out _))
                    Fail("wave-resource-composition", address);
                var resource = new WaveResource(address, level, settings, population, revision, composition, members);
                if (!addresses.TryAdd(address, resource)) Fail("wave-resource-duplicate", address);
                waves.Add(resource);
            }
            var hosts = new List<WaveHostProgram>();
            foreach (var program in Array(root, "hosts", "$"))
            {
                Fields(program, "hosts[]", "level", "host", "trigger", "readiness", "steps");
                var level = Level(program, "hosts[]");
                var host = Required(program, "host", "hosts[]");
                Host(host);
                var readiness = Text(program, "readiness", "hosts[]");
                if (readiness is not ("native" or "runtime-required"))
                    Fail("wave-host-readiness", readiness);
                if (Text(host, "kind", "host") == "scan")
                {
                    Index(host, "scanIndex");
                    if (readiness != "runtime-required") Fail("wave-host-scan", "host/readiness");
                    var trigger = Required(program, "trigger", "host");
                    Fields(trigger, "host/trigger", "capabilityId", "resource", "scanIndex");
                    if (Text(trigger, "capabilityId", "trigger") != Blocks.TriggerObjectiveScanStarted.Id
                        || UInt(trigger, "resource", "trigger") != UInt(host, "id", "host")
                        || Index(trigger, "scanIndex") != Index(host, "scanIndex")) Fail("wave-host-scan", "host/trigger");
                }
                else if (program.TryGetProperty("trigger", out _)) Fail("wave-host-trigger", "hosts[]/trigger");
                var steps = new List<WaveResource>();
                var afters = new Dictionary<int, WaveAfter>();
                foreach (var step in Array(program, "steps", "hosts[]"))
                {
                    Fields(step, "host/steps", "capabilityId", "wave", "after");
                    if (Text(step, "capabilityId", "step") != Blocks.ActionMapWaveStart.Id)
                        Fail("wave-host-step", "host/steps");
                    var reference = Required(step, "wave", "step");
                    Fields(reference, "step/wave", "id", "revision");
                    var id = Text(reference, "id", "step/wave");
                    var revision = Revision(reference, "revision", id);
                    if (!addresses.TryGetValue(id, out var resource) || resource.Level != level)
                        Fail("wave-host-reference", id);
                    if (resource!.Revision != revision) Fail("wave-host-revision", id);
                    if (step.TryGetProperty("after", out var anchor)) afters[steps.Count] = After(anchor, steps.Count);
                    steps.Add(resource);
                }
                if (steps.Count == 0) Fail("wave-host-steps", "hosts[]");
                var pinned = new WaveHostProgram(level, host.Clone(), readiness, steps.AsReadOnly());
                for (int position = 0; position < steps.Count; position++)
                {
                    if (steps[position].Host != null) Fail("wave-host-address-reused", steps[position].Address);
                    steps[position].Host = pinned;
                    steps[position].Position = position;
                    steps[position].After = afters.GetValueOrDefault(position);
                }
                hosts.Add(pinned);
            }
            return new(waves.AsReadOnly(), hosts.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new FormatException("wave-resource-invalid: " + error.GetType().Name, error); }
    }

    private static WaveAfter After(JsonElement value, int position)
    {
        Fields(value, "step/after", "ordinal", "when", "seconds");
        var ordinal = Index(value, "ordinal");
        var when = Text(value, "when", "step/after");
        var seconds = Number(value, "seconds", "step/after");
        if (ordinal >= position || when is not ("started" or "exhausted" or "cleared") || seconds < 0)
            Fail("wave-host-after", "step/after");
        return new(ordinal, when, seconds);
    }

    private static WaveComposition Composition(JsonElement value, string address)
    {
        Fields(value, address, "kind");
        var kind = Text(value, "kind", address);
        if (kind is not ("random" or "exact")) Fail("wave-resource-composition", address);
        return new(kind);
    }

    private static void Host(JsonElement host)
    {
        Fields(host, "host", "kind", "type", "id", "effectId", "path", "wave", "enemy", "index", "scanIndex", "alarm", "delay", "field");
        var kind = Text(host, "kind", "host");
        var type = Text(host, "type", "host");
        if (kind is not ("chained-puzzle" or "scan" or "event" or "reactor" or "scout" or "starting" or "objective")
            || !Regex.IsMatch(type, @"^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)) Fail("wave-host-invalid", "host");
        if (kind == "scout")
        {
            if (type != "Rundown" || Number(host, "id", "host") != 0) Fail("wave-host-id", "host");
        }
        else UInt(host, "id", "host");
        if ((kind is "scan" or "chained-puzzle") && type != "ChainedPuzzle"
            || (kind is "starting" or "objective" or "reactor") && type != "WardenObjective") Fail("wave-host-type", "host");
        foreach (var name in new[] { "wave", "enemy", "index", "scanIndex" }) if (host.TryGetProperty(name, out _)) Index(host, name);
        if (host.TryGetProperty("path", out var path))
        {
            if (path.ValueKind != JsonValueKind.Array) Fail("wave-host-path", "host");
            foreach (var key in path.EnumerateArray())
                if (key.ValueKind != JsonValueKind.String && (key.ValueKind != JsonValueKind.Number || !key.TryGetDouble(out var n)
                    || !double.IsFinite(n) || n < 0 || Math.Truncate(n) != n)) Fail("wave-host-path", "host");
        }
        if (host.TryGetProperty("effectId", out _) && Text(host, "effectId", "host").Length == 0) Fail("wave-host-effect", "host");
        if (host.TryGetProperty("alarm", out var alarm) && alarm.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("wave-host-alarm", "host");
        if (host.TryGetProperty("delay", out _)) Number(host, "delay", "host");
        if (host.TryGetProperty("field", out _) && Text(host, "field", "host") is not ("WavesOnActivate" or "WavesOnGotoWin")) Fail("wave-host-field", "host");
    }

    private static void Fields(JsonElement row, string path, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) Fail("wave-resource-object", path);
        if (StrictJson.FirstUnknownOrRepeated(row, names) is { } bad) Fail("wave-resource-field", path + "/" + bad);
    }
    private static JsonElement Required(JsonElement row, string key, string path)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var value)) Fail("wave-resource-required", path + "/" + key);
        return row.GetProperty(key);
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement row, string key, string path)
    {
        var value = Required(row, key, path);
        if (value.ValueKind != JsonValueKind.Array) Fail("wave-resource-array", path + "/" + key);
        return value.EnumerateArray();
    }
    private static string Text(JsonElement row, string key, string path)
    {
        var value = Required(row, key, path);
        if (value.ValueKind != JsonValueKind.String) Fail("wave-resource-string", path + "/" + key);
        return value.GetString()!;
    }
    private static double Number(JsonElement row, string key, string path)
    {
        var value = Required(row, key, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            Fail("wave-resource-number", path + "/" + key);
        return value.GetDouble();
    }
    private static uint UInt(JsonElement row, string key, string path, uint max = uint.MaxValue)
    {
        var number = Number(row, key, path);
        if (number < 1 || number > max || Math.Truncate(number) != number) Fail("wave-resource-id", path + "/" + key);
        return (uint)number;
    }
    private static int Index(JsonElement row, string key)
    {
        var number = Number(row, key, "host");
        if (number < 0 || number > int.MaxValue || Math.Truncate(number) != number) Fail("wave-host-index", "host/" + key);
        return (int)number;
    }
    private static string Level(JsonElement row, string path)
    {
        var value = Text(row, "level", path);
        if (!Regex.IsMatch(value, @"^[0-9]+:[A-E]:[0-9]+$", RegexOptions.CultureInvariant)) Fail("wave-resource-level", path);
        return value;
    }
    private static string Revision(JsonElement row, string key, string path)
    {
        var value = Text(row, key, path);
        if (!Regex.IsMatch(value, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)) Fail("wave-resource-revision", path);
        return value;
    }
    private static void Fail(string code, string path) => throw new FormatException(code + ": " + path);
}
