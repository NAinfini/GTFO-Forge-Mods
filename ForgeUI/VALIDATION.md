# Validation

## Latest run

- Release build with the Forge-MapEditor-QA BepInEx profile: 0 warnings, 0 errors.
- Offline tests: 883 passed, 0 failed (with GTFOBepInExPath set; 873 without, the patch-target checks then need local resources and do not run).
- The HUD screen's early 'could not be measured yet' error is removed (a transient start-up state, not a fault); the 'No camera draws the HUD layer' error stays.
- Not run in the game. No frame time measured.

The look, the patch mounting, the Frame callbacks and the screen effects are engine code with no offline test.

## Owner checklist (in game)

Patches and registration

1. ForgeUI patches mount on first use. Open the main menu, then a level: the menu bar buttons and the Settings sliders and key rows from other mods appear even when those mods register after ForgeUI has started.
2. The Settings, Audio page shows the MOD AUDIO sliders and the key page shows the mods' key rows; Reset puts the sliders back to full.

HUD and callbacks

3. The HUD, chat plate and enemy outlines run on the Frame callbacks: nothing flickers or lags a frame behind the camera, and in menus nothing runs.
4. Piloting a suit: the cockpit hides the game's HUD pieces and gives them back on leaving; the chat uses our plate while piloting (typed line bright, names in player colours, no overlap at 1080p and 4K) and is the game's again on foot.
5. Backpack and objectives readouts: the first read after a level start is complete (no empty or stale rows); they follow later changes.
6. Overlays, pieces, fonts and icons look right at 1080p and 4K; mod icons (for example the Frames pack) draw in their own colours.
7. Health bars, hit feedback, waypoints, confirm prompts, tooltips and teammate tags draw and clear correctly.
8. Screen effects fed by HitEdge and Visor stack without overlapping and fade out.
9. Cockpit parts power on with brackets, a sweep and typed text and settle without flicker; the call-in list and the inventory stand together at the bottom right, also while typing a code.
10. World markers (suit name plates, call-in countdown labels) have no soft double.
11. Language switch: all drawn text follows the game's language.

Menu screens

12. Menu screens open and close in place, the pointer works on them, and their timelines play and rewind smoothly.
13. Lobby: the ORDNANCE button and the loadout cells show no piece of the game's APPAREL look; hover lightens the button.
14. Picker: tabs, slot tiles, rows and details draw over the game's pieces; scrolling cuts rows at the window edge; readouts show seconds and the infinity sign.

Performance

15. Compare frame rate on foot and piloting with the HUD on.
16. Skill slots in every package (Frames, Ordnance): the key sits inside the plate and the name in small text under it.
17. A prompt with AtGamePrompt (the Frames cockpit) stands at the height of the game's own prompt, in the same place with and without the cockpit, also after a resolution change.
