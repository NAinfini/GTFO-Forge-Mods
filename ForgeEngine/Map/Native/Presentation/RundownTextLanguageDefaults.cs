using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Only the final native publication result supplies defaults. The current expedition selects its
/// Rundown; discovery, slot candidates and failed native publications never supply player text defaults.</summary>
internal sealed class RundownTextLanguageDefaults : IPlayerTextLanguageDefaults
{
    private Dictionary<uint, int> _languages = new();

    internal void Admit(IReadOnlyList<RundownPack> published)
    {
        var languages = new Dictionary<uint, int>();
        foreach (var pack in published)
        {
            if (pack.DefaultTextLanguage == null) continue;
            int language = PlayerTextLanguages.LanguageIndex(pack.DefaultTextLanguage);
            if (language < 0) throw new InvalidOperationException("Invalid published Rundown default text language.");
            languages.Add(pack.RundownId, language);
        }
        _languages = languages;
        Revision++;
    }

    public int? CurrentLanguage()
        => LevelIdentity.Read() is { } level && _languages.TryGetValue(level.RundownId, out var language)
            ? language : null;

    public long Revision { get; private set; }

    internal void Clear() { _languages.Clear(); Revision++; }
}
