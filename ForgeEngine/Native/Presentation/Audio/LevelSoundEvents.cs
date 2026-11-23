using System;
using GameData;
using Localization;

namespace ForgeEngine.Presentation.Native;

/// <summary>The game's own level-event system for the sound rows: one `PlaySound` event run through
/// `WorldEventManager.ExecuteEvent`, with the "check no condition" gate written explicitly because a freshly built
/// event carries the condition struct's zero value, a gate no vanilla event asks for.</summary>
internal static class LevelSoundEvents
{
    private const int NoCondition = -1;

    internal static bool Available => WorldEventManager.Current != null;

    internal static void Post(uint sound, string? filter, string? subtitle)
    {
        var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.PlaySound };
        data.Condition = new WorldEventConditionPair { ConditionIndex = NoCondition };
        if (sound != 0) data.SoundID = sound;
        if (!string.IsNullOrEmpty(subtitle)) data.SoundSubtitle = Literal(subtitle);
        if (!string.IsNullOrEmpty(filter)) data.WorldEventObjectFilter = filter;
        WorldEventManager.ExecuteEvent(data, 0f);
    }

    /// <summary>The interop string constructor invokes the value-type constructor on an unallocated wrapper. Only the
    /// parameterless constructor boxes a fresh native value, so all three fields are written explicitly and read back.</summary>
    private static LocalizedText Literal(string text)
    {
        var localized = new LocalizedText();
        localized.UntranslatedText = text;
        localized.Id = 0;
        localized.OldId = 0;
        if (localized.UntranslatedText != text || localized.Id != 0 || localized.OldId != 0)
            throw new InvalidOperationException("The native localized literal did not retain its text and zero translation ids.");
        return localized;
    }
}
