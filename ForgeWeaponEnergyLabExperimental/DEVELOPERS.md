# Forge Weapon Energy Lab Experimental: developer notes

Player documentation is in [README.md](README.md). This file covers how the guns are wired into the game and the extension API.

## How the guns run

- Every discharge uses one vanilla firing call, spends one round, and keeps vanilla shot counting, firing animations, empty-magazine feedback and reloads. Beam and Flamethrower have no per-shot camera or first-person model kick; the other six keep their donor recoil pattern at one third strength, including the separate first-person movement curves, with curve timing and random multipliers unchanged.
- Every enemy hit follows vanilla bullet rules for back damage, weakspots and armor: the incoming energy direction and the first limb reached by its physics ray determine the bonus. Beams and flame travel from the muzzle, chain hops from the previous target, moving contacts along the projectile's current velocity (including disc bounces), and blasts and field pulses outward from their centre.
- Each discharge has one counted shot ray. Beam hits, every chain hop, flame targets, and the Blast Gun's contact and enemy explosion hits occur inside the same firing call for hit-rate mods. The shooter's game deals these instant hits and sends the resolved effects to other players.
- Orbs, discs and gravity cores launch during their firing call. The host simulates their contacts, explosions, bounces and pulses after that call has returned, so these delayed hits do not count towards hit rate. Damage statistics follow each stats mod's normal attribution rules for hits outside a firing call, including after weapon switches. A client spends its round locally and sends one launch command; the host never bills it again or fires a gun to report a later hit. Bots launch on the host through their vanilla synced firing calls.
- On join, a compatibility check compares registered definitions, effective tuning, native IDs, model keys, bundle contents and the mod build.

## Gameplay rules

- Friendly fire and lock breaking follow vanilla rules: direct hits behave like bullets, and explosions behave like mines.
- Beam, Chain Arc, Flamethrower, Blast Gun, Ricochet Disc and Gravity Core use vanilla full-auto timing. Charged orbs reach full power after 1.1 seconds, stay fully charged while the trigger is held, and fire on release, with at least 0.85 seconds between shots. Active-projectile limits pause further launches until a slot is free.
- A per-shot hit rate counts each discharge once; a per-hit rate counts every beam hit, chain hop, flame target, Blast Gun contact and explosion hit, so it can pass 100% with these guns.
- Stock tracers, gunfire sounds, muzzle flashes and casings are disabled for all eight guns; their energy effects and sounds remain.
- Explosions hit each enemy once, on the first limb reached by a ray from the blast centre toward its nearest surface, with distance falloff. Mother and Tank spore pods in range can burst.
- Chain hops, shock pulses and the gravity pull select enemies only.
- Shots alert sleepers like vanilla gunfire, using energy-weapon sounds; explosions also use vanilla mine noise.

## Full default stats

Ammo costs are magazine rounds. Damage is the base value before vanilla receiver modifiers; explosions have distance falloff. Range describes aiming or direct hits: only the Chain Arc's first hit and the flame have a tuned range, every other shot reaches 1000 m, farther than any level sightline, with no distance falloff. Projectile travel follows its flight limits. Charged ranges run from minimum to full charge. Effect duration is the explosion's lifetime, not repeated damage. The Blast Gun's contact adds no separate damage; its damage comes from the explosion. Both orbs use 16 reserve units per round, at any charge power.

| Weapon | Slot | Magazine | Ammo / shot | Interval / charge (s) | Damage | Range (m) | Radius / duration | Bounces / targets |
| --- | --- | ---: | --- | --- | --- | --- | --- | --- |
| Forge Beam | Main | 40 | 1 | 0.1 | 4.35 / tick | Unlimited | - | 1 / tick |
| Forge Chain Arc | Main | 7 | 1 | 0.7 | 15.9 first hit; 80% retained / hop | 16 first hit; 6 / hop | - | 4 total |
| Forge Flamethrower | Main | 50 | 1 | 0.1 | 2.695 / tick / receiver | 8 | 0.15-1.43 m cone radius | All visible receivers in cone |
| Forge Charge Orb | Special | 4 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / explosion | Unlimited aim; 18-42 travel | 1.5-2.5 m blast; 0.8 s effect | All in blast |
| Forge Shock Orb | Special | 5 | 1 (every charge power) | 0.85; charge 0.3-1.1 | 12-62.7 / contact; 0.96-5.016 / pulse | Unlimited aim; 18-42 travel | 2-3.5 m pulse; every 0.4 s | Pierces actors; 3 enemies / pulse |
| Forge Blast Gun | Special | 6 | 1 | 0.45 | 30.13 / explosion | Unlimited | 2.2 m blast; 0.4 s effect | All in blast |
| Forge Ricochet Disc | Special | 16 | 1 | 0.55 | 23.95 / hit | Unlimited aim | 0.3 m disc; 1.6 s flight | 4 wall bounces; 1 hit / actor between bounces |
| Forge Gravity Core | Special | 2 | 1 | 2 | 6.75 core / 1.6875 edge / pulse | Unlimited aim | 5 m field (0.8 m core); 3.5 s incl. 0.35 s harmless collapse; pulse every 0.4 s | Visible enemies in field |

## Tuning files

`BepInEx/config/ForgeEnergyLab/tuning-defaults.json` is rewritten on every launch with this build's values and a `_descriptions` object explaining each field; it is never read. `tuning-overrides.json` holds only changed values: a `weapons` object keyed by weapon ID, each entry naming any subset of that weapon's fields. Every value it does not name follows the current defaults, so a new version's default changes reach players who tuned other values. Earlier versions' `tuning.json` is no longer read.

Distances are metres, times are seconds, and shot costs are whole magazine rounds; `ammoPerRound` controls the native reserve-ammo cost of one magazine round. Keep `ammoCost` at 1 for every weapon: each native discharge spends one round, including a fully charged orb.

Invalid JSON, unknown weapon IDs, duplicate or unknown fields, incorrect number types, non-finite or out-of-range values, and inconsistent charge, ammo, or radius settings reject the whole file. The error identifies the field, and Energy Lab stays disabled until the file is corrected. There is no partial application or live reload. Formatting does not affect the multiplayer compatibility check; the effective gameplay values do.

## Extension API

Reference `ForgeWeaponEnergyLabExperimental.dll` and declare `[BepInDependency(EnergyLab.PluginId, EnergyLab.Version)]`. The plugin GUID is **`NAinfini.ForgeWeaponEnergyLabExperimental`**.

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

Definitions can share a model. All players need the same extensions.

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
                Interval = 0.2, Damage = 18f, Stagger = 0.25f,
                ReloadTime = 3.3f, AimTime = 0.35f },
            EnergyLab.GetModel("beam")));
    }
}
```

