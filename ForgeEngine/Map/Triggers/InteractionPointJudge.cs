using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

internal readonly record struct InteractionPointPlayer(ulong Session, EntityReference Reference,
    double X, double Y, double Z, double MaximumDistance);

internal readonly record struct InteractionPointRequest(ulong SenderSession, long WorldEpoch, long LifeEpoch,
    string PointId, InteractionPointPhase Phase, ulong Sequence, float ClientProgress);

internal readonly record struct InteractionPointDecision(string Status, string Commit, string Code,
    string Detail, int FactsCount)
{
    public static InteractionPointDecision Rejected(string code, string detail = "")
        => new("rejected", "none", code, detail, 0);
}

/// <summary>What became of one hold fact. Each refusal is its own member so the decision carries the branch that
/// stopped it instead of a single catch-all code.</summary>
internal enum InteractionPointPublication
{
    NotObserved, Published,
    ProviderUnavailable, RuntimeNotReady, NotAuthority, ProgressInvalid,
    PointNotHeld, PointNotPlaced, LevelMismatch, AddressInvalid,
    // The kernel answered the dispatch without queueing it: no mounted card claimed it, the same event id was seen, or admission rejected it.
    Unclaimed, Duplicate, Rejected
}

internal sealed class InteractionPointJudge
{
    // CompleteAt: the tick a finish that came before the duration ran out is published at; -1 while none is held.
    private readonly record struct Hold(ulong Sequence, long StartedTick, long LastProgressTick,
        EntityReference Player, bool Active, long CompleteAt = -1);

    private readonly Func<long> _world;
    private readonly Func<long> _tick;
    private readonly Func<bool> _observed;
    private readonly Func<string, InteractionPoint?> _point;
    private readonly Func<ulong, InteractionPointPlayer?> _player;
    private readonly Func<InteractionPoint, EntityReference, InteractionPointPhase, ulong, double,
        InteractionPointPublication> _publish;
    private readonly Func<InteractionPoint, InteractionPointPlayer, bool> _inRange;
    private readonly Func<InteractionPoint, bool> _enabled;
    private readonly Dictionary<(string Point, ulong Session), Hold> _holds = new();
    private readonly List<(string Point, ulong Session)> _due = new();
    private long _heldWorld = long.MinValue;

    /// <summary>Finishes that came before their hold's duration ran out and wait for <see cref="Tick"/> to publish them.</summary>
    public int Held { get; private set; }

    public InteractionPointJudge(Func<long> world, Func<long> tick, Func<bool> observed,
        Func<string, InteractionPoint?> point, Func<ulong, InteractionPointPlayer?> player,
        Func<InteractionPoint, EntityReference, InteractionPointPhase, ulong, double,
            InteractionPointPublication> publish,
        Func<InteractionPoint, InteractionPointPlayer, bool>? inRange = null,
        Func<InteractionPoint, bool>? enabled = null)
    {
        _enabled = enabled ?? (_ => true);
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _observed = observed ?? throw new ArgumentNullException(nameof(observed));
        _point = point ?? throw new ArgumentNullException(nameof(point));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _inRange = inRange ?? InRange;
    }

    public InteractionPointDecision Execute(in InteractionPointRequest request)
    {
        var world = _world();
        if (_heldWorld != world) { _holds.Clear(); Held = 0; _heldWorld = world; }
        if (!_observed()) return InteractionPointDecision.Rejected("no-consumer");
        if (request.WorldEpoch != world) return InteractionPointDecision.Rejected("stale-world");
        if (request.Sequence == 0 || request.Phase is < InteractionPointPhase.Started or > InteractionPointPhase.Completed
            || !float.IsFinite(request.ClientProgress) || request.ClientProgress is < 0 or > 1)
            return InteractionPointDecision.Rejected("invalid-payload");
        var point = _point(request.PointId);
        if (point is not { Placed: true }) return InteractionPointDecision.Rejected("point-not-found");
        var player = _player(request.SenderSession);
        if (player is not { } actor || actor.Session != request.SenderSession)
            return InteractionPointDecision.Rejected("sender-not-player");
        if (request.LifeEpoch <= 0 || request.LifeEpoch != actor.Reference.LifeEpoch)
            return InteractionPointDecision.Rejected("stale-life");
        if (actor.Reference.WorldEpoch != world) return InteractionPointDecision.Rejected("stale-player");
        var now = _tick();
        if (now < 0) return InteractionPointDecision.Rejected("runtime-not-ready");
        var key = (point.Id, actor.Session);

        // A point the author switched off cannot be started or finished, so a client that held it before the switch arrived does not act.
        if (request.Phase is InteractionPointPhase.Started or InteractionPointPhase.Completed && !_enabled(point))
            return InteractionPointDecision.Rejected("point-disabled");
        if (request.Phase == InteractionPointPhase.Started)
        {
            if (!_inRange(point, actor)) return InteractionPointDecision.Rejected("out-of-range");
            if (_holds.TryGetValue(key, out var prior) && prior.Player == actor.Reference)
            {
                if (request.Sequence <= prior.Sequence) return InteractionPointDecision.Rejected("stale-sequence");
                if (prior.Active) return InteractionPointDecision.Rejected("already-active");
            }
            var publication = Publish(point, actor.Reference, request.Phase, request.Sequence, 0);
            if (publication.Status == "failed") return publication;
            _holds[key] = new Hold(request.Sequence, now, -1, actor.Reference, true);
            return publication;
        }
        if (!_holds.TryGetValue(key, out var hold) || !hold.Active || hold.Sequence != request.Sequence
            || hold.Player != actor.Reference)
            return InteractionPointDecision.Rejected("hold-not-active");
        if (request.Phase != InteractionPointPhase.Cancelled && !_inRange(point, actor))
            return InteractionPointDecision.Rejected("out-of-range");

        var progress = Math.Clamp((double)(now - hold.StartedTick) / point.Duration, 0, 1);
        if (request.Phase == InteractionPointPhase.Progress)
        {
            if (hold.LastProgressTick == now)
                return new("succeeded", "none", "same-tick", Correlation(request.Phase, request.Sequence), 0);
            var publication = Publish(point, actor.Reference, request.Phase, request.Sequence, progress);
            if (publication.Status == "failed") return publication;
            _holds[key] = hold with { LastProgressTick = now };
            return publication;
        }
        // The player's machine timed the whole hold, but its start reached this one a little later than its finish did
        // (the two travel times differ): the finish is accepted now and published when the duration has run out here.
        if (request.Phase == InteractionPointPhase.Completed && now - hold.StartedTick < point.Duration)
        {
            if (hold.CompleteAt < 0)
            {
                _holds[key] = hold with { CompleteAt = hold.StartedTick + point.Duration };
                Held++;
            }
            return new("succeeded", "none", HeldCode, Correlation(request.Phase, request.Sequence), 0);
        }

        if (request.Phase == InteractionPointPhase.Completed) progress = 1;
        // The native hold is over whatever became of its fact: a finish or cancel nobody claimed still ends it here,
        // or every later start on this point would be refused as already active.
        if (hold.CompleteAt >= 0) Held--;
        _holds[key] = hold with { Active = false, CompleteAt = -1 };
        return Publish(point, actor.Reference, request.Phase, request.Sequence, progress);
    }

    /// <summary>The answer to a finish that is published later by <see cref="Tick"/>.</summary>
    public const string HeldCode = "completion-held";

    /// <summary>Publishes the held finishes whose duration has run out. One that can no longer be published (the point is
    /// gone or switched off, the player's life ended, nothing claimed it) ends its hold and is handed to
    /// <paramref name="dropped"/> with the code; its player was already told the finish was accepted.</summary>
    public void Tick(Action<string, string>? dropped = null)
    {
        if (Held == 0) return;
        if (_heldWorld != _world()) { _holds.Clear(); Held = 0; return; }
        var now = _tick();
        if (now < 0) return;
        _due.Clear();
        foreach (var pair in _holds)
            if (pair.Value.Active && pair.Value.CompleteAt >= 0 && now >= pair.Value.CompleteAt) _due.Add(pair.Key);
        foreach (var key in _due)
        {
            var hold = _holds[key];
            _holds[key] = hold with { Active = false, CompleteAt = -1 };
            Held--;
            var point = _point(key.Point);
            var code = point is not { Placed: true } ? "point-not-found" : !_enabled(point) ? "point-disabled"
                : _player(key.Session) is not { } actor || actor.Reference != hold.Player ? "stale-life" : null;
            if (code == null)
            {
                var decision = Publish(point!, hold.Player, InteractionPointPhase.Completed, hold.Sequence, 1);
                if (decision.Status != "failed") continue;
                code = decision.Code;
            }
            dropped?.Invoke(key.Point, code);
        }
        _due.Clear();
    }

    private InteractionPointDecision Publish(InteractionPoint point, EntityReference player,
        InteractionPointPhase phase, ulong sequence, double progress)
    {
        var detail = Correlation(phase, sequence);
        return _publish(point, player, phase, sequence, progress) switch
        {
            InteractionPointPublication.Published => new("succeeded", "confirmed", "ok", detail, 1),
            InteractionPointPublication.NotObserved => new("succeeded", "none", "ok", detail, 0),
            // The Started gate is global; when no mounted card claims this point's start nobody observes it, which is not a failure.
            // Completed and Cancelled stay strict: a finish nobody claimed means the plan waiting on it never fires.
            InteractionPointPublication.Unclaimed when phase == InteractionPointPhase.Started
                => new("succeeded", "none", "ok", detail, 0),
            var failure => new("failed", "unknown", FailureCode(failure), detail, 0)
        };
    }

    internal static string FailureCode(InteractionPointPublication publication) => publication switch
    {
        InteractionPointPublication.ProviderUnavailable => "publish-provider-unavailable",
        InteractionPointPublication.RuntimeNotReady => "publish-runtime-not-ready",
        InteractionPointPublication.NotAuthority => "publish-not-authority",
        InteractionPointPublication.ProgressInvalid => "publish-progress-invalid",
        InteractionPointPublication.PointNotHeld => "publish-point-not-held",
        InteractionPointPublication.PointNotPlaced => "publish-point-not-placed",
        InteractionPointPublication.LevelMismatch => "publish-level-mismatch",
        InteractionPointPublication.AddressInvalid => "publish-address-invalid",
        InteractionPointPublication.Unclaimed => "publish-unclaimed",
        InteractionPointPublication.Duplicate => "publish-duplicate",
        _ => "publish-rejected"
    };

    // The sequence is the interaction's local request counter; SenderSession is an account key and is never formatted here.
    internal static string Correlation(InteractionPointPhase phase, ulong sequence)
        => ((byte)phase).ToString(CultureInfo.InvariantCulture) + ":"
            + sequence.ToString(CultureInfo.InvariantCulture);

    public static bool InRange(InteractionPoint point, InteractionPointPlayer player)
        => InRangeAt(player, point.Position[0], point.Position[1], point.Position[2], point.ExtraReach);

    internal static bool InRangeAt(InteractionPointPlayer player, double x, double y, double z, double extraReach)
    {
        if (!double.IsFinite(player.MaximumDistance) || player.MaximumDistance <= 0
            || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)
            || !double.IsFinite(extraReach) || extraReach < 0) return false;
        var dx = x - player.X;
        var dy = y - player.Y;
        var dz = z - player.Z;
        var maximum = player.MaximumDistance + extraReach;
        if (!double.IsFinite(maximum)) return false;
        return dx * dx + dy * dy + dz * dz <= maximum * maximum;
    }
}
