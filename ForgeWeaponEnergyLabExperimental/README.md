# Forge Weapon Energy Lab Experimental

Eight standalone energy guns for GTFO: beams, chain lightning, charged plasma, explosions, ricochets, flame, and gravity control.

**Beta 0.0.1.** Single-player and bot play have been tested; multiplayer has not been play-tested yet. Please report problems on the GitHub repository.

The Beam and Flamethrower have no per-shot camera or first-person model kick. The other six guns keep their donor recoil pattern at one third strength, including the separate first-person movement curves; curve timing and random multipliers stay unchanged. All eight keep their firing animations. Every enemy hit follows vanilla bullet rules for back damage, weakspots and armor: the incoming energy direction and the first limb reached by its physics ray determine the bonus, rather than automatically choosing the head. Beams and flame travel from the muzzle, chain hops from the previous target, moving contacts along the projectile's current velocity (including disc bounces), and blasts and field pulses outward from their centre.

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

Both orbs use 16 native reserve units per round, at any charge power.

## Gameplay rules

- Friendly fire and lock breaking follow vanilla rules: direct hits behave like bullets, and explosions behave like mines.
- Every discharge uses one vanilla firing call, spends one round, and keeps vanilla shot counting, firing animations, empty-magazine feedback and reloads. Beam and Flamethrower have no per-shot camera or first-person model kick; the other six use one-third recoil strength and movement curves. Beam, Chain Arc, Flamethrower, Blast Gun, Ricochet Disc and Gravity Core use vanilla full-auto timing; charged orbs fire on release, with at least 0.85 seconds between shots. Active-projectile limits pause further launches until a slot is free.
- Each discharge has one counted shot ray. Beam hits, every chain hop, flame targets, and the Blast Gun's contact and enemy explosion hits occur inside the same firing call for hit-rate mods. A per-shot hit rate counts such a shot once; a per-hit rate counts every one of these hits, so it can pass 100% with these four guns. The shooter's game deals these instant hits and sends the resolved effects to other players.
- Orbs, discs and gravity cores launch during their firing call. The host simulates their contacts, explosions, bounces and pulses after that call has returned; these delayed hits do not count towards hit rate. Damage statistics follow each stats mod's normal attribution rules for hits outside a firing call, including after weapon switches. A client spends its round locally and sends one launch command; the host never bills it again or fires a gun to report a later hit. Bots launch on the host through their vanilla synced firing calls.
- Stock tracers, gunfire sounds, muzzle flashes and casings are disabled for all eight guns; their energy effects and sounds remain.
- Explosions hit each enemy once, on the first limb reached by a ray from the blast centre toward its nearest surface, with the existing distance falloff. Back damage, weakspot and armor multipliers follow vanilla bullet rules, and Mother and Tank spore pods in range can burst.
- Chain hops, shock pulses, and the gravity pull select enemies only.
- Shots alert sleepers like vanilla gunfire, using energy-weapon sounds; explosions also use vanilla mine noise.
- Charged orbs reach full power after 1.1 seconds and stay fully charged for as long as the trigger is held; releasing fires.

## Installation

1. Open a GTFO profile in r2modman and install **Forge Weapon Energy Lab Experimental** (`forgeweapon_energylab_experimental`).
2. Let r2modman install its only package dependency, **BepInExPack_GTFO**, which includes GTFO-API and the BepInEx 6 IL2CPP loader.
3. Start the game with **Start modded** and select the new guns in the Main or Special loadout slots.

Every player in a lobby needs this mod with identical tuning. The compatibility check on join refuses mismatched setups for energy-weapon play and names the mismatched player in chat. Use the same mod build, model bundles, and any dependent weapon extensions on every client.

## Customising numbers

After the first successful load, open your r2modman profile folder through its settings. The file is `BepInEx/config/ForgeEnergyLab/tuning.json` inside that profile.

Close the game, edit the numbers, save the file, and restart. Keep all eight entries under `weapons` and all their numeric fields. The optional `_descriptions` object explains the fields in English. Distances are metres, times are seconds, and shot costs are whole magazine rounds; `ammoPerRound` controls the native reserve-ammo cost of one magazine round.

Keep `ammoCost` at 1 for every weapon: each native discharge spends one round, including a fully charged orb.
Invalid JSON, missing, duplicate or unknown fields, incorrect number types, non-finite or out-of-range values, and inconsistent charge, ammo, or radius settings reject the whole file. The error identifies the field, and Energy Lab stays disabled until the file is corrected. There is no partial application or live reload.

Copy the same effective tuning to every player's profile and restart all clients before playing together. Formatting and `_descriptions` do not affect compatibility; gameplay values do.

## For modders

Reference `ForgeWeaponEnergyLabExperimental.dll` and declare `[BepInDependency(EnergyLab.PluginId, EnergyLab.Version)]`. The plugin GUID is **`NAinfini.ForgeWeaponEnergyLabExperimental`**, and the current version is **`0.0.1`**.

The public namespace is `ForgeEnergyLab.Api`:

- `EnergyLab.Register(WeaponDefinition)` adds a weapon using an existing `EnergyMode`, a Main or Special `WeaponSlot`, a matching tuning record, and a `WeaponModel`.
- `GetDefinition(id)` and `GetTuning(id)` return immutable snapshots. Use a record's `with` expression and `SetTuning(id, tuning)` to replace tuning before installation.
- Tuning types are `BeamTuning`, `ArcChainTuning`, `FlameTuning`, `PlasmaBlastTuning`, `PlasmaArcTuning`, `BlastTuning`, `DiscTuning`, and `BlackHoleTuning`.
- `GetModel(key)` reuses a built-in model reference, including its inventory icon. Keys are `beam`, `arc`, `flame`, `plasma-blast`, `plasma-arc`, `blast`, `disc`, and `hole`. It does not instantiate a Unity object.

A weapon you register gets its own name, optional loadout description, slot, every gameplay number, its model and its icon. Firing, hit rules, sounds and effects come from the `EnergyMode` it reuses, so any model can be paired with any of the eight behaviours.

Register weapons and replace tuning in your dependent plugin's `Load()`, after Energy Lab has loaded successfully and before the first game-data gear installation. That installation closes registration: later `Register` or `SetTuning` calls throw `InvalidOperationException`; snapshot reads remain available. Invalid definitions, duplicate identities, and native-ID collisions are rejected.

Use a stable lowercase ID with at least three dot-separated segments, such as `author.mod.weapon`, up to 128 characters. For extension weapons, native IDs use FNV-1a 32-bit over UTF-8 `id + "/gear"`, `id + "/category"`, and `id + "/archetype"`. Gear and archetype IDs are `0x20000000 | (hash & 0x1fffffff)`; category IDs are `0x8000 | (hash & 0x7fff)`. Built-in weapons retain their fixed IDs. Collisions are refused rather than assigned another ID; keep your identity stable for saved loadouts.

For your own model, pass `new WeaponModel(key, absoluteBundlePath, absoluteIconPath)`; the icon is a PNG of at most 1 MiB and is required with your own bundle. With a built-in model you may still pass `iconPath` to replace its icon. Bundles load when the first gun using them is assembled and unload after no gun object uses them, so ship one bundle per model to keep unused models out of memory. Supply a Unity 2019.4.21f1 AssetBundle containing:

- A `presentation-format` text asset with value `4`, a `presentation-profile` JSON text asset, and a prefab named by the model key.
- The parts `Receiver`, `Rail`, `Front`, `Stock`, `Magazine`, and `Sight`, plus the sockets `RightHand`, `LeftHand`, `Muzzle`, and `SightLook`.
- Metre-scale Unity coordinates: +Z toward the muzzle, +Y upward, and the root origin at the right-hand grip. The profile describes the parts, socket positions, bounds, and `textureSets`; its `weapons[].mode` is the model key.
- Hip and relaxed holds use the donor position plus the shared `HoldOffset`; aim positions and all hold rotations stay native. Every energy model and its sockets are uniformly scaled to 85% in first and third person before grip, muzzle and sight alignment; native guns and hand rigs keep their scale. The support-hand proxy targets the wrist: it starts at the donor's `LeftRest` position and rotation, shifts along the gun's forward axis to the scaled, mounted `LeftHand` socket's longitudinal position, and slides back by any reach beyond the donor rest, counting the forward `HoldOffset` of the whole hold. The authored socket's lateral position, height and rotation do not replace the donor wrist pose. Both views use this rule; the wielded item's cached third-person SyncIK target is also bound to the proxy. Donor wrist turns, reloads and draws retain their independent motion; finger grip closure remains native.
- Body surfaces with their declared albedo/metallic textures and optional normal/emission textures; transparent sight glass uses `glass`, and sight marks use `reticle` surfaces.

Definitions can share a model. The compatibility check covers registered definitions, effective tuning, native IDs, model keys, bundle contents, and the mod build; all players need the same extensions.

This example registers a separate Special-slot beam using the built-in beam model:

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeEnergyLab.Api;

/// <summary>A dependent plugin that reuses the built-in beam behaviour and model.</summary>
[BepInPlugin("example.energylab.extension", "Energy Lab API Example", "1.0.0")]
[BepInDependency(EnergyLab.PluginId, EnergyLab.Version)]
public sealed class ExamplePlugin : BasePlugin
{
    /// <summary>Registers a distinct special-slot beam before Energy Lab installs game data.</summary>
    public override void Load()
    {
        var defaults = (BeamTuning)EnergyLab.GetTuning("forge.energylab.beam");
        EnergyLab.Register(new WeaponDefinition(
            "example.energylab.longbeam", "Example Long Beam", WeaponSlot.Special,
            EnergyMode.Beam,
            defaults with { Magazine = 24, AmmoPerRound = 4f,
                Range = 60f, Interval = 0.2, Damage = 18f, Stagger = 0.25f,
                ReloadTime = 3.3f, AimTime = 0.35f },
            EnergyLab.GetModel("beam")));
    }
}
```

## Building from source

You need the .NET 6 SDK, Python 3 (the build compresses the audio), and a GTFO BepInEx profile that has been started once, so it has generated interop assemblies and GTFO-API. From this mod's folder:

```powershell
dotnet build ForgeWeaponEnergyLabExperimental.csproj -c Release -p:GTFOBepInExPath="C:/path/to/profile/BepInEx"
```

The eight model bundles in `Assets/Models` ship prebuilt; copy them beside the DLL. `tools/build_presentation.ps1` rebuilds them with Unity 2019.4.21f1 and Blender 5.2, but it needs the source models, which are not published.

## Known limitations

Real multiplayer sessions have not yet been validated, including late joins, reconnects, and checkpoints. In-game verification of damage/ammo behaviour, hands and reloads in both views, audio, performance, and GPU stability remains outstanding.

## License

Version 0.0.1 (beta) is released under the [MIT license](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/LICENSE). The embedded shared effect bundle is CC0; see [`Assets/Vfx/LICENSE.txt`](https://github.com/NAinfini/GTFO-Forge-Mods/blob/main/ForgeWeaponEnergyLabExperimental/Assets/Vfx/LICENSE.txt).
