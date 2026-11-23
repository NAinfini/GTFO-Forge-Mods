using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>One zone's host-authored clock state. Advance is array/scalar-only and allocates nothing.
/// The same replicated checkpoint and simulation time produce the same position on every peer.</summary>
internal sealed class TriggerZoneMotion
{
    private readonly TriggerZone _zone;
    private readonly double[] _lengths;
    private readonly double _length;
    private readonly double[] _resizeFrom = new double[3], _resizeTo = new double[3];
    private double _resizeStarted, _resizeDuration;
    public TriggerZoneMotion(TriggerZone zone, double now, bool host)
    {
        _zone = zone;
        var points = zone.Route?.Points;
        _lengths = new double[points == null ? 0 : points.Count + (zone.Route!.Mode == "loop" ? 1 : 0)];
        if (points != null)
            for (var i = 1; i < points.Count; i++)
            {
                var x = points[i][0] - points[i - 1][0];
                var y = points[i][1] - points[i - 1][1];
                var z = points[i][2] - points[i - 1][2];
                _lengths[i] = _lengths[i - 1] + Math.Sqrt(x * x + y * y + z * z);
            }
        if (points != null && zone.Route!.Mode == "loop")
        {
            var x = points[^1][0] - points[0][0]; var y = points[^1][1] - points[0][1]; var z = points[^1][2] - points[0][2];
            _lengths[^1] = _lengths[^2] + Math.Sqrt(x * x + y * y + z * z);
        }
        _length = _lengths.Length == 0 ? 0 : _lengths[^1];
        AnchorTime = now;
        Running = host && zone.Route?.AutoStart == true;
    }

    public bool Running { get; private set; }
    public double AnchorTime { get; private set; }
    public double Elapsed { get; private set; }
    public long Revision { get; private set; }
    public bool Resizing => _resizeDuration > 0;
    public bool Active => Running || Resizing;

    public string Move(string mode, double now)
    {
        if (_zone.Route == null) return ResultCode.TriggerZoneRouteMissing;
        if (mode is not ("start" or "pause" or "resume" or "reset")) return ResultCode.TriggerZoneInput;
        if ((mode == "pause" && !Running) || (mode == "resume" && Running)) return ResultCode.Completed;
        var elapsed = Elapsed + (Running ? Math.Max(0, now - AnchorTime) : 0);
        switch (mode)
        {
            case "start": Elapsed = 0; Running = true; break;
            case "pause": Elapsed = elapsed; Running = false; break;
            case "resume": Elapsed = elapsed; Running = true; break;
            case "reset": Elapsed = 0; Running = false; break;
        }
        AnchorTime = now; Revision++;
        Advance(now);
        return ResultCode.Completed;
    }

    public string Resize(IReadOnlyList<double> size, double duration, double now)
    {
        if (size.Count != 3 || !Single(duration)) return ResultCode.TriggerZoneInput;
        for (var i = 0; i < 3; i++) if (!Single(size[i])) return ResultCode.TriggerZoneInput;
        if (!Resizing && size[0] == _zone.Size[0] && size[1] == _zone.Size[1] && size[2] == _zone.Size[2]) return ResultCode.Completed;
        Advance(now);
        for (var i = 0; i < 3; i++) { _resizeFrom[i] = _zone.Size[i]; _resizeTo[i] = size[i]; }
        _resizeStarted = now; _resizeDuration = Math.Max(0, duration); Revision++;
        if (_resizeDuration == 0) for (var i = 0; i < 3; i++) _zone.MutableSize[i] = size[i];
        return ResultCode.Completed;
    }

    internal static bool Single(double value) => double.IsFinite(value) && Math.Abs(value) <= float.MaxValue;

    public bool Advance(double now)
    {
        if (_resizeDuration > 0)
        {
            var amount = Math.Clamp((now - _resizeStarted) / _resizeDuration, 0, 1);
            for (var i = 0; i < 3; i++) _zone.MutableSize[i] = _resizeFrom[i] * (1 - amount) + _resizeTo[i] * amount;
            if (amount == 1) _resizeDuration = 0;
        }
        if (_zone.Route is not { } route) return false;
        if (_length <= 0)
        {
            if (Running && route.Mode == "once") { Running = false; AnchorTime = now; return true; }
            return false;
        }
        var elapsed = Elapsed + (Running ? Math.Max(0, now - AnchorTime) : 0);
        var distance = elapsed * route.Speed;
        var stopped = false;
        if (route.Mode == "once")
        {
            distance = Math.Clamp(distance, 0, _length);
            if (Running && route.Speed != 0 && (route.Speed > 0 ? distance >= _length : distance <= 0))
            { Elapsed = elapsed; AnchorTime = now; Running = false; stopped = true; }
        }
        else if (route.Mode == "pingpong")
        {
            distance = Mod(distance, _length * 2);
            if (distance > _length) distance = _length * 2 - distance;
        }
        else distance = Mod(distance, _length);
        var end = 1;
        while (end < _lengths.Length - 1 && _lengths[end] < distance) end++;
        var span = _lengths[end] - _lengths[end - 1];
        var t = span > 0 ? (distance - _lengths[end - 1]) / span : 0;
        for (var i = 0; i < 3; i++) _zone.MutablePosition[i] = route.Points[end - 1][i] * (1 - t) + route.Points[end % route.Points.Count][i] * t;
        return stopped;
    }

    private static double Mod(double value, double modulus) => (value % modulus + modulus) % modulus;

    public TriggerZoneClockState Capture()
        => new(Revision, _zone.Id, _zone.Enabled, _zone.Visible, _zone.LabelText, _zone.LabelVisible, _zone.LabelResourceId,
            Running, AnchorTime, Elapsed, _resizeStarted, _resizeDuration,
            (double[])_resizeFrom.Clone(), (double[])_resizeTo.Clone(), new[] { _zone.Size[0], _zone.Size[1], _zone.Size[2] });

    /// <summary>Takes the host's state as it is: the host table and the checkpoint hand states over in order, and a
    /// checkpoint reload puts an earlier revision back.</summary>
    public string Apply(TriggerZoneClockState state, double now)
    {
        if (state.ZoneId != _zone.Id) return ResultCode.TriggerZoneMissing;
        if (!double.IsFinite(state.AnchorTime) || !double.IsFinite(state.Elapsed) || !double.IsFinite(state.ResizeStarted)
            || !Single(state.ResizeDuration) || state.ResizeFrom is not { Length: 3 } || state.ResizeTo is not { Length: 3 }
            || state.Size is not { Length: 3 } || state.LabelText == null || (state.Running && _zone.Route == null))
            return ResultCode.TriggerZoneInput;
        for (var i = 0; i < 3; i++)
            if (!Single(state.ResizeFrom[i]) || !Single(state.ResizeTo[i]) || !Single(state.Size[i])) return ResultCode.TriggerZoneInput;
        Revision = state.Revision; _zone.Enabled = state.Enabled; _zone.Visible = state.Visible;
        _zone.LabelText = state.LabelText; _zone.LabelVisible = state.LabelVisible;
        _zone.LabelRevision++;
        _zone.LabelResourceId = state.LabelResourceId;
        Running = state.Running; AnchorTime = state.AnchorTime; Elapsed = state.Elapsed;
        _resizeStarted = state.ResizeStarted; _resizeDuration = state.ResizeDuration;
        for (var i = 0; i < 3; i++) { _resizeFrom[i] = state.ResizeFrom[i]; _resizeTo[i] = state.ResizeTo[i]; _zone.MutableSize[i] = state.Size[i]; }
        Advance(now); return ResultCode.Completed;
    }

    internal void Changed() => Revision++;
}

internal sealed record TriggerZoneClockState(long Revision, string ZoneId, bool Enabled, bool Visible,
    string LabelText, bool LabelVisible, uint? LabelResourceId, bool Running, double AnchorTime, double Elapsed, double ResizeStarted,
    double ResizeDuration, double[] ResizeFrom, double[] ResizeTo, double[] Size);
