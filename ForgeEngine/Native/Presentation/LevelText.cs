using System;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;
using ForgeUI;
using Localization;

namespace ForgeEngine.Presentation;

/// <summary>
/// Shows one level text in one language: a plain literal as itself, a game Text block reference as its column in that
/// language, and a reference the template node packed values onto (`forge:text:{id}@{Primary}:{values}`) as that column
/// filled with the values by the one template formatter (<see cref="TextTemplates"/>). Every display that shows author
/// text — HUD lines, HUD values, world signs, model text — reads it here, so a translated template and the option
/// names written in it follow each player's language the same way everywhere.
/// </summary>
internal static class LevelText
{
    public static bool TryRender(string literal, Language language, out string rendered)
        => TryRender(literal, language, false, out rendered, out _, out _);

    public static bool TryRender(string literal, Language language, out string rendered, out Exception? error)
        => TryRender(literal, language, false, out rendered, out _, out error);

    /// <summary>As <see cref="TryRender(string, Language, out string)"/>, but an authored empty column is true with
    /// <paramref name="empty"/> set instead of a missing text.</summary>
    public static bool TryRenderOptional(string literal, Language language, out string rendered, out bool empty, out Exception? error)
        => TryRender(literal, language, true, out rendered, out empty, out error);

    private static bool TryRender(string literal, Language language, bool allowEmpty, out string rendered, out bool empty, out Exception? error)
    {
        empty = false;
        error = null;
        rendered = literal;
        if (!GameText.IsReference(literal)) return true;
        var separator = literal.IndexOf(':', TextNodes.ReferencePrefix.Length);
        var reference = separator < 0 ? literal : literal.Substring(0, separator);
        if (!GameText.TryRenderOptional(reference, language, out var column, out empty, out error)) { rendered = ""; return false; }
        if (empty) { rendered = ""; return allowEmpty; }
        if (separator < 0) { rendered = column; return true; }
        rendered = "";
        return TextTemplates.TryDecode(literal.Substring(separator + 1), out var values)
            && TextTemplates.TryFill(column, values, out rendered, out _);
    }
}
