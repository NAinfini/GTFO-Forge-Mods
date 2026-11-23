using System;
using System.Collections.Generic;

namespace ForgeEngine.Framework;

/// <summary>
/// The entity kinds a graph port, a snapshot and an instance resolver speak. Every package names a kind through
/// these constants rather than spelling the string, so a kind a port promises and the kind a provider resolves
/// cannot drift. Object subkinds are port types only: the snapshot and the instance table publish their parent kind.
/// </summary>
public static class EntityKinds
{
    public const string Player = "gtfo.player";
    public const string Enemy = "gtfo.enemy";
    public const string MapObject = "gtfo.map_object";
    public const string LevelObject = "gtfo.level_object";
    public const string Level = "gtfo.level";
    public const string Zone = "gtfo.zone";
    public const string Equipment = "gtfo.equipment";
    public const string Door = "gtfo.map_object.door";
    public const string Terminal = "gtfo.map_object.terminal";
    public const string Generator = "gtfo.map_object.generator";
    public const string TriggerZone = "gtfo.map_object.trigger_zone";
    public const string Container = "gtfo.level_object.container";
    public const string Portal = "gtfo.map_object.portal";
    public const string ItemSlot = "gtfo.map_object.item_slot";
    public const string Shuttlebox = "gtfo.map_object.shuttlebox";

    /// <summary>Every kind, in the wire vocabulary's order.</summary>
    public static IReadOnlyList<string> All { get; } = Wire.EntityKinds;

    /// <summary>Whether one entity id is routed under <paramref name="kind"/>: the id is the kind followed by its
    /// first colon (<c>RuntimeJson.KindOf(id) == kind</c>), read in place, so a per-recipient test cuts nothing out
    /// of the id. A null or malformed id is of no kind.</summary>
    public static bool Is(string? entityId, string kind)
        => entityId != null && entityId.Length > kind.Length && entityId[kind.Length] == ':'
            && entityId.AsSpan(0, kind.Length).SequenceEqual(kind);

    /// <summary>Whether one reference is of <paramref name="kind"/>; a null reference is of no kind.</summary>
    public static bool Is(EntityReference? reference, string kind) => Is(reference?.Id, kind);
}

/// <summary>The scope id a fact of a whole world carries, <c>gtfo.world:&lt;epoch&gt;</c>, spelled once per world instead of once
/// per fact.</summary>
internal static class WorldScope
{
    private sealed record Spelled(long Epoch, string Scope);
    private static Spelled _last = new(long.MinValue, "");

    internal static string Of(long epoch)
    {
        var last = _last;
        if (last.Epoch == epoch) return last.Scope;
        _last = last = new Spelled(epoch, "gtfo.world:" + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return last.Scope;
    }
}
