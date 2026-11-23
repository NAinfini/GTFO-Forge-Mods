using System;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The one place this package keeps its per-tick cadence.
///
/// The runtime advances one kernel per simulation step. The session's in-level frame driver calls <see cref="Frame"/>
/// once per kernel tick, after that advance and outside every lifecycle notification: the work a tick owes judges
/// zones and publishes facts, and a lifecycle observer is read-only. Ticking off the kernel's tick rather than a
/// per-player update means one tick per step on every machine, the same step the game measured.
///
/// One lifecycle registration is acquired in the session's safe construction window; it only resets the cadence.
/// Host command handlers run during kernel dispatch and may not register observers. They can announce work through
/// Wake without mutating the registry. An idle tick only reads the pending predicates and returns; it does not
/// query the world or time. World/checkpoint notifications reset the cadence and invoke the session's
/// transient-work cleanup.
/// </summary>
internal sealed class MapClock : IDisposable
{
    /// <summary>
    /// The period a trigger zone is judged at, taken from the game's own collision trigger:
    /// `LG_CollisionWorldEventTrigger` carries `public const float COLLISION_CHECK_INTERVAL = 0.1` and re-tests its
    /// volume from its `Update` on that timer (`m_collisionCheckTimer` at 0x38, `m_playerWasInLastCheck` at 0x3C;
    /// dump.cs of build 20403457, SHA-256 `BF657C0E…DE1CC`, lines 697075-697094). A zone of this package asks the
    /// same kind of question — "is anything inside this volume now" — so it is judged at that cadence instead of
    /// per frame. Judging per fixed frame would spend the host's query budget five to ten times more often for an
    /// answer the game's own trigger only changes at this rate. The game's check is point-in-volume; the travelled
    /// segment this package adds is its own, because the native trigger cannot see a body that crossed a thin
    /// volume between two of its checks either (`TriggerZone.Crossed`).
    ///
    /// One beat is also what a placement is measured against: `TriggerZoneModule.MaximumJudgedTravel` is this
    /// period at the quickest speed the game carries a body at, and the two are the same fact — this file is
    /// compiled into the clock's own test project without the module beside it, so they are kept in step by this
    /// note rather than by a shared expression. One beat is 0.1 second and the bound is 3 metres.
    /// </summary>
    internal const float TriggerZoneSeconds = 0.1f;

    private readonly RuntimeModuleHandle _registration;
    private readonly Action _refresh;
    private readonly Func<bool> _zonesPending;
    private readonly Action _zones;
    private readonly Func<bool> _fadesPending;
    private readonly Action<float> _fades;
    private readonly Func<float> _seconds;
    private readonly Action _reset;
    private RuntimeLifecycleSubscription? _subscription;
    private float _sinceZones;
    private bool _disposed;

    /// <summary>One clock for one session. <paramref name="refresh"/> is the level-facing half of the zone work —
    /// placing a new level's zones and keeping its blocking bodies in step — and runs when work is announced rather
    /// than from inside the callback, because placement is a native side effect that belongs to a native stage.
    /// <paramref name="seconds"/> is the step the game measured, injected so the cadence is testable without a
    /// Unity frame.</summary>
    internal MapClock(RuntimeModuleHandle registration, Action refresh, Func<bool> zonesPending, Action zones,
        Func<bool> fadesPending, Action<float> fades, Func<float> seconds, Action reset)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _zonesPending = zonesPending ?? throw new ArgumentNullException(nameof(zonesPending));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _fadesPending = fadesPending ?? throw new ArgumentNullException(nameof(fadesPending));
        _fades = fades ?? throw new ArgumentNullException(nameof(fades));
        _seconds = seconds ?? throw new ArgumentNullException(nameof(seconds));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _subscription = _registration.ObserveLifecycle(OnLifecycle, replayCurrent: false);
        Registrations++;
    }

    /// <summary>One registration for the session, independent of how often work starts or becomes idle.</summary>
    internal long Registrations { get; private set; }

    /// <summary>Whether the clock is taken right now.</summary>
    internal bool Registered => _subscription?.IsActive == true;

    /// <summary>Announces that work may have appeared: a fade was scheduled, a level's zones were placed, a player
    /// reconciled. This does not register anything and can run inside a host command dispatch.</summary>
    internal void Wake()
    {
        if (_disposed) return;
        _refresh();
    }

    /// <summary>The world boundary. This is the clock's only lifecycle callback: it stays read-only, and the work a
    /// tick owes runs in <see cref="Frame"/>.</summary>
    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (_disposed) return;
        if (value.Kind is not (RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored) || value.IsMigration) return;
        _sinceZones = 0f;
        _reset();
    }

    /// <summary>One kernel tick's work, called once per tick by the session's in-level frame driver
    /// (<c>MapPluginSession.InLevelFrame</c>) and never from a lifecycle notification: judging a zone queries the
    /// kernel's entities and publishes facts, which a lifecycle observer may not do
    /// (<c>lifecycle-observer-mutation</c>).</summary>
    internal void Frame()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.MapClockFrame);
        if (_disposed) return;
        var zonesPending = _zonesPending();
        var fadesPending = _fadesPending();
        if (!zonesPending && !fadesPending) { _sinceZones = 0f; return; }
        var seconds = _seconds();
        if (fadesPending) _fades(seconds);
        // The cadence accumulates the game's own step, so a machine whose frame rate differs from the host's still
        // judges the same number of times per second of play. The remainder of a beat is kept rather than dropped,
        // so the beat does not drift. A step that fell more than a beat behind is judged once and the excess is
        // dropped: a long hitch must not turn into a burst of judgments on the frames that follow it.
        _sinceZones += seconds;
        if (_sinceZones >= TriggerZoneSeconds)
        {
            _sinceZones -= TriggerZoneSeconds;
            if (_sinceZones >= TriggerZoneSeconds) _sinceZones = 0f;
            if (_zonesPending()) _zones();
        }
    }

    private void Release()
    {
        var subscription = _subscription;
        _subscription = null;
        subscription?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
