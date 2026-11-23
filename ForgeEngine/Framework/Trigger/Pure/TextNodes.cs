using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The text family's implementation: the `g-text` row fills one template sentence with up to eight typed
/// values (<see cref="TextTemplates"/>), and the fragment row fills one native text fragment's placeholder in a template
/// from a value.
///
/// A plain template is filled here, once. A template that is a game Text block reference (`forge:text:{id}@{Primary}`)
/// is not: its sentence is a different column in every language, so the row appends the packed values to the
/// reference and every client fills its own column where the text is shown. Either way the answer is held to the
/// rules a string port carries text by, and a template the row cannot fill is refused in this family's own terms
/// instead of arriving at the far side of the plan as a port error.</summary>
internal static class TextNodes
{
    /// <summary>The longest text a kernel string port can carry; the same bound the framework enforces on every
    /// string it validates, applied here so the reason is this family's own code.</summary>
    public const int MaximumCharacters = 256;

    /// <summary>The prefix of a game Text block reference: the grammar the website lowers author text to and the
    /// library's text service resolves per language.</summary>
    public const string ReferencePrefix = "forge:text:";

    /// <summary>One line of text: the template with its placeholders filled from <paramref name="values"/>, one
    /// entry per slot (null where nothing was wired).</summary>
    public static string Compose(string template, IReadOnlyList<TextTemplates.Value?> values)
    {
        if (template is null)
            throw new RuntimeContractException("pure-text-template", "A text template must not be null.");
        if (template.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            // A reference that already carries values was composed by another template row: filling it twice would
            // drop the first row's values.
            if (template.IndexOf(':', ReferencePrefix.Length) >= 0)
                throw new RuntimeContractException("pure-text-template", "The template reference already carries values.");
            var packed = TextTemplates.Encode(values);
            return Carry(packed.Length == 0 ? template : template + ":" + packed);
        }
        // Every text this row answers places a value: a template that places none would silently drop a wired
        // input, which is a wiring error rather than an answer.
        if (!TextTemplates.PlacesValue(template))
            throw new RuntimeContractException("pure-text-template", "The template does not place a value.");
        if (!TextTemplates.TryFill(template, values, out var text, out var reason))
            throw new RuntimeContractException("pure-text-template", "The template cannot be filled: " + reason);
        return Carry(text);
    }

    /// <summary>The fragment row: every occurrence of one native fragment's marker is replaced with the value. The
    /// marker's literal spelling is the native fragment enum member's own name in square brackets — the game's own
    /// objective field description names its macros `[ITEM_SERIAL]` and `[ITEM_ZONE]` — so the name the structural
    /// parameter already carries is the whole marker. A template without that marker is returned unchanged rather than
    /// refused: a fragment the template does not use places no value and drops no wired input. The answer is held to
    /// the same three rules a string port validates text with, through the same <see cref="Carry"/> the text row
    /// uses.</summary>
    public static string ReplaceFragment(string template, string fragment, string value)
    {
        if (template is null)
            throw new RuntimeContractException("pure-text-template", "A text template must not be null.");
        if (fragment is null || value is null)
            throw new RuntimeContractException("pure-text-template", "A fragment and the value that fills it must not be null.");
        return Carry(template.Replace("[" + fragment + "]", value));
    }

    /// <summary>Whether one text is what a string port carries, answered by the same three rules the framework
    /// validates strings with: no control characters, a length of 1..256, and no surrounding whitespace.</summary>
    private static string Carry(string text)
    {
        foreach (var character in text)
            if (character < 32 || character == 127)
                throw new RuntimeContractException("pure-text-encoding", "Text must not contain control characters.");
        if (text.Length is 0 or > MaximumCharacters || text.Trim() != text)
            throw new RuntimeContractException("pure-text-range", "Text must be 1.." + MaximumCharacters + " trimmed characters.");
        return text;
    }
}
