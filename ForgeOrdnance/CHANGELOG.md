# Changelog

## 1.0.0

First release.

- Call-ins by code: hold the call key, enter a code with your movement keys or the arrow keys, throw the beacon, and the call-in arrives where it lands.
- Loadouts are picked in the lobby with an ORDNANCE button under APPAREL and saved on your computer. The level can set the number of slots and limit which call-ins are offered.
- Every lobby player bar shows the call-ins that player picked (frames, Arsenal weapons, strikes, supplies, turrets), updated as they change their picks. Call-in details in the picker can show rating bars (`Stats`).
- A call-in list at the right edge of the screen shows your call-ins with uses left or cooldown, and opens to every code while you hold the call key. The beacon never shows up in your weapon list.
- The summoning beacon is a throwable item. Other players see it fly, and you get your own consumable and weapon back afterwards.
- The host checks every call: loadout, uses, cooldown, throw range and standable ground. Each player has their own cooldowns and uses.
- Supplies arrive in a drop pod with opening doors. Turrets arrive in a shell that falls open around them. Both have their own effects and sounds.
- The beacon shows a beam and a countdown label. Arrival, landing and door effects use pooled particles and add no network traffic.
- Call-ins are JSON files. Level rules are set per expedition in `Settings.json`.
- Two built-in call-in kinds: `Item` (pickups) and `Gear` (sentry turrets with tunable damage, fire rate, ammo, range and turning).
- Other mods can add new call-in kinds, custom drop pods and their own art through the public extension API.
- Extension API: add-ons register payload types with `RegisterPayload(type, payload)`. The special weapon slot lease is ForgeCore's `Gear.SpecialSlot`, shared by every mod that lends a gun into the slot. Multiplayer compatibility is ForgeCore's automatic message format fingerprint.
- Only faults are logged, each place at most on its 1st, 10th, 100th occurrence.
- Networking, the call key's input hold and logging go through ForgeCore (required, installed with it). Players with different call-in files or versions are refused on both sides and told in chat.
- Pods, strikes and exosuit landings shake the camera through ForgeVisual (its one setting `Camera / ShakeStrength`, 0 to 1).
- Add-on interfaces: claiming from a pod, the shared camera shake and container-free drops, turret look registration with turret events, pod bay fitting, and registering categories.
- Late joiners see beacons, pods and turrets already in the level. Checkpoint reloads and host changes are handled.
- Every text is in English and Chinese and follows ForgeUI's `Language` setting, which by default follows the game. Call-in packs can translate their names and descriptions with a `lang/` folder.
- The call key (rear mouse button by default) and the code keys (arrow keys) are changed in the game's settings, Controls; your movement keys also enter codes.
- Requires MTFO, ForgeUI, ForgeAudio and ForgeVisual, which r2modman installs with it.
