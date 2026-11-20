using System;
using System.Collections.Generic;
using System.Globalization;
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
/// field code (reading keys, writing the game's bindings, menu ids) runs for it, so no key field method is patched.
/// One row per action: its name, a small tag with its trigger when that is not a plain press, and its key in the key box, marked with a
/// red "!" when the same press also fires another action (in an overlapping context) or a game action. A press on the key box starts
/// reading a key, Esc cancels, Backspace or Delete unbinds. A press on the name opens the row's trigger options in place (one row at a
/// time): the trigger modes the mod allows, the long-press or double-press time when the trigger has one, and whether a key the game
/// also uses fires both; a press there changes it, a press on the name again, on another row or a scroll closes them. Hovering a row
/// shows its tooltip (<see cref="MenuItemTooltip"/>): the actions or game actions on the same key, the mod's longer description of the
/// action (its text key with <c>.tip</c> added, when the mod has one) and the trigger's time. The game's reset of the key page also puts
/// every action back on its default. Every Forge row sits under one FORGE heading, then one heading per mod group.
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
        internal string Tip = "";
        internal MenuItemTooltip? Tooltip;
        internal bool TooltipTried;
    }

    /// <summary>The key box parts of a copied row, kept after its key field component is removed.</summary>
    private struct KeyBox
    {
        internal TMPro.TextMeshPro? Text;
        internal SpriteRenderer? Background;
        internal Color Active, Idle;
    }

    private static readonly KeyMode[] Modes = { KeyMode.Press, KeyMode.Release, KeyMode.Tap, KeyMode.Hold, KeyMode.Double };

    private static readonly List<Row> Rows = new();
    private static readonly List<(CM_SettingsItem Row, CM_SettingKeyBindingField Field)> GameRows = new();
    private static readonly Dictionary<(InputAction, bool), string> GameNames = new();
    private static readonly List<(string Action, string Key, bool Button)> Game = new();
    private static readonly List<int> Others = new();
    private static readonly List<string> GameHits = new();
    private static CM_ScrollWindow? _window;
    private static CM_PageBase? _page;
    private static Row? _capturing;
    private static bool _lateReported;
    private static int _builtCount = -1, _shownVersion = -1, _doneFrame = -1;
    private static string _shownLanguage = "";
    private static float _nextGameRead;

    // The open trigger options: their row, the canvas they are drawn on, where the row stood when they opened (a scroll closes them) and
    // what each of their plates does when pressed, in the row's units.
    private static Row? _editing;
    private static MenuCanvas? _editCanvas;
    private static float _editY;
    private static readonly List<(Rect Box, Action Press)> EditHits = new();

    /// <summary>Per frame: runs a key capture, keeps the rows' texts current and shows the hovered row's tooltip.</summary>
    private static void Update()
    {
        if (_window == null || _window.WasCollected) return;
        try
        {
            if (_builtCount >= 0 && _builtCount != KeyBinds.Book.Count && !_lateReported)
            {
                _lateReported = true;
                Plugin.Error("ui-key-action-s-were-added", "Key action(s) were added after the game built its settings page and are not listed there (" + (KeyBinds.Book.Count - _builtCount)
                             + " of " + KeyBinds.Book.Count + "); register key actions in the package's Load. They keep their saved keys (" + Plugin.Id + ".keys.cfg).");
            }
            if (Rows.Count == 0) return;
            if (_capturing != null) Capture();
            var visible = _window.gameObject.activeInHierarchy;
            if (_editing != null && (!visible || _editing.Item == null || _editing.Item.WasCollected || Math.Abs(RowY(_editing) - _editY) > 0.5f)) CloseEditor();
            var now = ForgeCore.Frame.UnscaledTime;
            var gameRead = visible && now >= _nextGameRead;
            if (gameRead) _nextGameRead = now + 1f;
            if (gameRead || _shownVersion != KeyBinds.Book.Version || _shownLanguage != UITexts.Language) Refresh(gameRead);
            if (visible) Tooltips();
        }
        catch (Exception error) { Plugin.Error("ui-key-settings-rows-failed-update", "The key settings rows failed to update: " + error); }
    }

    /// <summary>The game is about to lay out its keyboard key page: the mod rows are appended to the list it lays out.</summary>
    private static void AddRows(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content)
    {
        Reset();
        _window = window;
        _page = window.GetComponentInParent<CM_PageBase>();
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
            Plugin.Error("ui-games-keyboard-key-page", "The game's keyboard key page has no key row to copy; the mods' key actions are not listed and keep their saved keys (" + Plugin.Id + ".keys.cfg).");
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
            Rows.Add(Copy(template, holder, -1, "", ""));
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
        // The copy keeps the game's tooltip of the key row it was copied from; ours is drawn by the kit.
        MenuScreen.NoTooltip(item);
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
        CloseEditor();
        foreach (var row in Rows) Input.Unroute(row.Route);
        Rows.Clear();
        GameRows.Clear();
        GameNames.Clear();
        Game.Clear();
        _window = null;
        _page = null;
        _builtCount = -1;
        _lateReported = false;
    }

    /// <summary>A press on an action row: on its key box it reads a key; on its name it opens (or closes) the row's trigger options, and
    /// on one of their plates it changes that. Where the press is comes from the page's menu cursor (mouse and gamepad alike); without
    /// one the press reads a key.</summary>
    private static void Press(Row row)
    {
        if (row.Action < 0 || Time.frameCount == _doneFrame) return;
        var at = Cursor(row.Item.transform);
        var chip = Chip(row);
        if (at == null || chip.Width() <= 0f || at.Value.x >= chip.XMin() - chip.Height() * 0.25f)
        {
            if (_editing != null && _editing != row) CloseEditor();
            Read(row);
            return;
        }
        if (_capturing != null) Stop(_capturing);
        if (_editing != row) { OpenEditor(row); return; }
        foreach (var (box, act) in EditHits)
        {
            if (at.Value.x < box.XMin() || at.Value.x > box.XMax() || at.Value.y < box.YMin() || at.Value.y > box.YMax()) continue;
            act();
            DrawEditor();
            return;
        }
        CloseEditor();
    }

    /// <summary>The page's menu cursor in <paramref name="space"/>'s units, or null without one.</summary>
    private static Vector2? Cursor(Transform space)
    {
        var cursor = _page == null || _page.WasCollected ? null : _page.m_cursor;
        if (cursor == null) return null;
        var local = space.InverseTransformPoint(cursor.transform.position);
        return new Vector2(local.x, local.y);
    }

    /// <summary>The row's key box in the row's units (empty when it has none).</summary>
    private static Rect Chip(Row row)
    {
        var background = row.Box.Background;
        if (background == null || background.WasCollected) return default;
        var bounds = background.bounds;
        var t = row.Item.transform;
        var a = t.InverseTransformPoint(bounds.min);
        var b = t.InverseTransformPoint(bounds.max);
        return Rect.MinMaxRect(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
    }

    private static void Read(Row row)
    {
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

    /// <summary>The row's key box: the game's reading colour and "press a key" in amber while capturing, else the key in the menu's text
    /// colour, after a red "!" when the same press fires something else too.</summary>
    private static void Show(Row row, bool reading)
    {
        var box = row.Box;
        if (box.Background != null && !box.Background.WasCollected) box.Background.color = reading ? box.Active : box.Idle;
        if (box.Text == null || box.Text.WasCollected) return;
        box.Text.richText = true;
        box.Text.color = reading ? MenuLook.Amber : MenuLook.Text;
        var key = KeyBinds.Display(KeyBinds.Book[row.Action].Effective);
        NativeUi.SetText(box.Text, reading ? UIKit.Mod(Plugin.Id).Text("keys.press")
            : Clashes(row.Action, null) > 0 ? "<color=#" + MenuLook.Hex(UIKitColors.Alert) + ">!</color> " + key : key);
    }

    // ---- trigger options ----------------------------------------------------------------------------------------------

    private static void OpenEditor(Row row)
    {
        CloseEditor();
        var title = row.Item.m_title;
        var background = row.Box.Background;
        if (title == null || background == null) return;
        _editing = row;
        _editY = RowY(row);
        _editCanvas = new MenuCanvas(row.Item.transform, "ForgeUI.KeyOptions", background, title, MenuCanvas.TopOrder(row.Item) + 1);
        NativeUi.SetText(title, "");
        DrawEditor();
    }

    /// <summary>Where the row stands in its window (a scroll moves it; the page's panning with the cursor moves the window with it).</summary>
    private static float RowY(Row row) => _window == null || _window.WasCollected ? 0f : _window.transform.InverseTransformPoint(row.Item.transform.position).y;

    private static void CloseEditor()
    {
        if (_editing == null) return;
        _editCanvas?.Destroy();
        _editCanvas = null;
        _editing = null;
        EditHits.Clear();
        _shownVersion = -1;
    }

    /// <summary>The open row's trigger options, over its name's place up to its key box: an amber edge, the name, the allowed trigger modes
    /// (the current one amber), the long-press or double-press time when the trigger has one (a press steps it) and, on a key the game also
    /// uses, whether both fire (amber when they do).</summary>
    private static void DrawEditor()
    {
        var row = _editing;
        var c = _editCanvas;
        if (row == null || c == null || !c.Alive) return;
        var index = row.Action;
        var entry = KeyBinds.Book[index];
        var ui = UIKit.Mod(Plugin.Id);
        var whole = MenuCanvas.BoxIn(row.Item.transform, row.Item.GetComponent<BoxCollider2D>() ?? row.Item.GetComponentInChildren<BoxCollider2D>(true), default);
        var chip = Chip(row);
        float h = whole.Height(), mid = whole.CenterY(), size = h * 0.34f;
        if (h <= 0f) return;
        var left = whole.XMin() + h * 0.4f;
        var right = (chip.Width() > 0f ? chip.XMin() : whole.XMax()) - h * 0.4f;
        EditHits.Clear();
        c.Begin();
        c.Rect(Rect.MinMaxRect(whole.XMin() + 2f, whole.YMin() + h * 0.16f, whole.XMin() + 6f, whole.YMax() - h * 0.16f), MenuLook.Amber);
        var nameRoom = (right - left) * 0.3f;
        c.Text(RichText.Plain(UIKit.Mod(row.Mod).Texts.Text(entry.NameKey)), left, mid, size, MenuLook.Line, HudAlign.Left, nameRoom);

        var modes = new List<KeyMode>();
        foreach (var mode in Modes)
            if (KeyModeSet.Allows(entry.Allowed, mode)) modes.Add(mode);
        var timed = KeyModeSet.Defers(entry.Mode) || entry.Mode == KeyMode.Double;
        var shared = SharesGameButton(index);
        float gap = h * 0.4f, x = left + nameRoom + gap, ph = h * 0.56f, y0 = mid - ph / 2f;
        var weights = modes.Count + (timed ? 1f : 0f) + (shared ? 2f : 0f);
        var unit = Math.Max(0f, right - x - gap * ((timed ? 1 : 0) + (shared ? 1 : 0))) / Math.Max(1f, weights);

        if (modes.Count > 1)
        {
            var names = modes.ConvertAll(m => ModeWord(m));
            var choice = new UIChoice("", names, () => modes.IndexOf(KeyBinds.Book[index].Mode), i => KeyBinds.SetMode(index, modes[i]));
            var span = new Rect(x, y0, unit * modes.Count, ph);
            MenuControlLook.Plates(c, span, choice, true, size * 0.9f);
            var each = (span.Width() - ph / 6f * (modes.Count - 1)) / modes.Count;
            for (var i = 0; i < modes.Count; i++)
            {
                var pick = i;
                EditHits.Add((new Rect(x + i * (each + ph / 6f), y0, each, ph), () => choice.SetValue(pick)));
            }
        }
        x += unit * modes.Count + gap;
        if (timed)
        {
            var seconds = entry.Mode == KeyMode.Double ? KeyBinds.Book.WindowOf(index) : KeyBinds.Book.HoldOf(index);
            var box = new Rect(x, y0, unit, ph);
            Plate(c, box, ui.Text("keys.seconds", seconds.ToString("0.##", CultureInfo.InvariantCulture)), false, size * 0.9f);
            EditHits.Add((box, () =>
            {
                if (KeyBinds.Book[index].Mode == KeyMode.Double) KeyBinds.CycleWindow(index);
                else KeyBinds.CycleHold(index);
            }
            ));
            x += unit + gap;
        }
        if (shared)
        {
            var box = new Rect(x, y0, unit * 2f, ph);
            Plate(c, box, ui.Text("keys.game.chip"), entry.WithGame, size * 0.9f);
            EditHits.Add((box, () => KeyBinds.ToggleWithGame(index)));
        }
        c.End();
    }

    /// <summary>One plate of the trigger options with its word: amber when <paramref name="on"/>, else dark with a light frame.</summary>
    private static void Plate(MenuCanvas c, Rect box, string text, bool on, float size)
    {
        var cut = box.Height() * 0.2f;
        c.Plate(box, cut, on ? MenuLook.Faded(MenuLook.Amber, 0.9f) : MenuLook.Bay);
        c.Frame(box, cut, on ? MenuLook.Amber : MenuLook.LineMid);
        c.Text(text, box.CenterX(), box.CenterY(), size, on ? MenuLook.Ink : MenuLook.Text, HudAlign.Centre, box.Width() - 6f);
    }

    private static string ModeWord(KeyMode mode) => UIKit.Mod(Plugin.Id).Text("keys.mode." + mode.ToString().ToLowerInvariant());

    // ---- texts --------------------------------------------------------------------------------------------------------

    /// <summary>The action could hand its other presses to a game button on its key, so whether both fire means something.</summary>
    private static bool SharesGameButton(int action)
    {
        if (!KeyBinds.Book.CanHandBack(action)) return false;
        var key = KeyBinds.Book[action].Effective.Key;
        foreach (var (_, gameKey, button) in Game)
            if (button && string.Equals(gameKey, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>How many actions and game actions the same press of <paramref name="action"/>'s key fires too; their names go into
    /// <paramref name="names"/> when given.</summary>
    private static int Clashes(int action, List<string>? names)
    {
        var count = KeyBinds.Book.Conflicts(action, Others) + KeyBinds.Book.GameConflicts(action, Game, GameHits);
        if (names == null) return count;
        foreach (var other in Others) names.Add(RichText.Plain(UIKit.Mod(KeyBinds.Book[other].Mod).Texts.Text(KeyBinds.Book[other].NameKey)));
        foreach (var hit in GameHits) names.Add(RichText.Plain(hit));
        return count;
    }

    /// <summary>A game action as the game's settings name it: the title of its row on the key page, else the game's own title for it,
    /// else its name's words apart.</summary>
    private static string GameName(InputAction action, bool negative)
    {
        if (GameNames.TryGetValue((action, negative), out var name)) return name;
        var code = action.ToString();
        try
        {
            var title = CellSettingGlobals.GetKeyBindingTitle(action, negative);
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, code, StringComparison.OrdinalIgnoreCase)) return title;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("ui-key-action-title-unread", "The game has no settings title for its action " + code + "; its name is shown as words: " + error.Message); }
        return KeyNames.Words(code);
    }

    /// <summary>The row's tooltip: what else the same press fires, the mod's description of the action, the trigger's time.</summary>
    private static string Tooltip(UIKitMod ui, int action, Row row)
    {
        var entry = KeyBinds.Book[action];
        var lines = new List<string>();
        var names = new List<string>();
        if (Clashes(action, names) > 0) lines.Add(ui.Text("keys.conflict", string.Join(", ", names)));
        if (UIKit.Mod(row.Mod).Texts.TryText(entry.NameKey + ".tip", out var tip)) lines.Add(RichText.Plain(tip));
        if (KeyModeSet.Defers(entry.Mode) || entry.Mode == KeyMode.Double)
        {
            var seconds = entry.Mode == KeyMode.Double ? KeyBinds.Book.WindowOf(action) : KeyBinds.Book.HoldOf(action);
            lines.Add(ui.Text("keys.detail", ModeWord(entry.Mode), ui.Text("keys.seconds", seconds.ToString("0.##", CultureInfo.InvariantCulture))));
        }
        if (SharesGameButton(action))
            lines.Add(ui.Text(entry.WithGame ? "keys.game.hint.both" : entry.Mode switch { KeyMode.Hold => "keys.game.hint.hold", KeyMode.Double => "keys.game.hint.double", _ => "keys.game.hint.tap" }));
        return string.Join(" ", lines);
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
                Game.Add((GameName(binding.Action, binding.Negative), binding.Key.ToString(), binding.Button));
        }
        var ui = UIKit.Mod(Plugin.Id);
        var dim = "<color=#" + MenuLook.Hex(MenuLook.Dim) + "><size=75%>";
        foreach (var row in Rows)
        {
            if (row.Item == null || row.Item.WasCollected || row.Item.m_title == null) continue;
            if (row.Action < 0)
            {
                var heading = row.Mod.Length == 0 ? ui.Text("keys.forge").ToUpperInvariant() + "    " + dim + ui.Text("keys.edit") + "</size></color>"
                    : (row.Group.Length > 0 ? UIKit.Mod(row.Mod).Texts.Text(row.Group) : row.Mod).ToUpperInvariant();
                NativeUi.SetText(row.Item.m_title, heading);
                continue;
            }
            var entry = KeyBinds.Book[row.Action];
            row.Tip = Tooltip(ui, row.Action, row);
            if (row == _editing) DrawEditor();
            else
            {
                var title = RichText.Plain(UIKit.Mod(row.Mod).Texts.Text(entry.NameKey));
                if (entry.Mode != KeyMode.Press) title += "   " + dim + ModeWord(entry.Mode) + "</size></color>";
                NativeUi.SetText(row.Item.m_title, title);
            }
            if (row != _capturing) Show(row, false);
        }
    }

    /// <summary>The hovered action row's tooltip (each row's canvas is made at its first hover), over the row, or under it at the top of
    /// the list.</summary>
    private static void Tooltips()
    {
        foreach (var row in Rows)
        {
            if (row.Action < 0 || row.Item == null || row.Item.WasCollected) continue;
            var hover = row.Item.m_isHovering && row != _capturing && row != _editing;
            if (row.Tooltip == null)
            {
                if (!hover || row.TooltipTried || row.Tip.Length == 0) continue;
                row.TooltipTried = true;
                row.Tooltip = MenuItemTooltip.On(row.Item, box => new Rect(box.XMin(), box.YMin() - box.Height() * 3f, box.Width(), box.Height() * 7f));
                if (row.Tooltip == null) continue;
            }
            row.Tooltip.Show(row.Tip, hover);
        }
    }
}
