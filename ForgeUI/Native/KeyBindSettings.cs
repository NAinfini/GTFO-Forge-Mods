using System;
using System.Collections.Generic;
using CellMenu;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// Puts every registered key action into the game's own keyboard and mouse key page. The rows join the game's own build of that page:
/// while <c>CM_PageSettings.CreateKeyMappingSubMenu</c> builds the key page, the list the game hands to its window
/// (<c>CM_ScrollWindow.SetContentItems</c>) gets, after the game's rows, one heading row per mod group and one row per action, so the
/// game lays out, scrolls and hovers them in its single call exactly as its own rows. The window is never given a list again: the game's
/// <c>SetContentItems</c> destroys every child of the content container before it lays out the new list, so a second call destroyed the
/// game's rows with ours (an empty key page and a NullReferenceException in <c>UpdateContentItemVisibility</c> on every hover).
/// Each row is a copy of one of the game's key rows (<c>CM_SettingsItem</c>) made while inactive, with the copy's key field
/// (<c>CM_SettingKeyBindingField</c>) and its colliders removed before it ever runs: the copy keeps the key box's look, and no game key
/// field code (reading keys, writing the game's bindings, menu ids) runs for it, so no key field method is patched. A press on the row
/// starts reading a key, Esc cancels, Backspace or Delete unbinds; the game's reset of the key page also puts every action back on its
/// default. A key another action (in an overlapping context) or the game uses is named after the action's title in the warning colour.
/// Actions are registered in the packages' Load, before the game builds its settings; one added after that is reported as an error and
/// keeps its saved key.
/// </summary>
internal static class KeyBindSettings
{
    /// <summary>The key actions' rows on the game's keyboard and mouse key page, registered with <see cref="SettingsPages"/>.</summary>
    internal static readonly PageRows Group = new KeyRows();

    private sealed class KeyRows : PageRows
    {
        internal override PageBuilder Builder => PageBuilder.KeyMapping;
        internal override eSettingsSubMenuId Page => eSettingsSubMenuId.KeyMap;
        internal override string Owner => "the mods' key actions";
        internal override void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content) => AddRows(window, content);
        internal override void PageReset() => KeyBinds.ResetAll();
        internal override void Update() => KeyBindSettings.Update();
    }
    private sealed class Row
    {
        internal CM_SettingsItem Item = null!;
        internal KeyBox Box;
        internal int Action = -1;
        internal string Group = "", Mod = "";
        internal IntPtr Route;
    }

    /// <summary>The key box parts of a copied row, kept after its key field component is removed.</summary>
    private struct KeyBox
    {
        internal TMPro.TextMeshPro? Text;
        internal SpriteRenderer? Background;
        internal Color Active, Idle;
    }

    private static readonly List<Row> Rows = new();
    private static readonly List<(CM_SettingsItem Row, CM_SettingKeyBindingField Field)> GameRows = new();
    private static readonly Dictionary<(InputAction, bool), string> GameNames = new();
    private static readonly List<(string Action, string Key)> Game = new();
    private static readonly List<int> Others = new();
    private static readonly List<string> GameHits = new();
    private static CM_ScrollWindow? _window;
    private static Row? _capturing;
    private static bool _lateReported;
    private static int _builtCount = -1, _shownVersion = -1, _doneFrame = -1;
    private static string _shownLanguage = "";
    private static float _nextGameRead;

    /// <summary>Per frame: runs a key capture and keeps the rows' texts current.</summary>
    private static void Update()
    {
        if (_window == null || _window.WasCollected) return;
        try
        {
            if (_builtCount >= 0 && _builtCount != KeyBinds.Book.Count && !_lateReported)
            {
                _lateReported = true;
                Plugin.Error("Key action(s) were added after the game built its settings page and are not listed there (" + (KeyBinds.Book.Count - _builtCount)
                             + " of " + KeyBinds.Book.Count + "); register key actions in the package's Load. They keep their saved keys (" + Plugin.Id + ".keys.cfg).");
            }
            if (Rows.Count == 0) return;
            if (_capturing != null) Capture();
            var visible = _window.gameObject.activeInHierarchy;
            var now = Time.unscaledTime;
            var gameRead = visible && now >= _nextGameRead;
            if (gameRead) _nextGameRead = now + 1f;
            if (gameRead || _shownVersion != KeyBinds.Book.Version || _shownLanguage != UITexts.Language) Refresh(gameRead);
        }
        catch (Exception error) { Plugin.Error("The key settings rows failed to update: " + error); }
    }

    /// <summary>The game is about to lay out its keyboard key page: the mod rows are appended to the list it lays out.</summary>
    private static void AddRows(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content)
    {
        Reset();
        _window = window;
        _builtCount = KeyBinds.Book.Count;
        CM_SettingsItem? template = null;
        for (var i = 0; i < content.Count; i++)
        {
            var row = content[i]?.TryCast<CM_SettingsItem>();
            var field = row == null ? null : row.GetComponentInChildren<CM_SettingKeyBindingField>(true);
            if (row == null || field == null) continue;
            template ??= row;
            GameRows.Add((row, field));
        }
        if (_builtCount == 0) return;
        if (template == null)
        {
            Plugin.Error("The game's keyboard key page has no key row to copy; the mods' key actions are not listed and keep their saved keys (" + Plugin.Id + ".keys.cfg).");
            return;
        }

        var order = new List<int>();
        for (var i = 0; i < KeyBinds.Book.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var x = KeyBinds.Book[a];
            var y = KeyBinds.Book[b];
            var c = string.CompareOrdinal(x.Mod, y.Mod);
            if (c == 0) c = string.CompareOrdinal(x.GroupKey, y.GroupKey);
            return c != 0 ? c : a.CompareTo(b);
        });

        SettingsPages.CopyRows(Group, template, "ForgeUI.KeyRows", content, holder =>
        {
            string? mod = null, group = null;
            foreach (var index in order)
            {
                var entry = KeyBinds.Book[index];
                if (entry.Mod != mod || entry.GroupKey != group)
                {
                    mod = entry.Mod;
                    group = entry.GroupKey;
                    Rows.Add(Copy(template, holder, -1, mod, group));
                }
                Rows.Add(Copy(template, holder, index, entry.Mod, entry.GroupKey));
            }
            return Rows.ConvertAll(row => row.Item);
        });
        _shownVersion = -1;
    }

    private static Row Copy(CM_SettingsItem template, Transform holder, int action, string mod, string group)
    {
        var go = Object.Instantiate(template.gameObject, holder);
        go.name = action < 0 ? "ForgeUI.KeyGroup." + mod + "." + group : "ForgeUI.Key." + KeyBinds.Book[action].FullId;
        NativeUi.StripLocalization(go);
        var item = go.GetComponent<CM_SettingsItem>();
        var row = new Row { Item = item, Action = action, Mod = mod, Group = group };
        var field = go.GetComponentInChildren<CM_SettingKeyBindingField>(true);
        var boxObject = field == null ? null : field.gameObject;
        if (field != null)
        {
            row.Box = new KeyBox { Text = field.m_valueText, Background = field.m_background, Active = field.m_bgActive, Idle = field.m_bgNotActive };
            foreach (var collider in field.GetComponents<Collider2D>()) Object.DestroyImmediate(collider);
            Object.DestroyImmediate(field);
        }
        if (action < 0)
        {
            if (item.m_inputAlign != null) item.m_inputAlign.gameObject.SetActive(false);
            else if (boxObject != null) boxObject.SetActive(false);
            row.Box = default;
        }
        row.Route = Input.Route(item, () => Press(row), null);
        return row;
    }

    private static void Reset()
    {
        if (_capturing != null) { KeyBinds.Capture.Stop(); _capturing = null; }
        foreach (var row in Rows) Input.Unroute(row.Route);
        Rows.Clear();
        GameRows.Clear();
        GameNames.Clear();
        Game.Clear();
        _window = null;
        _builtCount = -1;
        _lateReported = false;
    }

    private static void Press(Row row)
    {
        if (row.Action < 0 || Time.frameCount == _doneFrame) return;
        if (_capturing != null && _capturing != row) Show(_capturing, false);
        _capturing = row;
        KeyBinds.BeginCapture();
        Show(row, true);
    }

    private static void Capture()
    {
        var row = _capturing!;
        if (row.Item == null || row.Item.WasCollected || !row.Item.gameObject.activeInHierarchy)
        {
            Stop(row);
            return;
        }
        switch (KeyBinds.StepCapture())
        {
            case KeyCapture.Result.Bound: KeyBinds.Set(row.Action, KeyBinds.Capture.Chord); Stop(row); break;
            case KeyCapture.Result.Cleared: KeyBinds.Set(row.Action, KeyChord.None); Stop(row); break;
            case KeyCapture.Result.Cancelled: Stop(row); break;
        }
    }

    private static void Stop(Row row)
    {
        KeyBinds.Capture.Stop();
        _capturing = null;
        _doneFrame = Time.frameCount;
        if (row.Item != null && !row.Item.WasCollected) Show(row, false);
    }

    /// <summary>The row's key box: the game's reading colour and "press a key" while capturing, else the key.</summary>
    private static void Show(Row row, bool reading)
    {
        var box = row.Box;
        if (box.Background != null && !box.Background.WasCollected) box.Background.color = reading ? box.Active : box.Idle;
        if (box.Text != null && !box.Text.WasCollected)
            NativeUi.SetText(box.Text, reading ? UIKit.Mod(Plugin.Id).Text("keys.press") : KeyBinds.Display(KeyBinds.Book[row.Action].Effective));
    }

    private static void Refresh(bool readGame)
    {
        _shownVersion = KeyBinds.Book.Version;
        _shownLanguage = UITexts.Language;
        if (readGame || Game.Count == 0)
        {
            GameNames.Clear();
            foreach (var (gameRow, field) in GameRows)
                if (!gameRow.WasCollected && !field.WasCollected && field.m_data != null && gameRow.m_title != null)
                    GameNames[(field.m_data.action, field.m_isNegCompOfTwoAxisValue)] = gameRow.m_title.text;
            Game.Clear();
            foreach (var binding in GameKeys.All())
                Game.Add((GameNames.TryGetValue((binding.Action, binding.Negative), out var name) ? name : binding.Action.ToString(), binding.Key.ToString()));
        }
        var ui = UIKit.Mod(Plugin.Id);
        foreach (var row in Rows)
        {
            if (row.Item == null || row.Item.WasCollected || row.Item.m_title == null) continue;
            var texts = UIKit.Mod(row.Mod).Texts;
            if (row.Action < 0)
            {
                NativeUi.SetText(row.Item.m_title, (row.Group.Length > 0 ? texts.Text(row.Group) : row.Mod).ToUpperInvariant());
                continue;
            }
            var entry = KeyBinds.Book[row.Action];
            var title = RichText.Plain(texts.Text(entry.NameKey));
            var clashes = new List<string>();
            if (KeyBinds.Book.Conflicts(row.Action, Others) > 0)
                foreach (var other in Others) clashes.Add(RichText.Plain(UIKit.Mod(KeyBinds.Book[other].Mod).Texts.Text(KeyBinds.Book[other].NameKey)));
            if (KeyBinds.Book.GameConflicts(row.Action, Game, GameHits) > 0)
                foreach (var hit in GameHits) clashes.Add(RichText.Plain(hit));
            if (clashes.Count > 0)
                title += "  <color=#" + ColorUtility.ToHtmlStringRGB(UIKitColors.Alert) + ">" + ui.Text("keys.conflict", string.Join(" / ", clashes)) + "</color>";
            NativeUi.SetText(row.Item.m_title, title);
            if (row != _capturing) Show(row, false);
        }
    }
}
