# Forge Ordnance Frames for developers

This file is for people who edit the suits or build a similar add-on. Players should read `README.md`.

Frames is an add-on for [Forge Ordnance](../ForgeOrdnance). It uses only the public extension API (version 2.0), ForgeCore for networking, input, the special weapon slot lease, damage, nearby enemies, noise and logging, ForgeUI for every piece of interface, ForgeAudio for sound, and ForgeVisual for drawing in the world. Suits are data. Code exists only for the mechanics.

## Layering: what this package holds and what it does not

Frames is the powered-armour content pack: the four suit models, opening and closing, driving rules, suit guns, the cockpit layout, look, icons and text, the boot screen's shape and pacing, and suit sounds. The cockpit is built with ForgeUI mechanisms, drops and call-ins go through Ordnance, effects through ForgeVisual. To add a suit, write a suit definition with its model, icons and sounds.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Rules with no game reference: the definition parser, host decisions, sprint heat, boarding timing, the power-on screen and plan, the cockpit's data, words and hide-and-restore state, network records. |
| `Native/` | The plugin: payload, network, world state, effects, the pilot, the first-person shell, the HUD, hooks. |
| `Definitions/frames.json` | The four shipped suits. They install as `ForgeOrdnance/frames.json` next to the DLL, where Ordnance finds definition files. |
| `Assets/audio/` | ForgeAudio `cues.json`, the source record and licenses. The Ogg files are local build outputs. |
| `Assets/Models/License.txt` | Authors and licenses of the suit models. |
| `tests/Core/` | Offline tests. |
| `tools/` | Model, icon, audio and packaging scripts. |

## Build and test

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeOrdnance_Frames/ForgeOrdnanceFrames.csproj -c Release --artifacts-path artifacts/dotnet-frames -p:GTFOBepInExPath=<profile>/BepInEx
dotnet run --project Mods/ForgeOrdnance_Frames/tests/Core/FramesCore.csproj -c Release --artifacts-path artifacts/dotnet-frames
python -B Mods/ForgeOrdnance_Frames/tests/Core/package_checks.py
python -B Mods/ForgeOrdnance_Frames/tests/Core/boarding_checks.py
python -B Mods/ForgeOrdnance_Frames/tests/Core/fx_checks.py
```

Frames is not part of `Forge.Architecture.sln`. `package_checks.py` also compares an installed plugin's `ForgeOrdnance/frames.json` and `audio/cues.json` with the sources (set `FRAMES_PLUGIN_DIR` to point at the plugin folder; without an installed plugin that check is skipped).

## Effects and shared mechanisms

- **Particles.** `tools/fx/FrameFxBuild.cs` authors twenty particle systems in one prefab (`FramesFx`, Unity 2019.4.21f1; `tools/fx/build_fx.ps1`, which holds `artifacts/work/unity-lock.txt`) and writes `Native/FxBundle.Generated.cs`. The bundle holds the prefab and the textures, no materials and no shader. `FrameFx` registers it with ForgeVisual (`Bundles.RegisterBytes`, `ParticlePool.Register`), takes one `ParticleChannel` per system with its `maxParticles` as the capacity and draws it with ForgeVisual's particle shader, texture and blend from `FrameFx.Table`. A system or texture added to the build is added to that table too (`fx_checks.py` compares them).
- **The game's own effects.** Muzzle flash (`Game:Sentrygun_MuzzleFlash_3RD`, `Fx.Attach`), tracer (`Fx.Tracer`) and the rocket explosion (`Game:FX_Tripmine`, `Fx.Play`) go through ForgeVisual's catalogue; `FrameFx` keeps the handles.
- **Barrier.** Drawn nowhere in the world or on the pilot's screen: it is the armour bar's barrier line, its sounds (`FrameVisual.TickBarrier`: raised, broken, hit when its strength drops), the visor's interference on a stopped attack and the push pulse (FrameVisualFx).
- **Worn suit, shell and leaving motion.** The body mechanisms are ForgeVisual's (`Models.CopyPose`, `Models.MirrorHeld`, `Models.WearFirstPerson`, `Models.BindBodyClip`; see its `DEVELOPERS.md`); this package passes its rig. `Core/FrameBodyRig.cs` holds the suits' bone map: `PoseLinks` maps the 16 suit bones (pelvis, spine, chest, head, upper arm, forearm, hand, thigh, shin, foot) to the game's humanoid bones (the game's upper chest and neck fold into the suit's chest; it has no shoulder, finger or toe bones), `ArmReach` bends both suit arms so each wrist lands where the body's does from the chest (the suit's arms are not the body's scaled, so turns alone leave the hands off the grips), and `ShellBones` turns a definition's `Shell` into the first-person shell's bones (the upper arms only line the skeletons up, each leg bone is sized by its own leg, the rest by the shoulder span; the opening parts stay shut on the shell). `Native/FrameVisualPose.cs`: every peer, after the game animated the pilot (in `LateUpdate`, and again after that body's `PlayerSyncIK.UpdateIK` when it ran later: `PoseAfterIK` in `PilotHooks.cs`, put on with the first pilot), the suit takes its pilot's body pose and holds a copy of the pilot's wielded item in its right hand; that copy's muzzle is the origin of the flash and tracer. The local pilot's suit and its held copy are drawn as shadows only; empty suits stand in the rest pose the bundle saved. `LocalPilot` builds the shell (`Models.WearFirstPerson`) during the elevator ride or a call-in's countdown and lights it in the suit's colour (`_EmissiveColor`). `PilotBody` loads the bundle's `pilot-motion` rig and its PilotExit clip, checks them against `PilotMotion`, and carries the camera on the eye point of the posed head.
- **Light strips.** `Glow.For(renderer)` per renderer; `FrameGlow` decides the colour.
- **Collider query.** The breach charge uses ForgeCore's `ColliderQuery(64, grow: true)`: a full buffer is replaced by a larger one and the query repeated, so the result is complete.
- **Readouts and settings.** The cockpit's zone, heading and height lines read ForgeUI's `UIKit.Location`. The extra-lights switch is bound through ForgeCore's `Settings` (the plugin's own config file). The keys are ForgeUI key actions (`Native/FrameKeys.cs`): `Exit` (G, hold), `Special` (V) and `Rockets` (B), all in the context `exosuit` so they may share keys with on-foot actions; the player changes them in the game's settings (Controls) and they are saved in `NAinfini.ForgeUI.keys.cfg`. Boarding is the game's own Use key.
- **Sound values.** Pitch and gain of a cue are in `audio/cues.json` (the planted gun's bolt: pitch 1.35, gain 0.88 x 0.55; the walking step: gain 0.25, no sound of its own at the start of a sprint); only the rising tone of the power-on blocks is set in code (`FrameAudio.Section`), because it follows play order.
- **Noise.** Enemies hear a piloted suit `FrameNoise.PilotMultiplier` (2) times as far as an ordinary player, in every movement state. The game gives a player no noise radius of its own: a walk, run, jump or landing is heard inside the enemy's own `movementDetectionDistance` (the default detection block's is 8 m) and a crouch inside a fixed 1.9 m. While a pilot wears a suit, `FrameUnit.HostPilot` holds a ForgeCore `PlayerNoise.Scale` lease on that player (host only, where enemies detect), so the game's own noise for the pilot is heard twice as far (crouch 3.8 m, walk and run 16 m with the default detection); ending the pilot or the level ends the lease. The suit adds no footstep noise and has no noise fields in its definition. A suit's own landing has no pilot to hear it, so the touchdown impact carries `OrdnanceImpact.NoiseRadius` = the multiplier times the default detection block's `movementDetectionDistance`, read from the game (`Noise.LandingRadius`, 16 m with the stock block; no block is an error line and no noise); ForgeOrdnance makes it on the host only, and a suit found already standing lands with no noise.
- **Rocket tail.** `FrameFx.RocketTail` rents a ForgeVisual `Trail` per rocket: two `TrailPuff`s into the `FlameSmall` and `SmokeLight` channels (flames 22 a second, smoke half as many; sizes, lives and drift are the numbers in that method).
- **Effect lights.** The destruction flash, the wreck's fire and the planted siege lamp are ForgeVisual `PointLights` held from `TickFxLight`; the extra-lights setting turns all three off. The siege lamp lights only on the other peers: it sits half a metre from its own pilot's eye, where the game's fog turned it into a large red glow over the right of the view.

## Release package

```
pwsh Mods/ForgeOrdnance_Frames/tools/build_assets.ps1
python Mods/ForgeOrdnance_Frames/tools/prepare_audio.py
python Mods/ForgeOrdnance_Frames/tools/package_release.py
```

`build_assets.ps1` needs Blender 5.2, Unity 2019.4.21f1 and Python 3 with numpy. It builds `forge-frames.bundle` from the four source models and the pilot's leaving motion (`-Pilot`: `PilotMotion.fbx` and `motion.json`, made by the pilot-motion Blender scripts) and writes a receipt that binds the bundle's hash to the durable inputs only: the tools and definitions of this repository and the cut files, cut textures, pilot motion and prop GLBs under their recorded roots (never the stage folder or per-model outputs, which every run rebuilds). Any change to one of them requires a rebuild. In a worktree, point `package_release.py` at the main checkout's local media with `FORGE_MODS_ROOT` and `FORGE_ARTIFACTS_ROOT`. `package_release.py` requires that receipt and writes `NAinfini-ForgeOrdnance_Frames-<version>.zip` to `artifacts/work/ordnance-release/`. It does not publish.

The zip holds `manifest.json`, `icon.png`, `README.md`, `CHANGELOG.md`, `LICENSE`, `ForgeOrdnanceFrames.dll`, `forge-frames.bundle`, `ForgeOrdnance/frames.json` with the four icons and the suits' names and descriptions (`ForgeOrdnance/lang/`, from `Definitions/lang/`), the cockpit's words (`lang/en.json`, `lang/zh.json`), the cockpit art delivered so far (`hud/`), `audio/` (Ogg files, `cues.json`, `LICENSE.txt`) and `models/LICENSE.txt`. Install all of it together. A DLL with older definitions rejects every suit. `package_release.py` lists the cockpit art that is still a placeholder.

## Suit definition

Payload type `frames.Exosuit`. Ordnance's definition fields (`Id`, `Name`, `Code`, `Cooldown`, `Uses`, and so on) work as in its `DEVELOPERS.md`.

```json
{
  "Type": "frames.Exosuit",
  "Model": "Frames:frame-m15",
  "Armor": 100,
  "DamageReduction": 0.25,
  "EyeHeight": 2.15,
  "Movement": { "Walk": 1.2, "Sprint": 1.3, "Jump": 1.6, "SprintSeconds": 6, "SprintRecovery": 8 },
  "Weapon": { "Gear": 38, "DamageMultiplier": 1.8, "Magazine": 100, "Reserve": 600, "RecoilMultiplier": 0.45, "ReloadMultiplier": 0.75 },
  "Light": { "RangeMultiplier": 2, "AngleMultiplier": 1.4, "IntensityMultiplier": 1.2, "Colour": "#FFEBD1", "Glow": "#22D3FF" },
  "Boost": { "Seconds": 3, "SpeedMultiplier": 1.8, "Cooldown": 20, "BreachDoors": true, "Shove": 10 },
  "Opening": { "Parts": [ { "Node": "plate.back.L" }, { "Node": "plate.back.R" } ] },
  "Shell": [ { "Bone": "upperarm.L", "Body": "LeftUpperArm" }, { "Bone": "pelvis", "Body": "Hips" } ]
}
```

Unknown fields, wrong types and out-of-range values are errors that name the field. Ordnance then skips that definition.

| Field | Meaning |
|---|---|
| `Model` | The suit's prefab as `Pack:item`, a ForgeVisual model of any registered pack (this mod's own pack is `Frames`; the shipped suits are `Frames:frame-m15` to `Frames:frame-m18`). It must meet the model contract below, or the definition is rejected before a call can spend a use. |
| `Armor` | The suit's only health. Never regenerates and no item heals it. At 0 the suit is wrecked. |
| `DamageReduction` | 0 to 0.9. Share of every hit the armor stops. |
| `EyeHeight` | Camera height above the feet while worn, in metres. |
| `Movement` | Optional. `Walk` and `Sprint` 1 to 3, `Jump` 1 to 4 (times the game's jump height). Never below the player on foot. Defaults: Walk 1.1, Sprint 1.2, Jump 1.3, SprintSeconds 5, SprintRecovery 8. |
| `Weapon` | `Gear` (required) is an offline gear ID of a special-slot weapon, lent into the special slot under the game gun's own name; the special key pressed while holding that slot swaps it with the pilot's own special weapon. `Magazine` (required) and `Reserve` are the suit's whole ammo; nothing refills it. `DamageMultiplier` applies to this gun only. `RecoilMultiplier` 0.05 to 1 and `ReloadMultiplier` 0.2 to 1 (default 1) scale the recoil and reload time of any gun the pilot holds while piloting, the pilot's own included. |
| `Light` | Optional. The suit's lamp. Defaults: range x2, cone x1.4, intensity x1.2, warm white. `Glow` is the colour of the light strips and the visor while the suit is worn and healthy, default cyan. An empty suit breathes at 12-22% of it (0.3 Hz); armour at or below 25% turns red (one flash as it drops); overheating pulses red-orange at 2 Hz; opening for a pilot shows the colour a quarter of the way to white; power-on after boarding flashes twice; a wreck flickers for 0.5 s and goes dark (Core `FrameGlow`). The build paints the strips and the visor into `emission.png` in grey (strips 0.8, visor up to 1; `tools/glow.py`), so a new model gets them from its own shell. |
| `Boost` | Sirius (assault) special: a charge. |
| `Siege` | Thunderbird (firepower) special: `{ "DeploySeconds": 1.2, "DamageMultiplier": 1.2, "DamageReduction": 0.1, "FireRateMultiplier": 2, "ReloadMultiplier": 0.5, "RecoilMultiplier": 0.5 }`. A definition cannot have both `Boost` and `Siege`, because both use the special key. |
| `Scan` | Nightowl (scout) special: `{ "Radius": 30, "Interval": 5 }`. |
| `Shield` | The barrier: `Capacity`, `RegenDelay`, `RegenRate`, `Radius`, `RaiseFraction`, `WhileSieged`. A shield with `WhileSieged` needs `Siege`. |
| `CarryHeavy` | Taishan (tank) only: the suit can carry heavy objects. |
| `Rockets` | Thunderbird: `Salvo`, `Damage`, `Radius`, `Spread`, `Cooldown`, `Range`, `Speed`, and `Launcher`: `Pod` (the launcher prefab as `Pack:item`, its tubes `muzzle_1`..`muzzle_N`), `Round` (the rocket prefab as `Pack:item`), `Bone` (the one launcher rides this bone, usually a plate that lifts when the suit opens), and `Position` and `Rotation` in the suit's own space at its rest (bind) pose: x right, y up, z forward, metres and degrees; never scaled. |
| `Opening` | Optional. The bones of the model's parts that open for boarding (`Node`, up to 32, unique; the helmet is not listed). How they move is the model's own `open` clip; the game plays it forwards to open and backwards to close. |
| `Shell` | Optional. Maps model bones to the player's first-person body bones for the first-person view of the suit's own arms and legs. A new model changes only this list and `Opening`. |

### Adding a suit from another mod

A third-party mod adds a suit with a ForgeVisual pack, a definition and (optionally) a cockpit mark. The common flow (writing a content pack, the definition file, `Callable`, `Place`) is in [`Mods/ForgeOrdnance/DEVELOPERS.md`](../ForgeOrdnance/DEVELOPERS.md); only what is particular to suits follows. Frames needs no C# interface for this.

1. **Register the pack.** In the plugin's `Load`, call `ForgeVisual.Bundles.Register(modId, "MyPack", path)` right away (not at level build), so admission, which runs at definition load, finds the prefab. `Model` is then `MyPack:walker`. A pack that is missing, or an item it does not hold, rejects the definition with `Model` named.
2. **Model contract.** The prefab holds a skinned renderer and the 16 rig bones named exactly as in `FrameBodyRig.PoseLinks`: `pelvis`, `spine`, `chest`, `head`, `upperarm.L`, `forearm.L`, `hand.L`, `upperarm.R`, `forearm.R`, `hand.R`, `thigh.L`, `shin.L`, `foot.L`, `thigh.R`, `shin.R`, `foot.R` (no neck, shoulder, finger or toe bones; the pilot's own body drives them, so the names are fixed; the `head` node is also where the name plate sits). Every `Opening.Parts[].Node`, `Shell[].Bone` and `Rockets.Launcher.Bone` must be a node of the prefab. With `Opening`, the prefab also carries a legacy `Animation` with the clips `open` and `close`. A missing node, bone or Animation rejects the definition and names the field. The clips cannot be listed on a prefab that was never instantiated, so a missing `open` or `close` clip is found when the suit is first shown and then writes one error line per model (`lacks its 'open' or 'close' clip; it does not visibly open`); the suit still works, it just does not visibly open.
3. **Cockpit mark.** The mark is the ForgeUI icon `mark-<item>` in the icon pack of the same name as the model's pack (`MyPack:mark-walker`). Without it admission writes one error line and the name plate and silhouette show no mark; the suit still works.
4. **Name.** The suit's name in the cockpit is the definition's name (`Name`, or its language key), shown in capitals; `FrameRecord.DefinitionKey` (`OrdnanceDefinition.Key`; every peer finds the definition by it with `OrdnanceApi.TryGetDefinition`, so a client, a late joiner and a reloaded checkpoint name the suit without having seen a call) carries it to every player, so the same model under two definitions keeps two names.
5. **Skills.** Only the six existing ones, tuned through their fields: `Boost`, `Siege`, `Scan`, `Shield`, `CarryHeavy`, `Rockets`. There is no way to add a seventh.
6. **Boarding.** Frames adds no interface: place an empty suit with `OrdnanceApi.Place` (or a `Drops` entry) and a player boards it by the ordinary interaction.

**A suit standing at level start.** Define it for levels only, then place it in the level's `Settings.json`:

```json
[ { "Id": "me.pack.start-suit", "Callable": false, "Payload": { "Type": "frames.Exosuit", "Model": "Frames:frame-m15", "Armor": 100, "DamageReduction": 0.25, "EyeHeight": 2.1, "Weapon": { "Gear": 38, "Magazine": 100, "Reserve": 600 } } } ]
```

```json
{ "Levels": { "Local_32_TierA_1": { "Drops": [ { "Name": "start-suit", "Ordnance": "me.pack.start-suit", "When": "LevelStart", "Arrival": "Direct", "Yaw": 180,
  "Where": { "DimensionIndex": 0, "Layer": 0, "LocalIndex": 0, "Area": 0, "Offset": { "x": 2, "y": 0, "z": 0 } } } ] } } }
```

The same from code, host only: `OrdnanceApi.Place("me.pack.start-suit", spawnPoint, 180f, OrdnanceArrivalMode.Direct, out var why)`.

### Shipped suits

| Model | Armor | Reduction | Eye | Walk / Sprint / Jump | Gun (gear, damage, stock) | Special |
|---|---|---|---|---|---|---|
| Nightowl, scout (`frame-m18`) | 64 | 15% | 2.05 m | x1.3 / x1.35 / x1.8 | 42 anti-materiel rifle, x1.5, 10 + 40 | Scan |
| Sirius, assault (`frame-m15`) | 75 | 25% | 2.1 m | x1.2 / x1.3 / x1.6 | 38 machine gun, x1.8, 100 + 600 | Charge |
| Thunderbird, firepower (`frame-m17`) | 80 | 30% (+10% planted) | 2.15 m | x1.12 / x1.25 / x1.4 | 38 machine gun, x2.0, 150 + 900 | Siege, rockets, planted barrier |
| Taishan, tank (`frame-m16`) | 90 | 40% | 2.3 m | x1.05 / x1.2 / x1.25 | 38 machine gun, x1.5, 80 + 480 | Barrier, heavy carry |

All four: boarding 2.85 s, leaving about 1.65 s (to the landing), cooldown 600 s, 2 uses, call-in delay 8 s. Codes are left, down, right, up, left, down, then down (Sirius), up (Taishan), left (Thunderbird) or right (Nightowl).

## How it works

- **Arrival.** The host creates the suit record and sends it. Every peer drops the model 40 m through the ceiling in 2.0 s. After a 1.5 s landing settle the suit is idle and can be boarded.
- **Replication.** The host sends the whole suit state through `OrdnanceApi.SendState` after every change (armor at most every 0.2 s) and republishes it every 2 s. Late joiners build the suit from that state alone. Board, leave, ammo and special requests go to the host. The pilot's hits reach other peers only as a flash and tracer; damage is the game's own bullet hit.
- **Boarding.** The player must stand behind an idle suit within 0.9 m, facing it, and hold Use for 1 s. The host checks the request. The suit never moves and the pilot has no boarding motion. The whole boarding is 2.85 s from the host's state start (`FrameSequence.EnterSeconds`, the same for every model; definitions do not set it), and every peer plays it from there (`Core/FrameSequence.cs`): the plates open by 1.9 s (about 1.4 times the clip's own pace) while the player stands still and the local view fades to black over 1.6-1.9 s; at 1.9 s (`FrameSequence.Black`) the pilot is put on the seat under the black, the pilot's body is hidden on every peer and the power-on starts; the plates close from the black (the 1.9 s close clip runs about twice its own pace to latch by the end); the controls open at 2.85 s, the moment the power-on's last block starts to come on (`BootPlan.ControlsAt`, 0.95 s of the power-on: blocks from 0.20 / 0.36 / 0.56 / 0.74 / 0.95 s, each settling over 0.40 s), and the last two blocks settle and the seams hold 0.12 s and fade 0.16 s while the pilot already walks, the picture whole at 3.53 s (`BootPlan.Assembled`, 1.63 s of the power-on). The pilot's own screen decides this from the state clock alone (`FrameSequence.ControlsOpen`; the power-on clock is the state clock from the black, so the blocks and the controls cannot drift apart): it gives the movement keys back on that frame without waiting for the host's Piloted message, and NEURAL LINK OK (locks at 4.25 s), the self-test and the ready voice play on while the pilot walks. The host's state still flips to Piloted at the same state-clock moment; nothing in the authority or the messages changed. Everyone else sees the shell open, the pilot vanish at the black and the shell close. Leaving lasts to the landing (`FrameSequence.ExitSeconds`, about 1.65 s): the panels go black block by block under a red SHUTDOWN, the pilot pulls out once every block is black (0.78 s), the black clears once the head is out of the shell (`PilotMotion.HeadOut`, 0.8 s into the leaving motion), the movement keys come back on the landing frame and the empty suit closes on its own afterwards (`FrameSequence.ClosePlates`), holding nobody. The first-person shell of a model (about 230 ms to build) is built while the elevator goes down, one per frame, for each model the players picked in the lobby (`OrdnanceApi.PickedParameters`), none when nobody carries a suit; a model new to the level (a late joiner's pick, a first call-in) is built when its beacon lands and the countdown starts (`IOrdnancePayload.OnBeaconLanded`), never on the landing frame; only a suit already standing when this player joined is built as boarding starts.
- **Leaving.** The visor powers down to black under a red SHUTDOWN; under the black the pilot pulls out of the open suit (the leaving motion), the black clears as the head leaves the shell, and at the landing the pilot is put on the first exit spot behind or beside the suit that passes ForgeCore's `Level.FindStandSpot` navigation, ground and wall checks (`FrameSequence.ExitClearance` sets the distance) and moves from that frame on; the suit closes in front of the pilot and stays standing.
- **Worn.** The suit follows the pilot's smoothed position. The pilot's own body is hidden. The pilot's hit boxes are scaled x1.3. The suit has no stamina; only sprint heat limits sprinting.
- **Damage.** Every hit goes to the armor. The armor reduces part of each hit and never regenerates.
- **Siege.** V starts deployment and locks movement. Bonuses wait for `DeploySeconds`. Damage and fire rate apply only to the suit's gun; the reload and recoil factors apply to any gun the pilot holds, on top of the model's handling.
- **Rockets.** The pilot sends the aimed point. The host checks pilot, cooldown and range, picks a seed, and sends the salvo to every peer. Only the host applies the explosion damage. Rocket `i` leaves the launcher's tube `i % tubes`, 0.12 s after the one before, with the sentry gun's muzzle flash at the tube and a back-blast; its tube stays empty until it is reloaded, the salvo's rockets in firing order evenly over the cooldown (`Core/FrameRocketRack.cs`, from the salvo's launch time on each peer; nothing is sent per tube). A suit whose art is missing still fires: its rockets leave from 2.2 m above its feet.
- **Checkpoints and host changes.** Checkpoint transfers are revisioned and retried. A new host takes over from the replicated state.
- **The special weapon slot.** The suit gun is lent into the slot through ForgeCore's `Gear.SpecialSlot` (`Core/SuitGunLoan.cs`, `Native/MechGun.cs`), the same lease a heavy weapon from Arsenal takes, so the two stack and each gets its gun back.
- **Input.** While boarding or leaving, the movement keys are held from the game through a ForgeCore `InputLease` (released at level end, death and leaving too).
- **Per-frame work.** `FrameWorld.Tick` and `LateTick` are registered on ForgeCore's `Frame.OnUpdate` / `OnLateUpdate`; the package has no driver component. A pilot is found by network lookup with `Level.PlayerByLookup` (the host caches it by list position and checks it with `Level.IsPlayer`); the rocket aim ray is `Combat.ShooterRay`. `FrameCarry` rescans the carried object's renderers twice a second, not every frame.

## Art

`forge-frames.bundle` holds four prefabs, `frame-m15` to `frame-m18`, each with the opening clips `open` and `close` (no other clips: a worn suit follows its pilot's body), one skinned mesh in two sub-meshes (the armour on a 2048 texture set, the parts' lining on 512 tiling textures), and `pilot-motion`: a renderer-less skeleton with the legacy clips `Rest` and `PilotExit` (root path baked in) and an `eye` point under its head. It also holds the parts in `tools/props.json`: `frame-rocket-pod` (the launcher, about 0.21 x 0.26 x 0.38 m, origin at the middle of its underside, front +Z, four tubes `muzzle_1`..`muzzle_4` at the tube mouths facing +Z, 1024 textures) and `frame-rocket` (the rocket, about 0.26 m long, origin at its middle, nose +Z, 512 textures), each one mesh renderer shaded with the gear shader. `tools/build_prop.py` stages each from its finished low-poly GLB (`-Props`, default `artifacts/work/frames-rockets/lowpoly`): turned and uniformly scaled as `props.json` says, the sockets added. `tools/preview_rockets.py` renders a suit's launchers as `frames.json` mounts them, shut and open, and reports any vertex of them inside the moving shell over the open clip. Sources and licenses are in `Assets/Models/License.txt`.

- `tools/make_cut_file.py` runs once per model in Blender and writes `<model>_cut.blend` (default folder `artifacts/work/frame-cut`). It scales the model, maps the source skeleton onto a 17-bone suit rig and bakes one texture set. The file holds the whole shut shell, unsplit, in the collection `固定`. It never overwrites a cut file without `--replace`. M16 (Wrath) has its own maker beside its local source model, `artifacts/work/ordnance-assets/models/M16/source/make_wrath_cut.py`, which writes the same layout.
- The owner draws the cut lines on the shell with Blender's Annotate tool, one layer per kind of line, and saves the cut file.
- `tools/hinge_tool` is the Blender 5.2 add-on 战甲开合 (load it with `open.ps1 <cut file>`). Its click tool cuts a piece between two lines on the mesh's own edges (a drawn annotation, a seam, the plate's edge or a straight run) and sets its hinge, angle and order; `api.py` does the same from scripts, by piece number. Everything is stored as custom properties (`model.py`). Its checks (hinge stays attached ≤ 5 mm, lifts off its surface, no body penetration, back entry cover, arc per limb) only report; `api.review` writes the GIFs, numbered overview and table. A piece's lining is the faces with the material `frame-lining` (a procedural dark grid, one UV unit a 4 cm cell). `hinge_tool/bake.py` writes the file the build reads from the owner's cut file (one collection per hinge, each part standing shut with its move in `fh_motion`); `hinge_tool/build.ps1 <model> [-File]` bakes `<model>_cut.draft.blend` into `<Cut>/build/<model>` and builds it with `preview_cut.ps1`. The motion (`motion.py`) is shared with `build_frame.py`.
- `tools/build_frame.py` builds from a baked file alone: the mesh objects in `固定` stay, every other collection holding meshes opens as one rigid part along the move `bake.py` gave it, and every object's shut place is where it stands. It thickens the shell (a dark matte inner wall; a lined object's plate and lining each grow at most half way toward the other, so the lining is the inside one sees), bakes the `frame-lining` material into three 512 tiling textures and keeps the lining as the mesh's second material, gives each part its own bone, keys the clips `open` and `close` (the parts' bones only), and fails the build on a shell that is not watertight, a second layer left over an opening part, or a small piece moving with a part it does not touch. The pilot-slot rays out of the back, parts passing through the body or each other on their path, and the clearance between layers are reported only. It writes `<model>_entry.json` with those checks and the slot rays.
- `tools/build_assets.ps1` builds the bundle from the four finished cut files `artifacts/work/frame-cut/<model>_cut.draft.blend` (bake, then `build_frame.py`, then Unity); it only reads them.
- `tools/preview_cut.ps1 <model> -File <baked file>` builds one baked file without stopping on failed checks, renders the opening with `tools/preview_opening.py` (the `open` clip, plus a frame-by-frame sheet) and prints the checks in Chinese.
- `tools/FramesBundleBuild.cs` runs in Unity batch mode. It cuts the imported `open` take down to the `Opening` bones, checks it (shut at its first frame, scale 1 throughout, no part ends nearer the nearest body axis (pelvis, spine, chest, limbs) than a tuck unless the owner allowed its hinge into the body in the add-on, the same slot rays clear on Unity's mesh), checks the lining sub-mesh against the build's triangle count, gives it the `<prefab>/lining` surface and writes the bundle. `FrameVisual` samples that clip; it computes no motion of its own.
- `tools/build_icons.ps1` renders one 256 x 256 icon per suit with the same camera into `Assets/icons/` (the package's icon source, local media), skipping files already there (`-Redo <file>` or `-Redo all` re-renders on purpose).
- `tools/import_hud_art.ps1` copies the owner's finished cockpit art (`A2-*` skill icons, `A6-*` model marks, `A1-前/后/左/右` silhouette layers, `A5-*` shield, heat, objective and teammate marks and `A7-绕背箭头`, each `name.png` and `name@2x.png`, white on transparency) into the local media folder `Assets/hud/` as `skill-*`, `mark-<model>`, `damage-*`, `shield`, `heat`, `objective`, `teammate` and `arrow-around`. Git does not track it. The plugin registers whatever `hud/` holds with ForgeUI as the `Frames:` icon pack (`Native/FrameHudArt.cs`; the @2x file above 1440 lines). `tools/import_visor_cracks.py` (needs Pillow) turns the owner's crack drawing (`U3-裂纹.webp`, brightness to alpha) into the single file `Assets/hud/visor-cracks.png`, which the visor reads through `Visor.CracksPicture`; packaging refuses to build without it. Nothing is drawn in Frames: a missing file is logged, a skill slot shows its name, a gauge or model shows no mark, and ForgeUI leaves out (and logs) a missing silhouette layer. Packaging refuses to build until every file, 1x and @2x, is in `Assets/hud/`.

## Sound

`tools/prepare_audio.py` checks every source file against the hash in `sources.json`, cuts each section exactly as recorded, and calls ForgeAudio's converter to write Ogg Vorbis. `Assets/audio/cues.json` follows the ForgeAudio format. The library is loaded as `NAinfini.ForgeOrdnanceFrames`. Sounds only the pilot hears play in 2D. World sounds follow replicated state, so no extra network message is needed. Boarding and leaving sounds run from the same state clock as the plates on every peer.

## Network

Frames' own messages go through its ForgeCore channel (`NAinfini.ForgeOrdnanceFrames`), whose format fingerprint (messages' names, delivery and layouts) makes ForgeCore refuse a player with other Frames messages explicitly, so such a player cannot call. Requests, answers and checkpoints are `Reliable` (never dropped by the queue); shots (`Fire`) and hit directions (`Hit`) are `Lossy` (the game's non-critical channel, queue of 256). The host sends each hit on a suit to its pilot (`Hit`: attacker position, damage, barrier or armour) for the cockpit's hit direction; the outcome itself travels in the suit state.

## Cockpit

While a suit is worn, ForgeUI hides the game's HUD except world markers and chat (docked at the bottom right under the inventory, on the corner's safe edge, in the cockpit's look: no game backings, the HUD face at `ChatTextPx` 18 and `ChatInputPx` 19, lines in `Primary`, alerts in `Warning`, a `Primary` cursor bar, players' names in their own colours; typing in it lights a plate behind it as tall as the lines shown and the cockpit stays on screen; not typing, only the newest 3 lines show at 0.85, on a faint plate with a thin bar, for 10 s after the newest line and then fade (`IdleLines`, `IdleOpacity`, `IdleSeconds`); the game's help line is not drawn, lines are cut to the dock with an ellipsis, and the game's version text at the bottom right is hidden with the HUD; `ChatWidth` 470, `ChatLines` 6, `ChatPriority` 20 and the accent `Primary` in `Native/FrameHud.cs`). The cockpit is ForgeUI templates fed from `Core/FrameHudModel.cs` (`Native/FrameHud.cs`). One palette for every part: ice (`Primary`, ForgeUI's `Cockpit`) for everything normal, orange (`Warning`) only for emphasis and what is ready to use (highlighted objective words, a call-in that can be called), red (`Danger`) only for danger, cyan only for the barrier, and text on ForgeUI's dark `Plate`:

- top centre: the zone, heading and altitude in one row, and right under it the barrier, armour and pilot-health bars: the barrier is a cyan layer `BarrierHeight` 5 on the armour bar's top edge with a shield mark and its points left of the bar (`OverlayReadoutMax` = the spec's capacity), flashing on a hit, brighter on a break, red 0 and a red track filling back while broken; a planted-only barrier (Thunderbird) is drawn only in siege mode (`FrameHudModel.BarrierShown`, the layer's place kept so the bar does not move);
- around the crosshair: the magazine arc (the rounds in the hand's magazine, ∞ without one; empty stays drawn at 0) and the heat arc, the crosshair dot (hidden while aiming down sights),  the rocket landing mark and the threat bearing arc (enemies out of view only; the game's compass points are not drawn on it, the pings stay in the world as the game's own markers);
- top left: objectives, a 330-wide column (`Width`, three objective lines) on a 46 % dark plate chamfered 10 with a 42 % ice accent bar; tags and header 12, objective 15, step 12.5 and fainter; only tags bold;
- under the crosshair: the interaction prompt with the scan line under it (190 under the crosshair, clear of the arcs and the magazine number; above the crosshair is the banner's place) and the subtitles;
- bottom right: the gun's name and reserve;
- bottom left: skills, lamp, exit and teammates (290 wide, rows 36 on a 42 step, 10-cell slanted health, a 50 % dark plate with a 22 % ice chamfered frame, a pilot's armour as a 60 % ice line, 16 units more space above LAMP / EXIT);
- also: hit glow at the edges, a faint visor, the enemy outline and enemy health bars, a banner while the pilot is grabbed or stunned, and world marks over idle and worn suits.

The suit draws nothing itself: ForgeUI's power-on player plays the suit's ten panels and rhythm (`Core/FrameBootScreen.cs`; the tint and the three glitch channel colours, magenta-red, green and blue, in `FrameHud.RegisterScreens`), and ForgeUI's power-on message plays the suit's beats, colours and words (`FrameBootMessage`); the barrier has no picture of its own (only its line on the armour bar). `Core/CockpitScope.cs` decides when the game's HUD is hidden and given back, read every frame from the pilot's state now (boarding, inside the shell, worn, leaving, dark, out; waiting while ForgeUI cannot hold the HUD, a dead pilot or no level), so a missed record, a release by ForgeUI or a failed cockpit update is corrected on the next frame instead of lasting the ride. `Core/FrameBootScreen.cs` (`BootPlan`) times the power-on from the black: after a short black the screen panels come into view block by block with a Titanfall-style red-green-blue channel-split glitch; the pilot moves as the last block starts to come on (0.95 s, 2.85 s into the boarding) and the picture is whole at 1.63 s (3.53 s); the cockpit parts come online in the plan's order through ForgeUI's `HudWidget.Reveal()` as their block lights; the centre group above the crosshair (header decode, bar, self-test under it on a dark plate) runs from 0.85 s, and NEURAL LINK OK decodes and locks with the ready voice at 2.35 s and folds away while the pilot already walks. Leaving, the same blocks go black in reverse while ForgeUI's power-on message shows the suit's red SHUTDOWN (`boot.shutdown`) in the middle until the last block is black. All words are keys in `lang/` (`Core/FrameWords.cs`); `tests/Core/LangChecks.cs` keeps both languages complete.

## Drawn ahead of first use

The worn shell holds the game's torso, legs and head `forceRenderingOff` (giving back the value each had), never a transparent material, which would still skin and draw them. ForgeVisual draws it once off screen right after it is built ahead of boarding (`Models.WearFirstPerson`).

## Logging

Logging goes through ForgeCore's `ModLog` and only errors are written, through `Plugin.Error(key, message)` with a fixed short key per call site (ForgeCore reports a key once and counts repeats); there are no info, trace, timing or debug-setting outputs.
