using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ForgeCore;

/// <summary>Measured observer CPU on one native frame; idle time and the observed game method are never charged.</summary>
public sealed class DiagnosticCpuBudget
{
    public static DiagnosticCpuBudget Shared { get; } = new(Stopwatch.GetTimestamp,
        Math.Max(1, Stopwatch.Frequency / 500));
    private readonly object gate = new();
    private readonly Func<long> clock;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly long limit;
    private Func<long>? nativeFrame;
    private long frame = long.MinValue, spent, checkpoint, sequence, current;
    private long total, maximumFrame, maximumStep, dropped, overruns, hits;
    private bool overrun, hit;
    private string currentOwner = "", stepOwner = "";
    private readonly System.Collections.Generic.Stack<string> ownerStack = new();
    private readonly System.Collections.Generic.Dictionary<string, long> owners = new();

    public DiagnosticCpuBudget(Func<long> clock, long limitTicks)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (limitTicks <= 0) throw new ArgumentOutOfRangeException(nameof(limitTicks));
        limit = limitTicks;
    }
    public void SetFrameSource(Func<long> readFrame) => nativeFrame = readFrame;
    private void RefreshFrame()
    {
        if (nativeFrame != null && Environment.CurrentManagedThreadId == thread && current == 0)
            BeginFrame(nativeFrame());
    }

    /// <summary>Same-frame, stale-frame and in-flight calls cannot replenish the allowance.</summary>
    public void BeginFrame(long nativeFrame)
    {
        if (Environment.CurrentManagedThreadId != thread) return;
        lock (gate)
        {
            if (nativeFrame <= frame || current != 0) return;
            frame = nativeFrame; spent = 0; overrun = hit = false;
        }
    }

    public bool CanContinue
    {
        get
        {
            lock (gate)
            {
                RefreshFrame();
                if (Used() >= limit) { Hit(); return false; }
                return Environment.CurrentManagedThreadId == thread;
            }
        }
    }
    private long Used() => spent + (current == 0 ? 0 : Math.Max(0, clock() - checkpoint));
    // Leave 0.75 ms of the 2 ms allowance for an already started native/encoding step (measured peak 0.56 ms).
    public bool HasCooperativeTime
    {
        get { lock (gate) { RefreshFrame(); return Environment.CurrentManagedThreadId == thread && Used() < limit - limit * 3 / 8; } }
    }

    /// <summary>Reserve the remaining frame allowance. Nested observer scopes share its charge, without double counting.</summary>
    public bool TryReserve(out DiagnosticReservation reservation,
        [CallerFilePath] string file = "", [CallerMemberName] string member = "")
        => TryReserveFor(Owner(file, member), out reservation);

    /// <summary>Same as <see cref="TryReserve"/> with an explicit owner label for the attribution counters.</summary>
    public bool TryReserveFor(string owner, out DiagnosticReservation reservation)
    {
        lock (gate)
        {
            RefreshFrame();
            if (Environment.CurrentManagedThreadId != thread || Used() >= limit)
            { Hit(); dropped++; reservation = default; return false; }
            reservation = Reserve(owner); return true;
        }
    }

    /// <summary>Account indivisible startup/cleanup or an already admitted return observation. Never skip required cleanup.</summary>
    public DiagnosticReservation MeasureRequired([CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        lock (gate)
        {
            RefreshFrame();
            if (Environment.CurrentManagedThreadId != thread) { dropped++; return default; }
            if (Used() >= limit) Hit();
            return Reserve(Owner(file, member));
        }
    }

    // Call sites pass compile-time constants, so each label is built once; a steady frame loop allocates no label strings.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string File, string Member), string> OwnerLabels = new();
    private static string Owner(string file, string member)
        => OwnerLabels.GetOrAdd((file, member), static key =>
            (key.File.Length == 0 ? "unknown" : System.IO.Path.GetFileNameWithoutExtension(key.File)) + "." + key.Member);

    private DiagnosticReservation Reserve(string owner)
    {
        var now = clock();
        if (current == 0) checkpoint = now;
        var parent = current; current = ++sequence; currentOwner = owner; ownerStack.Push(owner);
        return new(this, current, parent, now);
    }

    /// <summary>The label of the reservation that held the longest single step (outermost scope, nested time included).</summary>
    public string MaximumStepOwner { get { lock (gate) return stepOwner; } }

    /// <summary>The owners that spent the most allowance (time not inside a nested scope), largest first, as "owner=ms".</summary>
    public string TopOwners(int count)
    {
        lock (gate)
            return string.Join(",", owners.OrderByDescending(pair => pair.Value).Take(count)
                .Select(pair => pair.Key + "=" + (pair.Value * 1000d / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Charge an indivisible observer operation measured by an existing owner.</summary>
    public void Charge(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        lock (gate)
        {
            // A parent reservation already includes this elapsed interval.
            if (current != 0) return;
            Account(ticks, "DiagnosticCpuBudget.Charge");
            if (ticks > maximumStep) { maximumStep = ticks; stepOwner = "DiagnosticCpuBudget.Charge"; }
        }
    }
    private void Account(long ticks, string owner)
    {
        owners.TryGetValue(owner, out var before); owners[owner] = before + ticks;
        spent += ticks; total += ticks; maximumFrame = Math.Max(maximumFrame, spent);
        if (spent >= limit) Hit();
        if (spent > limit && !overrun) { overrun = true; overruns++; }
    }
    private void Hit() { if (!hit) { hit = true; hits++; } }
    internal void Charge(long id, long started, long parent, bool release)
    {
        lock (gate)
        {
            // A copied or stale value cannot refund a newer reservation.
            if (current != id || id == 0) return;
            var now = clock();
            Account(Math.Max(0, now - checkpoint), currentOwner); checkpoint = now;
            var step = Math.Max(0, now - started);
            if (step > maximumStep) { maximumStep = step; stepOwner = currentOwner; }
            if (release)
            {
                current = parent;
                if (ownerStack.Count != 0) ownerStack.Pop();
                currentOwner = ownerStack.Count == 0 ? "" : ownerStack.Peek();
            }
        }
    }
    public DiagnosticBudgetSnapshot Snapshot
    {
        get { lock (gate) return new(frame, limit, total, maximumFrame, maximumStep, dropped, overruns, hits); }
    }
}

public readonly record struct DiagnosticBudgetSnapshot(long Frame, long LimitTicks, long MeasuredTicks,
    long MaximumFrameTicks, long MaximumStepTicks, long Dropped, long OverrunFrames, long BudgetHits)
{
    public bool Incomplete => Dropped != 0 || OverrunFrames != 0;
}

/// <summary>Synchronous reserve → charge → return. Dispose also charges exceptional exits; never hold across game execution.</summary>
public struct DiagnosticReservation : IDisposable
{
    private DiagnosticCpuBudget? budget;
    private readonly long id, parent, started;
    internal DiagnosticReservation(DiagnosticCpuBudget budget, long id, long parent, long started)
    { this.budget = budget; this.id = id; this.parent = parent; this.started = started; }
    public bool CanContinue => budget?.CanContinue == true;
    public void Charge() => budget?.Charge(id, started, parent, false);
    public void Dispose()
    {
        budget?.Charge(id, started, parent, true); budget = null;
    }
}
