using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UInput = UnityEngine.Input;

namespace ForgeUI;

/// <summary>
/// One registered key action (see <see cref="UIKitMod.AddKeyAction"/>). Read <see cref="Pressed"/>, <see cref="Held"/>,
/// <see cref="Released"/> or <see cref="On"/> every frame: the keys are read once per frame for all actions together, and reading
/// costs no lookup and no allocation. The player picks the key in the game's settings (Controls); <see cref="Name"/> is what to show
/// on a key prompt. While the settings read a new key no action is down.
/// </summary>
public sealed class KeyAction
{
    internal KeyAction(UIKitMod mod, int index)
    {
        Mod = mod;
        Index = index;
    }

    internal readonly UIKitMod Mod;
    internal readonly int Index;
    internal KeyPulse Pulse;
    internal int Code, OwnMod;
    private int _driven; // 0 = the real key, 1 = driven down, 2 = driven up for one more read
    internal string? ShownName;
    internal int ShownVersion = -1;
    internal string ShownLanguage = "";

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id => KeyBinds.Book[Index].FullId;

    public KeyMode Mode => KeyBinds.Book[Index].Mode;

    /// <summary>The key the action has now (the player's, else the default); <see cref="KeyChord.None"/> when unbound.</summary>
    public KeyChord Chord => KeyBinds.Book[Index].Effective;

    /// <summary>True on the frame the key goes down.</summary>
    public bool Pressed { get { KeyBinds.Sync(); return Pulse.Pressed; } }

    /// <summary>True while the key is down.</summary>
    public bool Held { get { KeyBinds.Sync(); return Pulse.Held; } }

    /// <summary>True on the frame the key comes up.</summary>
    public bool Released { get { KeyBinds.Sync(); return Pulse.Released; } }

    /// <summary>A <see cref="KeyMode.Toggle"/> action's state, flipped at every press; set it to switch it yourself (for example off
    /// when the level ends).</summary>
    public bool On
    {
        get { KeyBinds.Sync(); return Pulse.On; }
        set => Pulse.On = value;
    }

    /// <summary>The key as the game names keys (<c>Ctrl+G</c>, the game's own mouse button names), or the "unbound" text. Cached: it
    /// changes only when the key or the language does.</summary>
    public string Name => KeyBinds.NameOf(this);

    /// <summary>For drivers and tests: reads the action as down, with a fresh press edge, until <see cref="ReleaseDriven"/>, without
    /// any key event. Works whatever key the action has, also none.</summary>
    public void HoldDriven() => _driven = 1;

    /// <summary>Ends <see cref="HoldDriven"/>: the action reads one released edge, then follows the real key again.</summary>
    public void ReleaseDriven() { if (_driven == 1) _driven = 2; }

    internal bool Driven => _driven != 0;

    internal bool DrivenDown()
    {
        var down = _driven == 1;
        if (_driven == 2) _driven = 0;
        return down;
    }

    /// <summary>Changes whenever any action's key changes, for redrawing key names you keep.</summary>
    public static int Version => KeyBinds.Book.Version;
}

/// <summary>Every key action of every mod: the book (defaults and the player's keys, saved in
/// <c>BepInEx/config/NAinfini.ForgeUI.keys.cfg</c>), the once-per-frame key read and the key capture the settings use.</summary>
internal static class KeyBinds
{
    internal static readonly KeyBindBook Book = new(Known);
    internal static readonly List<KeyAction> Actions = new();
    internal static readonly KeyCapture Capture = new();
    private static string? _path;
    private static int _frame = -1, _codesVersion = -1;
    private static KeyCode[] _candidates = Array.Empty<KeyCode>();
    private static readonly KeyCode[] Modifiers =
        { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr };

    /// <summary>The canonical spelling of a key code name, or null. Joystick buttons are not keys here (keyboard and mouse only).</summary>
    internal static string? Known(string name)
    {
        if (!Enum.TryParse<KeyCode>(name, true, out var key) || !Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.None) return null;
        var text = key.ToString();
        return text.StartsWith("Joystick", StringComparison.Ordinal) ? null : text;
    }

    /// <summary>Reads the settings file once, when ForgeUI loads.</summary>
    internal static void Load(string configFolder)
    {
        _path = Path.Combine(configFolder, Plugin.Id + ".keys.cfg");
        if (!File.Exists(_path)) return;
        try
        {
            var problems = new List<string>();
            Book.Load(File.ReadAllText(_path), problems);
            foreach (var problem in problems) Plugin.Error("Key bindings file " + _path + ": " + problem + "; that line is ignored.");
        }
        catch (Exception error) { Plugin.Error("Key bindings file " + _path + " could not be read; every action uses its default key: " + error); }
    }

    private static void Save()
    {
        if (_path == null) return;
        try { File.WriteAllText(_path, Book.Save()); }
        catch (Exception error) { Plugin.Error("Key bindings could not be saved to " + _path + ": " + error); }
    }

    internal static KeyAction? Add(UIKitMod mod, string name, string nameKey, string groupKey, string defaultKey, KeyMode mode, string context)
    {
        PatchGroups.Ensure(PatchGroup.Settings);
        var index = Book.Register(mod.Id, name, nameKey, groupKey, defaultKey, mode, context, out var problem);
        if (index < 0)
        {
            Plugin.Error(mod.Id + ": key action not added: " + problem + ".");
            return null;
        }
        var action = new KeyAction(mod, index);
        Actions.Add(action);
        return action;
    }

    internal static void Set(int index, KeyChord chord)
    {
        var entry = Book[index];
        if (chord == entry.Effective) return;
        Book.Set(index, chord);
        Save();
    }

    internal static void ResetAll()
    {
        var changed = false;
        for (var i = 0; i < Book.Count; i++) changed |= Book[i].Custom != null;
        if (!changed) return;
        Book.ResetAll();
        Save();
    }

    /// <summary>Once per frame, on the first read: every action's key state.</summary>
    internal static void Sync()
    {
        var frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        if (_codesVersion != Book.Version) Codes();
        if (Actions.Count == 0) return;
        var blocked = Capture.Active;
        var held = blocked ? KeyMods.None : HeldMods();
        for (var i = 0; i < Actions.Count; i++)
        {
            var action = Actions[i];
            var mode = Book[action.Index].Mode;
            if (action.Driven) { action.Pulse.Step(action.DrivenDown(), mode); continue; }
            if (blocked || action.Code == 0) { action.Pulse.Step(false, mode); continue; }
            var down = UInput.GetKey((KeyCode)action.Code) && (held & ~(KeyMods)action.OwnMod) == Book[action.Index].Effective.Mods;
            action.Pulse.Step(down, mode);
        }
    }

    /// <summary>Per frame from the driver, so held keys are tracked even when no mod reads them this frame.</summary>
    internal static void Update()
    {
        Sync();
    }

    private static KeyMods HeldMods()
    {
        var mods = KeyMods.None;
        if (UInput.GetKey(KeyCode.LeftControl) || UInput.GetKey(KeyCode.RightControl)) mods |= KeyMods.Ctrl;
        if (UInput.GetKey(KeyCode.LeftShift) || UInput.GetKey(KeyCode.RightShift)) mods |= KeyMods.Shift;
        if (UInput.GetKey(KeyCode.LeftAlt) || UInput.GetKey(KeyCode.RightAlt) || UInput.GetKey(KeyCode.AltGr)) mods |= KeyMods.Alt;
        return mods;
    }

    /// <summary>The key code of every action, worked out again when a key changes (not per frame).</summary>
    private static void Codes()
    {
        _codesVersion = Book.Version;
        foreach (var action in Actions)
        {
            var chord = Book[action.Index].Effective;
            action.Code = chord.IsNone || !Enum.TryParse<KeyCode>(chord.Key, out var key) ? 0 : (int)key;
            action.OwnMod = (int)KeyChords.ModOf(chord.Key);
            action.Pulse.Drop();
        }
    }

    internal static string NameOf(KeyAction action)
    {
        var language = UITexts.Language;
        if (action.ShownName != null && action.ShownVersion == Book.Version && action.ShownLanguage == language) return action.ShownName;
        action.ShownVersion = Book.Version;
        action.ShownLanguage = language;
        return action.ShownName = Display(Book[action.Index].Effective);
    }

    /// <summary>A chord as the player reads it: modifiers, then the key as the game names it.</summary>
    internal static string Display(KeyChord chord)
    {
        if (chord.IsNone) return UIKit.Mod(Plugin.Id).Text("keys.none");
        var text = "";
        if ((chord.Mods & KeyMods.Ctrl) != 0) text += "Ctrl+";
        if ((chord.Mods & KeyMods.Shift) != 0) text += "Shift+";
        if ((chord.Mods & KeyMods.Alt) != 0) text += "Alt+";
        return text + (Enum.TryParse<KeyCode>(chord.Key, out var key) ? UIKitKeys.Name(key) : chord.Key);
    }

    // ---- capture --------------------------------------------------------------------------------------------------

    internal static void BeginCapture()
    {
        if (_candidates.Length == 0) _candidates = Candidates();
        Capture.Begin();
        for (var i = 0; i < Actions.Count; i++) Actions[i].Pulse.Drop();
    }

    /// <summary>One frame of a running capture; the result says when it is over.</summary>
    internal static KeyCapture.Result StepCapture()
    {
        string? down = null, modDown = null, modUp = null;
        if (UInput.anyKeyDown)
            foreach (var key in _candidates)
                if (UInput.GetKeyDown(key)) { down = key.ToString(); break; }
        foreach (var key in Modifiers)
        {
            if (modDown == null && UInput.GetKeyDown(key)) modDown = key.ToString();
            if (modUp == null && UInput.GetKeyUp(key)) modUp = key.ToString();
        }
        return Capture.Step(UInput.anyKey, down, modDown, modUp, HeldMods());
    }

    /// <summary>Every keyboard key and mouse button except modifiers and joystick buttons, in key code order.</summary>
    private static KeyCode[] Candidates()
    {
        var keys = new List<KeyCode>();
        foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
        {
            if (key == KeyCode.None || Array.IndexOf(Modifiers, key) >= 0) continue;
            if (key.ToString().StartsWith("Joystick", StringComparison.Ordinal) || keys.Contains(key)) continue;
            keys.Add(key);
        }
        return keys.ToArray();
    }
}

/// <summary>The game's own keyboard and mouse bindings, read from its input map at most once a second.</summary>
internal static class GameKeys
{
    internal readonly struct Binding
    {
        internal Binding(InputAction action, bool negative, KeyCode key)
        {
            Action = action;
            Negative = negative;
            Key = key;
        }

        internal readonly InputAction Action;
        internal readonly bool Negative;
        internal readonly KeyCode Key;
    }

    private static readonly List<Binding> Read = new();
    private static float _readAt = float.NegativeInfinity;

    /// <summary>The bindings, re-read when a second has passed (the player may have changed one in the settings).</summary>
    internal static List<Binding> All()
    {
        var now = Time.unscaledTime;
        if (now - _readAt < 1f && Read.Count > 0) return Read;
        _readAt = now;
        Read.Clear();
        try
        {
            var map = InputMapper.Current?.m_currentKeyMouseMap;
            if (map == null) return Read;
            for (var i = 0; i < map.Length; i++)
            {
                var data = map[i];
                if (data == null) continue;
                if (data.type == InputType.AxisTwoInput)
                {
                    Add(data.action, false, data.axisPosIsMouseButton ? Mouse(data.axisPosMouseButtonIndex) : data.axisPosKeycode);
                    Add(data.action, true, data.axisNegIsMouseButton ? Mouse(data.axisNegMouseButtonIndex) : data.axisNegKeycode);
                }
                else if (data.device == InputMapperDevice.Keyboard) Add(data.action, false, data.keycode);
                else if (data.device == InputMapperDevice.Mouse) Add(data.action, false, Mouse(data.mouseButtonIndex));
            }
        }
        catch (Exception error) { Plugin.Error("The game's key bindings could not be read: " + error); }
        return Read;
    }

    private static KeyCode Mouse(int index) => index is >= 0 and <= 6 ? KeyCode.Mouse0 + index : KeyCode.None;

    private static void Add(InputAction action, bool negative, KeyCode key)
    {
        if (key != KeyCode.None) Read.Add(new Binding(action, negative, key));
    }

    /// <summary>The key of a game action (an axis' negative side with <paramref name="negative"/>), or None.</summary>
    internal static KeyCode Of(InputAction action, bool negative)
    {
        var all = All();
        for (var i = 0; i < all.Count; i++)
            if (all[i].Action == action && all[i].Negative == negative) return all[i].Key;
        return KeyCode.None;
    }
}

public sealed partial class UIKitMod
{
    /// <summary>
    /// Registers a key action the player can rebind in the game's settings (Controls, under your group's heading). Read it every frame
    /// through the handle. Keys the player never changed follow <paramref name="defaultKey"/>, also when you change the default in a
    /// later version. Two actions on one key in overlapping contexts, or on a key the game itself uses, are shown in the settings and
    /// logged; ForgeUI never moves anyone's key. Use the game's own bindings (<see cref="InputAction"/>) for what the game already has a
    /// key for, rather than a second action.
    /// </summary>
    /// <param name="name">Unique and stable inside your mod (the saved key is stored under it), for example <c>Exit</c>.</param>
    /// <param name="nameKey">Your language file key for the action's name in the settings.</param>
    /// <param name="groupKey">Your language file key for the heading the action is listed under.</param>
    /// <param name="defaultKey">A Unity key code name with optional modifiers: <c>G</c>, <c>Mouse3</c>, <c>Ctrl+Shift+G</c>, or <c>None</c>.</param>
    /// <param name="mode">Press, hold or toggle.</param>
    /// <param name="context">When the action is live, for example <c>exosuit</c>; actions in different contexts may share a key. Empty
    /// means everywhere.</param>
    public KeyAction? AddKeyAction(string name, string nameKey, string groupKey, string defaultKey, KeyMode mode = KeyMode.Press, string context = "")
        => KeyBinds.Add(this, name, nameKey, groupKey, defaultKey, mode, context);
}
