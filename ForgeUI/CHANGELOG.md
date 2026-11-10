# Changelog

## 1.0.0

Version number only: the first release was 0.1.0, and 1.0.0 is the version the Forge packages share from now on. Packages that depend on this library need 1.0.0 or later. Interface change: `SettingsSlider` takes a language `TextKey` instead of English and Chinese labels, and `SettingsPages.AddAudioSliders` takes one `headingKey` instead of two heading texts. The audio volume labels now live in ForgeUI's language files. `CommandMotion`, `EnemyBarBook`, `FloatingNumbers`, `IconBook`, `EdgeFlashes`, `ScreenEffectBook`, `WaypointBook` and `WaypointMath` are now internal: nothing outside the package used them.

- Item pictures wait for the game to build an item's model and fall back to the model prefab, so a consumable still in a backpack no longer loses its picture.
- A borrowed objective timer is taken down when released even if the game or another mod changed its text meanwhile.

## 0.1.0

First release.

- Lobby buttons for mods, stacked under APPAREL, with one offset setting for all of them.
- Pickers in the game's own loadout pop-up: a slot column, tabs, a card list, a details box with badges, and refusal text.
- Top menu bar buttons and pages that open in the game's menu pop-up.
- One shared HUD. Each mod registers HUD elements by region and priority instead of by position. The kit stacks them, keeps the game's weapon list and health bar free, and folds away the weakest element when a region is full.
- Hide the game's own HUD by lease: HideGameHud (all but a keep list) and HideGameHudParts (only the named parts) share one request table; each part is hidden at its root objects, at a cost independent of how many pieces the game draws, and restored exactly on release.
- One shared banner queue and one shared notice list for every mod.
- Ready-made HUD pieces: ability slot, key prompt, banner, notices, world marker, hit marker, damage numbers, hint line and a command list.
- Wrappers for the game's world marker, interaction prompt (with its hold ring) and weapon list line, so mods never touch the game's HUD code.
- A full-screen power-on layer with seams and boot message, one exclusive layer at a time across all mods.
- Picture files load as picker icons.
- Key actions: mods register their keys once and read them every frame; players change them in the game's own settings (Controls), choosing per key how it fires (on press, on release, short press, long press, double press), how long a long press lasts and how far apart a double press may be, what happens on a key the game also uses, with conflicts marked and changes saved in `NAinfini.ForgeUI.keys.cfg`.
- Texts in several languages: each mod ships `lang/<language>.json`, titles, pages and HUD labels take a key, and everything switches when the language changes. A `Language` setting follows the game's language by default. Missing texts fall back to English, and the files are checked against each other at first use.
- Cockpit pieces: hide the game's HUD by parts while a mod needs the whole screen (counted per mod, released on death, level end and disconnect), move the chat, a wide curved segmented bar with shield, health and infection strips, crosshair arcs, a ring of contacts out of sight with a turn-around hint, a hull damage disc, an inventory with a card for the selected slot, ability slots with cut corners, small text readouts, a fading key prompt, world marker bars and icons, picker stat rows and lobby icon rows.
- The game's objectives (with timers and map prompts), interaction prompt, subtitles, inventory, warden intel, boosters, pickups, the player's health and the teammates (with infection, ammunition and status) can be read and redrawn in the kit's style, in the game's language.
- Screen overlays: hit-direction flashes on the screen edge, and a helmet visor with corner shade, struts and brackets (each with its own opacity), three layers of cracks from a picture, heat on the bottom edge, interference and a boot sweep, faint by default and kept off the middle of the screen; strength settings under `Screen`.
- One cockpit look: cut-corner plates and frames, slanted cells and cell gauges in `HudCanvas`; lit parts of HUD pieces glow softly (`Screen / Glow`) with a faint 1 px colour fringe (`Screen / Fringe`); a timeline for boarding and leaving.
- The kit's own fonts: Rajdhani for Latin letters and digits and Glow Sans SC Condensed for Chinese, falling back to the game's font. Every HUD piece can pick them; the cockpit pieces use them by default.
- Enemy health bars that mods switch on: only enemies in sight; the bars show each hit's damage, add up bursts into one number and mark kills.
- Hiding the game's HUD also covers the cracked-glass camera effect; the kit's pieces follow the game when it puts its HUD away (map, terminal, menu); the player's grabbed or stunned state can be read.
- Icon packs: mods register pictures as `Pack:name` and anyone looks them up by that name, and the game's own icons by `Game:name`; every duplicate, missing name and unreadable file is reported once.
- Missing texts fall back to the same language family first (`zh-hant` to `zh`), then English.
- Settings for the lobby and HUD offsets, the HUD safe margin, and the language.
- Click callbacks for the library's silent presses. The library plays no sounds of its own.
- `UIKit.PostChatNotice` puts a line in the player's own chat box on this machine only, so mods never call the game's chat code.
