# Changelog

## 1.0.1

Changes since the first upload. Version 1.0.1 because it is released early; it still needs ForgeCore, ForgeUI, ForgeAudio and ForgeVisual 1.0.0.

- Turrets and devices now arrive by burrowing: the base drills in, the top half rises out of the opening, then the host spawns the sentry. New public `BurrowSpec`, `OrdnanceShellAppearance` with `IOrdnancePayload.GetShellAppearance` (default returns null), `OrdnanceDelivery.ShellRiseDelay` / `ShellReadyDelay`, `OrdnanceShell.Head` / `HeadRise`, and `TurretLookSpec.Burrow`.
- A turret that runs out of ammunition is recalled at once instead of sagging.
- Strikes show their real radius on the ground and warn players standing inside it. Walls now block gas, fire and stasis by line of sight.
- Hunter strikes that find no target search for 5 seconds, tell the caller and refund the use (`OrdnanceApi.RefundCall`, `PostNotice`).
- Supply pods can hold two heavy weapons per pod and leave once everything is taken.
- Host change: a sentry now keeps its strength and timer, and its shell and look stay bound to it; the new host takes running sentries over, and a pod still holding something to claim opens again. A checkpoint reload clears sentry state.
- A call-in's starting use count now travels with its slot, so a refund after a host change cannot exceed it. The slot record is 32 bytes; every player needs the same version.
- A saved loadout with an Id this install does not load is kept in the file and counts as empty, with no error line.
- The host's incoming call beams, warnings and strikes now show to every player in the level, even before that player's own call-in checks finish.
- A level with a bad number in its call-in settings now says which value is wrong instead of quietly using the default.

### Interface changes

- `OrdnanceApi.DeclareLevel` copies the `Overrides` of each drop, so changing the dictionary afterwards no longer changes the level.
- `OrdnanceDelivery.ShellContentsDelay(arrival)` is removed. Use `OrdnanceDelivery.ShellRiseDelay(arrival, burrow)` for when the top half rises and `ShellReadyDelay(arrival, burrow)` for when the turret may start.
- `OrdnanceDelivery.AddClaimable(summonerLookup, throwId, position, openAfter, spec)` and `OrdnanceDelivery.HostOpenClaim(summonerLookup, throwId)` gained a trailing `int bay = 0` parameter. Recompile; callers that pass the old arguments keep working at source level.
- `IOrdnancePayload` gained `GetShellAppearance(definition)` with a default body; implementers need no change.
- `IOrdnancePayload` gained `RefreshState(summonerLookup, throwId, tag, data)` with a default body (the kept bytes unchanged); implementers need no change. `OrdnanceApi.WorldEpoch` is new.
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
- Call-ins are `.forge` files (JSON inside). Level rules are set per expedition in `Settings.forge`.
- Two built-in call-in kinds: `Item` (pickups) and `Gear` (sentry turrets with tunable damage, fire rate, ammo, range and turning).
- Other mods can add new call-in kinds, custom drop pods and their own art through the public extension API.
- Extension API: add-ons register payload types with `RegisterPayload(type, payload)`. The special weapon slot lease is ForgeCore's `Gear.SpecialSlot`, shared by every mod that lends a gun into the slot. Multiplayer compatibility is ForgeCore's automatic message format fingerprint.
- Only faults are logged, and an exact repeat of a message is written once.
- Networking, the call key's input hold and logging go through ForgeCore (required, installed with it). Players with different call-in files or versions are refused on both sides and told in chat.
- Pods, strikes and exosuit landings shake the camera through ForgeVisual (its one setting `Camera / ShakeStrength`, 0 to 1).
- Add-on interfaces: claiming from a pod, the shared camera shake and container-free drops, turret look registration with turret events, pod bay fitting, and registering categories.
- Late joiners see beacons, pods and turrets already in the level. Checkpoint reloads and host changes are handled.
- Every text is in English and Chinese and follows ForgeUI's `Language` setting, which by default follows the game. Call-in packs can translate their names and descriptions with a `lang/` folder.
- The call key (rear mouse button by default) and the code keys (arrow keys) are changed in the game's settings, Controls; your movement keys also enter codes.
- Requires MTFO, ForgeUI, ForgeAudio and ForgeVisual, which r2modman installs with it.
