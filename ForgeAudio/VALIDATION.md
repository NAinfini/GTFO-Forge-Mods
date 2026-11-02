# ForgeAudio latest validation

## Latest run

Detached worktree, BepInEx references from the Forge-MapEditor-QA profile (read-only). Evidence level: **Release build and offline tests, not in game**; frame time was not measured.

- Release build of ForgeAudio: 0 warnings, 0 errors.
- Offline: ForgeAudio core tests **483 passed, 0 failed**.

## To test in game

1. The game starts to the main menu; Settings, Audio shows under the game's sliders a **MOD AUDIO** heading and ten sliders (ForgeUI draws them). Dragging and the mouse wheel move the number and a mod's sound gets quieter or louder at once; the values survive a restart (`NAinfini.ForgeAudio.cfg`); the page's Reset puts them back to 100. The game's own audio rows are intact and the Controls key rows still work.
2. Entering a level from the menu: the listener and the mod sounds (Ordnance, Arsenal, Frames, EnergyLab) play normally after the menus stopped looking up camera, player and listener every frame.
3. Weapon actions of a claimed weapon: one start sound and one end sound per reload and one click per trigger pull on an empty magazine (Frames suit gun); the pilot's own gun plays none; after leaving the suit and in the next level no suit cue on any weapon; with no claim no weapon patch is on.
4. Body sounds: a claimed body's footsteps, jump and landing follow the claim; unclaimed bodies sound as before.
5. A sound at a point in another dimension plays with that dimension's filtering.
6. Voice slot, `Sustain` and the prewarm in play: first real play without a start hitch, loops resume after level exit, checkpoint reload and device change.
7. Leaving the game writes no `WrongThread` line.
8. Wall and door muffling, category limits and cross-mod ducking by ear; the example in `DEVELOPERS.md` compiled as a separate plugin.