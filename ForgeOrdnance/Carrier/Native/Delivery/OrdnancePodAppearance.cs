using System;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>A hinged transform path and its signed opening rotation, in the transform's closed local frame.</summary>
public readonly struct OrdnancePodDoor
{
    public OrdnancePodDoor(string path, Vector3 axis, float degrees)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A door path is required.", nameof(path));
        if (!Vec.Finite(axis) || Vec.SqrLength(axis) < 1e-6f || !float.IsFinite(degrees))
            throw new ArgumentException("Door motion must have a finite, nonzero axis and finite angle.");
        Path = path; Axis = axis.normalized; Degrees = degrees;
    }
    public string Path { get; }
    public Vector3 Axis { get; }
    public float Degrees { get; }
}

/// <summary>Floor anchor path, usable metres (width, height, depth), and bay orientation relative to the anchor.
/// Up is upright; forward points through the doors. The anchor's own rotation is preserved.</summary>
public readonly struct OrdnancePodSocket
{
    public OrdnancePodSocket(string path, Vector3 size, Quaternion rotation)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A bay anchor path is required.", nameof(path));
        if (!Vec.Finite(size) || size.x <= 0 || size.y <= 0 || size.z <= 0)
            throw new ArgumentException("Bay size must be finite and positive.", nameof(size));
        var norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        if (!float.IsFinite(norm) || Math.Abs(norm - 1f) > .001f) throw new ArgumentException("Bay rotation must be a unit quaternion.", nameof(rotation));
        Path = path; Size = size; Rotation = rotation;
    }
    public string Path { get; }
    public Vector3 Size { get; }
    public Quaternion Rotation { get; }
}

/// <summary>Immutable custom pod art. Construct once after loading your bundle, return it from
/// IOrdnancePayload.GetPodAppearance on every peer. Forge owns spawn, door progress, replay and destruction.
/// Models use metres, unit scale, ground-level origin and the default pod's local X door-facing convention.</summary>
public sealed class OrdnancePodAppearance
{
    public OrdnancePodAppearance(ForgeVisual.ModelHandle model, OrdnancePodDoor[] doors, OrdnancePodSocket[] weaponBays,
        OrdnancePodSocket[]? pickupBays = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        if (doors == null || doors.Length == 0) throw new ArgumentException("At least one door is required.", nameof(doors));
        if (weaponBays == null || weaponBays.Length == 0) throw new ArgumentException("At least one weapon bay is required.", nameof(weaponBays));
        if (pickupBays != null && pickupBays.Length > ItemList.MaxPickups) throw new ArgumentException("At most four pickup bays are supported.", nameof(pickupBays));
        Doors = (OrdnancePodDoor[])doors.Clone();
        WeaponBays = (OrdnancePodSocket[])weaponBays.Clone();
        PickupBays = pickupBays == null ? Array.Empty<OrdnancePodSocket>() : (OrdnancePodSocket[])pickupBays.Clone();
        foreach (var door in Doors)
        {
            Require(door.Path);
            if (!Vec.Finite(door.Axis) || Vec.SqrLength(door.Axis) < 1e-6f || !float.IsFinite(door.Degrees)) throw new ArgumentException("Invalid door motion.", nameof(doors));
        }
        foreach (var socket in WeaponBays) Validate(socket);
        foreach (var socket in PickupBays) Validate(socket);
    }

    public ForgeVisual.ModelHandle Model { get; }
    public int WeaponBayCount => WeaponBays.Length;
    public int PickupBayCount => PickupBays.Length;
    internal OrdnancePodDoor[] Doors { get; }
    internal OrdnancePodSocket[] WeaponBays { get; }
    internal OrdnancePodSocket[] PickupBays { get; }
    private void Require(string path)
    {
        if (string.IsNullOrEmpty(path) || Model.Prefab.transform.Find(path) == null) throw new ArgumentException("The pod model lacks transform '" + path + "'.");
    }
    private void Validate(OrdnancePodSocket socket)
    {
        Require(socket.Path);
        if (!Vec.Finite(socket.Size) || socket.Size.x <= 0 || socket.Size.y <= 0 || socket.Size.z <= 0) throw new ArgumentException("Invalid bay size.");
    }
}
