using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The replicated record behind `forge.action.presentation.light_color` for ordinary zone lights. The game has
/// no entry that carries a light's colour or intensity (its light state is the on/off flip only), so the write is
/// this package's own and, left alone, would exist on the host's light objects only. The host records the
/// colour and the brightness each (zone, category) request leaves, and the record travels the
/// Runtime's own native-state path, which is what carries state to a late joiner, a reconnecting client, a
/// checkpoint restore and a host migration. Every machine applies the record to its own lights.
///
/// An entry is a request key — the zone address plus the category the request named, which is also the key a
/// transition is scheduled under — and what a light shows is composed from every entry of its zone that names its
/// category: the newest brightness and the newest colour win (<see cref="Entry.BrightnessRevision"/>,
/// <see cref="Entry.ColorRevision"/>). Brightness is a set value relative to the light's own native intensity
/// (<see cref="LightColorFade.ZoneOwnership"/> keeps that baseline): 1.5 is one and a half times the level's own
/// strength however often it is asked for. The same composition runs on the host when it records a request and on
/// every other machine when it restores one, so an "every category" entry and a category entry on one zone give the
/// same light on all of them, and applying an entry twice, or after a checkpoint restore, changes nothing.
///
/// A request with a transition carries the seconds it still has to run, and each machine fades its own lights
/// toward the composed value over what is left: a late joiner or a restore shows the transition at its present
/// progress, and the host's own fade is the same one.
///
/// The global ambient light has its own state source (`forge.map.global-light`) and is not recorded here.
/// </summary>
internal sealed class ZoneLightColorLedger : IDisposable
{
    internal const string StateKey = "forge.map.zone-light-color";
    internal const int MaximumEntries = 512;
    internal const string PendingCode = "light-color-ledger-zone-unresolved";
    internal const string BrightnessOverflowCode = "light-color-ledger-brightness-overflow";

    /// <summary>One request key. `ColorRevision` and `BrightnessRevision` are the revisions at which the colour and the
    /// brightness were last set (the entry's own revision moves with every request); `Seconds` is the transition time
    /// that was left at `StartTick`, on the kernel's tick clock.</summary>
    internal sealed record Entry(int Dimension, int Layer, int Zone, int Category, float[]? Color, int ColorRevision,
        float? Brightness, int BrightnessRevision, float Seconds, long StartTick, int Revision);

    private readonly IForgeEngine _kernel;
    private readonly Func<int, int, int, IReadOnlyList<LG_Light>?> _lights;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly List<Entry> _entries = new();
    // The revision of each key this machine has applied; the host marks its own writes applied when it records them.
    private readonly Dictionary<(int, int, int, int), int> _applied = new();
    private readonly ForgeCore.OnceReporter _failed = new();
    // An entry whose zone cannot take it yet is asked again after this many frames, not on every one.
    private const int RetryFrames = 15;
    private static readonly Comparison<Entry> ByRevision = static (a, b) => a.Revision.CompareTo(b.Revision);
    private readonly List<Entry> _due = new();
    private int _revision, _retryIn;
    private bool _disposed, _unresolvedReported;

    internal ZoneLightColorLedger(IForgeEngine kernel, Func<int, int, int, IReadOnlyList<LG_Light>?> lights, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore);
    }

    internal bool Pending { get; private set; }
    internal IReadOnlyList<Entry> Entries => _entries;

    internal static IReadOnlyList<LG_Light>? ZoneLights(int dimension, int layer, int zone)
        => EnvironmentZone.Of(dimension, layer, zone) is { } address && EnvironmentZoneTable.At(address) is { } native
            ? EnvironmentActions.Lights(native) : null;

    /// <summary>Whether one more key still fits; asked before any light is touched.</summary>
    internal bool HasRoomFor(int dimension, int layer, int zone, int category)
        => _entries.Count < MaximumEntries || _entries.Any(entry => Same(entry, dimension, layer, zone, category));

    private static bool Same(Entry entry, int dimension, int layer, int zone, int category)
        => entry.Dimension == dimension && entry.Layer == layer && entry.Zone == zone && entry.Category == category;

    /// <summary>The host's request: records it and fades the lights of its category toward what the zone now
    /// composes to. A request whose lights cannot be written leaves the record as it was.</summary>
    internal void Request(int dimension, int layer, int zone, int category, Color? color, float? brightness, float seconds)
    {
        var previous = Find(dimension, layer, zone, category);
        Record(dimension, layer, zone, category, color, brightness, seconds);
        try
        {
            if (!ApplyEntry(dimension, layer, zone, category, seconds)) throw new InvalidOperationException(PendingCode);
        }
        catch
        {
            Reinstate(dimension, layer, zone, category, previous);
            throw;
        }
    }

    /// <summary>Records what the host's request left on the zone's lights. A colour replaces the earlier colour and a
    /// brightness replaces the earlier brightness; nothing compounds.</summary>
    internal void Record(int dimension, int layer, int zone, int category, Color? color, float? brightness, float seconds = 0f)
    {
        int index = _entries.FindIndex(entry => Same(entry, dimension, layer, zone, category));
        var previous = index < 0 ? null : _entries[index];
        float[]? colour = color is { } c ? new[] { c.r, c.g, c.b, c.a } : previous?.Color;
        int revision = ++_revision;
        var entry = new Entry(dimension, layer, zone, category, colour, color != null ? revision : previous?.ColorRevision ?? 0,
            brightness ?? previous?.Brightness, brightness != null ? revision : previous?.BrightnessRevision ?? 0,
            seconds, _kernel.CurrentTick, revision);
        if (index < 0) _entries.Add(entry); else _entries[index] = entry;
        _applied[(dimension, layer, zone, category)] = entry.Revision;
        Pending = true;
        _state.Changed();
    }

    internal Entry? Find(int dimension, int layer, int zone, int category)
        => _entries.FirstOrDefault(entry => Same(entry, dimension, layer, zone, category));

    /// <summary>Puts one key back to the entry it held (or removes it) once the lights it names have been restored, so a record
    /// made only to be tested is not applied again by a late joiner or a checkpoint restore.</summary>
    internal void Reinstate(int dimension, int layer, int zone, int category, Entry? previous)
    {
        int index = _entries.FindIndex(entry => Same(entry, dimension, layer, zone, category));
        var key = (dimension, layer, zone, category);
        if (previous == null)
        {
            if (index >= 0) _entries.RemoveAt(index);
            _applied.Remove(key);
        }
        else
        {
            if (index < 0) _entries.Add(previous); else _entries[index] = previous;
            _applied[key] = previous.Revision;
        }
        _state.Changed();
    }

    private JsonElement Capture() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = FormatVersion.Current,
        revision = _revision,
        entries = _entries.Select(entry => new
        {
            entry.Dimension,
            entry.Layer,
            entry.Zone,
            entry.Category,
            entry.Color,
            entry.ColorRevision,
            entry.Brightness,
            entry.BrightnessRevision,
            Seconds = Remaining(entry),
            entry.Revision
        }).ToArray()
    });

    internal void Restore(JsonElement data)
    {
        if (_disposed) return;
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("light-ledger-object");
        string[] fields = { "schemaVersion", "revision", "entries" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name)) throw new FormatException("light-ledger-field");
        if (seen.Count != fields.Length) throw new FormatException("light-ledger-field");
        if (data.GetProperty("schemaVersion").GetString() != FormatVersion.Current) throw new FormatException("light-ledger-version");
        int revision = data.GetProperty("revision").GetInt32();
        var entries = new List<Entry>();
        long now = _kernel.CurrentTick;
        var array = data.GetProperty("entries");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumEntries) throw new FormatException("light-ledger-entries");
        foreach (var item in array.EnumerateArray())
        {
            float[]? colour = null;
            var raw = item.GetProperty("Color");
            if (raw.ValueKind == JsonValueKind.Array)
            {
                colour = raw.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                if (colour.Length != 4 || colour.Any(value => !float.IsFinite(value))) throw new FormatException("light-ledger-colour");
            }
            else if (raw.ValueKind != JsonValueKind.Null) throw new FormatException("light-ledger-colour");
            float? brightness = null;
            var amount = item.GetProperty("Brightness");
            if (amount.ValueKind == JsonValueKind.Number) brightness = amount.GetSingle();
            else if (amount.ValueKind != JsonValueKind.Null) throw new FormatException("light-ledger-brightness");
            if (brightness is { } value2 && !float.IsFinite(value2)) throw new FormatException("light-ledger-brightness");
            float seconds = item.GetProperty("Seconds").GetSingle();
            if (!float.IsFinite(seconds) || seconds < 0) throw new FormatException("light-ledger-seconds");
            entries.Add(new(item.GetProperty("Dimension").GetInt32(), item.GetProperty("Layer").GetInt32(),
                item.GetProperty("Zone").GetInt32(), item.GetProperty("Category").GetInt32(), colour,
                item.GetProperty("ColorRevision").GetInt32(), brightness, item.GetProperty("BrightnessRevision").GetInt32(),
                seconds, now, item.GetProperty("Revision").GetInt32()));
        }
        ReleaseWithdrawnZones(entries);
        _entries.Clear(); _entries.AddRange(entries);
        _revision = Math.Max(_revision, revision);
        _unresolvedReported = false;
        Pending = true;
        _retryIn = 0;
        Frame();
    }

    /// <summary>The seconds of an entry's transition that are left now; an entry with no tick to count from has all of them.</summary>
    private float Remaining(Entry entry)
    {
        long now = _kernel.CurrentTick;
        if (entry.Seconds <= 0 || entry.StartTick < 0 || now < entry.StartTick || _kernel.TicksPerSecond <= 0) return entry.Seconds;
        return Math.Max(0f, entry.Seconds - (float)((now - entry.StartTick) / _kernel.TicksPerSecond));
    }

    /// <summary>A restore that takes a key away, or goes back to an earlier revision of it, has no entry left to
    /// say what the light shows: the zone's lights go back to the level's own values and the entries still standing
    /// are applied again from there. A zone that only gains or advances entries is not touched.</summary>
    private void ReleaseWithdrawnZones(List<Entry> next)
    {
        var zones = new List<(int Dimension, int Layer, int Zone)>();
        foreach (var (key, done) in _applied)
        {
            var kept = next.Find(entry => Same(entry, key.Item1, key.Item2, key.Item3, key.Item4));
            var zone = (key.Item1, key.Item2, key.Item3);
            if ((kept == null || done > kept.Revision) && !zones.Contains(zone)) zones.Add(zone);
        }
        foreach (var (dimension, layer, zone) in zones)
        {
            LightColorFades.CancelZone(dimension, layer, zone);
            if (_lights(dimension, layer, zone) is { Count: > 0 } lights) LightColorFades.Release(lights);
            foreach (var key in _applied.Keys.Where(key => key.Item1 == dimension && key.Item2 == layer && key.Item3 == zone).ToArray())
                _applied.Remove(key);
        }
    }

    /// <summary>Applies every entry whose revision this machine has not applied, oldest first so the newest request
    /// leaves its transition time on the lights it shares. A zone whose lights are not built yet keeps the entry
    /// pending, reported once, and is retried <see cref="RetryFrames"/> frames later.</summary>
    internal void Frame()
    {
        if (_disposed || !Pending) return;
        if (_retryIn > 0) { _retryIn--; return; }
        bool complete = true;
        _due.Clear();
        foreach (var entry in _entries)
            if (!_applied.TryGetValue((entry.Dimension, entry.Layer, entry.Zone, entry.Category), out var done) || done != entry.Revision)
                _due.Add(entry);
        _due.Sort(ByRevision);
        foreach (var entry in _due)
        {
            if (ApplyEntry(entry.Dimension, entry.Layer, entry.Zone, entry.Category, Remaining(entry)))
                _applied[(entry.Dimension, entry.Layer, entry.Zone, entry.Category)] = entry.Revision;
            else complete = false;
        }
        Pending = !complete;
        if (!complete) _retryIn = RetryFrames;
        if (!complete && !_unresolvedReported)
        {
            _unresolvedReported = true;
            Report(PendingCode);
        }
    }

    /// <summary>What one light of a zone shows: the newest brightness of the entries naming its category, times the
    /// light's native intensity, and the newest colour. A field no entry sets is left to the light.</summary>
    private (Color? Color, float? Intensity) Composed(int dimension, int layer, int zone, LG_Light light, float original)
    {
        float? brightness = null;
        int brightnessRevision = -1;
        float[]? colour = null;
        int colourRevision = -1;
        foreach (var entry in _entries)
        {
            if (entry.Dimension != dimension || entry.Layer != layer || entry.Zone != zone) continue;
            if (entry.Category != LightColorFade.EveryCategory && entry.Category != (int)light.m_category) continue;
            if (entry.Brightness is { } set && entry.BrightnessRevision > brightnessRevision) { brightness = set; brightnessRevision = entry.BrightnessRevision; }
            if (entry.Color != null && entry.ColorRevision > colourRevision) { colour = entry.Color; colourRevision = entry.ColorRevision; }
        }
        float? intensity = brightness is { } factor ? original * factor : null;
        if (intensity is { } value && !float.IsFinite(value)) throw new InvalidOperationException(BrightnessOverflowCode);
        return (colour is { } c ? new Color(c[0], c[1], c[2], c[3]) : null, intensity);
    }

    /// <summary>Fades the zone's lights of one category toward what the zone's entries compose to, over `seconds`
    /// (nothing is written ahead of the frames for a transition with a length). False while the zone's lights are not
    /// built; a category no light of the zone has leaves nothing to change.</summary>
    private bool ApplyEntry(int dimension, int layer, int zone, int category, float seconds)
    {
        var lights = _lights(dimension, layer, zone);
        if (lights == null || lights.Count == 0) return false;
        try
        {
            LightColorFades.Prepare(_kernel.WorldEpoch);
            var fade = LightColorFade.Toward(lights, category, seconds,
                light => Composed(dimension, layer, zone, light, LightColorFades.Own(light).OriginalIntensity).Color,
                light => Composed(dimension, layer, zone, light, LightColorFades.Own(light).OriginalIntensity).Intensity);
            LightColorFades.Schedule(_kernel.WorldEpoch, new LightColorFade.Key(dimension, layer, zone, category), fade);
            return true;
        }
        catch (InvalidOperationException error) when (error.Message == BrightnessOverflowCode) { throw; }
        catch (Exception error)
        {
            // Reported once per revision: the entry is retried every frame, but a light that refuses the
            // write (for example one another writer owns) must not flood the log.
            if (_failed.ShouldReport(dimension + "/" + layer + "/" + zone + "/" + category + "/" + _revision))
                Report("light-ledger-exception", error.GetType().Name);
            return false;
        }
    }
    /// <summary>A world change or a stopped runtime ends the record: the lights it names are gone.</summary>
    internal void Clear()
    {
        _entries.Clear(); _applied.Clear(); _failed.Reset(); _revision = 0; _retryIn = 0; Pending = false; _unresolvedReported = false;
    }

    private void Report(string code, string? exceptionType = null)
    {
        try { _report(exceptionType == null ? "map." + code : "map." + code + ": " + exceptionType); }
        catch (Exception reportError) { MapLog.Contained("zone-light-color.report", reportError); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _state.Dispose();
    }
}
