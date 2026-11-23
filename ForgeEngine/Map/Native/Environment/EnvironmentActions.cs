using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The host half of the environment rows. The rows with a complete level-event entry are one
/// `WardenObjectiveEventData` handed to the game's own level-event entry, and every one of them is built here:
/// the type member selects the effect, the fields carry the parameters, and the game's own execution entry
/// replicates whatever the effect replicates. Nothing in this file re-implements an effect's state, which is why
/// there is no light table, no fog table and no marker table in it — the game owns all three and publishes them
/// through its own state replicator.
///
/// The fog row is not among them: it calls the game's fog transition request directly and reads the dimension's fog
/// back in the same call (see <see cref="Fog"/>).
///
/// The whole plan-level request check runs before the first write, because these entries are fire-and-forget:
/// `ExecuteEvent` returns nothing, so an argument that cannot be carried has to be refused before the entry is
/// reached rather than reported afterwards. Every handler writes one row per named zone; only an independent
/// native readback confirms it, and a request without one remains an unknown commit.
///
/// The light-colour row is the one exception to the split above and says so: the game has no entry that carries a
/// colour or an intensity for a light, so that row's write is this package's own, on the light objects of the zone
/// it names, over the frames the transition lasts (`LightColorFade`). It is also the one row whose request is
/// judged in full before anything is written, because it can write immediately.
/// </summary>
internal sealed partial class EnvironmentActions
{
    /// <summary>The game's own maximum for `WorldEventObjectFilter`: a name longer than this cannot be one.</summary>
    internal const int MaximumFilterLength = 512;

    private readonly Func<bool> _canExecute;
    private readonly Func<WorldEventManager?> _events;
    private readonly Action<string> _report;
    private readonly Func<ZoneLightColorLedger?> _lightLedger;
    private readonly Func<RuntimeModuleHandle?> _owner;
    private readonly Action<EntityReference, Exception> _voiceFailed;

    internal EnvironmentActions(Func<bool> canExecute, Func<WorldEventManager?> events, Action<string> report,
        Func<ZoneLightColorLedger?> lightLedger, Func<RuntimeModuleHandle?>? owner = null)
    {
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _lightLedger = lightLedger ?? throw new ArgumentNullException(nameof(lightLedger));
        _owner = owner ?? (() => null);
        _voiceFailed = VoiceFailed;
    }

    /// <summary>The production wiring: the session's own readiness gate and the level event manager, read late
    /// through the property that owns it. A world that was torn down leaves the property null, which is what
    /// every row refuses as `level-event-unavailable`.</summary>
    internal static EnvironmentActions For(Func<bool> canExecute, Action<string> report,
        Func<ZoneLightColorLedger?> lightLedger, Func<RuntimeModuleHandle?> owner)
        => new(canExecute, () => WorldEventManager.Current, report, lightLedger, owner);

    internal CommandResult HandleLighting(CommandContext context) => Lighting(context);
    internal CommandResult HandleLightColor(CommandContext context) => LightColor(context);
    internal CommandResult HandleFog(CommandContext context) => Fog(context);
    internal CommandResult HandleFogCycle(CommandContext context) => FogCycle(context);
    internal CommandResult HandleNavMarker(CommandContext context) => NavMarker(context);
    internal CommandResult HandleAnimation(CommandContext context) => Animation(context);

    /// <summary>The `forge.action.presentation.lighting` command: the level-wide and the zone-addressed light
    /// entries, one row per zone the request named.</summary>
    internal CommandResult Lighting(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? scope = context.Text(EnvironmentContract.LightingScope);
        if (scope != "zones" && scope != "expedition") return Outcome.Rejected(ResultCode.LightingScopeUnsupported);
        string? mode = context.Text(EnvironmentContract.LightingMode);
        int modeIndex = mode == null ? -1 : Array.IndexOf(EnvironmentContract.LightingModes, mode);
        if (modeIndex < 0) return Outcome.Rejected(ResultCode.LightingModeUnsupported);
        // The game's event table has no level-wide flip, so the one member that would have to be served as a
        // read-then-write is refused instead of being approximated by one.
        if (scope == "expedition" && mode == "toggle") return Outcome.Rejected(ResultCode.LightingModeUnsupported);
        // The level-wide events (AllLightsOn / AllLightsOff) carry no transition, position or count, so a request
        // that supplies one would be silently dropped; it is refused by name instead.
        if (scope == "expedition" && (context.Present(EnvironmentContract.LightingTransition)
            || context.Present(EnvironmentContract.LightingPosition) || context.Present(EnvironmentContract.LightingCount)))
            return Outcome.Rejected(ResultCode.LightingExpeditionArgumentUnsupported);
        if (!Targets(context, EnvironmentContract.LightingZones, out var zones, out var refusal)) return refusal;
        // `position` and `count` are the zone entry's own sequence arguments: the game partitions lights into
        // distance sections around `position`. They are read once and written into every zone's event,
        // and a present-but-mis-shaped vector is refused rather than narrowed to the origin.
        bool hasPosition = context.Present(EnvironmentContract.LightingPosition);
        var position = default(UnityEngine.Vector3);
        if (hasPosition && !GameVectors.TryUnity(context.Vector(EnvironmentContract.LightingPosition), out position))
            return Outcome.Rejected(ResultCode.EnvironmentPositionInvalid);
        long? countValue = context.Integer(EnvironmentContract.LightingCount);
        int count = countValue is >= int.MinValue and <= int.MaxValue ? (int)countValue.Value : 0;
        if (count < 0) return Outcome.Rejected(ResultCode.EnvironmentCountInvalid);
        if (_events() is null) return Outcome.Rejected(ResultCode.LevelEventUnavailable);

        float transition = Seconds(context, context.Number(EnvironmentContract.LightingTransition) ?? 0);
        // Definition is built before Register returns its handle. Resolve this session's registration only
        // when a request arrives, including after startup replaces a registration with withdrawn bindings.
        var owner = _owner();
        if (scope == "zones" && (transition != 0 || count != 0 || hasPosition) && owner == null)
            return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        if (scope == "expedition")
        {
            var data = Event(mode == "on" ? eWardenObjectiveEventType.AllLightsOn : eWardenObjectiveEventType.AllLightsOff);
            data.DimensionIndex = zones[0].DimensionIndex;
            // One level-wide event answers every named zone: one row per explicit recipient, all carrying its outcome.
            bool expected = mode == "on";
            Func<bool>? readback = transition == 0 && count == 0 && !hasPosition
                ? () => zones.All(zone => LightSettled(zone, expected)) : null;
            foreach (var zone in zones) TraceLighting(context, data, zone, "before", expected);
            var issued = Issue(data, readback);
            foreach (var zone in zones) TraceLighting(context, data, zone, "returned", expected, issued.Code);
            return context.EachTarget(ref issued, SameStep);
        }
        var request = new LightingRequest
        {
            Self = this,
            Context = context,
            Owner = owner,
            Mode = mode!,
            Transition = transition,
            HasPosition = hasPosition,
            Position = position,
            Count = count,
            Type = mode == "toggle" ? eWardenObjectiveEventType.LightsInZoneToggle : eWardenObjectiveEventType.LightsInZone
        };
        var result = context.EachTarget(ref request, LightingStep);
        // The kernel keeps a deferred row only under a confirmed conclusion, so any other one ends the settlements.
        if (result.Commit != Commit.Confirmed && request.Pending != null)
            foreach (var (work, handle) in request.Pending) { work.Abandon(); handle.Cancel(); }
        return result;
    }

    /// <summary>What the zone-addressed lighting step needs from its request, handed by reference.</summary>
    private struct LightingRequest
    {
        internal EnvironmentActions Self;
        internal CommandContext Context;
        internal RuntimeModuleHandle? Owner;
        internal eWardenObjectiveEventType Type;
        internal string Mode;
        internal float Transition;
        internal bool HasPosition;
        internal UnityEngine.Vector3 Position;
        internal int Count;
        internal List<(ReadbackSettlement Work, RuntimeScheduleHandle Handle)>? Pending;
    }

    private static readonly TargetStep<LightingRequest> LightingStep = LightZone;

    /// <summary>One named zone's own light event, in the order the request named the zones.</summary>
    private static void LightZone(ref LightingRequest request, EntityReference target, int index, RowWriter rows)
    {
        if (target.LifeEpoch != 0 || EnvironmentZone.Parse(target) is not { } zone)
        { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        var context = request.Context;
        bool? expected = request.Mode == "toggle" ? LightMode(zone) is { } current ? !current : null : request.Mode == "on";
        var data = Event(request.Type);
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        if (request.Type == eWardenObjectiveEventType.LightsInZone) data.Enabled = request.Mode == "on";
        data.Duration = request.Transition;
        if (request.HasPosition) data.Position = request.Position;
        data.Count = request.Count;
        Func<bool>? readback = request.Transition == 0 && request.Count == 0 && !request.HasPosition && expected.HasValue
            ? () => LightSettled(zone, expected.Value) : null;
        TraceLighting(context, data, zone, "before", expected);
        var issued = readback == null && expected.HasValue && request.Owner != null
            ? request.Self.IssueLighting(request.Owner, context, data, zone, target, expected.Value, ref request.Pending)
            : request.Self.Issue(data, readback);
        TraceLighting(context, data, zone, "returned", expected, issued.Code);
        rows.End(issued.Status, issued.Commit, issued.Code);
    }

    /// <summary>The level event manager's own replicated state (the condition slots and the sustained-event
    /// slots), or null when this machine has no manager, replicator or state.</summary>
    internal static pWorldEventManagerState? ReplicatedState()
    {
        var manager = WorldEventManager.Current;
        if (manager == null || manager.WasCollected) return null;
        var replicator = manager.m_stateReplicator;
        if (replicator == null || replicator.WasCollected) return null;
        return replicator.State;
    }

    /// <summary>Whether one sustained-event slot (the repeating fog's `slot`) holds a running event, read from the
    /// state the master writes `StartRepeatingFog` / `StopSustainedEvent` into (`pWorldEventSustainData.IsValid`).
    /// Null when the state or the slot cannot be read; that is not a slot that is idle.</summary>
    /// <summary>A sustained event of the given type on one slot of <paramref name="zone"/>; `stop` reads nothing else.</summary>
    internal static WardenObjectiveEventData SustainedEvent(eWardenObjectiveEventType type, EnvironmentZone zone, int slot)
    {
        var data = Event(type);
        data.DimensionIndex = zone.DimensionIndex;
        data.Layer = (LG_LayerType)zone.Layer;
        data.LocalIndex = zone.LocalIndex;
        data.SustainedEventSlotIndex = slot;
        return data;
    }

    internal static bool? SustainedEventActive(int slot)
    {
        var slots = ReplicatedState()?.SustainedWorldEventData;
        return slots == null || (uint)slot >= (uint)slots.Length ? null : slots[slot].IsValid();
    }

    private Outcome IssueLighting(RuntimeModuleHandle owner, CommandContext context, WardenObjectiveEventData data, EnvironmentZone zone,
        EntityReference target, bool expected, ref List<(ReadbackSettlement Work, RuntimeScheduleHandle Handle)>? pending)
    {
        var native = EnvironmentZoneTable.At(zone);
        var manager = EnvironmentStateManager.Current;
        var lights = native == null ? Array.Empty<LG_Light>() : Lights(native)
            .Where(light => !light.WasCollected && light.AvailableInLevel && !light.DisabledFromSettings).ToArray();
        var issued = Issue(data, null);
        if (issued.Code != ResultCode.ReadbackUnconfirmed || native == null || manager == null || lights.Length == 0) return issued;
        // The native sequence partitions lights by distance. Its last section runs after Count * Duration
        // (LG_Zone.SetLightsInZoneSequence, build 20403457, RVA 0x165DF90). Count=0 has no delayed section.
        // Observe on a later tick even then: returning from ExecuteEvent is only submission of a coroutine.
        var length = context.Number(EnvironmentContract.LightingTransition) ?? 0;
        var due = checked(context.SimulationTick + Math.Max(1L, (long)Math.Ceiling(length * Math.Max(1, data.Count))));
        // Native objects are pinned once; polling performs no zone lookup, string lookup, LINQ enumeration or
        // managed allocation.
        var objects = lights.Select(light => light.gameObject).ToArray();
        bool Settled()
        {
            if (native.WasCollected || EnvironmentStateManager.Current != manager
                || EnvironmentStateManager.GetLightMode(zone.Global) != expected) return false;
            for (int at = 0; at < lights.Length; at++)
            {
                var light = lights[at];
                if (light == null || light.WasCollected || !light.AvailableInLevel || light.DisabledFromSettings
                    || objects[at] == null || objects[at]!.WasCollected || objects[at]!.activeSelf != expected)
                    return false;
            }
            return true;
        }
        return Defer(owner, context, target, due, Settled, ResultCode.LightingNativePending,
            code => TraceLighting(context, data, zone, "settled", expected, code), ref pending);
    }

    /// <summary>Hands one issued request to the existing kernel queue: the row answers pending now and the
    /// terminal row is written by <see cref="ReadbackSettlement"/> once the native read confirms the effect or the
    /// bound (one second after the first observation) runs out.</summary>
    private static Outcome Defer(RuntimeModuleHandle owner, CommandContext context, EntityReference target, long due, Func<bool> settled,
        string pendingCode, Action<string>? onSettled, ref List<(ReadbackSettlement Work, RuntimeScheduleHandle Handle)>? pending)
    {
        var token = context.BeginDeferredResult(target);
        var deadline = checked(due + Math.Max(1L, (long)Math.Ceiling(context.TicksPerSecond)));
        var work = new ReadbackSettlement(owner, token, settled, deadline, onSettled);
        var scheduled = owner.Schedule(context.CommandId, due, work, target);
        if (scheduled.Handle == null) return Outcome.FailedUnknown(ResultCode.ReadbackUnconfirmed);
        (pending ??= new()).Add((work, scheduled.Handle));
        work.Arm();
        return new Outcome(Status.Partial, Commit.Confirmed, pendingCode);
    }

    // Only pending native requests have work on the existing kernel queue.
    internal sealed class ReadbackSettlement(RuntimeModuleHandle owner, DeferredCommandToken token, Func<bool> settled,
        long deadline, Action<string>? onSettled) : IRuntimeModuleContinuation
    {
        private bool armed, ended;
        internal void Arm() => armed = true;
        internal void Abandon() => armed = false;

        public long? Resume(long tick)
        {
            bool done;
            try { done = settled(); }
            catch (Exception error) { MapLog.Contained("environment.readback-settlement", error); done = false; }
            if (!done && tick < deadline) return checked(tick + 1);
            ended = true;
            var code = done ? ResultCode.LevelEventIssued : ResultCode.ReadbackUnconfirmed;
            onSettled?.Invoke(code);
            owner.CompleteDeferredResult(token, done ? Status.Succeeded : Status.Failed, done ? Commit.Confirmed : Commit.Unknown, code);
            return null;
        }

        public void End(string status, string code)
        {
            if (!armed || ended) return;
            ended = true;
            // World/target/module retirement also ends the deferred obligation in the kernel. A scope
            // cancellation in the current world still needs its terminal answer; duplicate completion is safe.
            try { owner.CompleteDeferredResult(token, Status.Cancelled, Commit.None, code); }
            catch (RuntimeContractException error) { MapLog.Contained("environment.readback-ended", error); }
        }
    }
    // Trace is enabled by authoring tests. A returned ExecuteEvent is only a submitted coroutine request;
    // preserve its real payload and independent mode/physical reads without promoting unknown to confirmed.
    private static void TraceLighting(CommandContext context, WardenObjectiveEventData data, EnvironmentZone zone,
        string phase, bool? expected, string code = "")
    {
        if (!MapLog.Logs(RuntimeLogLevel.Trace)) return;
        try
        {
            int on = 0, off = 0, missing = 0;
            var native = EnvironmentZoneTable.At(zone);
            if (native != null)
                foreach (var light in Lights(native))
                {
                    if (light == null || light.WasCollected || light.gameObject == null || light.gameObject.WasCollected) { missing++; continue; }
                    if (!light.AvailableInLevel || light.DisabledFromSettings) continue;
                    if (light.gameObject.activeSelf) on++; else off++;
                }
            MapLog.Record(RuntimeLogLevel.Trace, "map.test-phase", "",
                RuntimeLogValue.Of("result", "lighting-native-request"), RuntimeLogValue.Of("phase", phase),
                RuntimeLogValue.Quote("commandId", context.CommandId), RuntimeLogValue.Of("eventType", data.Type.ToString()),
                RuntimeLogValue.Of("dimension", zone.Dimension), RuntimeLogValue.Of("layer", zone.Layer), RuntimeLogValue.Of("zone", zone.Zone),
                RuntimeLogValue.Of("enabled", data.Enabled), RuntimeLogValue.Of("duration", data.Duration), RuntimeLogValue.Of("count", data.Count),
                RuntimeLogValue.Of("expected", expected?.ToString() ?? "unavailable"),
                RuntimeLogValue.Of("nativeMode", LightMode(zone)?.ToString() ?? "unavailable"),
                RuntimeLogValue.Of("onLights", on), RuntimeLogValue.Of("offLights", off),
                RuntimeLogValue.Of("missingLights", native == null ? -1 : missing), RuntimeLogValue.Of("code", code));
        }
        catch (Exception error) { MapLog.Contained("environment.lighting-trace", error); }
    }

    /// <summary>The `forge.action.presentation.light_color` command: the colour and intensity transition over the
    /// light objects the named zones hold.
    ///
    /// This is the one row of this file the game has no entry for. Lights have no level event that carries a colour
    /// or an intensity and no state replicator that would carry one — `EnvironmentStateManager`'s light entries are
    /// the two on/off flips, which is what the `lighting` row above runs — so the write is this package's own and
    /// is made on the light objects of the zone itself, frame by frame, through `LG_Light.ChangeColor` and
    /// `LG_Light.ChangeIntensity`. Because the game replicates none of it, the request is recorded in the
    /// <see cref="ZoneLightColorLedger"/> (the colour and the compounded brightness, with the transition time) and the
    /// zone's lights fade toward what the record composes to; the Runtime's state path carries the record to every
    /// other machine, late joiners and checkpoint restores included, and each one fades its own lights the same way.
    ///
    /// The whole request is judged before the first light is touched: every named zone has to be a zone of this
    /// level that holds at least one light of the requested category, and both a malformed colour, brightness or
    /// transition and a request naming neither a colour nor a brightness are refused by name rather than served as
    /// a transition to what the lights already are. Only then is a transition scheduled, one per zone, and the row
    /// reports the zone it was scheduled for and the commit it carries — the write of a transition of no length is
    /// already made by then, and one with a length is made by the frames that follow.</summary>
    internal CommandResult LightColor(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? scope = context.Text(EnvironmentContract.LightColorScope);
        if (scope != "zones" && scope != "expedition") return Outcome.Rejected(ResultCode.LightingScopeUnsupported);
        string? category = context.Text(EnvironmentContract.LightColorCategory);
        // A `category` that is present and not one of the members is refused rather than read as "every
        // category": an absent port already means that, and a value nobody declared must not mean the same thing.
        bool declaredCategory = context.Present(EnvironmentContract.LightColorCategory);
        if (declaredCategory && category == null) return Outcome.Rejected(ResultCode.LightCategoryUnsupported);
        int categoryIndex = category == null
            ? LightColorFade.EveryCategory
            : Array.IndexOf(EnvironmentContract.LightCategories, category);
        if (category != null && categoryIndex < 0) return Outcome.Rejected(ResultCode.LightCategoryUnsupported);
        if (!Targets(context, EnvironmentContract.LightColorZones, out var named, out var refusal)) return refusal;
        if (category == "ambient")
        {
            if (!TryBrightness(context, out var amount, out refusal) || !TryTransition(context, out float seconds, out refusal)) return AmbientRefusal(refusal);
            // The colour is the same 0–1 colour every category takes; how strong the ambient light is comes from `brightness`.
            if (!TryColor(context, out var ambientColor, out refusal)) return AmbientRefusal(refusal);
            if (amount == null && ambientColor == null) return AmbientRefusal(Outcome.Rejected(ResultCode.LightColorRequired));
            var lighting = MapHost.Session?.GlobalLight;
            if (lighting == null) return AmbientRefusal(Outcome.Rejected(ResultCode.GlobalLightUnavailable));
            var result = lighting.Change(context, named, scope == "expedition", ambientColor, amount, seconds);
            return result.Status == Status.Rejected ? AmbientRefusal(result) : result;
        }
        if (!TryColor(context, out var color, out refusal)) return refusal;
        if (!TryBrightness(context, out var brightness, out refusal)) return refusal;
        if (color == null && brightness == null) return Outcome.Rejected(ResultCode.LightColorRequired);
        if (!TryTransition(context, out float transition, out refusal)) return refusal;

        // `expedition` is every zone the standing level holds — the same scope the level-wide light entry means —
        // and `zones` is exactly the places the request named. The request names its recipients either way, which
        // the recipient contract requires; the difference is only which zones the write lands on.
        var plan = new List<(EnvironmentZone Address, List<LG_Light> Lights)>();
        if (scope == "expedition")
        {
            foreach (var zone in EnvironmentZoneTable.Standing())
            {
                var layer = zone.m_layer;
                if (layer == null) continue;
                if (EnvironmentZone.Of((int)zone.m_dimensionIndex, (int)layer.m_type, (int)zone.LocalIndex)
                    is not { } address) continue;
                plan.Add((address, Lights(zone)));
            }
            if (plan.Count == 0) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        }
        else
        {
            foreach (var address in named)
            {
                if (EnvironmentZoneTable.At(address) is not { } zone)
                    return Outcome.Rejected(ResultCode.EnvironmentZoneMissing);
                plan.Add((address, Lights(zone)));
            }
        }
        // The expedition scope expands to every zone the standing level holds, so its own size is bounded here as
        // well: the loop below schedules one transition per zone, and a set past the row bound is refused before
        // the first of them rather than after it.
        if (plan.Count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        foreach (var (_, lights) in plan)
        {
            if (!lights.Any(light => categoryIndex == LightColorFade.EveryCategory
                || (int)light.m_category == categoryIndex))
                return Outcome.Rejected(ResultCode.LightColorNoLights);
            if (brightness.HasValue)
                foreach (var light in lights)
                    if ((categoryIndex == LightColorFade.EveryCategory || (int)light.m_category == categoryIndex)
                        && (!float.IsFinite(LightColorFades.Own(light).OriginalIntensity)
                            || !float.IsFinite(LightColorFades.Own(light).OriginalIntensity * brightness.Value)))
                        return Outcome.Rejected(ResultCode.LightBrightnessInvalid);
        }

        // The record that carries the colour to the other machines is checked before any light is touched: a
        // colour it could not hold would sit on this machine's lights alone.
        if (_lightLedger() is not { } ledger) return Outcome.Rejected(ResultCode.LightColorLedgerUnavailable);
        foreach (var (address, _) in plan)
            if (!ledger.HasRoomFor(address.Dimension, address.Layer, address.Zone, categoryIndex))
                return Outcome.Rejected(ResultCode.LightColorLedgerFull);
        var request = new LightColorRows { Plan = plan, Failed = new bool[plan.Count], Expedition = scope == "expedition" };
        for (int at = 0; at < plan.Count; at++)
        {
            var address = plan[at].Address;
            try { ledger.Request(address.Dimension, address.Layer, address.Zone, categoryIndex, color, brightness, transition); }
            catch (Exception error)
            {
                // A light object the level already took throws on the write. It is recorded as an unknown commit
                // rather than a clean refusal: part of the zone's lights may already carry the new value.
                Report("map.light-color-failed: " + error.GetType().Name + ": " + error.Message);
                request.Failed[at] = true;
            }
        }
        return context.EachTarget(ref request, LightColorStep);
    }

    /// <summary>The zones a colour request wrote and which of those writes threw, in the plan's order.</summary>
    private struct LightColorRows
    {
        internal List<(EnvironmentZone Address, List<LG_Light> Lights)> Plan;
        internal bool[] Failed;
        internal bool Expedition;
        internal int Next;
    }

    private static readonly TargetStep<LightColorRows> LightColorStep = LightColorRow;

    /// <summary>One named zone's row. The `zones` scope planned exactly the live zones the request named, in its
    /// order; the `expedition` scope wrote every zone of the level, and a named zone the level does not hold has
    /// nothing to answer with.</summary>
    private static void LightColorRow(ref LightColorRows request, EntityReference target, int index, RowWriter rows)
    {
        if (target.LifeEpoch != 0 || EnvironmentZone.Parse(target) is not { } address)
        { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        int at = -1;
        if (!request.Expedition) at = request.Next++;
        else
            for (int candidate = 0; candidate < request.Plan.Count && at < 0; candidate++)
            {
                var planned = request.Plan[candidate].Address;
                if (planned.Dimension == address.Dimension && planned.Layer == address.Layer && planned.Zone == address.Zone) at = candidate;
            }
        if (at < 0) rows.End(Status.Rejected, Commit.None, ResultCode.EnvironmentZoneMissing);
        else if (request.Failed[at]) rows.End(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        else rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.LightColorScheduled);
    }

    private static Outcome AmbientRefusal(Outcome result) { GlobalLightSession.Refuse(result.Code, ""); return result; }

    /// <summary>The light objects one zone holds, as this package's own list: the zone's own `m_lightsInZone` is
    /// the one place the level records them, and the entries that are not lights are dropped rather than carried
    /// into a transition that would write at them. The device row reads the same list through here, so a light an
    /// EMP blanks is a light the colour and lighting rows can see.</summary>
    internal static List<LG_Light> Lights(LG_Zone zone)
    {
        var lights = new List<LG_Light>();
        var native = zone.m_lightsInZone;
        if (native == null) return lights;
        for (var index = 0; index != native.Count; index++)
        {
            var light = native[index];
            if (light != null) lights.Add(light);
        }
        return lights;
    }

    /// <summary>The native zone one recipient reference names, or null when the reference is not a zone address of
    /// this level. It is the one place a recipient becomes a zone: the zone-addressed lighting rows and the device
    /// row resolve their targets through here, so neither can disagree with the other about which place a
    /// reference names.</summary>
    internal static LG_Zone? Zone(EntityReference reference)
    {
        if (EnvironmentZone.Parse(reference) is not { } address) return null;
        return EnvironmentZoneTable.At(address);
    }

    /// <summary>A `color` port as the game's own colour, or null when the plan named none. Three numbers outside
    /// the unit range are refused rather than clamped, and a vector that is present and not three finite numbers
    /// is refused rather than narrowed, exactly as the `position` port of the lighting row is.</summary>
    private static bool TryColor(CommandContext context, out UnityEngine.Color? color, out Outcome refusal)
    {
        color = null;
        refusal = Outcome.Rejected(ResultCode.LightColorInvalid);
        if (!context.Present(EnvironmentContract.LightColorValue)) return true;
        if (!GameVectors.TryUnity(context.Vector(EnvironmentContract.LightColorValue), out var vector)) return false;
        if (vector.x is < 0f or > 1f || vector.y is < 0f or > 1f || vector.z is < 0f or > 1f) return false;
        color = new UnityEngine.Color(vector.x, vector.y, vector.z, 1f);
        return true;
    }

    /// <summary>A `brightness` multiplier, or null when the plan named none. Zero is a light driven dark and is
    /// carried; a negative or non-finite multiplier is refused.</summary>
    private static bool TryBrightness(CommandContext context, out float? brightness, out Outcome refusal)
    {
        brightness = null;
        refusal = Outcome.Rejected(ResultCode.LightBrightnessInvalid);
        if (!context.Present(EnvironmentContract.LightColorBrightness)) return true;
        if (context.Number(EnvironmentContract.LightColorBrightness) is not { } number
            || !double.IsFinite(number) || number < 0 || !float.IsFinite((float)number)) return false;
        brightness = (float)number;
        return true;
    }

    /// <summary>The `transition` length in seconds, zero when the plan named none — the write itself. The port is
    /// declared in ticks, so a negative or non-finite tick count is refused before the conversion: there is no
    /// frame it could mean.</summary>
    private static bool TryTransition(CommandContext context, out float transition, out Outcome refusal)
    {
        transition = 0f;
        refusal = Outcome.Rejected(ResultCode.LightTransitionInvalid);
        if (!context.Present(EnvironmentContract.LightColorTransition)) return true;
        if (context.Number(EnvironmentContract.LightColorTransition) is not { } number
            || !double.IsFinite(number) || number < 0) return false;
        transition = Seconds(context, number);
        return true;
    }

    /// <summary>One authored time in the seconds the native fields take. Every time an author writes on these
    /// rows is ticks — the runtime's own simulation unit — so the conversion happens once, here, at the boundary
    /// where the value reaches the game's own float fields, and it uses the rate the host reported for this world.
    /// A world that reported no rate refuses the read by name rather than being converted at a second rate.</summary>
    private static float Seconds(CommandContext context, double ticks) => (float)(ticks / context.TicksPerSecond);

    /// <summary>An optional tick-count port is acceptable when absent, or when it is a finite number that is not
    /// negative; there is no frame a negative length could mean.</summary>
    private static bool NonNegativeTicks(CommandContext context, HandlerSlot slot)
        => !context.Present(slot) || context.Number(slot) is { } number && double.IsFinite(number) && number >= 0;

    /// <summary>The `forge.action.presentation.fog` command: the game's own fog transition, whose three
    /// arguments are the fog block id, the transition duration and the dimension the named zone belongs to.
    ///
    /// The request goes to `EnvironmentStateManager.AttemptStartFogTransition` itself rather than through the level
    /// event entry. The game's `SetFogSetting` case (build 20403457, in the `WorldEventManager.DoExcecuteEvent`
    /// coroutine that `ExecuteEvent` starts instead of running inline) checks the master and then calls exactly
    /// `AttemptStartFogTransition(FogSetting, FogTransitionDuration, DimensionIndex)`; the named zone contributes only
    /// its dimension, and the event adds nothing this row uses (no delay, no condition slot). Through the coroutine
    /// the effect cannot be read in the same call; the master's replicator applies the new state inside this one, so
    /// `GetCurrentFogID` of the dimension answers whether the request was taken. The state's absolute start time is
    /// the caller's expedition clock; `EnvironmentStateClock` retimes a start still in a receiving peer's future, so a
    /// client behind the master begins the blend on receipt instead of waiting for its clock to catch up.</summary>
    internal CommandResult Fog(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!Targets(context, EnvironmentContract.FogZone, out var zones, out var refusal)) return refusal;
        long? fogValue = context.Integer(EnvironmentContract.FogValue);
        int fog = fogValue is >= int.MinValue and <= int.MaxValue ? (int)fogValue.Value : -1;
        if (fog < 0) return Outcome.Rejected(ResultCode.FogPresetRequired);
        if (!NonNegativeTicks(context, EnvironmentContract.FogTransition)) return Outcome.Rejected(ResultCode.LightTransitionInvalid);
        if (EnvironmentStateManager.Current is null) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        var zone = zones[0];
        float seconds = Seconds(context, context.Number(EnvironmentContract.FogTransition) ?? 0);
        Outcome answer;
        try
        {
            EnvironmentStateManager.AttemptStartFogTransition((uint)fog, seconds, zone.DimensionIndex);
            bool confirmed = false;
            try { confirmed = EnvironmentStateManager.GetCurrentFogID(zone.DimensionIndex) == (uint)fog; }
            catch (Exception error)
            {
                Report("map.fog-readback-failed: " + error.GetType().Name + ": " + error.Message);
            }
            answer = confirmed ? Outcome.Succeeded(ResultCode.FogTransitionIssued) : Outcome.FailedUnknown(ResultCode.ReadbackUnconfirmed);
        }
        catch (Exception error)
        {
            // The request may already have reached the replicator, so this is an unknown commit, not a refusal.
            Report("map.fog-transition-failed: " + error.GetType().Name + ": " + error.Message);
            answer = Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        return context.EachTarget(ref answer, SameStep);
    }

    /// <summary>The `forge.action.presentation.fog_cycle` command: the game's sustained-event pair. `start`
    /// fills the slot's own state count, state duration, start delay, fog parameters and loop sound; `stop`
    /// clears the slot and reads none of them, which is why the stop row carries the same shape without
    /// requiring any of the fields it does not use.</summary>
    internal CommandResult FogCycle(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? mode = context.Text(EnvironmentContract.FogCycleMode);
        if (mode != "start" && mode != "stop") return Outcome.Rejected(ResultCode.FogCycleModeUnsupported);
        if (!Targets(context, EnvironmentContract.FogCycleZone, out var zones, out var refusal)) return refusal;
        long? slotValue = context.Integer(EnvironmentContract.FogCycleSlot);
        int slot = slotValue is >= int.MinValue and <= int.MaxValue ? (int)slotValue.Value : -1;
        if (slot < 0) return Outcome.Rejected(ResultCode.FogCycleSlotRequired);
        long? fogValue = context.Integer(EnvironmentContract.FogCycleFog);
        int fog = fogValue is >= int.MinValue and <= int.MaxValue ? (int)fogValue.Value : -1;
        if (mode == "start" && fog < 0) return Outcome.Rejected(ResultCode.FogPresetRequired);
        if (mode == "start" && !NonNegativeTicks(context, EnvironmentContract.FogCycleTransition))
            return Outcome.Rejected(ResultCode.LightTransitionInvalid);
        if (_events() is null) return Outcome.Rejected(ResultCode.LevelEventUnavailable);

        var zone = zones[0];
        var data = SustainedEvent(mode == "start"
            ? eWardenObjectiveEventType.StartRepeatingFog
            : eWardenObjectiveEventType.StopSustainedEvent, zone, slot);
        if (mode == "start")
        {
            // The game's own "loop until stopped" value. It is the default the vanilla slot uses, so an absent
            // `states` port means the same thing the level data means by it.
            long? statesValue = context.Integer(EnvironmentContract.FogCycleStates);
            data.SustainedEventStateCount = statesValue is >= int.MinValue and <= int.MaxValue
                ? (int)statesValue.Value : -1;
            data.SustainedEventStateDuration = Seconds(context,
                context.Number(EnvironmentContract.FogCycleStateDuration) ?? 0);
            data.SustainedEventDelay = Seconds(context, context.Number(EnvironmentContract.FogCycleStartDelay) ?? 0);
            data.FogSetting = (uint)fog;
            data.FogTransitionDuration = Seconds(context, context.Number(EnvironmentContract.FogCycleTransition) ?? 0);
            long? soundValue = context.Integer(EnvironmentContract.FogCycleSound);
            int sound = soundValue is >= int.MinValue and <= int.MaxValue ? (int)soundValue.Value : -1;
            if (sound >= 0) data.SoundID = (uint)sound;
        }
        // The slot lives in the level event manager's replicated state, which the event's coroutine writes after
        // the entry returns: read it back at once, then on the next ticks until it shows the requested state.
        bool start = mode == "start";
        var issued = Issue(data, () => SustainedEventActive(slot) == start);
        var owner = _owner();
        List<(ReadbackSettlement Work, RuntimeScheduleHandle Handle)>? pending = null;
        if (issued.Code == ResultCode.ReadbackUnconfirmed && owner != null)
            issued = Defer(owner, context, RuntimeZones.Reference(context.WorldEpoch, zone.Dimension, zone.Layer, zone.Zone),
                checked(context.SimulationTick + 1), () => SustainedEventActive(slot) == start, ResultCode.FogCycleNativePending,
                null, ref pending);
        var result = context.EachTarget(ref issued, SameStep);
        if (result.Commit != Commit.Confirmed && pending != null)
            foreach (var (work, handle) in pending) { work.Abandon(); handle.Cancel(); }
        return result;
    }

    /// <summary>The `forge.action.presentation.hud_marker` command: the game's own navigation marker on the
    /// world event object the filter names, shown or hidden by the same entry the level data uses.</summary>
    internal CommandResult NavMarker(CommandContext context) => NativeMarkers.Handle(context, _canExecute());

    /// <summary>The `forge.action.presentation.animation_trigger` command. This build's AnimationTrigger event
    /// ignores the payload's separate dimension/layer/zone fields, while its optional location-prefix grammar
    /// cannot encode dimension or zone indices 10-20. The row therefore keeps the minimal region adapter: the
    /// game's own key query and place predicate select each component, whose replicated Trigger/ResetTrigger entry
    /// is then called and read back through that same component's SyncTrigger.</summary>
    internal CommandResult Animation(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string filter = context.Text(EnvironmentContract.AnimationFilter) ?? "";
        Outcome refusal = Outcome.Rejected(ResultCode.WorldEventObjectFilterRequired);
        if (!filter.StartsWith(VanillaWorldObjects.Prefix, StringComparison.Ordinal)
            && !Filter(context, EnvironmentContract.AnimationFilter, out filter, out refusal)) return refusal;
        if (!Enabled(context, EnvironmentContract.AnimationEnabled, out bool enabled)) return refusal;
        if (!Targets(context, EnvironmentContract.AnimationZone, out var zones, out refusal)) return refusal;
        if (_events() is null) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        Outcome answer;
        if (filter.StartsWith(VanillaWorldObjects.Prefix, StringComparison.Ordinal))
        {
            if (context.Rows.Count != 1) return Outcome.Rejected(ResultCode.EnvironmentTargetUnsupported);
            var selected = zones[0];
            try
            {
                var outcome = VanillaWorldObjects.Animate(context.WorldEpoch, filter,
                    new ZoneCoordinates(selected.Dimension, selected.Layer, selected.Zone), enabled);
                answer = outcome.Commit == MapActionCommit.Refused ? Outcome.Rejected(outcome.Code) : Applied(outcome.Code);
            }
            catch (Exception error)
            {
                VanillaWorldObjects.CommitFailed(filter, error.GetType().Name);
                answer = Outcome.FailedUnknown(ResultCode.NativeCommitException);
            }
            return context.EachTarget(ref answer, SameStep);
        }
        var coordinates = zones.Select(zone => new ZoneCoordinates(zone.Dimension, zone.Layer, zone.Zone)).ToArray();
        var request = new AnimationRequest { Enabled = enabled, Filter = filter };
        try
        {
            if (!VanillaWorldObjects.TryPrepareAnimations(context.WorldEpoch, filter, coordinates,
                out request.Regions, out var prepareCode))
            {
                answer = Outcome.Rejected(prepareCode);
                return context.EachTarget(ref answer, SameStep);
            }
        }
        catch (Exception error)
        {
            Report("map.animation-query-failed: " + error.GetType().Name + ": " + error.Message);
            answer = Outcome.Rejected(ResultCode.LevelEventUnavailable);
            return context.EachTarget(ref answer, SameStep);
        }
        return context.EachTarget(ref request, AnimationStep);
    }

    /// <summary>An animation component's own readback: anything but an unconfirmed read is the committed state.</summary>
    private static Outcome Applied(string code)
        => code != ResultCode.ReadbackUnconfirmed ? Outcome.Succeeded(code) : Outcome.FailedUnknown(code);

    /// <summary>The prepared regions, one per live named zone in the request's order, and the next one to apply.</summary>
    private struct AnimationRequest
    {
        internal VanillaWorldObjects.AnimationRegion[] Regions;
        internal int Next;
        internal bool Enabled;
        internal string Filter;
    }

    private static readonly TargetStep<AnimationRequest> AnimationStep = AnimateZone;

    private static void AnimateZone(ref AnimationRequest request, EntityReference target, int index, RowWriter rows)
    {
        if (target.LifeEpoch != 0) { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        Outcome answer;
        try { answer = Applied(request.Regions[request.Next++].Apply(request.Enabled).Code); }
        catch (Exception error)
        {
            VanillaWorldObjects.CommitFailed(request.Filter, error.GetType().Name);
            answer = Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        rows.End(answer.Status, answer.Commit, answer.Code);
    }

    /// <summary>One event of the given type with the game's own "check no condition" gate written explicitly. A
    /// freshly constructed event carries the condition struct's zero value, which is slot 0 expecting false —
    /// a gate no vanilla event asks for — so the gate every row means is written here rather than inherited.</summary>
    internal static WardenObjectiveEventData Event(eWardenObjectiveEventType type)
    {
        var data = new WardenObjectiveEventData { Type = type };
        data.Condition = new WorldEventConditionPair { ConditionIndex = NoCondition };
        return data;
    }

    /// <summary>The game's own "this event checks no condition slot" index.</summary>
    internal const int NoCondition = -1;

    /// <summary>Runs one built event through the game's own level-event entry. A caller that has an independent,
    /// immediate native read supplies it. A request with no such read is never reported as confirmed: its row
    /// answers `readback-unconfirmed` as an unknown commit, the same as a read that did not show the effect.</summary>
    private Outcome Issue(WardenObjectiveEventData data, Func<bool>? readback)
    {
        try
        {
            WorldEventManager.ExecuteEvent(data, 0f);
        }
        catch (Exception error)
        {
            // The entry is fire-and-forget, so a throw is the only failure it can report. It is recorded as an
            // unknown commit rather than a clean refusal: part of the effect may already have been applied.
            Report("map.environment-event-failed: " + error.GetType().Name + ": " + error.Message);
            return Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        if (readback != null)
        {
            try
            {
                if (readback()) return Outcome.Succeeded(ResultCode.LevelEventIssued);
            }
            catch (Exception error)
            {
                Report("map.environment-event-readback-failed: " + error.GetType().Name + ": " + error.Message);
            }
        }
        return Outcome.FailedUnknown(ResultCode.ReadbackUnconfirmed);
    }

    private static bool? LightMode(EnvironmentZone zone)
    {
        if (EnvironmentStateManager.Current == null) return null;
        try { return EnvironmentStateManager.GetLightMode(zone.Global); }
        catch (Exception error)
        {
            MapLog.Contained("environment.light-mode-read", error);
            return null;
        }
    }

    private static bool LightSettled(EnvironmentZone zone, bool expected)
    {
        if (LightMode(zone) != expected || EnvironmentZoneTable.At(zone) is not { } native) return false;
        bool observed = false;
        foreach (var light in Lights(native))
        {
            if (light == null || light.WasCollected || !light.AvailableInLevel || light.DisabledFromSettings) continue;
            if (light.gameObject == null || light.gameObject.WasCollected || light.gameObject.activeSelf != expected)
                return false;
            observed = true;
        }
        return observed;
    }

    /// <summary>The zone references a request named, in the order it supplied them: the row's recipient port is
    /// one entity for a request that acts on one place and a collection for a request that acts on several, and
    /// both spellings arrive here as the one list the entries are built from. A reference of another kind or of a
    /// malformed address refuses the request by name, because the entry would otherwise be handed a coordinate
    /// nobody assigned. A reference of an earlier world is left out of the list: the rows are written in the
    /// recipients' own order, and that zone answers its own `stale-entity` row while the other zones go on.</summary>
    private static bool Targets(CommandContext context, HandlerSlot slot, out List<EnvironmentZone> zones, out Outcome refusal)
    {
        zones = new List<EnvironmentZone>();
        refusal = Outcome.Rejected(ResultCode.MissingField);
        int count = context.Count(slot);
        // The row bound is checked before the first native call: a request past it is refused whole rather than
        // run once per zone and then reported as a set whose rows do not fit.
        if (count > CommandResult.MaximumFacts)
        {
            refusal = Outcome.Rejected(ResultCode.TooManyTargets);
            return false;
        }
        if (count < 0)
        {
            if (!context.TryEntity(slot, out var single)) return false;
            if (!Target(context, single, zones, out refusal)) return false;
        }
        else
        {
            for (int index = 0; index < count; index++)
            {
                if (!context.TryEntity(slot, index, out var reference))
                {
                    refusal = Outcome.Rejected(ResultCode.EnvironmentTargetUnsupported);
                    return false;
                }
                if (!Target(context, reference, zones, out refusal)) return false;
            }
        }
        if (zones.Count != 0) return true;
        // Nothing live is left: the stale rows are the whole answer, and an empty collection is the kernel's.
        var rows = context.Rows;
        for (int index = 0; index < rows.Count; index++) rows.Row(index, Status.Rejected, Commit.None, ResultCode.StaleEntity);
        refusal = rows.Conclude();
        return false;
    }

    private static bool Target(CommandContext context, EntityReference reference, List<EnvironmentZone> zones, out Outcome refusal)
    {
        refusal = Outcome.Rejected(ResultCode.EnvironmentTargetUnsupported);
        if (EnvironmentZone.Parse(reference) is not { } zone) return false;
        if (!StaleZone(context, reference)) zones.Add(zone);
        return true;
    }

    /// <summary>The world event object name a request names. The name is what the game's own object registry is
    /// queried with, so an absent or empty one is refused rather than looked up as the empty string.</summary>
    private static bool Filter(CommandContext context, HandlerSlot slot,
        out string filter, out Outcome refusal)
    {
        refusal = Outcome.Rejected(ResultCode.WorldEventObjectFilterRequired);
        filter = context.Text(slot) ?? "";
        if (filter.Length == 0) return false;
        if (filter.Length > MaximumFilterLength)
        {
            refusal = Outcome.Rejected(ResultCode.WorldEventObjectFilterTooLong);
            return false;
        }
        return true;
    }

    private static bool Enabled(CommandContext context, HandlerSlot slot, out bool enabled)
    {
        if (context.Boolean(slot) is { } value) { enabled = value; return true; }
        enabled = false;
        return false;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("environment.report", reportError); }
    }
}
