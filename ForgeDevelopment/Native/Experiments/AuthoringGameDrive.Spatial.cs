using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChainedPuzzles;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Development-only commands. The experiment engine owns polling, cancellation and the persisted step
// records; native locomotion, scanning and expedition settlement remain the only gameplay executors.
internal static partial class AuthoringGameDrive
{
    private const string RecordWriteFailedCode = "drive.record-write-failed";
    private static string Detail(Exception error) => error.GetType().Name + ":" + error.Message;
    internal sealed class SpatialRecord
    {
        internal readonly string File = "authoring-drive-" + Guid.NewGuid().ToString("N") + ".forge";
        internal readonly string Session = RecSession.SessionId;
        internal readonly long World = RecTime.WorldEpoch;
        internal bool Queued;
        internal string Operation = "", NativePath = "";
        internal JsonElement? Request;
        internal object? Before, After;
        internal bool Issued;
        internal readonly List<object> Samples = new();
        // The few facts a caller judges without loading the record (a teleport's landing, the variables read). It goes into the
        // receipt, so it stays bounded well below the step summary's 4096 characters.
        internal object? Summary;

        internal ExperimentCallResult Result(string code, Exception? failure = null)
        {
            string status = failure == null ? "observed" : failure is OperationCanceledException ? "aborted" : "failed";
            string commit = failure == null ? "confirmed" : Issued ? "unknown" : "none";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "forge.development-drive-record",
                schemaVersion = "1.0.0",
                record = File,
                session = Session,
                worldEpoch = World,
                operation = Operation,
                code,
                status,
                commitState = commit,
                nativePath = NativePath,
                request = Request,
                before = Before,
                after = After,
                samples = Samples,
                error = failure == null ? null : new { type = failure.GetType().Name, message = failure.Message }
            });
            string? recordError = null;
            try
            {
                Queued = Session.Length > 0 && RecSession.SessionId == Session && RecSession.WriteDocument(File, bytes);
                if (!Queued) recordError = "drive.record-not-queued";
            }
            catch (Exception error) { recordError = RecordWriteFailedCode + ":" + Detail(error); }
            if (ReferenceEquals(_spatialPending, this)) _spatialPending = null;
            // Full snapshots never go into the 4096-character step summary. The client checks these exact
            // document bytes after the existing recorder drain; a queue receipt alone is not durable evidence.
            var json = JsonSerializer.Serialize(new
            {
                format = "forge.development-drive-receipt",
                schemaVersion = "1.0.0",
                operation = Operation,
                code = recordError != null && failure == null ? "drive.record-not-queued" : code,
                status = recordError == null ? status : "failed",
                commitState = commit,
                record = Queued ? File : null,
                sha256 = Queued ? ForgeCore.Hash.Sha256Hex(bytes) : null,
                session = Session,
                worldEpoch = World,
                recordError,
                summary = Summary
            });
            return failure == null && recordError == null ? ExperimentCallResult.Done(json, json) : ExperimentCallResult.Failed(json);
        }

        internal ExperimentCallResult Guard(Func<ExperimentCallResult> action)
        {
            try { return action(); }
            catch (Exception error)
            {
                var code = error.Message.StartsWith("drive.", StringComparison.Ordinal) ? error.Message
                    : error is JsonException ? "drive.spec-json-invalid" : "drive.native-exception";
                return Result(code, error);
            }
        }
    }

    private sealed record SpatialPlayer(PlayerAgent Player, NativeLife Actor);
    private sealed record ExitSite(ChainedPuzzleInstance Puzzle, CP_Bioscan_Core Scan, CP_PlayerScanner Scanner, LG_Area Area);
    private static SpatialRecord? _spatialPending;
    private static bool IsSpatialOperation(string name) => name is "drive.player.teleport" or "drive.player.unseat"
        or "drive.scan.read" or "drive.extraction.complete" or "drive.model.shoot" or "drive.variables.read";

    private static ExperimentCallResult CallSpatial(ExperimentTargetResult target, ExperimentStep step)
    {
        if (_spatialPending != null) return ExperimentCallResult.Failed("drive.native-readback-already-pending");
        var record = new SpatialRecord { Operation = step.Name };
        _spatialPending = record;
        return record.Guard(() =>
        {
            RecObservation.RequireNativeRead();
            RequireAuthoring();
            if (target.IsSet || target.Value as Type != typeof(AuthoringGameDrive)
                || step.ResultArgument || step.ArgumentNames.Any(name => name.Length != 0))
                throw new InvalidOperationException("drive.static-target-and-positional-arguments-required");
            int count = step.Name == "drive.player.unseat" ? 0 : 1;
            if (step.Arguments.Count != count) throw new ArgumentException("drive.argument-count");
            if (count == 0) return Unseat(record);
            if (step.Arguments[0].ValueKind != JsonValueKind.String) throw new ArgumentException("drive.string-argument");
            var json = step.Arguments[0].GetString()!;
            if (json.Length > 8192) throw new ArgumentException("drive.spec-size");
            using var doc = JsonDocument.Parse(json);
            var spec = doc.RootElement;
            record.Request = spec.Clone();
            if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("drive.spec-object-required");
            return step.Name switch
            {
                "drive.player.teleport" => Teleport(record, spec),
                "drive.scan.read" => ReadScans(record, spec),
                "drive.model.shoot" => ShootModel(record, spec),
                "drive.variables.read" => ReadVariables(record, spec),
                _ => Extract(record, spec)
            };
        });
    }

    private static void CleanupSpatial()
    {
        if (_spatialPending is not { } record) return;
        record.Result("drive.action-aborted", new OperationCanceledException("drive.action-aborted"));
        if (!record.Queued) throw new InvalidOperationException("drive.aborted-record-not-queued");
    }

    private static void SpatialFields(JsonElement spec, params string[] keys)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("drive.spec-object-required");
        var fields = spec.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Length != keys.Length || keys.Any(key => fields.Count(field => field == key) != 1))
            throw new ArgumentException("drive.spec-unknown-duplicate-or-missing-field");
        if (spec.GetProperty("schemaVersion").ValueKind != JsonValueKind.String
            || spec.GetProperty("schemaVersion").GetString() != "1.0.0")
            throw new ArgumentException("drive.spec-version-regenerate-required");
    }

    private static Vector3 SpatialVector(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new ArgumentException("drive.spec-position");
        var point = new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        if (!SpatialFinite(point)) throw new ArgumentException("drive.spec-position-nonfinite");
        return point;
    }

    private static bool SpatialFinite(Vector3 point) => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
    private static float[] SpatialVector(Vector3 point) => new[] { point.x, point.y, point.z };
    private static SpatialPlayer SpatialLocal()
    {
        var player = LocalPlayer();
        return new(player, NativeLife.Of(player)!);
    }

    private static PlayerAgent SpatialCurrent(SpatialPlayer lease)
    {
        RecObservation.RequireNativeRead();
        RequireAuthoring();
        var player = LocalPlayer();
        if (player.Pointer != lease.Player.Pointer || !lease.Actor.IsCurrent(player))
            throw new InvalidOperationException("drive.player-life-or-world-changed");
        return player;
    }


    private static object SpatialPose(PlayerAgent player)
    {
        if (!SpatialFinite(player.Position)) throw new InvalidOperationException("drive.player-position-nonfinite");
        var area = player.CourseNode?.m_area;
        var zone = area?.m_zone;
        return new
        {
            world = RecTime.WorldEpoch,
            frame = RecTime.Frame,
            actor = NativeLife.Of(player),
            player = player.Pointer.ToInt64(),
            position = SpatialVector(player.Position),
            dimension = (int)player.DimensionIndex,
            area = area == null ? (long?)null : Convert.ToInt64(area.UID),
            zone = zone?.LocalIndex,
            layer = zone == null ? null : zone.m_layer.m_type.ToString(),
            locomotion = player.Locomotion?.m_currentStateEnum.ToString(),
            controllerEnabled = player.PlayerCharacterController?.m_characterController?.enabled
        };
    }

    private static void SpatialWarp(SpatialRecord record, PlayerAgent player, eDimensionIndex dimension, Vector3 point)
    {
        var locomotion = player.Locomotion;
        if (locomotion == null || locomotion.WasCollected || player.m_warpableStates == null
            || !player.m_warpableStates.Contains(locomotion.m_currentStateEnum))
            throw new InvalidOperationException("drive.teleport-native-state-refused");
        record.NativePath = "PlayerAgent.RequestWarpToSync";
        record.Issued = true;
        // The host's native sync entry; the game lands the player on the navigation node it samples under the point.
        player.RequestWarpToSync(dimension, point, player.transform.forward, PlayerAgent.WarpOptions.None);
    }

    private const float LandingTolerance = .05f;

    private static ExperimentCallResult Teleport(SpatialRecord record, JsonElement spec)
    {
        RequireHostLevel();
        Vector3 point;
        eDimensionIndex dimension;
        LG_Area? stationArea = null;
        if (spec.TryGetProperty("zone", out var zoneAddress))
        {
            SpatialFields(spec, "schemaVersion", "zone", "area");
            if (!ExperimentTargetSpec.TryParse("zone:" + zoneAddress.GetString(), out var selector)
                || ExperimentTargeting.Resolve(selector).Value is not LG_Zone zone)
                throw new ArgumentException("drive.teleport-zone-unresolved");
            int areaIndex = spec.GetProperty("area").GetInt32();
            if (zone.m_areas == null || areaIndex < 0 || areaIndex >= zone.m_areas.Count
                || zone.m_areas[areaIndex]?.m_courseNode == null)
                throw new ArgumentException("drive.teleport-area-station-unresolved");
            stationArea = zone.m_areas[areaIndex];
            point = stationArea.m_courseNode!.GetRandomPositionInside();
            dimension = zone.DimensionIndex;
        }
        else if (spec.TryGetProperty("exit", out _))
        {
            SpatialFields(spec, "schemaVersion", "exit", "offset");
            var site = SpatialExit(spec.GetProperty("exit").GetString());
            point = site.Scanner.transform.position + SpatialVector(spec.GetProperty("offset"));
            dimension = site.Area.m_zone.DimensionIndex;
        }
        else
        {
            SpatialFields(spec, "schemaVersion", "dimension", "position");
            int index = spec.GetProperty("dimension").GetInt32();
            if (!Enum.IsDefined(typeof(eDimensionIndex), index)) throw new ArgumentException("drive.spec-dimension");
            dimension = (eDimensionIndex)index;
            point = SpatialVector(spec.GetProperty("position"));
        }
        if (!SpatialFinite(point) || !Dimension.TryGetCourseNodeFromPos(point, out var node)
            || node?.m_area == null || node.m_dimension == null || node.m_dimension.DimensionIndex != dimension
            || stationArea != null && node.m_area.Pointer != stationArea.Pointer)
            throw new InvalidOperationException("drive.teleport-destination-area-unresolved");
        var area = node.m_area;
        // The game does not land on the requested point: PlayerAgent.TryWarpTo (which the warp request runs) puts the player on the
        // navigation voxel node under it, the same lookup as PlayerAgent.SampleWarpPosition (a grid of about half a metre). That
        // sample is the point the readback is held to.
        if (!PlayerAgent.SampleWarpPosition(dimension, point, out var landing) || !SpatialFinite(landing))
            throw new InvalidOperationException("drive.teleport-destination-area-unresolved");
        // The node may sit just across an area seam from the requested point; the player is read back in the landing's area.
        if (stationArea == null && Dimension.TryGetCourseNodeFromPos(landing, out var landingNode) && landingNode?.m_area != null)
            area = landingNode.m_area;
        var lease = SpatialLocal();
        record.Before = SpatialPose(lease.Player);
        record.Samples.Add(new { requestedPosition = SpatialVector(point), sampledLanding = SpatialVector(landing), dimension = (int)dimension, area = area.UID });
        record.Summary = new { requestedPosition = SpatialVector(point), sampledLanding = SpatialVector(landing) };
        SpatialWarp(record, lease.Player, dimension, point);
        float deadline = Time.realtimeSinceStartup + 10f;
        ExperimentCallResult Poll() => record.Guard(() =>
        {
            RequireHostLevel();
            var player = SpatialCurrent(lease);
            record.After = SpatialPose(player);
            // The sample is a voxel node's centre, which floats above the floor; the character controller then settles the feet onto
            // the floor collider under it, and its skin may shift them by a few centimetres. The landing is accepted grounded within
            // LandingTolerance of the sample horizontally and, vertically, within the larger of the controller's step offset and the
            // reach the level layer allows between the navigation data and its floor (ForgeCore.Level.SampleReach).
            var controller = player.PlayerCharacterController?.m_characterController;
            var offset = player.Position - landing;
            if (player.DimensionIndex == dimension && player.CourseNode?.m_area?.Pointer == area.Pointer
                && controller != null && !controller.WasCollected && controller.isGrounded
                && new Vector2(offset.x, offset.z).magnitude <= LandingTolerance
                && Mathf.Abs(offset.y) <= Mathf.Max(controller.stepOffset, ForgeCore.Level.SampleReach))
            {
                record.Samples.Add(new { grounded = true, landed = SpatialVector(player.Position), settledHeight = offset.y, stepOffset = controller.stepOffset });
                record.Summary = new { requestedPosition = SpatialVector(point), sampledLanding = SpatialVector(landing), landed = SpatialVector(player.Position) };
                return record.Result("drive.teleport-position-and-area-confirmed");
            }
            if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("drive.teleport-position-or-area-not-observed");
            return ExperimentCallResult.Await(Poll);
        });
        return ExperimentCallResult.Await(Poll);
    }

    private static ExperimentCallResult Unseat(SpatialRecord record)
    {
        RequireHostLevel();
        var lease = SpatialLocal();
        var locomotion = lease.Player.Locomotion;
        if (locomotion == null || locomotion.WasCollected) throw new InvalidOperationException("drive.unseat-locomotion-unavailable");
        record.Before = SpatialPose(lease.Player);
        if (locomotion.m_currentStateEnum != PlayerLocomotion.PLOC_State.InElevator)
        {
            record.After = record.Before;
            return record.Result("drive.player-already-unseated");
        }
        if (ElevatorRide.ElevatorRideInProgress) throw new InvalidOperationException("drive.unseat-elevator-still-moving");
        record.NativePath = "PlayerLocomotion.ChangeState -> PLOC_InElevator.Exit";
        record.Issued = true;
        // The state machine invokes the game's complete seat exit (landing point, controls, camera and gear).
        // Calling Exit directly or writing m_currentStateEnum would bypass that lifecycle.
        locomotion.ChangeState(PlayerLocomotion.PLOC_State.Stand);
        float deadline = Time.realtimeSinceStartup + 10f;
        ExperimentCallResult Poll() => record.Guard(() =>
        {
            RequireHostLevel();
            var player = SpatialCurrent(lease);
            record.After = SpatialPose(player);
            if (player.Locomotion == null || player.PlayerCharacterController?.m_characterController == null)
                throw new InvalidOperationException("drive.unseat-native-readback-missing");
            if (player.Locomotion.m_currentStateEnum != PlayerLocomotion.PLOC_State.InElevator
                && player.PlayerCharacterController.m_characterController.enabled)
                return record.Result("drive.unseat-native-state-confirmed");
            if (Time.realtimeSinceStartup >= deadline) throw new InvalidOperationException("drive.unseat-native-state-not-observed");
            return ExperimentCallResult.Await(Poll);
        });
        return ExperimentCallResult.Await(Poll);
    }

    private static ExitSite SpatialExit(string? kind)
    {
        if (WardenObjectiveManager.Current == null) throw new InvalidOperationException("drive.exit-manager-unavailable");
        ChainedPuzzleInstance? puzzle;
        LG_Area? area;
        switch (kind)
        {
            case "custom":
                var geo = WardenObjectiveManager.m_customGeoExitWinConditionItem?.TryCast<LG_LevelExitGeo>();
                puzzle = geo?.m_puzzle; area = geo?.m_exitArea; break;
            case "elevator":
                var landing = WardenObjectiveManager.m_elevatorExitWinConditionItem?.TryCast<ElevatorShaftLanding>();
                puzzle = landing?.m_puzzle; area = landing?.SpawnArea; break;
            default: throw new ArgumentException("drive.exit-kind");
        }
        if (puzzle == null || puzzle.WasCollected || !puzzle.IsExitPuzzle || area?.m_zone == null)
            throw new InvalidOperationException("drive.exit-host-or-area-unavailable");
        var scans = new List<CP_Bioscan_Core>();
        for (int index = 0; index < puzzle.NRofPuzzles(); index++)
        {
            var scan = puzzle.GetPuzzle(index)?.TryCast<CP_Bioscan_Core>();
            if (scan != null && !scan.WasCollected && scan.PlayerScanner?.IsExitScan == true) scans.Add(scan);
        }
        if (scans.Count != 1) throw new InvalidOperationException("drive.exit-scan-missing-or-ambiguous");
        var core = scans[0];
        if (core.PlayerScanner?.TryCast<CP_PlayerScanner>() is not { } scanner || scanner.WasCollected
            || core.CourseNode?.m_area?.Pointer != area.Pointer)
            throw new InvalidOperationException("drive.exit-scan-area-or-scanner-unavailable");
        return new(puzzle, core, scanner, area);
    }

    private static object SpatialScan(CP_Bioscan_Core scan, Vector3 center)
    {
        var area = scan.CourseNode?.m_area;
        if (area?.m_zone == null || scan.PlayerScanner?.TryCast<CP_PlayerScanner>() is not { } scanner || scanner.WasCollected)
            throw new InvalidOperationException("drive.scan-native-readback-missing");
        var state = scan.State;
        if (!SpatialFinite(scanner.transform.position) || !float.IsFinite(state.progress)
            || !float.IsFinite(scanner.Radius) || scanner.Radius <= 0)
            throw new InvalidOperationException("drive.scan-native-readback-invalid");
        return new
        {
            scan = scan.Pointer.ToInt64(),
            parent = scan.m_parentGUID,
            puzzleIndex = scan.m_puzzleIndex,
            position = SpatialVector(scanner.transform.position),
            distance = Vector3.Distance(center, scanner.transform.position),
            dimension = (int)area.m_zone.DimensionIndex,
            layer = area.m_zone.m_layer.m_type.ToString(),
            zone = area.m_zone.LocalIndex,
            area = area.UID,
            radius = scanner.Radius,
            progress = state.progress,
            status = state.status.ToString(),
            finished = scan.IsFinished(),
            playersInScan = state.playersInScan,
            playersMax = state.playersMax,
            isExit = scanner.IsExitScan,
            playerLookups = SpatialMembers(state)
        };
    }

    private static ulong[] SpatialMembers(pBioscanState state) => new[]
        { state.playerInScan1.lookup, state.playerInScan2.lookup, state.playerInScan3.lookup, state.playerInScan4.lookup };

    private static ExperimentCallResult ReadScans(SpatialRecord record, JsonElement spec)
    {
        if (GameStateManager.CurrentStateName != eGameStateName.InLevel) throw new InvalidOperationException("drive.level-required");
        Vector3 center;
        eDimensionIndex dimension;
        if (spec.TryGetProperty("exit", out _))
        {
            SpatialFields(spec, "schemaVersion", "exit", "radius");
            var site = SpatialExit(spec.GetProperty("exit").GetString());
            center = site.Scanner.transform.position; dimension = site.Area.m_zone.DimensionIndex;
        }
        else
        {
            SpatialFields(spec, "schemaVersion", "dimension", "center", "radius");
            int index = spec.GetProperty("dimension").GetInt32();
            if (!Enum.IsDefined(typeof(eDimensionIndex), index)) throw new ArgumentException("drive.spec-dimension");
            dimension = (eDimensionIndex)index; center = SpatialVector(spec.GetProperty("center"));
        }
        float radius = spec.GetProperty("radius").GetSingle();
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentException("drive.spec-radius");
        var rows = new List<object>();
        foreach (var scan in UnityEngine.Object.FindObjectsOfType<CP_Bioscan_Core>())
        {
            if (scan == null || scan.WasCollected) throw new InvalidOperationException("drive.scan-enumeration-stale");
            if (scan.PlayerScanner?.TryCast<CP_PlayerScanner>() is not { } scanner || scanner.WasCollected)
                throw new InvalidOperationException("drive.scan-native-readback-missing");
            if (!SpatialFinite(scanner.transform.position)) throw new InvalidOperationException("drive.scan-native-position-invalid");
            if (Vector3.Distance(center, scanner.transform.position) > radius) continue;
            if (scan.CourseNode?.m_area?.m_zone == null) throw new InvalidOperationException("drive.scan-native-area-missing");
            if (scan.CourseNode.m_area.m_zone.DimensionIndex == dimension) rows.Add(SpatialScan(scan, center));
        }
        record.After = new
        {
            world = RecTime.WorldEpoch,
            frame = RecTime.Frame,
            dimension = (int)dimension,
            center = SpatialVector(center),
            radius,
            scans = rows
        };
        if (rows.Count == 0) throw new InvalidOperationException("drive.scan-not-found-in-radius");
        record.NativePath = "CP_Bioscan_Core.State + CP_PlayerScanner.Radius";
        return record.Result("drive.scan-radius-readback");
    }

    private static ExperimentCallResult Extract(SpatialRecord record, JsonElement spec)
    {
        SpatialFields(spec, "schemaVersion", "exit", "timeoutSeconds");
        RequireHostLevel();
        int timeout = spec.GetProperty("timeoutSeconds").GetInt32();
        if (timeout is < 1 or > 3600) throw new ArgumentException("drive.spec-timeout");
        string kind = spec.GetProperty("exit").GetString()!;
        var site = SpatialExit(kind);
        var lease = SpatialLocal();
        if (!site.Puzzle.IsActive || site.Scan.State.status == eBioscanStatus.Disabled)
            throw new InvalidOperationException("drive.extraction-native-gate-not-active");
        if (site.Scan.IsFinished()) throw new InvalidOperationException("drive.extraction-scan-already-finished");
        if (lease.Player.DimensionIndex != site.Area.m_zone.DimensionIndex || lease.Player.CourseNode?.m_area?.Pointer != site.Area.Pointer
            || Vector3.Distance(lease.Player.Position, site.Scanner.transform.position) > 40f)
            throw new InvalidOperationException("drive.extraction-prepare-in-exit-area-required");
        if (lease.Player.Locomotion?.m_currentStateEnum == PlayerLocomotion.PLOC_State.InElevator
            || lease.Player.PlayerCharacterController?.m_characterController?.enabled != true)
            throw new InvalidOperationException("drive.extraction-unseat-required");
        if (Vector3.Distance(lease.Player.Position, site.Scanner.transform.position) < site.Scanner.Radius)
            throw new InvalidOperationException("drive.extraction-start-outside-scan-required");
        object Sample(PlayerAgent player) => new
        {
            player = SpatialPose(player),
            scan = SpatialScan(site.Scan, player.Position),
            gameState = GameStateManager.CurrentStateName.ToString(),
            puzzleActive = site.Puzzle.IsActive
        };
        record.NativePath = "PlayerCharacterController.ManualMoveToWithCollision -> native exit scan -> ExpeditionSuccess";
        record.Before = Sample(lease.Player);
        float started = Time.realtimeSinceStartup, nextSample = started, progressAt = started;
        float best = Vector3.Distance(lease.Player.Position, site.Scanner.transform.position), bestProgress = site.Scan.State.progress;
        int lastFrame = -1;
        bool joined = false, finished = false;
        ExperimentCallResult Poll() => record.Guard(() =>
        {
            var player = SpatialCurrent(lease);
            var current = SpatialExit(kind);
            if (current.Puzzle.Pointer != site.Puzzle.Pointer || current.Scan.Pointer != site.Scan.Pointer
                || current.Scanner.Pointer != site.Scanner.Pointer || current.Area.Pointer != site.Area.Pointer)
                throw new InvalidOperationException("drive.extraction-native-identity-changed");
            record.After = Sample(player);
            var state = GameStateManager.CurrentStateName;
            if (state == eGameStateName.ExpeditionFail || state == eGameStateName.ExpeditionAbort)
                throw new InvalidOperationException("drive.extraction-native-expedition-failed");
            if (player.DimensionIndex != site.Area.m_zone.DimensionIndex || player.CourseNode?.m_area?.Pointer != site.Area.Pointer)
                throw new InvalidOperationException("drive.extraction-player-left-exit-area");
            float distance = Vector3.Distance(player.Position, site.Scanner.transform.position);
            if (player.Owner == null || player.Owner.Lookup == 0) throw new InvalidOperationException("drive.extraction-player-owner-unavailable");
            bool member = SpatialMembers(site.Scan.State).Contains(player.Owner.Lookup);
            if (member && distance < site.Scanner.Radius) joined = true;
            finished = joined && site.Scan.IsFinished() && site.Scan.State.status == eBioscanStatus.Finished;
            if (Time.realtimeSinceStartup >= nextSample || state == eGameStateName.ExpeditionSuccess)
            {
                record.Samples.Add(record.After);
                nextSample = Time.realtimeSinceStartup + 1f;
            }
            if (state == eGameStateName.ExpeditionSuccess)
            {
                if (!joined || !finished) throw new InvalidOperationException("drive.extraction-entry-or-completion-not-observed");
                return record.Result("drive.extraction-native-success-confirmed");
            }
            RequireHostLevel();
            if (Time.realtimeSinceStartup - started >= timeout) throw new InvalidOperationException("drive.extraction-native-success-timeout");
            if (!finished && (joined || distance > .1f) && Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount;
                // Before the scan counts the player, progress is the approach; once it does, it is the scan's own progress. The scanner's
                // collider stops the body short of its centre (0.61 m in the 11-06 run), so the approach ends at the join, not at the centre.
                float scanProgress = site.Scan.State.progress;
                if (distance < best - .01f) { best = distance; progressAt = Time.realtimeSinceStartup; }
                if (joined && scanProgress > bestProgress) { bestProgress = scanProgress; progressAt = Time.realtimeSinceStartup; }
                if (Time.realtimeSinceStartup - progressAt >= 3f) throw new InvalidOperationException("drive.extraction-approach-no-progress");
                if (joined) return ExperimentCallResult.Await(Poll);
                float delta = Time.deltaTime;
                if (!float.IsFinite(delta) || delta <= 0) throw new InvalidOperationException("drive.extraction-frame-step-invalid");
                var movement = (site.Scanner.transform.position - player.Position) / distance
                    * MathF.Min(distance, 2.5f * MathF.Min(delta, .1f));
                var controller = player.PlayerCharacterController;
                if (controller?.m_characterController?.enabled != true
                    || player.Locomotion?.m_currentStateEnum == PlayerLocomotion.PLOC_State.InElevator)
                    throw new InvalidOperationException("drive.extraction-native-controller-unavailable");
                record.Issued = true;
                controller.ManualMoveToWithCollision(movement);
            }
            return ExperimentCallResult.Await(Poll);
        });
        return ExperimentCallResult.Await(Poll);
    }
}
