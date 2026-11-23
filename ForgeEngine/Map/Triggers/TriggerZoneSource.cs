using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The trigger-zone reader: the one `gtfo.map_object` category whose objects are authored rather than generated.
/// A door and a terminal are addressed from the level the game built and re-read from a native instance; a zone is
/// the package document's own record, so this source addresses the record and answers every read from the table it
/// holds. The table is this install's package data and never changes under a live world — a zone added mid-level
/// would make two machines disagree about the volume a player stands in — which is also why there is no reload.
///
/// The address key is the zone's authored id, so a plan's `map-object` mount names the zone the author placed and
/// nothing else: two zones of one document are two addresses, and a zone of another level is still an address this
/// source knows, because the level is what the judging tick filters on rather than what the identity carries.
/// </summary>
internal sealed class TriggerZoneSource : IMapObjectSource
{
    /// <summary>The category every trigger-zone address carries.</summary>
    public static readonly string ZoneCategory = TriggerZoneAddress.Category;

    private readonly LevelIdIndex<TriggerZone> _byId = new(zone => zone.Id, zone => zone.Level);
    private IReadOnlyList<TriggerZone> _zones = Array.Empty<TriggerZone>();

    public TriggerZoneSource() : this(Array.Empty<TriggerZone>()) { }

    /// <summary>The level the running world is in: an id authored in two levels names the zone of this one.</summary>
    internal Func<MapLevelReference?>? CurrentLevel { set => _byId.CurrentLevel = value; }

    /// <summary>A source over one install's zones: the document's order is kept for the readers that report it,
    /// and the id table is built once, because two zones of one level may not share an id.</summary>
    public TriggerZoneSource(IReadOnlyList<TriggerZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        Load(zones);
    }

    /// <summary>The zones this install declared, across every level, in the document's own order.</summary>
    public IReadOnlyList<TriggerZone> Zones => _zones;

    /// <summary>Replaces the table with one install's zones. Reading the document is the native half's job; the
    /// loaded set is handed over once, before the world the source answers for begins.</summary>
    public void Load(IReadOnlyList<TriggerZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _byId.Clear();
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index]; zone.RuntimeIndex = index;
            if (TriggerZoneAddress.Of(zone.Id) is null)
                throw new RuntimeContractException("trigger-zone-address",
                    "Zone id `" + zone.Id + "` is not one the map-object address can carry.");
            if (!_byId.Add(zone))
                throw new RuntimeContractException("trigger-zone-duplicate-id", "Zone id `" + zone.Id + "` is authored twice in level " + zone.Level + ".");
        }
        _zones = Array.AsReadOnly(new List<TriggerZone>(zones).ToArray());
    }

    /// <summary>The zone one authored id names, or null when this install has no such zone.</summary>
    public TriggerZone? ById(string? zoneId)
        => _byId.Find(zoneId);

    public string Category => ZoneCategory;

    /// <summary>Whether an instance is one of the zones this source addresses. The instance is the table entry
    /// itself, so an object that is not a zone of this install is not answered for at all.</summary>
    private bool Mine(object instance) => instance is TriggerZone zone && _byId.Holds(zone);

    public MapObjectReference? TryAddress(object instance) => Mine(instance) ? TriggerZoneAddress.Of(((TriggerZone)instance).Id) : null;

    public MapObjectReference? Parse(string text) => TriggerZoneAddress.TryParse(text);

    public MapObjectObservation? Read(object instance)
        => Mine(instance) ? new MapObjectObservation(true, null, null, (TriggerZone)instance) : null;

    public double[]? Position(object instance)
    {
        if (!Mine(instance)) return null;
        var position = ((TriggerZone)instance).Position;
        return new[] { position[0], position[1], position[2] };
    }

    /// <summary>Whether the table still holds the same zone under the address. A zone the document did not declare
    /// is not this world's object, and a table that no longer holds the entry no longer answers for it.</summary>
    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => Mine(instance) && TriggerZoneAddress.Of(((TriggerZone)instance).Id) == address;

    /// <summary>Nothing here reports: the document's own refusals are the native reader's diagnostics, and a zone
    /// table that does not read names no zone at all rather than a zone that failed later.</summary>
    public void Report(string message) { }
}
