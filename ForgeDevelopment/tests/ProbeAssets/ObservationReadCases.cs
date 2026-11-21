using ForgeDevelopment.Native;

internal static class ObservationReadCases
{
    private sealed class NativeState { public int Value => 17; public object? Optional => null; }
    private class Manager
    {
        internal static int GetterReads;
        public static NativeState Current { get { GetterReads++; return new(); } }
        public static int Counter = 0;
        public static object Throwing => throw new InvalidOperationException("static getter failed");
        public int InstanceOnly => 1;
    }
    private sealed class DerivedManager : Manager { }
    private sealed class GuardedProbe : IRecValueProbe
    {
        public string? NativeReadUnavailable(object value) => value is NativeState || value is Type type && typeof(Manager).IsAssignableFrom(type)
            ? RecObservation.NativeReadUnavailable() : null;
        public bool TryInstanceId(object value, out long id) { id = 0; return false; }
        public bool IsDestroyed(object value) => false;
        public bool IsUnityObject(object value) => false;
        public IntPtr NativeClass(object value) => IntPtr.Zero;
        public string? NativeClassName(IntPtr value) => null;
        public Func<object, object?>? ResolveNativeMember(IntPtr value, string name) => null;
        public bool Handles(Type type) => false;
        public void Write(RecReflect.RecWriter writer, object value, int depth) => throw new NotSupportedException();
    }
    internal static void Run(Action<bool, string> check)
    {
        var previous = RecReflect.Probe;
        RecReflect.Probe = new GuardedProbe();
        try
        {
            check(ExperimentObservationRead.Read(new NativeState(), "Value").Error.Contains("target-lease-required"),
                "unleased native reads remain explicitly refused");
            check(ExperimentObservationRead.Read(typeof(DerivedManager), "Current.Value").Error.Contains("target-lease-required"),
                "static-to-instance paths cannot bypass the native lease");
            check(Manager.GetterReads == 0 && ExperimentObservationRead.Read(typeof(DerivedManager), "Counter").Error.Contains("target-lease-required"),
                "static native getter/field is refused before it executes without a lease");
            var validations = 0;
            using (RecObservation.EnterReadLease(() => validations++))
            {
                var read = ExperimentObservationRead.Read(typeof(DerivedManager), "Current.Value");
                check(read.Ok && read.Value is 17 && validations > 0, "inherited static Current property reaches its leased native instance");
                var incomplete = ExperimentObservationRead.ReadMany(new object?[] { new NativeState(), new object() }, "Value", value => value!.ToString()!);
                check(!incomplete.Ok && incomplete.Error.Contains("#1") && incomplete.Error.Contains("missing-member"),
                    "one failed target fails the collection command");
                check(!ExperimentObservationRead.ReadMany(Array.Empty<object?>(), "Value", _ => "").Ok, "empty target collection cannot pass");
                check(RecObservation.TryEnter(), "hook fixture enters the existing observer boundary");
                try
                {
                    check(ExperimentObservationRead.Read(new NativeState(), "Value").Error.Contains("unverified-trace-object"),
                    "a valid experiment lease does not grant hook arguments read permission");
                }
                finally { RecObservation.Exit(); }
            }
            check(RecObservation.NativeReadUnavailable() == "target-lease-required", "native read permission does not escape the synchronous slice");
        }
        finally { RecReflect.Probe = previous; }
    }
}

namespace ForgeDevelopment.Native
{
    // No diagnostic file IO is needed by these source-linked reader tests.
    internal static class RecSession { internal static void WriteDocument(string name, byte[] content) { } }
}
