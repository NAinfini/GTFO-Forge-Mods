using System;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One placement-time body snapshot. A bound interaction is parented to the exact model root and its
/// collider is expressed in that root's unscaled local frame, so later model transforms apply exactly once.</summary>
internal readonly record struct InteractionPointBody(InteractionPoint Point, Transform? Parent,
    Vector3 Center, Vector3 Size)
{
    private const float Margin = 0.04f;

    internal static InteractionPointBody Read(InteractionPoint point, long world)
    {
        if (point.Appearance is not { } appearance)
            return new(point, null, Vector3.zero, new Vector3(.5f, .5f, .5f));
        var models = PlacedModelSession.Current;
        var reason = "model-session-unavailable";
        if (models == null || !models.TryReadBody(appearance.PlacedModelId, point.Level, world,
                out var body, out reason))
            throw new InvalidOperationException("appearance=" + appearance.PlacedModelId + " reason=" + reason);

        var scale = body.Scale;
        var absoluteScale = new Vector3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
        if (absoluteScale.x == 0 || absoluteScale.y == 0 || absoluteScale.z == 0)
            throw new InvalidOperationException("appearance=" + appearance.PlacedModelId + " reason=model-rendered-bounds-invalid");
        var inverse = Quaternion.Inverse(body.Rotation);
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        var bounds = body.WorldBounds;
        var extent = bounds.extents;
        for (var corner = 0; corner < 8; corner++)
        {
            var position = bounds.center + new Vector3((corner & 1) == 0 ? -extent.x : extent.x,
                (corner & 2) == 0 ? -extent.y : extent.y, (corner & 4) == 0 ? -extent.z : extent.z);
            var unscaled = inverse * (position - body.Position);
            var local = new Vector3(unscaled.x / scale.x, unscaled.y / scale.y, unscaled.z / scale.z);
            min = new Vector3(MathF.Min(min.x, local.x), MathF.Min(min.y, local.y), MathF.Min(min.z, local.z));
            max = new Vector3(MathF.Max(max.x, local.x), MathF.Max(max.y, local.y), MathF.Max(max.z, local.z));
        }
        var size = max - min + new Vector3(2 * Margin / absoluteScale.x,
            2 * Margin / absoluteScale.y, 2 * Margin / absoluteScale.z);
        var center = (min + max) * .5f;
        var worldCenter = new Vector3(center.x * scale.x, center.y * scale.y, center.z * scale.z);
        var worldSize = new Vector3(size.x * absoluteScale.x, size.y * absoluteScale.y, size.z * absoluteScale.z);
        var radius = worldCenter.magnitude + worldSize.magnitude * .5f;
        if (!float.IsFinite(radius) || !float.IsFinite(size.x) || !float.IsFinite(size.y) || !float.IsFinite(size.z))
            throw new InvalidOperationException("appearance=" + appearance.PlacedModelId + " reason=model-rendered-bounds-invalid");
        var placed = new InteractionPoint(point.Id, point.Level, body.Instance.Model.Room,
            new double[] { body.Position.x, body.Position.y, body.Position.z },
            new double[] { body.Rotation.x, body.Rotation.y, body.Rotation.z, body.Rotation.w },
            point.Duration, point.Prompt, true, point.Appearance,
            Math.Max(0, radius - InteractionPoint.DefaultColliderRadius), point.Enabled, point.Group);
        return new(placed, body.RootTransform, center, size);
    }
}
