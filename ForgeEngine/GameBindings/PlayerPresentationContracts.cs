using System;
using ForgeEngine.Framework;
using Localization;

namespace ForgeEngine.GameBindings;

internal static class PlayerTextLanguageService
{
    public const string MissingCode = "player-text-language-missing";

    public static bool TryLanguage(string? member, out Language language)
        => TryLanguage(PlayerTextLanguages.LanguageIndex(member), out language);

    public static bool TryLanguage(int index, out Language language)
    {
        language = default;
        return PlayerTextLanguages.LanguageName(index) is { } member
            && Enum.TryParse(member, false, out language) && Enum.IsDefined(typeof(Language), language);
    }
}
