# Driving the ForgeSkin editor with an AI agent

This guide is for an agent that controls the desktop editor through the Chromium DevTools protocol (Playwright
`connectOverCDP`, a DevTools MCP server, or plain CDP). The editor is an Electron app with one window; everything below
is done in that window.

## 1. Start and connect

The editor opens a debugging port only when it is started with `--ai-control`:

```
ForgeSkin.exe --ai-control            # port 9333
ForgeSkin.exe --ai-control=9444       # your own port, an integer from 1024 to 65535
```

- The port listens on 127.0.0.1 only. Without the flag no port is open. A port that is not an integer from 1024 to 65535
  is ignored (the editor starts without a port and writes one `ai-control` line to `GTFO_UserData/logs/app.log`).
- The editor has no single-instance lock: a second launch is a separate editor on the same data folder and settings. Close an
  editor that is already open before starting one with `--ai-control` so the port belongs to the only copy.
- Installed copy: run `ForgeSkin.exe` from the install folder (`app-<version>` under the Squirrel install folder) with the
  flag. From a source checkout: `pnpm start -- -- --ai-control` in `Mods/ForgeSkin/Editor`.
- Check it is up: `http://127.0.0.1:9333/json/version` answers, and `http://127.0.0.1:9333/json/list` has one target of type
  `page` titled `ForgeSkin`. Connect to that page (Playwright: `chromium.connectOverCDP('http://127.0.0.1:9333')`, then
  `browser.contexts()[0].pages()[0]`).
- The flag also keeps the window rendering when other windows cover it (otherwise menus and dialogs do not appear) and makes
  every file dialog an in-window path dialog (section 4).
- Close the editor window (or end the process) when you are done; the port closes with it.

The page exposes the preload API as `window.forge` (file dialogs, paths, backend call). Prefer the UI; `window.forge` is not
a supported automation surface.

## 2. First-start guide and language

A fresh editor (and **Settings** → **Restart the guide**) opens the spotlight guide: the window darkens and only the lit
control takes clicks. To work freely, press Escape, then **Leave**; it does not open by itself again. The **Tutorial**
button (book icon, top bar) opens the tutorial pages.


The UI is Chinese or English. A fresh editor follows the system language. All control names below are the English ones.
Switch to English first: **Settings** (gear button, top right) then the **English** option of the language switch, then press
Escape. Controls are found by accessible name (`aria-label`, or the button text); the English names below are exact.

## 3. Screen map

| Area | What it is | How to reach it |
|---|---|---|
| Top bar | **Settings**, **Publish** menu, test profile chip (named after the game profile), **Apply to game** with a **More** arrow | always visible |
| Left panel, tab **Parts** | The part tree of the open item: a row per part (eye, enable checkbox, name, **+** menu with the part's add menu), with the part's paint layers, animated layers and attachments below it. Search box **Search parts, attachments, layers** | button **Parts** on the left edge |
| Left panel, tab **Skins** | One card per skin of the open item, **New skin** card, each card's **More** menu | button **Skins** on the left edge |
| Centre | The 3D view. Title button **Change item (Ctrl+P)** opens the weapon gallery; **Kill / Hit / Fire / Equip** simulate game events | |
| Right panel | Inspector of what is selected. Its categories are the icon buttons on the right edge (named by their label) | select a row on the left |
| Status bar | Busy text, `Autosaved`, undo and redo (**Undo · Ctrl+Z**, **Redo · Ctrl+Y**) | always visible |

Right-edge tabs by selection: nothing selected (**Overview**, **Sounds**); a part (**Textures**, **Livery**, **Layers**,
**Attach**, **Place**, **Sounds**); an attachment (**Place**, **Look**, **Screen**, **Effects**, **Sounds**).

Part rows, layer rows and attachment rows are `role="option"` elements that carry `aria-selected`; skin cards are
`role="button"`; each texture slot is a `role="group"` named after its channel (**Albedo**, **Normal**, **Metal/Smooth**,
**Emissive**).

## 4. Files: path dialog and drops

Started with `--ai-control`, the editor never opens a system file dialog. Every button that would (open a file or folder, save,
export) opens an in-window **path dialog** instead:

- A modal dialog (`role="dialog"`) titled **Choose a file**, **Choose files**, **Choose a folder** or **Save as**, with one
  text field named **Path** (**Paths (one per line)** when several files are allowed), a line `File types: …`, and the buttons
  **Cancel** and **OK**. The field starts with the folder or file name the editor would have offered (the last place used for
  that kind of file, or the suggested file name) and has focus.
- Type or paste the absolute path and press Enter (or click **OK**); Escape or **Cancel** cancels, exactly like cancelling a
  system dialog. Quotes around a path are ignored. For several files put one path per line; Enter confirms, Shift+Enter adds a
  line.
- The dialog checks the path and stays open with a red message under the field when it is refused: **That path does not
  exist.**, **That is a file; choose a folder.**, **That is a folder; choose a file.**, **Enter a full path, ...** (relative
  paths are refused), and for saving **The folder that should hold the file does not exist.** or **That is a folder; give a
  file name.** Fix the text and confirm again. Saving adds the file type's extension when the name has none, and replaces an
  existing file without asking.
- In Playwright: `page.getByRole('dialog').getByLabel('Path').fill(path)` then `press('Enter')`; the dialog closes when the
  path was taken. A dialog is asked for one thing at a time; answer it before the next click.

Two more ways to bring files in without a dialog:

- **Drop files on the window.** Dispatch a real drag with CDP `Input.dispatchDragEvent` (`dragEnter`, `dragOver`, `drop`)
  whose `data.files` is a list of absolute paths on this machine.
  - Dropped anywhere: image files (or one folder) are matched to parts and channels by file name (section 6); a
    `.forgeskinproj` / `.gtfoskin` / `.gtfdyn` file opens as a project; with a part selected, video or gif files become
    animated layers on it. `.glb` / `.gltf` dropped on the window are refused with a hint (use **Import model (.glb)**).
  - Dropped on a texture slot in the **Textures** tab: that image becomes that channel of the selected part. Use the
    slot's on-screen position (`getBoundingClientRect` of the `role="group"` element) for `x` and `y`.
  Playwright's `setInputFiles` is not enough for these (they are drops, not file inputs); use a CDP session.
- **Apply to game** installs the built pack into the chosen game profile without any dialog (section 5, step 10).
## 5. Main flow

Each step lists the control and what tells you it worked.

1. **Open the editor and pick an item.** Click **Change item (Ctrl+P)**; the gallery has a search box and slot chips; click a
   card. Success: the 3D view's title shows the item name and the Parts tab lists its parts.
2. **New skin.** Left edge **Skins**, then the **New skin** card, then the **New skin** menu entry. Success: a new card
   appears and is the selected one (the first skin of an item is already there when the item has none yet). Rename: the card's
   **More** menu, **Rename**, type, Enter.
3. **Select a part.** Left edge **Parts**, click the part row by its name. Success: the row has `aria-selected="true"`, the
   right panel's header says **Part** with the part name, and the right-edge tabs include **Textures**.
4. **Replace a texture.** Right edge **Textures**; drop an image on the channel slot (section 4). Success: the slot's badge
   changes from `ORIGINAL` to `REPLACED` and its caption shows `Albedo · <file name>`; the part's row shows a lit channel
   square. A folder of images named after parts and channels can be dropped on the window instead; a toast says how many
   textures were matched, and unmatched files are listed in **Overview** under the section headed by their count. Matches the
   editor is unsure of are listed as yellow cells to confirm: **Confirm** on a slot, or the confirm-all button in
   **Overview**. Remove a replaced texture with the **x** on the slot.
5. **Add a paint layer.** Right edge **Livery**; pick a finish, or click **Base coat**, **Decal**, **Brush**, **Pattern** or
   **Wear**. Success: a row with that layer's name appears under the part in the tree and the right panel switches to
   **Paint layer · <kind>** with its settings (colour, opacity, blend, mask). Select the part row again to go back.
6. **Add an attachment.** Right edge **Attach**, **Add attachment**, then **Ammo screen** (a live screen), **Import model
   (.glb)** (path dialog) or **Shapes** (hologram shapes). Success: a row such as `Ammo screen 1` appears under the part and
   the right panel shows **Screen** with **Mount**, **Position & size** fields; its tabs **Look**, **Screen**, **Effects**
   configure it. Delete it with **Delete this attachment**. The part's **+** button in the tree opens the same add menu plus
   the paint-layer and animated-layer kinds (**Livery** and **Animated textures** are submenus). A new attachment starts just
   above its part. **Charm** (submenu next to **Shapes**) adds an imported model or a shape already hung as a charm, selected,
   with the **Place** tab open. At the top of **Place**, the **Charm** switch hangs any attachment on a rope from its
   hanging point (its origin, an orange dot drawn over the gun; a new charm's sits on the middle of its part's underside;
   move it with the 3D view's gizmo or the position fields): style **Cord / Chain / Beads**, sliders **Length**,
   **Thickness**, **Weight**, **Settles**, **Stiffness**, **Bends**, a **Colour**, the switch **Stays out of the gun**, and
   the buttons **Shake**, **Run**, **Turn** that play a motion in the 3D view. The switch is disabled once the gear has 4
   charms. Under the heading **Code effect** the same **+** menu has **Animated pattern on this part**: a row
   `Code effect 1` appears under the part and the right panel shows **Code effect** with **Where it draws** (**Draw on**: a
   material of the item or an attached model; **Paint into**: Albedo or Emissive), **Pattern** (a select, one slider per
   number, one colour field per colour), **Size and motion** (sliders **Scale**, **Rotation**, **Spin**, **Intensity**;
   **Blend** **Paint over / Add light**), **When it plays** (the event select; for an event **Shows for (seconds)** and
   **Play the event in the 3D view**) and **Delete effect**. Each slider and colour has a **Player** checkbox: ticked, players
   may change that value in game. Success: the pattern moves on the part in the 3D view and follows the sliders.
7. **Preview.** The 3D view shows the textures, paint layers and attachments as you edit. **Kill / Hit / Fire / Equip** play
   the skin's effects and sounds on a simulated event; **Frame selection**, **Perspective / Orthographic** and the view
   cube change the camera.
8. **Save.** The skin is autosaved to the editor's data folder about 1.5 s after each change; the status bar says
   `Autosaved`. To keep a project file elsewhere press `Ctrl+S` (or the card's **More** menu, **Save to file…**): the first
   time the **Save as** path dialog opens with `<skin name>.forgeskinproj`; give a full path, e.g.
   `C:\work\skins\mine.forgeskinproj`. Success: a toast says `Project saved` and the status bar's right side shows the file
   name. Later saves overwrite that file without asking. **Snapshots** (card **More** menu) keep named copies in the data
   folder.9. **Check.** **Publish** menu, **Check**. A dialog lists, per skin, what must be fixed before a build (missing files, pending
   confirmations, a part with fewer than four channels while replacing textures — **Keep missing parts as they are** under
   the **More** arrow of **Apply to game** relaxes that). Success: it says there is nothing to fix.
10. **Build and install into a game profile.** The profile chip in the top bar names the game profile (its menu lists the
    profiles found; **Add a profile folder** asks for the folder in the path dialog). Click **Apply to game**. It installs the
    plugin into that profile when it is missing, builds the pack, writes it to `<data folder>/packs/<item id>.forgeskin` and
    installs it into the profile's `BepInEx/plugins/ForgeSkin/skins` as the active skin. Success: a toast says the skin was
    installed and when the game will show it; with the game running and the plugin's `Development` setting on, the game
    reloads it and the editor reports whether it loaded. Failure reasons are in a red toast; the log button at the bottom
    left (with **Developer mode** on in **Settings**) shows the full log. Applying writes real files into that profile:
    close the game first and use a profile meant for testing.
11. **Export a skin pack.** **Publish**, **Export skin pack…** opens the **Save as** path dialog with `<skin name>.forgeskin`;
    give a full path, e.g. `C:\work\out\mine.forgeskin`. Success: the dialog closes, the editor builds (status bar text, a
    few seconds) and a toast says Skin pack exported with a **Show file** button; the file exists at that
    path. If the check finds something to fix, a red toast says so first (step 9). **Publish**, **Export as Thunderstore
    skin mod…** opens a dialog (format, name, version, description, icon, skins). The format is **ForgeSkin**, **Skins: a
    group per skin** (checkboxes of every item's skins, grouped under item names) or **Skins gun pack** (one select per item
    with skins, named after the item; **Not in the pack** leaves it out). Name and version must be valid and an icon is
    needed; **Choose image…** opens the path dialog for an image (the dialog fills in the item's picture by default).
    **Export** then asks for the zip path in **Save as**. When the Skins format left something out, a dialog titled **Skin
    mod exported** opens; its accordion **N things left out or changed** lists them.
12. **Import a Skins skin group.** **New skin** card, **Import a Skins skin group…**, then the path of a
    `manifest.skingroup.json` in the path dialog. Success: the editor opens the group's skin of the open item (else its
    first) and a dialog titled **Skins skin group imported** lists the items that got a skin (buttons that open them) and
    the accordion of what was left out or changed; **OK** closes it. A group that names no gear opens **This skin group
    names no weapon** instead: pick an item in **Put it on** and click **Import**, or **Cancel**. The same menu's **Export OBJ model…**, **Export UV
    templates…** and **Export game asset pack…** ask for a folder or file the same way.
## 6. Texture file names and limits

- Accepted image types for texture slots and folder drops: png, jpg, jpeg, tga, tif, tiff, bmp, webp.
- A name is matched to a channel by words in it (any case): `albedo`, `base colour`/`basecolor`, `diffuse`, `colour`
  → Albedo; `normal`, `nrm` → Normal; `metallic`, `metalness`, `smoothness`, `gloss`, `roughness` → Metal/Smooth; `emissive`,
  `emission`, `glow` → Emissive. The part is recognised from the part's id or role words in the name
  (`<part>_BaseColor.png`) and, failing that, from the image content compared with the game's own texture. A match below
  82 % confidence waits for confirmation.
- Sizes: the pack stores each texture resized (aspect kept) by the size mode in the **More** arrow of **Apply to game**:
  **Keep** (default), **Original** (the game's own size), **1K**, **2K**, **4K** (longest side). A texture over 8192 pixels on
  a side is rejected by the game plugin, so never supply one; keep power-of-two sizes. Textures cost video memory in game; the
  Overview header shows the skin's estimate after a build and warns above 256 MB (light) and 512 MB (heavy).
- The normal map is read in the usual tangent-space layout; the pack converts it for the game.
- Sounds: any common audio file (wav, mp3, ogg, flac, m4a, aac, opus, webm), at most 10 seconds, converted to Ogg Vorbis
  in the pack. **Add sound** (sound tab of any selection) is a menu: pick the event, then the file in the path dialog; the
  **Sounds** tile in **Overview** adds a kill sound. A kill sound's **Which enemy** list (searchable, every enemy of the
  game's data by its exact name and data block name) moves it to that enemy's own group; **Any enemy** plays for every kill
  of an enemy without its own group. Sounds of one event (for kills, one enemy) form a group that plays one at random by
  **Weight**; each group head has "Play one of …" and "Add a sound to …" buttons, each row "Play …" and "Open …".

## 7. What is in a skin and in a pack

- **Project file** (`.forgeskinproj`, and the autosave next to it in the data folder): the skin document as JSON. It holds
  the skin id and name, the target item, per-material texture paths (absolute paths on this machine), paint layers,
  animated layers, attachments, part layout, sounds and player colours. Paths in a project point at your files; the editor
  watches them and re-reads a file that another program rewrites, so an agent can regenerate a texture on disk and the
  view updates.
- **Pack** (`.forgeskin`): a zip with `manifest.json` at the root (the same document, with paths relative to the zip) and
  the files it names: `textures/<material>_<channel>.dds` (resized textures, compressed for the game to upload as they
  are; `.png` when a side is not a multiple of 4), `layers/<id>.png` (baked animated-layer
  atlases), `meshes/*.forgeskinmesh` (attachment and part models), `glyphs/*.forgeskinmesh` (screen digits and borders) and
  the sounds as Ogg files. Every referenced file must exist and be a PNG or JPEG (a material texture may also be a DDS), an
  Ogg file or a `.forgeskinmesh`; paths may not be absolute or contain `..`. The pack is not a source: open the project to
  get the original pictures back.
- **Code effects** (`codeEffects` in the document, at most 8 per skin): patterns the game draws live with ForgeCore's
  drawing programs. Fields of each entry: `id`, `name`, `visible`; the target, exactly one of `material` (a material that has
  an override in the skin; `channel` `albedo` or `emissive`, painted into that texture by its UV, over the textures and
  animated layers) and `attachment` (the id of an attachment with a model; drawn on a copy of the model just outside it);
  `program`; `params` (number name → value); `colors` (colour name → sRGB RGBA, each 0..1); `scale`, `rotation`
  (degrees), `spin` (degrees per second), `intensity`; `blend` `alpha` or `additive`; `trigger` (`always` or an event, with
  `seconds` it shows for, and `lowAmmoFraction` for `lowAmmo`); `playerTweaks` with the lists `params`, `colors`,
  `transform` naming the values players may change. The programs, their numbers (name, range, step, default) and colours
  (name, default) are the catalogue `Mods/ForgeCore/Visual/Data/fx-programs.json`, the same file the game checks packs
  against: read it there, the editor's sliders show the same ranges and labels. A program, number or colour the catalogue
  lacks, a value out of range or a wrong target fails the pack with `bad-fx-program`, `bad-fx-value`, `bad-fx-target`,
  `bad-fx-tweak`, `bad-trigger` or `too-many-fx`. The pack needs no file for a code effect. What players change is stored
  on their computer, never in the pack; the Skins formats cannot carry code effects (the export report lists them).
- What an agent can generate directly: the image files that go into texture slots and decals, and audio files. Hand-editing a
  pack or the autosave bypasses the editor's checks and bake step; make changes in the editor instead.

## 8. Known limits and things not to do

- Without `--ai-control` the same buttons open the system's dialogs, which a protocol client cannot drive; always start the
  editor with the flag.
- The path dialog does not browse: you must know the absolute path. The folder that should hold a saved file must already exist.
- Do not edit the game profile's `BepInEx/plugins/ForgeSkin` folder or `active.json` by hand; **Apply to game** and the
  **Installed skins** entry in the **More** menu next to it manage them.
- Do not run two editors at once; they share one data folder and settings.
- A change made through the UI is one undo step each; do not rely on keyboard shortcuts while a text field has focus.
- Applying into a profile writes real files there; use a profile meant for testing.
- Colour inputs and sliders are custom controls: set them with the on-screen picker or type in the number field next to
  them; setting the hidden input value directly does not update the skin.
