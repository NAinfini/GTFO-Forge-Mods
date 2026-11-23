using Localization;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Every card text is resolved through the addressed client's player text language.</summary>
internal static class TextCardValue
{
    internal const string InvalidCode = "map-localized-text-invalid";

    /// <summary>The native localized literal carrying an already rendered text (the library's text service builds
    /// it); a refusal is recorded in the Map log before it propagates. A terminal log passes its content identity
    /// (<see cref="TerminalLogReading.IdFor"/>); every other card text carries none.</summary>
    internal static LocalizedText FromRendered(string rendered, uint id = 0)
    {
        try { return ForgeUI.GameText.Localized(rendered, id); }
        catch (System.Exception error)
        {
            MapLog.Refused(new MapDiagnostic(InvalidCode, error.Message));
            throw;
        }
    }
    internal static string Reference(TextResourceContract.TextValue text)
        => text.Stored ? TextResourceContract.ReferencePrefix
            + text.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@English" : text.Literal ?? string.Empty;

    internal static bool TryLocalized(TextResourceContract.TextValue text, IForgeEngine? kernel, out LocalizedText localized)
    {
        if (StationTextValue.TryRender(Reference(text), kernel, out var rendered))
        {
            localized = FromRendered(rendered);
            return true;
        }
        localized = default!;
        return false;
    }

    internal static LocalizedText Localized(TextResourceContract.TextValue text, IForgeEngine? kernel)
        => TryLocalized(text, kernel, out var localized) ? localized
            : throw new RuntimeContractException(ResultCode.TextResourceMissing, "The requested display text is unavailable.");
}
