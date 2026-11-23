using System;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The native FactoryDone stage. GTFO-API patches `LG_Factory.FactoryDone` itself and raises
/// <c>LevelAPI.OnFactoryDone</c> from its postfix, after the body; Map subscribes there (<see cref="MapGameHooks"/>) after the
/// engine's own upstream subscriber, as its postfix used to run last. The model-build window resets, then every reader shares
/// the completed world in this order: the room recipes complete, the
/// authored doors take their initial state, the door locks resolve their generators, the vanilla world objects are
/// snapshotted, the per-zone placement result is reported, the ordinary fixed spawns are placed and their keys audited, and scan coverage is applied. Scan
/// coverage comes after the placements because a row's required items are placed fixed items it looks up by address;
/// the placements read nothing scan coverage writes. A step that throws ends the stage there, written once, and the other
/// subscribers of the event still run.</summary>
internal static class FactoryDoneHooks
{
    internal static void Done()
    {
        try { Steps(); }
        catch (Exception error) { MapLog.Contained("factory-done", error); }
    }

    private static void Steps()
    {
        // The window closes with the level build; nothing below reads it, so closing after the body is the same as before it.
        ModelBuildWindow.Close();
        RoomRecipeHost.Complete();
        DoorBuildApplier.Finish();
        DoorLocksNative.Current?.ResolveGenerators();
        VanillaWorldObjectsBuilt();
        ZonePlacementBuilt();
        LevelGenerationFinished();
        // After the fixed placements: a row's required items resolve through `FixedPlacementSession.WardenItem`.
        // Every scan core, its player scanner, graphics and movement component exist now, and every room's world pose
        // can be composed, so a row keyed by a ChainedPuzzle block is first resolvable here. The route is written here
        // rather than from `CP_BasicMovable.Setup`, where the movement component has no path back to its block
        // (`CP_Bioscan_Core.m_movingComp` is one-way and the block id lives on the instance's `Data`); whether the
        // movement routine recomputes `ScanPositions` afterwards is an in-game question. Which machine writes what is
        // the session's decision (radius and route everywhere, member set and coarse gate on the host alone).
        ScanCoverageSession.Current?.Apply();
    }

    private static void VanillaWorldObjectsBuilt()
    {
        try
        {
            ZoneIndex.Reset();
            VanillaWorldObjects.Build(ForgeEngine.Plugin.Runtime?.WorldEpoch ?? 0);
        }
        catch (Exception error)
        {
            VanillaWorldObjects.Clear();
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused, "Native world object snapshot refused.",
                RuntimeLogValue.Of("reason", error.GetType().Name));
        }
    }

    /// <summary>The per-zone placement result, read before fixed placement moves anything.</summary>
    private static void ZonePlacementBuilt()
    {
        try { ZonePlacementReport.Report(ForgeEngine.Plugin.Runtime?.WorldEpoch ?? 0); }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, ZonePlacementReport.LogCode, "Zone placement report refused.",
                RuntimeLogValue.Of("reason", error.GetType().Name));
        }
        try { EnemyGroupPlacementReport.Report(ForgeEngine.Plugin.Runtime?.WorldEpoch ?? 0); }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, EnemyGroupPlacementReport.LogCode, "Enemy group placement report refused.",
                RuntimeLogValue.Of("reason", error.GetType().Name));
        }
    }

    /// <summary>Ordinary fixed spawns are placed here, where every reader shares the completed world.</summary>
    private static void LevelGenerationFinished()
    {
        // Generation callbacks can read the table before native doors and terminals are populated.
        // FactoryDone closes that generation phase; all readers now share the completed world.
        ZoneIndex.Reset();
        if (MapHost.Session is { } session)
        {
            session.Build(session.Placements, placements => placements.Place(), "fixed placement");
            // After the placements, so a key a row moved is not reported at the position it left.
            session.Build(session.Placements, _ => KeyPlacementAudit.Run(session.WorldEpoch), "key placement audit");
        }
    }
}
