# Changelog

## 1.0.0

- An action state is stamped with the world epoch (the channel follows the world), so one a player sent in an earlier level or before a checkpoint reload no longer shows up afterwards.

- Action wheel on `H`: hold to open, release to choose. The key, how it fires and how long a long press lasts are in the game's key settings; the game's `Q` menu is not touched.
- Twenty-two actions (the last two, handshake and fist bump, need a teammate within 1.5 m): come here, stop, go there, quiet, wait, follow me, cover me, 3 2 1, middle finger, wave, cheer, thumbs up, pray, backflip, shrug on the ground, convulse on the ground, dance, twerk, hip hop, zombie stumble, handshake, fist bump.
- The three dances with music and the cheer a whistle, heard from the player in time with the action (ForgeAudio).
- The dance moves the view behind you; tactical signals and gestures stay in first person (gestures can be switched), and every action has first-person arms of its own.
- Teammates with the mod see the same action, including one already in progress when they join, and see it again when you repeat it.
- Dancing next to a teammate (8 m) who dances the same one joins their beat.
- Middle-click a wheel slot to favourite it; the first page holds favourites and the last used. Gestures can optionally use the view from behind (`View.ThirdPersonGestures`).
- Two-player actions (`Pair` in a definition): your hand goes out and stays out until a teammate within 1.5 m answers with the interaction key (or by choosing the same slot, which glows for them); they step up to arm's length, you face each other and play it together. Unanswered actions cancel after 15 seconds; moving or any action key cancels it for both.
- A player who is still joining is no longer reported as not having the mod: the log waits a minute for their answer first.
- Other mods can add actions with a definition file or `Emotes.Register`.
