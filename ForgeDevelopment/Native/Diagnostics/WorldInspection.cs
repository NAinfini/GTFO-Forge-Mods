using System;
using System.Collections.Generic;
using System.Diagnostics;
#if !WORLD_INSPECTION_RULES_ONLY
using AIGraph;
using Gear;
using LevelGeneration;
using UnityEngine;
using UnityEngine.AI;
#endif

namespace ForgeDevelopment.Native;

#if !WORLD_INSPECTION_RULES_ONLY
internal static class WorldInspection
{
    internal static IEnumerable<byte> Capture(ProjectInspectionSession session)
    {
        using var iterator = CaptureWorld(session).GetEnumerator();
        try
        {
            while (session.AcceptWorld(RuntimeDiagnostics.WorldEpoch, RuntimeDiagnostics.SimulationTick))
            {
                if (!iterator.MoveNext()) yield break;
                yield return iterator.Current;
            }
        }
        finally
        {
            if (!session.IsClosed) session.Cancel(session.WorldEpoch == RuntimeDiagnostics.WorldEpoch
                ? RuntimeDiagnostics.SimulationTick : null, "iterator_disposed");
        }
    }

    private static IEnumerable<byte> CaptureWorld(ProjectInspectionSession session)
    {
        var report = session.Report;
        if (!session.AcceptWorld(RuntimeDiagnostics.WorldEpoch, RuntimeDiagnostics.SimulationTick)) yield break;
        var floor = Builder.CurrentFloor;
        var layouts = ActiveLayouts(report, floor, out var layoutContextComplete);
        session.Start(layouts, RuntimeDiagnostics.SimulationTick);
        if (!layoutContextComplete) session.MarkPartial();
        if (floor == null)
        {
            report.Check("world", "floor", "missing", "Builder.CurrentFloor was unavailable after FactoryDone.");
            report.Issue("missing_floor", "Builder.CurrentFloor", "No generated floor was available for inspection.");
            session.MarkPartial();
            session.Complete(RuntimeDiagnostics.SimulationTick);
            yield break;
        }

        var activeTicks = 0L;
        var zoneCount = 0;
        var areaCount = 0;
        var geomorphCount = 0;
        var plugCount = 0;
        var terminalCount = 0;
        var terminalItemCount = 0;
        var itemCount = 0;
        var geomorphs = new HashSet<int>();
        var inspectedPairs = new HashSet<string>(StringComparer.Ordinal);

        var zones = floor.allZones;
        if (zones == null || zones.Count == 0)
        {
            session.MarkPartial();
            report.Check("world", "zones", "missing", "The generated floor contains no zones.");
        }

        if (zones != null)
        {
            foreach (var zone in zones)
            {
                if (zone == null)
                {
                    session.MarkPartial();
                    activeTicks += Inspect(session, "floor/zones", () =>
                    {
                        report.Check("zone", "null", "missing", "The floor zone list contains a null entry.");
                        report.Issue("missing_zone", "floor/zones", "The floor zone list contains a null entry.");
                    });
                    yield return 0;
                    continue;
                }

                zoneCount++;
                var zoneName = SafeZone(session, zone, "zone-path-unavailable");
                activeTicks += Inspect(session, zoneName, () => InspectZone(session, floor, layouts, zone, zoneName));
                yield return 0;

                var areas = zone.m_areas;
                if (areas == null || areas.Count == 0)
                {
                    session.MarkPartial();
                    activeTicks += Inspect(session, zoneName, () =>
                        report.Check("zone_areas", zoneName, "missing", "The zone contains no generated areas."));
                    yield return 0;
                    continue;
                }

                foreach (var area in areas)
                {
                    var areaPath = area == null ? zoneName + "/null-area" : SafePath(session, area, zoneName + "/area-path-unavailable");
                    if (area == null)
                    {
                        session.MarkPartial();
                        activeTicks += Inspect(session, areaPath, () =>
                        {
                            report.Check("area", areaPath, "missing", "The zone area list contains a null entry.");
                            report.Issue("missing_area", zoneName, "The zone area list contains a null entry.");
                        });
                        yield return 0;
                        continue;
                    }

                    areaCount++;
                    activeTicks += Inspect(session, areaPath, () => InspectArea(report, zone, area, areaPath));
                    yield return 0;

                    var geomorph = area.m_geomorph;
                    if (geomorph == null)
                    {
                        session.MarkPartial();
                        report.Check("area_geomorph", areaPath, "missing", "Area has no geomorph owner.");
                        continue;
                    }
                    var geomorphPath = SafePath(session, geomorph, areaPath + "/geomorph-path-unavailable");
                    if (!geomorphs.Add(geomorph.GetInstanceID())) continue;

                    geomorphCount++;
                    activeTicks += Inspect(session, geomorphPath, () => InspectGeomorph(session, geomorph, geomorphPath));
                    yield return 0;

                    var plugs = geomorph.m_plugs;
                    if (plugs == null) { session.MarkPartial(); continue; }
                    foreach (var plug in plugs)
                    {
                        var plugPath = plug == null ? geomorphPath + "/null-plug" : SafePath(session, plug, geomorphPath + "/plug-path-unavailable");
                        plugCount++;
                        if (plug == null) session.MarkPartial();
                        activeTicks += Inspect(session, plugPath, () => InspectPlug(report, plug, geomorph, plugPath, inspectedPairs));
                        yield return 0;
                    }
                }
            }
        }

        // Yield between scene objects instead of allocating the whole floor's component array.
        var components = ForgeCore.GameFloor.Components(floor.transform);
        foreach (var component in components)
        {
            if (component == null)
            {
                yield return 0;
                continue;
            }
            LG_GenericTerminalItem? terminalItem = null;
            LG_ComputerTerminal? terminal = null;
            ItemInLevel? item = null;
            activeTicks += Inspect(session, "floor/component-classification", () =>
            {
                terminalItem = component.TryCast<LG_GenericTerminalItem>();
                if (terminalItem == null) terminal = component.TryCast<LG_ComputerTerminal>();
                if (terminalItem == null && terminal == null) item = component.TryCast<ItemInLevel>();
            });
            if (terminalItem == null && terminal == null && item == null) { yield return 0; continue; }
            var path = SafePath(session, component, "floor/component-path-unavailable");
            if (terminalItem != null)
            {
                terminalItemCount++;
                activeTicks += Inspect(session, path, () => InspectTerminalItem(report, terminalItem, path));
            }
            else if (terminal != null)
            {
                terminalCount++;
                activeTicks += Inspect(session, path, () => InspectTerminal(report, terminal, path));
            }
            else if (item != null)
            {
                itemCount++;
                activeTicks += Inspect(session, path, () => InspectItem(report, item, path));
            }
            yield return 0;
        }

        session.Complete(RuntimeDiagnostics.SimulationTick);
        report.Event("world_inspection", session.IsPartial ? "partial" : "complete", RuntimeDiagnostics.PathOf(floor.transform), new()
        {
            ["zones"] = zoneCount.ToString(),
            ["areas"] = areaCount.ToString(),
            ["geomorphs"] = geomorphCount.ToString(),
            ["plugs"] = plugCount.ToString(),
            ["terminals"] = terminalCount.ToString(),
            ["terminalItems"] = terminalItemCount.ToString(),
            ["items"] = itemCount.ToString()
        }, activeTicks * 1000.0 / Stopwatch.Frequency);
        report.Check("world_inspection", "floor", session.IsPartial ? "partial" : "observed",
            session.IsPartial ? "Inspection ended with missing, unsupported or unreadable observations; coverage is partial."
                : "Read-only inspection completed. Objective and script behavior remain unverified.");
    }

    private static IReadOnlyList<ProjectLayoutKey> ActiveLayouts(DiagnosticsReport report, LG_Floor? floor,
        out bool complete)
    {
        complete = false;
        var expedition = Builder.LevelGenExpedition;
        if (floor?.MainDimension == null || (int)floor.MainDimension.DimensionIndex != 0 || expedition == null)
        {
            report.Check("project_layout_context", "floor", "unverified",
                "The main dimension or active expedition layout context is unavailable.");
            return Array.Empty<ProjectLayoutKey>();
        }
        // The three explicit expedition layout IDs describe Reality's layers only.
        // Extra/static dimensions need verified Map creation context, not a guessed ID.
        var ids = new[] { expedition.LevelLayoutData, expedition.SecondaryLayout, expedition.ThirdLayout };
        var layouts = new List<ProjectLayoutKey>(3);
        var nativeLayers = floor.MainDimension.Layers;
        var seen = new HashSet<int>();
        complete = nativeLayers != null && nativeLayers.Count is > 0 and <= 3;
        if (complete)
            foreach (var nativeLayer in nativeLayers!)
            {
                var layer = nativeLayer == null ? -1 : (int)nativeLayer.m_type;
                if (nativeLayer == null || (nativeLayer.m_dimension == null || nativeLayer.m_dimension.Pointer != floor.MainDimension.Pointer) ||
                    layer < 0 || layer >= ids.Length || !seen.Add(layer) || ids[layer] == 0)
                { complete = false; continue; }
                layouts.Add(new ProjectLayoutKey(ids[layer], 0, layer));
            }
        complete &= seen.Contains(0);
        if (!complete) report.Check("project_layout_context", "floor", "unverified",
            "Active main-dimension layers or their explicit expedition layout IDs are incomplete.");
        return layouts;
    }

    private static long Inspect(ProjectInspectionSession session, string source, Action action)
    {
        var report = session.Report;
        var started = Stopwatch.GetTimestamp();
        try { action(); }
        catch (Exception e) { session.MarkPartial(); report.Issue("diagnostic_error", source, e.Message, e.ToString()); }
        return Stopwatch.GetTimestamp() - started;
    }

    private static string SafePath(ProjectInspectionSession session, Component component, string fallback)
    {
        var report = session.Report;
        try { return RuntimeDiagnostics.PathOf(component.transform); }
        catch (Exception e)
        {
            session.MarkPartial();
            report.Issue("diagnostic_error", fallback, e.Message, e.ToString());
            return fallback;
        }
    }

    private static string SafeZone(ProjectInspectionSession session, LG_Zone zone, string fallback)
    {
        var report = session.Report;
        try { return RuntimeDiagnostics.Zone(zone); }
        catch (Exception e)
        {
            session.MarkPartial();
            report.Issue("diagnostic_error", fallback, e.Message, e.ToString());
            return fallback;
        }
    }

    private static void InspectZone(ProjectInspectionSession session, LG_Floor floor, IReadOnlyList<ProjectLayoutKey> layouts, LG_Zone zone, string subject)
    {
        var report = session.Report;
        var nodes = zone.m_courseNodes;
        var fields = new Dictionary<string, string>
        {
            ["path"] = RuntimeDiagnostics.PathOf(zone.transform),
            ["position"] = RuntimeDiagnostics.Vector(zone.Position),
            ["buildStatus"] = zone.m_buildStatus.ToString(),
            ["areas"] = (zone.m_areas?.Count ?? 0).ToString(),
            ["courseNodes"] = (nodes?.Count ?? 0).ToString(),
            ["navInfo"] = zone.m_navInfo == null ? "missing" : zone.m_navInfo.ToString() ?? "unavailable"
        };
        report.Event("world_inspection", "zone", subject, fields);
        var zoneData = zone.m_settings?.m_zoneData;
        if (zoneData == null)
        {
            session.MarkPartial();
            report.Check("zone_enemy_respawn_policy", subject, "missing_data", "The live zone has no ExpeditionZoneData; respawn tuning could not be observed.");
        }
        else
        {
            var respawn = new Dictionary<string, string>
            {
                ["enabled"] = zoneData.EnemyRespawning.ToString(),
                ["requireOtherZone"] = zoneData.EnemyRespawnRequireOtherZone.ToString(),
                ["courseNodeDistance"] = zoneData.EnemyRespawnRoomDistance.ToString(),
                ["intervalSeconds"] = RuntimeDiagnostics.Number(zoneData.EnemyRespawnTimeInterval),
                ["countMultiplier"] = RuntimeDiagnostics.Number(zoneData.EnemyRespawnCountMultiplier),
                // The authored value is a multiplier, not a bounded probability: clamping it would report an
                // authored 2.5x as 100% and silently hide the difference from 1x.
                ["countPercent"] = RuntimeDiagnostics.Number(zoneData.EnemyRespawnCountMultiplier * 100f),
                ["excludeCount"] = (zoneData.EnemyRespawnExcludeList?.Count ?? 0).ToString(),
                ["healthMulti"] = RuntimeDiagnostics.Number(zoneData.HealthMulti),
                ["weaponAmmoMulti"] = RuntimeDiagnostics.Number(zoneData.WeaponAmmoMulti),
                ["toolAmmoMulti"] = RuntimeDiagnostics.Number(zoneData.ToolAmmoMulti),
                ["disinfectionMulti"] = RuntimeDiagnostics.Number(zoneData.DisinfectionMulti)
            };
            report.Event("world_inspection", "zone_respawn_policy", subject, respawn);
            report.Check("zone_enemy_respawn_policy", subject, zoneData.EnemyRespawning ? "enabled" : "disabled",
                $"count={respawn["countPercent"]}%; interval={respawn["intervalSeconds"]}s; CourseNodeDistance={respawn["courseNodeDistance"]}; requireOtherZone={respawn["requireOtherZone"]}.");
        }
        var layer = zone.Layer;
        ProjectLayoutKey? layout = null;
        if (layer != null && (int)zone.DimensionIndex == 0 && layer.m_dimension != null && floor.MainDimension != null && layer.m_dimension.Pointer == floor.MainDimension.Pointer &&
            layer.m_zones?.Contains(zone) == true && floor.MainDimension?.Layers?.Contains(layer) == true)
            foreach (var key in layouts)
                if (key.Dimension == 0 && key.Layer == (int)layer.m_type) { layout = key; break; }
        if (layout == null)
        {
            session.MarkPartial();
            report.Check("project_zone_context", subject, "unverified",
                "No verified main-dimension layout/layer ownership was available; no authored zone is inferred.");
        }
        else session.Scan?.ObserveZone(new ProjectZoneCandidate(zone.GetInstanceID(), layout.LayoutId,
            layout.Dimension, layout.Layer, (int)zone.LocalIndex));
        report.Check("zone_course_nodes", subject, nodes == null || nodes.Count == 0 ? "missing_data" : "observed",
            nodes == null || nodes.Count == 0 ? "No course nodes are registered to the zone." : $"Observed {nodes.Count} registered course nodes.");
    }

    private static void InspectArea(DiagnosticsReport report, LG_Zone expectedZone, LG_Area area, string subject)
    {
        var node = area.m_courseNode;
        var actualZone = area.m_zone;
        var nodeStatus = node == null ? "missing" : node.IsValid ? "observed" : "invalid";
        var fields = new Dictionary<string, string>
        {
            ["zone"] = RuntimeDiagnostics.Zone(actualZone),
            ["expectedZone"] = RuntimeDiagnostics.Zone(expectedZone),
            ["position"] = RuntimeDiagnostics.Vector(area.Position),
            ["uid"] = area.UID.ToString(),
            ["courseNode"] = NodeSubject(node),
            ["courseNodeStatus"] = nodeStatus,
            ["navigationHolder"] = node?.m_navigationInfoHolder == null ? "missing" : "present"
        };
        report.Event("world_inspection", "area", subject, fields);

        if (actualZone != expectedZone)
        {
            report.Check("area_zone", subject, "mismatch", "Area ownership does not match the zone list that contains it.");
            report.Issue("area_zone_mismatch", subject, $"Expected {RuntimeDiagnostics.Zone(expectedZone)}, observed {RuntimeDiagnostics.Zone(actualZone)}.");
        }
        else report.Check("area_zone", subject, "observed", "Area ownership matches its containing zone list.");

        if (node == null)
        {
            report.Check("area_course_node", subject, "missing_data", "The area has no CourseNode; no navigation sample was attempted.");
            return;
        }

        var nodeConsistent = node.m_area == area && node.m_zone == actualZone && actualZone?.m_courseNodes?.Contains(node) == true;
        report.Check("area_course_node", subject, node.IsValid && nodeConsistent ? "observed" : "mismatch",
            $"valid={node.IsValid}; nodeAreaMatches={node.m_area == area}; nodeZoneMatches={node.m_zone == actualZone}; zoneRegistersNode={actualZone?.m_courseNodes?.Contains(node) == true}.");
        report.Check("area_navigation_data", subject, node.m_navigationInfoHolder == null ? "missing_data" : "observed",
            node.m_navigationInfoHolder == null ? "The CourseNode navigation holder is missing." : "The CourseNode navigation holder is present.");

        if (!ForgeCore.Level.TrySnap(node.Position, 4f, out var hit))
        {
            report.Check("area_navmesh_sample", subject, "sample_failed",
                "CourseNode data exists, but NavMesh.SamplePosition failed within 4 m. This alone does not prove that the area is unreachable.");
            return;
        }
        report.Check("area_navmesh_sample", subject, "observed_sample",
            $"Observed a NavMesh sample at {RuntimeDiagnostics.Vector(hit)}. This is not a proof of route reachability.");
    }

    private static void InspectGeomorph(ProjectInspectionSession session, LG_Geomorph geomorph, string subject)
    {
        var report = session.Report;
        var zone = RuntimeDiagnostics.Zone(geomorph.m_zone);
        report.Event("world_inspection", "geomorph", subject, new()
        {
            ["prefab"] = geomorph.m_geoPrefab == null ? geomorph.name : geomorph.m_geoPrefab.name,
            ["zone"] = zone,
            ["position"] = RuntimeDiagnostics.Vector(geomorph.transform.position),
            ["rotation"] = RuntimeDiagnostics.Rotation(geomorph.transform.rotation),
            ["placed"] = geomorph.m_placed.ToString(),
            ["areasSetup"] = geomorph.m_areasSetup.ToString(),
            ["gatesSetup"] = geomorph.m_gatesSetup.ToString()
        });
        var owner = geomorph.m_zone;
        if (owner == null) { session.MarkPartial(); return; }
        var nativeAreas = geomorph.m_areas;
        var areas = new List<ProjectAreaCandidate>();
        if (nativeAreas == null || nativeAreas.Length == 0) session.MarkPartial();
        else if (nativeAreas.Length > ProjectObjectReferences.MaximumAreas)
        {
            session.MarkPartial();
            report.Check("project_geomorph_areas", subject, "partial", "Geomorph area budget exceeded; mapping was not enumerated.");
        }
        else foreach (var area in nativeAreas)
        {
            if (area == null || area.m_geomorph != geomorph || area.m_zone != owner)
            { session.MarkPartial(); continue; }
            areas.Add(new ProjectAreaCandidate(area.GetInstanceID(), area.UID));
        }
        // The areas are this scan's own observation; which authored reference names this geomorph is the one
        // room resolver's answer, so no source identity is manufactured here from the prefab's display name.
        session.Scan?.ObserveAreas(new ProjectGeomorphAreas(geomorph.GetInstanceID(), owner.GetInstanceID(), areas));
        report.Check("project_geomorph_source", subject, "unverified",
            "Creation-context source identity is unavailable; prefab names cannot establish an authored room match.",
            new DiagnosticNativeObject("geomorph", geomorph.GetInstanceID()));
    }

    private static void InspectPlug(DiagnosticsReport report, LG_Plug? plug, LG_Geomorph geomorph, string subject, HashSet<string> inspectedPairs)
    {
        if (plug == null)
        {
            report.Check("plug", subject, "missing", "A geomorph plug list contains a null entry.");
            return;
        }

        var pair = plug.m_pariedWith;
        var geomorphZone = geomorph.m_zone;
        var fromZone = geomorphZone == null ? plug.m_linksFrom?.m_zone : null;
        var parentZone = geomorphZone == null && fromZone == null ? plug.OriginalParentArea?.m_zone : null;
        var toZone = geomorphZone == null && fromZone == null && parentZone == null ? plug.m_linksTo?.m_zone : null;
        var ownerZone = WorldInspectionRules.PlugOwner(geomorphZone, fromZone, parentZone, toZone, out var ownerSource);
        var pairPath = pair == null ? "none" : RuntimeDiagnostics.PathOf(pair.transform);
        var directionDot = pair == null || plug.m_forward.sqrMagnitude < 0.0001f || pair.m_forward.sqrMagnitude < 0.0001f
            ? "unavailable"
            : RuntimeDiagnostics.Number(Vector3.Dot(plug.m_forward.normalized, pair.m_forward.normalized));
        report.Event("world_inspection", "plug", subject, new()
        {
            ["zone"] = RuntimeDiagnostics.Zone(ownerZone),
            ["ownerSource"] = ownerSource,
            ["geomorphZone"] = RuntimeDiagnostics.Zone(geomorphZone),
            ["pairedWith"] = pairPath,
            ["direction"] = plug.m_dir.ToString(),
            ["forward"] = RuntimeDiagnostics.Vector(plug.m_forward),
            ["position"] = RuntimeDiagnostics.Vector(plug.m_position),
            ["linkPosition"] = RuntimeDiagnostics.Vector(plug.m_linkPosition),
            ["pairDirectionDot"] = directionDot,
            ["pairHeightDifference"] = pair == null ? "unavailable" : RuntimeDiagnostics.Number(Math.Abs(plug.m_linkPosition.y - pair.m_linkPosition.y)),
            ["linksFrom"] = AreaSubject(plug.m_linksFrom),
            ["linksTo"] = AreaSubject(plug.m_linksTo)
        });
        report.Check("plug_owner", subject, ownerZone == null ? "missing_data" : "observed",
            $"ownerSource={ownerSource}; paired={pair != null}. " + (ownerZone == null
                ? "Neither the containing geomorph nor the plug's associated areas supplied a zone."
                : "Owning zone: " + RuntimeDiagnostics.Zone(ownerZone) + ". Unpaired plugs may belong to unused areas without a zone; the containing geomorph remains their owner."));

        if (pair == null)
        {
            report.Check("plug_pair", subject, "not_checked", "The plug is unpaired. No declared connection expectation was supplied by the generated world.");
            return;
        }

        var reverse = pair.m_pariedWith == plug;
        report.Check("plug_pair", subject, reverse ? "observed" : "mismatch",
            reverse ? "The paired plug points back to this plug." : "The paired plug does not point back to this plug.");
        if (!reverse) report.Issue("plug_pair_mismatch", subject, "The native plug pairing is not bidirectional.");
        report.Check("plug_alignment", subject, directionDot == "unavailable" ? "missing_data" : "observed_measurement",
            $"opposingForwardDot={directionDot}; linkHeightDifference={RuntimeDiagnostics.Number(Math.Abs(plug.m_linkPosition.y - pair.m_linkPosition.y))}. Values are recorded without assuming a prefab-specific validity threshold.");

        var pairSubject = string.CompareOrdinal(subject, pairPath) <= 0 ? subject + " <-> " + pairPath : pairPath + " <-> " + subject;
        if (!inspectedPairs.Add(pairSubject)) return;
        InspectConnectionPath(report, plug, pair, pairSubject);
    }

    private static void InspectConnectionPath(DiagnosticsReport report, LG_Plug first, LG_Plug second, string subject)
    {
        var firstForward = first.m_forward.sqrMagnitude < 0.0001f ? first.transform.forward : first.m_forward.normalized;
        var secondForward = second.m_forward.sqrMagnitude < 0.0001f ? second.transform.forward : second.m_forward.normalized;
        var firstQuery = first.m_position - firstForward * 2f;
        var secondQuery = second.m_position - secondForward * 2f;
        if (!ForgeCore.Level.TrySnap(firstQuery, 3f, out var firstHit) || !ForgeCore.Level.TrySnap(secondQuery, 3f, out var secondHit))
        {
            report.Check("plug_navmesh_path", subject, "sample_failed",
                "A NavMesh endpoint could not be sampled within 3 m. This does not by itself prove the connection or expedition is unreachable.");
            return;
        }

        var path = new NavMeshPath();
        var calculated = NavMesh.CalculatePath(firstHit, secondHit, -1, path);
        var complete = calculated && path.status == NavMeshPathStatus.PathComplete;
        report.Check("plug_navmesh_path", subject, complete ? "complete_path_observed" : "path_not_observed",
            $"calculated={calculated}; status={path.status}; start={RuntimeDiagnostics.Vector(firstHit)}; end={RuntimeDiagnostics.Vector(secondHit)}. " +
            "This limited connector check does not establish that an objective route or the expedition is completable.");
    }

    private static void InspectTerminalItem(DiagnosticsReport report, LG_GenericTerminalItem? item, string subject)
    {
        if (item == null)
        {
            report.Check("terminal_item", subject, "missing", "The terminal-item component enumeration contains a null entry.");
            return;
        }
        var node = item.SpawnNode;
        var zone = RuntimeDiagnostics.Zone(node?.m_zone);
        report.Event("world_inspection", "terminal_item", subject, new()
        {
            ["key"] = item.TerminalItemKey ?? "",
            ["spawnNode"] = NodeSubject(node),
            ["zone"] = zone,
            ["showInInventory"] = item.ShowInFloorInventory.ToString()
        });
        report.Check("terminal_item_node", subject, node == null ? "missing_data" : node.IsValid ? "observed" : "invalid",
            node == null ? "LG_GenericTerminalItem.SpawnNode is missing." : $"SpawnNode valid={node.IsValid}; zone={zone}.");
    }

    private static void InspectTerminal(DiagnosticsReport report, LG_ComputerTerminal? terminal, string subject)
    {
        if (terminal == null)
        {
            report.Check("terminal", subject, "missing", "The terminal enumeration contains a null entry.");
            return;
        }
        var node = terminal.SpawnNode;
        var manager = LG_ComputerTerminalManager.Current;
        var managerHas = false;
        var managerMatches = false;
        if (manager?.m_terminals != null)
        {
            managerHas = manager.m_terminals.ContainsKey(terminal.SyncID);
            if (managerHas) managerMatches = manager.m_terminals[terminal.SyncID] == terminal;
        }
        var zoneHas = node?.m_zone?.TerminalsSpawnedInZone?.Contains(terminal) == true;
        var zone = RuntimeDiagnostics.Zone(node?.m_zone);
        report.Event("world_inspection", "terminal", subject, new()
        {
            ["syncId"] = terminal.SyncID.ToString(),
            ["publicName"] = terminal.PublicName ?? "",
            ["spawnNode"] = NodeSubject(node),
            ["zone"] = zone,
            ["isSetup"] = terminal.m_isSetup.ToString(),
            ["isRegistered"] = terminal.IsRegistered.ToString(),
            ["registrationScope"] = "warden_objective_item",
            ["wardenObjectiveType"] = terminal.WardenObjectiveType.ToString(),
            ["connectedReactor"] = terminal.ConnectedReactor == null ? "none" : RuntimeDiagnostics.PathOf(terminal.ConnectedReactor.transform),
            ["managerHasId"] = managerHas.ToString(),
            ["managerMatches"] = managerMatches.ToString(),
            ["zoneRegistersTerminal"] = zoneHas.ToString()
        });
        var status = WorldInspectionRules.TerminalSetupStatus(terminal.m_isSetup, managerMatches, zoneHas, node?.IsValid == true, out var failures);
        report.Check("terminal_setup", subject, status,
            $"setup={terminal.m_isSetup}; registeredFlag={terminal.IsRegistered}; managerMatches={managerMatches}; zoneRegisters={zoneHas}; nodeValid={node?.IsValid == true}; failures={failures}. " +
            "Basis: initialized terminal, matching manager instance, zone terminal membership and valid SpawnNode. IsRegistered records iWardenObjectiveItem registration, not LG_ComputerTerminalManager registration, and is not a setup requirement.");
    }

    private static void InspectItem(DiagnosticsReport report, ItemInLevel? item, string subject)
    {
        if (item == null)
        {
            report.Check("item", subject, "missing", "The ItemInLevel enumeration contains a null entry.");
            return;
        }
        var courseNode = item.CourseNode;
        var isArtifact = item.TryCast<ArtifactPickup_Core>() != null;
        var artifactSpawnNode = isArtifact && courseNode == null ? item.GetComponentInParent<LG_PickupItem>()?.SpawnNode : null;
        var node = WorldInspectionRules.ItemNode(courseNode, isArtifact, artifactSpawnNode, out var nodeSource);
        var parentArea = item.GetComponentInParent<LG_Area>();
        var zone = RuntimeDiagnostics.Zone(node?.m_zone);
        var nodeContains = node?.m_itemsInNode?.Contains(item) == true;
        var pack = item.TryCast<ResourcePackPickup>();
        report.Event("world_inspection", "item", subject, new()
        {
            ["courseNode"] = NodeSubject(courseNode),
            ["artifactSpawnNode"] = NodeSubject(artifactSpawnNode),
            ["inspectedNode"] = NodeSubject(node),
            ["nodeSource"] = nodeSource,
            ["zone"] = zone,
            ["parentArea"] = AreaSubject(parentArea),
            ["nodeContainsItem"] = nodeContains.ToString(),
            ["itemInstanceId"] = item.GetInstanceID().ToString(),
            ["itemDataId"] = item.ItemDataBlock?.persistentID.ToString() ?? "unavailable",
            ["inventorySlot"] = item.ItemDataBlock?.inventorySlot.ToString() ?? "unavailable",
            ["nativeAmmo"] = RuntimeDiagnostics.Number(item.GetCustomData().ammo),
            ["resourcePackType"] = pack == null ? "not_resource_pack" : pack.m_packType.ToString(),
            ["authority"] = ForgeCore.Session.IsHost ? "host" : "client_observation",
            ["position"] = RuntimeDiagnostics.Vector(item.transform.position)
        });
        if (node == null)
        {
            report.Check("item_course_node", subject, WorldInspectionRules.ItemNodeStatus(false, false, false), isArtifact
                ? "ItemInLevel.CourseNode and the artifact's parent LG_PickupItem.SpawnNode are both missing; no node was inferred from position or area."
                : "ItemInLevel.CourseNode is missing; the artifact-only spawn-node rule does not apply.");
            return;
        }
        var parentMatches = parentArea == null || node.m_area == parentArea;
        report.Check("item_course_node", subject, WorldInspectionRules.ItemNodeStatus(true, node.IsValid, parentMatches),
            $"nodeSource={nodeSource}; courseNodePresent={courseNode != null}; valid={node.IsValid}; parentAreaMatches={parentMatches}; nodeContainsItem={nodeContains}. " +
            (nodeSource == "artifact_spawn_node" ? "LG_PickupItem.SetupAsArtifact uses its SpawnNode without assigning the child ItemInLevel.CourseNode. " : "") +
            "The node item list is recorded but is not assumed mandatory for every item state.");
    }

    private static string NodeSubject(AIG_CourseNode? node) => node == null
        ? "missing"
        : $"{RuntimeDiagnostics.PathOf(node.gameObject?.transform)}#node-{node.NodeID}";

    private static string AreaSubject(LG_Area? area) => area == null
        ? "missing"
        : RuntimeDiagnostics.PathOf(area.transform) + "#area-" + area.UID;

}
#endif

// Native adapters above supply observed members; these rules can run offline from the built assembly without Unity.
internal static class WorldInspectionRules
{
    internal static string TerminalSetupStatus(bool setup, bool managerMatches, bool zoneRegisters, bool nodeValid, out string failures)
    {
        failures = (!setup ? "not_setup;" : "") + (!managerMatches ? "manager_entry_missing_or_different;" : "") +
            (!zoneRegisters ? "absent_from_zone_terminal_list;" : "") + (!nodeValid ? "spawn_node_missing_or_invalid;" : "");
        if (failures.Length != 0) return "mismatch";
        failures = "none";
        return "observed";
    }

    internal static T? ItemNode<T>(T? courseNode, bool isArtifact, T? artifactSpawnNode, out string source) where T : class
    {
        source = courseNode != null ? "course_node" : isArtifact && artifactSpawnNode != null ? "artifact_spawn_node" : "unavailable";
        return courseNode ?? (isArtifact ? artifactSpawnNode : null);
    }

    internal static string ItemNodeStatus(bool hasNode, bool valid, bool parentMatches) =>
        !hasNode ? "missing_data" : valid && parentMatches ? "observed" : "mismatch";

    internal static T? PlugOwner<T>(T? geomorphZone, T? linksFromZone, T? originalParentZone, T? linksToZone, out string source) where T : class
    {
        source = geomorphZone != null ? "geomorph_zone" : linksFromZone != null ? "links_from_zone" :
            originalParentZone != null ? "original_parent_zone" : linksToZone != null ? "links_to_zone" : "unavailable";
        return geomorphZone ?? linksFromZone ?? originalParentZone ?? linksToZone;
    }
}
