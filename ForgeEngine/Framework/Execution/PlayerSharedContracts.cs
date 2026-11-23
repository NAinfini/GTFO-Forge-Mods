using System;

namespace ForgeEngine.Framework;

/// <summary>The admitted Rundown's text language; null explicitly means the Rundown did not choose one.</summary>
internal interface IPlayerTextLanguageDefaults
{
    int? CurrentLanguage();

    /// <summary>Goes up whenever the admitted Rundowns' defaults change, so a reader that keeps the answer knows to ask again.</summary>
    long Revision { get; }
}

/// <summary>The canonical text-language vocabulary shared by content and player presentation.</summary>
internal static class PlayerTextLanguages
{
    public static int LanguageIndex(string? member)
    {
        var languages = RuntimeEnumSets.Members("text_language");
        for (int index = 0; index < languages.Count; index++)
            if (string.Equals(languages[index], member, StringComparison.Ordinal)) return index;
        return -1;
    }

    public static string? LanguageName(int index)
    {
        var languages = RuntimeEnumSets.Members("text_language");
        return index >= 0 && index < languages.Count ? languages[index] : null;
    }
}
