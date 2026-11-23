namespace ForgeEngine.Framework;

/// <summary>
/// The closed device-category table of `forge.action.map.device_disable`: the electronic devices one pulse can blank,
/// each named for the thing the player sees go dark. Every member has a native off and on entry on build 20403457
/// (evidence/device-switch.json); a category the game gives no such pair was left out rather than approximated.
/// The order is the table's own and is the order a request that names no category walks.
/// </summary>
internal static class DeviceCategories
{
    /// <summary>The zone's own light objects (`LG_Light.SetEnabled`). Written by ForgeMap.</summary>
    public const string EnvironmentLight = "environment_light";
    /// <summary>The local player's flashlight (`PlayerSync.WantsToSetFlashlightEnabled`). Written by ForgePlayer.</summary>
    public const string Flashlight = "flashlight";
    /// <summary>The local player's status, inventory, compass and objective readouts. Written by ForgePlayer.</summary>
    public const string PlayerHud = "player_hud";
    /// <summary>The local player's map page (`CM_PageMap.SetMapVisualsIsActive`). Written by ForgePlayer.</summary>
    public const string Map = "map";
    /// <summary>The local player's bio tracker (`EnemyScanner`). Written by ForgeWeapon.</summary>
    public const string Biotracker = "biotracker";

    public static readonly string[] All = { EnvironmentLight, Flashlight, PlayerHud, Map, Biotracker };

    /// <summary>A target this writer does not address at all: a zone handed to a player device, a player handed to
    /// the zone lights. It is not a failure — the request picked several categories and this one simply has no
    /// device on that target.</summary>
    public const string TargetKindCode = "device-target-kind";
}

/// <summary>One category's native half on this machine: whether a target's device of this category is drawn here,
/// and the immediate off and on writes. It holds no timer and no hold table; the Player domain's device switchboard owns
/// both, so every category expires, refreshes and restores by the same rule.</summary>
internal interface IDeviceWriter
{
    string Category { get; }
    /// <summary>Null when this machine draws the target's device of this category; otherwise the refusal code:
    /// <see cref="DeviceCategories.TargetKindCode"/>; <see cref="ResultCode.DevicePlayerRemote"/> for a player whose
    /// devices another machine draws (the presentation tier runs the row on every addressed machine and each writes
    /// only the player it draws); or <see cref="ResultCode.DeviceUnavailable"/> when the native object that carries
    /// the category is not there on this machine right now.</summary>
    string? Refusal(EntityReference target);
    /// <summary>Turns the device off, remembering whatever the matching <see cref="Restore"/> puts back. Throws when
    /// the native write fails.</summary>
    void Disable(EntityReference target);
    /// <summary>Puts back what the matching <see cref="Disable"/> took away.</summary>
    void Restore(EntityReference target);
}

/// <summary>What one hold request changed.</summary>
internal enum DeviceHoldChange
{
    /// <summary>The device was on and is now held off.</summary>
    Disabled,
    /// <summary>The device was already held off; only its expiry moved. No second write, no second restore.</summary>
    Refreshed,
    /// <summary>The hold ended and the device was put back.</summary>
    Restored,
    /// <summary>A restore for a device no hold covers: nothing was written.</summary>
    NotHeld
}
