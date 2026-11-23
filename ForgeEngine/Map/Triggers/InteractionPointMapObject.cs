using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap;

internal sealed class InteractionPointSource : IMapObjectSource
{
    private IReadOnlyList<InteractionPoint> _points = Array.Empty<InteractionPoint>();
    private readonly LevelIdIndex<InteractionPoint> _byId = new(point => point.Id, point => point.Level);

    /// <summary>The level the running world is in: an id authored in two levels names the point of this one.</summary>
    internal Func<MapLevelReference?>? CurrentLevel { set => _byId.CurrentLevel = value; }

    public string Category => InteractionPointAddress.Category;
    public IReadOnlyList<InteractionPoint> Points => _points;

    public void Load(IReadOnlyList<InteractionPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _byId.Clear();
        foreach (var point in points)
            if (!_byId.Add(point))
                throw new RuntimeContractException("interaction-point-duplicate", point.Id);
        _points = points;
    }

    public InteractionPoint? ById(string? id) => _byId.Find(id);
    private bool Mine(object instance) => instance is InteractionPoint point && _byId.Holds(point);
    public MapObjectReference? TryAddress(object instance)
        => Mine(instance) ? InteractionPointAddress.Of(((InteractionPoint)instance).Id) : null;
    public MapObjectReference? Parse(string text) => InteractionPointAddress.TryParse(text);
    public MapObjectObservation? Read(object instance)
        => Mine(instance) ? new MapObjectObservation(true, null, null) : null;
    public double[]? Position(object instance)
        => Mine(instance) ? ((InteractionPoint)instance).Position.ToArray() : null;
    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => Mine(instance) && InteractionPointAddress.Of(((InteractionPoint)instance).Id) == address;
    public void Report(string message) { }
}

internal sealed partial class MapObjectModule
{
    private readonly InteractionPointSource _interactionPoints = new();
    private RuntimeSubscriptionGate? _interactionStartedGate, _interactionProgressGate,
        _interactionCancelledGate, _interactionCompletedGate;

    public InteractionPointSource InteractionPoints
    {
        get { CheckThread(); return _interactionPoints; }
    }

    public bool InteractionPointsObserved
        => _interactionStartedGate is { HasSubscribers: true }
            || _interactionProgressGate is { HasSubscribers: true }
            || _interactionCancelledGate is { HasSubscribers: true }
            || _interactionCompletedGate is { HasSubscribers: true };

    private RuntimeSubscriptionGate? InteractionGate(InteractionPointPhase phase) => phase switch
    {
        InteractionPointPhase.Started => _interactionStartedGate,
        InteractionPointPhase.Progress => _interactionProgressGate,
        InteractionPointPhase.Cancelled => _interactionCancelledGate,
        InteractionPointPhase.Completed => _interactionCompletedGate,
        _ => null
    };

    public InteractionPointPublication InteractionPointFact(InteractionPoint point, EntityReference player, InteractionPointPhase phase,
        ulong sequence, double progress)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(player);
        var gate = InteractionGate(phase);
        if (gate is not { HasSubscribers: true }) return InteractionPointPublication.NotObserved;
        if (_disposed || _registration is not { IsRegistered: true } registration) return InteractionPointPublication.ProviderUnavailable;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return InteractionPointPublication.RuntimeNotReady;
        if (!_authority()) return InteractionPointPublication.NotAuthority;
        if (!double.IsFinite(progress) || progress < 0 || progress > 1) return InteractionPointPublication.ProgressInvalid;
        if (!ReferenceEquals(_interactionPoints.ById(point.Id), point)) return InteractionPointPublication.PointNotHeld;
        if (!point.Placed) return InteractionPointPublication.PointNotPlaced;
        if (_currentLevel == null || Level() != point.Level) return InteractionPointPublication.LevelMismatch;
        if (InteractionPointAddress.Of(point.Id) is not { } address) return InteractionPointPublication.AddressInvalid;

        var pointReference = EntityId(address);
        var row = InteractionPointContract.Rows[(int)phase - 1];
        var eventId = EntityKinds.MapObject + "." + row.Fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + address + ":" + player.Id + ":" + sequence.ToString(CultureInfo.InvariantCulture)
            + ":" + Math.Max(0, _kernel.CurrentTick).ToString(CultureInfo.InvariantCulture);
        // The four hold rows declare the same outputs in the same order: point, player, progress.
        var raised = registration.BeginEvent(gate);
        raised.Values.Entity(Blocks.TriggerInteractionHoldStarted.OutPoint, pointReference);
        raised.Values.Entity(Blocks.TriggerInteractionHoldStarted.OutPlayer, player);
        raised.Values.Number(Blocks.TriggerInteractionHoldStarted.OutProgress, progress);
        var result = raised.Raise(eventId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope(),
            player, InteractionPointCommand.Token(phase, sequence));
        if (result.Status != "queued")
        {
            if (result.Status != "ignored")
                ReportOnce("interaction-fact:" + result.Status + ":" + result.Code,
                    "map-object interaction-point fact not queued: phase=" + phase + " status=" + result.Status
                    + " kernel-code=" + result.Code + " world=" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ".");
            return result.Status switch
            {
                "ignored" => InteractionPointPublication.Unclaimed,
                "duplicate" => InteractionPointPublication.Duplicate,
                _ => InteractionPointPublication.Rejected
            };
        }
        return InteractionPointPublication.Published;
    }

    /// <summary>On a client, plays at once what this machine's own start or finish of a hold shows its own player,
    /// before the host has judged the request: the event is written as the host publishes it (a start carries progress
    /// 0, a finish 1) and kept under the same token <see cref="InteractionPointFact"/> publishes it with. A cancel is
    /// not predicted: its progress is the host's own count.</summary>
    public void PredictInteraction(InteractionPoint point, EntityReference player, InteractionPointPhase phase, ulong sequence)
    {
        CheckThread();
        if (phase is not (InteractionPointPhase.Started or InteractionPointPhase.Completed)
            || InteractionGate(phase) is not { HasSubscribers: true } gate || _authority()
            || _disposed || _registration is not { IsRegistered: true } registration
            || _kernel.StartupState != RuntimeStartupState.Ready || !point.Placed
            || _currentLevel == null || Level() != point.Level || InteractionPointAddress.Of(point.Id) is not { } address) return;
        // The request is already sent and the game's own interaction is in progress: nothing thrown here may reach either.
        try
        {
            var raised = registration.BeginEvent(gate);
            raised.Values.Entity(Blocks.TriggerInteractionHoldStarted.OutPoint, EntityId(address));
            raised.Values.Entity(Blocks.TriggerInteractionHoldStarted.OutPlayer, player);
            raised.Values.Number(Blocks.TriggerInteractionHoldStarted.OutProgress, phase == InteractionPointPhase.Completed ? 1 : 0);
            raised.Predict(_kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), Scope(), InteractionPointCommand.Token(phase, sequence));
        }
        catch (Exception error) { registration.PredictionFailed(gate, error); }
    }
}
