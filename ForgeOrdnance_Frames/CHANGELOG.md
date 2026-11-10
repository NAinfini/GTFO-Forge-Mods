# Changelog

## 1.0.1

Changes since the first upload. Version 1.0.1 because it is released early; it needs ForgeOrdnance 1.0.1.

- A suit request or rocket salvo made before a checkpoint reload or in an earlier boarding is no longer carried out afterwards, and a plant request no longer toggles. A player who joins later gets the suit's cooldowns as they stand, not as they stood when last sent.
- Suit effects now come from the ForgeVisual effect slots (`frame.wreck.smoke`, `frame.charge.dust` and the others) instead of an effects package built into Frames.
- Leaving a suit no longer shows the inside of the suit: the screen stays black until you have landed and the camera is back at your own eyes.
- Hits on the suit's shell now count for the local pilot, even when they land outside the body.
- A suit placed in the level is ready before you reach it, so the first boarding no longer stutters.

### Interface changes

- The built-in effects builder `FrameFxBuild` and `FrameFxPreview` are removed. Use the ForgeVisual effect slot table (`FxSlots`) to replace a suit effect.
- The public thrust, jet and heat helper functions in `FrameFx` (`ArrivalThrust`, `JetSpeed`, `JetSize`, `JetLife`, `HeatLevel`, `HeatInterval`, `ExplosionFlash`) and their constants are removed. Effects are driven by the slot table; there is no replacement.

## 1.0.0

First release.

- Four exosuits as Forge Ordnance call-ins: Sirius (assault), Taishan (tank), Thunderbird (firepower) and Nightowl (scout). Each drops through the ceiling and stands empty until someone boards it.
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
- English and Chinese.
- A lamp on the suit that your flashlight key switches on. Suit landings and steps wake sleeping enemies.
- Climbing out leaves the suit standing with its armor and ammo. Late joiners see suits already in the level.
- The climb-out, special and rocket keys are in the game's settings, Controls.
- Requires Forge Ordnance, ForgeCore, ForgeUI, ForgeAudio and ForgeVisual, which r2modman installs with it.
- The opening and closing play from the model's own keyed open clip, parts moving along their own hinges in order. The sounds and the steam at the seam follow the moments the parts actually unlatch, turn and come to rest.
