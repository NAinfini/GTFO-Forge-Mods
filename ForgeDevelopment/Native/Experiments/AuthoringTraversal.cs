using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Called only by the existing experiment command runner; no independent update loop or input injection.
internal static class AuthoringTraversal
{
    private static Run? _run;
    private sealed class Run
    {
        internal AuthoringTraversalRoute Route = null!;
        internal PlayerAgent Player = null!;
        internal NativeLife PlayerIdentity = null!;
        internal NativeLife Identity = null!;
        internal string Session = "";
        internal string File = "";
        internal EnemyAgent? Enemy;
        internal long Pointer;
        internal long ControllerPointer;
        internal long ColliderPointer;
        internal long World;
        internal int Next = 1, LastFrame = -1;
        internal float Started, ProgressAt, BestDistance;
        internal TraversalPoint Last;
        internal readonly List<object> Samples = new();
        internal object? Arrival;
        internal float? ReachedAt;
        internal int? ReachedFrame;
        internal string? Terminal;
        internal string Reason = "";
        internal bool Written;
    }

    internal static string BeginPlayer(string routeJson) => Begin(routeJson, null);
    internal static string BeginEnemy(int globalId, string routeJson)
    {
        if (globalId < 0 || globalId > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(globalId));
        Gate();
        var matches = UnityEngine.Object.FindObjectsOfType<EnemyAgent>()
            .Where(e => e != null && !e.WasCollected && e.GlobalID == globalId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("traversal-enemy-missing-or-ambiguous");
        return Begin(routeJson, matches[0]);
    }

    private static string Begin(string json, EnemyAgent? enemy)
    {
        Gate();
        if (_run != null) throw new InvalidOperationException("traversal-already-active; finish or clean up first");
        var route = AuthoringTraversalRoute.Parse(json);
        if (!Enum.IsDefined(typeof(eDimensionIndex), route.Dimension)) throw new ArgumentException("traversal-dimension-invalid");
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive) throw new InvalidOperationException("traversal-local-player-unavailable");
        var playerIdentity = NativeLife.Of(player) ?? throw new InvalidOperationException("traversal-player-identity-unavailable");
        var identity = enemy == null ? playerIdentity : NativeLife.Of(enemy)
            ?? throw new InvalidOperationException("traversal-enemy-identity-unavailable");
        var run = new Run
        {
            Route = route,
            Player = player,
            PlayerIdentity = playerIdentity,
            Identity = identity,
            Enemy = enemy,
            Pointer = enemy == null ? player.Pointer.ToInt64() : enemy.Pointer.ToInt64(),
            World = RecTime.WorldEpoch,
            Session = RecSession.SessionId,
            File = "authoring-traversal-" + Guid.NewGuid().ToString("N") + ".json",
            Started = Time.realtimeSinceStartup,
            ProgressAt = Time.realtimeSinceStartup
        };
        _run = run;
        return Execute(run, () =>
        {
            Validate(run);
            var position = Point(enemy == null ? player.Position : enemy.Position);
            if (!position.Finite || position.Distance(route.Waypoints[0]) > route.Tolerance)
                throw new InvalidOperationException("traversal-start-not-at-first-waypoint; prepare separately");
            run.Last = position; run.BestDistance = position.Distance(route.Waypoints[1]);
            if (enemy == null)
            {
                run.ControllerPointer = player.PlayerCharacterController.Pointer.ToInt64();
                run.ColliderPointer = player.PlayerCharacterController.m_characterController.Pointer.ToInt64();
            }
            Sample(run, "begin", null, null);
            if (enemy != null) enemy.AI.SetTarget(player); // Target was already this player; never replace another target.
            return "traversal-started:" + run.File;
        });
    }

    internal static string TickPlayer() => Tick(false);
    internal static string TickEnemy() => Tick(true);
    private static string Tick(bool enemy)
    {
        var run = Require(enemy);
        return Execute(run, () =>
        {
            if (run.Arrival != null)
            {
                ValidateIdentity(run);
                return "traversal-already-reached; finish required";
            }
            Validate(run);
            if (Time.frameCount == run.LastFrame) throw new InvalidOperationException("traversal-duplicate-frame");
            float step = run.Route.Step(Time.deltaTime);
            if (Time.realtimeSinceStartup - run.Started > 60f) throw new InvalidOperationException("traversal-timeout");
            if (run.Samples.Count >= 8192) throw new InvalidOperationException("traversal-sample-budget");
            run.LastFrame = Time.frameCount;
            var before = Position(run);
            int previousNext = run.Next;
            TraversalPoint? requested = null;
            // Observe native movement since the previous sample before submitting any new movement.
            if (!before.Finite) throw new InvalidOperationException("traversal-position-nonfinite");
            if (before.Distance(run.Last) > 1f) throw new InvalidOperationException("traversal-position-discontinuity");
            run.Next = run.Route.Arrive(run.Next, before);
            if (!enemy && run.Next < run.Route.Waypoints.Length)
            {
                var destination = run.Route.Waypoints[run.Next];
                float distance = before.Distance(destination), amount = MathF.Min(step, distance);
                requested = new((destination.X - before.X) / distance * amount,
                    (destination.Y - before.Y) / distance * amount, (destination.Z - before.Z) / distance * amount);
                run.Player.PlayerCharacterController.ManualMoveToWithCollision(Vector(requested.Value));
            }
            var after = Position(run);
            if (!after.Finite) throw new InvalidOperationException("traversal-position-nonfinite");
            Sample(run, "tick", requested, before);
            if (after.Distance(before) > .75f) throw new InvalidOperationException("traversal-native-move-discontinuity");
            run.Next = run.Route.Arrive(run.Next, after);
            if (run.Next < run.Route.Waypoints.Length)
            {
                float remaining = after.Distance(run.Route.Waypoints[run.Next]);
                if (remaining < run.BestDistance - .01f || run.Next != previousNext)
                { run.ProgressAt = Time.realtimeSinceStartup; run.BestDistance = remaining; }
                if (Time.realtimeSinceStartup - run.ProgressAt > 3f) throw new InvalidOperationException("traversal-no-progress");
            }
            run.Last = after;
            if (run.Next == run.Route.Waypoints.Length)
            {
                run.ReachedAt = Time.realtimeSinceStartup; run.ReachedFrame = Time.frameCount;
                run.Arrival = Sample(run, "reached", requested, before);
            }
            return run.Next == run.Route.Waypoints.Length ? "traversal-waypoints-reached; finish required" : "traversal-next:" + run.Next;
        });
    }

    internal static string FinishPlayer() => Finish(false);
    internal static string FinishEnemy() => Finish(true);
    private static string Finish(bool enemy)
    {
        var run = Require(enemy);
        return Execute(run, () =>
        {
            ValidateIdentity(run);
            if (run.Arrival == null || run.Next != run.Route.Waypoints.Length)
                throw new InvalidOperationException("traversal-incomplete-ordered-route");
            run.Terminal = "completed"; run.Reason = "observed-ordered-route-only";
            Persist(run); _run = null;
            return "traversal-completed:" + run.File;
        });
    }

    internal static void Cleanup()
    {
        if (_run is not { } run) return;
        if (run.Terminal == null) { run.Terminal = "aborted"; run.Reason = "experiment-ended-before-finish"; }
        Persist(run); _run = null;
    }

    private static Run Require(bool enemy)
    {
        var run = _run ?? throw new InvalidOperationException("traversal-not-active");
        if ((run.Enemy != null) != enemy || run.Terminal != null) throw new InvalidOperationException("traversal-wrong-kind-or-ended");
        return run;
    }

    private static string Execute(Run run, Func<string> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            run.Terminal = "failed"; run.Reason = error.GetType().Name + ":" + error.Message;
            try { Persist(run); }
            catch (Exception writeError) { throw new AggregateException("traversal-failed-and-document-unwritten", error, writeError); }
            throw;
        }
    }

    private static void Gate()
    {
        RecObservation.RequireNativeRead();
        if (!RecSession.Active
            || GameStateManager.CurrentStateName != eGameStateName.InLevel)
            throw new InvalidOperationException("traversal-requires-authoring-level-recorder");
    }

    // After arrival only identity is rechecked. Subsequent attacks or motion cannot erase the captured route.
    private static void ValidateIdentity(Run run)
    {
        Gate();
        var player = ForgeCore.Players.Local;
        if (RecSession.SessionId != run.Session || RecTime.WorldEpoch != run.World || run.Identity.WorldEpoch != run.World
            || player == null || player.WasCollected || player.Pointer != run.Player.Pointer
            || !run.PlayerIdentity.IsCurrent(player) || !player.Alive)
            throw new InvalidOperationException("traversal-session-world-player-life-changed");
        if (run.Enemy is { } enemy && (enemy.WasCollected || enemy.Pointer.ToInt64() != run.Pointer
            || !run.Identity.IsCurrent(enemy) || !enemy.Alive))
            throw new InvalidOperationException("traversal-enemy-life-changed");
    }

    private static void Validate(Run run)
    {
        ValidateIdentity(run);
        var player = run.Player;
        if (!player.Alive || (int)player.DimensionIndex != run.Route.Dimension)
            throw new InvalidOperationException("traversal-player-not-alive-or-dimension-changed");
        if (run.Enemy is { } e)
        {
            if (!ForgeCore.Session.IsHost || !e.Alive || (int)e.DimensionIndex != run.Route.Dimension || e.CourseNode == null
                || e.AI == null || e.Locomotion == null || e.Locomotion.CurrentStateEnum != ES_StateEnum.PathMove
                || e.AI.Target?.m_agent == null || e.AI.Target.m_agent.Pointer != player.Pointer)
                throw new InvalidOperationException("traversal-enemy-requires-live-original-target-local-player-and-native-PathMove");
            if (Point(player.Position).Distance(run.Route.Waypoints[^1]) > run.Route.Tolerance)
                throw new InvalidOperationException("traversal-enemy-bait-must-remain-at-route-end");
            if (!Point(player.Position).Finite) throw new InvalidOperationException("traversal-enemy-bait-position-nonfinite");
        }
        else
        {
            var c = player.PlayerCharacterController;
            if (c == null || c.WasCollected || !c.enabled || !c.gameObject.activeInHierarchy
                || run.ControllerPointer != 0 && c.Pointer.ToInt64() != run.ControllerPointer
                || c.m_characterController == null || !c.m_characterController.enabled
                || run.ColliderPointer != 0 && c.m_characterController.Pointer.ToInt64() != run.ColliderPointer
                || !c.m_characterController.gameObject.activeInHierarchy || player.CourseNode == null
                || player.Locomotion == null || player.Locomotion.m_currentStateEnum is not
                    (PlayerLocomotion.PLOC_State.Stand or PlayerLocomotion.PLOC_State.Crouch))
                throw new InvalidOperationException("traversal-requires-enabled-collision-controller-and-native-stand-or-crouch");
        }
    }

    private static object Sample(Run run, string phase, TraversalPoint? requested, TraversalPoint? before)
    {
        var p = run.Player; var e = run.Enemy;
        var c = e == null ? p.PlayerCharacterController : null;
        var collider = c?.m_characterController;
        var sample = new
        {
            phase,
            frame = Time.frameCount,
            deltaTime = Time.deltaTime,
            time = Time.realtimeSinceStartup,
            nextWaypoint = run.Next,
            position = Position(run).Array,
            before = before?.Array,
            requestedMotion = requested?.Array,
            dimension = (int)(e == null ? p.DimensionIndex : e.DimensionIndex),
            node = e == null ? p.CourseNode?.NodeID : e.CourseNode?.NodeID,
            nodePointer = e == null ? p.CourseNode?.Pointer.ToInt64() : e.CourseNode?.Pointer.ToInt64(),
            nativeState = e == null ? p.Locomotion.m_currentStateEnum.ToString() : e.Locomotion.CurrentStateEnum.ToString(),
            enemyDataId = e?.EnemyDataID,
            targetPointer = e?.AI.Target?.m_agent?.Pointer.ToInt64(),
            entityPointer = run.Pointer,
            globalId = e == null ? p.GlobalID : e.GlobalID,
            baitPosition = e == null ? null : Point(p.Position).Array,
            controllerPosition = c == null ? null : Point(c.Position).Array,
            lastNativeMove = c == null ? null : Point(c.LastMove).Array,
            collisionFlags = collider == null ? null : collider.collisionFlags.ToString(),
            controllerPointer = c?.Pointer.ToInt64(),
            colliderPointer = collider?.Pointer.ToInt64(),
            colliderEnabled = collider?.enabled,
            colliderHeight = collider?.height,
            colliderRadius = collider?.radius,
            colliderCenter = collider == null ? null : Point(collider.center).Array,
            grounded = collider?.isGrounded
        };
        run.Samples.Add(sample);
        return sample;
    }

    private static void Persist(Run run)
    {
        if (run.Written) return;
        if (RecSession.SessionId != run.Session) throw new InvalidOperationException("traversal-document-original-session-unavailable");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = ForgeCore.FormatVersion.Current,
            kind = run.Enemy == null ? "player-collision-route" : "enemy-native-chase-observation",
            session = run.Session,
            worldEpoch = run.World,
            entity = run.Identity,
            player = run.PlayerIdentity,
            pointer = run.Pointer,
            route = new { dimension = run.Route.Dimension, waypoints = run.Route.Waypoints.Select(p => p.Array), speed = run.Route.Speed, tolerance = run.Route.Tolerance },
            stimulus = run.Enemy == null ? "PlayerCharacterController.ManualMoveToWithCollision(motion)" : "EnemyAI.SetTarget(already-current-local-player)",
            speedScope = run.Enemy == null ? "requested displacement per native deltaTime; other native motion is observed" : "route speed does not control enemy locomotion",
            status = run.Terminal,
            reason = run.Reason,
            nextWaypoint = run.Next,
            reachedAt = run.ReachedAt,
            reachedFrame = run.ReachedFrame,
            arrival = run.Arrival,
            evidenceScope = "Actual sampled movement only; no navigation, door clearance, AI correctness or reverse-direction approval.",
            samples = run.Samples
        });
        if (!RecSession.WriteDocument(run.File, bytes)) throw new InvalidOperationException("traversal-document-not-queued:" + run.File);
        run.Written = true;
    }

    private static TraversalPoint Position(Run run) => Point(run.Enemy == null ? run.Player.Position : run.Enemy.Position);
    private static TraversalPoint Point(Vector3 value) => new(value.x, value.y, value.z);
    private static Vector3 Vector(TraversalPoint p) => new(p.X, p.Y, p.Z);
}
