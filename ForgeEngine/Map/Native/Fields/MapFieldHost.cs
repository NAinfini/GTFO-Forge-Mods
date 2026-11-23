using System;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The entry of the map block fields, called once from `Plugin.Load` while the one Map registration's window is
/// still open: it declares <see cref="ModuleDefinition.DefinitionFields"/> on that registration, reads
/// `plugins/&lt;package&gt;/forge/map-fields/*.forge` once, and hands each group's plan to that group's applier.
///
/// Cargo, item-spawn and landing hooks are declared with the package's native hooks (`MapNativeHooks`), and a group
/// whose hook Runtime refused is not activated here: <see cref="Hooks"/> names the withdrawn features. Door-build
/// and zone-room rows require their complete declared hook group before native Rundown publication.
///
/// Every field refusal is an error record under its own `map.field-*` code in the Map provider's log, so
/// <see cref="MapLog"/> is attached before this runs. Room-build documents are admitted
/// later by the session's one post-content startup callback.
///
/// A later group adds `Fields/MapFields.&lt;group&gt;.cs`, `Level/&lt;Group&gt;Applier.cs` and one line below.
/// </summary>
internal static class MapFieldHost
{
    private static readonly MapPreparedContent None = new(Array.Empty<MapPackageStage>());

    /// <summary>The declaration pass of the session this host started with.</summary>
    internal static MapHookAvailability Hooks { get; private set; } = MapHookAvailability.Complete;

    /// <summary>Declares fields during registration. Package preparation and activation happen in content startup.</summary>
    internal static void Start(RuntimeModuleHandle registration, MapHookAvailability? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Hooks = hooks ?? MapHookAvailability.Complete;
        ModuleDefinition.DeclareDefinitionFields(registration);
    }

    /// <summary>Require declared infrastructure before any native Rundown commit.</summary>
    internal static void Prepare(MapPreparedContent content, Action<string> report)
    {
        Activate(None, report);
        if (!content.DoorFields.IsEmpty && !Hooks.Offers(MapHookFeatures.DoorBuildFieldsFeature))
            throw new InvalidOperationException("door-build-hooks-unavailable");
    }

    /// <summary>Assign previously validated rows only. No filesystem access or hook installation follows commit.</summary>
    internal static void Activate(MapPreparedContent content, Action<string> report)
    {
        DoorBuildApplier.Activate(content.DoorFields);
        GlobalWaveApplier.Install(content.GlobalFields);
        ItemSpawnApplier.Activate(Offered(MapHookFeatures.ItemSpawnFieldFeature, content.SystemFields.AnyItemSpawns, "item-spawn")
            ? content.SystemFields : None.SystemFields);
        ElevatorCargoApplier.Activate(Offered(MapHookFeatures.ElevatorCargoFieldsFeature, content.SystemFields.AnyCargo, "elevator-cargo")
            ? content.SystemFields : None.SystemFields);
        LevelSentrySpawner.Install(content.SystemFields);
    }

    /// <summary>Whether a group with authored rows may be written: a withdrawn feature refuses its rows by name,
    /// once per activation, instead of leaving them silently unapplied.</summary>
    private static bool Offered(string feature, bool hasRows, string group)
    {
        if (Hooks.Offers(feature)) return true;
        if (hasRows)
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused,
                "group=" + group + " code=native-hook-unavailable feature=" + feature,
                RuntimeLogValue.Of("group", group), RuntimeLogValue.Of("code", "native-hook-unavailable"));
        return false;
    }
}
