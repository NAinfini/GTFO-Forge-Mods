# Forge Ordnance for developers

This file is for people who write call-ins, place them from a rundown, edit the shipped call-ins and suits, or build on the mod. Players should read `README.md`.

Forge Ordnance runs the call-in loop (code entry, beacon, host checks, delivery) and ships its own content on top of it: the Arsenal (supplies, turrets, standing devices, strikes, heavy weapons) and four exosuits. A call-in is a JSON definition. Content mods add definitions, and code mods add new call-in kinds.

The file goes in this order: what the package holds; the carrier, which is everything a third party builds on ([Writing a content pack](#writing-a-content-pack), [Writing call-ins](#writing-call-ins), [Extension API](#extension-api), [Network](#network)); [Arsenal content](#arsenal-content); [Exosuit content](#exosuit-content); [Building assets and the release package](#building-assets-and-the-release-package); [Tests](#tests).

## What the package holds

One assembly (`ForgeOrdnance.dll`), one plugin (`NAinfini.ForgeOrdnance`, in `Plugin.cs`) and one ForgeCore network channel, in three parts:

| Part | Folder | Namespace | Holds |
|---|---|---|---|
| Carrier | `Carrier/` | `ForgeOrdnance` | The call-in platform: call keys and codes, beacons and throwables, cooldowns and uses, network sync, drop pods, the turret drop shell, container-free drops, arrival impact, loadouts and lobby sync, the two built-in kinds `Item` and `Gear`, and the extension API (payload types, placing items in pods, in-pod pickup, a hook to re-skin vanilla sentry guns, category registration). |
| Arsenal | `Arsenal/` | `ForgeOrdnance.Arsenal` | Content: strikes and area bombardments, heavy weapon drops, supply pod contents, three turrets and four standing devices, with their definitions, models, looks, icons and sounds. |
| Exosuits | `Frames/` | `ForgeOrdnance.Frames` | Content: the four suit models, opening and closing, driving rules, suit guns, the cockpit layout, look, icons and text, the boot screen's shape and pacing, and suit sounds. |

`Plugin.Load` starts the carrier, then Arsenal, then the exosuits. A content part that throws while it starts is logged and the carrier and the other part keep working. The parts are one assembly and call each other directly. `Plugin.cs` hands each content part's payload types, the `Exosuit` category and the turret looks to the carrier's registries before the definition files are read (each part keeps the registered type it was given and sends with it, so a send looks no name up); the public `RegisterPayload`, `RegisterCategory` and `RegisterLook` are for other mods. `Plugin.cs` is the only file that names all three parts: neither content part names the other, and the carrier names neither. It also holds the mod's one handler of each of the game's moments (frame, late frame, game data ready, level built, entered and left, checkpoint reached and reloaded, host change, item spawn): each runs the carrier's steps, then Arsenal's, then the exosuits', and none of the steps of a part that failed to start. The frame handler reads the time and who hosts once and hands them down. The parts share the plugin's identity (one log, one set of key actions and words under one mod id), its network channel, its one checkpoint copy and its `lang/` files; each has its own sound set (`PartSounds`) and effect library.

Only the carrier's types listed under [Extension API](#extension-api) are public; the content parts add nothing public but their entries on the console type `OrdnanceControl` (see [Tests](#tests)). Concrete call-ins, models and sounds belong to the part (or the mod) that adds them: write definition JSON, and register a payload type only for new gameplay. To add your own turret, supply, heavy weapon or suit, do it in a separate mod; nothing here needs to change. If a general mechanism is missing, add it to the layer below (the carrier, or a Forge library).

Everything else comes from the Forge libraries: ForgeCore for networking, input, the special weapon slot lease, damage, nearby enemies, noise and logging, ForgeUI for every piece of interface, ForgeAudio for sound, and ForgeVisual for drawing in the world (models, particle pools, beam, ground mark, trails, glow); the plugin has no shaders, materials or line renderers of its own.

## Folder layout

| Path | Contents |
|---|---|
| `Plugin.cs` | The plugin entry: starts the three parts. |
| `Carrier/Core/` | Carrier rules with no game reference: definitions, loadouts, slot book, messages. |
| `Carrier/Native/` | The carrier in the game: controller, host logic, delivery, the built-in payloads, presentation, hooks. |
| `Arsenal/Core/` | Rules with no game reference: payload parsing, strike patterns, target choice, the beam's route, the heavy weapon loan rules, network records. |
| `Arsenal/Native/` | Payloads, host logic, effects on every peer, heavy weapon loan and launcher. |
| `Arsenal/Data/` | The heavy weapons' grip and sight rows for ForgeVisual, embedded in the DLL. |
| `Arsenal/Definitions/` | `arsenal.forge`, the 30 shipped definitions, and `lang/` with their names and descriptions in each language (`<Id>.Name`, `<Id>.Description`). |
| `Frames/Core/` | Rules with no game reference: the definition parser, host decisions, sprint heat, boarding timing, the power-on screen and plan, the cockpit's data, words and hide-and-restore state, network records. |
| `Frames/Native/` | Payload, network, world state, effects, the pilot, the first-person shell, the HUD, hooks. |
| `Frames/Definitions/` | `frames.forge`, the four shipped suits, and `lang/` with their names and descriptions. |
| `lang/` | The plugin's own words for all three parts (`en.forge`, `zh.forge`). |
| `Assets/<part>/` | One folder per part (`Carrier`, `Arsenal`, `Frames`): `Audio/` (ForgeAudio `cues.forge`, the source record and licenses), `Models/` (`LICENSE.forge`) and `Vfx/` (the effect slot table and its licenses). The bundles, receipts, Ogg files, icons (`icons/`) and cockpit art (`hud/`) beside them are local media and build outputs, not in Git. |
| `tools/` | `package_release.py`, and each part's asset, icon and audio scripts in `tools/Carrier/`, `tools/Arsenal/` and `tools/Frames/`. |
| `tests/Core/` | Offline tests of the three `Core/` folders. |
| `tests/tools/` | Python tests of the packaging script and the audio tools. |

Installed, everything is in `BepInEx/plugins/NAinfini-ForgeOrdnance/`, each part's files in its own folder:

```
ForgeOrdnance.dll  manifest.json  icon.png  README.md  CHANGELOG.md  LICENSE  NOTICE
lang/en.forge  lang/zh.forge
Carrier/forge-ordnance.bundle          Carrier/models/LICENSE.forge
Carrier/audio/cues.forge, LICENSE.forge, *.ogg
Carrier/forge-ordnance-fx.bundle       Carrier/fx-slots.forge  Carrier/fx-licences.forge
Arsenal/forge-ordnance-arsenal.bundle  Arsenal/models/LICENSE.forge
Arsenal/audio/cues.forge, LICENSE.forge, *.ogg
Arsenal/forge-arsenal-fx.bundle        Arsenal/fx-slots.forge  Arsenal/fx-licences.forge
Arsenal/ForgeOrdnance/arsenal.forge    Arsenal/ForgeOrdnance/lang/{en,zh}.forge  Arsenal/ForgeOrdnance/<icon>.png
Frames/forge-frames.bundle             Frames/models/LICENSE.forge
Frames/audio/cues.forge, LICENSE.forge, *.ogg
Frames/forge-frames-fx.bundle          Frames/fx-slots.forge  Frames/fx-licences.forge
Frames/hud/*.png
Frames/ForgeOrdnance/frames.forge      Frames/ForgeOrdnance/lang/{en,zh}.forge  Frames/ForgeOrdnance/<icon>.png
```

The definition folders are still named `ForgeOrdnance`: the carrier finds definitions by that folder name anywhere under `BepInEx/plugins`, which is also the rule for third-party content packs.

## Writing a content pack

The one recipe for a mod that adds call-ins to this platform. A whole plugin that adds a category, a call-in kind and a turret look:

```csharp
[BepInPlugin("author.MyCallIns", "My Call-ins", "1.0.0")]
[BepInDependency("NAinfini.ForgeOrdnance")]
[BepInDependency("NAinfini.ForgeCore")]
public sealed class MyCallIns : BasePlugin
{
    public override void Load()
    {
        var folder = Path.GetDirectoryName(typeof(MyCallIns).Assembly.Location)!;
        if (ForgeVisual.Bundles.Register("author.MyCallIns", "MyPack", Path.Combine(folder, "my-pack.bundle")) == null) return;   // ForgeCore logged why
        OrdnanceApi.RegisterCategory("Drone", () => "Drones", 5, 0x40C8FF);
        OrdnanceApi.RegisterPayload("author.Drone", new DronePayload());          // an IOrdnancePayload, see Extension API
        OrdnanceTurrets.RegisterLook("MyPack:turret", ForgeVisual.Models.Get("MyPack:turret")!,
            new TurretLookSpec { WorldScale = 1f, Burrow = new BurrowSpec { RiseHeight = 1.2f, TurnSeconds = 0.8f } });
    }
}
```

Its definitions are in `ForgeOrdnance/*.forge` beside the DLL: `{ "Id": "author.drone", "Category": "Drone", "Payload": { "Type": "author.Drone" }, ... }` and a `Gear` turret with `"Look": "MyPack:turret"`. The steps:

1. **A plugin** that depends on `NAinfini.ForgeOrdnance` (and the libraries it uses) with `[BepInDependency]`. Everything below happens in its `Load`, which runs before definition files are read.
2. **Models and effects.** Build one asset bundle and register it once: `ForgeVisual.Bundles.Register(modId, pack, path)` returns a handle whose models you pass to `OrdnanceTurrets.RegisterLook`, `OrdnancePodAppearance` and the like. Keep the bundle in your own package; do not copy it into Ordnance.
3. **Definitions.** JSON files in a `ForgeOrdnance` folder inside your package (see [Writing call-ins](#writing-call-ins)); every `Id` carries your namespace (`author.name`). Payload types you add are registered with `OrdnanceApi.RegisterPayload("author.Type", ...)`, categories with `OrdnanceApi.RegisterCategory`.
4. **Icons and texts.** Each definition's picker icon is a PNG beside its definition file (`.forge`, JSON inside) (`Icon`); ForgeUI draws it. Names and descriptions in other languages go in a `lang` folder beside the file (`en.forge`, `zh.forge`, keys `<Id>.Name` and `<Id>.Description`). Show names with `OrdnanceApi.DisplayName`.
5. **Names you register** (looks, sounds, bundles) are `Pack:item`, the pack being your short prefix, so two packs never collide.
6. **A definition only a level brings** (a suit standing at the start, a supply crate): write `"Callable": false` and leave out the player-facing fields (see [Developer drops](#developer-drops-for-level-authors)).

## Writing call-ins

Put definition files in any folder named `ForgeOrdnance` under `BepInEx/plugins`, for example `BepInEx/plugins/MyCallIns/ForgeOrdnance/*.forge`. For a custom rundown, use its MTFO custom folder: `<CustomPath>/ForgeOrdnance/*.forge`. Each `.forge` file holds a JSON array of definitions. All files load together.

```json
[
  {
    "Id": "me.pack.resupply",
    "Name": "Resupply",
    "Category": "Supply",
    "Description": "Two ammo packs",
    "Icon": "resupply.png",
    "Code": ["Down", "Down", "Up", "Right"],
    "Cooldown": 120,
    "CallInDelay": 5,
    "Payload": { "Type": "Item", "Items": [ { "Item": 101, "Count": 2 }, { "Item": 102 }, { "Item": 127 } ] }
  },
  {
    "Id": "me.pack.sentry",
    "Name": "Sentry",
    "Category": "Defensive",
    "Code": ["Down", "Right", "Down", "Up"],
    "Cooldown": 240,
    "Uses": 2,
    "CallInDelay": 6,
    "Payload": { "Type": "Gear", "Gear": 24, "AmmoRatio": 0.5, "Lifetime": 120, "Pickable": false }
  }
]
```

| Field | Meaning |
|---|---|
| `Id` | Unique, up to 64 characters, starting with your own namespace: at least two dot-separated parts of letters, digits, `_` and `-` (`me.pack.resupply`). Saved loadouts refer to it. |
| `Name` | Shown in the list, the picker and on the beacon. Defaults to `Id`. |
| `Category` | `Supply`, `Defensive`, `Weapon` or `Offensive`, or a category an add-on registered with `OrdnanceApi.RegisterCategory` (the exosuit part registers `Exosuit`). Picks the picker tab. An unregistered category skips the entry with an error. |
| `Description` | Optional, up to 400 characters. Use `\n` for a new line. |
| `Icon` | Optional. A PNG file name in the same folder as the definition file, no folders. A square image of 128 to 512 pixels works best. If the file is missing, the entry is skipped. It is also the picture in the player's loadout cells on every lobby bar, drawn there as a white shape cropped to its outline, so give each call-in a shape that is recognisable without its colours. |
| `Stats` | Optional, up to 6 rows such as `[{ "Name": "Armor", "Value": 4 }, { "Name": "Speed", "Value": 2, "Max": 5 }]`. Each row shows in the picker details as `Value` lit boxes out of `Max` (1 to 10, default 5). `Name` is up to 16 characters. |
| `Code` | 1 to 8 steps, each `Up`, `Down`, `Left` or `Right`. In one loadout, no code may be the beginning of another. |
| `Cooldown` | Seconds after an accepted call, 0 to 3600. Swapping a call-in into or out of a loadout slot (also from a console) resets that slot's cooldown to zero; its uses are kept. |
| `Uses` | Optional. Calls per level, 1 to 99. Leave it out for unlimited calls. |
| `CallInDelay` | Seconds from beacon landing to arrival, 0 to 120. |
| `BeamColour` | Optional `"#RRGGBB"`. Defaults by category: Supply blue, Defensive light green, Weapon white, Offensive orange; a registered category brings its own colour. |
| `Payload` | What arrives. `Type` picks the kind; the other fields belong to that kind. |

To show names and descriptions in the player's language, put a `lang/` folder beside the definition files with one ForgeUI language file per language (`en.forge`, `zh.forge`, ...), keyed `<Id>.Name` and `<Id>.Description`:

```json
{ "me.pack.resupply.Name": "补给", "me.pack.resupply.Description": "两个弹药包" }
```

A text missing there falls back to English, then to the definition's own `Name` and `Description`. The mod's own words are in `lang/` beside `ForgeOrdnance.dll`.

An entry with a mistake is skipped. The BepInEx log (`LogOutput.log`) names the file, the entry number and the field. The other entries still load.

### Payload `Item`

Pickups of item data blocks, delivered in the drop pod. The pod has four bays, so one definition holds at most 4 pickups in total.

| Field of each `Items` entry | Meaning |
|---|---|
| `Item` | The item's persistent ID (a number) or its block name. Ammo pack 101, medi pack 102, tool refill 127, disinfection pack 132. |
| `Count` | 1 to 4, default 1. |
| `AmmoRatio` | 0 to 1, default 1. Share of a full resource pack, or of a consumable's uses. |

### Payload `Gear`

A sentry gun from any `PlayerOfflineGearDataBlock`, delivered in a shell that bores into the ground; the base bores in until its top is level with the ground, throws its two lids (`lid_a`, `lid_b`) off to the sides with a puff of dust, the turret rises out of the opening barrel up, turns the barrel level, and only then does the game's sentry stand.

| Field | Meaning |
|---|---|
| `Gear` | The sentry's gear block by ID or name: 24 burst, 6 auto, 23 sniper, 25 shotgun. Other gear is refused. |
| `AmmoRatio` | 0 to 1, default 1. Share of the caller's class ammo pool. |
| `Lifetime` | Seconds until it is removed, 0 to 3600. 0 means it stays. |
| `Pickable` | `false` removes the pick-up interaction. Default `true`. |
| `Look` | Optional `"Pack:item"`: a turret look an add-on registered with `OrdnanceTurrets.RegisterLook` (Arsenal: `Arsenal:turret-burst`, `Arsenal:turret-sniper`, `Arsenal:turret-shotgun`). Without it the game's own sentry look stays. An id nobody registered skips the entry with an error. |
| `DamageScale`, `FireRateScale`, `AmmoScale`, `RangeScale`, `TurnScale`, `AcquireScale` | 0.1 to 5, default 1. Multipliers over the normal sentry of the same gear: damage, shots per second, ammo, range (the detection range, the damage falloff distances and the shot ray length by the same factor), turning speed, and how fast it opens fire on a new target. They apply to a copy; other sentries are untouched. |

### Keys

The call key and the four code keys are ForgeUI key actions (`Carrier/Native/OrdnanceKeys.cs`): `Summon` (`Mouse3`, press, every context) and `CodeUp` / `CodeDown` / `CodeLeft` / `CodeRight` (arrow keys, context `ordnance-code`). The player changes them in the game's settings (Controls); they are saved in `NAinfini.ForgeUI.keys.cfg`. While a code is entered the movement keys are held off the game and read from the player's own game bindings (`UIKitKeys.GameKeyDown`), so they enter code steps as well.

### Level rules: `Settings.forge`

Optional. Read only from `<CustomPath>/ForgeOrdnance/Settings.forge`.

```json
{
  "LoadoutSize": 3,
  "Allowed": ["me.pack.resupply", "me.pack.sentry"],
  "CooldownMultiplier": 1.5,
  "UsesMultiplier": 1,
  "MaxThrowDistance": 80,
  "Levels": { "Local_31_TierA_0": { "LoadoutSize": 2, "Allowed": null } }
}
```

| Field | Meaning |
|---|---|
| `LoadoutSize` | 1 to 8, default 4. |
| `Allowed` | Ids players may bring. Leave it out or use `null` for all. |
| `CooldownMultiplier`, `UsesMultiplier` | Scale every cooldown and every call's uses. |
| `MaxThrowDistance` | Metres from the throw origin to the beacon. |
| `LobbyPicker` | `false` turns the lobby choice off: no ORDNANCE button, no loadout row on the player bars, players' own picks are not used; `Loadouts` says who has what. Default `true`. See [Level control](#level-control-for-rundown-authors). |
| `CallMenu` | `false` turns the in-level call-ins off: no call list, no call key, the host refuses throws. Default `true`. A file that writes only `CallMenu` (no `LobbyPicker` at the same place) turns both off, as it always did. |
| `Loadouts` | The call-ins each player has while `LobbyPicker` is `false`, by player slot. See below. |
| `Drops` | The level's developer drops (see below). Default none. |
| `Levels` | Overrides any of the above for one level. Keys are the expedition's unique key (`RundownManager.ActiveExpeditionUniqueKey`, for example `Local_31_TierA_0`), not the display name such as `R1A1`; read it from that property in code, or from the log, where the host writes `Host level rules published for '<key>'`. A key that matches no level is not an error, so when the host enters a level and rules exist but none for it, one warning lists the level's actual key and the keys that matched nothing. A mod can set a level's rules from code too (see [Declaring a level from code](#declaring-a-level-from-code)). |

The host sends its selected expedition's rules to the lobby picker. While waiting for them, the button shows `WAIT`. A level override replaces a field as a whole: a level with its own `Drops` uses only those, a level without uses the top-level `Drops`.

### Level control for rundown authors

Everything here is written by hand in the rundown's `ForgeOrdnance/Settings.forge` (globally or under `Levels.<key>`); the website does not write it. Two independent switches say what the players get:

| `LobbyPicker` | `CallMenu` | Result |
|---|---|---|
| `true` | `true` | The default: players pick in the lobby and call in the level. |
| `false` | `true` | No lobby choice; each player has the level's `Loadouts` and calls them in the level. |
| `true` | `false` | Players may pick in the lobby but have no calls in the level (rarely wanted). |
| `false` | `false` | No player call-ins at all; only drops. Written as `"CallMenu": false` alone, as before. |

**Loadouts** (used only while `LobbyPicker` is `false`; with it `true` they are ignored, with one log line). Keys are the player's slot in the party, `"0"` to `"3"`, or `"All"` for every player not named; a slot's own list replaces `All` as a whole. A player nothing is written for has an empty list, which is not an error. The host gives them through the same path as a lobby pick (same checks, same sync to every player, resent to a player who joins later, uses and cooldowns kept, restored after a checkpoint reload or host change), so nothing else differs:

```json
{
  "Levels": {
    "Local_32_TierA_1": {
      "LobbyPicker": false,
      "CallMenu": true,
      "Loadouts": {
        "All": ["arsenal.supply.mixed", "arsenal.strike.gas"],
        "0": ["arsenal.supply.mixed", "arsenal.strike.hunter", "arsenal.turret.watch"]
      }
    }
  }
}
```

**Overrides** are the numbers one drop changes: `"Overrides": { "AmmoRatio": 0.5, "Magazine": 3 }` in a `Drops` entry (or `OrdnanceApi.Place(..., overrides)`). The framework only checks that the payload type declares each field and that the value is in range, then hands the numbers to the payload (`OrdnanceExecution.Overrides`, host); the payload applies them and syncs them with its own state, so every player and a late joiner sees them. A drop that writes a field its payload does not declare is skipped with an error. Declared fields:

| Payload | Field | Range |
|---|---|---|
| `arsenal.HeavyWeapon` | `AmmoRatio` | 0 to 1, reserve as a share of the special slot's full reserve |
| `arsenal.HeavyWeapon` | `Magazine` | whole, 1 to 1000, rounds loaded |
| `arsenal.HeavyWeapon` | `AmmoCost` | 0.01 to 1000, reserve one shot spends (the block's CostOfBullet); the rounds given are the reserve share divided by it, plus the magazine |
| `frames.Exosuit` | `Armor` | 1 to the model's own `Armor` |
| `frames.Exosuit` | `Magazine` | whole, 1 to the model's own `Magazine` |
| `frames.Exosuit` | `Reserve` | whole, 0 to 100000 |
| `Item`, `Gear`, `arsenal.Strike`, others | none | declare yours with `IOrdnancePayload.OverrideFields` and `CheckOverrides` |

A suit that already exists (restored from a checkpoint, mirrored by a new host) keeps its own numbers; a level-start drop places a missing one again with its `Overrides`. A heavy weapon's rack is rebuilt from the same drop, so the values hold.

**Triggering by event.** Every call-in type (supply pods, sentries, heavy weapons, exosuits, third-party types and every strike) can be fired by the level event type `75000` of an `Event` drop. A strike with `"Arrival": "Direct"` (or `Drop`: a developer drop has no beacon, so the two are the same) starts at the written point once, through the same scheduler and arrival entry. Hand-edit the exported level data to add the event.

A complete example with all three parts, for a level that has no call menu, an event strike, a pre-placed suit and a heavy weapon:

```json
{
  "LobbyPicker": false,
  "CallMenu": false,
  "Drops": [
    { "Name": "start-suit", "Ordnance": "nainfini.frames.tank", "When": "LevelStart", "Arrival": "Direct",
      "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0 }, "Overrides": { "Armor": 60, "Magazine": 40, "Reserve": 200 } },
    { "Name": "start-rifle", "Ordnance": "arsenal.weapon.recoilless", "When": "LevelStart", "Arrival": "Direct",
      "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0, "Offset": { "x": 2, "y": 0, "z": 0 } }, "Overrides": { "AmmoRatio": 0.4, "Magazine": 2, "AmmoCost": 1.5 } },
    { "Name": "gate-strike", "Ordnance": "arsenal.strike.creeping", "When": "Event", "Arrival": "Direct",
      "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 2, "Position": { "x": 12.5, "y": -3.0, "z": 40.0 } } }
  ]
}
```

with the event in a door's `EventsOnOpenDoor`: `{ "Type": 75000, "Trigger": 0, "Delay": 0.0, "WorldEventObjectFilter": "gate-strike" }`. Placing from code is `OrdnanceApi.Place` with `overrides`; checking before placing is `OrdnanceApi.CheckOverrides(definition, overrides)`. The `OrdnanceLevelRules` of `DeclareLevel` has `LobbyPicker`, `CallMenu`, `Loadouts` and `Drops` too.

### Developer drops (for level authors)

A rundown can place any registered call-in itself, with no beacon and no player involved: at level start, or when a level event fires. The drops live in the same `Settings.forge`, so they ship with the rundown; players need nothing in their own config. A drop uses no loadout slot, cooldown or use and ignores `Allowed`. The host places it; every player sees the same arrival, and players who join later see it in its current state.

**A drop at level start.** It arrives `Delay` seconds after the level starts (when the game enters the level, as the elevator lands).

```json
{
  "Levels": {
    "Local_32_TierA_1": {
      "Drops": [
        {
          "Name": "start-suit",
          "Ordnance": "nainfini.frames.assault",
          "When": "LevelStart",
          "Arrival": "Direct",
          "Yaw": 180,
          "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0, "Offset": { "x": 2, "y": 0, "z": 0 } }
        }
      ]
    }
  }
}
```

**A drop on a level event.** Write the drop with `"When": "Event"`, then add a vanilla event of type `75000` to any event list of the level (a door's, a scan's, a terminal command's, an objective's, a zone's `EventsOnEnter`) and put the drop's `Name` in `WorldEventObjectFilter`. The game's own `Trigger` and `Delay` decide when it fires.

```json
{
  "Levels": {
    "Local_32_TierA_1": {
      "Drops": [
        {
          "Name": "door-supply",
          "Ordnance": "arsenal.supply.mixed",
          "When": "Event",
          "Arrival": "Drop",
          "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 1, "Position": { "x": 12.5, "y": -3.0, "z": 40.0 } }
        }
      ]
    }
  }
}
```

The event, for example in a security door's `EventsOnOpenDoor` in the level layout datablock:

```json
{ "Type": 75000, "Trigger": 0, "Delay": 2.0, "WorldEventObjectFilter": "door-supply" }
```

An event drop may leave out `Where`; it then lands where its event says: the event's `DimensionIndex`, `Layer`, `LocalIndex` and `Position` (a world position). One drop entry can so be fired by several events at several places:

```json
{ "Name": "on-the-spot", "Ordnance": "arsenal.turret.watch", "When": "Event", "Arrival": "Direct" }
```

```json
{ "Type": 75000, "Trigger": 0, "DimensionIndex": 0, "Layer": 0, "LocalIndex": 3, "Position": { "x": -8.0, "y": 0.0, "z": 21.5 }, "WorldEventObjectFilter": "on-the-spot" }
```

The event only drops: its `WardenIntel`, `SoundID` and the other fields of a vanilla event do nothing on type `75000`; put them on an event of their own.

**Drop or direct.** `Arrival` picks how the call-in arrives. `Drop` (the default) arrives the way a player's call does: the drop pod falls and opens, the turret shell bores in, the exosuit falls through the ceiling; the contents come when the vehicle opens. `Direct` leaves the vehicle out: the contents are there at once. `Drop` is the default because a drop is seen and heard, so players notice an event drop; use `Direct` to set a level up quietly, for example a suit standing ready at the start.

| Payload | `Drop` | `Direct` |
|---|---|---|
| `Item` (supply pods) | Pod falls, opens; pickups inside | Pickups on the ground around the point |
| `Gear` (turrets) | Shell bores in and throws its lids off; the turret rises out of it and turns its barrel level | Turret stands at the point |
| `arsenal.HeavyWeapon` | Pod with the weapon racked | Weapon on the ground, ready to take |
| `frames.Exosuit` | Suit falls and lands | Suit stands empty, ready to board |
| `arsenal.Strike` | Shells fall and strike | The same as `Drop`: a developer drop has no beacon, so the strike starts at the written point |

A drop's turret takes its ammunition from the host's class tool pool, as the host owns it.

**Turning the call menu off.** With `"CallMenu": false` the players have no call-ins in that level: no call list or cooldowns on the HUD, the call key does nothing, and the host refuses any throw (and, written alone, the lobby hides the ORDNANCE button and picks row too: see `LobbyPicker`). Developer drops still arrive. Use it for a level that only wants the drops:

```json
{ "Levels": { "Local_32_TierA_1": { "CallMenu": false, "Drops": [ { "Name": "start-suit", "Ordnance": "nainfini.frames.tank", "When": "LevelStart", "Arrival": "Direct", "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0 } } ] } } }
```

Put `"CallMenu": false` at the top level instead to turn the call-ins off in every level of the rundown.

**Fields.**

| Field | Meaning |
|---|---|
| `Name` | Required. Unique in its list. Events name the drop by it; the log names it in every line. |
| `Overrides` | Optional numbers for this one placement, `{ "Field": number }`. Only the fields the call-in's payload type declares (see **Overrides** under [Level control for rundown authors](#level-control-for-rundown-authors)). |
| `Ordnance` | Required. The `Id` of any loaded call-in definition (this package's or your own). |
| `When` | Required. `LevelStart` or `Event`. |
| `Arrival` | `Drop` (default) or `Direct`. |
| `Delay` | `LevelStart` only: seconds after the level starts, 0 to 3600, default 0. An event drop uses its event's `Delay`. |
| `Yaw` | Facing in degrees about the vertical, 0 to 360, default 0 (world +Z). A drop pod's doors and a suit's back follow it. |
| `Where` | Required for `LevelStart`, optional for `Event`. |

**Where.** Written like a vanilla event: `DimensionIndex` (0 is reality), `Layer` (0 main, 1 secondary, 2 third) and `LocalIndex` (the zone's local index), then the point, one of:

- `Position`: a world position `{ "x": .., "y": .., "z": .. }`, used exactly as written (no snapping).
- `Area`: an area of the zone, 0-based in the zone's own area order, at the centre of its course node; `Offset` `{ "x": .., "y": .., "z": .. }` (world axes, metres) moves it.

The point is yours: nothing checks that it is open floor, out of a wall or under open ceiling. The host only refuses what cannot run.

**Errors.** Every refusal is one error line in the BepInEx log (source `NAinfini.ForgeOrdnance`); nothing is dropped silently.

| Log line | When | Meaning |
|---|---|---|
| `Settings.forge ... Drops[i] <field>: ...` | Load | A drop entry is malformed (missing field, unknown field, a value out of range, both or neither of `Position` and `Area`, a `Delay` on an event drop, a duplicate `Name`). The whole `Settings.forge` is then ignored, as for any invalid field, and default rules apply. |
| `... drop '<name>' Ordnance: '<id>' is not a loaded call-in; this drop is skipped.` | Load | No definition has that `Id` (its package is not installed, or the id is misspelt). The other drops load. |
| `... drop '<name>' Arrival: '<id>' (payload <type>) cannot arrive Direct; this drop is skipped.` | Load | The call-in's payload has no direct arrival (an add-on payload without `SupportsDirect`; every payload type of this package has one). |
| `... drop '<name>' Overrides.<Field>: ...; this drop is skipped.` | Load | The field is not one the payload type declares, the value is outside its range or not whole, or the payload refuses it (for example an `Armor` above the model's own). The other drops load. |
| `... Loadouts.<slot>[i]: '<id>' is not a loaded call-in; left out.` / `... : <reason> Left out.` | Load | The Id is not loaded, or the entry fails the lobby loadout checks (not callable, not in `Allowed`, listed twice, past `LoadoutSize`, its code begins another's). The rest of the list stays. |
| `... Loadouts: ignored while LobbyPicker is true ...` / `... have no effect while CallMenu is false ...` | Load | The loadouts would not be used with those two switches. |
| `Loadout from <player> ignored: this level has LobbyPicker off ...` | Level | A modified client submitted its own picks; the host keeps the level's `Loadouts`. |
| `Developer drop '<name>' (...) not placed at <place>: dimension <n> does not exist in this level.` | Firing | The level has no such dimension. |
| `... not placed at <place>: there is no zone <z> in layer <l> of dimension <d>.` | Firing | No zone with that `Layer` and `LocalIndex` in that dimension. |
| `... not placed at <place>: zone <z> (...) has <n> area(s); Area <a> does not exist.` | Firing | `Area` is past the zone's last area. |
| `... not placed at <place>: the point (x, y, z) is not inside any area of dimension <d> (no course node within 2 m).` | Firing | The point is outside the level's areas in that dimension (an event drop without `Where` and an event without `Position` lands at 0, 0, 0). |
| `Ordnance drop event (type 75000) names drop '<name>' in WorldEventObjectFilter, but level '<key>' has no event drop of that name in Settings.forge; nothing is dropped.` | Firing | The event names no `Event` drop of this level (misspelt name, the drop is in another level's list, or it is a `LevelStart` drop). |
| `Developer drop '<id>' (call <n>) failed on arrival: ...` | Arrival | The payload itself failed (for example a pickup prefab missing); its message says why. |

**Callable: false.** A definition written with `"Callable": false` is for levels only: players never see it in the picker or a loadout (the host refuses it in one), and only `Drops` and `OrdnanceApi.Place` can bring it. It then has only `Id`, `Payload` and the optional `Name`, `Icon` and `Description`; `Category`, `Code`, `Cooldown`, `Uses`, `CallInDelay`, `Stats` and `BeamColour` must be left out, and giving one rejects that entry with an error naming the field. Use it for a suit or a supply that a level places at its start. Because they exist only to be placed, `PickedParameters` always lists their parameters (for their payload type) in every level, so a `Place` call finds its assets prepared.

```json
[ { "Id": "me.pack.start-suit", "Callable": false, "Payload": { "Type": "frames.Exosuit", "Model": "Frames:frame-m15", "Armor": 100, "DamageReduction": 0.25 } } ]
```

**Placing from code.** `OrdnanceApi.Place(ordnanceId, position, yaw, arrival, out reason, dimension = -1, overrides = null)` (`overrides`: see **Overrides** under [Level control for rundown authors](#level-control-for-rundown-authors)) brings a call-in in now, through the same delivery as a `Drops` entry (one scheduler, every player sees it, late joiners see its current state). It is host work: test `SNet.IsMaster` first; a client gets `false` and a `reason` without a log line, any other refusal (not in a level, unknown id, `Direct` for a payload without it, a point inside no area) is `false`, a `reason` and one error in the log, written on every call (not throttled). Pass `dimension` to require the point to be in that dimension; the default takes whichever contains it.

```csharp
if (SNet.IsMaster && !OrdnanceApi.Place("me.pack.start-suit", spawnPoint, 180f, OrdnanceArrivalMode.Direct, out var why))
    Logger.LogWarning("start suit not placed: " + why);
```

### Declaring a level from code

`OrdnanceApi.DeclareLevel(levelKey, rules)` gives a level the rules `Settings.forge` `Levels.<key>` would, without any file: turn the call menu off and place a suit at level start from your own plugin. Call it from `Load`. `rules` is an `OrdnanceLevelRules` (`LoadoutSize`, `Allowed`, `CooldownMultiplier`, `UsesMultiplier`, `MaxThrowDistance`, `CallMenu`, `Drops` of `OrdnanceLevelDrop`, `DropWhere`, `DropPoint`, `DropWhen`); it is the level's whole rule set, with defaults for what you leave out (nothing is inherited from the file's top level), and is checked like the file. It is merged with `Settings.forge` into one effective setting that also passes the drops check. A key already declared by another call is refused with one error; a key `Settings.forge` also sets is reported at load and the file wins.

```csharp
OrdnanceApi.DeclareLevel("Local_32_TierA_1", new OrdnanceLevelRules
{
    CallMenu = false,
    Drops = new[] { new OrdnanceLevelDrop("start-suit", "me.pack.start-suit", DropWhen.LevelStart, OrdnanceArrivalMode.Direct, 0, 180,
        new DropWhere(0, 0, 0, new DropPoint(0, 2, 0, 0))) }   // dimension 0, layer 0, zone 0, area 0 offset (2, 0, 0)
});
```

### After a checkpoint reload or a host change

Both void the drops still waiting and every call in flight. After a checkpoint reload the host places **every** `LevelStart` drop again at once (`Delay` ignored), whether it had arrived or not: a suit that was standing is standing again (a suit someone had boarded is not boarded; that state is lost). A host change does not roll the world back, so the new host schedules only the `LevelStart` drops that had not arrived (the host's table of arrived drops says which), each with its `Delay`. A level-start drop has a fixed call number made from its `Name`, so every player sees the same call; two names that would share a number are refused when `Settings.forge` is read. `Event` drops and what `Place` placed are not placed again; an event that fires again places its drop again.

A host change does not roll the world back, so sentries and pods already standing stay. A sentry's identity is made from its call (`SentryIdentity.For(summoner, throwId)`, the same on every peer), and every peer records the strengths its `Gear` payload applied; the new host takes the running sentries over by identity, applies those strengths again, and recalls or retires them on their own timers. A checkpoint reload instead clears sentry state with the rest, because the level replays its drops under the same call numbers. Each slot's row in the host's table carries its starting use count (`InitialUses`), so a new host caps a `RefundCall` at the same number the old host did.

## Extension API

The public API is in the `ForgeOrdnance` namespace. It ships with the package (version 1.0.1). Everything not listed below is internal and may change in any release.

| Type | Use |
|---|---|
| `OrdnanceApi` | `RegisterPayload`, `RegisterCategory`, `DefinitionsLoaded`, `DefinitionsReady` (event, raised once on the main thread after the definition files are read), `IsPeerCompatible`, `SendData`, `SendState`, `ClearState`, `RefundCall` (host: a call that found nothing to do gives its use back and keeps a short cooldown), `PostNotice` (a line on this player's call-in list), `BlockCalls`, `CallsBlocked`, `PickedParameters`, `DisplayName`, `DeclareLevel`, `Place`, `TryGetDefinition(uint key)` and `TryGetDefinition(string id)` (the loaded definition; `OrdnanceDefinition.Key` is the value the network and saves carry; false before definitions load; every peer loads the same set, so clients and late joiners find it too), and `Definitions` (every loaded definition, ordered by `Id`, the same on every peer). |
| `OrdnanceLevelRules`, `OrdnanceLevelDrop`, `DropWhere`, `DropPoint`, `DropWhen` | The rules of a level and its drops, for `DeclareLevel`. |
| `IOrdnancePayload`, `IOrdnancePayloadParser`, `PayloadParameterException` | A new call-in kind. |
| `OrdnanceExecution`, `OrdnancePresentation`, `OrdnancePayloadData`, `OrdnanceDefinition`, `OrdnanceStat`, `OrdnanceCategory`, `Direction`, `OrdnanceArrivalMode` | Read-only data given to your callbacks (`OrdnanceCategory` is a string id). |
| `OrdnanceDelivery`, `OrdnancePod`, `OrdnanceShell` | Reuse the drop pod and turret shell. |
| `OrdnanceClaims` types: `ClaimableSpec`, `OrdnanceClaimable`, `ClaimPrompt`, `ClaimVerdict`, `ClaimInstall`, `ClaimResult` | In-pod (or pod-less) claiming: `OrdnancePod.AddClaimable(bay, spec)`, `OrdnanceDelivery.AddClaimable`, `OrdnanceDelivery.HostOpenClaim`. |
| `OrdnanceArrival`, `OrdnanceImpact`, `OrdnanceDropStyle`, `OrdnanceDrop` | The one camera shake (`Shake`), landing impact (`Impact`) and a container-free drop (`Drop`). |
| `OrdnanceTurrets`, `TurretLookSpec` | Register turret looks and listen to turret moments. |
| `OrdnancePodAppearance`, `OrdnancePodDoor`, `OrdnancePodSocket`, `OrdnancePodBay` | A custom pod look (its model is a ForgeVisual `ModelHandle`). `OrdnancePodBay.Fit(look, frontToForward, out scale, out lean)` scales, lays flat or stands up any look to the bay's size and parents it to the bay anchor. |
| `OrdnanceCallBlock` | Hold the call key off, for example while piloting; `Dispose` releases it. |
| `OrdnanceArrivalSounds`, `OrdnanceArrivalSound` | The drop pod's fall, touchdown and rubble sounds, for a payload with its own arrival that falls the same way (`Play(sound, transform, dimension)`: `Incoming` when the fall starts, `Impact` and `Debris` on touchdown, `Open` and `Leave` as outer plates open and close). |

The public surface carries no separate API version: it is the package version (1.0.1). An add-on that uses something missing fails to load. Register your payload types from `Load`:

```csharp
OrdnanceApi.RegisterPayload("author.MyType", new MyPayload());   // returns false (and logs) when refused

public sealed class MyPayload : IOrdnancePayload
{
    public object Parse(JsonElement payload) { /* validate, return your parameters, or throw PayloadParameterException(field, message) */ }
    public void Execute(OrdnanceExecution execution) { /* host only, when the call arrives */ }
    public void OnBeaconLanded(OrdnancePresentation presentation) { }   // optional, every peer
    public void OnArrived(OrdnancePresentation presentation) { }        // optional, every peer
    public bool HasOwnArrival => false;                                 // true skips the generic arrival shake
    public bool SupportsDirect => false;                                // true: honours a direct arrival (no vehicle)
    public void OnData(OrdnancePayloadData data) { }                    // optional, every peer: data the host sent
    public byte[] RefreshState(ulong summoner, uint throwId, byte tag, byte[] data) => data; // optional, host: rebuild a kept state when a player joins
}
```

Registration is refused for a type name without an author namespace (`author.Type`), a taken name, or registering after definitions were read.

- **`Parse`** gets the whole `Payload` object. It runs at game-data initialization, so it may read data blocks. Reject unknown fields yourself. The returned object becomes `OrdnanceDefinition.Parameters`.
- **`Execute`** runs on the host only. It gets the definition, parameters, landing position (a throw's snapped to the navigation mesh, a developer drop's exact), rotation, course node, summoner and `Arrival`. A developer drop has `SummonerLookup` 0 and no `Summoner`. An exception is logged and the call is lost with its use spent.
- **Presentation callbacks** run on every peer. `OnBeaconLanded` gets the delay until arrival. The beacon's pillar (the slot `beacon.pillar`: a held line in the beam colour from the landing point up to the ceiling found by `ForgeCore.Level.LiftUnderRoof`, at most 20 m, fading out in the air where there is none) and countdown label are always shown; the label is ForgeUI's countdown world marker (`AddCountdownMarker`).
- **Per-call data.** `OrdnanceApi.SendData(payloadType, summonerLookup, throwId, tag, bytes)` (host only, at most 192 bytes) reaches `OnData` on every peer. `SendState` keeps the bytes as the call's current state for that tag, a row in the host's table: every peer's `OnData` gets it when it changes (send the whole state each time; the same bytes again are no change and reach nobody), and a player who joins later gets the state as it stands. `Replayed` is true on a peer that did not see the call arrive (it joined later, or a checkpoint was reloaded), so it builds the current look from the state alone. `ClearState` forgets a finished call. The host keeps at most 256 states; a full table makes `SendState` return false, so refuse or undo the world change. A state that holds times relative to when it was sent (seconds left) overrides `RefreshState`, which the host calls on each kept state when a player joins, so the late joiner's clocks run from the record as it is now, not from when the state was kept; bytes that differ from the kept ones reach every peer as the state's new value. An add-on's own messages to the host are requests or events on its own ForgeCore channel; ForgeCore drops what was sent in another level or before a checkpoint reload, so they carry no level number.
- **Lobby picks.** `OrdnanceApi.PickedParameters(payloadType, into)` fills `into` with the parameters of every call-in of that payload type that can arrive in the selected level, each once: the players' synced lobby picks (none when the level has `CallMenu` off) the level's developer drops, and every `Callable: false` definition of that type (read only; empty when there is none), so a payload can prepare its assets before the level.
- **Arrival mode.** `OrdnanceExecution.Arrival` and `OrdnancePresentation.Arrival` are `OrdnanceArrivalMode.Drop` or `Direct`. Player calls always drop; a level's developer drop may ask for `Direct`, and only payloads that return `SupportsDirect => true` get it (others are refused when the definitions load). A direct arrival is the same arrival with the vehicle stage left out: `OrdnanceDelivery.StartPod` / `StartShell` return null for it and `PodContentsDelay(arrival)` / `ShellRiseDelay(arrival, burrow)` / `ShellReadyDelay(arrival, burrow)` are 0, so a payload written against them handles both; a payload with its own fall (an exosuit) leaves the fall out itself.
- **Blocking the call key.** `OrdnanceApi.BlockCalls(reason)` returns an `OrdnanceCallBlock`. While any block exists (`OrdnanceApi.CallsBlocked`), a call key press is ignored and a call in progress is cancelled. `Dispose` the block when its reason ends; calling it twice is safe, and calls work again when no block is left.
- **Lending a gun into the special slot.** The special weapon slot lease is ForgeCore's `Gear.SpecialSlot` (not part of this API): one arbiter for every mod that lends a gun into the slot, with the own weapon's kept stock, the special ammunition redirect and checkpoints. See ForgeCore's `DEVELOPERS.md`.
- **Compatibility.** Gate your own network handlers with `OrdnanceApi.IsPeerCompatible(lookup)`. Send your own messages on your own ForgeCore channel (`Net.Open` with your plugin GUID). Check senders in your handlers.

### Reusing the drop pod and turret shell

Return `HasOwnArrival => true`, start the arrival from `OnArrived`, and place networked contents from `Execute` after the matching delay.

```csharp
public void OnArrived(OrdnancePresentation p)            // every peer
{
    var pod = OrdnanceDelivery.StartPod(p);               // null for a direct arrival, and (one log line) when the art bundle did not load
    if (pod == null) return;
    pod.Opened += () => { if (pod.WeaponBay is { } bay) myModel.Spawn(bay.Position, bay.Rotation, pod.WeaponAnchor); };
}

public void Execute(OrdnanceExecution e)                 // host
{
    OrdnanceDelivery.TryPodWeaponBay(e.Position, e.Rotation, out var bay);
    OrdnanceDelivery.HostAfter(OrdnanceDelivery.PodContentsDelay(e.Arrival), () => { /* spawn the networked item at bay.Position, or e.Position when direct */ });
}
```

- `StartPod` and `StartShell` return a handle with `Root`, `HasLanded`, `IsOpen` and the events `Landed`, `Opened` and `Left`. The pod also has `Anchor(0..3)`, `WeaponAnchor`, `Bay(0..3)` and `WeaponBay`. Objects parented to an anchor fall, open and sink with the pod.
- The pod has four pickup bays (0 and 1 on the +X side, 2 and 3 on the -X side) and two weapon bays. A weapon bay is 0.78 x 1.23 x 0.32 m, enough for a 1.13 m weapon standing upright.
- **Burrow timeline (`BurrowSpec`, all data).** From touchdown: the base bores in over `BurrowSeconds` until its top is level with the ground, waits `HoldSeconds`, then its two lids (the base model's nodes `lid_a` and `lid_b`; a base without them skips this step with no error) are thrown outward in an arc (`LidSpeed` m/s out, `LidLift` m/s up, `LidSpin` degrees/s of tumble, via ForgeVisual `Thrown`), lie `LidRestSeconds` and vanish, with a puff of dust. `LidClearSeconds` after the throw the upper part, wholly inside the base and barrel up, rises `RiseHeight` over `RiseSeconds`; a gun then turns level over `TurnSeconds`, and the host stands the game's sentry at that moment. At the end the barrel turns up, the upper part sinks back, and the base sinks away over `LeaveSeconds`. Every pose is a function of the arrival's shared time, so a late joiner sees the current one. A device sets the same fields in its definition (`BurrowSpec.Fields`, `BurrowSpec.Parse`).
- The turret shell is one burrow for every turret and standing device (the base is shared; only the upper part differs). A device that goes in and comes up while it works sets `OrdnanceShellAppearance.Dive` (seconds since the device was ready, to 0 standing or 1 down; it must depend on the seconds alone): the upper part then sinks back into the open base (the base stays), `RiseHeight` down at 1. `BurrowSpec.BaseScale` sizes the base to the upper part. Return an `OrdnanceShellAppearance(burrow, lifetime)` from your payload's `GetShellAppearance(definition)` (every peer, late joiners too, build it from the definition alone) and call `StartShell(presentation)`; Forge draws the rest from the arrival's shared timeline. `BurrowSpec` holds the numbers, all data: `BurrowSeconds` (the drill boring in after the 1.1 s fall, 0.6), `RiseHeight` (how deep the stack sinks so that the upper part is below the ground: its full height, barrel up), `RiseSeconds` (both rising until the base top is level with the ground) and `TurnSeconds` (a gun's barrel from straight up to level, 0 for a head with no gun). The two models are the base and the upper part: the upper part is `Head` (a ForgeVisual model with its origin on the centre of the base's top face, scaled `HeadScale`, front turned by `HeadFront`); a gun names its barrel part in `Gun`, and that part is what turns. `ShellRiseDelay` / `ShellReadyDelay` give the host's moments (the stack starts to rise; it has risen and turned level, when the host stands a sentry and a device starts its work); `OrdnanceShell.HeadRise` (0 to 1) and `Head` follow it each frame. The base model (`Ordnance:turret-shell`) has the nodes `body` (the drill and rim; the rim's top comes to rest 1 cm above the ground), `well` (a dark disc under the rim that reads as the bore once the lids are gone) and `lid_a`, `lid_b`; origin on its vertical axis. The upper part is on it from the fall on, barrel straight up. After `Lifetime` the barrel turns up and both sink out of sight (`Lifetime` 0: they stay until the level ends).
- `PodContentsDelay(arrival)` and `ShellReadyDelay(arrival, burrow)` give the host's timing for when the pod's weapon racks show their contents and the shell's head is ready (0 for a direct arrival). The built-in `Item` payload does not wait for it: it spawns its pickups right after touchdown (1.5 s after the arrival) and the platform keeps them shut away (not drawn, not takeable) until the doors start to open at 4.5 s.
- A pod stays until the host says it is empty. `HostWatchPickups(summoner, throwId, ItemInLevel[])` does that for pickups. `HostReportPodEmpty(summoner, throwId)` declares it now. The empty pod then stays 15 s and sinks.
- `HostAfter(seconds, action)` is dropped when calls in flight are voided (level exit, checkpoint reload, host change).

### Claiming from a pod

`OrdnancePod.AddClaimable(bay, spec)` (every peer, from `OnArrived`; without a pod `OrdnanceDelivery.AddClaimable(summoner, throwId, position, openAfter, spec)` and `HostOpenClaim`) makes something in a bay claimable. The platform does the aiming (reach 2.4 m, aim cone), the prompt, the request with a 1 s cooldown, the host's alive, reach, open and not-reserved checks, the reservation with an 8 s timeout, the messages on Ordnance's channel and the removal when the pod leaves. Your `ClaimableSpec` says the rest:

| Member | Where | Meaning |
|---|---|---|
| `Prompt` | local, each frame aimed at | `ClaimPrompt(text, key, claimable)`; return a cached value while nothing changed. |
| `HostCheck` | host | A refusal reason for this player, or null to reserve it for them. |
| `Install`, `Cancelled` | claimant | Install the gear, then `ClaimInstall.Installed()` or `Failed()`; `Cancelled` when the host took the reservation back. |
| `HostVerify` | host, each frame after `Installed()` | `ClaimVerdict.Wait`, `Refuse(reason)` or `Accept(token)`. |
| `Committed` | every peer | The claim was taken (`ClaimResult`: player, attempt, token, `Ours`). On the host this is where its bookkeeping starts. |
| `Opened`, `Removed` | every peer | Show the thing in its bay; remove it. `OrdnanceClaimable.Withdraw()` removes it early. |

Borrowing rules (who may take, loans, returns) are yours; the heavy weapons keep them in their own `LoanBook` and send only small loan notices (`Loan`) on the mod's channel.

### Camera shake and drops

Camera shake is ForgeVisual's `CameraShake.Shake(at, ShakeSpec)` for every package (distance fall-off, a cap on shakes running together, the player's `Camera / ShakeStrength` in ForgeVisual's config). `Impact(owner, ground, OrdnanceImpact)` plays a touchdown (thud, debris, shake; `ShakeIf` lets you skip the shake, for example inside a vehicle; `NoiseRadius` above 0 also makes the game's world noise there that wakes sleeping enemies, on the host only, default 0). `Drop(body, landing, seconds, OrdnanceDropStyle)` falls a body without a container, with the touchdown at the end; `OrdnanceDrop.Cancel()` stops it.

### Turret looks

Register looks from `Load`, before definitions are read: `OrdnanceTurrets.RegisterLook("Pack:item", modelHandle, new TurretLookSpec { WorldScale = 1.24f, Burrow = new BurrowSpec { RiseHeight = 1.5f, TurnSeconds = 0.8f } })`. The model is a ForgeVisual `ModelHandle` from your own bundle with the parts `base`, `yaw`, `head` and `muzzle`, flat under the prefab, and optionally a `barrel` under `head` with its origin on the bore axis (it slides back and spins on its own; without it the whole head recoils); `BurrowSpec.BaseScale` and `RiseHeight` fit the drop base to it. `TurretLookSpec.Motion` (`TurretMotionSpec`) sets the idle scan, the kick of a shot, `BarrelRecoil` and `BarrelSpin`. A turret with `Pickable` false ends when it is out of ammunition, by the same path as `Lifetime`: the host decides, the turret stows into its shell, the shell sinks and the sentry is removed, so an empty turret cannot be refilled (a part-used one still takes ammunition packs as the game does). The look is drawn to the game's aim on every peer (a client reads the sentry's synced target direction), and the game's muzzle point stays on the look's muzzle along the barrels, so shots leave the barrel that is drawn. A `Gear` definition with `"Look": "Pack:item"` gets that look on every peer, late joiners included; the look id travels in the sentry's marker. `OrdnanceTurrets.Deployed(lookId, shell)`, `Online(lookId, sentry, at)` and `Expiring(lookId, sentry, position)` fire on every peer for sentries wearing a registered look, so you can add your own sounds. `TryLook(sentry, out lookId)` tells which look a sentry wears. `Deployed` fires when the base has bored in and the stack starts to rise. The look is a drawn copy standing on the base; the game's sentry is stood by the host only when the barrel has turned level (so it neither aims nor fires before), and its own look takes over from the copy.

### Custom pod looks

Override `IOrdnancePayload.GetPodAppearance(OrdnanceDefinition)` to return a cached `OrdnancePodAppearance` built after your bundle loads:

```csharp
var art = new OrdnancePodAppearance(ForgeVisual.Models.Get("MyPack:my-pod")!,
    new[] { new OrdnancePodDoor("door", Vector3.up, 105f) },
    new[] { new OrdnancePodSocket("weapon_anchor", new Vector3(.91f, 1.87f, .41f), Quaternion.Euler(0, 90, 0)) });
```

The model's origin is on the ground, its units are metres, and its scale is one. All peers must have the same model and definition, and the callback must give the same answer every time.

### Art and sound

- Build your bundle with the carrier's pipeline: `tools/Carrier/export_blender.py` per model (it keeps the node hierarchy) and `tools/Carrier/OrdnanceBundleBuild.cs` in Unity 2019.4.21f1. Copy `tools/Carrier/build_assets.ps1` and change the inputs and the bundle name (`FORGE_ORDNANCE_BUNDLE`); `tools/Arsenal/build_assets.ps1` is such a copy. `tools/Carrier/tripo_masters.py` makes the low-poly masters of the carrier and Arsenal from the repaired high-poly models; a model that has not arrived is skipped and the build keeps its older one.
- Register the bundle with ForgeVisual (the bundle format is unchanged): `Bundles.Register(modId, pack, path)` from your plugin's `Load`, then `Models.Get("Pack:prefab")`; `model.Spawn(position, rotation, parent)` makes an instance with the game's gear shader once a level has been built, and `bundle.TryTexture(name)` returns a standalone texture from the bundle's `textures/` folder. Ordnance's own bundle is the pack `Ordnance`. Particles you bake into a bundle go to `ParticlePool` (an active prefab of looping world-space systems with emission off, `maxParticles` equal to the capacity you pass to `Channel`).
- Metallic textures use R for metallic and G for smoothness. The shader has no detail map, so bake surface detail into the textures.
- Sound goes through ForgeAudio, not Ordnance. Put Ogg files and `cues.forge` in an `audio` folder next to your DLL and call `AudioKit.Load`. The carrier's own cues are `Assets/Carrier/Audio/cues.forge`.

## Network

Everything goes through the plugin's one ForgeCore channel (`Net.Open` with the plugin id, names `NAinfini.ForgeOrdnance/<Name>`): `Carrier/Native/OrdnanceNetwork.cs` opens it, and Arsenal (`ArsenalNetwork`) and the exosuits (`FrameNetwork`) register their own messages on it. ForgeCore's handshake compares the channel's format fingerprint (the registered names, versions, delivery and field layouts of all three parts) and its content value, a fingerprint of the definition files and the registered payload kinds, so a player whose copy differs is refused on both sides and told in chat; a refused player cannot call anything. The mod keeps no handshake, member list or level number of its own: a peer is in the level while its side of the channel is ready, and ForgeCore drops what was sent in another level or before a checkpoint reload. Handlers run on the main thread and check the sender themselves, because the network layer does not authenticate it.

- **Requests** (client to host, exactly one answer, one refusal byte). Carrier: `Loadout` and `Landing` (a `Rejection`). A throw the host did not answer in time, or whose host left, is refused with `NoAnswer` and the player is told; a loadout that got no answer goes again to whoever is the host then. Exosuits: `FrameRequest` (board, leave, report ammunition, use a special) and `FrameRocketRequest` (a `FrameRejection`; one the host did not answer is shown as "not now").
- **Host tables** (carrier; ForgeCore sends them to a player who joins and keeps them through a host change): `Loadouts`, `Slots` (uses left, cooldown left, starting uses), `Floors` (the highest throw number accepted from each player), `Calls` (waiting to arrive), `Deliveries` (pods and shells standing), `Sentries` (those that end by themselves), `States` (`SendState`; the suits' whole state and Arsenal's mines and areas travel here) and `Started` (the level-start drops that have arrived). The host writes the rows that hold seconds left again when a player joins, so that player's clocks start from now.
- **Events.** Carrier, host to every peer: `Arrived`, `Failed` and `PayloadData` (`SendData`); claiming uses `Claim` (client to host) and `ClaimState` (the host's word to every peer). Arsenal: loan notices (`Loan`), grenade shots (`Grenade`) and bursts (`GrenadeBurst`, 41 bytes, says whether the round was a rocket) are reliable, and the tracking rifle's tracer (`Homing`) is lossy because it is only shown. Exosuits: a granted salvo (`FrameRocketLaunch`) is reliable; shots (`FrameFire`), the shoulder hit (`FrameBump`) and the host's word to a pilot where a hit came from (`FrameHit`: attacker position, damage, barrier or armour) are lossy, because they are only shown; the outcome itself travels in the suit state.
- **Checkpoint copy.** One (`Checkpoint`): the slot book and, after it, the suits as they stand (`CheckpointCopy` in `Carrier/Core/SlotBook.cs`: a 4-byte length, the slot book, then the content parts' bytes). The host publishes an empty one when the level is entered and a full one at each checkpoint; ForgeCore keeps it on every peer, and every peer restores from it on a reload.

Lobby picks and the host's level rules are ForgeCore lobby values (`picks`, `rules`): each player publishes its own, and players who join later receive them.

Add-ons from other mods open their own ForgeCore channel for their own messages; its format fingerprint refuses peers whose add-on messages differ. The registered payload type names are part of this plugin's channel content, so a player missing an add-on is refused too.

## Arsenal content

Supplies and turrets are plain definitions of the carrier's built-in `Item` and `Gear` kinds; a turret's `"Look"` names one of the three looks Arsenal registers with `OrdnanceTurrets.RegisterLook` (`Arsenal:turret-burst`, `Arsenal:turret-sniper`, `Arsenal:turret-shotgun`; code in `Arsenal/Native/ArsenalTurrets.cs`, which also names each look's deploy, idle and expire sounds and its muzzle effect; the carrier plays them for sentries wearing those looks). Code exists only for the three kinds Arsenal adds: `arsenal.Strike`, `arsenal.HeavyWeapon` and `arsenal.Deployable`.

Damage goes through ForgeCore's `Combat` (the game's explosion and fire damage requests), area queries through `Enemies.Near`, players by network lookup through `Level.PlayerByLookup`, the floor under a point through `Level.FloorBelow`, the heavy weapon swap through `Gear.EquipLocal` / `Gear.WriteLocalAmmo`, and the special weapon slot lease through `Gear.SpecialSlot` (the one lease an exosuit's gun also takes).

### Supplies and turrets

| Id | Kind | Contents (item data block ID) |
|---|---|---|
| `arsenal.supply.mixed` | `Item` | Ammo pack 101 x2, medipack 102, tool refill 127 |
| `arsenal.supply.ammo` | `Item` | 101 x3 |
| `arsenal.supply.medical` | `Item` | 102 x2, disinfection pack 132 |
| `arsenal.supply.tool` | `Item` | 127 x2 |
| `arsenal.supply.consumables` | `Item` | C-foam grenade 115 x2 |
| `arsenal.supply.syringe` | `Item` | Health syringe 140 x2, melee syringe 142 x2 (2 uses) |
| `arsenal.supply.mines` | `Item` | Explosive trip mine 139 x2, C-foam trip mine 144 x2 |
| `arsenal.supply.utility` | `Item` | Fog repeller 117, lock melter 116, C-foam grenade 115 x2 |
| `arsenal.turret.watch` | `Gear` | Burst sentry 24 |
| `arsenal.turret.bulwark` | `Gear` | Sniper sentry 23 |
| `arsenal.turret.scythe` | `Gear` | Shotgun sentry 25 |

All three turrets use `AmmoRatio` 1, `Lifetime` 180, `Pickable` false, `FireRateScale` 1.15, `RangeScale` 1.8, `TurnScale` 1.4 and `AcquireScale` 1.4.

### Payload `arsenal.Deployable`

The standing devices (`arsenal.deploy.foam` and `.mines`, both the `Dispenser` kind; `.scanner`, `.floodlight`). They share the turrets' delivery: the turret shell and its base (`OrdnanceDelivery.StartShell`); only the head differs, and `Head` (`Pack:item`) names it, so a new head model changes the definition only. The shell bores in, throws its lids off and the head rises out of it over `RiseSeconds` (hidden `RiseHeight` below until then; the timeline fields and `BaseScale`, the base's size for this head, are the carrier's `BurrowSpec`), the device works, then the head sinks back and the shell sinks away (`Deployables`, every peer; the host runs the work in `DeployableActions` and `Dispenser`). `Kind` picks the work and its fields, all with defaults in `DeployableSpec`:

- `Dispenser`: one flow for every munition, the data says which. The head rises, its plates open like a skirt, its column spins and throws `Count` munitions out of the holes (one after another, each in a short arc to a landing the host chose), the plates shut, the upper part sinks back into the base (which stays), is loaded and rises again (the shell's `OrdnanceShellAppearance.Dive`), `Rounds` times (`Count` times `Rounds` at most 36); then the head sinks and the munitions keep lying for `Lifetime` seconds. `MinRange`, `MaxRange` (landing distance), `FlightSeconds`, `OpenSeconds`, `SpinSeconds`, `SpinTurns`, `ReloadSeconds` set the motion (`DispenserPlan`, the one timeline every peer follows); `Munition` is the munition's own model (`Pack:item`, empty: the strikes' bomblet), `SeatScale` its size in a hole against the size it lands at (it grows in flight), `Plates` how many plates the head has. A lying munition arms after `ArmSeconds` and, when an enemy comes within `TriggerRadius`, bursts: an explosion (`Damage` over `BlastRadius`, pushing `Force`; through ForgeCore `Combat.Explode`, hurts teammates like any explosion) and/or `Spray` pellets of the game's C-foam thrown in every direction at about `SpraySpeed` m/s (Arsenal's `ArsenalCombat.SprayGlue`, the game's own foam projectile, so the game replicates, merges and settles it, on enemies and doors too). At least one of `Damage` and `Spray` must be set. The field is `SendState` data (`DataTag.Mines`, `MineWire`), so late joiners see the munitions still lying. The vanilla trip mine is not used: its only detector is a laser line along a wall mount (no radius to shrink), and its foam charge throws in a cone from that mount; a hand-placed mine or foam mine in a player's hands is unchanged.
  The dispenser is two models: the base (the shell's closed drill and cap, shared by every device) and the `Head`, the upper half, which must carry the nodes below (a model without them is refused when its definition is read): `column` (turns about its up axis, its origin on the axis); under it `plate1`, `plate2` ..., as many as the model has (open like a skirt: turned about their own X axis, hinged at the top edge with the origin on the hinge, the bottom edge swinging outward toward local -Z, 70 degrees open); `mount1`, `mount2` ... (an empty node at each hole, its -Z outward, under its plate so it swings with it; munitions take them in number order, so number them plate after plate round the column from the plate at the model's front, clockwise from above, the first hole of every plate and then the next: one round then never repeats a hole). `Articulation` (`Arsenal/Native/Mechanisms`) poses them and `Thrown.Arc` draws the flight. A munition lying in the field is a `MineWire` record that carries the definition's hash, so a late joiner or a new host draws and arms it by the definition that threw it.
- `Scanner`: `Range`, `Scan`, `UnfoldSeconds`. The head may have `petal1`, `petal2` ... (modelled open, folded 42 degrees about their own X axis) and a `yaw` part that turns: it rises folded, unfolds over `UnfoldSeconds` and only then works and turns, and folds again before it sinks. The host tags every untagged enemy in range that is awake (`ForgeCore.Enemies.Alert` is Awake or Hunting; sleepers are skipped) with the game's own tag (`ToolSyncManager.WantToTagEnemy`), so every player sees the usual markers.
- `Floodlight`: `LightRadius`, `FogRadius`, `Intensity`. A `fan` part of the head keeps turning about its own up axis. A ForgeVisual point light on every peer, and the game's own fog repeller item (item 117, stood as an instance) whose sphere is resized on every peer.

Code prefixes: supplies start with up, down. Turrets start with left, up, right. Standing devices start with left, down, left. Strikes start with right. Heavy weapons start with down, left. A test keeps the shipped codes clear of each other and of the suits' codes.

### Payload `arsenal.Strike`

```json
{ "Type": "arsenal.Strike", "Kind": "Area", "Damage": 180, "Shells": 7, "Spread": 15 }
```

Unknown fields, fields of another kind, non-numbers and values out of range are refused, and the log names the field.

| Kind | Fields (default) |
|---|---|
| `Precision` | `Damage` 600, `BlastRadius` 5, `Force` 800 |
| `Area` | `Damage` 180, `BlastRadius` 4.5, `Force` 500, `Shells` 7, `Spread` 15 (m), `Duration` 3.5 (s) |
| `Creeping` | `Damage` 160, `BlastRadius` 4.5, `Force` 500, `Shells` 3 (per row), `Rows` 5, `Length` 5 (m between rows), `Width` 10, `RowInterval` 0.8 (s) |
| `Cluster` | `Damage` 90, `BlastRadius` 3.5, `Force` 300, `Shells` 10, `Duration` 1.5, `Length` 20 (line across the throw) |
| `Incendiary` | Impact `Damage` 80, `BlastRadius` 3.5, `Force` 300. Area `ZoneRadius` 6, `Duration` 10, `Tick` 0.5, `TickDamage` 3 |
| `Gas` | `ZoneRadius` 7, `Duration` 12, `Tick` 0.5, `TickDamage` 2.5 |
| `Stasis` | `ZoneRadius` 8, `Duration` 20, `Slow` 0.2 (share of its own speed an enemy keeps, 0.05 to 0.95) |
| `Beam` | `Spread` 18 (the radius around the beam's current point in which it looks for enemies, and the longest navigation path to one), `Duration` 10, `Tick` 0.2, `TickDamage` 6, `Speed` 4 (m/s) |
| `Strafe` | `Damage` 150 (per enemy hit), `Length` 30, `Width` 4 (metres), `Speed` 30 (m/s the run advances), `Tick` 0.1 |
| `Hunter` | `Spread` 25 (search radius), `Damage` 1050 (to the target), `TickDamage` 60 (splash), `BlastRadius` 3, `Force` 800 |

How a strike runs:

- When the beacon lands the host fixes the call's direction (thrower toward beacon, at that moment) and sends every peer the ground the call can hurt (`StrikeFootprint`, `DataTag.Danger`): a hazard band at the true radius on the floor, or the outline of a strip cut at the walls, held until the last shell. A player standing on it who can be hurt by it gets a notice on the call-in list. The gun run and the stasis field are drawn too but tell nobody to run; the beam and the hunter have no fixed ground.
- The host builds the shell pattern from the summoner and throw, brings each point back to this side of the first wall between it and the beacon, drops it onto the floor, and applies damage when each shell lands. Each shell first shows its reach (a ring at exactly its radius, a glow filling it), then falls, gaining speed (`StrikeTiming.FallCurve`), nose first; fired shells on a slant of about 12 degrees from one side per call (`StrikePlan.ApproachSide`, derived on every peer). `Arsenal/Core/StrikeTiming.cs` holds these seconds per kind (`Pace`: warn + drop, from 0.35 s for Hunter to 1.3 s for Precision) and every other time below.
- Gas areas start 0.4 s after landing, incendiary 0.25 s, stasis 0.3 s. The beam charges for 1 s before it moves, and `Duration` counts from then.
- The beam only picks enemies it can reach on the navigation mesh (a complete path no longer than `Spread`) and see from above. It follows that path at constant speed, checks every move against the level, drops a target it can no longer reach, and holds still when none is left.
- A strafing run starts 1.2 s after its call arrives and runs from the beacon along the throw direction (flattened to the ground plane) up to the first wall ahead; an enemy beside the strip behind a wall is not hit. Every `Tick` the host sends one game bullet hit (`Combat.BulletDamage`, so armour and weak spots apply) to each enemy standing in the strip between the head's last and new place, so each is hit once as the head passes; there is no explosion. Peers draw a tracer streak with its ground point and scorch marks from the host's `Strafe` record (no aircraft).
- Hunter picks its target with `ArsenalCombat.Largest` (highest maximum health within `Spread` of the beacon among the enemies whose course node is the beacon's landing node, never one in another room; no node, no target; an enemy in sight of the beacon before one that is not, nearer wins ties). A laser (`TargetLaser` (`Arsenal/Native/Mechanisms`), every peer following the host's reports 10 times a second) stays on it for 1 s, moving to the next largest if it dies, then the shell drops in 0.35 s (`StrikeTiming`); `Damage` goes straight to the target (always in the landing room), `TickDamage` is `ForgeCore.Combat.AreaDamage` around the impact, hurting only enemies of the landing room (no blockers query, no teammates). With no enemy the laser sweeps around the beacon for 5 s, picks up any enemy that comes in reach, and otherwise flickers out over the words "No target": no shell, no damage, the use comes back and 30 s of cooldown stay (`OrdnanceApi.RefundCall`), and the caller gets a notice. A target that walks out of the room is dropped like a dead one.
- Stasis slows enemies in the field to the `Slow` share through ForgeCore `Enemies.Slow` (it scales the animator speed): one lease per enemy per field, taken while the enemy is inside, ended when it leaves, when the field ends, or when the level, a checkpoint or the host changes. Overlapping fields take the slowest share and do not stack; ForgeCore puts the enemy's own speed back when the last lease ends or the enemy dies or is reused, and leaves a speed the game set meanwhile alone. Arsenal keeps the share, radius, duration and the rule that enemies held by C-Foam are skipped. Arsenal patches no enemy lifecycle method itself.
- Explosions use the game's explosion damage, so falloff, wall blocking and friendly fire follow the game. Fire and gas use the game's fire damage entry, on what is inside the radius and in sight of the centre (a wall stops them, an open door does not), a teammate at 0.4 of the tick. The stasis field stops at walls the same way (`Enemies.SlowZone`'s `admit`).
- Every peer draws shells, blasts, areas and the beam from data it already has; effects send no messages. Arsenal owns what they look like (the moments in `Arsenal/Native/StrikeFx.cs`, the strafing streak and the area marks in `Arsenal/Native/StrikeEffects.cs`) and ForgeVisual draws them: blasts, shell trails, area fields and the beam are slots of `ArsenalFxSlots` (a slot the table does not list logs its error and draws nothing, no moment has a second look); warnings, reach rings, a bomblet's small blast and a dropped canister's tracer are sprites of its shared pool (`SpritePool`; the sheets are registered as `Arsenal:smoke`, `Arsenal:fireball` ... when the bundle loads, and smoke is the sheddable sheet the overdraw budget gives up first), scorches and beam burns use its decal rings (`SpritePool.CreateDecals`), short flashes and the area and beam lights its shared effect lights `PointLights` (flashes and held lights; the player's ForgeVisual `Effects / FlashLights` turns the flashes off), the shell landing shake its `CameraShake`, a canister's tracer `Trails`, area ground marks and the strafing streak's reticle `GroundMarker` (gas `Hatched`, fire `Flames`, stasis `Dial`, streak `Reticle`), and the streak's lines `Beams`.

### Payload `arsenal.HeavyWeapon`

```json
{ "Type": "arsenal.HeavyWeapon", "Model": "Arsenal:heavy-mg", "Gear": 38, "AmmoRatio": 1 }
```

| Field | Meaning |
|---|---|
| `Model` | A ForgeVisual model, `Pack:item`. Any pack works, so a third party adds a heavy weapon with its own pack and a definition (the general steps are in [Writing a content pack](#writing-a-content-pack)). Checked when the definition file is read, not at the first shot: the pack must already be registered (in the owning plugin's `Load`, before definitions are read), otherwise the definition is refused naming `Model`; the model's own `RightHand` grip (`SightLook` below is optional) sets the hold; a missing right grip is the model's origin and a missing left hand is derived from the game's own hold. A pack that is missing does not load the weapon in a lesser form, the definition simply is not loaded. A refused definition holds no hash. In the weapon list the lent weapon carries the name of the first definition (ordered by Id) that uses the model, as `OrdnanceApi.DisplayName` gives it in this player's language; which definition names the model is the same on every peer. |
| `Gear` | Offline gear ID or name of a special-slot weapon. Shipped: machine gun 38 (`MachinegunAuto`), rifle 42 (`HELRifleSemi`), launcher 43 (`HELGun`: two-handed, 9 rounds, 3.3 s reload, no charge-up on the lent weapon, with its bullet replaced by a grenade). |
| `Count` | 1 or 2, default 1: how many of the weapon lie in one pod, one per weapon bay; the pod leaves once all of them are taken. |
| `AmmoRatio` | 0 to 1, default 1: the reserve given besides a full magazine, as a share of the slot's full reserve (0 is the magazine alone, so a one-round gun goes back after its shot). |
| `Muzzle` | `none`, `mg` (default) or `amr`: the library muzzle flash drawn when the gun fires; `none` for a weapon whose effect is drawn elsewhere. Shipped: anti-materiel rifle `amr`, tracking rifle, grenade launcher and recoilless rifle `none`, the rest `mg`. |
| `RecoilScale` | 0.1 to 5, default 1: how hard the gun kicks, as a share of the lent gear's own recoil block (`ForgeCore.Gear.ScaledRecoil`). Shipped: machine gun 1.4, anti-materiel rifle 1.6, recoilless rifle 1.5. |
| `Homing` | Optional `{ "Angle", "Range" }` (defaults 12 degrees, 60 m). When the local shooter fires, `ArsenalCombat.PickAimed` locks the enemy nearest the crosshair inside that cone and range with a clear line to it, the game's bullet is withheld (`BulletHit` hook, as for the grenade launcher) and the hit is resolved from the muzzle to that enemy through `Combat.Shot` (the game's bullet resolution: limb, weak spot, armour, replication, hit marker), so it hits even where the straight ray would hit air. The shooter's peer decides the hit as for a stock bullet and the host settles the damage; the one `HomingShot` message (origin, target, seed) only lets every peer draw the curving tracer (Arsenal's `ArcTrails`), which decides nothing. A shot with no enemy in the cone is the game's bullet, untouched. |
| `Grenade` | Optional `{ "Damage", "Radius", "Speed", "TrueDamage", "EdgeShare" }` (defaults 140, 4, 32 m/s, false and 0.5; unknown members are refused). `TrueDamage` makes the blast `ForgeCore.Combat.AreaDamage` (host only): each living enemy in reach that the centre can see takes the damage at its body's own damage request, no armour or weak spot step runs and no enemy field is written (other guns hitting the same enemy are reduced by its armour as ever); the damage falls off linearly from `Damage` at the centre to `EdgeShare` of it at `Radius`; players are not hurt. Without it the blast is the game's explosion (`Combat.Explode`). With it the weapon fires grenades instead of bullets, host-checked as described below; without it the weapon fires its own bullets. |

Model contract (the shipped models are `Arsenal:heavy-mg`, `Arsenal:anti-materiel-rifle` and `Arsenal:grenade-launcher`; those names are stable): the root carries an optional empty `RightHand` (the right grip; without it the model is held at its origin, the authoring grip), an optional `LeftHand` (its own palm point; ForgeVisual derives the left hand and leaves this node where it is) and optionally `SightLook` (the centre of the sight window, forward = the model's front; without it the game's own sight aims), plus a `Muzzle` at the bore's end, which `GunMount` uses; the model is built in real metres and its size in the hand is ForgeVisual's gun-holding rule (`GunHold`), the same for every gun: no gun here has a size of its own. `Arsenal/Data/gun-grips.json` (ForgeVisual `GunGrips`, embedded) gives each model the `machinegun-2` game hold preset of gear 43, not hand points: the mount keeps the model's own hand sockets (a missing right grip is its origin) and derives a missing left hand from that hold, and it pulls the left hand back along the gun into the arm's reach; a row's optional `adjust` is the only per-gun hand fine-tune. The lent gear's public name is `Forge Heavy ` and the model id's 32-bit FNV-1a hash in 8 hex digits (20 characters; the repository's other markers assume about 25 at most), filled in when a definition is accepted; two ids with the same hash are refused naming both. The rack record carries the same hash, and a record whose hash this install never accepted is dropped with one error line (every peer needs the same heavy weapon packs).

How it works:

- The weapon stands upright in one of the pod's two weapon bays (chosen by throw ID, the same on every peer).
- A player within 2.4 m, looking at the weapon, gets an interaction prompt. The use key makes the claim through Ordnance's `OrdnancePod.AddClaimable` (reach, aim, prompt, request, reservation, timeout and messages are Ordnance's); Arsenal supplies the prompt text, the host check (one loan per player, no borrowed gear, effects ready), the install, and the host verification that builds the loan. Committing starts the loan; Arsenal's `LoanBook` keeps loans and stashes, and returns travel as a small `LoanNotice` on the mod's channel.
- The loan saves the player's special weapon and ammo, equips the lent gear with a marker name (the packet name stays the marker; `ArsenalText.NameHeavyWeapons` registers each marker with `UIKit.GearName`, so ForgeUI shows the call-in's name from the definitions' `lang` folder on this player's weapon list), and fills the ammo to `AmmoRatio`. The payload may set absolute archetype numbers (`Damage`, `ShotDelay`, `Cooldown`, `Chargeup`, `Magazine`, `Reload`, `PrecisionMultiplier`, `StaggerMultiplier`, `Pierce`, `AmmoCost`, and `FalloffStart` / `FalloffEnd`, the metres where the damage falloff begins and bottoms out, written together with the end beyond the start; unset ones stay the game's; the grenade launcher fires no bullets and ignores both falloff fields; the rack record `RackBytes` is 108 bytes and carries the tuned numbers and the pod's weapon count, so every peer needs the same build): `HeavyWeapons.Tune` gives the lent instance its own copy of the archetype with them applied and reads it back (the shared block, and the player's own gun of the same model, are untouched), and the rack record carries them to every peer. The host's launcher budget and cadence come from the same `ArchetypeTuning.Over` of the vanilla block. It ends when the clip and reserve are both empty (the heavy weapon is gone), on the drop key, or when the player goes down; the own weapon is then put back with its saved ammo.
- Switching: with the special weapon in hand, `InputAction.SelectSpecial` (the player's own binding, not while reloading, with a 0.5 s cooldown) swaps through the game's own `EquipLocalGear`, so the swap replicates the usual way. The heavy weapon's magazine and reserve wait in a stash, the own weapon's in the loan's restoration record; each is written back once its gear has assembled in the slot. The switching peer tells the host (`LoanAction.Stashed`) before the swap; `LoanBook.Observe` then keeps the loan while the own weapon is in the slot and takes the heavy weapon back once the marker returns. The stash never survives a checkpoint reload, a host change keeps it, and drop, down and level end clear it.
- Ammunition packs: ForgeCore's special slot (its hooks on `PlayerAmmoStorage.PickupAmmo`) swaps the pool for the saved own weapon's reserve around the game's pickup while the heavy weapon holds the slot, so the game's caps and rounding apply to the own weapon and the heavy weapon's pool is put back untouched.
- Grenades need a host-issued loan and an increasing shot number. The host checks the held weapon, the origin, the fire rate and the remaining ammo, then launches from its own view of the player. Clients only predict flight: the shooter's round shows at once with its own muzzle flash and sound, the host's broadcast replaces it without showing those again, and a round the host did not answer within 1.5 s is removed. The host's burst position decides the explosion for everyone.
- Checkpoint reloads and host changes revoke launcher firing. Installed loans keep their recovery data and can be returned.

### Arsenal settings

Arsenal has no setting of its own. The give-back key is the ForgeUI key action `DropHeavyWeapon` (`Arsenal/Native/ArsenalKeys.cs`, G, context `on-foot`), changed in the game's settings (Controls) and saved in `NAinfini.ForgeUI.keys.cfg`.

### Arsenal audio

`Assets/Arsenal/Audio/cues.forge` follows the ForgeAudio format. Each cue lists layers of Ogg clips, a bus and a length cap. The four boom cues (small boom, big boom, cluster split, grenade burst) have their own far layer from 40 m. Sound sources and licenses are in `Assets/Arsenal/Audio/LICENSE.forge`.

Loops (fire, gas, stasis and beam) are attached to their owner and fade out in 150 ms when it is destroyed. A cue that fails to load is logged by name and only that cue goes silent. The devices' loops (dispenser spin, floodlight fan) are `Sustain`s that end with their device or when the part stops; the scanner sounds once per scan (`radar.sweep`, a 0.8 s one-shot), when the scan goes out. The glue munition's spray plays the game's own glue gun shot (`GLUEGUNFIRE1SHOT`) through `AudioKit.PostGameEvent`, not a cue; the shell's lid blast (`turret.lid_blast`) is a carrier cue.

`tools/Arsenal/build_audio.ps1` rebuilds the clips from `sources.forge` using the carrier's `tools/Carrier/stage_audio.py` and ForgeAudio's converter. No Unity build is needed for audio.

### Arsenal art

`forge-ordnance-arsenal.bundle` holds the models (the three turret looks included) and the strike effect textures; it is registered and loaded through ForgeVisual (`Bundles.Register`, `SpriteSheet.Register`), and the heavy weapon models are put on the game's gear with `GunMount`. Audio is never put in the bundle.

| Prefab | Model |
|---|---|
| `shell`, `shell-precision`, `shell-hunter`, `shell-incendiary` | The strike shells: area, precision, hunter, incendiary |
| `bomblet` | Cluster bomblet |
| `rocket`, `grenade-40` | The heavy weapons' rocket and 40 mm grenade in flight |
| `gas-canister` | The dropped gas bomb, standing nose down (origin on its nose tip) |
| `stasis-emitter` | Stasis emitter |
| `mine`, `glue-mine` | The dispenser's munitions |
| `turret-burst`, `turret-sniper`, `turret-shotgun` | The turret looks; `barrel` under `head` (the burst's spins, the others slide) |
| `dispenser`, `radar`, `floodlight` | The standing devices |
| `recoilless` | Recoilless rifle; in game it wears ForgeVisual's `red-dot-1x` on its top block (`Arsenal/Data/gun-sights.json`) |
| `heavy-mg` | Minimi with an open reflex sight on its top cover (carry handle and sights taken off), the ammo box is the separate `Magazine`; in game ForgeVisual's `holo-1x` takes the reflex sight's place |
| `anti-materiel-rifle` | B&T SPR 300 with an open reflex sight on its top rail, the magazine is the separate `Magazine`; in game ForgeVisual's `scope-2x` takes the reflex sight's place |
| `grenade-launcher` | RG-6 revolver grenade launcher, open reflex sight on its top tube; in game ForgeVisual's `red-dot-1x` takes its place |
| `tracking-rifle` | Honey Badger with an open reflex sight; in game ForgeVisual's `holo-1x` takes its place |

In game every heavy weapon wears a ForgeVisual sight (`Arsenal/Data/gun-sights.json`, ForgeVisual `GunSights`; its DEVELOPERS): `holo-1x` on the machine gun (fast pickup while it sweeps) and the tracking rifle (it locks on, it needs view more than magnification), `scope-2x` on the anti-materiel rifle (long precise shots, aimed at the 2x field of view), `red-dot-1x` on the grenade launcher (small, keeps the drum and tube in view) and the recoilless rifle (its own `SightLook` hung 7 cm left of the tube with no optic, so aiming looked past the tube's edge). Each row stands the sight's base on the highest part of the gun under it, with the line through the window clear of the gun from 0.3 m behind; the reflex sight and `reticle` below are replaced. Each heavy weapon model with an optic has the empty `SightLook` at the centre of its sight's window (identity rotation, so its forward is the model's front) and a small emissive `reticle` there. When the look goes on a weapon, `GunMount` points the gear's `GearPartHolder.m_aSightLook` and `ItemEquippable.SightLookAlign` at it, so aiming lines the camera up with the model's own sight instead of the hidden game sight. A model without `SightLook` (the recoilless rifle) aims with the game's own sight and logs nothing. The sight has no glass (the gear shader is opaque), so its window is open. The game's item camera narrows to 15 degrees while aiming, so a sight's size on screen is set by its distance from the aim camera: the built-in sights are 50 to 60 percent of the download's size and stand forward of the grip, so the block under the window stays within about 3 degrees of the centre. `tools/Arsenal/fit_sight.py` puts the sight on a gun master (Blender, background): it cuts the sight's own base plate and lever block away, stands the housing straight on the highest part of the gun under it (no riser), stops when any part of the gun shows through the window from 0.3 m behind it (it prints the `import_body.py --remove` boxes that take that part off), and adds the reticle and `SightLook`; the arguments used for M08–M10 are in its header. The three gun bodies are textured Sketchfab downloads turned into masters by `tools/Arsenal/import_body.py` (front −Y, grip at the origin, the bore on x = 0, scope parts removed, reduced to about 19,000 triangles when larger; `--split` makes the separate `Magazine` object and `--socket` the `RightHand` and `LeftHand` palm points). The gear shader has no occlusion input, so the occlusion is multiplied into the base colour, and the normal and metallic/roughness maps are kept.

Authors and licenses are in `Assets/Arsenal/Models/LICENSE.forge`, which ships in the package. Effect textures are CC0. Every definition names an icon `<group>-<name>.png` that sits beside `arsenal.forge`. The icon source is `Assets/Arsenal/icons/` (local media, not in Git): the owner's hand-made icons go there as `<group>-<name>.png`, 256 x 256 transparent PNG, and replace the rendered ones; the package script reads this folder.

How a heavy weapon model sits in the hands: ForgeVisual's `GunMount` (its DEVELOPERS), the same calculation as every gun model. Arsenal gives only the model, `Front` and its game hold preset (`Arsenal/Data/gun-grips.json`); the model's `RightHand`, optional `LeftHand`, `muzzle`, `SightLook` and `Magazine` are its data. Its size, both hands (the model's own sockets, the origin for a missing right grip and the game's own hold for a missing left hand, with the left pulled back along the gun into reach) and the first-person hold come from ForgeVisual's `GunHold` rule; the recoilless rifle's back-blast is measured from the tube's shown length (`GunMount.Hold.ShownLength`). A model or game gun that cannot be mounted leaves the game's gun showing (one error in the log).

A level drop may set a heavy weapon's AmmoRatio (0 to 1), Magazine (whole, 1 to 1000) and AmmoCost (0.01 to 1000, the reserve one shot spends, the block's CostOfBullet) through its Overrides; the rounds given are floor(AmmoRatio * the slot's full reserve / AmmoCost) + Magazine, with the native numbers behind them on every peer, and `OrdnanceControl.Racks()` reads all three back (`default` where the block's own stands); `arsenal.Strike` accepts Direct (same as Drop for a developer drop). See [Level control for rundown authors](#level-control-for-rundown-authors).

## Exosuit content

Suits are data; code exists only for the mechanics. The cockpit is built with ForgeUI mechanisms, drops and call-ins go through the carrier, effects through ForgeVisual. To add a suit, write a suit definition with its model, icons and sounds.

### Effects and shared mechanisms

- **Particles.** Every moment is a slot of `FramesFxSlots` (a charge's dust is `frame.charge.dust`, a wreck's smoke `frame.wreck.smoke`); the code builds no effect of its own; a slot the table does not list logs its error and draws nothing. The moments in a suit's light colour (thruster, charge flame, kick and landing rings, barrier pulse, hit and break, siege ring, ram) are one slot each, resolved once in `FramesFxSlots`, and played with the suit's `Light.Glow` as the colour passed to `Fx.Play` / `Fx.Hold`; their layers are converted with `@tint` (`Tools/Vfx/FxLibrary/fx-adjust.json`) and take that hue at their own brightness. `sirius.dash` is empty: a charge shows its back flame (`frame.charge`) and the hit (`sirius.ram`).
- **The game's own effects.** The tracer (`Fx.Tracer`) and the rocket explosion (`Game:FX_Tripmine`, `Fx.Play`) go through ForgeVisual's catalogue; `FrameFx` keeps the handle.
- **Barrier.** What it stops: an enemy's melee hit by where the attacker stands (inside the dome or out), and an enemy projectile by its flight: on every peer, a live projectile from the game's own list that was outside a raised dome the last frame and is inside it now is ended through its own lifetime and never reaches anyone inside; the host charges the barrier with the projectile's damage (`Frames/Native/BarrierProjectiles.cs`). Projectiles fired inside the dome pass. No standing dome in the world or on the pilot's screen: it is the armour bar's barrier line, its sounds (`FrameVisual.TickBarrier`: raised, broken, hit when its strength drops), the visor's interference on a stopped attack, a short flash around the suit's body on a hit and when it breaks (`frame.shield.hit` / `frame.shield.break`, on the other peers only: the pilot's cockpit shows the hit on its edge), and the push pulse, a ring along the floor out to the dome's radius (FrameVisualFx). `frame.shield.field` stays empty.
- **Charge, seen by others.** While a Boost lasts every peer leans the suit's spine and chest forward after the pose copy (`FrameVisual.ChargeLean`, from the replicated record, nothing networked); when the pilot's breach hits a door or an enemy, `LocalPilot.Bump` (at most one per 0.6 s) plays the right-shoulder bash and `FrameFx.Bash` (the slot sirius.ram) and sends `FrameBumpMessage` (lossy) so the others do the same. The pilot's own camera is never turned.
- **Worn suit, shell and leaving motion.** The body mechanisms are ForgeVisual's (`Models.CopyPose`, `Models.MirrorHeld`, `Models.WearFirstPerson`, `Models.BindBodyClip`; see its `DEVELOPERS.md`); the exosuit code passes its rig. `Frames/Core/FrameBodyRig.cs` holds the suits' bone map: `PoseLinks` maps the 16 suit bones (pelvis, spine, chest, head, upper arm, forearm, hand, thigh, shin, foot) to the game's humanoid bones (the game's upper chest and neck fold into the suit's chest; it has no shoulder, finger or toe bones), `ArmReach` bends both suit arms so each wrist lands where the body's does from the chest (the suit's arms are not the body's scaled, so turns alone leave the hands off the grips), and `ShellBones` turns a definition's `Shell` into the first-person shell's bones (the upper arms only line the skeletons up, each leg bone is sized by its own leg, the rest by the shoulder span; the opening parts stay shut on the shell). `Frames/Native/FrameVisualPose.cs`: every peer, after the game animated the pilot (in `LateUpdate`, and again after that body's `PlayerSyncIK.UpdateIK` when it ran later: `PoseAfterIK` in `PilotHooks.cs` subscribes to ForgeCore's `GameHooks.SyncIkSolved` with the first pilot; this package patches no game method for it), the suit takes its pilot's body pose and holds a copy of the pilot's wielded item in its right hand; that copy's muzzle is the origin of the flash and tracer. The local pilot's suit and its held copy are drawn as shadows only; empty suits stand in the rest pose the bundle saved. `LocalPilot` builds the shell (`Models.WearFirstPerson`) during the elevator ride or a call-in's countdown and lights it in the suit's colour (`_EmissiveColor`). `PilotBody` loads the bundle's `pilot-motion` rig and its PilotExit clip, checks them against `PilotMotion`, and carries the camera on the eye point of the posed head.
- **Light strips.** `Glow.For(renderer)` per renderer; `FrameGlow` decides the colour.
- **Collider query.** The breach charge uses ForgeCore's `ColliderQuery(64, grow: true)`: a full buffer is replaced by a larger one and the query repeated, so the result is complete.
- **Readouts and settings.** The cockpit's zone, heading and height lines read ForgeUI's `UIKit.Location`. The keys are ForgeUI key actions (`Frames/Native/FrameKeys.cs`): `Exit` (G, hold), `Special` (V) and `Rockets` (B), all in the context `exosuit` so they may share keys with on-foot actions; the player changes them in the game's settings (Controls) and they are saved in `NAinfini.ForgeUI.keys.cfg`. Boarding is the game's own Use key.
- **Sound values.** Pitch and gain of a cue are in `audio/cues.forge` (the planted gun's bolt: pitch 1.35, gain 0.88 x 0.55; the walking step: gain 0.25, no sound of its own at the start of a sprint); only the rising tone of the power-on blocks is set in code (`FrameAudio.Section`), because it follows play order.
- **Noise.** Enemies hear a piloted suit `FrameNoise.PilotMultiplier` (2) times as far as an ordinary player, in every movement state. The game gives a player no noise radius of its own: a walk, run, jump or landing is heard inside the enemy's own `movementDetectionDistance` (the default detection block's is 8 m) and a crouch inside a fixed 1.9 m. While a pilot wears a suit, `FrameUnit.HostPilot` holds a ForgeCore `PlayerNoise.Scale` lease on that player (host only, where enemies detect), so the game's own noise for the pilot is heard twice as far (crouch 3.8 m, walk and run 16 m with the default detection); ending the pilot or the level ends the lease. The suit adds no footstep noise and has no noise fields in its definition. A suit's own landing has no pilot to hear it, so the touchdown impact carries `OrdnanceImpact.NoiseRadius` = the multiplier times the default detection block's `movementDetectionDistance`, read from the game (`Noise.LandingRadius`, 16 m with the stock block; no block is an error line and no noise); the carrier makes it on the host only, and a suit found already standing lands with no noise.
- **Effect lights.** The destruction flash is the light of the `frame.explode` slot (`fx-slots.forge`), so ForgeVisual's `Effects / FlashLights` turns it off with every other flash. The wreck's fire and the planted siege lamp are ForgeVisual `PointLights` held from `TickFxLight`; held lights have no switch, as in ForgeVisual. The siege lamp lights only on the other peers: it sits half a metre from its own pilot's eye, where the game's fog turned it into a large red glow over the right of the view.

### Suit definition

Payload type `frames.Exosuit`. The carrier's definition fields (`Id`, `Name`, `Code`, `Cooldown`, `Uses`, and so on) work as in [Writing call-ins](#writing-call-ins).

```json
{
  "Type": "frames.Exosuit",
  "Model": "Frames:frame-m15",
  "Armor": 100,
  "DamageReduction": 0.25,
  "EyeHeight": 2.15,
  "Movement": { "Walk": 1.2, "Sprint": 1.3, "Jump": 1.6, "SprintSeconds": 6, "SprintRecovery": 8 },
  "Weapon": { "Gear": 38, "DamageMultiplier": 1.6, "Magazine": 100, "Reserve": 400, "RecoilMultiplier": 0.45, "ReloadMultiplier": 0.75 },
  "Light": { "RangeMultiplier": 2, "AngleMultiplier": 1.4, "IntensityMultiplier": 1.2, "Colour": "#FFEBD1", "Glow": "#22D3FF" },
  "Boost": { "Seconds": 3, "SpeedMultiplier": 1.8, "Cooldown": 25, "BreachDoors": true, "Shove": 20 },
  "Opening": { "Parts": [ { "Node": "plate.back.L" }, { "Node": "plate.back.R" } ] },
  "Shell": [ { "Bone": "upperarm.L", "Body": "LeftUpperArm" }, { "Bone": "pelvis", "Body": "Hips" } ]
}
```

Unknown fields, wrong types and out-of-range values are errors that name the field. The carrier then skips that definition.

| Field | Meaning |
|---|---|
| `Model` | The suit's prefab as `Pack:item`, a ForgeVisual model of any registered pack (this mod's own pack is `Frames`; the shipped suits are `Frames:frame-m15` to `Frames:frame-m18`). It must meet the model contract below, or the definition is rejected before a call can spend a use. |
| `Armor` | The suit's only health. Never regenerates and no item heals it. At 0 the suit is wrecked. |
| `DamageReduction` | 0 to 0.9. Share of every hit the armor stops. |
| `EyeHeight` | Camera height above the feet while worn, in metres. |
| `Movement` | Optional. `Walk` and `Sprint` 1 to 3, `Jump` 1 to 4 (times the game's jump height). Never below the player on foot. Defaults: Walk 1.1, Sprint 1.2, Jump 1.3, SprintSeconds 5, SprintRecovery 8. |
| `Weapon` | `Gear` (required) is an offline gear ID of a special-slot weapon, lent into the special slot under the game gun's own name; the special key pressed while holding that slot swaps it with the pilot's own special weapon. `Magazine` (required) and `Reserve` are the suit's whole ammo; nothing refills it. `DamageMultiplier` applies to this gun only. `RecoilMultiplier` 0.05 to 1 and `ReloadMultiplier` 0.2 to 1 (default 1) scale the recoil and reload time of any gun the pilot holds while piloting, the pilot's own included. |
| `Light` | Optional. The suit's lamp. Defaults: range x2, cone x1.4, intensity x1.2, warm white. `Glow` is the colour of the light strips and the visor while the suit is worn and healthy, default cyan. An empty suit breathes at 12-22% of it (0.3 Hz); armour at or below 25% turns red (one flash as it drops); overheating pulses red-orange at 2 Hz; opening for a pilot shows the colour a quarter of the way to white; power-on after boarding flashes twice; a wreck flickers for 0.5 s and goes dark (Core `FrameGlow`). The build paints the strips and the visor into `emission.png` in grey (strips 0.8, visor up to 1; `tools/Frames/glow.py`), so a new model gets them from its own shell; `VISORS` and `VISOR_PANES` there draw the visor by hand for a head with no readable face or an open hole. |
| `Boost` | Sirius (assault) special: a charge. Standing still it runs where the pilot looks (any movement key takes over); each bump (door or enemy, at most one per 0.6 s) slows the pilot for 0.12 s. `Shove` is the melee damage an enemy takes once per charge. |
| `Siege` | Thunderbird (firepower) special: `{ "DeploySeconds": 1.2, "DamageMultiplier": 1.2, "DamageReduction": 0.1, "FireRateMultiplier": 2, "ReloadMultiplier": 0.5, "RecoilMultiplier": 0.5 }`. A definition cannot have two of `Boost`, `Siege` and `CarryHeavy`, because all three use the special key. |
| `Scan` | Nightowl (scout) special: `{ "Radius": 30, "Interval": 5 }`. |
| `Shield` | The barrier: `Capacity`, `RegenDelay`, `RegenRate`, `Radius`, `RaiseFraction`, `WhileSieged`. A shield with `WhileSieged` needs `Siege`. |
| `CarryHeavy` | Taishan (tank) only: the suit can carry heavy objects. |
| `Rockets` | Thunderbird: `Salvo`, `Damage`, `Radius`, `Spread`, `Cooldown`, `Range`, `Speed`, and `Launcher`: `Pod` (the launcher prefab as `Pack:item`, its tubes `muzzle_1`..`muzzle_N`), `Round` (the rocket prefab as `Pack:item`), `Bone` (the one launcher rides this bone, usually a plate that lifts when the suit opens), and `Position` and `Rotation` in the suit's own space at its rest (bind) pose: x right, y up, z forward, metres and degrees; never scaled. A salvo aimed closer than `Spread` + `Radius` + 0.5 m is refused on the pilot's machine: its blast would reach the suit. |
| `Opening` | Optional. The bones of the model's parts that open for boarding (`Node`, up to 64, unique; the helmet is not listed; `Seam: true` marks a part whose gap drives the steam). How they move is the model's own `open` clip; the game plays it forwards to open and backwards to close. |
| `Shell` | Optional. Maps model bones to the player's first-person body bones for the first-person view of the suit's own arms and legs. A new model changes only this list and `Opening`. |

#### Adding a suit from another mod

A third-party mod adds a suit with a ForgeVisual pack, a definition and (optionally) a cockpit mark. The common flow (writing a content pack, the definition file, `Callable`, `Place`) is in [Writing a content pack](#writing-a-content-pack) and [Developer drops](#developer-drops-for-level-authors); only what is particular to suits follows. No C# interface is needed for this.

1. **Register the pack.** In the plugin's `Load`, call `ForgeVisual.Bundles.Register(modId, "MyPack", path)` right away (not at level build), so admission, which runs at definition load, finds the prefab. `Model` is then `MyPack:walker`. A pack that is missing, or an item it does not hold, rejects the definition with `Model` named.
2. **Model contract.** The prefab holds a skinned renderer and the 16 rig bones named exactly as in `FrameBodyRig.PoseLinks`: `pelvis`, `spine`, `chest`, `head`, `upperarm.L`, `forearm.L`, `hand.L`, `upperarm.R`, `forearm.R`, `hand.R`, `thigh.L`, `shin.L`, `foot.L`, `thigh.R`, `shin.R`, `foot.R` (no neck, shoulder, finger or toe bones; the pilot's own body drives them, so the names are fixed; the `head` node is also where the name plate sits). Every `Opening.Parts[].Node`, `Shell[].Bone` and `Rockets.Launcher.Bone` must be a node of the prefab. With `Opening`, the prefab also carries a legacy `Animation` with the clips `open` and `close`. A missing node, bone or Animation rejects the definition and names the field. The clips cannot be listed on a prefab that was never instantiated, so a missing `open` or `close` clip is found when the suit is first shown and then writes one error line per model (`lacks its 'open' or 'close' clip; it does not visibly open`); the suit still works, it just does not visibly open.
3. **Cockpit mark.** The mark is the ForgeUI icon `mark-<item>` in the icon pack of the same name as the model's pack (`MyPack:mark-walker`). Without it admission writes one error line and the name plate and silhouette show no mark; the suit still works.
4. **Name.** The suit's name in the cockpit is the definition's name (`Name`, or its language key), shown in capitals; `FrameRecord.DefinitionKey` (`OrdnanceDefinition.Key`; every peer finds the definition by it with `OrdnanceApi.TryGetDefinition`, so a client, a late joiner and a reloaded checkpoint name the suit without having seen a call) carries it to every player, so the same model under two definitions keeps two names.
5. **Skills.** Only the six existing ones, tuned through their fields: `Boost`, `Siege`, `Scan`, `Shield`, `CarryHeavy`, `Rockets`. There is no way to add a seventh.
6. **Boarding.** The exosuits add no interface: place an empty suit with `OrdnanceApi.Place` (or a `Drops` entry) and a player boards it by the ordinary interaction.

**A suit standing at level start.** Define it for levels only, then place it in the level's `Settings.forge`:

```json
[ { "Id": "me.pack.start-suit", "Callable": false, "Payload": { "Type": "frames.Exosuit", "Model": "Frames:frame-m15", "Armor": 100, "DamageReduction": 0.25, "EyeHeight": 2.1, "Weapon": { "Gear": 38, "Magazine": 100, "Reserve": 600 } } } ]
```

```json
{ "Levels": { "Local_32_TierA_1": { "Drops": [ { "Name": "start-suit", "Ordnance": "me.pack.start-suit", "When": "LevelStart", "Arrival": "Direct", "Yaw": 180,
  "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0, "Offset": { "x": 2, "y": 0, "z": 0 } } } ] } } }
```

The same from code, host only: `OrdnanceApi.Place("me.pack.start-suit", spawnPoint, 180f, OrdnanceArrivalMode.Direct, out var why)`.

#### Shipped suits

| Model | Armor | Reduction | Eye | Walk / Sprint / Jump | Gun (gear, damage, stock) | Special |
|---|---|---|---|---|---|---|
| Nightowl, scout (`frame-m18`) | 60 | 15% | 2.05 m | x1.3 / x1.35 / x1.8 | 42 anti-materiel rifle, x1.5, 10 + 50 | Scan |
| Sirius, assault (`frame-m15`) | 75 | 25% | 2.1 m | x1.1 / x1.3 / x1.6 | 38 machine gun, x1.6, 100 + 400 | Charge |
| Thunderbird, firepower (`frame-m17`) | 64 | 20% (+10% planted) | 2.15 m | x1.05 / x1.25 / x1.4 | 38 machine gun, x2.0, 120 + 480 | Siege, rockets, planted barrier |
| Taishan, tank (`frame-m16`) | 90 | 40% | 2.3 m | x1.02 / x1.2 / x1.25 | 38 machine gun, x1.3, 100 + 400 | Barrier, heavy carry |

All four: boarding 2.85 s, leaving about 2.05 s (to the landing, the camera hand-back and the cleared black), cooldown 480 s (Nightowl), 540 s (Sirius), 660 s (Thunderbird, Taishan), 2 uses, call-in delay 8 s. Codes are left, down, right, up, left, down, then down (Sirius), up (Taishan), left (Thunderbird) or right (Nightowl).

### How a suit works

- **Arrival.** The host creates the suit record and sends it. Every peer drops the model 40 m through the ceiling in 2.0 s. After a 1.5 s landing settle the suit is idle and can be boarded.
- **Replication.** The host sends the whole suit state through `OrdnanceApi.SendState` after every change (armor at most every 0.2 s); the place of a worn suit is not sent on a timer (its record is as old as its last change). Late joiners build the suit from that state alone; the host cuts its time fields again from the suit's record when a player joins (`IOrdnancePayload.RefreshState`), so a cooldown that ran out while the state was kept does not start over. Board, leave, ammo, special and rocket requests are ForgeCore requests to the host: each gets exactly one answer, a `FrameRejection` byte, and one the host did not answer in time (or whose host left) is shown as "not now" (`FrameRejection.NoAnswer`); an ammo report shows nothing. ForgeCore drops a request made in another level or before a checkpoint reload. But for boarding, a request names the boarding it belongs to (`FrameRecord.Drive`, counted up by each boarding and sent with the state), and the host refuses one from another boarding however late it arrives. A granted rocket salvo goes from the host to every peer as one reliable event. The siege request names its target (plant or stand down), so a repeated request changes nothing. The pilot's hits reach other peers only as a flash and tracer; damage is the game's own bullet hit.
- **Boarding.** The player must stand behind an idle suit within 0.9 m, facing it, and hold Use for 1 s. The host checks the request. The suit never moves and the pilot has no boarding motion. The whole boarding is 2.85 s from the host's state start (`FrameSequence.EnterSeconds`, the same for every model; definitions do not set it), and every peer plays it from there (`Frames/Core/FrameSequence.cs`): the plates open by 1.9 s (about 1.4 times the clip's own pace) while the player stands still and the local view fades to black over 1.6-1.9 s; at 1.9 s (`FrameSequence.Black`) the pilot is put on the seat under the black, the pilot's body is hidden on every peer and the power-on starts; the plates close from the black (the 1.9 s close clip runs about twice its own pace to latch by the end); the controls open at 2.85 s, the moment the power-on's last block starts to come on (`BootPlan.ControlsAt`, 0.95 s of the power-on: blocks from 0.20 / 0.36 / 0.56 / 0.74 / 0.95 s, each settling over 0.40 s), and the last two blocks settle and the seams hold 0.12 s and fade 0.16 s while the pilot already walks, the picture whole at 3.53 s (`BootPlan.Assembled`, 1.63 s of the power-on). The pilot's own screen decides this from the state clock alone (`FrameSequence.ControlsOpen`; the power-on clock is the state clock from the black, so the blocks and the controls cannot drift apart): it gives the movement keys back on that frame without waiting for the host's Piloted message, and NEURAL LINK OK (locks at 4.25 s), the self-test and the ready voice play on while the pilot walks. The host's state still flips to Piloted at the same state-clock moment; nothing in the authority or the messages changed. Everyone else sees the shell open, the pilot vanish at the black and the shell close. Leaving lasts until the landing and the camera's hand-back are done and the black has cleared (`FrameSequence.ExitSeconds`, about 2.05 s): the panels go black block by block under a red SHUTDOWN, the pilot pulls out once every block is black (0.78 s), the pilot lands behind the suit (0.78 s + 0.87 s) and the camera eases to the player's own eye over 0.25 s, all under the still-black screen, and only then does the black clear (0.15 s): the inside of the suit is never shown; the movement keys come back on the landing frame and the empty suit closes on its own afterwards (`FrameSequence.ClosePlates`), holding nobody. The first-person shell of a model (about 230 ms to build) is built while the elevator goes down, one per frame, for each model the players picked in the lobby (`OrdnanceApi.PickedParameters`), none when nobody carries a suit; a model new to the level (a late joiner's pick, a first call-in) is built when its beacon lands and the countdown starts (`IOrdnancePayload.OnBeaconLanded`), never on the landing frame; only a suit already standing when this player joined is built as boarding starts.
- **Leaving.** The visor powers down to black under a red SHUTDOWN; under the black the pilot pulls out of the open suit (the leaving motion), lands on the first exit spot behind or beside the suit that passes ForgeCore's `Level.FindStandSpot` navigation, ground and wall checks (`FrameSequence.ExitClearance` sets the distance), and the camera eases to the player's own eye; only after that hand-back does the black clear, so the inside of the shell is never shown. The pilot moves from the landing frame on; the suit closes in front of the pilot and stays standing.
- **Worn.** The suit follows the pilot's smoothed position. The pilot's own body is hidden. The pilot's hit boxes are scaled x1.3. The suit has no stamina; only sprint heat limits sprinting.
- **Damage.** Every hit goes to the armor. The armor reduces part of each hit and never regenerates. The whole shell is hittable: while a suit is worn, every peer gives its pilot one more game player limb (`Frames/Native/PilotHull.cs`), a capsule from the feet to `EyeHeight` + 0.3 m with a quarter of that as radius, on the layer of the pilot's own limbs and pointing at the pilot's damage base; projectiles, blasts, melee and teammates' bullets that reach the shell outside the person count as hits on the pilot.
- **Siege.** V starts deployment and locks movement. Bonuses wait for `DeploySeconds`. Damage and fire rate apply only to the suit's gun; the reload and recoil factors apply to any gun the pilot holds, on top of the model's handling.
- **Rockets.** The pilot sends the aimed point. The host checks pilot, cooldown and range, picks a seed, and sends the salvo to every peer. Only the host applies the explosion damage. Rocket `i` leaves the launcher's tube `i % tubes`, 0.12 s after the one before, with the sentry gun's muzzle flash at the tube and a back-blast; its tube stays empty until it is reloaded, the salvo's rockets in firing order evenly over the cooldown (`Frames/Core/FrameRocketRack.cs`, from the salvo's launch time on each peer; nothing is sent per tube). A suit whose art is missing still fires: its rockets leave from 2.2 m above its feet.
- **Checkpoints and host changes.** At each checkpoint the host hands the suits as they stand (each saved empty and idle, wrecks left out) to the carrier, which publishes them after its slot book in the mod's one checkpoint copy; ForgeCore keeps it on every peer, a player who joins later gets it too, and on a reload every peer rebuilds exactly that population from it. A peer that holds no usable copy logs an error and keeps its suits as they are. A new host takes over from the replicated state: it sends the suits' states again in the same call as the host change, after the carrier has dropped the states the old host kept.
- **The special weapon slot.** The suit gun is lent into the slot through ForgeCore's `Gear.SpecialSlot` (`Frames/Core/SuitGunLoan.cs`, `Frames/Native/MechGun.cs`), the same lease a heavy weapon from Arsenal takes, so the two stack and each gets its gun back.
- **Input.** While boarding or leaving, the movement keys are held from the game through a ForgeCore `InputLease` (released at level end, death and leaving too).
- **Per-frame work.** `FrameWorld.Tick(now, isHost)` and `LateTick(now)` are called from the plugin's one frame and late-frame handler (`Plugin.Tick` / `LateTick`, on ForgeCore's `Frame.OnUpdate` / `OnLateUpdate`), which read the time and who hosts once; the package has no driver component. A pilot is found by network lookup with `Level.PlayerByLookup` (the host caches it by list position and checks it with `Level.IsPlayer`); the rocket aim ray is `Combat.ShooterRay`. `FrameCarry` rescans the carried object's renderers twice a second, not every frame.

### Exosuit art

`forge-frames.bundle` holds four prefabs, `frame-m15` to `frame-m18`, each with the opening clips `open` and `close` (no other clips: a worn suit follows its pilot's body), one skinned mesh in four sub-meshes (the armour on a 2048 texture set; the inside in fabric, metal and rubber: the fabric's colour map is the suit's own at 1024, its normal and smoothness and the metal and rubber maps are one set all four suits share), and `pilot-motion`: a renderer-less skeleton with the legacy clips `Rest` and `PilotExit` (root path baked in) and an `eye` point under its head. It also holds the parts in `tools/Frames/props.json`: `frame-rocket-pod` (the launcher, about 0.21 x 0.26 x 0.38 m, origin at the middle of its underside, front +Z, four tubes `muzzle_1`..`muzzle_4` at the tube mouths facing +Z, 1024 textures) and `frame-rocket` (the rocket, about 0.26 m long, origin at its middle, nose +Z, 512 textures), each one mesh renderer shaded with the gear shader. `tools/Frames/build_prop.py` stages each from its finished low-poly GLB (`-Props`, default `Source/mods/frames-rockets/lowpoly`): turned and uniformly scaled as `props.json` says, the sockets added. Sources and licenses are in `Assets/Frames/Models/LICENSE.forge`.

- `tools/Frames/make_cut_file.py` runs once per model in Blender and writes `<model>_cut.blend` into the folder `--out` names (the cut files are kept in `Source/mods/frame-cut`). It scales the model, maps the source skeleton onto a 17-bone suit rig and bakes one texture set. The file holds the whole shut shell, unsplit, in the collection `固定`. It never overwrites a cut file without `--replace`. M16 (Wrath) has its own maker beside its local source model, `Source/mods/ordnance-assets/models/M16/source/make_wrath_cut.py`, which writes the same layout.
- The owner draws the cut lines on the shell with Blender's Annotate tool, one layer per kind of line, and saves the cut file.
- `Tools/Blender/hinge_tool` is the Blender 5.2 add-on 战甲开合 (load it with `open.ps1 <cut file>`). Its click tool cuts a piece between two lines on the mesh's own edges (a drawn annotation, a seam, the plate's edge or a straight run) and sets its hinge, angle and order; `api.py` does the same from scripts, by piece number. Everything is stored as custom properties (`model.py`). Its checks (hinge stays attached ≤ 5 mm, lifts off its surface, no body penetration, back entry cover, arc per limb) only report; `api.review` writes the GIFs, numbered overview and table. A piece's lining is the faces with the material `frame-lining`; the build dresses them with the rest of the inside (`interior.py`). `hinge_tool/bake.py` writes the file the build reads from the owner's cut file (one collection per hinge, each part standing shut with its move in `fh_motion`); `hinge_tool/build.ps1 <model> [-File]` bakes `<model>_cut.draft.blend` into `<Cut>/build/<model>` and builds it with `preview_cut.ps1`. The motion (`motion.py`) is shared with `build_frame.py`.
- `tools/Frames/build_frame.py` builds from a baked file alone: the mesh objects in `固定` stay, every other collection holding meshes opens as one rigid part along the move `bake.py` gave it, and every object's shut place is where it stands. It thickens the shell (a lined object's plate and lining each grow at most half way toward the other, so the lining is the inside one sees), gives each part its own bone, keys the clips `open` and `close` (the parts' bones only), and fails the build on a shell that is not watertight, a second layer left over an opening part, or a small piece moving with a part it does not touch. The pilot-slot rays out of the back, parts passing through the body or each other on their path, and the clearance between layers are reported only. It writes `<model>_entry.json` with those checks and the slot rays.
- `tools/Frames/wear.py` runs inside `build_frame.py` right after `glow.paint`: Cycles bakes the shell's ambient occlusion, its edges (a bevel against the true normal) and its up-facing mask on the armour UVs, and fine noise over those masks writes thin chips (on raised ridges only, none on the hands), faint scratches, low-contrast grime and rust (in creases and seams only), top-face dust, oil and weld beads into the armour albedo (raised to 4096) and metal-smoothness maps, in each suit's own style (`STYLES`, whose `grade` first sets the source albedo's white, tint, saturation and gloss); the lights and the reserved strip are left alone. `tools/Frames/preview_look.py <rigged blend>` renders a texture folder on a built suit for judging it; `wear.py` also runs on a finished `<model>_rigged.blend` alone.
- `tools/Frames/interior.py` dresses the inside after the shell is checked, from geometry and never from part names. A face is wall when nothing outside can see it in the shut suit (rays from 26 directions) or when it sees a body part's axis; a face that can be seen stays armour whatever its material, so no rubber shows on the outside, and a lining vertex within 4 mm of its plate is pushed inward first (`build_frame.clear_lining`). Every wall becomes a rubber mat with 8 cm sewn panels. Light bars on the spine beam, at the waist and down each limb use the armour material's lit tile, so the open cockpit glows in the suit's colour. On it stand closed solids: quilted pads laid out by body region and fitted by rays per opening part (from the body part's axis, or from a plate's own plane where the plate lies aslant to those rays), so each pad opens with the part it sits on and splits where the wall has a step; metal frames at the region borders, a spine beam, conduits, webbing and warning plates. Rims of opening parts become gaskets. Each suit's colours, pad sizes and layout are its entry in `SUITS`.
- `tools/Frames/preview_look.py` renders a built suit as the game lights it (a dark room, the player's lamp); `POSE=open VIEWS=in,in_l,in_r,in_chest,in_waist,in_legs` shows the inside from behind the open suit.
- `tools/Frames/build_assets.ps1` builds the bundle from the four finished cut files `Source/mods/frame-cut/<model>_cut.draft.blend` (bake, then `build_frame.py`, then Unity); it only reads them.
- `tools/Frames/preview_cut.ps1 <model> -File <baked file>` builds one baked file without stopping on failed checks, renders the opening with `tools/Frames/preview_opening.py` (the `open` clip, plus a frame-by-frame sheet) and prints the checks in Chinese.
- `tools/Frames/FramesBundleBuild.cs` runs in Unity batch mode. It cuts the imported `open` take down to the `Opening` bones, checks it (shut at its first frame, scale 1 throughout, no part ends nearer the nearest body axis (pelvis, spine, chest, limbs) than a tuck unless the owner allowed its hinge into the body in the add-on, the same slot rays clear on Unity's mesh), checks the three lining sub-meshes against the build's triangle counts (Unity orders sub-meshes by first use, so they are matched by count), gives them the `<prefab>/lining` (fabric), `<prefab>/lining-metal` and `<prefab>/lining-rubber` surfaces and writes the bundle. `FrameVisual` samples that clip; it computes no motion of its own.
- `tools/Frames/build_icons.ps1` renders one 256 x 256 icon per suit with the same camera into `Assets/Frames/icons/` (the package's icon source, local media), skipping files already there (`-Redo <file>` or `-Redo all` re-renders on purpose).
- `tools/Frames/import_hud_art.ps1` copies the owner's finished cockpit art (`A2-*` skill icons, `A6-*` model marks, `A1-前/后/左/右` silhouette layers, `A5-*` shield, heat, objective and teammate marks and `A7-绕背箭头`, each `name.png` and `name@2x.png`, white on transparency) into the local media folder `Assets/Frames/hud/` as `skill-*`, `mark-<model>`, `damage-*`, `shield`, `heat`, `objective`, `teammate` and `arrow-around`. Git does not track it. The plugin registers whatever `hud/` holds with ForgeUI as the `Frames:` icon pack (`Frames/Native/FrameHudArt.cs`; the @2x file above 1440 lines). `tools/Frames/import_visor_cracks.py` (needs Pillow) turns the owner's crack drawing (`U3-裂纹.webp`, brightness to alpha) into the single file `Assets/Frames/hud/visor-cracks.png`, which the visor reads through `Visor.CracksPicture`; packaging refuses to build without it. The code draws nothing in their place: a missing file is logged, a skill slot shows its name, a gauge or model shows no mark, and ForgeUI leaves out (and logs) a missing silhouette layer. Packaging refuses to build until every file, 1x and @2x, is in `Assets/Frames/hud/`.

### Exosuit sound

`tools/Frames/prepare_audio.py` checks every source file against the hash in `sources.forge`, cuts each section exactly as recorded, and calls ForgeAudio's converter to write Ogg Vorbis. `Assets/Frames/Audio/cues.forge` follows the ForgeAudio format. The library is loaded as `NAinfini.ForgeOrdnance.Frames` (Arsenal's is `NAinfini.ForgeOrdnance.Arsenal`, the carrier's `NAinfini.ForgeOrdnance`). Sounds only the pilot hears play in 2D. World sounds follow replicated state, so no extra network message is needed. Boarding and leaving sounds run from the same state clock as the plates on every peer.

### Cockpit

While a suit is worn, ForgeUI hides the game's HUD except world markers and chat (docked at the bottom right under the inventory, on the corner's safe edge, in the cockpit's look: no game backings, the HUD face at `ChatTextPx` 18 and `ChatInputPx` 19, lines in `Primary`, alerts in `Warning`, a `Primary` cursor bar, players' names in their own colours; typing in it lights a plate behind it as tall as the lines shown and the cockpit stays on screen; not typing, only the newest 3 lines show at 0.85, on a faint plate with a thin bar, for 10 s after the newest line and then fade (`IdleLines`, `IdleOpacity`, `IdleSeconds`); the game's help line is not drawn, lines are cut to the dock with an ellipsis, and the game's version text at the bottom right is hidden with the HUD; `ChatWidth` 470, `ChatLines` 6, `ChatPriority` 20 and the accent `Primary` in `Frames/Native/FrameHud.cs`). The cockpit is ForgeUI templates fed from `Frames/Core/FrameHudModel.cs` (`Frames/Native/FrameHud.cs`). One palette for every part: ice (`Primary`, ForgeUI's `Cockpit`) for everything normal, orange (`Warning`) only for emphasis and what is ready to use (highlighted objective words, a call-in that can be called), red (`Danger`) only for danger, cyan only for the barrier, and text on ForgeUI's dark `Plate`:

- top centre: the zone, heading and altitude in one row, and right under it the barrier, armour and pilot-health bars: the barrier is a cyan layer `BarrierHeight` 5 on the armour bar's top edge with a shield mark and its points left of the bar (`OverlayReadoutMax` = the spec's capacity), flashing on a hit, brighter on a break, red 0 and a red track filling back while broken; a planted-only barrier (Thunderbird) is drawn only in siege mode (`FrameHudModel.BarrierShown`, the layer's place kept so the bar does not move);
- around the crosshair: the magazine arc (the rounds in the hand's magazine, ∞ without one; empty stays drawn at 0) and the heat arc, the crosshair dot (hidden while aiming down sights),  the rocket landing mark and the threat bearing arc (enemies out of view only; the game's compass points are not drawn on it, the pings stay in the world as the game's own markers);
- top left: objectives, a 330-wide column (`Width`, three objective lines) on a 46 % dark plate chamfered 10 with a 42 % ice accent bar; tags and header 12, objective 15, step 12.5 and fainter; only tags bold;
- the interaction prompt with the scan line under it, redrawn in the cockpit's face at the position the game's own prompt object has (`HudInteraction.AtGamePrompt` reads it, once per screen size or interaction-layer change), and under the crosshair the subtitles;
- bottom right: the gun's name and reserve;
- bottom left: skills, lamp, exit and teammates (290 wide, rows 36 on a 42 step, 10-cell slanted health, a 50 % dark plate with a 22 % ice chamfered frame, a pilot's armour as a 60 % ice line, 16 units more space above LAMP / EXIT);
- also: hit glow at the edges, a faint visor, the enemy outline and enemy health bars, a banner while the pilot is grabbed or stunned, and world marks over idle and worn suits.

The suit draws nothing itself: ForgeUI's power-on player plays the suit's ten panels and rhythm (`Frames/Core/FrameBootScreen.cs`; the tint and the three glitch channel colours, magenta-red, green and blue, in `FrameHud.RegisterScreens`), and ForgeUI's power-on message plays the suit's beats, colours and words (`FrameBootMessage`); the barrier has no picture of its own (only its line on the armour bar). `Frames/Core/CockpitScope.cs` decides when the game's HUD is hidden and given back, read every frame from the pilot's state now (boarding, inside the shell, worn, leaving, dark, out; waiting while ForgeUI cannot hold the HUD, a dead pilot or no level), so a missed record, a release by ForgeUI or a failed cockpit update is corrected on the next frame instead of lasting the ride. `Frames/Core/FrameBootScreen.cs` (`BootPlan`) times the power-on from the black: after a short black the screen panels come into view block by block with a Titanfall-style red-green-blue channel-split glitch; the pilot moves as the last block starts to come on (0.95 s, 2.85 s into the boarding) and the picture is whole at 1.63 s (3.53 s); the cockpit parts come online in the plan's order through ForgeUI's `HudWidget.Reveal()` as their block lights; the centre group above the crosshair (header decode, bar, self-test under it on a dark plate) runs from 0.85 s, and NEURAL LINK OK decodes and locks with the ready voice at 2.35 s and folds away while the pilot already walks. Leaving, the same blocks go black in reverse while ForgeUI's power-on message shows the suit's red SHUTDOWN (`boot.shutdown`) in the middle until the last block is black. All words are keys in `lang/` (`Frames/Core/FrameWords.cs`); a key missing from the player's language is logged once by ForgeUI and shown in English (`en.forge` is the reference).

### Drawn ahead of first use

The worn shell holds the game's torso, legs and head `forceRenderingOff` (giving back the value each had), never a transparent material, which would still skin and draw them. ForgeVisual draws it once off screen right after it is built ahead of boarding (`Models.WearFirstPerson`).

A level drop may set a suit's Armor (1 to the model's own), Magazine (whole, at most the model's own) and Reserve (whole, 0 to 100000) through its Overrides; a suit already present keeps its numbers. See [Level control for rundown authors](#level-control-for-rundown-authors).

## Building assets and the release package

Run everything from the repository root. Replace `<profile>` with a GTFO r2modman profile.

### Build

```
dotnet build Mods/ForgeOrdnance/ForgeOrdnance.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
```

The project is not part of `Mods/Forge.Architecture.sln`.

### Models, icons and sounds

Bundles, Ogg files, icons and cockpit art are local media: the scripts read their sources (by default under the local `Source/mods/` folder) and write the results into `Assets/<part>/`, outside Git.

```
pwsh Mods/ForgeOrdnance/tools/Carrier/build_assets.ps1
pwsh Mods/ForgeOrdnance/tools/Carrier/build_audio.ps1 -Library <sound library>
pwsh Mods/ForgeOrdnance/tools/Arsenal/build_assets.ps1
pwsh Mods/ForgeOrdnance/tools/Arsenal/build_audio.ps1 -Library <sound library>
pwsh Mods/ForgeOrdnance/tools/Arsenal/build_icons.ps1
pwsh Mods/ForgeOrdnance/tools/Frames/build_assets.ps1
pwsh Mods/ForgeOrdnance/tools/Frames/build_icons.ps1
pwsh Mods/ForgeOrdnance/tools/Frames/import_hud_art.ps1
python Mods/ForgeOrdnance/tools/Frames/import_visor_cracks.py
python Mods/ForgeOrdnance/tools/Frames/prepare_audio.py
```

- **Carrier.** `tools/Carrier/build_assets.ps1` (Blender and Unity 2019.4.21f1) builds `forge-ordnance.bundle`: the summoning ball, the one drop pod and the turret base, from the masters `tools/Carrier/tripo_masters.py` makes. It keeps the bundle with its Unity receipt and build log in `Assets/Carrier/Models/`; a failed run removes the receipt, so an old bundle cannot ship. `tools/Carrier/build_audio.ps1` rebuilds the sounds from `sources.forge` with `tools/Carrier/stage_audio.py` and ForgeAudio's converter.
- **Arsenal.** `tools/Arsenal/build_assets.ps1` builds `forge-ordnance-arsenal.bundle` (models and strike effect textures) with the carrier's pipeline. `tools/Arsenal/build_icons.ps1` renders each call-in's picker icon in Blender (a supply shows the game's own pickups, one per item the definition delivers: `tools/Arsenal/extract_pickups.py` reads their meshes and textures from the game files into the local folder `Source/mods/pod-native/meshes` (needs UnityPy on `PYTHONPATH`), and the kinds and counts come from the `Item` payload, so a changed delivery re-renders into a changed picture; the shell and beam strikes that differ in the game only by colour are drawn in their BeamColour; dark models such as the weapons get `--exposure`: a front light and a higher view exposure), but only the ones missing from `Assets/Arsenal/icons/`; a file already there is never overwritten (`-Redo <file>` or `-Redo all` re-renders on purpose). `tools/Arsenal/build_audio.ps1` rebuilds the sounds the same way as the carrier's.
- **Exosuits.** `tools/Frames/build_assets.ps1` needs Blender 5.2, Unity 2019.4.21f1 and Python 3 with numpy. It builds `forge-frames.bundle` from the four source models and the pilot's leaving motion (`-Pilot`: `PilotMotion.fbx` and `motion.json`, made by the pilot-motion Blender scripts) and writes a receipt that binds the bundle's hash to the durable inputs only: the tools and definitions of this repository and the cut files, cut textures, pilot motion and prop GLBs under their recorded roots (never the stage folder or per-model outputs, which every run rebuilds). Any change to one of them, a moved tool included, requires a rebuild. The other scripts are described under [Exosuit art](#exosuit-art) and [Exosuit sound](#exosuit-sound).
- **Effect libraries.** Each part's `fx-slots.forge` and effect bundle (`forge-ordnance-fx.bundle`, `forge-arsenal-fx.bundle`, `forge-frames-fx.bundle`) come from `Tools/Vfx/FxLibrary/fx_slots.py`.

### Release package

```
python Mods/ForgeOrdnance/tools/package_release.py --artifacts-path artifacts/dotnet --output artifacts/work/ordnance-release
```

Both arguments are required: the `--artifacts-path` of the Release build and the folder that receives the zip. The one script checks the manifest against Thunderstore's rules, the plugin's `Version` against the manifest, every bundle against its build receipt (the carrier's also against its kept Unity log, the exosuits' against the hashes of today's definitions, tools and model sources), the drop pod's parts, every prefab the plugin loads by name, each part's sound folder (exactly the clips `cues.forge` names), a 256 x 256 icon for every definition and every cockpit art file at 1x and @2x, then writes `NAinfini-ForgeOrdnance-<version>.zip` with the layout shown under [Folder layout](#folder-layout). The same inputs give the same bytes. It does not publish. A media file is read from this checkout when it is there, else from the main checkout; `FORGE_MODS_ROOT` names another `Mods` folder to fall back to.

Install all of it together. A DLL with older definitions rejects every suit.

## Tests

```
dotnet run --project Mods/ForgeOrdnance/tests/Core/OrdnanceCore.csproj -c Release --artifacts-path artifacts/dotnet
python -B -m unittest discover -s Mods/ForgeOrdnance/tests/tools -p "test_*.py"
```

`tests/Core/` is one project. It compiles the three parts' `Core/` sources (they reference no game or Unity assembly) with the few ForgeCore, ForgeUI and ForgeVisual Core files they use, and runs `CarrierChecks`, `ArsenalChecks` and `FramesChecks` in turn; its one result line gives each part's share of the checks. Arsenal's checks also load the shipped `arsenal.forge` through the carrier's definition parser and compare its codes with the suits' shipped ones.

`tests/tools/` holds the Python tests: the gates of `tools/package_release.py` on fixtures (a stale, changed or unreceipted bundle, a missing clip and a stray Ogg are refused; a complete package is laid out by part and comes out byte-identical twice), `tools/Frames/prepare_audio.py` and `tools/Carrier/stage_audio.py`.

`python -B Mods/verify.py` runs both.

## Logging

Logging goes through ForgeCore's `ModLog` (`Plugin.Logger`, one log for the three parts) and only errors are written: `Error`, and `ErrorOnce`, which writes each exact message once. There are no info, trace, timing or debug-setting outputs. Positions and numbers in a message are formatted with `ForgeCore.Log`.

## Scripted entry

Public static console entries, one answer line each:

- `OrdnanceControl` (`Carrier/Native/OrdnanceControl.cs`): `List()`, `Loadouts()`, `Arrivals()`, and `Place(id, x, y, z, yaw, arrival, overrides)` and `PlaceAhead(id, metres, arrival, overrides)` over `OrdnanceApi.Place` (host only).
- Arsenal's entry on the same type (`Arsenal/Native/ArsenalControl.cs`): `Racks()`, the heavy weapon racks with their ammunition numbers.
- The exosuits' entries on the same type (`Frames/Native/FramesControl.cs`): `Suits()`, `BoardSuit()` (the host request a completed use-key hold sends, for the nearest idle suit) and `ExitSuit()`. Bring a suit in with `OrdnanceControl.PlaceAhead(id, metres, "direct")`.
