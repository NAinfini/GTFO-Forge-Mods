using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using Localization;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

internal sealed partial class InteractionPointSession : IDisposable
{
    private const float ColliderRadius = InteractionPoint.DefaultColliderRadius;

    private sealed class PointView : IDisposable
    {
        private readonly InteractionPointSession _session;
        private readonly Il2CppSystem.Action<PlayerAgent, bool> _selected;
        private readonly Il2CppSystem.Func<PlayerAgent, bool> _canInteract;
        private readonly Il2CppSystem.Action<float> _timer;
        private readonly Il2CppSystem.Action _aborted;
        private readonly Il2CppSystem.Action<PlayerAgent> _triggered;
        private readonly Transform? _appearanceRoot;
        private readonly IntPtr _interactionTextKey;

        internal PointView(InteractionPointSession session, InteractionPoint point, float seconds, int order)
        {
            _session = session;
            Order = order;
            var body = InteractionPointBody.Read(point, session._kernel.WorldEpoch);
            Point = body.Point;
            _appearanceRoot = body.Parent;
            if (!StationTextValue.TryRender(point.Prompt, session._kernel, out var prompt))
                throw new InvalidOperationException("prompt");
            Root = new GameObject("ForgeInteractionPoint:" + point.Id) { layer = LayerManager.LAYER_INTERACTION };
            Root.SetActive(false);
            if (ReferenceEquals(_appearanceRoot, null))
            {
                Root.transform.position = new Vector3((float)Point.Position[0], (float)Point.Position[1],
                    (float)Point.Position[2]);
                Root.transform.rotation = new Quaternion((float)Point.Rotation[0], (float)Point.Rotation[1],
                    (float)Point.Rotation[2], (float)Point.Rotation[3]);
            }
            else
            {
                Root.transform.SetParent(_appearanceRoot, false);
                Root.transform.position = _appearanceRoot.position;
                Root.transform.rotation = _appearanceRoot.rotation;
                Root.transform.localPosition = Vector3.zero;
                Root.transform.localRotation = Quaternion.identity;
                Root.transform.localScale = Vector3.one;
            }
            // A solid box, as every collider the game puts on its own interaction layer is: the player's interaction
            // search does not pick up a trigger there.
            Collider = Root.AddComponent<BoxCollider>();
            Collider.center = body.Center;
            Collider.size = body.Size;
            Interact = Root.AddComponent<Interact_Timed>();
            _interactionTextKey = Interact.Pointer;
            HoldPoint.Configure(Interact, Collider, seconds, prompt);

            _selected = (Action<PlayerAgent, bool>)OnSelected;
            _canInteract = (Func<PlayerAgent, bool>)OnCanInteract;
            _timer = (Action<float>)OnTimer;
            _aborted = (Action)OnAborted;
            _triggered = (Action<PlayerAgent>)OnTriggered;
            HoldPoint.Subscribe(Interact, _selected, _canInteract, _timer, _aborted, _triggered);
        }

        internal InteractionPoint Point { get; }
        internal int Order { get; }
        internal GameObject Root { get; }
        internal BoxCollider Collider { get; }
        internal Interact_Timed Interact { get; }
        internal PlayerAgent? SelectedAgent { get; set; }
        internal ulong Sequence { get; set; }
        internal bool Active { get; set; }
        internal bool StartAccepted { get; set; }
        internal bool CompletionPending { get; set; }
        /// <summary>The local player's life the hold in progress was started under; every later step of it names the same.</summary>
        internal EntityReference? Player { get; set; }
        internal long Life => Player?.LifeEpoch ?? 0;
        internal string? LastNote { get; set; }
        /// <summary>Host code that refused the Started of the hold in progress; no new Started is sent until that native hold ends.</summary>
        internal string? StartRefusal { get; set; }
        internal string? LastRefusalLogged { get; set; }
        internal long LastProgressTick { get; set; } = -1;
        internal float LastProgress { get; set; } = -1;
        internal bool Available => Root != null && !Root.WasCollected && Interact != null && !Interact.WasCollected
            && Collider != null && !Collider.WasCollected
            && (ReferenceEquals(_appearanceRoot, null) || !_appearanceRoot.WasCollected);

        internal bool InRange(InteractionPointPlayer player) => InRange(player, out _);

        internal bool InRange(InteractionPointPlayer player, out double maximumDistance)
        {
            maximumDistance = double.NaN;
            if (!Available) return false;
            var transform = !ReferenceEquals(_appearanceRoot, null) ? _appearanceRoot! : Root.transform;
            var position = transform.position;
            var scale = transform.lossyScale;
            var absoluteScale = new Vector3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
            var center = new Vector3(Collider.center.x * scale.x, Collider.center.y * scale.y,
                Collider.center.z * scale.z);
            var size = new Vector3(Collider.size.x * absoluteScale.x, Collider.size.y * absoluteScale.y,
                Collider.size.z * absoluteScale.z);
            var radius = center.magnitude + size.magnitude * .5f;
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                || !float.IsFinite(radius)) return false;
            var extraReach = Math.Max(0, radius - InteractionPoint.DefaultColliderRadius);
            maximumDistance = player.MaximumDistance + extraReach;
            return InteractionPointJudge.InRangeAt(player, position.x, position.y, position.z, extraReach);
        }

        /// <summary>Shows the point with the switch the author gave it at level start; a confirmed behaviour switch is
        /// replayed over it afterwards (<see cref="MapStateActions.Reapply"/>).</summary>
        internal void Activate()
        {
            Root.SetActive(true);
            HoldPoint.SetActive(Interact, Point.Enabled);
        }

        internal bool RefreshPrompt(Language language)
        {
            if (!StationTextValue.TryRender(Point.Prompt, language, out var prompt)) return false;
            InteractionTextActions.Rewrite(Interact, ref prompt);
            HoldPoint.SetPrompt(Interact, prompt);
            return true;
        }

        private void OnSelected(PlayerAgent agent, bool selected)
            => _session.SelectedSafe(this, agent, selected);

        private bool OnCanInteract(PlayerAgent agent)
            => _session.CanInteractSafe(this, agent);

        private void OnTimer(float progress)
            => _session.TimerSafe(this, progress);

        private void OnAborted()
            => _session.AbortedSafe(this);

        private void OnTriggered(PlayerAgent agent)
            => _session.TriggeredSafe(this, agent);

        public void Dispose()
        {
            InteractionTextActions.Remove(_interactionTextKey);
            if (Interact != null && !Interact.WasCollected)
            {
                HoldPoint.Unsubscribe(Interact, _selected, _canInteract, _timer, _aborted, _triggered);
                HoldPoint.SetActive(Interact, false);
            }
            if (Root != null && !Root.WasCollected) UnityEngine.Object.Destroy(Root);
        }
    }

    private readonly IForgeEngine _kernel;
    private readonly MapTextLanguage _language;
    private string? _promptLanguageFailure;
    private readonly MapObjectModule _mapObjects;
    private readonly InteractionPointSource _source;
    private readonly Func<MapLevelReference?> _level;
    private readonly RoomLookup? _rooms;
    private readonly Action<string> _report;
    private readonly OnceReporter _once;
    private readonly Dictionary<string, PointView> _views = new(StringComparer.Ordinal);
    private int _languageViews; // views whose prompt references a player-language text block
    private readonly InteractionPointJudge _judge;
    private readonly IDisposable _commands;
    private readonly IDisposable _results;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private IDisposable? _buildStarted, _buildFinished;
    // The game's own delegate, not ForgeCore's state event (a postfix of DoChangeState): the failed-generation skip must run before
    // the level's cleanup resets the failure flag, and that order has not been shown to hold for the postfix.
    private readonly Il2CppSystem.Action<eGameStateName, eGameStateName> _stateChanged;
    private bool _buildEventsAttached;
    private bool _roomsReady;
    // The level whose factory finished: the level identity moves on before the next world and factory begin, so a
    // ready flag left over from the previous level never judges the next level's points before their rooms exist.
    private MapLevelReference? _readyLevel;
    private bool _generationFailed;
    private string _placementPhase = "world-ready";
    private IReadOnlyList<InteractionPoint> _authored = Array.Empty<InteractionPoint>();
    private MapLevelReference? _placedLevel;
    private long _placedWorld = long.MinValue;
    internal Interact_Timed? InspectInteraction(string address, long epoch)
    {
        if (_disposed || _placedWorld != epoch || _kernel.WorldEpoch != epoch
            || InteractionPointAddress.TryParse(address) is not { } parsed
            || _mapObjects.AddressReference(address) is not { } reference || reference.WorldEpoch != epoch
            || !_kernel.IsEntityCurrent(reference) || LabelReceiver(reference) is not { } point
            || !_views.TryGetValue(parsed.Key, out var view) || !ReferenceEquals(view.Point, point)
            || !view.Available) return null;
        return view.Interact;
    }

    private bool _refreshPending;

    internal void RefreshPrompt(EntityReference reference)
    {
        if (LabelReceiver(reference) is not { } point || !_views.TryGetValue(point.Id, out var view)) return;
        if (_language.TryRead(out var language, out _, out _)) view.RefreshPrompt(language);
    }
    private ulong _nextSequence;
    private Language _promptLanguage;
    private bool _promptLanguageKnown;
    private bool _disposed;

    private InteractionPointSession(IForgeEngine kernel, MapObjectModule mapObjects,
        Func<MapLevelReference?> level, RoomLookup? rooms, Action<string> report)
    {
        _kernel = kernel;
        _language = new();
        _mapObjects = mapObjects;
        _source = mapObjects.InteractionPoints;
        _level = level;
        _rooms = rooms;
        _report = report; _once = new OnceReporter(report);
        _judge = new InteractionPointJudge(() => _kernel.WorldEpoch, () => _kernel.CurrentTick,
            () => _mapObjects.InteractionPointsObserved, _source.ById, ResolvePlayer,
            _mapObjects.InteractionPointFact, InRange,
            point => _views.TryGetValue(point.Id, out var view) && view.Available && view.Interact.IsActive);
        _commands = DomainCommands.Register(InteractionPointCommand.Endpoint, Execute);
        try { _results = DomainCommands.RegisterResults(InteractionPointCommand.Endpoint, Result); }
        catch { _commands.Dispose(); throw; }
        _stateChanged = (Action<eGameStateName, eGameStateName>)StateChanged;
        try
        {
            _lifecycle = mapObjects.Registration.ObserveLifecycle(value =>
            {
                // A host migration keeps the level and its placed points: only the world number they are stamped with moves.
                if (value.IsMigration)
                {
                    // The player's life is read again from the identity table when the next hold starts.
                    if (_placedWorld == value.PreviousWorldEpoch) _placedWorld = value.Current.WorldEpoch;
                    return;
                }
                if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored)
                {
                    SkipFailedGeneration();
                    _roomsReady = false;
                    _placementPhase = value.Kind == RuntimeLifecycleKind.CheckpointRestored ? "checkpoint-restored" : "world-ready";
                    ClearPlacement();
                }
            }, replayCurrent: false);
        }
        catch { _results.Dispose(); _commands.Dispose(); throw; }
    }

    /// <summary>The in-level frame's placement step, once per kernel tick (<c>MapPluginSession.InLevelFrame</c>),
    /// not a lifecycle callback: placing views is a native side effect. A client's world identity can arrive after
    /// its local factory completed; the native completion flag plus InLevel is a fact, not a delayed room-read
    /// retry.</summary>
    internal void Frame()
    {
        if (_disposed) return;
        if (_placedWorld != _kernel.WorldEpoch)
        {
            if (!_roomsReady && LG_Factory.BuildDone && GameStateManager.CurrentStateName == eGameStateName.InLevel)
            { _roomsReady = true; _readyLevel = _level(); ZoneIndex.Reset(); }
            Refresh();
        }
        // Personal text must refresh even when no hold completion or zone judgment is pending on MapClock.
        RefreshPrompts();
    }

    internal static InteractionPointSession Start(IForgeEngine kernel, MapObjectModule mapObjects,
        Func<MapLevelReference?> level, RoomLookup? rooms, Action<string> report,
        IReadOnlyList<InteractionPoint>? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(mapObjects);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(report);
        var session = new InteractionPointSession(kernel, mapObjects, level, rooms, report);
        try
        {
            session.AdmitRows(prepared ?? InteractionPointData.Load(report));
            session._buildEventsAttached = true;
            session._buildStarted = ForgeCore.FactoryEvents.OnBuildStart(session.GenerationStarted);
            session._buildFinished = ForgeCore.FactoryEvents.OnBuildDone(session.GenerationFinished);
            GameStateManager.add_OnGameStateChanged(session._stateChanged);
            session._roomsReady = LG_Factory.BuildDone && GameStateManager.CurrentStateName == eGameStateName.InLevel;
            if (session._roomsReady) session._readyLevel = level();
            session.Refresh();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Activates the prepared table on the already registered command owner. View creation and
    /// room placement remain in the next normal level refresh.</summary>
    internal void AdmitRows(IReadOnlyList<InteractionPoint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _authored = rows;
        _source.Load(rows);
        _placedLevel = null;
        _placedWorld = long.MinValue;
        _refreshPending = true;
        _once.Reset();
    }

    internal int Authored => _authored.Count;

    private void ClearPlacement()
    {
        ClearViews();
        _source.Load(Array.Empty<InteractionPoint>());
        _placedLevel = null;
        _placedWorld = long.MinValue;
        _refreshPending = true;
    }

    private void GenerationStarted()
    {
        if (_disposed) return;
        _roomsReady = false;
        _generationFailed = false;
        _placementPhase = "factory-done";
        ClearPlacement();
        _once.Reset();
    }

    private void GenerationFinished()
    {
        if (_disposed) return;
        _roomsReady = true; _readyLevel = _level();
        _placementPhase = "factory-done";
        ZoneIndex.Reset();
        try { Refresh(); }
        catch (Exception error) { _report("interaction-point-placement: generation-complete placement failed." + Environment.NewLine + error); }
    }

    private void StateChanged(eGameStateName oldState, eGameStateName newState)
    {
        if (_disposed) return;
        // Failed generation never publishes FactoryBuildDone. Observe its existing AfterLevel transition
        // before cleanup resets G1's failure flag; do not interpret a state transition as generation success.
        SkipFailedGeneration();
    }

    private bool SkipFailedGeneration()
    {
        if (!RoomGenerationFailure.Pending && !_generationFailed) return false;
        if (_generationFailed) return true;
        _generationFailed = true;
        _roomsReady = false;
        var level = _level();
        var count = 0;
        foreach (var point in _authored) if (point.Level == level) count++;
        ClearPlacement();
        _report("interaction-point-placement-skipped: level=" + level + " world=" + _kernel.WorldEpoch
            + " points=" + count + " reason=generation-failed; placement skipped because level generation failed.");
        return true;
    }

    internal Action? WorkScheduled { get; set; }

    /// <summary>The placed point a map-object reference names in this world, which is where a sign or HUD marker
    /// on that point is drawn. A point is data with a world pose, so this is the whole receiver.</summary>
    internal InteractionPoint? LabelReceiver(EntityReference reference)
    {
        const string prefix = EntityKinds.MapObject + ":";
        if (_disposed || reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var address = InteractionPointAddress.TryParse(reference.Id[prefix.Length..]);
        return address != null && _source.ById(address.Key) is { Placed: true } point && point.Level == _placedLevel
            && _views.TryGetValue(address.Key, out var view) && ReferenceEquals(view.Point, point) && view.Available
            ? point : null;
    }

    /// <summary>The placed points of this level in <paramref name="group"/>, added to <paramref name="members"/>.</summary>
    internal void GroupMembers(string group, List<EntityReference> members)
    {
        if (_disposed) return;
        foreach (var view in _views.Values)
            if (view.Point.Group == group && view.Available
                && _mapObjects.AddressReference(InteractionPointAddress.Of(view.Point.Id)!.ToString()) is { } reference)
                members.Add(reference);
    }

    /// <summary>The root a point's visibility switch shows or hides, or null when the reference names no point placed in
    /// this world. A point inside a placed model is also hidden with that model.</summary>
    internal GameObject? VisibilityRoot(EntityReference reference)
        => LabelReceiver(reference) is { } point && _views.TryGetValue(point.Id, out var view) ? view.Root : null;

    /// <summary>The host holds a finish that came before its hold's duration ran out here.</summary>
    internal bool Pending => !_disposed && _judge.Held != 0;

    internal void Refresh()
    {
        if (_disposed) return;
        if (SkipFailedGeneration()) return;
        if (!_roomsReady) return;
        // Ready belongs to the level whose factory finished; another level's rooms do not exist yet.
        if (_readyLevel is { } built && _level() is { } now && built != now) return;
        var world = _kernel.WorldEpoch;
        var level = _level();
        var changed = _placedWorld != world || _placedLevel != level;
        if (!changed && !_refreshPending) return;
        if (level is not { } current)
        {
            ClearViews();
            _source.Load(Array.Empty<InteractionPoint>());
            _placedWorld = world;
            _placedLevel = null;
            _refreshPending = false;
            return;
        }

        double ticksPerSecond;
        try { ticksPerSecond = _kernel.TicksPerSecond; }
        catch (RuntimeContractException) { return; }
        // Native culling normally places models first. A session admitted after BuildDone
        // must complete that same placement step before creating any bound interaction view.
        PlacedModelSession.Current?.Place();
        if (PlacedModelSession.Current is { } models)
            foreach (var point in _authored)
                if (point.Level == current && point.Appearance is { } appearance && models.IsPending(appearance.PlacedModelId))
                    return; // Keep the existing refresh pending until its exact appearance is loaded.
        var placed = InteractionPointPlacement.Of(_authored, current, _rooms, ReportOnce, ReportResolverFailure);
        var views = new List<PointView>(placed.Count);
        foreach (var point in placed)
        {
            try
            {
                var seconds = (float)(point.Duration / ticksPerSecond);
                if (!float.IsFinite(seconds) || seconds <= 0) throw new InvalidOperationException("duration");
                var view = new PointView(this, point, seconds, views.Count);
                views.Add(view);
            }
            catch (Exception error)
            {
                string cause = error is RuntimeContractException contract
                    && MapPluginSession.IsOptionalPlayerServiceMissing(contract) ? contract.Code
                    : error.GetType().Name + ": " + error.Message;
                ReportOnce("interaction-point-build: point `" + point.Id + "` could not be built ("
                    + cause + ").");
            }
        }

        ClearViews();
        _source.Load(Array.Empty<InteractionPoint>());
        var active = new List<InteractionPoint>(views.Count);
        foreach (var view in views)
        {
            try
            {
                view.Activate();
                _views.Add(view.Point.Id, view);
                if (StationTextValue.RequiresLanguage(view.Point.Prompt)) _languageViews++;
                active.Add(view.Point);
            }
            catch (Exception error)
            {
                try { view.Dispose(); }
                catch (Exception disposeError) { MapLog.Contained("interaction-point.activate-cleanup", disposeError); }
                string reason = error is RuntimeContractException refusal
                    && MapPluginSession.IsOptionalPlayerServiceMissing(refusal)
                    ? refusal.Code : error.GetType().Name;
                ReportOnce("interaction-point-activate: point `" + view.Point.Id + "` could not be activated ("
                    + reason + ").");
            }
        }
        _source.Load(Array.AsReadOnly(active.ToArray()));
        // The views start switched on; the host's confirmed switches (kept with the saved state) go back over them.
        MapHost.Session?.MapState?.Reapply();
        var expected = 0;
        foreach (var point in _authored) if (point.Level == current) expected++;
        _placedWorld = world;
        _placedLevel = current;
        _refreshPending = false;
        // The result of a placement is an information record, not a refusal: zero authored and zero placed is a normal
        // answer. A point that was refused already has its own error line above.
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.InteractionPointPlaced, "Interaction points resolved after native world readiness.",
                RuntimeLogValue.Of("level", current.ToString()), RuntimeLogValue.Of("world", world),
                RuntimeLogValue.Of("authored", expected), RuntimeLogValue.Of("placed", active.Count),
                RuntimeLogValue.Of("refused", expected - active.Count), RuntimeLogValue.Of("phase", _placementPhase));
    }

    /// <summary>The host's step: the finishes it accepted before their duration ran out here are published once it has.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        _judge.Tick(_heldDropped ??= (point, code) => ReportOnce("interaction-point-complete: the accepted finish of point `"
            + point + "` was not published when its hold time ran out on the host (" + code + ")."));
    }

    private Action<string, string>? _heldDropped;

    private void RefreshPrompts()
    {
        if (_languageViews == 0)
        {
            _promptLanguageKnown = false;
            return;
        }
        if (!_language.TryRead(out var language, out var code, out _))
        {
            if (_promptLanguageFailure != code)
            { _promptLanguageFailure = code; ReportOnce("interaction-point-prompt: " + code); }
            return;
        }
        _promptLanguageFailure = null;
        bool changed = !_promptLanguageKnown || _promptLanguage != language;
        // With no animated rule in force and an unchanged language no prompt can differ, so skip the per-view lookups.
        if (!changed && !InteractionTextActions.AnyAnimated) return;
        foreach (var view in _views.Values)
        {
            if (!changed && !InteractionTextActions.Animated(view.Interact)) continue;
            try
            {
                if (!view.RefreshPrompt(language))
                    ReportOnce("interaction-point-prompt: point `" + view.Point.Id + "` text is unavailable.");
            }
            catch (Exception error)
            {
                ReportOnce("interaction-point-prompt: point `" + view.Point.Id + "` refresh failed ("
                    + error.GetType().Name + ").");
            }
        }
        _promptLanguage = language;
        _promptLanguageKnown = true;
    }

    private DomainCommandResult Execute(DomainCommandRequest request)
    {
        if (!_mapObjects.InteractionPointsObserved) return Rejected("no-consumer");
        if (!InteractionPointCommand.TryRead(request.Payload, out var phase, out var sequence, out var progress))
            return Rejected("invalid-payload");
        var decision = _judge.Execute(new InteractionPointRequest(request.SenderSession, request.WorldEpoch,
            request.LifeEpoch, request.ResourceId, (InteractionPointPhase)phase, sequence, progress));
        // A held finish is published by Tick: the map clock is woken for it.
        if (decision.Code == InteractionPointJudge.HeldCode) WorkScheduled?.Invoke();
        var detail = decision.Detail.Length == 0 && sequence != 0
            && phase is >= (byte)InteractionPointPhase.Started and <= (byte)InteractionPointPhase.Completed
            ? InteractionPointJudge.Correlation((InteractionPointPhase)phase, sequence) : decision.Detail;
        return new DomainCommandResult(decision.Status, decision.Commit, decision.Code, detail,
            decision.FactsCount);
    }

    private InteractionPointPlayer? ResolvePlayer(ulong sender)
    {
        var count = Players.Count;
        for (var index = 0; index < count; index++)
        {
            var agent = Players.At(index);
            if (agent == null || agent.Owner == null || agent.Owner.Lookup != sender) continue;
            if (!TryPlayerService(static (players, value) => players.ReferenceOf(value), agent, "resolve", out EntityReference? reference)
                || reference == null) return null;
            // The game searches from the first-person camera, so the eye (lower when crouched) is the origin, not the feet.
            if (!PlayerInteractionReach.TryRead(agent, out var origin, out var radius))
                return new InteractionPointPlayer(sender, reference, double.NaN, double.NaN, double.NaN, 0);
            return new InteractionPointPlayer(sender, reference, origin.x, origin.y, origin.z, radius + ColliderRadius);
        }
        return null;
    }

    internal bool InRange(InteractionPoint point, InteractionPointPlayer player)
        => _views.TryGetValue(point.Id, out var view) && ReferenceEquals(view.Point, point) && view.InRange(player);

    private bool CanInteract(PointView view, PlayerAgent agent)
        => !_disposed && _mapObjects.InteractionPointsObserved && Current(view) && Identity(agent, out _);

    private void Selected(PointView view, PlayerAgent agent, bool selected)
    {
        if (_disposed || !Local(agent)) return;
        if (!_mapObjects.InteractionPointsObserved)
        {
            view.SelectedAgent = null;
            return;
        }
        if (!selected)
        {
            if (Same(view.SelectedAgent, agent)) view.SelectedAgent = null;
            view.StartRefusal = null;
            if (view.Active && !view.CompletionPending)
            {
                Note(view, "selection", "deselected-while-active", true);
                Cancel(view);
            }
            return;
        }
        view.SelectedAgent = agent;
        if (view.Active && !view.CompletionPending && !view.Interact.TimerIsActive)
        {
            Note(view, "selection", "reselected-without-timer", true);
            Cancel(view);
        }
    }

    private void Timer(PointView view, float progress)
    {
        if (_disposed) return;
        var drop = !_mapObjects.InteractionPointsObserved ? "not-observed"
            : !Current(view) ? "point-not-current"
            : !view.Interact.TimerIsActive ? "timer-inactive"
            : !float.IsFinite(progress) || progress is < 0 or > 1 ? "progress-invalid"
            : view.SelectedAgent == null ? "not-selected"
            : !Local(view.SelectedAgent) ? "selected-not-local" : null;
        if (drop != null)
        {
            if (drop == "timer-inactive") view.StartRefusal = null;
            Note(view, "timer", drop, false);
            return;
        }
        var agent = view.SelectedAgent!;
        if (!view.Active)
        {
            if (view.StartRefusal != null) return;
            if (!Identity(agent, out var player))
            {
                Note(view, "timer", "identity-pending", false);
                return;
            }
            view.Player = player;
            view.Sequence = NextSequence();
            view.Active = true;
            view.StartAccepted = false;
            view.CompletionPending = false;
            view.LastProgressTick = -1;
            view.LastProgress = progress;
            var sent = Send(view, InteractionPointPhase.Started, progress);
            if (!sent) view.Active = false;
            // A host refusal that came back inside Send already wrote its own record.
            if (sent && view.StartRefusal != null) return;
            Note(view, "started", sent ? "sent" : "send-refused", true);
            return;
        }
        if (view.CompletionPending || view.LastProgressTick == _kernel.CurrentTick || progress == view.LastProgress)
            return;
        var priorTick = view.LastProgressTick;
        var priorProgress = view.LastProgress;
        view.LastProgressTick = _kernel.CurrentTick;
        view.LastProgress = progress;
        if (!Send(view, InteractionPointPhase.Progress, progress))
        {
            view.LastProgressTick = priorTick;
            view.LastProgress = priorProgress;
        }
    }

    private void Aborted(PointView view)
    {
        view.StartRefusal = null;
        if (view.Active && !view.CompletionPending)
        {
            Note(view, "abort", "native-abort-while-active", true);
            Cancel(view);
        }
    }

    private void Cancel(PointView view)
    {
        var sent = Send(view, InteractionPointPhase.Cancelled, Math.Clamp(view.LastProgress, 0, 1));
        Note(view, "cancelled", sent ? "sent" : "send-refused", true);
        if (sent)
        {
            view.Active = false;
            view.CompletionPending = false;
        }
    }

    private void Triggered(PointView view, PlayerAgent agent)
    {
        if (_disposed) return;
        view.StartRefusal = null;
        var drop = !view.Active ? "no-started-hold" : view.CompletionPending ? "completion-already-pending"
            : !Local(agent) ? "agent-not-local" : null;
        if (drop != null)
        {
            Note(view, "completed", drop, true);
            return;
        }
        view.CompletionPending = true;
        var sent = Send(view, InteractionPointPhase.Completed, 1);
        Note(view, "completed", sent ? "sent" : "send-refused", true);
        if (!sent) AbandonCompletion(view, "send-failed");
    }

    /// <summary>One record for a step of a hold that either reached the host or was dropped on this machine, with the
    /// state that decided it. A drop repeats every frame, so the same step and outcome is written once per change.</summary>
    private void Note(PointView view, string step, string outcome, bool always)
    {
        var key = step + ":" + outcome;
        if (!always && view.LastNote == key) return;
        view.LastNote = key;
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.InteractionPointHold, "Interaction point hold step.",
            RuntimeLogValue.Of("id", view.Point.Id), RuntimeLogValue.Of("step", step), RuntimeLogValue.Of("outcome", outcome),
            RuntimeLogValue.Of("active", view.Active), RuntimeLogValue.Of("pending", view.CompletionPending),
            RuntimeLogValue.Of("selected", view.SelectedAgent != null), RuntimeLogValue.Of("life", view.Life),
            RuntimeLogValue.Of("sequence", (long)view.Sequence), RuntimeLogValue.Of("tick", _kernel.CurrentTick));
    }
    private bool Send(PointView view, InteractionPointPhase phase, float progress)
    {
        if (!_mapObjects.InteractionPointsObserved || view.Player is not { } player) return false;
        if (!DomainCommands.Send(InteractionPointCommand.Endpoint, view.Point.Id, player.LifeEpoch,
            InteractionPointCommand.Payload(phase, view.Sequence, progress))) return false;
        // What the request shows this player is shown now; the host's order for it is then answered, not replayed.
        _mapObjects.PredictInteraction(view.Point, player, phase, view.Sequence);
        return true;
    }

    /// <summary>The local player's current life in this world, read from the identity table every machine holds (a
    /// client's copy is the host's, sent with the session state). False while the table has no row for the player in this
    /// world: the point cannot be held until it has.</summary>
    private bool Identity(PlayerAgent agent, out EntityReference? player)
    {
        player = null;
        if (!Local(agent) || !TryPlayerService(static (players, value) => players.ReferenceOf(value), agent, "identity", out EntityReference? reference)
            || reference == null || reference.WorldEpoch != _kernel.WorldEpoch || reference.LifeEpoch <= 0) return false;
        player = reference;
        return true;
    }

    private void Result(DomainCommandResponse response)
    {
        if (_disposed) return;
        var stale = response.Result.Code is "stale-life" or "stale-world";
        if (response.WorldEpoch != _kernel.WorldEpoch)
        {
            if (stale) ResetCorrelatedHold(response);
            return;
        }
        if (!_views.TryGetValue(response.ResourceId, out var view)
            || !TryCorrelation(response.Result.Detail, out var phase, out var sequence)
            || sequence != view.Sequence) return;

        if (phase == InteractionPointPhase.Started)
        {
            view.StartAccepted = response.Result.Status == "succeeded";
            if (response.Result.Status != "succeeded") NoteStartRefusal(view, response.Result.Code);
            else view.LastRefusalLogged = null;
        }
        if (stale)
        {
            ResetHold(view);
            return;
        }

        if (phase == InteractionPointPhase.Started && response.Result.Status != "succeeded")
        {
            view.Active = false;
            // The press is still held: resending every frame would only repeat the same refusal.
            view.StartRefusal = response.Result.Code;
        }
        else if (phase == InteractionPointPhase.Cancelled)
        {
            view.Active = false;
            view.CompletionPending = false;
        }
        else if (phase == InteractionPointPhase.Completed)
        {
            if (response.Result.Status == "succeeded") ResetHold(view);
            else AbandonCompletion(view, response.Result.Code);
        }
    }

    private void NoteStartRefusal(PointView view, string code)
    {
        if (view.LastRefusalLogged == code) return;
        view.LastRefusalLogged = code;
        Note(view, "started", "refused:" + code, true);
    }

    private void ResetCorrelatedHold(DomainCommandResponse response)
    {
        if (_views.TryGetValue(response.ResourceId, out var view)
            && TryCorrelation(response.Result.Detail, out _, out var sequence) && sequence == view.Sequence)
            ResetHold(view);
    }

    private static void ResetHold(PointView view)
    {
        view.Active = false;
        view.StartAccepted = false;
        view.CompletionPending = false;
    }

    private void AbandonCompletion(PointView view, string code)
    {
        var cancellationSent = Send(view, InteractionPointPhase.Cancelled,
            Math.Clamp(view.LastProgress, 0, 1));
        ResetHold(view);
        ReportCompletionFailure(view, code);
        if (!cancellationSent)
            ReportOnce("interaction-point-cancel: point `" + view.Point.Id
                + "` could not submit cleanup for its unfinished host hold.");
    }

    private void ReportCompletionFailure(PointView view, string code)
        => ReportOnce("interaction-point-complete: point `" + view.Point.Id
            + "` was not committed by the host (" + code + "); interact again to retry.");

    private bool Current(PointView view)
        => view.Available && ReferenceEquals(_source.ById(view.Point.Id), view.Point) && view.Point.Level == _level();

    private bool Local(PlayerAgent? agent)
        => TryPlayerService(static (players, _) => players.LocalAgent, null, "local-check", out PlayerAgent? local)
            && Same(agent, local);

    private bool TryPlayerService<T>(Func<IPlayerDirectory, PlayerAgent?, T> read, PlayerAgent? agent, string operation, out T value)
    {
        if (ForgePlayer.Native.PlayerHost.Players is not { } players)
        {
            value = default!;
            ReportOnce("interaction-point-player-service:" + operation + ":" + PlayerDirectoryService.MissingCode);
            return false;
        }
        try { value = read(players, agent); return true; }
        catch (RuntimeContractException error) when (MapPluginSession.IsOptionalPlayerServiceMissing(error))
        {
            value = default!;
            ReportOnce("interaction-point-player-service:" + operation + ":" + error.Code);
            return false;
        }
    }

    private static bool Same(PlayerAgent? left, PlayerAgent? right)
        => left != null && right != null && left.Pointer == right.Pointer;

    private ulong NextSequence()
    {
        _nextSequence++;
        if (_nextSequence == 0) _nextSequence++;
        return _nextSequence;
    }

    private static bool TryCorrelation(string? detail, out InteractionPointPhase phase, out ulong sequence)
    {
        phase = 0; sequence = 0;
        if (string.IsNullOrEmpty(detail)) return false;
        var separator = detail.IndexOf(':');
        return separator == 1 && byte.TryParse(detail.AsSpan(0, 1), NumberStyles.None,
                   CultureInfo.InvariantCulture, out var phaseValue)
            && phaseValue is >= (byte)InteractionPointPhase.Started and <= (byte)InteractionPointPhase.Completed
            && ulong.TryParse(detail.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && (phase = (InteractionPointPhase)phaseValue) != 0;
    }

    private static DomainCommandResult Rejected(string code)
        => new("rejected", "none", code, "", 0);

    private void ClearViews()
    {
        foreach (var view in _views.Values)
        {
            try { view.Dispose(); }
            catch (Exception error) { ReportOnce("interaction-point-dispose:" + error.GetType().Name); }
        }
        _views.Clear();
        _languageViews = 0;
        _promptLanguageKnown = false;
    }

    private void ReportOnce(string message)
    {
        _once.Report(message, message);
    }

    private void ReportResolverFailure(InteractionPoint point, Exception error)
    {
        // Il2CppInterop embeds the native stack in Message. Keep it in the evidence, but not in the cause key:
        // checkpoint rebinds can reach the same failure through a different native stack.
        var reason = error.Message;
        var stack = reason.IndexOf("\n--- BEGIN IL2CPP STACK TRACE ---", StringComparison.Ordinal);
        if (stack >= 0) reason = reason[..stack];
        var key = "resolver\0" + point.Id + "\0" + error.GetType().FullName + "\0" + reason;
        _once.Report(key, InteractionPointPlacement.ResolverFailure(point, error));
    }

    private void SelectedSafe(PointView view, PlayerAgent agent, bool selected)
    {
        try { Selected(view, agent, selected); }
        catch (Exception error) { CallbackFailed("selected", view, error); }
    }

    private bool CanInteractSafe(PointView view, PlayerAgent agent)
    {
        try { return CanInteract(view, agent); }
        catch (Exception error)
        {
            CallbackFailed("can-interact", view, error);
            return false;
        }
    }

    private void TimerSafe(PointView view, float progress)
    {
        try { Timer(view, progress); }
        catch (Exception error) { CallbackFailed("timer", view, error); }
    }

    private void AbortedSafe(PointView view)
    {
        try { Aborted(view); }
        catch (Exception error) { CallbackFailed("abort", view, error); }
    }

    private void TriggeredSafe(PointView view, PlayerAgent agent)
    {
        try { Triggered(view, agent); }
        catch (Exception error) { CallbackFailed("complete", view, error); }
    }

    private void CallbackFailed(string callback, PointView view, Exception error)
        => ReportOnce("interaction-point-callback:" + callback + ":" + view.Point.Id + ":"
            + (error is RuntimeContractException contract && MapPluginSession.IsOptionalPlayerServiceMissing(contract)
                ? contract.Code : error.GetType().Name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buildEventsAttached)
        {
            GameStateManager.remove_OnGameStateChanged(_stateChanged);
            _buildStarted?.Dispose();
            _buildFinished?.Dispose();
        }
        _lifecycle.Dispose();
        _results.Dispose();
        _commands.Dispose();
        WorkScheduled = null;
        ClearViews();
        _source.Load(Array.Empty<InteractionPoint>());
    }
}
