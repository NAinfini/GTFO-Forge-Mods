# Forge Weapon Energy Lab: developer notes

Player documentation is in [README.md](README.md). This file covers how the guns are wired into the game and the extension API.

## Layering: what this package holds and what it does not

EnergyLab is energy-weapon content: the eight guns' numbers, firing and hit rules, effects, models, sounds and icons. Firing, hits and damage follow the vanilla path; sound goes through ForgeAudio, effects and models through ForgeVisual, nearby enemies, collider range queries and data blocks through ForgeCore. A new energy weapon is a definition file plus a model bundle (see Definition files); the eight shipped guns are exactly such a file and get no other path. A general mechanism it needs goes into the layer below, not a second copy here.

## How the guns run

- Every discharge uses one vanilla firing call, spends one round, and keeps vanilla shot counting, firing animations, empty-magazine feedback and reloads. Beam and Flamethrower have no per-shot camera or first-person model kick; the other six keep their donor recoil pattern at one third strength, including the separate first-person movement curves, with curve timing and random multipliers unchanged.
- Every enemy hit follows vanilla bullet rules for back damage, weakspots and armor: the incoming energy direction and the first limb reached by its physics ray determine the bonus. Beams and flame travel from the muzzle, chain hops from the previous target, moving contacts along the projectile's current velocity (including disc bounces), and blasts and field pulses outward from their centre.
- Each discharge has one counted shot ray. Beam hits, every chain hop, and flame targets occur inside the same firing call for hit-rate mods. The shooter's game deals these instant hits and sends the resolved effects to other players.
- Orbs, discs, Blast Gun shells and gravity cores launch during their firing call. The host simulates their contacts, explosions, bounces and pulses after that call has returned, so these delayed hits do not count towards hit rate. Damage statistics follow each stats mod's normal attribution rules for hits outside a firing call, including after weapon switches. A client spends its round locally and sends one launch command; the host never bills it again or fires a gun to report a later hit. Bots launch on the host through their vanilla synced firing calls. The Blast Gun shell is drawn from the muzzle on every peer, moves by `Speed` (default 40 m/s) each frame with a 5 cm sphere sweep, and explodes at the first terrain, enemy or player it touches (up to 64 shells at once, nothing allocated per frame). The host deals the explosion damage (the shooter's damage with boosters, sent with the launch), noise, force and a zero-damage contact on the limb struck; other peers only draw. A shell whose shooter has died or left is dropped.
- What other players see never rests on one message. Everything that flies is simulated from the shooter's reported start on every peer, so it lands where the host deals it; what starts at the gun (flash, sound, a held beam, flame or charge) is drawn from the muzzle that machine shows, every frame. A held beam or flame is one Lossy message per shot and ends 0.3 s after the last; a charge has no shots, so the shooter repeats it every 0.5 s with how long it has been held, and another machine ends it 1.5 s after the last repeat (a reliable stop ends it at once, and the host relays a stop even when the shooter has already switched guns or gone down). A launch that a stalled link delivers while its slot is still taken drops that shot, not the gun.
- On join, ForgeCore's channel handshake compares ForgeCore's message format fingerprint and a fingerprint of the registered definitions, effective tuning, native IDs, model keys, bundle contents and the mod build; a player who differs is refused on both sides (an error in each log, a chat line) and their energy messages are dropped. Damage and explosions go through ForgeCore's `Combat` (the game's own bullet and explosion requests) and the new gear's data blocks through ForgeCore's add-only `DataBlocks`. Every collider-level sphere query (spawn and disc contacts, flame cone, gravity field, arc and explosion targets) is a ForgeCore `ColliderQuery` of 512 colliders (128 for the Blast Gun's shells) owned by its call site, with the layer mask and trigger mode each query always had; a full buffer is `Truncated` and the query is refused before any damage or force is submitted: the shot, projectile or blast that needed it is dropped with one error (`BudgetExhausted`) and the gun keeps working. Dedupe, nearest-first order and visibility stay in this package.

## Sounds and effects

- The gear packet's public name is the definition's `Name` on every machine. `EnergyText.Register` (at every gear installation) registers each gun's translated name and description (from the `lang` folder beside its definition file) and the default description with ForgeUI (`UIKit.GearName`, `UIKit.GearText`), which shows them in each player's language on the lobby gear slots, weapon window and in-level weapon list. The datablock names and descriptions (`ArchetypeDataBlock`, `GearCategoryDataBlock`) are still written here on install and on a language change.
- Sounds are the cues of `audio/cues.forge` (Ogg Vorbis files beside it) played through ForgeAudio: the `energy` bus is on its weapon category and the `boom` bus (the two explosions) on its explosion category, each cue has its own near and far distance, and wall muffling is off. Both buses carry gain 2, the boost the earlier built-in mixer gave every clip. ForgeAudio owns voices, placement, the game's volume sliders and the shared mix; `Presentation/EnergyAudio.cs` only names the cues; each slot is a ForgeAudio `AudioVoice` (`EnergyAudio.Library.Voice(anchor)`, a cue riding on a transform). Plugin start loads the library and the preload waits until it has decoded, then `Require`s every cue in `EnergyAudio.Cues`, so the guns stay disabled, with an error in the log, when one is missing.
- Every effect is a slot of the effect library (`Presentation/EnergyVfx.cs`, played with ForgeVisual's `Fx`); the ricochet disc's model is a Unity bundle embedded in the DLL and registered with ForgeVisual as the pack `EnergyLabDisc`; the point lights come from its `PointLights`. A slot or a disc that cannot be prepared logs its error and is not drawn; it never keeps the guns from firing.

## Gameplay rules

- Friendly fire and lock breaking follow vanilla rules: direct hits behave like bullets, and explosions behave like mines.
- Beam, Chain Arc, Flamethrower, Blast Gun, Ricochet Disc and Gravity Core use vanilla full-auto timing. Charged orbs reach full power after 1.1 seconds, stay fully charged while the trigger is held, and fire on release, with at least 0.85 seconds between shots. A charge cannot start inside the 0.85-second cooldown. Active-projectile limits pause further launches until a slot is free (Blast Gun shells have 64 slots); a launch the shooter's game already admitted replaces the oldest projectile of its kind instead of vanishing. The shooter's game announces where every shell, orb, disc and gravity core ends (`flight-end`); other players' simulations hold at their own stop point for at most 0.6 seconds and finish at the announced position, so every peer shows the same endpoint.
- A per-shot hit rate counts each discharge once; a per-hit rate counts every beam hit, chain hop, flame target and Blast Gun contact, and stats mods that attribute hits outside a firing call also count the Blast Gun's explosion hits, so it can pass 100% with these guns. Blast Gun explosions fall outside the firing call.
- Stock tracers, gunfire sounds, muzzle flashes and casings are disabled for all eight guns; their energy effects and sounds remain.
- Explosions hit each enemy once, on the first limb reached by a ray from the blast centre toward its nearest surface, with distance falloff. Mother and Tank spore pods in range can burst.
- Chain hops, shock pulses and the gravity pull select enemies only.
- Shots alert sleepers like vanilla gunfire, using energy-weapon sounds; explosions also use vanilla mine noise.

## Full default stats

Ammo costs are magazine rounds. Damage is the base value before vanilla receiver modifiers; explosions have distance falloff. Range describes aiming or direct hits: only the Chain Arc's first hit and the flame have a tuned range, every other shot reaches 1000 m, farther than any level sightline, with no distance falloff. Projectile travel follows its flight limits. Charged ranges run from minimum to full charge. Effect duration is the explosion's lifetime, not repeated damage. The Blast Gun's contact adds no separate damage; its damage comes from the explosion. Both orbs use 16 reserve units per round, at any charge power.

| Weapon | Slot | Magazine | Ammo / shot | Interval / charge (s) | Damage | Range (m) | Radius / duration | Bounces / targets |
| --- | --- | ---: | --- | --- | --- | --- | --- | --- |
| Forge Beam | Main | 40 | 1 | 0.1 | 4.35 / tick | Unlimited | - | 1 / tick |
| Forge Chain Arc | Main | 7 | 1 | 0.7 | 15.9 first hit; 80% retained / hop | 16 first hit; 6 / hop | - | 4 total |
| Forge Flamethrower | Main | 50 | 1 | 0.1 | 2.4 / tick / receiver | 8 | 0.15-1.43 m cone radius | All visible receivers in cone |
| Forge Charge Orb | Special | 4 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / explosion | Unlimited aim; 18-42 travel | 1.5-2.5 m blast; 0.8 s effect | All in blast |
| Forge Shock Orb | Special | 5 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / contact; 0.96-5.016 / pulse | Unlimited aim; 18-42 travel | 2-3.5 m pulse; every 0.4 s | Pierces actors; 3 enemies / pulse |
| Forge Blast Gun | Special | 6 | 1 | 0.45 | 30.13 / explosion | Unlimited; shell 40 m/s | 2.2 m blast; 0.4 s effect | All in blast |
| Forge Ricochet Disc | Special | 16 | 1 | 0.55 | 23.95 / hit | Unlimited aim | 0.3 m disc; 1.6 s flight | 4 wall bounces; 1 hit / actor between bounces |
| Forge Gravity Core | Special | 2 | 1 | 2 | 9 core / 2.25 edge / pulse | Unlimited aim | 5 m field (0.8 m core); 3.5 s incl. 0.35 s harmless collapse; pulse every 0.4 s | Visible enemies in field |

## Tuning files

`BepInEx/config/ForgeWeapon_EnergyLab/tuning-defaults.forge` is rewritten on every launch with the values of every loaded weapon (shipped, data-file and plugin-registered) and a `_descriptions` object explaining each field; it is never read. `tuning-overrides.forge` holds only changed values: a `weapons` object keyed by weapon ID, each entry naming any subset of that weapon's fields. Every value it does not name follows the weapon's own values, so a new version's default changes reach players who tuned other values. It is applied when registration closes (the first game-data gear installation), so it also overrides `SetTuning` and data-file `Tuning`.

Distances are metres, times are seconds, and shot costs are whole magazine rounds; `ammoPerRound` controls the native reserve-ammo cost of one magazine round. Keep `ammoCost` at 1 for every weapon: each native discharge spends one round, including a fully charged orb.

A weapon entry with an unknown or duplicate field, an incorrect number type, a non-finite or out-of-range value, or an inconsistent charge, ammo or radius setting is skipped with one log line naming the field, and that weapon keeps its own tuning. A weapon ID that is not loaded (a content pack the player removed) gets one line and is skipped. A file that is not valid JSON, or has an unknown top-level member, is ignored whole with one line. Energy Lab itself never switches off over this file. There is no live reload. Formatting does not affect the multiplayer compatibility check; the effective gameplay values do.

## Building from source

The plugin builds with the .NET 6 SDK against a GTFO r2modman profile that has BepInExPack_GTFO and GTFO-API (the separate GTFOModding-GTFO_API package, under `plugins/GTFOModding-GTFO_API/`) installed and has been started once, so its `BepInEx/interop` folder exists:

```
dotnet build ForgeWeapon_EnergyLab/ForgeWeaponEnergyLab.csproj -c Release -p:GTFOBepInExPath=<profile>/BepInEx
```

`GTFO_BEPINEX_PATH` can be set instead of the property. All media of this package is local-only; Git tracks only its licences, manifests and `audio/cues.forge` (`AGENTS.md`). The build needs these local files under `ForgeModsRoot` (the `Mods/` folder that holds them; pass `-p:ForgeModsRoot=<path>/Mods/` when building from another checkout): `Assets/Vfx/forge-energy-disc.bundle`, which is embedded into the DLL, so the build fails without it; the eight `Assets/Models/forge-energy-model-*.bundle`, copied beside the DLL into `ForgeEnergyLab/`; and the Ogg files in `Assets/Audio/ogg`, copied with `audio/cues.forge` into an `audio` folder beside the DLL. A build without the model bundles or the Ogg files succeeds, but in game a gun whose model bundle is missing is skipped with one log line, and with a missing cue the guns stay disabled with an error in the log; ship those folders with the DLL. The build references `../ForgeCore`, `../ForgeUI`, `../ForgeAudio` and `../ForgeVisual`, and copies `lang/en.forge` and `lang/zh.forge` (the default description and the mismatch chat line) and the `ForgeEnergyLab` folder (`energylab.forge`, its `lang/` and the eight model bundles) beside the DLL; ship those folders with the DLL. The gear's public name (`pGearIDRange.publicName`) is part of the synced gear packet and of saved loadouts, so it stays the definition's `Name` on every machine; the loadout picker, the lobby gear slots and weapon window, and the in-level weapon list draw the current language's name in its place on each player's own screen.

To rebuild them:

- **Effects**: the owner picks library effects for the slots of `Assets/Vfx/fx-slots.forge`; `Tools/Vfx/FxLibrary/fx_slots.py` (see its header) converts the picks and builds `forge-energylab-fx.bundle` and `fx-slots.forge` for this package. Only the ricochet disc has its own bundle (`forge-energy-disc.bundle`, below). The bundle and slot files are local media; Git tracks the slot table, licences and manifest.
- **Models and the disc**: `tools/build_presentation.ps1` exports parts (and their reduced third-person meshes) with Blender, builds the eight model bundles and the disc bundle with Unity 2019.4.21f1. Icons are not shipped: the game draws them from the models. Its input parts library is local-only and not published, so follow the bundle format under Extension API for your own models.
- **Audio**: `tools/import_audio.py` (Python 3, numpy, FFmpeg with libvorbis on `PATH`) turns `<weapon>-<event>-<n>.wav` recordings into 48 kHz mono Ogg Vorbis files in `Assets/Audio/ogg`, one per cue of `audio/cues.forge`. The recordings are not in this repository; only cues present in the source folder are replaced.

## Definition files

A third party adds guns with data alone. Writing and shipping a content pack (folder layout, Thunderstore package, rundown `CustomPath`) follows the general steps in [../ForgeOrdnance/DEVELOPERS.md](../ForgeOrdnance/DEVELOPERS.md); this section is only what is specific to Energy Lab.

Energy Lab reads every folder named `ForgeEnergyLab` (any case) under `BepInEx/plugins` (found through ForgeCore's `ContentFolders.Find`) and the active rundown's `CustomPath/ForgeEnergyLab` (MTFO). Each `*.forge` file directly in it is an array of definitions (JSON with comments and trailing commas allowed; `Settings.forge` is the one exception, see Rundown rules). The eight shipped guns are `ForgeEnergyLab/energylab.forge` of this package and load by the same code as any other file.

| Field | Meaning |
| --- | --- |
| `Id` | Required. Stable lowercase namespaced identity: at least three dot-separated segments of `a-z`, `0-9`, `_`, `-`, such as `author.pack.weapon`, at most 128 characters. Never change it: saved loadouts and the native IDs derive from it. |
| `Name` | Required. The gear packet's name, shown as written to every peer (at most 128 characters; the game's gear name field fits 25). |
| `Description` | Optional loadout description; without it the default text is shown. |
| `Slot` | Required. `Main` or `Special`. |
| `Mode` | Required. One of the firing behaviours: `Beam`, `ArcChain`, `Flame`, `PlasmaBlast`, `PlasmaArc`, `Blast`, `Disc`, `BlackHole`. |
| `Model` | Required. `{ "Key": prefab name in the bundle, "Bundle": path of the bundle file relative to the folder of this file }`. An absolute path or any `..` step is refused. See Extension API for what a bundle holds. Instead of both, `{ "From": "other.gun.id" }` takes the whole model of another gun (any gun in a `ForgeEnergyLab` file; a plugin registers its guns only after the files are read, so a plugin's gun cannot be named; a plugin passes its own `WeaponModel`), so a variant needs no copy of the bundle; an unknown Id is named in the log and the gun is skipped. |
| `Tuning` | Optional. Fields of the `Mode`'s tuning record, camelCase as in `tuning-defaults.forge`; unnamed fields keep the behaviour's defaults. Range and consistency rules are those of the tuning file. |
| `Look` | Optional. Other sounds and effects for this gun only: `{ "Sounds": { cue: sound }, "Fx": { slot: effect } }`. See Look. |

Texts: a `lang` folder beside the definition file holds `<language>.forge` files (`en.forge`, `zh.forge`) with `<Id>.Name` and `<Id>.Description` keys; those are shown in the player's language instead of `Name` and `Description`, on the player's own screens only (the packet keeps `Name`). A `<Id>.Name` or `<Id>.Description` key always wins over the file's field, and a `<Id>.Description` key applies even when the file wrote no `Description`. Guns are listed in registration order: definition files by folder path, then file path, then array position; the multiplayer fingerprint sorts by Id, so the list order never affects it. ForgeUI replaces by text, so the first gun in list order that holds a name or description text claims it; a later gun holding the same text is skipped with one log line, never refused, and shares that translation on screens already drawn (its datablocks still carry its own text). Give each gun its own text to translate them separately.

```json
[
  { "Id": "example.pack.longbeam", "Name": "Example Long Beam", "Slot": "Special", "Mode": "Beam",
    "Model": { "Key": "beam", "Bundle": "models/long-beam.bundle" },
    "Tuning": { "magazine": 24, "ammoPerRound": 4, "interval": 0.2, "damage": 18 } }
]
```

A broken entry is skipped alone: the log gets one line with the file, the Id, the field and the reason, and the other entries and files still load. A repeated Id, or two Ids whose derived native IDs collide (the line names both), keeps the first and refuses the second. The bundle file itself is checked when game data installs (present, 1 KiB to 128 MiB); a gun without it is skipped with a line. Every player in a lobby needs the same definition files: the join check compares definitions, tuning and bundle contents.

## Rundown rules (`Settings.forge`)

A rundown author limits which energy guns players may pick and sets the guns' numbers in one hand-written file, `CustomPath/ForgeEnergyLab/Settings.forge` of the rundown (the site writes nothing here; there is no template). It is read only from the rundown's folder; one found in a plugin folder is ignored with a log line. Comments and trailing commas are allowed.

```json
{
  // Guns players may choose in every level that has no list of its own. Omit or null: every gun.
  "Allowed": ["forge.energylab.beam", "forge.energylab.blast", "example.pack.longbeam"],

  // Fields of a gun's own Tuning, keyed by gun Id (same names and checks as a definition's Tuning).
  "Tuning": { "forge.energylab.beam": { "damage": 6, "magazine": 40 } },

  // Per level, keyed by the expedition key (ForgeCore Level.ActiveKey, for example Local_31_TierA_0).
  // A level that writes no Allowed offers what the rundown offers.
  "Levels": {
    "Local_31_TierA_0": { "Allowed": ["forge.energylab.beam"] },
    "Local_31_TierA_1": { "Allowed": [] }
  }
}
```

| Member | Meaning |
| --- | --- |
| `Allowed` | Ids of the energy guns players may choose. `[]` offers none; null or absent offers all. Only energy guns are ever hidden; every other gear is untouched. |
| `Levels.<key>.Allowed` | The same for one level; the key is the expedition's unique key. A level the file does not list offers what the rundown offers, with nothing logged (a level without its own list is a normal state, not an error); `EnergyControl.Offered()` shows the level key and what is offered, to check a key. |
| `Tuning.<Id>` | Tuning fields for one gun. Layers, lowest first: the definition's `Tuning`, then this, then the player's `tuning-overrides.forge`. `tuning-defaults.forge` keeps showing the definition's own values. |

Limiting goes through ForgeCore's one offer rule, `Gear.RestrictOffers`: refused guns leave the picker, and a saved or last-used choice of one is replaced by the slot's first offered gear (a slot left empty is refused with one error line). The rule is local; it does not stop another player's client. Errors are log lines, never silent: invalid JSON, an unknown member, a duplicate member or a wrong type ignores the whole file with the reason; an `Allowed` Id that is not a loaded gun is named; a bad `Tuning` field is reported and that gun keeps its values. `Allowed` and the effective `Tuning` count in the multiplayer compatibility check, so a host and a client with different files are refused.

## Look: other sounds and effects per gun

Without `Look` a gun plays and draws what Energy Lab ships. Each entry replaces one sound or effect of this gun only; a name that cannot be found is logged once and that sound or effect stays the shipped one. `Look` counts in the multiplayer compatibility check.

```json
[
  { "Id": "example.pack.longbeam", "Name": "Example Long Beam", "Slot": "Special", "Mode": "Beam",
    "Model": { "From": "forge.energylab.beam" },
    "Look": {
      "Sounds": { "fire": "my-beam-start", "beam-hit": "othermod.sounds/zap" },
      "Fx": { "beam": "MyPack:green-beam", "beam.hit": "Game:impact_sparks" }
    } }
]
```

- `Sounds` keys are cue names of the shipped sounds, or `fire`, which means the sound of this gun's shot (`beam-start`, `arc-fire`, `orb-fire`, `shock-fire`, `blast-fire`, `disc-fire`, `flame-start`, `gravity-fire` by `Mode`). The shipped cues are `beam-start beam-loop beam-stop beam-hit`, `arc-fire arc-loop arc-hit`, `plasma-charge-rise plasma-charge-loop plasma-charge-full plasma-charge-cancel`, `orb-fire orb-fly-loop shock-fly-loop orb-explode`, `shock-fire shock-fade shock-hit shock-pulse`, `blast-fire blast-cycle blast-explode`, `disc-fire disc-fly-loop disc-bounce disc-drop`, `flame-start flame-loop flame-stop flame-hit flame-crackle` and `gravity-fire gravity-fly-loop gravity-open gravity-close gravity-field-loop`.
- A `Sounds` value is a bare cue name, looked up first in the gun's own `audio/cues.forge` beside its definition file (a ForgeAudio library, Ogg files beside it), then among Energy Lab's own; or `identity/cue` for a cue of another loaded ForgeAudio library. Sounds play through ForgeAudio.
- `Fx` keys are effect slot names of `fx-slots.forge`, with or without the `energy.` prefix: `beam beam.muzzle beam.hit arc arc.hit flame flame.hit orb orb.charge orb.charged orb.trail orb.muzzle plasma.blast shock.orb shock.burst discharge blast blast.muzzle sparks disc disc.hit disc.launch hole hole.shell hole.launch hole.open hole.collapse`. A value is `Pack:item` of a bundle registered with ForgeVisual, or `Game:name` of a game effect. Effects play through ForgeVisual.
- An unknown key, a duplicate key or a value that is not text refuses that definition with one line naming `Look.Sounds.<key>` or `Look.Fx.<key>`. A flying projectile draws with the look of the gun last drawn on that machine.

## Extension API

Reference `ForgeWeaponEnergyLab.dll` and declare `[BepInDependency(EnergyLab.PluginId, ">=1.0.0")]`, naming the first version that has the API you use; the `EnergyLab.Version` constant is copied into your plugin at compile time and would pin the version you built against. The plugin GUID is **`NAinfini.ForgeWeaponEnergyLab`**.

The public namespace is `ForgeWeaponEnergyLab.Api`:

- `bool EnergyLab.Register(WeaponDefinition definition, out string? error)` adds a weapon using an existing `EnergyMode`, a Main or Special `WeaponSlot`, a matching tuning record, a `WeaponModel`, and optionally a last `WeaponLook? Look` (the same sound and effect names as the `Look` of a definition file, keyed by canonical cue and slot names such as `beam-start` and `energy.beam.muzzle`). It never throws: a refusal returns false with one line (field and reason). Data files go through this same call.
- `GetDefinition(id)` and `GetTuning(id)` return immutable snapshots. Use a record's `with` expression and `SetTuning(id, tuning)` to replace tuning before installation; it throws `InvalidOperationException` once registration is closed, `KeyNotFoundException` for an unknown Id, and the validation error for an invalid tuning.
- Tuning types are `BeamTuning`, `ArcChainTuning`, `FlameTuning`, `PlasmaBlastTuning`, `PlasmaArcTuning`, `BlastTuning`, `DiscTuning`, and `BlackHoleTuning`.

A weapon you register gets its own name, optional loadout description, slot, every gameplay number, and its model. Firing and hit rules come from the `EnergyMode` it reuses, and its sounds and effects too unless its `Look` names others, so any model can be paired with any of the eight behaviours.

Register weapons and replace tuning in your dependent plugin's `Load()`, before the first game-data gear installation. That installation closes registration: later `Register` calls return false and `SetTuning` throws `InvalidOperationException`; snapshot reads remain available. Players' `tuning-overrides.forge` is applied at that moment on top of everything registered. Invalid definitions, duplicate identities, and native-ID collisions are refused.

Use a stable lowercase ID with at least three dot-separated segments, such as `author.mod.weapon`, up to 128 characters. Native IDs of every weapon, shipped ones included, use FNV-1a 32-bit over UTF-8 `id + "/gear"`, `id + "/category"`, and `id + "/archetype"`. Gear and archetype IDs are `0x20000000 | (hash & 0x1fffffff)`; category IDs are `0x8000 | (hash & 0x7fff)`. Collisions are refused rather than assigned another ID; keep your identity stable for saved loadouts.

For your own model, pass `new WeaponModel(key, absoluteBundlePath)` (a data file's `Model.Bundle` is made absolute for you). Its icon needs no file: the game's own icon renderer draws the model (ForgeUI's `GearIcons` takes over the job; `Presentation/EnergyIcons.cs` supplies the model) with the camera, lighting, post material and framing of every vanilla gun, so the model's materials must use the vanilla gear shader as the built-in ones do. Bundles load when the first gun using them is assembled and unload after no gun object uses them, so ship one bundle per model to keep unused models out of memory. Supply a Unity 2019.4.21f1 AssetBundle containing:

- A `presentation-format` text asset with value `4`, a `presentation-profile` JSON text asset, and a prefab named by the model key.
- The parts `Receiver`, `Rail`, `Front`, `Stock`, `Magazine`, and `Sight`, plus the sockets `RightHand` (the grip; a model without it is held at its origin), `Muzzle` and `SightLook`; `LeftHand` is optional (ForgeVisual derives that hand, and a bare left socket no longer places it).
- Metre-scale Unity coordinates: +Z toward the muzzle, +Y upward, and the root origin at the right-hand grip. The profile describes the parts, socket positions, bounds, and `textureSets`; its `weapons[].mode` is the model key.
- Every energy model goes on through ForgeVisual's `GunMount`, in first and third person: its size, seat, both hands, the first-person hold and hip field of view are ForgeVisual's one gun-holding rule (`GunHold`, see its DEVELOPERS), whose size and hold were approved on these eight guns; aim positions and all hold rotations stay native. This package gives only the model, its sockets, the game hold each model follows (`Data/gun-grips.json`: `rifle-2` for the main guns, `machinegun-2` for the special guns; the mount keeps the model's own hand sockets (a missing right grip is its origin) and derives a missing left hand from that hold, and it pulls the left hand back along the gun into the arm's reach, so the table gives no hand points and only an optional `adjust` fine-tunes a hand), the ForgeVisual sight a model wears instead of its own optic (`Data/gun-sights.json`, ForgeVisual `GunSights`: the beam takes `scope-2x` on its rail in place of its holo-wedge, as it holds a line on far targets, and aims at the 2x field of view; the mount removes `Sight`, `SightGlass` and `SightReticle1`, so its reticle projection has nothing to move) and its own surfaces (`ApplyView`). Native guns and hand rigs keep their scale. Finger grip closure remains native.
- Body surfaces with their declared albedo/metallic textures and optional normal/emission textures; sight glass uses `glass` (not drawn: it only bounds where a mark shows, so the optic reads as an open frame with the target untinted), and sight marks use `reticle` surfaces.
- Optional reduced meshes: a body part whose profile entry has `reducedTriangles` above 0 ships a second mesh asset named `<model key>-<part>-reduced` that uses the same UVs and textures. The prefab's own mesh is drawn in first person and in icons; the reduced mesh in every other view (third person, teammates). The shipped models keep each gun at about 15 000 triangles in those views (`tools/lod_mesh.py`).

Definitions can share a model. All players need the same extensions.

This example registers a separate Special-slot beam using the shipped beam model's bundle from a plugin:

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeWeaponEnergyLab.Api;

/// <summary>A dependent plugin that reuses the built-in beam behaviour and model.</summary>
[BepInPlugin("example.energylab.extension", "Energy Lab API Example", "1.0.0")]
[BepInDependency(EnergyLab.PluginId, ">=1.0.0")]
public sealed class ExamplePlugin : BasePlugin
{
    /// <summary>Registers a distinct special-slot beam before Energy Lab installs game data.</summary>
    public override void Load()
    {
        var defaults = (BeamTuning)EnergyLab.GetTuning("forge.energylab.beam");
        var bundle = System.IO.Path.Combine(Paths.PluginPath, "NAinfini-ForgeWeapon_EnergyLab", "ForgeEnergyLab", "forge-energy-model-beam.bundle");
        if (!EnergyLab.Register(new WeaponDefinition(
                "example.energylab.longbeam", "Example Long Beam", WeaponSlot.Special,
                EnergyMode.Beam,
                defaults with { Magazine = 24, AmmoPerRound = 4f,
                    Interval = 0.2, Damage = 18f, Stagger = 0.25f,
                    ReloadTime = 3.3f, AimTime = 0.35f },
                new WeaponModel("beam", bundle)), out var error))
            Log.LogError(error);
    }
}
```

## Readback by name

`EnergyControl.Guns` (one line per loaded gun: id, slot, mode, model, the numbers a rundown or definition can set, and its Look) and `EnergyControl.Offered` (the energy guns the picker offers now, per slot, after the rundown's Allowed rule) are read-only and main-thread only. A driver with no keyboard calls them by name; see the signatures in `Native/Host/EnergyControl.cs`.

## Game hooks

Everything that more than one package reads is a ForgeCore `GameHooks` subscription, subscribed at plugin load (`EnergyGameHooks.Subscribe`): the four firing bodies (`GameHooks.Fire.*`, one frame each), the shared weapon ray (`GameHooks.AlignedRay`), the bullet hit, wield and unwield, parts spawned, gear spawned and reload. The first-person holder's `LateUpdate` (`GameHooks.FirstPersonHolderLateUpdate`) is subscribed the first time an energy gun is wielded, together with the bot shot gate, and acts only while the local player's energy gun is in the hand. The trigger check of automatic guns is not a Harmony patch (see `NativeEnergyShotReady`).

## Logging

Logging writes errors only (`Plugin.Logger.ErrorOnce(message)` through ForgeCore's `Log.For`; each exact message is written once). There is no Info or Warning output.
