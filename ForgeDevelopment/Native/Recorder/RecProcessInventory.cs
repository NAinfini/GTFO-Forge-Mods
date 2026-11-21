using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>
/// What this process loaded and patched, as the session sees it: the chainloader's plugins in load order and every
/// Harmony patch installed in the process with its owners. The patch list is
/// written when the chainloader finishes and again when a level has been built, because a package may patch lazily;
/// an unchanged list is not written twice. Assembly paths are written as file names: a full path carries the user's
/// profile folder.
/// </summary>
internal static class RecProcessInventory
{
    private const int MaxErrors = 32;
    private static readonly object Gate = new();
    private static IL2CPPChainloader? _chainloader;

    private static int _loadIndex;
    private static string _lastPatchFingerprint = "";

    /// <summary>Writes the plugin table as it stands when the recorder starts and subscribes to the loads that
    /// follow. The plugins before this one are only in the table; the ones after it are recorded as they load, which
    /// is the order the chainloader actually ran them in.</summary>
    internal static void Start()
    {
        var chainloader = IL2CPPChainloader.Instance ?? throw new InvalidOperationException("The BepInEx IL2CPP chainloader is unavailable.");
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            _chainloader = chainloader;
            _loadIndex = 0;
            _lastPatchFingerprint = "";
        }
        WritePluginOrder("recorder-start", chainloader);
        chainloader.PluginLoaded += PluginLoaded;
        chainloader.Finished += Finished;
    }

    internal static void Stop()
    {
        IL2CPPChainloader? chainloader;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            chainloader = _chainloader;
            _chainloader = null;
        }
        if (chainloader == null) return;
        chainloader.PluginLoaded -= PluginLoaded;
        chainloader.Finished -= Finished;
    }

    /// <summary>Writes the process's Harmony patch list when it differs from the last one written. It visits every
    /// patched method in the process, so it runs at the chainloader's end and after a level build, never per frame.
    /// A list larger than half a record is split into parts, each naming its index and the part count.</summary>
    internal static void Refresh(string phase)
    {
        if (!RecSession.Active) return;
        try
        {
            var rows = new List<PatchRow>();
            var owners = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                rows.Add(new PatchRow(MethodName(method), info.Owners.OrderBy(owner => owner, StringComparer.Ordinal).ToArray(),
                    info.Prefixes.Count, info.Postfixes.Count, info.Transpilers.Count, info.Finalizers.Count, info.ILManipulators.Count));
                CountOwners(owners, info.Prefixes);
                CountOwners(owners, info.Postfixes);
                CountOwners(owners, info.Transpilers);
                CountOwners(owners, info.Finalizers);
                CountOwners(owners, info.ILManipulators);
            }
            rows.Sort((left, right) => string.CompareOrdinal(left.Method, right.Method));
            var fingerprint = Fingerprint(rows);
            using (RecGate.Enter(Gate, "recorder.lock-timeout"))
            {
                if (fingerprint == _lastPatchFingerprint) return;
                _lastPatchFingerprint = fingerprint;
            }
            var parts = Split(rows, Math.Max(4096, RecSession.RecordBudget / 2));
            RecSession.Note("harmony_patch_summary", json =>
            {
                json.WriteString("phase", phase);
                json.WriteString("fingerprint", fingerprint);
                json.WriteNumber("patchedMethods", rows.Count);
                json.WriteNumber("parts", parts.Count);
                json.WritePropertyName("owners");
                json.WriteStartObject();
                foreach (var owner in owners) json.WriteNumber(owner.Key, owner.Value);
                json.WriteEndObject();
            });
            for (var part = 0; part != parts.Count; part++)
            {
                var index = part;
                RecSession.Note("harmony_patches", json =>
                {
                    json.WriteString("phase", phase);
                    json.WriteString("fingerprint", fingerprint);
                    json.WriteNumber("part", index);
                    json.WriteNumber("parts", parts.Count);
                    json.WritePropertyName("methods");
                    json.WriteStartArray();
                    foreach (var row in parts[index])
                    {
                        json.WriteStartObject();
                        json.WriteString("method", row.Method);
                        WriteStrings(json, "owners", row.Owners);
                        json.WriteNumber("prefixes", row.Prefixes);
                        json.WriteNumber("postfixes", row.Postfixes);
                        json.WriteNumber("transpilers", row.Transpilers);
                        json.WriteNumber("finalizers", row.Finalizers);
                        json.WriteNumber("ilManipulators", row.IlManipulators);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                });
            }
        }
        catch (Exception error) { Fail("harmony_patches:" + phase, error); }
    }

    private static void PluginLoaded(PluginInfo plugin)
    {
        try
        {
            int index;
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) index = _loadIndex++;
            RecSession.Note("plugin_loaded", json =>
            {
                json.WriteNumber("loadIndex", index);
                WritePlugin(json, plugin);
            });
        }
        catch (Exception error) { Fail("plugin_loaded", error); }
    }

    private static void Finished()
    {
        try
        {
            IL2CPPChainloader? chainloader;
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) chainloader = _chainloader;
            if (chainloader != null) WritePluginOrder("chainloader-finished", chainloader);
        }
        catch (Exception error) { Fail("plugin_order", error); }
        Refresh("chainloader-finished");
    }

    private static void WritePluginOrder(string phase, IL2CPPChainloader chainloader)
    {
        var plugins = chainloader.Plugins.Values.ToArray();
        var errors = chainloader.DependencyErrors?.ToArray() ?? Array.Empty<string>();

        RecSession.Note("plugin_order", json =>
        {
            json.WriteString("phase", phase);
            // The table's enumeration order. Whether it equals the load order is BepInEx's implementation, not a
            // documented contract; the plugin_loaded records are the observed order for every later load.
            json.WriteString("orderSource", "IL2CPPChainloader.Plugins enumeration");

            json.WritePropertyName("plugins");
            json.WriteStartArray();
            for (var i = 0; i != plugins.Length; i++)
            {
                json.WriteStartObject();
                json.WriteNumber("index", i);
                WritePlugin(json, plugins[i]);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteNumber("dependencyErrors", errors.Length);
            WriteStrings(json, "errors", errors.Take(MaxErrors).Select(error => RecReflect.Truncate(error, 300)));
            json.WriteNumber("errorsOmitted", Math.Max(0, errors.Length - MaxErrors));

        });
    }

    private static void WritePlugin(Utf8JsonWriter json, PluginInfo plugin)
    {
        json.WriteString("guid", plugin.Metadata?.GUID ?? "unknown");
        json.WriteString("name", plugin.Metadata?.Name ?? "unknown");
        json.WriteString("version", plugin.Metadata?.Version?.ToString() ?? "unknown");
        json.WriteString("assembly", string.IsNullOrEmpty(plugin.Location) ? "unknown" : Path.GetFileName(plugin.Location));
        json.WriteBoolean("instantiated", plugin.Instance != null);
    }


    private static string MethodName(MethodBase method)
    {
        string parameters;
        try { parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name)); }
        catch (Exception) { parameters = "?"; } // Il2Cpp read guard: the parameter list is a label in an inventory row, and "?" is its explicit unknown.
        return (method.DeclaringType?.FullName ?? "unknown") + "::" + method.Name + "(" + parameters + ")";
    }

    private static void CountOwners(SortedDictionary<string, int> owners, IEnumerable<Patch> patches)
    {
        foreach (var patch in patches)
        {
            var owner = string.IsNullOrWhiteSpace(patch.owner) ? "unknown" : patch.owner;
            owners.TryGetValue(owner, out var count);
            owners[owner] = count + 1;
        }
    }

    /// <summary>Splits the rows so each part stays under the byte budget. The estimate is the UTF-8 text of the row
    /// plus a fixed allowance for the property names and numbers; a single row is never split.</summary>
    private static List<List<PatchRow>> Split(List<PatchRow> rows, int budget)
    {
        var parts = new List<List<PatchRow>> { new() };
        var used = 0;
        foreach (var row in rows)
        {
            var size = 160 + Encoding.UTF8.GetByteCount(row.Method);
            foreach (var owner in row.Owners) size += 8 + Encoding.UTF8.GetByteCount(owner);
            if (used != 0 && used + size > budget)
            {
                parts.Add(new List<PatchRow>());
                used = 0;
            }
            parts[^1].Add(row);
            used += size;
        }
        return parts;
    }

    private static string Fingerprint(List<PatchRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(row.Method).Append('|').Append(string.Join(",", row.Owners)).Append('|')
                .Append(row.Prefixes).Append(',').Append(row.Postfixes).Append(',').Append(row.Transpilers).Append(',')
                .Append(row.Finalizers).Append(',').Append(row.IlManipulators).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WritePropertyName(name);
        json.WriteStartArray();
        foreach (var value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }

    private static void Fail(string where, Exception error)
    {
        try
        {
            RecSession.Note("inventory_error", json =>
            {
                json.WriteString("where", where);
                json.WriteString("error", error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 300));
            });
        }
        catch (Exception) { } // Last resort: the inventory error note itself failed, and there is no other sink to report to.
    }

    private sealed record PatchRow(string Method, string[] Owners, int Prefixes, int Postfixes, int Transpilers, int Finalizers, int IlManipulators);
}
