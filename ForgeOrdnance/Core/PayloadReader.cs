using System;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnance;

/// <summary>A payload's JSON object read through ForgeCore's <see cref="StrictJson"/> (a bad value throws a
/// <see cref="StrictJsonException"/> naming the field), plus the readings only call-ins have.</summary>
internal static class PayloadReader
{
    private static readonly string[] TypeField = { "Type" };

    /// <summary>Refuses a member that is not <c>Type</c> or in <paramref name="known"/>.</summary>
    internal static void OnlyKnown(JsonElement payload, params string[] known) => StrictJson.OnlyKnown(payload, "", known, TypeField);

    internal static double Number(JsonElement payload, string field, double fallback, double min, double max)
        => StrictJson.Number(payload, "", field, fallback, min, max);

    internal static bool Bool(JsonElement payload, string field, bool fallback) => StrictJson.Bool(payload, "", field, fallback);

    /// <summary>A DataBlock reference: a persistentID (number) or a block name (string). Exactly one of the two forms.</summary>
    internal static (uint? Id, string? Name) Reference(JsonElement payload, string field) => StrictJson.Reference(payload, "", field);

    /// <summary>Checks an item data-block ID before it enters the game's 16-bit replicated prefab field.</summary>
    internal static uint ReplicatedPrefabId(uint id, string field)
    {
        if (id <= ushort.MaxValue) return id;
        throw new PayloadParameterException(field, "data block " + id + " cannot be spawned because the game's replicated prefab ID is limited to " + ushort.MaxValue + ".");
    }

    /// <summary>An optional turret <c>Look</c>: a <c>Pack:item</c> id that <paramref name="isRegistered"/> knows (a package registers its
    /// looks with <c>OrdnanceTurrets.RegisterLook</c>), or empty when the field is left out (the game's own look stays).</summary>
    internal static string Look(JsonElement payload, Func<string, bool> isRegistered)
    {
        if (!payload.TryGetProperty("Look", out var value)) return "";
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!TurretLookBook.IsLookId(id)) throw new PayloadParameterException("Look", "must be a turret look written Pack:item.");
        if (!isRegistered(id!)) throw new PayloadParameterException("Look", "'" + id + "' is not a registered turret look (its package is not installed, or registered it under another name).");
        return id!;
    }
}