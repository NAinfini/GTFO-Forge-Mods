using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// Turns a target selector into live objects. Every failure says what was missing, because "the enemy was
/// not under the crosshair" and "the type is not loaded" are different problems for the person running the
/// experiment.
/// </summary>
internal static partial class ExperimentTargeting
{
    internal const float DefaultAimDistance = 40f;

    internal static ExperimentTargetResult Resolve(ExperimentTargetSpec spec)
    {
        switch (spec.Kind)
        {
            case ExperimentTargetKind.LocalPlayer:
                {
                    var player = ForgeCore.Players.Local;
                    return player == null ? ExperimentTargetResult.Failed("no local player agent; the run has not spawned one") : ExperimentTargetResult.One(Describe(player), player);
                }
            case ExperimentTargetKind.LocalPlayerStatus:
                {
                    var layer = GuiManager.PlayerLayer;
                    var status = layer == null ? null : layer.m_playerStatus;
                    return status == null ? ExperimentTargetResult.Failed("GuiManager.PlayerLayer.m_playerStatus is unavailable") : ExperimentTargetResult.One(Describe(status), status);
                }
            case ExperimentTargetKind.GuiManager:
                {
                    var manager = GuiManager.Current;
                    return manager == null ? ExperimentTargetResult.Failed("GuiManager.Current is unavailable") : ExperimentTargetResult.One(Describe(manager), manager);
                }
            case ExperimentTargetKind.NavMarkerLayer:
                {
                    var layer = GuiManager.NavMarkerLayer;
                    return layer == null ? ExperimentTargetResult.Failed("GuiManager.NavMarkerLayer is unavailable") : ExperimentTargetResult.One(Describe(layer), layer);
                }
            case ExperimentTargetKind.LookedAtEnemy:
                {
                    if (!Aim(out var hit, out var error)) return ExperimentTargetResult.Failed(error);
                    var enemy = ComponentInParents<EnemyAgent>(hit.collider);
                    return enemy == null ? ExperimentTargetResult.Failed("the crosshair is not on an EnemyAgent") : ExperimentTargetResult.One(Describe(enemy), enemy);
                }
            case ExperimentTargetKind.LookedAtDoor:
                {
                    if (!Aim(out var hit, out var error)) return ExperimentTargetResult.Failed(error);
                    var door = DoorOf(hit.collider);
                    return door == null
                        ? ExperimentTargetResult.Failed("the crosshair is not on a door")
                        : ExperimentTargetResult.One(Describe(door.Component) + " (" + door.Kind + ")", door.Component);
                }
            case ExperimentTargetKind.LookedAtTerminal:
                {
                    if (!Aim(out var hit, out var error)) return ExperimentTargetResult.Failed(error);
                    var terminal = ComponentInParents<LG_ComputerTerminal>(hit.collider);
                    return terminal == null ? ExperimentTargetResult.Failed("the crosshair is not on a computer terminal") : ExperimentTargetResult.One(Describe(terminal), terminal);
                }
            case ExperimentTargetKind.LookedAtGenerator:
                {
                    if (!Aim(out var hit, out var error)) return ExperimentTargetResult.Failed(error);
                    var generator = ComponentInParents<LG_PowerGenerator_Core>(hit.collider);
                    return generator == null ? ExperimentTargetResult.Failed("the crosshair is not on a power generator") : ExperimentTargetResult.One(Describe(generator), generator);
                }
            case ExperimentTargetKind.NearestDoor:
                return Nearest(spec, DoorTypes(), "door");
            case ExperimentTargetKind.NearestGenerator:
                return Nearest(spec, new[] { typeof(LG_PowerGenerator_Core), typeof(LG_PowerGeneratorCluster) }, "power generator");
            case ExperimentTargetKind.NearestTerminal:
                return Nearest(spec, new[] { typeof(LG_ComputerTerminal) }, "computer terminal");
            case ExperimentTargetKind.NearestEnemy:
                return Nearest(spec, new[] { typeof(EnemyAgent) }, "enemy");
            case ExperimentTargetKind.Zone:
                {
                    var floor = Builder.CurrentFloor;
                    if (floor == null) return ExperimentTargetResult.Failed("Builder.CurrentFloor is unavailable; no level is loaded");
                    LG_Zone? match = null;
                    foreach (var zone in AllZones(floor))
                    {
                        if (!Matches(zone, spec, floor)) continue;
                        match = zone;
                        break;
                    }
                    return match == null
                        ? ExperimentTargetResult.Failed("no zone matches " + spec.Raw + "; loaded zones are " + LoadedZones(floor))
                        : ExperimentTargetResult.One(Describe(match), match);
                }
            case ExperimentTargetKind.StaticType:
                {
                    var type = ExperimentTypes.Find(spec.TypeName);
                    return type == null
                        ? ExperimentTargetResult.Failed("no loaded type is named '" + spec.TypeName + "'")
                        : ExperimentTargetResult.One("static " + type.FullName, type);
                }
            case ExperimentTargetKind.AllOfType:
                {
                    var types = ExperimentTypes.Match(spec.TypeName);
                    if (types.Count == 0) return ExperimentTargetResult.Failed("no loaded type matches '" + spec.TypeName + "'");
                    var found = new List<object?>();
                    foreach (var type in types)
                    {
                        if (!typeof(Component).IsAssignableFrom(type)) continue;
                        foreach (var component in ComponentsOf(type)) found.Add(component);
                    }
                    return found.Count == 0
                        ? ExperimentTargetResult.Failed("no live instance of " + spec.TypeName + " is loaded")
                        : ExperimentTargetResult.Set("all " + found.Count + " live instances of " + spec.TypeName, found);
                }
            default:
                return ExperimentTargetResult.Failed("the selector '" + spec.Raw + "' has no resolver");
        }
    }

    private static ExperimentTargetResult Nearest(ExperimentTargetSpec spec, IReadOnlyList<Type> types, string what)
    {
        var player = ForgeCore.Players.Local;
        if (player == null) return ExperimentTargetResult.Failed("no local player agent; 'nearest' needs a reference position");
        var origin = player.transform.position;
        var limit = spec.MaximumDistance > 0 ? spec.MaximumDistance : float.MaxValue;
        object? best = null;
        var bestDistance = float.MaxValue;
        foreach (var type in types)
        {
            foreach (var found in ComponentsOf(type))
            {
                if (spec.LayerFilter.Length > 0 && !MatchesLayer(found, spec.LayerFilter)) continue;
                var distance = Vector3.Distance(origin, found.transform.position);
                if (distance > limit || distance >= bestDistance) continue;
                bestDistance = distance;
                best = found;
            }
        }
        return best == null
            ? ExperimentTargetResult.Failed("no " + what + " within " + (spec.MaximumDistance > 0 ? spec.MaximumDistance.ToString("0.#", CultureInfo.InvariantCulture) + " m" : "range"))
            : ExperimentTargetResult.One(Describe(best) + " distance=" + bestDistance.ToString("0.##", CultureInfo.InvariantCulture), best);
    }

    /// <summary>
    /// The crosshair ray. It starts at the first-person camera and uses the physics layer mask the game
    /// itself uses for interaction, so a ray that sees a door in game sees it here too.
    /// </summary>
    private static bool Aim(out RaycastHit hit, out string error)
    {
        hit = default;
        error = "";
        Camera? camera = null;
        try
        {
            var player = ForgeCore.Players.Local;
            var firstPerson = player == null ? null : player.FPSCamera;
            camera = firstPerson == null ? null : firstPerson.m_camera;
        }
        catch (Exception e)
        {
            error = "the first-person camera could not be read: " + ExperimentValue.Unwrap(e).Message;
            return false;
        }
        if (camera == null)
        {
            error = "the local player has no active first-person camera";
            return false;
        }
        // The default raycast mask; the constant is not projected into the interop assembly, so the value
        // is written out rather than guessed from a layer name.
        const int InteractionLayers = ~(1 << 2);
        if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out hit, DefaultAimDistance, InteractionLayers))
        {
            error = "the crosshair ray hit nothing within " + DefaultAimDistance.ToString("0.#", CultureInfo.InvariantCulture) + " m";
            return false;
        }
        return true;
    }

    private static T? ComponentInParents<T>(Collider? collider) where T : Component
    {
        if (collider == null) return null;
        var component = collider.GetComponentInParent<T>();
        return component;
    }

    /// <summary>
    /// The door on a hit collider. Only the two concrete door classes are reachable by a component lookup:
    /// the interop project projects <c>iLG_Door_Core</c> as a proxy class rather than an interface, so a
    /// type test against it never matches a live component. Doors that use neither class are reported as
    /// "not a door" rather than mislabelled.
    /// </summary>
    private static TouchTarget? DoorOf(Collider? collider)
    {
        if (collider == null) return null;
        var door = collider.GetComponentInParent<LG_SecurityDoor>();
        if (door != null) return new TouchTarget(door, "security door");
        var weak = collider.GetComponentInParent<LG_WeakDoor>();
        if (weak != null) return new TouchTarget(weak, "weak door");
        return null;
    }

    /// <summary>A door component plus what kind of door it is, because the two door types share no base
    /// class and a bare component would lose the distinction in the record.</summary>
    internal sealed record TouchTarget(Component Component, string Kind);

    private static IReadOnlyList<Type> DoorTypes() => new[] { typeof(LG_SecurityDoor), typeof(LG_WeakDoor) };

    private static IEnumerable<LG_Zone> AllZones(LG_Floor floor)
    {
        var zones = floor.allZones;
        if (zones != null)
            foreach (var zone in zones)
                if (zone != null) yield return zone;
        // Extra and static dimensions are not part of allZones; the layer lists are the only other owner.
        var dimension = floor.MainDimension;
        if (dimension?.Layers == null) yield break;
        foreach (var layer in dimension.Layers)
        {
            if (layer?.m_zones == null) continue;
            foreach (var zone in layer.m_zones)
                if (zone != null) yield return zone;
        }
    }

    private static bool Matches(LG_Zone? zone, ExperimentTargetSpec spec, LG_Floor floor)
    {
        if (zone?.Layer == null) return false;
        if ((int)zone.DimensionIndex != (int.TryParse(spec.Dimension, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimension) ? dimension : -1)) return false;
        if (LayerValue(spec.Layer) != (int)zone.Layer.m_type) return false;
        return (int)zone.LocalIndex == (int.TryParse(spec.LocalIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localIndex) ? localIndex : -1);
    }

    private static int LayerValue(string layer) => layer switch
    {
        "MainLayer" => 0,
        "SecondaryLayer" => 1,
        "ThirdLayer" => 2,
        _ => int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1
    };

    private static bool MatchesLayer(Component component, string layer)
    {
        var value = LayerValue(layer);
        if (component.gameObject.layer == value) return true;
        var zone = component.GetComponentInParent<LG_Zone>();
        return zone != null && zone.Layer != null && (int)zone.Layer.m_type == value;
    }

    private static string LoadedZones(LG_Floor floor)
    {
        var zones = AllZones(floor).Take(12).ToArray();
        return zones.Length == 0 ? "none" : string.Join(", ", zones.Select(zone => RuntimeDiagnostics.Zone(zone)));
    }

    internal static string Describe(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case Type type: return "static " + type.FullName;
            case LG_Zone zone: return "zone " + RuntimeDiagnostics.Zone(zone);
            case Component component:
                {
                    if (component == null) return "destroyed component";
                    return component.GetType().Name + " " + RuntimeDiagnostics.PathOf(component.transform);
                }
            default: return ExperimentValue.Identity(value);
        }
    }
}
