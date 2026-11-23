using System;
using System.Globalization;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>The game-free half of the `hud_value` row: how one value is written as a line, and the colour grammar.</summary>
internal static class HudValueFormat
{
    /// <summary>The one line a value is rendered as. `number_of_max` and `percent` need the maximum the plan
    /// supplied; a plan that asked for either without one gets the number alone rather than a division by a
    /// maximum nobody named.</summary>
    internal static string Line(string form, double value, double maximum, string? label)
    {
        string body = form switch
        {
            "number_of_max" => maximum > 0
                ? value.ToString("0.##", CultureInfo.InvariantCulture) + " / " + maximum.ToString("0.##", CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            "percent" => maximum > 0
                ? Math.Round(value / maximum * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            "time" => ForgeUI.HudClock.Text(value),
            _ => value.ToString("0.##", CultureInfo.InvariantCulture)
        };
        return string.IsNullOrEmpty(label) ? body : label + " " + body;
    }

    /// <summary>What a `screen` element writes: a label with a placeholder says for itself where the value goes; otherwise the
    /// value follows the label the way the form reads, and a bar, row of cells or clock draws it itself. `text` is the label over
    /// the body.</summary>
    internal static string ScreenText(string form, string? label, string? body, double maximum)
    {
        label ??= "";
        if (form == "text") return string.IsNullOrEmpty(body) ? label : label.Length == 0 ? body! : label + "\n" + body;
        if (ForgeUI.HudPlacedText.FirstPlaceholder(label) >= 0) return label;
        string shown = form switch
        {
            "number_of_max" when maximum > 0 => "{value} / {max}",
            "percent" when maximum > 0 => "{percent}",
            "number" or "number_of_max" or "percent" => "{value}",
            _ => ""
        };
        return shown.Length == 0 ? label : label.Length == 0 ? shown : label + " " + shown;
    }

    /// <summary>The status bar is one row tall: its line never wraps.</summary>
    internal static string SingleLine(string line) => line.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>One `#rrggbb` colour. Absent means "leave the game's own colour alone"; a present value that is
    /// not that grammar is refused rather than parsed into a colour nobody asked for.</summary>
    internal static bool TryColor(string? text, out Color? color)
    {
        color = null;
        if (string.IsNullOrEmpty(text)) return true;
        string hex = text.StartsWith("#", StringComparison.Ordinal) ? text.Substring(1) : text;
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed)) return false;
        color = new Color(((packed >> 16) & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, (packed & 0xFF) / 255f, 1f);
        return true;
    }
}
