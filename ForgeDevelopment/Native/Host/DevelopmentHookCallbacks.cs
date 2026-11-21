using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ForgeDevelopment.Native;

// Managed observation only: no detour, signature inference, original invocation,
// or write-back to native arguments/results. Object references are shallow observations;
// this is a contract for trusted fixed callbacks, not a sandbox for arbitrary code.
internal static class DevelopmentHookCallbacks
{
    internal enum Phase { Prefix, Postfix, Finalizer }
    internal sealed record Callback(Phase Phase, MethodInfo Method);
    internal sealed record Failure(MethodBase Target, MethodInfo Callback, Phase Phase, Exception Error);

    internal sealed class Context
    {
        private static readonly ReadOnlyCollection<object?> EmptyArguments = Array.AsReadOnly(Array.Empty<object?>());
        private readonly object?[] _arguments;
        internal object? Instance { get; }
        internal ReadOnlyCollection<object?> Arguments { get; }
        internal object? Result { get; }
        internal bool RunOriginal { get; }
        internal Exception? Exception { get; }
        internal Context(object? instance, object?[] arguments, object? result = null,
            bool runOriginal = true, Exception? exception = null)
        {
            Instance = instance;
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            _arguments = arguments.Length == 0 ? Array.Empty<object?>() : (object?[])arguments.Clone();
            Arguments = _arguments.Length == 0 ? EmptyArguments : Array.AsReadOnly(_arguments);
            Result = result;
            RunOriginal = runOriginal;
            Exception = exception;
        }
        internal object?[] CopyArguments() => _arguments.Length == 0 ? Array.Empty<object?>() : (object?[])_arguments.Clone();
    }

    internal sealed class Invocation
    {
        internal readonly Plan Owner;
        internal readonly object?[] States;
        internal Invocation(Plan owner, int stateCount)
        { Owner = owner; States = stateCount == 0 ? Array.Empty<object?>() : new object?[stateCount]; }
    }

    internal static MethodInfo ResolveCallback(Type type, string name)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name).ToArray();
        if (candidates.Length == 0) throw new MissingMethodException(type.FullName, name);
        if (candidates.Length != 1) throw new AmbiguousMatchException($"Ambiguous callback {type.FullName}.{name}");
        return candidates[0];
    }

    internal sealed class Plan
    {
        private sealed record Bound(Callback Callback, Func<Context, Invocation, object?>[] Arguments, int OutputSlot,
            Action<Context, Invocation, Func<Context, Invocation, object?>[], int> Invoke);
        private static readonly Dictionary<MethodInfo, Action<Context, Invocation, Func<Context, Invocation, object?>[], int>> Calls = new();
        private readonly Bound[] _callbacks;
        private readonly Action<Failure>? _report;
        private readonly int _stateCount;
        internal MethodBase Target { get; }
        private Plan(MethodBase target, Bound[] callbacks, Action<Failure>? report, int stateCount)
        { Target = target; _callbacks = callbacks; _report = report; _stateCount = stateCount; }

        internal static Plan Create(MethodBase target, IEnumerable<Callback> callbacks, Action<Failure>? report = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.ContainsGenericParameters) throw new ArgumentException("Open generic target is not observable.", nameof(target));
            var entries = (callbacks ?? throw new ArgumentNullException(nameof(callbacks))).ToArray();
            var states = new Dictionary<Type, Type>();
            foreach (var callback in entries)
            {
                if (callback == null || callback.Method == null) throw new ArgumentException("Missing callback method.");
                var method = callback.Method;
                if (!Enum.IsDefined(typeof(Phase), callback.Phase) || !method.IsStatic || method.IsAbstract ||
                    method.ContainsGenericParameters || method.DeclaringType == null ||
                    method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false) ||
                    (method.CallingConvention & CallingConventions.VarArgs) != 0)
                    throw Refuse(method, "Callback must be a synchronous closed static method with a valid phase.");
                if (method.ReturnType != typeof(void) &&
                    !(callback.Phase == Phase.Finalizer && method.ReturnType == typeof(Exception)))
                    throw Refuse(method, "Only void callbacks or Exception-returning finalizers are observers.");
                foreach (var p in method.GetParameters())
                {
                    if (p.ParameterType.IsByRef || p.IsOut)
                    {
                        if (p.Name != "__state" || !p.IsOut || !p.ParameterType.IsByRef || callback.Phase != Phase.Prefix)
                            throw Refuse(method, "Writable ref/out parameters are forbidden except prefix out __state.");
                        var stateType = p.ParameterType.GetElementType()!;
                        if (!Boxable(stateType) || !states.TryAdd(method.DeclaringType, stateType))
                            throw Refuse(method, "State must have one boxable prefix producer per declaring type.");
                    }
                    else if (!Boxable(p.ParameterType)) throw Refuse(method, "Unboxable callback parameter.");
                }
            }
            // Native callbacks are bound once when subscribed. An invocation owns
            // its state slots, so nested calls and other threads never share them.
            var stateSlots = states.Keys.Select((type, index) => (type, index)).ToDictionary(p => p.type, p => p.index);
            var parameters = target.GetParameters();
            var bound = entries.Select(callback =>
            {
                var method = callback.Method;
                var stateOutput = -1;
                var outputSlot = -1;
                var getters = method.GetParameters().Select((p, index) =>
                {
                    Type source;
                    Func<Context, Invocation, object?> get;
                    switch (p.Name)
                    {
                        case "__instance":
                            if (target.IsStatic || target.DeclaringType == null) throw Refuse(method, "Static target has no instance.");
                            source = target.DeclaringType; get = (c, _) => c.Instance; break;
                        case "__args": source = typeof(object[]); get = (c, _) => c.CopyArguments(); break;
                        case "__originalMethod": source = target.GetType(); get = (_, _) => target; break;
                        case "__result":
                            source = (target as MethodInfo)?.ReturnType ?? typeof(void);
                            if (source == typeof(void) || !Boxable(source)) throw Refuse(method, "Target has no boxable result.");
                            get = (c, _) => c.Result; break;
                        case "__runOriginal": source = typeof(bool); get = (c, _) => c.RunOriginal; break;
                        case "__exception": source = typeof(Exception); get = (c, _) => c.Exception; break;
                        case "__state":
                            if (!states.TryGetValue(method.DeclaringType!, out source!)) throw Refuse(method, "State has no producer in the same declaring type.");
                            if (p.IsOut) stateOutput = index;
                            else if (callback.Phase == Phase.Prefix) throw Refuse(method, "Prefix state must be out.");
                            var stateType = source;
                            var slot = stateSlots[method.DeclaringType!];
                            if (p.IsOut) outputSlot = slot;
                            var defaultState = stateType.IsValueType ? Activator.CreateInstance(stateType) : null;
                            get = (_, invocation) => invocation.States[slot] ?? defaultState;
                            break;
                        default:
                            var argument = Array.FindIndex(parameters, a => a.Name == p.Name);
                            if (p.Name != null && p.Name.StartsWith("__", StringComparison.Ordinal))
                            {
                                if (!int.TryParse(p.Name.Substring(2), System.Globalization.NumberStyles.None,
                                    System.Globalization.CultureInfo.InvariantCulture, out argument)) argument = -1;
                            }
                            if (argument < 0 || argument >= parameters.Length) throw Refuse(method, "Unbound parameter: " + p.Name);
                            source = parameters[argument].ParameterType;
                            if (source.IsByRef) source = source.GetElementType()!;
                            var position = argument;
                            get = (c, _) => c.Arguments[position]; break;
                    }
                    var destination = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                    if (!Boxable(source) || !destination.IsAssignableFrom(source))
                        throw Refuse(method, "Incompatible binding for " + p.Name);
                    return get;
                }).ToArray();
                return new Bound(callback, getters, outputSlot, CompileCall(method, stateOutput));
            }).ToArray();
            return new Plan(target, bound, report, states.Count);
        }

        // Trace profiles bind the same callback to thousands of native targets.
        // Share its code, while each Bound keeps its own target-specific getters.
        private static Action<Context, Invocation, Func<Context, Invocation, object?>[], int> CompileCall(MethodInfo method, int stateOutput)
        {
            lock (Calls)
            {
                if (Calls.TryGetValue(method, out var existing)) return existing;
                var context = Expression.Parameter(typeof(Context), "context");
                var invocation = Expression.Parameter(typeof(Invocation), "invocation");
                var getters = Expression.Parameter(typeof(Func<Context, Invocation, object?>[]), "getters");
                var outputSlot = Expression.Parameter(typeof(int), "outputSlot");
                var callbackParameters = method.GetParameters();
                var arguments = new Expression[callbackParameters.Length];
                ParameterExpression? state = null;
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i == stateOutput)
                    {
                        state = Expression.Variable(callbackParameters[i].ParameterType.GetElementType()!, "state");
                        arguments[i] = state;
                    }
                    else arguments[i] = Expression.Convert(
                        Expression.Invoke(Expression.ArrayIndex(getters, Expression.Constant(i)), context, invocation), callbackParameters[i].ParameterType);
                }
                var call = Expression.Call(method, arguments);
                Expression body = state == null ? Expression.Block(call, Expression.Empty())
                    : Expression.Block(new[] { state }, call,
                        Expression.Assign(Expression.ArrayAccess(Expression.Field(invocation, nameof(Invocation.States)),
                            outputSlot), Expression.Convert(state, typeof(object))), Expression.Empty());
                var compiled = Expression.Lambda<Action<Context, Invocation, Func<Context, Invocation, object?>[], int>>(
                    body, context, invocation, getters, outputSlot).Compile();
                Calls.Add(method, compiled);
                return compiled;
            }
        }

        internal Invocation Before(Context context)
        {
            var invocation = new Invocation(this, _stateCount);
            Dispatch(Phase.Prefix, context, invocation);
            return invocation;
        }
        internal void After(Context context, Invocation invocation) => Dispatch(Phase.Postfix, context, invocation);
        internal void Finally(Context context, Invocation invocation) => Dispatch(Phase.Finalizer, context, invocation);

        private void Dispatch(Phase phase, Context context, Invocation invocation)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (invocation == null || !ReferenceEquals(invocation.Owner, this))
                throw new ArgumentException("Invocation belongs to a different plan.", nameof(invocation));
            foreach (var bound in _callbacks)
            {
                if (bound.Callback.Phase != phase) continue;
                try
                {
                    // The compiled call discards every return, including finalizers.
                    // No reflection, parameter array or type-key lookup per job.
                    bound.Invoke(context, invocation, bound.Arguments, bound.OutputSlot);
                }
                catch (Exception error)
                {
                    var cause = error is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException! : error;
                    try { _report?.Invoke(new Failure(Target, bound.Callback.Method, phase, cause)); }
                    catch { /* Diagnostic failures must not affect the original or other observers. */ }
                }
            }
        }
        private static bool Boxable(Type type) => type != typeof(void) && !type.IsPointer && !type.IsByRef && !type.IsByRefLike && !type.ContainsGenericParameters;
        private static ArgumentException Refuse(MethodInfo method, string reason) => new($"{method.DeclaringType?.FullName}.{method.Name}: {reason}");
    }
}
