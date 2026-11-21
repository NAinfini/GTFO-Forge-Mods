using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>
/// The game-facing half of a run: resolving targets, calling into live objects, waiting on the frame clock
/// and capturing screenshots. Kept behind an interface so the execution semantics can be tested against
/// doubles instead of the running game.
/// </summary>
internal interface ExperimentEnvironment
{
    long Frame { get; }
    double Seconds { get; }
    /// <summary>The rate this run's frames are counted at, in frames per second: one frame of this engine is one
    /// host <c>FixedUpdate</c>, so it is the world's own tick rate (contract §3.2 时间单位) and the one rate a
    /// step's authored wait in seconds converts through. It is null when this environment cannot read a rate; the
    /// engine then falls back to the wait's own frame bound instead of converting at a rate nobody declared.</summary>
    double? TicksPerSecond { get; }
    bool IsHost { get; }

    /// <summary>The value of the last successful call, addressable as target <c>result</c>.</summary>
    object? LastResult { get; set; }

    ExperimentTargetResult Resolve(ExperimentTargetSpec spec);
    ExperimentCallResult Call(ExperimentTargetResult target, ExperimentStep step);
    ExperimentValue.ReadResult Read(ExperimentTargetResult target, string path);
    ExperimentValue.WriteResult Write(ExperimentTargetResult target, string path, JsonElement literal);

    /// <summary>
    /// Duplicates <paramref name="path"/> under <paramref name="parent"/> and returns the copy. Implementing
    /// environments may hand the same object back as <see cref="LastResult"/>, so a command can address it
    /// as target <c>result</c> afterwards.
    /// </summary>
    ExperimentCloneResult Clone(ExperimentTargetResult target, string path, string parent, string text);
    ExperimentWaitResult WaitForPath(ExperimentTargetResult target, string path, bool changed, object? baseline);

    /// <summary>Builds a WardenObjectiveEventData from the given fields and hands it to WorldEventManager.</summary>
    ExperimentCallResult WorldEvent(JsonElement fields);

    string Screenshot(string label);

    /// <summary>True when the tracer has observed the call since the run started.</summary>
    bool TraceSeen(string typeName, string methodName);
}

internal readonly record struct ExperimentTargetResult(string Describe, object? Value, IReadOnlyList<object?>? Many, string Error)
{
    internal static ExperimentTargetResult One(string describe, object? value) => new(describe, value, null, "");
    internal static ExperimentTargetResult Set(string describe, IReadOnlyList<object?> many) => new(describe, null, many, "");
    internal static ExperimentTargetResult Failed(string error) => new("", null, null, error);

    internal bool Ok => Error.Length == 0;
    internal bool IsSet => Many != null;
    internal int Count => Many?.Count ?? (Value == null ? 0 : 1);
}

internal readonly record struct ExperimentCallResult(bool Ok, string Value, string Error, object? Returned)
{
    internal Func<ExperimentCallResult>? Readback { get; init; } = null;
    internal static ExperimentCallResult Done(string value, object? returned) => new(true, value, "", returned);
    internal static ExperimentCallResult Failed(string error) => new(false, "", error, null);
    internal static ExperimentCallResult Await(Func<ExperimentCallResult> readback) => new(true, "", "", null) { Readback = readback };
}

internal readonly record struct ExperimentWaitResult(bool Done, string Detail, string Error)
{
    internal static ExperimentWaitResult Complete(string detail) => new(true, detail, "");
    internal static ExperimentWaitResult Pending(string detail) => new(false, detail, "");
    internal static ExperimentWaitResult Failed(string error) => new(false, "", error);
}

internal readonly record struct ExperimentCloneResult(bool Ok, string Describe, object? Value, string Error)
{
    internal static ExperimentCloneResult Done(string describe, object? value) => new(true, describe, value, "");
    internal static ExperimentCloneResult Failed(string error) => new(false, "", null, error);
}
