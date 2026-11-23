using System.Text.Json;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The read-only environment row `v-env` (`forge.query.environment.state`). It answers the facts the checklist names
/// for one zone and nothing else — the fog block the zone's dimension currently shows and its live density height,
/// whether the zone's lights are on, and how many native light objects the zone holds — and reads them from the
/// game's own state, which is the state the game replicates to every client.
///
/// The row is a `query` capability, so it runs on the host through the kernel's budgeted evaluation path: it writes
/// nothing, publishes no fact and produces no command. Its read is declared on the capability (`reads: ["world"]`)
/// rather than hidden in the handler, which is what lets a plan's authority be judged from the graph before the
/// step ever runs.
/// </summary>
internal static class EnvironmentQuery
{
    internal const string UnavailableCode = "environment-unavailable";
    /// <summary>A `zone` input that is not a `gtfo.zone` address, or an address whose three coordinates are
    /// outside the enums the game indexes them with. It is refused rather than narrowed to a nearby zone.</summary>
    internal const string ZoneInputCode = "environment-zone-input";
    /// <summary>A zone reference of another world. The reference names a place of a level that is gone, so the read
    /// is refused instead of answered from the level standing now.</summary>
    internal const string StaleZoneCode = "environment-zone-stale";
    /// <summary>The zone address is well formed but this level has no zone at it. An empty answer here would read
    /// exactly like a zone whose lights are off and which holds none.</summary>
    internal const string ZoneMissingCode = "environment-zone-missing";
    /// <summary>The dimension reported a fog id this install has no `FogSettingsDataBlock` for, so the row's
    /// `height` port has nothing to read. The whole read is refused rather than answered with the other two ports
    /// and a height of zero: zero is a real density height, and an author cannot tell it from "not read".</summary>
    internal const string FogBlockCode = "environment-fog-block-missing";
    internal const string FogStateCode = "environment-fog-state-unavailable";

    /// <summary>Runs as the `query` step's evaluator. The zone is the row's wired input, addressed by the level's
    /// own `gtfo.zone` reference — the address the `zone` resource kind and the `a-lights` recipient already speak —
    /// and resolved through the level's own zone list, so a place this level does not have is refused instead of
    /// answered as an empty, dark one. The fog answers are the zone's dimension's; the light count is the length of
    /// `LG_Zone.m_lightsInZone`, the one place the level records how many lights a zone has.</summary>
    internal static void Evaluate(EvaluationContext context, EvaluationOutputs outputs)
    {
        var reference = Input(context, "zone");
        var coordinates = EnvironmentZone.Parse(reference)
            ?? throw new RuntimeContractException(ZoneInputCode, "The input is not a zone address: " + reference.Id);
        if (reference.WorldEpoch != context.Query.WorldEpoch)
            throw new RuntimeContractException(StaleZoneCode, "The zone belongs to another world: " + reference.Id);
        var manager = EnvironmentStateManager.Current
            ?? throw new RuntimeContractException(UnavailableCode, "No environment state manager is loaded.");
        var zone = EnvironmentZoneTable.At(coordinates)
            ?? throw new RuntimeContractException(ZoneMissingCode, "This level has no zone at " + reference.Id);
        var (fog, height) = DimensionFog(manager, coordinates.DimensionIndex);
        bool light = EnvironmentStateManager.GetLightMode(coordinates.Global);
        outputs.Integer(0, fog);
        outputs.Boolean(1, light);
        outputs.Number(2, height);
        outputs.Integer(3, zone.m_lightsInZone?.Count ?? 0);
    }

    /// <summary>The `forge.query.environment.fog_at` evaluator: below the dimension's live fog height and outside every
    /// infection shield sphere. A fog repeller keeps its sphere in `EffectVolumeManager.volumes` whichever way it got
    /// there (placed, thrown or carried), and each machine simulates it, so the host's read covers all of them.</summary>
    internal static void EvaluateFogAt(EvaluationContext context, EvaluationOutputs outputs)
    {
        if (!context.Inputs.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 3)
            throw new RuntimeContractException("missing-field", "position");
        if (!context.Inputs.TryGetProperty("dimension", out var dimension) || !dimension.TryGetInt32(out var index))
            throw new RuntimeContractException("missing-field", "dimension");
        var manager = EnvironmentStateManager.Current
            ?? throw new RuntimeContractException(UnavailableCode, "No environment state manager is loaded.");
        var (fog, height) = DimensionFog(manager, (eDimensionIndex)index);
        var at = new UnityEngine.Vector3((float)position[0].GetDouble(), (float)position[1].GetDouble(), (float)position[2].GetDouble());
        outputs.Boolean(0, at.y < height && !Shielded(at));
        outputs.Integer(1, fog);
    }

    private static bool Shielded(UnityEngine.Vector3 at)
    {
        var volumes = EffectVolumeManager.volumes;
        if (volumes == null) return false;
        for (var i = 0; i < volumes.Count; i++)
        {
            var volume = volumes[i];
            if (volume == null || volume.modification != eEffectVolumeModification.Shield
                || volume.contents is not (eEffectVolumeContents.Infection or eEffectVolumeContents.All)) continue;
            var sphere = volume.TryCast<EV_Sphere>();
            if (sphere != null && (at - sphere.position).sqrMagnitude < sphere.maxRadius * sphere.maxRadius) return true;
        }
        return false;
    }

    /// <summary>The fog block a dimension currently shows and its live world-space density height. Matches
    /// LocalPlayerAgentSettings.UpdateBlendTowardsTargetFogSetting: the captured start is already world-space; only the
    /// target preset receives GroundY, on the game's own clock and blend.</summary>
    private static (uint Fog, double Height) DimensionFog(EnvironmentStateManager manager, eDimensionIndex dimension)
    {
        var replicator = manager.m_stateReplicator;
        if (replicator == null || replicator.WasCollected)
            throw new RuntimeContractException(FogStateCode, "No replicated environment state is available.");
        var snapshot = replicator.State;
        if (snapshot == null || snapshot.WasCollected || snapshot.FogStates == null)
            throw new RuntimeContractException(FogStateCode, "No replicated fog states are available.");
        if (!Dimension.GetDimension(dimension, out var resolved) || resolved == null)
            throw new RuntimeContractException(FogStateCode, "The zone's dimension is not loaded.");
        var states = snapshot.FogStates;
        int index = resolved.CreationOrderIndex;
        if (index < 0 || index >= states.Length)
            throw new RuntimeContractException(FogStateCode, "The zone's dimension has no replicated fog state.");
        // Vanilla UpdateFog indexes by CreationOrderIndex, not the author's logical dimension enum.
        var state = states[index];
        uint fog = state.FogDataID;
        var settings = GameDataBlockBase<FogSettingsDataBlock>.GetBlock(fog);
        if (settings == null || !float.IsFinite(settings.DensityHeightAltitude))
            throw new RuntimeContractException(FogBlockCode, "Fog block " + fog + " has no finite density height.");
        float startHeight = state.StartFogSettings.DensityHeightAltitude;
        float ground = resolved.GroundY;
        float targetHeight = settings.DensityHeightAltitude + ground;
        float blend = state.GetFogBlend();
        if (!float.IsFinite(startHeight) || !float.IsFinite(ground) || !float.IsFinite(targetHeight) || !float.IsFinite(blend))
            throw new RuntimeContractException(FogStateCode, "The native fog transition has nonfinite state.");
        double height = UnityEngine.Mathf.Lerp(startHeight, targetHeight, blend);
        if (!double.IsFinite(height))
            throw new RuntimeContractException(FogStateCode, "The native fog transition has no finite height.");
        return (fog, height);
    }

    /// <summary>The row's one required input, refused by name when the plan left it out: an absent zone answered as
    /// a dark zone would read exactly like a zone that really is dark.</summary>
    private static EntityReference Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? RuntimeJson.Entity(value)
            : throw new RuntimeContractException("missing-field", port);
}
