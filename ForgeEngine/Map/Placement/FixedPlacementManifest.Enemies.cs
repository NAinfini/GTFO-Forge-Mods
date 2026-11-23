using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for Enemies. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string EnemyFileName = "fixed-enemies.forge";
    /// <summary>The enemy document's one file name.</summary>
    public static string EnemyFileNameOnly => EnemyFileName;

    /// <summary>Reads one enemy document, with the same row-level contract as an item document.</summary>
    public static bool TryParseEnemies(string text, out FixedDocument<FixedEnemy>? document, out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, EnemyRootCodes, "enemies", out code, out reason);
        if (parsed == null) return false;
        var enemies = new List<FixedEnemy>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("enemies").EnumerateArray())
        {
            index++;
            if (!TryEnemy(row, out var enemy, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(enemy!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-enemy-duplicate",
                    Row(index, "Enemy id `" + enemy.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            enemies.Add(enemy);
        }
        document = new FixedDocument<FixedEnemy>(Array.AsReadOnly(enemies.ToArray()), Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    private static bool TryEnemy(JsonElement row, out FixedEnemy? enemy, out string? code, out string? reason)
    {
        enemy = null;
        code = "fixed-enemy-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "An enemy row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "locator", "p_u", "yaw_u", "typeId", "intent"))
        { code = "fixed-enemy-field"; reason = "Unknown enemy field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "enemy:"))
        { code = "fixed-enemy-id"; reason = "An enemy needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `enemy:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "fixed-enemy-level"; reason = "An enemy needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "fixed-enemy-room"; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "fixed-enemy-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "fixed-enemy-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        if (!RuntimeJson.TryInteger(row, "typeId", 1, uint.MaxValue, out var typeValue))
        { code = "fixed-enemy-type"; reason = "A `typeId` is a native enemy block id: a whole number in [1, " + uint.MaxValue + "]."; return false; }
        if (!DocumentReading.Text(row, "intent", 32, out var intentText) || !TryIntent(intentText!, out var intent))
        { code = "fixed-enemy-intent"; reason = "An intent is `hibernate`, `aggressive`, `patrolling` or `scout`."; return false; }
        enemy = new FixedEnemy(id!, level, locator!, position!, yaw, (uint)typeValue, intent);
        return true;
    }

    private static bool TryIntent(string text, out FixedEnemyIntent intent)
    {
        switch (text)
        {
            case "hibernate": intent = FixedEnemyIntent.Hibernate; return true;
            case "aggressive": intent = FixedEnemyIntent.Aggressive; return true;
            case "patrolling": intent = FixedEnemyIntent.Patrolling; return true;
            case "scout": intent = FixedEnemyIntent.Scout; return true;
            default: intent = FixedEnemyIntent.Hibernate; return false;
        }
    }
}
