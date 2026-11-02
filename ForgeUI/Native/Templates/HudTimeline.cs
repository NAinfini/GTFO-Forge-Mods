using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>A point on a <see cref="HudTimeline"/>: when, its colour and the short text shown while the pointer is on it.</summary>
public readonly record struct TimelineMark(uint TimeMs, Color Colour, string Label);

/// <summary>
/// A time bar across the bottom of the screen for playing something back (a replay, a cutscene): the bar with marks, a draggable play
/// head, step back, play / pause, step forward, the clock and the speeds. It draws what you set every frame (<see cref="DurationMs"/>,
/// <see cref="PositionMs"/>, <see cref="Playing"/>, <see cref="Speed"/>, <see cref="Marks"/>) and reports the player's clicks through
/// the callbacks; it never moves time itself. Clicks need the HUD pointer (<see cref="HudPointer.Hold"/>), which the owner holds while
/// the bar is in use. Made with <see cref="UIKitMod.AddTimeline"/>, in <see cref="HudRegion.World"/>.
/// </summary>
public sealed class HudTimeline : HudWidget
{
    private const float BandHeight = 96f, BarY = 22f, ButtonsY = -18f, Button = 34f, Margin = 24f, TextPx = 18f, SmallPx = 15f;
    private const float SeekEverySeconds = 0.1f;
    private static readonly Color Track = new(0.871f, 0.886f, 0.875f, 0.22f);
    private HudCanvas? _canvas;
    private bool _dragging;
    private uint _dragMs, _lastSentMs = uint.MaxValue;
    private float _lastSent;
    private uint _clockSeconds = uint.MaxValue, _clockDuration = uint.MaxValue;
    private string _clock = "";
    private readonly string[] _speedTexts = new string[8];
    private float[] _speedsShown = Array.Empty<float>();

    private readonly Func<int, uint> _markTime;

    internal HudTimeline() => _markTime = i => Marks[i].TimeMs;

    public uint DurationMs { get; set; }
    public uint PositionMs { get; set; }
    public bool Playing { get; set; }
    public float Speed { get; set; } = 1f;

    /// <summary>The speed buttons, slowest first (at most 8).</summary>
    public float[] Speeds { get; set; } = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

    /// <summary>The marks on the bar; change the list whenever you like.</summary>
    public List<TimelineMark> Marks { get; } = new();

    /// <summary>The play head was dragged or the bar or a mark clicked: the moment to go to. While dragging it is sent at most ten times a second, and once more on release.</summary>
    public Action<uint>? Seek { get; set; }

    public Action? PlayPause { get; set; }

    /// <summary>-1 one step back, +1 one step forward.</summary>
    public Action<int>? Step { get; set; }

    public Action<float>? SpeedChosen { get; set; }

    /// <summary>The colour of what is played and selected; the kit's accent by default.</summary>
    public Color Colour { get; set; } = UIKitColors.Accent;

    /// <summary>The band's height in HUD units from the bottom edge, so other pieces can stand above it.</summary>
    public float Height => BandHeight + UIKit.SafeMargin;

    /// <summary>True while the pointer is on the band or dragging the head: a click there belongs to the bar.</summary>
    public bool Busy { get; private set; }

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        if (!HudPointer.ScreenBounds(hud, out var screenLeft, out var screenBottom, out var screenRight, out _)) { _canvas.End(); return; }
        var bottom = screenBottom + UIKit.SafeMargin;
        var centreX = (screenLeft + screenRight) / 2f;
        var centreY = bottom + BandHeight / 2f;
        var width = screenRight - screenLeft - Margin * 2f;
        var left = screenLeft + Margin + 16f; var barWidth = width - 32f;
        var barY = centreY + BarY;
        var rowY = centreY + ButtonsY;
        Busy = HudPointer.Over(hud, centreX, centreY, width, BandHeight) || _dragging;

        _canvas.Plate(centreX, centreY, width, BandHeight, 12f, UIKitColors.Shade);
        _canvas.Frame(centreX, centreY, width, BandHeight, 12f, UIKitColors.Faded(UIKitColors.Line, 0.5f));

        // The bar, the played part, the marks and the head.
        var shown = _dragging ? _dragMs : PositionMs;
        var fraction = TimelineMath.Fraction(shown, DurationMs);
        _canvas.Rect(left + barWidth / 2f, barY, barWidth, 4f, Track);
        if (fraction > 0f) _canvas.Rect(left + barWidth * fraction / 2f, barY, barWidth * fraction, 4f, Colour);
        var overBar = HudPointer.Over(hud, left + barWidth / 2f, barY, barWidth + 16f, 28f);
        HudPointer.Position(hud, out var px, out _);
        var hovered = overBar && !_dragging ? TimelineMath.NearestMark(px, left, barWidth, DurationMs, Marks.Count, _markTime, 6f) : -1;
        for (var i = 0; i < Marks.Count; i++)
        {
            var mark = Marks[i];
            var x = left + TimelineMath.Fraction(mark.TimeMs, DurationMs) * barWidth;
            var big = i == hovered;
            _canvas.Rect(x, barY + (big ? 11f : 8f), big ? 5f : 3f, big ? 14f : 9f, mark.Colour);
        }
        var headX = left + barWidth * fraction;
        _canvas.Rect(headX, barY, 4f, 20f, UIKitColors.Bright);
        if (hovered >= 0)
        {
            var mark = Marks[hovered];
            var x = left + TimelineMath.Fraction(mark.TimeMs, DurationMs) * barWidth;
            var label = mark.Label.Length > 0 ? mark.Label + "  " + TimelineMath.Clock(mark.TimeMs) : TimelineMath.Clock(mark.TimeMs);
            var w = _canvas.TextWidth(label, SmallPx) + 20f;
            var tx = Math.Clamp(x, left + w / 2f, left + barWidth - w / 2f);
            _canvas.Plate(tx, barY + 36f, w, 24f, 6f, UIKitColors.Shade);
            _canvas.Rect(tx - w / 2f + 1.5f, barY + 36f, 3f, 24f, mark.Colour);
            _canvas.Text(label, tx, barY + 36f, SmallPx, UIKitColors.Bright, HudAlign.Centre);
        }

        // Dragging: a press on the bar takes the head; moving sends a seek now and then; release sends the last one.
        if (HudPointer.Pressed && overBar)
        {
            if (hovered >= 0) Send(Marks[hovered].TimeMs, now, force: true);
            else { _dragging = true; _dragMs = TimelineMath.At(px, left, barWidth, DurationMs); Send(_dragMs, now, force: true); }
        }
        if (_dragging)
        {
            if (HudPointer.Held) { _dragMs = TimelineMath.At(px, left, barWidth, DurationMs); Send(_dragMs, now, force: false); }
            else { _dragging = false; Send(_dragMs, now, force: true); }
        }

        // The buttons: step back, play / pause, step forward.
        var x0 = left + Button / 2f;
        if (Pressable(hud, x0, rowY)) Step?.Invoke(-1);
        _canvas.Rect(x0 - 8f, rowY, 3f, 14f, UIKitColors.Text);
        _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Left), x0 + 3f, rowY, 14f, 14f, UIKitColors.Text);
        var x1 = x0 + Button + 8f;
        if (Pressable(hud, x1, rowY)) PlayPause?.Invoke();
        if (Playing) { _canvas.Rect(x1 - 5f, rowY, 5f, 16f, Colour); _canvas.Rect(x1 + 5f, rowY, 5f, 16f, Colour); }
        else _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Right), x1 + 1f, rowY, 18f, 18f, Colour);
        var x2 = x1 + Button + 8f;
        if (Pressable(hud, x2, rowY)) Step?.Invoke(1);
        _canvas.Sprite(UIKitSprites.Arrow(ArrowDirection.Right), x2 - 3f, rowY, 14f, 14f, UIKitColors.Text);
        _canvas.Rect(x2 + 8f, rowY, 3f, 14f, UIKitColors.Text);

        // The clock.
        var seconds = shown / 1000;
        if (seconds != _clockSeconds || DurationMs != _clockDuration)
        {
            _clockSeconds = seconds; _clockDuration = DurationMs;
            _clock = TimelineMath.Clock(shown) + " / " + TimelineMath.Clock(DurationMs);
        }
        _canvas.Text(_clock, x2 + Button / 2f + 18f, rowY, TextPx, UIKitColors.Bright, HudAlign.Left, bold: true);

        // The speeds, right-aligned.
        var speeds = Speeds ?? Array.Empty<float>();
        if (!ReferenceEquals(speeds, _speedsShown))
        {
            _speedsShown = speeds;
            for (var i = 0; i < speeds.Length && i < _speedTexts.Length; i++) _speedTexts[i] = TimelineMath.Speed(speeds[i]);
        }
        var right = left + barWidth;
        for (var i = Math.Min(speeds.Length, _speedTexts.Length) - 1; i >= 0; i--)
        {
            var text = _speedTexts[i];
            var w = Math.Max(48f, _canvas.TextWidth(text, SmallPx) + 18f);
            var cx = right - w / 2f;
            var current = Math.Abs(speeds[i] - Speed) < 0.001f;
            var over = HudPointer.Over(hud, cx, rowY, w, Button - 4f);
            if (current) _canvas.Plate(cx, rowY, w - 4f, Button - 8f, 5f, Colour);
            else if (over) _canvas.Frame(cx, rowY, w - 4f, Button - 8f, 5f, Colour);
            _canvas.Text(text, cx, rowY, SmallPx, current || over ? UIKitColors.Bright : UIKitColors.Text, HudAlign.Centre, bold: current);
            if (over && HudPointer.Pressed && !current) SpeedChosen?.Invoke(speeds[i]);
            right -= w;
        }
        _canvas.End();
    }

    /// <summary>A square button's hover frame; true when it was clicked this frame.</summary>
    private bool Pressable(HudElement hud, float x, float y)
    {
        var over = HudPointer.Over(hud, x, y, Button, Button);
        _canvas!.Frame(x, y, Button - 4f, Button - 4f, 5f, over ? Colour : UIKitColors.Faded(UIKitColors.Line, 0.6f));
        return over && HudPointer.Pressed;
    }

    private void Send(uint ms, float now, bool force)
    {
        if (!force && (ms == _lastSentMs || now - _lastSent < SeekEverySeconds)) return;
        _lastSentMs = ms; _lastSent = now;
        try { Seek?.Invoke(ms); }
        catch (Exception error) { Plugin.Error("Time bar '" + Id + "' seek failed: " + error); }
    }
}

public sealed partial class UIKitMod
{
    /// <summary>A time bar across the bottom of the screen (<see cref="HudTimeline"/>), hidden until you set <see cref="HudWidget.Visible"/>.
    /// Null (logged) when the name cannot be used or is taken.</summary>
    public HudTimeline? AddTimeline(string name)
    {
        var id = Full(name, "time bar");
        if (id == null) return null;
        var bar = Hud.Add(id, new HudTimeline(), new HudSlot(HudRegion.World));
        if (bar != null) bar.Visible = false;
        return bar;
    }
}
