using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The publisher behind the three level-world trigger rows of <see cref="MapWorldTriggerContract"/>: a spitter burst,
/// a zone's lights switched and a fog transition begun. It knows no game type — the native half reads the entry's own
/// arguments and hands the values here — and it owns no registration of its own: the rows are declared on the one Map
/// provider and published through its handle.
///
/// **Gate first.** The three gates are resolved once, when the half is built, and kept as fields. A native entry asks
/// <see cref="SpitterObserved"/>, <see cref="ZoneLightsObserved"/> or <see cref="FogObserved"/> before it reads
/// anything or builds a closure, so a level whose plans never listen to one of these rows pays one field read per
/// native call and allocates nothing.
///
/// **Host only.** The rows are host-executed: a peer that is not the authority reports once per row and publishes
/// nothing. Each fact is its own event, numbered by this world's own sequence of that fact, so a second burst, switch
/// or transition is a second event and the ids of two worlds never collide.
///
/// Every port of the three rows is required. A reading that cannot be turned into its port — a position that is not
/// finite, a zone coordinate that is negative, a transition length that is not a finite non-negative number or a
/// world whose tick rate is not known yet — is not published and is reported once by the row and the port.
/// </summary>
internal sealed class MapWorldFacts : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Func<bool> _authority;
    private readonly RuntimeSubscriptionGate? _spitterGate, _lightsGate, _fogGate;
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private RuntimeLifecycleSubscription? _lifecycle;
    private long _spitterSequence, _lightsSequence, _fogSequence;
    private bool _disposed;

    /// <summary>The one attached half the native entries read. Set by the session that owns the registration and
    /// cleared when it releases the half, so an entry that runs outside a session reads null and does nothing.</summary>
    internal static MapWorldFacts? Current { get; set; }

    public MapWorldFacts(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> authority, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        var gates = registration.SubscriptionGates();
        _spitterGate = Gate(gates, MapWorldTriggerContract.SpitterExplodedBinding);
        _lightsGate = Gate(gates, MapWorldTriggerContract.ZoneLightsChangedBinding);
        _fogGate = Gate(gates, MapWorldTriggerContract.FogChangedBinding);
        // A world numbers its facts from one again; the epoch in the event id keeps two worlds' ids apart.
        _lifecycle = registration.ObserveLifecycle(change =>
        {
            if (change.Kind == RuntimeLifecycleKind.WorldChanged) _spitterSequence = _lightsSequence = _fogSequence = 0;
        });
    }

    private static RuntimeSubscriptionGate? Gate(IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates, string binding)
        => gates.TryGetValue(binding, out var gate) ? gate : null;

    /// <summary>Whether a plan consumes the spitter row right now. A registration that does not declare the binding
    /// has no gate, and nothing is published for it.</summary>
    public bool SpitterObserved => !_disposed && _spitterGate is { HasSubscribers: true };
    public bool ZoneLightsObserved => !_disposed && _lightsGate is { HasSubscribers: true };
    public bool FogObserved => !_disposed && _fogGate is { HasSubscribers: true };

    /// <summary>What the kernel answered to the last event this half offered it, or null when it offered none.</summary>
    public DispatchResult? LastDispatch { get; private set; }

    /// <summary>The event id of the last event this half offered the kernel.</summary>
    public string? LastEventId { get; private set; }

    /// <summary>One spitter burst at a world position (metres, x/y/z).</summary>
    public bool SpitterExploded(double x, double y, double z)
    {
        CheckThread();
        if (!Ready(MapWorldTriggerContract.SpitterExplodedFact)) return false;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return Missing(MapWorldTriggerContract.SpitterExplodedFact, "position", "the spitter's position is not finite");
        if (_spitterGate is not { } gate) return false;
        long sequence = ++_spitterSequence;
        var raised = _registration.BeginEvent(gate);
        raised.Values.Vector(Blocks.TriggerMapSpitterExploded.OutPosition, x, y, z);
        return Offer(MapWorldTriggerContract.SpitterExplodedFact, sequence, raised);
    }

    /// <summary>One zone's lights switched: the zone's three coordinates as the level names them (dimension, layer,
    /// local zone index) and whether the lights went on.</summary>
    public bool ZoneLightsChanged(int dimension, int layer, int zone, bool on)
    {
        CheckThread();
        if (!Ready(MapWorldTriggerContract.ZoneLightsChangedFact)) return false;
        if (dimension < 0 || layer < 0 || zone < 0)
            return Missing(MapWorldTriggerContract.ZoneLightsChangedFact, "zone", "the zone coordinates are negative");
        if (_lightsGate is not { } gate) return false;
        long sequence = ++_lightsSequence;
        var raised = _registration.BeginEvent(gate);
        raised.Values.Resource(Blocks.TriggerMapZoneLightsChanged.OutZone,
            new ResourceRef(RuntimeZones.ResourceKind, RuntimeZones.Id(dimension, layer, zone)));
        raised.Values.Boolean(Blocks.TriggerMapZoneLightsChanged.OutOn, on);
        return Offer(MapWorldTriggerContract.ZoneLightsChangedFact, sequence, raised);
    }

    /// <summary>One fog transition begun: the fog preset id the level is changing to and the transition's length in
    /// seconds as the game takes it. The length is published in ticks, the one time unit the contract carries,
    /// converted through the running world's own tick rate.</summary>
    public bool FogChanged(long fog, double seconds)
    {
        CheckThread();
        if (!Ready(MapWorldTriggerContract.FogChangedFact)) return false;
        if (!double.IsFinite(seconds) || seconds < 0)
            return Missing(MapWorldTriggerContract.FogChangedFact, "duration", "the transition length is not a finite non-negative number");
        double ticksPerSecond;
        try { ticksPerSecond = _kernel.TicksPerSecond; }
        catch (RuntimeContractException)
        {
            return Missing(MapWorldTriggerContract.FogChangedFact, "duration", "the running world has reported no tick rate yet");
        }
        if (_fogGate is not { } gate) return false;
        long sequence = ++_fogSequence;
        var raised = _registration.BeginEvent(gate);
        raised.Values.Integer(Blocks.TriggerMapFogChanged.OutFog, fog);
        raised.Values.Number(Blocks.TriggerMapFogChanged.OutDuration, seconds * ticksPerSecond);
        return Offer(MapWorldTriggerContract.FogChangedFact, sequence, raised);
    }

    private bool Ready(string fact)
    {
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (_authority()) return true;
        ReportOnce("client:" + fact, fact + " observed on a non-authoritative peer: the host publishes it, a client does not.");
        return false;
    }

    /// <summary>Publishes one fact written by its trigger's output positions (the `next` port is position 0).</summary>
    private bool Offer(string fact, long sequence, EventWriter raised)
    {
        long epoch = _kernel.WorldEpoch;
        LastEventId = MapWorldTriggerContract.EventId(fact, epoch, sequence);
        var result = raised.Raise(LastEventId, epoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + epoch.ToString(CultureInfo.InvariantCulture));
        LastDispatch = result;
        if (result.Status == "queued") return true;
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return false;
    }

    private bool Missing(string fact, string port, string reason)
    {
        ReportOnce("port:" + fact + ":" + port, fact + " not published: required port `" + port + "` did not read (" + reason + ").");
        return false;
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Level-world facts require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        _lifecycle?.Dispose();
        _lifecycle = null;
    }
}
