# GTFO Forge Mods

Source code and release files for NAinfini's GTFO mods, published on Thunderstore.

| Mod | Version | Description |
|---|---|---|
| [Forge Weapon Energy Lab](ForgeWeapon_EnergyLab/README.md) | 0.0.4 beta | Eight standalone energy guns: beams, chain lightning, charged plasma, explosions, ricochets, flame and gravity control. |

## Layout

- `ForgeWeapon_EnergyLab/`: the Energy Lab plugin (C#, BepInEx 6 IL2CPP), its embedded effect, model, icon and audio assets, and its build tools. [DEVELOPERS.md](ForgeWeapon_EnergyLab/DEVELOPERS.md) explains how it works, how to build it and the extension API.
- `Tools/Vfx/ForgeVfx/`: the Unity 2019.4.21f1 project that generates every effect texture, material and prefab from code and builds the effect bundle.

Report problems in this repository's Issues. Each mod's licence is in its own folder; the effect bundle and its Unity project are CC0.
