using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ForgeEngine.GameBindings;

/// <summary>A single writer's immutable native callback declaration. Only Runtime applies it.</summary>
public sealed record NativeHookDefinition(string Writer, MethodInfo Target, MethodInfo? Prefix = null,
    MethodInfo? Postfix = null, MethodInfo? Finalizer = null,
    int PrefixPriority = 800, int PostfixPriority = 800, int FinalizerPriority = 0);

// patch-targets: run time (Install writes the declarations packages make; each declaration's target is checked where it is written)
public static partial class NativeHookRegistration
{
    private static readonly NativeHookInstallations Installations = new(Install);
    private static readonly List<Func<IEnumerable<NativeHookDiagnostic>>> SharedReaders = new();
    internal static void TrackShared(Func<IEnumerable<NativeHookDiagnostic>> read) => SharedReaders.Add(read);
    /// <summary>Frozen counters from the actual installer and active shared leases, not consumer-side estimates.</summary>
    public static IReadOnlyList<NativeHookDiagnostic> ReadSharedDiagnostics()
        => SharedReaders.SelectMany(read => read()).ToArray();
    internal static (int Attempts, int Installations) InstallationCounts(MethodInfo target) => Installations.Counts(target);
    private static Func<long> _world = static () => -1;
    private static Action<string, Exception>? _fault;
    private static Action<NativeHookDeclared>? _declared;
    private static Action<NativeHookDiagnostic, string>? _sharedCounters;
    /// <summary>The host's world, never a package-owned clock. Negative means no live host.</summary>
    public static long WorldEpoch => _world();
    /// <summary>The host binds its world and its observer-failure report; the declaration record point receives one summary
    /// per writer batch and the shared-counter record point every lease boundary.</summary>
    internal static void BindWorld(Func<long> world, Action<string, Exception> fault,
        Action<NativeHookDeclared>? declared = null, Action<NativeHookDiagnostic, string>? sharedCounters = null)
    { _world = world; _fault = fault; _declared = declared; _sharedCounters = sharedCounters; }
    internal static void ReportSharedDiagnostic(NativeHookDiagnostic row, string boundary)
    {
        try { _sharedCounters?.Invoke(row, boundary); }
        catch (Exception error) { ObserverFailed("native-hook-shared-counters", error); }
    }
    internal static void ObserverFailed(string consumer, Exception error)
    {
        try { _fault?.Invoke(consumer, error); }
        catch (Exception reporting) { error.Data["ForgeEngine.ObserverReportingFailure"] = reporting; }
    }
    internal static string Refusal(string reason, NativeHookDefinition definition)
    {
        var declaration = string.IsNullOrWhiteSpace(definition.Writer) ? "<invalid>" : definition.Writer;
        return reason + " declaration=" + declaration + " target=" + TargetIdentity(definition.Target);
    }

    /// <summary>Declare once; the installation stays for the process. Identical declarations change nothing; a second
    /// writer, a callback change or an unsupported target refuses.</summary>
    public static void Declare(NativeHookDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ObservationWriter(definition.Target)) throw new NotSupportedException(Refusal("native-hook-writer-conflict", definition));
        Installations.Ensure(definition);
    }

    /// <summary>One writer's parts on one target, installed by ForgeCore as that writer's whole set, callbacks of each kind
    /// in declaration order. ForgeCore's refusal keeps its code and gains the declaration it refused.</summary>
    private static void Install(IReadOnlyList<NativeHookDefinition> parts)
    {
        using var timing = ForgeCore.StartupOperationTiming.Measure(ForgeCore.StartupOperationTiming.Operation.PatchInstallation);
        try
        {
            ForgeCore.Hooks.Own(parts[0].Writer, parts[0].Target, parts.Select(part => new ForgeCore.HookPart(
                Method(part.Prefix, part.PrefixPriority), Method(part.Postfix, part.PostfixPriority), Method(part.Finalizer, part.FinalizerPriority))).ToArray());
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException)
        { throw new NotSupportedException(Refusal(error.Message, parts[0])); }
    }

    internal static HarmonyMethod? Method(MethodInfo? callback, int priority, string[]? before = null, string[]? after = null)
        => callback == null ? null : new HarmonyMethod(callback)
        { priority = priority, before = before ?? Array.Empty<string>(), after = after ?? Array.Empty<string>() };
}

/// <summary>Actual physical installation attempts, verified installations and one subscriber phase's callback count.</summary>
public readonly record struct NativeHookDiagnostic(string Writer, MethodInfo Target, string Phase, string Consumer,
    long WorldEpoch, string Status, int InstallationAttempts, int Installations, long Callbacks)
{
    /// <summary>Process-cumulative entries into this provider phase, including calls with no subscribers.
    /// Callbacks is this individual lease's count; compare provider deltas over the same lease interval.
    /// A specialized reader that does not measure provider entries leaves this absent, never a fabricated zero.</summary>
    public long? ProviderCallbacks { get; init; }
}

/// <summary>One writer's declaration batch: callback classes, distinct targets and refused callbacks.</summary>
internal readonly record struct NativeHookDeclared(string Writer, int Callbacks, int Targets, int Refused);

// The same rules run in production and offline tests. Tests replace only the installer.
internal sealed class NativeHookInstallations
{
    private sealed class Record
    {
        internal readonly IReadOnlyList<NativeHookDefinition> Parts;
        internal int Attempts, Installations;
        internal Record(IReadOnlyList<NativeHookDefinition> parts) => Parts = parts;
    }
    private readonly Action<IReadOnlyList<NativeHookDefinition>> _apply;
    private readonly Dictionary<MethodBase, Record> _records = new();
    internal NativeHookInstallations(Action<IReadOnlyList<NativeHookDefinition>> apply) => _apply = apply;

    internal void Ensure(NativeHookDefinition definition) => Ensure(new[] { definition });
    internal (int Attempts, int Installations) Counts(MethodInfo target)
        => _records.TryGetValue(target, out var record) ? (record.Attempts, record.Installations) : default;
    internal bool HasDeclaration(MethodInfo target) => _records.ContainsKey(target);

    /// <summary>One writer's callbacks on one target, in declaration order, installed once. A different writer on the same
    /// target is a conflict, and a writer's declaration is whole: it is never extended later.</summary>
    internal void Ensure(IReadOnlyList<NativeHookDefinition> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("native-hook-declaration-invalid parts=0");
        var definition = parts[0];
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Writer) || part.Writer.Length > 128
                || part.Target == null || part.Prefix == null && part.Postfix == null && part.Finalizer == null)
                throw new ArgumentException(NativeHookRegistration.Refusal("native-hook-declaration-invalid", part));
            if (part.Writer != definition.Writer || !Equals(part.Target, definition.Target))
                throw new ArgumentException(NativeHookRegistration.Refusal("native-hook-declaration-invalid parts=mixed", part));
            foreach (var callback in new[] { part.Prefix, part.Postfix, part.Finalizer })
                if (callback != null && (!callback.IsStatic || callback.ContainsGenericParameters))
                    throw new ArgumentException(NativeHookRegistration.Refusal("native-hook-callback-invalid", part));
        }
        var target = definition.Target;
        if (NativeHookRegistration.RuntimeSharedWriter(target) is { } shared && shared != definition.Writer)
            throw new NotSupportedException(NativeHookRegistration.Refusal("native-hook-runtime-shared-target subscribe-via=" + shared, definition));
        if (_records.TryGetValue(target, out var known))
        {
            if (known.Parts[0].Writer != definition.Writer)
                throw new NotSupportedException(NativeHookRegistration.Refusal(
                    "native-hook-writer-conflict existing=" + known.Parts[0].Writer, definition));
            if (!known.Parts.SequenceEqual(parts))
                throw new NotSupportedException(NativeHookRegistration.Refusal("native-hook-declaration-changed", definition));
            return;
        }
        var record = new Record(Array.AsReadOnly(parts.ToArray()));
        record.Attempts++;
        _apply(record.Parts);
        record.Installations++;
        _records.Add(target, record);
    }
}

/// <summary>Typed readonly fan-out. The declaration's adapter is the only producer exposed by its owning package.</summary>
internal enum NativeHookDispatchStatus { Delivered, NoSubscribers, WorldEpochStale }

/// <summary>The one subscription a world-bound shared entry takes to its <see cref="ForgeCore.Bracket{TCall}"/>: ForgeCore patches the game
/// method and carries no world; the host's epoch begins here. Taken on the first subscriber; a bracket whose game method is missing
/// refuses with native-hook-unavailable.</summary>
internal sealed class SharedBracket
{
    private readonly Func<IDisposable> _subscribe;
    private IDisposable? _subscription;
    internal string Writer { get; }
    internal MethodInfo Target { get; }
    internal int Attempts { get; private set; }
    internal int Installations => _subscription == null ? 0 : 1;
    internal SharedBracket(string writer, MethodInfo target, Func<IDisposable> subscribe)
    { Writer = writer; Target = target; _subscribe = subscribe; }
    internal void Ensure()
    {
        if (_subscription != null) return;
        Attempts++;
        try { _subscription = _subscribe(); }
        catch (InvalidOperationException error)
        { throw new NotSupportedException("native-hook-unavailable declaration=" + Writer + " target=" + NativeHookRegistration.TargetIdentity(Target) + " " + error.Message); }
    }
}

/// <summary>One Runtime-owned shared entry's calls handed, in subscription order, to every subscriber of the host's current world
/// (<see cref="NativeHookObservers{T}"/>); the entry is declared, or its ForgeCore bracket subscribed, on the first subscription.</summary>
// patch-targets: run time (a channel over a ForgeCore bracket names no patch; its definition only identifies the entry in refusals and diagnostics)
internal sealed class NativeHookChannel<T>
{
    private readonly NativeHookDefinition _definition;
    private readonly SharedBracket? _bracket;
    private readonly string _phase;
    private readonly NativeHookObservers<Action<T>> _observers = new(static () => NativeHookRegistration.WorldEpoch, NativeHookRegistration.ObserverFailed, 256);
    private long _calls;
    public NativeHookChannel(NativeHookDefinition definition, string phase = "observation")
    { _definition = definition; _phase = phase; NativeHookRegistration.TrackShared(ReadDiagnostics); }
    internal NativeHookChannel(SharedBracket bracket, string phase) : this(new NativeHookDefinition(bracket.Writer, bracket.Target), phase) => _bracket = bracket;
    private (int Attempts, int Installations) Counts()
        => _bracket != null ? (_bracket.Attempts, _bracket.Installations) : NativeHookRegistration.InstallationCounts(_definition.Target);
    private IEnumerable<NativeHookDiagnostic> ReadDiagnostics()
    {
        var counts = Counts();
        var entries = _observers.Items;
        if (entries.Length == 0)
            yield return new(_definition.Writer, _definition.Target, _phase, "none", NativeHookRegistration.WorldEpoch,
                "no-subscribers", counts.Attempts, counts.Installations, 0)
            { ProviderCallbacks = Interlocked.Read(ref _calls) };
        foreach (var entry in entries)
            yield return ReadDiagnostic(entry.Lease);
    }
    private NativeHookDiagnostic ReadDiagnostic(NativeHookLease lease)
    {
        var counts = Counts();
        return new(_definition.Writer, _definition.Target, _phase, lease.Consumer, lease.WorldEpoch,
            lease.Status, counts.Attempts, counts.Installations, lease.Callbacks)
        { ProviderCallbacks = Interlocked.Read(ref _calls) };
    }
    public NativeHookLease Subscribe(string consumer, long worldEpoch, Action<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (string.IsNullOrWhiteSpace(consumer) || consumer.Length > 128) throw new ArgumentException(NativeHookRegistration.Refusal("native-hook-consumer-invalid", _definition));
        if (worldEpoch < 0 || NativeHookRegistration.WorldEpoch != worldEpoch)
            throw new InvalidOperationException(NativeHookRegistration.Refusal("native-hook-world-epoch-stale", _definition));
        if (_bracket != null) _bracket.Ensure();
        else NativeHookRegistration.Declare(_definition);
        var lease = _observers.Subscribe(consumer, worldEpoch, 0, callback, reason => NativeHookRegistration.Refusal(reason, _definition),
            ended => NativeHookRegistration.ReportSharedDiagnostic(ReadDiagnostic(ended), "lease-ended"));
        NativeHookRegistration.ReportSharedDiagnostic(ReadDiagnostic(lease), "subscribed");
        return lease;
    }

    /// <summary>Hands <paramref name="observation"/> of world <paramref name="epoch"/> to the active subscribers; a world change
    /// inside a callback stops delivery to the rest.</summary>
    public NativeHookDispatchStatus Publish(T observation, long epoch)
    {
        if (epoch < 0 || NativeHookRegistration.WorldEpoch != epoch) return NativeHookDispatchStatus.WorldEpochStale;
        Interlocked.Increment(ref _calls);
        var delivered = false;
        foreach (var entry in _observers.Items)
        {
            if (NativeHookRegistration.WorldEpoch != epoch) return NativeHookDispatchStatus.WorldEpochStale;
            if (!entry.Lease.IsActive) continue;
            try { entry.Lease.CallbackEntered(); entry.Observer(observation); delivered = true; }
            catch (Exception error) { _observers.Failed(entry.Lease, error); }
        }
        return NativeHookRegistration.WorldEpoch != epoch ? NativeHookDispatchStatus.WorldEpochStale
            : delivered ? NativeHookDispatchStatus.Delivered : NativeHookDispatchStatus.NoSubscribers;
    }
}
