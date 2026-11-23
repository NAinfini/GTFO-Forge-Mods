# ForgeSkin for developers

A standalone mod: it depends on ForgeCore and BepInExPack_GTFO only. Game events that other packages also watch come from
ForgeCore `GameHooks` (gear parts spawned, gear spawn completed, the local player's hit requests on enemies (`EnemyHitSent`: made where
the shot or swing happens, host or client, in its own frame; a gun's shot is a hit when one is sent in its frame or the next, else
a Miss, which gives Accuracy and Miss the same on every machine, and the item that sent the latest one is credited with the
settlements below), reload start and end, dry fire, unwield,
flashlight, glue fire, throw, resource pack, player revived, item spawned, game state, the synced guns' fire, a player's health
arriving), ForgeCore `GameEvents` (local player down) and ForgeCore `Combat` for the local player's hits and kills on enemies, the
same on host and client. The game settles a client's hits on the host only, so what shows follows `Combat.OwnHitPredicted` (the hit
as predicted in its own frame: the Kill and Hit events, kill sounds and the kill and damage counters on screen, credited to the item
in hand) and `Combat.OwnHitCorrected` (the difference once the host settled it or 3 s passed without: the counters take it, and only
a kill or hit that never showed raises its event then), and what is saved follows `Combat.OwnHitSettled` (the host's settlement:
wear with kills counts only these). A prediction no host will settle (`Confirmable` false) shows its Hit and counts nothing. An
explosion carries no source agent in the game, so a mine's kills are nobody's, on host and client alike. Only methods no other package patches are ForgeSkin's own Harmony
patches in `Native/Hooks.cs` (fire animation, melee attack, mine and sentry placed, the placer's mine-spawned callback, item used, equip, aim, the spawn-complete
overrides of melee, mine, sentry and scanner, item layer, first-person rendering). Each patch class installs on its own and
skips a target whose function start ForgeCore refuses (`Hooks.Unsupported`); one that fails logs `skin-hook-unavailable` and
the rest keep working. The frame tick is ForgeCore `Frame.OnUpdate`; a pack's sounds are one ForgeAudio library
(`AudioKit.Load` from the pack's Ogg bytes, at most 10 s a clip; the sounds of one trigger, for Kill of one enemy (`enemyId`, the `EnemyDataBlock.persistentID` read once from the killed enemy; a kill whose enemy has no group of its own plays the group without `enemyId`), are one group that plays one of them by weight); kept textures and meshes go through `Assets.Keep`; the
settings, picker choices, player colours and readback files go through `Settings` and `Store.WriteText`; wear counts through `Store.Read` / `Store.Write` at their own path.

What ForgeSkin draws goes through ForgeCore's visual and UI parts: a pack's precompressed texture through `Images.QueueRaw`, a
pack's PNG or JPEG through `Images.Queue` (the options of `ImageTextures.Decode`)
(normal maps in the game's DXT5nm layout), a pack's SFM1 mesh through `MeshArrays.Build` (the reader of the format stays in
`Core/`), every emission write through `Glow` (`Write`, `SetColour`, `Clear`), gear icons through `GearIcons.Redraw` and `Unframed`, and
the picker through `UIKit` (`PickerSource` and a lobby button), code effects through `Procedural` (below). Texts are in
`lang/en.forge` and `lang/zh.forge` beside the DLL. What stays here is ForgeSkin's own: the pack format, the skin targets,
live screens, animated layers, trigger effects, casing pools and the part layout.

## What is skinned

Every gear instance by its owner: a gear built from parts waits (`SkinnedGear` pending list, looked at four times a second for as
long as it exists) until its item's `Owner` (`PlayerAgent`, then its `SNet_Player`) is known. The local player's gear (first and
third person) wears their own choices; a teammate's (third person, and the sentry they placed) wears their synced look (below);
a bot's the look the host picked for it; an icon or lobby preview, which never gets an owner, is skinned only on the icon and menu paths. The key is
(owner, gear id): two players with the same gun each show their own skin, colours and changes. Every look goes through
`SkinnedGear` (apply, revert, wait for the skin to load, keep every loaded skin while a level runs, release after 30 s unused outside one).

- **Placed sentry.** `SentryGunInstance` is spawned from the gear's `GearIDRange` and builds its own parts: it is skinned where
  its parts spawn like any gear, by its `Owner` (set in its `OnSpawn`). Picking it up destroys it.
- **Placed mine.** The deployer's (or consumable mine's) `OnStickyMineSpawned` callback, run for the placer only, hands over
  the spawned `MineDeployerInstance`; it wears the deployer's skin key, materials only (its model is its own prefab).
- **Dropped magazines.** A gun's 5 drop copies (`BulletWeapon.m_magDropPool`, made once in its `OnGearSpawnComplete` from the
  magazine under `m_aMag`) are the gun's own and reused oldest first; the gun's apply swaps their slots by the same material rule
  to world-projection copies of its looks, and its revert puts the game's materials back.
- **Pickups.** `GameHooks.ItemSpawned` in pickup mode (level pickups, container contents, dropped items): a pickup whose item
  has a skin (`item:<ItemId>`) is skinned on its object, materials only, and keeps it while picked up and dropped again. Every
  pickup of the level is listed so a pick or reload reaches it; one that loses its skin goes back to vanilla.
- A placed mine or a pickup shares only some materials with the item the skin was made for; Development logs every slot as
  `swap` or `kept`. A teammate's mine is reported to its placer only and stays vanilla; a pickup with no owner wears the local
  player's choice.

## Teammates' looks

`Native/Looks.cs` shares each player's look through ForgeCore `Net` (channel `ForgeSkin`, lobby value `look`: each player writes
only their own; ForgeCore sends it to every player with the same ForgeSkin, again to one who joins later or after a host change,
and forgets the look of one who left). ForgeSkin never calls NetworkAPI and touches no vanilla message. The bytes are
`Core/Look.cs` (the layout is in its header comment): version, flags, the character entry (skin number 0 when no character is
picked), then per gear of the loadout that has a skin of its own (at most 8): gear id, skin number (FNV-1a 32 of the skin id),
content hash (FNV-1a 64 of the `.forgeskin` file, read on a worker and kept with the loaded skin), the Thunderstore mod folder it
came in, the wear stage reached and the player's changes that differ from the author's (kind, a 16-bit effect index and a 16-bit
entry index, then the value: a player colour in 3 bytes, a number or transform in 2, an effect colour in 4). A whole value is at most
64 KiB (the largest ForgeCore Net lobby message); a larger or unreadable one is dropped and
logs `skin-look-malformed`. The local look is written half a second after the last pick, colour or adjustment, and within a
second of a gear of the local player's being built (a loadout change builds the new gear; the backpack is read only then) or a
wear stage moving; the same bytes are never sent again.

A teammate's gear wears their skin when the same pack is installed here (same number, same content hash, the skin made for that
gear) with their changes (`Look.Fit`); otherwise it shows its original look and logs one line per player and skin
(`skin-look-missing`, `skin-look-mismatch`, `skin-look-bad-value`), and the picker's details list it with the player's name, the
gear and the Thunderstore mod to install. A replay stand-in's gear the look has no skin for is one log line naming its key and the
look's keys; one whose gear id cannot be worked out here (its range matches no gear) is `skin-look-gear-unknown`. Nothing of the local player's own choices is put on a teammate's gear. On it, the ammo
screen, low-ammo, empty, ammo pickup and aim end are drawn as the menu preview draws them (no reads of this player's backpack or
hands); code effects draw while it is in its owner's hands; its sounds play at the gear (3D) for what this machine sees: the shot
(`GameHooks.Fire.BulletWeaponSynced` / `ShotgunSynced`), wield and unwield, going down and getting up (the player's health
arriving through `GameHooks.HealthSetReceived` falling to 0 or rising from it). The vanilla shot sound stays.

## Character models

A character skin is a skin document with `character` (target `character`); its format is defined only in `Core/Character.cs`
(the header has a full example) and checked by `SkinPack.Validate` (`bad-character-*`) and, when the model is read,
`CharacterRig.Matches` (the descriptor's hashes) and `CharacterRig.Check` (only what cannot work: no bone for `hip`, a mapped or
spring bone the skeleton lacks, a capsule on an unmapped joint, materials unlike the descriptor's). Of the 50 joints only `hip` is
required, the pose copy's root; a joint left out of `bones` keeps its rest pose on the bone above it, so a model without fingers,
without an arm or with unlike sides is a character too. `CharacterRig.Links` and `CharacterRig.Arms` (ForgeCore `HumanRig.Links` and
`HumanRig.ArmWear` on the map and the model's rest positions) are the one rule of what each joint does, for the plugin and the
editor's preview. A character pack is its own Thunderstore mod depending on ForgeSkin, found as gun packs are. `Native/Characters.cs`
wears it, all through ForgeCore: the model is built once by `ImportedModels` (format 2, `Skin.BuildCharacter`, its pictures decoded
and its meshes built over earlier frames with `ImportedModels.Prepare(model, mesh, pictures)`, so its first frame makes only the materials), hung on the body's animator with `PlayerLooks.Hang`, scaled to the body's height
times the author's `height` (`Characters.Fit`: `CharacterRig.Stature` against the body's bind-pose head and feet; spring radii,
capsule radii and gravity scale with it), drawn on the layer of the body's first active mesh (the lobby figure keeps inactive spares
on a layer its camera does not draw), posed by `Models.CopyPose` over `CharacterRig.Links` and `HumanRig.ArmReach` with `Follow`, its spring
chains bound with `Chains.Bind` at rest (capsules on the mapped joints), and the original person hidden with `Persons.Hide`
(`Look = true`). The local player wears their own pick, a bot the character the host picked for it (`bots`), a teammate the
character entry of their look (same pack and content, else the original person and `skin-look-missing` or `-mismatch`). Only when
a teammate's look names no character, or none came (no ForgeSkin, or a look that cannot be read), they wear the character this
player picked for their lobby slot: the SKINS button on another player's bar (shown with a character pack installed) opens the
picker on the character alone, its first card 按他自己选的 / THEIR OWN CHOICE clears the pick. The picks are kept per slot in
`BepInEx/ForgeSkin/others.json` (`{"<slot>": "<skin id>"}`), shown on this machine only and never sent; a replay stand-in keeps
the shared look. The order is `Look.CharacterSource` (their pick, this player's pick for the slot, the original). Who wears what is put right twice a second only after something changed
(`Characters.Dirty`). A model that cannot be built or posed leaves the original person and logs `skin-character-failed` once.
What a model has no bone for is never silent: `skin-character-part-skipped` once per skin names the joints left in their rest pose,
a model without both upper arms (the game's own first-person arms show) and a height that cannot be measured (no head, or a head
not above the feet: the model shows at its own size); ForgeCore logs an arm that does not reach its grip
(`visual-pose-copy-limb-does-not-reach`) and hips resting on the model's floor (`visual-pose-copy-root-does-not-rise`).
The local player also sees the model's own arms in first person: a second instance of the model worn on the game's first-person arms
with `Models.WearFirstPerson` (`CharacterRig.Arms`, `Hide = BodyParts.Arms`, `Descendants`, the `hide` materials as `LeaveOut`), so its
hands and fingers are the game's on the grip; when it cannot be built the game's own arms stay (ForgeCore logs why). The author's
`height` does not touch these arms: each bone sits on the game's first-person bone, scaled to the game's arm lengths
(`WearSolver`), which is what keeps the hands on the grip.

ForgeSkin is ForgeCore's player look provider (`PlayerLooks.Provide`): `Describe(player)` is that player's shared look (character and
gear skins); `Dress` resolves a look against the installed packs as a teammate's is, wears its character on the given body and
returns a dress whose `Hold(item)` puts the look's skin on a weapon the body holds (a look with gear skins alone still dresses).
Materials the author marked `shader: toon` are drawn through ForgeCore `ToonDress` with their `params` (`Toon.Apply`; unknown names go to the author
log), in the lighting this machine's `Toon` setting picks (`Forward`, `Deferred`, the default, or `Off`: drawn lit). The setting is a
local display preference, never in the look sent to others; a change re-dresses every worn model at once (`Characters.ToonChanged`).
A cut-out material (`cutout`) draws with ForgeCore `LitCutout`, or clipped in either toon entry. `CharacterRig.Matches` ends with
ForgeCore `ImportedModelDescriptor.CheckFiles`, the same check the game build runs, so the editor's export refuses what the game
would. Not yet: a separate first-person arms model (`arms.model`: one warning, the model's own arms are worn), player colours on
character materials and a slot icon of the character worn.

## Picker and player colours

The picker opens in the lobby's loadout pop-up, from the SKINS lobby button (there is no key), as one screen without tabs
(ForgeCore's grid layout, side panel and title bar gear). Left, one slot per gear or item that has a loaded skin (a tick on
those wearing one); middle, a grid of its looks, the original look first, a press wears it (kept in
`BepInEx/config/ForgeSkin.choices.json`); right, the details of the card under the cursor over the panel of the skin worn.
The panel's colour schemes set the player colours (`playerColors`) of that skin: 原色 (every `default`), each of the
document's `colorSchemes` (`{id, name, colors: {playerColorId: sRGB}}`, a colour left out keeps its default), and 我的, chosen
when the colours worn match no scheme; pressing 我的 opens, under the scheme cards, a swatch row per colour named as the author
named it (its default and eleven hues at the default's saturation and brightness). A scheme writes each colour (the default as none), so the look and the colours file
are as before. A character has no colour schemes. Colours are kept per skin ID in
`BepInEx/config/ForgeSkin.colours.json`, written 0.5 s after the last change; a pick paints a copy of the skin document (`Skin.Painted`) and live gear takes it on
the next tick, as a skin pick does. The picker does not open inside a level. Its details add, in the warning colour, that the game's texture quality is below
full (`QualitySettings.masterTextureLimit`: every mipmapped skin texture drawn at half or a quarter), and, in the alert
colour, each pack the last reload rejected with its reason (`Skins.Failed`: version, bone map, unsupported material, file in use, else
damaged), and, in the warning colour, that packs left in the old `plugins/ForgeSkin/skins` folder are not loaded. The title
bar's gear opens ForgeSkin's settings in the panel: the two switches and the toon choice (前向 / 延迟 / 关闭).

A gear with several installed skins (several packs for one target, or an editor gun pack) lists them all, each card with the
skin's name and its author when the pack has one; the character slot shows the avatar icon, and a gear slot tile shows the
gear's game icon (`GearIconRendering.TryGetGearIconSprite`), which ForgeSkin redraws in the skin it wears. Which skin a target wears, first match
wins: the picker's choice (ignored while that skin is not installed for the target; "" is the original look), `active.json`
`targets`, then the first pack by path among skin mods and registered packs. The editor's 应用 alone goes before the
picker's choice: the backend (`pack.install` with `pick`) leaves a one-shot `<ticks>.pick` (`{"target", "skin"}`) in the skins
folder, and the plugin's next reload writes it into its own choices and deletes it; a pack put in by hand or another change to
`active.json` does not, and a skin it names that a choice keeps off is reported in `forgeskin.log`. A gear wears its own skin and
nothing else: its sight, magazine, light, lens and ammo materials are part of that skin and change on that gear instance alone
(`SkinnedGear.ApplyMaterials` copies the material per renderer of the item; casings and dropped magazines use the gun's own pools).
There is no skin for every gear: a pack whose target names no gear or item (older editors made those) is not worn and logs
`skin-global-unsupported` once when it is read, and so does an `active.json` that still has a `global` list (once per change of
that list); the editor drops that list at its next write of `active.json`. Choices, colours and changes
of the gear's own skin reach teammates through the look (above); a skin may have any number of player colours, and the first 65536 travel.

`Native/Previews.cs` shows each skin card the picture its pack carries, drawn by the editor when it packed the skin (a gear
skin's `picture`, a character's `character.picture`): read from the pack and decoded off the main thread (`Images.Queue`), only
uploaded on the main thread; the skin is not loaded for it and nothing is rendered or photographed in the game. The picture is
what the author packed: colours a player picks show on the gear and the character in the lobby, which the details say for a
skin with player colours. A pack without a picture (packed by an older editor) gets a placeholder (the avatar icon for a
character, none for a gear) and the details ask for it to be exported again with the new editor. The original look shows the
avatar icon for the character and the gear's game icon while the gear wears no skin of the picker's. Pictures are kept per pack
while it is installed, at most 24. On every gear the icon renderer builds, a
skin's attachments are left out of the icon's framing (`GearIcons.Unframed`), so a skinned gear's icon is framed as without
its skin.

The panel's 调整 section lists, for the skin the slot wears, every code-effect value its author marked for players (`playerTweaks`): a
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

## Holograms

`Native/Holograms.cs`, `AmmoScreen.cs`, `SkinnedGear.Surface`. The game's `Cell/Player/Display_Hologram` has `_EmissiveMap` and
`_EmissiveColor` but no `_MainTex` or `_Color`: an attachment's picture goes to `_EmissiveMap` when the shader has it, else
`_MainTex`, and `hologram.opacity` multiplies the glow gain. The editor bakes `hologram.color2` into the attachment's picture
(`.frames/<id>-hologram.png`) and turns `scanlines` and `edgeGlow` into code effects on the attachment (ids `<id>~scan`,
`<id>~edge`) when it builds the pack; they count toward the 8 code effects. `SkinPack.Validate` checks `flicker`, `opacity`,
`scanlines` and `edgeGlow` in 0..1 and `color2` as an RGB vector (`bad-vector`).

## Sight reticles

A holographic sight's lens (`Unlit/HolographicSight_3Layers`, also the thermal `Unlit/HolographicSight_Thermal`) draws up to three
reticle layers additively, picture x colour: `_Reticule<L>`, `_ReticuleColor<L>` (`[HDR]`), `_ReticuleScale<L>`,
`_ReticuleOffset<L>` (projection depth) and `_Fade<L>` (0 not drawn), L = A, B, C. A skin sets them with the texture channels
`reticleA`..`reticleC` of the lens material's override (`TextureChannels.Property`) and `params.reticles` keyed by the same
channels: `color` (sRGB) and `intensity` (`ColourSpace.Glow`, the property's own units, alpha kept; `intensity` alone scales
the game's colour), `scale`, `offset`, `visible` (`_Fade<L>` 1 or 0); left out keeps the game's. `pack.build` stores a reticle
picture with its colour multiplied by its alpha, so a picture on a transparent background shows only its lines whether the
shader reads alpha or not, and the plugin clamps it (a layer scaled below 1 shows it once). `SkinPack.Validate` rejects
reticle values under another channel, non-finite numbers and reticles on an attachment's material (`bad-reticle`). In game
`SkinnedGear.Reticles` writes only what the material's shader has and logs the rest once (`skin-reticle-skipped`); a wear stage
keeps the reticle. Teammates get it through the pack and its hash like every other material value (nothing on the wire), but
see it only through the sight, which in practice is the player aiming it. `assets.apply` writes the same values into the game
files (a layer the material has no picture slot for is reported), and the Skins export writes them as properties (an
intensity without a colour is reported, `reticleIntensity`). Folder matching never assigns a reticle channel.

A game lens (any `HolographicSight` shader, the floating holograms Sight_20, 21 and 24 included) takes only its reticles: the
plugin (`SkinnedGear.Look`) and `assets.apply` put an override on it through `MaterialOverride.OnLens`, which keeps the reticle
pictures and `params.reticles` on a copy of the game's lens material (its shader, blending and dirt picture stay) and leaves out
every picture of another channel, glow, tint, metal, paint layer and wear stage, named once (`skin-sight-skipped` in game, a
patch warning in `assets.apply`): on the lens shader the albedo slot is the lens dirt, so a part's whole import or bake there
covered the view with an opaque picture. A lens is never reached by the mesh-name fallback, only by its own material name. The
editor's "every channel of every part" check skips lens materials.

## Sight on an imported model

`sight` in the document (`Core/SkinDocument.cs`, plan §4.15) puts a game lens on the skin's own model. `lens` is exactly one
of `attachment` (that attachment's model takes the lens material), `part` with an optional `path` (that node's own meshes
take it) or, with `size` (width and height in metres) and `position`/`rotation`, a pane anchored at `part`/`path`. `style`
is the `GearSightPartDataBlock` id whose flat lens material is copied (ForgeCore `SightLenses.Of`), `zoom` the id whose
`SightProperties.GearFovZoom`/`WorldFovZoom` the gun wears while held in first person (ForgeCore `HeldSettings.Wear`, key
`ForgeSkin.Sight`). The lens's reticles are the override named `sight:lens` (`Sight.LensMaterial`), reticle content only; it
works as on a game lens (above). `SkinPack.Validate` rejects a lens without a style, a style without a lens, a lens on both or
neither of an attachment and a part, an attachment without a model, a bad pane size, and a `sight:lens` override without a lens
or with anything but reticles (`bad-sight`). In game `Native/SkinnedGear.Sight.cs` waits for the lens template, logs an id the
game lacks or a zoom of 0 once (`skin-sight-skipped`) and takes the lens and the zoom off with the skin. Nothing goes on the
wire: teammates get the lens through the pack, the zoom is the holder's own. The editor's backend method `sights.list`
(`Backend/Sights.cs`) gives each sight part's id, name, both fields of view, offline gear id and lens material name. A click
with 从模型里选 on the whole-gun model (below) puts a pane there instead: that model is one mesh.

## Whole-gun model

A model in place of the whole gun (plan §4.2) adds no field. It is an attachment on the receiver's root (else the first part's).
The model is made the glTF way, its front (muzzle end) along glTF +Z, which is -Z once imported into Unity space, so the
attachment is turned 180° about Y (`gun.seat` returns that `rotation`) and its muzzle points down the gear's +Z; every part root of the gun is `visible: false`; the right hand, left hand, muzzle and aim point nodes
are part layout entries (position only). Hiding a part hides the game's renderers there, never the skin's own attachments
(plugin `PartPose`, editor `hiddenMeshes`): an attachment hides with its own `visible`. So the plugin, teammates' copies and
replay stand-ins wear it as any skin.

Where the model and the hold points go is one rule, the backend's `gun.seat` (`Backend/GunSwap.cs`), which calls ForgeCore
`GunHold.Solve` with the numbers of ForgeCore `Data/gun-hold.json` (the backend links both files). The
model's points are its nodes named `RightHand`, `LeftHand`, `muzzle` and `SightLook` (ForgeCore `GunNodes`, any case;
`mesh.import` returns them as `sockets`, Unity space, the model's own size). A hand without its node grips at that hand's
palm touch point on the gun (the hand node plus `palm.right` or `palm.left`) at the same share of each axis of the model's
bounds as of the gun's, so it stays on the model whatever the model's length. The model shows at `shownScale` the first
time and at its attachment's scale on 重新自动摆. The answer, in the gear's frame: the model's origin, both wrists, and the
model's muzzle and aim point where it has them (else those nodes keep the game's). A shown length outside `length.min`–`max`
is one hint in the editor. ForgeSkin does not go through `GunMount`, so nothing pulls a far left hand into the arm's reach.
The editor keeps the model's sockets and bounds in its own state (`editor.wholeGun`, never packed); `gunSwap.ts` reads the 3D
view's gear frame for the rule and writes its answer as one undo step.

## Part models and the flashlight's light

A part layout entry's `mesh` replaces that node's own model; the plugin's `PartPose` swaps the mesh of a plain renderer only
and keeps the node's whole material set, and the editor preview draws the replacement with that same set
(`viewport/PartMeshes.tsx`). The backend's preview marks each node with a model with its mesh name `model` and `skinned`
for a `SkinnedMeshRenderer`. `ui/PartModel.tsx` holds the editor side: `modelState` (replaceable, `skinned` or `empty`, the
last two shown disabled with their reason), `modelPieces` (the pieces with a model under a node, listed when the node has none
of its own, as part roots usually do), `replacePartModel` and `replaceWholePart` (root `visible: false` plus an attachment on
the root placed the whole-gun way, through `importMesh`'s `place` callback, one undo step). The 挂件 page's Model section and
the part row's + menu (`ui/AddMenu.tsx`) call the same functions; `pieces.ts` names a piece by its node name, then its mesh
name.

The light comes out of the node `GearPartFlashlight.m_lightAlign` names. `Backend/Unity.cs` reads that field when it loads the
prefab and marks the node `Point = "light"`; when the field names no transform of the prefab it logs once and takes the node
named `Spot`. The preview lists it with the hold points (`points`, `GearPoint` in `protocol.ts`), so the editor drags it with
the same marks and saves it as that node's part layout entry; the plugin needs nothing new. `mesh.import` returns a model's
`light` node in `sockets`; replacing a flashlight piece or the whole flashlight places the light there in the same undo step
(`gunSwap.ts` `placePoint`), else selects it and says the light stayed. `LightStays` in the Place tab says so for a moved
lamp body.

## Charms

An attachment's `charm` hangs its model and screen on a rope generated in code, from the attachment's origin (its -Y the
rest direction); the model hangs by its own origin. A gear may have any number of charms; `SkinPack.Validate` rejects a value out of
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
| `color` | [0.8, 0.8, 0.82] | 3 values | sRGB of the rope |
| `collide` | true | | Keeps the rope out of boxes taken from the gear's part meshes |

The rope moves by ForgeCore's one chain physics (`ChainState`, `ChainSpec.Charm`: Verlet at a fixed 1/240 s step, riding
along with 85% of the player's body move; see ForgeCore's `DEVELOPERS.md`); `Core/CharmRope.cs` builds the charm's chain
(hanging point, `segments` pieces, the pendant four times as heavy on a rigid arm), passes the author's values and draws the
rope mesh and the pendant's pose. The plugin passes the first-person camera's position as the body. The editor computes no
rope: the 3D view sends the hanging point's path for a preview motion (or one frame at rest) to the backend's
`charm.simulate`, which runs this same `CharmRope` on ForgeCore's linked `ChainPhysics.cs` and returns each frame's mesh and
pendant pose for the view to play back. ForgeCore's `Mods/ForgeCore/tests/Core/ChainChecks.cs` locks the trajectory. Only the charms on
the gun in the local player's first-person hands move; third-person, menu and other players' guns hang still. Nothing is
synced.

## Character tab (editor)

The editor's 人物模型 tab (`Editor/src/renderer/character/`) reads a skinned `.glb` with three.js, matches its bones to the
canonical humanoid with the site's own matcher (`Web/src/forge/animation-bone-match.ts`: `matchHumanBones` for the body,
`matchHumanFingers` for the 30 finger joints; a VRM file's own humanoid comes first and the matcher fills only the joints it
does not name, on import and on 重新自动匹配 alike, `autoMatch` in `model.ts`; a joint neither recognises stays empty, a bone serves one joint, and the author
may give any joint a bone or leave it out) and asks the backend how the game will take the match:
`character.check { joints }` (canonical joint to rest position, game axes) returns the joints of `CharacterRig.Required` it lacks
(the hip: the only thing that blocks), the joints the game leaves in their rest pose, whether the model wears its own
first-person arms, which hands reach for the grip, and, for the author only, `RigMap.Check`'s findings as kinds the editor
words itself and whether the body faces backwards.
Spring bones are previewed like charms: `springs.simulate { chain, nodes, strands?, frames }` runs ForgeCore's linked
`ChainPhysics.cs` with the document's own `SpringChain` (its `Spec()` is the preset with the author's values) on the anchor bone's
path for a preview motion (`viewport/motion.ts`, shared with the charm preview) and returns node positions per frame;
`springs.presets` gives each kind's starting values. Exporting a character pack is not wired yet.

The motion row under the preview plays moves the way the game does. A clip plays on a hidden canonical body, the skeleton the
action file carries, and the model copies that body's pose every frame (`character/clips.ts`).
`character.links { descriptor, bones, fingers }` returns, per canonical joint, the link the plugin copies with (`CharacterRig.Links`
on the model's own rest positions: `toward`, `from`) and whether `PoseSolver.Skipped` leaves it out; it also returns the reaches of
`HumanRig.ArmReach` the game keeps as `[upper, lower, end]`. `character.check { joints }` says how the game will take a match:
`missing` (the required joints without a bone, the only thing that stops export), `unposed`, `reach`, `arms` and the rest-pose
`notes` the panel words for the author. The copy is
the site's port of `PoseCopy`, `Web/src/shared/render/imported-skin-pose.ts` (`createSkinPose`; its optional `reach` bends those
limbs as `LimbReach.Bend` does). While a move plays, the springs run through the same frames (`simulate(motion, clip)` in
`character/model.ts`). With no move or preview motion they run 8 s held still from the bound pose, so hair and skirts drop under
gravity and come to rest as in the game; the last frame then holds.

The clips are listed in `character/motions.json`: game player actions by name in the site's `player-actions.json`, plus Mixamo
clips from `Mods/ForgeEmote/Assets/Clips`. `pnpm ts Tools/Models/stage-skin-motions.ts` copies them into the
editor's `motions` folder beside `character` (not in Git), and the editor bundles that folder. `build-editor-release.ps1` runs the copy before
packaging. A clip that has not been copied is hidden from the row.

## Weapon inspect motions

The inspect key plays a motion from a table: `Core/inspect-motions.json` is embedded in the plugin, and
`BepInEx/config/ForgeSkin.inspect.json`, when present, is read over it with the same shape. A motion says what to show, not
where in metres; the plugin solves the rest for the gun in hand when the look starts (`Core/InspectMotion.cs`, `InspectLook`).
A press the hands are not ready for (`Inspect.NotReady`: the game's item holder busy or in an animation sequence, aim held, the gun
reloading, a game screen open, or a player motion other than stand, crouch or stand-still) is kept for half a second and retried each
tick, so one press starts the look as soon as the hands are free; a press that never found them free logs one line, `inspect: the key
was pressed and no look started: <reason>`, with the first reason and then every state it reads (player motion, holder busy, animation sequence, aimed, aim held, reloading, screen). Crouching is not a reason of its own.

A melee weapon is inspected by the same mechanism (`MeleeWeaponFirstPerson`; a press while it charges, swings or pushes, its state
other than idle, waits like any other). Its model's length is its own Y where a gun's is its Z, so the plugin turns the measured rig
(`InspectRig.LengthAsY`, `InspectRig.Turned`) and the look is solved and posed with the length as Z; the turn is taken off again as
the item is posed. Its motions are written from the
hold the game has it in (`"from": "hold"`, below) and come from the table by gear id, else from the `fallback` entries with
`"melee": true` by its length; a table with no melee entry leaves the key without a look and the log says so.

Anything else a player holds plays the table's `default` list (`InspectTable.Default`, `DefaultKind`, `DefaultFeel`): a tool (the
bio tracker, the C-foam launcher, the mine deployer, a sentry gun), a pack, a consumable, another mod's item. It is decided by what
the item is (neither a `BulletWeapon` nor a melee weapon, `Inspect.Plain`) and never by a list of items, so a new item needs no
entry. A gun or melee weapon that can play none of its own motions nor one of its length's plays the default too. Each is logged
once. The default's motions are from the hold (checked when the table is read), since such an item is held any way; the shipped one
is `item-look`: the item turned to one side and the other from where the game holds it, both hands staying on it as the game has
them. Only some item kinds' unwield is hooked, so a look whose item is no longer the one in hand is cut on the next tick
(`Inspect.Tick`, against ForgeCore's `GunMount.HeldItem`: what the holder's last step had in the hands, which ForgeCore reads every
frame for its gun models, so the look does not read the holder again).

- `gun`: keys `{ "t": seconds, "turn", "tilt", "roll", "at": [x, y], "fill", "spin", "flip", "rise", "palm" }`. A key with only `t` is the
  game's own pose; the first key (at 0) and the last are. `turn` swings the muzzle to the view's right, `tilt` raises it, `roll`
  turns the gun about its bore with the top going right, all in degrees from the gun held level and pointing ahead (at most 180;
  not from the game's pose this frame, which is lowered when the game relaxes the gun at idle). A motion with `"from": "hold"`
  beside its `gun` takes them from the hold the game has the item in when the look starts instead, about the view's up, right and
  forward: for an item whose hold is not a gun's ready pose (every melee weapon, every item of the default look). `palm: true` on a
  key of such a motion takes the key's angles from that hold with the grip hand's palm turned up instead, the shortest way and at
  most 75 degrees (`InspectLook.PalmUp`): where an item is tossed from and caught, whichever way the game holds it, so a knife held
  point down in a reverse grip leaves from the palm's side and not through the back of the hand; the keys after the catch that do
  not write it turn the item back into the game's hold. A motion from the hold turns a free item (`spin`) about the axis across its
  length nearest the line of the grip hand's fingers, so it goes end over end in the plane across the fingers and the forearm and
  does not sweep through them; a gun turns about its side axis. `at` is where the centre
  of the gun's bounds should sit in the view (-1..1 of the half width and height): a key that writes it is held there within about
  0.1, giving way only to the frame rules and the grip hand's range; a key without it leans a little toward the middle. `fill` is the
  share of the half view the shown part stays inside (default 0.95; over 1, up to 2, lets it reach past the view): it sets how far the
  item camera widens for that key, not how far the gun is held (the grip hand's range sets that).
- `rise` alone (metres up) lifts the gun in both hands from where the key holds it (the hand is placed for the gun as held, then lifted). The free-gun track: `spin` (degrees about the gun's side axis) and `flip`
  (degrees about the bore) free the gun from the grip hand. `spin` alone is a twirl on the trigger finger: the gun turns about its
  trigger guard and the hand goes with it (a small circle, the wrist rocking once a turn); it takes no `flip` and no `rise`.
  A twirl spins up over 0.08 s, turns at a steady rate that fills the way, slows evenly to a stop over its last half turn, goes 6
  degrees past and back over 0.1 s (starting and ending at no speed), and the wrist takes the knock (4 degrees, 6 mm).
  `toss` (metres, up to 0.5) on a key makes the way into it a toss, from a key of the motion's own by whole turns of `spin` and/or
  `flip`, with no `rise` on either key; `g` (8.5..10.5 m/s², default 9.8, toss keys only) is its gravity. The flight is real: it is
  T = 2√(2·toss/g) long and the gun leaves the hand T before the key at √(2·g·toss), its centre on one parabola along the world's
  up (measured in the view as the look starts, `InspectRig.Up`) from where the held pose leaves it at the release to where the held
  pose takes it at the catch (both fixed for the flight), while it turns at an even rate about its bounds centre. The hand winds up
  over the 0.3 s before it lets go (sinks 4 cm, cocks the wrist 12 degrees back), throws over the last 0.14 s (the wrist whipping 10
  forward), follows through for 0.15 s, lifts to meet the gun 0.09 s before it lands, takes it at its speed and sinks with it on a
  second-order spring (3 Hz, ζ 0.5) for 0.5 s, the wrist pressing the muzzle down from nothing and at no rate as the hand sinks below
  where it took the gun (x²/(x + 1 cm) of the sink, 150 degrees a metre). The gun turns in the fingers for 0.07 s before it leaves and after it lands, its rate
  rising from and falling to nothing; the grip hand's fingers open as it leaves and close on it only once it has landed and stopped
  turning in the hand. For a `flip` the open hand waits 5 cm off the side it holds the gun from (`TossAside`, from 0.15 s after
  the gun left) and closes on the gun over the 0.07 s it stops turning: at the grip, the gun's underside would go through the wrist
  on the last of the turn. The wrist's cock and whip take only what keeps the wrist within its limits (`WristKept`), and the hand that
  throws and catches is turned back to them (`InspectArm.Comfort`). The key must come
  at least T + 0.3 s after the key before. The item camera keeps its view through the flight: its keys' `fill` decides it, so a
  toss whose keys have `fill` 2 is seen at the view the game's hold has, the item as large as there and partly out of the view as
  there (the camera widens only for what would reach past twice the half view). In a motion with a toss the keys
  place the grip hand within 0.12 m of where the game holds the item (`InspectLook.TossMove`; 0.3 m, `Move`, in every other
  motion): the item is thrown and caught where it is held, not pushed out ahead first. Either way the hand takes the gun
  back without a jump at a whole turn. They are interpolated as numbers (up to 1440 degrees), so one key can spin twice; a game's-pose key takes the whole turn
  nearest its neighbour, so a gun spun round is not wound back, and an interrupted look fades the rest of the spin.
- `hit: true` on a key of the motion's own: the gun is knocked as it arrives there (a slap home, a bump), with the kind's `hit`
  impulse (see `feel`). `cue` on a gun, hand or part key names a sound beat (see `sounds`).
- `windup` and `settle` (shares of the way, 0..0.3) on a key of the motion's own the gun does not go through: the way into it first
  turns back by `windup` over its first fifth while the hand stays where it starts (anticipation; drawing the hand back too could
  take the gun out of the view), then goes past the key by `settle`, turn and hand, and comes back over its last 30% (overshoot and
  settle). A key without them keeps the plain smooth step.
- `through: true` on a key of the motion's own: the gun does not stop there. The span into it only eases in and the span out of
  it only eases out, so two beats read as one motion (a spin that carries the hand on).
- `hold: "stock"` on a key of the motion's own: the grip hand holds the gun under its stock (`InspectGrip.Stock`: 0.55 of the way from
  the grip to the back of the bounds, at the grip's height; a gun with no stock behind the grip keeps the grip). Between two keys
  that hold it at different places the hand goes straight while its place slides along the gun. A key next to the game's pose holds
  the grip.
- `hand`: the off hand's keys `{ "t", "on", "along", "node", "face", "at", "offset", "spin", "tips", "clasp", "fingers", "scale", "cue", "regrip" }`, first and last `grip`. `on` is `grip` (the game's own grip), `away`
  (lowered out of the view, the hand hanging straight on its forearm), `under` (turned as the game holds it, its palm's middle 1.5 cm
  under the meshes over the hand there, whatever gap the game's own hold leaves; without `along` at the hold's own place along the
  gun), `side` (the gun's left side),
  `top`, `muzzle` (under the front end, as `under`), `mag` (on the motion's own `@mag` part, which it needs) or `node` (on the motion's
  part `node`), or `open` (as `under`, but under the gun as it was held and not as it flies: during a toss the hand waits where it
  held the gun and takes it back at the whole turn); `along` is 0 at the back of the bounds and 1 at the front. A hand on `mag` or
  `node` lies on a face of that part's mesh box as this gun has it (measured when the look starts): `face` (`left`, `right`, `top`,
  `bottom`; default `left`) and `at` [u, v], u from the back of the face to its front, v across it (default
  [0.5, 0.5]; a raked handgun magazine takes u 0.125, its back), turned about the bore until its palm faces it, the palm's middle
  (6 cm from the wrist toward the fingers) 1.5 cm off that point, then shifted by `offset` [out, across, along] metres (each at most
  0.1; out is along the face's outward normal, so a negative out presses in). `spin` (degrees, -180..180) turns the hand on the
  face about the way out of it, to lay it across the part instead of along it (a palm arched over the back of a slide, the fingers
  down one side and the heel down the other). `tips: true` (on a `node` key only) lays the pads of the index and middle fingers
  on the point instead of the palm (`InspectLook.Pads`: the middle of their last bones, bent 0.1, a finger's 8 mm off the face), so
  the palm stays a hand's length back and touches nothing: a finger stroking a cylinder. `clasp: true` (on a `node` key without
  `tips`) takes the part between the thumb and the fingers with the same search as a magazine's clasp (`InspectLook.ClaspTarget`):
  on `top` or `bottom` the palm faces the face and the fingers point across the gun; on `left` or `right` the palm faces the gun's
  side or, the second way, the back of the part. A small part takes a pinch (the thumb and one finger or more) or a hook (two
  fingers over it, the thumb open beside them); each place is put on the gun at rest and its fingers closed until they meet a mesh,
  and one whose palm lies in the gun's other meshes, or whose thumb or a finger stays more than 0.4 cm in them, is no place. The
  places are ranked (a thumb before a hook, then the most fingers on the part) and found once per gun and motion. A hand `under`,
  `side` or `top` wraps the gun's body the same way (`InspectLook.SupportBox`): the meshes no part moves at its place along the
  gun whose face the palm lies on (within 3 cm of the section's own), 4.5 cm either way along it, the palm on that face and the
  fingers round the far side, turned about the palm toward the game's own fingers by one of a few shares (half first); its places
  are ranked by how far the wrist goes past its limits with the gun held as the game holds it. Each such hold stands alone: one
  with no place, or whose clasp breaks the solved look, keeps its plain hold, said once a gun and motion (`SupportNote`), and the
  look is solved again without it; the part's clasp and the other holds are not touched. No clasp may make the off hand's
  fingers twitch (rule J) more than the plain look does (`InspectLook.Plain`: no clasp, every hand on the gun as its keys say,
  solved alongside): a support hold whose clasp twitches more over its keys keeps its plain hold, and a look whose clasps twitch
  more over the whole look plays the plain look, unless the plain look lays a finger more than 0.4 cm deeper in the gun; each
  said in `SupportNote`. Where the best ranked place leaves the off hand off what it holds, the ranked place that leaves it
  closest plays. Holding a finger back (`FingerBack`, below) must not leave it deeper in the gun or the hand further from what it
  holds than a finger stopped sample by sample does (`InspectLook.Snug`, measured at the audit's times): where it would, the
  look's twin whose fingers close back faster plays, the slowest of 2.5 to 40 of its bend a second (or at once) that lies as
  shallow and close as that (no shallower than the grip hand's index finger lies anyway), said in `SupportNote`. Where no place
  holds and keeps the look's rules the look cannot play on that gun (`InspectLook.Unplayable`): the plugin says why once and solves
  another motion of the gun's pool in its place. The plugin solves every motion an item can play off the main thread once the item is in the hands
  at rest with no look on (`Inspect.Prepare`), kept by item and motion for the last four items held; a press whose camera, gun
  bounds and parts, arms and hold (the gun and the off hand in the grip hand's frame, not where they are in the view: the game
  sways arms and gun together at rest, and a look plays on the gun's live pose) are within 5 mm and 1 degree of that solve plays
  it at once (`Inspect.Unlike`); a press before that solve is done (or after one of those changed, said once with what changed)
  plays the plain look, solved on the press, says so once, and the clasped looks are solved again at rest. At rest the item is
  measured again every 0.5 s and its looks solved again where that changed (a skin's attachment shown a moment after a look ends),
  so the next press finds them. The gun's bounds take a skinned mesh's own box about its root bone (a world box, square to the
  world, grows and shrinks in the gun's space as the gun turns), a mesh once left out of them (a rope) stays out, a skin's charm
  (its rope and pendant, which swing) is left out, and a mesh once counted stays counted while it is switched on and off. A motion with a `clasp`
  key plays only its look solved ahead: until that is done it is left out of the pick (said once), a press whose solve no longer
  holds plays another motion, and one solved unplayable leaves the gun's pool when the solve at rest finishes. The log gives
  the time from rest to every look solved and, per press, from the key to the look. A magazine in the hand (`mag` with no `at`) is held
  on the magazine's own box (`InspectLook.MagBox`: a magazine that leans in the gun is held along its own down axis, not the
  gun's), and its `offset` is to the magazine's left, up it and to its front. With no `face` it is held round its body as a bottle
  is: the palm toward its left face (its normal level and square to the magazine's length), the fingers toward its front and the
  thumb's side up it, the hand's middle (half way from the index knuckle to the little finger's, `MagHandMiddle`) at its middle;
  the pad of the closed thumb (`MagThumbPad`, the thumb bent `MagThumbBend` 1) a finger's half thickness off the left face and
  0.5 cm behind the front edge (`MagThumbBack`), so the knuckles lie past that edge and the fingers, closing until they meet it,
  curl round the front onto the right face. The nominal hand's thumb lies about 5 cm in from the palm's plane, so the palm stands
  that far off the face: the thumb and the curled fingers hold it, and R1 and the fingers' touch count the thumb's pad. `spin`
  turns the hand about the way out of the left face, about the thumb's pad. That pinch is the fallback of the clasp: a
  magazine by its side is first clasped (`InspectLook.Clasp`, searched once per magazine size): its back narrow face on the palm
  (the palm's skin at most 1.5 cm off it), the thumb's pad on its right face and the pads of three fingers or more on its left,
  each pad's skin at most 0.6 cm off its face, nothing sunk into the magazine, the fingers shaped by joint and the thumb's root
  spread; the magazine keeps its pose and the hand turns round it (`ClaspAnchor`). The back face takes the palm because the
  magazine then keeps the turn its keys give it and the camera sees the back of the hand and the fingers wrapped over the front;
  the front face turned the magazine and asked a handgun's wrist past its limit. A `regrip` into or out of the clasp goes as a
  hand changes its hold (`InspectMotion.RegripOpen`, `RegripClose`): over the first third of the span the fingers open while the
  hand stays on the magazine (`InspectMotion.Opened`: every joint at rest, the thumb straight and spread as far as it goes, since
  beside the fingers it would lie across the magazine), in the middle third the hand goes round to the new hold, and over the last
  third the fingers close on it. On a clasped span the hand lifts toward its back by the least that keeps every finger's middle
  line within 0.2 cm of the meshes (the open hand on the way; the fingers stopped on the meshes, pressing in at most
  `ClaspSquash`, in the holds), and the look samples the fingers at 120 a second (`InspectLook.FingerRate`; 30 otherwise). Each
  finger closes as far as it stays out of the meshes, searched down from its whole shape, and the baked fingers take each joint's
  median over 0.05 s (`InspectLook.Steady`) except where that would press a finger deeper than the raw sample: a finger stopped
  on an edge as the hand sways no longer falls open and closes again between samples. From one sample to the next a finger the
  gun holds back opens at once but closes back toward its shape at most 1.5 of its bend a second (`InspectLook.FingerBack`, under
  200 degrees a second), as does the hand's hold on the gun (the palm's touch that closes the fingers), and the look takes its
  fingers over from the game's grip at most 8 times its weight a second (`WeightSpeed`); a clasp's fingers close on what it holds
  as placed, and on the way into a clasp each joint opens at most `FingerBack` a second, sooner where a mesh opens it ahead. The
baked tracks are then smoothed over `SmoothFor` (0.05 s) either side (`InspectLook.Smoothed`: each joint first held at its most
open within twice that, so smoothing never closes a finger into the gun, then two running means), away from a clasp's still hold
(kept as placed) and its regrips (means only), and taken back to the raw sample wherever the smoothed finger would sit deeper
in the meshes (`SmoothDeeper`); between the samples the look reads them on a monotone cubic (Steffen), and `InspectFingers`
bakes its clip at 120 a second (`InspectLook.ClipRate`), so the speed a finger shows has no step at each sample. The
  solved look is then checked every 1/240 s of every clasped span (`InspectLook.Solve`): no finger more than 0.4 cm into the
  gun's or the magazine's meshes, the wrist within its limits, on the way between the holds some part of the hand (the palm's
  skin or a finger's) within 0.6 cm of the magazine on every sample, and in the hold the thumb's pad and three fingers' pads
  within 0.6 cm of it and the palm within 1.5 cm. Where no place clasps the size, or the solved look fails any of these, the
  look keeps the pinch and `MagNote` says why, logged once a gun and motion. A magazine still in a long gun is taken by its base
  (`face` `bottom`) and held so only once it is clear (a `regrip`): round its middle the hand would sit in the gun.
  With no `face` and wider than 4.3 cm (`MagSpan`: sniper and marksman magazines, drums, boxes) it is cradled (`InspectLook.MagCradle`):
  its length (its longest side) across the palm, along the palm's crease and square to the fingers, the palm on the thinner of its
  other two sides (the left face unless the other is under 1/1.25 of it, `MagLeftFirst`), the fingers curling over the far edge
  of the third and the palm's middle 4 cm in from that edge; `spin` turns the hand about the palm's way. With `face` `bottom` the
  palm takes the base: the fingers point to its front and 30 degrees across (`MagYaw`, the left arm comes from
  the left; a magazine longest front to back or across has the fingers square to that length, so it still lies across the palm), the palm's middle 4 cm behind its front edge (`MagKnuckles`) and the knuckles just past that edge, so the fingers close up its front, and the hand's middle
  line lies 2 cm in from its left face (`MagThumb`), so the thumb goes up that face; its top, the feed lips and the first round,
  stays clear. `side` and `top` put the palm's middle 1.5 cm off the
  left side or top of the gun's body (the meshes no part moves) where it is at `along`, the same way. Where the wrist's limits turn
  the hand, the palm stays on its place and the wrist moves. It goes with the part, never round the gun, a carried one too (a part
  with a `symmetry` under a whole turn, a cylinder, takes the hand round only between the keys that hold it: once the hand is on
  its way off, the part spins on without it, `InspectLook.WithPart`); holding a
  carried part it keeps the turn the key gives it on the part (the wrist's limits give way there, and the wrist rule reports a
  carry that asks the wrist more than 5 degrees past them); the way off the part (a regrip too) starts from the hand as the ride left it and comes to its
  own place over that span, and the way onto it ends in the hand as the ride takes it, so the hand never steps where it takes or
  lets go of the part. Where the off forearm, or the palm nearer than touching, would sink deeper into the gun than
  in the game's hold, the hand lifts toward its back by as much (a palm on a face then stands off it by that much more), except
  where the keys themselves press the palm in. `fingers` [thumb, index, middle, ring, pinky], each 0 (open, as the hand rests) to 1
  (closed, the thumb across the palm), or any of them by joint as [knuckle, middle, last] (each 0..1 of that joint's range,
  ForgeCore `FingerBend.ByJoints`; the thumb's entry may add its root's spread and opposition, [root, knuckle, last, spread,
  oppose]), shapes the off hand at that key (ForgeCore `ClipFingers` on the first-person arms through
  `Models.BindArms`, masked to the left fingers); a key without one holds the gun's shape (0.5, 0.7, 0.75, 0.8, 0.85) on the gun and
  rests relaxed (0.2) at `away`; between a key with a shape and a `grip` key the shape fades from or to the game's own grip, so the
  first and last keys have none. `scale` (above 0, default 1) draws the off hand that much of its size at that key (its hand bone
  scaled, the palm's middle kept where the place puts it, `InspectLook.OffScale`), eased between keys as the place is; the first and
  last keys keep the game's size. A hand working a part close to the eye (the slide racked, 0.8) is drawn smaller so the part shows.
  Where the palm and fingertips are more than 2 cm farther from every mesh of the gun than in the game's
  hold, the shape relaxes toward 0.2, so the hand never grips air (`InspectLook.OffFingers`, baked at 30 a second when the look is
  solved). Each finger then closes only until it meets a mesh box of the gun (`InspectLook.FingerStop`, along the finger's line from
  `InspectHand`: a nominal hand's knuckles and bones, bent as ForgeCore `ClipFingers` bends them), so the fingers stop on what the
  hand holds instead of passing into it; a finger that lies in a box with the hand open takes the bend that leaves it least deep
  (within how far it may close since the sample before, `FingerBack`).
  Two kinds of part are met closer than their boxes along the gun's axes: a magazine on its own box (`MagBox`, so a finger stops
  on a leaning magazine and not on the air beside it) and a part with a `symmetry` under a whole turn, with the parts that follow
  it, on the cylinder its box turns in (about its axis through its pivot, the radius the least way from the pivot to a side of
  the box: `InspectLook.Round`), so a fingertip stops on a cylinder's own radius all the way round. A
  hand `under` the gun with no `fingers` keeps the game's own fingers, the support hold that already lies on this gun. The stop is
  on the meshes' boxes and a nominal hand: neither the game nor the offline data gives the look a mesh's surface or the arms' finger
  bones, so a finger stops within about a finger's width of a boxy part and stands off a raked or rounded one. First person only. A place on the gun (`under`, `open`, `side`, `top`, `muzzle`) keeps its place on the gun as the gun turns:
  the gun is brought near enough for the off arm to reach it (each metre out of reach weighs in the solve, `InspectLook.OutOfReach`;
  as far past the arm as the game's own hold at rest counts as in reach), and a place the arm still cannot reach falls short of it
  rather than sliding along the gun. Between keys the hand moves on a smooth step, following the gun
  (to or from `away` the wrist turns evenly in the forearm's frame);
  a way whose straight line passes through the gun's bounds from outside them to outside them (from the grip or from out of view onto the top, a part or the magazine)
  goes round under the gun on the off-hand side, through a point 6 cm out from its left side and half its half height below its
  middle, decided once per span.
  `regrip: true` on a key on a part whose key before holds the same part declares the way into it a change of hold on that part
  (from under a magazine to its side, say, as a handgun's magazine comes clear of the grip): the palm goes round the part's mesh
  box, as far off its surface as the two holds stand off it (1.5 cm for a palm on a face; a hold in the fingers has its palm off
  the part, and the palm leaves late and comes to it early), the hand turning from one hold to the other, and the part goes with
  its own keys. The
  way is at most 0.4 s long (`InspectMotion.RegripMost`: moving a magazine from the palm to the fingers is one movement of fingers
  and wrist, about a quarter to a third of a second; slower reads as the hand sliding over the part); a longer one, one onto
  another part, or one that keeps the hold (the same face with `at` moved by under a quarter of the face in u and v,
  `InspectMotion.RegripShift`: the hand's own give in a hold is half a centimetre, and a quarter of the game's magazine faces, 4 to
  30 cm, is 1 to 7 cm) is refused. Only there may the part move in the hand (frame rule R3).
- `parts`: the parts of the gun the motion moves, `{ "node", "about", "follow", "optional", "symmetry", "carrier", "keys": [{ "t", "move", "out", "clear", "turn", "show", "carry", "ease", "bounce", "cue" }] }`.
  `node` names every node of that name under the gun (a rig some guns carry twice, in two parts, moves as one; names are the ones the
  editor backend's `preview.weapon` lists), or an array of names, the first present that draws taken (the first name is the part's
  identity), `@mag` the magazine's mount (the part holder's `m_aMag`, so the magazine part), `@round` a
  copy of the magazine's first `Cartridge` round the look adds at the shell-eject point `Front_SE` (no gun has a chambered round; it
  lies as the magazine's round does and is drawn while `show` is at least 0.5). A key's `move` is metres in the gun's space (x right,
  y up, z toward the muzzle: a slide pulled back is `[0, 0, -0.03]`), `out` metres along the node's own down axis (a magazine out of
  its well), `turn` degrees (up to 1440) about `about` (`bore`, `side` or `up`, the gun's axes at rest) through the node's origin.
  `clear` (0..3) takes it out along its down axis by that many of its own lengths (its mesh box along that axis), so a magazine
  clears its well on every gun. `carry` `{ "at": [x, y], "dist", "roll", "tip" }` holds the part in the view instead, off the gun: its box
  centre at that point of the view (-1..1 of the half width and height) `dist` metres (0.2..0.6) from the eye, its top toward the
  eye or `tip` degrees (0..80) up from that line (a part held aslant for a glance, not shown to the eye), its length turned `roll` degrees about the line to the eye or, held by the off hand, by the turn nearest `roll` (in steps of
  15 degrees) that leaves that hand's wrist most at ease (`InspectLook.CarryRoll`), so the hand shows the part to the eye the way an
  arm can on every gun; a hand on it goes with it (a part the wrist's limits turned with the hand, let go while still out of the
  gun, turns back to its keyed turn over the hand's way off it, not at once, and over a regrip goes over to the turn
  the next ride gives it), and into and out of a carried key the box centre goes on a straight line. A
  carried key takes no `move`, `out`, `turn`, `show` or `clear`; a carried `@round` is drawn (its `show` is 1 there, so on the way to a key with `show` 0 it hides halfway). A carried part follows no other part. `ease` is `in` (gathering speed and braking into the key: u³(4 − 3u)), `out` (leaving fast from no speed and braking long into the key: 1 − (1 − u)³(1 + 3u)) or `spring` (gathering speed into a stop) for the way into that key (default
  the smooth step); `spring` bounces back off the key, leaving it at 3 × `bounce` (0..0.3, default 0.08) of the speed it came in at
  and settling within 0.08 s (the next key comes at least that much later), and knocks the gun with the kind's `hit`; parts that
  slam home together (within 0.02 s) knock it once. `symmetry` (degrees, default 360) is the part's turn
  that looks the same (a six-shot cylinder 60): a rest key winds its turn to the nearest multiple, so a cylinder spun round is not
  wound back. The first and last keys are at rest (only `t`, and a `cue`, `ease` or `bounce`); before the first the part is at rest, and after the last too but for the whole turns its last key is wound to, which it keeps; as the look fades out its weight takes off only the turn past the nearest whole turn of its symmetry, and the plugin puts the node back when the look ends, where the two look the same. A part whose node hangs under another part's node goes with that part (a revolver's cylinder on its crane); `follow` names
  an earlier part a node not under it goes with too (the rounds of a speed loader with the cylinder). `carrier: true` (not on an
  `@` node) moves only the parts under the node: a child of it that neither is nor holds another part's node keeps its pose in the
  gun (`InspectParts`), so a crane node that also holds the barrel and frame (the R-66's `a_RevolverFold`) swings out the cylinder alone. A gun without a part's node, or
  whose node draws nothing (a skin hid it, a mounted model replaced the gun), does not play the motion and the log says so once per
  gun and motion; an `optional` part is moved where it is there. The plugin (`Native/Inspect/InspectParts.cs`) measures each node's
  pose and its parent's, and each part's mesh box (`InspectPartRig.Lo`, `Hi`: the meshes whose nearest part node is that part; an
  `@mag` with none takes its `Cartridge` boxes; `@round` a 6 mm box) and every drawn mesh's box (`InspectRig.Meshes`) in the gun's
  space when the look starts and writes the node's local pose each frame before the holder's
  LateUpdate (`InspectLook.PartDelta`; nothing of the game is read per frame), and puts every node back when the look ends. Parts
  move in first person only: teammates and replays see the wrist. A carried part is placed against the gun where the last
  `GunPose` put it, so a caller (the plugin each frame, an offline check each sample) calls `GunPose` for a time before
  `PartDelta` and `HandPose` for it.

`feel` (the plugin's table only, one per kind, every kind needs one): `{ "f", "zeta", "carry", "wind", "hit": [degrees, metres], "spin", "slow" }`.
Every motion's pose is its keyed pose with the item's weight laid over it (`InspectLook.Follow`; one layer for every motion of
every gun and melee weapon, nothing in it is written per motion or per frame). A spring of f hertz (0.3..10) and zeta damping
(0.1..1.5) joins the hand and the item, and only the keys' own acceleration drives it (k2 e'' + k1 e' + e = k2 x'' on the offset e
from the keyed pose x, k1 = 2ζ/2πf, k2 = 1/(2πf)², semi-implicit at 240 Hz): the item hangs back as the hand starts, goes past where
the hand stops and settles, and at a steady rate is on its keys, so a key is reached when it says. `carry` (0..2, default 1) is how
much of that shows. Before a turn of at least 45 degrees between two keys the item stops at, the hand draws it back against the
turn by 4% of it, at most `wind` degrees (0..12, default 5), over the 0.16 s before (`Winds`; not into a toss or a twirl, whose
hand has its own wind-up). A held pose is never dead still: it sways 0.6 degrees and 1.5 mm at three unrelated rates (`Sway`). The
offset, the sway with it, is at most about 14 degrees from the keyed turn (softly), does not carry the grip wrist past its limits
(`WristKept`: all of it while three times as much would still be within 0.9 degrees of them, eased off toward none past that), is
gone over the last
0.15 s, and turns about the grip point. A `hit` key, a `spring` part key and a twirl's stop knock it: the muzzle dips `hit` degrees
and the gun drops `hit` metres at the height of the knock. `spin` (30..3000 degrees a second) is the most a gun in the hand (not
tossed or twirled) may turn; `slow` (0.5..3, default 1) plays the kind's motions that many times slower (heavy and long guns). The
frame rules measure the followed pose.

`sounds` (the plugin's table only): `roles` `{ name: { "match": [patterns], "default": event or { kind: event, "*": event }, "zoom": "in" | "out" } }`
and `cues` `{ name: [role or { "role", "after" }] }`. A cue plays its roles, `after` seconds (0..0.3) late. A role finds the game's own
Wwise event for the gun in hand (`Native/Inspect/InspectSounds.cs`): the first `match` pattern (lower case, `*` at its start or end)
that names a sound item of the gun's own reload sequence (its front part's, else its stock part's) or of either part's fire
sequence, by trigger time; else its kind's `default`, else `*`'s; `zoom` takes the gun's own aim sound. The local player hears the
event in their head (ForgeCore `AudioKit.PlayGameEvent`); a teammate's gun plays its `_3rd` form following the gun object; an event
with no `_3rd` form is 3D only and follows the gun for both. A role with no event is logged once per gear, motion and role and the
beat plays nothing. Every cue a motion names must be in `cues`, and every role a cue names in `roles`. The plugin adds three cues
itself: `toss` when a tossed gun leaves the hand, `catch` when it lands and `twirl-catch` when a twirl stops (`InspectLook.Cues`, in
real seconds). Each beat plays once, on the frame its time is crossed, and none after the look was stopped. Nothing goes over the
network: each machine rebuilds a teammate's beats from the motion number and start time it already gets, and a teammate seen late
skips the beats already gone.

The frame rules added to those below (`InspectLook.Rules`, on the followed pose, every 30th of a second; R1, R3 and R6, which
measure where the arms are and need no even frames, also at every hand and part key's own time, so a pose that breaks only on a
key's frame is caught; the offline audit samples the same times):
R5, each pose shown (a key the gun does not go through, with the keys after it within 10 degrees of turn, 0.1 of the view and 2 cm
of rise) holds still (under 25 degrees and 6 cm a second) for 0.4 s,
from 0.3 s before its first key to the key after its last;
R1, where both keys of the off hand's span put it on the gun (its body, or one part), the nearer of its palm and fingertips is at
most 2 cm from what it touches (`InspectLook.ContactOf`, `ContactGap`: absolute, the game's own hold is not taken off; between
two `tips` keys it is the pads of the index and middle fingers that are measured, not the palm; the offline audit calls the
same); R3, a part out of its place that the off hand holds moves at most 0.5 cm in the hand (its box centre in the
hand's frame, from where the hold began; `SlipMost`) except over a declared `regrip`, after which the hold begins again; R6, where the item camera sees it (a point of the arm out of the view does not count), the grip arm (forearm a 3.5 cm capsule, palm a
4.5 cm ball) sinks into a mesh box of the gun at most 1.5 cm deeper than the whole arm does in the game's hold, and so does the off
forearm, with the off palm at most 1.5 cm nearer than touching (1.5 cm off); the off arm's baseline is the deeper of the game's hold and the key's own place without its `offset` (a hand on a part's face already presses its wrist into the gun's box), and the look lifts the off hand toward its back, rechecking the wrist, up to eight rounds, then keeps only the least share of that lift that still meets R6 (so a palm on a face
stands off it no farther than R6 needs, which R1 also measures); R7, in the middle of the view (30% of its half width and height, 20 by 20 rays) the arms cover at most 30% while the gun holds still and the off hand is idle (neither on the gun nor moving; `InspectLook.Idle`), and may hide it otherwise (a hand touching the gun, working a part or on its way); a carried part covers at most 40% of the middle with its centre at least 0.3 m from the eye (the gun itself is not counted: it is framed in the middle; any input puts it back within 0.15 s);
speed, a gun in the hand turns at most its kind's `spin`;
W, the wrist, measured from the game's own hold (the first-person arm's bones lie 59 to 103 degrees off an anatomical zero, and that
hold is what the player sees as a relaxed wrist): where the item camera sees the hand, it flexes at most 65 degrees toward the palm,
extends at most 65, bends at most 70 toward the thumb and 58 toward the little finger, and the forearm turns at most 80, each give or
take 2 (`InspectArm.Joint`, `InspectLook.WristBreaks`, `WristPast`); and the hand's line (the wrist to the knuckles) lies at
most 90 degrees off the forearm's, whichever way it bends, or as far as the game's own hold has it where that is more, give or
take the same 2 (`InspectArm.Fold`, `WristFold`): the joint's angles are counted from a hold that is itself folded on some guns,
so a hand at their limits can hang off its forearm past a right angle, which they do not see (a hand brought down over the top
of the gun onto a charging handle did). The look keeps both wrists inside them itself, for every
motion: `InspectArm.Comfort` turns a hand back to them (the four bends as an ellipse, and the twist) and `InspectArm.Past` is how
far a pose asks past them; the solve places the grip hand and swings its elbow so the forearm carries the gun where the wrist alone
would pass them, the off elbow swings the same way, and the follow, the sway and a toss's wrist take only what stays within them
(`WristKept`). Flexion, extension and the forearm's turn are within the active
range of a healthy wrist (Greene and Heckman, The Clinical Measurement of Joint Motion, AAOS 1994: 80, 70, 80) and a little past what
daily tasks use (Ryu et al., J Hand Surg 16A:409-419, 1991: 54 to 60); deviation is not held to the AAOS's 20 and 30, because the
off hand moved from under the gun to its flank turns about the palm's normal by about 60 degrees from the game's hold, and the two
motions the owner holds good (`pistol`, `revolver`) reach 64 radial and 53 ulnar, so the limits are theirs plus 5.
No gun key asks the grip wrist more than 5 degrees past those limits (`KeyBack`, measured from the solve; the pose is not changed by
it), the off hand is turned no more than 30 degrees off the turn its hold asks (`OffBack`, a judgement, not a measured human bound:
past it the hand visibly lets go of the shape it holds; the two good motions turn it 0), and neither forearm, wound on from the first
frame, turns more than 5 past 80;
I, inertia, on the followed pose and every part the motion moves, at 60 a second (`InspectLook.Inertia`): the gun's angular velocity
changes by at most 2600 degrees a second per second and its centre's by 10 m a second per second, those changes change by at most
67000 degrees and 430 m a second cubed (the jerk: a move that starts or stops in one frame spikes there however slow), and neither
turns back within a frame while faster than 30 degrees or 0.1 m a second; these are 1.5 times the most the left and right look of
each kind and the revolver's twirl reached outside a knock under the first follow (1724, 6.8, 44535, 287), and what the present
follow and motions exceed is reported as it is. A part moves at most 15 m a second per second, 750 m a
second cubed and 23000 degrees a second per second, from minimum-jerk reaching (Flash and Hogan, J Neurosci 5:1688-1703, 1985: 10 cm
in 0.2 s, a quarter turn in 0.15 s). A knock (a `hit` key, a `spring` part key, a twirl stopping or caught into the next move, a toss caught) is let through for
0.2 s after it, and a gun free of the hand moves by its own physics. A turn of at least 45 degrees between two keys draws back before
it or goes past its end and settles by at least 0.5% of the turn;
F, the fingers. The grip hand's thumb and three fingers round the grip (`InspectLook.FingersBreak`): the look has no surface of
the gun to stop a finger on at the grip (a grip sits inside its frame's box), so the one pose of theirs known to lie on the gun is
the game's own grip; reported are a look that moves them on a hand holding the gun still (more than 5% off the game's grip outside
0.1 s before and 0.35 s after a twirl or a toss), a shape of the look's own that closes them (mean bend over 0.45) and fingers
less than 0.6 open while a tossed gun turns into the hand. The grip hand's index finger, which the look lays along the gun's side
(once a look): its middle line more than 0.4 cm inside a mesh, at the worst sample it lies wholly along the side with the gun in
the hand, the boxes counted from the depth its knuckle has in them in the game's hold; and its skin more than 2 cm (`TouchGap`)
off every mesh where it was laid. The off hand (`InspectLook.OffFingerSunk`): a finger whose middle line lies more than 0.4 cm inside a mesh box
with the fingers as stopped, where the hand is wholly on its keyed shape (on the game's grip, on the way from or to it and `under`
the gun its fingers are the game's, which the look does not know);
P, an item free of the hand (tossed, twirled): it passes more than 1.5 cm deeper into the grip hand or forearm than the held item
lies (R6's capsule and ball), or it leaves or lands where the palm faces more than 30 degrees down (`InspectLook.ThroughBack`,
`BackDown`: it would go up through the back of the hand and be caught on it);
J, jerking fingers: a joint of either hand, as the game shows it (the finger clip as `InspectFingers` plays it, times the share the
look takes), whose speed changes by more than 150 degrees a second from one 60 a second frame to the next (`InspectLook.Jerks`,
`JerkSpeed`), from two frames before the look to two after it (the hand-over from and back to the game's fingers counts): a step, a plateau that starts or stops at once, a twitch; once per hand and joint with the count. The same static
`InspectLook.Jerks(track, times)` counts a joint read back in game frame by frame (ForgeDevelopment `FingerPoseCapture.Record`);
whether a clasp is calmer than the plain hold is still weighed by turns one way and straight back from one finger sample to the
next, both ways faster than 200 degrees a second;
S, still fingers: no joint of either hand (the index finger's lift off the trigger not counted) turns through more than 15 degrees
over the look (`InspectLook.StillFingers`);
T, a toss turns the item as its keys ask (the flip and spin the toss key adds to the key before it: one turn of a gun, two of a
knife): summed frame to frame at 240 a second from the throw's lead to the catch's settle, more than 60 degrees off that is reported, and so is every pair of neighbouring frames whose turns lie the long way apart
(`InspectLook.Tosses`); R1 is also listed at every sample;
K, jumps (`InspectLook.Jumps`, at 240 a second from two frames before the look to two after it, the game's hold outside it): either
hand's target, where the item camera sees it, moving faster than 2.5 m or turning faster than 1200 degrees a second, or a joint of
either hand's fingers as the game shows it faster than 1500 degrees a second (the grip hand not counted while the gun is free of it);
D, drift (`InspectLook.Drifts`): over a run of keys holding the gun up, the off palm's middle moves more than 2 cm on the gun;
G, a magazine in the hand (`InspectLook.MagInHand`): its middle not on the palm's side within the curled fingers' reach (8 cm) and a
hand's breadth across (6 cm), or its length (where that is 1.3 times its next side) nearer than 45 degrees to the fingers' way;
V, the off hand working a part (`InspectLook.HandInView`): its palm or fingertips nearer the eye than 0.2 m, or the hand (at its
`scale`) and forearm covering more than 40% of the middle of the view.
`InspectLook.Audit` lists every W, I, F, R6, P, J, S, T, R1, K, D, G and V finding of a look, R6 at every sample an arm sinks past the game's hold (the
frame rules stop at the first); the offline audit counts those.
Each fault names its rule (`R1:`, `R3:`, `R5:`, `R6:`, `R7:`, `speed:`, `W:`, `I:`, `F:`, `P:`, `J:`, `S:`, `T:`; the audit's own `K:`, `D:`, `G:`, `V:`). A fault is a finding, never a reason not to
play: the format test reports the shipped table's findings, the game logs each once per gun and motion as a warning and plays the
look, and the owner decides from the look itself whether a motion stays. The rules only measure; none of them changes a pose.

The shipped motions. Every gun's pool (its kind in `kinds`, single guns in `gear`) has three common motions and up to two of its
own; a gun without the part a motion moves does not get that motion (logged once per gun and motion).

1. The look (`pistol`, `revolver`, `compact`, `rifle`, `shotgun`, `heavy`, `long`), the wrist roll in two beats and back: the gun's
   left face, muzzle to the left and level, top rolled a little right; then its right side with the muzzle still up and to the
   left: pistols, revolvers and compact guns all the way over in the wrist (the gun upside down, the back of the grip hand toward
   the viewer, the elbow swung out), rifles, shotguns, machine guns and long guns in both hands under a third of a turn the other way
   (roll -78 from +30, where the grip wrist ends its range and the forearm and elbow have carried the rest), enough to show the
   right side and top, since nobody rolls a machine gun over in one hand; then the game's
   pose. The revolver's goes on as one motion: two turns on the trigger finger (the grip hand's index finger, on the gun's centre
   plane), upright, the hand going with it (watched from as near as the gun is held: its far end may leave the view as it goes
   round); the last turn runs straight on into the hand going up until the muzzle points up (the key that ends the spin is
   `through`), and the hand brings it back down. Handguns and compact guns drop the off hand out of view; the others keep it under
   the gun at the same place through both faces, the hand going round with the gun.
2. The magazine check (`handgun-mag`, `rifle-mag` for rifles, compact guns, shotguns and long guns, `heavy-mag`): the off hand takes
   the magazine by its side low down (a `mag` key with no `face`, `spin` 15; the fingers stopped on the magazine's own box),
   draws it out by 1.1 of its own height, lifts it a little out from there, to where the hand has it (low on the left of the view,
   0.47 to 0.50 m from the eye), aslant, its top 50 degrees up from straight at the eye (`carry` with `tip`, the hand riding it,
   its roll picked for the wrist), holds it 0.6 s for a glance, seats it with a push (`spring`, `hit`) and slides back off it
   (`offset` [0.012, 0, -0.03]). A handgun's is inside the grip: it comes out onto the palm (`face: bottom`), the hand takes it by
   its side as it comes clear (`regrip`) and by its base again to seat it. A magazine wider than 4.3 cm is cradled across
   the palm where a narrower one is held by its side, the thumb laid along it (0.3): the keys are the same, and which hold a key
   with no `face` gives follows the magazine's width. Revolvers do it
   in one hand (`revolver-check` on the R-66, `break-check` on the 3RB): a flick of the wrist (a `through` wind-up into a `hit`
   key) throws the cylinder out on its crane, 80 degrees about the bore, or breaks the frame open, 40 about the side; the cylinder
   runs on a third of a turn; a flick back shuts it.
3. The toss (`handgun-toss`: one turn end over end, 0.16 m up; `longgun-toss`: one turn about the bore, 0.12 m, the off hand out of
   the way and back under the gun once it is caught). Thrown, turned and caught where the gun is held (`TossMove`). A long gun's
   is seen at the view the game's hold has (`fill` 2), so the gun stays as large as it is held and about half to four fifths of
   its length is in the view; a handgun's widens the item camera a little (`fill` 0.6, to 36..47 degrees from the game's 33..36)
   to keep the flight in the view.

Of a kind's own: `slide-rack` (large pistols: the gun turned 25 degrees left and rolled 55 so the top of the slide and the port
face the eye; the off hand arches over the back of the slide from above, the fingers down one side and the heel of the palm down
the other (`face: top`, `spin: 90`), clear of the muzzle, draws the slide back 2.6 cm on the round in the chamber, lets it go and
leaves before it slams home; the gun a little smaller in the view, `fill` 0.75, and the hand at 0.8 of its size nearer the
slide's back, `at` [0.08, 0], so the chamber shows), `revolver-swing` and `revolver-break` (the flick, then the off hand comes up from outside the
cylinder with the pads of its index and middle fingers on its outer surface (`tips`, the palm clear of the frame and of the
chamber face), strokes it 24 degrees round over 0.07 s riding its surface and lifts off; the cylinder runs on from about the
stroke's top speed, some 600 degrees a second, slowing through the rest of one turn, and is pressed shut by the palm on the cylinder's side, riding it as the crane closes. The part eases all end
at rest except `spring`, which is a knock, so between the stroke and the run the cylinder's rate dips by about a fifth for two
frames), `rifle-charge` (rifles, compact guns, long guns and shotguns that have a charging
handle) and `heavy-charge` (the handle is on the left side of these guns: the gun is turned only a little, the off forearm comes
up from below or below and beside it, the off hand goes from under the gun to the handle and takes it between the thumb and the
fingers (`face: left`, `clasp`), draws it back along the bolt's axis, lets go at the back and the handle springs home past its
rest and settles; no hand comes down onto it over the top of the gun; a gun whose handle no clasp takes does not play it),
`shotgun-pump`.

Melee weapons (`"from": "hold"`; the off hand lets go and is out of the view) have three each: `knife-look` (one flat, then the
other), `knife-twirl` (a 7 cm hop, one turn end over end), `knife-toss` (two turns end over end, 0.14 m);
`bat-look`, `bat-twirl` (0.1 m, one turn), `bat-toss` (0.25 m, one turn); `hammer-look`, `hammer-spin` (a 0.1 m hop turning twice
about the handle), `hammer-toss` (one turn about the handle, 0.12 m); `spear-look`, `spear-thrust` (levelled, drawn back and
thrust, a `hit`), `spear-toss` (one turn about the shaft, 0.15 m). Every hop and toss is thrown from and caught in the palm turned
up (`palm`), whichever way the game holds the weapon, and the weapon is turned back into the game's hold after the catch. The
hammer and the spear are thrown at the view the game's hold has (`fill` 1.2 and 0.8), as low as keeps in the air what the view
showed before the throw: about half the hammer's length, its head, the handle's end behind the hand out of the view as in the
game's hold; the knife and the bat widen the item camera a little (`fill` 0.6, to at most 49 degrees). Those views are the
forward hold's, which the offline rigs assume; in a reverse hold the same keys widen the camera (the hammer's to 66 degrees).

Everything else held plays `item-look` (the `item` kind, the table's `default`).

The off hand's rule is the owner's, and the format test holds the shipped table to it: the off hand holds the gun up from under it
(`under`), is out of the view (`away`), or works the part the motion is about (`mag`, `node`: the magazine, the slide, the cylinder,
the charging handle, the pump); it never takes the grip or the trigger, and on a melee weapon it only supports or stays away. The
format still takes every hold, for skin packs and override files.

How a key is solved: the gun's bounds are every drawn mesh under it (parts, attachments, a skin's models), in the gun's own space,
except a mesh whose box alone makes the bounds more than twice as wide or tall as all the other meshes together (a sling or rope
with a placeholder box, such as the Köning's `rope`); that mesh is logged once. The gun's body cannot be left out by this rule, since
its other parts span about as much. The shown part is the box ahead of and above the grip hand (receiver to muzzle); the stock behind
it and the grip and magazine below it may leave the view. The grip hand (the right hand) and its elbow are placed by a sweep (coarse,
then fine round the best) of places within 0.3 m (0.12 m in a motion with a toss) of where the game holds it and its arm's reach, and of elbow swivels from 20
degrees tucked in to 90 swung out (the game's two-bone IK bends toward a goal the look moves for the whole look). A place where the
wrist would pass its limits by more than 2 degrees to hold the key's turn (`InspectArm.Comfort`: rule W's four bends as an ellipse
and the 80-degree twist) loses to every place where it would not, so the arm and elbow carry the gun before the wrist bends
further; where no place is within them the gun is held back to them and rule W reports the key (`KeyHeldBack`). Among the
rest, the frame rules come first (below, a margin of 0.1 inside them), then the narrowest item camera view the shown part needs
(three times as much past the game's view), the hand's distance from where the game holds it, the swivel, the centre's distance
from `at`, and each place the off hand touches about that key being out of its arm's reach. A key's view is never narrower than the
game's and at most 75 degrees (vertical); between two keys of the motion the view widens per frame as far as the gun needs, so the
gun and hands draw smaller while the world's camera is untouched.

What a key aims for (`InspectShot`, the hand's place is a bound of the sweep): at least 65% of the gun's length (33 points on its bounds' centre line, back
to front) in the view (the length and not the drawn area, which a stock at the eye would fill); the outline of its bounds covering at
most 75% of the view (the outline is about twice the gun's own silhouette); the shown part's nearest corner at least 0.15 m from the
eye, or as far as the game's own hold has it (a compact gun's receiver sits under 0.1 m there; the stock may pass the eye as it does
in that hold); the bounds centre and the shown part in the view; the grip hand in the lower right of the view (at least 0.2 of the
half width right of the middle and not above it, in the game's view, or as far as the game's own hold is) and from 0.08 m nearer to
0.18 m farther than the game holds it (0.12 left the right side of machine guns and long guns under 0.1 m from the eye), so the gun runs up and left from a hand where the game holds it and is not pushed out to
fit. Between two keys of the motion the grip point moves on a straight line, so the hand stays in that place while the gun turns
over in it, and the gun turns with the grip hand's wrist, which goes between the keys' wrist turns the short way in its forearm's
frame (`InspectArm.Wrist`; the short way in view space wound a compact gun's forearm a whole turn round while the arm swung). To and from the game's pose the back of the gun moves on a straight line (so the stock leaves the eye without swinging
nearer); every way is bowed off it forward, down or out by the least bow that keeps the rules below, except the way into a toss (the gun flies there, and a bowed hand would turn it with its forearm about a leaning axis).

What no frame may do: leave more than half of the gun's length out of the view, or bring the shown part nearer than 0.12 m (or than
the game's own hold has it); frames on the way from and to the game's pose are held to the poorer of the game's pose and these, give
or take 0.1 of a share (the follow carries the gun a little past the game's pose as it settles there) and 0.02 m. Nor may a joint jump or wind: from one frame to the next no hand's twist about its forearm or
its wrist's bend, and not the grip elbow's swivel, moves more than 15 degrees (an off hand dropping out of view bends about 10 a frame;
a wrist limit flipping across its half turn, as an off hand under a rifle rolled over does, jumps 160), and neither forearm's twist,
wound on from the first frame, goes more than 5 degrees past the wrist's 80-degree limit, nor is the off hand turned off its hold past
rule W's 30 degrees. The off hand is turned within `InspectArm.Comfort` (rule W's four bends and the 80-degree twist off its rest,
then stops) about the wrist with the palm left in place; the grip hand's place is held to the same by the sweep. Every frame is sampled at 60 a second, with
both hands' reach; what a look breaks anywhere in these hands is logged once per gun and motion as a warning with the time and the
rule, and the look plays. Only a technical failure (a part the motion names missing on the gun) leaves a motion out, logged. The press only
measures (the log line gives its milliseconds); the solve (10 to 75 ms, the revolver's spin the most) runs on a worker thread on
that copy of the measures, and the look starts on the first tick after it is done if the same gun is still in the hands. The off hand's places are points of the gun's bounds (the `side` and `top` hands are its
grip hand turned about the bore), so one motion fits every gun of a kind, and its wrist is kept within the same limits. One-handed
guns use the same roles: the right hand holds the grip, the left hand, which supports it in the game's pose, is the one that lets go. Where the off wrist would pass its limits its elbow swings the forearm round instead (`InspectLook.OffSwivel`, solved a frame at a time, reached ahead and at most 8 degrees a 60th; `OffElbow`/`OffElbowAt` give the elbow, and the plugin bends the off arm's IK toward it). A carried part turns about the palm by what the wrist can follow, not past it; a hand riding a part keeps the hold it had at that span's start; keys of one pose (same turn, no toss or spin) share one set of off hand contacts with no bow between them; the off fingers open as soon as they leave the grip and touch nothing, and close near a contact. `under` and `muzzle` find the gun's underside from the meshes over the hand (within 2 cm across it), so a spent shell hanging beside the gun does not pull the hand down.
The fingers (`Native/Inspect/InspectFingers.cs`) play three clips per look, baked from it: the off hand's shapes
(`InspectLook.OffFingers`), the grip hand's index finger (`InspectLook.IndexFinger`) and the grip hand's other fingers
(`InspectLook.GripFingersBaked`), each masked to those fingers on the first-person arms and weighed each frame by how far the look
takes them times the look's own weight. The grip hand's fingers are not in the table; every motion gets the same
(`InspectLook.GripFingers`). The thumb and the three fingers round the grip keep the game's own grip while the hand holds the
weapon, the one pose of theirs known to lie on this gun; on a twirl they open to let the gun round and close over the end of the
spin; round a toss all five open as the gun leaves and close once it has landed and stopped turning in the hand. The index finger
comes off the trigger for the look, over the first 0.25 s, and lies along the side of the gun until the last 0.25 s; for a twirl
it is back in the guard 0.2 s before the spin starts and until 0.2 s after it ends (the gun turns on it), and a motion from the
hold (melee weapons, the default look) keeps the game's. Where it lies is solved once a look (`InspectLook.IndexBend`, in the
game's hold): the bend at which its line from knuckle to tip is parallel to the gun's side, then from 6 degrees at the knuckle
more open than that (`IndexGive`) it closes until its skin meets a mesh that no part of the motion moves (a cylinder that swings
out or a frame that breaks open leaves it on the frame behind), at most 6 degrees past flat, the boxes counted from the depth
its knuckle has in them in the hold (a nominal hand's knuckle lies in the grip's box). On a gun wider than its grip the knuckle
bends back, up to 24 degrees (`InspectHand.Lift`), which ForgeCore's `FingerBend` (0..1) does not say: `InspectHand.Lifted` gives
those three joints' turns, the knuckle's whole-bend turn scaled back. A hand that does not hold the gun from its side, a finger
left more than 0.4 cm inside a mesh or more than 2 cm off them all is `InspectLook.IndexFault`, logged once a gun and motion; the
finger is a nominal hand's against mesh boxes, so on the real hand it can stand a little off the gun or press into it.

`kinds` names lists of motions (`pistol`, `highcal`, `revolver`, `compact`, `rifle`, `shotgun`, `heavy`, `long`, and for melee
weapons `knife`, `bat`, `hammer`, `spear`, and `item` for everything else); `gear` maps an offline gear id to its pool, motion ids and `@kind`s (each motion
once); `fallback` picks the pool for any other gun by its measured length in metres (`upTo`, the last entry without it), and its
entries with `"melee": true` do the same for melee weapons (a list of their own, its last entry without `upTo`). Each press plays a motion of the gun's pool at random, never the one
that gun played last while the pool has another (`InspectTable.Pick`); motions the gun cannot play (a part missing or hidden) are
left out first, and a gun that can play none of its own takes the fallback pool for its length, then the default. `default` lists
the motions and `@kind`s of anything held that has none of its own (above), each from the hold; its first `@kind` names the kind
whose `feel` and sounds it plays with. A later file replaces motions and
kinds by name and gun entries by id; its `fallback` and its `default`, when given, replace the whole list. So a skin author can give a gun of their
own motions that move their model's named nodes. A faulty file is not used at all and logged once as `skin-inspect-failed`.

A skin pack can carry its own motions: `inspect.json` at the root of the `.forgeskin` zip, beside `manifest.json` (the editor's build
copies an `inspect.json` that sits beside the project file). It has the table's shape without `fallback` and `default`: `motions`, `kinds` and
`gear`, whose lists name only the pack's own motions and kinds. It adds and never replaces: while a gun shows the skin, the pack's `gear` list for that gun's offline gear id and its `kinds` of the names the gun's list in the table uses
(`"rifle": [...]` joins every gun whose list has `@rifle`, the fallback's included) come after the gun's own motions in its pool
(`InspectTable.For` with the packs' tables). Its motions are named `<skin id>/<motion id>`, so they never take the place of the
plugin's or another pack's, and teammates with the same pack see them by that name's number. `SkinPack.Open` reads and checks it with
the same rules as the table; one that cannot be read, names a motion or kind it lacks, has a `fallback` or a `default`, or sits in a character
skin rejects the pack as `bad-inspect` (the picker lists it as damaged, the log has the rule). A kind the plugin's table does not have
is logged once when the pack is read, since no gun would play it. Exporting to the Skins format leaves the motions out and says so.

Each
look writes one line to `forgeskin.log`: the gun, its kind, its motion, its length in seconds and its number of sounds, its bounds,
the item camera, each key's item view in degrees and each key's elbow swivel. Teammates see a motion by its name's FNV-1a 32 hash, so a motion only one player's table has is not shown on
that body; on their screens (and in a replay) the right wrist turns with the motion's roll and turn at the pace of the gun's kind
(`slow`; each motion's third-person clip is made at every pace the table's kinds use, named `<id>@<slow>`) and the off hand stays
on the gun, and the gun plays the motion's sounds. The follow, the hand on the gun's parts, a toss and a twirl are solved for
first-person arms and the gun pose of a body is the game's: they are not sent, as a teammate's third-person body shows the wrist
only and the motion number and start time already rebuild the rest each machine needs.

## Other packages on the same gun

- A gun whose model a package mounts with ForgeCore `GunMount` (EnergyLab's guns, Workshop gun models) is not skinned:
  `GunMount.Of(holder)` names it, and `GunMount.Mounting` puts a skinned gun back to its own look before the mount reads
  its poses and hides its parts. Part mounts (`GunPartMount`) are not excluded; a skin and a mounted part share the gun.
- Gear icons: ForgeSkin redraws only the icons of gear whose skin or colours changed; a
  package that supplies its own icons (`GearIcons.Supply`) draws them again itself.
- Ejected casings: ForgeSkin appends its own pools after the game's in `WeaponShellManager.SetupPools` and points a skinned
  gun's casing type at its pool while it ejects; ForgePerformance's casing limit counts and drops those casings like the
  game's, before any casing is taken from a pool. `Shells` holds a reference to each pool it builds and stores into the
  game's pool array with the collector's write barrier (`Shells.Put`): the game runs the incremental collector, and an
  Il2CppInterop array indexer store alone lets it free a pool the array still points at. A slot without a pool of ours
  points at one of the game's pools, never at a pool that was not set up.
- ForgeSkin installs no patch on a method another package patches; shared game events come from `GameHooks`.

## Build

Paths are the repository's (`Mods/ForgeSkin/...`). In the public mirror (`NAinfini/GTFO-Forge-Mods`) the package folders sit at the
root, so drop the `Mods/` prefix (`ForgeSkin/ForgeSkin.csproj`, `ForgeSkin/tools/package_release.py`). Run the commands from the
repository or mirror root.

The plugin needs a BepInEx profile of GTFO (the build references its BepInEx, GTFO-API and game interop assemblies); without
`-p:GTFOBepInExPath` those references do not resolve:

```
dotnet build Mods/ForgeSkin/ForgeSkin.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
python Mods/ForgeSkin/tools/package_release.py --artifacts-path artifacts/dotnet --output <folder>
```

The pack reader (`Core/`) is compiled into `ForgeSkin.Plugin.dll`; the zip holds that one DLL, `lang/` and the metadata.

The build also compiles ForgeCore, which embeds self-made media: the three sights' meshes and textures
(`ForgeCore/Assets/Sights/*.forgemesh` and `*.t<index>.png`) and the effect bundle (`ForgeCore/Assets/Vfx/forge-vfx.bundle`), each folder
with its `LICENSE.txt`. The mirror carries them. This repository's Git does not track them (local media of the maintainer's machine, put
into the mirror by `export-mirror.ps1`); a checkout without them stops with an error naming the missing file, and
`-p:ForgeModsRoot=<a Mods folder that holds them, trailing slash>` points the build at a folder that has them.

The release bytes (`ForgeCore.dll` of the Thunderstore package) come out identical from the mirror when the clone sits in a folder
named `Mods` (`git clone https://github.com/NAinfini/GTFO-Forge-Mods.git Mods`; `Directory.Build.targets` maps the checkout to a fixed
prefix, and `CallerFilePath` strings carry `Mods/...`), on Windows with Git's default CRLF checkout of the sources and the mirror's
`.gitattributes` keeping `*.json` LF (embedded resources). Any other folder name or an all-LF checkout builds a working plugin whose
bytes differ.

The editor (`Editor/`) needs the .NET 10 SDK (`pnpm start` builds `Backend/` itself), Node 26.5.1 or newer and pnpm 11 (the
versions in the repository root `package.json`), and builds the same way in this repository and in an export (the mirror and the
friend's repository, `Mods/Release/README.md`). It needs no BepInEx profile and no local media. `pnpm install` and the first
`pnpm start` download Electron, so the machine must be online then:

```
pnpm install --frozen-lockfile    # in Editor/, its own lockfile; nothing above Editor/ is read
pnpm lint                         # eslint and tsc; also writes src/renderer/generated/skin-document.ts from the backend (builds it)
pnpm start
```

The editor imports site code from `Web/src` by relative path (the character editor and the model import use it), so an export root
holds a `Web/` folder beside the package folders: the transitive closure of those imports (about 36 files of shared site modules,
among them `Web/src/forge/generated/wire.ts`), which `export-mirror.ps1` writes whenever it exports ForgeSkin. Without it `pnpm lint`
fails with "Cannot find module '../../../../../Web/src/...'" and `pnpm start` cannot bundle the renderer. The editor also reads
`ForgeCore/Visual/Data/fx-programs.json` and `ForgeCore/Visual/tools/shaders/ForgeFxPrograms.hlsl`, so ForgeCore sits beside ForgeSkin.
The dev server takes port 5190 (`FORGESKIN_DEV_PORT` sets another). Every npm package that code imports (`zod`, `i18next`, `fast-png`,
`three`, `@gltf-transform/core`, `@gltf-transform/extensions`) is declared in `Editor/package.json`, resolved from the editor's
own `node_modules` by `paths` in `Editor/tsconfig.json` (type check) and `resolve.dedupe` in `Editor/vite.renderer.config.ts`
(bundle); a new package in that code goes into all three, and `export-mirror.ps1` fails when one is missing from `package.json`.
In this repository the site's `Web/src/forge/generated/wire.ts` must exist first (`pnpm generate:ephemeral` in the repository root);
an export carries the copy it found. `pnpm make` and `pnpm package` need the folder `tools/build-editor-release.ps1` stages
(`FORGESKIN_PUBLISH_DIR`).

## Adding packs

- Put `.forgeskin` packs in `BepInEx/ForgeSkin/skins` (`ForgeSkin.Format.SkinFolders.Skins`), wherever the DLL is installed
  (a mod manager puts it in `plugins/NAinfini-ForgeSkin/ForgeSkin/`); the editor installs there, and `active.json` there
  picks the skin per target. ForgeSkin 1.1.0 and earlier used `BepInEx/plugins/ForgeSkin/skins`; the editor moves what it
  finds there (`Backend/OldSkinMove.cs`), the plugin only reads the new folder and, while skins are left
  in the old one, says so in the picker (a player's state, not an error).
- Or ship a mod folder directly under `BepInEx/plugins` with a `forgeskin.forge` marker (`SkinPacks.Marker`) listing its packs,
  one path per line relative to that folder; blank lines and `#` comments are skipped. Only the marker directly inside a mod
  folder is read: a marker in `plugins` itself or a folder deeper (a zip with a top folder unpacked as it is), and a pack in
  `plugins` itself or in a mod folder without a marker (and not registered by code), log `skin-pack-misplaced` with the path,
  at each full scan.
- Or call `ForgeSkin.Plugin.SkinPacks.Register(path)` from your plugin's `Load`.

A pack the reader refuses is not loaded and logs `skin-pack-rejected` with the reason code of `SkinFormatException`
(`bad-audio`, `bad-image`, `bad-mesh`, `missing-file` and the others).

A MetallicSmoothness texture is in the game gear shader's layout (`Cell/Player/CustomGearShader`, as the game's own gear
textures): metallic in R, smoothness in G. A material's or attachment's glow (`EmissiveColor` x `EmissiveIntensity`, effect
flashes, unlit pictures) is in "as itself" units, as the editor previews it; it reaches the game's `_EmissiveColor` times
ForgeCore `ColourSpace.EmissionPerUnit` (0.01, the gear shaders' measured emission gain, the one glow unit) for white; its colour is sRGB like every skin
colour, and ForgeCore `ColourSpace.Glow` (`Glow.Value` in game) works out the value so the shader gets the picked hue whether the property is `[HDR]` (set as it
is) or a plain colour (Unity converts it from sRGB). With no `EmissiveColor` the intensity scales the game material's own glow colour.

A texture set straight on a material (a material's, a wear stage's or an attachment material's, not a layer atlas) is a
PNG, a JPEG or a precompressed DDS (`Core/Dds.cs`), told apart by its header: a standard DDS with the DX10 header, BC1 when
opaque or BC3 with alpha and for normal maps (DXT5nm), sRGB in the DXGI format, Unity's full mip chain with rows from the
bottom, both sides multiples of 4 up to 16384 (the texture side Unity's Direct3D 11 takes). The editor writes every such texture as `textures/<name>.dds` only (a PNG
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
manifest was read is read again first. A loaded skin stays while a level runs (`Skins.InLevel`, from `Generating` to `AfterLevel` or the lobby); outside one, a skin no gear shows or waits for is freed after 30 s (`Skins.Release`), except a character the local player picks or a teammate's look names, kept while they do (`Characters.InUse`); a replaced
or removed one as soon as no gear shows it.

## Logs

Errors go to `BepInEx/LogOutput.log` through ForgeCore `Log` under their registered codes. With `Development` on, the plugin
also writes `BepInEx/ForgeSkin/forgeskin.log` (`<UTC time> info|warn <message>` per line, cleared at launch), which the editor reads
as its game feed; with it off no file is written and info lines are dropped. Each skin applied lists every renderer slot
of the gear: `swap <path>[slot] mesh '<mesh>' <material>#<id> -> <look>` (with `(skin target '<name>')` when the slot took
the look through the material object it shares with a matched slot, or through its mesh name) or `kept ...: no skin target
'<name>'`. A material override covers every slot showing that material object, as in the editor.

## Editor control by an AI agent

Started with `--ai-control` (optionally `--ai-control=<port>`, 1024 to 65535, default 9333) or with the environment variable
`FORGESKIN_AI_CONTROL` (a port, or `1`), the editor opens the Chromium DevTools protocol on 127.0.0.1 only and offers the
structured interface `window.forgeAI`; without either no port is open and `forgeAI` does not exist. An invalid port is ignored
with one `ai-control` line in `GTFO_UserData/logs/app.log`. The flag is read before the app is ready and has no effect on the
Squirrel install, update and uninstall runs. The editor has no single-instance lock, so close an editor that is already open
first (two copies share one data folder). All three copies run the same compiled code (the flag is read in `Desktop/shell/aiControl.ts`, the
interface is an ordinary renderer module loaded on demand); an installed copy starts with
`%LocalAppData%\ForgeSkin\ForgeSkin.exe --ai-control` (Squirrel's launcher forwards arguments), a portable copy with
`ForgeSkin.exe --ai-control`, a source run with `pnpm start -- -- --ai-control` in `Editor/`. In this mode the window also keeps
rendering when covered or minimized (`backgroundThrottling` off). Every file dialog is the system's: the main process asks for
paths through one function (`pickPaths` in `Desktop/shell/dialogs.ts`), and since a protocol client cannot drive the system's dialog, an agent
answers the next dialog beforehand with `forgeAI` `pickNext {paths}`; that dialog then does not open, and nothing is shown in the
window for it.

`Editor/src/renderer/aiControl.ts` is the interface: `forgeAI.state()`, `command(name, args)`, `patch(ops)`, `get(path)`. Every
call answers `{ ok: true, ... }` or `{ ok: false, code, message }`. It adds no logic of its own to the document: commands call
the store and livery actions the UI calls (`edit`, `addLivery`, `addLayer`, `duplicateAttachment`, `importMesh`, `applyToGame`,
...), so validation, one undo step per command and autosave are the UI's. `patch` is generic over the skin document (paths with
`key=value` array selectors; set, add, remove, merge in one edit), so a field or layer kind added to the document needs no
change in `aiControl.ts`; a new command is added only where the UI has an action with no document-field equivalent (placing a
decal by UV, the camera, a render, the UV islands). `command('help')` lists every command with its arguments, and the command
table, paths and reason codes are in [AI-GUIDE.md](AI-GUIDE.md), the agent's operating guide (that file also holds the UI route:
screen map, main flow, file handling, limits).

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

## Editor main process and the desktop shell

`Desktop/shell/` (README there) is the main-process and preload code every desktop app of ours shares: the backend process
(`Backend`: spawn, one JSON line per message, restart after a crash, `dotnet build` first in a checkout), the data folder and
its move, the Squirrel update check, system dialogs with `dialog:preset`, `--ai-control`, `app.log`, the window's recovery,
the frameless window's title bar (name, icon, window buttons) and the local file protocol. `Editor/src/main/main.ts` is a thin entry: it calls `createShell` with ForgeSkin's settings (folder
names, backend paths and arguments, `FORGESKIN_AI_CONTROL`, the update feed, the `sf` protocol and its file types) and keeps only
what is the skin editor's: the write whitelist (`writable`), the file and profile watches, the hidden card window, plugin staging
and the window's title and icon. The preload spreads `shellBridge()` into its own `window.forge` object and calls`nmountWindowChrome()`; the editor's own top-bar controls (help, settings, export) sit in the title bar's slot (`ui/TopBar.tsx`). Vite and tsc reach the
shell by relative import (tsconfig `paths` maps `electron` for it); `Mods/Release/export-mirror.ps1` exports `Desktop/shell` with
ForgeSkin and cuts one `../` off those imports.
## Editor data folder and editions

`Desktop/shell/dataDir.ts` (the desktop shell shared with other desktop apps; ForgeSkin passes its folder names and data markers in `Editor/src/main/main.ts`) is the only place that decides the data folder; the backend gets it as `--data-dir`, and the
app log, dialog memory, write checks and the window (`app:dataDir`) ask `shell.dataDir()`. `scripts/gen-types.mjs` runs the
backend on a throwaway folder. The edition comes from where the app runs:

| Edition | Recognised by | Default data folder | Updates |
| --- | --- | --- | --- |
| dev | not packaged | `Editor/GTFO_UserData`, fixed | none |
| installed | exe in `<install>/app-<version>/` | `Documents\ForgeSkin` | Squirrel feed |
| portable | packaged, anywhere else | `GTFO_UserData` beside `ForgeSkin.exe` | none, no Squirrel start-up handling |

Settings → data folder picks another folder (installed and portable). The choice (`dir`) and the folders the data was
moved out of (`former`) are kept in `data-folder.json`, never in the data folder: the installed copy's in its Electron
profile (`%AppData%\ForgeSkin`), the portable copy's beside `ForgeSkin.exe`, where a folder inside the exe's folder is
written relative to it, so a portable folder copied elsewhere still finds its data. `moveData` stops the backend and file watches, copies, checks that every file arrived with its size, switches,
then deletes the old folder; on failure it removes its copy and the old folder stays in use. An empty or new folder takes
the copy, a folder that already holds ForgeSkin data (`settings.json`, `autosave/` or `logs/backend.log`) is used as it is
and the old one is left alone, any other non-empty folder is refused. The window saves its open skin first and reloads
after a move. An installed copy that finds `<install>/GTFO_UserData` (versions up to 1.1.0) moves it to its data folder
the same way at start, before the backend starts; when that fails it keeps the old folder as its data folder.

Projects store files under their own folder or the data folder relative to the project file, so autosaved skins move
with the data folder. A project saved outside it that names a data file (a sticker, a shape, the tutorial sample) holds
that path relative to itself, which points into the old folder after a move. The editor passes every `former` folder to
the backend (`--former-data-dir`); `Projects.Load` takes a named file that is gone and lies under a former folder from the
same relative place in the current data folder when it is there, and the next save writes the new place. Nothing else is
guessed: a file found in neither place stays as named and building and the pre-export check name it as missing. Only the
project file holds relative paths: `Load` and `Save` convert, and every document the backend gets (`project.save`,
`project.missing`, `project.files`, `pack.build`) must name its files by absolute path, else `bad-request`
(`Projects.Absolute`).

## Editor release

One release is the plugin's Thunderstore zip and the editor's Squirrel installer and portable zip. They have separate version
sources: the plugin and its zip use the ForgeSkin row of `Mods/Release/release.json` (`pnpm ts Tools/Forge/set-package-version.ts
ForgeSkin <x.y.z>` writes it into `manifest.json` and the code), the editor uses `Editor/package.json`; each is checked against its
own source, and the two need not match. Build it from the repository root with a BepInEx IL2CPP profile for the game references:

```powershell
pwsh Mods/ForgeSkin/tools/build-editor-release.ps1 -ForgeCore <release>/packages/NAinfini-ForgeCore-<version>.zip -BepInEx "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-Multiplayer\BepInEx" [-Output <dir>] [-SkipMake]
```

The script, all under `<Output>` (default `artifacts/forge-skin-release`):

1. builds `ForgeSkin.csproj` in Release (ForgeCore builds with it) into `dotnet/`;
2. writes the ForgeSkin Thunderstore zip into `thunderstore/` with `tools/package_release.py`, puts the `-ForgeCore` zip beside it (the
   release set's own package from `Mods/Release/build-all.ps1` or `build-release-set.ps1`, never a second build of it) and checks
   both against this build with `Mods/Build/verify_release.py`;
3. publishes the backend (`Backend/ForgeSkin.Backend.csproj`, .NET 10, self-contained single file) into `publish/`;
4. stages the plugin the installer carries: `publish/plugin/ForgeSkin/` (`ForgeSkin.Plugin.dll` and `lang/`) and `publish/plugin/ForgeCore/`
   (the ForgeCore zip's `plugins/ForgeCore/`, the folder a mod manager installs as `plugins/NAinfini-ForgeCore/ForgeCore/`);
5. runs `pnpm install --frozen-lockfile` and `pnpm make` in `Editor/` with `FORGESKIN_PUBLISH_DIR` set to `publish/`, which
   `forge.config.ts` ships as the app's `resources/publish` (the prePackage hook refuses to package without it);
   Squirrel writes `ForgeSkin-<version> Setup.exe`, `ForgeSkin-<version>-full.nupkg` and `RELEASES` into
   `Editor/out/make/squirrel.windows/x64/`; the script then zips the packaged app `Editor/out/ForgeSkin-win32-x64/` into
   `ForgeSkin-<version>-portable.zip` (.NET's ZipFile) and checks it (it holds `ForgeSkin.exe`, `resources/app.asar`, the
   backend and both plugins under one `ForgeSkin-win32-x64/` folder, and no `GTFO_UserData`, `data-folder.json` or `Update.exe`);
6. copies the files to upload into `upload/` and lists their SHA-256 in `upload/upload.sha256`.

`-SkipMake` stops after step 4; `-PluginOnly` skips step 3 too. The installer is not code-signed. Nothing is uploaded; the upload steps and the fixed update
feed (`ForgeSkin-update` release of NAinfini/GTFO-Forge-Mods) are in `Mods/Release/README.md`.

In a checkout the backend installs the plugin from `artifacts/forge-skin-release/publish/plugin` (`Install.PluginSource`).
The development editor never installs on its own (nor do the editors `docs-shots.mjs` and `gallery-thumbs.mjs` start); an
install the user asks for (the profile menu's 从源码构建并安装插件, or 应用 when the profile's plugin is missing or differs) first runs
this script with `-PluginOnly` and no `-ForgeCore` (`Editor/src/main/main.ts` `stageDevPlugin`): it builds ForgeSkin and ForgeCore
from source and stages ForgeCore's folder from that build and the files `release.json` lists for it (local media from this
checkout, else the main checkout), with no Thunderstore zip; nothing has to be packaged first. `-BepInEx` defaults to
`GTFO_BEPINEX_PATH`, else the Forge-Multiplayer r2modman profile. Its whole output goes to `app.log` (`plugin stage`). A packaged
editor installs on its own (at start, on a profile change, after the game closes) only into a profile the user picked in the
profile menu (`prefs.profileChosen`), from its own `resources/publish/plugin`. The script stages `publish/plugin` in a sibling
folder and swaps it in whole, so an interrupted run leaves no half folder.

`plugin.install` (`Install.Plugin`, rule in `Install.Plan`) leaves each package as one loadable copy at its home
`BepInEx/plugins/NAinfini-<Package>/<Package>/`, where a mod manager puts it (`Install.Home`). Every other folder under
`BepInEx/plugins` holding `ForgeCore.dll` or `ForgeSkin.Plugin.dll` goes: the editor's former folder `plugins/ForgeSkin/` is cleared
(with the `ForgeSkin.Format.dll` and `NVorbis.dll` 1.0.3 left there); any other copy goes to the Recycle Bin, its folder whole when it
holds nothing but the package's files, else its DLL, and is reported (`notes`, `plugin-copy-removed`, with its path). The home copy
takes the shipped files unless it is a newer copy straight from the store (`Install.NewerFromStore`: its package folder's Thunderstore
`manifest.json` `version_number` equals the DLL's file version, above the shipped DLL's), which stays and is reported (`newer`,
profile `pluginNewer`); a newer store ForgeSkin stays only while the ForgeCore the profile ends with meets the minimum of its
`[BepInDependency]` (`PluginMeta`, read from the DLL's metadata), else the shipped ForgeSkin replaces it. BepInEx and GTFO-API
belong to the mod manager: the editor neither installs nor checks them. `plugin.loadCheck` (`LoadLog.Check`) reads, never writes,
the profile's `BepInEx/LogOutput.log` when it is newer than the last change under `BepInEx/plugins` (the editor stamps the home
folders it writes) and turns BepInEx's `Could not load [...]` and `Skipping [...]` lines about ForgeCore or ForgeSkin into notes
(`load-missing-dependency`, `-any`, `load-incompatible`, `load-duplicate`, `load-dependency-failed`, `load-refused`); the editor runs it
when it opens, on a profile change and after the game starts or closes, through the shell's `loadCheck` (`Desktop/shell/loadCheck.ts`,
shared with the map workshop: each note once per run, a dependency-failed note left to the note that names its cause).
`plugin.gameLog` (`LoadLog.Lines`) reads the same log and returns every Fatal, Error and Warning line and every line of a Forge
source (`level`, `source`, `text` with its continuation lines), with `exists`, `written`, `changed` and `stale` (written before the
last change under `BepInEx/plugins`). `plugin.mods` (`LoadLog.Mods`) lists the profile's Forge plugins (`guid`, `name`, `version`,
`path`, `sha256` of the DLL). Both only read. Every note is two layers: `args` make the short toast
(`install.<code>` in the locales), `detail` (what was checked, paths, versions, the log line, cause and fix) goes to `backend.log`
and `app.log` (`useStore.report`). Uninstalling removes the home ForgeSkin unless a mod manager owns it (a `manifest.json` in
`NAinfini-ForgeSkin/`) and what is left in `plugins/ForgeSkin/`; the skins folder and ForgeCore stay.

### The backend as a general profile tool

The install, load-check and profile code names no package: `ProgramManifest` (`Backend/ProgramManifest.cs`) is the program's list, and
the ForgeSkin editor's is `ProgramManifest.ForgeSkin` (ForgeCore, then ForgeSkin; all methods), used when no argument is given.
Another program starts the backend with `--manifest <file.json>` and/or `--methods a,b,x.*` (the flag wins over the file):

```json
{ "packages": [{ "name": "ForgeCore", "dll": "ForgeCore.dll" }, { "name": "ForgeEngine", "dll": "ForgeEngine.dll", "minVersion": "1.0.0" }],
  "dependencies": [{ "guid": "dev.gtfomodding.gtfo-api", "name": "GTFO-API", "package": "GTFOModding-GTFO_API", "minVersion": "0.6.1" }],
  "primary": "ForgeEngine", "pluginRoot": "plugin", "skinProfile": false,
  "methods": ["game.*", "profiles.*", "plugin.*", "content.*"] }
```

- `packages` in install order; each ships as `<pluginRoot>/<source or name>/<dll>` and ends at `BepInEx/plugins/<team>-<name>/<name>/`
  (`team` defaults to `NAinfini`). `plugin.install`, `plugin.uninstall` (the primary package), `plugin.loadCheck` and `Install.Plan`
  work on this list; the rule that a newer store copy stays only while every package decided before it meets its `[BepInDependency]`
  minimum is the same for any pair. `pluginRoot` is relative to the repository root (the folder holding `.git`) or absolute.
  `skinProfile: false` leaves out ForgeSkin's skins folder and the files older ForgeSkin editors left.
- `profiles.list|get|add` report `packages` (`name`, installed `version`, shipped `version`, `current` = one copy at its home with
  the shipped files' hashes or a store copy that stays, `belowMinimum`) and `dependencies` (`name`, `package`, `version`, `ok` =
  found by GUID under `BepInEx/plugins` at least at `minVersion`; never installed). `pluginVersion`, `pluginCurrent`,
  `pluginShipped` and `pluginNewer` keep speaking for the primary package.
- `methods`: a method outside the set fails with `method-not-allowed` before it runs. A program that must not read the game's assets
  leaves out `catalog.*`, `preview.*`, `assets.*`, `enemies.*`, `sights.*` and the rest of the weapon tools.
- `content.install { zip, profile }` installs an exported Rundown or content zip (its `forge/rundown.forge` or
  `forge/content/pack.forge` names the `package`, `Author-Name`) as r2modman would, with the same routes and refusals as
  `Tools/Game/game_profile.py` `install_export_pack`: `plugins` entries to `BepInEx/plugins/<package>/`, `GameData` entries to
  `BepInEx/GameData/<package>/` (MTFO), anything else refused (`unsafe-entry`, `no-identity`). It unpacks into
  `BepInEx/.forge-staging/<id>/`, moves a package of the same name to the Recycle Bin, renames each folder whole into place and
  leaves `.forge-installed.json` (`{path: sha256}`, `GameData/` prefix for tables) in the plugin folder. `content.list { profile }`
  lists the folders that hold that record; `content.remove { profile, package }` sends such a folder (and its tables) to the Recycle
  Bin and never touches one without the record. Running GTFO blocks only DLLs (`game-running`): a package of `.forge` files and
  tables installs while the game runs and is hot-loaded.
