# Changelog

## 1.0.0

Version number only: the first release was 0.1.0, and 1.0.0 is the version the Forge packages share from now on. Packages that depend on this library need 1.0.0 or later. `OutlineClaims` is now internal: nothing outside the package used it. The kept material copies of a level's own materials are destroyed when the level ends.

- The heavy grenade launcher's explosion now ships with ForgeVisual: Arsenal's launcher and Energy Lab's Blast Gun draw the same shared blast instead of each shipping a copy.
- A gun model no longer needs a left-hand socket: the hand is derived from the game's own hold and pulled back along the gun into the arm's reach at the hip; a missing right grip is the model's origin.

## 0.1.0

First release.

- Model bundles registered by pack and loaded through ForgeCore; materials rebuilt with the game's gear shader from the bundle's profile.
- Vanilla objects dressed in a model, fitted to their bounds, with the vanilla meshes hidden and an undo.
- Rigged models on players: worn on the first-person body (skeletons matched through their bind poses, scaled by the shoulder span or a limb, cut to the worn bones), the third-person pose copied onto a skeleton of other proportions (chosen limbs bent so their ends land where the body's do, hands staying on grips), a copy of the held item in the model's hand, and clips played on the player's own body with its IK paused. The bone map, proportions, covered bones, hidden parts and IK handling come from the calling mod.
- Imported models (a converted .glb or a geomorph's own model) built from the bytes a caller hands over: a mesh with a submesh per material, mipped textures and the game's gear shader as the lit material, explicit failure codes, a per-level cache with a memory budget. Decoration only, no collider.
- Glow materials with HDR colours, restorable; pulsing glow spots.
- The game's own effects by name (`Game:`): impacts, blood, sparks, tripmine, tracers, flyer and bulb deaths, muzzle flashes. One-shot, held, line and attached playback.
- Particle systems built in Unity, one shared instance per pack, with capacity and priority; code-configured sprite particles shared by every mod, with priority, pinned sprites and an overdraw budget.
- Decals on any surface with a life and a fade-out, in sets that recycle their oldest mark; one shared set built ahead of use; `Decals.Stick` finds the surface a shot hits.
- Impact presets: an effect by name, a pooled prefab, a light flash, a decal and a camera shake.
- Camera shake and directional kicks through the game's own camera, falling off with distance, capped when several run together, scaled by the player's `Camera / ShakeStrength`.
- One particle shader (alpha and additive), built-in soft dot and soft line textures.
- Ground markers that follow floors, slopes and steps and stop at walls, in five base styles.
- Beams and trails, with every colour and timing given by the mod that uses them.
- `FxInstancePool` and `FxInstance`: pooled copies of a prefab effect that the caller places, holds, switches by layer or state child and releases itself.
- `PointLights`: the one shared pool of effect lights (32, the game's own light objects, no shadows), flashed, held or blinking; flashes can be turned off (`Effects / FlashLights`).
- A bundle built with addressable names registers its prefabs without a model profile.
- Failures are explicit and logged once with the mod and the name; the log holds real failures only. Nothing allocates per frame. Updates run as ForgeCore frame callbacks.
