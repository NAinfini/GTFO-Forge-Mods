# Validation

## Latest run: heavy weapon models and turret strength (2026-11-01)

On the api-ordnance commits (7c1f7ba2b); Forge-Ordnance-Test profile references (read-only), ForgeModsRoot = main checkout Mods, --disable-build-servers -m:1; evidence level: **build and offline tests, not in game**. Heavy weapons take a Pack:item model and an optional Grenade object; turrets are stronger and the shotgun look is smaller. Arsenal Release 0 warnings 0 errors; offline tests 1781 checks passed (native lifecycle 301); alidate-dependencies --check pass. Not done: no game run.

## Earlier run: heavy weapon and exosuit gun swaps (2026-11-01)

Detached worktree on origin/main 7d2274e83, Forge-MapEditor-QA profile references (read-only), ForgeModsRoot = main checkout Mods, `--disable-build-servers -m:1`; evidence level: **build and offline tests, not in game**.

- Heavy weapon first raise: the model (its sight node included) is put on before the wield request, not by the periodic scan after it (`HeavyWeapons` Equipping). Swap gap: the incoming gun's parts are finished at once through ForgeCore `LoanedGun.FinishParts` (equip and return). Arsenal Release 0 warnings 0 errors; offline tests 1740 checks passed (native lifecycle 298); `validate-dependencies --check` pass.
- Not done: no game run.

## Owner tests (in game)

- Heavy weapons, ForgeVisual models and the Grenade object: the machine gun, rifle and launcher look, fire and behave as before (launcher grenade 120 damage, radius 4, speed 32); a sample third-party heavy weapon (own pack, Pack:item model, gear) is accepted, and one with a missing model or missing RightHand, LeftHand or SightLook is refused at load naming Model.
- Heavy weapons: hands on grip and foregrip, magazine leaves and returns with the reload, no stock parts through the model, aim through the model's sight; launcher fires with no charge-up and its own bullet and reload sound are suppressed correctly; G, going down, spent weapon and checkpoint reload give back the own special weapon with its ammunition.
- Strikes: fall direction, blast and friendly fire, fire and gas damage over time, stasis slow and exact speed restored, cutting beam, cluster split, ground under strike points on stairs and under low ceilings (floor probe now `Level.FloorBelow`).
- Grenades: same blast on every peer; client timeout path.
- Lights and shake with `Extra lights` on and off; compare brightness with the earlier build.
- Sounds: first falling shell and first landing without a hitch; turret hum while standing; loops fade when their owner goes.
- Turrets: the three looks, deploy and expire sounds.
- Turret strength (owner feedback 11-01): every turret's detection range 1.5 times, ammunition 2 times and damage 1.5 times the earlier build (against the game's sentry now damage 2.175, ammunition 3, detection range 1.8); the shotgun turret (now world scale 1.05, muzzle 0.90 m up) hits standing and crawling enemies at close range and no longer fires over them; the burst and sniper turrets still hit as before.
- Log: only error lines appear in the BepInEx log for `NAinfini.ForgeOrdnanceArsenal`; an exact repeat of a failure message is written once.