# Changelog

## Unreleased

- Changed: Forge Development now needs only Forge Core. It works in any level, the game's own, an MTFO rundown or a Forge map, without Forge Engine installed and without Authoring mode. Turn it off with `[General] Enabled = false` in `NAinfini.ForgeDevelopment.cfg`.
- Changed: reads that only Forge maps have (reloading plans, level variables, generation progress, authored room lookup, model shots, bot slots) come from Forge Engine when it is installed and in Authoring mode; otherwise they answer `forge-engine-not-installed` or `forge-engine-not-authoring`.
- Changed: the drive tools enter a level by rundown and level (`run-drive-session.py start --rundown <id> --level A1`) without a map package, find the game through Steam when `--game` is left out, take `--profile` and `--lock`, and run from a folder outside the repository.
- Changed: `god on` now blocks every hit the player takes and every infection change a level event applies (disinfection too), through Forge Core. The recorder no longer copies Forge Engine's per-step behavior trace (removed `[Recorder] BehaviorTraceEnabled`); Forge Engine's own logs keep those codes. `[Logging] Level` is gone.

- Changed: the test console no longer lets a passing command stand for an effect. `call` answering False, an empty `--until` answer, a `fire` with no shot or no readback, a `reload` that changed nothing and a `throw` that threw nothing now fail; each script run starts and ends from a reset baseline, its `expect log` reads only its own run (with `ignore=<regex>` for known noise), and a failure names the expected and actual value, the command and its result folder. Scripts find rooms by name or `@prefab` and buttons by prompt text instead of generated ids.
- Added: `SignLayers SelfTest` (a correct and three deliberately broken materials through the same judgement), `drop.stall` and `drop.wait` log lines between drop stage markers, and `rec-query.py` reads a recording that is still being written.

## 1.0.0

- First release. Optional diagnostics for level and mod authors: generation and performance reports, a session recorder, snapshots and in-game experiments. Players never need it, and it only starts when Forge Engine runs in Authoring mode.
