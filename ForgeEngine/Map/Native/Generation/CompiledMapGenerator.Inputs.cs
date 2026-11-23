using System.Collections.Generic;
using GameData;
using LevelGeneration;
using UnityEngine;
using ForgeMap.Generation;
using ForgeCore;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private readonly record struct ZoneInputMatch(ExpeditionZoneData Data, int Count);

    private static void ApplyInputs(Run run)
    {
        foreach (var declaration in Builder.LevelGenExpedition.DimensionDatas)
            if (declaration.Enabled && (int)declaration.DimensionIndex == 22)
                throw new GenerationRefusal("gen-dimension-unsupported", run.Level + "/arena-declaration",
                    "Arena dimensions are not supported, including declarations outside the compiled generation plan.");
        foreach (int dimensionIndex in run.LevelProgram.Dimensions)
        {
            var dimension = run.Program.Dimensions[dimensionIndex];
            if (dimension.NativeAddress == 0) continue;
            int found = 0;
            foreach (var declaration in Builder.LevelGenExpedition.DimensionDatas)
            {
                if ((int)declaration.DimensionIndex != dimension.NativeAddress) continue;
                found++; bool original = declaration.Enabled;
                run.RestoreInputs.Add(() =>
                {
                    if (declaration.Enabled != dimension.Enabled)
                        throw new GenerationRefusal("gen-pin-mismatch", dimension.Id + "/enabled-input-ownership");
                    declaration.Enabled = original;
                });
                declaration.Enabled = dimension.Enabled;
            }
            if (found != 1) throw new GenerationRefusal("gen-dimension-mismatch", dimension.Id + "/native-declaration");
            if (!dimension.Enabled) run.Record("dimension-disabled", dimension.Id, new { nativeAddress = dimension.NativeAddress, enabled = false, beforeCreateAllDimensions = true });
        }
        var zoneInputs = IndexZoneInputs(run);
        foreach (int zoneIndex in run.LevelProgram.Zones)
        {
            var zone = run.Program.Zones[zoneIndex];
            var data = zoneInputs[zoneIndex]
                ?? throw new GenerationRefusal("gen-layout-owner", zone.Id + "/native-zone");
            int sub = data.SubSeed, marker = data.MarkerSubSeed, light = data.LightsSubSeed; var coverage = data.CoverageMinMax;
            int nextSub = unchecked((int)zone.ZoneSeed.Apply(unchecked((uint)sub))),
                nextMarker = unchecked((int)zone.MarkerSeed.Apply(unchecked((uint)marker))),
                nextLight = unchecked((int)zone.LightSeed.Apply(unchecked((uint)light)));
            var nextCoverage = new Vector2(zone.CoverageMinimum, zone.CoverageMaximum);
            run.RestoreInputs.Add(() =>
            {
                if (data.SubSeed != nextSub && data.SubSeed != sub || data.MarkerSubSeed != nextMarker && data.MarkerSubSeed != marker
                    || data.LightsSubSeed != nextLight && data.LightsSubSeed != light
                    || !(data.CoverageMinMax.x == nextCoverage.x && data.CoverageMinMax.y == nextCoverage.y)
                    && !(data.CoverageMinMax.x == coverage.x && data.CoverageMinMax.y == coverage.y))
                    throw new GenerationRefusal("gen-pin-mismatch", zone.Id + "/input-ownership-changed");
                data.SubSeed = sub; data.MarkerSubSeed = marker; data.LightsSubSeed = light; data.CoverageMinMax = coverage;
            });
            data.SubSeed = nextSub; data.MarkerSubSeed = nextMarker; data.LightsSubSeed = nextLight; data.CoverageMinMax = nextCoverage;
            run.Record("zone-inputs-before-constructor", zone.Id, new
            {
                zoneSeed = unchecked((uint)data.SubSeed),
                markerSeed = unchecked((uint)data.MarkerSubSeed),
                lightSeed = unchecked((uint)data.LightsSubSeed),
                minimum = data.CoverageMinMax.x,
                maximum = data.CoverageMinMax.y,
                batch = "before-SetupFloor"
            });
        }
    }

    private static ExpeditionZoneData?[] IndexZoneInputs(Run run)
    {
        var result = new ExpeditionZoneData?[run.Program.Zones.Length];
        var layouts = new Dictionary<uint, Dictionary<int, ZoneInputMatch>>();
        foreach (int zoneIndex in run.LevelProgram.Zones)
        {
            var zone = run.Program.Zones[zoneIndex];
            if (!layouts.TryGetValue(zone.Layout, out var inputs))
            {
                var layout = (LevelLayoutDataBlock)NativeDataBlockTables.RequireBlock("LevelLayout", zone.Layout);
                inputs = new Dictionary<int, ZoneInputMatch>();
                foreach (var candidate in layout.Zones)
                {
                    int localIndex = (int)candidate.LocalIndex;
                    inputs[localIndex] = inputs.TryGetValue(localIndex, out var current)
                        ? new(current.Data, current.Count + 1) : new(candidate, 1);
                }
                layouts.Add(zone.Layout, inputs);
            }
            if (!inputs.TryGetValue(zone.LocalIndex, out var match) || match.Count != 1)
                throw new GenerationRefusal("gen-layout-owner", zone.Id + "/native-zone");
            result[zoneIndex] = match.Data;
        }
        return result;
    }
}
