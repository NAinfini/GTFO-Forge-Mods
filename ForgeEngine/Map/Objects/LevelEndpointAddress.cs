using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Stable level-object addresses: fixed heavy repellers keep their placement token;
/// win-condition items use their native owning zone and endpoint kind.</summary>
internal static class LevelEndpointAddress
{
    public const string FogRepeller = "fog_repeller";
    public const string Extraction = "extraction";
    public const string Elevator = "elevator";
    public const string Exit = "exit";

    public static MapObjectReference? Parse(string? text)
    {
        if (text == null) return null;
        var parts = text.Split('/');
        if (parts.Length != 5 || !Coordinate(parts[1]) || !Coordinate(parts[2]) || !Coordinate(parts[3])
            || parts[2] is not ("0" or "1" or "2")) return null;
        bool key = parts[0] == FogRepeller ? FixedItemIdentity.Token("item:" + parts[4]) != null
            : parts[0] == Extraction && parts[4] is Elevator or Exit;
        return key ? new MapObjectReference(parts[0], parts[1], parts[2], parts[3], parts[4]) : null;
    }

    public static string? RepellerOfItem(string itemKey)
        => itemKey.StartsWith(FixedItemIdentity.Category + "/", StringComparison.Ordinal)
            && Parse(FogRepeller + itemKey[FixedItemIdentity.Category.Length..]) is { } address
            ? address.ToString() : null;

    public static EntityReference Reference(MapObjectReference address, long world)
        => new(EntityKinds.LevelObject + ":" + address, world, 1);

    public static string? Key(EntityReference reference, string category)
    {
        return EntityKinds.Is(reference, EntityKinds.LevelObject)
            && Parse(reference.Id[(EntityKinds.LevelObject.Length + 1)..]) is { } address && address.Category == category
            ? address.ToString() : null;
    }

    private static bool Coordinate(string value)
        => value.Length > 0 && (value.Length == 1 || value[0] != '0')
            && int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int number) && number >= 0;
}
