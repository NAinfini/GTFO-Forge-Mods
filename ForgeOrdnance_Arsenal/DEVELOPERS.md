# Forge Ordnance Arsenal for developers

This file is for people who edit the call-ins or build a similar add-on. Players should read `README.md`.

Arsenal is a working example of a Forge Ordnance add-on. It uses only the public APIs of [Forge Ordnance](../ForgeOrdnance) (1.0.1), [ForgeCore](../ForgeCore), [ForgeAudio](../ForgeAudio) and [ForgeVisual](../ForgeVisual). Supplies and turrets are plain definitions of Ordnance's built-in `Item` and `Gear` kinds; a turret's `"Look"` names one of the three looks Arsenal registers with `OrdnanceTurrets.RegisterLook` (`Arsenal:turret-burst`, `Arsenal:turret-sniper`, `Arsenal:turret-shotgun`; code in `Native/ArsenalTurrets.cs`, which also plays the deploy, idle and expire sounds for sentries wearing those looks). Code exists only for the three kinds Arsenal adds: `arsenal.Strike`, `arsenal.HeavyWeapon` and `arsenal.Deployable`.

## Layering: what this package holds and what it does not

Arsenal exposes no C# interface (every type, wire messages included, is internal). Arsenal is the example content pack: strikes and area bombardments, heavy weapon drops, supply pod contents and three turrets (models, looks, sounds), with their definitions, icons and sounds. It uses only Ordnance's public API and the four libraries. To add your own turret or supply, copy its approach in a separate mod; Arsenal does not need to change. If a general mechanism is missing, add it to the layer below.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Rules with no game reference: payload parsing, strike patterns, target choice, the beam's route, the heavy weapon loan rules, network records. |
| `Native/` | The plugin: payloads, host logic, effects on every peer, heavy weapon loan and launcher. |
| `ForgeOrdnance/arsenal.forge` | The 30 shipped definitions. Ordnance reads every `ForgeOrdnance` folder under `BepInEx/plugins`. |
| `ForgeOrdnance/lang/` | The definitions' names and descriptions in each language (`<Id>.Name`, `<Id>.Description`). |
| `lang/` | Arsenal's own words (the rack prompt) in each language. |
| `Assets/Audio/cues.forge` | ForgeAudio cue settings. The converted Ogg files beside it are local build outputs. |
| `tests/Core/` | Offline tests. They also load `arsenal.forge` through Ordnance's definition parser. |
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

The zip holds `manifest.json`, `icon.png`, `README.md`, `CHANGELOG.md`, `LICENSE`, `ForgeOrdnanceArsenal.dll`, `forge-ordnance-arsenal.bundle`, `audio/` (Ogg files, `cues.forge`, `LICENSE.txt`), `models/LICENSE.forge`, `lang/`, and `ForgeOrdnance/arsenal.forge` with the 30 icons and `lang/` beside it; both `lang/` folders must hold `en.forge` and `zh.forge`. A manual install puts the same files in `BepInEx/plugins/NAinfini-ForgeOrdnance_Arsenal/`.

## Supplies and turrets

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

## Payload `arsenal.Deployable`

The standing devices (`arsenal.deploy.foam` and `.mines`, both the `Dispenser` kind; `.scanner`, `.floodlight`). They share the turrets' delivery: the turret shell and its base (`OrdnanceDelivery.StartShell`); only the head differs, and `Head` (`Pack:item`) names it, so a new head model changes the definition only. The shell bores in, throws its lids off and the head rises out of it over `RiseSeconds` (hidden `RiseHeight` below until then; the timeline fields and `BaseScale`, the base's size for this head, are ForgeOrdnance's `BurrowSpec`), the device works, then the head sinks back and the shell sinks away (`Deployables`, every peer; the host runs the work in `DeployableActions` and `Dispenser`). `Kind` picks the work and its fields, all with defaults in `DeployableSpec`:

- `Dispenser`: one flow for every munition, the data says which. The head rises, its plates open like a skirt, its column spins and throws `Count` munitions out of the holes (one after another, each in a short arc to a landing the host chose), the plates shut, the upper part sinks back into the base (which stays), is loaded and rises again (the shell's `OrdnanceShellAppearance.Dive`), `Rounds` times (`Count` times `Rounds` at most 36); then the head sinks and the munitions keep lying for `Lifetime` seconds. `MinRange`, `MaxRange` (landing distance), `FlightSeconds`, `OpenSeconds`, `SpinSeconds`, `SpinTurns`, `ReloadSeconds` set the motion (`DispenserPlan`, the one timeline every peer follows); `Munition` is the munition's own model (`Pack:item`, empty: the strikes' bomblet), `SeatScale` its size in a hole against the size it lands at (it grows in flight), `Plates` how many plates the head has. A lying munition arms after `ArmSeconds` and, when an enemy comes within `TriggerRadius`, bursts: an explosion (`Damage` over `BlastRadius`, pushing `Force`; through ForgeCore `Combat.Explode`, hurts teammates like any explosion) and/or `Spray` pellets of the game's C-foam thrown in every direction at about `SpraySpeed` m/s (Arsenal's `ArsenalCombat.SprayGlue`, the game's own foam projectile, so the game replicates, merges and settles it, on enemies and doors too). At least one of `Damage` and `Spray` must be set. The field is `SendState` data (`DataTag.Mines`, `MineWire`), so late joiners see the munitions still lying. The vanilla trip mine is not used: its only detector is a laser line along a wall mount (no radius to shrink), and its foam charge throws in a cone from that mount; a hand-placed mine or foam mine in a player's hands is unchanged.
  The dispenser is two models: the base (the shell's closed drill and cap, shared by every device) and the `Head`, the upper half, which must carry the nodes below (a model without them is refused when its definition is read): `column` (turns about its up axis, its origin on the axis); under it `plate1`, `plate2` ..., as many as the model has (open like a skirt: turned about their own X axis, hinged at the top edge with the origin on the hinge, the bottom edge swinging outward toward local -Z, 70 degrees open); `mount1`, `mount2` ... (an empty node at each hole, its -Z outward, under its plate so it swings with it; munitions take them in number order, so number them plate after plate round the column from the plate at the model's front, clockwise from above, the first hole of every plate and then the next: one round then never repeats a hole). `Articulation` (Native/Mechanisms) poses them and `Thrown.Arc` draws the flight. A munition lying in the field is a `MineWire` record that carries the definition's hash, so a late joiner or a new host draws and arms it by the definition that threw it.
- `Scanner`: `Range`, `Scan`, `UnfoldSeconds`. The head may have `petal1`, `petal2` ... (modelled open, folded 42 degrees about their own X axis) and a `yaw` part that turns: it rises folded, unfolds over `UnfoldSeconds` and only then works and turns, and folds again before it sinks. The host tags every untagged enemy in range that is awake (`ForgeCore.Enemies.Alert` is Awake or Hunting; sleepers are skipped) with the game's own tag (`ToolSyncManager.WantToTagEnemy`), so every player sees the usual markers.
- `Floodlight`: `LightRadius`, `FogRadius`, `Intensity`. A `fan` part of the head keeps turning about its own up axis. A ForgeVisual point light on every peer, and the game's own fog repeller item (item 117, stood as an instance) whose sphere is resized on every peer.

Code prefixes: supplies start with up, down. Turrets start with left, up, right. Standing devices start with left, down, left. Strikes start with right. Heavy weapons start with down, left. A test keeps these clear of the codes in Ordnance and Frames.

## Payload `arsenal.Strike`

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
- The host builds the shell pattern from the summoner and throw, brings each point back to this side of the first wall between it and the beacon, drops it onto the floor, and applies damage when each shell lands. Each shell first shows its reach (a ring at exactly its radius, a glow filling it), then falls, gaining speed (`StrikeTiming.FallCurve`), nose first; fired shells on a slant of about 12 degrees from one side per call (`StrikePlan.ApproachSide`, derived on every peer). `Core/StrikeTiming.cs` holds these seconds per kind (`Pace`: warn + drop, from 0.35 s for Hunter to 1.3 s for Precision) and every other time below.
- Gas areas start 0.4 s after landing, incendiary 0.25 s, stasis 0.3 s. The beam charges for 1 s before it moves, and `Duration` counts from then.
- The beam only picks enemies it can reach on the navigation mesh (a complete path no longer than `Spread`) and see from above. It follows that path at constant speed, checks every move against the level, drops a target it can no longer reach, and holds still when none is left.
- A strafing run starts 1.2 s after its call arrives and runs from the beacon along the throw direction (flattened to the ground plane) up to the first wall ahead; an enemy beside the strip behind a wall is not hit. Every `Tick` the host sends one game bullet hit (`Combat.BulletDamage`, so armour and weak spots apply) to each enemy standing in the strip between the head's last and new place, so each is hit once as the head passes; there is no explosion. Peers draw a tracer streak with its ground point and scorch marks from the host's `Strafe` record (no aircraft).
- Hunter picks its target with `ArsenalCombat.Largest` (highest maximum health within `Spread` of the beacon among the enemies whose course node is the beacon's landing node, never one in another room; no node, no target; an enemy in sight of the beacon before one that is not, nearer wins ties). A laser (`TargetLaser` (Native/Mechanisms), every peer following the host's reports 10 times a second) stays on it for 1 s, moving to the next largest if it dies, then the shell drops in 0.35 s (`StrikeTiming`); `Damage` goes straight to the target (always in the landing room), `TickDamage` is `ForgeCore.Combat.AreaDamage` around the impact, hurting only enemies of the landing room (no blockers query, no teammates). With no enemy the laser sweeps around the beacon for 5 s, picks up any enemy that comes in reach, and otherwise flickers out over the words "No target": no shell, no damage, the use comes back and 30 s of cooldown stay (`OrdnanceApi.RefundCall`), and the caller gets a notice. A target that walks out of the room is dropped like a dead one.
- Stasis slows enemies in the field to the `Slow` share through ForgeCore `Enemies.Slow` (it scales the animator speed): one lease per enemy per field, taken while the enemy is inside, ended when it leaves, when the field ends, or when the level, a checkpoint or the host changes. Overlapping fields take the slowest share and do not stack; ForgeCore puts the enemy's own speed back when the last lease ends or the enemy dies or is reused, and leaves a speed the game set meanwhile alone. Arsenal keeps the share, radius, duration and the rule that enemies held by C-Foam are skipped. Arsenal patches no enemy lifecycle method itself.
- Explosions use the game's explosion damage, so falloff, wall blocking and friendly fire follow the game. Fire and gas use the game's fire damage entry, on what is inside the radius and in sight of the centre (a wall stops them, an open door does not), a teammate at 0.4 of the tick. The stasis field stops at walls the same way (`Enemies.SlowZone`'s `admit`).
- Every peer draws shells, blasts, areas and the beam from data it already has; effects send no messages. Arsenal owns what they look like (the moments in `Native/StrikeFx.cs`, the strafing streak and the area marks in `Native/StrikeEffects.cs`) and ForgeVisual draws them: blasts, shell trails, area fields and the beam are slots of `ArsenalFxSlots` (a slot the table does not list logs its error and draws nothing, no moment has a second look); warnings, reach rings, a bomblet's small blast and a dropped canister's tracer are sprites of its shared pool (`SpritePool`; the sheets are registered as `Arsenal:smoke`, `Arsenal:fireball` ... when the bundle loads, and smoke is the sheddable sheet the overdraw budget gives up first), scorches and beam burns use its decal rings (`SpritePool.CreateDecals`), short flashes and the area and beam lights its shared effect lights `PointLights` (flashes and held lights; the player's ForgeVisual `Effects / FlashLights` turns the flashes off), the shell landing shake its `CameraShake`, a canister's tracer `Trails`, area ground marks and the strafing streak's reticle `GroundMarker` (gas `Hatched`, fire `Flames`, stasis `Dial`, streak `Reticle`), and the streak's lines `Beams`.

## Payload `arsenal.HeavyWeapon`

```json
{ "Type": "arsenal.HeavyWeapon", "Model": "Arsenal:heavy-mg", "Gear": 38, "AmmoRatio": 1 }
```

| Field | Meaning |
|---|---|
| `Model` | A ForgeVisual model, `Pack:item`. Any pack works, so a third party adds a heavy weapon with its own pack and a definition (the general steps are in [Writing a content pack](../ForgeOrdnance/DEVELOPERS.md#writing-a-content-pack)). Checked when the definition file is read, not at the first shot: the pack must already be registered (in the owning plugin's `Load`, before definitions are read), otherwise the definition is refused naming `Model`; the model's own `RightHand` grip (`SightLook` below is optional) sets the hold; a missing right grip is the model's origin and a missing left hand is derived from the game's own hold. A pack that is missing does not load the weapon in a lesser form, the definition simply is not loaded. A refused definition holds no hash. In the weapon list the lent weapon carries the name of the first definition (ordered by Id) that uses the model: Arsenal's own text for its three, else the definition's `OrdnanceApi.DisplayName`, the same on every peer. |
| `Gear` | Offline gear ID or name of a special-slot weapon. Shipped: machine gun 38 (`MachinegunAuto`), rifle 42 (`HELRifleSemi`), launcher 43 (`HELGun`: two-handed, 9 rounds, 3.3 s reload, no charge-up on the lent weapon, with its bullet replaced by a grenade). |
| `Count` | 1 or 2, default 1: how many of the weapon lie in one pod, one per weapon bay; the pod leaves once all of them are taken. |
| `AmmoRatio` | 0 to 1, default 1: the reserve given besides a full magazine, as a share of the slot's full reserve (0 is the magazine alone, so a one-round gun goes back after its shot). |
| `Muzzle` | `none`, `mg` (default) or `amr`: the library muzzle flash drawn when the gun fires; `none` for a weapon whose effect is drawn elsewhere. Shipped: anti-materiel rifle `amr`, tracking rifle, grenade launcher and recoilless rifle `none`, the rest `mg`. |
| `RecoilScale` | 0.1 to 5, default 1: how hard the gun kicks, as a share of the lent gear's own recoil block (`ForgeCore.Gear.ScaledRecoil`). Shipped: machine gun 1.4, anti-materiel rifle 1.6, recoilless rifle 1.5. |
| `Homing` | Optional `{ "Angle", "Range" }` (defaults 12 degrees, 60 m). When the local shooter fires, `ArsenalCombat.PickAimed` locks the enemy nearest the crosshair inside that cone and range with a clear line to it, the game's bullet is withheld (`BulletHit` hook, as for the grenade launcher) and the hit is resolved from the muzzle to that enemy through `Combat.Shot` (the game's bullet resolution: limb, weak spot, armour, replication, hit marker), so it hits even where the straight ray would hit air. The shooter's peer decides the hit as for a stock bullet and the host settles the damage; the one `HomingShot` message (origin, target, seed) only lets every peer draw the curving tracer (Arsenal's `ArcTrails`), which decides nothing. A shot with no enemy in the cone is the game's bullet, untouched. |
| `Grenade` | Optional `{ "Damage", "Radius", "Speed", "TrueDamage", "EdgeShare" }` (defaults 140, 4, 32 m/s, false and 0.5; unknown members are refused). `TrueDamage` makes the blast `ForgeCore.Combat.AreaDamage` (host only): each living enemy in reach that the centre can see takes the damage at its body's own damage request, no armour or weak spot step runs and no enemy field is written (other guns hitting the same enemy are reduced by its armour as ever); the damage falls off linearly from `Damage` at the centre to `EdgeShare` of it at `Radius`; players are not hurt. Without it the blast is the game's explosion (`Combat.Explode`). With it the weapon fires grenades instead of bullets, host-checked as described below; without it the weapon fires its own bullets. |

Model contract (the shipped models are `Arsenal:heavy-mg`, `Arsenal:anti-materiel-rifle` and `Arsenal:grenade-launcher`; those names are stable): the root carries an optional empty `RightHand` (the right grip; without it the model is held at its origin, the authoring grip), an optional `LeftHand` (its own palm point; ForgeVisual derives the left hand and leaves this node where it is) and optionally `SightLook` (the centre of the sight window, forward = the model's front; without it the game's own sight aims), plus a `Muzzle` at the bore's end, which `GunMount` uses; the model is built in real metres and its size in the hand is ForgeVisual's gun-holding rule (`GunHold`), the same for every gun: no gun here has a size of its own. `Data/gun-grips.json` (ForgeVisual `GunGrips`, embedded) gives each model the `machinegun-2` game hold preset of gear 43, not hand points: the mount keeps the model's own hand sockets (a missing right grip is its origin) and derives a missing left hand from that hold, and it pulls the left hand back along the gun into the arm's reach; a row's optional `adjust` is the only per-gun hand fine-tune. The lent gear's public name is `Forge Heavy ` and the model id's 32-bit FNV-1a hash in 8 hex digits (20 characters; the repository's other markers assume about 25 at most), filled in when a definition is accepted; two ids with the same hash are refused naming both. The rack record carries the same hash, and a record whose hash this install never accepted is dropped with one error line (every peer needs the same heavy weapon packs).

How it works:

- The weapon stands upright in one of the pod's two weapon bays (chosen by throw ID, the same on every peer).
- A player within 2.4 m, looking at the weapon, gets an interaction prompt. The use key makes the claim through Ordnance's `OrdnancePod.AddClaimable` (reach, aim, prompt, request, reservation, timeout and messages are Ordnance's); Arsenal supplies the prompt text, the host check (one loan per player, no borrowed gear, effects ready), the install, and the host verification that builds the loan. Committing starts the loan; Arsenal's `LoanBook` keeps loans and stashes, and returns travel as a small `LoanNotice` on Arsenal's own channel.
- The loan saves the player's special weapon and ammo, equips the lent gear with a marker name (the packet name stays the marker; `ArsenalText.NameHeavyWeapons` registers each marker with `UIKit.GearName`, so ForgeUI shows the call-in's name from `ForgeOrdnance/lang` on this player's weapon list), and fills the ammo to `AmmoRatio`. The payload may set absolute archetype numbers (`Damage`, `ShotDelay`, `Cooldown`, `Chargeup`, `Magazine`, `Reload`, `PrecisionMultiplier`, `StaggerMultiplier`, `Pierce`, `AmmoCost`, and `FalloffStart` / `FalloffEnd`, the metres where the damage falloff begins and bottoms out, written together with the end beyond the start; unset ones stay the game's; the grenade launcher fires no bullets and ignores both falloff fields; the rack record `RackBytes` is 108 bytes and carries the tuned numbers and the pod's weapon count, so every peer needs the same build): `HeavyWeapons.Tune` gives the lent instance its own copy of the archetype with them applied and reads it back (the shared block, and the player's own gun of the same model, are untouched), and the rack record carries them to every peer. The host's launcher budget and cadence come from the same `ArchetypeTuning.Over` of the vanilla block. It ends when the clip and reserve are both empty (the heavy weapon is gone), on the drop key, or when the player goes down; the own weapon is then put back with its saved ammo.
- Switching: with the special weapon in hand, `InputAction.SelectSpecial` (the player's own binding, not while reloading, with a 0.5 s cooldown) swaps through the game's own `EquipLocalGear`, so the swap replicates the usual way. The heavy weapon's magazine and reserve wait in a stash, the own weapon's in the loan's restoration record; each is written back once its gear has assembled in the slot. The switching peer tells the host (`LoanAction.Stashed`) before the swap; `LoanBook.Observe` then keeps the loan while the own weapon is in the slot and takes the heavy weapon back once the marker returns. The stash never survives a checkpoint reload, a host change keeps it, and drop, down and level end clear it.
- Ammunition packs: a Harmony pair on `PlayerAmmoStorage.PickupAmmo` swaps the pool for the saved own weapon's reserve around the game's pickup while the heavy weapon holds the slot, so the game's caps and rounding apply to the own weapon and the heavy weapon's pool is put back untouched.
- Grenades need a host-issued loan and an increasing shot number. The host checks the held weapon, the origin, the fire rate and the remaining ammo, then launches from its own view of the player. Clients only predict flight: the shooter's round shows at once with its own muzzle flash and sound, the host's broadcast replaces it without showing those again, and a round the host did not answer within 1.5 s is removed. The host's burst position decides the explosion for everyone.
- Checkpoint reloads and host changes revoke launcher firing. Installed loans keep their recovery data and can be returned.

## Settings

Arsenal has no config file of its own. The give-back key is the ForgeUI key action `DropHeavyWeapon` (`Native/ArsenalKeys.cs`, G, context `on-foot`), changed in the game's settings (Controls) and saved in `NAinfini.ForgeUI.keys.cfg`.

## Audio

`audio/cues.forge` follows the ForgeAudio format. Each cue lists layers of Ogg clips, a bus and a length cap. The four boom cues (small boom, big boom, cluster split, grenade burst) have their own far layer from 40 m. Sound sources and licenses are in `Assets/Audio/LICENSE.forge`.

Loops (fire, gas, stasis and beam) are attached to their owner and fade out in 150 ms when it is destroyed. A cue that fails to load is logged by name and only that cue goes silent. The devices' loops (dispenser spin, floodlight fan) are `Sustain`s that end with their device or when the part stops; the scanner sounds once per scan (`radar.sweep`, a 0.8 s one-shot), when the scan goes out. The glue munition's spray plays the game's own glue gun shot (`GLUEGUNFIRE1SHOT`) through `AudioKit.PostGameEvent`, not a cue; the shell's lid blast (`turret.lid_blast`) is a ForgeOrdnance cue.

`tools/build_audio.ps1` rebuilds the clips from `sources.forge` using ForgeOrdnance's `stage_audio.py` and ForgeAudio's converter. No Unity build is needed for audio.

## Art

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
| `recoilless` | Recoilless rifle; in game it wears ForgeVisual's `red-dot-1x` on its top block (`Data/gun-sights.json`) |
| `heavy-mg` | Minimi with an open reflex sight on its top cover (carry handle and sights taken off), the ammo box is the separate `Magazine`; in game ForgeVisual's `holo-1x` takes the reflex sight's place |
| `anti-materiel-rifle` | B&T SPR 300 with an open reflex sight on its top rail, the magazine is the separate `Magazine`; in game ForgeVisual's `scope-2x` takes the reflex sight's place |
| `grenade-launcher` | RG-6 revolver grenade launcher, open reflex sight on its top tube; in game ForgeVisual's `red-dot-1x` takes its place |
| `tracking-rifle` | Honey Badger with an open reflex sight; in game ForgeVisual's `holo-1x` takes its place |

In game every heavy weapon wears a ForgeVisual sight (`Data/gun-sights.json`, ForgeVisual `GunSights`; its DEVELOPERS): `holo-1x` on the machine gun (fast pickup while it sweeps) and the tracking rifle (it locks on, it needs view more than magnification), `scope-2x` on the anti-materiel rifle (long precise shots, aimed at the 2x field of view), `red-dot-1x` on the grenade launcher (small, keeps the drum and tube in view) and the recoilless rifle (its own `SightLook` hung 7 cm left of the tube with no optic, so aiming looked past the tube's edge). Each row stands the sight's base on the highest part of the gun under it, with the line through the window clear of the gun from 0.3 m behind; the reflex sight and `reticle` below are replaced. Each heavy weapon model with an optic has the empty `SightLook` at the centre of its sight's window (identity rotation, so its forward is the model's front) and a small emissive `reticle` there. When the look goes on a weapon, `GunMount` points the gear's `GearPartHolder.m_aSightLook` and `ItemEquippable.SightLookAlign` at it, so aiming lines the camera up with the model's own sight instead of the hidden game sight. A model without `SightLook` (the recoilless rifle) aims with the game's own sight and logs nothing. The sight has no glass (the gear shader is opaque), so its window is open. The game's item camera narrows to 15 degrees while aiming, so a sight's size on screen is set by its distance from the aim camera: the built-in sights are 50 to 60 percent of the download's size and stand forward of the grip, so the block under the window stays within about 3 degrees of the centre. `tools/fit_sight.py` puts the sight on a gun master (Blender, background): it cuts the sight's own base plate and lever block away, stands the housing straight on the highest part of the gun under it (no riser), stops when any part of the gun shows through the window from 0.3 m behind it (it prints the `import_body.py --remove` boxes that take that part off), and adds the reticle and `SightLook`; the arguments used for M08–M10 are in its header. The three gun bodies are textured Sketchfab downloads turned into masters by `tools/import_body.py` (front −Y, grip at the origin, the bore on x = 0, scope parts removed, reduced to about 19,000 triangles when larger; `--split` makes the separate `Magazine` object and `--socket` the `RightHand` and `LeftHand` palm points). The gear shader has no occlusion input, so the occlusion is multiplied into the base colour, and the normal and metallic/roughness maps are kept.

Authors and licenses are in `Assets/Models/LICENSE.forge`, which ships in the package. Effect textures are CC0. Every definition names an icon `<group>-<name>.png` that sits beside `arsenal.forge`. The icon source is `Assets/icons/` (local media, not in Git): the owner's hand-made icons go there as `<group>-<name>.png`, 256 x 256 transparent PNG, and replace the rendered ones; the package script reads this folder.

## Network

Arsenal's own messages (loan notices, grenade shots and bursts; a burst record, 41 bytes, says whether the round was a rocket) go through its ForgeCore channel, whose message format fingerprint ForgeCore computes. A player with other Arsenal messages is refused by both handshakes (an error in each player's log) and cannot call. Damage goes through ForgeCore's `Combat` (the game's explosion and fire damage requests), area queries through `Enemies.Near`, players by network lookup through `Level.PlayerByLookup`, the floor under a point through `Level.FloorBelow`, the heavy weapon swap through `Gear.EquipLocal` / `Gear.WriteLocalAmmo`, and the special weapon slot lease through `Gear.SpecialSlot` (the one lease an exosuit's gun from Frames also takes).

## Logging

Logging writes errors only: `Plugin.Logger.ErrorOnce` goes to ForgeCore's `ModLog`, which writes each exact message once. There are no other log lines and no diagnostics setting.


How a heavy weapon model sits in the hands: ForgeVisual's `GunMount` (its DEVELOPERS), the same calculation as every gun model. This package gives only the model, `Front` and its game hold preset (`Data/gun-grips.json`); the model's `RightHand`, optional `LeftHand`, `muzzle`, `SightLook` and `Magazine` are its data. Its size, both hands (the model's own sockets, the origin for a missing right grip and the game's own hold for a missing left hand, with the left pulled back along the gun into reach) and the first-person hold come from ForgeVisual's `GunHold` rule; the recoilless rifle's back-blast is measured from the tube's shown length (`GunMount.Hold.ShownLength`). A model or game gun that cannot be mounted leaves the game's gun showing (one error in the log).


A level drop may set a heavy weapon's AmmoRatio (0 to 1), Magazine (whole, 1 to 1000) and AmmoCost (0.01 to 1000, the reserve one shot spends, the block's CostOfBullet) through its Overrides; the rounds given are floor(AmmoRatio * the slot's full reserve / AmmoCost) + Magazine, with the native numbers behind them on every peer, and `ArsenalControl.Racks()` reads all three back (`default` where the block's own stands); `arsenal.Strike` accepts Direct (same as Drop for a developer drop). See ForgeOrdnance's DEVELOPERS, Level control.
