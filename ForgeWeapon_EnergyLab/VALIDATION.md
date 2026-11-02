# Validation

## Latest run: lowered gun and charge orb anchor

Detached worktree on origin/main 7d2274e83, Forge-MapEditor-QA profile references (read-only), `--disable-build-servers -m:1`. Evidence level: **build, offline tests and repository checks; not in game**.

- Change: a fire input brings a lowered gun up (`Combat.RaiseGun` each frame the button is held, ForgeCore); the orbs charge only once the gun is no longer relaxed (`Combat.GunLowered`); every energy shot and effect that begins while the gun is lowered starts from the raised muzzle in front of the look camera (`Shot.Trace`, `GunPose.RaisedMuzzle`), for all eight guns; a remote player's charging orb follows that player's third-person muzzle and turns with the player's yaw (`GunPose.ToYawFrame`/`FromYawFrame`, `RemoteWeapon.ChargePoint`); the first-person charge preview keeps clear of the sight line only while aiming.
- Results: ForgeWeaponEnergyLab Release build 0 warnings 0 errors; `tests/Core` 37 PASS, 0 FAIL (unchanged: the changed logic is game-bound); ForgeCore `tests/Core` 404 passed (5 new gun-pose checks); `validate-dependencies --check`, `build-library-manifests --check`, `check:codes` exit 0.
- Not verified: that `DontRelax` alone lifts a relaxed gun (read from the member names, not the game); that the first-person orb is now seen.
## Owner test list (not run by the agent)

0. Blast Gun shell: a small orange glowing shell is seen leaving the muzzle and flying at about 40 m/s (colour and size against the other guns; say if it reads poorly) in first person, in a second player's view and in the host's view; it explodes where it first touches a wall, floor, enemy or player; an enemy stepping into its path is hit, a target that moves away is missed and the shell hits what is behind; damage, radius, falloff and sounds match the earlier instant gun, also with a damage booster active and when a client or a bot fires; a hit enemy shows the hit marker; kills at range still fling the enemy; a bot's shots work; a shot at a wall a step away explodes at once; several shells in the air at once; shooter dying or leaving drops its shells; a client's explosion damage arrives through the host (stats mods count it outside the firing call).

1. Blast Gun shell: the explosion comes after a flight time (a far wall about distance/40 s later, a near one almost at once), at the aimed point, with the same damage, radius and sounds as before; a second player and the host see and hear it at the same delay; a bot's shot works; the death-force on kills at long range still pushes the enemy; killing yourself or leaving before the explosion drops it without an error line; Blast Gun feels right at 40 m/s (say if faster or slower is wanted).

2. Fire and hit: all eight guns fire and hit as before (tap and held; orbs on release); a bot and a second player's shots (synced bodies) keep their shooter and damage; stock guns (single, burst, auto, shotgun) fire, hit and aim as before; no `bracket-GameHooks` line in the log.
3. First person: the hand pose and the sight's red dot follow the gun in the same frame (strafing, aim in and out, switching guns with aim held, reload); the red dot shows only through the glass; a stock gun in the hand is unaffected.
4. Blast Gun: shot and explosion sound like the Ordnance grenade launcher and its burst, in first person and in a second player's third person; the other seven guns sound unchanged; if the energy cycle sound muddies the grenade shot, say so.
5. Sounds: every gun's sounds against the earlier build (loudness at the same game volume, near and far distance, loop joins free of clicks, loops stop when the gun is holstered or dies, launch timing, gravity collapse sound); the ForgeAudio level-start table lists the `energy`, `boom` and grenade buses; no sound pushed out in a crowded fight.
6. Effects and lights: beam, arcs, orbs (charging, launch), flame, explosions, sparks, gravity well, disc against the earlier build; lights while strafing; beam, flame and charging orb stay on the muzzle; nothing left shown after a level change; the ForgeVisual log shows both packs registered.
7. Hit rules: orb and disc contacts (spawn inside a receiver, passing through actors), flame cone, gravity pull and damage (enemies only), arc chain and pulses (each enemy once), explosion damage and force on enemies and loose rigidbodies, back hits, weakspots, armor, damage statistics, hit markers; no "dropped" line in a normal fight.
8. Gravity Core: the pull moves enemies as before at the lower step rate (not choppier to the eye), the damage pulses keep their timing, the field stays visible while it collapses.
9. Icons: the eight guns in the lobby slots, picker, weapon window and cockpit list look like vanilla ones (shading, size); no stray gun model at the world origin after repeated loadout changes; the log has no icon error.
10. Names and language: Chinese and English names and the default description in the picker, every lobby slot, the weapon window and the in-level list, changing with the language without a restart; a Chinese and an English player match gear and saved loadouts load; the mismatch chat line in both languages.
11. Sight and model: the orb below the reticle through each optic, hip fire and the aim transition, left hand in both views, model size, height and kick, surface gloss.
12. Muzzle flash: no vanilla flash on any energy gun in first or third person (tap; Beam and Flame held), also after a swap or a reassembly; a stock gun and a sentry still flash; if one shows, send the observer's `Player.log`.
13. Loading: loads with MTFO; BepInEx skips Energy Lab without MTFO, ForgeUI, ForgeAudio or ForgeVisual; the config folder `ForgeWeapon_EnergyLab` is created with `tuning-defaults.json`, an override in `tuning-overrides.json` takes effect, a mistake in it is named in the log.
14. Range: Blast Gun and Beam beyond 40-45 m.
15. Co-installed mods: Hikaria Core, StatDisplay and TheArchive together with Energy Lab.
16. Data files: a third-party gun added through a `ForgeEnergyLab/*.json` file in another mod folder appears in the picker and fires.
17. Bad entry: a broken entry in a data file is skipped with one log line naming file, Id and field; the other guns of that file and the other files still load.
18. Missing bundle: a gun whose bundle file is missing is skipped with one line; the other guns stay.
19. Overrides: a `tuning-overrides.json` entry with an unknown Id is skipped with one line; known guns are tuned.
20. Order and look: the eight shipped guns are listed as Beam, Chain Arc, Flamethrower, Charge Orb, Shock Orb, Blast Gun, Ricochet Disc, Gravity Core, with their models, names and icons as before.
21. Names: a gun with `<Id>.Name` and `<Id>.Description` in its `lang` folder shows them in both languages even when its file has no `Description`; two guns with the same description text give one log line, not a refusal.
