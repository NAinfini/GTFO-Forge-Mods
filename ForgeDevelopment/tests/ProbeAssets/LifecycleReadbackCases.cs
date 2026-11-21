using System.Text.Json;
using ForgeDevelopment.Native;

internal static class LifecycleReadbackCases
{
    internal static void Run(Action<bool, string> check)
    {
        void Refuse(Action action, string name)
        {
            try { action(); }
            catch (InvalidOperationException error)
            { check(error.Message == "experiment.read-world-changed", name); return; }
            check(false, name);
        }
        var context = new ExperimentWorldReadback(4);
        Refuse(() => context.Validate(5), "ordinary native call still fails an unexpected world change");
        context.Begin(false);
        check(!context.LevelCleanup(), "drop does not absorb unrelated level cleanup");
        check(!context.Readback("Lobby", false), "drop acknowledgement without the next world is not a readback");
        context.Validate(5);
        check(context.Readback("Generating", true), "EXT_StartExpedition reads the declared next generation");
        Refuse(() => context.Validate(6), "the completed drop cannot accept a second world change");
        context = new ExperimentWorldReadback(8);
        context.Begin(true);
        context.Validate(9);
        check(!context.Readback("Lobby", false), "lobby status without native level cleanup remains pending");
        check(context.LevelCleanup() && !context.Readback("Lobby", true), "return owns native cleanup but still waits for floor removal");
        check(context.Readback("Lobby", false), "return completes only after cleanup, next world and cleaned lobby agree");
        context = new ExperimentWorldReadback(512);
        context.Begin(true);
        context.Validate(768);
        check(context.LevelCleanup() && context.Readback("Lobby", false), "in a session the next world is the next span (512 to 768), not number + 1");
        context = new ExperimentWorldReadback(20);
        context.BeginLeave();
        context.Validate(21);
        check(!context.Readback("Lobby", false), "leaving the hub is not read back while the lobby remains");
        check(context.LevelCleanup() && context.Readback("NoLobby", false), "leaving the hub owns its cleanup and completes in NoLobby with no floor");

        ExperimentTargetSpec.TryParse("static:GameStateManager", out var spec);
        var environment = new Environment();
        var outcomes = new List<ExperimentOutcome>();
        var command = new ExperimentCommand
        {
            Target = spec.Raw,
            TimeoutSeconds = 2,
            Steps = new[] { new ExperimentStep { Kind = ExperimentStepKind.Call, Name = "native-transition" } }
        };
        var engine = new ExperimentEngine(command, spec, environment, outcomes.Add);
        engine.Advance(engine.Next(0, 2)!);
        engine.Next(0, 2);
        check(environment.Calls == 1 && outcomes.Count == 0, "issued lifecycle call waits without reissuing or reporting a false pass");
        environment.Ready = true;
        environment.Clock = .5;
        engine.Next(0, 2);
        engine.Next(0, 2);
        check(engine.Finished && engine.FinalStatus == ExperimentRunStatus.Passed && outcomes.Single().Message.Contains("native-readback"),
            "independent native readback becomes exactly one successful call record");
        environment = new Environment(); outcomes.Clear();
        engine = new ExperimentEngine(command, spec, environment, outcomes.Add);
        engine.Advance(engine.Next(0, 2)!);
        environment.Clock = 3;
        engine.Next(0, 2);
        check(engine.Finished && engine.FinalStatus == ExperimentRunStatus.Failed
            && outcomes.Single().Message.Contains("native-readback-timeout"), "missing lifecycle readback fails at its bounded timeout");
    }

    private sealed class Environment : ExperimentEnvironment
    {
        internal double Clock;
        internal int Calls;
        internal bool Ready;
        public long Frame => (long)(Clock * 60);
        public double Seconds => Clock;
        public double? TicksPerSecond => 30;
        public bool IsHost => true;
        public object? LastResult { get; set; }
        public ExperimentTargetResult Resolve(ExperimentTargetSpec spec) => ExperimentTargetResult.One(spec.Raw, this);
        public ExperimentCallResult Call(ExperimentTargetResult target, ExperimentStep step) { Calls++; return Poll(); }
        private ExperimentCallResult Poll() => Ready ? ExperimentCallResult.Done("native-readback", null) : ExperimentCallResult.Await(Poll);
        public ExperimentValue.ReadResult Read(ExperimentTargetResult target, string path) => throw new NotSupportedException();
        public ExperimentValue.WriteResult Write(ExperimentTargetResult target, string path, JsonElement literal) => throw new NotSupportedException();
        public ExperimentCloneResult Clone(ExperimentTargetResult target, string path, string parent, string text) => throw new NotSupportedException();
        public ExperimentWaitResult WaitForPath(ExperimentTargetResult target, string path, bool changed, object? baseline) => throw new NotSupportedException();
        public ExperimentCallResult WorldEvent(JsonElement fields) => throw new NotSupportedException();
        public string Screenshot(string reason) => throw new NotSupportedException();
        public bool TraceSeen(string typeName, string methodName) => false;
    }
}

// Only the game's value formatting boundary is doubled; the production parser and engine are linked by the project.
namespace ForgeDevelopment.Native
{
    internal static class ExperimentValue
    {
        internal readonly record struct ReadResult(bool Ok, object? Value, string Error);
        internal readonly record struct WriteResult(bool Ok, string Error);
        internal static string Format(object? value) => value?.ToString() ?? "null";
        internal static string FormatJson(JsonElement value) => value.GetRawText();
    }
}
