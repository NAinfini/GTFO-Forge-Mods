using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using Player;
using UnityEngine;
using NV = System.Numerics.Vector3;
using ForgeCore;
using Ambient = ForgeVisual.Ambient;
using AmbientLease = ForgeVisual.AmbientLease;
using AmbientValues = ForgeVisual.AmbientValues;

namespace ForgeMap.Native;

/// <summary>One lighting receipt, carried by Runtime snapshots; location comes from the existing native facts.</summary>
internal sealed class GlobalLightSession : IDisposable
{
    private sealed class Value
    {
        internal GlobalLightValue From, To; internal long Start; internal double Ticks;
        internal Value(GlobalLightValue value) { From = To = value; }
        internal GlobalLightValue At(long tick)
        {
            float t = Ticks <= 0 ? 1 : (float)Math.Clamp((tick - Start) / Ticks, 0, 1);
            return new(To.Brightness.HasValue ? From.Brightness.GetValueOrDefault(To.Brightness.Value) + (To.Brightness.Value - From.Brightness.GetValueOrDefault(To.Brightness.Value)) * t : null,
                To.Color.HasValue ? NV.Lerp(From.Color.GetValueOrDefault(To.Color.Value), To.Color.Value, t) : null);
        }
        internal double Left(long tick) => Ticks <= 0 ? 0 : Math.Max(0, Ticks - (tick - Start));
        internal void Set(GlobalLightValue next, GlobalLightValue effective, long tick, double duration)
        { From = effective; To = next; Start = tick; Ticks = duration; }
    }
    private sealed class Zone
    {
        internal LG_Zone Native; internal readonly EnvironmentZone Address; internal readonly Value State;
        internal Zone(LG_Zone native, EnvironmentZone address, GlobalLightValue value) { Native = native; Address = address; State = new(value); }
    }
    private readonly IForgeEngine _kernel; private readonly GlobalLightLevel[] _definitions;
    private readonly RuntimeLifecycleSubscription _lifecycle; private readonly DomainStateChannel.NativeStateSource _state;
    private Zone[] _zones = Array.Empty<Zone>(); private Value _global = new(default);
    private LocalPlayerAgent? _player; private EffectLight? _light;
    /// <summary>The session's hold on the local player's ambient light (ForgeVisual <see cref="Ambient"/>); the fade, the receipts and the restore are the library's.</summary>
    private AmbientLease? _lease;
    private string? _level; private int _current = -2; private bool _started;
    private JsonElement? _pendingState;
    internal GlobalLightSession(IForgeEngine kernel, RuntimeModuleHandle registration, IReadOnlyList<GlobalLightLevel> definitions)
    {
        _kernel = kernel; _definitions = definitions.ToArray();
        _state = DomainStateChannel.RegisterNativeState(kernel, "forge.map.global-light", Capture, Restore);
        _lifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration) { RestoreOriginal(); _current = -2; _started = false; _pendingState = null; }
            else if (value.Kind == RuntimeLifecycleKind.CheckpointRestored) { RebindZones(); _current = -2; ApplyCurrent(true); }
            if (value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) RestoreOriginal();
        });
    }
    internal void StartLevel()
    {
        var level = LevelIdentity.Read(); if (level == null) return;
        RestoreOriginal(); _level = level.Value.ToString(); _started = true;
        var definition = _definitions.FirstOrDefault(row => row.Level == level); _global = new(definition?.Value ?? default);
        var zones = new List<Zone>();
        foreach (var native in EnvironmentZoneTable.Standing())
        {
            if (native.m_layer == null) continue;
            if (EnvironmentZone.Of((int)native.m_dimensionIndex, (int)native.m_layer.m_type, (int)native.LocalIndex) is not { } address) continue;
            var value = definition?.Zones.FirstOrDefault(z => z.Dimension == address.Dimension && z.Layer == address.Layer && z.Zone == address.Zone)?.Value ?? default;
            zones.Add(new(native, address, value));
        }
        _zones = zones.ToArray(); _current = -2;
        var pending = _pendingState; _pendingState = null; if (pending.HasValue) Restore(pending.Value); else ApplyCurrent(true);
    }
    /// <summary>The session's whole lighting record, and the way back to it: a test that wrote a value restores the record it found.</summary>
    internal JsonElement Snapshot() => Capture();
    internal void Revert(JsonElement snapshot) => Restore(snapshot);
    internal void LocationChanged(PlayerAgent agent)
    {
        if (!agent.IsLocallyOwned) return;
        EnsureLevel(); if (!_started) return;
        ApplyCurrent(false);
    }
    private void EnsureLevel() { if (!_started && GameStateManager.CurrentStateName == eGameStateName.InLevel && Players.Local?.CourseNode != null) StartLevel(); }
    internal void PlayersChanged() { EnsureLevel(); if (_started) ApplyCurrent(true); }
    internal void RebindZones() { foreach (var zone in _zones) if (EnvironmentZoneTable.At(zone.Address) is { } native) zone.Native = native; }
    private bool Bind()
    {
        var player = Players.Local?.TryCast<LocalPlayerAgent>(); var light = player?.m_ambientLight;
        if (player == null || light == null) { RestoreOriginal(); return false; }
        if (_light != null && _light.Pointer == light.Pointer && _player?.Pointer == player.Pointer) return true;
        RestoreOriginal(); _player = player; _light = light; return true;
    }
    private int CurrentZone()
    {
        var zone = _player?.CourseNode?.m_zone;
        if (zone == null) return -1;
        for (int i = 0; i < _zones.Length; i++) if (_zones[i].Native.Pointer == zone.Pointer) return i;
        return -1;
    }
    private AmbientValues Native => Ambient.Native ?? new(_light?.Intensity ?? 0, _light?.Range ?? 0, _light?.Color ?? Color.white);
    private GlobalLightValue Original => new(Native.Intensity, new NV(Native.Color.r, Native.Color.g, Native.Color.b));
    private GlobalLightValue Effective(int zone, long tick) => zone < 0 ? _global.At(tick) : _zones[zone].State.At(tick).Over(_global.At(tick));
    internal void ApplyCurrent(bool force)
    {
        if (!_started || !Bind()) return;
        int zone = CurrentZone(); if (!force && zone == _current) return;
        bool switched = _current != -2 && zone != _current; _current = zone;
        var effective = Effective(zone, _kernel.CurrentTick);
        var applied = _lease is { Active: true };
        if (!effective.Configured) { if (applied) { var native = Native; _lease!.Release(); _lease = null; Log(switched ? GlobalLightCodes.OverrideChanged : GlobalLightCodes.Restored, Original, zone, native.Range); } return; }
        var target = (zone < 0 ? _global.To : _zones[zone].State.To.Over(_global.To)).Over(Original);
        double left = Math.Max(_global.Left(_kernel.CurrentTick), zone < 0 ? 0 : _zones[zone].State.Left(_kernel.CurrentTick));
        float? brightness = effective.Brightness.HasValue ? target.Brightness : null;
        Color? color = effective.Color.HasValue ? ToColor(target.Color!.Value) : null;
        float seconds = left <= 0 ? 0 : (float)(left / _kernel.TicksPerSecond);
        // A level-wide light reaches the whole view distance, whichever of brightness and colour the value sets.
        if (applied) _lease!.Set(brightness, ClusteredRendering.IN_LEVEL_VIEW_DISTANCE, color, seconds);
        else _lease = Ambient.Lease("ForgeMap", brightness, ClusteredRendering.IN_LEVEL_VIEW_DISTANCE, color, seconds);
        Log(switched ? GlobalLightCodes.OverrideChanged : GlobalLightCodes.Applied, target, zone);
    }
    internal Outcome Change(CommandContext context, IReadOnlyList<EnvironmentZone> named, bool expedition, Color? color, float? brightness, float seconds)
    {
        if (!_started) { Refuse(ResultCode.GlobalLightLevelUnavailable, "No active level."); return Outcome.Rejected(ResultCode.GlobalLightLevelUnavailable); }
        var indexes = new List<int>();
        if (expedition)
        {
            for (int i = 0; i < _zones.Length; i++) indexes.Add(i);
        }
        else foreach (var address in named)
        {
            int index = -1; for (int i = 0; i < _zones.Length; i++) if (_zones[i].Address.Dimension == address.Dimension && _zones[i].Address.Layer == address.Layer && _zones[i].Address.Zone == address.Zone) { index = i; break; }
            if (index < 0) { Refuse(ResultCode.GlobalLightZoneUnavailable, "The requested zone is unavailable."); return Outcome.Rejected(ResultCode.GlobalLightZoneUnavailable); }
            indexes.Add(index);
        }
        if (indexes.Count == 0) { Refuse(ResultCode.GlobalLightZoneUnavailable, "The active level has no zones."); return Outcome.Rejected(ResultCode.GlobalLightZoneUnavailable); }
        NV? rgb = color.HasValue ? new NV(color.Value.r, color.Value.g, color.Value.b) : null;
        double ticks = seconds <= 0 ? 0 : seconds * _kernel.TicksPerSecond; long now = _kernel.CurrentTick;
        if (expedition)
        {
            var next = new GlobalLightValue(brightness ?? _global.To.Brightness, rgb ?? _global.To.Color);
            _global.Set(next, _global.At(now).Over(Original), now, ticks);
        }
        else foreach (int index in indexes)
        {
            var state = _zones[index].State; var next = new GlobalLightValue(brightness ?? state.To.Brightness, rgb ?? state.To.Color);
            state.Set(next, Effective(index, now).Over(Original), now, ticks);
        }
        ApplyCurrent(true); _state.Changed();
        // One row per explicit recipient: `expedition` writes the level-wide state, but the rows still answer for the zones the request named.
        var scheduled = Outcome.Succeeded(ResultCode.GlobalLightScheduled);
        return context.EachTarget(ref scheduled, EnvironmentActions.SameStep);
    }
    private static Color ToColor(NV value) => new(value.X, value.Y, value.Z, 1);
    internal void RestoreOriginal()
    {
        if (_lease is { Active: true }) { var native = Native; _lease.Release(); Log(GlobalLightCodes.Restored, Original, _current, native.Range); }
        _lease = null;
        _light = null; _player = null;
    }
    private void Log(string code, GlobalLightValue value, int index, float? range = null)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        EnvironmentZone? a = index >= 0 && index < _zones.Length ? _zones[index].Address : null;
        var color = _light != null ? _light.Color : ToColor(value.Color ?? NV.Zero);
        MapLog.Record(RuntimeLogLevel.Info, code, "Local lighting override processed; newer external fields are preserved.",
            RuntimeLogValue.Of("level", _level ?? ""), RuntimeLogValue.Of("brightness", _light != null ? _light.Intensity : value.Brightness ?? 0),
            RuntimeLogValue.Of("r", color.r), RuntimeLogValue.Of("g", color.g), RuntimeLogValue.Of("b", color.b),
            RuntimeLogValue.Of("dimension", a?.Dimension ?? -1), RuntimeLogValue.Of("layer", a?.Layer ?? -1), RuntimeLogValue.Of("zone", a?.Zone ?? -1),
            RuntimeLogValue.Of("range", _light != null ? _light.Range : range ?? 0), RuntimeLogValue.Of("ownedFields", _lease?.OwnedFields ?? 0));
    }
    internal static void Refuse(string reason, string detail) => MapLog.Record(RuntimeLogLevel.Error, GlobalLightCodes.Refused, "Global lighting refused.", RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Quote("detail", detail));
    private object Save(Value value) => new { from = Data(value.At(_kernel.CurrentTick)), to = Data(value.To), ticks = value.Left(_kernel.CurrentTick) };
    private static object Data(GlobalLightValue value) => new { brightness = value.Brightness, color = value.Color.HasValue ? new[] { value.Color.Value.X, value.Color.Value.Y, value.Color.Value.Z } : null };
    private JsonElement Capture() => JsonSerializer.SerializeToElement(new { level = _started ? _level : null, global = Save(_global), zones = _zones.Select(z => new { dimension = z.Address.Dimension, layer = z.Address.Layer, zone = z.Address.Zone, state = Save(z.State) }).ToArray() });
    private static GlobalLightValue ReadValue(JsonElement data)
    {
        float? brightness = null; NV? color = null;
        var number = data.GetProperty("brightness"); if (number.ValueKind != JsonValueKind.Null) { float v = number.GetSingle(); if (!float.IsFinite(v) || v < 0) throw new FormatException("global-light-state"); brightness = v; }
        var vector = data.GetProperty("color"); if (vector.ValueKind != JsonValueKind.Null) { var v = vector.EnumerateArray().Select(x => x.GetSingle()).ToArray(); if (v.Length != 3 || v.Any(x => !float.IsFinite(x) || x < 0)) throw new FormatException("global-light-state"); color = new NV(v[0], v[1], v[2]); }
        return new(brightness, color);
    }
    private Value ReadState(JsonElement row)
    {
        var value = new Value(ReadValue(row.GetProperty("to"))); var from = ReadValue(row.GetProperty("from")); double ticks = row.GetProperty("ticks").GetDouble();
        if (!double.IsFinite(ticks) || ticks < 0) throw new FormatException("global-light-state"); value.Set(value.To, from, _kernel.CurrentTick, ticks); return value;
    }
    private void Restore(JsonElement data)
    {
        string? level = data.GetProperty("level").GetString(); if (level == null) return;
        EnsureLevel();
        if (!_started || _zones.Length == 0) { _pendingState = data.Clone(); return; }
        if (level != _level) throw new FormatException("global-light-state-level");
        var global = ReadState(data.GetProperty("global")); var states = new Value?[_zones.Length];
        foreach (var row in data.GetProperty("zones").EnumerateArray())
        {
            int d = row.GetProperty("dimension").GetInt32(), l = row.GetProperty("layer").GetInt32(), z = row.GetProperty("zone").GetInt32(), index = -1;
            for (int i = 0; i < _zones.Length; i++) { var a = _zones[i].Address; if (a.Dimension == d && a.Layer == l && a.Zone == z) { index = i; break; } }
            if (index < 0 || states[index] != null) throw new FormatException("global-light-state-zone"); states[index] = ReadState(row.GetProperty("state"));
        }
        if (states.Any(s => s == null)) throw new FormatException("global-light-state-zone");
        _global = global; for (int i = 0; i < _zones.Length; i++) { var s = states[i]!; _zones[i].State.Set(s.To, s.From, s.Start, s.Ticks); }
        ApplyCurrent(true);
    }
    public void Dispose() { RestoreOriginal(); _started = false; _state.Dispose(); _lifecycle.Dispose(); }
}
