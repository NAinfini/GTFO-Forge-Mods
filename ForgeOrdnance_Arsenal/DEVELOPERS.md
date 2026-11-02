# Forge Ordnance Arsenal for developers

This file is for people who edit the call-ins or build a similar add-on. Players should read `README.md`.

Arsenal is a working example of a Forge Ordnance add-on. It uses only the public APIs of [Forge Ordnance](../ForgeOrdnance) (version 2.0), [ForgeCore](../ForgeCore), [ForgeAudio](../ForgeAudio) and [ForgeVisual](../ForgeVisual). Supplies and turrets are plain definitions of Ordnance's built-in `Item` and `Gear` kinds; a turret's `"Look"` names one of the three looks Arsenal registers with `OrdnanceTurrets.RegisterLook` (`Arsenal:turret-burst`, `Arsenal:turret-sniper`, `Arsenal:turret-shotgun`; code in `Native/ArsenalTurrets.cs`, which also plays the deploy, idle and expire sounds for sentries wearing those looks). Code exists only for the two kinds Arsenal adds: `arsenal.Strike` and `arsenal.HeavyWeapon`.

## Layering: what this package holds and what it does not

Arsenal is the example content pack: strikes and area bombardments, heavy weapon drops, supply pod contents and three turrets (models, looks, sounds), with their definitions, icons and sounds. It uses only Ordnance's public API and the four libraries. To add your own turret or supply, copy its approach in a separate mod; Arsenal does not need to change. If a general mechanism is missing, add it to the layer below.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Rules with no game reference: payload parsing, strike patterns, target choice, the beam's route, the heavy weapon loan rules, network records. |
| `Native/` | The plugin: payloads, host logic, effects on every peer, heavy weapon loan and launcher. |
| `ForgeOrdnance/arsenal.json` | The 20 shipped definitions. Ordnance reads every `ForgeOrdnance` folder under `BepInEx/plugins`. |
| `ForgeOrdnance/lang/` | The definitions' names and descriptions in each language (`<Id>.Name`, `<Id>.Description`). |
| `lang/` | Arsenal's own words (the rack prompt) in each language. |
| `Assets/Audio/cues.json` | ForgeAudio cue settings. The converted Ogg files beside it are local build outputs. |
| `tests/Core/` | Offline tests. They also load `arsenal.json` through Ordnance's definition parser. |
| `tools/` | Asset, icon, audio and packaging scripts. |

## Build and test

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeOrdnance_Arsenal/ForgeOrdnanceArsenal.csproj -c Release --artifacts-path artifacts/dotnet-arsenal -p:GTFOBepInExPath=<profile>/BepInEx --disable-build-servers -m:1
dotnet run --project Mods/ForgeOrdnance_Arsenal/tests/Core/ArsenalCore.csproj -c Release --artifacts-path artifacts/dotnet-arsenal
pwsh Mods/ForgeOrdnance_Arsenal/tools/build_assets.ps1
pwsh Mods/ForgeOrdnance_Arsenal/tools/build_icons.ps1
python Mods/ForgeOrdnance_Arsenal/tools/package_release.py
```

`build_assets.ps1` builds the model bundle with Ordnance's pipeline (Blender and Unity 2019.4.21f1). `build_icons.ps1` renders each call-in's picker icon in Blender (a supply shows the game's own pickups, one per item the definition delivers: `tools/extract_pickups.py` reads their meshes and textures from the game files into `artifacts/work/pod-native/meshes` (local only, needs UnityPy on `PYTHONPATH`), and the kinds and counts come from the `Item` payload, so a changed delivery re-renders into a changed picture; the shell and beam strikes that differ in the game only by colour are drawn in their BeamColour; dark models such as the weapons get `--exposure`: a front light and a higher view exposure), but only the ones missing from `Assets/icons/`; a file already there is never overwritten (`-Redo <file>` or `-Redo all` re-renders on purpose).

## Release package

`package_release.py` checks the manifest against Thunderstore's rules and writes `NAinfini-ForgeOrdnance_Arsenal-<version>.zip` to `artifacts/work/ordnance-release/`. It does not publish.

The zip holds `manifest.json`, `icon.png`, `README.md`, `CHANGELOG.md`, `LICENSE`, `ForgeOrdnanceArsenal.dll`, `forge-ordnance-arsenal.bundle`, `audio/` (Ogg files, `cues.json`, `License.txt`), `models/License.txt`, `lang/`, and `ForgeOrdnance/arsenal.json` with the 20 icons and `lang/` beside it; both `lang/` folders must hold `en.json` and `zh.json`. A manual install puts the same files in `BepInEx/plugins/NAinfini-ForgeOrdnance_Arsenal/`.

## Supplies and turrets

| Id | Kind | Contents (item data block ID) |
|---|---|---|
| `arsenal.supply.mixed` | `Item` | Ammo pack 101 x2, medipack 102, tool refill 127 |
| `arsenal.supply.ammo` | `Item` | 101 x3 |
| `arsenal.supply.medical` | `Item` | 102 x2, disinfection pack 132 |
| `arsenal.supply.tool` | `Item` | 127 x2 |
| `arsenal.supply.consumables` | `Item` | C-foam grenade 115 x2 |
| `arsenal.turret.watch` | `Gear` | Burst sentry 24 |
| `arsenal.turret.bulwark` | `Gear` | Sniper sentry 23 |
| `arsenal.turret.scythe` | `Gear` | Shotgun sentry 25 |

All three turrets use `AmmoRatio` 1, `Lifetime` 180, `Pickable` false, `DamageScale` 2.175, `FireRateScale` 1.15, `AmmoScale` 3, `RangeScale` 1.8, `TurnScale` 1.4 and `AcquireScale` 1.4.

Code prefixes: supplies start with up, down. Turrets start with left, up, right. Strikes start with right. Heavy weapons start with down, left. A test keeps these clear of the codes in Ordnance and Frames.

## Payload `arsenal.Strike`

```json
{ "Type": "arsenal.Strike", "Kind": "Area", "Damage": 180, "Shells": 7, "Spread": 15 }
```

Unknown fields, fields of another kind, non-numbers and values out of range are refused, and the log names the field.

| Kind | Fields (default) |
|---|---|
| `Precision` | `Damage` 600, `BlastRadius` 5, `Force` 800 |
| `Area` | `Damage` 180, `BlastRadius` 4.5, `Force` 500, `Shells` 7, `Spread` 15 (m), `Duration` 5 (s) |
| `Creeping` | `Damage` 160, `BlastRadius` 4.5, `Force` 500, `Shells` 3 (per row), `Rows` 5, `Length` 5 (m between rows), `Width` 10, `RowInterval` 1.2 (s) |
| `Cluster` | `Damage` 90, `BlastRadius` 3.5, `Force` 300, `Shells` 10, `Duration` 1.5, `Length` 20 (line across the throw) |
| `Incendiary` | Impact `Damage` 80, `BlastRadius` 3.5, `Force` 300. Area `ZoneRadius` 6, `Duration` 10, `Tick` 0.5, `TickDamage` 3 |
| `Gas` | `ZoneRadius` 7, `Duration` 12, `Tick` 0.5, `TickDamage` 2.5 |
| `Stasis` | `ZoneRadius` 8, `Duration` 20, `Slow` 0.35 (share of its own speed an enemy keeps, 0.05 to 0.95) |
| `Beam` | `Spread` 18 (how far the beam may walk from its start), `Duration` 10, `Tick` 0.2, `TickDamage` 6, `Speed` 4 (m/s) |
| `Hunter` | `Spread` 25 (search radius), `Damage` 2000 (to the target), `TickDamage` 60 (splash), `BlastRadius` 3, `Force` 800 |

How a strike runs:

- The host builds the shell pattern from the summoner and throw, drops each point onto the floor, and applies damage when each shell lands. Every shell falls for 1.8 s.
- Gas and incendiary areas start 0.8 s after landing, stasis 1.0 s. The beam charges for 1.5 s before it moves, and `Duration` counts from then.
- The beam only picks enemies it can reach on the navigation mesh (a complete path no longer than `Spread`) and see from above. It follows that path at constant speed, checks every move against the level, drops a target it can no longer reach, and holds still when none is left.
- Hunter picks the living enemy with the highest maximum health within `Spread` (nearer wins ties).
- Stasis slows enemies in the field to the `Slow` share through ForgeCore `Enemies.Slow` (it scales the animator speed): one lease per enemy per field, taken while the enemy is inside, ended when it leaves, when the field ends, or when the level, a checkpoint or the host changes. Overlapping fields take the slowest share and do not stack; ForgeCore puts the enemy's own speed back when the last lease ends or the enemy dies or is reused, and leaves a speed the game set meanwhile alone. Arsenal keeps the share, radius, duration and the rule that enemies held by C-Foam are skipped. Arsenal patches no enemy lifecycle method itself.
- Explosions use the game's explosion damage, so falloff, wall blocking and friendly fire follow the game. Fire and gas use the game's fire damage entry.
- Every peer draws shells, blasts, areas and the beam from data it already has; effects send no messages. Arsenal owns what they look like (sheets, colours, sizes, timings, the recipe of each moment in `Native/StrikeFx.cs`, the clouds and the beam in `Native/StrikeEffects.cs`) and ForgeVisual draws them: blasts, trails and cloud puffs are sprites of its shared pool (`SpritePool`; the sheets are registered as `Arsenal:smoke`, `Arsenal:fireball` ... when the bundle loads, and smoke is the sheddable sheet the overdraw budget gives up first), scorches and beam burns use its decal rings (`SpritePool.CreateDecals`), short flashes and the area and beam lights its shared effect lights `PointLights` (flashes and held lights; with Arsenal's `Extra lights` off none are drawn), the shell landing shake its `CameraShake`, shell trails `Trails`, area ground marks and the beam's reticle `GroundMarker` (gas `Hatched`, fire `Flames`, stasis `Dial`, beam `Reticle`), the beam's lines `Beams`, and the grenade burst is the game's own `Game:FX_Tripmine` played with `Fx.Play`.

## Payload `arsenal.HeavyWeapon`

```json
{ "Type": "arsenal.HeavyWeapon", "Model": "Arsenal:heavy-mg", "Gear": 38, "AmmoRatio": 1 }
```

| Field | Meaning |
|---|---|
| `Model` | A ForgeVisual model, `Pack:item`. Any pack works, so a third party adds a heavy weapon with its own pack and a definition (the general steps are in [Writing a content pack](../ForgeOrdnance/DEVELOPERS.md#writing-a-content-pack)). Checked when the definition file is read, not at the first shot: the pack must already be registered (in the owning plugin's `Load`, before definitions are read) and the model needs the three nodes below, otherwise the definition is refused naming `Model`; a pack that is missing or lacks a node does not load the weapon in a lesser form, the definition simply is not loaded. A refused definition holds no hash. In the weapon list the lent weapon carries the name of the first definition (ordered by Id) that uses the model: Arsenal's own text for its three, else the definition's `OrdnanceApi.DisplayName`, the same on every peer. |
| `Gear` | Offline gear ID or name of a special-slot weapon. Shipped: machine gun 38 (`MachinegunAuto`), rifle 42 (`HELRifleSemi`), launcher 43 (`HELGun`: two-handed, 9 rounds, 3.3 s reload, no charge-up on the lent weapon, with its bullet replaced by a grenade). |
| `AmmoRatio` | 0.05 to 1, default 1. |
| `Grenade` | Optional `{ "Damage", "Radius", "Speed" }` (defaults 140, 4 and 32 m/s; unknown members are refused). With it the weapon fires grenades instead of bullets, host-checked as described below; without it the weapon fires its own bullets. |

Model contract (the shipped models are `Arsenal:heavy-mg`, `Arsenal:anti-materiel-rifle` and `Arsenal:grenade-launcher`; those names are stable): the root carries empty nodes `RightHand` (grip), `LeftHand` (foregrip) and `SightLook` (the centre of the sight window, forward = the model's front), which `Models.Dress` and the aim use. The lent gear's public name is `Forge Heavy ` and the model id's 32-bit FNV-1a hash in 8 hex digits (20 characters; the repository's other markers assume about 25 at most), filled in when a definition is accepted; two ids with the same hash are refused naming both. The rack record carries the same hash, and a record whose hash this install never accepted is dropped with one error line (every peer needs the same heavy weapon packs).

How it works:

- The weapon stands upright in one of the pod's two weapon bays (chosen by throw ID, the same on every peer).
- A player within 2.4 m, looking at the weapon, gets an interaction prompt. The use key makes the claim through Ordnance's `OrdnancePod.AddClaimable` (reach, aim, prompt, request, reservation, timeout and messages are Ordnance's); Arsenal supplies the prompt text, the host check (one loan per player, no borrowed gear, effects ready), the install, and the host verification that builds the loan. Committing starts the loan; Arsenal's `LoanBook` keeps loans and stashes, and returns travel as a small `LoanNotice` on Arsenal's own channel.
- The loan saves the player's special weapon and ammo, equips the lent gear with a marker name (the packet name stays the marker; `ArsenalText.NameHeavyWeapon` registers each marker with `UIKit.GearName`, so ForgeUI shows the call-in's name from `ForgeOrdnance/lang` on this player's weapon list), and fills the ammo to `AmmoRatio`. The payload may set absolute archetype numbers (`Damage`, `ShotDelay`, `Cooldown`, `Chargeup`, `Magazine`, `Reload`, `PrecisionMultiplier`, `StaggerMultiplier`, `Pierce`, `AmmoCost`, and `FalloffStart` / `FalloffEnd`, the metres where the damage falloff begins and bottoms out, written together with the end beyond the start; unset ones stay the game's; the grenade launcher fires no bullets and ignores both falloff fields; a rack record is now 88 bytes, so every peer needs the same build): `HeavyWeapons.Tune` gives the lent instance its own copy of the archetype with them applied and reads it back (the shared block, and the player's own gun of the same model, are untouched), and the rack record carries them to every peer. The host's launcher budget and cadence come from the same `ArchetypeTuning.Over` of the vanilla block. It ends when the clip and reserve are both empty (the heavy weapon is gone), on the drop key, or when the player goes down; the own weapon is then put back with its saved ammo.
- Switching: with the special weapon in hand, `InputAction.SelectSpecial` (the player's own binding, not while reloading, with a 0.5 s cooldown) swaps through the game's own `EquipLocalGear`, so the swap replicates the usual way. The heavy weapon's magazine and reserve wait in a stash, the own weapon's in the loan's restoration record; each is written back once its gear has assembled in the slot. The switching peer tells the host (`ClaimAction.Stashed`) before the swap; `ClaimBook.Observe` then keeps the loan while the own weapon is in the slot and takes the heavy weapon back once the marker returns. The stash never survives a checkpoint reload, a host change keeps it, and drop, down and level end clear it.
- Ammunition packs: a Harmony pair on `PlayerAmmoStorage.PickupAmmo` swaps the pool for the saved own weapon's reserve around the game's pickup while the heavy weapon holds the slot, so the game's caps and rounding apply to the own weapon and the heavy weapon's pool is put back untouched.
- Grenades need a host-issued loan and an increasing shot number. The host checks the held weapon, the origin, the fire rate and the remaining ammo, then launches from its own view of the player. Clients only predict flight. The host's burst position decides the explosion for everyone.
- Checkpoint reloads and host changes revoke launcher firing. Installed loans keep their recovery data and can be returned.

## Settings

`BepInEx/config/NAinfini.ForgeOrdnanceArsenal.cfg`: `Presentation / Extra lights` (on). The give-back key is the ForgeUI key action `DropHeavyWeapon` (`Native/ArsenalKeys.cs`, G, context `on-foot`), changed in the game's settings (Controls) and saved in `NAinfini.ForgeUI.keys.cfg`.

## Audio

`audio/cues.json` follows the ForgeAudio format. Each cue lists layers of Ogg clips, a bus and a length cap. The four boom cues (small boom, big boom, cluster split, grenade burst) have their own far layer from 40 m. Sound sources and licenses are in `Assets/Audio/License.txt`.

Loops (fire, gas, stasis and beam) are attached to their owner and fade out in 150 ms when it is destroyed. A cue that fails to load is logged by name and only that cue goes silent.

`tools/build_audio.ps1` rebuilds the clips from `sources.json` using ForgeOrdnance's `stage_audio.py` and ForgeAudio's converter. No Unity build is needed for audio.

## Art

`forge-ordnance-arsenal.bundle` holds the models (the three turret looks included) and the strike effect textures; it is registered and loaded through ForgeVisual (`Bundles.Register`, `SpriteSheet.Register`), and the heavy weapon models are put on the game's gear with `Models.Dress`. Audio is never put in the bundle.

| Prefab | Model |
|---|---|
| `shell` | 155 mm artillery shell |
| `bomblet` | 40 mm VOG-25P grenade |
| `gas-canister` | Sci-fi gas canister |
| `stasis-emitter` | Stasis emitter |
| `heavy-mg` | M249 with an open reflex sight on its top rail (carry and charging handles taken off), the ammo pouch is the separate `Magazine` |
| `anti-materiel-rifle` | Barrett M82 with an open reflex sight on its top rail, the box magazine is the separate `Magazine` |
| `grenade-launcher` | RG-6 revolver grenade launcher, open reflex sight on its top tube |

Each heavy weapon model has the empty `SightLook` at the centre of its sight's window (identity rotation, so its forward is the model's front) and a small emissive `reticle` there. When the look goes on a weapon, `HeavyWeapons` points the gear's `GearPartHolder.m_aSightLook` and `ItemEquippable.SightLookAlign` at it, so aiming lines the camera up with the model's own sight instead of the hidden game sight; the game's nodes go back before the look comes off. A model without `SightLook` logs an error and aims with the game's sight. The sight has no glass (the gear shader is opaque), so its window is open. `tools/fit_sight.py` puts the sight on a gun master (Blender, background): it cuts the sight's own base plate and lever block away, stands the housing straight on the highest part of the gun under it (no riser), stops when any part of the gun shows through the window from 0.3 m behind it (it prints the `import_body.py --remove` boxes that take that part off), and adds the reticle and `SightLook`; the arguments used for M08–M10 are in its header. The three gun bodies are textured Sketchfab downloads turned into masters by `tools/import_body.py` (front −Y, grip at the origin, the bore on x = 0, scope parts removed, reduced to about 19,000 triangles when larger; `--split` makes the separate `Magazine` object and `--socket` the `RightHand` and `LeftHand` palm points). `tools/render_preview.py` renders the review pictures (side, top, rear, hip, aim through the sight, the magazine pulled out); the gear shader has no occlusion input, so the occlusion is multiplied into the base colour, and the normal and metallic/roughness maps are kept.

Authors and licenses (mostly CC BY 4.0) are in `Assets/Models/License.txt`, which ships in the package. Effect textures are CC0. Every definition names an icon `<group>-<name>.png` that sits beside `arsenal.json`. The icon source is `Assets/icons/` (local media, not in Git): the owner's hand-made icons go there as `<group>-<name>.png`, 256 x 256 transparent PNG, and replace the rendered ones; the package script reads this folder.

## Network

Arsenal's own messages (loan notices, grenade shots and bursts) go through its ForgeCore channel, whose message format fingerprint ForgeCore computes. A player with other Arsenal messages is refused by both handshakes (an error in each player's log) and cannot call. Damage goes through ForgeCore's `Combat` (the game's explosion and fire damage requests), area queries through `Enemies.Near`, players by network lookup through `Level.PlayerByLookup`, the floor under a point through `Level.FloorBelow`, the heavy weapon swap through `Gear.EquipLocal` / `Gear.WriteLocalAmmo`, and the special weapon slot lease through `Gear.SpecialSlot` (the one lease an exosuit's gun from Frames also takes).

## Logging

Logging writes errors only: `Plugin.Error` goes to ForgeCore's `ModLog.ErrorOnce`, which writes each exact message once. There are no other log lines and no diagnostics setting.


How a heavy weapon model sits in the hands (ForgeVisual `Models.Dress` with `DressOptions.Gun`, `GunNodes`): the model's `RightHand` and `LeftHand` sockets (palm points of the pistol grip and the fore grip) are put on the game gun's own hand nodes (`ItemEquippable.m_rightHandGripAlign` / `m_leftHandGripAlign`, else the part holder's `m_aRightHand` / `m_aLeftHand`) the hands are IK-aimed at: scaled so the two are as far apart along the barrel as the game's (0.7 to 1.4), the right one on the game's along the barrel, both at the same mean height, the bore (`muzzle`) on the game's bore across, so the three guns sit alike whatever hangs off one side. The model's `Magazine` is hung on the game's magazine node (`GearPartHolder.m_aMag`), so the game's reload animation moves it; the game's own meshes are hidden with the renderer forced off (the game switches renderers back on at the end of a reload) and the gun's animators always update, since the nodes only move while they do. A model without the two hand sockets, or a game gun without its hand nodes, is not dressed (one error in the log).

