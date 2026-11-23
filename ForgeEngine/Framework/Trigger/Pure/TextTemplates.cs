using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForgeEngine.Framework;

/// <summary>The one level-text template: a sentence with up to <see cref="SlotCount"/> placeholders `{1}`…`{8}`, each
/// filled from one typed value. The template node (`forge.modifier.value.text`) packs its values with
/// <see cref="Encode"/>; whoever shows the text fills the template with <see cref="TryFill"/> — immediately for a plain
/// template, and on each client in its own language column for a game Text block reference, so a translated template
/// and the option names written in it follow every player's language.
///
/// A placeholder may carry a spec after a colon, written in the template so each language column writes its own:
/// a number takes a .NET custom numeric format (`{1:0.00}`, `{1:0%}`; none is the shortest invariant form), a flag
/// takes `true text|false text` (`{2:开|关}`; none is `1`/`0`), and an option value takes its set's member names in
/// member order (`{3:已关闭|已打开|已上锁}`). `{{` and `}}` are literal braces.</summary>
internal static class TextTemplates
{
    public const int SlotCount = 8;

    /// <summary>One packed value: its kind tag and its invariant payload.</summary>
    public readonly record struct Value(char Kind, string Payload)
    {
        public static Value Number(double value) => new('n', (value == 0d ? 0d : value).ToString("R", CultureInfo.InvariantCulture));
        public static Value Text(string value) => new('s', value);
        public static Value Flag(bool value) => new('b', value ? "1" : "0");
        public static Value Option(int index) => new('e', index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The args suffix a reference carries: one field per slot, empty for a slot nothing was wired to.
    /// `;` separates fields and `\` escapes `;` and `\` inside text.</summary>
    public static string Encode(IReadOnlyList<Value?> values)
    {
        var builder = new StringBuilder();
        // Trailing empty slots say nothing, so the suffix stops at the last wired one.
        var last = values.Count - 1;
        while (last >= 0 && values[last] is null) last--;
        for (var slot = 0; slot <= last; slot++)
        {
            if (slot > 0) builder.Append(';');
            if (values[slot] is not { } value) continue;
            builder.Append(value.Kind);
            foreach (var character in value.Payload)
            {
                if (character is ';' or '\\') builder.Append('\\');
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    /// <summary>Reads an args suffix back into one value per slot; false when the suffix is not one this side wrote.</summary>
    public static bool TryDecode(string encoded, out Value?[] values)
    {
        values = new Value?[SlotCount];
        var slot = 0;
        var field = new StringBuilder();
        char kind = '\0';
        bool started = false;
        for (var index = 0; index <= encoded.Length; index++)
        {
            if (index == encoded.Length || encoded[index] == ';')
            {
                if (slot >= SlotCount) return false;
                if (started) values[slot] = new Value(kind, field.ToString());
                slot++; field.Clear(); started = false;
                continue;
            }
            var character = encoded[index];
            if (!started)
            {
                if (character is not ('n' or 's' or 'b' or 'e')) return false;
                kind = character; started = true;
                continue;
            }
            if (character == '\\')
            {
                if (++index == encoded.Length) return false;
                character = encoded[index];
            }
            field.Append(character);
        }
        return true;
    }

    /// <summary>Fills every placeholder of <paramref name="template"/>. False, with the reason, when a placeholder
    /// names a slot outside 1..8 or one nothing was wired to, when a spec does not fit its value, or when a brace is
    /// unbalanced; the text is never shown with a placeholder left in it.</summary>
    public static bool TryFill(string template, IReadOnlyList<Value?> values, out string text, out string reason)
    {
        var builder = new StringBuilder(template.Length + 16);
        text = ""; reason = "";
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}') { builder.Append('}'); index++; continue; }
                reason = "unbalanced-brace"; return false;
            }
            if (character != '{') { builder.Append(character); continue; }
            if (index + 1 < template.Length && template[index + 1] == '{') { builder.Append('{'); index++; continue; }
            var close = template.IndexOf('}', index + 1);
            if (close < 0) { reason = "unbalanced-brace"; return false; }
            var body = template.Substring(index + 1, close - index - 1);
            var colon = body.IndexOf(':');
            var slotText = colon < 0 ? body : body.Substring(0, colon);
            var spec = colon < 0 ? null : body.Substring(colon + 1);
            if (!int.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot < 1 || slot > SlotCount)
            { reason = "placeholder-slot"; return false; }
            if (slot > values.Count || values[slot - 1] is not { } value) { reason = "placeholder-unwired"; return false; }
            if (!TryRender(value, spec, out var shown)) { reason = "placeholder-spec"; return false; }
            builder.Append(shown);
            index = close;
        }
        text = builder.ToString();
        return true;
    }

    /// <summary>Whether the template places at least one value; a template that places none drops every wire.</summary>
    public static bool PlacesValue(string template)
    {
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '{') continue;
            if (index + 1 < template.Length && template[index + 1] == '{') { index++; continue; }
            return true;
        }
        return false;
    }

    private static bool TryRender(Value value, string? spec, out string shown)
    {
        shown = "";
        switch (value.Kind)
        {
            case 'n':
                if (!double.TryParse(value.Payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    return false;
                try { shown = spec is null ? number.ToString(CultureInfo.InvariantCulture) : number.ToString(spec, CultureInfo.InvariantCulture); }
                catch (FormatException) { return false; }
                return true;
            case 's':
                shown = value.Payload;
                return spec is null;
            case 'b':
                if (spec is null) { shown = value.Payload; return true; }
                return Pick(spec, value.Payload == "1" ? 0 : 1, 2, out shown);
            case 'e':
                return spec is not null && int.TryParse(value.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var member)
                    && Pick(spec, member, -1, out shown);
            default:
                return false;
        }
    }

    /// <summary>One `|`-separated name; <paramref name="count"/> is the exact number of names the spec must hold, or -1
    /// for any number that covers the index.</summary>
    private static bool Pick(string spec, int index, int count, out string shown)
    {
        shown = "";
        var names = spec.Split('|');
        if (count >= 0 && names.Length != count || index < 0 || index >= names.Length) return false;
        shown = names[index];
        return true;
    }
}
