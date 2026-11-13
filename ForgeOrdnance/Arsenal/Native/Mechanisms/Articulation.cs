using System.Collections.Generic;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>Moves named parts of a spawned model by pose, not by animation clip: a part turns about one of its own axes,
/// always measured from the pose it had when first touched, so a caller sets "open 40 degrees" every frame and never accumulates drift.
/// Hinges and turntables are all <see cref="Turn"/>. Find parts once (<see cref="Named"/>) and keep
/// the transforms; nothing here looks a name up per frame.</summary>
internal sealed class Articulation
{
    private readonly Transform _root;
    private readonly Dictionary<Transform, (Vector3 Position, Quaternion Rotation)> _rest = new();

    public Articulation(GameObject model)
    {
        if (model == null) throw new System.ArgumentNullException(nameof(model));
        _root = model.transform;
    }

    /// <summary>The model's root.</summary>
    public Transform Root => _root;

    /// <summary>Every descendant whose name starts with <paramref name="prefix"/>, in depth-first order (<c>plate</c> finds <c>plate1</c>,
    /// <c>plate2</c> ...; a part found under another found part is listed too). Empty when the model has none.</summary>
    public List<Transform> Named(string prefix)
    {
        var found = new List<Transform>();
        Collect(_root, prefix, found);
        return found;
    }

    private static void Collect(Transform node, string prefix, List<Transform> into)
    {
        for (var i = 0; i < node.childCount; i++)
        {
            var child = node.GetChild(i);
            if (child.name.StartsWith(prefix, System.StringComparison.Ordinal)) into.Add(child);
            Collect(child, prefix, into);
        }
    }

    /// <summary>The part turned <paramref name="degrees"/> about <paramref name="axis"/> (its own local axis) from its rest pose.</summary>
    public void Turn(Transform part, Vector3 axis, float degrees)
    {
        var rest = Rest(part);
        part.localRotation = rest.Rotation * Quaternion.AngleAxis(degrees, axis);
    }

    private (Vector3 Position, Quaternion Rotation) Rest(Transform part)
    {
        if (!_rest.TryGetValue(part, out var rest))
        {
            rest = (part.localPosition, part.localRotation);
            _rest[part] = rest;
        }
        return rest;
    }
}
