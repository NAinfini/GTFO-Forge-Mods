using ForgeEngine.Framework;
using System.Text.Json;

namespace ForgeMap;

internal static partial class FixedPlacementManifest
{
    private static bool TryTaskObject(JsonElement row, RoomLocator locator, out FixedTaskObjectRole? role,
        out string? code, out string? reason)
    {
        role = null;
        code = "fixed-object-role";
        reason = "A task object needs an explicit native role and zero amount.";
        if (!DocumentReading.Text(row, "kind", out var kindText)) return false;
        var kind = kindText switch
        {
            "generator" => FixedTaskObjectKind.Generator,
            "hsu" => FixedTaskObjectKind.Hsu,
            "uplink-terminal" => FixedTaskObjectKind.UplinkTerminal,
            "terminal" => FixedTaskObjectKind.Terminal,
            "download-terminal" => FixedTaskObjectKind.DownloadTerminal,
            "generator-cluster" => FixedTaskObjectKind.GeneratorCluster,
            _ => (FixedTaskObjectKind?)null
        };
        if (kind == null || !row.TryGetProperty("role", out var target) || target.ValueKind != JsonValueKind.Object
            || row.TryGetProperty("itemId", out _) || row.TryGetProperty("targetDoor", out _)
            || !DocumentReading.Number(row, "amount", 0, 0, out _)) return false;
        if (!DocumentReading.Text(target, "scope", out var scope)) return false;
        if (scope == "door")
        {
            if (kind != FixedTaskObjectKind.Generator || !DocumentReading.Known(target, out _, "scope", "targetDoor")
                || !DocumentReading.Text(target, "targetDoor", MaximumDoorAddressChars, out var text)
                || MapObjectDoorAddress.TryParse(text) is not { } door || door.Key != MapObjectDoorAddress.Security) return false;
            role = new(kind.Value, FixedTaskObjectScope.Door, 0, 0, 0, 1, false, door);
        }
        else if (scope == "zone")
        {
            if (kind is not (FixedTaskObjectKind.Generator or FixedTaskObjectKind.Terminal) || !DocumentReading.Known(target, out _, "scope", "placementId", "index", "count")
                || !DocumentReading.Text(target, "placementId", kind == FixedTaskObjectKind.Terminal ? 512 : 71, out var placementId)
                || kind == FixedTaskObjectKind.Terminal && !placementId!.StartsWith("author:", System.StringComparison.Ordinal)
                || kind == FixedTaskObjectKind.Generator && (MapObjectGeneratorAddress.AuthorKey(placementId) is not { } key || !key.StartsWith("author-", System.StringComparison.Ordinal))
                || !RuntimeJson.TryInteger(target, "count", 1, int.MaxValue, out var count)
                || !RuntimeJson.TryInteger(target, "index", 0, count - 1, out var index)) return false;
            role = new(kind.Value, FixedTaskObjectScope.Zone, 0, 0, (int)index, (int)count, false, null) { PlacementId = placementId };
        }
        else if (scope == "objective")
        {
            if (kind == FixedTaskObjectKind.Terminal) return false;
            if (!DocumentReading.Known(target, out _, "scope", "layer", "chainIndex", "index", "count", "endpoint")
                || !RuntimeJson.TryInteger(target, "layer", 0, 2, out var layer)
                || !RuntimeJson.TryInteger(target, "chainIndex", 0, int.MaxValue, out var chain)
                || !RuntimeJson.TryInteger(target, "count", 1, int.MaxValue, out var count)
                || !RuntimeJson.TryInteger(target, "index", 0, count - 1, out var index)
                || locator.Layer != layer) return false;
            var receiver = false;
            if (kind == FixedTaskObjectKind.UplinkTerminal)
            {
                if (!DocumentReading.Text(target, "endpoint", out var endpoint) || endpoint is not ("source" or "receiver")) return false;
                receiver = endpoint == "receiver";
            }
            else if (target.TryGetProperty("endpoint", out _) || kind is FixedTaskObjectKind.Hsu or FixedTaskObjectKind.GeneratorCluster && (index != 0 || count != 1)
                || kind == FixedTaskObjectKind.GeneratorCluster && chain != 0) return false;
            role = new(kind.Value, FixedTaskObjectScope.Objective, (int)layer, (int)chain, (int)index, (int)count, receiver, null);
        }
        else return false;
        code = null;
        reason = null;
        return true;
    }
}
