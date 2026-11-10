# Changelog

## 1.0.1

Changes since the first upload. Version 1.0.1 because it is released early; it needs ForgeOrdnance 1.0.1.

- Strikes are rebuilt with a drop warning and fast fall; the hunter strike locks the largest enemy with a laser, and gas and fire only count half for teammates.
- Turrets and devices arrive by burrowing (see ForgeOrdnance 1.0.1), and a spent turret is recalled at once.
- Heavy weapons: grenades can deal true damage with an edge share, and the heavy weapons take a homing spec and a recoil scale. New: Recoilless Rifle and Tracking Rifle (five heavy weapons in all).
- New strike: Strafing Run (ten in all), and supplies grew to eight pods (adding syringe, mine and utility).
- New standing devices (the `arsenal.Deployable` kind): Foam Sprayer, Mine Layer, Scanner Tower and Floodlight Tower.
- Fixes: a late joiner or a new host now sees the mines still lying with the right definition; a pod's heavy weapons are racked for the right count; the heavy weapon caches no longer mix up two weapons that share a model; a recoilless rocket is drawn as a rocket on every peer; a Strafing Run hits each enemy once per pass.
- The grenade launcher's blast is now the shared heavy explosion from ForgeVisual, the same one the Blast Gun uses.
- Heavy weapons hold better: the left hand follows the game's own grip and is pulled back along the gun when it is out of reach.

### Interface changes

- `ClusterScatter` is removed. Cluster bomblet offsets are now computed inside the strike code and are not public; there is no replacement.
- `HeavyWeaponSpec.Restore(model, gearId, ammoRatio, grenade, tuning)` gained `HomingSpec? homing = null, float recoilScale = 1f`. Recompile callers.
- `GrenadeSpec(Damage, Radius, Speed)` gained `TrueDamage = false` and `EdgeShare = 0.5f`. Recompile callers.
- `ShellLook` gained `Precision`, `Hunter` and `Incendiary`; code that switches over it must handle them.
- Some strike timing constants (`FallSeconds`, `IgniteDelay`, `BeamChargeSeconds`) are removed; read the values from the strike definitions instead.
- The rack record (`RackBytes`, now 108) carries the pod's weapon count, the mine record carries its definition's hash, and the grenade burst record (41 bytes) says whether the round was a rocket. Every player needs the same version.

## 1.0.0

First release.

- Five supply pods: mixed, ammo, medical, tool and consumables.
- Three sentry turrets: Watch (burst), Bulwark (sniper) and Scythe (shotgun), with their own models and sounds. They are stronger than the game's sentries and sink back underground when out of ammunition or after 180 seconds.
- Nine strikes: precision, area barrage, creeping barrage, cluster, incendiary, gas, stasis, cutting beam and hunter shell.
- The cutting beam only chases enemies it can walk to and see, and follows the path around corners.
- Stasis slows enemies inside its field and gives them back their exact speed when they leave.
- Three heavy weapons that arrive in a drop pod: a machine gun, an anti-materiel rifle and a grenade launcher. They take the place of your special weapon; the special weapon key swaps between the two with both keeping their ammunition. A spent heavy weapon is gone, packs never refill it (they refill your own special weapon), and G, going down or a checkpoint reload leave you with your own special weapon.
- Grenades are launched and settled by the host, so every player sees the same blast.
- Every call-in has its own beacon colour, picture, effects and sounds.
- Every name, description and prompt is in English and Chinese, following ForgeUI's `Language` setting.
- The give-back key is in the game's settings, Controls.
- Requires Forge Ordnance, MTFO, ForgeUI, ForgeAudio and ForgeVisual, which r2modman installs with it.
