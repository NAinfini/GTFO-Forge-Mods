# Changelog

## 1.0.0

- Weapons are defined in `ForgeEnergyLab/*.json` data files (any mod folder named `ForgeEnergyLab`); the eight guns above are the first file, and others add guns the same way without code. A loadout that used an earlier build's guns must be chosen again.
- Eight energy guns: Beam, Chain Arc, Flamethrower, Charge Orb, Shock Orb, Blast Gun, Ricochet Disc and Gravity Core.
- Beam and Flamethrower have no recoil; the other six have light recoil.
- Back hits, weakspots and armor work the same as with normal guns.
- Bots can use all eight guns, with their effects and sounds.
- The Blast Gun fires a visible shell that flies at 40 m/s and explodes at the first terrain, enemy or player it touches, so a target that steps into its path is hit and one that moves away is missed.
- The Blast Gun and Beam have no range limit; the Chain Arc (16 m) and Flamethrower (8 m) keep theirs.
- The Blast Gun's shot and explosion use the Ordnance grenade launcher's sounds.
- Gun icons are drawn by the game's own icon renderer from each gun's model, with the same shading and size as vanilla gun icons.
- Sounds play through ForgeAudio and effects through ForgeVisual, mixed with the other Forge mods and following the game's sound-effects volume.
- Gun names and descriptions are in English and Chinese and switch with the language without restarting. Players using different languages still match each other's gear, and the chat notice about a different Energy Lab build or tuning is translated too.
- The Charge Orb and Shock Orb leave your sight clear while charging; other players see the full charge effect on you.
- The beam, flame and charging orb stay on the muzzle while you strafe.
- `BepInEx/config/ForgeWeapon_EnergyLab/tuning-overrides.json` holds only the values you change; `tuning-defaults.json` lists every value.
- Requires ForgeCore, ForgeUI, ForgeAudio, ForgeVisual and MTFO. MTFO marks the game as modded and turns off achievements and public matchmaking, so these guns stay out of official play; without MTFO, Energy Lab does not load.
- The source code and Unity effect project are on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/tree/main/ForgeWeapon_EnergyLab). Media are local-only and are not included in Git.