using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// Only managed, already-read values enter this recorder. Native callbacks retain no game objects.
internal sealed class PopulationBudgetFields
{
    internal readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string> Reads = new(StringComparer.Ordinal);
    internal bool Complete => Reads.Values.All(value => value is "read" or "explicit-null");
    internal void Read(string name, Func<object?> read)
    {
        try
        {
            var value = read();
            if (value == null) { Reads[name] = "unavailable"; return; }
            if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
            { Reads[name] = "non-finite"; return; }
            Values[name] = value; Reads[name] = "read";
        }
        catch (Exception error) { Reads[name] = "read-error:" + error.GetType().Name; }
    }
    internal void Missing(string name, string reason) => Reads[name] = reason;
    internal void Write(Utf8JsonWriter json)
    {
        json.WritePropertyName("values"); JsonSerializer.Serialize(json, Values);
        json.WritePropertyName("reads"); JsonSerializer.Serialize(json, Reads);
    }
}

internal sealed class PopulationBudgetCalls
{
    internal const string Schema = "forge.population-budget.v1";
    internal const int MaxDepth = 64, MaxThreads = 16, MaxCallsPerSecond = 2048;
    internal sealed class Frame
    {
        internal long CallId, ParentCallId, Epoch, WorldEpoch;
        internal int ThreadId;
        internal string Method = "", Instance = "", Group = "", Population = "", Context = "";
        internal long GroupLife;
        internal bool Admitted, BeforeComplete, Closed, DepthDenied, Returned, RanOriginal, Correlated;
        internal PopulationBudgetFields? After;
        internal object? Result;
    }
    private readonly object _gate = new();
    private readonly Dictionary<int, List<Frame>> _threads = new();
    private readonly Action<string, Action<Utf8JsonWriter>> _write;
    private readonly Func<long> _milliseconds;
    private long _nextCall, _epoch, _world, _window, _windowCalls, _lost, _interrupted;
    private string _context = "";
    private bool _active;
    internal PopulationBudgetCalls(Action<string, Action<Utf8JsonWriter>> write, Func<long>? milliseconds = null)
    { _write = write; _milliseconds = milliseconds ?? (() => Environment.TickCount64); }
    internal long Epoch { get { using (RecGate.Enter(_gate, "recorder.lock-timeout")) return _epoch; } }
    internal void Reset(string context, long world, string reason, bool active)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout"))
        {
            _interrupted += _threads.Values.Sum(stack => stack.Count);
            _threads.Clear(); _epoch++; _context = context; _world = world; _active = active;
            _window = 0; _windowCalls = 0;
            Status(reason);
        }
    }
    internal void EnsureContext(string context, long world)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout")) if (_context != context || _world != world) Reset(context, world, "context-changed", _active);
    }
    internal Frame? Begin(string method, string instance, string group, long groupLife, string population,
        PopulationBudgetFields before, bool selection = false, int? threadId = null)
        => Begin(method, instance, group, groupLife, population, () => before, selection, threadId);
    internal Frame? Begin(string method, string instance, string group, long groupLife, string population,
        Func<PopulationBudgetFields> readBefore, bool selection = false, int? threadId = null)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout"))
        {
            if (!_active) return null;
            var thread = threadId ?? Environment.CurrentManagedThreadId;
            if (!_threads.TryGetValue(thread, out var stack))
            {
                if (_threads.Count >= MaxThreads) { Loss("thread-cap"); return null; }
                _threads[thread] = stack = new List<Frame>();
            }
            var parent = stack.Count == 0 ? null : stack[^1];
            var frame = new Frame
            {
                CallId = ++_nextCall,
                ParentCallId = parent?.CallId ?? 0,
                Epoch = _epoch,
                WorldEpoch = _world,
                Context = _context,
                ThreadId = thread,
                Method = method,
                Instance = instance,
                Group = group,
                GroupLife = groupLife,
                Population = population
            };
            if (stack.Count >= MaxDepth || parent?.DepthDenied == true)
            {
                // Do not push an unbounded stack. A rejected nested call cannot inherit an older caller.
                frame.DepthDenied = true; Loss("depth-cap"); return frame;
            }
            var second = _milliseconds() / 1000;
            if (_window != second) { _window = second; _windowCalls = 0; }
            frame.Admitted = ++_windowCalls <= MaxCallsPerSecond && parent?.Admitted != false;
            var before = new PopulationBudgetFields();
            if (frame.Admitted)
            {
                try { before = readBefore(); }
                catch (Exception error) { before.Missing("observer", "read-error:" + error.GetType().Name); }
                if (selection)
                {
                    var caller = stack.LastOrDefault(value => value.Method == "Enemies.EnemyGroupScoreSpawnData.AddEnemyData");
                    frame.Correlated = caller != null && caller.Population.Length != 0 && caller.Population == population
                        && caller.Group.Length != 0 && caller.GroupLife != 0 && caller.Admitted && caller.BeforeComplete;
                    if (frame.Correlated) { frame.Group = caller!.Group; frame.GroupLife = caller.GroupLife; }
                    else before.Missing("callerGroup", caller == null ? "no-add-enemy-caller" : "caller-unavailable-or-population-mismatch");
                }
            }
            if (_world < 0) before.Missing("worldEpoch", "unavailable");
            if (string.IsNullOrWhiteSpace(_context)) before.Missing("context", "unavailable");
            frame.BeforeComplete = before.Complete && instance.Length != 0;
            stack.Add(frame);
            if (frame.Admitted) Write(frame, "before", "entered", before, null, false);
            else Loss("call-rate-or-parent-cap");
            return frame;
        }
    }
    // Postfix freezes returned data; only finalizer closes the scope, including exceptions in other postfixes.
    internal void Returned(Frame? frame, PopulationBudgetFields fields, object? result, bool ranOriginal)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout"))
        {
            if (!Current(frame)) return;
            frame!.After = fields; frame.Result = result; frame.Returned = true; frame.RanOriginal = ranOriginal;
        }
    }
    internal void Finish(Frame? frame, Exception? exception)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout"))
        {
            if (!Current(frame)) return;
            frame!.Closed = true;
            if (frame.DepthDenied) return;
            if (!_threads.TryGetValue(frame.ThreadId, out var stack) || stack.Count == 0 || !ReferenceEquals(stack[^1], frame))
            { Loss("scope-order-mismatch"); _threads.Remove(frame.ThreadId); return; }
            stack.RemoveAt(stack.Count - 1); if (stack.Count == 0) _threads.Remove(frame.ThreadId);
            if (!frame.Admitted) return;
            var completion = exception != null ? "threw" : !frame.Returned ? "postfix-missing" : !frame.RanOriginal ? "original-skipped" : "returned";
            var fields = completion == "returned" ? frame.After! : new PopulationBudgetFields();
            Write(frame, exception != null ? "exception" : "after", completion, fields, exception, completion == "returned");
        }
    }
    private bool Current(Frame? frame) => frame != null && !frame.Closed && _active && frame.Epoch == _epoch
        && frame.Context == _context && frame.WorldEpoch == _world;
    private void Write(Frame frame, string phase, string completion, PopulationBudgetFields fields, Exception? error, bool result)
        => SafeWrite("call", json =>
        {
            json.WriteString("schema", Schema); json.WriteNumber("callId", frame.CallId);
            json.WriteNumber("parentCallId", frame.ParentCallId); json.WriteNumber("threadId", frame.ThreadId);
            json.WriteNumber("observationEpoch", frame.Epoch); json.WriteNumber("worldEpoch", frame.WorldEpoch);
            json.WriteString("context", frame.Context); json.WriteString("method", frame.Method);
            json.WriteString("instance", frame.Instance); json.WriteString("group", frame.Group);
            json.WriteNumber("groupLife", frame.GroupLife); json.WriteString("population", frame.Population);
            json.WriteBoolean("callerCorrelated", frame.Correlated);
            json.WriteString("phase", phase); json.WriteString("completion", completion);
            json.WriteString("observationPoint", phase == "before" ? "first-priority-prefix" : completion == "returned" ? "last-priority-postfix-frozen-before-finalizers" : "finalizer-terminal-outcome");
            json.WriteBoolean("nativeFinalValueVerified", false);
            fields.Write(json);
            if (result && frame.Result != null) { json.WritePropertyName("result"); JsonSerializer.Serialize(json, frame.Result); }
            if (error != null) json.WriteString("error", error.GetType().FullName);
            json.WriteBoolean("observationComplete", frame.BeforeComplete && fields.Complete && completion is "entered" or "returned");
        });
    internal void Loss(string reason)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout"))
        {
            _lost++;
            // First loss is immediate; later totals are retained on snapshot and lifecycle records.
            if (_lost == 1) Status(reason);
        }
    }
    internal void Status(string reason)
    {
        using (RecGate.Enter(_gate, "recorder.lock-timeout")) SafeWrite("status", json =>
    {
        json.WriteString("schema", Schema); json.WriteString("reason", reason); json.WriteBoolean("active", _active);
        json.WriteNumber("observationEpoch", _epoch); json.WriteNumber("worldEpoch", _world);
        json.WriteNumber("observerLost", _lost); json.WriteNumber("interruptedCalls", _interrupted);
        json.WriteNumber("openCalls", _threads.Values.Sum(stack => stack.Count));
        json.WriteNumber("maxDepth", MaxDepth); json.WriteNumber("maxThreads", MaxThreads);
        json.WriteNumber("maxCallsPerSecond", MaxCallsPerSecond);
    });
    }
    private void SafeWrite(string kind, Action<Utf8JsonWriter> body)
    {
        try { _write(kind, body); }
        catch (Exception) { _lost++; } // Never recurse into a failed sink or leave an orphaned native scope.
    }
}
