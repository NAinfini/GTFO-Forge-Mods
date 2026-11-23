using ForgeCore;
using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Three Forge-drawn styles and a world-space label. Native bioscan graphics expose an instance
/// material only, with no evidenced stable asset name. This renderer uses the embedded Forge/TriggerZone
/// shader, never labels that material as a native sensor asset. All objects/buffers are built once per world.</summary>
internal sealed class TriggerZoneVisuals : IDisposable
{
    private readonly Action<string> _report;
    private readonly List<Visual> _built = new();
    private readonly OnceReporter _once;
    private IReadOnlyList<TriggerZone>? _source;
    private Visual?[] _byIndex = Array.Empty<Visual?>();
    private MapLevelReference? _level;
    private long _epoch = -1;
    private bool _disposed;
    private double _frameTime = double.NaN;
    private Transform? _camera;
    private bool _languageRead, _languageAvailable;
    private Localization.Language _language;
    private string _languageCode = "";
    internal TriggerZoneVisuals(Action<string> report) { _report = report; _once = new OnceReporter(report); }
    internal int Count => _built.Count;

    internal void Sync(IReadOnlyList<TriggerZone> zones, MapLevelReference? level, long epoch = 0)
    {
        if (_disposed || (ReferenceEquals(_source, zones) && _level == level && _epoch == epoch)) return;
        Clear(); _source = zones; _level = level; _epoch = epoch;
        _byIndex = zones.Count == 0 ? Array.Empty<Visual?>() : new Visual?[zones.Count];
        if (level is not { } current) return;
        ReadFrame(0);
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            if (!zone.Placed || zone.Level != current) continue;
            try
            {
                var visual = new Visual(zone, epoch, _report, this);
                _byIndex[index] = visual; _built.Add(visual);
            }
            catch (Exception error)
            {
                var message = "trigger-zone-render: " + zone.Id + " (" + error.GetType().Name + ": " + error.Message + "); the zone stays active but is not drawn.";
                _once.Report(message, message);
            }
        }
    }

    internal void Tick(double now)
    {
        ReadFrame(now);
        for (var i = 0; i < _built.Count; i++) _built[i].Tick(now, this);
    }

    internal void Tick(int index, double now)
    {
        if ((uint)index >= (uint)_byIndex.Length || _byIndex[index] is not { } visual) return;
        if (_frameTime != now) ReadFrame(now);
        visual.Tick(now, this);
    }

    /// <summary>Whether that zone's drawn volume is still closing on a corrected pose.</summary>
    internal bool Easing(int index)
        => (uint)index < (uint)_byIndex.Length && _byIndex[index]?.Easing == true;

    private void ReadFrame(double now)
    {
        _frameTime = now;
        _languageRead = false;
        _camera = MapCamera.Transform();
        if (_camera != null && _camera.WasCollected) _camera = null;
    }

    private bool TryLanguage(out Localization.Language language, out string code)
    {
        if (!_languageRead)
        {
            _languageAvailable = StationTextValue.TryCurrentLanguage(out _language, out _languageCode);
            _languageRead = true;
        }
        language = _language; code = _languageCode;
        return _languageAvailable;
    }

    private static ZoneShape Shape(TriggerZoneShape shape) => shape switch
    {
        TriggerZoneShape.Box => ZoneShape.Box,
        TriggerZoneShape.Sphere => ZoneShape.Sphere,
        TriggerZoneShape.Cylinder => ZoneShape.Cylinder,
        TriggerZoneShape.Capsule => ZoneShape.Capsule,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown trigger-zone shape."),
    };

    private sealed class Visual : IDisposable
    {
        /// <summary>How fast the drawn volume closes on a pose a state arrival corrected, per second: the same follow a
        /// target laser gives its reported point (about 63 % after 1 / 22 s).</summary>
        private const float FollowRate = 22f;
        /// <summary>Metres a drawn volume may trail the zone's own pose before the difference is treated as a correction.
        /// The route's own movement between two frames is expected; the constant leaves room for a hitch.</summary>
        private const float CatchSlack = 0.1f;
        /// <summary>Metres between the drawn and the exact pose at which the catch-up ends by copying the pose.</summary>
        private const float CaughtMetres = 0.02f;
        private readonly TriggerZone _zone;
        private readonly long _world;
        private readonly ZoneVolume _volume;
        private readonly WorldText _label;
        private bool _labelShown;
        private long _labelRevision = -1, _textRevision = long.MinValue;
        private bool _usesPlayerLanguage;
        private readonly Action<string> _report;
        private Localization.Language _labelLanguage;
        private bool _labelLanguageKnown, _labelLanguageMissingReported;
        // The logical zone keeps the exact pose for judgment and collision; only the drawn volume eases toward it.
        private Vector3 _shownPosition;
        private double _shownAt;
        private bool _posed, _catching;

        internal Visual(TriggerZone zone, long world, Action<string> report, TriggerZoneVisuals frame)
        {
            _report = report;
            _zone = zone;
            _world = world;
            ZoneLook? look = null;
            if (zone.Appearance is { } appearance)
            {
                look = new ZoneLook(appearance.Style switch { "outline" => ZoneStyle.Outline, "sensor" => ZoneStyle.Sensor, _ => ZoneStyle.Solid },
                    (float)appearance.Red, (float)appearance.Green, (float)appearance.Blue, (float)appearance.Opacity, (float)appearance.Glow);
                if (appearance.Opacity < 0 || appearance.Opacity > 1 || appearance.Glow < 0 || appearance.Glow > ZoneVolume.GlowCeiling)
                    report("trigger-zone-appearance-saturates: " + zone.Id + "; authored values were preserved; rendering saturates them.");
            }
            _volume = ZoneVolume.Create("ForgeTriggerZone:" + zone.Id, Shape(zone.Shape), look);
            try
            {
                // The label offset is the text's lower edge above the volume; a long list grows upward, never into the floor.
                _label = WorldText.Create(_volume.Root, "label");
                Tick(0, frame);
            }
            catch
            {
                // A half-built visual would stay in the scene with no owner to destroy it.
                _volume.Dispose();
                throw;
            }
        }
        internal void Tick(double now, TriggerZoneVisuals frame)
        {
            if (!_label.Alive) return;
            var position = new Vector3((float)_zone.Position[0], (float)_zone.Position[1], (float)_zone.Position[2]);
            var rotation = new Quaternion((float)_zone.Rotation[0], (float)_zone.Rotation[1], (float)_zone.Rotation[2], (float)_zone.Rotation[3]);
            if (!_posed) { _posed = true; _shownPosition = position; _shownAt = now; }
            else
            {
                var seconds = Mathf.Clamp((float)(now - _shownAt), 0f, 0.1f);
                _shownAt = now;
                var step = position - _shownPosition;
                var speed = _zone.Route == null ? 0f : (float)Math.Abs(_zone.Route.Speed);
                var allowance = CatchSlack + 2f * speed * seconds;
                // A state arrival can correct the route's phase (a host clock this peer does not share) or reset it. The
                // difference is then much larger than the route's own movement between two frames; ease that one instead
                // of teleporting, and keep following exactly once the two agree again.
                if (_catching || step.sqrMagnitude > allowance * allowance)
                {
                    _catching = true;
                    _shownPosition.x = ForgeUI.Ease.Damp(_shownPosition.x, position.x, FollowRate, seconds);
                    _shownPosition.y = ForgeUI.Ease.Damp(_shownPosition.y, position.y, FollowRate, seconds);
                    _shownPosition.z = ForgeUI.Ease.Damp(_shownPosition.z, position.z, FollowRate, seconds);
                    if ((position - _shownPosition).sqrMagnitude <= CaughtMetres * CaughtMetres)
                    { _shownPosition = position; _catching = false; }
                }
                else _shownPosition = position;
            }
            _volume.SetPose(_shownPosition, rotation);
            _volume.SetShown(_zone.Visible && _zone.Appearance != null);
            var labelShown = _zone.LabelVisible && !_zone.LabelOverridden;
            if (_textRevision != _zone.LabelRevision)
            {
                _usesPlayerLanguage = StationTextValue.RequiresLanguage(_zone.LabelText);
                _textRevision = _zone.LabelRevision;
            }
            bool usesPlayerLanguage = _usesPlayerLanguage;
            Localization.Language language = default;
            bool languageAvailable = true;
            if (usesPlayerLanguage)
            {
                languageAvailable = frame.TryLanguage(out language, out var code);
                if (languageAvailable) _labelLanguageMissingReported = false;
                else
                {
                    if (!_labelLanguageMissingReported)
                    {
                        _report("trigger-zone-label-text-refused: " + _zone.Id + " (" + code + ").");
                        _labelLanguageMissingReported = true;
                    }
                }
            }
            labelShown &= languageAvailable;
            if (_labelShown != labelShown) { _label.SetShown(labelShown); _labelShown = labelShown; }
            var labelLayoutChanged = languageAvailable && (_labelRevision != _zone.LabelRevision
                || usesPlayerLanguage && (!_labelLanguageKnown || _labelLanguage != language));
            if (labelLayoutChanged)
            {
                if (!(usesPlayerLanguage
                        ? StationTextValue.TryRender(_zone.LabelText, language, out var text)
                        : StationTextValue.TryRender(_zone.LabelText, out text)))
                { text = ""; _report("trigger-zone-label-text-unavailable: " + _zone.Id); }
                // Fixed layout width (as placed-model text) so shrinking the rect cannot change TMP's wrap.
                _label.SetContent(text, (float)PlacedModelTextStyle.DefaultLineWidth);
                _labelRevision = _zone.LabelRevision;
                _labelLanguage = language;
                _labelLanguageKnown = usesPlayerLanguage;
            }
            // ForgeVisual turns the label just before the cameras render; the same camera again is a no-op.
            _label.FaceCamera(frame._camera);
            _label.SetLocalPosition(new Vector3(0, (float)(Top() + (_zone.Label?.Offset ?? 0)), 0));
            if (labelLayoutChanged && labelShown)
                StationSignPlacementReadback.Record(_world, _zone, _volume.Root, _label.Text);
            _volume.Pulse(now);
            _volume.Resize((float)_zone.Size[0], (float)_zone.Size[1], (float)_zone.Size[2]);
        }

        /// <summary>Whether the drawn volume is still closing on a corrected pose; the session keeps such a zone in its
        /// frame set until it has.</summary>
        internal bool Easing => _posed && _catching && _label.Alive;

        private double Top() => _zone.TopHeight();
        public void Dispose() => _volume.Dispose();
    }
    private void Clear()
    {
        for (var i = 0; i < _built.Count; i++) _built[i].Dispose(); _built.Clear();
        _byIndex = Array.Empty<Visual?>();
        _camera = null; _frameTime = double.NaN; _languageRead = false;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); _source = null; }
}
