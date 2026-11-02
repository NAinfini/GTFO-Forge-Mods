using CellMenu;
using ForgeUI;
using UnityEngine;

/// <summary>A menu screen on the settings page: it switches to the page, puts the page's own sections and windows away (never the cursor),
/// builds from copies of them, and gives them back on any page change. These are the residue and the vanished cursor the owner saw.</summary>
internal static class MenuScreenChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var layer = new MainMenuGuiLayer();
        var rundown = new GameObject("Rundown").AddComponent<CM_PageBase>();
        var page = new GameObject("Settings").AddComponent<CM_PageSettings>();
        layer.Pages[eCM_MenuPage.CMP_RUNDOWN_NEW] = rundown;
        layer.Pages[eCM_MenuPage.CMP_SETTINGS] = page;
        layer.ChangePage(eCM_MenuPage.CMP_RUNDOWN_NEW);
        GuiManager.MainMenuLayer = layer;

        var cursor = Under(page.transform, "CM_Cursor(Clone)").AddComponent<CM_Item>();
        page.m_cursor = cursor;
        var holder = Under(page.transform, "MovingContent").transform;
        page.m_movingContentHolder = holder;
        page.m_resolutionHolder = Under(holder, "ResolutionHolder").transform;
        var section = Section(holder, "CM_ButtonFramed(Clone)");
        Section(holder, "CM_ButtonFramed(Clone)");
        Section(holder, "CM_RedButtonFramed(Clone)").gameObject.SetActive(false);
        var window = Window(holder);
        page.m_allSettingsWindows = new Il2CppSystem.Collections.Generic.List<CM_ScrollWindow> { window };

        MenuScreenWindow? built = null;
        var button = UIKit.Mod("Tests").AddMenuBarScreen("Screen.List", "LIST", screen =>
        {
            screen.AddSection("RECENT", true, () => { });
            built = screen.AddWindow("RECENT", 1160f);
            built.AddRow("ROW", () => { }, true);
            built.Columns(new[] { 25f, 75f }, 2);
            built.AddRow(new[] { "A", "B", "9" });
            built.AddButton("PLAY", () => { }, true);
        })!;

        check(button.Open() && layer.m_currentPageEnum == eCM_MenuPage.CMP_SETTINGS && built != null, "menu screen: opening switches to the settings page and builds");
        var cells = Children(holder).Where(go => go.name == "ForgeUI.ScreenWindow").SelectMany(go => go.GetComponentsInChildren<TMPro.TextMeshPro>(true)).Where(t => t.gameObject.name == "ForgeUI.Cell").ToList();
        // Row text width 1160 - 20 - 2 x 14 = 1112: columns 0-25 %, 25-75 %, 75-100 %, 12 between.
        check(cells.Count == 3 && cells[0].text == "A" && cells[1].text == "B" && cells[2].text == "9"
              && Math.Abs(cells[1].rectTransform.sizeDelta.x - (0.5f * 1112f - 12f)) < 0.01f && Math.Abs(cells[1].transform.localPosition.x - (14f + 278f + (0.5f * 1112f - 12f) / 2f)) < 0.01f
              && cells[0].overflowMode == TMPro.TextOverflowModes.Ellipsis && !cells[0].enableWordWrapping
              && cells[2].alignment == TMPro.TextAlignmentOptions.MidlineRight && cells[0].alignment == TMPro.TextAlignmentOptions.MidlineLeft,
            "menu screen: table cells are texts of their own, sized and placed by column, cut with an ellipsis, numbers right-aligned");
        var copiedItems = Children(holder).Where(go => go.name.StartsWith("ForgeUI.", StringComparison.Ordinal)).SelectMany(go => go.GetComponentsInChildren<CM_Item>(true)).Where(i => i.gameObject.name.StartsWith("ForgeUI.Screen", StringComparison.Ordinal) && !(i is CM_ScrollWindow)).ToList();
        check(copiedItems.Count > 0 && copiedItems.All(i => i.m_tooltipInfo is { UseTooptip: false, TooltipText: "" }), "menu screen: copied rows and buttons drop the hover tooltip of the setting they were copied from");
        var native = new[] { section.gameObject, window.gameObject };
        check(native.All(go => !go.activeSelf) && page.m_resolutionHolder.gameObject.activeSelf && cursor.gameObject.activeSelf,
            "menu screen: the settings sections and windows are put away, the resolution holder and the cursor never");
        var copies = Children(holder).Where(go => go.name.StartsWith("ForgeUI.", StringComparison.Ordinal)).ToList();
        check(copies.Count == 3 && copies.All(go => go.activeSelf), "menu screen: a section, a window and a button are copies on the page");

        check(button.Open() && Children(holder).Count(go => go.name.StartsWith("ForgeUI.", StringComparison.Ordinal) && go.activeSelf) == 3,
            "menu screen: opening again replaces the copies, nothing left behind");

        layer.ChangePage(eCM_MenuPage.CMP_SETTINGS);
        check(section.gameObject.activeSelf && window.gameObject.activeSelf && Children(holder).All(go => !go.name.StartsWith("ForgeUI.", StringComparison.Ordinal) || !go.activeSelf),
            "menu screen: the settings button again closes it and gives the page back");

        button.Open();
        layer.ChangePage(eCM_MenuPage.CMP_RUNDOWN_NEW);
        check(section.gameObject.activeSelf && cursor.gameObject.activeSelf, "menu screen: another page closes it and gives the settings page back");
        button.Remove();
        GuiManager.MainMenuLayer = null;
        Text(check);
        Tap(check);
    }

    // The rows' rich text: columns, colours, author text as written.
    private static void Text(Action<bool, string> check)
    {
        check(MenuText.Tint("x", MenuTone.Dim) == "<color=#FFFFFF80>x</color>" && MenuText.Hex(MenuTone.Alert) == "EB4538", "menu text: the game's menu colours");
        check(MenuText.Plain("a<b>") != "a<b>" && MenuText.Plain("ab") == "ab", "menu text: a name with < is not read as a tag");
    }

    // Control for the HUD pointer: held is on, a short tap latches, a long press does not.
    private static void Tap(Action<bool, string> check)
    {
        var key = new TapOrHold();
        check(!key.Update(false, false, false, 0f), "tap or hold: off at first");
        check(key.Update(true, true, false, 1f) && !key.Update(false, false, true, 2f), "tap or hold: on while held, a long press does not latch");
        key.Update(true, true, false, 3f);
        check(key.Update(false, false, true, 3.1f) && key.Update(false, false, false, 4f), "tap or hold: a tap latches on");
        key.Update(true, true, false, 5f);
        check(!key.Update(false, false, true, 5.1f), "tap or hold: another tap lets go");
        key.Set(true);
        check(key.On && key.Update(false, false, false, 6f), "tap or hold: set without the key");
    }
    private static GameObject Under(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    // The row's title text, copied as the game copies it.
    private static TMPro.TextMeshPro Title(Transform row)
    {
        GameObject Make() { var go = new GameObject("Title"); go.AddComponent<TMPro.TextMeshPro>(); return go; }
        var title = Make();
        title.transform.SetParent(row, false);
        title.Factory = Make;
        return title.GetComponent<TMPro.TextMeshPro>()!;
    }

    private static List<GameObject> Children(Transform parent) => Enumerable.Range(0, parent.childCount).Select(i => parent.GetChild(i).gameObject).ToList();

    private static CM_Item Section(Transform holder, string name)
    {
        var go = Under(holder, name);
        go.Factory = () => { var copy = new GameObject(name); copy.AddComponent<CM_Item>(); return copy; };
        return go.AddComponent<CM_Item>();
    }

    private static CM_ScrollWindow Window(Transform holder)
    {
        GameObject Make()
        {
            var go = new GameObject("CM_ScrollWindow(Clone)");
            var window = go.AddComponent<CM_ScrollWindow>();
            window.m_background = go.AddComponent<SpriteRenderer>();
            var content = Under(go.transform, "ContentContainer");
            window.m_contentContainer = content.AddComponent<UIClipFeeder>();
            var row = Under(content.transform, "CM_SettingsItem(Clone)");
            var item = row.AddComponent<CM_SettingsItem>();
            item.m_inputAlign = Under(row.transform, "InputAlign").transform;
            Under(item.m_inputAlign, "Toggle");
            item.m_title = Title(row.transform);
            row.Factory = () =>
            {
                var copy = new GameObject("CM_SettingsItem(Clone)");
                var copied = copy.AddComponent<CM_SettingsItem>();
                copied.m_inputAlign = Under(copy.transform, "InputAlign").transform;
                Under(copied.m_inputAlign, "Toggle");
                copied.m_title = Title(copy.transform);
                return copy;
            };
            return go;
        }
        var source = Make();
        source.transform.SetParent(holder, false);
        source.Factory = () =>
        {
            // A copy has only what the game saves with the window: not the lists its set-up made.
            var copy = Make();
            var w = copy.GetComponent<CM_ScrollWindow>()!;
            w.m_contentItems = null; w.m_nonContentItems = null; w.m_headersDict = null;
            return copy;
        };
        return source.GetComponent<CM_ScrollWindow>()!;
    }
}
