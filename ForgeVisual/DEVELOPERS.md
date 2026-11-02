# ForgeVisual for developers

This file is for mod authors. Players should read `README.md`.

ForgeVisual draws things in the game world for any mod: model bundles, vanilla objects dressed in your models, rigged models worn by players, glow materials, the game's own effects by name, particle systems you built in Unity, code-configured sprite particles, decals, impact presets, effect lights, outlines round enemies and objects, ground markers, beams and trails, and it shakes the local camera. It only says how to draw; when, where and for whom is yours. Looks (colours, textures, timings, sizes) are always passed in by the mod that owns them.

It needs only BepInExPack_GTFO and ForgeCore. Within 1.x, public members are only added, never removed or changed. Internal classes, log wording and packaging are not part of the public API.

## Layering: what this package holds and what it does not

ForgeVisual holds the mechanisms for drawing in the world: effect and model bundle registration, playing by name, the vanilla effect catalogue, particle pools, additive and glow shaders, decals, impact presets, effect lights, ground rings, beams, trails, outlines (and outlines seen through walls), camera shake, and loading models or re-skinning vanilla objects. What is drawn on the screen (full-screen layers, hit edges, visor, power-on, and screen effects such as a hit flash, vignette, blur or chromatic aberration) is ForgeUI's. Finished effects, textures, models and colours stay in the mod that adds them, registered as `pack:entry` and played by name. A mechanism other mods may use goes here without waiting for a second user.

## Folder layout

| Path | Contents |
|---|---|
| `Core/` | Plain logic with no Unity reference: names, slots, hold windows, ribbon samples, marker numbers, overdraw budget, model profile, vanilla catalog, the body solvers (`BodyRig.cs`: skeleton matching, pose corrections, held placement, mesh cut). |
| `Native/` | The plugin and everything that touches Unity. `ShaderBundle.Generated.cs` is generated; do not edit it. |
| `tools/shaders/` | The shader sources (marker, particle, outline) and their Unity build. |
| `tests/Core/` | Offline tests of `Core/`. |

## Build and test

Run from the repository root. Replace `<profile>` with a GTFO r2modman profile.

```
dotnet build Mods/ForgeVisual/ForgeVisual.csproj -c Release --artifacts-path artifacts/dotnet --disable-build-servers -m:1 -p:GTFOBepInExPath=<profile>/BepInEx
dotnet run --project Mods/ForgeVisual/tests/Core/ForgeVisualCore.csproj -c Release --artifacts-path artifacts/dotnet
```

Shaders: hold `artifacts/work/unity-lock.txt` in the main checkout, then `pwsh Mods/ForgeVisual/tools/shaders/build_shaders.ps1` (Unity 2019.4.21f1). It rewrites `Native/ShaderBundle.Generated.cs`; the bundle is deterministic. Delete the Unity work folder afterwards.

## Rules

- **Names.** Everything is `Pack:item`. `Game:` is the vanilla catalog (`Fx.GameNames`), `Forge:` is this library (`Forge:soft-dot`, `Forge:soft-line`); neither can be registered by a mod. The first registration of a name keeps it; a second is refused and logged.
- **Resolve once.** `Fx.Find`, `Models.Get`, `ParticlePool.Get`, `ParticlePool.Channel`, `SpriteSheet.Get` look a name up; call them at load and keep the handle. Nothing in play looks up a name.
- **Failures are explicit.** A missing name, a pack that is not installed, a bundle that does not load or missing shaders return null or `FxResult.Unavailable` and log once with the mod and the name. Nothing falls back to another effect.
- **Nothing allocates per frame.** Playing, emitting, moving markers, beams, trails and domes reuse pooled objects and one property block.
- **Levels.** On every level change, checkpoint reload or return to the lobby the library clears its own pools and instances (sprites, decals, effect lights, running shakes, particle channels, held effects and impact copies, markers, beams, trails, glow spots). Your handles to bundles, models, sheets, channels and `FxHandle`s stay valid; a `GroundMarker`, `Beam`, `Trail` or `Sprite` you held is gone (`Alive` is false).

## Using it

- **Bundles and models.** `Bundles.Register(modId, pack, path)` loads a bundle through ForgeCore (a relative path is next to your DLL). A bundle with an `ordnance-profile` text asset (format 1) has its materials rebuilt with the game's gear shader on first use; otherwise its prefabs are indexed by name. `Models.Get("Pack:item").Spawn()`; `Models.Dress(vanillaObject, model, options)` puts a model on a vanilla object fitted to its bounds and can hide the vanilla meshes; `Dressed.Undo()` restores it.
- **Rigged models on players.** Four mechanisms for outfits, mechs, transformations and the like. Your mod passes the bone map, the proportions, which bones are covered, which vanilla parts hide and how IK is treated; the library holds no model's bone table. Bones are `BodyBone` (Unity's humanoid names and numbers).
  - *Worn on the first-person body.* `Models.WearFirstPerson(agent, look, options)` spawns `look`, matches its skeleton to the local player's first-person skeleton through both bind poses (shoulders and hips set the common frame; each bone's rest direction is swung onto the game bone's), parents every `WearBone` with `Worn` onto its game bone once, scaled by its `BodyMeasure` (`ShoulderSpan`, or one leg's or arm's two segments; a limb missing a bone falls back to the span), and keeps only the triangles skinned to the worn bones (`FollowParent`: hinged parts whose skin counts as their parent's). `Bones` must list `LeftUpperArm`, `RightUpperArm` and `Hips` (`worn: false` only lines them up). It is drawn once off screen and costs a fraction of a second, so build it while the player cannot act. `FirstPersonWear.Activate()` holds the vanilla parts in `Hide` (`BodyParts`, default head, torso, legs) off with `forceRenderingOff`; `Follow()` every late frame shows the model exactly while the game shows its arms; `Deactivate()`, `SetColor(property, colour)`, `Alive` (false once the game replaced its first-person body: build again), `Destroy()`.
  - *Pose copy.* `Models.ReadPoseTarget(root, skin, links, label)` once per model instance, `Models.CopyPose(animator, target, reach, label)` once per body, then `PoseCopy.Apply()` every frame after the game animated the body and solved its limb IK (a player's hands go on the item's grips in `PlayerSyncIK.UpdateIK`, from a game `LateUpdate` whose order against yours is not fixed). Each `PoseReach(upper, lower, end, anchor)` bends a two-bone limb after the turns so its end joint lands where the body's does, carried over from the anchor link (one anchor for both arms keeps both hands on the item); pass an empty array for turns only. Each `PoseLink` names a model bone, its body bone (with an alternative for an optional one) and its direction (toward a link or a body bone, or from a link), so different rest poses copy to the same pose; link 0 is the root, whose height and sway scale by root height. `Turn`, `Scale`, `Source(link)`, `PoseTarget.Bone(link)`, `PoseTarget.Rest()`, `Release()` (the animator's culling mode back).
  - *Held item copy.* `Models.MirrorHeld(agent, layer, label)`: `Refresh()` a few times a second, `Place(copy.Turn, copy.Source(hand), target.Bone(hand), copy.Scale)` every frame, `TryMuzzle`, `ShadowOnly`, `Hide`, `Destroy`.
  - *Clips on the player's body.* `Models.BodyClipRig(root, animated, "Hip", label)` once for a hidden model with the game's player bone names and your legacy clips; `Models.BindBodyClip(agent, rig, options)` once per player; `BodyClip.Pose(clip, time, position, rotation)` every frame it plays, after the game's animator; `Stop()`. `BodyClipOptions.PauseIk` (default on) turns FinalIK and `PlayerSyncIK` off while it plays and back as they were; `FirstPerson(on, camera)` lets the local player watch from inside (`FirstPersonHead`, `FirstPersonSkip` for renderers that would sit in front of the camera).
  ```csharp
  static readonly PoseLink[] Links = { new("pelvis", BodyBone.Hips, toward: 1), new("spine", BodyBone.Spine), /* ... */ };
  static readonly PoseReach[] Reach = { new(upperArmL, forearmL, handL, chest), new(upperArmR, forearmR, handR, chest) }; // link indices
  var target = Models.ReadPoseTarget(suit.transform, suitSkin, Links, "MyMod:suit");            // once per model
  var copy = target == null ? null : Models.CopyPose(agent.AnimatorBody, target, Reach, "MyMod:suit"); // once per body
  void LateUpdate() { if (copy != null && !copy.Apply()) copy = null; }
  ```
- **Glow.** `Glow.For(renderer)` copies its materials into glowing ones; `GlowHandle.Set(hdrColour)` every time it changes, `Dispose()` restores. `GlowSpot` is a soft dot on a transform: `Pulse` swells and fades it, `Show` holds it (`Shown` says whether it is lit), `Place` moves it to a world position, `Hide` and `Destroy` end it.
- **Effects by name.** `Fx.Play(handle, position, direction[, size, colour])` for one-shots, `Fx.Hold` for a sustained effect (a call near a held copy within its window moves it instead of starting another), `Fx.Line`/`Fx.Tracer` for line effects (a line effect registered with `HoldSeconds` is held: calling `Fx.Line` again moves and stretches the live copy), `Fx.Attach` for an effect riding a transform. `Fx.Register(bundle, item, options)` adds your own prefab effect under `Pack:item`. The effects of the bundle Forge ships are registered by the library itself as `Forge:forge_sparks`, `Forge:forge_explosion`, `Forge:forge_energy_beam`, `Forge:forge_flame` and so on (`Forge:forge_<name>`, 11 in all), so any mod plays them through `Fx` too.
- **Prefab effects you steer yourself.** `new FxInstancePool(name)`, then `pool.Take(model, tint)` gives a pooled copy of a registered prefab (a particle system at its root) that you keep: `Place`/`Stretch` it every frame, `Hold()` it, `Emit(prefix, on)` switches the layers whose name starts with the prefix, `PlayState(child)` plays an inactive state child, `Start()`/`Keep()` play it, `Release()` lets a held copy finish and go back to the pool. `pool.Prepare(model, count)` fills the pool one copy per step. The copy's Unity particle lights are removed; light it with `PointLights`. A bundle built with addressable names (prefabs listed without `.prefab`) registers its prefabs as models too.
- **Effect lights.** `PointLights` is the one pool of temporary lights for every mod: `Flash(position, spec, now)` for a blast or a muzzle, `Hold(ref handle, position, spec, now, strength)` every frame for a light on something burning or glowing (pass your own pulse or fade as `strength`), `Release(ref handle)` when done. They are the game's own `FX_PointLight`s: they cast no shadow, and the game's clustered renderer lights only through them (a Unity `Light` you add lights nothing). The budget is 32 for every mod together: a full pool gives a flash the flash nearest its end and never takes a held or leased light. `Place(position, spec, seconds, now)` leases a steady light (full intensity for `seconds`, no fade, not a flash, so the player's flash setting does not touch it) and returns `PointLightResult.Full` instead of taking another light when every slot is held or leased or the game has no free effect light; it is what an authored "light here for N seconds" step uses. `PointLightSpec` gives colour, range, peak, life, flicker and, with the six-argument constructor, `Blink` (blinks per second) for a warning lamp. The player's `Effects / FlashLights` turns flashes off; held lights stay. The library ticks them every frame; a mod whose effects move later in the frame may call `PointLights.Tick(Time.time)` again after they moved.
  ```csharp
  static readonly PointLightSpec Alarm = new(new Color(1f, 0.15f, 0.1f), 6f, 2.5f, 0f, 0f, blink: 2f);
  int _alarm;
  void Update() { if (alarmOn) PointLights.Hold(ref _alarm, lampPosition, Alarm, Time.time); else PointLights.Release(ref _alarm); }
  ```
- **Decals.** Marks on any surface (holes, scorches, blood, ground marks): flat quads lying on the surface normal, lifted 2 cm off it, each with a life and a fade-out at its end. `Decals.Shared` is one set of 128 for every mod, built during the elevator ride; `SpritePool.CreateDecals(capacity)` makes your own set (at load). A full set recycles its oldest mark. `Decals.Stick(from, direction, reach, out point, out normal)` finds the surface a shot hits. The game's own decal system draws only its own deferred-decal materials registered to its level culling, so it cannot take a mod's texture; these marks use your sheet.
  ```csharp
  var holes = SpriteSheet.Register("MyName.MyMod", "MyMod:holes", holeTexture, 2, 2, FxBlend.Alpha);
  if (Decals.Stick(muzzle, aim, 60f, out var point, out var normal))
      Decals.Shared?.Place(holes!, point, normal, 0.12f, Color.white, life: 30f, fade: 5f);
  ```
- **Impacts.** An `ImpactLook` (made once) says what a hit shows: any of an effect by name (`Fx`), a prefab effect (`Model`, from the library's own pool, filled ahead with `Impacts.Prepare(look, copies)`), a light flash (`Light`, `LightLift`), a mark (`Decal`, `DecalSize`, `DecalLife`, `DecalFade`, `DecalColour`) and a camera shake (`Shake`). `Impacts.Play(look, point, normal, scale, strength)` shows it, facing the normal. `Fx.Tracer` is the game's own bullet tracer.
  ```csharp
  var sparks = new ImpactLook { Model = Models.Get("MyMod:sparks"), Light = new PointLightSpec(new Color(1f, 0.7f, 0.4f), 2.5f, 2f, 0.12f, 0.3f), LightLift = 0.1f };
  foreach (var step in Impacts.Prepare(sparks, 8)) yield return step;          // at load
  Impacts.Play(sparks, hit.point, hit.normal);                                  // per hit
  ```
- **Camera shake.** `CameraShake.Shake(at, spec)` shakes the local player's view through the game's own `FPSCamera.Shake`: a `ShakeSpec` (amplitude, range, seconds, frequency, inner radius; make it once) falls off linearly from the inner radius to the range. `CameraShake.Kick(direction, amplitude, seconds)` is a short push of the view one way (a recoil, a blast from one side). Every shake is scaled by the player's `Camera / ShakeStrength` (0 turns them all off), and shakes running together never add up past `CameraShake.Cap`: a new one only fills what is left. Both return the amplitude that reached the camera.
  ```csharp
  static readonly ShakeSpec Blast = new(0.5f, 30f, seconds: 0.6f, frequency: 20f, inner: 4f);
  CameraShake.Shake(blastPosition, Blast);
  ```
- **Particles you built in Unity.** One prefab, world-space looping child systems with emission off. `ParticlePool.Register(bundle, item)`, then `Channel(child, maxParticles)` once per child; `Emit` single particles or `Burst` from the system's own shape. Give the prefab's `maxParticles` as the capacity (the game's interop does not expose it): when full, priority 0 is skipped and counted. `Look(texture, blend)` draws the system with ForgeVisual's particle shader.
- **Sprites configured in code.** `SpriteSheet.Register(modId, "Pack:sheet", texture, columns, rows, blend, sheddable)`, then `SpritePool.Emit(SpriteSpawn.Of(...))`. 256 sprites shared by every mod: a higher priority takes the oldest lower one, attached sprites (`Attach`, driven each frame with `Move`/`Resize`/`Recolour`/`Turn`; they do not age) are never taken, `SpriteSpawn.FlipPhase` starts a flipbook part-way along, and sheddable sprites fade when everything on screen covers more than five screens. Marks on surfaces are decals and light comes from `PointLights` (both above).
- **Outlines.** One line round the whole visible silhouette of an enemy or any object, none inside it, laid over the finished frame (colours shown exactly as given). `Outlines.Add(modId, target, colour, priority = 0, seconds = 0, xRay = false)` (a `GameObject`, or an `EnemyAgent`) returns an `OutlineHandle` (`Active`, `Remove()`; `seconds` 0 holds until removed, else it ends by itself). `Outlines.AddEnemyOutline(modId, name, style)` returns an `EnemyOutline` (`Active`, `Style`, `Remove()`) that outlines the enemies in sight by themselves: ForgeCore's `Enemies.Live` and `Enemies.Alert`, nearest first, coloured `Sleeping`, `Awake`, `Hunting` (`EnemyOutlineStyle`: `Range` 35 m, `FadeDistance` 5 m, `MaxEnemies` 24, `Width` 1.6 px at 1080p, `Priority` 0, `XRay` false). Several sources on one object: the highest priority draws, alone (an equal priority keeps the newer); an enemy given its own `Add` at a priority at or above the automatic outline's takes that outline's place. A limb shot off leaves the outline at once. `Outlines.Enabled` is the player's `Outlines / Enabled`; off, nothing is drawn. While one of the game's own screens is open (map, terminal, menu) nothing is drawn.
  - **X-ray is an opt-in, off by default.** Without it an outline is depth tested: behind a wall nothing shows, behind cover only the visible part. `xRay: true` (or `EnemyOutlineStyle.XRay = true`) draws the whole silhouette through walls; use it only where your mod means to show what is behind a wall. Forge Frames' cockpit does not use it: its enemy outline keeps the default style and outlines only enemies the camera sees.
  ```csharp
  var marked = Outlines.Add("MyName.MyMod", enemy, new OutlineColor(1f, 0.85f, 0.2f, 0.9f), priority: 10, seconds: 8f);  // in sight only
  var scan = Outlines.Add("MyName.MyMod", enemy, new OutlineColor(0.3f, 0.8f, 1f, 0.8f), seconds: 3f, xRay: true);       // through walls, on purpose
  ```- **Text and boards.** `WorldText.Create(parent, name, style)` is a hidden block of text that wraps at a fixed width and grows upward from its lower edge (`WorldTextStyle`: size, colour, alignment, pivot, line spacing, `SceneDepth` to be hidden by scenery); `SetShown`, `SetContent(text, lineWidth)` (returns the preferred size), `FaceCamera`, `Destroy`. `WorldBackdrop.TryCreate(parent, name, colour)` is a flat coloured rectangle behind text or an icon, sized with `Place` and recoloured in place with `SetColor`. `SignBoard.TryCreate(face, backdropName, width, height, backdropColour)` is a sign: a face with an optional backdrop and centred text filling it (`ShowText`). `ImageBoard.TryCreate(parent, name, layer, texture, width, height)` is a picture board, and `ImageTextures.TryGet(key, png, width, height, ...)` decodes and caches the PNG by your content hash (`Dispose` frees them). The `TryCreate` calls return null when the game has no `Sprites/Default` shader; you decide when to show and to whom.
- **Ground markers.** `GroundMarker.Place(centre, radius, colour, style)`; set `Remaining`, `Urgency`, `PingRate`, `Opacity`, call `Blink()` on a tick, `Move()` to follow a point, `Remove()` when done.
- **Beams.** `Beams.Create(colour, style)`, `Show(from, to)` every frame it moves, `Release()`. `Opacity`, `Width` and `FlareScale` scale the whole beam; `CoreLevel`, `GlowLevel`, `FlareLevel` and `GlowWidthScale` scale one part for a beam whose parts breathe differently; `BeamStyle.FlareLift` raises the flare above its point.
- **Trails.** `Trails.Rent(start, style)`, `Move(position, progress)` every frame, `End()`. A `TrailStyle` is a ribbon, an optional head flame and glow (your sheets) and puffs (your sheets or channels).

## From the old per-mod code

Each mod moves over in its own batch; until then its old code is unchanged.

| Old implementation | ForgeVisual |
| --- | --- |
| Ordnance `OrdnanceAssets` / `OrdnanceBundle` / `OrdnanceModel`, Frames `FrameAssets` | `Bundles.Register`, `BundleHandle`, `Models.Get`, `ModelHandle` |
| Arsenal `WeaponFit.FitToGear`, `HeavyWeapons.Skin`, Ordnance `BeaconLook` dressing | `Models.Dress`, `DressOptions`, `Dressed`, `Models.NativeBounds`, `Models.Find` |
| Frames `ViewShell`, `SuitSkeleton` / `PilotRig`, `HeldGun`, `PilotBody` bone matching and playback | `Models.WearFirstPerson` (`FirstPersonWear`), `Models.ReadPoseTarget` / `Models.CopyPose` (`PoseTarget`, `PoseCopy`), `Models.MirrorHeld` (`HeldMirror`), `Models.BodyClipRig` / `Models.BindBodyClip` (`BodyClipRig`, `BodyClip`) |
| Frames `FrameVisualGlow` | `Glow.For`, `GlowHandle` |
| Ordnance `OrdnanceFx.HandPulse` | `GlowSpot` |
| Arsenal `VanillaCombat.PlayExplosion`, Frames `FrameEffects.Tracer` / muzzle flash, ForgeEngine's former `GameEffectPlayer`, `ForgeEffectInstances` and `ForgeBundlePrefabs` (its effect card now calls `Fx` itself) | `Fx.Find("Game:...")` and `Fx.Find("Forge:...")`, `Fx.Play`, `Fx.Line`, `Fx.Hold`, `Fx.Tracer`, `Fx.Attach` |
| Arsenal `FxPool` / `FxEmit` primitives, `Settle` | `SpritePool`, `SpriteSpawn`, `SpriteSheet`, `DecalRing`, `SpritePool.Settle` |
| Arsenal `FxLight`, area and beam Unity `Light`s; the library's former two-light `FxLights`, ForgeEngine's former `TemporaryLights` | `PointLights` (`Flash`, `Hold`, `Place`), `PointLightSpec` |
| Ordnance `OrdnanceArrival.Shake`, `ShakeMath` and its `CameraShake` setting | `CameraShake.Shake`, `ShakeSpec`, the player's `Camera / ShakeStrength` |
| EnergyLab spark, disc-hit and blast one-shots with their light flash | `ImpactLook`, `Impacts.Play`, `Impacts.Prepare` |
| Arsenal `FxBudget` | the overdraw budget inside `SpritePool` (`sheddable` sheets) |
| Frames `FrameFx` systems, Ordnance `OrdnanceFx` slots, `FxRecipes.FallEmitter`, pod impact effects | `ParticlePool`, `ParticleChannel` (`Emit`, `Burst`, `Look`) |
| Ordnance `OrdnanceParticleAdditive.shader`, Arsenal `ForgeArsenalFx.shader`, Frames `ForgeFramesFx.shader` | ForgeVisual particle shader (`FxBlend.Alpha` / `FxBlend.Additive`) |
| Ordnance `BeaconFx` soft line and soft dot textures | `SpriteSheet.SoftLine` (`Forge:soft-line`), `SpriteSheet.SoftDot` (`Forge:soft-dot`) |
| Ordnance `Presentation` `GroundLay` + `OrdnanceMarker.shader`, Arsenal `StrikeEffects.GroundLay` + `ArsenalMarker.shader` | `GroundMarker` (`GroundMarkerStyle` Countdown, Hatched, Flames, Dial, Reticle) |
| Arsenal `StrikeEffects.Beam`, Ordnance beacon beam line renderers | `Beams.Create`, `Beam`, `BeamStyle` |
| Ordnance `BallTrail`, fall trail, Arsenal `FxEmit.TrailBegin/Move/End`, rocket tail | `Trails.Rent`, `Trail`, `TrailStyle`, `TrailPuff` |
| EnergyLab `VfxLibrary` / `VfxPool` / `VfxInstance` | `Bundles.RegisterBytes`, `Models.Get`, `FxInstancePool`, `FxInstance` |
| EnergyLab `EnergyLights` | `PointLights`, `PointLightSpec` |
| ForgeUI `ui.AddEnemyOutline`, `EnemyOutlineStyle` and its `EnemyOutline / Enabled` setting (removed there) | `Outlines.AddEnemyOutline`, `EnemyOutlineStyle`, the player's `Outlines / Enabled` (a new setting; the old one is not read) |

## Drawn ahead of first use

`ShaderWarm` (from `Scene.Tick`, once, at the elevator ride or the first level frame) draws the marker, particle (additive, alpha) and dome materials once with a temporary camera into a 16 x 16 texture: a shader's GPU programs are made at its first draw, not when the bundle loads. A new shader of the library belongs in that list. The shared decals (`Decals.Shared`) are built in the same step, so the first hit does not make their quads in its own frame. Decals and impacts draw with the particle shader, which is already in the list. The outline shader is warmed on its own, once a mod first uses outlines and the ride or level has started: it makes the screen-sized mask and draws its three passes (fill, fill through walls, composite) once.

## Logging

Logging goes through ForgeCore's `ModLog` and only real failures are written (`ErrorOnce` with a fixed key per kind of failure, `Error`); the outline code reports a failure once per key. Nothing is written for loads, clears or timing.

## Per-frame work

The library's updates run as ForgeCore `Frame.OnUpdate` and `Frame.OnLateUpdate` callbacks; it adds no component to the scene. `SpritePool` and `PointLights` return at once when nothing of theirs is alive.
