# Forge Ordnance for developers

This file is for people who write call-ins or build on the framework. Players should read `README.md`.

Forge Ordnance runs the call-in loop: code entry, beacon, host checks, delivery. A call-in is a JSON definition. Content mods add definitions, and code mods add new call-in kinds. [Forge Ordnance Arsenal](../ForgeOrdnance_Arsenal) and [Forge Ordnance Frames](../ForgeOrdnance_Frames) are worked examples of both.

## Layering: what this package holds and what it does not

ForgeOrdnance is the call-in platform and vehicle layer: call keys and codes, beacons and throwables, cooldowns and uses, network sync, drop pods, the turret drop shell, container-free drops, arrival impact, loadouts and lobby sync, and the extension API (payload types, placing items in pods, in-pod pickup, a hook to re-skin vanilla sentry guns, category registration). Concrete call-ins, models and sounds belong to the mod that adds them: write definition JSON, and register a payload type only for new gameplay. Turret models belong to Arsenal, suits to Frames.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Rules with no game reference: definitions, loadouts, slot book, messages. |
| `Native/` | The plugin: controller, host logic, delivery, art, hooks. All drawing goes through ForgeVisual (models, particle pools, beam, ground mark, trails, glow); the plugin has no shaders, materials or line renderers of its own. |
| `Assets/` | Model sources, sound records and licenses. |
| `tests/Core/` | Offline tests for `Core/`. |
| `tools/` | Asset, audio and packaging scripts. |

## Build and test

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeOrdnance/ForgeOrdnance.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<profile>/BepInEx
dotnet run --project Mods/ForgeOrdnance/tests/Core/OrdnanceCore.csproj -c Release --artifacts-path artifacts/dotnet
```

The model bundle needs Blender and Unity 2019.4.21f1: `pwsh Mods/ForgeOrdnance/tools/build_assets.ps1`. The sounds are rebuilt with `pwsh Mods/ForgeOrdnance/tools/build_audio.ps1 -Library <sound library>`.

## Release package

`python Mods/ForgeOrdnance/tools/package_release.py` checks the manifest against Thunderstore's rules and writes `NAinfini-ForgeOrdnance-<version>.zip` to `artifacts/work/ordnance-release/`. It does not publish.

The zip holds `manifest.json`, `icon.png`, `README.md`, `CHANGELOG.md`, `LICENSE`, `ForgeOrdnance.dll`, `lang/` (`en.json`, `zh.json`; the script refuses to build without both), `forge-ordnance.bundle`, `audio/` (Ogg files, `cues.json`, `License.txt`) and `models/License.txt`.

## Writing a content pack

The one recipe for a mod that adds call-ins to this platform; the Arsenal, Frames and Energy Lab documents link here instead of repeating it. Arsenal is the worked example.

1. **A plugin** that depends on `NAinfini.ForgeOrdnance` (and the libraries it uses) with `[BepInDependency]`. Everything below happens in its `Load`, which runs before definition files are read.
2. **Models and effects.** Build one asset bundle and register it once: `ForgeVisual.Bundles.Register(modId, pack, path)` returns a handle whose models you pass to `OrdnanceTurrets.RegisterLook`, `OrdnancePodAppearance` and the like. Keep the bundle in your own package; do not copy it into Ordnance.
3. **Definitions.** JSON files in a `ForgeOrdnance` folder inside your package (see [Writing call-ins](#writing-call-ins)); every `Id` carries your namespace (`author.name`). Payload types you add are registered with `OrdnanceApi.RegisterPayload("author.Type", ...)`, categories with `OrdnanceApi.RegisterCategory`.
4. **Icons and texts.** Each definition's picker icon is a PNG beside its JSON file (`Icon`); ForgeUI draws it. Names and descriptions in other languages go in a `lang` folder beside the file (`en.json`, `zh.json`, keys `<Id>.Name` and `<Id>.Description`). Show names with `OrdnanceApi.DisplayName`.
5. **Names you register** (looks, sounds, bundles) are `Pack:item`, the pack being your short prefix, so two packs never collide.
6. **A definition only a level brings** (a suit standing at the start, a supply crate): write `"Callable": false` and leave out the player-facing fields (see [Developer drops](#developer-drops-for-level-authors)).

## Writing call-ins

Put definition files in any folder named `ForgeOrdnance` under `BepInEx/plugins`, for example `BepInEx/plugins/MyCallIns/ForgeOrdnance/*.json`. For a custom rundown, use its MTFO custom folder: `<CustomPath>/ForgeOrdnance/*.json`. Each file is a JSON array of definitions. All files load together.

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
| `Category` | `Supply`, `Defensive`, `Weapon` or `Offensive`, or a category an add-on registered with `OrdnanceApi.RegisterCategory` (Frames registers `Exosuit`). Picks the picker tab. An unregistered category skips the entry with an error. |
| `Description` | Optional, up to 400 characters. Use `\n` for a new line. |
| `Icon` | Optional. A PNG file name in the same folder as the definition file, no folders. A square image of 128 to 512 pixels works best. If the file is missing, the entry is skipped. It is also the picture in the player's loadout cells on every lobby bar, drawn there as a white shape cropped to its outline, so give each call-in a shape that is recognisable without its colours. |
| `Stats` | Optional, up to 6 rows such as `[{ "Name": "Armor", "Value": 4 }, { "Name": "Speed", "Value": 2, "Max": 5 }]`. Each row shows in the picker details as `Value` lit boxes out of `Max` (1 to 10, default 5). `Name` is up to 16 characters. |
| `Code` | 1 to 8 steps, each `Up`, `Down`, `Left` or `Right`. In one loadout, no code may be the beginning of another. |
| `Cooldown` | Seconds after an accepted call, 0 to 3600. Swapping a call-in into or out of a loadout slot (also from a console) resets that slot's cooldown to zero; its uses are kept. |
| `Uses` | Optional. Calls per level, 1 to 99. Leave it out for unlimited calls. |
| `CallInDelay` | Seconds from beacon landing to arrival, 0 to 120. |
| `BeamColour` | Optional `"#RRGGBB"`. Defaults by category: Supply blue, Defensive light green, Weapon white, Offensive orange; a registered category brings its own colour. |
| `Payload` | What arrives. `Type` picks the kind; the other fields belong to that kind. |

To show names and descriptions in the player's language, put a `lang/` folder beside the definition files with one ForgeUI language file per language (`en.json`, `zh.json`, ...), keyed `<Id>.Name` and `<Id>.Description`:

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

A sentry gun from any `PlayerOfflineGearDataBlock`, delivered in a shell that falls open around it.

| Field | Meaning |
|---|---|
| `Gear` | The sentry's gear block by ID or name: 24 burst, 6 auto, 23 sniper, 25 shotgun. Other gear is refused. |
| `AmmoRatio` | 0 to 1, default 1. Share of the caller's class ammo pool. |
| `Lifetime` | Seconds until it is removed, 0 to 3600. 0 means it stays. |
| `Pickable` | `false` removes the pick-up interaction. Default `true`. |
| `Look` | Optional `"Pack:item"`: a turret look an add-on registered with `OrdnanceTurrets.RegisterLook` (Arsenal: `Arsenal:turret-burst`, `Arsenal:turret-sniper`, `Arsenal:turret-shotgun`). Without it the game's own sentry look stays. An id nobody registered skips the entry with an error. |
| `DamageScale`, `FireRateScale`, `AmmoScale`, `RangeScale`, `TurnScale`, `AcquireScale` | 0.1 to 5, default 1. Multipliers over the normal sentry of the same gear: damage, shots per second, ammo, range (the detection range, the damage falloff distances and the shot ray length by the same factor), turning speed, and how fast it opens fire on a new target. They apply to a copy; other sentries are untouched. |

### Keys

The call key and the four code keys are ForgeUI key actions (`Native/OrdnanceKeys.cs`): `Summon` (`Mouse3`, hold, every context) and `CodeUp` / `CodeDown` / `CodeLeft` / `CodeRight` (arrow keys, context `ordnance-code`). The player changes them in the game's settings (Controls); they are saved in `NAinfini.ForgeUI.keys.cfg`. While a code is entered the movement keys are held off the game and read from the player's own game bindings (`UIKitKeys.GameKeyDown`), so they enter code steps as well.

### Level rules: `Settings.json`

Optional. Read only from `<CustomPath>/ForgeOrdnance/Settings.json`.

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
| `CallMenu` | `false` turns the players' call-ins off (see [Developer drops](#developer-drops-for-level-authors)). Default `true`. |
| `Drops` | The level's developer drops (see below). Default none. |
| `Levels` | Overrides any of the above for one level. Keys are the expedition's unique key (`RundownManager.ActiveExpeditionUniqueKey`, for example `Local_31_TierA_0`), not the display name such as `R1A1`; read it from that property in code, or from the log, where the host writes `Host level rules published for '<key>'`. A key that matches no level is not an error, so when the host enters a level and rules exist but none for it, one warning lists the level's actual key and the keys that matched nothing. A mod can set a level's rules from code too (see [Declaring a level from code](#declaring-a-level-from-code)). |

The host sends its selected expedition's rules to the lobby picker. While waiting for them, the button shows `WAIT`. A level override replaces a field as a whole: a level with its own `Drops` uses only those, a level without uses the top-level `Drops`.

### Developer drops (for level authors)

A rundown can place any registered call-in itself, with no beacon and no player involved: at level start, or when a level event fires. The drops live in the same `Settings.json`, so they ship with the rundown; players need nothing in their own config. A drop uses no loadout slot, cooldown or use and ignores `Allowed`. The host places it; every player sees the same arrival, and players who join later see it in its current state.

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

**Drop or direct.** `Arrival` picks how the call-in arrives. `Drop` (the default) arrives the way a player's call does: the drop pod falls and opens, the turret shell falls open, the exosuit falls through the ceiling; the contents come when the vehicle opens. `Direct` leaves the vehicle out: the contents are there at once. `Drop` is the default because a drop is seen and heard, so players notice an event drop; use `Direct` to set a level up quietly, for example a suit standing ready at the start.

| Payload | `Drop` | `Direct` |
|---|---|---|
| `Item` (supply pods) | Pod falls, opens; pickups inside | Pickups on the ground around the point |
| `Gear` (turrets) | Shell falls and opens; turret stands up | Turret stands at the point |
| `arsenal.HeavyWeapon` | Pod with the weapon racked | Weapon on the ground, ready to take |
| `frames.Exosuit` | Suit falls and lands | Suit stands empty, ready to board |
| `arsenal.Strike` | Shells fall and strike | Not supported: a strike has no vehicle to leave out (the drop is refused at load) |

A drop's turret takes its ammunition from the host's class tool pool, as the host owns it.

**Turning the call menu off.** With `"CallMenu": false` the players have no call-ins in that level: no call list or cooldowns on the HUD, the call key does nothing, the lobby hides the ORDNANCE button and the picks row, and the host refuses any throw. Developer drops still arrive. Use it for a level that only wants the drops:

```json
{ "Levels": { "Local_32_TierA_1": { "CallMenu": false, "Drops": [ { "Name": "start-suit", "Ordnance": "nainfini.frames.tank", "When": "LevelStart", "Arrival": "Direct", "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0 } } ] } } }
```

Put `"CallMenu": false` at the top level instead to turn the call-ins off in every level of the rundown.

**Fields.**

| Field | Meaning |
|---|---|
| `Name` | Required. Unique in its list. Events name the drop by it; the log names it in every line. |
| `Ordnance` | Required. The `Id` of any loaded call-in definition (this package's, Arsenal's, Frames' or your own). |
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
| `Settings.json ... Drops[i] <field>: ...` | Load | A drop entry is malformed (missing field, unknown field, a value out of range, both or neither of `Position` and `Area`, a `Delay` on an event drop, a duplicate `Name`). The whole `Settings.json` is then ignored, as for any invalid field, and default rules apply. |
| `... drop '<name>' Ordnance: '<id>' is not a loaded call-in; this drop is skipped.` | Load | No definition has that `Id` (its package is not installed, or the id is misspelt). The other drops load. |
| `... drop '<name>' Arrival: '<id>' (payload <type>) cannot arrive Direct; this drop is skipped.` | Load | The call-in's payload has no direct arrival (`arsenal.Strike`, or an add-on payload without `SupportsDirect`). |
| `Developer drop '<name>' (...) not placed at <place>: dimension <n> does not exist in this level.` | Firing | The level has no such dimension. |
| `... not placed at <place>: there is no zone <z> in layer <l> of dimension <d>.` | Firing | No zone with that `Layer` and `LocalIndex` in that dimension. |
| `... not placed at <place>: zone <z> (...) has <n> area(s); Area <a> does not exist.` | Firing | `Area` is past the zone's last area. |
| `... not placed at <place>: the point (x, y, z) is not inside any area of dimension <d> (no course node within 2 m).` | Firing | The point is outside the level's areas in that dimension (an event drop without `Where` and an event without `Position` lands at 0, 0, 0). |
| `Ordnance drop event (type 75000) names drop '<name>' in WorldEventObjectFilter, but level '<key>' has no event drop of that name in Settings.json; nothing is dropped.` | Firing | The event names no `Event` drop of this level (misspelt name, the drop is in another level's list, or it is a `LevelStart` drop). |
| `Developer drop '<id>' (call <n>) failed on arrival: ...` | Arrival | The payload itself failed (for example a pickup prefab missing); its message says why. |

A successful drop logs `Developer drop '<name>' ... scheduled as call <n>` when it is accepted and `Developer drop '<id>' (call <n>) arrives (Drop|Direct)` when it arrives.

**Callable: false.** A definition written with `"Callable": false` is for levels only: players never see it in the picker or a loadout (the host refuses it in one), and only `Drops` and `OrdnanceApi.Place` can bring it. It then has only `Id`, `Payload` and the optional `Name`, `Icon` and `Description`; `Category`, `Code`, `Cooldown`, `Uses`, `CallInDelay`, `Stats` and `BeamColour` must be left out, and giving one rejects that entry with an error naming the field. Use it for a suit or a supply that a level places at its start. Because they exist only to be placed, `PickedParameters` always lists their parameters (for their payload type) in every level, so a `Place` call finds its assets prepared.

```json
[ { "Id": "me.pack.start-suit", "Callable": false, "Payload": { "Type": "frames.Exosuit", "Model": "Frames:frame-m15", "Armor": 100, "DamageReduction": 0.25 } } ]
```

**Placing from code.** `OrdnanceApi.Place(ordnanceId, position, yaw, arrival, out reason, dimension = -1)` brings a call-in in now, through the same delivery as a `Drops` entry (one scheduler, every player sees it, late joiners see its current state). It is host work: test `SNet.IsMaster` first; a client gets `false` and a `reason` without a log line, any other refusal (not in a level, unknown id, `Direct` for a payload without it, a point inside no area) is `false`, a `reason` and one error in the log, written on every call (not throttled). Pass `dimension` to require the point to be in that dimension; the default takes whichever contains it.

```csharp
if (SNet.IsMaster && !OrdnanceApi.Place("me.pack.start-suit", spawnPoint, 180f, OrdnanceArrivalMode.Direct, out var why))
    Logger.LogWarning("start suit not placed: " + why);
```

### Declaring a level from code

`OrdnanceApi.DeclareLevel(levelKey, rules)` gives a level the rules `Settings.json` `Levels.<key>` would, without any file: turn the call menu off and place a suit at level start from your own plugin. Call it from `Load`. `rules` is an `OrdnanceLevelRules` (`LoadoutSize`, `Allowed`, `CooldownMultiplier`, `UsesMultiplier`, `MaxThrowDistance`, `CallMenu`, `Drops` of `OrdnanceLevelDrop`, `DropWhere`, `DropPoint`, `DropWhen`); it is the level's whole rule set, with defaults for what you leave out (nothing is inherited from the file's top level), and is checked like the file. It is merged with `Settings.json` into one effective setting that also passes the drops check. A key already declared by another call is refused with one error; a key `Settings.json` also sets is reported at load and the file wins.

```csharp
OrdnanceApi.DeclareLevel("Local_32_TierA_1", new OrdnanceLevelRules
{
    CallMenu = false,
    Drops = new[] { new OrdnanceLevelDrop("start-suit", "me.pack.start-suit", DropWhen.LevelStart, OrdnanceArrivalMode.Direct, 0, 180,
        new DropWhere(0, 0, 0, new DropPoint(0, 2, 0, 0))) }   // dimension 0, layer 0, zone 0, area 0 offset (2, 0, 0)
});
```

### After a checkpoint reload or a host change

Both void the drops still waiting and every call in flight. The host then places **every** `LevelStart` drop again at once (`Delay` ignored), whether it had arrived or not: a suit that was standing is standing again (a suit someone had boarded is not boarded; that state is lost). A level-start drop has a fixed call number made from its `Name`, so every player sees the same call; two names that would share a number are refused when `Settings.json` is read. `Event` drops and what `Place` placed are not placed again; an event that fires again places its drop again.

## Extension API

The public API is in the `ForgeOrdnance` namespace. It ships with the package (version 1.0.0). Everything not listed below is internal and may change in any release.

| Type | Use |
|---|---|
| `OrdnanceApi` | `RegisterPayload`, `RegisterCategory`, `DefinitionsLoaded`, `DefinitionsReady` (event, raised once on the main thread after the definition files are read), `IsPeerCompatible`, `SendData`, `SendState`, `ClearState`, `BlockCalls`, `CallsBlocked`, `PickedParameters`, `DisplayName`, `DeclareLevel`, `Place`, `TryGetDefinition(uint key)` and `TryGetDefinition(string id)` (the loaded definition; `OrdnanceDefinition.Key` is the value the network and saves carry; false before definitions load; every peer loads the same set, so clients and late joiners find it too), and `Definitions` (every loaded definition, ordered by `Id`, the same on every peer). |
| `OrdnanceLevelRules`, `OrdnanceLevelDrop`, `DropWhere`, `DropPoint`, `DropWhen` | The rules of a level and its drops, for `DeclareLevel`. |
| `IOrdnancePayload`, `IOrdnancePayloadParser`, `PayloadParameterException` | A new call-in kind. |
| `OrdnanceExecution`, `OrdnancePresentation`, `OrdnancePayloadData`, `OrdnanceDefinition`, `OrdnanceStat`, `OrdnanceCategory`, `Direction`, `OrdnanceArrivalMode` | Read-only data given to your callbacks (`OrdnanceCategory` is a string id). |
| `OrdnanceDelivery`, `OrdnancePod`, `OrdnanceShell` | Reuse the drop pod and turret shell. |
| `OrdnanceClaims` types: `ClaimableSpec`, `OrdnanceClaimable`, `ClaimPrompt`, `ClaimVerdict`, `ClaimInstall`, `ClaimResult` | In-pod (or pod-less) claiming: `OrdnancePod.AddClaimable(bay, spec)`, `OrdnanceDelivery.AddClaimable`, `OrdnanceDelivery.HostOpenClaim`. |
| `OrdnanceArrival`, `OrdnanceImpact`, `OrdnanceDropStyle`, `OrdnanceDrop` | The one camera shake (`Shake`), landing impact (`Impact`) and a container-free drop (`Drop`). |
| `OrdnanceTurrets`, `TurretLookSpec` | Register turret looks and listen to turret moments. |
| `OrdnancePodAppearance`, `OrdnancePodDoor`, `OrdnancePodSocket`, `OrdnancePodBay` | A custom pod look (its model is a ForgeVisual `ModelHandle`). `OrdnancePodBay.Fit(look, frontToForward, out scale, out lean)` scales, lays flat or stands up any look to the bay's size and parents it to the bay anchor. |
| `OrdnanceCallBlock` | Hold the call key off, for example while piloting; `Dispose` releases it. |
| `OrdnanceArrivalSounds`, `OrdnanceArrivalSound` | The drop pod's fall, touchdown and rubble sounds, for a payload with its own arrival that falls the same way (`Play(sound, transform, dimension)`: `Incoming` when the fall starts, `Impact` and `Debris` on touchdown). |

The public surface carries no separate API version: it is the package version (1.0.0). An add-on that uses something missing fails to load. Register your payload types from `Load`:

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
}
```

Registration is refused for a type name without an author namespace (`author.Type`), a taken name, or registering after definitions were read.

- **`Parse`** gets the whole `Payload` object. It runs at game-data initialization, so it may read data blocks. Reject unknown fields yourself. The returned object becomes `OrdnanceDefinition.Parameters`.
- **`Execute`** runs on the host only. It gets the definition, parameters, landing position (a throw's snapped to the navigation mesh, a developer drop's exact), rotation, course node, summoner and `Arrival`. A developer drop has `SummonerLookup` 0 and no `Summoner`. An exception is logged and the call is lost with its use spent.
- **Presentation callbacks** run on every peer. `OnBeaconLanded` gets the delay until arrival. The beacon beam and countdown label are always shown; the label is ForgeUI's countdown world marker (`AddCountdownMarker`).
- **Per-call data.** `OrdnanceApi.SendData(payloadType, summonerLookup, throwId, tag, bytes)` (host only, at most 192 bytes) reaches `OnData` on every peer. `SendState` also keeps the message, and the host replays it to players who join later with `Replayed == true`. `ClearState` forgets a finished call. The host keeps at most 256 states; a full store makes `SendState` return false, so refuse or undo the world change.
- **Lobby picks.** `OrdnanceApi.PickedParameters(payloadType, into)` fills `into` with the parameters of every call-in of that payload type that can arrive in the selected level, each once: the players' synced lobby picks (none when the level has `CallMenu` off) the level's developer drops, and every `Callable: false` definition of that type (read only; empty when there is none), so a payload can prepare its assets before the level.
- **Arrival mode.** `OrdnanceExecution.Arrival` and `OrdnancePresentation.Arrival` are `OrdnanceArrivalMode.Drop` or `Direct`. Player calls always drop; a level's developer drop may ask for `Direct`, and only payloads that return `SupportsDirect => true` get it (others are refused when the definitions load). A direct arrival is the same arrival with the vehicle stage left out: `OrdnanceDelivery.StartPod` / `StartShell` return null for it and `PodContentsDelay(arrival)` / `ShellContentsDelay(arrival)` are 0, so a payload written against them handles both; a payload with its own fall (an exosuit) leaves the fall out itself.
- **Blocking the call key.** `OrdnanceApi.BlockCalls(reason)` returns an `OrdnanceCallBlock`. While any block exists (`OrdnanceApi.CallsBlocked`), a call key press is ignored and a call in progress is cancelled. `Dispose` the block when its reason ends; calling it twice is safe, and calls work again when no block is left.
- **Lending a gun into the special slot.** The special weapon slot lease is ForgeCore's `Gear.SpecialSlot` (2.0 moved it out of this API): one arbiter for every mod that lends a gun into the slot, with the own weapon's kept stock, the special ammunition redirect and checkpoints. See ForgeCore's `DEVELOPERS.md`.
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
- `PodContentsDelay(arrival)` and `ShellContentsDelay(arrival)` give the host's timing for when the pod's weapon racks show their contents and the shell's petals are open (0 for a direct arrival). The built-in `Item` payload does not wait for it: it spawns its pickups right after touchdown (2.0 s) and the platform keeps them shut away (not drawn, not takeable) until the doors start to open at 5.0 s.
- A pod stays until the host says it is empty. `HostWatchPickups(summoner, throwId, ItemInLevel[])` does that for pickups. `HostReportPodEmpty(summoner, throwId)` declares it now. The empty pod then stays 15 s and sinks. A shell sinks 6 s after opening.
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

Borrowing rules (who may take, loans, returns) are yours; Arsenal keeps them in its own `LoanBook` and sends only its small loan notices on its own channel.

### Camera shake and drops

Camera shake is ForgeVisual's `CameraShake.Shake(at, ShakeSpec)` for every package (distance fall-off, a cap on shakes running together, the player's `Camera / ShakeStrength` in ForgeVisual's config). `Impact(owner, ground, OrdnanceImpact)` plays a touchdown (thud, debris, shake; `ShakeIf` lets you skip the shake, for example inside a vehicle; `NoiseRadius` above 0 also makes the game's world noise there that wakes sleeping enemies, on the host only, default 0). `Drop(body, landing, seconds, OrdnanceDropStyle)` falls a body without a container, with the touchdown at the end; `OrdnanceDrop.Cancel()` stops it.

### Turret looks

Register looks from `Load`, before definitions are read: `OrdnanceTurrets.RegisterLook("Pack:item", modelHandle, new TurretLookSpec { WorldScale = 1.24f })`. The model is a ForgeVisual `ModelHandle` from your own bundle with the parts `base`, `yaw`, `head` and `muzzle`. A `Gear` definition with `"Look": "Pack:item"` gets that look on every peer, late joiners included; the look id travels in the sentry's marker. `OrdnanceTurrets.Deployed(lookId, shell)`, `Online(lookId, sentry, at)` and `Expiring(lookId, sentry, position)` fire on every peer for sentries wearing a registered look, so you can add your own sounds. `TryLook(sentry, out lookId)` tells which look a sentry wears.

### Custom pod looks

Override `IOrdnancePayload.GetPodAppearance(OrdnanceDefinition)` to return a cached `OrdnancePodAppearance` built after your bundle loads:

```csharp
var art = new OrdnancePodAppearance(bundle.Model("my-pod"),
    new[] { new OrdnancePodDoor("door", Vector3.up, 105f) },
    new[] { new OrdnancePodSocket("weapon_anchor", new Vector3(.91f, 1.87f, .41f), Quaternion.Euler(0, 90, 0)) });
```

The model's origin is on the ground, its units are metres, and its scale is one. All peers must have the same model and definition, and the callback must give the same answer every time.

### Art and sound

- Build your bundle with this package's pipeline: `tools/export_blender.py` per model and `OrdnanceBundleBuild.cs` in Unity 2019.4.21f1. Copy `build_assets.ps1` and change the inputs and the bundle name (`FORGE_ORDNANCE_BUNDLE`).
- Register the bundle with ForgeVisual (the bundle format is unchanged): `Bundles.Register(modId, pack, path)` from your plugin's `Load`, then `Models.Get("Pack:prefab")`; `model.Spawn(position, rotation, parent)` makes an instance with the game's gear shader once a level has been built, and `bundle.TryTexture(name)` returns a standalone texture from the bundle's `textures/` folder. Ordnance's own bundle is the pack `Ordnance`. Particles you bake into a bundle go to `ParticlePool` (an active prefab of looping world-space systems with emission off, `maxParticles` equal to the capacity you pass to `Channel`).
- Metallic textures use R for metallic and G for smoothness. The shader has no detail map, so bake surface detail into the textures.
- Sound goes through ForgeAudio, not Ordnance. Put Ogg files and `cues.json` in an `audio` folder next to your DLL and call `AudioKit.Load`. Ordnance's own cues are in `audio/cues.json`.

## Network

Messages go through ForgeCore's channel for this plugin (events `NAinfini.ForgeOrdnance/<Name>`). The channel's handshake carries ForgeCore's message format fingerprint (computed from the registered messages' names, delivery and field layouts) and a fingerprint of the definition files and the registered payload kinds, so a player whose copy differs is refused on both sides and told in chat; a refused player cannot call anything. Clients talk only to the host. At level start every peer also sends the host a level greeting (the level epoch), which the host answers with the call state. Lobby picks and the host's level rules are ForgeCore lobby values (`picks`, `rules`): each player publishes its own, and players who join later receive them.

Add-ons open their own ForgeCore channel for their own messages; its format fingerprint refuses peers whose add-on messages differ. The registered payload type names are part of this plugin's handshake content, so a player missing an add-on is refused too.

## Logging

Logging goes through ForgeCore's `ModLog` and only errors are written (`Plugin.Error`). The mod calls `ForgeCore.Log` for positions and numbers.
