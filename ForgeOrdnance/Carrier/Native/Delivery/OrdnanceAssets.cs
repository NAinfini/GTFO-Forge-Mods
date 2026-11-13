using System;
using System.IO;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance;
/// <summary>Carrier/forge-ordnance.bundle next to the plugin, registered with ForgeVisual as the pack <c>Ordnance</c>: the summoning ball, drop pod, turret shell and the effect prefabs. Registered when a level has been built and kept for the session; a level whose load failed tries again at the next level. When the file is missing or broken
/// the ball keeps the glow stick look, deliveries fall back to the game's items on the ground and sentries keep the game's
/// look, and the log says why.</summary>
internal static class OrdnanceAssets
{
    internal const string BundleFile = "Carrier/forge-ordnance.bundle";
    /// <summary>The pack name of the bundle in ForgeVisual: its models are <c>Ordnance:drop-pod</c>, <c>Ordnance:turret-shell</c> and <c>Ordnance:beacon</c>.</summary>
    internal const string Pack = "Ordnance";
    internal const string PodPrefab = "drop-pod";
    internal const string ShellPrefab = "turret-shell";
    internal const string BeaconPrefab = "beacon";

    internal static BundleHandle? Bundle { get; private set; }
    internal static ModelHandle? Pod { get; private set; }
    /// <summary>The one drop pod's art description: four hinged doors, two weapon bays and four pickup bays.
    /// Every call-in without a pod appearance of its own uses it.</summary>
    internal static OrdnancePodAppearance? PodAppearance { get; private set; }

    /// <summary>Usable room of the pod's bays, in metres: x across, y height, z depth from the middle divider to the closed
    /// doors. Measured on the pod: a weapon bay stands a 1.13 m long gun upright; each pickup
    /// bay is one tier of a bay, the upper tier standing on the shelf (<see cref="ShelfPrefabs"/>).</summary>
    internal static readonly Vector3 WeaponBayRoom = new(0.78f, 1.23f, 0.32f);
    internal static readonly Vector3 FloorTierRoom = new(0.78f, 0.59f, 0.32f);
    internal static readonly Vector3 ShelfTierRoom = new(0.78f, 0.61f, 0.32f);
    /// <summary>Height of the bay floor above the pod's ground line, the same for every anchor tier base.</summary>
    internal const float BayFloorHeight = 0.783f;
    /// <summary>How far the hatches swing out, in degrees (further they would strike the corner posts).</summary>
    internal const float DoorOpenDegrees = 85f;
    /// <summary>The shelves that make the upper pickup tier; the pod shows them only for pickups (a weapon stands the whole bay high).</summary>
    internal static readonly string[] ShelfPrefabs = { "shelf_1", "shelf_2" };

    /// <summary>Bay <paramref name="index"/> (0-based) of a pod with the given world pose: a pickup bay, or a weapon bay when
    /// <paramref name="weapon"/>. Pickup bays 0 and 1 are the floor tier of the +X and -X bay, 2 and 3 the shelf tier.</summary>
    internal static bool TryBay(Vector3 podPosition, Quaternion podRotation, int index, bool weapon, out OrdnancePodBay bay)
    {
        bay = default;
        if (PodAppearance is not { } appearance || Pod is not { } pod) return false;
        var sockets = weapon ? appearance.WeaponBays : appearance.PickupBays;
        if (index < 0 || index >= sockets.Length) return false;
        var socket = sockets[index];
        var local = pod.Prefab.transform.Find(socket.Path)!.localPosition;
        bay = new OrdnancePodBay(podPosition + podRotation * local, podRotation * socket.Rotation, socket.Size);
        return true;
    }
    internal static ModelHandle? Shell { get; private set; }
    internal static ModelHandle? Beacon { get; private set; }

    /// <summary>When a level has been built, until one load succeeds.</summary>
    internal static void Load()
    {
        if (Bundle != null) return;
        try { LoadChecked(); }
        catch (Exception error)
        {
            Bundle = null;
            Pod = Shell = Beacon = null;
            PodAppearance = null;
            Plugin.Logger.ErrorOnce("Forge Ordnance art could not be loaded; the ball keeps the glow stick look, supplies and turrets arrive without the drop pod and shell: " + error);
        }
    }

    private static void LoadChecked()
    {
        // A level whose load failed after the pack was registered finds it registered: the pack is taken, not registered twice.
        var bundle = (Bundles.TryGet(Pack, out var registered) ? registered : Bundles.Register(Plugin.Id, Pack, BundleFile))
            ?? throw new InvalidOperationException("ForgeVisual did not register " + BundleFile + " (its error is in the log)");
        var pod = Model(bundle, PodPrefab);
        var shell = Model(bundle, ShellPrefab);
        Bundle = bundle;
        Pod = pod;
        Shell = shell;
        Beacon = bundle.TryModel(BeaconPrefab, out var beacon) ? beacon : null;
        PodAppearance = BuildPodAppearance(pod);
        try { PodImpactEffects.Load(bundle); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Pod impact particles could not be prepared: " + error); }
    }

    private static ModelHandle Model(BundleHandle bundle, string prefab)
        => bundle.TryModel(prefab, out var model) ? model : throw new InvalidDataException("The bundle has no prefab '" + prefab + "'.");

    private static OrdnancePodAppearance BuildPodAppearance(ModelHandle pod)
    {
        // Each leaf hinges on the outer edge away from its bay's corner post and swings outward; the signs are in the exported
        // Unity frame (door_1 and door_3 turn clockwise seen from above, door_2 and door_4 counter-clockwise).
        var angles = new[] { DoorOpenDegrees, -DoorOpenDegrees, DoorOpenDegrees, -DoorOpenDegrees };
        var doors = new OrdnancePodDoor[angles.Length];
        for (var i = 0; i < doors.Length; i++) doors[i] = new OrdnancePodDoor("door_" + (i + 1), Vec.Up, angles[i]);
        var plus = Quaternion.Euler(0f, 90f, 0f);
        var minus = Quaternion.Euler(0f, -90f, 0f);
        var weapon = new[] { new OrdnancePodSocket("weapon_anchor_1", WeaponBayRoom, plus), new OrdnancePodSocket("weapon_anchor_2", WeaponBayRoom, minus) };
        var pickups = new[] {
            new OrdnancePodSocket("pickup_anchor_1", FloorTierRoom, plus), new OrdnancePodSocket("pickup_anchor_2", FloorTierRoom, minus),
            new OrdnancePodSocket("pickup_anchor_3", ShelfTierRoom, plus), new OrdnancePodSocket("pickup_anchor_4", ShelfTierRoom, minus) };
        foreach (var shelf in ShelfPrefabs)
            if (pod.Prefab.transform.Find(shelf) == null) throw new InvalidDataException("The drop pod lacks " + shelf + "; rebuild forge-ordnance.bundle.");
        // The room constants are measured on the pod: refuse a pod whose floor is not where they were taken.
        if (Math.Abs(pod.Prefab.transform.Find("weapon_anchor_1")!.localPosition.y - BayFloorHeight) > 0.01f)
            throw new InvalidDataException("The drop pod's bays are not where this build expects them; rebuild forge-ordnance.bundle.");
        return new OrdnancePodAppearance(pod, doors, weapon, pickups);
    }
}
