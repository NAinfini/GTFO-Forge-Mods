using ForgeDevelopment.Native;
using UnityEngine;

internal static class LoadedComponentCases
{
    private sealed class Zone : Component { internal int ID = 42; }

    internal static void Run(Action<bool, string> check)
    {
        var zone = new Zone();
        var wrapper = new UnityEngine.Object(zone);
        UnityEngine.Object.Loaded = new[] { wrapper, null, new UnityEngine.Object(new Zone { Alive = false }) };
        var found = ExperimentTargeting.ComponentsOf(typeof(Zone)).ToArray();
        check(found.Length == 1 && ReferenceEquals(found[0], zone), "native component survives wrapping and destroyed/null objects are excluded");
        UnityEngine.Object.Loaded = Enumerable.Range(0, 45).Select(_ => new UnityEngine.Object(new Zone())).ToArray();
        check(ExperimentTargeting.ComponentsOf(typeof(Zone)).Count() == 45, "loaded object enumeration is complete beyond 32 instances");
        UnityEngine.Object.Loaded = Array.Empty<UnityEngine.Object>();
        var original = new InvalidOperationException("native query failed");
        UnityEngine.Object.QueryFailure = original;
        try { ExperimentTargeting.ComponentsOf(typeof(Zone)).ToArray(); check(false, "query failure must propagate"); }
        catch (InvalidOperationException error) { check(ReferenceEquals(error, original), "query failure is not rewritten as an absent object"); }
        finally { UnityEngine.Object.QueryFailure = null; }
    }
}

// Only the native query/cast boundary is doubled. The production query bridge above is source linked.
namespace Il2CppInterop.Runtime
{
    internal static class Il2CppType { internal static Type Of<T>() => typeof(T); }
}
namespace UnityEngine
{
    internal class Object
    {
        private readonly Object _native;
        internal bool Alive = true;
        internal static Object?[] Loaded = Array.Empty<Object>();
        internal static Type? QueriedType;
        internal static Exception? QueryFailure;
        internal Object() { _native = this; }
        internal Object(Object native) { _native = native; }
        internal T Cast<T>() where T : Object => _native as T ?? throw new InvalidCastException();
        internal static Object?[] FindObjectsOfType(Type type)
        {
            QueriedType = type;
            if (QueryFailure != null) throw QueryFailure;
            return Loaded;
        }
        public static bool operator ==(Object? left, Object? right)
            => (ReferenceEquals(left, null) || !left._native.Alive) && (ReferenceEquals(right, null) || !right._native.Alive)
                || ReferenceEquals(left?._native, right?._native);
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? value) => value is Object other && this == other;
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_native);
    }
    internal class Component : Object { }
}
