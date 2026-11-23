using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeMap.Native;

internal enum PlacedModelTransformKind { Position, Rotation, Scale }

internal sealed partial class PlacedModelSession
{
    private sealed record TransformOrigin(DeferredCommandToken Token);

    private sealed class ModelTransition
    {
        internal Vector3 Value, From, To;
        internal long Start, SampleTick;
        internal double Duration;
        internal bool Active;
        internal TransformOrigin? Origin;

        internal ModelTransition(Vector3 value) { Value = From = To = value; }

        internal Vector3 At(long tick)
        {
            if (!Active) return Value;
            var amount = Duration <= 0 ? 1 : Math.Clamp((tick - Start) / Duration, 0, 1);
            return Lerp(From, To, amount);
        }

        internal double Left(long tick) => !Active ? 0 : Math.Max(0, Duration - (tick - Start));

        internal bool Advance(long tick)
        {
            if (!Active) return false;
            Value = At(tick);
            SampleTick = tick;
            if (Left(tick) > 0) return false;
            Value = To; Active = false; Origin = null;
            return true;
        }

        internal void Begin(Vector3 target, long tick, double duration, TransformOrigin? origin)
        {
            Value = At(tick); From = Value; To = target; Start = SampleTick = tick; Duration = duration;
            Active = duration > 0;
            Origin = Active ? origin : null;
            if (!Active) Value = target;
        }

        internal void Restore(Vector3 value, Vector3 target, double remaining, TransformOrigin? origin, long tick)
        {
            Value = From = value; To = target; Start = SampleTick = tick; Duration = remaining;
            Active = remaining > 0; Origin = Active ? origin : null;
            if (!Active) Value = target;
        }

        internal TransformOrigin? Cancel()
        {
            if (!Active) return null;
            var origin = Origin; From = To = Value; Duration = 0; Active = false; Origin = null;
            return origin;
        }

        private static Vector3 Lerp(Vector3 from, Vector3 to, double amount)
            => new((float)(from.x + ((double)to.x - from.x) * amount),
                (float)(from.y + ((double)to.y - from.y) * amount),
                (float)(from.z + ((double)to.z - from.z) * amount));
    }

    private sealed class PlacedModelTransformState
    {
        internal readonly Vector3 InitialPosition, InitialScale;
        internal readonly Quaternion InitialRotation;
        internal readonly ModelTransition Position = new(Vector3.zero);
        internal readonly ModelTransition Rotation = new(Vector3.zero);
        internal readonly ModelTransition Scale = new(Vector3.one);

        internal PlacedModelTransformState(Vector3 position, Quaternion rotation, Vector3 scale)
        { InitialPosition = position; InitialRotation = rotation; InitialScale = scale; }

        internal ModelTransition Of(PlacedModelTransformKind kind) => kind switch
        {
            PlacedModelTransformKind.Position => Position,
            PlacedModelTransformKind.Rotation => Rotation,
            _ => Scale
        };
    }

    private readonly record struct TransformSlots(HandlerSlot Models, HandlerSlot X, HandlerSlot Y, HandlerSlot Z,
        HandlerSlot Duration, HandlerSlot Mode);

    private JsonElement? _pendingTransformState;
    private long _transformReadyWorld = long.MinValue;

    /// <summary>What one transform request hands each of its models' steps, and whether any of them changed.</summary>
    private struct TransformRequest
    {
        internal PlacedModelSession Session;
        internal CommandContext Context;
        internal PlacedModelTransformKind Kind;
        internal (double? X, double? Y, double? Z) Axes;
        internal string Mode;
        internal double Duration;
        internal bool Changed;
    }

    private static readonly TargetStep<TransformRequest> TransformStep = TransformOne;

    internal CommandResult Transform(CommandContext context, PlacedModelTransformKind kind)
    {
        var slots = Slots(kind);
        if (!_authority()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!Axes(context, slots, out var axes, out var axisCode)) return Outcome.Rejected(axisCode);
        var mode = context.Kind(slots.Mode) == ValueKind.Missing ? "increment" : context.Text(slots.Mode);
        if (mode is not ("increment" or "set")) return Outcome.Rejected(ResultCode.ModelTransformModeInvalid);
        // The port is ticks (contract §3.2 时间单位), the unit the transform's own clock runs in.
        if (context.Number(slots.Duration) is not { } duration || !double.IsFinite(duration) || duration < 0)
            return Outcome.Rejected(ResultCode.ModelTransformDurationInvalid);
        var request = new TransformRequest
        { Session = this, Context = context, Kind = kind, Axes = axes, Mode = mode, Duration = duration };
        // A timed row is partial + confirmed until its deferred result settles; it counts as done here.
        var outcome = context.EachTarget(ref request, TransformStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            ResultCode.ModelTransformCommitFailed);
        if (request.Changed) _transformState.Changed();
        return outcome;
    }

    /// <summary>One model: the new value composed, written to the native root and read back, and a timed change
    /// started as a transition whose deferred result settles when it ends. The position, rotation and scale blocks
    /// declare the same row, so one block's column constant addresses all three.</summary>
    private static void TransformOne(ref TransformRequest request, EntityReference target, int index, RowWriter rows)
    {
        var self = request.Session;
        var kind = request.Kind;
        var tick = self._kernel.CurrentTick;
        var code = self.Resolve(request.Context, target, out var entry);
        if (code != null)
        {
            rows.Integer(Blocks.ActionMapSetModelPosition.RowTargetCount, 0).End(Status.Rejected, Commit.None, code);
            return;
        }
        var transition = entry!.Transform.Of(kind);
        var current = transition.At(tick);
        var targetValue = Target(current, request.Axes, request.Mode);
        if (!CanCompose(entry, kind, targetValue, tick))
        {
            rows.Integer(Blocks.ActionMapSetModelPosition.RowTargetCount, 0)
                .End(Status.Rejected, Commit.None, ResultCode.ModelTransformValueInvalid);
            return;
        }
        try
        {
            var applied = request.Duration <= 0 ? targetValue : current;
            ApplyTransform(entry,
                kind == PlacedModelTransformKind.Position ? applied : entry.Transform.Position.At(tick),
                kind == PlacedModelTransformKind.Rotation ? applied : entry.Transform.Rotation.At(tick),
                kind == PlacedModelTransformKind.Scale ? applied : entry.Transform.Scale.At(tick));
            self.Advance(entry, tick);
            var restarted = transition.Active;
            if (restarted) self.Cancel(transition, ResultCode.TransitionRestarted);
            var origin = request.Duration > 0 ? new TransformOrigin(request.Context.BeginDeferredResult(target)) : null;
            transition.Begin(targetValue, tick, request.Duration, origin);
            request.Changed = true;
            var success = request.Duration <= 0 ? Updated(kind)
                : restarted ? ResultCode.TransitionRestarted
                : ResultCode.TransitionStarted;
            // A timed transition is accepted now and finishes later: its dispatch row is the deferred result's partial
            // receipt, and the terminal row (CompleteDeferredResult) settles it. An instant write is final.
            rows.Integer(Blocks.ActionMapSetModelPosition.RowTargetCount, 1)
                .End(request.Duration <= 0 ? Status.Succeeded : Status.Partial, Commit.Confirmed, success);
        }
        catch (Exception error)
        {
            request.Changed |= self.Cancel(entry, ResultCode.ModelTransformCommitFailed);
            MapLog.Contained("placed-model-transform.commit", error);
            try
            {
                self._report("placed-model-transform-failed: id=" + entry.Instance.Model.Id
                + " kind=" + Name(kind) + " exception=" + error.GetType().Name);
            }
            catch (Exception reportError) { MapLog.Contained("placed-model-transform.commit-report", reportError); }
            rows.Integer(Blocks.ActionMapSetModelPosition.RowTargetCount, 0)
                .End(Status.Failed, Commit.Unknown, ResultCode.ModelTransformCommitFailed);
        }
        self.TrackMoving(entry);
    }

    private static bool Axes(CommandContext context, TransformSlots slots, out (double? X, double? Y, double? Z) axes,
        out string code)
    {
        axes = default; code = ResultCode.ModelTransformValueInvalid;
        if (!Axis(context, slots.X, out var x) || !Axis(context, slots.Y, out var y)
            || !Axis(context, slots.Z, out var z)) return false;
        if (!x.HasValue && !y.HasValue && !z.HasValue)
        { code = ResultCode.ModelTransformAxisRequired; return false; }
        axes = (x, y, z); code = "";
        return true;
    }

    private static bool Axis(CommandContext context, HandlerSlot slot, out double? value)
    {
        value = null;
        if (context.Kind(slot) == ValueKind.Missing) return true;
        if (context.Number(slot) is not { } number || !double.IsFinite(number) || Math.Abs(number) > float.MaxValue)
            return false;
        value = number;
        return true;
    }

    private string? Resolve(CommandContext context, EntityReference target, out Entry? entry)
    {
        entry = null;
        const string prefix = EntityKinds.MapObject + ":";
        if (!target.Id.StartsWith(prefix, StringComparison.Ordinal)
            || PlacedModelAddress.TryParse(target.Id[prefix.Length..]) is not { } address)
            return ResultCode.UnsupportedRecipient;
        if (context.WorldEpoch != _kernel.WorldEpoch || target.WorldEpoch != _kernel.WorldEpoch)
            return ResultCode.StaleEntity;
        var current = _mapObjects.AddressReference(address.ToString());
        if (current == null) return ResultCode.ModelTransformTargetMissing;
        if (current.WorldEpoch != target.WorldEpoch || current.LifeEpoch != target.LifeEpoch || current.Id != target.Id)
            return ResultCode.StaleEntity;
        if (_placedWorld != _kernel.WorldEpoch || !_entries.TryGetValue(address.Key, out entry)
            || !ReferenceEquals(entry.Instance, _mapObjects.PlacedModels.ById(address.Key)))
        { entry = null; return ResultCode.ModelTransformTargetMissing; }
        if (entry.Root == null || entry.Root.WasCollected || entry.RootTransform == null || entry.RootTransform.WasCollected)
        { entry = null; return ResultCode.StaleEntity; }
        return null;
    }

    private static Vector3 Target(Vector3 current, (double? X, double? Y, double? Z) axes, string mode)
    {
        float Value(double? value, float prior) => value.HasValue
            ? mode == "increment" ? prior + (float)value.Value : (float)value.Value : prior;
        return new(Value(axes.X, current.x), Value(axes.Y, current.y), Value(axes.Z, current.z));
    }

    private static bool CanCompose(Entry entry, PlacedModelTransformKind kind, Vector3 candidate, long tick)
    {
        var state = entry.Transform;
        var position = kind == PlacedModelTransformKind.Position ? candidate : state.Position.At(tick);
        var rotation = kind == PlacedModelTransformKind.Rotation ? candidate : state.Rotation.At(tick);
        var scale = kind == PlacedModelTransformKind.Scale ? candidate : state.Scale.At(tick);
        return CanCompose(entry, position, rotation, scale);
    }

    private static bool CanCompose(Entry entry, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        var pose = ComposePose(entry, position, rotation, scale);
        return ForgeCore.Vec.Finite(pose.Position) && ForgeCore.Vec.Finite(pose.Rotation) && ForgeCore.Vec.Finite(pose.Scale);
    }

    private static void ApplyTransform(Entry entry, long tick)
        => ApplyTransform(entry, entry.Transform.Position.At(tick), entry.Transform.Rotation.At(tick),
            entry.Transform.Scale.At(tick));

    private static void ApplyTransform(Entry entry, Vector3 positionValue, Vector3 rotationValue, Vector3 scaleValue)
        => WriteNativePose(entry, ComposePose(entry, positionValue, rotationValue, scaleValue));

    private static NativePose ComposePose(Entry entry, Vector3 positionValue, Vector3 rotationValue, Vector3 scaleValue)
    {
        var state = entry.Transform;
        var position = state.InitialPosition + state.InitialRotation * positionValue;
        var rotation = state.InitialRotation * Quaternion.Euler(rotationValue.x, rotationValue.y, rotationValue.z);
        var scale = new Vector3(state.InitialScale.x * scaleValue.x,
            state.InitialScale.y * scaleValue.y, state.InitialScale.z * scaleValue.z);
        return new(position, rotation, scale);
    }

    private readonly record struct NativePose(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private static NativePose ReadNativePose(Entry entry)
    {
        if (entry.Root == null || entry.Root.WasCollected || entry.RootTransform == null || entry.RootTransform.WasCollected
            || entry.Root.transform.Pointer != entry.RootTransform.Pointer)
            throw new InvalidOperationException("placed-model-transform-readback-unavailable");
        var pose = new NativePose(entry.RootTransform.position, entry.RootTransform.rotation, entry.RootTransform.localScale);
        if (!ForgeCore.Vec.Finite(pose.Position) || !ForgeCore.Vec.Finite(pose.Rotation) || !ForgeCore.Vec.Finite(pose.Scale))
            throw new InvalidOperationException("placed-model-transform-readback-invalid");
        return pose;
    }

    private static bool Near(Vector3 a, Vector3 b)
        => Math.Abs(a.x - b.x) <= .0001f && Math.Abs(a.y - b.y) <= .0001f && Math.Abs(a.z - b.z) <= .0001f;

    private static bool Near(Quaternion a, Quaternion b)
    {
        bool Components(float sign) => Math.Abs(a.x - sign * b.x) <= .0001f && Math.Abs(a.y - sign * b.y) <= .0001f
            && Math.Abs(a.z - sign * b.z) <= .0001f && Math.Abs(a.w - sign * b.w) <= .0001f;
        return Components(1) || Components(-1);
    }

    private static void WriteNativePose(Entry entry, NativePose desired)
    {
        var prior = ReadNativePose(entry);
        var poseChanged = !Near(prior.Position, desired.Position) || !Near(prior.Rotation, desired.Rotation);
        var scaleChanged = !Near(prior.Scale, desired.Scale);
        if (poseChanged)
            entry.RootTransform.SetPositionAndRotation(desired.Position, desired.Rotation);
        if (scaleChanged) entry.RootTransform.localScale = desired.Scale;
        var actual = ReadNativePose(entry);
        if (!Near(actual.Position, desired.Position) || !Near(actual.Rotation, desired.Rotation) || !Near(actual.Scale, desired.Scale))
            throw new InvalidOperationException("placed-model-transform-readback-mismatch");
        entry.Instance.SetPosition(actual.Position.x, actual.Position.y, actual.Position.z);
        if (poseChanged || scaleChanged) FollowDetachedLabel(entry);
    }

    private static void FollowDetachedLabel(Entry entry)
    {
        if (entry.LabelRoot == null || entry.LabelRoot.WasCollected || entry.LabelTransform == null
            || entry.LabelTransform.WasCollected || entry.LabelTransform.parent != null) return;
        var collider = LabelAnchor(entry);
        if (collider == null || collider.WasCollected) return;
        entry.LabelTransform.position = entry.RootTransform.TransformPoint(
            collider.center + new Vector3(0, collider.size.y * .5f, 0));
    }

    private void AdvanceTransforms()
    {
        var finished = false;
        for (var index = 0; index < _movingEntries.Count;)
        {
            var entry = _movingEntries[index];
            if (!Moving(entry)) { RemoveMoving(index); continue; }
            if (entry.Root == null || entry.Root.WasCollected
                || !ReferenceEquals(entry.Instance, _mapObjects.PlacedModels.ById(entry.Instance.Model.Id)))
            {
                finished |= Cancel(entry, ResultCode.StaleEntity);
                RemoveMoving(index);
                continue;
            }
            try
            {
                ApplyTransform(entry, _kernel.CurrentTick);
                finished |= Advance(entry, _kernel.CurrentTick);
            }
            catch (Exception error)
            {
                finished |= Cancel(entry, ResultCode.ModelTransformCommitFailed);
                MapLog.Contained("placed-model-transform.advance", error);
                try
                {
                    _report("placed-model-transform-failed: id=" + entry.Instance.Model.Id
                    + " kind=advance exception=" + error.GetType().Name);
                }
                catch (Exception reportError) { MapLog.Contained("placed-model-transform.advance-report", reportError); }
            }
            if (!Moving(entry)) RemoveMoving(index);
            else index++;
        }
        if (finished && _authority()) _transformState.Changed();
    }

    private static bool Moving(Entry entry)
        => entry.Transform.Position.Active || entry.Transform.Rotation.Active || entry.Transform.Scale.Active;

    private void TrackMoving(Entry entry)
    {
        if (entry.MovingQueued || !Moving(entry)) return;
        entry.MovingQueued = true;
        // Deferred completions keep the original placement order even when motions start in another order.
        var index = 0;
        while (index < _movingEntries.Count && _movingEntries[index].Order < entry.Order) index++;
        _movingEntries.Insert(index, entry);
    }

    private void RemoveMoving(int index)
    {
        _movingEntries[index].MovingQueued = false;
        _movingEntries.RemoveAt(index);
    }

    /// <summary>The models are leaving the world: every transition still easing ends as a stale receiver.</summary>
    private void CancelTransforms()
    {
        foreach (var entry in _entries.Values) Cancel(entry, ResultCode.StaleEntity);
    }

    private bool Cancel(Entry entry, string reason)
    {
        var changed = false;
        changed |= Cancel(entry.Transform.Position, reason);
        changed |= Cancel(entry.Transform.Rotation, reason);
        changed |= Cancel(entry.Transform.Scale, reason);
        return changed;
    }

    private bool Cancel(ModelTransition transition, string reason)
    {
        var origin = transition.Cancel();
        if (origin == null) return false;
        Complete(origin, reason == ResultCode.ModelTransformCommitFailed ? Status.Failed : Status.Cancelled, reason);
        return true;
    }

    private static readonly TargetStep<Outcome> EndStep = EndOne;

    /// <summary>The terminal row of a timed transition: the model counts as changed only when it arrived.</summary>
    private static void EndOne(ref Outcome end, EntityReference target, int index, RowWriter rows)
        => rows.Integer(Blocks.ActionMapSetModelPosition.RowTargetCount, end.Status == Status.Succeeded ? 1 : 0)
            .End(end.Status, end.Commit, end.Code);

    private void Complete(TransformOrigin origin, Status status, string code)
    {
        if (!_authority() || origin.Token.WorldEpoch != _kernel.WorldEpoch) return;
        var end = new Outcome(status, status == Status.Succeeded ? Commit.Confirmed
            : status == Status.Failed ? Commit.Unknown : Commit.None, code);
        _mapObjects.Registration.CompleteDeferredResult(origin.Token, ref end, EndStep);
    }

    private JsonElement CaptureTransformState()
    {
        foreach (var entry in _entries.Values)
        {
            var actual = ReadNativePose(entry);
            var transform = entry.Transform;
            var expected = ComposePose(entry, transform.Position.Value, transform.Rotation.Value, transform.Scale.Value);
            if (!Near(actual.Position, expected.Position) || !Near(actual.Rotation, expected.Rotation) || !Near(actual.Scale, expected.Scale))
                throw new InvalidOperationException("placed-model-transform-snapshot-readback-mismatch");
        }
        object Save(ModelTransition transition)
        {
            // A checkpoint carries the last pose actually read back, not an unobserved future interpolation.
            var remaining = transition.Left(transition.SampleTick);
            var value = transition.Value;
            return new
            {
                value = Values(value),
                target = Values(remaining > 0 ? transition.To : value),
                remaining,
                completion = remaining > 0 ? transition.Origin?.Token : null
            };
        }
        return RuntimeJson.From(new
        {
            schemaVersion = FormatVersion.Current,
            worldEpoch = _kernel.WorldEpoch,
            level = _level()?.ToString(),
            ready = _placedWorld == _kernel.WorldEpoch && _pendingModels.Count == 0,
            models = (_placedWorld == _kernel.WorldEpoch && _pendingModels.Count == 0
                ? _entries.Values : Enumerable.Empty<Entry>())
                .OrderBy(entry => entry.Instance.Model.Id, StringComparer.Ordinal).Select(entry => new
                {
                    id = entry.Instance.Model.Id,
                    lifeEpoch = entry.Instance.LifeEpoch,
                    position = Save(entry.Transform.Position),
                    rotation = Save(entry.Transform.Rotation),
                    scale = Save(entry.Transform.Scale)
                }).ToArray()
        });
    }

    private void RestoreTransformState(JsonElement state)
    {
        try
        {
            if (!FormatVersion.Matches(state)
                || state.GetProperty("worldEpoch").GetInt64() != _kernel.WorldEpoch)
                throw new FormatException("placed-model-transform-state-world");
            if (!state.GetProperty("ready").GetBoolean())
            { _pendingTransformState = null; return; }
            if (_placedWorld != _kernel.WorldEpoch || _pendingModels.Count != 0)
            {
                if (_pendingTransformState is { } pending)
                {
                    var generations = pending.GetProperty("models").EnumerateArray().ToDictionary(
                        row => row.GetProperty("id").GetString()!, row => row.GetProperty("lifeEpoch").GetInt64(),
                        StringComparer.Ordinal);
                    foreach (var row in state.GetProperty("models").EnumerateArray())
                        if (generations.TryGetValue(row.GetProperty("id").GetString()!, out var life)
                            && row.GetProperty("lifeEpoch").GetInt64() < life)
                            throw new FormatException(PlacedModelSource.StateLifeStale);
                }
                _pendingTransformState = state.Clone(); return;
            }
            var level = state.GetProperty("level");
            if (level.ValueKind != JsonValueKind.Null && level.GetString() != _level()?.ToString())
                throw new FormatException("placed-model-transform-state-level");
            var restored = new List<(Entry Entry, long LifeEpoch, RestoredTransition Position, RestoredTransition Rotation,
                RestoredTransition Scale)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in state.GetProperty("models").EnumerateArray())
            {
                var id = row.GetProperty("id").GetString();
                if (id == null || !ids.Add(id)) throw new FormatException("placed-model-transform-state-target");
                // The host holds a model this side refused to place: this row has no receiver, the others still apply.
                if (!_entries.TryGetValue(id, out var entry))
                {
                    ReportStateRowRefused(id, "not-placed-here");
                    continue;
                }
                var life = row.GetProperty("lifeEpoch").GetInt64();
                if (life <= 0 || life > RuntimeJson.MaxSafeInteger) throw new FormatException(PlacedModelSource.StateLifeStale);
                // The row names a life of the model that ended (it was placed again since, or its body is gone): the
                // row ends with that life and the others still apply.
                if (!ReferenceEquals(entry.Instance, _mapObjects.PlacedModels.ById(id))
                    || entry.Root == null || entry.Root.WasCollected || life < entry.Instance.LifeEpoch)
                {
                    ReportStateRowRefused(id, "life-ended");
                    continue;
                }
                var position = ReadTransition(row.GetProperty("position"));
                var rotation = ReadTransition(row.GetProperty("rotation"));
                var scale = ReadTransition(row.GetProperty("scale"));
                foreach (var transition in new[] { position, rotation, scale })
                    if (transition.Origin is { } origin && (origin.Token.Target.Id
                        != EntityKinds.MapObject + ":" + PlacedModelAddress.Of(id) || origin.Token.Target.LifeEpoch != life))
                        throw new FormatException(PlacedModelSource.StateLifeStale);
                if (!CanCompose(entry, position.Value, rotation.Value, scale.Value)
                    || !CanCompose(entry, position.Target, rotation.Target, scale.Target))
                    throw new FormatException("placed-model-transform-state-value");
                restored.Add((entry, life, position, rotation, scale));
            }
            foreach (var id in _entries.Keys)
                if (!ids.Contains(id)) ReportStateRowRefused(id, "not-placed-by-host");
            // Validate every row before touching native bodies. Commit logical state only after all bodies read back;
            // a failed restore keeps the previous transitions and explicitly reports any rollback failure.
            // A completion that arrives late, or in the same batch as the start it belongs to, must not replace the
            // pose this peer's own transition is still easing: keep that interpolation and let it reach the host's end
            // pose. A new move (a remaining duration) and a state this peer saw no start for still replace it.
            var continued = restored.Select(row => (
                row.Entry,
                row.LifeEpoch,
                Position: Continue(row.Entry.Transform.Position, row.Position),
                Rotation: Continue(row.Entry.Transform.Rotation, row.Rotation),
                Scale: Continue(row.Entry.Transform.Scale, row.Scale))).ToArray();
            var prior = continued.Select(row => (row.Entry, Pose: ReadNativePose(row.Entry))).ToArray();
            var attempted = -1;
            try
            {
                for (var index = 0; index < continued.Length; index++)
                {
                    attempted = index;
                    var row = continued[index];
                    ApplyTransform(row.Entry, row.Position.Value, row.Rotation.Value, row.Scale.Value);
                }
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                for (var index = attempted; index >= 0; index--)
                    try { WriteNativePose(prior[index].Entry, prior[index].Pose); }
                    catch (Exception rollback)
                    {
                        Cancel(prior[index].Entry, ResultCode.ModelTransformCommitFailed);
                        failures.Add(rollback);
                    }
                if (failures.Count > 1)
                    throw new FormatException("placed-model-transform-state-rollback-failed", new AggregateException(failures));
                throw;
            }
            foreach (var row in continued)
            {
                _mapObjects.PlacedModels.RestoreGeneration(row.Entry.Instance, row.LifeEpoch);
                Restore(row.Entry.Transform.Position, row.Position);
                Restore(row.Entry.Transform.Rotation, row.Rotation);
                Restore(row.Entry.Transform.Scale, row.Scale);
                TrackMoving(row.Entry);
            }
            _pendingTransformState = null;
        }
        catch (Exception error) when (error is not FormatException)
        { throw new FormatException("placed-model-transform-state", error); }
    }

    private readonly HashSet<(long World, string Id)> _stateRowsRefused = new();

    /// <summary>One explicit record per world and model: the same difference repeats with every state body.</summary>
    private void ReportStateRowRefused(string id, string reason)
    {
        if (!_stateRowsRefused.Add((_kernel.WorldEpoch, id))) return;
        MapLog.Record(RuntimeLogLevel.Info, "map.placed-model-state-row-refused", "A placed model's state row has no receiver on one side.",
            RuntimeLogValue.Quote("id", id), RuntimeLogValue.Quote("reason", reason));
    }

    private sealed record RestoredTransition(Vector3 Value, Vector3 Target, double Remaining, TransformOrigin? Origin);

    private RestoredTransition ReadTransition(JsonElement row)
    {
        var value = ReadVector(row.GetProperty("value"));
        var target = ReadVector(row.GetProperty("target"));
        var remaining = row.GetProperty("remaining").GetDouble();
        if (!double.IsFinite(remaining) || remaining < 0) throw new FormatException("placed-model-transform-state-time");
        TransformOrigin? origin = null;
        if (remaining > 0)
        {
            var completion = row.GetProperty("completion");
            var token = new DeferredCommandToken(completion.GetProperty("commandId").GetString()!,
                completion.GetProperty("worldEpoch").GetInt64(), RuntimeJson.Entity(completion.GetProperty("target")));
            if (string.IsNullOrWhiteSpace(token.CommandId))
                throw new FormatException("placed-model-transform-state-origin");
            if (token.WorldEpoch != _kernel.WorldEpoch || token.Target.WorldEpoch != _kernel.WorldEpoch)
                throw new FormatException("placed-model-transform-state-world");
            origin = new(token);
        }
        return new(value, target, remaining, origin);
    }

    private void Restore(ModelTransition transition, RestoredTransition value)
        => transition.Restore(value.Value, value.Target, value.Remaining, value.Origin, _kernel.CurrentTick);

    /// <summary>How long a completion the host already reached is still eased here, so a start and its finish that arrived
    /// together are shown as a short move rather than replayed for the whole authored duration.</summary>
    private const float CatchUpSeconds = 0.15f;

    /// <summary>A finished state for the very move this peer is still interpolating keeps that interpolation instead of
    /// writing the end pose in one frame; every other state is taken as it stands.</summary>
    private RestoredTransition Continue(ModelTransition current, RestoredTransition next)
    {
        var left = current.Left(_kernel.CurrentTick);
        if (next.Remaining > 0 || left <= 0 || !current.Active || !Near(current.To, next.Value)) return next;
        return new(current.Value, next.Value, Math.Min(left, CatchUpSeconds * _kernel.TicksPerSecond), current.Origin);
    }

    private void ApplyPendingTransformState()
    {
        if (_pendingModels.Count != 0 || !_pendingTransformState.HasValue) return;
        var state = _pendingTransformState.Value;
        _pendingTransformState = null;
        RestoreTransformState(state);
    }

    private static double[] Values(Vector3 value) => new double[] { value.x, value.y, value.z };
    private bool Advance(Entry entry, long tick)
    {
        bool Finish(PlacedModelTransformKind kind)
        {
            var transition = entry.Transform.Of(kind);
            var origin = transition.Origin;
            if (!transition.Advance(tick)) return false;
            if (origin != null) Complete(origin, Status.Succeeded, Updated(kind));
            return true;
        }
        var finished = Finish(PlacedModelTransformKind.Position);
        finished |= Finish(PlacedModelTransformKind.Rotation);
        finished |= Finish(PlacedModelTransformKind.Scale);
        return finished;
    }
    private static Vector3 ReadVector(JsonElement row)
    {
        var values = row.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (values.Length != 3 || values.Any(value => !float.IsFinite(value)))
            throw new FormatException("placed-model-transform-state-vector");
        return new(values[0], values[1], values[2]);
    }

    private static TransformSlots Slots(PlacedModelTransformKind kind) => kind switch
    {
        PlacedModelTransformKind.Position => new(PlacedModelTransformContract.PositionModels,
            PlacedModelTransformContract.PositionX, PlacedModelTransformContract.PositionY,
            PlacedModelTransformContract.PositionZ, PlacedModelTransformContract.PositionDuration,
            PlacedModelTransformContract.PositionMode),
        PlacedModelTransformKind.Rotation => new(PlacedModelTransformContract.RotationModels,
            PlacedModelTransformContract.RotationX, PlacedModelTransformContract.RotationY,
            PlacedModelTransformContract.RotationZ, PlacedModelTransformContract.RotationDuration,
            PlacedModelTransformContract.RotationMode),
        _ => new(PlacedModelTransformContract.ScaleModels, PlacedModelTransformContract.ScaleX,
            PlacedModelTransformContract.ScaleY, PlacedModelTransformContract.ScaleZ,
            PlacedModelTransformContract.ScaleDuration, PlacedModelTransformContract.ScaleMode)
    };

    private static string Updated(PlacedModelTransformKind kind) => kind switch
    {
        PlacedModelTransformKind.Position => ResultCode.ModelPositionUpdated,
        PlacedModelTransformKind.Rotation => ResultCode.ModelRotationUpdated,
        _ => ResultCode.ModelScaleUpdated
    };

    private static string Name(PlacedModelTransformKind kind) => kind switch
    {
        PlacedModelTransformKind.Position => "position",
        PlacedModelTransformKind.Rotation => "rotation",
        _ => "scale"
    };
}
