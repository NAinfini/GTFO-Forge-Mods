# Forge Performance

Cuts visual work in GTFO to raise the frame rate. Every cut has its own switch, a cut that is off installs nothing of its own, and with every cut off the plugin does nothing.

Report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues), or find me on the GTFO Modding Discord as **nainfini**.

## What it cuts

The defaults aim at the highest frame rate and accept some visual loss.

| Group | What changes | Main switches |
| --- | --- | --- |
| Corpses and bodies | Dead enemies cast no shadow, disappear sooner and stop blocking shots once hidden; old ragdolls stop moving; broken debris settles. | `CorpseShadows`, `FastCorpseHide`, `CorpseLifetime`, `RagdollCap`, `RagdollLimit`, `CorpseRecycle`, `CorpseRecycleSeconds`, `DecayCleanup`, `DebrisFreeze` |
| Air, wind and plants | Ambient dust is off or thinner; only nearby wind effects move plants. | `AmbientDustOff`, `AmbientParticles`, `ParticleFactor`, `ParticleHz`, `WindNearest`, `WindLimit`, `WindCapacityProtection`, `GardenShadows` |
| Decoration | Far or off-screen decoration, its shadows, lights and animation are reduced. | `DecorationShadows`, `DecorationCulling`, `CullDistance`, `DecorationLights`, `LightLimit`, `DecorationAnimation`, `IrfWorldCameraOnly`, `IrfOffscreenSkip` |
| Effects and screen | Shorter or fewer decals, no soft-particle edges, no visor liquids, fewer shell casings, no interface mirror. | `CombatDecals`, `DecalLimit`, `DecalLifetime`, `SoftParticlesOff`, `VisorLiquidOff`, `ShellEjection`, `ShellLimit`, `GuiMirrorOff`, `LowQualitySpitter` |
| Distance and cleanup | Shorter shadow distance, texture streaming off where it is unused, and what the game forgets is cleaned up at the end of a level. | `ShadowDistance`, `ShadowDistanceFactor`, `TextureStreamingOff`, `LeakCleanup`, `GlueSoundRecycle`, `StackTraceTrim` |
| Doors | Behind a closed door, draw only the first rooms instead of every room the door leads on to. | `ClosedDoorDepthLimit`, `ClosedDoorDepth` |
| Multiplayer | One option shares a single melee hit message for all of your melee weapons and keeps the message numbers the same as the unmodded game. | `MeleePacketReuse` (off) |

Three defaults change play. Set them to `false` for the vanilla behaviour:

- `FastCorpseHide`: a hidden corpse also stops blocking bullets. The enemy's death and the game's own despawn are unchanged.
- `CorpseRecycle`: the game's own corpse timer is shortened, so a body is despawned sooner and stops blocking bullets sooner.
- `ClosedDoorDepthLimit`: behind a closed door only the first `ClosedDoorDepth` rooms are drawn (2 by default). Through a door gap or window, a farther room may be empty, including enemies you could otherwise spot. Rooms you can see another way, and damaged or destroyed doors, are drawn as usual.

`FastCorpseHide`, `CorpseRecycle` and `ClosedDoorDepthLimit` run on each machine. Players with different settings can see a body or a far room differently. `MeleePacketReuse` is off by default; leave it off until it has been tested in multiplayer.

`RagdollNodeRefresh` (on) fixes a vanilla bug where a corpse thrown across a room boundary can disappear.

## Settings

All options are in `BepInEx/config/NAinfini.ForgePerformance.cfg`, section `[Performance]`. The default is on unless the table above says otherwise. Numbers such as `CorpseLifetime` and `CullDistance` sit beside their switch. Values are read when the plugin loads, so restart the game after editing.

`SummaryLog` is off by default. Turned on, it writes one line per setting at the start of a session and one count line per cut when a level ends. The log otherwise holds errors only.

## Installation

Install with r2modman or the Thunderstore Mod Manager. It also installs BepInExPack_GTFO and GTFO_API. No Forge package is needed.

## Limits

- This mod changes what is drawn and cleaned up on your machine. It does not change damage, hit detection, enemy count or spawning, detection, objectives, level layout or player controls. The three defaults above are the exceptions.
- The frame rate gain has not been measured in game; the offline checks cover the settings, the log and the game methods used, not a frame-rate comparison.
- It does not measure frame time or memory; with `SummaryLog` on it writes the settings and per-cut counts only.

## For developers

The build, the offline check, the game methods each cut uses and the log format are in the [developer guide](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/DEVELOPERS.md).

## License

[MIT](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgePerformance/LICENSE).
