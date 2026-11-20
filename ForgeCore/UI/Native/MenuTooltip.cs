using System;
using CellMenu;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The kit's tooltip in the lobby and menus: a small dark chamfered box with a thin outline and one or two lines of text, over its
/// button when there is room inside the given bounds and under it otherwise (<see cref="TooltipLayout"/>), kept inside them sideways.
/// Drawn on a <see cref="MenuCanvas"/> in the button's own space.
/// </summary>
internal static class MenuTooltip
{
    private static readonly Color Box = new(0.03f, 0.04f, 0.047f, 0.94f);

    /// <summary>The longest line, in text heights: a longer text wraps onto a second line rather than running across the screen.</summary>
    private const float MaxLine = 30f;

    /// <summary>Draws <paramref name="text"/> for a button whose box is <paramref name="anchor"/>; <paramref name="size"/> is the text
    /// size and <paramref name="bounds"/> the space it must stay in. A text longer than a line wraps onto a second one (and is drawn
    /// smaller, then cut, past that: one or two short sentences are what a tooltip holds).</summary>
    internal static void Draw(MenuCanvas canvas, Rect anchor, string text, float size, Rect bounds)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pad = size * 0.6f;
        var room = Math.Max(size * 4f, bounds.Width() - 2f * pad);
        var line = Math.Min(room, size * MaxLine);
        var full = canvas.TextWidth(text, size);
        var wrap = full > line;
        // Two lines: about half the text each, with room for the break falling between words.
        var textWidth = wrap ? Math.Min(line, full * 0.6f) : full;
        var width = textWidth + 2f * pad;
        var height = wrap ? size * 3.1f : size * 1.7f;
        var (x, y) = TooltipLayout.Place(anchor.CenterX(), anchor.CenterY(), anchor.Height() / 2f, width, height, size * 0.4f,
            bounds.CenterX(), bounds.CenterY(), bounds.Width() / 2f, bounds.Height() / 2f, out _);
        var box = new Rect(x - width / 2f, y - height / 2f, width, height);
        var cut = Math.Min(height, size * 1.7f) * 0.22f;
        canvas.Plate(box, cut, Box);
        canvas.Frame(box, cut, MenuLook.LineDim);
        if (wrap) canvas.Paragraph(text, new Rect(x - textWidth / 2f, y - height / 2f + size * 0.25f, textWidth, height - size * 0.5f), size, MenuLook.Text);
        else canvas.Text(text, x, y, size, MenuLook.Text, HudAlign.Centre, room);
    }
}

/// <summary>The tooltip of one menu item (a menu bar button, a button of a menu screen): a <see cref="MenuCanvas"/> on the item, drawn
/// while the pointer is over it (the game's own hover state, <c>CM_Item.m_isHovering</c>, polled by the owner each frame) inside the
/// bounds the owner gives for the item's box.</summary>
internal sealed class MenuItemTooltip
{
    private readonly MenuCanvas _canvas;
    private readonly Rect _box, _bounds;
    private string? _shown;

    private MenuItemTooltip(MenuCanvas canvas, Rect box, Rect bounds)
    {
        _canvas = canvas;
        _box = box;
        _bounds = bounds;
    }

    /// <summary>A tooltip on <paramref name="item"/>; <paramref name="bounds"/> gets the item's box (in its own units) and returns where
    /// the tooltip may go. Null (logged) when the item has no sprite or text to draw with.</summary>
    internal static MenuItemTooltip? On(CM_Item item, Func<Rect, Rect> bounds)
    {
        var sprite = item.GetComponentInChildren<SpriteRenderer>(true);
        var label = item.GetComponentInChildren<TextMeshPro>(true);
        if (sprite == null || label == null)
        {
            Plugin.Error("ui-menu-bar-button-has-no-sprite", "Menu item '" + item.gameObject.name + "' has no sprite or text to draw a tooltip with; it shows none.");
            return null;
        }
        var box = MenuCanvas.BoxIn(item.transform, item.GetComponent<BoxCollider2D>() ?? item.GetComponentInChildren<BoxCollider2D>(true), new Rect(0f, -20f, 160f, 40f));
        return new MenuItemTooltip(new MenuCanvas(item.transform, "ForgeUI.ItemTooltip", sprite, label, MenuCanvas.TopOrder(item) + 5), box, bounds(box));
    }

    /// <summary>Per frame: draws <paramref name="text"/> while <paramref name="hover"/>, clears it otherwise; touches the canvas only on
    /// a change.</summary>
    internal void Show(string? text, bool hover)
    {
        var want = hover && !string.IsNullOrEmpty(text) ? text : null;
        if (want == _shown || !_canvas.Alive) return;
        _shown = want;
        _canvas.Begin();
        if (want != null) MenuTooltip.Draw(_canvas, _box, want, _box.Height() * 0.42f, _bounds);
        _canvas.End();
    }

    internal void Destroy() => _canvas.Destroy();
}
