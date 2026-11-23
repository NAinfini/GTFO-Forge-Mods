# Changelog

## 1.0.0

- First release. The shared Forge host every Forge package runs on: it loads the behaviour plans that levels, weapons and enemies ship, runs them on one common scheduler with shared state, results and budgets, keeps multiplayer sessions in step (the host runs plans, clients follow), carries the player, map, enemy and weapon behaviour (doors, terminals, objectives, alarms and waves, fixed items and enemies, enemy profiles, weapon and tool rules), exposes the SDK other mods build on, and ships the shared logic building blocks (conditions, comparisons and target filters such as area fields) that levels, weapons and enemies combine into their behaviours.

- Added: `ForgeEngine.Authoring.AuthorReads`, the Forge map reads the developer console calls by name in Authoring mode (plans, variables, generation progress, rooms, model shots, bot slots).
- Changed: the hook counters, the shared diagnostic time budget, drop timing, the generation attempt clock and the floor walk moved to Forge Core; Forge Engine uses them from there.
- Fixed: on a client, finishing a held button no longer waits one more round trip to the host.
- Fixed: on a client, what your own button press or your own shot on a map object shows you (a flash, a text, a marker) appears at once instead of after the host answers.
- Fixed: in a level with its own start landing point, each player now lands under their own elevator seat, around that point, instead of being pulled several metres to under the middle of the elevator. The elevator itself now stops further back, as it stands behind the seats in the game's own start rooms.
- Fixed: a player who joined a level under way could lose everything the level places (models, buttons, texts, trigger areas) when the connection to the host settled only after the level was built. The level now keeps what was placed and takes the host's world number.
- Logs: the host no longer writes a start elevator landing error for another player's seat exit that reaches it after its own elevator went back up.
- Fixed: an objective HSU placed in a zone that also has its own HSUs (a zone's HSU count above zero, as in the game's own levels) is now built at the point the level gives it, instead of being built elsewhere and moved there afterwards with an error in the log.
- Logs: a terminal or door the level itself leaves out of what maps can name (for example a terminal of a game level's zone that lists its terminals one by one) is now noted at info instead of written as an error. A terminal or door that cannot be read at all is still an error.
