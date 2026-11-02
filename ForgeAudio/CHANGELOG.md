# Changelog

## 0.1.0

First release.

- Drop `.ogg` files in an `audio` folder next to your DLL, call `AudioKit.Load` once and play cues by name.
- 2D, positioned and following playback. Sounds follow the game's volume sliders and mute in the background.
- One shared mix for every mod: nine categories (`ui`, `weapon`, `explosion`, `debris`, `ambience`, `voice`, `vehicle`, `device`, `music`), each with one voice limit over all mods. A stronger or nearer sound pushes out the weakest one.
- `Volume / Master` and one volume per category, as sliders under the game's own on its Settings, Audio page (and in the kit's config). The page's Reset puts them back to full.
- Buses group cues. A bus can limit how many of its sounds play at once and can quiet other categories while it plays.
- Cues are layered mixes: several clips with offsets, gains, ranges, fades, pitch, low-pass and distance ranges, mixed once at load so a cue costs one voice. A length cap fades the mix out.
- Variants: each play picks one clip from a layer's pool. A random play never repeats the cue's previous variant when it has two or more. An explicit `PlayOptions.Variant` plays as given.
- A cue's `gain` or `gainDb` can be a `[min, max]` range: each play picks a volume, evenly in decibels.
- `AudioLibrary.Cue` finds a cue once; `AudioCue.PlayEverywhere` plays it for every player at the same point with the same variant, pitch and volume, through the kit's own network channel. Later joiners do not hear earlier plays; loops are refused.
- Space settings per cue: placement, near and far distance, linear, log or point curves, blend, spread, doppler. A distance-limited layer can replace the near mix with a far one.
- `Sustain` keeps a loop on an object playing through level changes, checkpoint reloads and device changes.
- Budgeted wall and door muffling, and automatic dimension from the sound's position; a followed sound whose object teleports or warps takes the new dimension, and a spectating listener hears the dimension of the player watched.
- A followed object that is destroyed fades its sound out in 150 ms.
- Failures are explicit and name the mod and cue. The shared log holds real failures only, and a repeated failure is written once per key.
- Ogg Vorbis decoding with NVorbis in bounded time, with sample-exact loops. A broken file only silences the cues that use it.
- `AudioKit.PostGameEvent` plays one of the game's own sound events at a point, by event ID or by `AK.EVENTS` name, through the game's own mix, so mods never call the game's sound code.
- `AudioKit.ClaimBodySounds` replaces or silence one player's own footsteps, jump and landing sounds, with priorities between mods and automatic release at level exit; unclaimed players sound as before.
- `AudioKit.ClaimWeaponActions` lets a mod play its own cue on a weapon's reload start, reload end and empty-magazine click, by weapon instance, with the same priorities and level-exit release.
- Author tools: convert a folder of WAV, MP3 or FLAC files to Ogg Vorbis, validate a folder exactly as the game loads it, and render a cue's preview at any distance.
- Voice slot (`library.Voice`), cue check (`library.Require`), silent prewarm (`library.Prewarm`), `Play` and `Sustain` on a game object, `DimensionAt(position, preferLocalPlayer)`.
