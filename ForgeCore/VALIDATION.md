# Validation

## Latest run: error logging (2026-11-01)

Detached worktree on origin/main 2219f0652, Forge-Ordnance-Test profile references (read-only), `--disable-build-servers -m:1`; evidence level: **build and offline tests, not in game**.

- `ModLog.ErrorOnce(message)` dedupes by the exact message, not by call site; `ModLog.Count` and `Net.StaleMessages` (read only by tests) and the unused lazy `OnceReporter.Report` are removed. Errors log the whole exception. Two swallowed catches now log (expedition checksum in the level fingerprint, plugin DLL hash). Stale text fixed in `Plugin.cs` (hook error message) and `Patches.cs` (overload comment).
- ForgeCore.csproj Release 0 warnings 0 errors (`--no-incremental`); ForgeCore core tests 522 passed, 0 failed; `validate-dependencies --check` passes. The solution was not built: other packages still call `ErrorOnce(key, message)`.
- Not verified: in-game log content.

## Still open from the previous run (release clean-up and shared capabilities, 2026-10-31)
Not verified (needs the game; owner test):
1. Sentry: summon, pick up, recall and destroy a turret; ForgeWeapon and ForgeOrdnance still see every step through `SentryLifecycle`.
2. `HoldPoint.Disable`: a shut point stays shut after the game switches it on again; `Enable` opens it.
3. Startup log of a profile with EnergyLab has no "does not contain a block with ID" lines from `DataBlocks.Check`.
