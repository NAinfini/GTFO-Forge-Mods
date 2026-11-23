using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.map.device_disable`: hold the electronic devices a request names off for a while,
/// or bring them back. The game's own electromagnetic pulse is local to each machine — `EmpManager.Enabled` and every
/// `IEmpReceiver` run with no network message — so this row is a `presentation` row: every addressed machine runs it
/// and writes only the devices it draws itself. The target decides which device a category means: a zone's device is
/// its light objects, a player's devices are the ones on that player's own screen and in that player's own hands, and
/// a player another machine draws is that machine's to write.
///
/// **One hold table, three writers.** The zone lights are this package's: <see cref="ZoneLights"/> writes
/// `LG_Light.SetEnabled`. The flashlight, the player interface and the map are supplied by the player provider, the bio tracker is
/// ForgeWeapon's; both register their writers on the <see cref="ForgePlayer.DeviceSwitchboard"/> the Player domain owns, and this
/// row reaches them only through it — it never touches a player object. The same board owns every hold, so all five
/// categories share one rule: a `disable` holds the device for the ticks the request named and the board puts it
/// back when they run out; a `restore` ends the hold at once; a second `disable` of a held device moves its expiry
/// and writes nothing, so there is never a second restore to run.
///
/// **What the row does not carry.** The recipients are explicit objects, so there is no radius; there is no toggle,
/// because the table holds only what this row itself turned off and a device's pulse state is not read back; and
/// there is no handle, because the hold is the board's and `operation = restore` is the way back.
///
/// **A machine that joins later.** The row reached only the machines that were there. What the host's own board still
/// holds travels in the kernel's native state (<see cref="StateKey"/>: each hold's category, target and the seconds it
/// has left), so a machine that receives it holds the same devices for the time that is left, through the same
/// writers and the same board; a hold it already has only gets that expiry. The board ends every hold with its world
/// and with the session, on every machine.</summary>
internal sealed class DeviceActions : IDisposable
{
    /// <summary>The two `operation` members, spelled the way the capability declares them.</summary>
    internal const string DisableOperation = "disable";
    internal const string RestoreOperation = "restore";
    internal const string StateKey = "forge.map.device-holds";
    private const int MaximumHolds = 256;
    // A received hold whose device is not built here yet is asked again after this many frames, until it runs out.
    private const int RetryFrames = 15;

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly Func<ForgePlayer.DeviceSwitchboard?> _board;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly List<(string Category, EntityReference Target, double Seconds)> _running = new();
    // Received holds this machine has not taken yet, each with the kernel tick it runs out at.
    private readonly List<(string Category, EntityReference Target, long Until)> _received = new();
    // The holds this machine took from the stream rather than from the row, and those of them the host has since let go.
    private readonly Dictionary<(string Category, string Target), EntityReference> _taken = new();
    private readonly List<(string Category, EntityReference Target)> _released = new();
    private int _retryIn;

    internal DeviceActions(IForgeEngine kernel, Func<bool> ready, Func<ForgePlayer.DeviceSwitchboard?> board, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        Lights = new ZoneLights();
        _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore);
    }

    public void Dispose() => _state.Dispose();

    /// <summary>What this machine's board holds in the current world, with the seconds each hold has left.</summary>
    private JsonElement Capture()
    {
        _running.Clear();
        _board()?.Running(_running);
        long world = _kernel.WorldEpoch;
        return JsonSerializer.SerializeToElement(_running.Where(hold => hold.Target.WorldEpoch == world).Take(MaximumHolds)
            .Select(hold => new
            {
                category = hold.Category,
                target = new { id = hold.Target.Id, worldEpoch = hold.Target.WorldEpoch, lifeEpoch = hold.Target.LifeEpoch },
                seconds = hold.Seconds
            }).ToArray());
    }

    /// <summary>The host's holds arriving here. The whole state is checked first; nothing is written from inside the
    /// receipt, the session's own frame takes the holds (<see cref="Frame"/>).</summary>
    private void Restore(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() > MaximumHolds)
            throw new RuntimeContractException("native-state-device-holds", "Device hold state is malformed.");
        var received = new List<(string, EntityReference, long)>(state.GetArrayLength());
        long now = _kernel.CurrentTick;
        foreach (var item in state.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("category", out var category)
                || category.ValueKind != JsonValueKind.String || Array.IndexOf(DeviceCategories.All, category.GetString()) < 0
                || !item.TryGetProperty("target", out var target) || !item.TryGetProperty("seconds", out var seconds)
                || seconds.ValueKind != JsonValueKind.Number || !(seconds.GetDouble() > 0) || !double.IsFinite(seconds.GetDouble()))
                throw new RuntimeContractException("native-state-device-holds", "Device hold state is malformed.");
            received.Add((category.GetString()!, RuntimeJson.Entity(target), now + (long)Math.Ceiling(seconds.GetDouble() * _kernel.TicksPerSecond)));
        }
        // A hold taken from the stream that the stream no longer carries was restored on the host (or ran out there).
        foreach (var taken in _taken.ToArray())
            if (!received.Exists(hold => hold.Item1 == taken.Key.Category && hold.Item2.Id == taken.Key.Target))
            {
                _released.Add((taken.Key.Category, taken.Value));
                _taken.Remove(taken.Key);
            }
        _received.Clear();
        _received.AddRange(received);
        _retryIn = 0;
    }

    /// <summary>Takes the received holds on this machine: each through its category's writer and the board, for the time
    /// it has left. One whose device is not here yet (a zone still being built) waits and is asked again; one that ran
    /// out, belongs to another world or names a device this machine does not draw is dropped.</summary>
    internal void Frame()
    {
        if (_received.Count == 0 && _released.Count == 0) return;
        if (_retryIn > 0) { _retryIn--; return; }
        if (!_ready() || _board() is not { } board) { _retryIn = RetryFrames; return; }
        long now = _kernel.CurrentTick, world = _kernel.WorldEpoch;
        foreach (var (category, target) in _released)
        {
            var writer = category == DeviceCategories.EnvironmentLight ? Lights : board.WriterOf(category);
            if (writer == null) continue;
            try { board.Restore(writer, target); }
            catch (Exception error) { Report(writer, target, error); }
        }
        _released.Clear();
        for (int index = _received.Count - 1; index >= 0; index--)
        {
            var (category, target, until) = _received[index];
            var writer = category == DeviceCategories.EnvironmentLight ? (LightsAvailable ? Lights : null) : board.WriterOf(category);
            bool wait = false;
            if (writer != null && until > now && target.WorldEpoch == world && _kernel.TicksPerSecond > 0)
            {
                try
                {
                    string? refusal = writer.Refusal(target);
                    if (refusal == null)
                    {
                        if (board.Disable(writer, target, (until - now) / _kernel.TicksPerSecond) == DeviceHoldChange.Disabled)
                            _taken[(category, target.Id)] = target;
                    }
                    else wait = refusal == ResultCode.EnvironmentZoneMissing;
                }
                catch (Exception error) { Report(writer, target, error); }
            }
            if (!wait) _received.RemoveAt(index);
        }
        if (_received.Count != 0) _retryIn = RetryFrames;
    }

    /// <summary>The late read of the Player domain's switchboard: nothing while the Player domain is not running.</summary>
    internal static ForgePlayer.DeviceSwitchboard? Board() => ForgePlayer.Native.PlayerHost.Session?.Devices;

    /// <summary>This package's own writer: the light objects of a zone.</summary>
    internal ZoneLights Lights { get; }

    /// <summary>Whether the zone-light writer is offered. A refused light readback (see
    /// <see cref="EnvironmentHookFeatures"/>) withdraws it: the category then answers like one nobody registered,
    /// because a hold whose restore could overwrite a later native decision must not be started.</summary>
    internal bool LightsAvailable { get; set; } = true;

    internal CommandResult Handle(CommandContext context)
    {
        if (!_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? operation = context.Text(EnvironmentContract.DeviceOperation);
        if (operation is not (DisableOperation or RestoreOperation)) return Outcome.Rejected(ResultCode.OperationUnsupported);
        if (!TryCategories(context, out var categories, out bool named)) return Outcome.Rejected(ResultCode.DeviceFilterUnknown);
        if (!TryDuration(context, operation, out double seconds)) return Outcome.Rejected(ResultCode.DurationOutOfRange);
        int count = context.Count(EnvironmentContract.DeviceTargets);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        for (int index = 0; index < count; index++)
            if (!context.TryEntity(EnvironmentContract.DeviceTargets, index, out _))
                return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (_board() is not { } board) return Outcome.Rejected(ResultCode.DeviceSwitchboardMissing);

        // Which writer answers each selected category on this machine. A category the request named that nobody
        // registered here is refused by name; one the request reached only by naming none is simply not a device
        // this machine carries.
        var writers = new List<IDeviceWriter>(categories.Count);
        foreach (var category in categories)
        {
            var writer = category == DeviceCategories.EnvironmentLight ? (LightsAvailable ? Lights : null) : board.WriterOf(category);
            if (writer != null) writers.Add(writer);
            else if (named) return Outcome.Rejected(ResultCode.DeviceCategoryUnavailable);
        }

        // A presentation row: every recipient answers its own row and nothing here commits world state. The
        // skeleton refuses a recipient of another world and writes the recipient count on every row.
        var request = new DeviceRequest
        { Self = this, Board = board, Writers = writers, Disable = operation == DisableOperation, Seconds = seconds };
        return context.EachTargetPresented(ref request, DeviceStep, countColumn: Blocks.ActionMapDeviceDisable.RowTargetCount);
    }

    private struct DeviceRequest
    {
        internal DeviceActions Self;
        internal ForgePlayer.DeviceSwitchboard Board;
        internal List<IDeviceWriter> Writers;
        internal bool Disable;
        internal double Seconds;
    }

    private static readonly TargetStep<DeviceRequest> DeviceStep = DeviceOne;

    private static void DeviceOne(ref DeviceRequest request, EntityReference target, int index, RowWriter rows)
    {
        var answer = target.LifeEpoch != 0 && EntityKinds.Is(target, EntityKinds.Zone)
            ? Outcome.Rejected(ResultCode.StaleEntity)
            : request.Self.Row(request.Board, request.Writers, target, request.Disable, request.Seconds);
        // A player another machine draws is that machine's to answer: the row says so and decides nothing here.
        if (answer.Code == ResultCode.DevicePlayerRemote) rows.EndElsewhere(answer.Status, answer.Commit, answer.Code);
        else rows.End(answer.Status, answer.Commit, answer.Code);
    }

    /// <summary>One target's row: every selected writer that has a device on it is driven through the board, and the
    /// row says what changed. A writer that has no device on this target is skipped, and when none had one the row
    /// carries the most telling reason: a player another machine draws (handed to that machine, not refused), then
    /// a device this machine cannot reach, then a zone this level does not hold, and otherwise that no selected
    /// category has a device there.</summary>
    private Outcome Row(ForgePlayer.DeviceSwitchboard board, List<IDeviceWriter> writers,
        EntityReference target, bool disable, double seconds)
    {
        int disabled = 0, refreshed = 0, restored = 0, notHeld = 0, failed = 0;
        bool remote = false, unavailable = false, zoneMissing = false;
        foreach (var writer in writers)
        {
            string? refusal;
            try { refusal = writer.Refusal(target); }
            catch (Exception error) { Report(writer, target, error); failed++; continue; }
            if (refusal != null)
            {
                remote |= refusal == ResultCode.DevicePlayerRemote;
                unavailable |= refusal == ResultCode.DeviceUnavailable;
                zoneMissing |= refusal == ResultCode.EnvironmentZoneMissing;
                continue;
            }
            try
            {
                switch (disable ? board.Disable(writer, target, seconds) : board.Restore(writer, target))
                {
                    case DeviceHoldChange.Disabled: disabled++; break;
                    case DeviceHoldChange.Refreshed: refreshed++; break;
                    case DeviceHoldChange.Restored: restored++; break;
                    default: notHeld++; break;
                }
            }
            catch (Exception error) { Report(writer, target, error); failed++; }
        }
        if (disabled + refreshed + restored > 0)
        {
            // What the host holds changed: a machine that joins later is owed it.
            if (ForgeCore.Session.IsHost) _state.Changed();
            return new Outcome(Status.Partial, Commit.None,
                disable ? (disabled > 0 ? ResultCode.DeviceDisabled : ResultCode.DeviceRefreshed) : ResultCode.DeviceRestored);
        }
        if (failed > 0) return Outcome.Failed(ResultCode.NativeCommitException);
        if (notHeld > 0) return Outcome.Rejected(ResultCode.DeviceNotDisabled);
        // A player another machine draws: the same command runs on that machine and writes the devices there. This
        // machine wrote nothing and cannot read that write, so the row is the presentation tier's unconfirmed one
        // (partial, nothing committed) and not a refusal: the host answers the same for its own player and a client's.
        // The row is ended as another machine's (DeviceOne), so it never becomes this machine's conclusion.
        if (remote) return new Outcome(Status.Partial, Commit.None, ResultCode.DevicePlayerRemote);
        return Outcome.Rejected(unavailable ? ResultCode.DeviceUnavailable
            : zoneMissing ? ResultCode.EnvironmentZoneMissing
            : ResultCode.DeviceReceiverMissing);
    }

    /// <summary>The selected categories in the table's own order. `device_filter` is a collection of the shared
    /// `device_category` set, so it arrives as member indices — the set lists the categories in
    /// <see cref="DeviceCategories.All"/>'s order, and an index is a position in that table. An absent, null or empty
    /// collection is every category; a list names exactly those. Anything that is not a list of indices inside the
    /// table refuses the whole request. <paramref name="named"/> says whether the request chose the set itself.</summary>
    internal static bool TryCategories(CommandContext context, out List<string> categories, out bool named)
    {
        categories = new List<string>(DeviceCategories.All.Length);
        named = false;
        int count = context.Count(EnvironmentContract.DeviceFilter);
        if (count < 0 && context.Present(EnvironmentContract.DeviceFilter)) return false;
        if (count <= 0)
        {
            categories.AddRange(DeviceCategories.All);
            return true;
        }
        var chosen = new bool[DeviceCategories.All.Length];
        for (int item = 0; item < count; item++)
        {
            if (context.Integer(EnvironmentContract.DeviceFilter, item) is not { } value
                || value < 0 || value >= chosen.Length) return false;
            int index = (int)value;
            chosen[index] = true;
        }
        for (int index = 0; index < chosen.Length; index++)
            if (chosen[index]) categories.Add(DeviceCategories.All[index]);
        named = true;
        return true;
    }

    /// <summary>The hold time, in the seconds of the machine that runs the row, converted from the ticks the row
    /// declares at the rate the running world reported (contract §3.2 时间单位). A `disable` with no duration is
    /// refused rather than read as zero — a device that comes back on the next frame is not a state an author asked
    /// for — and a `restore` takes no duration at all.</summary>
    private static bool TryDuration(CommandContext context, string operation, out double seconds)
    {
        seconds = 0;
        bool present = context.Present(EnvironmentContract.DeviceDuration);
        if (operation == RestoreOperation) return !present;
        if (!present || context.Number(EnvironmentContract.DeviceDuration) is not { } ticks) return false;
        if (!double.IsFinite(ticks) || ticks <= 0 || !(context.TicksPerSecond > 0)) return false;
        seconds = ticks / context.TicksPerSecond;
        return double.IsFinite(seconds) && seconds > 0;
    }

    private void Report(IDeviceWriter writer, EntityReference target, Exception error)
    {
        try { _report("map.device-write-failed: " + writer.Category + " " + target.Id + ": " + error.GetType().Name + ": " + error.Message); }
        catch (Exception reportError) { MapLog.Contained("device.write-failed-report", reportError); }
    }

    /// <summary>
    /// The zone-light writer. The game's own `LG_Light.EMPDisabled(rebootTime)` is not a light switch on this build:
    /// its body only starts `RebootTime`, which swaps the emitter mesh colour and back, and `EMPEnabled` is the
    /// shared empty stub — the light itself keeps shining (evidence/device-actions.json). The switch that really turns
    /// a light off is `LG_Light.SetEnabled(bool)`: it sets the light's game object active, its emitter mesh and its
    /// culling light, and the point-light override also switches its `C_Light`. Only a light this request actually
    /// switches from on to off owes a restore. A later native SetEnabled call gives that light back to the native
    /// flow, even when it writes the same false value: expiry must not overwrite its new decision. No zone-wide
    /// state snapshot is taken, and the zone's own list is never changed.
    /// </summary>
    internal sealed class ZoneLights : IDeviceWriter
    {
        private sealed class OwnedLight
        {
            internal OwnedLight(ZoneLights owner, string target, LG_Light light)
            { Owner = owner; Target = target; Light = light; NativeId = light.GetInstanceID(); NativePointer = light.Pointer; }
            internal ZoneLights Owner { get; }
            internal string Target { get; }
            internal LG_Light Light { get; }
            internal int NativeId { get; }
            internal IntPtr NativePointer { get; }
        }

        private readonly Dictionary<string, List<OwnedLight>> _taken = new(StringComparer.Ordinal);
        // The observer must not keep an ended session or its native objects alive. The writer owns its receipts;
        // the process-wide index only locates them across distinct interop wrappers of the same Unity object.
        private static readonly Dictionary<int, WeakReference<OwnedLight>> Owners = new();
        [ThreadStatic] private static LG_Light? _writing;
        [ThreadStatic] private static IntPtr _writingPointer;
        internal static bool HasPending => Owners.Count != 0;

        public string Category => DeviceCategories.EnvironmentLight;

        public string? Refusal(EntityReference target)
        {
            if (EnvironmentZone.Parse(target) == null) return DeviceCategories.TargetKindCode;
            if (EnvironmentActions.Zone(target) is not { } zone) return ResultCode.EnvironmentZoneMissing;
            if (EnvironmentActions.Lights(zone).Count == 0) return ResultCode.DeviceReceiverMissing;
            return MapLazyHooks.Ensure(MapLazyHooks.Group.ZoneLights) == null ? null : ResultCode.DeviceCategoryUnavailable;
        }

        public void Disable(EntityReference target)
        {
            PruneDestroyed();
            if (_taken.ContainsKey(target.Id)) return;
            // The six light-switch observations go on with the first hold; a level with no hold never carries them.
            if (MapLazyHooks.Ensure(MapLazyHooks.Group.ZoneLights) is { } refusal)
                throw new InvalidOperationException(ResultCode.DeviceCategoryUnavailable + ": " + refusal);
            var zone = EnvironmentActions.Zone(target) ?? throw new InvalidOperationException("The zone left the level.");
            var lights = EnvironmentActions.Lights(zone);
            var taken = new List<OwnedLight>(lights.Count);
            _taken.Add(target.Id, taken);
            try
            {
                foreach (var light in lights)
                {
                    if (light == null || !light.gameObject.activeSelf) continue;
                    var owned = new OwnedLight(this, target.Id, light);
                    taken.Add(owned);
                    Owners[owned.NativeId] = new WeakReference<OwnedLight>(owned);
                    SetEnabled(owned, false);
                }
                if (taken.Count == 0) _taken.Remove(target.Id);
            }
            catch (Exception error)
            {
                // The board records its hold only after Disable returns. Roll back these specific writes now,
                // including a setter that changed the object before throwing; otherwise nobody owns cleanup.
                try { Restore(target); }
                catch (Exception restoreError) { throw new AggregateException(error, restoreError); }
                throw;
            }
        }

        public void Restore(EntityReference target)
        {
            PruneDestroyed();
            if (!_taken.TryGetValue(target.Id, out var taken)) return;
            if (taken.Count == 0) { _taken.Remove(target.Id); return; }
            Exception? first = null;
            foreach (var owned in taken.ToArray())
            {
                var light = owned.Light;
                if (!Owners.TryGetValue(owned.NativeId, out var reference) || !reference.TryGetTarget(out var current)
                    || !ReferenceEquals(current, owned)) { Forget(owned); continue; }
                try
                {
                    // A native switch supersedes this receipt, and a destroyed or already enabled object owes
                    // no write. This is an inverse of our own off write, never a saved-state replay.
                    if (light != null && !light.gameObject.activeSelf) SetEnabled(owned, true);
                    Forget(owned);
                }
                catch (Exception error) { first ??= error; }
            }
            // Keep a failed light's receipt for an explicit writer retry or a later native takeover. The shared
            // switchboard removes its hold before calling us; this writer cannot supply its missing retry clock.
            if (first != null) throw first;
        }

        private void Forget(OwnedLight owned)
        {
            if (Owners.TryGetValue(owned.NativeId, out var reference) && reference.TryGetTarget(out var current)
                && ReferenceEquals(current, owned)) Owners.Remove(owned.NativeId);
            if (_taken.TryGetValue(owned.Target, out var taken))
            {
                taken.Remove(owned);
                if (taken.Count == 0) _taken.Remove(owned.Target);
            }
        }

        private void PruneDestroyed()
        {
            foreach (var owned in _taken.Values.SelectMany(value => value).Where(value => value.Light == null).ToArray())
                Forget(owned);
            foreach (var entry in Owners.ToArray())
                if (!entry.Value.TryGetTarget(out _)) Owners.Remove(entry.Key);
        }

        private static void SetEnabled(OwnedLight owned, bool enabled)
        {
            // The verified virtual overrides can call their base switch. Only this light and this synchronous
            // native call are ours; successful native calls on any other held light still revoke its receipt.
            var previous = _writing;
            var previousPointer = _writingPointer;
            _writing = owned.Light;
            // The receipt captured this identity while the object was readable. Do not re-read Unity or the
            // wrapper to recognise our own detoured call, which may be presented through a different wrapper.
            _writingPointer = owned.NativePointer;
            try
            {
                owned.Light.SetEnabled(enabled);
                if (owned.Light.gameObject.activeSelf != enabled)
                    throw new InvalidOperationException("device-light-readback-unconfirmed");
            }
            finally { _writing = previous; _writingPointer = previousPointer; }
        }

        internal static void NativeEnabled(LG_Light light, IntPtr before)
        {
            if (Owners.Count == 0 || ReferenceEquals(light, _writing)) return;
            if (before != IntPtr.Zero && before == _writingPointer) return;
            if (light == null || light == _writing) return;
            int id = light.GetInstanceID();
            if (!Owners.TryGetValue(id, out var reference)) return;
            if (reference.TryGetTarget(out var owned)) owned.Owner.Forget(owned);
            else Owners.Remove(id);
        }

        internal static string NativeObservationFailed(LG_Light light, IntPtr before)
        {
            // Unity identity was unreadable. The prefix captured this call's interop address before the native
            // switch; that still identifies a replacement wrapper if its post-call identity is unavailable.
            IntPtr pointer = before;
            string pointerFailure = "";
            if (pointer == IntPtr.Zero && !ReferenceEquals(light, null))
            {
                try { pointer = light.Pointer; }
                catch (Exception error) { pointerFailure = "; pointer=" + error.GetType().Name; }
            }
            if (pointer != IntPtr.Zero && pointer == _writingPointer)
                return "active authored native switch retains its restore";
            foreach (var reference in Owners.Values)
            {
                if (!reference.TryGetTarget(out var owned)
                    || !(ReferenceEquals(owned.Light, light)
                        || pointer != IntPtr.Zero && owned.NativePointer == pointer)) continue;
                owned.Owner.Forget(owned);
                return "matching authored light restore canceled";
            }
            // Neither identity phase resolved this wrapper. Do not guess an owner or clear unrelated holds.
            // A later identifiable native call can resolve the debt; this observation alone cannot prove it.
            return pointer == IntPtr.Zero ? "unverifiable native identity; no restore receipt changed" + pointerFailure
                : "no matching authored light restore";
        }
    }
}

/// <summary>Observe only light-exclusive native bodies checked in the build 20403457 dump and GameAssembly.
/// LG_Light.SetEnabled(bool, string) is a shared virtual-dispatch thunk: detouring its native address also
/// intercepts unrelated GUI objects before this observer can check their type. The base single-argument
/// overload has its own exclusive body.
/// Point and spot lights declare independent bodies for both overloads. Both ambient overloads call their
/// exclusive SetEnabledCommon body, so one observer covers them. Interop declaration checks and compilation
/// alone do not establish this native-address uniqueness or these native call edges.
/// Capture only identity and observe completion; never change arguments, results or original control flow.</summary>
internal static class DeviceLightEnabledObservation
{
    internal static void Capture(LG_Light __instance, out IntPtr __state)
    {
        __state = IntPtr.Zero;
        if (!DeviceActions.ZoneLights.HasPending || ReferenceEquals(__instance, null)) return;
        try { __state = __instance.Pointer; }
        catch (Exception error)
        {
            MapLog.Write(RuntimeLogLevel.Error, "map.device-write-failed", new RuntimeLogFacts
            {
                Detail = "Device light SetEnabled observation identity capture failed: " + error.GetType().Name
            });
        }
    }

    internal static void Observe(LG_Light __instance, IntPtr __state)
    {
        try { DeviceActions.ZoneLights.NativeEnabled(__instance, __state); }
        catch (Exception error)
        {
            string recovery = DeviceActions.ZoneLights.NativeObservationFailed(__instance, __state);
            // Reuse the device row's existing diagnostic and the Map sink's non-throwing failure boundary.
            // Reporting must not turn this observer's failure into a failure of the completed native switch.
            MapLog.Write(RuntimeLogLevel.Error, "map.device-write-failed", new RuntimeLogFacts
            {
                Detail = "Device light SetEnabled observation failed: " + error.GetType().Name + "; " + recovery
            });
        }
    }
}

// The six exclusive bodies, one declaration each. Entry ids and native evidence are the h10.map.light.* rows.

[HarmonyPatch(typeof(LG_Light), nameof(LG_Light.SetEnabled), new[] { typeof(bool) })]
internal static class LightBaseEnabledReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}

[HarmonyPatch(typeof(LG_PointLight), nameof(LG_PointLight.SetEnabled), new[] { typeof(bool) })]
internal static class PointLightEnabledReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}

[HarmonyPatch(typeof(LG_PointLight), nameof(LG_PointLight.SetEnabled), new[] { typeof(bool), typeof(string) })]
internal static class PointLightEnabledReasonReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}

[HarmonyPatch(typeof(LG_SpotLight), nameof(LG_SpotLight.SetEnabled), new[] { typeof(bool) })]
internal static class SpotLightEnabledReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}

[HarmonyPatch(typeof(LG_SpotLight), nameof(LG_SpotLight.SetEnabled), new[] { typeof(bool), typeof(string) })]
internal static class SpotLightEnabledReasonReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}

// Native-private, but public in the supplied interop wrapper; this exact declaration is the target.
[HarmonyPatch(typeof(LG_SpotLightAmbient), nameof(LG_SpotLightAmbient.SetEnabledCommon), new[] { typeof(bool) })]
internal static class AmbientLightEnabledCommonReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static void Prefix(LG_Light __instance, out IntPtr __state) => DeviceLightEnabledObservation.Capture(__instance, out __state);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Light __instance, IntPtr __state) => DeviceLightEnabledObservation.Observe(__instance, __state);
}
