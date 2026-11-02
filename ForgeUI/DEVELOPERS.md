# Forge UI for developers

This file is for mod authors. Players should read `README.md`.

Plugin GUID `NAinfini.ForgeUI`, Thunderstore package `NAinfini-ForgeUI`. It depends only on BepInExPack_GTFO. Everything is in the `ForgeUI` namespace and runs on the main thread.

## Layering: what this package holds and what it does not

ForgeUI holds on-screen interface mechanisms: lobby buttons and cells, pickers, menu pages, HUD zones, templates, boot sequences, screen-edge overlays, visors, damage indicators, world markers and countdowns, location readouts, hiding and redrawing the vanilla HUD, a screen effect stack, enemy health bars, drawing helpers and translations. It holds no look or wording of its own: appearance, text, colours and icons belong to the mod that adds them and are passed in through the API. A mechanism other mods may use goes here without waiting for a second user.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Plain logic with no Unity reference: layout, easing, queues, command codes, screen math. |
| `Native/` | The plugin: lobby buttons, pickers, menu bar, HUD, templates, screen layers, language. |
| `lang/` | The kit's own texts, one `<language>.json` per language. |
| `tests/Core/` | Offline tests. |
| `tools/package_release.py` | Builds the Thunderstore zip. |
| `tools/fonts/` | `build_fonts.ps1`, `make_symbols.py` and `FontBundleBuild.cs`: make ForgeUI Symbols (the kit's own ∞, U+221E, drawn to Rajdhani's digits; needs fontTools) and build the six font files into `Assets/fonts/ForgeUI-fonts.bundle` (local only) with Unity 2019.4.21f1. |
| `Assets/fonts/OFL-*.txt` | The two fonts' licences, shipped in `fonts/`. |

## Build, test and package

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeUI/ForgeUI.csproj -c Release --disable-build-servers -m:1 --artifacts-path artifacts/dotnet-uikit "-p:GTFOBepInExPath=<profile>/BepInEx"
python Mods/ForgeUI/tools/package_release.py --artifacts-path artifacts/dotnet-uikit
```

The package script checks the manifest against Thunderstore's rules and writes `NAinfini-ForgeUI-0.1.0.zip` to `artifacts/work/ordnance-release/`. The zip holds `ForgeUI.dll`, `lang/*.json` (it refuses to build without `en.json` and `zh.json`), `fonts/` (the font bundle and both licences; it refuses to build without them), `icons/*.png` that were imported, `manifest.json`, `icon.png`, `README.md`, `CHANGELOG.md` and `LICENSE`. Identical inputs give identical bytes. It does not publish.

Before the first package, build the fonts once (take `artifacts/work/unity-lock.txt` first; Unity runs outside the sandbox):

```
pwsh Mods/ForgeUI/tools/fonts/build_fonts.ps1 -Latin <folder with Rajdhani-Medium.ttf, Rajdhani-SemiBold.ttf> -Chinese <folder with GlowSansSC-Condensed-Medium-common.ttf, GlowSansSC-Condensed-Bold-common.ttf>
```

Rajdhani comes from google/fonts (`ofl/rajdhani`), Glow Sans SC Condensed from welai/glow-sans 0.93 (`GlowSansSC-Condensed-*-common`). Unity
only imports them as dynamic fonts; the TextMesh Pro font assets are made in the game by the game's own TextMesh Pro, so no
TextMesh Pro version is baked into the bundle.

The offline tests are in `tests/Core/`.

## Quick start

Reference the DLL (do not copy it into your package) and the game assemblies your code uses:

```xml
<ItemGroup>
  <Reference Include="$(GTFOBepInExPath)/plugins/NAinfini-ForgeUI/ForgeUI.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/core/BepInEx.Core.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/core/BepInEx.Unity.IL2CPP.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/core/Il2CppInterop.Runtime.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/interop/Il2Cppmscorlib.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/interop/Modules-ASM.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/interop/UnityEngine.CoreModule.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/interop/Unity.TextMeshPro.dll" Private="false" />
  <Reference Include="$(GTFOBepInExPath)/interop/UnityEngine.UI.dll" Private="false" />
</ItemGroup>
```

The last two are needed only by code that touches the kit's `TextMeshPro` or UI types (for example `HudElement.AddText`); a mod that only registers through the kit's other members compiles without them, as the ForgeOrdnance series does.

Add `NAinfini-ForgeUI-0.1.0` to your `manifest.json` dependencies and `[BepInDependency(UIKit.PluginId)]` to your plugin.

```csharp
using System.Collections.Generic;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeUI;

[BepInPlugin(Id, "Hello", "1.0.0")]
[BepInDependency(UIKit.PluginId)]
public sealed class HelloPlugin : BasePlugin
{
    public const string Id = "Me.Hello";

    public override void Load()
    {
        var ui = UIKit.Mod(Id);                        // every registration of this mod goes through its handle
        // A lobby button under APPAREL that opens a picker.
        ui.AddLobbyButton("Colour", "COLOUR", bar => UIKit.TogglePicker(bar, Colours.Instance));
        // A top menu bar button with a page.
        ui.AddMenuBarPage("Guide", "HELLO", page => page.AddText("Pick a colour in the lobby."));
        // A line on the in-level HUD, in the region under the weapon list (priority 10, 300 x 30).
        ui.AddHud("Line", new HudSlot(HudRegion.WeaponList, 10, 300, 30), hud => hud.AddText().SetText("COLOUR " + Colours.Instance.Chosen));
        // A banner in the shared queue: it waits behind stronger banners of any mod instead of covering them.
        ui.AddBanner("Banner", priority: 10)?.Show("HELLO");
    }
}

public sealed class Colours : PickerSource
{
    public static readonly Colours Instance = new();
    private static readonly PickerCard[] Cards = { new() { Title = "RED" }, new() { Title = "GREEN" }, new() { Title = "BLUE" } };
    public string Chosen = "NONE";

    public override int SlotCount => 1;
    public override PickerCard SlotCard(int slot, bool chosen) => new() { Subtitle = "COLOUR", Title = Chosen };
    public override IReadOnlyList<PickerCard> Entries(int tab) => Cards;
    public override string? Pick(int slot, int tab, int entry)
    {
        Chosen = entry < 0 ? "NONE" : Cards[entry].Title;
        return null;
    }
}
```

Buttons, pages and HUD lines look like the game's, follow resolution changes, and are rebuilt when the game rebuilds its menus or HUD.

## Joining the shared layout

1. Take your handle once in `Load`: `var ui = UIKit.Mod(YourPlugin.Id);`. Your registrations are named `YourPlugin.Id/name`, so two mods can both have a "Banner".
2. For each HUD element, say where it belongs and how much it matters, not where to draw it: `new HudSlot(region, priority, width, height)`. The kit stacks all mods' elements in each region by priority, keeps them off the game's weapon list and health bar, and folds away the weakest when a region is full. Use `hud.Resize(w, h)` when your content changes.
3. Use the shared pieces instead of your own: `ui.AddBanner(name, priority)` for big centre text, `ui.AddNotices(name)` for corner messages, `ui.AddPowerOn(name, spec)` for a power-on that covers the screen.
4. An element the layout cannot place (its region is full) is folded away without a log line and read from `HudElement.State` (`Collapsed`).

## Rules

- Call everything from the main thread, from your plugin's `Load` or later.
- `UIKit.Mod(modId)` takes your mod ID (letters, digits, `.`, `_`, `-`, no `/`) and returns the same handle every time. An invalid ID throws. Names are unique inside your mod, stable, and have no spaces or `/`. The full ID is `modId/name`, at most 128 characters. An empty, spaced or taken name returns null and logs why.
- Every `Add...` returns a handle. `Remove()` takes it off. An old handle never removes a newer registration with the same ID.
- An exception in your callback is logged with your full ID and never reaches the game or other mods.
- Titles, card texts, notes and tab names show exactly as written (after a key is looked up); `<` is not read as a tag. Picker details (`PickerInfo.Body`), page text and HUD text accept TextMeshPro rich text.
- The library has no audio. Presses on its buttons, slots and cards do not reach the game's own handlers, so the game plays no click. Play your own from `onClick`, `PickerSource.Clicked` or the page's `clicked`.
- The game is patched only for the features you use, the first time you use them (clicks, menu bar, menu pages, picker, settings page, gear names and icons, hiding the game's HUD, weapon list, enemy health bars, local hits, and each game readout when `UIKit.<readout>` is first read: `Vitals`, `Inventory`, `Objectives`, `Interaction`, `Subtitles`, `Intel`, `Boosters`, `Pickups`). A hook that is on stays on. A readout read for the first time catches up with the game's current state.
- Per-frame work runs as ForgeCore `Frame.OnUpdate` / `Frame.OnLateUpdate` callbacks (the late one after every mod's `Update`); the kit adds no component to the scene.

## Texts in several languages

Put one file per language in `lang/` beside your DLL, each a flat JSON object of key to text, and ship the folder in your package:

```
lang/en.json   { "button": "COLOUR", "picked": "Picked {0}" }
lang/zh.json   { "button": "颜色", "picked": "已选 {0}" }
```

`en.json` is required; adding a language is adding a file. Codes are lower case: `en`, `zh`, `zh-hant`, `fr`, `it`, `de`, `es`, `ru`, `pt-br`, `pl`, `ja`, `ko`.

```csharp
var ui = UIKit.Mod(Id);
ui.AddLobbyButton("Colour", UIText.Key("button"), bar => UIKit.TogglePicker(bar, Colours.Instance));
ui.AddMenuBarPage("Guide", UIText.Key("guide.title"), page => page.AddText(UIText.Key("guide.body")));
dash.Label = UIText.Key("dash");                 // HUD pieces resolve keys every time they draw
string line = ui.Text("picked", colourName);      // a string in the current language, for your own text
UIKit.LanguageChanged += Rebuild;                 // rebuild anything you composed from strings yourself
```

- Anything that takes a `UIText` takes a plain string as before (shown as written) or `UIText.Key(key, args)`. Keyed titles, pages, labels, hint lines and command lists switch the moment the language changes; an open menu page is rebuilt in place. Banners and notices resolve when pushed.
- `ui.Text(key)` and `ui.Text(key, args)` read your `lang/`; `UIKit.Texts(folder)` gives the same for another folder (a definition pack's). `TryText` returns false without logging, for optional texts.
- The language is the `Language / Language` setting: `Auto` (the default) follows the game's language, or a code forces one. `UIKit.Language` is the code in use.
- A key missing in the chosen language falls back to `en.json` and logs one error; missing there too, the key itself is shown. With no file for the chosen language, the whole mod shows English and warns once.
- Files are read once, on first use, and checked against `en.json`: duplicate keys, broken files, missing or extra keys and different `{n}` placeholders are logged. After that a lookup is a dictionary read; texts with no arguments allocate nothing.

## Lobby buttons

| Member | |
| --- | --- |
| `ui.AddLobbyButton(name, title, onClick, note = null, visible = null)` returns `LobbyButton?` | A copy of APPAREL, stacked below it. `onClick(CM_PlayerLobbyBar)` gets the pressed bar. `note()` is small grey text after the title, read once per frame while shown. `visible(bar)` defaults to "only the local player's own bar". |
| `LobbyButton.Title` | A `UIText`; change it any time. |
| `LobbyButton.Tooltip` | A `UIText?` shown in a small box over the button while the pointer is on it; null (default) shows none. |
| `LobbyButton.Remove()` | Takes it off every bar. |

## Pickers

Subclass `PickerSource`, then call `UIKit.TogglePicker(bar, source)` from a lobby button. The picker opens in the game's own loadout pop-up: a slot column on the left, tabs and cards in the middle, details on the right.

| `PickerSource` member | |
| --- | --- |
| `SlotCount`, `SlotCard(slot, chosen)` (required) | The slot column. A slot tile shows `Icon`, `Title` and a bar in `Accent`. |
| `Entries(tab)` (required) | The cards of a tab. `PickerCard.Icon` is the picture (kept in its own colours; null shows none), `Accent` colours the category square and `Subtitle`, `Equipped` (1-based slot number, 0 for none) shows an amber number tag. |
| `Pick(slot, tab, entry)` (required) | Put `entry` (-1: the clear card) into `slot`. Return null when accepted, or the refusal text, shown in red. |
| `Tabs` | Up to five tab names. Default `ALL`. |
| `ClearCard` | First card of every tab that empties the slot. Null leaves it out. Default `EMPTY`. |
| `Info(slot, tab, entry)` | The details box. `entry` -1 means "what the slot holds". `PickerInfo.Badges` draws up to 12 key caps under the title; `Accent` tints the glow behind the picture; `Readouts` shows up to four `new PickerReadout(label, value, unit = "", fill = 0)` blocks (`PickerReadout.Unlimited(label)` draws ∞). |
| `SlotsHeader`, `FirstSlot()`, `SlotAfterPick(slot)` | Column heading (default `LOADOUT`), and which slot is chosen on opening and after a pick. |
| `Clicked(PickerClick.Slot / Entry)` | Every press of a slot or card, for your click sound. |
| `Closed()` | Once per opening, however it closed. |

The picker asks your source for everything again after each pick and on `UIKit.RefreshPicker()`. Hovering a card refreshes only its details. Call `RefreshPicker()` when your data changes elsewhere. `UIKit.ClosePicker()` and `UIKit.IsShowing(source)` do what they say.

## Top menu bar

| Member | |
| --- | --- |
| `ui.AddMenuBarButton(name, title, onClick)` returns `MenuBarButton?` | A button on every page's menu bar after SETTINGS, RUNDOWN and LOADOUT. A button that would reach the game's right-hand group is hidden and an error says so. |
| `ui.AddMenuBarPage(name, title, build, clicked = null)` returns `MenuBarButton?` | A button that opens and closes a page in the game's menu pop-up window (1400 x 720). `build(MenuBarPage)` runs the first time the page opens on each game page. If it throws, the partial page is dropped and the next click retries. |
| `MenuBarPage.AddText(text)`, `AddRow(label, value, gap = 8)` | Heading text and label/value rows, one under the other. |
| `MenuBarPage.Root`, `Size`, `Window`, `IsOpen`, `Close()` | `Root` is the content area's top left (content goes at negative y). `Window` is the game's `CM_ScrollWindow`. |
| `ui.AddMenuBarScreen(name, title, build, clicked = null)` returns `MenuBarButton?` | A button that opens a full screen on the game's own settings page, built from copies of that page's own pieces (`MenuScreen`). The main menu switches to the settings page, the page's own sections and windows are put away while the screen is open, and any page change (the game's SETTINGS, RUNDOWN or another button) closes it and gives them back; the backdrop, menu bar and cursor stay the page's own. `build` runs every time it opens. |
| `MenuScreen.AddSection(text, selected, onClick)` | A section button down the left, a copy of a settings section button where those stand; the selected one keeps its text bright. |
| `MenuScreen.AddWindow(header, width)` returns `MenuScreenWindow` | A copy of a settings window (frame, header, scroll bar, clipping) right of the windows before it. |
| `MenuScreenWindow.AddRow(text, onClick = null, selected = false)` | A copy of a settings row: one line of rich text, the game's row box; `selected` lights its box; without `onClick` it takes no clicks. Copied rows and buttons drop the hover tooltip of the setting they were copied from. |
| `MenuScreenWindow.Columns(stops, rightAligned...)`, `AddHeader(cells...)`, `AddRow(cells, onClick = null, selected = false)` | A table: `Columns` sets where columns 2, 3, ... start in per cent of a row's width (each ends where the next starts) and which columns line up on the right (numbers); `AddHeader` writes a header row in the game's dim label colour; every cell of a header or cell row is a copy of the row's own title text sized to its column, one line, cut with an ellipsis, so cells never run into each other. |
| `MenuScreenWindow.AddField(label, value, at = 38)` | A details row: the label dim in the first column, the value in a second from `at` per cent, both cells as above. |
| `MenuText.Tint(text, MenuTone)`, `MenuText.Hex(tone)`, `MenuText.Plain(text)` | Rich text for those rows: the game's menu colours (`Bright`, `Dim`, `Alert` red, `Warn` yellow, `Mark` orange) and author text shown as written. |
| `MenuScreenWindow.AddButton(text, onClick, main = false)` | A button under the window: a copy of the settings section button, or of the page's red button for the main action. |
| `MenuScreen.Rebuild()` | Builds the screen again at once while it is open (after a click changed what it shows). |
| `MenuBarPage.AddButton(text, x, y, onClick, maxWidth = 560)` returns `CM_MenuBarItem` | A copy of the menu bar's own item, left edge at `x`, middle at `y`; game hovers, presses go to `onClick`. Rich text is allowed: line columns up with `<pos=12em>`. |
| `MenuBarPage.AddLabel(text, x, y, width)` | One line of text, top left at (`x`, `y`), cut off with an ellipsis. |
| `MenuBarPage.AddRect(x, y, width, height, colour)` | A filled box, top left at (`x`, `y`), over the window's background and under every text and button. |
| `MenuBarButton.Open()` | Opens the page or screen as a click would; false when the main menu is not up (try again next frame). |
| `MenuBarButton.Tooltip` | A `UIText?` shown in a small box under the button while the pointer is on it (not while its page is open); null (default) shows none. |

## HUD regions

| Region | Grows from | Up to |
|---|---|---|
| `WeaponList` | Just below the game's weapon list, right-aligned, downwards | The screen's bottom |
| `Vitals` | Just above the health bar, centred, upwards | The middle of the screen |
| `CenterBanner` | A little above the middle of the screen; one slot | One element at a time |
| `TopCenter` | The top edge, centred, downwards | A little above the middle |
| `Notices` | The top left, below the game's own messages, downwards | The middle |
| `RightColumn` | The right edge, a quarter screen above the middle, downwards | A little below the middle |
| `BottomLeft` | The bottom left corner, upwards | A little below the middle |
| `BottomRight` | The bottom right corner, upwards | Half way between the middle and the top |
| `TopRight` | The top right corner, right-aligned, downwards | A little above the middle |
| `World` | The middle of the screen; not laid out (markers, hit ticks, damage numbers) | |

Each frame, every region stacks its visible elements from all mods by priority (higher first, ties by full ID) with a 12-unit gap. The game's weapon list and health area are occupied first. An element that would overlap them, or another element, moves past it. An element that would pass the region's limit, or a single-slot region held by a stronger one, is folded away (an expected outcome, not logged). The layout uses the larger of your declared size and what the element draws.

`new HudSlot(region, priority, width, height, space: s)` leaves `s` more units (default 0) between the element and the one it follows in its region, on top of the region's gap (a line takes its members' largest). `new HudSlot(region, priority, width, height, row: n)` with `n` above 0 puts the element on a shared line: the shown elements of one region with the same row stand side by side from the corner outwards (highest priority nearest the corner) on the edge the region grows from, the line takes the height of its tallest member and stacks like one element, and a line that does not fit folds away whole. While a mod hides the game's weapon list and an element marked `Entry.WeaponListStandIn` (the kit's loadout) is shown, `WeaponList` elements stack on past everything in that element's region instead of keeping the empty corner; `Entry.BesideStandIn` says so, and the command list then takes the loadout's row style.

| Member | |
| --- | --- |
| `ui.AddHud(name, HudSlot slot, build)` returns `HudElement?` | An element in `slot.Region` with `slot.Priority` and the declared size. `build(HudElement)` runs each time the game creates its HUD (every level). If it throws, the element stays off until the next HUD. |
| `HudElement.Root`, `Visible`, `Slot`, `Region`, `State`, `Box`, `Remove()` | `Root` is the corner your region grows from, in HUD units (about pixels at 1080p). It is null while there is no HUD. `Visible` is yours; a hidden element takes no room. `State` is `Placed`, `Collapsed`, `Hidden`, `Unavailable` or `Free`. |
| `HudElement.Resize(width, height)` | Change the declared size. |
| `HudElement.FlowX`, `FlowY` | The region's growth direction (-1, 0, 1). |
| `HudElement.Reveal()`, `HudWidget.Reveal()` | Brings the element online over 0.42 s from now (`HudReveal`): corner brackets trace out, a bright scan line sweeps the way the region grows (from the middle outwards for a centred one), only what it has passed is drawn and texts type on, then the whole element flashes and the brackets burst away. Works for everything drawn with `HudCanvas`; allocates nothing. |
| `HudElement.AddText(parent = null)` returns `TextMeshPro` | A copy of the weapon names' text with the game's font and CJK fallbacks. |
| `HudElement.AddSprite(sprite, order = 0, parent = null)` returns `SpriteRenderer` | Drawn with the weapon list's sprite material. `UIKitSprites.Pixel`, `Outline` and `Arrow(direction)` are white shapes to tint. |
| `HudElement.FontSize`, `HasGlyph(c)`, `Px(px)`, `BarWidth`, `HalfWidth`, `HalfHeight`, `ScreenVersion` | Font and screen measures. `HasGlyph(c)` tells whether the game's HUD font can draw a character (the `Tech` face has its own ∞). |
| `HudElement.WorldToHud(world, out point, out behind)` | A world point in HUD units from the middle of the screen. `ScreenMath.EdgeMarker` turns it into an on-screen or edge place. |
| `UIKit.SafeMargin` | The player's safe margin. |

## Time bar and HUD pointer

```csharp
var bar = ui.AddTimeline("Timeline")!;          // World region, hidden until Visible
bar.Seek = ms => player.Jump(ms); bar.PlayPause = player.Toggle; bar.Step = player.Step; bar.SpeedChosen = player.SetSpeed;
var pointer = HudPointer.Hold(MyPlugin.Id);       // pointer shown, the game's look / fire / move / weapon keys held off
// every frame
bar.Visible = true; bar.DurationMs = length; bar.PositionMs = now; bar.Playing = playing; bar.Speed = speed;
```

| Member | |
| --- | --- |
| `ui.AddTimeline(name)` returns `HudTimeline?` | A playback bar across the bottom: marks, a draggable head, step back, play / pause, step forward, the clock and speed buttons. It draws what you set (`DurationMs`, `PositionMs`, `Playing`, `Speed`, `Speeds`, `Marks` of `TimelineMark(timeMs, colour, label)`, `Colour`) and reports clicks through `Seek` (at most ten a second while dragging, once more on release; a click on a mark seeks to it), `PlayPause`, `Step(-1 / +1)` and `SpeedChosen`. It never moves time itself. `Height` is the band's height from the bottom edge; `Busy` is true while the pointer is on it or dragging. |
| `HudPointer.Hold(modId)` returns `PointerHold` | While any hold is `Active` in a level and the game shows none of its own screens: a pointer arrow is drawn on the HUD and moved by the mouse's own pixel movement (`Input.GetAxisRaw("Mouse X" / "Mouse y", the spelling of the game's InputManager)`, scaled to the share of the screen it covers, so it crosses the screen like the system cursor; `[Hud] PointerScale` in ForgeUI's config, default 1), and the game's look, fire, aim, reload, use, movement, weapon and flashlight keys are held off (ForgeCore `InputLease`). The system cursor stays locked the whole time, so the game never pulls it back. The last hold let go hides the pointer and gives the input back. |
| `HudPointer.Toggle(modId)` returns `PointerToggle` | A hold Control drives: watching (pointer hidden, the mod turns the view) or using the screen (Control held, or tapped to stay until the next tap; pointer shown). `Enabled` turns it on and off (either starts with the pointer hidden), `Using` tells which state it is in, `Set(usingScreen)` changes it without the key. The press logic is `TapOrHold` (public, usable with any key). |
| `HudPointer.Position(hud, out x, out y)`, `Over(hud, x, y, w, h)`, `Clicked(...)`, `Pressed`, `Held`, `Released`, `Shown` | Hit tests in a `World` element's units, y up. |
| `HudPointer.ScreenBounds(hud, out left, out bottom, out right, out top)` | The screen's edges in the same units, read this frame through the HUD camera; lay a full-screen layout out from these, not from `HalfWidth` (which can be off while a mod moves the cameras). |
| `TimelineMath` (Core) | Moment to bar position and back, nearest mark, clock and speed texts. |
## Ready-made HUD pieces

Each `ui.Add...` registers a HUD element in its default region and returns a handle (null and a logged reason when the name is taken). The optional `slot` argument takes a `HudSlot` to change region, priority or size. `Visible` and `Remove()` are on every handle.

```csharp
var ui = UIKit.Mod("Me.Hello");
// Ability slot: seconds left (0 = ready), cooldown length, key text.
ui.AddSkillSlot("Dash", () => dash.SecondsLeft, 8f, UIKitKeys.Name(KeyCode.Q)).Label = "DASH";
// Key prompt that shows the key the player actually bound. HoldSeconds makes it hold-to-confirm.
var revive = ui.AddKeyHint("Revive", KeyCode.F, "REVIVE"); revive.HoldSeconds = 1.2f;
if (revive.Hold(Input.GetKey(KeyCode.F), Time.deltaTime)) Revive();      // true on the frame the hold completes
ui.AddKeyHint("Reload", InputAction.Reload, "RELOAD");                   // a game action's binding
// Big centre text through the shared banner queue.
var banner = ui.AddBanner("Banner", priority: 20); banner.Show("WAVE 3", NoticeTone.Warn);
// Corner notices on the shared list.
var notices = ui.AddNotices("Notices"); notices.Push("PICKED UP KEY");
// A line of words and key boxes: "Hold [F] to revive".
ui.AddHintLine("Hint").Set(HintPart.Word("Hold"), HintPart.Key("F"), HintPart.Word("to revive"));
// A list of commands entered with an arrow code.
var calls = ui.AddCommandList("Calls", "CALL-IN", "CALL-IN READY", "THROW", maxItems: 8, maxSteps: 8);
calls.SetHint(HintPart.Word("Release"), HintPart.Key("F"), HintPart.Word("to cancel"));
calls.Show(CommandListMode.Entering, items, typedSteps, held: null);   // every frame it should be up; calls.Hide() otherwise
calls.Notify("Not enough ammo", 3.5f);   // a line under the list that goes away by itself
```

| Handle | Properties |
| --- | --- |
| `ProgressGauge` | Not a handle: the progress calculation for your own element. Keep one (`Fraction` for a share 0..1, negative when nothing runs; `Cooldown` for seconds left; `Hold` for a key held down; `HoldSeconds`; `Fill`, `Sweep`, `Text`, `DoneAge`) and draw it with `HudCanvas.Progress(...)` (a bar) or `HudCanvas.Ring(...)` (a ring). The ability slot and key prompt use the same calculation for their cooldown ring and hold fill. |
| `HudSkillSlot` | `Cooldown`, `Key`, `Icon` or `Label`, `Count` (uses left; 0 greys it out, negative shows ∞), `Size` |
| `HudKeyHint` | `Label`, `HoldSeconds`, `Progress`, `Hold(down, dt)` |
| `HudHintLine` | `Set(params HintPart[])`. `HintPart.Word(text)` is plain text and `HintPart.Key(name, wide = false)` a key name in a box. Inside your own element, use `new HintRow(hud)`. |
| `HudCommandList` | `Show(mode, items, typed, held)` every frame it should be up, `Notify(text, seconds)` (a line under the list that expires by itself and keeps the list up with no commands), `Hide()`, `SetHint(...)`. Items are `CommandItem(name, code, accent)` (`accent` is the category colour: the mark before the name and the cooldown bar) with `Uses` (negative: no limit, 0: used up), `CooldownSeconds` and `CooldownProgress`. Modes: `Idle` (compact rows, an accent value cell while a command can be called), `Entering` (each row opens its code cells, the typed steps lit), `Holding` (the one armed command). It takes no region: it stands at the right edge of the screen, middle height, or just left of whatever fills that column (a stand-in inventory), and keeps clear of the rest of the HUD. Forms ease into each other in about 0.17 s without jumping. While it shows, the game's glass reflection is off. |
| `HudBanner` | `Show(text, tone, seconds, sub)`, `Hide()`, `IsShowing`, `Priority`. One banner shows at a time for all mods. A higher-priority banner takes the slot from another mod's. An equal or lower one waits, and is dropped after 6 s (at most 4 wait). |
| `HudNotices` | `Push(text, tone, seconds)`, `Clear()`. Every mod pushes into the same list of 6 lines. The same text refreshes; a full list drops the oldest. |

Defaults: ability slots, key prompts and hint lines go under the weapon list; the command list docks at the right edge, middle height. Banners and notices use the shared queue and list. World points use `World`.

## Cockpit pieces

For a mod that takes the whole screen (a vehicle, a suit). Every value comes from you, apart from what the game itself showed before
it was hidden (objectives, prompt, subtitles, vitals, teammates, inventory). One look throughout: faint, the middle of the screen kept
dark, movement only when a state changes, nothing allocated per frame. Ice white `UIKitColors.Cockpit` is the main colour, `Cyan` the
shield, `Orange` current or active, `Amber` low or awake, `Red` critical or hunting, `Teal` asleep, `Plate` the dark backing
(`UIKitColors.Tone` picks warn and critical). Sizes are 1080p pixels; the kit multiplies them by `HudCanvas.Unit`.

```csharp
var ui = UIKit.Mod(Id);
var hide = ui.HideGameHud("Pilot");                       // keeps world markers, chat and crosshair hit feedback by default
var chat = ui.MoveGameChat("Chat", HudRegion.BottomRight, 20, 470f, 6); // docked, six lines over the input row; lit while typed in
hide.Active = chat.Active = piloting;                     // released for you on death, level end and disconnect (Released event)
var bar = ui.AddSegmentBar("Armor", () => armor / maxArmor, 10);
bar.Overlay = () => shield / maxShield; bar.Under = () => UIKit.Vitals.Health; bar.Infection = () => UIKit.Vitals.Infection; bar.ReadoutMax = maxArmor;
bar.CriticalAt = 0.25f; bar.CriticalPulse = MeterPulse.FlashOnLoss; bar.FailAt = 0.1f; bar.RollUp();       // eased fill, alarm, number rolling up
var hull = ui.AddSilhouette("Hull", new[] { front, back, left, right }); hull.CenterLabel = UIText.Key("class"); hull.Hit(UIKit.Bearing(attacker.position), damage, 1f - armor / maxArmor);
var clip = ui.AddArcGauge("Clip", () => rounds / (float)size, ArcSide.Left); clip.Cells = size; clip.EndReadout = () => roundsText;
var heat = ui.AddArcGauge("Heat", () => heatValue, ArcSide.Right); heat.HigherIsWorse = true; heat.SteadyAlpha = 0.7f; heat.Critical = () => overheated;
var contacts = ui.AddBearingArc("Contacts"); contacts.HideInView = true; contacts.RearIcon = arrow; contacts.RearText = UIText.Key("behind");
var dash = ui.AddSkillSlot("Dash", () => dashLeft, 8f, "Q"); dash.Label = UIText.Key("dash");
var loadout = ui.AddLoadout("Loadout"); loadout.Mark(InventorySlot.GearSpecial, cannonIcon, UIText.Key("frame-weapon"));
ui.AddObjectives("Objectives"); ui.AddInteraction("Prompt"); ui.AddSubtitles("Subtitles"); ui.AddTeammates("Team");
ui.AddLabel("Readout", new HudSlot(HudRegion.TopCenter, 60, 480f, 28f), () => gridText, () => headingText).Inline = true;
var edge = ui.AddHitEdge("Hits"); var visor = ui.AddVisor("Visor");
edge.Flash(UIKit.Bearing(attacker.position), damage / 30f); visor.Interfere(0.5f);
visor.Show(new VisorState { Strength = 0.62f, Cracks = VisorMath.CracksFor(armor / maxArmor), Heat = overheat, Struts = 0f });
```

| Member | |
| --- | --- |
| `ui.HideGameHud(name, keep = GameHudClaims.DefaultKeep)` (`NavMarkers \| Chat \| CrosshairFeedback`: what the game draws on the world and at the crosshair, and the chat) returns `GameHudHide?` | `Active`, `Keep` (`GameHudPart` flags: `Vitals`, `WeaponList`, `Objectives`, `Crosshair`, `Interaction`, `NavMarkers`, `Chat`, `Subtitles`, `Teammates`, `Compass`, `CrosshairFeedback`, `ScreenFlash`, `Watermark` (the game's version text at the bottom right; its FPS counter and status hints stay), `GlassShatter` (the cracked-glass camera effect of a big enemy's strangle), `GlassReflection` (the game's helmet-glass reflection: a faint offset copy of every HUD text and shape; hidden, its strength is 0 and the game's values come back on release)), `Stamina` (the game's stamina read-out and low-stamina warning: they have no pieces to switch off, so the game's stamina update and warning are held while hidden and the bar keeps its last look; stamina itself is untouched), `Released` (`GameHudRelease`: `LevelEnded`, `LeftLevel`, `PlayerDied`, `Disconnected`), `Remove()`. Requests add up per mod; a part stays hidden while any active request hides it, and the last release restores it exactly. A part is hidden at its root objects (`Vitals`: `m_playerStatus`, `WeaponList`: `Inventory`, `Crosshair`: each of `m_crosshairs`, `Interaction`: the interaction layer's prompt, message and timer, not the player layer they share with the world markers, ...): the roots are scaled to zero, which hides everything under them, and get their own scale back on release; the game's objects stay active and keep running. A root that holds something that must stay drawn (the kit's own elements, a kept part) is not collapsed, its other children are. Nothing is collected or watched piece by piece: the per-frame cost is a few dozen objects. `ui.HideGameHudParts(name, parts)` is the second entry, over the same request table: it hides only the named parts (`ui.HideGameHudParts("NoVitals", GameHudPart.Vitals)`), `HideGameHud` hides all but the kept ones; `GameHudHide.Hides` is the named side, `Keep` the rest. The named parts are the `GameHudPart` flags above. `WeaponList` and `Vitals` regions stay usable from their last measured place. While one of the game's own screens is open in a level (map, terminal, in-game menu, console, pop-up; read from the game's focus state), every hide is lifted and the chat goes home so the game shows its screen as without mods, and the kit's elements, crosshair attachments and full-screen layers are off (ForgeVisual's outlines too); all of it comes back on the return. |
| `ui.MoveGameChat(name, region = BottomRight, priority = 0, width = 460, lines = 6)` returns `GameChatMove?` | `Active`, `Accent` (the lit plate's left bar and input row), `Look` (`ChatLook`: `TextPx`, `InputPx`, `Text`, `Alert`, `Input`, `Caret`; null keeps the game's look), `Released`, `Remove()`. The highest priority active move wins. Its space is `width` wide and as tall as `lines` chat lines (1 to 20) over the input row plus a 10 pad above and below, measured from the game's chat box (`ChatDockMath.SlotHeight`; the prefab's 20 per line and 25 for the input row until measured). The chat box's lower left corner goes a pad inside the space's; older lines reaching above it are not drawn until they fit again. With a `Look` the docked chat drops the game's backing sprites (plate, pink input bar, corner), its lines, typed line and help line take the kit's HUD face at those 1080p sizes (`ChatDockMath.TextSize`, never above a line), line types are coloured `Text` and `Alert`, the typed line `Input`, the cursor a `Caret` bar; colours written in the text (players' names) stay. Not typing, only the newest `IdleLines` (3) show at `IdleOpacity` (0.85) on a faint dark plate fading to the right with a thin `Accent` bar (following the lines actually drawn), held `IdleSeconds` (10) after the newest line or closing the chat and then faded over 1.5 s (`ChatDockMath.IdleCeiling`, `IdleAlpha`); the game's help line is not drawn; every chat line is cut to the space's right edge less the pad, one line ending in an ellipsis (the typed line is not cut). Everything is given back exactly when the move ends. Typing in the chat keeps every HUD element on screen; a docked chat fades a plate in behind it (0.12 s in, 0.2 s out): dark, cut corners, bright outline, left bar, the input row lit under a thin line, as tall as the lines shown plus the input row, on the space's bottom edge. Where the game keeps the chat nothing is changed or drawn. `Native/ChatSkin.cs` holds the look. `Native/ChatDock.cs`, geometry in `Core/ChatDockMath.cs`. Game HUD hides leave a kept part's pieces alone. |
| `ui.AddSegmentBar(name, value, segments, slot = TopCenter)` | The bar says it all, no words: the slanted main cells (lit in `Colour` with a lighter upper and darker lower half; lost cells stay as hollow outlines), centred; `Overlay` a layer on their top edges (`OverlaySegments`, `OverlayHeight`; eased like the bar, a white flash and a fading trail on each drop, a brighter longer flash when it breaks (`LayerHits`); at 0 it is a red track that `OverlayProgress` fills back; `OverlayShown` false leaves its place empty so the bar never moves; `OverlayReadoutMax` above 0 numbers it left of the left bracket at `OverlayReadoutPx` 20 in `OverlayColour`, red at 0, after an optional `OverlayIcon`), `Under` a hairline on their bottom edges (`UnderSegments`, `Infection`, `InfectionColour`, `UnderWarnAt`, `UnderCriticalAt`); folded brackets at both ends and one `Readout` (`ReadoutPx` 26) in the bar's colour beside the right one. `Width` (420), `Height` (12), `OverlayHeight` (3), `UnderHeight` (2), `Curve`, `Skew` (6), `SegmentGap` (5), `Plate`, `Ruler`, `Reveal` (0..1, cells come in one by one), `Colour`, `OverlayColour`, `UnderColour`, `WarnAt`, `CriticalAt` (negative: never red), `CriticalPulse` (`Pulse`, `FlashThenSteady`, `FlashOnLoss`: a white flash on going critical and on each segment lost, steady between, without pause under `FailAt`), `Trail`. The drawn fill eases to the value (`EaseRate` 14 per second); `RollUp()` runs it up from 0 (a part coming online), `Restart()` shows a new subject's value at once; `ReadoutMax` above 0 numbers the eased fill (`Readout` overrides it). Tones are judged on the real value. |
| `ui.AddArcGauge(name, value, ArcSide)` (World) | `Cells` (one per round, merged above `MaxCells` 40), `Radius` (96), `Sweep` (72), `Thickness` (3.2), `OuterTicks`, `InnerRing`, `EndReadout`, `EndIcon`, `Colour`, `WarnAt`, `CriticalAt`, `CriticalPulse`, `HigherIsWorse`, `Refill(seconds)`, `Refilling`, `Critical` (forces the critical tone, for example overheated). It rests at `SteadyAlpha` (1) and flares to full for `FlareSeconds` (0.8) when its tone worsens, a refill starts or `Flare()` is called; full while critical. |
| `ui.AddBearingArc(name, capacity = 24)` (World) | `Begin()`, `Add(azimuth, strength, ForgeCore.EnemyAlert)`, `Radius` (300), `Span`, `Ticks` (4), `Ring`, `Tail` (12°), `HideInView` (skip contacts the camera sees), `HuntingColour`, `AwakeColour`, `SleepColour`, `TickColour`; the turn-around hint `RearIcon`, `RearText` (`{0}` is the count), `RearMin` (2 hunting), `RearArc` (±40°), `RearOffset` (332 below the centre); `Compass` also draws `UIKit.Compass` just outside the ring (`CompassOffset` 30), in and out of view alike: a line glyph per `CompassPointKind` on a dark plate, teammates in that player's colour, beacons in `BeaconColour` (orange), the distance under every point but a teammate |
| `ui.AddSilhouette(name, layers, slot = BottomLeft)` | A hull disc of four quarters: `Set(layer, damage)` (white, amber, red by thirds), `Flash(layer)` (red 0.4 s), `Hit(layer or azimuth, amount, loss)` (adds the hit to its side, flashes it and shows the overall `loss` shared out by what each side took: the most hit side shows all of it), `Reset()`, `Size` (124), `Center`, `CenterLabel`, `TickRing`, `Caption`; `Bearing.Quadrant(azimuth)` gives 0 front, 1 back, 2 left, 3 right. A null layer (a picture file that is missing) is logged as an error and not drawn. |
| `ui.AddLoadout(name, slot = BottomRight)` | The game's inventory, read by the kit: the selected slot as a card (`CardWidth` 340, `CardHeight` 132) with name, clip, reserve and cells (one per share of all the rounds carried, magazine and reserve together, out of a full magazine and a full reserve; red at `LowAt`), the rest as rows (`RowWidth` 300, `RowHeight` 26, `RowGap` 6; a row draws its figures first and drops the kind word when it does not fit; a kind equal to the name is not drawn); the card's weapon picture is drawn as a bright grey copy at full strength; keys use the kit's key cap; `Title`, `CellStrip`, `KindLabels`, `Accent`, `LowAt`, `AmmoSource` (per slot, `LoadoutAmmo(Clip, Reserve, ClipSize, ReserveMax)` in place of the game's figures for a gun the game counts in part; read every frame, must not allocate); `Mark(slot, icon, kind)`, `Lock(slot, locked)` (30 %, no key box). Carrying shows a carry card. | Rows ease open and shut; the loadout reports its exact size to the layout every frame, so whatever stacks past it or keeps clear of it follows smoothly.
| `ui.AddObjectives` / `AddInteraction` / `AddSubtitles` / `AddTeammates` | The game's objectives (Notices; the game's highlighted words in `HighlightColour`, no raw tags, lines wrapped with the font across `Width` (400); `TagIcon`, `Ruler` (as long as the text column), `Align` (null: the side of its region, so a top-right block sits flush in the corner), `ShowTimer`, `ShowIntel` (the warden intel and sub-objective flashes in `IntelColour`, two lines at most, for as long as the game shows them), `MapIcon`, `DoneStrike`, `TagPx`, `HeaderPx`, `TextPx`, `SubPx`, `HeaderColour`, `TextColour`, `SubColour`, `MainLines`; `Backing` a chamfered plate (`Chamfer`) and `AccentBar` a thin bar on the aligned side whose top takes the first tag's tier colour, both moving the text in and giving the tag icon its own column; only tags are bold, the header is never highlighted), prompt (`Offset` 128 under the crosshair; the game's coloured words kept, red in `AlarmColour` (an alarm door's warning) and any other colour in `HighlightColour`; `KeyBox`, `ProgressStyle` `Bar` or `Underline`, `LeadIcon`, `ShowPickups`; under it the game's message line in its style's colour (`ScanColour`, `AlarmColour`, a warning in `DisabledColour`) with its progress bar (a scan, a reactor step) and, for a scan, a mark per player it counts, filled for those inside), subtitles (390 under the crosshair; `Backing`, `AccentBar`, `Chamfer`) and other players (BottomLeft; `RowBacking` (a colour), `RowFrame` (a chamfered outline), `Chamfer`, `Segments` (slanted health cells, 0 a plain bar), `IconColour`, `Width`, `RowHeight`, `RowStep`, `ShowNumber`, `ShowInfection`, `ShowAmmo`, `AmmoCells` 4, `AmmoWarnBelow` 0.25, `AmmoWarnColour`; every mod's teammate tags), redrawn. AtGamePrompt puts the first line at the height of the game's own prompt (read from its object, cached until the screen size or interaction layer changes). |
| `ui.AddLabel(name, slot = TopLeft, params Func<string>[] lines)` | Ice-white lines: `Colour` (85 %), `Px` (16), `LineStep` (21), `Align`; `Inline` puts the values side by side on one row (`Gap` 28, a thin divider between, empty values take no room), lined up by `Align` inside the slot. |
| `UIKit.Vitals`, `Teammates`, `Inventory`, `Objectives`, `Interaction`, `Scan`, `Subtitles`, `Intel`, `Boosters`, `Pickups` | The game's own values and texts in its language. Texts carry `Version` and `Changed` and change only when the game shows something new. `Vitals.Held` is `PlayerHold.Grabbed` (a big enemy's strangle, a snatcher, a trap) or `Stunned`. Teammates carry `Infection`, `Ammo` (main and special together, as the game syncs it; `TeammateAmmo.Rel` is the same sum), `Held` (read like `Vitals.Held`, from the player's synced locomotion state), `Speaking`. `Interaction` is the prompt (`Visible`, `Text`, `Key`, `Style`, `Progress` of the hold bar, `AdditionalText`) and the message line under it (`MessageVisible`, `Message`, `MessageStyle`, `MessageProgress`), taken where the game writes its two prompt pieces, so every interaction (pickups, terminals, doors, holds, scans, reactor and uplink steps, timed prompts, other mods' prompts) reads the same; `InteractionStyle` is `Usable`, `Message`, `Scan`, `Warning` or `ScanAlarm`. `Scan` is the scan nearest the player while its HUD is up: `Active`, `PlayersIn`, `PlayersMax`, `Requirement` (`ScanRequirement` `Any`, `All`, `Solo`), `LocalIn`, `Alarm`. `UIKit.Bearing(world)` is the azimuth from where the player looks (0 ahead, 90 right). |
| `UIKit.Compass` | What the game's own compass strip shows, from the same synced state it draws: the map's locator beacon, a terminal's PING result and the other players in the level (the players' middle-mouse pings are not here: the game draws them itself in the world). `Count`, `Get(i)` gives a `CompassPoint`: `Kind` (`CompassPointKind`: `Here`, `Enemy`, `Ammo`, `Health`, `Tool`, `Supply`, `Item`, `Container`, `Terminal`, `Door`, `Generator`, `Caution`, `Sign`, `Hsu`, `Locator`, `TerminalPing`, `Teammate`), `Azimuth` (from the view, 0 ahead, 90 right; NaN when it cannot be told), `Distance` (metres from the eye), `Slot` (−1 for a beacon), `PlayerColour` and `Mod` with `R`, `G`, `B` (the player's colour, a mod point's own colour; white for a beacon). Points of `HudWaypoints` with `Bearing` follow the game's points (at most 32). `CompassPoints.Glyph(kind)` is the kind's line glyph (segments in a −1..1 box). The game's objects are looked up once a second; each frame reads only their synced state and positions into fixed arrays, so reading allocates nothing. Objective and other world markers are not on the game's compass and are not here (they stay on screen with `NavMarkers` kept). |
| `UIKit.Location` | Where the player is: `Zone` ("ZONE 49"), `Heading` ("090° E": degrees clockwise from north and the nearest of N, E, S, W), `Altitude` ("-212 M", whole metres), and `Version`, bumped when a text changes. A value the game does not give (no zone, no camera, outside a level) reads empty. Read once per frame (the zone twice a second); each value's text is made once, so reading is free. `ui.AddLabel("Readings", slot, () => UIKit.Location.Zone, () => UIKit.Location.Heading, () => UIKit.Location.Altitude)` draws them (`Inline` for one row). |
| `ui.AddHitEdge(name)` returns `HitEdge?` | An orange-red chamfered line with a scratched glow on the edge a hit came from: `Flash(azimuth, strength)`, `Clear()`, `Seconds`, `Warm()`. At most six flashes, nearby ones join; drawn by the screen effect stack, so the hit bands of every mod are one pass. |
| `ui.AddVisor(name)` returns `Visor?` | `Show(VisorState)` every frame it is up, `Hide()`, `Interfere(strength, seconds)`, `Sweep(seconds)`, `Warm()`. `VisorState`: `Strength` (corner shade, the middle 55 % stays clear), `Tint`, `Cracks` (three picture layers; `VisorMath.CracksFor(armour)` brings them in at 40, 20 and 10 %), `CrackCorner`, `Heat` (bottom edge), `Struts` (window struts), `Brackets` (corner brackets), `StrutSlide` (0 above the screen, 1 in place). The shade is a held `Vignette` effect on the screen effect stack (its strength and tint each frame), the heat glow, interference and sweep are drawn by the same pass; only the cracks, struts and brackets are separate layers. Off while `Screen / Visor` is off. `CracksPicture` is the PNG file of your crack drawing (white lines on transparency, impact at the top left; set it before the first `Show` or `Warm`); the library ships none, so without one there are no cracks, and a missing or unreadable file is logged and gives none. |
| `HudElement.Effects`, `HudElement.Tilt`; `UIKit.HudGlow`, `UIKit.HudFringe` | Lit pieces get a soft glow and a 1 px red and blue fringe (16 %) unless `Effects` is false; `HudFringe.Pulse(seconds)` strengthens the fringe for a moment. The player scales the glow with `Screen / Glow` and turns the fringe off with `Screen / Fringe`. `Tilt` (0..1) enlarges a piece slightly towards the screen edge. |
| `HudSequence` | A timeline of tracks that hand `0..1` to a callback: `Add(start, seconds, set)`, `Play(now, backwards)`, `Tick(now)`, `Skip()`, `Finished`. For boarding and leaving. |
| `ui.AddCrosshairAttachment(name)` | A ring, ticks, a dot and a landing mark on the game's crosshair layer: `Visible`, `Radius`, `Ticks`, `TickLength`, `Ring`, `Dot`, `Tighten`, `Colour`, `HideWhileAiming` (ring, ticks and dot are hidden while the player aims down sights and drawn again on release; the landing mark goes too), `Mark(world, ok)`, `ClearMark()`. |
| `HudSkillSlot` additions | A square with two cut corners (`Size` 76, `Cut` 12), `Label` above, `Text` inside without an icon, the key below in the kit's key cap (the key prompt's: outlined in the accent while usable, grey and faint while cooling or used up); `Active` turns it orange (`ActiveColour`, `ActiveFill` 0.10); `ReadyFlash`, `Deny()`, `Progress`, `Note`, `HoldSeconds`, `Hold(down, dt)` |
| `HudKeyHint` additions | `Icon`, `FadeWhenFamiliar(idle, faded)`, `Used()`, `Attention()` |
| `ui.AddWaypoints(name, capacity = 16)` returns `HudWaypoints?` (World) | Points a mod places itself, up to `capacity` (1..64): `Add(new Waypoint(position, label, kind) { Follow, Icon, Colour, Lift, Bar, EdgeArrow, Bearing })` returns a `WaypointId`; `Set`, `Move`, `Remove`, `Contains`, `Clear`, `Count`, `Capacity`. Past the capacity the point added longest ago is dropped (counted); an id of a gone point finds nothing. Group style: `ShowDistance`, `EdgeArrow` (an arrow on the screen edge while off screen, the right way when behind), `Bearing` (also on `UIKit.Compass` and every bearing ring that shows it), `Above`, `Colour`, `BarColour`, `FadeStart`, `FadeEnd`. A `Kind` other than `Here` draws its compass glyph; an `Icon` replaces it. Nothing is allocated per frame. |
| `ui.AddBootMessage(name, BootMessageBeats, BootMessageLook, slot = CenterBanner)` returns `HudBootMessage?` | A power-on message over the crosshair: an ignition line and brackets, a header decoding over a segmented bar that stalls (its word blinking), self-test lines typing on downwards under the bar with an OK stamp each on a dark plate, then the message decoding split in two colours `Look.MessageLift` over the bar's line (so the header, bar and self-test stand under it, never under its frame), locking with a flash, a burst and a shake, and folding to a dot; `Look.LineY` is the lowest the bar's line goes over the element's centre. `Look.Status` colours the status word ending each self-test line (the line's name keeps the accent). `Look.Keepout` (a `HudBox` in crosshair space, for example `GaugeMath.ArcExtent` of the arcs either side) raises the whole stack until it clears the box by `KeepoutGap`, given `Look.OriginRise` (the element centre's height over the crosshair, the banner region's by default); `BootMessage.LineY` is the result and `BootMessage.Extent(lineY)` the box of everything it can draw; the element asks for room for all of it. Powering down, the words' `Down` (for example SHUTDOWN) in `Look.Down` at `DownPx` on the centre: decoding between `DownStart` and `DownDecoded`, in brackets with a darker offset copy and short drop-outs, gone at `DownEnd`. `Prepare(BootMessageWords)` once per showing or language, then `Play(seconds)` or `PowerDown(seconds since powering down began)` each frame, `Stop()`; `Warm()` in a prewarm (false until the HUD exists). The beats (`BootMessageBeats`), colours and sizes (`BootMessageLook`) and words are yours; `BootMessage` is the same without Unity. |
| `WeaponListLine.Text`; `ui.ClaimWeaponListLine(name)` | Shows other text on the claimed line; keys use your `lang/`. |
| `PickerInfo.Stats` | Rows of `new PickerStat(label, value, max = 5)` above the details text. |
| `ui.AddLobbyStrip(name, player => items, maxItems = 6)` | A row of slot cells on every lobby bar, as wide as the APPAREL button: `LobbyStripItem(icon, text)` draws the picture in its own colours (else the text) in a chamfered cell, every cell the same muted dark fill; `LobbyStripItem.Empty` is a free slot (faint outline and a plus). |
| `ui.Icons(pack)` returns `IconPack?`; `pack.Register(name, png)`, `pack.RegisterFolder(folder, names or prefix*)` (`@2x` above 1440 lines, one log line for the missing), `pack.Icon(name)`; `UIKit.Icon("Pack:name")` | Your icons, named `Pack:name` (pack names: letters, digits, `_`, `-`; the first mod to take a name keeps it, `Game` is reserved). Anyone looks them up by the full name; a duplicate, a missing name, a pack whose mod is not installed or an unreadable file returns null (or false) with one log line, never another icon. `UIKit.Icon("Game:symbol_hostile")` takes one of the game's own sprites from the loaded ones (the catalog is `GameIcons.All`: `symbol_hostile`, `cellUI_SmallTriangleFill`, `CircleBigMiddleSize_Fill`, `GUIX_Detail_BoxFilled_AA_Centered`). |
| `NumberText.Tabular(digits)` | Digits of equal width; call it when the value changes. |
| `HudWidget.Font`, `HudElement.Font`, `HudCanvas.Font` (`UIKitFont.Game`, `UIKitFont.Tech`) | The face of the texts. `Tech` is Rajdhani for Latin letters and digits, then ForgeUI Symbols (∞; the kit's own work, no third-party licence), then Glow Sans SC Condensed for Chinese and the game's font for anything else; bold uses their heavier weights. The cockpit pieces above and `HudSkillSlot` start on `Tech`, everything else on `Game`. A canvas follows its element unless set. Without `fonts/ForgeUI-fonts.bundle` everything draws in the game's font and one line is logged. |
| Screen effect stack | No call pushes an effect. Hit edges (`AddHitEdge`) and the visor (`AddVisor`) feed one stack of channels (`Flash`, `Vignette`, `LowHealth`, `Blur`, `Chromatic`); per channel only the highest priority present counts, the strongest of those sets the strength, colours are averaged by strength. Everything is drawn by one full-screen pass at the top of the overlay order; blur and colour split copy the screen once, only while one of them is on, otherwise the pass only blends. Nothing is allocated per frame and no name is looked up. |
| `ui.AddEnemyHealthBars(name, EnemyHealthBarStyle? style = null)` returns `EnemyHealthBar?` | While `Active`: a thin bar over each enemy the local player hits (the game's synced health, so teammates' damage shows too), the lost piece flashing white then shrinking in orange. Only enemies in sight; a bar fades `Linger` seconds after the last hit and goes at once on death. `Style`, `Remove()`; the last switched on picks the style. Damage numbers and kill marks are the hit feedback's. |
| `EnemyHealthBarStyle` | `Bars` (on by default), `MaxBars` (6, up to 12; a new enemy takes the place of the bar hit longest ago), `Linger` (3 s), `Range` (45 m). Off while the player's `EnemyHealthBars / Enabled` is off. Explosions carry no source in the game and do not count as your hits. |
| `ui.AddHitFeedback(name, HitFeedbackStyle? style = null)` returns `HitFeedback?` | While `Active`: damage numbers rising from where hits land (hits on one target within `MergeSeconds` add up; weak spots in `CriticalColour`, bold), four ticks round the crosshair (`HitMarker`; bigger and red for a kill), a mark where a killed target was and a small cross beside the crosshair. `Pulse(HitKind)`, `Show(amount, kind)` (beside the crosshair), `ShowAt(world, amount, kind)` (a `Kill` also leaves the kill mark) drive it from your code while it is active; `Style`, `Remove()`; the last switched on picks the style. Off while the player's `HitFeedback / Enabled` is off. |
| `HitFeedbackStyle` | `Numbers`, `HitMarker`, `KillMark`, `KillConfirm`, `LocalHits` (the local player's hits on enemies drive it; each on by default), `MaxNumbers` (8, 1..32; past it the oldest gives way), `NumberPx` (15), `Rise` (26), `NumberSeconds` (0.8), `MergeSeconds` (0.45), `NumberColour`, `CriticalColour`, `KillColour`, `MarkerColour`. |
| `ui.AddTeammateTag(name, player => TeammateTag)` returns `TeammateTagSource?` | Adds to every row of every teammates panel: `new TeammateTag { Icon, Text, Bar, Colour }` (`TeammateTag.None` for a player it does not apply to). Pictures and short values stand right to left before the health figures, shares as hairlines under the health. `Enabled`, `Colour` (parts without their own), `Remove()`. Runs once per row per frame; a callback that throws is turned off (logged once). |

The library ships no finished icons: a themed picture belongs to the package that adds it and is registered by it (`Frames:` for the suit cockpit, for example).
## Confirmation

```csharp
var ticket = ui.Confirm(UIText.Key("reset.title"), UIText.Key("reset.body"), answer =>
{
    if (answer == ConfirmAnswer.Confirm) ResetLoadout();
});
ticket.Withdraw();          // take it back if it no longer applies; the callback gets Dismissed
```

| Member | |
| --- | --- |
| `ui.Confirm(title, body, answered, confirm = null, cancel = null)` returns `ConfirmTicket` | One shared yes-or-no window for the lobby and the level. In a level: a cockpit panel a little under the middle of the screen, answered with ForgeUI's `Confirm` and `Cancel` key actions (Y and N by default, rebindable under Controls), not while the HUD gives way. In the lobby and menus: the game's pop-up window with two buttons; a click outside or its close box cancels; the same keys answer too. One question shows at a time, at most four wait in order, one more is refused at once. `answered` runs exactly once with `Confirm`, `Cancel` or `Dismissed` (withdrawn, queue full, its screen went away). |
| `ConfirmTicket.Asked`, `Withdraw()` | False when the queue was full; withdraws a question still showing or waiting. |
| `ConfirmQueue<T>`, `TooltipLayout` (Core) | The queue's state machine and the tooltip's placement, without Unity. |

## Drawing your own HUD

| Member | |
| --- | --- |
| `new HudCanvas(hud)`; `Begin()`, `Rect`, `Cell`, `Line`, `Sprite`, `Icon`, `Picture` (a detailed picture drawn once, without the glow and fringe copies), `Text`, `TextWidth` (the width in the canvas's font, kept per text, size and face: up to 1024 widths, so measuring every frame costs a lookup), `Plate`, `Frame`, `Skew`, `CellGauge`, `End()` | Immediate-mode drawing on a HUD element. Call `Begin()` each frame, draw what is visible, call `End()`. Pieces come from pools and are written only when they changed. Positions are centres in HUD units; multiply 1080p pixels by `Unit`. Text sizes are 1080p pixels. `Plate` and `Frame` cut the top-left and bottom-right corners by `cut` (0 square), `Skew` leans a cell right, `CellGauge(x, y, width, height, cells, value, warnBelow, colour, warn)` lights cells from the bottom, the partial one in proportion, in `warn` below `warnBelow` and framed red at 0. `Chip(text, left, y, fill, ink, icon = null)` draws a state badge (a label on a plate, an optional icon before it) and returns its width; `Button(ref right, y, text, on, accent)` draws a text button laid out leftwards from `right`, lit while `on`, framed while the HUD pointer is over it, true on the frame it is clicked. |
| `HudDraw.Box`, `Frame`, `Line`, `SetText`, `SetActive`, `Measure` | For pieces you keep yourself. |
| `UIKitColors` | `Text`, `Bright`, `Dim`, `Accent`, `OnAccent`, `Warn`, `Alert`, `Good`, `Slab`, `Line`, `Shade`, `Cockpit`, `Faded(colour, alpha)` and `Tone(tone, normal)`. |
| `UIKitSprites` | `Pixel`, `Outline`, `Arrow(dir)`, `Fade`, `Slant`, `ArrowGlyph(dir)`, `Pie(step, ring)`, `GlowBand`, `GlowRing`, `GlowBlob`. |
| `UIKitKeys.Name(KeyCode)`, `UIKitKeys.Binding(InputAction, fallback)` | Key names as the game shows them. `Binding` allocates, so read it about once a second. |
| `CommandCode.StartsWith(code, typed)`, `CommandState` | The code prefix test and the ready, cooling and used-up state, without Unity. |
| `Ease`, `MeterMath`, `MeterTrail`, `CooldownMath`, `NumberText`, `TextWidth`, `SegmentBarMotion`, `ArcFlare`, `DamageShares`, `PowerOnTimeline`, `BootMessage`, `HoldTimer`, `NoticeQueue`, `BannerQueue`, `ScreenArbiter`, `HudLayout`, `ScreenMath` | Plain logic without Unity. |

## Game widgets

| Member | |
| --- | --- |
| `UIKit.AddNavMarker(anchor, title, showDistance = true)` returns `NavMarkerHandle?` | One of the game's own world markers on a GameObject. `Title` changes the text, `IsPlaced`, `Remove()`. |
| `ui.AddCountdownMarker(position, UIText name, arriveAt, accent)` returns `NavMarkerHandle?` | A world marker that counts down to `arriveAt` (`Time.time`): the name small and pale, the seconds in large monospaced digits (one decimal) in `accent`, red and larger over the last three seconds, the unit from the `countdown.seconds` key. ForgeUI makes the anchor. Call `handle.Update()` every frame: it stands the marker beside `position` to the camera's right (`Beside`, on by default; `BesideGap`, metres per metre of distance, 0.24; closer than 2.4 m the marker stays 2.4 m ahead) and changes the text only when the tenth of a second, the urgency or the language changed. `Remaining` is the seconds left; `Remove()` also removes the anchor. |
| `UIKit.PostChatNotice(text)` returns `bool` | One line in the local player's own chat box, only on this machine (nothing is sent to other players). False, logged, when the text is empty, there is no local player or the game refuses the line. |
| `ui.AddInteractPrompt(name)` returns `InteractPrompt?` | The game's interaction prompt. Call `Show(text, key)` every frame it should be up, `Progress(fraction)` after it for the hold ring (negative removes the ring), and `Hide()` when it should go. |
| `UIKit.ClaimWeaponListLine(name)` returns `WeaponListLine` | Set `Hidden` every frame it should hold. The weapon list's gear-name line of any slot reading exactly `name` stays off. |
| `UIKit.GearName(texts, packetName, key)` | Shows the gear whose gear-packet public name is `packetName` under the text of `key` (from `texts`, in this player's language) on every gear screen: the lobby gear slots and weapon window, the picker, and the in-level weapon list, where the local weapon slot holding that packet also gets the name on its slim line and on the selected header's archetype and name lines. The packet name itself never changes (it replicates). Registered once, follows language changes, no per-frame work. |
| `UIKit.GearName(packetName, Func<string> name)` | The same for a name that is in no language file of yours: `name` returns it in the current language and runs again after every language change. The same mod registering the packet name again replaces its rule. |
| `UIKit.GearText(texts, text, key)` | The same for a text that is not a gear name (a default description): any text a gear screen draws that holds `text` shows `key`'s text. A text another mod registered is refused (logged). |
| `ItemModelIcons.Of(Item)` returns `Sprite?` | A picture of any item rendered from its own game model (meshes and materials copied to a private stage, photographed over black and white by a private camera, kept per item type so each type renders once). Null while the item is null or collected (ask again later); null with one logged error when the model has no visible mesh or the render failed (not retried). The weapon list uses it for the resource pack, consumable, heavy-consumable and pickup slots. |
| `GearIcons.Supply(categoryId, model)` | Takes over the game icon renderer's job for every gear of a gear category (its persistent ID), so the icon of a gear of your own is drawn by the game's renderer from your model with vanilla camera, light and framing instead of the donor gun. `model` is a `GearIconModel(GearIDRange gear, out string? failure)` asked once a frame while a job waits: return the model to photograph (a fresh copy; the renderer destroys it), or null with `failure` null (not ready yet), or null with a `failure` text. A failure, a throw or 20 s without a model gives an empty picture and one logged error, never the donor gun. Loading the model stays yours. A category already supplied by another provider is refused (logged). |
| `GearIcons.ClearCached(GearIDRange)` | The game keeps each drawn icon as a file per gear checksum and never redraws it. Call this with a supplied gear's packet before its first request: the file is removed once per run for each checksum. |
| `UIKit.Inventory.Held`, `.HeldText`, `.Aiming` | The magazine of the item in hand (`HeldMagazine`: `Limited`, `Clip`, `Size`, `Fraction`; a scanner, a hammer, a carried item or an empty hand has none and reads unlimited; an emptied magazine is 0 of its size), its text (the rounds, or ∞ / INF; the same string while unchanged) and whether the aim button is held on it, all read without allocating. Feed an arc with `() => UIKit.Inventory.Held.Fraction` and `() => UIKit.Inventory.HeldText`. `HeldMagazine.Pick` and `CrosshairRules.Drawn` are the Core rules behind them. | Call once at load for a tool item your mod puts in the backpack for a moment (`ItemDataBlock` id). While the local backpack holds it, the game's weapon list, `UIKit.Inventory` and `HudLoadout` stay as they were before it came in: no row, no selection change, no count; the other slots' counts still update. |
| `UIKit.LoadIcon(path)` returns `Sprite?` | A PNG file as a sprite for pickers (long side about 2.56 units). Loaded once per file. Null and one logged error for a missing or unreadable file. |

## Key actions

Keys your mod reads are registered once at load and read every frame through the handle; the player changes them in the game's own settings (Controls), where they are listed under your group's heading after the game's keys.

```csharp
// lang/en.json { "keys.group": "My Mod", "keys.dash": "Dash" }
var dash = ui.AddKeyAction("Dash", "keys.dash", "keys.group", "LeftShift", KeyMode.Press, "on-foot");
if (dash!.Pressed) Dash();                         // per frame: no lookup, no allocation
ui.AddKeyHint("DashHint", dash, UIText.Key("keys.dash"));   // the prompt shows the player's key
```

| Member | |
| --- | --- |
| `ui.AddKeyAction(name, nameKey, groupKey, defaultKey, mode = Press, context = "")` returns `KeyAction?` | `defaultKey` is a Unity `KeyCode` name with optional `Ctrl+`, `Shift+`, `Alt+` (`G`, `Mouse3`, `Ctrl+Shift+G`, `None`). Refused (null, logged) for a taken or bad name, an empty name key or a key that is not a keyboard key or mouse button. `context` says when the action is live (`exosuit`, `on-foot`…; empty is everywhere): two actions conflict only when their contexts overlap. |
| `KeyAction.Pressed`, `Held`, `Released` | The key went down this frame, is down, came up this frame. A chord matches only with exactly its modifiers held (`G` and `Ctrl+G` are different keys). Every action is read once per frame, on the first read or by the driver; while the settings read a new key every action is up. |
| `KeyAction.On` | A `KeyMode.Toggle` action's state, flipped at each press; settable. |
| `KeyAction.HoldDriven()`, `ReleaseDriven()` | For drivers and tests (ForgeDevelopment's input drive): the action reads as down, with a fresh press edge, whatever key it has and without any key event, until `ReleaseDriven()`; then it reads one released edge and follows the real key again. |
| `KeyAction.Name`, `Chord`, `KeyAction.Version` | The key as the game names it (the game's mouse button names, `Ctrl+G`; the "unbound" text), cached until the key or the language changes; the chord; a counter that changes with any key. |
| `UIKitKeys.GameKeyDown(action, negative = false)` | The frame the player's own key for a game action goes down, read from the game's input map (`InputMapper.Current.m_currentKeyMouseMap`, re-read once a second; for a two-key axis such as `MoveVertical` the positive or negative key). Works while ForgeCore's input lease holds the action off the game. Use it instead of a second action for anything the game already has a key for. |
| `ui.AddKeyHint(name, KeyAction, label)` | A key prompt that shows the action's key. |

- **Saved keys.** `BepInEx/config/NAinfini.ForgeUI.keys.cfg`, one line `<mod>/<action> = <key>` per changed key (`None` for unbound), read when ForgeUI loads, written at each change. A key the player never changed is not stored and follows your default, also a new default in a later version; choosing the default again stores nothing. Lines of mods not installed are kept; bad lines are skipped with a warning.
- **Conflicts.** Two actions on one chord with overlapping contexts are shown in the settings; so is a game key equal to the chord's main key (the game key fires whatever modifiers are held). Nothing is moved: the player decides.
- **Settings rows (game facts).** The game builds its settings in `CM_PageSettings.Setup`: each sub-menu is a `CM_ScrollWindow` in `m_allSettingsWindows`; the keyboard and mouse key page (`eSettingsSubMenuId.KeyMap`) lists `CM_SettingsItem` rows, each holding a `CM_SettingKeyBindingField` (`m_data` is the game's `InputMapData`, `device` Keyboard or Mouse; the gamepad page's are Gamepad; `m_valueText`, `m_background` with `m_bgActive` / `m_bgNotActive`, `m_readingActive`; it reads keys itself in `Update` and writes them through `iKeyBindingInputReceiver`). After `Setup` ForgeUI copies one of those rows per action, plus one per group as a heading (the copy with its key box off), and adds them after the game's rows in the list the game itself hands to `SetContentItems` while it builds the page (never a second call: the game's `SetContentItems` destroys the container's children first). The copies' field methods (`Update`, `OnEnable`, `OnBtnPress`, `OnBtnPressAnywhere`, `OnHoverUpdate`, `OnHoverOut`, `UpdateValueField`, `UpdateText`, `ResetValue`, `SetReadingActive`, `SetupInputItem`) and the rows' `OnEnable` / `UpdateText` are skipped, so the game's reading never touches its own bindings for them; ForgeUI reads the key (it waits for every key to be up, Esc cancels, Backspace or Delete unbinds) and writes the box. `CM_PageSettings.ResetPageSettings(KeyMap)` (the page's reset) also resets every action. When no keyboard key row is found the error is logged and every action keeps its saved key.

## Settings page rows

`SettingsPages` is the one place that adds rows to the game's settings pages (the key actions above use it too). The game builds the key pages in `CM_PageSettings.CreateKeyMappingSubMenu` and every other page in `CreateSubmenu`, each handing its finished row list to `CM_ScrollWindow.SetContentItems` once; ForgeUI appends its rows to that list, checks afterwards that every row is in the list and under the content container, and passes the game's page reset on. Every added row is a copy of one of the game's rows.

| Call | What it does |
| --- | --- |
| `SettingsPages.AddAudioSliders(sectionId, headingEnglish, headingChinese, sliders, onPageReset = null)` returns `bool` | Adds a heading and one slider row per `SettingsSlider(Id, English, Chinese, Get, Set)` (value 0 to 1) to the game's Audio page, copied from its sound-effects row. A drag calls `Set` at once; `Get` is read again twice a second when the player is not dragging; `onPageReset` runs when the player resets the page. Call it in `Load`, before the game builds its settings. The same `sectionId` is added once (a repeat returns false and does nothing); a call after the page was built returns false and logs an error. |

ForgeUI does not know what the values are: `ForgeAudio.AudioVolumes.Controls` gives the kit's volumes in this shape (see ForgeAudio's DEVELOPERS).

## Screen layers

Layers draw over the whole picture, after every game camera, with one shared camera. Only one *exclusive* layer covers the screen at a time. A layer that wants the screen asks every frame. It gets the screen when it is free. A higher-priority layer takes it at once, and the holder is told to put itself away. An equal or lower one waits. *Overlay* layers (hit edges, the visor) take no part: any number draw together, under the exclusive one.

| Member | |
| --- | --- |
| `UIKit.LoadShader(id, bytes, asset)` returns `Shader?` | A shader from an embedded asset bundle, loaded once per `id`. |
| `ui.AddPowerOn(name, PowerOnSpec)` returns `ScreenPowerOn?` | A power-on played over the whole picture (exclusive, `Priority` 50): your `Panels` and `Seams`, each panel black until its block's turn in `Timeline` (`new PowerOnTimeline(blockOfEachPanel, PowerOnRhythm)`), then ForgeUI's glitch (the picture in `Tint`, banding, scanlines, split into three copies slid left, in place and right in `ChannelLeft` / `ChannelMiddle` / `ChannelRight` — red, green, blue by default, adding back to the picture where they line up — with torn bands and short noise bars lit in those colours) settling to the plain picture, the seams fading after the last block; once they are gone the picture is whole (`Timeline.Assembled`, `Timeline.IsAssembled(seconds)`; `PowerOn` returns true from then and hides). When the player gets control back is the caller's: the power-on never holds movement. Powering down runs the blocks in reverse to black, holds and clears (the word in the middle is `HudBootMessage.PowerDown`'s). `Fade(black)`, `PowerOn(seconds)`, `PowerDown(seconds)`, `Hide()`, `Warm()`; `Shader` replaces the built-in glitch; a custom `Shader` gets `_State`, `_Tint`, `_ChannelLeft`, `_ChannelMiddle`, `_ChannelRight`. |
| `ui.WarmNextLayer()` returns `string?` | Builds and compiles the next of this mod's screen layers (power-ons, hit edges, visors) not ready for the current screen, drawn once off screen; one per call, null when all are ready (again after the screen size changed). `while (ui.WarmNextLayer() is { } id) yield return null;` |

## Notes

- Everything is parented to the game's own UI objects and copies their layer. The menu camera is disabled and drawn by the game's UI pass, so a check such as "no enabled camera draws layer 5" is expected and does not mean something is invisible.
- Do not move pieces to another layer or parent them outside the game's UI.
- `Auto` reads the game's language from `Localization.Text.TextLocalizationService.CurrentLanguage` (`Modules-ASM`, enum `Localization.Language`: English, French, Italian, German, Spanish, Russian, Portuguese_Brazil, Polish, Japanese, Korean, Chinese_Traditional, Chinese_Simplified), four times a second. A mod's `lang/` is found from its plugin's DLL location in the BepInEx chainloader.

## Drawn ahead of first use

The screen effect stack is warmed with the other screen layers (`ui.WarmNextLayer()`, `HitEdge.Warm()`, `Visor.Warm()`, and on its own on the elevator ride): it loads its shader, builds the hit band and interference textures and draws its pass once off screen in both variants (with and without the screen copy), so turning blur on later compiles nothing.

## Logging

Logging goes through ForgeCore's `ModLog` and only real failures are written: `Plugin.Error` takes a message and uses its text up to the first colon as a short fixed key, so a given failure is written once. Expected limits and refusals (a full HUD region, a full pool, a full confirmation queue) write nothing. The library has no info, warning or timing log calls and no diagnostics setting.
