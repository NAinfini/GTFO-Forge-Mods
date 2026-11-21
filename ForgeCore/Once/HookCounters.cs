using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ForgeCore;

/// <summary>
/// The measured sections of the hot path (plan §4.5 P1): the host's fixed tick split into its three halves, and the
/// native hooks that run once per hit, per enemy or per weapon every frame. A section is a fixed index, so a hook
/// names it with a constant and the counters are three arrays rather than a table keyed by text.
/// </summary>
public enum HookSection
{
    /// <summary>The kernel's own advance of one host tick, lifecycle notification excluded.</summary>
    TickAdvance,
    /// <summary>The lifecycle observers the advance notifies.</summary>
    TickLifecycle,
    /// <summary>What the host sends after the advance: presentations, owner commands, variables.</summary>
    TickNetwork,
    /// <summary><c>Dam_EnemyDamageBase.ProcessReceivedDamage</c>, the opening and closing halves apart.</summary>
    EnemyDamagePrefix,
    EnemyDamagePostfix,
    /// <summary>The two <c>EnemyDetection.UpdateTargets</c> postfixes: behaviour facts and tag state.</summary>
    EnemyBehaviorTargets,
    EnemyTagTargets,
    /// <summary><c>BulletWeaponArchetype.Update</c>; its prefix and postfix each count as one call.</summary>
    WeaponUpdate,
    /// <summary><c>SentryGunInstance_Firing_Bullets.UpdateFireMaster</c>; its prefix and postfix each count as one call.</summary>
    SentryFireMaster,
    RuntimeFixedFrame,
    RuntimeDiagnosticsPoll,
    PlayerFrame,
    PlayerLanguageFrame,
    PlayerKeyPoll,
    PlayerActionLockFrame,
    EnemyFrame,
    EnemyPhasePoll,
    WeaponMeleeCharge,
    WeaponAim,
    WeaponProjectilePump,
    WeaponScanner,
    WeaponUsePoll,
    TriggerFieldRefresh,
    PlayerBotRoster,
    MapFrame,
    DevelopmentFrame,
    DevelopmentDiagnostics,
    DevelopmentMaterials,
    DevelopmentSeeds,
    DevelopmentModelProbe,
    DevelopmentGuideCapture,
    DevelopmentCapturePump,
    DevelopmentRecorderPump,
    DevelopmentTraceBegin,
    DevelopmentTraceComplete,
    DevelopmentObservationBegin,
    DevelopmentObservationComplete,
    DevelopmentPackTest,
    DevelopmentExperiment,
    RuntimeLogWrite,
    DevelopmentCollector,
    DevelopmentObserver,
    PlayerActionLifecycle,
    PlayerModifierLifecycle,
    EnemyGlueLifecycle,
    EnemyTickPump,
    WeaponLifecycle,
    MapClockFrame,
}

/// <summary>
/// Call count, Stopwatch ticks and managed bytes allocated per <see cref="HookSection"/>, collected only while
/// <see cref="Enabled"/> is set. Off is the shipped state: a scope then reads one static boolean, stores nothing and
/// allocates nothing, which is what the Perf `gate` block proves. The author layer (ForgeDevelopment) turns it on,
/// reads and resets it on the main thread and writes each section as a `forge_hook` line in the performance log.
///
/// Every section is written from the simulation thread, so the counters are plain fields. The bytes are
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>: managed allocations of this thread only, not the Il2Cpp heap.
/// </summary>
public static class HookCounters
{
    private static readonly int Count = Enum.GetValues(typeof(HookSection)).Length;
    private static readonly long[] Calls = new long[Count], Ticks = new long[Count], Bytes = new long[Count],
        MaxTicks = new long[Count], MaxBytes = new long[Count];
    private static readonly long[] SelfTicks = new long[Count], SelfBytes = new long[Count],
        MaxSelfTicks = new long[Count], MaxSelfBytes = new long[Count];
    private static volatile bool _enabled;
    private static int _ownerThread;
    [ThreadStatic] private static long _completedTicks, _completedBytes;

    /// <summary>Whether scopes record. Scopes opened while off or closed after collection stops record nothing.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set { if (value) _ownerThread = Environment.CurrentManagedThreadId; _enabled = value; }
    }

    /// <summary>Opens one measured scope; dispose it where the section ends.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HookScope Measure(HookSection section)
        => Enabled && Environment.CurrentManagedThreadId == _ownerThread
            ? new HookScope(section, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), _completedTicks, _completedBytes) : default;

    internal static void Record(HookSection section, long ticks, long bytes, long completedTicks, long completedBytes)
    {
        if (!_enabled) return;
        var index = (int)section;
        var selfTicks = Math.Max(0, ticks - (_completedTicks - completedTicks));
        var selfBytes = Math.Max(0, bytes - (_completedBytes - completedBytes));
        _completedTicks += selfTicks; _completedBytes += selfBytes;
        SelfTicks[index] += selfTicks; SelfBytes[index] += selfBytes;
        if (selfTicks > MaxSelfTicks[index]) MaxSelfTicks[index] = selfTicks;
        if (selfBytes > MaxSelfBytes[index]) MaxSelfBytes[index] = selfBytes;
        Calls[index]++;
        Ticks[index] += ticks;
        if (ticks > MaxTicks[index]) MaxTicks[index] = ticks;
        if (bytes <= 0) return;
        Bytes[index] += bytes;
        if (bytes > MaxBytes[index]) MaxBytes[index] = bytes;
    }

    /// <summary>What one section recorded since the last <see cref="Reset"/>. Ticks are <see cref="Stopwatch"/> ticks.</summary>
    public static HookSample Read(HookSection section)
    {
        var index = (int)section;
        return new HookSample(section, Calls[index], Ticks[index], MaxTicks[index], Bytes[index], MaxBytes[index]);
    }

    /// <summary>Cost outside nested measured scopes. Sum this cost, rather than inclusive spans, for package attribution.</summary>
    public static HookSample ReadExclusive(HookSection section)
    {
        var index = (int)section;
        return new HookSample(section, Calls[index], SelfTicks[index], MaxSelfTicks[index], SelfBytes[index], MaxSelfBytes[index]);
    }

    public static void Reset()
    {
        Array.Clear(Calls); Array.Clear(Ticks); Array.Clear(Bytes); Array.Clear(MaxTicks); Array.Clear(MaxBytes);
        Array.Clear(SelfTicks); Array.Clear(SelfBytes); Array.Clear(MaxSelfTicks); Array.Clear(MaxSelfBytes);
    }
}

public readonly record struct HookSample(HookSection Section, long Calls, long Ticks, long MaxTicks, long Bytes, long MaxBytes);

/// <summary>One open section. The default value is the closed scope a disabled counter hands out.</summary>
public readonly struct HookScope : IDisposable
{
    private readonly HookSection section;
    private readonly long started, allocated, completedTicks, completedBytes;
    private readonly bool active;

    internal HookScope(HookSection section, long started, long allocated, long completedTicks, long completedBytes)
    {
        this.section = section; this.started = started; this.allocated = allocated;
        this.completedTicks = completedTicks; this.completedBytes = completedBytes; active = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!active || !HookCounters.Enabled) return;
        HookCounters.Record(section, Stopwatch.GetTimestamp() - started, GC.GetAllocatedBytesForCurrentThread() - allocated, completedTicks, completedBytes);
    }
}
