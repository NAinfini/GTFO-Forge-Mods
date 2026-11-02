# Changelog

## 0.1.0

First release.

- Modules: `Log`, `Settings`, `Store`, `Pool`, `Assets`, `Frame`, `Patches` (first-use and lazy patching), `Net` (handshake by format fingerprint, `NetDelivery`, lobby values, world epoch), `InputLease`, `CameraLease`, `GameEvents`, `GameHooks` / `Bracket`, `Enemies`, `EnemyAlert`, `EnemySpawn`, `Level`, `Combat`, `Gear`, `Items`, `SoloDrop`, `DataBlocks`, `PlayerHits`, `PlayerNoise`, `Puppets`, `WorldStates`, `HoldPoint`, `Compact`, `ChunkLog`, fingerprints and hashes.
- `GameHooks` patches the game methods several packages read once and hands each call to every subscriber (`Before`, `After`, `Around`). `GameHooks.SentryLifecycle` covers the seven sentry methods that move a sentry in or out of its owner's backpack and the world (`SentryStep`, `SentryCall`).
- `Level.PlayerByLookup` / `Level.IsPlayer` find the in-level player agent of a network player.
- `Combat.ShooterRay` is the bullet ray that steps over the shooter's own colliders.
- `HoldPoint.Disable` / `Enable` shut a timed hold point and keep it shut when the game switches it on again, without a per-frame check.
- `GameEvents.ExpeditionSelected` is raised on the host and on clients.
- Logging writes errors only.
- `ModLog.ErrorOnce(message)` writes an exact message once; errors log the whole exception.
