# ForgeSkin for developers

A standalone mod: it depends on ForgeCore and BepInExPack_GTFO only. Game events that other packages also watch come from
ForgeCore `GameHooks` (gear parts spawned, gear spawn completed, enemy damage, reload start and end, dry fire, unwield,
flashlight, glue fire, throw, resource pack, player revived, item spawned, game state) and ForgeCore `GameEvents` (local player down); only methods no other package patches are ForgeSkin's own Harmony
patches in `Native/Hooks.cs` (fire animation, melee attack, mine and sentry placed, the placer's mine-spawned callback, item used, equip, aim, the spawn-complete
overrides of melee, mine, sentry and scanner, item layer, first-person rendering). Each patch class installs on its own and
skips a target whose function start ForgeCore refuses (`Hooks.Unsupported`); one that fails logs `skin-hook-unavailable` and
the rest keep working. The frame tick is ForgeCore `Frame.OnUpdate`; a pack's sounds are one ForgeAudio library
(`AudioKit.Load` from the pack's Ogg bytes, at most 10 s a clip; the sounds of one trigger, for Kill of one enemy (`enemyId`, the `EnemyDataBlock.persistentID` read once from the killed enemy; a kill whose enemy has no group of its own plays the group without `enemyId`), are one group that plays one of them by weight); kept textures and meshes go through `Assets.Keep`; the
settings, picker choices, player colours and readback files go through `Settings` and `Store.WriteText`; wear counts through `Store.Read` / `Store.Write` at their own path.

What ForgeSkin draws goes through ForgeCore's visual and UI parts: a pack's precompressed texture through `Images.QueueRaw`, a
pack's PNG or JPEG through `Images.Queue` (the options of `ImageTextures.Decode`)
(normal maps in the game's DXT5nm layout), a pack's SFM1 mesh through `MeshArrays.Build` (the reader of the format stays in
`Core/`), every emission write through `Glow` (`Write`, `SetColour`, `Clear`), gear icons through `GearIcons.Redraw`, and
the picker through `UIKit` (`PickerSource` and a lobby button), code effects through `Procedural` (below). Texts are in
`lang/en.forge` and `lang/zh.forge` beside the DLL. What stays here is ForgeSkin's own: the pack format, the skin targets,
live screens, animated layers, trigger effects, casing pools and the part layout.

## What is skinned

Only the local player's own: their gear in first and third person, and what it leaves in the world. Other players' and bots'
gear is never skinned (scope, not a gap: skins are not synced). Every look goes through `SkinnedGear` (apply, revert, wait for
the skin to load, release after 30 s unused).

- **Placed sentry.** `SentryGunInstance` is spawned from the gear's `GearIDRange` and builds its own parts: it is skinned where
  its parts spawn like any gear, once its `Owner` (set in its `OnSpawn`) is the local player. Picking it up destroys it.
- **Placed mine.** The deployer's (or consumable mine's) `OnStickyMineSpawned` callback, run for the placer only, hands over
  the spawned `MineDeployerInstance`; it wears the deployer's skin key, materials only (its model is its own prefab).
- **Dropped magazines.** A gun's 5 drop copies (`BulletWeapon.m_magDropPool`, made once in its `OnGearSpawnComplete` from the
  magazine under `m_aMag`) are the gun's own and reused oldest first; the gun's apply swaps their slots by the same material rule
  to world-projection copies of its looks, and its revert puts the game's materials back.
- **Pickups.** `GameHooks.ItemSpawned` in pickup mode (level pickups, container contents, dropped items): a pickup whose item
  has a skin (`item:<ItemId>`) is skinned on its object, materials only, and keeps it while picked up and dropped again. Every
  pickup of the level is listed so a pick or reload reaches it; one that loses its skin goes back to vanilla.
- A placed mine or a pickup shares only some materials with the item the skin was made for; Development logs every slot as
  `swap` or `kept`.

## Picker and player colours

The picker opens in the lobby's loadout pop-up, from the SKINS lobby button (there is no key). One slot per gear or item that has a loaded skin; the SKINS
tab picks one of its skins or the original look (kept in `BepInEx/config/ForgeSkin.choices.json`), the COLOURS tab sets the
player colours (`playerColors`) of the skin it wears: per colour the skin's own value, the author's palette, or, with no
palette, twelve hues at the default's saturation and brightness. Colours are kept per skin ID in
`BepInEx/config/ForgeSkin.colours.json`; a pick paints a copy of the skin document (`Skin.Painted`) and live gear takes it on
the next tick, as a skin pick does. The picker does not open inside a level. Its details add, in the warning colour, that the game's texture quality is below
full (`QualitySettings.masterTextureLimit`: every mipmapped skin texture drawn at half or a quarter), and, in the alert
colour, each pack the last reload rejected with its reason (`Skins.Failed`).

A gear with several installed skins (several packs for one target, or an editor gun pack) lists them all, each card with the
skin's name, its author when the pack has one, and "adjustable" when it has player tweaks; a slot tile shows the gear's game icon
(`GearIconRendering.TryGetGearIconSprite`), which ForgeSkin redraws in the skin it wears. Which skin a target wears, first match
wins: the picker's choice (ignored while that skin is not installed for the target; "" is the original look), `active.json`
`targets`, then the first pack by path among skin mods and registered packs. The editor's 应用到游戏 alone goes before the
picker's choice: the backend (`pack.install` with `pick`) leaves a one-shot `<ticks>.pick` (`{"target", "skin"}`) in the skins
folder, and the plugin's next reload writes it into its own choices and deletes it; a pack put in by hand or another change to
`active.json` does not, and a skin it names that a choice keeps off is reported in `forgeskin.log`. Global skins (`active.json` `global`) are on every
gear under its own skin (the gear's skin wins on a material both cover); the picker neither lists nor removes them, so the
original look removes only the gear's own skin, and its details say how many global skins stay. Everything is local to this
machine.

`Native/Previews.cs` makes the picture in the details of the card in focus, never for the whole list: the skin loads on demand
as a worn one does (`Skins.Ready`; until then the details say the preview is being prepared), then a tracked gear of that target
nobody holds (the lobby model, else the one the game's icon renderer builds after `GearIcons.Redraw`, at most twice) wears that
skin for one frame (`SkinnedGear.Photograph`: revert, apply with the tried skin, ForgeCore `ItemModelIcons.Photograph`, revert,
apply again) and the picker refreshes. Pictures are kept per target and skin until the pack, the skin's colours or the global
skins change (`Skins.LookOf`, `GlobalsLook`), at most 24; one not made in 20 s logs a warning and is not tried again. Items
(no icon-renderer model) get no picture; code effects and charm motion are not in it.

The ADJUST tab lists, for the skin the slot wears, every code-effect value its author marked for players (`playerTweaks`): a
`UISlider` per number or transform entry and a `UISwatches` row per colour (the skin's value and eleven hues). A change is kept
under `fx:<effect>/<param|color|transform>/<name>` in the same skin's entry of `ForgeSkin.colours.json` (one number, or sRGB
RGBA), written 0.5 s after the last change; live gear re-reads it through `Skins.TweakVersion`.

## Code effects

`Native/CodeFx.cs`. The document's `codeEffects` are checked against ForgeCore's program catalogue (`Core/CodeEffect.cs`,
`CodeEffects.Validate`; the plugin sets `CodeEffects.Catalog = Procedural.Catalog`, the backend reads the same embedded
`fx-programs.json`). One `ProceduralLook` per effect; `CodeEffects.Resolve` fills it from the document and the player's
values, only where the author allowed them.

- **On a material.** One `FxCanvas` per skin material and channel: a RenderTexture of the texture's size and its painters exist only while the gear is held; on a frame with an active effect the
  base (the skin's texture after its animated layers, or the original) is copied in, then each active effect is painted over
  it with `Procedural.Painter(..., over: blend)` per mesh and submesh that uses the material, in document order, then mips.
  The canvas replaces the texture on the material and gives it back on release; a wear stage that swaps the base rebases it.
- **On an attachment.** A copy of the attachment's mesh 1 % larger about its bounds centre with a `Procedural.CreateMaterial`
  material; each frame only `Procedural.SetTime`, `ProceduralLook.Apply` when its version changed.
- **When.** Only while the gear is held: first person when wielded, third person for the same gear as the local wielded one
  while a renderer is visible, the lobby preview; not icons. Event effects (`trigger` other than `always`) draw for
  `seconds` after the event, then one more frame to restore the base. Canvases and painters are released when the gear leaves the hand,
  and everything when the skin is reverted or removed.

## Charms

An attachment's `charm` hangs its model and screen on a rope generated in code, from the attachment's origin (its -Y the
rest direction); the model hangs by its own origin. At most 4 charms on one gear; `SkinPack.Validate` rejects a value out of
range with `bad-vector`.

| Field | Default | Range | Meaning |
| --- | --- | --- | --- |
| `length` | 0.05 | 0.005–0.3 | Metres from the hanging point to the pendant |
| `segments` | 6 | 1–12 | Pieces the rope bends in, none shorter than 5 mm |
| `weight` | 1 | 0.1–3 | The charm's mass: heavier swings longer, lighter settles sooner |
| `damping` | 0.3 | 0–1 | 0 swings long, 1 settles at once (critically damped) |
| `stiffness` | 0.25 | 0–1 | 0 limp rope, 1 stiff wire that keeps the attachment's -Y |
| `style` | `Chain` | `Cord`, `Chain`, `Beads` | Smooth tube, alternating oval links, or beads |
| `thickness` | 0.002 | 0.0003–0.01 | Metres: cord diameter, link width or bead diameter |
| `color` | [0.8, 0.8, 0.82] | 3 values | Linear RGB of the rope |
| `collide` | true | | Keeps the rope out of boxes taken from the gear's part meshes |

`Core/CharmRope.cs` is the one algorithm (Verlet rope at a fixed 1/240 s step under real gravity, each step taking the
hanging point at that step's own time in the frame; the rope rides along with 85% of the player's body move (the plugin
passes the first-person camera's position, the editor the preview run's), so steady walking excites nothing and starting
or stopping only a little, while the gear's move against the body reaches the rope through the hanging point; at most 24
steps an update, the rest of a longer frame followed rigidly; node speed relative to the hanging point capped at 5 m/s;
drag on the nodes' move against the hanging point, a share of critical damping for the rope's own swing plus a part
growing with that speed, and a damped bend spring, all divided by `weight`;
length sweeps enough that the rope swings at its pendulum period, then a last pass down the rope that keeps every piece at
most its length; reset to a still hang after a 0.25 s gap or a 0.5 m jump of the hanging point or the body; the rope mesh), and the editor's
`viewport/charm.ts` mirrors it line by line;
`FormatTests.CharmMatchesEditor` runs both on the same input and compares nodes, pendant pose and mesh, so a change to one is
a change to both. Only the charms on the gun in the local player's first-person hands move; third-person, menu and other
players' guns hang still. Nothing is synced.

## Other packages on the same gun

- A gun whose model a package mounts with ForgeCore `GunMount` (EnergyLab's guns, Workshop gun models) is not skinned:
  `GunMount.Of(holder)` names it, and `GunMount.Mounting` puts a skinned gun back to its own look before the mount reads
  its poses and hides its parts. Part mounts (`GunPartMount`) are not excluded; a skin and a mounted part share the gun.
- Gear icons: ForgeSkin redraws only the icons of gear whose skin or colours changed (every gear when a global skin did); a
  package that supplies its own icons (`GearIcons.Supply`) draws them again itself.
- Ejected casings: ForgeSkin appends its own pools after the game's in `WeaponShellManager.SetupPools` and points a skinned
  gun's casing type at its pool while it ejects; ForgePerformance's casing limit counts and drops those casings like the
  game's, before any casing is taken from a pool. `Shells` holds a reference to each pool it builds and stores into the
  game's pool array with the collector's write barrier (`Shells.Put`): the game runs the incremental collector, and an
  Il2CppInterop array indexer store alone lets it free a pool the array still points at. A slot without a pool of ours
  points at one of the game's pools, never at a pool that was not set up.
- ForgeSkin installs no patch on a method another package patches; shared game events come from `GameHooks`.

## Build

```
dotnet build Mods/ForgeSkin/ForgeSkin.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
python Mods/ForgeSkin/tools/package_release.py --artifacts-path artifacts/dotnet --output <folder>
```

The pack reader (`Core/`) is compiled into `ForgeSkin.Plugin.dll`; the zip holds that one DLL, `lang/` and the metadata.

## Adding packs

- Put `.forgeskin` packs in `BepInEx/plugins/ForgeSkin/skins`, wherever the DLL is installed (a mod manager puts it in
  `plugins/NAinfini-ForgeSkin/`); the editor installs there, and `active.json` there picks the skin per target.
- Or ship a mod folder directly under `BepInEx/plugins` with a `forgeskin.forge` marker (`SkinPacks.Marker`) listing its packs,
  one path per line relative to that folder; blank lines and `#` comments are skipped.
- Or call `ForgeSkin.Plugin.SkinPacks.Register(path)` from your plugin's `Load`.

A pack the reader refuses is not loaded and logs `skin-pack-rejected` with the reason code of `SkinFormatException`
(`bad-audio`, `bad-image`, `bad-mesh`, `missing-file` and the others).

A MetallicSmoothness texture is in the game gear shader's layout (`Cell/Player/CustomGearShader`, as the game's own gear
textures): metallic in R, smoothness in G. A material's or attachment's glow (`EmissiveColor` x `EmissiveIntensity`, effect
flashes, unlit pictures) is in "as itself" units, as the editor previews it; it reaches the game's `_EmissiveColor` times
`MaterialParams.EmissionUnit` (0.01, the gear shaders' measured emission gain). With no `EmissiveColor` the intensity scales
the game material's own glow colour.

A texture set straight on a material (a material's, a wear stage's or an attachment material's, not a layer atlas) is a
PNG, a JPEG or a precompressed DDS (`Core/Dds.cs`), told apart by its header: a standard DDS with the DX10 header, BC1 when
opaque or BC3 with alpha and for normal maps (DXT5nm), sRGB in the DXGI format, Unity's full mip chain with rows from the
bottom, both sides multiples of 4 up to 8192. The editor writes every such texture as `textures/<name>.dds` only (a PNG
when a side is not a multiple of 4) and the manifest names that file; a layer atlas must stay a PNG or JPEG (`bad-image`
otherwise). A layer's frames are its `TargetRect` region at full size, or, where they would not fit a 4096 atlas
(`PackBuilder.AtlasLayout`), smaller by one scale for both sides (never larger, `bad-atlas`); the plugin copies a full-size
frame with `Graphics.CopyTexture` and stretches a smaller one onto a region-sized RenderTexture with `Graphics.Blit` first. The plugin uploads a DDS as it is after checking its header, sizes, mip count, block length and sRGB against
the channel (`bad-image` when any is off), and decodes a PNG or JPEG as before. The document schema stays 1, so packs made
with PNGs still load; ForgeSkin 1.0.3 cannot read a pack with DDS textures.

A reload reads and checks each pack's manifest only. A skin's files are read when a gear first wears it (`Skins.Ready`):
a worker thread reads the zip, the SFM1 meshes and checks the precompressed textures, the main thread builds the meshes and the
audio library and queues the textures on ForgeCore: a precompressed one on `Images.QueueRaw` (uploaded as it is), any other
on `Images.Queue` (decoded and compressed in the game). Until every skin a gear wears is loaded the gear keeps its look and waits; a picture,
mesh or sound that does not decode rejects the pack then (`skin-pack-rejected`), and a pack whose file changed since its
manifest was read is read again first. A skin no gear shows or waits for is freed after 30 s (`Skins.Release`); a replaced
or removed one as soon as no gear shows it.

## Logs

Errors go to `BepInEx/LogOutput.log` through ForgeCore `Log` under their registered codes. With `Development` on, the plugin
also writes `BepInEx/plugins/ForgeSkin/forgeskin.log` (`<UTC time> info|warn <message>` per line, cleared at launch), which the editor reads
as its game feed; with it off no file is written and info lines are dropped. Each skin applied lists every renderer slot
of the gear: `swap <path>[slot] mesh '<mesh>' <material>#<id> -> <look>` (with `(skin target '<name>')` when the slot took
the look through the material object it shares with a matched slot, or through its mesh name) or `kept ...: no skin target
'<name>'`. A material override covers every slot showing that material object, as in the editor.

## Editor control by an AI agent

Started with `--ai-control` (optionally `--ai-control=<port>`, 1024 to 65535, default 9333), the editor opens the Chromium
DevTools protocol on 127.0.0.1 only; without the flag no port is open, and an invalid port is ignored with one `ai-control`
line in `GTFO_UserData/logs/app.log`. It is the only way to open the port. The flag is read before the app is ready and has
no effect on the Squirrel install, update and uninstall runs. The editor has no single-instance lock, so close an editor that is already open first (two copies share one data folder). Installed copy: `ForgeSkin.exe --ai-control` from the install's `app-<version>` folder; source run: `pnpm start -- -- --ai-control` in
`Editor/`. In this mode every file dialog is an in-window path dialog (`Editor/src/renderer/ui/PathDialog.tsx`): the main process asks for\npaths through one function (`pickPaths` in `main.ts`, the system dialog without the flag), and the dialog checks that a\nfile or folder exists, or that a saved file's folder does, before it answers. [AI-GUIDE.md](AI-GUIDE.md) is the agent's operating guide (screen map, main flow, file handling, limits).

## Skins library interop

`Backend/SkinsFormat.cs` reads and writes the skin group format of the Skins library (food-Skins; format and source at
git.takina.io/gtfo/skins): `manifest.skingroup.json` plus `*.mattarget.json`.

- Backend method `skins.import { manifest, root, weapon?, lang }` writes one project per gear the group names into
  `root/<item id>/<skin id>.forgeskinproj` (the editor's autosave folders), texture macros as PNGs in `<skin id>.textures/`
  beside it (deleted with the project). Gears come from `filterGroups` (and the legacy flat `filters`, one more group):
  `OfflineGearPersistentID` → `SkinTarget.GearId`, `ItemPersistentID` → `SkinTarget.ItemId`, type as name or number.
  A material target goes to every gear that has its material and whose game texture names (`SkinsFormat.Materials`:
  shader and slot → texture name per material, read from the game files) fit its `match_groups` / `matchGroups` /
  `texture_names`; the first fitting target of a material wins, as in Skins. A group naming no gear returns
  `needsTarget` and writes nothing until a `weapon` is given. The result's `notes` (`SkinsNote` kind, skin, what, count)
  list everything left out or changed; the editor words them by `skinsNote.<kind>` (`ui/SkinsReport.tsx`).
- `pack.exportMod { …, format: "skins", gunPack }` writes the built packs as one group (`gunPack`, one pack per gear) or one
  group per pack. Item skins are skipped, a material's last override is written (the plugin uses the last too), material
  targets name the gear's texture names where another gear of the group has a material of that name, a BlendHS material
  with a picture gets `shader: Cell/Player/CustomGearShader` and macros for the slots the plugin fills, an emissive
  texture writes `_EnableEmissive`. `dropped` carries the same notes.
- `project.listAll { root }` lists every item's skins for the export dialog.

## Editor release

One release is the plugin's Thunderstore zip and the editor's Squirrel installer, both at the version of the ForgeSkin row
of `Mods/Release/release.json` (`pnpm ts Tools/Forge/set-package-version.ts ForgeSkin <x.y.z>` writes it everywhere,
`Editor/package.json` included). Build it from the repository root with a BepInEx IL2CPP profile for the game references:

```powershell
pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -BepInEx "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-Multiplayer\BepInEx" [-Output <dir>] [-SkipMake]
```

The script, all under `<Output>` (default `artifacts/forge-skin-release`):

1. builds `ForgeSkin.csproj` in Release (ForgeCore builds with it) into `dotnet/`;
2. writes the ForgeCore and ForgeSkin Thunderstore zips into `thunderstore/` with each package's `tools/package_release.py`
   and checks them with `Mods/Build/verify_release.py`;
3. publishes the backend (`Backend/ForgeSkin.Backend.csproj`, .NET 10, self-contained single file) into `publish/`;
4. stages the plugin the installer carries: `publish/plugin/ForgeSkin/` (`ForgeSkin.Plugin.dll` and `lang/`) and `publish/plugin/ForgeCore/`
   (the ForgeCore zip unpacked);
5. runs `pnpm install --frozen-lockfile` and `pnpm make` in `Editor/` with `FORGESKIN_PUBLISH_DIR` set to `publish/`, which
   `forge.config.ts` ships as the app's `resources/publish` (the prePackage hook refuses to package without it);
   Squirrel writes `ForgeSkin-<version> Setup.exe`, `ForgeSkin-<version>-full.nupkg` and `RELEASES` into
   `Editor/out/make/squirrel.windows/x64/`;
6. copies the files to upload into `upload/` and lists their SHA-256 in `upload/upload.sha256`.

`-SkipMake` stops after step 4. The installer is not code-signed. Nothing is uploaded; the upload steps and the fixed update
feed (`ForgeSkin-update` release of NAinfini/GTFO-Forge-Mods) are in `Mods/Release/README.md`.

In a checkout the backend installs the plugin from `artifacts/forge-skin-release/publish/plugin` (`Install.PluginSource`),
so run the script, at least with `-SkipMake`, after changing the plugin. Installing into a profile deletes the
`ForgeSkin.Format.dll` and `NVorbis.dll` that 1.0.3 and earlier left in `BepInEx/plugins/ForgeSkin/`. When the mod manager
installed the plugin (a `ForgeSkin.Plugin.dll` elsewhere under `BepInEx/plugins`), the editor keeps that copy and deletes
its own `ForgeSkin.Plugin.dll` and `lang/` from `plugins/ForgeSkin/`, so one copy loads; a managed copy older than the
shipped one makes the install fail with `forge-skin-old`. Without a managed copy it copies `ForgeSkin.Plugin.dll` and
`lang/` to `plugins/ForgeSkin/`. ForgeCore is left to the mod manager: a profile that has `ForgeCore.dll` anywhere under
`BepInEx/plugins` keeps it, and one older than the shipped copy makes the install fail with `forge-core-old`; a profile
without ForgeCore gets the shipped package in `BepInEx/plugins/NAinfini-ForgeCore/`. Uninstalling removes only the
editor's own plugin copy and its `lang/`; the skins folder and a managed copy stay.
