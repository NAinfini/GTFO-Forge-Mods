# ForgeVisual latest validation

## Latest run

Detached worktree, BepInEx references from the Forge-MapEditor-QA profile (read-only). Evidence level: **Release build and offline tests, not in game**; frame time was not measured.

- Release build of ForgeVisual: 0 warnings, 0 errors.
- Offline: ForgeVisual core tests **235 passed, 0 failed**. The vfx bundle checks need local resources and were not run.

## To test in game

1. Frame callbacks: the library now updates through ForgeCore `Frame.OnUpdate` / `OnLateUpdate`. Outlines, effects, markers, beams, trails and glow spots behave as before.
2. Effect lights: with nothing alive `PointLights` and `SpritePool` return at once; flashes, held lights, blinking lamps and sprite effects still appear normally after an idle stretch.
3. Outlines, with Forge Frames piloting:
   - Enemies in sight get one line round their whole shape, none across a head or limb in front of the body; sleepers faint teal, awake amber, hunting red; past 35 m the line fades out.
   - No X-ray in the cockpit: an enemy behind a wall or closed door shows nothing; one half behind a crate is outlined only where it is visible.
   - Fog and dark: the line is clear and does not change with the flashlight. Width about 1.6 px at 1080p, scaled at 1440p and 4K.
   - Shot-off limbs: the line follows the body that is drawn, no head or hand outline left in the air; a dead enemy has no line.
   - Waking enemies (sleepers, ones that climb out of a wall or the ground): the line hugs the animated body for the whole animation, none at the spot it slept or spawned; `get_bounds_Injected` resolves (no `Outlines are off` error).
   - `Outlines / Enabled` off hides them at once; as a client they show the same; map, terminal and menu show none on top and play brings them back.
   - Frame time with 20-40 enemies, `Outlines / Enabled` on and off, both recorded; until then the cost is unmeasured.
4. Effect cards of ForgeEngine: the 28 vanilla and 11 Forge members play, and `point_light` (steady brightness, off at the end, an explicit failure when full).
5. Frames: the helmet lamp as a `GlowSpot`; the first beacon marker of a session draws without a hitch.
6. Models.Dress gun seating: hands on the grips, the magazine moves with the reload, no `visual-gun-*` errors.
7. Decals, impacts, shakes: the first hit that leaves a decal does not hitch; Arsenal strike areas and the cutting beam light the room through the game's own lights (compare brightness and colour; with extra lights off nothing lights); shakes as before at `Camera / ShakeStrength` 1 and none at 0, a salvo no harder than one big blast; EnergyLab sparks, disc hits and the blast gun look and light as before; `Effects / FlashLights` off stops flashes and keeps held lights; after a level change no decal, light or shake is left.
8. Released beams (a beacon's and the cutting beam's columns) disappear at arrival, after a level clear and after the host removes them.
9. Not yet seen in game at all: model dressing and gear-shader materials, glow, every `Game:` effect, particle channels, sprites and the overdraw fade, ground markers, trails, the level-change clearing, EnergyLab's pooled effects and lights (strafing); the body mechanisms (first-person wear, pose copy, held item copy, clips on the body).