using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One generation observation after native lock CheckFlip, never an orientation override.
/// Keep every built gate, including same-zone and prefab gates which have no authored security address.</summary>
internal static class DoorOrientationObservation
{
    private static readonly Dictionary<IntPtr, (LG_Gate Gate, bool PlugFlipped)> Gates = new();

    internal static void Capture(LG_BuildGateJob job)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        if (job?.m_gate is { } gate && !gate.WasCollected)
            Gates.TryAdd(gate.Pointer, (gate, job.m_plugWasFlipped));
    }

    internal static void Clear() => Gates.Clear();

    internal static void Finish()
    {
        try
        {
            if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
            var index = ZoneIndex.Current(null);
            foreach (var row in Gates.Values)
            {
                var gate = row.Gate;
                string target = "native-gate:" + gate.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                try
                {
                    var core = gate.SpawnedDoor;
                    if (core == null) continue; // A capped opening has no door to orient.
                    var component = core.TryCast<Component>()
                        ?? throw new InvalidOperationException("The spawned door has no readable component.");
                    target = "native-door:" + component.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                    var security = core.TryCast<LG_SecurityDoor>();
                    var owner = security == null ? null : index.ZoneOfDoor(security);
                    if (owner != null && ZoneIndex.Coordinates(owner) is { } at)
                        target = MapObjectDoorAddress.Create(at.Dimension, at.Layer, at.Zone)!.ToString();
                    var transform = component.transform
                        ?? throw new InvalidOperationException("The spawned door has no readable transform.");
                    // Read the game's cached properties themselves. This observer runs once per build;
                    // reflection avoids requiring these native-only properties in shared adapter doubles.
                    var progression = ReadProgression(gate);
                    Record(target, security == null ? "weak" : security.m_securityDoorType == eSecurityDoorType.Bulkhead ? "bulkhead" : "security", transform.position,
                        transform.rotation * new Vector3(0, 0, 1), progression.Direction,
                        Area(gate.m_linksFrom), Area(gate.m_linksTo), Area(progression.Source),
                        ReadProperty(gate, "ExpanderRotation").ToString()!, row.PlugFlipped,
                        ReadProperty(gate, "name").ToString()!, gate.GetType().Name);
                }
                catch (Exception error)
                {
                    MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.DoorPoseFailed,
                        "target=" + target + " error=" + error.GetType().Name + ": " + error.Message,
                        RuntimeLogValue.Of("target", target), RuntimeLogValue.Of("exception", error.GetType().Name));
                }
            }
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.DoorPoseFailed,
                "target=generation error=" + error.GetType().Name + ": " + error.Message,
                RuntimeLogValue.Of("target", "generation"), RuntimeLogValue.Of("exception", error.GetType().Name));
        }
        finally { Clear(); }
    }

    internal static (Vector3 Direction, LG_Area Source) ReadProgression(object gate)
        => ((Vector3)ReadProperty(gate, "ProgressionSourceDirection"),
            (LG_Area)ReadProperty(gate, "ProgressionSourceArea"));

    private static object ReadProperty(object native, string name)
        => native.GetType().GetProperty(name)?.GetValue(native)
            ?? throw new InvalidOperationException("Native " + name + " is unavailable.");

    private static string Area(LG_Area? area)
    {
        if (area == null || area.WasCollected) return "unavailable";
        var zone = area.m_zone is { } owner ? ZoneIndex.Coordinates(owner) : null;
        var address = zone is { } at ? "gtfo.zone:" + at.Dimension + ":" + at.Layer + ":" + at.Zone : "unavailable";
        var nativeZoneId = area.m_zone is { } nativeZone ? ReadProperty(nativeZone, "ID").ToString()! : "unavailable";
        return address + " zoneID=" + nativeZoneId + " area=" + area.GetInstanceID() + " areaName=" + (area.gameObject?.name ?? "unavailable")
            + " room=" + (area.m_geomorph?.m_geoPrefab?.name ?? "unavailable")
            + " roomInstance=" + (area.m_geomorph?.GetInstanceID().ToString(CultureInfo.InvariantCulture) ?? "unavailable");
    }

    internal static void Record(string target, string kind, Vector3 position, Vector3 forward, Vector3 progression,
        string from, string to, string source, string expanderRotation, bool plugFlipped, string gateName, string gateClass)
    {
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.DoorPose,
            "target=" + target + " kind=" + kind + " position=" + Vector(position) + " forward=" + Vector(forward)
            + " progressionDirection=" + Vector(progression) + " from={" + from + "} to={" + to + "} progressionSource={" + source
            + "} expanderRotation=" + expanderRotation + " plugFlipped=" + (plugFlipped ? "true" : "false")
            + " gateName=" + gateName + " gateClass=" + gateClass,
            RuntimeLogValue.Of("target", target), RuntimeLogValue.Of("kind", kind),
            RuntimeLogValue.Of("position", Vector(position)), RuntimeLogValue.Of("forward", Vector(forward)),
            RuntimeLogValue.Of("progressionDirection", Vector(progression)), RuntimeLogValue.Of("from", from),
            RuntimeLogValue.Of("to", to), RuntimeLogValue.Of("progressionSource", source),
            RuntimeLogValue.Of("expanderRotation", expanderRotation), RuntimeLogValue.Of("plugFlipped", plugFlipped),
            RuntimeLogValue.Of("gateName", gateName), RuntimeLogValue.Of("gateClass", gateClass));
    }

    private static string Vector(Vector3 value)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z))
            throw new InvalidOperationException("Native door pose contains a non-finite vector.");
        return "[" + value.x.ToString("R", CultureInfo.InvariantCulture) + ","
            + value.y.ToString("R", CultureInfo.InvariantCulture) + "," + value.z.ToString("R", CultureInfo.InvariantCulture) + "]";
    }
}


