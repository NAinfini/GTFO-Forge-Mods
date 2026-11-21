using System;
using System.Linq;
using System.Text.Json;
using AIGraph;
using LevelGeneration;
using Gear;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Development-only: shoot one Forge placed model the way a player does. The driver walks the local player to a spot
// with a clear line to the model's own shooting target, points the player's camera at it, and holds the game's own
// Fire input until the wielded gun's clip drops (HeldInputLease answers the game's input query; no key is injected).
// What happens after the trigger pull — hit resolution, the published event, the behavior and its action — is read
// from the Forge Engine log, never from this driver.
internal static partial class AuthoringGameDrive
{
    /// <summary>A model shot holds the single input lease; command cleanup gives back only a lease a shot took, never one GameInputDrive.Hold keeps across commands.</summary>
    private static bool _modelShotLease;

    private static ExperimentCallResult ShootModel(SpatialRecord record, JsonElement spec)
    {
        if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("model", out _)) SpatialFields(spec, "schemaVersion", "model");
        else SpatialFields(spec, "schemaVersion", "position", "within", "expect");
        RequireHostLevel();
        // Either a Forge placed model (found by its own collider) or a point on a native object: a door or terminal at "position".
        Func<Collider, Vector3, bool> isTarget;
        Vector3 center;
        object targetSample;
        if (spec.TryGetProperty("model", out var modelProperty))
            (isTarget, center, targetSample) = ResolveModelShotTarget(modelProperty.GetString() ?? throw new ArgumentException("drive.model-id"));
        else
        {
            var at = spec.GetProperty("position");
            center = new Vector3(at[0].GetSingle(), at[1].GetSingle(), at[2].GetSingle());
            float within = spec.TryGetProperty("within", out var withinProperty) ? withinProperty.GetSingle() : 1.5f;
            string expect = spec.TryGetProperty("expect", out var expectProperty) ? expectProperty.GetString() ?? "" : "";
            var aim = center;
            isTarget = (hit, point) => (point - aim).magnitude <= within && expect switch
            {
                "door" => OwnedBy<LG_SecurityDoor>(hit) || OwnedBy<LG_WeakDoor>(hit),
                "terminal" => OwnedBy<LG_ComputerTerminal>(hit),
                _ => true
            };
            targetSample = new { position = SpatialVector(center), within, expect };
        }
        var run = new ModelShotRun(record, isTarget, center, targetSample);
        ExperimentCallResult Poll() => record.Guard(() =>
            run.Step() ? record.Result("drive.model-shot-fired") : ExperimentCallResult.Await(Poll));
        return ExperimentCallResult.Await(Poll);
    }

    /// <summary>The target is the model's one registered shooting collider itself. Other colliders of the same placed model (a shelf, a base)
    /// are geometry in the way, not the button: a ray whose first hit is one of them is blocked.</summary>
    internal static (Func<Collider, Vector3, bool> IsTarget, Vector3 Center, object Sample) ResolveModelShotTarget(string modelId)
    {
        var collider = EngineLookup.Call<Collider>("ModelShotCollider", modelId);
        if (collider.WasCollected || !collider.enabled || !collider.gameObject.activeInHierarchy)
            throw new InvalidOperationException("drive.model-shot-collider-inactive");
        var root = ModelRoot(collider);
        var center = collider.bounds.center;
        var targetPointer = collider.Pointer;
        return ((hit, _) => hit != null && !hit.WasCollected && hit.Pointer == targetPointer, center, new
        {
            model = modelId,
            collider = collider.name,
            colliderLayer = collider.gameObject.layer,
            root = root.name,
            rootLayer = root.gameObject.layer,
            center = SpatialVector(center),
            size = SpatialVector(collider.bounds.size)
        });
    }

    /// <summary>One shot at a target, advanced one frame per Step: walk to a clear spot, aim, hold the game's own Fire
    /// input until the clip drops, then wait a few ticks for the plan. Both the drive command and the auto test call it.</summary>
    internal sealed class ModelShotRun
    {
        private readonly SpatialRecord record;
        private readonly Func<Collider, Vector3, bool> isTarget;
        private readonly Vector3 center;
        // Stand points with a clear predicted line, in the order they are tried; a point whose line is blocked from the
        // eye the warp really lands on gives way to the next one.
        private readonly Vector3[] stands = new Vector3[MaxStands];
        private readonly int standCount;
        private int standIndex;
        private Vector3 stand => stands[standIndex];
        private readonly SpatialPlayer lease;
        // The gun the shot is aimed with. Before the trigger is pulled it follows the gun the game wields (a gear swap asked by an
        // earlier check's action or its cleanup can finish after the game's request returned); after the pull the clip drop is
        // read on this gun, and a later swap by the behavior under test is that behavior's own result, not a driver failure.
        private BulletWeapon weapon;
        private readonly eDimensionIndex dimension;
        private readonly long startWorld;
        private float deadline;
        private int phase, phaseFrame, clipBefore, aimReadFrame = -1, rebinds, bestLookFrame;
        private float bestLook;
        private bool retried, rewielded, wrongHeld, notedWrong;
        // Another gun in hand: the frame it was first seen and the wield requests made for the planned gun. The game wields nothing by
        // itself, so the driver asks for the planned gun's slot once at once and once more after PlannedAskFrames.
        private int wrongSince = -1, plannedAsks;
        private const int PlannedAskFrames = 90;
        // The gear identity the caller's plan asks this shot to fire (when it declares none, the gun in hand as the shot starts).
        // Another gear in hand before the trigger is pulled is a restore or swap still in flight.
        private readonly string plannedGear;
        private const int MaxRebinds = 3;
        // Frames the camera may go without turning closer to the target before its look counts as stalled.
        private const int AimStallFrames = 30;
        private const float StrictAlignCosine = .9995f;

        /// <summary>Cosine of the centre misalignment a ray that already hits the target may have: 0.6 of the angle the target's
        /// second-largest half extent subtends, kept between the strict 1.8 degrees and 6 degrees.</summary>
        internal static float AlignCosine(Vector3 extents, float distance)
        {
            float half = Math.Min(Math.Max(extents.x, extents.y), Math.Max(Math.Min(extents.x, extents.y), extents.z));
            double degrees = Math.Atan2(half, Math.Max(distance, .01f)) * 180d / Math.PI * .6d;
            return (float)Math.Cos(Math.Clamp(degrees, 1.81d, 6d) * Math.PI / 180d);
        }
        private const float AimProgress = .0005f;


        internal ModelShotRun(SpatialRecord record, Func<Collider, Vector3, bool> isTarget, Vector3 center, object targetSample,
            bool retryUsed = false, string? plan = null)
        {
            retried = retryUsed;
            this.record = record; this.isTarget = isTarget; this.center = center;
            lease = SpatialLocal();
            var player = lease.Player;
            weapon = Held(player) ?? throw WieldFirst(player);
            plannedGear = plan ?? GearOf(player, weapon);
            wrongHeld = GearOf(player, weapon) != plannedGear;
            if (!wrongHeld && weapon.GetCurrentClip() < 2) weapon.SetCurrentClip(weapon.GetMaxClip()); // the driver loads its own gun; no shot is counted from the refill
            if (!wrongHeld && weapon.GetCurrentClip() <= 0) throw new InvalidOperationException("drive.model-shot-clip-empty");
            record.Before = SpatialPose(player);
            record.Samples.Add(targetSample);
            // The stand search and the warp belong to the dimension the target stands in (a landing room's button lies in another
            // dimension than the player who starts the shot); the player's own dimension is only the answer when the target has no node.
            var targetDimension = Dimension.TryGetCourseNodeFromPos(center, out var targetNode) && targetNode?.m_dimension != null
                ? targetNode.m_dimension.DimensionIndex : player.DimensionIndex;
            standCount = FindStandPoints(player, targetDimension, isTarget, center, record, stands);
            if (standCount == 0)
                throw new InvalidOperationException("drive.model-shot-no-clear-line");
            dimension = targetDimension;
            startWorld = RecTime.WorldEpoch;
            SpatialWarp(record, player, dimension, stand);
            deadline = Time.realtimeSinceStartup + 10f;
            phaseFrame = Time.frameCount; clipBefore = weapon.GetCurrentClip();
        }

        /// <summary>The drop wields no gun; the game wields the standard slot through its own sync call, and the caller retries.</summary>
        internal static InvalidOperationException WieldFirst(PlayerAgent player)
        {
            player.Sync.WantsToWieldSlot(InventorySlot.GearStandard, false);
            return new InvalidOperationException("drive.model-shot-wield-issued-retry");
        }

        /// <summary>Returns true when the shot is over and the plan has had its ticks.</summary>
        internal bool Step()
        {
            RequireHostLevel();
            var current = SpatialCurrent(lease);
            record.After = SpatialPose(current);
            if (Time.realtimeSinceStartup >= deadline)
                throw new InvalidOperationException(wrongHeld ? "drive.model-shot-wrong-weapon-held" : phase == 1
                    ? "drive.model-shot-aim-not-on-target"
                    : "drive.model-shot-phase-timeout:" + phase);
            var gun = Held(current);
            if (phase < 2)
            {
                if (gun == null)
                {
                    if (!rewielded) { rewielded = true; current.Sync.WantsToWieldSlot(InventorySlot.GearStandard, false); }
                    return false;
                }
                // Only the gun the plan started with may fire. Another gear in hand is an earlier check's restore (or a swap) still
                // in flight: wait for it, and name the failure when it never settles.
                if (GearOf(current, gun) != plannedGear)
                {
                    wrongHeld = true;
                    if (!notedWrong)
                    {
                        notedWrong = true;
                        record.Samples.Add(new { phase = "weapon-not-planned", frame = Time.frameCount, held = GearOf(current, gun), planned = plannedGear });
                    }
                    phaseFrame = Time.frameCount; aimReadFrame = -1;
                    if (phase == 1) phase = 0;
                    if (wrongSince < 0) wrongSince = Time.frameCount;
                    if (plannedAsks == 0 || plannedAsks == 1 && Time.frameCount - wrongSince >= PlannedAskFrames) AskForPlannedGun(current, gun);
                    return false;
                }
                wrongHeld = false; wrongSince = -1;
                // The same gear rebuilt by a finished restore is a new instance of the planned gun.
                if (gun.Pointer != weapon.Pointer)
                {
                    if (++rebinds > MaxRebinds) throw new InvalidOperationException("drive.model-shot-weapon-changed");
                    record.Samples.Add(new { phase = "weapon-rebound", frame = Time.frameCount, rebinds });
                    weapon = gun;
                    if (weapon.GetCurrentClip() < 2) weapon.SetCurrentClip(weapon.GetMaxClip());
                    phase = 0; phaseFrame = Time.frameCount; aimReadFrame = -1;
                    return false;
                }
            }
            var camera = current.FPSCamera ?? throw new InvalidOperationException("drive.model-shot-camera-unavailable");
            switch (phase)
            {
                case 0:
                    // The native warp settles the player onto the floor within a step of the asked spot, not on the exact point.
                    var flat = current.Position - stand; flat.y = 0f;
                    if (current.DimensionIndex == dimension && flat.magnitude <= 1f && Time.frameCount - phaseFrame >= 30)
                    {
                        phase = 1; phaseFrame = Time.frameCount; deadline = Time.realtimeSinceStartup + 10f;
                        bestLook = -1f; bestLookFrame = Time.frameCount;
                        camera.SetLookDirection((center - camera.Position).normalized);
                        break;
                    }
                    // A sync warp can be dropped while the player is still settling from the last one; ask again.
                    if (Time.frameCount - phaseFrame >= 180 && Retry("warp-not-settled"))
                    {
                        phaseFrame = Time.frameCount;
                        SpatialWarp(record, current, dimension, stand);
                    }
                    break;
                case 1:
                    {
                        // UpdateCameraRay (0x193AA50) stores the HIT POINT in CameraRayPos, or the far end on a miss.
                        // BulletWeapon.Fire (0x145FB30) fires from FPSCamera.Position toward that point. Feeding the hit
                        // point back as an origin makes the requested direction reverse or collapse as aim converges.
                        // SetLookDirection updates yaw/pitch, not that cached ray: observe only after a native frame.
                        if (Time.frameCount <= phaseFrame || Time.frameCount == aimReadFrame) break;
                        aimReadFrame = Time.frameCount;
                        var rayOrigin = camera.Position;
                        var toward = center - rayOrigin;
                        int waited = Time.frameCount - phaseFrame;
                        var rayDirection = (camera.CameraRayPos - rayOrigin).normalized;
                        bool lookAligned = Vector3.Dot(camera.transform.forward.normalized, toward.normalized) > StrictAlignCosine;
                        bool rayHit = Physics.Raycast(rayOrigin, rayDirection, out var aimHit, toward.magnitude + .75f,
                            LayerManager.MASK_BULLETWEAPON_RAY);
                        bool rayOnTarget = rayHit && aimHit.collider != null && isTarget(aimHit.collider, aimHit.point);
                        // The firing ray itself hits the target collider, so the shot lands; the camera turns at a fixed fraction of
                        // the remaining angle per frame (about 3% at the loaded game's 10 frames a second), and demanding the centre
                        // to 1.8 degrees ran the 10 s deadline out with the ray already on a 0.36 m target (best 3.3 degrees, 1.2 m away).
                        bool rayAligned = Vector3.Dot(rayDirection, toward.normalized)
                            > (rayOnTarget ? AlignCosine(aimHit.collider!.bounds.extents, toward.magnitude) : StrictAlignCosine);
                        bool onTarget = rayAligned && rayOnTarget;
                        AimSample(record, camera, current, center, waited, onTarget, rayHit, aimHit);
                        if (!onTarget)
                        {
                            // Stand selection used the predicted standing eye. A warp can settle at a different height
                            // or offset: test the desired line from the actual firing origin before attempting correction.
                            bool clear = Physics.Raycast(rayOrigin, toward.normalized, out var lineHit, toward.magnitude + .75f,
                                LayerManager.MASK_BULLETWEAPON_RAY) && lineHit.collider != null && isTarget(lineHit.collider, lineHit.point);
                            record.Samples.Add(new
                            {
                                phase = "aim-line",
                                clear,
                                origin = SpatialVector(rayOrigin),
                                center = SpatialVector(center),
                                distance = toward.magnitude,
                                maxRayDist = weapon.MaxRayDist,
                                rayFirstHit = ShotHit(lineHit)
                            });
                            if (!clear)
                            {
                                // The warp settled at an eye the stand search did not predict. Ask for the next stand point; the hit
                                // rule is unchanged, and only when every point is blocked is the line reported as not clear.
                                if (++standIndex >= standCount) throw new InvalidOperationException("drive.model-shot-no-clear-line");
                                record.Samples.Add(new { phase = "restand", standIndex, position = SpatialVector(stand), frame = Time.frameCount });
                                SpatialWarp(record, current, dimension, stand);
                                phase = 0; phaseFrame = Time.frameCount; aimReadFrame = -1;
                                deadline = Time.realtimeSinceStartup + 10f;
                                break;
                            }
                            RequireRange(lineHit);
                            // SetLookDirection has already settled the camera transform, but UpdateCameraRay can still
                            // expose the previous frame's hit point. That passive cache lag is not another spatial
                            // correction: keep the strict ray/target test and wait within this phase's existing deadline.
                            if (lookAligned && !rayAligned) break;
                            if (!lookAligned)
                            {
                                // The camera is still turning toward the request, or something keeps pushing it off; the request is
                                // repeated every frame (as while firing) and the spatial retry is spent only when the turn stalls.
                                float look = Vector3.Dot(camera.transform.forward.normalized, toward.normalized);
                                if (look > bestLook + AimProgress) { bestLook = look; bestLookFrame = Time.frameCount; }
                                camera.SetLookDirection(toward.normalized);
                                if (Time.frameCount - bestLookFrame < AimStallFrames) break;
                            }
                            string reason = !rayAligned ? "camera-ray-not-aligned" : "camera-ray-hit-mismatch";
                            if (!Retry(reason)) throw new InvalidOperationException("drive.model-shot-aim-not-on-target");
                            camera.SetLookDirection(toward.normalized);
                            phaseFrame = Time.frameCount; bestLook = -1f; bestLookFrame = Time.frameCount;
                            break;
                        }
                        RequireRange(aimHit);
                        clipBefore = weapon.GetCurrentClip();
                        HeldInputLease.ArmGuard(() => GameStateManager.CurrentStateName == eGameStateName.InLevel && RecTime.WorldEpoch == startWorld
                                ? (true, "") : (false, "model-shot-world-ended"),
                            reason => { }, error => { });
                        HeldInputLease.Press(InputAction.Fire);
                        _modelShotLease = true;
                        record.Issued = true;
                        phase = 2; phaseFrame = Time.frameCount; deadline = Time.realtimeSinceStartup + 10f;
                        break;
                    }
                case 2:
                    {
                        camera.SetLookDirection((center - camera.Position).normalized);
                        // The clip is read on the gun that fired. If the behavior already swapped the wielded gear, the fired gun's
                        // clip is still its own; a swap before any clip drop was seen leaves the shot unconfirmed.
                        int clipNow = weapon.WasCollected ? clipBefore : weapon.GetCurrentClip();
                        bool swapped = gun == null || gun.Pointer != weapon.Pointer;
                        if (clipNow >= clipBefore && swapped) throw new InvalidOperationException("drive.model-shot-weapon-changed");
                        if (clipNow >= clipBefore && Time.frameCount - phaseFrame < 120) break;
                        HeldInputLease.Release(InputAction.Fire, false);
                        record.Samples.Add(new { phase = "fired", frame = Time.frameCount, clipBefore, clipAfter = clipNow, swapped });
                        if (clipNow >= clipBefore) throw new InvalidOperationException("drive.model-shot-gun-did-not-fire");
                        phase = 3; phaseFrame = Time.frameCount; deadline = Time.realtimeSinceStartup + 10f;
                        break;
                    }
                default:
                    // Give the plan a few ticks to run before the record closes.
                    if (Time.frameCount - phaseFrame < 20) break;
                    HeldInputLease.Restore();
                    HeldInputLease.DisarmGuard();
                    _modelShotLease = false;
                    return true;
            }
            return false;
        }

        /// <summary>The planned gun lives in another slot than the gun in hand (the round 17 D1 records: every shot held a different gear
        /// for the whole window): ask the game, through its own sync call, to wield the slot that holds it.</summary>
        private void AskForPlannedGun(PlayerAgent player, BulletWeapon held)
        {
            plannedAsks++;
            var slot = SlotOfGear(player, plannedGear);
            record.Samples.Add(new { phase = "wield-planned", ask = plannedAsks, slot, held = GearOf(player, held), planned = plannedGear, frame = Time.frameCount });
            if (slot >= 0) player.Sync.WantsToWieldSlot((InventorySlot)slot, false);
        }

        private bool Retry(string reason)
        {
            if (retried) return false;
            retried = true;
            record.Samples.Add(new { phase = "retry", reason, frame = Time.frameCount });
            return true;
        }

        private void RequireRange(RaycastHit hit)
        {
            if (hit.distance <= weapon.MaxRayDist) return;
            record.Samples.Add(new
            {
                phase = "range",
                distance = hit.distance,
                maxRayDist = weapon.MaxRayDist,
                rayFirstHit = ShotHit(hit)
            });
            throw new InvalidOperationException("drive.model-shot-target-out-of-range");
        }
    }

    private static object? ShotHit(RaycastHit hit)
        => hit.collider == null ? null : new
        {
            collider = hit.collider.name,
            layer = hit.collider.gameObject.layer,
            point = SpatialVector(hit.point),
            normal = SpatialVector(hit.normal),
            hit.distance,
            bounds = new { center = SpatialVector(hit.collider.bounds.center), size = SpatialVector(hit.collider.bounds.size) }
        };
    private static void AimSample(SpatialRecord record, FPSCamera camera, PlayerAgent player, Vector3 center, int waited,
        bool onTarget, bool hit, RaycastHit observed)
    {
        var ray = camera.CameraRayCollider;
        var origin = camera.Position;
        var direction = (camera.CameraRayPos - origin).normalized;
        var distance = (center - origin).magnitude + .75f;
        int mask = LayerManager.MASK_BULLETWEAPON_RAY;
        record.Samples.Add(new
        {
            phase = "aim",
            waited,
            onTarget,
            rayCollider = ray == null ? null : ray.name,
            rayLayer = ray == null ? -1 : ray.gameObject.layer,
            cameraPos = SpatialVector(camera.transform.position),
            cameraForward = SpatialVector(camera.transform.forward),
            cameraRayEnd = SpatialVector(camera.CameraRayPos),
            cameraRayDir = SpatialVector(camera.CameraRayDir),
            rayPos = SpatialVector(origin),
            rayDir = SpatialVector(direction),
            rayMask = mask,
            rayDistance = distance,
            aimDot = Vector3.Dot(direction, (center - origin).normalized),
            rayFirstHit = hit ? ShotHit(observed) : null,
            player = SpatialVector(player.Position),
            center = SpatialVector(center)
        });
    }

    /// <summary>The gear identity of a backpack item: the game's own offline gear record on the item's id range, else its catalog id.
    /// The planned gun and the held gun are both read from the backpack item, so the two sides always spell the same identity.</summary>
    internal static string GearIdentity(BackpackItem? item)
    {
        var range = item?.GearIDRange;
        if (range == null) return "none";
        return string.IsNullOrEmpty(range.PlayfabItemInstanceId) ? range.PlayfabItemId ?? "none" : range.PlayfabItemInstanceId;
    }

    /// <summary>The identity of the backpack item that owns the held gun instance.</summary>
    internal static string GearOf(PlayerAgent player, BulletWeapon gun)
    {
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var pack) || pack?.Slots == null) return "none";
        for (var i = 0; i < pack.Slots.Length; i++)
        {
            var item = pack.Slots[i];
            if (item?.Instance != null && item.Instance.Pointer == gun.Pointer) return GearIdentity(item);
        }
        return "none";
    }

    /// <summary>The backpack slot whose item has this gear identity, or -1 when the backpack holds no such gear.</summary>
    private static int SlotOfGear(PlayerAgent player, string gear)
    {
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var pack) || pack?.Slots == null) return -1;
        for (var i = 0; i < pack.Slots.Length; i++)
            if (pack.Slots[i] is { } item && GearIdentity(item) == gear) return i;
        return -1;
    }

    private static BulletWeapon? Held(PlayerAgent player)
        => player.Inventory?.WieldedItem?.TryCast<BulletWeapon>() ?? player.FPItemHolder?.WieldedItem?.TryCast<BulletWeapon>();

    private static void CleanupModelShot()
    {
        if (!_modelShotLease) return;
        _modelShotLease = false;
        HeldInputLease.Release(InputAction.Fire, false);
        HeldInputLease.Restore();
        HeldInputLease.DisarmGuard();
    }

    private static Transform ModelRoot(Collider collider)
        => collider.name.StartsWith("Forge model shot collider", StringComparison.Ordinal) ? collider.transform.parent : collider.transform;

    private static bool OwnedBy<T>(Collider hit) where T : Component
        => hit.GetComponentInParent(Il2CppInterop.Runtime.Il2CppType.Of<T>()) != null;

    /// <summary>The most stand points kept and the ring walked to find them: nearest radii first, each ring from the same start
    /// angle, so the order is the same on every run.</summary>
    private const int MaxStands = 6, StandSteps = 24;
    private static readonly float[] StandRadii = { 1.6f, 2.2f, 1.1f, 2.8f, 3.4f, 4.2f, 5.5f, 7f };
    // Heights below the target's centre a ring point is also sampled at when the target's own height has no landing, and the most
    // landing samples one search may spend (so a target with no floor anywhere near ends the search within a bounded frame cost).
    private static readonly float[] StandDrops = { 0f, -1.5f, -3f, -4.5f };
    // One probe per ring point and height: the cap is the whole search (a smaller cap cut a wall-side target's search off at
    // radius 3.4 of 7 with probeLimit reached and no stand tried beyond it).
    private static readonly int MaxStandProbes = StandRadii.Length * StandSteps * StandDrops.Length;
    // Landings already ray-tested in this search (different ring points often land on the same voxel centre); reused, never reallocated.
    private static readonly Vector3[] SeenLandings = new Vector3[StandRadii.Length * StandSteps];

    /// <summary>Walks rings around the target and keeps up to <see cref="MaxStands"/> distinct landings whose line from a standing
    /// eye reaches the target collider itself. Every blocked landing is recorded with what blocked it.</summary>
    private static int FindStandPoints(PlayerAgent player, eDimensionIndex dimension, Func<Collider, Vector3, bool> isTarget, Vector3 center,
        SpatialRecord record, Vector3[] stands)
    {
        int mask = LayerManager.MASK_BULLETWEAPON_RAY;
        int tried = 0, noLanding = 0, noNode = 0, blocked = 0, duplicate = 0, found = 0, seen = 0, probes = 0;
        object? firstBlock = null;
        foreach (float radius in StandRadii)
            for (int step = 0; step < StandSteps && found < stands.Length; step++)
            {
                double angle = step * Math.PI / (StandSteps / 2);
                var around = center + new Vector3((float)Math.Cos(angle) * radius, 0f, (float)Math.Sin(angle) * radius);
                tried++;
                // Course-node proximity accepts points outside the voxel/area chain that TryWarpTo requires. Resolve
                // the game's own landing first, then validate and sight from the position the warp will actually use.
                // A target hanging above the floor (a panel, a light, a high prop) has no walkable surface at its own height:
                // the same ring point is also tried at lower heights, so the floor under it can be the landing. The sight
                // check below is unchanged; a landing only counts when its line reaches the target collider itself.
                var landing = default(Vector3);
                var landed = false;
                foreach (float drop in StandDrops)
                {
                    if (probes++ >= MaxStandProbes) break;
                    var probe = around + Vector3.up * drop;
                    if (!PlayerAgent.SampleWarpPosition(dimension, probe, out landing)
                        || !float.IsFinite(landing.x) || !float.IsFinite(landing.y) || !float.IsFinite(landing.z)) continue;
                    landed = true; around = probe; break;
                }
                if (!landed) { noLanding++; if (probes >= MaxStandProbes) break; continue; }
                bool repeat = false;
                for (int index = 0; index < seen && !repeat; index++) repeat = (SeenLandings[index] - landing).sqrMagnitude < .0025f;
                if (repeat) { duplicate++; continue; }
                SeenLandings[seen++] = landing;
                if (!AIG_CourseNode.TryGetCourseNode(dimension, landing, .05f, out var node) || node == null
                    || node.WasCollected || node.m_dimension == null || node.m_dimension.DimensionIndex != dimension)
                { noNode++; continue; }
                var eye = landing + Vector3.up * 1.65f;
                var toward = center - eye;
                var direction = toward.normalized;
                var distance = toward.magnitude + .75f;
                bool rayHit = Physics.Raycast(eye, direction, out var hit, distance, mask);
                if (!rayHit || hit.collider == null || !isTarget(hit.collider, hit.point))
                {
                    blocked++;
                    record.Samples.Add(new
                    {
                        phase = "stand-blocked",
                        position = SpatialVector(landing),
                        collider = rayHit && hit.collider != null ? hit.collider.name : "none",
                        distance = rayHit ? hit.distance : 0f
                    });
                    firstBlock ??= new
                    {
                        requested = SpatialVector(around),
                        position = SpatialVector(landing),
                        origin = SpatialVector(eye),
                        end = SpatialVector(center),
                        direction = SpatialVector(direction),
                        distance,
                        mask,
                        hit = rayHit ? ShotHit(hit) : null
                    };
                    continue;
                }
                stands[found++] = landing;
                record.Samples.Add(new
                {
                    phase = "stand",
                    tried,
                    radius,
                    requested = SpatialVector(around),
                    position = SpatialVector(landing),
                    eye = SpatialVector(eye),
                    rayFirstHit = hit.collider.name + "/layer" + hit.collider.gameObject.layer,
                    distance = toward.magnitude
                });
            }
        if (found == 0) record.Samples.Add(new
        {
            phase = "stand-failed",
            tried,
            probes,
            probeLimit = MaxStandProbes,
            noLanding,
            noNode,
            blocked,
            duplicate,
            firstBlock,
            targetHeight = center.y,
            playerHeight = player.Position.y
        });
        return found;
    }
}
