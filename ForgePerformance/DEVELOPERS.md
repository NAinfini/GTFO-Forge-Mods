# ForgePerformance developer notes

ForgePerformance is a standalone load-shedding plugin. It depends only on BepInExPack_GTFO and GTFO_API, not on any Forge package, and exposes no public interface; third parties have no reason to depend on it. Everything it needs is in this package. Players should read `README.md`.

## Build and offline check

```
dotnet build Mods/ForgePerformance/ForgePerformance.csproj -c Release --artifacts-path artifacts/dotnet -p:GTFOBepInExPath=<QA profile>/BepInEx -warnaserror
```

The P4 offline gate runs from the repository root:

```
python -B Mods/verify.py ForgePerformance --game <GTFO directory> --dump <same version dump.cs> --work-dir artifacts/work/p4-perf/verify
```

It runs four groups: configuration and off routing, the written log, native layout, and the shedding binaries. A missing native input keeps an incomplete record and fails the full gate; a skip is never a pass. Run the layout group alone with the `ForgePerformance:LoadShedding native layout` selector. Nothing here starts the game.

At startup the plugin reads the native module fingerprint (`LoadShedding/NativeModuleFingerprint.cs`); all other implementation is in this package.

## Settings

Everything is under `[Performance]` in `BepInEx/config/NAinfini.ForgePerformance.cfg` and is read once, when the plugin loads. The source of truth for keys, ranges and descriptions is `LoadShedding/SheddingConfiguration.cs` and `LoadShedding/SheddingSettings.cs`.

All cut switches default to `true` except `MeleePacketReuse`. `SummaryLog` defaults to `false`. Numeric defaults:

| Setting | Default |
| --- | ---: |
| `CorpseLifetime` | 0.01 s |
| `RagdollLimit` | 4 |
| `WindLimit` | 1 |
| `ParticleFactor` | 0.01 |
| `ParticleHz` | 1 |
| `ShellLimit` | 6 |
| `DecalLimit` | 1 |
| `DecalLifetime` | 0.01 s |
| `CullDistance` | 10 m |
| `LightLimit` | 12 |
| `CorpseRecycleSeconds` | 1 s |
| `ClosedDoorDepth` | 2 |
| `ShadowDistanceFactor` | 0.65 |

A cut that is off installs none of its own hooks and does no per-frame work for itself; shared entry points stay only for the cuts still on. With every cut off, the plugin creates no shedding controller or update component. `WindCapacityProtection` and `WindNearest` both off means the three wind hooks are not installed and the wind lifecycle is not subscribed.

`ShadowDistance` and `SoftParticlesOff` differ from the game's own video options: `ShadowDistance` shortens the value the game already set by `ShadowDistanceFactor` (never below 20 m and never farther than the game's value), and `SoftParticlesOff` turns off only soft particle blending. Both are written when a level starts and restored when it ends. `TextureStreamingOff` installs no hook and does no per-frame work: at level start and at build completion it reads `Texture.streamingTextureCount` and turns `QualitySettings.streamingMipmapsActive` off only when that count is 0; it writes the original and written values to a `load_shedding` line at level end.

`ClosedDoorDepthLimit` is applied at the game's one visibility entry, `C_Camera.RunVisibilityOnPreCull` (called from `FPSCamera.OnPreCull`; its `PropagateFrustra` is a breadth-first walk over open `C_Portal`s). A prefix works out which portals this pass must close from the camera's current cull node and writes `C_Portal.<IsOpen>k__BackingField` (offset 0x128, checked against the layout when installed) to 0; a finalizer writes the real value back, including on an exception, so no other reader sees the changed value outside the visibility call. The camera's node with no closed door in the way is layer 0, the first node behind a closed door is layer 1, and every open portal adds a layer; only portals between `ClosedDoorDepth` layers and the next are closed. Door state is not patched: after the level is built, `AIG_CourseNode.s_allNodes` gives every portal's `m_cullPortal` and `m_door`, and the plugin subscribes to the door sync's `LG_Door_Sync.OnDoorStateChange` and the weak door animation's `OnDoorIsClosed`; it unsubscribes at level exit. The set is recomputed only when the camera's cull node or a door state changes (the node and portal graph and every buffer are built once at level build, so a recompute allocates nothing); each frame is two pointer reads, one comparison and one write and restore per closed portal. A door that was damaged or destroyed, a door without a sync component, or one portal shared by two doors stays vanilla. Level exit writes `item=closed-door-depth`: seen (doors managed), changed (visibility passes that closed a portal), layers, doors without a sync component, shared portals, doors released because they were damaged or destroyed, nodes, portals, recomputes, total and maximum portals closed in one pass, and failures.

`RagdollNodeRefresh` reuses the `ES_Dead.SetRagdollEnabled` patch that `RagdollCap` installs (neither is installed when both are off). It refreshes only when the call was a ragdoll before and has stopped after: it calls the game's own `C_MovingCuller.RefreshNode` once, so a body thrown across a room boundary is looked up in the room where it actually lies instead of vanishing with its old room. Level exit writes `load_shedding` `ragdoll-node-refresh` with seen (ragdolls stopped) and changed (refreshed).

## Wind, decoration and culling

The wind capacity protection covers loading, the level and level exit, independently of the visual controller, with the native fixed capacity of 64; `WindNearest` / `WindLimit` control only the optional visual reduction. The native update reads a reusable small set and a finalizer restores the original set reference, instead of removing and adding n-1 originals each call. The original set is always kept; enable/disable events are mirrored into the set as it was before the call, and a world switch never inserts old objects into the new world. Membership is maintained by pointer index with a last-item swap; a registered member that is enabled and disabled repeatedly reuses its Transform wrapper. The first registration of a new Transform creates a wrapper and keeps it until level exit or unload; first fill, set replacement and growth beyond the reserved capacity are cold paths, with an initial 4096 workspace that doubles as needed. The first choice scans everything; after that, including frequent enable/disable, at most 64 candidate positions are read per call, and the small set is not rebuilt while the nearest choice did not change. Distance culling walks only the registered candidates and the already hidden entries per camera; the shadow filter caches positions to remove by the native list's version, count and candidate set, and repeats the scan when any changes, with no cache across nested calls; corpses are queued by expiry and only the earliest is read per frame, still subject to the per-frame checks and the renderer budget.

A camera cache is built only for a call that exceeds capacity, and every use checks the camera, the underlying camera and the Transform for liveness and instance identity. The set's static field, the camera's fields and the native Transform return values are read as pointers; the steady-state identity check does not call the three QA interop getters that create new wrappers. Only the first use or a replacement creates wrappers; a member entry keeps a strong managed reference to the affector and Transform, and the set and camera cache keep their wrappers. An invalid camera refuses the update and is removed from the cache; replacement or pointer reuse rebuilds the selection; level exit and unload clear the cache. After a move, the nearest choice is refreshed by full scan rounds, so with n > 64 it takes ceil(n/64) calls and may show a small visual delay; each camera chooses independently, and equal distances sort by stable identity. Offline checks show 0 B on the prewarmed CLR stand-in path and no wrapper-creation call, and compare the production IL with the QA getters; whether the real IL2CPP hot path is 0 B can only be measured in game, and neither the cold-path cost nor the CPU gain has been measured.

The two IRF switches call a hook once per decoration per frame: a switch that is off installs no hook, and an installed hook reads one static Boolean first. The main camera and frustum are read once per frame, and the decoration's fields are read directly at checked offsets without creating wrappers. `IrfWorldCameraOnly` draws instanced decoration only into `Camera.main`; `IrfOffscreenSkip` skips the per-frame dispatch, count copy and draw of static instanced decoration whose fixed bounds are fully outside the main camera frustum. Dynamic transforms, decoration parented to an enemy, one that has not had its first transform update, or one whose bounds move with the object are never skipped; the frustum test is expanded by 1 m plus 20% of the distance to cover a camera that moves after the decoration's update. Off-screen plants stop swaying and their shadows use the last computed instances. A level exit writes one `load_shedding` line for each of `MeleePacketReuse`, `IrfWorldCameraOnly` and `IrfOffscreenSkip` that is on.

Decoration classification uses native types and a component whitelist; an unknown object stays vanilla. A rotating decoration must have its own draw group holding only its own subtree, no collider, gameplay or unknown script; the distance hide allows only checked game assets, a known additive shader with a single pass and no depth write, and a unique runtime identity; other materials stay vanilla. Animation must have no root motion, events, state scripts or external animation binding. Lights consider only ordinary static ambient decoration spotlights and exclude the flashlight, controllers, objectives and environment-event light references; when a reference cannot be read completely, vanilla stays. Decals check both the hit-effect source and a separate decal component; scan rings, glue, target marks and generic decals are not treated as combat decals.

Level build starts the opening setup when the elevator is waiting to stop (`ReadyToStopElevatorRide`, before landing), one scene-wide search per block in a queue with at most 12 ms per frame (2 ms after entering the level), never finished on the landing frame; blocks not reached yet stay vanilla. New decoration is scanned once in this round; an object that appears later and is not confirmed stays vanilla. Hit decals are registered at the actual play and pool-return entries. These changes affect only this machine's presentation and send no multiplayer state; corpse collision, the native cleanup coroutine and the multiplayer lifecycle are kept.

## Other cuts

`LeakCleanup` destroys or releases what the game forgets after a level: empty text assets, effect point lights, instanced-render buffers, flyer materials, terminal build-done callbacks, queued objective hint messages, and duplicate menu header entries left by a scrolling window resetting its header. Nothing visible changes. Its instanced-render buffers are destroyed only in the level-exit teardown, never for in-level features, including ones on enemies, that share a buffer.

`DecayCleanup` clears a corpse's dissolve particles about five seconds after they finish; the corpse is already invisible and a corpse that is dissolving again is skipped. `DebrisFreeze` freezes breakable debris three to five seconds after it lands (rechecking each second while it still moves, at most three times), turning off collision and making it static until the object breaks again. `GlueSoundRecycle` returns a destroyed foam ball's audio source to the pool; only the audio object is recycled. `StackTraceTrim` stops Unity writing stack traces for ordinary log and warning lines; errors and exceptions keep theirs. `GuiMirrorOff` removes the interface's mirror and reflection effect. `LowQualitySpitter` blurs the infection spitter's texture and removes its ground decal; its texture offset is restored at level exit, and a decal already destroyed is not restored. `CombatDecals` has no `Decal.Shown` hook: the game only hides a decal on pool return, and the pool-return entry already handles it.

`MeleePacketReuse` (off by default) makes every first-person melee weapon of this machine share one network hit packet. The packet is addressed by replicator number and position in the replicator, so the plugin does not remove a position: after setup it points the new packet's position at the session's first packet, and the weapon uses that one, so the replicator's length and order stay exactly vanilla and later registrations (revive state, communication menu) keep their positions. The wire numbers are the vanilla ones, so a lobby with players without the option, without the mod or with another version does not go out of range; the gain is a few small objects less per melee setup, the frame-rate gain is unmeasured, and multiplayer is unverified, which is why it is off.

## Logging

The default log writes errors only. With `[Performance] SummaryLog` true, the session start writes one `type=performance_setting` line per effective setting. Level exit and a normal shutdown write one `type=load_shedding` line per cut and per decoration class, separating changed, unchanged, restored, skipped draws, recycled and native-lifecycle takeovers. There is no per-frame logging.

The real effective range, the picture, multiplayer behaviour and an on-machine A/B comparison have not been verified; performance gain is unmeasured. Frame time, hitches and memory are ForgeDevelopment's Performance Diagnostics, not this plugin's job.
