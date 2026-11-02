# Validation

## Latest run: log clean-up (2026-11-01)

Detached worktree on origin/main 89711d5ef, Forge-MapEditor-QA profile references (read-only), ForgeModsRoot at the main checkout; evidence level: build and offline checks only, no game.

- Removed SummonTrace (the throw-step recorder that only fed the 
ever appeared timeout line); the timeout error stays, without the step detail.
- Kept EmptyPackGuard: a resource pack entering the level with 0 ammo points breaks the 'a full pack is never empty' rule; it reports once (Plugin.Error keys by call site), naming the previous state, recall flag and position.
- Forge.Architecture.sln and ForgeOrdnance.csproj Release: 0 warnings, 0 errors. ForgeOrdnance core: 762 checks passed.

Not verified in game: everything below.
## Owner test list (in game, not run by any agent)

0. Call a supply pod, pick the packs up, swap and drop them, board and leave a suit; die and reload the checkpoint, then pick up, swap and drop again: every pack keeps its uses. The log may hold the game's own `self amanged replicators during play` and `CullBucket was null` lines per pod pickup; `Replication Key collision!` must not appear. A pack placed with no uses is an `EmptyPackGuard` error.

Level placement (Callable false, Drops, Place, DeclareLevel):
- Single player: a level whose `Drops` has a `Direct` `LevelStart` suit (a `Callable: false` definition) starts with the suit standing at the place; walk to it and board.
- `CallMenu: false` for the level: no ORDNANCE button in the lobby, no call list on the HUD, no call key; the picker never lists a `Callable: false` entry; another level without the switch is unchanged.
- Load a checkpoint: the level-start suit stands again at its place (a suit that was boarded is not boarded).
- `OrdnanceApi.Place` from a test plugin on the host: the call-in arrives at the point for everyone; on a client it returns false without a log line; a point in no area logs one error.
- A rundown whose `Settings.json` has a mistyped level key logs one warning naming the played level's key and the unmatched key on entering the level.

Regression of this run's replaced hooks:
1. Turret summon (host and client): the summoner's tool slot (class slot) is unchanged after the summon, a pick-up, a despawn/expiry and a checkpoint reload; a summoned turret never makes the HUD show "Deploy". No "changed the tool slot" error in the log.
2. Unpickable turret (`Pickable: false`): no pick-up prompt at any point, also after the sentry settles or after a checkpoint; a pickable turret still shows its prompt and can be taken.
3. Supply pod pickups: before the doors open nothing shows and nothing can be taken (check from the ground and from close range, host and client); the moment the doors start to open the packs show and can be taken. A pod that leaves or is cleared before opening does not leave shut packs behind.
4. Lobby rules: pick a different expedition in the lobby (host, then client). The ORDNANCE button, slot count and allowed call-ins follow within a moment on both sides; a client who joins after the host picked sees the host's rules (not WAIT) without anyone changing the selection again. If the client keeps WAIT until the host re-picks, `ExpeditionSelected` does not fire on the client at that moment and the poll must come back.
5. Heavy-weapon claiming from a pod (Arsenal): reserve, install, commit and cancel still find the claimant (the player lookup now comes from ForgeCore).
6. Log: after a full play session the ForgeOrdnance log holds no repeated line; any error shows once, then at its 10th and 100th occurrence.

Still open from earlier runs:
- Call-in flow: hold the call key (movement, firing and switching are off), enter a code with movement or arrow keys, throw the beacon, get the consumable and weapon back; a wrong step clears the code; the key is released after the throw, death or leaving the level.
- Notices (no code match, refused, ball failed, awaiting delivery) show under the call-in list for about 3.5 s and go away; a new notice replaces the old.
- Beacon marker: the ground ring stays whole on a step edge, over a low crate and on a slope; at a wall the part behind shows only as a dim dashed line near the mark; the rim empties clockwise as the countdown runs; the last three seconds speed up and turn red; the beam is visible far away without flicker and not glaring in a dark room; the label sits ahead of the beam and reads `NAME  4.2s`.
- Pod and shell timeline: landing at 1.6 s, doors at 5.0 s, host pickup spawn at 2.0 s after the call arrives; shell opens at 2.8 s, turret at 4.0 s; doors and petals move smoothly; sounds line up. Listen to every pod, beacon and turret cue a few times (each plays one of up to five variants). Camera shake follows ForgeVisual's `Camera / ShakeStrength` (0 stops it).
- Special ammunition pickups while a lent gun is in the special slot go to the player's own special weapon; the lent gun's counter is unchanged.
- Lobby: under the ORDNANCE button one row of cells as wide as the button, one cell per slot, picked cells drawn white with the beam colour in the corner, free slots a dim outline; each bar (own and others') shows that player's picks in slot order, updates within a moment, a late joiner sees everyone's picks and is seen, a player who leaves loses the row and gets it back after reconnecting, a player without the mod shows none, the host changing the level updates every row. The picker tabs sit above the list frame and the first row (EMPTY) is whole inside it at 1080p and 1440p.
- Changing the loadout in a level (CheatConsole): the next call uses the new loadout and the list refreshes at once; swapping a spent call-in out and back does not refresh its uses; changing picks during a call does not interrupt it.
- Chinese: with the game or `Language` in Chinese every text reads in Chinese and switching in a level updates the call-in list and hint line at once.
- The call-in list stands at the right edge, middle height, clear of the weapon list, ammo and flashlight.
