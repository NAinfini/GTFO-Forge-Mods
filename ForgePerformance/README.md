# Forge Performance

Cuts visual work in GTFO to raise the frame rate. Every cut has its own switch, a cut that is off installs nothing of its own, and with every cut off the plugin does nothing.

Report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues), or find me on the GTFO Modding Discord as **nainfini**.

## What it cuts

The defaults aim at the highest frame rate and accept some visual loss.

| Group | What changes | Main switches |
| --- | --- | --- |
| Corpses and bodies | Dead enemies cast no shadow, disappear sooner and stop blocking shots once hidden; old ragdolls stop moving; broken debris settles. | `CorpseShadows`, `FastCorpseHide`, `CorpseLifetime`, `RagdollCap`, `RagdollLimit`, `CorpseRecycle`, `CorpseRecycleSeconds`, `DecayCleanup`, `DebrisFreeze` |
| Air, wind and plants | Ambient dust is off or thinner; only nearby wind effects move plants; plant sway is computed every other frame. | `AmbientDustOff`, `AmbientParticles`, `ParticleFactor`, `ParticleHz`, `WindNearest`, `WindLimit`, `WindCapacityProtection`, `GardenShadows`, `IrfPhysicsHalfRate` |
| Decoration | Far or off-screen decoration, its shadows, lights and animation are reduced. | `DecorationShadows`, `DecorationCulling`, `CullDistance`, `DecorationLights`, `LightLimit`, `DecorationAnimation`, `IrfWorldCameraOnly`, `IrfOffscreenSkip` |
| Effects and screen | Shorter or fewer decals, no soft-particle edges, no visor liquids, fewer shell casings, magazines dropped on reload disappear after 3 seconds, used fog repellers disappear from the floor, no interface mirror. | `CombatDecals`, `DecalLimit`, `DecalLifetime`, `SoftParticlesOff`, `VisorLiquidOff`, `ShellEjection`, `ShellLimit`, `DroppedMagazineCleanup`, `DroppedMagazineSeconds`, `SpentFogRepellerCleanup`, `GuiMirrorOff`, `LowQualitySpitter` |
| Distance and cleanup | Shorter shadow distance, texture streaming off where it is unused, what the game forgets is cleaned up at the end of a level, and a repeated game error is written once when it first happens and once with its count when the level ends. | `ShadowDistance`, `ShadowDistanceFactor`, `TextureStreamingOff`, `LeakCleanup`, `GlueSoundRecycle`, `StackTraceTrim`, `TentacleLimitErrorFold` |
| Doors | Behind a closed door, draw only the first rooms instead of every room the door leads on to. | `ClosedDoorDepthLimit`, `ClosedDoorDepth` |
| Multiplayer | One option shares a single melee hit message for all of your melee weapons and keeps the message numbers the same as the unmodded game. | `MeleePacketReuse` (off) |

Five defaults change play. Set them to `false` for the vanilla behaviour:

- `FastCorpseHide`: a hidden corpse also stops blocking bullets. The enemy's death and the game's own despawn are unchanged.
- `CorpseRecycle`: the game's own corpse timer is shortened, so a body is despawned sooner and stops blocking bullets sooner.
- `FarAnimatorThrottle`: walking enemies farther than `FarAnimatorDistance` are animated every `FarAnimatorInterval` frames, so they move choppily and their hit boxes lag behind their body for up to that many frames.
- `PathfindingIterations`: the host searches enemy paths with a smaller budget per frame (`PathfindingIterationsPerFrame`), so after you move away enemies can start following a little later.
- `ClosedDoorDepthLimit`: behind a closed door only the first `ClosedDoorDepth` rooms are drawn (2 by default). Through a door gap or window, a farther room may be empty, including enemies you could otherwise spot. Rooms you can see another way, and damaged or destroyed doors, are drawn as usual.

`FastCorpseHide`, `CorpseRecycle`, `ClosedDoorDepthLimit`, `DroppedMagazineCleanup` and `SpentFogRepellerCleanup` run on each machine. Players with different settings can see a body or a far room differently. `MeleePacketReuse` is off by default; leave it off until it has been tested in multiplayer.

`DroppedMagazineCleanup` (on) is a visible change: a magazine that falls from a gun on reload disappears `DroppedMagazineSeconds` (3 by default) after it drops, also for magazines other players drop. Reloading, ammunition and sound are unchanged. Set it to `false` and the game keeps up to five dropped magazines per gun on the floor as usual.

`SpentFogRepellerCleanup` (on) is a visible change: a thrown fog repeller that has stopped repelling fog disappears from the floor on your screen as soon as its effect ends. It is never hidden while it still repels fog, and the large carried fog repeller is untouched. Only the drawing on your machine changes: the item stays in the game with its collision, nothing is sent, and other players see it as their own setting says. Used glow sticks already disappear in the game when their light goes out, so there is no switch for them.

`RagdollNodeRefresh` (on) fixes a vanilla bug where a corpse thrown across a room boundary can disappear.

## Enemy crowds

Options for many living enemies. Each has its own switch; "who" says on which machine it acts.

| Switch | Default | Who | Effect on look or play |
| --- | --- | --- | --- |
| `SetupRendererLookup` | on | each machine | None. Removes this mod's own second walk over a new enemy's renderers. |
| `SpawnFrameDefer` | on | each machine | None. On a frame where an enemy was just spawned, this mod's own periodic work waits one frame (never two in a row). |
| `FarSkinQuality`, `FarSkinDistance` (20 m) | on | each machine | Looks: enemies farther than the distance bend their mesh from two bones instead of four and write no motion vectors; joints of far enemies look coarser and can smear slightly with TAA. |
| `FarAnimatorThrottle`, `FarAnimatorDistance` (25 m), `FarAnimatorInterval` (3) | on | each machine | Play: walking enemies farther than the distance are animated every few frames, so they move choppily and their hit boxes lag up to two frames. Enemies doing anything but walking are never throttled. |
| `ClientHiddenSyncThrottle`, `HiddenSyncDistance` (30 m), `HiddenSyncInterval` (4) | off | clients only | Play: out-of-sight enemies farther than the distance take their position from the host every few frames, so their colliders lag behind the host's for shots or explosions through walls. The host is never affected. |
| `UpdateBudgets` and the six `Budget…` values | off | host only | Play when the values are changed: enemies think, detect and move on a different schedule. The values given are the game's own, so turning it on without changing them changes nothing. |
| `AvoidanceQuality`, `AvoidanceType` (1) | off | host only | Play when the value is changed: the game uses low-quality crowd avoidance (1); 0 turns avoidance off, so enemies pass through each other and bunch differently in doorways. Enemies already walking switch to the new value at once. |
| `PathfindingIterations`, `PathfindingIterationsPerFrame` (50) | on | host only | Play: path searches spread over more frames, so enemies can start moving later. The game uses 100. |
| `HitReactKeepsAgent` | off | host only | Play: a staggered enemy's navigation is stopped instead of switched off and on, so it can end a stagger in a slightly different place. |

With a switch off the game's own value and code are used unchanged.

## Settings

All options are in `BepInEx/config/NAinfini.ForgePerformance.cfg`, section `[Performance]`. The default is on unless the table above says otherwise. Numbers such as `CorpseLifetime` and `CullDistance` sit beside their switch. Values are read when the plugin loads, so restart the game after editing.

`SummaryLog` is off by default. Turned on, it writes one line per setting at the start of a session and one count line per cut when a level ends. The log otherwise holds errors only.

## Installation

Install with r2modman or the Thunderstore Mod Manager. It also installs BepInExPack_GTFO and GTFO_API. No Forge package is needed.

## Limits

- This mod changes what is drawn and cleaned up on your machine. It does not change damage, hit detection, enemy count or spawning, detection, objectives, level layout or player controls. The five defaults above are the exceptions.
- No frame-rate gain has been measured yet. With the whole Forge mod set installed, the cuts were switched off and on in six alternating sessions (three each) on one machine (Ryzen 9 9900X, RTX 4080, 3840x2160), standing still in one room of R8A1 with about 60 sleeping enemies: 86, 81 and 103 fps with every cut off, 89, 78 and 98 fps with them on. The spread between sessions is larger than the difference, so this shows no gain; fights were not compared, and the mod has not been compared with the unmodded game. Measured on the build described under Unreleased in the changelog.
- With `Probe` on (off by default, for developers) it writes frame time, main and render thread time and enemy work once a second to `BepInEx/PerformanceLogs`; otherwise it measures nothing.

## For developers

The build, the offline check, the game methods each cut uses and the log format are in the [developer guide](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/DEVELOPERS.md).

## License

[MIT](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/LICENSE).
