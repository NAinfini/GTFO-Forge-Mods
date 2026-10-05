# Forge Weapon Energy Lab Experimental

Eight standalone energy guns for GTFO: beams, chain lightning, charged plasma, explosions, ricochets, flame, and gravity control.

**Beta 0.0.1.** Single-player and bot play have been tested; multiplayer has not been play-tested yet. Please report problems on the GitHub repository.

The Beam and Flamethrower have no recoil; the other six keep a light recoil, one third of a normal gun's. Every hit follows the game's normal bullet rules, so back hits, weakspots and armor work as they do for regular guns, judged from the direction the energy arrives from.

![The eight Energy Lab weapons](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/lineup.png)

## Weapons

These are renders of the shipped models, not in-game screenshots.

### Forge Beam

Hold fire for a sustained, precise beam. Track a target to keep dealing damage; its reach suits long sightlines, but sustained fire quickly spends the magazine.

![Forge Beam](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/beam.png)

### Forge Chain Arc

Land the first hit to send lightning through nearby visible enemies. Each hop loses some damage, making clustered targets a better use of a discharge than an isolated enemy.

![Forge Chain Arc](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/arc.png)

### Forge Flamethrower

Hold fire to sweep a short, widening cone of flame. Cover several exposed targets at close range; walls block the cone, and teammates caught in it can take direct-hit damage.

![Forge Flamethrower](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/flame.png)

### Forge Charge Orb

Hold to charge, then release an orb that explodes on contact. More charge increases its damage, size, speed, travel distance, and blast radius. Each fired orb costs one round, at any power. Lead moving targets and leave room for the explosion.

![Forge Charge Orb](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/plasma-blast.png)

### Forge Shock Orb

Hold to charge, then release an orb that passes through actors and shocks nearby enemies as it travels. Each fired orb costs one round, at any power. Direct contact damages each actor once per flight; repeated pulses favour firing along a group rather than across it. Walls stop the orb.

![Forge Shock Orb](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/plasma-arc.png)

### Forge Blast Gun

Create an instant explosion at the aimed impact point. There is no flying orb to lead: use a clear line of sight to place a blast into a group, while keeping teammates away from the impact.

![Forge Blast Gun](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/blast.png)

### Forge Ricochet Disc

Hold fire to launch fast discs that bounce off walls. A disc can hit an actor again after a bounce, so bank shots reward careful angles in confined spaces. Its flight ends after a short lifetime or when its bounce allowance runs out.

![Forge Ricochet Disc](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/disc.png)

### Forge Gravity Core

Hold fire to launch a core that forms a temporary gravity field on impact or when its flight expires. The field pulls visible enemies inward and damages them in pulses, with stronger damage near the centre. Place it where enemies will gather; only one gravity core can be active per shooter, and holding fire launches the next one after that slot becomes free.

![Forge Gravity Core](https://raw.githubusercontent.com/NAinfini/GTFO-Forge-Mods/main/ForgeWeaponEnergyLabExperimental/docs/images/hole.png)

## Default stats

Ammo costs are magazine rounds. Damage is the base value before vanilla receiver modifiers; explosions have distance falloff. Range describes aiming or direct hits; projectile travel follows its flight limits. Charged ranges run from minimum to full charge. Effect duration is the explosion's lifetime, not repeated damage.

The Blast Gun's contact adds no separate damage; its damage comes from the explosion.

| Weapon | Slot | Magazine | Ammo / shot | Interval / charge (s) | Damage | Range (m) | Radius / duration | Bounces / targets |
| --- | --- | ---: | --- | --- | --- | --- | --- | --- |
| Forge Beam | Main | 40 | 1 | 0.1 | 4.35 / tick | 45 | - | 1 / tick |
| Forge Chain Arc | Main | 7 | 1 | 0.7 | 15.9 first hit; 80% retained / hop | 16 first hit; 6 / hop | - | 4 total |
| Forge Flamethrower | Main | 50 | 1 | 0.1 | 2.695 / tick / receiver | 8 | 0.15-1.43 m cone radius | All visible receivers in cone |
| Forge Charge Orb | Special | 4 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / explosion | 75 aim; 18-42 travel | 1.5-2.5 m blast; 0.8 s effect | All in blast |
| Forge Shock Orb | Special | 5 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / contact; 0.96-5.016 / pulse | 75 aim; 18-42 travel | 2-3.5 m pulse; every 0.4 s | Pierces actors; 3 enemies / pulse |
| Forge Blast Gun | Special | 6 | 1 | 0.45 | 30.13 / explosion | 40 | 2.2 m blast; 0.4 s effect | All in blast |
| Forge Ricochet Disc | Special | 16 | 1 | 0.55 | 23.95 / hit | 60 | 0.3 m disc; 1.6 s flight | 4 wall bounces; 1 hit / actor between bounces |
| Forge Gravity Core | Special | 2 | 1 | 2 | 6.75 core / 1.6875 edge / pulse | 60 | 5 m field (0.8 m core); 3.5 s incl. 0.35 s harmless collapse; pulse every 0.4 s | Visible enemies in field |

These are the defaults; change them in `tuning.json`.

Both orbs cost 16 reserve ammo per round, at any charge power.

## Gameplay rules

- Friendly fire and lock breaking follow vanilla rules: direct hits behave like bullets, and explosions behave like mines.
- Every shot spends one round and keeps the normal firing animations, empty-magazine click and reloads. Beam, Chain Arc, Flamethrower, Blast Gun, Ricochet Disc and Gravity Core fire full-auto; charged orbs fire when you release, at most one every 0.85 seconds. Each gun has a limit on projectiles in flight; holding fire waits until one ends.
- Stats mods count each shot once. Chain hops, flame targets and Blast Gun hits all count as hits of that shot, so a hit rate that counts every hit can pass 100% with these guns. Orb, disc and gravity-core hits land after the shot and do not count towards hit rate; their damage still counts.
- Bots can use all eight guns, with their energy effects and sounds.
- Stock tracers, gunfire sounds, muzzle flashes and casings are disabled for all eight guns; their energy effects and sounds remain.
- Explosions hit each enemy once, with damage falling off over distance. Back hits, weakspots and armor work as they do for bullets, and Mother and Tank spore pods in range can burst.
- Chain hops, shock pulses, and the gravity pull select enemies only.
- Shots alert sleepers like vanilla gunfire, using energy-weapon sounds; explosions also use vanilla mine noise.
- Charged orbs reach full power after 1.1 seconds and stay fully charged for as long as the trigger is held; releasing fires.

## Installation

1. Open a GTFO profile in r2modman and install **Forge Weapon Energy Lab Experimental** (`forgeweapon_energylab_experimental`).
2. Let r2modman install its only dependency, **BepInExPack_GTFO**.
3. Start the game with **Start modded** and select the new guns in the Main or Special loadout slots.

Every player in the lobby needs this mod at the same version with the same `tuning.json`. When a player's setup differs, the energy guns are refused for that game and chat names the player.

## Customising numbers

After the first successful load, open your r2modman profile folder through its settings. The file is `BepInEx/config/ForgeEnergyLab/tuning.json` inside that profile.

Close the game, edit the numbers, save the file, and restart. Keep all eight entries under `weapons` and all their numeric fields. The optional `_descriptions` object explains the fields in English. Distances are metres, times are seconds, and shot costs are whole magazine rounds; `ammoPerRound` controls the reserve-ammo cost of one magazine round.

Keep `ammoCost` at 1 for every weapon: each shot spends one round, including a fully charged orb.
Invalid JSON, missing, duplicate or unknown fields, incorrect number types, non-finite or out-of-range values, and inconsistent charge, ammo, or radius settings reject the whole file. The error identifies the field, and Energy Lab stays disabled until the file is corrected. There is no partial application or live reload.

Copy the same tuning to every player's profile and restart all clients before playing together. Formatting and `_descriptions` do not affect compatibility; gameplay values do.

## Known limitations

Multiplayer, including late joins, reconnects and checkpoints, has not been play-tested yet.

## License

Version 0.0.1 (beta) is released under the [MIT license](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/LICENSE). The embedded shared effect bundle is CC0; see [`Assets/Vfx/LICENSE.txt`](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/Assets/Vfx/LICENSE.txt).
