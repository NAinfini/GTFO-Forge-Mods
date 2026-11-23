using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger;

/// <summary>
/// The one place this package hangs on the runtime's own clock, and the reason the field table is world-scoped.
///
/// A field definition is level-attached content: it is installed by a package, read from that package's published
/// runtime resources, and it belongs to the world that was running when it was read. The definitions are therefore
/// re-read when the runtime reports a new world epoch — and only then — so a field whose document this install no
/// longer ships stops being resolvable for the new world, and a row that still names it answers its own `unloaded`
/// policy instead of measuring a volume from a definition nobody publishes any more.
///
/// The subscription is the runtime's own (`RuntimeModuleHandle.ObserveLifecycle`), not a frame patch of this
/// package's: the kernel raises `WorldChanged` exactly once per world, on every machine, which is the event this
/// work is about, and it replays the state a subscription starts from so a world already running when this package
/// registers is read at the same moment as every world after it. The read is a folder scan and a handful of small
/// documents, once per world — not per frame — and it mutates nothing the kernel owns, which is the one rule a
/// lifecycle observer has to keep.
/// </summary>
internal sealed class AreaFieldPump : IDisposable
{
    private readonly AreaFieldSession _session;
    private readonly System.Collections.Generic.IReadOnlyList<PublishedResource> _documents;
    private readonly Action<string> _report;
    private readonly RuntimeLifecycleSubscription _subscription;

    /// <param name="documents">The install's `area_field` runtime resources, handed out once by ForgeEngine's content
    /// discovery; each world re-reads these files, it never lists the install again.</param>
    public AreaFieldPump(RuntimeModuleHandle registration, AreaFieldSession session,
        System.Collections.Generic.IReadOnlyList<PublishedResource> documents, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _subscription = registration.ObserveLifecycle(OnLifecycle);
        if (!_subscription.IsActive)
            throw new InvalidOperationException("area-field-lifecycle-subscription-failed: initial field observation failed.");
    }

    /// <summary>One lifecycle event. Only a world boundary re-reads the field documents: a startup change or a tick
    /// is not a moment the install's own content can have changed for.</summary>
    private void OnLifecycle(RuntimeLifecycleEvent lifecycle)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.TriggerFieldRefresh);
        if (lifecycle.Kind is not (RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.Snapshot)) return;
        try { _session.Replace(AreaFieldTable.Load(_documents, lifecycle.Current.WorldEpoch, _report)); }
        catch (Exception error) { _report("area-field-refresh: the field documents could not be re-read: " + error.Message); }
    }

    public void Dispose() => _subscription.Dispose();
}
