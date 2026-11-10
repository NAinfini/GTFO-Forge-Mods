# Changelog

## 1.0.0

- Recording: players, enemies, damage, shots, melee, doors, scans, alarms, waves, terminals, generators, items, checkpoints, chat, noise, pings, sentries, mines, tongues and projectiles, in a crash-safe block file (format version 9) with the level seeds and the map fingerprint in its header.
- Models other mods showed (suits, turret looks, pods) and the renderers they hid are kept in every keyframe, so dragging the timeline builds them again instead of losing them; a vanilla sentry covered by a turret look is covered in the replay as well. Recordings made before this still play, but their models do not survive a jump.
- A recording stores the mode an item was spawned in; playback puts only pickups on the ground, so an item taken in hand is no longer shown lying in the level. A recording without the mode plays as before.
- Replays list in the main menu: the 20 newest recordings and every starred one, with result, length and team, and a short reason on every file that cannot be read (other format, damaged file). A recording made with another game version, other data or other mods still plays: what this game cannot restore is skipped and counted on screen.
- Playback: the level is rebuilt from the recorded seeds; players and enemies are puppets driven by the recording; the game's own sounds and effects are triggered again by the recorded events.
- Time bar with event marks, 0.25x to 8x (also the up and down keys), jumps, previous and next mark (Shift and an arrow) and frame steps; automatic, follow (circled by the mouse), first-person, free (speed on the wheel) and overhead cameras with a glide between views; player cards (downed and grabbed), kill feed with the enemies' common names, chat log, statistics (exact, estimated or empty, top weapon and enemy) beside the cards or as a Tab scoreboard, enemy outlines by alertness, teammate outlines, clear fog and brighten; every colour read from the game's own HUD and menu.
- Stats: a Rating page gives each player a grade from Output, Kills, Accuracy, Survival, Support and Mission, with a radar chart and an overall grade, and the replay list and a run's details show the grades. `~` marks an estimate and grades run from D- to S+.
- When the host also runs Forge Replay and is recording, the hits, kills, packs, supplies and hit sources its game settles are copied to clients that run it, so their statistics are exact for every player. Without the host's records the same numbers stay estimates.
- First person is now the recorded player's own eye, stance and look, including aiming down sights without the weapon dropping out of the sight.
- The enemy view's health line no longer runs out of its plate.
- The replay list refreshes when a recording finishes, is starred or is deleted.
- Fixes: a fourth player taking a pack no longer leaves a weapon showing.
- Objects hidden inside a shut supply pod and enemies in another dimension now replay in the right place.
- Scan clusters and fog turbines hidden from view restore again on a jump.
- A jump no longer leaves a corpse decaying.
- Item icons wait for the game to build the item's model instead of staying blank.
