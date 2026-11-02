using System;
using System.Collections.Generic;
using System.Text;

namespace ForgeUI;

/// <summary>Modifier keys a chord needs held. Left and right keys count the same.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>How an action is used: <see cref="Press"/> acts on the frame the key goes down, <see cref="Hold"/> while it is held,
/// <see cref="Toggle"/> flips the action's <c>On</c> at every press. All three report pressed, held and released alike; the mode
/// says what the action does and drives the toggle.</summary>
public enum KeyMode
{
    Press,
    Hold,
    Toggle,
}

/// <summary>
/// A key with the modifiers that must be held with it: <c>G</c>, <c>Ctrl+Shift+G</c>, <c>Mouse3</c>. The key is a Unity key code name
/// (<c>KeyCode</c>); <see cref="None"/> is "not bound". A chord matches only when exactly its modifiers are held, so <c>G</c> and
/// <c>Ctrl+G</c> are different keys. A modifier key on its own (<c>LeftShift</c>) is a key like any other.
/// </summary>
public readonly struct KeyChord : IEquatable<KeyChord>
{
    private const string NoneText = "None";

    public KeyChord(string key, KeyMods mods = KeyMods.None)
    {
        _key = key ?? "";
        Mods = _key.Length == 0 ? KeyMods.None : mods & ~KeyChords.ModOf(_key);
    }

    /// <summary>The key code name, or empty when unbound.</summary>
    public string Key => _key ?? "";
    private readonly string? _key;

    public KeyMods Mods { get; }

    public static KeyChord None => default;

    public bool IsNone => Key.Length == 0;

    /// <summary>The settings-file form: <c>Ctrl+Shift+G</c>, or <c>None</c>.</summary>
    public override string ToString()
    {
        if (IsNone) return NoneText;
        var text = new StringBuilder();
        if ((Mods & KeyMods.Ctrl) != 0) text.Append("Ctrl+");
        if ((Mods & KeyMods.Shift) != 0) text.Append("Shift+");
        if ((Mods & KeyMods.Alt) != 0) text.Append("Alt+");
        return text.Append(Key).ToString();
    }

    /// <summary>Reads <c>Ctrl+Shift+G</c>, <c>Mouse3</c> or <c>None</c> (case is ignored). <paramref name="known"/> turns a key name into its
    /// canonical spelling, or null for a name that is not a key; the parse fails with the reason in <paramref name="problem"/>.</summary>
    public static bool TryParse(string? text, Func<string, string?> known, out KeyChord chord, out string? problem)
    {
        chord = None;
        problem = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Equals(NoneText, StringComparison.OrdinalIgnoreCase)) return true;
        var parts = value.Split('+');
        var mods = KeyMods.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var mod = KeyChords.ParseMod(parts[i].Trim());
            if (mod == KeyMods.None) { problem = "'" + parts[i].Trim() + "' is not Ctrl, Shift or Alt"; return false; }
            if ((mods & mod) != 0) { problem = "'" + parts[i].Trim() + "' is given twice"; return false; }
            mods |= mod;
        }
        var name = parts[^1].Trim();
        if (name.Length == 0) { problem = "no key after '+'"; return false; }
        var key = known(name);
        if (key == null) { problem = "'" + name + "' is not a key"; return false; }
        chord = new KeyChord(key, mods);
        return true;
    }

    public bool Equals(KeyChord other) => Mods == other.Mods && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Key) * 8 + (int)Mods;
    public static bool operator ==(KeyChord a, KeyChord b) => a.Equals(b);
    public static bool operator !=(KeyChord a, KeyChord b) => !a.Equals(b);
}

/// <summary>Modifier key names (Unity key code names).</summary>
internal static class KeyChords
{
    internal static readonly string[] ModifierKeys = { "LeftControl", "RightControl", "LeftShift", "RightShift", "LeftAlt", "RightAlt", "AltGr" };

    /// <summary>The modifier <paramref name="key"/> is, or None.</summary>
    internal static KeyMods ModOf(string key) => key switch
    {
        "LeftControl" or "RightControl" => KeyMods.Ctrl,
        "LeftShift" or "RightShift" => KeyMods.Shift,
        "LeftAlt" or "RightAlt" or "AltGr" => KeyMods.Alt,
        _ => KeyMods.None,
    };

    internal static KeyMods ParseMod(string text)
    {
        if (text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || text.Equals("Control", StringComparison.OrdinalIgnoreCase)) return KeyMods.Ctrl;
        if (text.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return KeyMods.Shift;
        if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase)) return KeyMods.Alt;
        return KeyMods.None;
    }
}

/// <summary>
/// Every registered action, its default and the player's own key. Only what the player changed is stored: an action the player never
/// rebound follows its default, so a new default in a later version reaches it; setting a key equal to the default stores nothing.
/// Entries in the settings file for actions not registered this session (a mod that is not installed) are kept as they are.
/// </summary>
internal sealed class KeyBindBook
{
    internal const string FileHeader = "# Forge key bindings: <mod>/<action> = <key>, for example Ctrl+Shift+G, Mouse3 or None. Only changed keys are listed.";

    internal sealed class Entry
    {
        internal string FullId = "", Mod = "", Name = "", NameKey = "", GroupKey = "", Context = "";
        internal KeyChord Default;
        internal KeyMode Mode;
        /// <summary>The player's key; null follows <see cref="Default"/>.</summary>
        internal KeyChord? Custom;
        internal KeyChord Effective => Custom ?? Default;
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _stored = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _known;

    /// <param name="known">Turns a key name into its canonical spelling, or null (see <see cref="KeyChord.TryParse"/>).</param>
    internal KeyBindBook(Func<string, string?> known) => _known = known;

    internal int Count => _entries.Count;
    internal Entry this[int index] => _entries[index];

    /// <summary>Counts every change to a key, so callers can redraw key names.</summary>
    internal int Version { get; private set; }

    /// <summary>Reads the settings file text; bad lines are skipped and described in <paramref name="problems"/>. A key the player had for
    /// an action registered earlier applies at once.</summary>
    internal void Load(string text, List<string> problems)
    {
        _stored.Clear();
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var at = line.IndexOf('=');
            if (at <= 0) { problems.Add("line " + (i + 1) + " has no '='"); continue; }
            var id = line.Substring(0, at).Trim();
            var (mod, name) = ModIds.Split(id);
            if (mod.Length == 0 || ModIds.Problem(mod, "mod ID") != null || ModIds.Problem(name, "action name") != null)
            {
                problems.Add("line " + (i + 1) + ": '" + id + "' is not <mod>/<action>");
                continue;
            }
            var value = line.Substring(at + 1).Trim();
            if (!KeyChord.TryParse(value, _known, out _, out var problem)) { problems.Add(id + ": " + problem); continue; }
            if (_stored.ContainsKey(id)) problems.Add(id + " is listed twice; the last one is used");
            _stored[id] = value;
        }
        foreach (var entry in _entries) Apply(entry);
        Version++;
    }

    /// <summary>The settings file text: the header and one line per changed key, sorted.</summary>
    internal string Save()
    {
        var text = new StringBuilder(FileHeader).Append('\n');
        foreach (var (id, value) in _stored) text.Append(id).Append(" = ").Append(value).Append('\n');
        return text.ToString();
    }

    /// <summary>Adds an action; returns its index, or -1 with the reason in <paramref name="problem"/>.</summary>
    internal int Register(string mod, string name, string nameKey, string groupKey, string defaultKey, KeyMode mode, string context, out string? problem)
    {
        problem = ModIds.Problem(mod, "mod ID") ?? ModIds.Problem(name, "action name");
        if (problem != null) return -1;
        var id = ModIds.Join(mod, name);
        if (_index.ContainsKey(id)) { problem = "the action '" + id + "' is already registered"; return -1; }
        if (string.IsNullOrWhiteSpace(nameKey)) { problem = "the action '" + id + "' has no name key"; return -1; }
        if (!KeyChord.TryParse(defaultKey, _known, out var chord, out var bad)) { problem = "the default key of '" + id + "' is wrong: " + bad; return -1; }
        var entry = new Entry
        {
            FullId = id, Mod = mod, Name = name, NameKey = nameKey, GroupKey = groupKey ?? "", Context = context ?? "",
            Default = chord, Mode = mode,
        };
        Apply(entry);
        _index.Add(id, _entries.Count);
        _entries.Add(entry);
        Version++;
        return _entries.Count - 1;
    }

    private void Apply(Entry entry)
    {
        entry.Custom = null;
        if (!_stored.TryGetValue(entry.FullId, out var value) || !KeyChord.TryParse(value, _known, out var chord, out _)) return;
        if (chord == entry.Default) return;
        entry.Custom = chord;
    }

    internal int IndexOf(string fullId) => _index.TryGetValue(fullId, out var i) ? i : -1;

    /// <summary>Gives the action <paramref name="chord"/> (<see cref="KeyChord.None"/> unbinds it). The default itself is stored as "not
    /// changed". Other actions are never touched, also when they have the same key.</summary>
    internal void Set(int index, KeyChord chord)
    {
        var entry = _entries[index];
        if (chord == entry.Default)
        {
            entry.Custom = null;
            _stored.Remove(entry.FullId);
        }
        else
        {
            entry.Custom = chord;
            _stored[entry.FullId] = chord.ToString();
        }
        Version++;
    }

    internal void Reset(int index) => Set(index, _entries[index].Default);

    internal void ResetAll()
    {
        foreach (var entry in _entries)
        {
            entry.Custom = null;
            _stored.Remove(entry.FullId);
        }
        Version++;
    }

    /// <summary>Whether actions in two contexts can be live at once: the same context, or either is empty (everywhere).</summary>
    internal static bool Overlap(string a, string b) => a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>The other actions that share the key of <paramref name="index"/> where both can be live; fills <paramref name="into"/>
    /// (cleared first) and returns how many.</summary>
    internal int Conflicts(int index, List<int> into)
    {
        into.Clear();
        var entry = _entries[index];
        var chord = entry.Effective;
        if (chord.IsNone) return 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i == index) continue;
            var other = _entries[i];
            if (other.Effective == chord && Overlap(entry.Context, other.Context)) into.Add(i);
        }
        return into.Count;
    }

    /// <summary>The game's own bindings that fire together with <paramref name="index"/>'s key: a game key equal to its main key fires
    /// whatever modifiers are held. <paramref name="game"/> is the game's (action name, key name) list; matching names go into
    /// <paramref name="into"/> (cleared first), each once.</summary>
    internal int GameConflicts(int index, IReadOnlyList<(string Action, string Key)> game, List<string> into)
    {
        into.Clear();
        var chord = _entries[index].Effective;
        if (chord.IsNone) return 0;
        foreach (var (action, key) in game)
            if (string.Equals(key, chord.Key, StringComparison.OrdinalIgnoreCase) && !into.Contains(action)) into.Add(action);
        return into.Count;
    }
}

/// <summary>One action's per-frame state from whether its key is down: pressed on the frame it goes down, released on the frame it
/// comes up, and the toggle flipped at each press of a <see cref="KeyMode.Toggle"/> action.</summary>
internal struct KeyPulse
{
    internal bool Held, Pressed, Released, On;

    internal void Step(bool down, KeyMode mode)
    {
        Pressed = down && !Held;
        Released = !down && Held;
        Held = down;
        if (Pressed && mode == KeyMode.Toggle) On = !On;
    }

    /// <summary>Lets go without a release (input taken away, the key rebound): nothing is pressed or held, a toggle stays as it is.</summary>
    internal void Drop()
    {
        Held = Pressed = Released = false;
    }
}

/// <summary>
/// Reads one new key in the settings: it waits until every key is up (so the click that started it is not taken), then takes the
/// first key that goes down with the modifiers held at that moment. Esc cancels, Backspace or Delete clears the binding. A modifier
/// pressed and let go on its own is bound by itself.
/// </summary>
internal sealed class KeyCapture
{
    internal enum Result { Waiting, Bound, Cleared, Cancelled }

    private bool _armed, _otherKey;
    private string? _modifier;

    internal bool Active { get; private set; }
    internal KeyChord Chord { get; private set; }

    internal void Begin()
    {
        Active = true;
        _armed = false;
        _otherKey = false;
        _modifier = null;
        Chord = KeyChord.None;
    }

    internal void Stop() => Active = false;

    /// <param name="anyHeld">Whether any key or mouse button is held this frame.</param>
    /// <param name="down">A key that went down this frame other than a modifier, or null.</param>
    /// <param name="modifierDown">A modifier key that went down this frame, or null.</param>
    /// <param name="modifierUp">A modifier key that came up this frame, or null.</param>
    /// <param name="mods">The modifiers held this frame.</param>
    internal Result Step(bool anyHeld, string? down, string? modifierDown, string? modifierUp, KeyMods mods)
    {
        if (!Active) return Result.Waiting;
        if (!_armed)
        {
            if (!anyHeld) _armed = true;
            return Result.Waiting;
        }
        if (down != null)
        {
            _otherKey = true;
            if (down == "Escape") return Finish(Result.Cancelled, KeyChord.None);
            if ((down == "Backspace" || down == "Delete") && mods == KeyMods.None) return Finish(Result.Cleared, KeyChord.None);
            return Finish(Result.Bound, new KeyChord(down, mods));
        }
        if (modifierDown != null && _modifier == null) { _modifier = modifierDown; _otherKey = false; }
        if (modifierUp != null && modifierUp == _modifier && !_otherKey) return Finish(Result.Bound, new KeyChord(modifierUp));
        return Result.Waiting;
    }

    private Result Finish(Result result, KeyChord chord)
    {
        Active = false;
        Chord = chord;
        return result;
    }
}
