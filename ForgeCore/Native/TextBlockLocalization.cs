using GameData;
using Localization;

namespace ForgeCore;

public static partial class NativeDataBlockTables
{
    // Text.Get reads GameDataTextLocalizationService.m_texts, which SetCurrentLanguage fills from every Text block's
    // GetText(language) and only when the language changes (build 20403457: it returns early for the same language). A
    // Text block published after the language was chosen is therefore missing from Text.Get until the next change. Put
    // the same column into that lookup now (a replaced block overwrites its entry); the next language change reads every block
    // again, this one included. A removed block leaves the lookup the same way.
    static partial void Published(object block)
    {
        if (block is not TextDataBlock text || CurrentService() is not { } service) return;
        service.m_texts[text.persistentID] = text.GetText(service.CurrentLanguage, false);
    }

    static partial void Withdrawn(object block)
    {
        if (block is TextDataBlock text && CurrentService() is { } service) service.m_texts.Remove(text.persistentID);
    }

    private static GameDataTextLocalizationService? CurrentService()
    {
        var service = Localization.Text.TextLocalizationService?.TryCast<GameDataTextLocalizationService>();
        return service == null || service.CurrentLanguage == 0 ? null : service;
    }
}
