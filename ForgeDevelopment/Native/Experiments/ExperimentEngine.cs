using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ForgeDevelopment.Native;

/// <summary>
/// The unit of work the driver performs. A plain descriptor instead of a coroutine, so the driver can
/// decide per frame whether it still has budget to continue the same run.
/// </summary>
internal sealed record ExperimentAction(ExperimentStep Step, ExperimentTargetSpec Spec, long Frame)
{
    internal string Node => Step.Node;
}

/// <summary>
/// Walks a validated command one action at a time. The engine owns the semantics the game cannot supply:
/// what each step means, that a failure stops the remaining steps, that cleanup still runs afterwards, and
/// that every step reports exactly one record.
/// <para>
/// Progress lives in an explicit block stack rather than an iterator, so a run can be suspended between any
/// two steps when the frame budget is spent and resumed on the next frame without losing its place.
/// </para>
/// </summary>
internal sealed class ExperimentEngine
{
    private const int MaximumActionsPerFrame = 64;

    private readonly ExperimentCommand _command;
    private readonly ExperimentEnvironment _environment;
    private readonly Action<ExperimentOutcome> _report;
    private readonly Func<ExperimentAction, string?>? _guard;
    private readonly Stack<Block> _stack = new();

    private ExperimentTargetSpec _spec;
    private Block _main;
    private Block _cleanup;
    private WaitState? _waiting;
    private sealed record CallReadback(ExperimentAction Action, string Target, double Started, Func<ExperimentCallResult> Poll);
    private CallReadback? _readback;
    private string _blocked = "";
    private bool _finished;
    private bool _aborted, _rejected;

    private sealed class Block
    {
        internal Block(IReadOnlyList<ExperimentStep> steps, bool isRepeat)
        {
            Steps = steps;
            IsRepeat = isRepeat;
        }

        internal IReadOnlyList<ExperimentStep> Steps { get; }
        internal bool IsRepeat { get; }
        internal int Position { get; set; }
        internal int RemainingIterations { get; set; }
        internal bool IsActive { get; set; }

        /// <summary>Frame budget on loan for this block's remaining iterations, restored when the block ends.</summary>
        internal int Credit { get; set; }
    }

    private sealed class WaitState
    {
        internal WaitState(ExperimentAction action, double seconds, long frame)
        {
            Action = action;
            StartedSeconds = seconds;
            StartedFrame = frame;
        }

        internal ExperimentAction Action { get; }
        internal double StartedSeconds { get; }
        internal long StartedFrame { get; }
        internal object? Baseline { get; set; }
        internal bool BaselineRead { get; set; }
    }

    internal ExperimentEngine(ExperimentCommand command, ExperimentTargetSpec spec, ExperimentEnvironment environment,
        Action<ExperimentOutcome> report, Func<ExperimentAction, string?>? guard = null)
    {
        _command = command;
        _spec = spec;
        _environment = environment;
        _report = report;
        _guard = guard;
        Description = spec.Describe();
        _main = new Block(command.Steps, false);
        _cleanup = new Block(command.Cleanup, false);
        _stack.Push(_main);
    }

    internal string Description { get; }
    internal bool Failed { get; private set; }
    internal bool Finished => _finished;
    internal int Total => _command.Steps.Count;
    internal int Done { get; private set; }
    internal string Blocked => _blocked;
    internal ExperimentAction? Pending { get; private set; }
    /// <summary>Terminal command execution status only; it is never a gameplay-validation result.</summary>
    internal ExperimentRunStatus FinalStatus => !_finished
        ? throw new InvalidOperationException("The experiment has not finished its cleanup.")
        : Failed ? ExperimentRunStatus.Failed : _rejected ? ExperimentRunStatus.Rejected
        : _aborted ? ExperimentRunStatus.Aborted : ExperimentRunStatus.Passed;

    /// <summary>Starts a rejected run: the host gate or a target lookup already refused the command.</summary>
    internal void Reject(string reason)
    {
        _rejected = true;
        _blocked = reason;
        _finished = true;
        Report("run", "run", false, reason + " :: " + Description, 0);
    }

    internal void Cancel(string reason)
    {
        if (_finished || _aborted) return;
        _aborted = true;
        // A user stop never erases a real failure, including one that is already cleaning up.
        if (!Failed) _blocked = reason;
        Report("run", "run", false, reason, 0);
        // Cleanup is mandatory even if Escape is pressed during it. Do not discard a waiting or
        // pending cleanup action, restart cleanup, or run its already-completed steps again.
        if (_cleanup.IsActive) return;
        _stack.Clear();
        _waiting = null;
        _readback = null;
        Pending = null;
        _frameCredit = 0;
        _main.IsActive = false;
        if (_command.Cleanup.Count > 0)
        {
            _cleanup.IsActive = true;
            _cleanup.Position = 0;
            _stack.Push(_cleanup);
            return;
        }
        _finished = true;
    }

    /// <summary>
    /// Returns the action to perform now, or null when the driver should come back next frame. Call once per
    /// frame and again after every <see cref="Advance"/>.
    /// </summary>
    internal ExperimentAction? Next(double elapsedMilliseconds, double budgetMilliseconds)
    {
        if (_finished) return null;
        if (Pending != null) return Pending;
        if (_readback != null)
        {
            PollCall();
            return null;
        }
        if (_waiting != null)
        {
            PollWait();
            return null;
        }
        var processed = 0;
        while (processed++ < MaximumActionsPerFrame)
        {
            if (_stack.Count == 0)
            {
                _finished = true;
                return null;
            }
            var block = _stack.Peek();
            if (block.Position >= block.Steps.Count)
            {
                _stack.Pop();
                if (block.IsRepeat)
                {
                    // What is left of the loop's loan goes back; the rest was spent on its frames.
                    _frameCredit += Math.Max(0, block.Credit);
                    if (block.RemainingIterations > 0)
                    {
                        block.RemainingIterations--;
                        block.Position = 0;
                        _stack.Push(block);
                        continue;
                    }
                }
                if (ReferenceEquals(block, _main))
                {
                    _main.IsActive = false;
                    if (_command.Cleanup.Count == 0)
                    {
                        _finished = true;
                        return null;
                    }
                    _cleanup.IsActive = true;
                    _cleanup.Position = 0;
                    _stack.Push(_cleanup);
                }
                continue;
            }

            var step = block.Steps[block.Position];
            if (step.Kind == ExperimentStepKind.Repeat)
            {
                block.Position++;
                if (step.Times < 1) continue;
                // The whole loop is charged to the frame budget up front and refunded when it ends, so a
                // tight loop is paced by frames instead of running to completion inside one frame.
                var credit = FrameCost(step.Body, _environment.TicksPerSecond) * step.Times;
                _frameCredit += credit;
                _stack.Push(new Block(step.Body, true) { RemainingIterations = step.Times - 1, Credit = credit });
                continue;
            }
            if (step.Kind == ExperimentStepKind.Wait)
            {
                block.Position++;
                _waiting = new WaitState(new ExperimentAction(step, SpecFor(step), _environment.Frame), _environment.Seconds, _environment.Frame);
                PollWait();
                return null;
            }
            if (NeedsFrame(step))
            {
                // A loop iteration spends one frame of its loan; the credit is what makes the loop wait.
                if (_frameCredit > 0) { _frameCredit--; }
                else if (elapsedMilliseconds >= budgetMilliseconds) return null;
                if (block.IsRepeat && block.Credit > 0) block.Credit--;
            }
            else if (elapsedMilliseconds >= budgetMilliseconds)
            {
                return null;
            }
            block.Position++;
            Pending = new ExperimentAction(step, SpecFor(step), _environment.Frame);
            return Pending;
        }
        return null;
    }

    /// <summary>Hands a finished action back so the engine records it and continues.</summary>
    internal void Advance(ExperimentAction action)
    {
        if (_finished || !ReferenceEquals(action, Pending)) return;
        Pending = null;
        if (_guard?.Invoke(action) is { } refusal) { Fail(action, refusal, 0); return; }
        switch (action.Step.Kind)
        {
            case ExperimentStepKind.Call:
                {
                    var target = _environment.Resolve(action.Spec);
                    if (!target.Ok) { Fail(action, target.Error, 0); return; }
                    var started = Stopwatch.GetTimestamp();
                    var result = _environment.Call(target, action.Step);
                    var elapsed = Elapsed(started);
                    if (!result.Ok) { Fail(action, result.Error, elapsed); return; }
                    if (result.Readback != null)
                    {
                        _environment.LastResult = null; // Never carry an old-world native object into the new lease.
                        _readback = new CallReadback(action, target.Describe, _environment.Seconds, result.Readback);
                        return;
                    }
                    _environment.LastResult = result.Returned;
                    Report("call", action.Node, true, target.Describe + " -> " + result.Value, elapsed);
                    break;
                }
            case ExperimentStepKind.Set:
                {
                    var target = _environment.Resolve(action.Spec);
                    if (!target.Ok) { Fail(action, target.Error, 0); return; }
                    var started = Stopwatch.GetTimestamp();
                    var result = _environment.Write(target, action.Step.Path, action.Step.Literal ?? default);
                    var elapsed = Elapsed(started);
                    if (!result.Ok) { Fail(action, result.Error, elapsed); return; }
                    Report("set", action.Node, true, target.Describe + "." + action.Step.Path + " = " + ExperimentValue.FormatJson(action.Step.Literal ?? default), elapsed);
                    break;
                }
            case ExperimentStepKind.Read:
                {
                    var target = _environment.Resolve(action.Spec);
                    if (!target.Ok) { Fail(action, target.Error, 0); return; }
                    var started = Stopwatch.GetTimestamp();
                    var values = new List<string>(action.Step.Paths.Count);
                    var ok = true;
                    foreach (var path in action.Step.Paths)
                    {
                        var read = _environment.Read(target, path);
                        if (read.Ok) values.Add(path + "=" + ExperimentValue.Format(read.Value));
                        else { values.Add(path + "=<error: " + read.Error + ">"); ok = false; }
                    }
                    if (action.Step.Screenshot) values.Add("screenshot=" + _environment.Screenshot("read-" + action.Node));
                    var elapsed = Elapsed(started);
                    if (!ok) { Fail(action, target.Describe + " :: " + string.Join("; ", values), elapsed); return; }
                    Report("read", action.Node, true, target.Describe + " :: " + string.Join("; ", values), elapsed);
                    break;
                }
            case ExperimentStepKind.Screenshot:
                {
                    var started = Stopwatch.GetTimestamp();
                    var shot = _environment.Screenshot(action.Step.Reason.Length > 0 ? action.Step.Reason : action.Node);
                    Report("screenshot", action.Node, true, shot, Elapsed(started));
                    break;
                }
            case ExperimentStepKind.Clone:
                {
                    var target = _environment.Resolve(action.Spec);
                    if (!target.Ok) { Fail(action, target.Error, 0); return; }
                    var started = Stopwatch.GetTimestamp();
                    var result = _environment.Clone(target, action.Step.Path, action.Step.CloneParent, action.Step.CloneText);
                    var elapsed = Elapsed(started);
                    if (!result.Ok) { Fail(action, result.Error, elapsed); return; }
                    _environment.LastResult = result.Value;
                    Report("clone", action.Node, true, result.Describe, elapsed);
                    break;
                }
            case ExperimentStepKind.WorldEvent:
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = _environment.WorldEvent(action.Step.Literal ?? default);
                    if (!result.Ok) { Fail(action, result.Error, Elapsed(started)); return; }
                    Report("worldEvent", action.Node, true, result.Value, Elapsed(started));
                    break;
                }
        }
        if (!_cleanup.IsActive) Done++;
    }

    private void PollCall()
    {
        var readback = _readback!;
        if (_guard?.Invoke(readback.Action) is { } refusal) { Fail(readback.Action, refusal, 0); return; }
        if (_environment.Seconds - readback.Started >= _command.TimeoutSeconds)
        {
            Fail(readback.Action, "experiment.native-readback-timeout", 0);
            return;
        }
        ExperimentCallResult result;
        try { result = readback.Poll(); }
        catch (Exception error) { Fail(readback.Action, error.Message, 0); return; }
        if (!result.Ok) { Fail(readback.Action, result.Error, 0); return; }
        if (result.Readback != null) { _readback = readback with { Poll = result.Readback }; return; }
        _readback = null;
        _environment.LastResult = result.Returned;
        Report("call", readback.Action.Node, true, readback.Target + " -> " + result.Value,
            (_environment.Seconds - readback.Started) * 1000);
        if (!_cleanup.IsActive) Done++;
    }

    private void PollWait()
    {
        var waiting = _waiting;
        if (waiting == null) return;
        if (_guard?.Invoke(waiting.Action) is { } refusal) { Fail(waiting.Action, refusal, 0); _waiting = null; return; }
        var wait = waiting.Action.Step.Wait;
        var started = Stopwatch.GetTimestamp();
        if (wait.Frames is { } frames)
        {
            if (_environment.Frame - waiting.StartedFrame < frames) return;
            FinishWait(waiting, "waited " + frames.ToString(CultureInfo.InvariantCulture) + " frames", Elapsed(started));
            return;
        }
        if (wait.Seconds is { } seconds)
        {
            if (_environment.Seconds - waiting.StartedSeconds < seconds) return;
            FinishWait(waiting, "waited " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s", Elapsed(started));
            return;
        }
        if (wait.TraceType.Length > 0)
        {
            if (_environment.TraceSeen(wait.TraceType, wait.TraceMethod))
            {
                FinishWait(waiting, "trace " + wait.TraceType + "." + wait.TraceMethod + " observed", Elapsed(started));
                return;
            }
            if (_environment.Frame - waiting.StartedFrame >= wait.TimeoutFrames)
            {
                Fail(waiting.Action, "trace " + wait.TraceType + "." + wait.TraceMethod + " was not observed within " + wait.TimeoutFrames + " frames", Elapsed(started));
                _waiting = null;
            }
            return;
        }
        if (wait.Path.Length > 0)
        {
            var target = _environment.Resolve(waiting.Action.Spec);
            if (!target.Ok)
            {
                Fail(waiting.Action, target.Error, 0);
                _waiting = null;
                return;
            }
            if (wait.Changed && !waiting.BaselineRead)
            {
                // Without a baseline there is nothing to compare against: an unreadable start would make the first readable value count as a change.
                var baseline = _environment.Read(target, wait.Path);
                if (!baseline.Ok)
                {
                    Fail(waiting.Action, "path " + wait.Path + " has no starting value to wait for a change from: " + baseline.Error, Elapsed(started));
                    _waiting = null;
                    return;
                }
                waiting.Baseline = baseline.Value;
                waiting.BaselineRead = true;
            }
            var result = _environment.WaitForPath(target, wait.Path, wait.Changed, waiting.Baseline);
            if (result.Error.Length > 0)
            {
                Fail(waiting.Action, result.Error, Elapsed(started));
                _waiting = null;
                return;
            }
            if (result.Done)
            {
                FinishWait(waiting, result.Detail, Elapsed(started));
                return;
            }
            if (_environment.Frame - waiting.StartedFrame >= wait.TimeoutFrames)
            {
                Fail(waiting.Action, "path " + wait.Path + " did not change within " + wait.TimeoutFrames + " frames", Elapsed(started));
                _waiting = null;
            }
            return;
        }
        FinishWait(waiting, "no condition", Elapsed(started));
    }

    private void FinishWait(WaitState waiting, string detail, double milliseconds)
    {
        _waiting = null;
        Report("wait", waiting.Action.Node, true, detail, milliseconds);
    }

    private void Fail(ExperimentAction action, string error, double milliseconds)
    {
        Failed = true;
        _blocked = error;
        Report(action.Step.Kind.ToString().ToLowerInvariant(), action.Node, false, error, milliseconds);
        _waiting = null;
        _readback = null;
        Pending = null;
        _stack.Clear();
        _frameCredit = 0;
        if (_cleanup.IsActive || _command.Cleanup.Count == 0)
        {
            _finished = true;
            return;
        }
        _main.IsActive = false;
        _cleanup.IsActive = true;
        _cleanup.Position = 0;
        _stack.Push(_cleanup);
    }

    private void Report(string kind, string node, bool ok, string message, double milliseconds)
        => _report(new ExperimentOutcome(kind, ok, node + " " + message, milliseconds));

    private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    private static bool NeedsFrame(ExperimentStep step) => step.Kind is ExperimentStepKind.Call or ExperimentStepKind.WorldEvent;

    /// <summary>
    /// A step's own target wins over the command's, so one command can touch two objects. The reserved name
    /// <c>result</c> addresses whatever the previous call returned.
    /// </summary>
    private ExperimentTargetSpec SpecFor(ExperimentStep step)
    {
        if (step.Target == "result") return ExperimentTargetSpec.ForResult;
        return step.Target.Length != 0 && ExperimentTargetSpec.TryParse(step.Target, out var spec) ? spec : _spec;
    }

    private int _frameCredit;

    internal static int FrameCost(IReadOnlyList<ExperimentStep> steps, double? ticksPerSecond)
    {
        var total = 0;
        foreach (var step in steps)
            switch (step.Kind)
            {
                case ExperimentStepKind.Wait:
                    // An authored wait in seconds is a span of the world's own ticks, so it converts at the rate the
                    // environment reports. An environment that cannot report one falls back to the wait's frame
                    // bound rather than being charged at a rate nobody declared.
                    total += step.Wait.Frames ?? (step.Wait.Seconds is { } seconds && ticksPerSecond is { } rate
                        ? Math.Max(1, (int)Math.Ceiling(seconds * rate))
                        : Math.Max(1, step.Wait.TimeoutFrames));
                    break;
                case ExperimentStepKind.Repeat:
                    total += FrameCost(step.Body, ticksPerSecond) * step.Times;
                    break;
                case ExperimentStepKind.Call:
                case ExperimentStepKind.WorldEvent:
                    total += 1;
                    break;
            }
        return total;
    }
}
