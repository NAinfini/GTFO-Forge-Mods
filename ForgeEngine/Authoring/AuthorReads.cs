using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;

namespace ForgeEngine.Authoring;

/// <summary>
/// Author diagnostics of Forge maps, read by name from the developer console (ForgeDevelopment reaches this type at run time,
/// never through a compile-time reference; <c>call static:ForgeEngine.Authoring.AuthorReads &lt;method&gt;</c> works the same).
/// Every read answers only in Authoring mode on a running kernel; anything else is the explicit refusal
/// <see cref="NotAuthoring"/>. Arguments and results are plain text and numbers so a caller needs none of this assembly's types.
/// </summary>
public static partial class AuthorReads
{
    public const string NotAuthoring = "forge-engine-not-authoring";

    private static IForgeEngine Kernel()
        => Plugin.ConfiguredMode == RuntimeMode.Authoring && !Plugin.IsSuspended && Plugin.Runtime is { } kernel ? kernel
            : throw new InvalidOperationException(NotAuthoring);

    /// <summary>The configured runtime mode (Off, Play or Authoring).</summary>
    public static string Mode() => Plugin.ConfiguredMode.ToString();

    /// <summary>The kernel's world epoch, -1 when no kernel runs.</summary>
    public static long WorldEpoch() => Plugin.Runtime?.WorldEpoch ?? -1;

    /// <summary>How many authoring plans the kernel holds loaded.</summary>
    public static int LoadedPlans() => Kernel().LoadedPlans;

    /// <summary>Reloads the authoring plans from disk; the answer is the confirmed command result as JSON.</summary>
    public static string ReloadPlans()
    {
        Kernel();
        var result = Plugin.ReloadAuthoringPlans();
        var text = JsonSerializer.Serialize(new { result.Status, result.CommitState, result.Code, result.Detail, result.Outputs });
        if (result.Status != CommandStatuses.Succeeded || result.CommitState != CommitStates.Confirmed)
            throw new InvalidOperationException("drive.reload-not-confirmed:" + text);
        return text;
    }

    /// <summary>The author's 「重载内容」: reads the installed content again and commits it as the next content generation (in a clean
    /// lobby only; it enters no level). The answer is the
    /// hand-off's result document (contract I-RELOAD) as JSON; a refusal is in its status and code, not thrown.</summary>
    public static string ReloadContent()
    {
        Kernel();
        return System.Text.Encoding.UTF8.GetString(ContentReloadHandoff.Json(ContentReloadHandoff.Reload()));
    }

    /// <summary>Map generation's own progress line for the current world, or empty when Map published none.</summary>
    public static string GenerationProgress()
    {
        var kernel = Kernel();
        return kernel.TryService<MapGenerationReadback>(out var read) && read != null ? read(kernel.WorldEpoch) ?? "" : "";
    }

    /// <summary>The authored room instance id Map wrote for a generated room, or null when Map admitted none.</summary>
    public static string? RoomInstanceId(LG_Geomorph room) => MapCaptureService.Require(Kernel()).RoomInstanceId(room);

    /// <summary>Every generated room of the current world for one authored room reference inside one zone. Each hit is
    /// <c>geomorphInstanceId|zoneInstanceId</c>; a refusal is the single entry <c>!&lt;refusal&gt;</c>.</summary>
    public static string[] ResolveRoom(string sourcePrefab, int dimension, int layer, int localZone,
        string zoneAuthorId, string roomId, string roomRevision, string instanceId)
    {
        var kernel = Kernel();
        var answer = kernel.ResolveAuthoringRoom(kernel.WorldEpoch, sourcePrefab, new AuthoringRoomScope(dimension, layer, localZone,
            instanceId.Length == 0 ? null : new AuthoringRoomIdentity(zoneAuthorId, roomId, roomRevision, instanceId)));
        if (answer.Rooms.Count == 0) return new[] { "!" + (answer.Refusal ?? AuthoringRoomResolution.NoRoom) };
        return answer.Rooms.Select(room => room.GeomorphInstanceId.ToString(CultureInfo.InvariantCulture) + "|"
            + room.ZoneInstanceId.ToString(CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>The runtime's variable store rows whose name starts with <paramref name="prefix"/>, as a JSON array of
    /// <c>{name, scope, subject, type, value}</c>, so a behavior's final variable value is observed and not inferred.</summary>
    public static string Variables(string prefix)
    {
        if (Kernel() is not RuntimeKernel kernel) throw new InvalidOperationException("drive.variables-store-unavailable");
        var rows = kernel.kernelVariables.variables.Snapshot();
        using var buffer = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("entries", rows.Count);
            json.WriteStartArray("rows");
            foreach (var row in rows)
            {
                if (!row.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                json.WriteStartObject();
                json.WriteString("name", row.Name);
                json.WriteString("scope", row.ScopeKind);
                json.WriteString("subject", row.Subject);
                json.WriteString("type", row.Type);
                json.WritePropertyName("value");
                row.Value.WriteTo(json);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The placed model's one registered shooting collider in the current world, for the console's model shot.</summary>
    public static UnityEngine.Collider ModelShotCollider(string modelId)
    {
        var kernel = Kernel();
        var found = MapInspectionService.Require(kernel).ModelColliders(kernel.WorldEpoch).Where(value => value.ModelId == modelId).ToArray();
        if (found.Length != 1) throw new InvalidOperationException("drive.model-shot-target-count:" + found.Length.ToString(CultureInfo.InvariantCulture));
        return (UnityEngine.Collider)found[0].Collider;
    }

    /// <summary>Starts a bot-slot request on Player's production slot adapter (author levels only): <paramref name="json"/> is
    /// <c>{slot, bot, character?, loadout?}</c> with loadout keys GearStandard / GearSpecial / GearClass / GearMelee naming
    /// GearManager keys. Poll <see cref="BotSlotRequest.Ready"/>, call <see cref="BotSlotRequest.Apply"/>, then poll
    /// <see cref="BotSlotRequest.Applied"/>.</summary>
    public static BotSlotRequest BotSlot(string json) => BotSlotRequest.Begin(Kernel(), json);
}
