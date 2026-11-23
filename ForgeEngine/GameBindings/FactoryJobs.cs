using System;
using System.Collections.Generic;
using System.Reflection;
using LevelGeneration;

namespace ForgeEngine.GameBindings;


/// <summary>Returns true to run the native dequeue; false means the generator supplied the result.</summary>
public delegate bool FactoryJobGenerator(LG_Factory factory, ref bool result);

/// <summary>Per-call state, kept on Harmony's call stack rather than allocated for each native job.</summary>
public readonly record struct FactoryJobExecutionToken(object? Context, int Scope);

/// <summary>Measures the real native Build call. Observers cannot replace its result or exception.</summary>
public abstract class FactoryJobExecutionObserver
{
    public abstract FactoryJobExecutionToken Enter(LG_FactoryJob job);
    public abstract void Exit(FactoryJobExecutionToken token, bool completed, Exception? error);
}

/// <summary>One factory dequeue installation, one immutable generator, and epoch-bound read-only observers.</summary>
public static class FactoryJobs
{
    private static readonly NativeHookDefinition Definition = new("ForgeEngine.FactoryJobs",
        typeof(LG_Factory).GetMethod(nameof(LG_Factory.GetNewJob), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException("LG_Factory.GetNewJob()"),
        ((FactoryJobGenerator)Before).Method, ((Action<bool>)After).Method);
    private static readonly NativeHookChannel<bool> Dequeued = new(Definition);
    private static FactoryJobGenerator? _generator;
    private static string? _writer;
    private static FactoryJobExecutionObserver? _executionObserver;
    private sealed record BuildBinding(NativeHookLease? Lease, string? Refusal);
    private static readonly Dictionary<MethodInfo, BuildBinding> BuildBindings = new();

    private readonly record struct ExecutionState(FactoryJobExecutionObserver? Observer, FactoryJobExecutionToken Token);

    /// <summary>Only the admitted queue writer can supply timing consumers. No additional scheduler is installed.</summary>
    public static void ObserveExecution(string writer, FactoryJobExecutionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (writer != _writer) throw new InvalidOperationException("native-hook-timing-writer-mismatch");
        _executionObserver = observer;
    }

    /// <summary>Cold, once per encountered native class. A rejected measurement stays unavailable, never zero.</summary>
    public static string? PrepareBuildTiming(string nativeType)
    {
        var wrapper = typeof(LG_FactoryJob).Assembly.GetType(nativeType);
        var method = wrapper?.GetMethod("Build", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (wrapper == null || !typeof(LG_FactoryJob).IsAssignableFrom(wrapper) || method == null
            || method.IsAbstract || method.ReturnType != typeof(bool)) return "native-job-build-signature-unavailable";
        if (BuildBindings.TryGetValue(method, out var known)) return known.Refusal;
        BuildBinding binding;
        try
        {
            var callbacks = typeof(FactoryJobs);
            var lease = NativeHookRegistration.DeclareObservation(new("ForgeEngine.FactoryJobs.Build." + wrapper.Name, method,
                callbacks.GetMethod(nameof(BuildBefore), BindingFlags.Static | BindingFlags.NonPublic), null,
                callbacks.GetMethod(nameof(BuildFinished), BindingFlags.Static | BindingFlags.NonPublic), new()));
            binding = new(lease, null);
        }
        catch (Exception error)
        {
            binding = new(null, RecordBuildTimingRefusal(wrapper.Name, error));
        }
        BuildBindings.Add(method, binding);
        return binding.Refusal;
    }

    internal static string RecordBuildTimingRefusal(string nativeType, Exception error)
    {
        if (error is not NotSupportedException
            || error.Message != "short-or-branch-native-body")
            ReportTimingFailure("Build." + nativeType, error);
        return error.Message;
    }

    private static void BuildBefore(LG_FactoryJob __instance, out ExecutionState __state)
    {
        var observer = _executionObserver;
        __state = default;
        try { __state = new(observer, observer?.Enter(__instance) ?? default); }
        catch (Exception error) { ExecutionObserverFailed(error); }
    }

    private static void BuildFinished(bool __result, Exception? __exception, ExecutionState __state)
    {
        try { __state.Observer?.Exit(__state.Token, __result, __exception); }
        catch (Exception error) { ExecutionObserverFailed(error); }
    }

    private static void ExecutionObserverFailed(Exception error)
    {
        _executionObserver = null;
        ReportTimingFailure("BuildTiming", error);
    }

    private static void ReportTimingFailure(string phase, Exception error)
    {
        try { NativeHookRegistration.ObserverFailed("ForgeEngine.FactoryJobs." + phase, error); }
        catch (Exception reporter)
        {
            Console.Error.WriteLine("ForgeEngine.FactoryJobs timing unavailable: " + phase + " " + error.GetType().Name + ": " + error.Message
                + " (observer report failed: " + reporter.GetType().Name + ")");
        }
    }

    internal static void Prepare() => NativeHookRegistration.Declare(Definition);

    // A package registers its algorithm, never another native patch. Keeping the immutable callback for
    // the process also keeps the physical installation stable across package unload and re-admission.
    // The algorithm itself decides whether the active level has a compiled generation plan.
    public static void DeclareGenerator(string writer, FactoryJobGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var declaration = Definition with { Writer = writer };
        if (string.IsNullOrWhiteSpace(writer) || writer.Length > 128 || generator.Target != null
            || !generator.Method.IsStatic || generator.GetInvocationList().Length != 1)
            throw new ArgumentException(NativeHookRegistration.Refusal("native-hook-generator-invalid", declaration));
        if (_generator != null && (_writer != writer || _generator != generator))
            throw new NotSupportedException(NativeHookRegistration.Refusal("native-hook-writer-conflict", declaration));
        NativeHookRegistration.Declare(Definition);
        _writer = writer; _generator = generator;
    }

    public static NativeHookLease Subscribe(string consumer, long worldEpoch, Action<bool> callback)
        => Dequeued.Subscribe(consumer, worldEpoch, callback);

    private static bool Before(LG_Factory __instance, ref bool __result)
        => _generator == null || _generator(__instance, ref __result);

    private static void After(bool __result)
    {
        // Count actual successful dequeues, including a compiled generator that replaced the native
        // body. A refused queue returns false and cannot create a fictitious successful job.
        if (__result) Dequeued.Publish(true, NativeHookRegistration.WorldEpoch);
    }
}
