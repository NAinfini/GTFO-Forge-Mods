using System;

namespace ForgeMap.Native;

/// <summary>Ownership of the native member events of one Forge-started wave run. This schedules nothing and never
/// generates a member: every member is one original `Mastermind.TriggerSurvivalWave`, and selection, group creation,
/// cost accounting and completion belong to the original SurvivalWave. The kernel handle owns the lifetime; the
/// session slot exists only while a member's native start runs. The run is live while any member is live, and a
/// cancellation stops every member through its own StopEvent.</summary>
internal sealed class NativeWaveLifetime
{
    private sealed class Member
    {
        internal ushort Id;
        internal Mastermind.MastermindEvent? Wave;
        internal bool StopAttempted;
    }

    private readonly WaveResourceSession _session;
    private readonly WaveResourceRegistry.Run _run;
    private readonly long _epoch;
    private readonly Member[] _members;
    private int _starting = -1;
    private bool _cancelRequested, _readFailureRecorded;

    internal NativeWaveLifetime(WaveResourceSession session, WaveResourceRegistry.Run run, long epoch)
    {
        _session = session; _run = run; _epoch = epoch;
        _members = new Member[run.Members];
        for (int index = 0; index < _members.Length; index++) _members[index] = new Member();
    }

    internal WaveResourceRegistry.Run Run => _run;
    internal WaveResource Resource => _run.PinnedResource;
    internal long Epoch => _epoch;
    internal int Members => _members.Length;
    internal bool Observed(int member) => _members[member].Wave != null;
    internal ushort EventOf(int member) => _members[member].Id;

    /// <summary>Only the member whose native start is running may register; a nested unrelated Spawn falls through.</summary>
    internal void BeginMember(int member) => _starting = member;
    internal void EndMember() => _starting = -1;

    internal bool AcceptsRegistration(ushort settings, ushort population)
    {
        if (_starting < 0 || _members[_starting].Id != 0) return false;
        var expected = Resource.NativeMembers[_starting];
        return settings == expected.SettingsId && population == expected.PopulationId;
    }

    internal void Registered(ushort eventId)
    {
        if (_starting >= 0 && _members[_starting].Id == 0 && Owns(eventId)) _members[_starting].Id = eventId;
    }

    // Called after the existing static native Spawn returned. A throwing Spawn never reaches this postfix.
    internal void ObserveCreated(ushort eventId)
    {
        try
        {
            if (eventId == 0 || !Owns(eventId)) return;
            foreach (var member in _members)
            {
                if (member.Id != eventId || member.Wave != null) continue;
                if (Mastermind.Current is not { } master || master.WasCollected
                    || !master.TryGetEvent(eventId, out var candidate) || candidate == null
                    || !AlarmWaveActions.IsCurrentWave(master, candidate)) return;
                member.Wave = candidate;
                return;
            }
        }
        catch (Exception error)
        {
            // Observation cannot interrupt a completed native Spawn. The action may observe the
            // same event after Trigger returns; an unreadable event remains an unknown commit.
            RecordReadFailure(error);
        }
    }

    private bool Owns(ushort eventId)
        => eventId != 0 && _session.WorldEpoch == _epoch && _session.Registry.ReadIdentity(_epoch, eventId) is { } identity
            && identity.Instance == _run.Instance && identity.Address == _run.PinnedResource.Address;

    /// <summary>Whether any member is still the current native event of this run.</summary>
    internal bool IsLive() => Successor != null ? Successor.IsLive() : !_cancelRequested && AnyCurrent();

    /// <summary>The run a checkpoint restore started again for the same list entry. The handle the author's behavior still holds names
    /// this lifetime, so every question about it is answered by the run that replaced it and a stop by that handle reaches the new run.</summary>
    internal NativeWaveLifetime? Successor { get; private set; }

    internal void ReplaceWith(NativeWaveLifetime next)
    {
        var tail = this;
        while (tail.Successor != null) tail = tail.Successor;
        if (tail != next) tail.Successor = next;
    }

    internal bool AnyCurrent()
    {
        if (Successor != null) return Successor.AnyCurrent();
        foreach (var member in _members) if (OwnsCurrent(member)) return true;
        return false;
    }

    // The kernel's sweep has only a boolean answer. An unreadable native object has not been proved
    // gone: retain this owner so a later readable cancellation can still stop the precise events.
    internal bool KeepHandleWhileUncertain(object native)
    {
        try { return IsLive() || Successor == null && _session.RestorePending; }
        catch (Exception error) { RecordReadFailure(error); return true; }
    }

    private void RecordReadFailure(Exception error)
    {
        if (_readFailureRecorded) return;
        _readFailureRecorded = true;
        _session.Record(Resource, FirstEvent(), "wave-liveness-unavailable", detail: error.GetType().Name);
    }

    private ushort FirstEvent()
    {
        foreach (var member in _members) if (member.Id != 0) return member.Id;
        return 0;
    }

    private bool OwnsCurrent(Member member)
        => member.Wave != null && Owns(member.Id) && Mastermind.Current is { } master
            && member.Wave.EventID == member.Id && AlarmWaveActions.IsCurrentWave(master, member.Wave);

    internal void Cancel(string reason)
    {
        if (Successor != null) { Successor.Cancel(reason); return; }
        if (_cancelRequested) return;
        _cancelRequested = true;
        try { StopObserved(reason); }
        catch (Exception error) { RecordReadFailure(error); throw; }
    }

    /// <summary>Stops every observed member that is still this run's current event. One failing member does not
    /// keep the others running; the first failure is rethrown after all were attempted.</summary>
    private void StopObserved(string reason)
    {
        Exception? failure = null;
        foreach (var member in _members)
        {
            try
            {
                if (member.StopAttempted || member.Wave == null || !OwnsCurrent(member)) continue;
                member.StopAttempted = true;
                // Mark cancellation before the original StopEvent observer sees it. Never stop by an unbound ID.
                _session.Registry.Cancel(_epoch, member.Id, reason);
                member.Wave.StopEvent();
            }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null) throw failure;
    }

    internal void AbortUnpublished()
    {
        // A scope can end during Spawn before the read-only postfix has observed the event. Cleanup is
        // performed here after native start unwinds, never as a write inside that observation hook.
        _cancelRequested = true;
        try { StopObserved("wave-start-unpublished"); }
        catch (Exception error) { _session.Record(Resource, FirstEvent(), "wave-start-cleanup-exception", detail: error.GetType().Name); }
        foreach (var member in _members)
            if (member.Wave == null && member.Id != 0)
                _session.Record(Resource, member.Id, "wave-start-unobserved-object");
    }
}
