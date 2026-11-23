using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Localization;

namespace ForgeEngine.Presentation.Native;

/// <summary>The two HUD rows' shared reads: this client's player text language, and the text a row's two text
/// entries name.</summary>
internal sealed class HudLanguage
{
    /// <summary>The language this client reads in, or the code that names why it cannot be read: the Player domain
    /// is not running (<see cref="PlayerTextLanguageService.MissingCode"/>), refused the read, or threw.</summary>
    internal bool TryRead(out Language language, out string code)
    {
        language = default;
        if (ForgePlayer.Native.PlayerHost.Session?.TextLanguage is not { } provider)
        {
            code = PlayerTextLanguageService.MissingCode;
            return false;
        }
        try
        {
            language = provider.CurrentLanguage;
            code = "";
            return true;
        }
        catch (RuntimeContractException error)
        {
            code = error.Code;
            return false;
        }
        catch (Exception contained)
        {
            PresentationLog.Contained("hud-text.language", contained);
            code = ResultCode.PlayerTextLanguageUnavailable;
            return false;
        }
    }
}

/// <summary>The one two-entry text read of a text row: the literal a plan computed, or the id of the original Text
/// block its id entry named. The stored block wins when both are filled (the card's own precedence); a stored entry
/// of another kind, or whose id is not a block id, is refused by name rather than folded into the literal.</summary>
internal static class HudTextEntry
{
    internal const string ResourceKind = "text";

    /// <summary>The text as a reference <see cref="ForgeUI.GameText"/> resolves: the stored block as a reference
    /// carrying the English primary column, the literal as itself (empty when the plan computed none).</summary>
    internal static bool TryRead(CommandContext context, HandlerSlot literal, HandlerSlot stored,
        out string text, out string? missing)
    {
        if (context.TryResource(stored, out string kind, out string storedId))
        {
            missing = null;
            if (kind == ResourceKind
                && uint.TryParse(storedId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint id))
            {
                text = ForgeUI.GameText.ReferencePrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@English";
                return true;
            }
            text = "";
            missing = ResultCode.TextResourceMissing;
            return false;
        }
        text = context.Text(literal) ?? "";
        missing = null;
        return true;
    }
}
