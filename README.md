# GTFO Forge Mods

Source code and release notes for NAinfini's GTFO mods, published on Thunderstore.

| Package | Version | What it is | Thunderstore |
|---|---|---|---|
| [ForgeCore](ForgeCore/README.md) | 0.1.0 | Foundation library: asset bundles, logging, settings, pools, networking, input leases, local saves and calls into the game's own systems. | [NAinfini/ForgeCore](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeCore/) |
| [ForgeUI](ForgeUI/README.md) | 0.1.0 | Shared interface library: lobby buttons, pickers, menu pages and HUD elements in the game's own style. | [NAinfini/ForgeUI](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeUI/) |
| [ForgeAudio](ForgeAudio/README.md) | 0.1.0 | Audio library: Ogg Vorbis cues, positioned playback, voice budgets and occlusion. | [NAinfini/ForgeAudio](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeAudio/) |
| [ForgeVisual](ForgeVisual/README.md) | 0.1.0 | World-visuals library: named effects, particle pools, ground markers, glow materials and model loading. | [NAinfini/ForgeVisual](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeVisual/) |
| [ForgeOrdnance](ForgeOrdnance/README.md) | 1.0.0 | Call-in system: enter a directional code, throw a beacon and receive supplies or turrets. | [NAinfini/ForgeOrdnance](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeOrdnance/) |
| [ForgeOrdnance_Arsenal](ForgeOrdnance_Arsenal/README.md) | 1.0.0 | Supply pods, sentry turrets, strikes and drop-pod heavy weapons for ForgeOrdnance. | [NAinfini/ForgeOrdnance_Arsenal](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeOrdnance_Arsenal/) |
| [ForgeOrdnance_Frames](ForgeOrdnance_Frames/README.md) | 1.0.0 | Four powered exosuits you call in and climb into. | [NAinfini/ForgeOrdnance_Frames](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeOrdnance_Frames/) |
| [ForgeWeapon_EnergyLab](ForgeWeapon_EnergyLab/README.md) | 1.0.0 | Eight standalone energy guns: beams, chain lightning, charged plasma, explosions, ricochets, flame and gravity control. | [NAinfini/ForgeWeapon_EnergyLab](https://thunderstore.io/c/gtfo/p/NAinfini/ForgeWeapon_EnergyLab/) |

## Layout

- One folder per package: the plugin source (C#, BepInEx 6 IL2CPP), tests, build tools, README, CHANGELOG and, in each folder's DEVELOPERS.md, how to build against and extend it. Sibling packages reference each other as ../<Package>, so keep the folders side by side.
- Build/ and Directory.Build.props: the shared MSBuild settings (GTFO-API and BepInEx references, warnings as errors) and the release-check scripts.
- Testing/: a test double used by the ForgeCore tests.
- Tools/Vfx/ForgeVfx/: the Unity 2019.4.21f1 project that generates the Energy Lab effect textures, materials and prefabs from code and builds the effect bundle.
- Large binary assets (model and audio bundles, Unity sources) are not stored here; the Thunderstore packages contain the finished files.

Report problems in this repository's Issues, or find me on the GTFO Modding Discord as **nainfini**. Each package's licence is in its own folder; the effect bundle and its Unity project are CC0.
