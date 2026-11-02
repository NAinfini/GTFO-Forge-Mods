using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// A full screen opened from the menu bar (<see cref="UIKitMod.AddMenuBarScreen"/>), shown on the game's own settings page and made of
/// copies of that page's own pieces as the game set them up: section buttons copied from the settings sections and put where they stand,
/// windows copied from a settings window (its frame, header, scroll bar and clipping) with rows copied from its rows (the game's row box,
/// text and hover), buttons copied from the section button and the page's red button. While it is open the settings page's own sections
/// and windows are put away; they come back when it closes. Everything else (backdrop, menu bar, cursor) is the settings page as the game
/// shows it.
/// </summary>
public sealed class MenuScreen
{
    // The settings page's own layout, in its moving content's units (read from the page: sections at x -1180 from y 550 every 80;
    // windows at x -830, y 550, 880 tall; rows 50 tall with 5 between).
    internal const float SectionX = -1180f, SectionTop = 550f, SectionStep = 80f;
    internal const float WindowX = -830f, WindowTop = 550f, WindowGap = 60f, WindowHeight = 880f, RowHeight = 50f, RowGap = 5f, RowInset = 20f;
    // Table cells: the text's inset from the row's edges and the space between columns.
    internal const float CellPad = 14f, CellGap = 12f;
    internal const string RowName = "ForgeUI.ScreenRow";

    private readonly UITexts _texts;
    private readonly List<MenuScreenWindow> _windows = new();
    private int _sections;
    private float _nextX = WindowX;

    internal MenuScreen(CM_PageSettings page, Transform holder, UITexts texts, GameObject section, GameObject? redButton, CM_ScrollWindow window, CM_SettingsItem row)
    {
        Page = page;
        Holder = holder;
        _texts = texts;
        SectionTemplate = section;
        RedTemplate = redButton;
        WindowTemplate = window;
        RowTemplate = row;
    }

    internal CM_PageSettings Page { get; }
    internal Transform Holder { get; }
    internal GameObject SectionTemplate { get; }
    internal GameObject? RedTemplate { get; }
    internal CM_ScrollWindow WindowTemplate { get; }
    internal CM_SettingsItem RowTemplate { get; }
    internal readonly List<GameObject> Created = new();
    internal readonly HashSet<IntPtr> Mine = new();
    internal readonly List<IntPtr> Routes = new();

    internal string Text(UIText text) => (text ?? UIText.Empty).In(_texts);

    /// <summary>A section button in the left column, under the ones before it, as the settings sections are; the selected one keeps its
    /// text bright.</summary>
    public void AddSection(UIText text, bool selected, Action onClick)
    {
        var item = Button(SectionTemplate, Text(text), onClick);
        item.transform.localPosition = new Vector3(SectionX, SectionTop - SectionStep * _sections++, 0f);
        Bright(item, selected);
    }

    /// <summary>A window like the settings windows with <paramref name="header"/>, <paramref name="width"/> across, right of the ones
    /// before it; its rows scroll inside it.</summary>
    public MenuScreenWindow AddWindow(UIText header, float width)
    {
        var go = Copy(WindowTemplate.gameObject, "ForgeUI.ScreenWindow");
        var window = go.GetComponent<CM_ScrollWindow>() ?? throw new InvalidOperationException("the settings window copy has no CM_ScrollWindow");
        // The copy brings the settings rows and buttons along: only the frame is kept.
        var content = window.m_contentContainer != null ? window.m_contentContainer.transform : null;
        if (content != null) for (var i = content.childCount - 1; i >= 0; i--) Drop(content.GetChild(i).gameObject);
        foreach (var button in go.GetComponentsInChildren<CM_TimedButton>(true)) Drop(button.gameObject);
        // A copy carries only what the game saves with the window: its set-up (content lists that SetSize walks, the clipping that
        // shows the rows) runs again, as the settings page runs it on each window it makes.
        window.Setup();
        window.SetSize(new Vector2(width, WindowHeight));
        window.SetHeader(Text(header));
        go.transform.localPosition = new Vector3(_nextX, WindowTop, 0f);
        var result = new MenuScreenWindow(this, window, width, _nextX);
        _nextX += width + WindowGap;
        _windows.Add(result);
        return result;
    }

    /// <summary>Builds the screen again at once (after a click changed what it shows).</summary>
    public void Rebuild() => MenuScreens.Rebuild();

    internal void Finish()
    {
        foreach (var window in _windows) window.Finish();
    }

    internal GameObject Copy(GameObject template, string name, Transform? parent = null)
    {
        var go = Object.Instantiate(template, parent ?? Holder);
        go.name = name;
        NativeUi.StripLocalization(go);
        go.SetActive(true);
        if (parent == null) { Created.Add(go); Mine.Add(go.Pointer); }
        return go;
    }

    internal CM_Item Button(GameObject template, string text, Action onClick)
    {
        var item = Copy(template, "ForgeUI.ScreenButton").GetComponent<CM_Item>() ?? throw new InvalidOperationException("the settings button copy has no CM_Item");
        NoTooltip(item);
        item.SetText(text);
        Routes.Add(Input.Route(item, Guard(onClick), null));
        return item;
    }

    /// <summary>A copy keeps the hover tooltip of the setting it was copied from ("Toggles the crouch mode..."): ours have none.</summary>
    internal static void NoTooltip(CM_Item item)
    {
        var info = item.m_tooltipInfo;
        if (info == null) return;
        info.UseTooptip = false;
        info.TooltipHeader = "";
        info.TooltipText = "";
        item.m_tooltipInfo = info;
    }
    internal static void Bright(CM_Item item, bool on)
    {
        if (on) item.m_textAlphaOut = 1f;
        item.CheckAndSetTextAlpha();
    }

    internal static Action Guard(Action onClick) => () =>
    {
        try { onClick(); }
        catch (Exception error) { Plugin.Error("Menu screen click failed: " + error); }
    };

    private static void Drop(GameObject go)
    {
        go.SetActive(false);
        Object.Destroy(go);
    }
}

/// <summary>A window of a <see cref="MenuScreen"/>: rows like the settings rows, buttons under it.</summary>
public sealed class MenuScreenWindow
{
    private static readonly Color Selected = new(1f, 1f, 1f, 0.18f);
    private readonly MenuScreen _screen;
    private readonly CM_ScrollWindow _window;
    private readonly float _width, _x;
    private readonly Il2CppSystem.Collections.Generic.List<iScrollWindowContent> _rows = new();
    private int _buttons;
    private float[] _stops = Array.Empty<float>();
    private int[] _right = Array.Empty<int>();

    internal MenuScreenWindow(MenuScreen screen, CM_ScrollWindow window, float width, float x)
    {
        _screen = screen;
        _window = window;
        _width = width;
        _x = x;
    }

    /// <summary>A row like the settings rows with one line of rich text; with <paramref name="onClick"/> it answers the cursor like a
    /// settings row; <paramref name="selected"/> lights its box.</summary>
    public void AddRow(UIText text, Action? onClick = null, bool selected = false) => Row(onClick, selected).SetText(_screen.Text(text));

    /// <summary>The table columns of this window's following <see cref="AddHeader"/> and cell rows: column <c>i</c> (after the first)
    /// starts at <paramref name="stops"/>[i - 1] per cent of the row's width and ends where the next starts; the columns in
    /// <paramref name="rightAligned"/> (0 is the first) line their text up on the right, as numbers are.</summary>
    public void Columns(float[] stops, params int[] rightAligned)
    {
        _stops = stops ?? Array.Empty<float>();
        _right = rightAligned ?? Array.Empty<int>();
    }

    /// <summary>A table header: <paramref name="cells"/> in the game's dim label colour on the <see cref="Columns"/>, not clickable.</summary>
    public void AddHeader(params UIText[] cells)
    {
        var texts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++) texts[i] = MenuText.Tint(_screen.Text(cells[i]), MenuTone.Dim);
        Cells(Row(null, false), texts, _stops, _right);
    }

    /// <summary>A table row: each of <paramref name="cells"/> (rich text; <see cref="MenuText"/> tints it and shows author text as
    /// written) is a text of its own in its <see cref="Columns"/> column, cut with an ellipsis where it would run into the next.</summary>
    public void AddRow(IReadOnlyList<string> cells, Action? onClick = null, bool selected = false) => Cells(Row(onClick, selected), cells, _stops, _right);

    /// <summary>A label and value row of a details window: <paramref name="label"/> dim at the left, <paramref name="value"/> (rich text)
    /// from <paramref name="at"/> per cent of the row's width.</summary>
    public void AddField(UIText label, string value, float at = 38f)
        => Cells(Row(null, false), new[] { MenuText.Tint(_screen.Text(label), MenuTone.Dim), value }, new[] { at }, Array.Empty<int>());

    private CM_SettingsItem Row(Action? onClick, bool selected)
    {
        // Made beside the content, not in it: the window's SetContentItems clears its content container and moves the items in, as
        // the settings page does with the rows it makes.
        var row = _screen.Copy(_screen.RowTemplate.gameObject, MenuScreen.RowName, _window.transform).GetComponent<CM_SettingsItem>()
            ?? throw new InvalidOperationException("the settings row copy has no CM_SettingsItem");
        if (row.m_inputAlign != null)
            for (var i = row.m_inputAlign.childCount - 1; i >= 0; i--) Object.Destroy(row.m_inputAlign.GetChild(i).gameObject);
        MenuScreen.NoTooltip(row);
        row.SetSize(new Vector2(_width - MenuScreen.RowInset, MenuScreen.RowHeight));
        if (selected && row.m_background != null) row.m_background.color = Selected;
        if (onClick == null) { if (row.m_collider != null) row.m_collider.enabled = false; }
        else _screen.Routes.Add(Input.Route(row, MenuScreen.Guard(onClick), null));
        _rows.Add(new iScrollWindowContent(row.Pointer));
        return row;
    }

    // Each cell a copy of the row's own title text, placed in its column of the row (x from the row's left edge), one line, cut with an
    // ellipsis; the row's title itself stays empty.
    private void Cells(CM_SettingsItem row, IReadOnlyList<string> cells, IReadOnlyList<float> stops, IReadOnlyList<int> right)
    {
        var title = row.m_title;
        row.SetText("");
        if (title == null) return;
        var width = _width - MenuScreen.RowInset - 2f * MenuScreen.CellPad;
        for (var i = 0; i < cells.Count; i++)
        {
            var from = i == 0 ? 0f : i - 1 < stops.Count ? stops[i - 1] : 100f;
            var to = i < stops.Count ? stops[i] : 100f;
            if (to <= from) continue;
            var cell = Object.Instantiate(title.gameObject, title.transform.parent).GetComponent<TMPro.TextMeshPro>();
            if (cell == null) continue;
            cell.gameObject.name = "ForgeUI.Cell";
            cell.transform.SetParent(row.transform, true);
            var scale = cell.transform.localScale.x != 0f ? cell.transform.localScale.x : 1f;
            var columnWidth = (to - from) / 100f * width - (i < cells.Count - 1 ? MenuScreen.CellGap : 0f);
            var rect = cell.rectTransform;
            // Only the horizontal pivot moves: the row's text keeps its own vertical placement on the row's box.
            rect.pivot = new Vector2(0.5f, rect.pivot.y);
            rect.sizeDelta = new Vector2(columnWidth / scale, rect.sizeDelta.y);
            var p = cell.transform.localPosition;
            cell.transform.localPosition = new Vector3(MenuScreen.CellPad + from / 100f * width + columnWidth / 2f, p.y, p.z);
            cell.enableWordWrapping = false;
            cell.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            cell.alignment = Contains(right, i) ? TMPro.TextAlignmentOptions.MidlineRight : TMPro.TextAlignmentOptions.MidlineLeft;
            cell.text = cells[i];
        }
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return true;
        return false;
    }
    /// <summary>A button under the window, below the ones before it: the settings section button, or the red one for the main action.</summary>
    public void AddButton(UIText text, Action onClick, bool main = false)
    {
        var template = main && _screen.RedTemplate != null ? _screen.RedTemplate : _screen.SectionTemplate;
        var item = _screen.Button(template, _screen.Text(text), onClick);
        item.transform.localPosition = new Vector3(_x, MenuScreen.WindowTop - MenuScreen.WindowHeight - 70f - MenuScreen.SectionStep * _buttons++, 0f);
        MenuScreen.Bright(item, main);
    }

    internal void Finish()
    {
        _window.SetContentItems(_rows, MenuScreen.RowGap);
        _window.SetVisible(true);
    }
}

/// <summary>Opens, rebuilds and closes the one menu screen that can be open, on the settings page.</summary>
internal static class MenuScreens
{
    private static MenuBarButton? _button;
    private static MenuScreen? _open;
    private static CM_PageSettings? _page;
    private static readonly List<GameObject> PutAway = new();
    private static bool _switching;

    internal static bool IsOpen(MenuBarButton button) => _open != null && ReferenceEquals(_button, button);

    /// <summary>Switches the main menu to the settings page and builds <paramref name="button"/>'s screen there.</summary>
    internal static bool Open(MenuBarButton button)
    {
        var layer = GuiManager.MainMenuLayer;
        if (layer == null || button.BuildScreen == null) return false;
        if (layer.GetPage(eCM_MenuPage.CMP_SETTINGS)?.TryCast<CM_PageSettings>() is not { } page) return false;
        Close();
        if (layer.m_currentPageEnum != eCM_MenuPage.CMP_SETTINGS)
        {
            _switching = true;
            try { layer.ChangePage(eCM_MenuPage.CMP_SETTINGS); }
            finally { _switching = false; }
        }
        _button = button;
        _page = page;
        Build();
        return _open != null;
    }

    internal static void Rebuild()
    {
        if (_button == null || _page == null) return;
        Discard();
        Build();
    }

    private static void Build()
    {
        var page = _page!;
        MenuScreen screen;
        try { screen = Templates(page, _button!.Mod.Texts); }
        catch (Exception error)
        {
            Plugin.Error("Menu screen '" + _button!.Id + "' cannot be shown: " + error);
            Restore();
            _button = null;
            return;
        }
        try
        {
            _button.BuildScreen!(screen);
            screen.Finish();
        }
        catch (Exception error)
        {
            Plugin.Error("Menu screen '" + _button.Id + "' could not be built: " + error);
            Teardown(screen);
            Restore();
            _button = null;
            return;
        }
        _open = screen;
        Hide();
        MenuBar.ScreenChanged();
    }

    // The settings page's own pieces to copy: a section button, the red button, its first window and a row of it with an input.
    private static MenuScreen Templates(CM_PageSettings page, UITexts texts)
    {
        var holder = page.m_movingContentHolder ?? throw new InvalidOperationException("the settings page has no content holder");
        GameObject? section = null, red = null;
        for (var i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i).gameObject;
            if (child.name.StartsWith("ForgeUI.", StringComparison.Ordinal)) continue;
            if (red == null && child.name.StartsWith("CM_RedButtonFramed", StringComparison.Ordinal)) red = child;
            else if (section == null && child.name.StartsWith("CM_ButtonFramed", StringComparison.Ordinal)) section = child;
        }
        var windows = page.m_allSettingsWindows;
        var window = windows != null && windows.Count > 0 ? windows[0] : null;
        if (section == null || window == null) throw new InvalidOperationException("the settings page has not built its sections and windows yet");
        CM_SettingsItem? row = null;
        foreach (var item in window.GetComponentsInChildren<CM_SettingsItem>(true))
            if (row == null || (item.m_inputAlign != null && item.m_inputAlign.childCount > 0)) { row = item; if (item.m_inputAlign != null && item.m_inputAlign.childCount > 0) break; }
        if (row == null) throw new InvalidOperationException("the settings window has no rows to copy");
        return new MenuScreen(page, holder, texts, section, red, window, row);
    }

    /// <summary>Per frame: the screen closes when the settings page is gone, and the page's own sections and windows stay away while it
    /// is open (the page shows its current window again when it is enabled).</summary>
    internal static void Update()
    {
        if (_open == null) return;
        if (_page == null || _page.WasCollected || !_page.gameObject.activeInHierarchy) { Close(); return; }
        Hide();
    }

    internal static void Close()
    {
        if (_open == null) return;
        Discard();
        Restore();
        _button = null;
        _page = null;
        MenuBar.ScreenChanged();
    }

    private static void Discard()
    {
        if (_open == null) return;
        Teardown(_open);
        _open = null;
    }

    private static void Teardown(MenuScreen screen)
    {
        foreach (var route in screen.Routes) Input.Unroute(route);
        screen.Routes.Clear();
        foreach (var go in screen.Created)
            if (go != null && !go.WasCollected) { go.SetActive(false); Object.Destroy(go); }
        screen.Created.Clear();
        screen.Mine.Clear();
    }

    // Everything the settings page shows in its moving content besides the screen and the resolution holder.
    private static void Hide()
    {
        var holder = _page?.m_movingContentHolder;
        if (holder == null || _open == null) return;
        var keep = _page!.m_resolutionHolder != null ? _page.m_resolutionHolder.gameObject.Pointer : IntPtr.Zero;
        for (var i = 0; i < holder.childCount; i++)
        {
            var child = holder.GetChild(i).gameObject;
            if (!child.activeSelf || child.Pointer == keep || _open.Mine.Contains(child.Pointer)) continue;
            child.SetActive(false);
            PutAway.Add(child);
        }
    }

    private static void Restore()
    {
        foreach (var go in PutAway)
            if (go != null && !go.WasCollected) go.SetActive(true);
        PutAway.Clear();
    }

    /// <summary>Any page change (a menu bar button of the game, the settings button again) closes the screen first, so the page comes
    /// back as the game left it.</summary>
    internal static void PageChanging()
    {
        if (_switching) return;
        try { Close(); }
        catch (Exception error) { Plugin.Error("Closing the menu screen failed: " + error); }
    }

    [PatchGroup(PatchGroup.MenuScreen)]
    [HarmonyPatch(typeof(MainMenuGuiLayer), nameof(MainMenuGuiLayer.ChangePage))]
    private static class PageChange
    {
        [HarmonyPrefix]
        private static void Prefix() => PageChanging();
    }
}
