# Forge Weapon Energy Lab Experimental: developer notes

Player documentation is in [README.md](README.md). This file covers how the guns are wired into the game and the extension API.

## How the guns run

- Every discharge uses one vanilla firing call, spends one round, and keeps vanilla shot counting, firing animations, empty-magazine feedback and reloads. Beam and Flamethrower have no per-shot camera or first-person model kick; the other six keep their donor recoil pattern at one third strength, including the separate first-person movement curves, with curve timing and random multipliers unchanged.
- Every enemy hit follows vanilla bullet rules for back damage, weakspots and armor: the incoming energy direction and the first limb reached by its physics ray determine the bonus. Beams and flame travel from the muzzle, chain hops from the previous target, moving contacts along the projectile's current velocity (including disc bounces), and blasts and field pulses outward from their centre.
- Each discharge has one counted shot ray. Beam hits, every chain hop, flame targets, and the Blast Gun's contact and enemy explosion hits occur inside the same firing call for hit-rate mods. The shooter's game deals these instant hits and sends the resolved effects to other players.
- Orbs, discs and gravity cores launch during their firing call. The host simulates their contacts, explosions, bounces and pulses after that call has returned, so these delayed hits do not count towards hit rate. Damage statistics follow each stats mod's normal attribution rules for hits outside a firing call, including after weapon switches. A client spends its round locally and sends one launch command; the host never bills it again or fires a gun to report a later hit. Bots launch on the host through their vanilla synced firing calls.
- On join, a compatibility check compares registered definitions, effective tuning, native IDs, model keys, bundle contents and the mod build.

## Extension API

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
                Range = 60f, Interval = 0.2, Damage = 18f, Stagger = 0.25f,
                ReloadTime = 3.3f, AimTime = 0.35f },
            EnergyLab.GetModel("beam")));
    }
}
```

