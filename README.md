# GTFO Forge Mods

Source code and release notes for NAinfini's GTFO mods, published on Thunderstore.

| Package | Version | What it is | Thunderstore |
|---|---|---|---|
| [ForgeCore](ForgeCore/README.md) | 1.0.0 | The one shared library: asset bundles, logging, settings, pools, networking, input leases and local saves, plus its interface (lobby buttons, pickers, menu pages, HUD), audio (Ogg Vorbis cues, positioned playback) and world-visuals (effects, markers, model loading) parts. | [NAinfini/ForgeCore](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeCore/) |
| [ForgeOrdnance](ForgeOrdnance/README.md) | 1.0.1 | Call-in system: enter a directional code and throw a beacon. Includes the Arsenal (supply pods, turrets, strikes, heavy weapons) and four powered exosuits. | [NAinfini/ForgeOrdnance](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeOrdnance/) |
| [ForgeWeapon_EnergyLab](ForgeWeapon_EnergyLab/README.md) | 1.0.1 | Eight standalone energy guns: beams, chain lightning, charged plasma, explosions, ricochets, flame and gravity control. | [NAinfini/ForgeWeapon_EnergyLab](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeWeapon_EnergyLab/) |
| [ForgeReplay](ForgeReplay/README.md) | 1.0.0 | Records your runs and plays them back in game: free and first-person cameras, team stats and member ratings. | [NAinfini/ForgeReplay](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeReplay/) |
| [ForgeEmote](ForgeEmote/README.md) | 1.0.0 | An action wheel: dances, gestures, callouts, handshakes and fist bumps. | [NAinfini/ForgeEmote](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeEmote/) |
| [ForgePerformance](ForgePerformance/README.md) | 1.0.0 | Raises the frame rate without changing gameplay. | [NAinfini/ForgePerformance](https://thunderstore.io/c/gtfo/p/NAinfini/ForgePerformance/) |
| [ForgeSkin](ForgeSkin/README.md) | 1.0.3 | Applies ForgeSkin packs (textures, attachments, live ammo screens, animated layers) to your own weapons; includes the desktop skin editor. | [NAinfini/ForgeSkin](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeSkin/) |

## Layout

- One folder per package: the plugin source (C#, BepInEx 6 IL2CPP), tests, build tools, README, CHANGELOG and, in each folder's DEVELOPERS.md, how to build against and extend it. Sibling packages reference each other as ../<Package>, so keep the folders side by side.
- Build/ and Directory.Build.props: the shared MSBuild settings (GTFO-API and BepInEx references, warnings as errors) and the release-check scripts.
- Testing/: a test double used by the ForgeCore tests.
- Tools/Vfx/ForgeVfx/: the Unity 2019.4.21f1 project that generates Forge Visual's own effect textures, materials and prefabs from code and builds the effect bundle.
- Large binary assets (model and audio bundles, Unity sources) are not stored here; the Thunderstore packages contain the finished files.

Report problems in this repository's Issues, or find me on the GTFO Modding Discord as **nainfini**. Each package's licence is in its own folder; the effect bundle and its Unity project are CC0.
