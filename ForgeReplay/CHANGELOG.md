# Changelog

## 1.0.3

Needs ForgeCore 1.0.4.

- Fixed: pausing a replay and then moving the free camera no longer freezes the game after a few seconds. The game's sound stays silent while the replay is paused.

## 1.0.2

Needs ForgeCore 1.0.3.

- Recordings are stored in a newer format (12) so a replay can copy a worn model's pose from the right player. This version cannot read recordings made with 1.0.1; they are listed under "Unreadable files" in the replays list.
- The stand-in is built from the game's own player body: it wears the recorded clothing and face, a ForgeSkin character skin and gun skins when the recording has them (and the viewer has those packs), and holds its weapon the way the game holds it for a teammate, with both hands on the gun and aiming where the player aimed. A look that is still loading is asked for again until it is ready; a look that cannot be worn falls back to the original.
- The stand-in holds the same item the recorded player held (guns, resource packs, consumables, carried objects), made through the game's own item spawn, so what other mods put on that item shows too. An exosuit shows the weapon in its hand.
- The outline of a watched player covers the whole character model and follows a change of look.
- Recordings also carry the pickups lying in the level when recording starts and objects other Forge mods place in the level (their position, size, visibility and state), so a replay shows them where they were.
- The terminal panel in a replay is laid out like the game's: command lists in two aligned columns, long lines wrapped instead of cut, the newest line at the bottom, each line recorded once.
- A recording that cannot be played is dimmed in the list with "UNAVAILABLE", and under the play button it says what is missing and what to do (the rundown not installed, the level missing, a file that cannot be read).
- Fixed: after a player left an exosuit, their character stayed hidden for the rest of the replay. It is shown again when the suit is let go.
- Fixed: dragging the time bar back no longer puts items that only appear later back into the level, and a resource pack no longer sits at the map origin before it is spawned.
- Fixed: the rating table showed every number in white. The scores are named Damage, Kills, Accuracy, Survival, Support, Objective and Overall, each with a hover explanation.
- Fixed: the View menu, the centred score board, the key hints, the mission card and the hold-Esc bar now own the mouse: the player cards, panels and buttons under them no longer light up or get clicked.
- Changed: shorter key names in the key settings, long explanations moved into hover tips; the "cannot play in a level" hint is shorter.
- Changed: less work every frame while recording (enemy sampling, empty-table early outs); a new icon.
## 1.0.1

- Recordings are stored in a new way: they are much smaller and get on better with other mods. Because of this change, this version cannot read the old way of storing them, so recordings made with 1.0.0 cannot be played in this version; they are listed under "Unreadable files" in the replays list.
- Needs ForgeCore 1.0.1.
- Every player, yourself included, is played back by the same stand-in body, which now replays the recorded body animation layers (stance, movement, interactions, throws, melee, shooting).
- The Chase camera no longer turns with the mouse: it stays behind the player and looks where they looked. Orbit still circles the player with the mouse.
- Replay screen and replays list redesigned: four fixed player cards, collapsed to a narrow strip at the right edge by default and opened on mouse-over; one bottom panel with the time bar and play controls; list filters as tabs (result, rundown, unreadable files) with the total size.
- The events page lists wake-ups: who woke which enemy and how.
- Closing a recording no longer holds up the end of the level: pruning and reading the finished file run in the background. Stopping a replay stops its background analysis.
- Multiplayer goes through ForgeCore's networking.
- Replay screen: the view buttons and the watched player's name, weapon and health moved from the top middle to the row above the time bar, between the panel tabs and the key hints.
- Replays list: a row no longer carries the players' names and grades (they were crowding the next row); they are in the details' team table, in each player's colour.
- Added: `Recording.Folder` chooses where recordings are kept (empty: `replay` beside the game); recordings already in the old place are not moved. The rating cache moved to `BepInEx/cache/ForgeReplay` (rebuilt on its own).

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
