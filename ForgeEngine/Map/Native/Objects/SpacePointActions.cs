using System;
using AIGraph;
using Agents;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

internal sealed class SpacePointActions
{
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private static readonly NavigationPointSampler Sampler = Sample;
    internal SpacePointActions(IForgeEngine kernel, Func<bool> ready) { _kernel = kernel; _ready = ready; }

    private static bool UnityPoint((double X, double Y, double Z)? value, out Vector3 point)
    {
        point = default;
        if (value is not { } p || !float.IsFinite((float)p.X) || !float.IsFinite((float)p.Y) || !float.IsFinite((float)p.Z)) return false;
        point = new((float)p.X, (float)p.Y, (float)p.Z); return true;
    }

    // Forward candidates may project from an aim-height origin to the nearby walkable surface. Return the
    // surface point, not a player-warp voxel centre, so the exact spawn gate can consume this query's answer.
    private static bool Landing(int dimension, Vector3 requested, out Vector3 landing, out AIG_CourseNode? node)
    {
        node = null; landing = default;
        if (!EnvironmentZone.DimensionInRange(dimension)
            || !ForgeCore.Level.TrySnap(requested, 2f, out landing)) return false;
        if (!float.IsFinite(landing.x) || !float.IsFinite(landing.y) || !float.IsFinite(landing.z)) return false;
        if (!ForgeCore.Level.CourseNodeIn(dimension, landing, .05f, out var found)
            || found == null || found.m_dimension == null
            || (int)found.m_dimension.DimensionIndex != dimension) return false;
        node = found; return true;
    }
    private static SpacePoint? Sample(int dimension, SpacePoint requested)
    {
        if (!UnityPoint((requested.X, requested.Y, requested.Z), out var input)
            || !Landing(dimension, input, out var point, out _)) return null;
        return new(point.x, point.y, point.z);
    }

    internal static string PointText(Vector3 point)
        => FormattableString.Invariant($"({point.x:R},{point.y:R},{point.z:R})");
    private static string SpawnPointText(EnvironmentZone address, Vector3 position)
        => FormattableString.Invariant($"zone=gtfo.zone:{address.Dimension}:{address.Layer}:{address.Zone};requested={PointText(position)}");

    // SampleWarpPosition returns a voxel node's centre, not a surface point. Comparing that centre with an
    // authored foot position rejects valid floors. Check the surface and course ownership independently;
    // neither check may replace the position passed to the native allocator. The navigation mesh floats above the
    // floor it was baked from, so the gap is judged against the reach the level layer uses for "ground
    // under this place" (ForgeCore.Level.SampleReach). The enemy stands on the sampled navigation point, as the game's own
    // placements do; `stand` is that point.
    internal static bool SpawnNode(EnvironmentZone address, LG_Zone zone, Vector3 position,
        out AIG_CourseNode? node, out Vector3 stand, out string detail)
    {
        node = null; stand = position;
        var request = SpawnPointText(address, position);
        if (!ForgeCore.Level.TrySnap(position, ForgeCore.Level.SampleReach, out var point))
        {
            // The refusal stays exact; the wider probe only names the nearest walkable point so a height offset can be told from a hole.
            var nearest = ForgeCore.Level.TrySnap(position, 2f, out var near)
                ? FormattableString.Invariant($";nearest={PointText(near)};dy={near.y - position.y:R};dxz={Math.Sqrt((near.x - position.x) * (near.x - position.x) + (near.z - position.z) * (near.z - position.z)):R}")
                : ";nearest=none-within-2m";
            detail = request + FormattableString.Invariant($";rule=walkable-surface-unavailable;maxReach={ForgeCore.Level.SampleReach:R}") + nearest; return false;
        }
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
        { detail = request + ";rule=walkable-surface-nonfinite;sampled=" + PointText(point); return false; }
        stand = point;
        if (!AIG_CourseNode.TryGetCourseNode(address.DimensionIndex, point, .05f, out var found) || found == null)
        { detail = request + ";rule=course-node-unavailable;cause=the game cannot assign a room node or attach the enemy AI at this point"; return false; }
        if (found.WasCollected)
        { detail = request + ";rule=course-node-collected"; return false; }
        if (found.m_dimension == null || found.m_dimension.DimensionIndex != address.DimensionIndex)
        { detail = request + ";rule=course-node-dimension-mismatch"; return false; }
        if (found.m_zone == null || found.m_zone.Pointer != zone.Pointer)
        {
            // Name the zone and area the point really belongs to: an author box that reaches into the next zone and a course lookup
            // that answers with another room's node at a point inside the requested zone are different faults.
            detail = request + ";rule=course-node-zone-mismatch;foundZone=" + (found.m_zone == null ? "none"
                : ((int)found.m_zone.LocalIndex).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ";foundArea=" + (found.m_area?.name ?? "none");
            return false;
        }
        node = found; detail = ""; return true;
    }

    internal CommandResult Spawn(CommandContext context)
    {
        if (!_ready() || !Session.IsHost || !context.IsHost || _kernel.WorldEpoch != context.WorldEpoch)
            return RefuseSpawn(context, ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(EnemySpawnContract.Zone, out var zone) || zone.WorldEpoch != context.WorldEpoch
            || EnvironmentZone.Parse(zone) is not { } address
            || ZoneIndex.Current(context.WorldEpoch).ZoneAt(new(address.Dimension, address.Layer, address.Zone)) is not { } targetZone)
            return RefuseSpawn(context, ResultCode.EnemySpawnZoneUnavailable);
        if (!context.TryResource(EnemySpawnContract.EnemyType, out var kind) || kind.ResourceKind != "enemy"
            || !uint.TryParse(kind.ResourceId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var typeId)
            || typeId == 0)
            return RefuseSpawn(context, ResultCode.EnemySpawnTypeInvalid);
        // An unwired position or direction is the author's "anywhere the game would put it"; a wired one that carries no valid vector is
        // refused, never replaced by a random pick.
        var positionGiven = context.Kind(EnemySpawnContract.Position) != ValueKind.Missing;
        var directionGiven = context.Kind(EnemySpawnContract.Direction) != ValueKind.Missing;
        Vector3 position = default, direction = default;
        if (positionGiven && !UnityPoint(context.Vector(EnemySpawnContract.Position), out position)
            || directionGiven && (!UnityPoint(context.Vector(EnemySpawnContract.Direction), out direction) || direction.x * direction.x + direction.z * direction.z < 1e-12f))
            return RefuseSpawn(context, ResultCode.EnemySpawnPoseInvalid);
        AgentMode? mode = context.Text(EnemySpawnContract.Mode) switch
        {
            "off" => AgentMode.Off,
            "aggressive" => AgentMode.Agressive,
            "patrolling" => AgentMode.Patrolling,
            "scout" => AgentMode.Scout,
            "hibernate" => AgentMode.Hibernate,
            _ => null
        };
        if (mode == null) return RefuseSpawn(context, ResultCode.EnemySpawnModeInvalid);
        AIG_CourseNode? node;
        var rotation = directionGiven ? Quaternion.Euler(0, (float)(Math.Atan2(direction.x, direction.z) * 180 / Math.PI), 0) : Quaternion.identity;
        var placed = "";
        try
        {
            if (positionGiven)
            {
                if (!SpawnNode(address, targetZone, position, out node, out var stand, out var detail))
                    return RefuseSpawn(context, ResultCode.EnemySpawnPositionIllegal, detail);
                MapLog.Record(RuntimeLogLevel.Info, "map.enemy-spawn-snapped", "Enemy spawns on the sampled navigation point.",
                    RuntimeLogValue.Of("requested", PointText(position)), RuntimeLogValue.Of("sampled", PointText(stand)),
                    RuntimeLogValue.Of("event", context.EventId), RuntimeLogValue.Of("node", context.NodeId));
                position = stand;
                // The game's own random facing, the one it gives a group member without an authored pose.
                if (!directionGiven) rotation = Enemies.EnemyGroup.GetRandomRotation();
            }
            else
            {
                if (!ZoneSpawnPlacement.TryPick(address, targetZone, (uint)typeId, context.WorldEpoch, out position, out var picked, out node, out placed))
                    return RefuseSpawn(context, ResultCode.EnemySpawnPositionIllegal, placed);
                if (!directionGiven) rotation = picked;
            }
        }
        catch (Exception error)
        {
            return RefuseSpawn(context, ResultCode.EnemySpawnNavigationUnavailable, SpawnPointText(address, position)
                + ";rule=navigation-read-failed;cause=" + error.GetType().Name + ":" + error.Message);
        }
        // The fixed placement's one vanilla call also owns arena preflight. No debug group, substitute or second allocator.
        if (!FixedEnemyPlacement.TrySpawn(position, rotation, node!, (uint)typeId, mode.Value,
                out var enemy, out var reason, out var attempted, out var preflight))
            return SpawnResult(context, attempted ? Status.Failed : Status.Rejected, attempted ? Commit.Unknown : Commit.None,
                preflight == ForgeCore.SpawnRefusal.ArenaMissing
                    ? ResultCode.EnemySpawnArenaMissing : attempted ? ResultCode.EnemySpawnNativeUnknown : ResultCode.EnemySpawnPreflightRefused, detail: reason ?? "");
        var entity = enemy != null && _kernel.WorldEpoch == context.WorldEpoch
            ? _kernel.ResolveEntityInstance(EntityKinds.Enemy, enemy) : null;
        return entity == null ? SpawnResult(context, Status.Failed, Commit.Unknown, ResultCode.EnemySpawnIdentityUnavailable)
            : SpawnResult(context, Status.Succeeded, Commit.Confirmed, ResultCode.EnemySpawned, entity,
                detail: placed, mode: context.Text(EnemySpawnContract.Mode) ?? "", state: enemy!.AI?.m_behaviour?.m_currentStateName.ToString() ?? "");
    }
    private static Outcome RefuseSpawn(CommandContext context, string code, string detail = "")
        => SpawnResult(context, Status.Rejected, Commit.None, code, detail: detail);

    /// <summary>The one row, whose target is the enemy type the request named, and the command's own conclusion with
    /// the same code; a request that named no enemy type has no recipient and so no row. The spawned column and the
    /// named output carry the same observed enemy. A refusal's detail stays on the command result.</summary>
    private static Outcome SpawnResult(CommandContext context, Status status, Commit commit, string code, EntityReference? spawned = null, string detail = "", string mode = "", string state = "")
    {
        MapLog.Record(status == Status.Succeeded ? RuntimeLogLevel.Info : RuntimeLogLevel.Error,
            status == Status.Succeeded ? "map.enemy-spawned" : "map.enemy-spawn-refused", "Enemy spawn result.",
            RuntimeLogValue.Of("reason", code), RuntimeLogValue.Of("detail", detail), RuntimeLogValue.Of("mode", mode), RuntimeLogValue.Of("state", state),
            RuntimeLogValue.Of("event", context.EventId), RuntimeLogValue.Of("command", context.CommandId),
            RuntimeLogValue.Of("plan", context.PlanId), RuntimeLogValue.Of("resource", context.ResourceId),
            RuntimeLogValue.Of("node", context.NodeId), RuntimeLogValue.Of("world", context.WorldEpoch));
        if (context.TryResource(EnemySpawnContract.EnemyType, out _))
        {
            context.Rows.Begin(0).Entity(Blocks.ActionEnemySpawn.RowSpawned, spawned).End(status, commit, code);
            context.Outputs.Entity(Blocks.ActionEnemySpawn.OutSpawned, spawned);
        }
        var result = new Outcome(status, commit, code);
        // On success the detail is only the log's (where the game's placement put the enemy); the command keeps a refusal's reason alone.
        return detail.Length == 0 || status == Status.Succeeded ? result : result.WithDetail(detail);
    }

    internal void Contains(EvaluationContext context, EvaluationOutputs outputs)
    {
        try
        {
            if (!_ready()) throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The map provider is unavailable.");
            if (!context.Query.Available) throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The world query is unavailable.");
            if (!context.TryEntity(SpacePointContract.Field, out var field))
                throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The named range input is unavailable.");
            if (field.WorldEpoch != context.Query.WorldEpoch)
                throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The named range belongs to another world.");
            if (context.Vector(SpacePointContract.Position) is not { } point)
                throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The position input is unavailable.");
            var range = context.EntityInstance(field) as TriggerZone ?? MapHost.Session?.TriggerZones?.LabelReceiver(field);
            if (range == null || !range.Placed) throw new RuntimeContractException(SpacePointContract.ContainsUnresolvedCode, "The named range has no current room pose.");
            bool inside = range.ContainsPoint(point.X, point.Y, point.Z);
            if (MapLog.Logs(RuntimeLogLevel.Info))
                MapLog.Record(RuntimeLogLevel.Info, SpacePointContract.ContainsReadCode, "Named range measured.", RuntimeLogValue.Of("inside", inside));
            outputs.Boolean(0, inside);
        }
        catch (RuntimeContractException error)
        {
            if (MapLog.Logs(RuntimeLogLevel.Error))
                MapLog.Write(RuntimeLogLevel.Error, SpacePointContract.ContainsRefusedCode, new RuntimeLogFacts
                {
                    Binding = SpacePointContract.ContainsBinding,
                    Detail = "Named range unavailable.",
                    Values = new[] { RuntimeLogValue.Of("status", CommandStatuses.Rejected), RuntimeLogValue.Of("reason", error.Code),
                        RuntimeLogValue.Quote("cause", error.Message), RuntimeLogValue.Quote("node", context.NodeId) }
                });
            throw;
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, SpacePointContract.ContainsRefusedCode, "Named range lookup failed.",
                RuntimeLogValue.Quote("cause", error.GetType().Name + ": " + error.Message));
            throw;
        }
    }

    internal void Forward(EvaluationContext context, EvaluationOutputs outputs)
    {
        try
        {
            if (!_ready() || !context.Query.Available || context.Integer(SpacePointContract.Dimension) is not { } dimension
                || dimension < 0 || dimension >= (int)eDimensionIndex.MAX_COUNT
                || context.Vector(SpacePointContract.Origin) is not { } origin || context.Vector(SpacePointContract.Direction) is not { } direction
                || context.Number(SpacePointContract.Near) is not { } near || context.Number(SpacePointContract.Far) is not { } far
                || context.Number(SpacePointContract.Count) is not { } count
                || !double.IsFinite(count) || Math.Truncate(count) != count || count < 0 || count > ForwardPositions.MaximumCount || context.Number(SpacePointContract.Attempts) is not { } attempts
                || !double.IsFinite(attempts) || Math.Truncate(attempts) != attempts || attempts < 0 || attempts > ForwardPositions.MaximumAttempts || context.Number(SpacePointContract.Seed) is not { } seed
                || !double.IsFinite(seed) || Math.Truncate(seed) != seed || Math.Abs(seed) > 9007199254740991d)
                throw new RuntimeContractException("forward-position-request-invalid", "The forward position request is invalid.");
            bool sector = context.Text(SpacePointContract.Shape) switch
            { "box" => false, "sector" => true, _ => throw new RuntimeContractException("forward-position-shape-invalid", "Unknown forward shape.") };
            // A box spans a width in metres, a sector an opening angle in degrees; each shape reads only its own port.
            if (context.Number(sector ? SpacePointContract.Angle : SpacePointContract.Width) is not { } width)
                throw new RuntimeContractException("forward-position-request-invalid", sector ? "A sector needs its angle." : "A box needs its width.");
            ForwardPositionResult answer;
            try
            {
                answer = ForwardPositions.Find((int)dimension, new(origin.X, origin.Y, origin.Z), new(direction.X, direction.Y, direction.Z),
                near, far, width, (int)count, (int)attempts, (long)seed, sector, Sampler);
            }
            catch (ArgumentException error) { throw new RuntimeContractException(error.Message, "Invalid forward geometry or budget."); }
            if (MapLog.Logs(RuntimeLogLevel.Info))
                MapLog.Record(RuntimeLogLevel.Info, "map.forward-positions", "Navigation positions measured.",
                    RuntimeLogValue.Of("requested", count), RuntimeLogValue.Of("actual", (long)answer.ActualCount), RuntimeLogValue.Of("exhausted", answer.Exhausted));
            outputs.Json(0, RuntimeJson.From(answer.Positions));
            outputs.Integer(1, answer.ActualCount);
            outputs.Boolean(2, answer.Exhausted);
        }
        catch (Exception)
        { MapLog.Record(RuntimeLogLevel.Error, "map.forward-positions-refused", "Forward navigation query refused."); throw; }
    }
}
