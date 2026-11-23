using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeMap.Generation;
using GameData;
using LevelGeneration;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    /// <summary>The set whose pools build a connection or a cap: the terrain of the room the connection leaves (<paramref name="room"/>),
    /// the rule the website's connectionTerrainGroup (room-door-match.ts) predicts candidates with, so one level can mix rooms of
    /// any terrain. A recipe room is registered in the dimension's set, so at a recipe/native seam the native room's terrain is used.
    /// A room of the dimension's own terrain keeps the dimension's set (the native choice, including the author's set); a room of
    /// another terrain uses the vanilla set the plan loads for it (every room resource's set is in RequiredResourceSets).</summary>
    private static ComplexResourceSetDataBlock ConnectionSet(Run run, LG_Plug plug, int room, int other, string path)
    {
        var dimension = plug.OriginalParentArea?.m_zone?.Dimension ?? throw new GenerationRefusal("gen-area-identity", path);
        var dimensionSet = DimensionResources(dimension) ?? throw new GenerationRefusal("gen-area-identity", path);
        var program = run.Program;
        var own = program.Resources[program.Rooms[room].Resource];
        if (own.ForgeRoom && other >= 0 && !program.Resources[program.Rooms[other].Resource].ForgeRoom) own = program.Resources[program.Rooms[other].Resource];
        if (own.ForgeRoom || own.ComplexResourceSet == dimensionSet.persistentID) return dimensionSet;
        ComplexResourceSetDataBlock set;
        try { set = (ComplexResourceSetDataBlock)NativeDataBlockTables.RequireBlock("ComplexResourceSet", own.ComplexResourceSet); }
        catch (Exception containedError) { MapLog.Contained("compiled-map-generator-connections.connection-set", containedError); throw new GenerationRefusal("gen-reference-missing", path + "/complexResourceSet"); }
        return set.ComplexType == dimensionSet.ComplexType ? dimensionSet : set;
    }

    /// <summary>The requested SubComplex pool, or the All pool (5) when that one is empty, as native BuildPlug retries it.</summary>
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>? ConnectorPool(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>>? pools, int sub)
    {
        if (pools == null) return null;
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>? At(int slot) => slot >= 0 && slot < pools.Length && pools[slot] != null && pools[slot].Length > 0 ? pools[slot] : null;
        return At(sub) ?? At((int)Expedition.SubComplex.All);
    }

    /// <summary>LG_BuildPlugJob.Build selects the straight (flat) family for any socket difference up to 1 m, so a straight connector
    /// has no rise to match: the two sockets of a vanilla pair may sit up to that far apart (catalog sockets differ by 0.03 to 0.15 m).</summary>
    internal const float StraightConnectorMaximumDelta = 1f;

    /// <summary>An author-chosen connector must belong to the row's drop family: the family decides where the piece is anchored.
    /// How far its fixed floor-to-floor rise is from the two live sockets is not a refusal (LG_BuildPlugJob.Build checks none; the
    /// original game builds a flat piece between sockets up to 0.58 m apart and a stair within 0.12 m of its rise): it is written
    /// to the readback as a hint with both numbers, and the exporter shows the author the same figures.</summary>
    private static void RequireFixedConnectorHeight(Run run, string connection, int connectorFamily, float connectorRise, int family, float rise)
    {
        if (connectorFamily < 0 || connectorFamily != family)
            throw new GenerationRefusal("gen-connection-height", connection + "/heightFamily",
                "connectorFamily=" + connectorFamily.ToString(CultureInfo.InvariantCulture) + " rowFamily=" + family.ToString(CultureInfo.InvariantCulture));
        float gap = family == 0 ? Math.Max(0f, rise - StraightConnectorMaximumDelta) : Math.Abs(connectorRise - rise);
        if (gap > AlignedGap)
            run.Record("connector-height", connection, new { connectorRise, socketRise = rise, gap, family });
    }

    /// <summary>Door-to-door gap (metres) up to which a connection counts as aligned and says nothing; the website uses the same figure.</summary>
    internal const float AlignedGap = .05f;
    /// <summary>The gate the native pairing linked to either plug of the connection, or null when it linked none (tech-04: a door can hang on either plug; a free-passage gate is not an identity). The native LG_Plug.GetGate throws a
    /// NullReferenceException for connectors without that link (game runs: tech-04 and tech-05), so the field is read instead; it is the
    /// same field GeneratedRoomRegistry.CompleteConnection checks the gate against. A connector instance can hold several gate components
    /// (an instance lookup picked another one and failed gate-mismatch on mining-01), so the instance is not searched.</summary>
    internal static LG_Gate? ConnectorGate(LG_Plug plug)
    {
        var gate = plug.m_gate ?? plug.m_pariedWith?.m_gate;
        return gate != null && gate.Type == LG_GateType.FreePassage ? null : gate; // the game links a free-passage gate at its own time (tech-04); only a door gate is an identity
    }

    private static PlugBinding? FindPlugBinding(Run run, long pointer)
    {
        if (!run.PlugsSorted)
        {
            Array.Sort(run.PlugBindings, 0, run.PlugBindingCount, Comparer<PlugBinding>.Create((a, b) => a.Pointer.CompareTo(b.Pointer)));
            run.PlugsSorted = true;
        }
        int low = 0, high = run.PlugBindingCount - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2; long value = run.PlugBindings[middle].Pointer;
            if (value == pointer) return run.PlugBindings[middle];
            if (value < pointer) low = middle + 1; else high = middle - 1;
        }
        return null;
    }

    /// <summary>The pair and connector of one connection, written at build time: a level refused later never reaches the generation readback.</summary>
    private static void LogConnector(string connection, GameObject prefab, bool withGate, int family, float rise, LG_Plug from, LG_Plug to)
    {
        if (MapLog.Logs(ForgeEngine.Framework.RuntimeLogLevel.Info)) MapLog.Record(ForgeEngine.Framework.RuntimeLogLevel.Info, RoomRecipeCodes.PlugSelected, "Connector chosen for a paired connection.",
            ForgeEngine.Framework.RuntimeLogValue.Quote("connection", connection), ForgeEngine.Framework.RuntimeLogValue.Quote("prefab", prefab.name),
            ForgeEngine.Framework.RuntimeLogValue.Of("withGate", withGate), ForgeEngine.Framework.RuntimeLogValue.Of("family", family), ForgeEngine.Framework.RuntimeLogValue.Of("rise", rise),
            ForgeEngine.Framework.RuntimeLogValue.Quote("from", PlugPose(from)), ForgeEngine.Framework.RuntimeLogValue.Quote("to", PlugPose(to)));
    }
}
