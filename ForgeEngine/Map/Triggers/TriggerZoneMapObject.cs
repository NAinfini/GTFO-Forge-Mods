using System;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The trigger-zone half of the map-object provider. A trigger zone is one more category of `gtfo.map_object`: it
/// has an address, an identity in the one namespace, an observation and a position, and the two facts it publishes
/// are map-object facts — the event's subject is the zone, so a plan is hung on the zone itself and the existing
/// `map-object` attachment matcher decides which zone a behaviour belongs to. No second mount kind, no second
/// identity namespace, and no resource kind for a volume nobody resolves through a resource port.
///
/// This half is a partial declaration rather than a second module because there is exactly one map-object provider
/// and one publication path for it. The declared half composes the definition; this half adds the zone category to
/// it, owns the zone table's reads, and publishes the two edges the judging tick produces through the same
/// registration and under the same guards the door and terminal facts are published under.
/// </summary>
internal sealed partial class MapObjectModule
{
    /// <summary>The zones this module addresses. A module that was composed without zones answers for none of
    /// them rather than refusing to exist: the zone category is optional to this provider exactly as the packages
    /// that declare zones are optional to the install.</summary>
    private TriggerZoneSource _zoneSource = new();

    /// <summary>The same registration, composed with the zones' own reader and category. The zone source is handed
    /// over before <see cref="Register"/> because every category is declared on the one definition at registration
    /// time; a module that is never given one registers the empty table and publishes no zone fact.</summary>
    public MapObjectModule(IForgeEngine kernel, RuntimeLogLevel logLevel, IMapObjectSource doors,
        IMapObjectSource terminals, Func<string, string, object?> resolve, Func<object, EntityReference?>? players,
        Func<bool> authority, Action<string> report, Action<string> log, Func<MapLevelReference?>? currentLevel = null,
        Func<object, object?>? hit = null, TriggerZoneSource? zones = null)
        : this(kernel, logLevel, doors, terminals, resolve, players, authority, report, log, currentLevel, hit)
    {
        _zoneSource = zones ?? new TriggerZoneSource();
        if (_currentLevel != null) _zoneSource.CurrentLevel = Level;
    }

    /// <summary>The zone table this module addresses.</summary>
    public TriggerZoneSource Zones
    {
        get
        {
            CheckThread();
            return _zoneSource;
        }
    }

    /// <summary>
    /// One membership edge of one zone: the target walked into the volume or out of it. The zone is the fact's
    /// subject, re-read from the zone table exactly as a door's status is re-read from its instance, and the
    /// payload carries the zone entity and the target entity. The answer is whether this half published the event,
    /// false when the edge reached the kernel as no fact at all: queued and ignored are both an edge the kernel
    /// accepted — the second because no plan hangs on the binding right now — while a zone this module does not
    /// hold, a client's own tick and a refused publication publish nothing, which is the same answer a door that
    /// no longer reads its address gets.
    /// </summary>
    public bool ZoneMembership(string fact, TriggerZone zone, EntityReference target, long transition)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(target);
        bool entered = fact == TriggerZoneContract.EnteredFact;
        if (!entered && fact != TriggerZoneContract.ExitedFact)
            throw new ArgumentOutOfRangeException(nameof(fact), fact, "Not a trigger-zone membership fact.");
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this
        // call would build, so neither the address, the subject nor the event value is built. A zone judged only
        // because a plan reads its members reaches here on every edge, and that read needs no event at all.
        var gate = entered ? _zoneEnteredGate : _zoneExitedGate;
        if (Unsubscribed(gate)) return false;
        if (TriggerZoneAddress.Of(zone.Id) is not { } address || !ReferenceEquals(_zoneSource.ById(zone.Id), zone))
        {
            ReportOnce("not-held:" + zone.Id, "map-object trigger-zone fact refused: zone `" + zone.Id + "` is not this provider's own record.");
            return false;
        }
        if (_disposed || _registration is not { IsRegistered: true } registration)
        {
            ReportOnce("unregistered", "map-object trigger-zone fact refused: the provider is not registered.");
            return false;
        }
        if (_kernel.StartupState != RuntimeStartupState.Ready)
        {
            ReportOnce("not-ready", "map-object trigger-zone fact refused: the runtime is not ready.");
            return false;
        }
        // The same guard the other map-object facts carry: the host publishes map-object state and a client does
        // not, so a client's own tick could never reach here with a fact to send.
        if (!_authority())
        {
            ReportOnce("client:" + address,
                "map-object fact observed on a non-authoritative peer: the host publishes map-object state, a client does not.");
            return false;
        }
        var subject = new MapObjectSubject(EntityId(address), address, _zoneSource, zone);
        // The entered and exited rows declare the same outputs: the zone first, then the body that walked in or out.
        DispatchResult result;
        try
        {
            var raised = registration.BeginEvent(gate!);
            raised.Values.Entity(Blocks.TriggerSpatialEntered.OutZone, subject.Reference);
            raised.Values.Entity(Blocks.TriggerSpatialEntered.OutTarget, target);
            result = raised.Raise(
                EntityKinds.MapObject + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
                    + ":" + address + ":" + target.Id + ":" + transition.ToString(CultureInfo.InvariantCulture),
                _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope());
        }
        catch (Exception error)
        {
            ReportOnce("publish:" + fact + ":" + address, "map-object trigger-zone fact threw: " + error.GetType().Name);
            return false;
        }
        if (result.Status is "queued" or "ignored") return true;
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return false;
    }
}
