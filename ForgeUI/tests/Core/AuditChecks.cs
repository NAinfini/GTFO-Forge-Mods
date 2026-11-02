using CellMenu;
using ForgeUI;
using TMPro;
using UnityEngine;

/// <summary>Regression checks for the audit fixes: author text, per-frame note reads, missing native pieces, click callbacks,
/// page text, and HUD elements (build, placement, stacking, rebuild, failures).</summary>
internal static class AuditChecks
{
    private sealed class ClickSource : PickerSource
    {
        public readonly List<string> Events = new();
        public bool Throw;
        public override int SlotCount => 2;
        public override PickerCard SlotCard(int slot, bool chosen) { if (chosen) Events.Add("slot:" + slot); return new() { Title = "SLOT<" + slot + ">" }; }
        public override IReadOnlyList<PickerCard> Entries(int tab) => new[] { new PickerCard { Title = "A" }, new PickerCard { Title = "B<i>", Subtitle = "SUB" } };
        public override string? Pick(int slot, int tab, int entry) { Events.Add("pick:" + entry); return null; }
        public override void Clicked(PickerClick click)
        {
            Events.Add("click:" + click);
            if (Throw) throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Only the members a picker cannot do without; details come from the default.</summary>
    private sealed class MinimalSource : PickerSource
    {
        public override int SlotCount => 1;
        public override PickerCard SlotCard(int slot, bool chosen) => new() { Title = "HELD" };
        public override IReadOnlyList<PickerCard> Entries(int tab) => new[] { new PickerCard { Title = "A" }, new PickerCard { Title = "B", Subtitle = "SUB" } };
        public override string? Pick(int slot, int tab, int entry) => null;
    }

    internal static void Run(Action<bool, string> check)
    {
        TextChecks(check);
        LobbyChecks(check);
        PickerChecks(check);
        PageChecks(check);
        HudChecks(check);
    }

    private static void TextChecks(Action<bool, string> check)
    {
        const string plain = "ORDNANCE 2/4";
        check(ReferenceEquals(RichText.Plain(plain), plain), "text without '<' is passed through unchanged");
        check(RichText.Plain("A<b>B") == "<noparse>A<b>B</noparse>", "author '<' is not read as a tag");
        check(!RichText.Plain("x</noparse><color=red>y").Replace("<noparse>", "").Contains("</noparse><color"),
            "author text cannot close the noparse block");
        check(RichText.Plain(null) == "", "null author text shows nothing");
        // The game's objective text as R5A2 sends it: its tags go before the kit breaks it into lines (a tag cut by a break showed as text).
        check(RichText.StripTags("<color=#ffffffa6>>Gather information about the location of <color=orange>HISEC_CARGO_253</color></indent>")
              == ">Gather information about the location of HISEC_CARGO_253", "the game's tags are taken out, its text kept");
        check(RichText.StripTags("R5A2: Recollect - <color=orange>Main</color>") == "R5A2: Recollect - Main" && RichText.StripTags("a < b > c") == "a < b > c"
              && ReferenceEquals(RichText.StripTags(plain), plain), "a '<' that opens no tag stays, text without one is passed through");
        // The alarm door's prompt as the game's text block writes it (InGame.InteractionPrompt.SecurityDoor, "Start Security Scan Sequence
        // <color=red>[WARNING:{0} DETECTED]</color>"): the warning stays red, in the kit's red.
        check(RichText.Toned("Start Security Scan Sequence <color=red>[WARNING:Class II Cluster Alarm DETECTED]</color>", "FFA040", "FF4040")
              == "Start Security Scan Sequence <color=#FF4040>[WARNING:Class II Cluster Alarm DETECTED]</color>", "a red stretch of a prompt keeps the warning colour");
        check(RichText.Toned("Deactivate alarm on <color=orange>ZONE_42</color>", "FFA040", "FF4040") == "Deactivate alarm on <color=#FFA040>ZONE_42</color>"
              && RichText.Toned("<color=#FF2020>x</color><color=#ffffffa6>y</color>", "A", "R") == "<color=#R>x</color>y",
            "another colour is the highlight, a hex red is a warning, a grey is plain");
        check(RichText.Toned("a < b <size=80%>c</size>", "A", "R") == "<noparse>a < b c</noparse>" && ReferenceEquals(RichText.Toned(plain, "A", "R"), plain),
            "other tags go, a '<' that opens no tag is shown as written");
    }

    private static void LobbyChecks(Action<bool, string> check)
    {
        var bar = UiChecks.Lobby();
        CM_PageLoadout.Current!.m_playerLobbyBars = new[] { bar, bar, bar };
        var notes = 0;
        var button = UIKit.Mod("Tests").AddLobbyButton("Audit.Note", "A<b>B", _ => { }, () => { notes++; return "1/2"; }, _ => true)!;
        Time.frameCount = 100;
        LobbyButtons.Update();
        var once = notes == 1;
        Time.frameCount = 101;
        LobbyButtons.Update();
        check(once && notes == 2, "note is read once per frame however many bars show the button");
        var shown = bar.GetComponentsInChildren<TextMeshPro>().ToArray();
        check(shown.Count(t => t.text == "A<b>B" && !t.richText && !t.enableWordWrapping) == 1 && shown.Count(t => t.text == "1/2") == 1,
            "lobby title shown as written on one line, the note beside it");
        var longTitle = new string('W', 80);
        var wide = UIKit.Mod("Tests").AddLobbyButton("Audit.Long", longTitle, _ => { }, visible: _ => true)!;
        LobbyButtons.Update();
        check(bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == longTitle && t.overflowMode == TextOverflowModes.Ellipsis),
            "a lobby title too long for the button is cut with an ellipsis");
        wide.Remove();
        button.Remove();

        var bare = UiChecks.Lobby();
        bare.m_clothesButton = null!;
        var lonely = UIKit.Mod("Tests").AddLobbyButton("Audit.Bare", "X", _ => { })!;
        LobbyButtons.Update();
        check(Plugin.Errors.Any(e => e.Contains("no APPAREL button")), "a bar without APPAREL is reported, not silently skipped");
        lonely.Remove();
    }

    private static void PickerChecks(Action<bool, string> check)
    {
        var minimal = new MinimalSource();
        check(minimal.Info(0, 0, 1).Title == "B" && minimal.Info(0, 0, 1).Subtitle == "SUB" && minimal.Info(0, 0, -1).Title == "HELD" &&
              minimal.Info(0, 0, 9).Title == "", "default details show the focused card, or the slot's for -1");

        var bar = UiChecks.Lobby();
        bar.m_scrollWindowCardPrefab = CardPrefab();
        var source = new ClickSource();
        Picker.Open(bar, source);
        var window = bar.m_popupScrollWindow;
        var slot = window.GetComponentsInChildren<CM_LobbyScrollItem>(true).Single(i => i.gameObject.name == "ForgeUI.Slot2");
        var card = window.GetComponentsInChildren<CM_LobbyScrollItem>(true).Single(i => i.gameObject.name == "ForgeUI.Entry1");
        source.Events.Clear();
        UiChecks.Press(slot);
        check(source.Events.Count > 1 && source.Events[0] == "click:Slot" && source.Events.Contains("slot:1"), "slot press reports a Slot click before choosing");
        source.Events.Clear();
        UiChecks.Press(card);
        check(source.Events.Count > 1 && source.Events[0] == "click:Entry" && source.Events[1] == "pick:1", "card press reports an Entry click before the pick");
        source.Throw = true;
        source.Events.Clear();
        UiChecks.Press(card);
        check(source.Events.Contains("pick:1") && Plugin.Errors.Any(e => e.Contains("failed on a Entry click")),
            "a throwing click callback is logged and the pick still happens");
        check(slot.GetComponentsInChildren<TextMeshPro>().Count(l => l.text == "SLOT<1>" && !l.richText) == 1, "a slot's name is shown as written");
        var rowTexts = card.GetComponentsInChildren<TextMeshPro>();
        check(rowTexts.Count(t => t.text == "B<i>" && !t.richText) == 1 && rowTexts.All(t => t.text != "B<i>" || t != card.m_nameText),
            "a list row's title is drawn as written by the row, not by the game's label");
        UIKit.ClosePicker();
        FallbackClipChecks(check);
    }

    /// <summary>Chinese titles scrolled past the list's edge stayed on screen while their rows were cut: their glyphs come from a
    /// fallback font, drawn by sub-meshes the text made only when it rendered, after the list's clipping had taken its pieces.</summary>
    private static void FallbackClipChecks(Action<bool, string> check)
    {
        var bar = UiChecks.Lobby();
        bar.m_scrollWindowCardPrefab = CardPrefab();
        var window = bar.m_popupScrollWindow;
        var feeder = window.m_uiClipFeeder = window.gameObject.AddComponent<UIClipFeeder>();
        var source = new FallbackSource();
        Picker.Open(bar, source);
        var card = window.GetComponentsInChildren<CM_LobbyScrollItem>(true).Single(i => i.gameObject.name == "ForgeUI.Entry0");
        static MeshRenderer[] SubMeshes(Component row) => row.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.gameObject.name == "TMP SubMesh").ToArray();
        var first = SubMeshes(card);
        check(first.Length == 1 && first.All(feeder.Seen.Contains), "a row's fallback-font glyphs are built with the row and handed to the list's clipping");
        source.Title = "补给舱 弹药";
        UiChecks.Press(card);
        var after = SubMeshes(card);
        check(after.Length == 2 && after.All(feeder.Seen.Contains), "a title that needs the fallback font later hands its new glyphs to the clipping too");
        UIKit.ClosePicker();
    }

    private sealed class FallbackSource : PickerSource
    {
        public string Title = "AMMO";
        public override int SlotCount => 1;
        public override PickerCard SlotCard(int slot, bool chosen) => new() { Title = "HELD" };
        public override IReadOnlyList<PickerCard> Entries(int tab) => new[] { new PickerCard { Title = Title, Subtitle = "补给" } };
        public override string? Pick(int slot, int tab, int entry) => null;
    }

    private static GameObject CardPrefab()
    {
        var go = UiChecks.ButtonPrefab();
        var card = go.GetComponent<CM_LobbyScrollItem>()!;
        card.m_nameText = UiChecks.Child<TextMeshPro>(go.transform, "Name");
        card.m_nameText.richText = true;
        card.m_nameText.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TextMeshPro>(); return copy; };
        go.Factory = CardPrefab;
        return go;
    }

    private static CM_ScrollWindow PageWindow()
    {
        var window = UiChecks.Window(new GameObject().transform);
        var cross = UiChecks.Child<CM_Item>(window.transform, "CrossBox");
        cross.m_collider = cross.gameObject.AddComponent<BoxCollider2D>();
        var header = UiChecks.Child<CM_ScrollWindowHeader>(window.transform, "Header");
        var text = UiChecks.Child<TextMeshPro>(header.transform, "HeaderText");
        text.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TextMeshPro>(); return copy; };
        window.m_headers = new[] { header };
        return window;
    }

    private static void PageChecks(Action<bool, string> check)
    {
        var menu = UiChecks.Bar();
        menu.m_popupWindowPrefab = new GameObject { Factory = () => PageWindow().gameObject };
        var clicks = 0;
        MenuBarPage? page = null;
        TextMeshPro? body = null;
        var registration = UIKit.Mod("Tests").AddMenuBarPage("Audit.Page", "P<1>", p => { page = p; body = p.AddText("hello"); }, () => clicks++)!;
        MenuBar.Update();
        var item = UiChecks.CustomItem(menu, registration.Id);
        check(item.m_text!.text == "<noparse>P<1></noparse>", "menu bar title shown as written");
        Time.frameCount = 200;
        UiChecks.Press(item);
        check(clicks == 1 && page != null && page.IsOpen, "page button press calls the click callback and opens the page");
        check(body != null && body.text == "hello" && body.enableWordWrapping && body.alignment == TextAlignmentOptions.TopLeft &&
              body.transform.parent == page!.Root && body.rectTransform.sizeDelta.x > 0, "AddText puts wrapped heading-style text at the top left of the page");
        var cross = page!.Window.GetComponentsInChildren<CM_Item>(true).Single(i => i.gameObject.name == "CrossBox");
        UiChecks.Press(cross);
        check(clicks == 2 && !page.IsOpen, "page close box calls the click callback and closes the page");
        registration.Remove();
        MenuBar.Update();
    }

    private static PUI_Inventory WeaponList(out PUI_InventoryItem item, out SpriteRenderer icon)
    {
        var list = new GameObject("Inventory").AddComponent<PUI_Inventory>();
        item = UiChecks.Child<PUI_InventoryItem>(list.transform, "Item");
        var name = UiChecks.Child<TextMeshPro>(item.transform, "Name");
        name.gameObject.layer = 5;
        name.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
        name.transform.localPosition = new Vector3(50f, -30f, 0f);
        name.fontSize = 20f;
        name.font = new TMP_FontAsset();
        name.font.Chars.Add('∞');
        name.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TextMeshPro>(); return copy; };
        var renderer = name.gameObject.AddComponent<MeshRenderer>();
        renderer.sortingLayerID = 3;
        renderer.sortingOrder = 10;
        renderer.enabled = false; // bounds come from the icon and the text itself
        icon = UiChecks.Child<SpriteRenderer>(item.transform, "Icon");
        icon.sharedMaterial = new Material();
        icon.Size = new Vector2(200f, 40f);
        icon.transform.localPosition = new Vector3(50f, -30f, 0f);
        item.m_slim_archetypeName = name;
        list.m_inventorySlots[0] = item;
        return list;
    }

    private static void HudChecks(Action<bool, string> check)
    {
        Time.time = 0f;
        HudScreen.Measurable = true;
        HudScreen.CentreX = HudScreen.CentreY = 0f;
        HudScreen.HalfWidth = 960f;
        HudScreen.HalfHeight = 540f;
        var ui = UIKit.Mod("Audit");
        check(ReferenceEquals(UIKit.Mod("Audit"), ui) && ui.Id == "Audit", "a mod gets the same handle every time");
        var threwName = false;
        try { UIKit.Mod("bad/mod"); }
        catch (ArgumentException) { threwName = true; }
        check(threwName, "a mod ID with a slash is refused");
        var builds = 0;
        TextMeshPro? text = null;
        SpriteRenderer? sprite = null;
        var a = ui.AddHud("HudA", new HudSlot(HudRegion.WeaponList, 10), hud =>
        {
            builds++;
            text = hud.AddText();
            sprite = hud.AddSprite(UIKitSprites.Pixel, -2);
            sprite.Size = new Vector2(100f, 40f);
            sprite.transform.localPosition = new Vector3(-50f, -20f, 0f);
        })!;
        check(a.Id == "Audit/HudA" && a.Mod == "Audit" && a.Name == "HudA" && a.FlowX == -1 && a.FlowY == -1, "a HUD element is named mod/name and grows the way its region does");
        check(ui.AddHud("HudA", new HudSlot(HudRegion.WeaponList), _ => { }) == null && Plugin.Errors.Any(e => e.Contains("'Audit/HudA' is already registered")),
            "a name taken in the same mod is refused and logged");
        var other = UIKit.Mod("Other").AddHud("HudA", new HudSlot(HudRegion.World), _ => { });
        check(other != null && other.Id == "Other/HudA", "another mod may use the same name");
        other!.Remove();
        check(ui.AddHud("Hud/A", new HudSlot(HudRegion.WeaponList), _ => { }) == null, "a name with a slash is refused");
        var threw = false;
        try { a.AddText(); }
        catch (InvalidOperationException) { threw = true; }
        check(threw, "adding a piece before the HUD exists is an explicit error");

        GuiManager.PlayerLayer = null;
        Hud.Update();
        check(builds == 0 && a.Root == null, "no level HUD: nothing built");

        var list = WeaponList(out var item, out var icon);
        var source = item.m_slim_archetypeName;
        item.m_slim_archetypeName = null;
        GuiManager.PlayerLayer = new PlayerGuiLayer { Inventory = list };
        Hud.Update();
        Hud.Update();
        check(builds == 0, "a weapon list without text builds nothing");

        item.m_slim_archetypeName = source;
        Hud.Update();
        var root = a.Root;
        check(builds == 1 && root != null && root.parent == list.transform && root.gameObject.layer == 5 && a.WeaponList == list,
            "element built once under the weapon list on the HUD's layer");
        check(root != null && root.localPosition == new Vector3(150f, -50f - Hud.Gap, 0f) && a.State == LayoutState.Placed, "element sits right-aligned under the lowest weapon row");
        check(text != null && text.transform.localScale.x == 0.5f && text.alignment == TextAlignmentOptions.MidlineLeft &&
              text.rectTransform.pivot.x == 0f && text.text == "" && text.richText, "HUD text copies the weapon-name scale, left edge on its position, empty");
        check(sprite != null && sprite.sortingLayerID == 3 && sprite.sortingOrder == 8 && sprite.sharedMaterial == icon.sharedMaterial &&
              sprite.gameObject.layer == 5, "HUD sprite uses the weapon list's material, layer and sorting plus its order");
        check(a.HasGlyph('∞') && !a.HasGlyph('€') && a.FontSize == 20f, "glyph check and font size come from the HUD font");
        HudFocus.Away = true;
        Hud.Update();
        var awayHidden = root != null && !root.gameObject.activeSelf;
        HudFocus.Away = false;
        Hud.Update();
        check(awayHidden && root != null && root.gameObject.activeSelf && builds == 1, "a game screen (map, terminal, menu) hides every element; back in play they return, not rebuilt");

        var b = ui.AddHud("HudB", new HudSlot(HudRegion.WeaponList, 0), hud => hud.AddText())!;
        Time.time = 1f;
        Hud.Update();
        check(b.Root != null && b.Root.localPosition.y == -50f - Hud.Gap - 40f - Hud.Gap, "a lower priority element stacks under the first one's drawing");
        var c = ui.AddHud("HudC", new HudSlot(HudRegion.WeaponList, 20, 80f, 30f), hud => hud.AddText())!;
        Time.time = 1.1f;
        Hud.Update();
        check(c.Root!.localPosition.y == -50f - Hud.Gap && a.Root!.localPosition.y == -50f - Hud.Gap - 30f - Hud.Gap, "a higher priority element takes the place nearest the weapon list");
        c.Remove();
        a.Visible = false;
        Time.time = 1.2f;
        Hud.Update();
        check(!a.Root!.gameObject.activeSelf && b.Root!.localPosition.y == -50f - Hud.Gap, "a hidden element takes no room");
        Plugin.HudOffsetX = 5f;
        Hud.Update();
        check(b.Root!.localPosition.x == 155f, "the shared HUD offset moves the region");
        Plugin.HudOffsetX = 0f;
        a.Visible = true;

        var tall = ui.AddHud("Tall", new HudSlot(HudRegion.WeaponList, -5, 50f, 2000f), hud => hud.AddText())!;
        Time.time = 1.3f;
        Hud.Update();
        check(tall.State == LayoutState.Collapsed && !tall.Root!.gameObject.activeSelf,
            "an element its region cannot hold collapses");
        tall.Remove();

        HudScreen.Measurable = false;
        Time.time = 1.4f;
        Hud.Update();
        check(!b.Root!.gameObject.activeSelf, "without a measured screen nothing is laid out or shown");
        HudScreen.Measurable = true;

        icon.gameObject.SetActive(false);
        item.m_slim_archetypeName!.gameObject.SetActive(false);
        Time.time = 2f;
        Hud.Update();
        check(!a.Root!.gameObject.activeSelf && !b.Root!.gameObject.activeSelf && a.State == LayoutState.Unavailable, "elements wait while the weapon list draws nothing");

        Time.time = 3.5f;
        Hud.Update();

        var oldRoot = a.Root!;
        var next = WeaponList(out _, out _);
        GuiManager.PlayerLayer!.Inventory = next;
        Time.time = 4f;
        Hud.Update();
        check(builds == 2 && oldRoot.gameObject.Destroyed && a.Root != null && a.Root.parent == next.transform && a.Root.gameObject.activeSelf,
            "a new weapon list rebuilds every element and drops the old pieces");

        var failures = 0;
        var broken = ui.AddHud("HudBroken", new HudSlot(HudRegion.WeaponList), _ => { failures++; throw new InvalidOperationException("bad build"); })!;
        Hud.Update();
        Hud.Update();
        check(failures == 1 && broken.Root == null && Plugin.Errors.Any(e => e.Contains("'Audit/HudBroken' failed to build")),
            "a failing build is logged once and not retried on the same HUD");
        broken.Remove();

        HudElement? self = null;
        self = ui.AddHud("HudSelf", new HudSlot(HudRegion.WeaponList), _ => self!.Remove())!;
        var before = Plugin.Errors.Count;
        Hud.Update();
        check(self.Root == null && Plugin.Errors.Count == before, "an element removing itself while building is harmless");

        // A vitals element sits under the weapon list's transform, centred on the game's health bar just above the vitals, and learns the bar's width.
        var status = new GameObject("Status").AddComponent<PUI_LocalPlayerStatus>();
        status.transform.localScale = new Vector3(2f, 2f, 1f);
        status.transform.localPosition = new Vector3(0f, -400f, 0f);
        var health = UiChecks.Child<SpriteRenderer>(status.transform, "Health");
        health.Size = new Vector2(160f, 4f);
        health.transform.localPosition = new Vector3(0f, -3f, 0f);
        status.m_health1 = health;
        GuiManager.PlayerLayer!.m_playerStatus = status;
        var barBuilds = 0;
        var onBar = ui.AddHud("HudBar", new HudSlot(HudRegion.Vitals, 0, 200f, 20f), hud => { barBuilds++; hud.AddText(); })!;
        Time.time = 5f;
        Hud.Update();
        var onBarRoot = onBar.Root;
        check(barBuilds == 1 && onBarRoot != null && onBarRoot.parent == next.transform && onBar.FlowY == 1, "a vitals element is built under the weapon list and grows upwards");
        check(onBarRoot != null && Math.Abs(onBarRoot.localPosition.x) < 1e-3f && Math.Abs(onBarRoot.localPosition.y - (-402f + HudRegions.VitalsGap)) < 1e-3f,
            "its root is the top centre of the native vitals (here only the bar: its top plus the small gap)");
        check(Math.Abs(onBar.BarWidth - 320f) < 1e-3f, "it knows the bar's width in HUD units");
        health.gameObject.SetActive(false);
        Time.time = 5.1f;
        Hud.Update();
        check(!onBar.Root!.gameObject.activeSelf && a.Root!.gameObject.activeSelf, "a hidden health bar hides its elements and nothing else");
        health.gameObject.SetActive(true);
        onBar.Remove();

        // A text copy carries no second text layer of the source: an offset ghost child would keep the source's own text.
        var ghostList = WeaponList(out var ghostItem, out _);
        ghostItem.m_slim_archetypeName!.gameObject.Factory = () =>
        {
            var copy = new GameObject();
            copy.AddComponent<TextMeshPro>();
            var ghost = new GameObject("Ghost");
            ghost.AddComponent<TextMeshPro>();
            ghost.transform.SetParent(copy.transform, false);
            // A shadow that is not a text (a sprite child) and a script that would make or drive one when it wakes.
            var shadow = new GameObject("Shadow");
            shadow.AddComponent<SpriteRenderer>();
            shadow.transform.SetParent(copy.transform, false);
            copy.AddComponent<MonoBehaviour>();
            return copy;
        };
        GuiManager.PlayerLayer!.Inventory = ghostList;
        GuiManager.PlayerLayer.m_playerStatus = null;
        TextMeshPro? ghosted = null;
        var ghostElement = ui.AddHud("HudGhost", new HudSlot(HudRegion.WeaponList, 30), hud => ghosted = hud.AddText())!;
        Time.time = 6f;
        Hud.Update();
        check(ghosted != null && ghostElement.Root!.GetComponentsInChildren<TextMeshPro>(false).Length == 1 && ghostElement.Root.GetComponentsInChildren<SpriteRenderer>(false).Length == 0,
            "a copied HUD text drops the game's ghost layers (text or sprite children), so each line is drawn once");
        check(ghosted != null && ghosted.gameObject.GetComponents<Behaviour>().Length == 0, "a copied HUD text drops the game's scripts, which could make a ghost layer when they wake");

        // The game's weapon list sways a little under the measurement: within the tolerance nothing moves; a real change moves the element once.
        var ghostRoot = ghostElement.Root!.localPosition;
        var ghostIcon = ghostList.m_inventorySlots[0].GetComponentInChildren<SpriteRenderer>(true)!;
        var iconAt = ghostIcon.transform.localPosition;
        ghostIcon.transform.localPosition = iconAt + new Vector3(0f, -3f, 0f);
        Time.time = 6.6f;
        Hud.Update();
        check(ghostElement.Root.localPosition == ghostRoot, "a weapon list wobbling by a few units moves no element");
        ghostIcon.transform.localPosition = iconAt + new Vector3(0f, -40f, 0f);
        Time.time = 7.2f;
        Hud.Update();
        var movedTo = ghostElement.Root.localPosition;
        Time.time = 7.8f;
        Hud.Update();
        check(movedTo.y < ghostRoot.y - 30f && ghostElement.Root.localPosition == movedTo, "a weapon list that really moved moves the element once, then it stays");
        ghostIcon.transform.localPosition = iconAt;
        ghostElement.Remove();

        // A world element is rooted at the middle of the screen in the weapon list's space and takes no room.
        var worldBuilds = 0;
        var marker = ui.AddHud("Marker", new HudSlot(HudRegion.World), hud => { worldBuilds++; hud.AddText(); })!;
        HudScreen.CentreX = 10f;
        HudScreen.CentreY = -20f;
        Time.time = 8.5f;
        Hud.Update();
        check(worldBuilds == 1 && marker.State == LayoutState.Free && marker.Root!.localPosition == new Vector3(10f, -20f, 0f) && marker.HalfWidth == 960f,
            "a world element sits at the middle of the screen and knows the screen's size");
        HudScreen.CentreX = HudScreen.CentreY = 0f;
        marker.Remove();

        // A template ticks every frame while it shows and not while it is hidden; one that throws is switched off, logged once and not ticked again.
        var counting = Hud.Add("Audit/Counting", new CountingWidget(false), new HudSlot(HudRegion.WeaponList))!;
        var throwing = Hud.Add("Audit/Throwing", new CountingWidget(true), new HudSlot(HudRegion.WeaponList))!;
        Time.time = 9f;
        Hud.Update();
        Time.time = 9.1f;
        Hud.Update();
        var tickedCounting = (CountingWidget)counting;
        var tickedThrowing = (CountingWidget)throwing;
        check(tickedCounting.Builds == 1 && tickedCounting.Ticks == 2 && Math.Abs(tickedCounting.LastDt - 0.1f) < 1e-4f, "a template is built once and ticked every frame with the frame time");
        check(tickedThrowing.Ticks == 1 && !throwing.Visible && Plugin.Errors.Count(e => e.Contains("'Audit/Throwing' failed while updating")) == 1, "a template that throws is off, logged once and not ticked again");
        counting.Visible = false;
        Time.time = 9.2f;
        Hud.Update();
        check(tickedCounting.Ticks == 2, "a hidden template is not ticked");
        counting.Remove();
        throwing.Remove();
        a.Remove();
        b.Remove();
        check(oldRoot != null && a.Root == null && b.Root == null, "removed elements drop their pieces");
        HudScreen.Measurable = false;
    }}

/// <summary>A template that counts its builds and ticks, and can throw from a tick.</summary>
internal sealed class CountingWidget : HudWidget
{
    private readonly bool _throws;
    internal int Builds, Ticks;
    internal float LastDt;

    internal CountingWidget(bool throws) { _throws = throws; }

    internal override void Build(HudElement hud) => Builds++;

    internal override void Tick(HudElement hud, float now, float dt)
    {
        Ticks++;
        LastDt = dt;
        if (_throws) throw new InvalidOperationException("bad tick");
    }
}