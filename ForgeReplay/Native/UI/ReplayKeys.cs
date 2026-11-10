using ForgeUI;

namespace ForgeReplay;

/// <summary>
/// The replay's keys, each an action the player can rebind in the game's settings (Controls, under REPLAY), live only while a replay plays
/// (context <c>replay</c>). The game's own look and move axes are read through the input lease instead, so a key changed in the game's own
/// settings moves the free camera too. Defaults and why: design in <c>DEVELOPERS.md</c> (keys).
/// </summary>
internal static class ReplayKeys
{
    internal const string Context = "replay";

    internal static KeyAction? CameraNext, CameraNextKey, PlayerNext, PlayerPrevious, PlayerNextKey, PlayerPreviousKey, Auto, Free, Above, Reset;
    internal static KeyAction? Play, Back, Forward, Rewind, FastForward, Close, PreviousMark, NextMark, MarkBack, MarkForward, Faster, Slower, FrameBack, FrameForward, ToStart, ToEnd;
    internal static KeyAction? Scores, Chat, Help, HideUi, Rise, Sink, Fast, Enemy, PathMark, PathClear, PathPlay;
    internal static readonly KeyAction?[] Watch = new KeyAction?[4];

    internal static void Load(UIKitMod ui)
    {
        KeyAction? Add(string name, string key, KeyMode mode = KeyMode.Press) => ui.AddKeyAction(name, "keys." + name.ToLowerInvariant(), "keys.group", key, mode, Context);
        CameraNext = Add("CameraNext", "Mouse0");
        CameraNextKey = Add("CameraNextKey", "V");
        PlayerNext = Add("PlayerNext", "Mouse1");
        PlayerPrevious = Add("PlayerPrevious", "Shift+Mouse1");
        PlayerPreviousKey = Add("PlayerPreviousKey", "LeftBracket");
        PlayerNextKey = Add("PlayerNextKey", "RightBracket");
        for (var i = 0; i < 4; i++) Watch[i] = Add("Watch" + (i + 1), "Alpha" + (i + 1));
        Auto = Add("Auto", "Alpha0");
        Free = Add("Free", "F");
        Above = Add("Above", "T");
        Reset = Add("Reset", "Mouse2");
        Play = Add("Play", "Space");
        // A tap of an arrow jumps 5 s (on the key coming up); holding it plays 4x forward, or keeps jumping back.
        Back = Add("Back", "LeftArrow", KeyMode.Tap);
        Forward = Add("Forward", "RightArrow", KeyMode.Tap);
        Rewind = Add("Rewind", "LeftArrow", KeyMode.Hold);
        FastForward = Add("FastForward", "RightArrow", KeyMode.Hold);
        PreviousMark = Add("PreviousMark", "Shift+LeftArrow");
        NextMark = Add("NextMark", "Shift+RightArrow");
        MarkBack = Add("MarkBack", "Mouse3");
        MarkForward = Add("MarkForward", "Mouse4");
        Faster = Add("Faster", "UpArrow");
        Slower = Add("Slower", "DownArrow");
        FrameBack = Add("FrameBack", "Comma");
        FrameForward = Add("FrameForward", "Period");
        ToStart = Add("ToStart", "Home");
        ToEnd = Add("ToEnd", "End");
        Scores = Add("Scores", "Tab");
        Chat = Add("Chat", "Return");
        Help = Add("Help", "Slash");
        HideUi = Add("HideUi", "F1");
        Rise = Add("Rise", "E");
        Sink = Add("Sink", "Q");
        Fast = Add("Fast", "LeftShift");
        // G watches the enemy in the middle of the view, or the next one near (hunting and big ones first); K marks a stop of the film camera's
        // path in the free camera, Shift+K clears the path, L flies it with the screen hidden (again: stops).
        Enemy = Add("Enemy", "G");
        PathMark = Add("PathMark", "K");
        PathClear = Add("PathClear", "Shift+K");
        PathPlay = Add("PathPlay", "L");
        // Esc: a tap closes the top layer (ReplayScreen), held a second leaves the replay; the game's menu key is held off meanwhile.
        Close = Add("Close", "Escape");
        ui.TakeGameInput(Context);
    }

    internal static bool Pressed(KeyAction? action) => action != null && action.Pressed;
    internal static bool Held(KeyAction? action) => action != null && action.Held;
    internal static bool Released(KeyAction? action) => action != null && action.Released;

    // The key of an action whatever modifiers are held, for the flight keys: Shift (faster) is held with them, and a chord matches only
    // with exactly its own modifiers. Worked out again only when a key is changed.
    private static readonly System.Collections.Generic.Dictionary<KeyAction, (string Key, UnityEngine.KeyCode Code)> Codes = new();

    internal static bool HeldAnyModifiers(KeyAction? action)
    {
        if (action == null) return false;
        if (action.Held) return true;
        var key = action.Chord.Key;
        if (!Codes.TryGetValue(action, out var known) || !ReferenceEquals(known.Key, key))
        {
            known = (key, System.Enum.TryParse<UnityEngine.KeyCode>(key, out var code) ? code : UnityEngine.KeyCode.None);
            Codes[action] = known;
        }
        return known.Code != UnityEngine.KeyCode.None && UnityEngine.Input.GetKey(known.Code);
    }

    /// <summary>The key's name as the game writes keys, for the hints and the keys list.</summary>
    internal static string Name(KeyAction? action) => action?.Name ?? "";
}
