# Changelog

## 1.0.3

Needs ForgeCore 1.0.4.

- Fixed: a skin no longer covers a free-standing holographic sight with an opaque lens; a vanilla lens only takes the skin's reticle, so you see through it again.
- Changed: changing skins stutters less: models and skin meshes are built over several frames, and skins are not released and rebuilt inside a level.
- Changed: there are no skins for every gear any more. A sight, magazine, light, lens or ammo change belongs to one gun's skin and shows on that gun alone; the same sight on another gun keeps its own look. A pack an older editor made for no gear, and an `active.json` that still lists `global` skins, are not worn and say so once in the log (`skin-global-unsupported`): open the work in the editor and make it a gun's skin. Nothing changes in what teammates exchange: such skins were never shared, so players on this version and on 1.0.2 still see each other's skins as before.
- Logs: a `forgeskin.forge` placed in `BepInEx/plugins` itself or deeper than the mod's own folder (a mod zip unpacked with its top folder), and a skin pack lying in the plugins folder without a marker that lists it, are now named in the log with where they belong (`skin-pack-misplaced`) instead of being skipped without a word.
- Logs: a teammate wearing a skin you don't have installed (`skin-look-missing`) is now a warning instead of an error, and the log names the player instead of an ID number.

## Editor 1.1.7

Not a plugin version: the editor's own release on GitHub. Comes with the ForgeSkin plugin 1.0.3 and ForgeCore 1.0.4 (the same files as on Thunderstore) and installs them into your profile.

- Changed: sights, magazines, lights, lenses and ammo are no longer offered as targets of their own; change them in the skin of the gun that carries them. Building a skin that names no gun, tool or item is refused with a message saying so.
- New: opening a work an older editor made for a sight, magazine, light, lens or ammo alone asks for the gun it goes on. The material changes that gun has are kept, attachments and part changes on a part it has too, and everything that does not fit is listed; the work moves to that gun and the old file goes to the Recycle Bin.
- Changed: a Skins export leaves out a pack that names no gun and says so in its report.

## Editor 1.1.6

Not a plugin version: the editor's own release on GitHub. Comes with the ForgeSkin plugin 1.0.2 and ForgeCore 1.0.3 and installs them into your profile.

- New: replace a part. The first click on a part lists the pieces that can be swapped and a command to replace the whole part; what cannot be swapped is greyed out with the reason.
- New: the gun light's beam point is read from the game and can be dragged in the 3D view; it is saved with the part layout.
- New: swap a whole gun for one imported model, and a Sight page for the lens, reticle, magnification and aiming.
- New: installing the plugin keeps one copy each of ForgeCore and ForgeSkin where the mod manager keeps them; other copies go to the recycle bin with a notice. After installing, the editor reads the game's log and tells you in one sentence why a plugin was refused. A development build builds ForgeCore from source and no longer needs a ForgeCore package.
- Changed: no internal names are shown to the author (part ids, model file names, unnamed pieces are called by part name and number); long explanations move behind an i icon and empty lists say one line; the works list no longer cuts titles; the first tutorial picks the gear and creates the skin in one step and the left column goes straight to Parts or Steps after creating a skin or importing a character; long folder paths in Settings are cut in the middle; three English button labels that were cut off are shorter; the character view's action buttons sit at the bottom like the weapon view's.
- Fixed: importing a model onto the Buckland SBS III or the PDW (any part) turned the whole window black. These guns have two pieces of one name in a part, which the mount list showed twice; only the first of them can be moved, as in game, so the second is no longer listed.
- Fixed: undoing or redoing a charm out of the scene no longer fills the console with an error every frame.

## 1.0.2

Needs ForgeCore 1.0.3. In multiplayer the host and every client should update ForgeCore.

- Changed: a gun with a charm costs less each frame: the rope's mesh is written to the game in one copy instead of two calls into the game per vertex, and the cord's round sections are not recomputed with sine and cosine every frame. The charm moves as before.
- Fixed: a gun built while the game was drawing the weapon picture of the same gun could be taken for the picture's gun and miss its skin. The picture's gun is now told apart exactly.
- Logs: a character model with no bone for the upper chest or a shoulder, which many skeletons (most MMD models among them) do not have, no longer writes a warning; the bone above carries those parts, and the log notes it at info.
- New: skins for your bots. The host sees a SKINS button on each bot's bar and picks the character and the skin of each of its guns; everyone in the lobby sees the bot dressed that way.
- New: characters for other players, on your screen only. With a character pack installed, each other player's bar has a SKINS button where you pick the character you see them in. You see the character they picked themselves; if they picked none or do not have ForgeSkin, the one you chose for that player's slot; if you chose none, the original. The choice is kept on this machine by slot. Nothing is sent to anyone.
- New: hand shape and fingers. A gun skin can choose how each hand holds that gun (one of the game's own grips), and bend every finger joint by hand in the editor. The hand shape shows on your hands in first person; the finger bends show there and on your body in third person. A skin that sets them needs ForgeSkin 1.0.2 or later.
- New: character models that are not a whole human. A creature without fingers, with one arm or leg, or with unlike sides now works as a character skin: only its hips need a bone. A part without a bone keeps its shape and moves with the part above it, a hand whose arm is incomplete does not reach for the gun, and a model without both upper arms shows the game's own arms in first person. A pack made this way needs ForgeSkin 1.0.2 or later.
- New: sight reticles. A gun skin can replace the reticle in a holographic sight's lens, up to three picture layers, each with its own colour, strength, size, depth and on or off. Only you see it, when you aim down the sight. A skin with a reticle needs ForgeSkin 1.0.2 or later.
- New: inspect your weapon. Press the inspect key (I by default; change it under Forge mods on the game's key settings page) with a gun in your hands, skinned or not, to turn it over and look at it, then bring it back. Each kind of gun has several motions and each press plays one of them at random, never the same one twice in a row: pistols are turned over, or tossed up end over end, hanging a moment at the top, and caught with a little give in the hand; pistols, revolvers and compact guns also dip the muzzle to show the sights, and pistols and revolvers turn to show the muzzle at a slant; a gun with a magazine can have it slip out a little and get slapped home; the Shelling Arid 5 and S49 also drop the magazine into the other hand and bring it up before your eyes with its top round toward you, or pull the slide back a little to see the round in the chamber; revolvers spin twice on the finger, and the Mastaba R66 swings its cylinder out and spins it while the Bataldo 3RB breaks open, the other hand pushing from under the cylinder and letting go before it spins, each snapped shut with a flick of the wrist; rifles, shotguns, machine guns and snipers are turned in both hands, or held by the stock, panned across, tossed straight up and rolled over once, then turned to their other side; rifles and shotguns also pull the magazine out to look at it and slap it back, pull the charging handle back to show the round in the chamber, or are turned to look along the barrel, and pump shotguns rack the pump; machine guns drop the box magazine into the other hand and slap it back, pull the charging handle, or are panned slowly across the view; snipers pull the bolt back to show the round, look at the magazine, or bring the scope up close. The other hand's fingers pinch the slide, push the cylinder with the thumb, close round the magazine and slap it flat. A skin that hides a part a motion moves (the magazine, the slide, the cylinder) leaves that motion out for that gun. Firing, aiming, reloading, melee, using, sprinting, opening a menu or being hit ends it, and nothing you press is held back. Teammates see your wrist turn the gun. Known issue: rifles, shotguns, machine guns and snipers are held badly while their right side is shown; this is fixed in a later version.
- New: swap a whole gun. A skin can replace the entire gun with one imported model: the game's own parts are hidden and the four hold points are placed by rule. A skin that does this needs ForgeSkin 1.0.2 or later.
- New: sights. A gun skin can set a sight's lens (an imported mesh or a flat plane, in the look of a vanilla sight), its reticle and its magnification, which applies in your first-person view when you aim. A reticle layer that is switched off is now hidden in the lens instead of left faintly visible. A skin with a sight needs ForgeSkin 1.0.2 or later.
- New: the gun light follows the model. When a skin replaces the part the gun light sits on, the beam comes out of the light point the model gives it.
- New: skins can bring their own inspect moves. A skin pack can add inspect moves for its gun (or for every gun of a type), which join that gun's own moves while you wear the skin. Teammates with the same pack see them too. A pack whose inspect moves are broken is not loaded, and the log says why.
- Fixed: character models are now drawn with Forge's own shader, which fixes the strange glow when moving fast or inspecting a gun (Tda Miku Append TdaStyle's first-person arms took on the colour of the gun). Needs ForgeCore 1.0.3; the packs need no update. A skin pack whose metallic, smoothness or glow strength is not a finite number is now refused when it loads instead of reaching the shader.
- Fixed: the inspect key no longer needs a second press. A press made while the gun was not quite ready (just after a reload, a swap or a shot) is kept for half a second and plays as soon as the gun is free; one that never could is written to the log with the reason. Inspecting works while crouching too; when a press is refused, the log now lists why. A motion that cannot be framed for a gun in your hands (it would leave the gun out of view or bring it into your face) is dropped for that gun and another one plays on the same press, instead of nothing happening.
- Fixed: after joining a lobby that others were already in, everyone in it, you included, stood in their original look until someone changed a skin. Character skins now go on as soon as each figure appears.
- Fixed: on the loadout page opened from the menu during a level, your figure and your teammates' stood in their original look until you picked another character and back. The figures keep their characters while the page is closed, so they show them as soon as it opens.
- Changed: inspect feels like a hand holding a gun: every move has a little wind-up, follow-through and settle, the wrist stays inside what a wrist can do, and the other hand's fingers rest against the gun instead of going through it. Every kind of gun has three general moves plus its own, and melee weapons can be inspected too.
- Changed: inspect no longer jerks: finger movement is smoothed and played at 120 frames a second, a hold that cannot close around the gun is replaced for that part by the plain version (a line in the log), and the moves are worked out in the background once you pick the gun up, so the key plays at once.
- Changed: inspect grips: the charging handle is really pinched, a magazine is taken in a bottle grip (hand opens, moves, closes), and a hand that supports the gun wraps round it.
- Changed: a model imported into a skin can carry its own collision shapes (`COL_` nodes) and parts that always turn to your camera (`FACE_` nodes).
- Changed: less work every frame: finger poses skip joints with no bend, a held item is checked ten times a second, and the gun in a weapon picture is worked out once.
- Changed: ForgeSkin's glow uses one glow unit shared with the editor screens (needs ForgeCore 1.0.3).

## 1.0.1

Needs ForgeCore 1.0.2. In multiplayer the host and every client should update ForgeCore: a client's kills come from the host's ForgeCore.

- Fixed: on a client, kill and damage screens, kill sounds, Kill and Hit flashes and wear with kills counted nothing. They now show in the frame of your hit and are corrected when the host's result arrives (a kill that did not happen is taken off the screen); wear with kills follows the host's result only.
- Fixed: on a client, Miss flashes and the accuracy screen judged most hits as misses; they now follow your own shots, the same as on the host.
- Fixed: a teammate's character skin turned flat pink once the level started.
- Fixed: hologram rings on your gun (such as Critical Line's) were drawn in the middle of the screen while you danced or did another action seen from behind.
- Two skin packs with the same skin: the newer file is used, and the log says where both are.

## 1.0.0

First release on Thunderstore. Needs ForgeCore 1.0.1, which the mod manager installs with it. Skins are separate mods that depend on this one; install them from Thunderstore or make your own with the ForgeSkin editor.

- Weapon skins on your own gear: replaced textures and materials (precompressed in the pack, so a skin goes on without a stall), models mounted on the gun or replacing its parts, charms that swing on a rope or chain, live screens (ammo, kills, damage dealt and taken, accuracy, health, infection), animated layers, code effects drawn live, holograms, wear stages, and flashes and sounds on game events (kill, hit, miss, fire, reload, aim, low ammo, empty, going down, revived, ...; kill sounds per enemy).
- What you leave in the world wears your skin too: the sentry and mines you place, your gun's dropped magazines, and pickups whose item has a skin.
- Character skins: replace your whole character with an imported humanoid model. It moves with the game's own animations, the original character is hidden, and where you can be hit stays the original's. Hair, skirts and ribbons swing and rest against the body; in first person you see the model's own arms. Each player chooses how toon materials are drawn on their screen (`Toon` in `ForgeSkin.cfg`).
- The SKINS picker in the lobby (button or rebindable key, English and Chinese): gear and character slots, skin cards with the picture each pack carries, colour schemes and player colours, the adjust page for what a skin lets you change, and the settings behind the gear in its title bar.
- Teammates who have the same skin pack see the skin, colours and adjustments you picked, on your gear and your character, and you see theirs; gear whose skin they do not have shows its original look, and the picker says which Thunderstore mod to install. With ForgeReplay, replays show each player's skins.
- Skins come from skin mods in the plugins folder (a `forgeskin.forge` file lists their packs), from the skins folder `BepInEx/ForgeSkin/skins` the editor installs into (followed while the game runs), and from other plugins through `SkinPacks.Register`.
- Errors are written to `BepInEx/LogOutput.log` with a code each; `BepInEx/ForgeSkin/forgeskin.log` is what the editor reads after "Apply to game".
