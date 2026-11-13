# Changelog

## 1.0.1

Changes since the first upload. Version 1.0.1 because it is released early; it still needs ForgeCore, ForgeUI, ForgeAudio and ForgeVisual 1.0.0.

ForgeOrdnance_Arsenal and ForgeOrdnance_Frames are now part of this package and are no longer updated. Uninstall both before you install it; their entries are under Arsenal and Exosuits below.

- Turrets and devices now arrive by burrowing: the base drills in, the top half rises out of the opening, then the host spawns the sentry. New public `BurrowSpec`, `OrdnanceShellAppearance` with `IOrdnancePayload.GetShellAppearance` (default returns null), `OrdnanceDelivery.ShellRiseDelay` / `ShellReadyDelay`, `OrdnanceShell.Head` / `HeadRise`, and `TurretLookSpec.Burrow`.
- A turret that runs out of ammunition is recalled at once instead of sagging.
- Strikes show their real radius on the ground and warn players standing inside it. Walls now block gas, fire and stasis by line of sight.
- Hunter strikes that find no target search for 5 seconds, tell the caller and refund the use (`OrdnanceApi.RefundCall`, `PostNotice`).
- Supply pods can hold two heavy weapons per pod and leave once everything is taken.
- Host change: a sentry now keeps its strength and timer, and its shell and look stay bound to it; the new host takes running sentries over, and a pod still holding something to claim opens again. A checkpoint reload clears sentry state.
- A call-in's starting use count now travels with its slot, so a refund after a host change cannot exceed it. The slot record is 32 bytes; every player needs the same version.
- The suits saved at a checkpoint now travel in the same checkpoint copy as the call-in uses and cooldowns; every player needs the same version.
- A saved loadout with an Id this install does not load is kept in the file and counts as empty, with no error line.
- The host's incoming call beams, warnings and strikes now show to every player in the level, even before that player's own call-in checks finish.
- A level with a bad number in its call-in settings now says which value is wrong instead of quietly using the default.

### Arsenal

- Strikes are rebuilt with a drop warning and fast fall; the hunter strike locks the largest enemy with a laser, and gas and fire only count half for teammates.
- Heavy weapons: grenades can deal true damage with an edge share, and the heavy weapons take a homing spec and a recoil scale. New: Recoilless Rifle and Tracking Rifle (five heavy weapons in all).
- New strike: Strafing Run (ten in all), and supplies grew to eight pods (adding syringe, mine and utility).
- New standing devices (the `arsenal.Deployable` kind): Foam Sprayer, Mine Layer, Scanner Tower and Floodlight Tower.
- Fixes: a late joiner or a new host now sees the mines still lying with the right definition; a pod's heavy weapons are racked for the right count; the heavy weapon caches no longer mix up two weapons that share a model; a recoilless rocket is drawn as a rocket on every peer; a Strafing Run hits each enemy once per pass.
- The grenade launcher's blast is now the shared heavy explosion from ForgeVisual, the same one the Blast Gun uses.
- Heavy weapons hold better: the left hand follows the game's own grip and is pulled back along the gun when it is out of reach.

### Exosuits

- A suit request or rocket salvo made before a checkpoint reload or in an earlier boarding is no longer carried out afterwards, and a plant request no longer toggles. A player who joins later gets the suit's cooldowns as they stand, not as they stood when last sent.
- Suit effects now come from the ForgeVisual effect slots (`frame.wreck.smoke`, `frame.charge.dust` and the others) instead of an effects package built into the suits' code.
- Leaving a suit no longer shows the inside of the suit: the screen stays black until you have landed and the camera is back at your own eyes.
- Hits on the suit's shell now count for the local pilot, even when they land outside the body.
- A suit placed in the level is ready before you reach it, so the first boarding no longer stutters.

### Interface changes

- `OrdnanceApi.DeclareLevel` copies the `Overrides` of each drop, so changing the dictionary afterwards no longer changes the level.
- `OrdnanceDelivery.ShellContentsDelay(arrival)` is removed. Use `OrdnanceDelivery.ShellRiseDelay(arrival, burrow)` for when the top half rises and `ShellReadyDelay(arrival, burrow)` for when the turret may start.
- `OrdnanceDelivery.AddClaimable(summonerLookup, throwId, position, openAfter, spec)` and `OrdnanceDelivery.HostOpenClaim(summonerLookup, throwId)` gained a trailing `int bay = 0` parameter. Recompile; callers that pass the old arguments keep working at source level.
- `IOrdnancePayload` gained `GetShellAppearance(definition)` with a default body; implementers need no change.
- `IOrdnancePayload` gained `RefreshState(summonerLookup, throwId, tag, data)` with a default body (the kept bytes unchanged); implementers need no change.
- Arsenal: `ClusterScatter` is removed. Cluster bomblet offsets are now computed inside the strike code and are not public; there is no replacement.
- Arsenal: `HeavyWeaponSpec.Restore(model, gearId, ammoRatio, grenade, tuning)` gained `HomingSpec? homing = null, float recoilScale = 1f`. Recompile callers.
- Arsenal: `GrenadeSpec(Damage, Radius, Speed)` gained `TrueDamage = false` and `EdgeShare = 0.5f`. Recompile callers.
- Arsenal: `ShellLook` gained `Precision`, `Hunter` and `Incendiary`; code that switches over it must handle them.
- Arsenal: the rack record (`RackBytes`, now 108) carries the pod's weapon count, the mine record carries its definition's hash, and the grenade burst record (41 bytes) says whether the round was a rocket. Every player needs the same version.
- Exosuits: the built-in effects builder `FrameFxBuild` and `FrameFxPreview` are removed. Use the ForgeVisual effect slot table (`FxSlots`) to replace a suit effect.
- Exosuits: the public thrust, jet and heat helper functions in `FrameFx` (`ArrivalThrust`, `JetSpeed`, `JetSize`, `JetLife`, `HeatLevel`, `HeatInterval`, `ExplosionFlash`) and their constants are removed. Effects are driven by the slot table; there is no replacement.

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

### Arsenal

- Five supply pods: mixed, ammo, medical, tool and consumables.
- Three sentry turrets: Watch (burst), Bulwark (sniper) and Scythe (shotgun), with their own models and sounds. They are stronger than the game's sentries and sink back underground when out of ammunition or after 180 seconds.
- Nine strikes: precision, area barrage, creeping barrage, cluster, incendiary, gas, stasis, cutting beam and hunter shell.
- The cutting beam only chases enemies it can walk to and see, and follows the path around corners.
- Stasis slows enemies inside its field and gives them back their exact speed when they leave.
- Three heavy weapons that arrive in a drop pod: a machine gun, an anti-materiel rifle and a grenade launcher. They take the place of your special weapon; the special weapon key swaps between the two with both keeping their ammunition. A spent heavy weapon is gone, packs never refill it (they refill your own special weapon), and G, going down or a checkpoint reload leave you with your own special weapon.
- Grenades are launched and settled by the host, so every player sees the same blast.
- Every call-in has its own beacon colour, picture, effects and sounds.
- The give-back key is in the game's settings, Controls.

### Exosuits

- Four exosuits as call-ins: Sirius (assault), Taishan (tank), Thunderbird (firepower) and Nightowl (scout). Each drops through the ceiling and stands empty until someone boards it.
- An empty suit shows its badge, name and armor 20 cm over its helmet in its glow colour. A worn suit carries its pilot's own name tag there instead, the name in the suit's colour behind its badge.
- Board from behind by holding the Use key. Only the back opens, along the armor's own seams: the back plates lift, then the rear plate of each arm and leg slides out and swings open on its own hinge; feet, hands and helmet stay shut. You step in, and the screen powers up. Hold G to climb out.
- The suit's gun takes your special weapon slot under the game gun's own name and hits harder. Press the special weapon key again to switch between it and your own special weapon. Its ammo is limited and nothing refills it; your own special weapon comes back when it runs dry or you climb out.
- Every gun you hold in a suit kicks less and reloads faster; your own guns keep their damage. Ammo refills only your own gear; medical packs heal you, never the suit.
- Every hit lands on the suit's armor. Armor never regenerates. At zero the suit is wrecked and you are forced out.
- Every suit walks, sprints and jumps further than you do on foot. Shift sprints until the suit overheats. Suits have no stamina.
- In first person you see the suit's own arms and legs holding your weapon. Other players see the suit instead of you, its hands on your item's grips like a player's.
- Sirius: a charge that breaks weak doors and shoves enemies aside.
- Taishan: an energy barrier that stops enemy attacks from outside (it is not drawn in the world; its bar sits on top of the armor bar), and heavy objects stowed on its back with V.
- Thunderbird: siege mode with higher damage, faster fire and reload and a planted barrier, plus four rockets (B) from a launcher set into the right shoulder: they leave its four tubes in turn with a muzzle flash, and the empty tubes reload one by one.
- Nightowl: marks every enemy within 30 m every 5 seconds.
- Inside a suit the normal HUD makes way for a cockpit: barrier, armor and health bars with a damage silhouette at the top, magazine and heat arcs beside the crosshair, objectives at the top left, the interaction prompt where the game puts its own and subtitles under the crosshair, the gun and its reserve at the bottom right, skills and teammates at the bottom left, chat moved to the bottom right. Nearby enemies show as a faint bearing ring and an outline, enemies you hit get a thin health bar with damage numbers and a kill mark, a banner warns while the pilot is grabbed or stunned, hits glow at the screen edge on their side, and a faint visor reddens when the suit overheats and cracks as its armor runs low. Everything comes back as it was when you climb out.
- The suit powers up with you: the visor slides in, each screen panel lights as a plate locks shut, the cockpit reports in part by part and the suit greets you. Move or fire to skip it.
- Idle suits and suits other players wear show their model and armor above them. The boarding prompt shows the suit's armor, and an arrow points you round to its back.
- A lamp on the suit that your flashlight key switches on. Suit landings and steps wake sleeping enemies.
- Climbing out leaves the suit standing with its armor and ammo. Late joiners see suits already in the level.
- The climb-out, special and rocket keys are in the game's settings, Controls.
- The opening and closing play from the model's own keyed open clip, parts moving along their own hinges in order. The sounds and the steam at the seam follow the moments the parts actually unlatch, turn and come to rest.
