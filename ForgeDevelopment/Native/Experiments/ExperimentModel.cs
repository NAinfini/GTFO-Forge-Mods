using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>
/// A command file under <c>BepInEx/ForgeDevelopment/commands/</c>. Parsing keeps every literal as
/// JSON; values become CLR objects only when a step runs against a real parameter, field or property,
/// so an unknown or mistyped literal is reported against the step that needed it.
/// </summary>
internal sealed class ExperimentCommand
{
    internal string Id { get; init; } = "";
    internal string Title { get; init; } = "";
    internal IReadOnlyList<string> Points { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();
    internal ExperimentHost Host { get; init; } = ExperimentHost.Any;
    internal string Target { get; init; } = "";
    internal int TimeoutSeconds { get; init; } = ExperimentParser.DefaultTimeoutSeconds;
    internal ExperimentStage Before { get; init; } = ExperimentStage.Empty;
    internal IReadOnlyList<ExperimentStep> Steps { get; init; } = Array.Empty<ExperimentStep>();
    internal IReadOnlyList<ExperimentStep> Cleanup { get; init; } = Array.Empty<ExperimentStep>();
    internal IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    internal string Source { get; init; } = "";

    internal bool Valid => Problems.Count == 0;
}

internal enum ExperimentHost { Any, Host, Client }

/// <summary>One step. Exactly one payload key is set; <c>Read</c>/<c>Write</c> stand for the step's sole key.</summary>
internal sealed class ExperimentStep
{
    internal ExperimentStepKind Kind { get; init; }
    internal string Name { get; init; } = "";
    internal IReadOnlyList<JsonElement> Arguments { get; init; } = Array.Empty<JsonElement>();
    internal IReadOnlyList<string> ArgumentNames { get; init; } = Array.Empty<string>();
    internal string Path { get; init; } = "";
    internal JsonElement? Literal { get; init; }
    internal IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    internal bool Screenshot { get; init; }
    internal string Reason { get; init; } = "";

    /// <summary>Per-step target override; empty means the command's own target. Parsed at load, so a typo
    /// fails the file instead of the run.</summary>
    internal string Target { get; init; } = "";

    /// <summary><c>call</c> with <c>result: true</c> binds the first argument to what the previous step
    /// returned, which is how a clone or a created object is handed back to the game for cleanup.</summary>
    internal bool ResultArgument { get; init; }
    internal ExperimentWait Wait { get; init; } = ExperimentWait.None;
    internal IReadOnlyList<ExperimentStep> Body { get; init; } = Array.Empty<ExperimentStep>();
    internal int Times { get; init; }
    internal string Node { get; init; } = "";

    /// <summary><c>clone</c>: the member path of the object to duplicate, its new parent, and the text to set
    /// when the copy has a TMP component.</summary>
    internal string CloneParent { get; init; } = "";
    internal string CloneText { get; init; } = "";
}

internal enum ExperimentStepKind { Call, Set, Read, Wait, Screenshot, WorldEvent, Repeat, Clone }

internal sealed class ExperimentWait
{
    internal static readonly ExperimentWait None = new();

    /// <summary>Null when the step does not wait on frames; otherwise the frame count, including a bare integer.</summary>
    internal int? Frames { get; init; }
    internal double? Seconds { get; init; }
    internal string TraceType { get; init; } = "";
    internal string TraceMethod { get; init; } = "";
    internal string Path { get; init; } = "";
    internal bool Changed { get; init; }
    internal int TimeoutFrames { get; init; }

    internal bool IsEmpty => Frames == null && Seconds == null && TraceType.Length == 0 && Path.Length == 0;
}

/// <summary>Stage metadata shared by <c>before</c> and the command itself.</summary>
internal sealed class ExperimentStage
{
    internal static readonly ExperimentStage Empty = new();

    internal IReadOnlyList<string> Snapshot { get; init; } = Array.Empty<string>();
    internal bool Screenshot { get; init; }
}

internal enum ExperimentRunStatus { NotRun, Passed, Failed, Rejected, Aborted }

internal sealed record ExperimentOutcome(string Kind, bool Ok, string Message, double Milliseconds);
