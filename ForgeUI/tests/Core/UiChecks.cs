using System.Reflection;
using CellMenu;
using ForgeUI;
using TMPro;
using UnityEngine;
using Input = ForgeUI.Input;

internal static class UiChecks
{
    internal static void Hook(Type owner, string patch, params object[] args) => owner.GetNestedType(patch, BindingFlags.NonPublic)!
        .GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    internal static void Press(CM_Item item) => typeof(Input).GetMethod("Press", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { item });
    internal static T Child<T>(Transform parent, string name) where T : Component, new()
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<T>();
    }
    internal static GameObject ButtonPrefab(bool menu = false)
    {
        var go = new GameObject();
        CM_Item item = menu ? go.AddComponent<CM_MenuBarItem>() : go.AddComponent<CM_LobbyScrollItem>();
        item.m_collider = go.AddComponent<BoxCollider2D>();
        item.m_collider.size = new(450, 55);
        Child<SpriteRenderer>(go.transform, "Background");
        var label = Child<TextMeshPro>(go.transform, "Text");
        label.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TextMeshPro>(); return copy; };
        if (item is CM_MenuBarItem barItem) barItem.m_text = label;
        go.Factory = () => ButtonPrefab(menu);
        return go;
    }
    internal static CM_ScrollWindow Window(Transform parent)
    {
        var window = Child<CM_ScrollWindow>(parent, "Window");
        window.m_background = Child<SpriteRenderer>(window.transform, "Background");
        window.m_background.Size = new(1600, 760);
        window.m_outline = window.m_background;
        window.m_contentContainer = Child<Component>(window.transform, "Content");
        return window;
    }
    internal static CM_PlayerLobbyBar Lobby()
    {
        var page = new GameObject("Loadout").AddComponent<CM_PageLoadout>();
        CM_PageLoadout.Current = page;
        var bar = Child<CM_PlayerLobbyBar>(page.transform, "LobbyBar");
        var clothes = ButtonPrefab();
        clothes.transform.SetParent(bar.transform, false);
        bar.m_clothesButton = clothes.GetComponent<CM_LobbyScrollItem>()!;
        bar.m_player = new();
        bar.m_popupScrollWindow = Window(page.transform);
        bar.m_scrollWindowCardPrefab = ButtonPrefab();
        bar.m_popupInfoBoxWeaponPrefab = new();
        page.m_playerLobbyBars = new[] { bar };
        return bar;
    }
    private sealed class Source : PickerSource
    {
        public Action? OnClosed;
        public int Closes;
        public override int SlotCount => 2;
        public override PickerCard SlotCard(int slot, bool chosen) => new() { Title = "Slot " + slot };
        public override IReadOnlyList<PickerCard> Entries(int tab) => Array.Empty<PickerCard>();
        public override PickerInfo Info(int slot, int tab, int entry) => new();
        public override string? Pick(int slot, int tab, int entry) => null;
        public override void Closed() { Closes++; OnClosed?.Invoke(); }
    }
    internal static CM_MenuBar Bar()
    {
        var page = new GameObject("MenuPage").AddComponent<CM_PageBase>();
        var bar = Child<CM_MenuBar>(page.transform, "Bar");
        bar.m_basePage = page;
        bar.m_menuBarItemPrefab = ButtonPrefab(true);
        bar.m_popupWindowPrefab = new GameObject { Factory = () => Window(new GameObject().transform).gameObject };
        bar.m_settings = UnityEngine.Object.Instantiate(bar.m_menuBarItemPrefab, bar.transform).GetComponent<CM_MenuBarItem>()!;
        bar.m_settings.SetText("SETTINGS");
        bar.m_settings.CorrectSizeToText(0, 560);
        bar.m_quitGame = UnityEngine.Object.Instantiate(bar.m_menuBarItemPrefab, bar.transform).GetComponent<CM_MenuBarItem>()!;
        bar.m_quitGame.SetText("QUIT");
        bar.m_quitGame.CorrectSizeToText(0, 560);
        bar.m_quitGame.SetXOffset(2400);
        Hook(typeof(MenuBar), "BarSetup", bar);
        return bar;
    }
    internal static CM_MenuBarItem CustomItem(CM_MenuBar bar, string id) => bar.GetComponentsInChildren<CM_MenuBarItem>(true)
        .Single(item => !item.WasCollected && item.gameObject.name == "ForgeUI.MenuBar." + id);
    private static bool Centered(CM_MenuBar bar, MenuBarPage page)
    {
        var left = bar.transform.InverseTransformPoint(bar.m_settings!.m_collider.bounds.min).x;
        var right = bar.transform.InverseTransformPoint(bar.m_quitGame!.m_collider.bounds.max).x;
        var frame = page.Window.m_background.bounds;
        var center = bar.transform.InverseTransformPoint((frame.min + frame.max) * 0.5f).x;
        return Math.Abs(center - (left + right) / 2) < 0.01f;
    }

    internal static void Run(Action<bool, string> check)
    {
        var bar = Lobby();
        LobbyButton? b = null;
        var bCalls = 0;
        var a = UIKit.Mod("Tests").AddLobbyButton("Reentry.A", "A", _ => { }, () => { b!.Remove(); return ""; })!;
        b = UIKit.Mod("Tests").AddLobbyButton("Reentry.B", "B", _ => { }, visible: _ => { bCalls++; return true; })!;
        LobbyButtons.Update();
        LobbyButtons.Update();
        check(bCalls == 0 && !bar.GetComponentsInChildren<CM_LobbyScrollItem>(true).Any(i => i.gameObject.name.EndsWith("Reentry.B")),
            "removed later lobby registration never recreated from stale snapshot");
        a.Remove();
        LobbyButton? self = null;
        var remove = false;
        self = UIKit.Mod("Tests").AddLobbyButton("Self", "SELF", _ => { }, visible: _ => { if (remove) self!.Remove(); return true; })!;
        LobbyButtons.Update();
        var selfCopy = bar.GetComponentsInChildren<CM_LobbyScrollItem>(true).Single(i => i.gameObject.name.EndsWith("Self"));
        remove = true;
        LobbyButtons.Update();
        check(!selfCopy.gameObject.activeSelf, "self-removal in Visible immediately disables existing copy");
        LobbyButton? replacement = null;
        a = UIKit.Mod("Tests").AddLobbyButton("Reentry.A", "A", _ => { }, () =>
        {
            if (replacement == null) { b!.Remove(); replacement = UIKit.Mod("Tests").AddLobbyButton("Reentry.B", "NEW", _ => { }); }
            return "";
        })!;
        b = UIKit.Mod("Tests").AddLobbyButton("Reentry.B", "OLD", _ => { })!;
        LobbyButtons.Update();
        LobbyButtons.Update();
        check(bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "NEW") && !bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "OLD"),
            "ID replacement during note callback displays only replacement");
        a.Remove(); replacement!.Remove();

        var sourceA = new Source();
        var sourceB = new Source();
        var sourceC = new Source();
        sourceA.OnClosed = () => Picker.Open(bar, sourceB);
        Picker.Open(bar, sourceA);
        Picker.Open(bar, sourceC);
        check(UIKit.IsShowing(sourceB) && !UIKit.IsShowing(sourceC) && sourceA.Closes == 1 && sourceB.Closes == 0,
            "newer picker requested by Closed wins over outer Open");
        var hit = bar.m_popupScrollWindow.m_nonContentItems!.Single(i => i.gameObject.name == "ForgeUI.ColumnHitArea");
        check(hit.m_collider.size.x > 330 && hit.m_collider.size.y == 760 &&
              bar.m_popupScrollWindow.PopupChildItems.Contains(hit), "column background is a sized non-content popup child");
        Press(hit);
        check(UIKit.IsShowing(sourceB), "pressing column background leaves picker open");
        UIKit.ClosePicker();
        check(sourceB.Closes == 1 && bar.m_popupScrollWindow.m_nonContentItems!.Count == 0,
            "picker closes once and removes column and slot hit targets");
        foreach (var observeFirst in new[] { false, true })
        {
            var source = new Source();
            Picker.Open(bar, source);
            if (observeFirst) Picker.Tick();
            CM_PageLoadout.Current!.gameObject.SetActive(false);
            Picker.Tick();
            CM_PageLoadout.Current.gameObject.SetActive(true);
            Picker.Tick();
            check(Picker.Current == null && !bar.m_popupScrollWindow.gameObject.activeSelf && source.Closes == 1,
                "parent hide closes picker permanently, first tick observed=" + observeFirst);
        }

        var menu = Bar();
        MenuBarPage? built = null;
        var registration = UIKit.Mod("Tests").AddMenuBarPage("Review.Page", "PAGE", p => built = p)!;
        MenuBar.Update();
        var item = CustomItem(menu, registration.Id);
        Press(item);
        var page = built!;
        check(page.IsOpen && Centered(menu, page), "menu page opens centered");
        menu.m_basePage.gameObject.SetActive(false);
        MenuBar.Update();
        menu.m_basePage.gameObject.SetActive(true);
        check(!page.IsOpen && !page.Window.gameObject.activeSelf && item.m_alphaTextOnHover,
            "switching native page closes cached page and resets highlight");
        Time.frameCount++;
        Press(item);
        registration.Title = new string('L', 40);
        menu.m_quitGame!.SetXOffset(750);
        MenuBar.Update();
        check(!page.IsOpen && !item.gameObject.activeSelf, "overflow hides button and closes its open page");
        registration.Title = "PAGE";
        menu.m_quitGame.SetXOffset(2400);
        MenuBar.Update();
        Time.frameCount++;
        Press(item);
        menu.m_quitGame.SetXOffset(2000);
        Screen.width = 2100;
        MenuBar.Update();
        check(page.IsOpen && Centered(menu, page), "resolution change recenters an open cached menu page");
        menu.m_quitGame.SetXOffset(1900);
        MenuBar.Update();
        check(Centered(menu, page), "late native anchor movement is followed without another resolution change");
        page.Close();
        menu.m_quitGame.SetXOffset(1800);
        Time.frameCount++;
        Press(item);
        check(page.IsOpen && Centered(menu, page), "reopening recenters after native anchor movement");

        menu.m_expText = Child<TextMeshPro>(menu.transform, "ExpeditionText");
        menu.m_expText.Size = new(400, 55);
        menu.m_expText.transform.localPosition = new(500, 0, 0);
        Hook(typeof(MenuBar), "BarLayout", menu);
        check(!item.gameObject.activeSelf && !page.IsOpen, "visible expedition text bounds prevent button overlap");
        menu.m_expText.gameObject.SetActive(false);
        menu.m_statusAlign = Child<Component>(menu.transform, "StatusAlign").transform;
        var status = Child<TextMeshPro>(menu.m_statusAlign, "MenuStatus");
        status.Size = new(400, 55);
        status.transform.localPosition = new(500, 0, 0);
        Hook(typeof(MenuBar), "BarLayout", menu);
        check(!item.gameObject.activeSelf, "visible menu status descendants reserve space");
        status.gameObject.SetActive(false);
        Hook(typeof(MenuBar), "BarLayout", menu);
        check(item.gameObject.activeSelf, "empty status alignment reserves no phantom space");

        var nativeListener = new Il2CppSystem.Object();
        menu.m_basePage.m_clickAnywhereListeners.Add(nativeListener);
        registration.Remove();
        MenuBar.Update();
        check(menu.m_basePage.m_clickAnywhereListeners.SequenceEqual(new[] { nativeListener }),
            "removing a page removes its native listener and preserves other listeners");
        var stable = true;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            registration = UIKit.Mod("Tests").AddMenuBarPage("Review.Page", "PAGE", p => built = p)!;
            MenuBar.Update();
            Time.frameCount++;
            Press(CustomItem(menu, registration.Id));
            stable &= menu.m_basePage.m_clickAnywhereListeners.Count == 2;
            registration.Remove();
            MenuBar.Update();
            stable &= menu.m_basePage.m_clickAnywhereListeners.Count == 1;
        }
        check(stable, "repeated page registration and destruction do not accumulate listeners");
        registration = UIKit.Mod("Tests").AddMenuBarPage("Review.Page", "PAGE", p => built = p)!;
        MenuBar.Update();
        Time.frameCount++;
        Press(CustomItem(menu, registration.Id));
        UnityEngine.Object.Destroy(built!.Window.gameObject);
        registration.Remove();
        MenuBar.Update();
        check(menu.m_basePage.m_clickAnywhereListeners.SequenceEqual(new[] { nativeListener }),
            "listener ownership survives a window destroyed before registration removal");
        check(!Plugin.Errors.Any(e => !e.Contains("does not fit")), "UI scenarios report no unexpected callback or construction errors");
        RetryChecks(check, menu);

        var nativeSource = new Source();
        Picker.Open(bar, nativeSource);
        typeof(Picker).GetNestedType("BarShowOwnPopup", BindingFlags.NonPublic)!
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { bar });
        check(Picker.Current == null && nativeSource.Closes == 1 && bar.m_popupScrollWindow.gameObject.activeSelf &&
              bar.m_popupScrollWindow.m_nonContentItems!.Count == 0,
            "native picker entry releases custom controls before native resize without hiding the shared window");
        var closed = CloseTemporarySource(bar, check);
        Picker.Tick();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        check(closed.IsAlive, "same-frame close guard retains its source until the closing frame ends");
        Time.frameCount++;
        Picker.Tick();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        check(!closed.IsAlive, "closed temporary picker source is collectable after the next driver tick");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CloseTemporarySource(CM_PlayerLobbyBar bar, Action<bool, string> check)
    {
        var source = new Source();
        Picker.Open(bar, source);
        UIKit.ClosePicker();
        UIKit.TogglePicker(bar, source);
        check(Picker.Current == null && source.Closes == 1, "same-frame toggle does not reopen the just-closed picker");
        return new WeakReference(source);
    }

    private static void RetryChecks(Action<bool, string> check, CM_MenuBar menu)
    {
        var errors = Plugin.Errors.Count;
        var prefab = menu.m_menuBarItemPrefab;
        GameObject? failedClone = null;
        menu.m_menuBarItemPrefab = new GameObject { Factory = () => failedClone = new GameObject() };
        var button = UIKit.Mod("Tests").AddMenuBarButton("Retry.Button", "INITIAL", () => { })!;
        MenuBar.Update();
        check(Plugin.Errors.Count == errors + 1 && failedClone!.WasCollected && !failedClone.activeSelf,
            "failed button construction is logged and its partial clone is destroyed");
        menu.m_menuBarItemPrefab = prefab;
        MenuBar.Update();
        var item = CustomItem(menu, button.Id);
        check(item.gameObject.activeSelf && item.m_text!.text == "INITIAL",
            "button construction retries next tick without another registration change");

        errors = Plugin.Errors.Count;
        var text = item.m_text;
        item.m_text = null;
        button.Title = "UPDATED";
        MenuBar.Update();
        check(Plugin.Errors.Count == errors + 1, "failed native title write is reported");
        item.m_text = text;
        MenuBar.Update();
        check(text!.text == "UPDATED", "failed title write retries next tick without another title change");

        errors = Plugin.Errors.Count;
        var collider = menu.m_settings!.m_collider;
        menu.m_settings.m_collider = null!;
        item.transform.localPosition = new(-10000, 0, 0);
        button.Title = "LAYOUT";
        MenuBar.Update();
        check(Plugin.Errors.Count == errors + 1 && item.transform.localPosition.x == -10000,
            "failed native layout is reported without claiming synchronization");
        menu.m_settings.m_collider = collider;
        MenuBar.Update();
        check(item.transform.localPosition.x >= 280, "failed layout retries next tick at unchanged resolution");

        errors = Plugin.Errors.Count;
        menu.m_settings.m_collider = null!;
        item.transform.localPosition = new(-10000, 0, 0);
        Screen.width++;
        MenuBar.Update();
        check(Plugin.Errors.Count == errors + 1 && item.transform.localPosition.x == -10000,
            "resolution-only native layout failure is reported");
        menu.m_settings.m_collider = collider;
        MenuBar.Update();
        check(item.transform.localPosition.x >= 280, "failed resolution-only layout retries without another resize");
        button.Remove();
        MenuBar.Update();

        var baseline = menu.m_basePage.m_clickAnywhereListeners.ToArray();
        var builds = 0;
        MenuBarPage? failedPage = null, completedPage = null;
        var registration = UIKit.Mod("Tests").AddMenuBarPage("Retry.Page", "RETRY", page =>
        {
            builds++;
            if (builds == 1)
            {
                failedPage = page;
                Child<Component>(page.Root, "Partial content");
                throw new InvalidOperationException("dependency not ready");
            }
            completedPage = page;
            Child<Component>(page.Root, "Complete content");
        })!;
        MenuBar.Update();
        item = CustomItem(menu, registration.Id);
        errors = Plugin.Errors.Count;
        Time.frameCount++;
        Press(item);
        check(Plugin.Errors.Count == errors + 1 && Plugin.Errors[^1].Contains(registration.Id) &&
              failedPage != null && !failedPage.IsOpen && failedPage.Window.WasCollected,
            "failed page build logs its ID and destroys the unshown partial page");
        check(menu.m_basePage.m_clickAnywhereListeners.SequenceEqual(baseline),
            "failed page build releases its native click-anywhere listener");
        Time.frameCount++;
        Press(item);
        check(builds == 2 && completedPage?.IsOpen == true && !ReferenceEquals(failedPage, completedPage) &&
              completedPage.Root.GetChild(0).gameObject.name == "Complete content",
            "next click rebuilds a fresh page after its dependency becomes ready");
        completedPage!.Close();
        Time.frameCount++;
        Press(item);
        check(builds == 2 && completedPage.IsOpen, "successful page build remains cached on reopen");
        registration.Remove();
        MenuBar.Update();

        var windowPrefab = menu.m_popupWindowPrefab;
        CM_ScrollWindow? failedWindow = null;
        menu.m_popupWindowPrefab = new GameObject { Factory = () =>
        {
            failedWindow = Window(new GameObject().transform);
            failedWindow.m_background = null!;
            return failedWindow.gameObject;
        } };
        registration = UIKit.Mod("Tests").AddMenuBarPage("Retry.Window", "WINDOW", _ => builds++)!;
        MenuBar.Update();
        item = CustomItem(menu, registration.Id);
        errors = Plugin.Errors.Count;
        Time.frameCount++;
        Press(item);
        check(Plugin.Errors.Count == errors + 1 && failedWindow!.WasCollected &&
              menu.m_basePage.m_clickAnywhereListeners.SequenceEqual(baseline) && builds == 2,
            "failed native window setup destroys its clone and listener before invoking the builder");
        menu.m_popupWindowPrefab = windowPrefab;
        Time.frameCount++;
        Press(item);
        check(builds == 3 && menu.m_basePage.m_clickAnywhereListeners.Count == baseline.Length + 1,
            "native window construction retries on the next click");
        registration.Remove();
        MenuBar.Update();
        check(menu.m_basePage.m_clickAnywhereListeners.SequenceEqual(baseline),
            "retry scenarios leave only the original native listeners");
    }
}
