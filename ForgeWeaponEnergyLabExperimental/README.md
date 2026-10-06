# Forge Weapon Energy Lab Experimental

Eight energy guns for GTFO.

**Beta.** Tested solo and with bots; multiplayer has not been tested yet. Please report bugs on [GitHub](https://github.com/NAinfini/GTFO-Forge-Mods/issues).

![The eight Energy Lab weapons](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/lineup.png)

## Weapons

### Forge Beam

Hold fire for a continuous beam. No recoil.

![Forge Beam](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/beam.png)

### Forge Chain Arc

Lightning that jumps from the first target to up to three more nearby enemies.

![Forge Chain Arc](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/arc.png)

### Forge Flamethrower

A short flame cone that hits everything inside it. No recoil.

![Forge Flamethrower](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/flame.png)

### Forge Charge Orb

Hold to charge, release to fire an orb that explodes on impact. More charge, bigger blast.

![Forge Charge Orb](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/plasma-blast.png)

### Forge Shock Orb

Hold to charge, release to fire an orb that flies through enemies and shocks the ones nearby.

![Forge Shock Orb](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/plasma-arc.png)

### Forge Blast Gun

An instant explosion where you aim.

![Forge Blast Gun](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/blast.png)

### Forge Ricochet Disc

Discs that bounce off walls up to four times.

![Forge Ricochet Disc](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/disc.png)

### Forge Gravity Core

Fires a core that opens a gravity field, pulling enemies in and damaging them.

![Forge Gravity Core](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/hole.png)

## Stats

| Weapon | Slot | Magazine | Damage | Range | Area |
| --- | --- | ---: | --- | --- | --- |
| Beam | Main | 40 | 4.35 per tick | Unlimited | - |
| Chain Arc | Main | 7 | 15.9, -20% per hop | 16 m | Hops up to 6 m |
| Flamethrower | Main | 50 | 2.7 per tick | 8 m | Cone |
| Charge Orb | Special | 4 | 12-62.7 | Flies 18-42 m | 1.5-2.5 m blast |
| Shock Orb | Special | 5 | 12-62.7 on contact | Flies 18-42 m | 2-3.5 m shock |
| Blast Gun | Special | 6 | 30.1 | Unlimited | 2.2 m blast |
| Ricochet Disc | Special | 16 | 23.95 | Flies 1.6 s, 4 bounces | - |
| Gravity Core | Special | 2 | up to 6.75 per pulse | Flies up to 3 s | 5 m field |

## Installation

Install with r2modman; it also installs BepInExPack_GTFO. Everyone in the lobby needs the same version and the same tuning changes.

## Tuning

Every number is listed in `BepInEx/config/ForgeEnergyLab/tuning-defaults.json`. To change one, copy only that value into `tuning-overrides.json` in the same folder, for example:

```json
{
  "weapons": {
    "forge.energylab.blast": { "damage": 40, "blastRadius": 3 }
  }
}
```

Restart the game after editing. Values you did not copy follow each update's defaults. If the file has a mistake, the mod stays off and the log names the field.

## Known issues

- Multiplayer has not been tested.
- In stats mods, hit rate that counts every hit can go over 100% with Chain Arc, Flamethrower and Blast Gun.

## License

[MIT](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/LICENSE). The effect bundle is [CC0](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/Assets/Vfx/LICENSE.txt).
