using System;
using System.Collections.Generic;
using GameData;
using ForgeCore;
using ForgeEngine.Framework;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

internal sealed partial class WeaponNativeSession
{
    partial void PrepareExtraResourceRetirement()
    {
        if (GearParts?.ExtraSpawner is { } extras) extras.PrepareResourceRetirement(contentDetach: true);
    }

    partial void PollExtraShards()
    {
        if (GearParts?.ExtraSpawner is { } extras) extras.PollForeignLoads();
    }

    internal static Func<bool>? CaptureExtraResourceRetirement(bool contentDetach)
    {
        var session = Current;
        if (session == null) return null;
        var world = session._kernel.WorldEpoch;
        var generation = session._contentGeneration + (contentDetach ? 1 : 0);
        return () => ReferenceEquals(Current, session) && !session._disposed && !session.Down
            && session._kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && session._kernel.WorldEpoch == world && session._contentGeneration == generation;
    }

    internal static WeaponLog? CaptureExtraLog() => Current?.Log;

    // A lease identifies one session/world/level incarnation, never a saved native value to restore.
    internal static Func<bool>? CaptureExtraPlacement()
    {
        var session = Current;
        if (session == null) return null;
        var world = session._kernel.WorldEpoch;
        var generation = session._contentGeneration;
        return () => ReferenceEquals(Current, session) && !session._disposed && !session.Down
            && session._kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && session._kernel.WorldEpoch == world && session._contentGeneration == generation;
    }
}

/// <summary>Places added parts from the game's own part assets, without the game's part spawner.
///
/// An added part is read the way the spawner reads a gear record's part: its part data block (for a tool delivery,
/// payload or targeting part, the block's entry for the named type) names the model and the asset shard it lives
/// in. Loaded models are copied at once; otherwise one native request serves this owner's waiting parts.
/// Only a verified increment is a releasable pin; reset/unknown ownership is retained and reported. Two sinks carry
/// the reports: <c>report</c> is the console warning for a problem the player of a build should see (a failed
/// native read, a changed shard identity, a surviving copy), <c>note</c> is the info line for the designed paths —
/// a shard Forge had to load itself, an owned increment, a retention at a lifecycle boundary, a request the game
/// cancelled by destroying its holder. Every pin is stated once at acquisition; a release repeats it only when it
/// has something new to say. The copy is presentation only: it is instantiated
/// under an inactive staging object so none of the part's own scripts wake, every component but its transforms,
/// meshes, renderers and LOD group is removed, it takes the layer of the node it hangs under, and the holder's own
/// spawner gives it the gear's materials. A copy an earlier spawn of the same node left is replaced, never
/// stacked.</summary>
internal sealed class GearPartExtraAssets : IDisposable
{
    private const string NamePrefix = "ForgeExtra.";
    internal const string ShardRefusedCode = WeaponLogCodes.GearPartShardRefused;
    private readonly OnceReporter _once, _noted;
    private readonly OnceReporter _logged = new();
    private readonly Dictionary<string, List<Pending>> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForeignLoad> _foreign = new(StringComparer.Ordinal);
    /// <summary>The per-tick copy of <see cref="_foreign"/>'s keys the poll walks while it removes entries.</summary>
    private readonly List<string> _foreignShards = new();
    /// <summary>How long a request waits for another caller's native load before it is refused as unregistered.</summary>
    internal const long ForeignLoadLimitMs = 60_000;
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    // Names are presentation labels, never proof that an object belongs to this spawner.
    private readonly Dictionary<HolderLifetime, Dictionary<IntPtr, Dictionary<string, GameObject>>> _placed = new();
    private readonly Dictionary<IntPtr, HolderLifetime> _holders = new();
    private readonly List<HolderLifetime> _retiredHolders = new();
    private readonly Dictionary<long, ShardPin> _pins = new();
    private readonly Dictionary<(HolderLifetime Holder, IntPtr Parent, string Name), object> _requests = new();
    private readonly string _pinOwner = Guid.NewGuid().ToString("N");
    private long _generation, _nextPin;
    private bool _cloneLifetimeUnknown, _clearing;
    private int _constructing;
    private GameObject? _staging;
    private bool _disposed;

    internal GearPartExtraAssets(Action<string> report, Action<string> note)
    {
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _noted = new OnceReporter(note ?? throw new ArgumentNullException(nameof(note)));
    }

    public string? Place(GearPartHolder holder, Transform parent, GearPartAddedPart part, Action<string?>? completed, out bool pending)
    {
        pending = false;
        if (_disposed || _clearing) return "the extra-part owner has ended";
        if (holder == null || parent == null) return "the extra-part holder or parent is no longer available";
        ForgetDestroyedHolders();
        var sessionCurrent = WeaponNativeSession.CaptureExtraPlacement();
        if (sessionCurrent == null || !sessionCurrent()) return "the weapon session is no longer available";
        if (!_holders.TryGetValue(holder.Pointer, out var lifetime) || lifetime.Holder == null)
            _holders[holder.Pointer] = lifetime = new(holder, _generation);
        if (lifetime.Retiring) return "the extra-part holder is being destroyed";
        var generation = _generation;
        var placement = (lifetime, parent.Pointer, part.Name);
        var request = new object();
        bool ownerCurrent() => !_disposed && !_clearing && generation == _generation && sessionCurrent();
        bool current() => ownerCurrent() && !lifetime.Retiring && lifetime.Holder != null
            && _requests.TryGetValue(placement, out var active) && ReferenceEquals(active, request);
        var data = GeneralData(part, out var refusal);
        if (data == null) return refusal;
        var model = data.Model;
        if (string.IsNullOrEmpty(model)) return "the part block names no model";
        _requests[placement] = request;
        var log = WeaponNativeSession.CaptureExtraLog();
        // A newer request completes the superseded wait as a refusal, never as a placed part.
        var superseded = new List<Pending>();
        foreach (var pendingQueue in _waiting.Values)
            pendingQueue.RemoveAll(item =>
            {
                bool same = ReferenceEquals(item.Lifetime, lifetime) && item.Parent.Pointer == parent.Pointer && item.Part.Name == part.Name;
                if (same) superseded.Add(item);
                return same;
            });
        foreach (var item in superseded) Complete(item, GearPartExtraOutcome.Cancelled("the extra-part request was superseded"));
        if (!current()) return "the placement owner ended before native construction";
        var shard = GameAssets.ShardName(data.AssetBundle, data.BundleShard);
        GameShardState before;
        try { before = GameAssets.ReadShard(shard); }
        catch (Exception error)
        {
            ReportError(shard, "request-state", error);
            return "asset shard " + shard + " has no trustworthy native state; load ownership is unknown: "
                + error.GetType().FullName + ": " + error.Message;
        }
        bool waiting = _waiting.TryGetValue(shard, out var queue);
        // A native load resets the key to 1 before its manifest registers, so a positive count without a manifest
        // is someone else's in-flight load (the game loads the shards of the gear a level starts with while the
        // level generates). It is joined, never restarted: the request waits for that load to register its manifest
        // (PollForeignLoads), exactly as GearEffectAssets waits for the same state. A count <= 0 without a manifest
        // is the ordinary fully-unloaded state (Unload decrements to <= 0, removes the manifest and leaves the key),
        // which native Load itself restarts.
        if (!before.Registered && before.HasCount && before.Count > 0 && !waiting)
        {
            queue = new List<Pending> { new(holder, parent, part, model, current, completed, log, lifetime, ownerCurrent) };
            _waiting[shard] = queue;
            _foreign[shard] = new ForeignLoad(data, queue, Clock(), before.Count);
            pending = true;
            return null;
        }
        if (before.HasManifest && !before.Registered
            || before.Registered && (!before.HasCount || before.Count <= 0 || before.Count == int.MaxValue))
            return "asset shard " + shard + " has an untrustworthy dependency count; load ownership is unknown";
        // The native generic getter throws for a missing key; null is not its miss contract.
        var asset = GameAssets.Get<GameObject>(model);
        // Only a live manifest proves the shard is loaded and counted. A model that is still in the asset lookup with
        // no manifest and no positive count is a shard being torn down (native Unload removes the manifest first; the
        // lookup entry goes only when the unloaded scene's loader is destroyed) or a leftover registration. Native
        // LoadShardAsync keys on the manifest alone and, on a miss, resets the count to 1 and loads the scene, so the
        // lookup hit is no reason to refuse: such a request falls through to Load below and is placed in its callback.
        if (asset != null && before.Registered)
        {
            AcquireLoadedPin(shard, before, ownerCurrent, lifetime);
            return Copy(holder, parent, part, asset, current, lifetime);
        }
        if (waiting)
        {
            AttachHolder(shard, lifetime);
            queue!.Add(new Pending(holder, parent, part, model, current, completed, log, lifetime, ownerCurrent));
            pending = true;
            return null;
        }
        // Only an empty or fully-unloaded native state (no key, or no manifest with count <= 0, whether or not a stale
        // model is still in the lookup) or a live registration with a valid count reaches Load. Never reset another
        // caller's in-flight (count > 0, no manifest) key to one.
        queue = new List<Pending> { new(holder, parent, part, model, current, completed, log, lifetime, ownerCurrent) };
        pending = true;
        BeginLoad(shard, data, before, queue, ownerCurrent);
        return null;
    }

    /// <summary>Starts this owner's one native load of a shard nobody else is loading and holds the receipt for it.</summary>
    private void BeginLoad(string shard, GearPartGeneralData data, GameShardState before, List<Pending> queue, Func<bool> ownerCurrent)
    {
        _waiting[shard] = queue;
        var pin = NewPin(shard, before, ownerCurrent);
        foreach (var item in queue) pin.Holders.Add(item.Lifetime);
        _pins[pin.Id] = pin;
        Action<AsyncOperation> loaded = operation => ShardLoaded(shard, queue, pin, operation);
        try
        {
            // false means an existing manifest was incremented and callback(null) was queued.
            // The true branch is a candidate only when its one synchronous native Reset can be attributed to this
            // exact call. Its callback must still prove the matching registration before the receipt is releasable.
            var resetBefore = ShardDependencyResetReceipts.Snapshot(shard);
            bool startedScene = GameAssets.LoadShard(data.AssetBundle, data.BundleShard, loaded);
            var after = GameAssets.ReadShard(shard);
            if (!startedScene) ConfirmIncrement(pin, before, after);
            else ConfirmStartedLoadCandidate(pin, before, after, resetBefore);
        }
        catch (Exception error)
        {
            ReportError(shard, "load-or-readback", error);
            PinReport(pin, "unknown", "load-or-readback-threw:" + error.GetType().Name);
            throw; // Keep the exact queue/receipt: native work may already have been submitted.
        }
    }

    /// <summary>Settles the requests that joined a native load somebody else started. Called once per simulation tick.
    /// A registered manifest means that load finished: the shard is pinned once and every waiter is placed. A key that
    /// fell back to <c>&lt;= 0</c> without a manifest means the other load ended without leaving the shard, so this owner
    /// starts its own. A key that stays positive without a manifest past <see cref="ForeignLoadLimitMs"/> is refused
    /// with the original diagnostic, never waited on forever.</summary>
    internal void PollForeignLoads()
    {
        if (_foreign.Count == 0 || _disposed || _clearing) return;
        _foreignShards.Clear();
        _foreignShards.AddRange(_foreign.Keys);
        foreach (var shard in _foreignShards)
        {
            var wait = _foreign[shard];
            if (!_waiting.TryGetValue(shard, out var queue) || !ReferenceEquals(queue, wait.Queue) || queue.Count == 0)
            {
                _foreign.Remove(shard); // every waiter was cancelled or retired elsewhere
                continue;
            }
            try { SettleForeign(shard, wait, queue); }
            catch (Exception error)
            {
                ReportError(shard, "foreign-load", error);
                _foreign.Remove(shard);
                _waiting.Remove(shard);
                var refusal = error.GetType().FullName + ": " + error.Message;
                foreach (var item in queue.ToArray()) Complete(item, refusal);
            }
        }
    }

    private void SettleForeign(string shard, ForeignLoad wait, List<Pending> queue)
    {
        var state = GameAssets.ReadShard(shard);
        if (state.Registered && state.HasCount && state.Count > 0 && state.Count != int.MaxValue)
        {
            _foreign.Remove(shard);
            _waiting.Remove(shard);
            var owner = queue.Find(item => !item.Ended && item.Current());
            if (owner != null)
            {
                AcquireLoadedPin(shard, state, owner.Owner, owner.Lifetime);
                foreach (var item in queue) AttachHolder(shard, item.Lifetime);
            }
            foreach (var item in queue.ToArray()) Complete(item, Resolve(item, shard));
            return;
        }
        if (!state.Registered && !state.HasManifest && (!state.HasCount || state.Count <= 0))
        {
            _foreign.Remove(shard);
            var owner = queue.Find(item => !item.Ended && item.Current());
            if (owner == null)
            {
                _waiting.Remove(shard);
                foreach (var item in queue.ToArray()) Complete(item, GearPartExtraOutcome.Cancelled("the extra-part request ended before the shard was free to load"));
                return;
            }
            BeginLoad(shard, wait.Data, state, queue, owner.Owner);
            return;
        }
        if (state.HasManifest && !state.Registered || state.Registered && (!state.HasCount || state.Count <= 0 || state.Count == int.MaxValue))
        {
            _foreign.Remove(shard);
            _waiting.Remove(shard);
            foreach (var item in queue.ToArray())
                Complete(item, "asset shard " + shard + " has an untrustworthy dependency count; load ownership is unknown");
            return;
        }
        if (Clock() - wait.StartedMs < ForeignLoadLimitMs) return;
        _foreign.Remove(shard);
        _waiting.Remove(shard);
        foreach (var item in queue.ToArray())
            Complete(item, item.Ended ? null : ShardRefused(item.Log, item.Part, item.Model, shard, state.HasCount ? state.Count : wait.FirstCount, "callback"));
    }

    private bool AttachHolder(string shard, HolderLifetime lifetime)
    {
        bool found = false;
        foreach (var existing in _pins.Values)
            if (existing.Shard == shard) { existing.Holders.Add(lifetime); found = true; }
        return found;
    }

    private void AcquireLoadedPin(string shard, GameShardState before, Func<bool> current, HolderLifetime lifetime)
    {
        if (AttachHolder(shard, lifetime)) return;
        var pin = NewPin(shard, before, current);
        pin.Holders.Add(lifetime);
        _pins[pin.Id] = pin;
        try
        {
            GameAssets.IncreaseShardDependency(shard);
            ConfirmIncrement(pin, before, GameAssets.ReadShard(shard));
        }
        catch (Exception error)
        {
            ReportError(shard, "increase-or-readback", error);
            PinReport(pin, "unknown", "increase-or-readback-threw:" + error.GetType().Name);
            // An increment may already have happened. Never retry or compensate from a guessed count.
        }
    }

    private ShardPin NewPin(string shard, GameShardState before, Func<bool>? current = null)
        => new(shard, _generation, ++_nextPin, before, current, WeaponNativeSession.CaptureExtraLog());

    private void ConfirmIncrement(ShardPin pin, GameShardState before, GameShardState after)
    {
        if (pin.Ended || pin.Generation != _generation)
        {
            PinReport(pin, "unknown", "owner-ended-during-acquisition; no-retry");
            return;
        }
        pin.Owned = pin.Current != null && pin.Current() && before.Registered && before.HasCount && before.Count > 0 && before.Count < int.MaxValue
            && before.SameRegistration(after) && after.HasCount && after.Count == before.Count + 1;
        if (pin.Owned) pin.Registration = after;
        pin.AfterCount = after.HasCount ? after.Count : null;
        PinReport(pin, pin.Owned ? "owned" : "unknown", pin.Owned ? "exact-increment" : "reset-or-generation-unproven", expected: pin.Owned);
    }

    private void ConfirmStartedLoadCandidate(ShardPin pin, GameShardState before, GameShardState after, object resetBefore)
    {
        pin.AfterCount = after.HasCount ? after.Count : null;
        bool exactReset = ShardDependencyResetReceipts.ExactNext(pin.Shard, resetBefore, out var receipt);
        bool zeroBaseline = !before.HasCount || before.Count == 0;
        if (pin.Ended || pin.Generation != _generation || pin.Current == null || !pin.Current())
            PinReport(pin, "unknown", "owner-ended-during-acquisition; shard kept loaded");
        else if (!exactReset)
            PinReport(pin, "unknown", ReferenceEquals(resetBefore, receipt)
                ? "native-reset-observer-unproven; shard kept loaded"
                : "native-reset-generation-contended; shard kept loaded");
        else if (!zeroBaseline || !after.HasCount || after.Count != 1)
            PinReport(pin, "unknown", "native-reset-not-exact-plus-one; shard kept loaded");
        else if (!before.SameTables(after))
            PinReport(pin, "unknown", "native-table-generation-unproven; shard kept loaded");
        else
        {
            pin.ResetReceipt = receipt;
            pin.StartedLoadProofPending = true;
            PinReport(pin, "pending", "exact-reset-plus-one; awaiting-native-load-callback", expected: true);
        }
    }

    private void ConfirmStartedLoad(ShardPin pin, AsyncOperation? operation)
    {
        if (!pin.StartedLoadProofPending) return;
        pin.StartedLoadProofPending = false;
        try
        {
            var state = GameAssets.ReadShard(pin.Shard);
            pin.AfterCount = state.HasCount ? state.Count : null;
            string? reason = null;
            if (pin.Ended || pin.Generation != _generation || pin.Current == null || !pin.Current())
                reason = "owner-ended-before-load-callback";
            else if (operation == null) reason = "native-load-operation-unproven";
            else if (!pin.Before.SameTables(state)) reason = "native-table-generation-unproven";
            else if (!state.Registered || !state.HasCount || state.Count <= 0 || state.Count == int.MaxValue)
                reason = "native-registration-unproven";
            if (reason == null && (pin.ResetReceipt == null || !ShardDependencyResetReceipts.IsCurrent(pin.Shard, pin.ResetReceipt)))
            {
                // Another caller reset this shard's count after our Load (the game loads the same gear shard while the
                // level generates), so the count no longer shows which load it came from and the first-load receipt
                // proves nothing. The shard is registered now, so Forge takes its own exact increment on the stable
                // registration; the count already merged our load into the other caller's, and this increment is the
                // one release Forge owes.
                pin.ResetReceipt = null;
                GameAssets.IncreaseShardDependency(pin.Shard);
                ConfirmIncrement(pin, state, GameAssets.ReadShard(pin.Shard));
                return;
            }
            if (reason != null)
            {
                PinReport(pin, "unknown", reason + "; shard kept loaded");
                return;
            }
            pin.Owned = true;
            pin.Registration = state;
            PinReport(pin, "owned", "exact-first-load-plus-one", expected: true);
        }
        catch (Exception error)
        {
            ReportError(pin.Shard, "load-receipt", error);
            PinReport(pin, "unknown", "load-receipt-read-failed:" + error.GetType().Name + "; shard kept loaded");
        }
    }

    private void ReleasePin(ShardPin pin, bool lifetimeUnknown)
    {
        // Claim this receipt before any native call or report can re-enter cleanup. Unknown outcomes never retry.
        if (pin.Ended) return;
        pin.Ended = true;
        string reason;
        try
        {
            // A pin that never became owned was already stated, with its reason, when it was acquired; releasing it
            // changes nothing (no native call), so it is not stated a second time.
            if (!pin.Owned)
            {
                if (pin.StartedLoadProofPending)
                {
                    pin.StartedLoadProofPending = false;
                    PinReport(pin, "retained-or-unknown", "native-load-completion-unproven; shard kept loaded");
                }
                return;
            }
            else if (pin.RetirementCurrent == null || !pin.RetirementCurrent()) reason = "owner-generation-unproven";
            else if (lifetimeUnknown) reason = "clone-lifetime-unproven";
            else
            {
                var now = GameAssets.ReadShard(pin.Shard);
                pin.ReleaseCount = now.HasCount ? now.Count : null;
                if (pin.Registration is not { } registration || !registration.SameRegistration(now) || !now.HasCount)
                    reason = "native-generation-unproven";
                else if (pin.ResetReceipt != null && !ShardDependencyResetReceipts.IsCurrent(pin.Shard, pin.ResetReceipt))
                    reason = "native-reset-generation-unproven";
                else if (now.Count <= 0) reason = "nonpositive-dependency-count";
                else if (!pin.RetirementCurrent()) reason = "owner-generation-unproven";
                else
                {
                    // Unload performs the one decrement itself (0x24417b0), including 1 -> 0 after
                    // the other owners retire. This receipt and its dead copies authorize that call,
                    // not completion of native scene/manifest/asset cleanup. No report before the call.
                    pin.UnloadAttempted = true;
                    GameAssets.UnloadShard(pin.Shard, "Forge extra-part pin " + pin.Id);
                    PinReport(pin, "release-call-returned", "unload-once; scene-unload-not-claimed", expected: true);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            ReportError(pin.Shard, pin.UnloadAttempted ? "unload" : "release-state", error);
            reason = (pin.UnloadAttempted ? "unload-outcome-unknown-no-retry:" : "state-read-failed:") + error.GetType().Name;
        }
        // A lifecycle boundary (world change, checkpoint, session end) ended the lease before cleanup, so an owned pin is
        // kept rather than released into a generation it was not taken in. That is the designed outcome at every
        // boundary; every other reason is a native or ownership fact that did not hold and stays a warning.
        PinReport(pin, "retained-or-unknown", reason, expected: reason == "owner-generation-unproven");
    }

    private void PinReport(ShardPin pin, string state, string reason, bool expected = false)
    {
        // Retain the acquiring session's writer: a late callback cannot report against a replacement session.
        // Record before console observers can re-enter cleanup; neither observer may interrupt native work.
        var key = "pin:" + pin.Id + ":" + state;
        var once = expected ? _noted : _once;
        var first = once.Count(key) == 0;
        var level = expected ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
        try
        {
            if (first && pin.Log != null && pin.Log.Logs(level))
            {
                var adjusted = new List<string>();
                var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("owner", _pinOwner),
                    RuntimeLogValue.Of("receipt", pin.Id), RuntimeLogValue.Of("generation", pin.Generation),
                    GearPartTransformApplier.DiagnosticText("shard", pin.Shard, adjusted),
                    RuntimeLogValue.Of("state", state), RuntimeLogValue.Of("reason", reason),
                    RuntimeLogValue.Of("unloadAttempted", pin.UnloadAttempted),
                    RuntimeLogValue.Of("beforeKnown", pin.Before.HasCount),
                    RuntimeLogValue.Of("afterKnown", pin.AfterCount.HasValue),
                    RuntimeLogValue.Of("releaseBeforeKnown", pin.ReleaseCount.HasValue) };
                if (pin.Before.HasCount) values.Add(RuntimeLogValue.Of("before", (long)pin.Before.Count));
                if (pin.AfterCount is int after) values.Add(RuntimeLogValue.Of("after", (long)after));
                if (pin.ReleaseCount is int release) values.Add(RuntimeLogValue.Of("releaseBefore", (long)release));
                GearPartTransformApplier.AddTextAdjustment(values, adjusted);
                pin.Log.Write(level, expected ? WeaponLogCodes.GearPartShardPin : WeaponLogCodes.GearPartShardPinUnproven,
                    new RuntimeLogFacts { Values = values });
            }
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.PinReport", failure); } // Diagnostics cannot grant, revoke or consume a native receipt.
        try
        {
            once.Report(key, "weapon.gear-part-shard-pin: owner=" + _pinOwner
            + " receipt=" + pin.Id + " generation=" + pin.Generation + " shard=" + pin.Shard
            + " manifest=" + pin.Before.ManifestPointer + " manifests=" + pin.Before.Manifests
            + " acquiredManifest=" + (pin.Registration?.ManifestPointer ?? IntPtr.Zero)
            + " counts=" + pin.Before.Counts + " before=" + (pin.Before.HasCount ? pin.Before.Count.ToString() : "missing")
            + " after=" + (pin.AfterCount?.ToString() ?? "unknown")
            + " releaseBefore=" + (pin.ReleaseCount?.ToString() ?? "unknown") + " state=" + state + " reason=" + reason);
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.PinReport#2", failure); }
    }

    private string ShardRefused(WeaponLog? log, GearPartAddedPart part, string model, string shard, int count, string phase)
    {
        const string reason = "unregistered-dependency-key";
        var refusal = ShardRefusedCode + ": shard=" + shard + " key=" + shard + " count="
            + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " model=" + model + " reason=" + reason;
        try
        {
            var key = "shard-refused:" + _generation + ":" + part.Name + ":" + shard + ":" + count + ":" + phase;
            var first = _logged.ShouldReport(key);
            if (first && log != null && log.Logs(RuntimeLogLevel.Error))
            {
                var adjusted = new List<string>();
                var values = new List<RuntimeLogValue> { GearPartTransformApplier.DiagnosticText("shard", shard, adjusted),
                    GearPartTransformApplier.DiagnosticText("key", shard, adjusted), RuntimeLogValue.Of("count", (long)count),
                    GearPartTransformApplier.DiagnosticText("name", part.Name, adjusted),
                    RuntimeLogValue.Of("component", part.Component.ToString()), RuntimeLogValue.Of("partId", (long)part.PartId),
                    GearPartTransformApplier.DiagnosticText("model", model, adjusted), RuntimeLogValue.Of("phase", phase),
                    RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("outcome", "rejected") };
                GearPartTransformApplier.AddTextAdjustment(values, adjusted);
                log.Write(RuntimeLogLevel.Error, ShardRefusedCode, new RuntimeLogFacts { Values = values });
            }
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.ShardRefused", failure); } // A broken log observer never changes the refusal or interrupts the native callback queue.
        return refusal;
    }

    /// <summary>Completes each waiter once; a callback or a queued request alone never means the model was placed.</summary>
    private void ShardLoaded(string shard, List<Pending> expected, ShardPin pin, AsyncOperation? operation)
    {
        if (!_waiting.TryGetValue(shard, out var queue) || !ReferenceEquals(queue, expected)) return;
        _waiting.Remove(shard);
        ConfirmStartedLoad(pin, operation);
        foreach (var item in queue) Complete(item, Resolve(item, shard));
    }

    /// <summary>The terminal result of one waiting request once its shard is loaded: null when the part was placed.</summary>
    private string? Resolve(Pending item, string shard)
    {
        {
            string? refusal;
            try
            {
                // The game destroys menu-preview and icon holders (and a level start ends the session's world) while
                // their shard is still loading, so a vanished target is a cancelled request, not a failed placement.
                // Only a parent that is alive but no longer under its holder is a refusal.
                if (item.Holder == null || item.Parent == null)
                    refusal = GearPartExtraOutcome.Cancelled("the game destroyed the gear holder or its part before the shard finished loading");
                else if (!item.Current())
                    refusal = GearPartExtraOutcome.Cancelled("the weapon session, world or placement request ended before the shard finished loading");
                else if (item.Parent != item.Holder.transform && !item.Parent.IsChildOf(item.Holder.transform))
                    refusal = "the extra-part parent was detached from its holder before the shard finished loading";
                else
                {
                    var state = GameAssets.ReadShard(shard);
                    if (!state.Registered && state.HasCount)
                        refusal = ShardRefused(item.Log, item.Part, item.Model, shard, state.Count, "callback");
                    else if (!state.Registered || !state.HasCount || state.Count <= 0)
                        refusal = "asset shard " + shard + " completed without a trustworthy native manifest and dependency count; load ownership is unknown";
                    else
                    {
                        var asset = GameAssets.Get<GameObject>(item.Model);
                        refusal = asset == null ? "model " + item.Model + " is not in its loaded asset shard " + shard
                            : Copy(item.Holder, item.Parent, item.Part, asset, item.Current, item.Lifetime);
                    }
                }
            }
            catch (Exception exception)
            {
                ReportError(shard, "callback-placement", exception);
                refusal = exception.GetType().FullName + ": " + exception.Message;
            }
            return refusal;
        }
    }

    private void Complete(Pending item, string? refusal)
    {
        if (item.Ended) return;
        item.Ended = true;
        if (item.Completed != null)
        {
            try { item.Completed(refusal); }
            catch (Exception exception)
            {
                Once("completion:" + item.Part.Name + ":" + item.Model, "weapon.gear-part-extra-refused: name=" + item.Part.Name
                    + " completion observer failed: " + exception.GetType().Name);
            }
        }
        else if (refusal != null)
        {
            // The game destroying a holder, or a world ending, while a request waited is the ordinary course, not a failure.
            bool cancelled = GearPartExtraOutcome.IsCancelled(refusal);
            Once("load:" + item.Part.Name + ":" + item.Model, (cancelled
                    ? GearPartExtraOutcome.CancelledCode : WeaponLogCodes.GearPartExtraRefused) + ": name=" + item.Part.Name
                + " component=" + item.Part.Component + " partId=" + item.Part.PartId + ": "
                + (cancelled ? refusal.Substring(GearPartExtraOutcome.CancelledCode.Length + 2) : refusal), expected: cancelled);
        }
    }

    internal void PrepareResourceRetirement(bool contentDetach = false)
    {
        // One-shot permission for these exact receipts, captured before placement is invalidated.
        // World/checkpoint/fault cleanup cannot reauthorize an already stale placement owner.
        foreach (var pin in _pins.Values)
        {
            if (pin.Ended || pin.RetirementCurrent != null || !pin.Owned) continue;
            try
            {
                if (pin.Current != null && pin.Current())
                    pin.RetirementCurrent = WeaponNativeSession.CaptureExtraResourceRetirement(contentDetach);
            }
            catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.PrepareResourceRetirement", failure); } // No trustworthy owner lifetime: ReleasePin reports retention, never guesses.
        }
    }

    /// <summary>Capture the exact holder before the sentry's native destructor can clear its references.
    /// Native destruction keeps running; the returned continuation retires only our requests and copies.</summary>
    internal Action? CaptureHolderRetirement(GearPartHolder? holder)
    {
        ForgetDestroyedHolders();
        if (ReferenceEquals(holder, null) || !_holders.TryGetValue(holder.Pointer, out var lifetime) || lifetime.Retiring) return null;
        // A delayed destructor can carry a dead wrapper after a different live holder reused its address.
        // A dead wrapper is still accepted for its own dead tracked lifetime, never for the live replacement.
        if (holder == null && lifetime.Holder != null) return null;
        // Capture release permission before invalidating this holder's placement callbacks. Shared receipts
        // remain alive for their other holders and receive permission only when their last holder retires.
        foreach (var pin in _pins.Values)
            if (pin.Holders.Contains(lifetime) && pin.Holders.Count == 1 && pin.Owned && !pin.Ended
                && pin.Current != null && pin.Current())
                pin.RetirementCurrent = WeaponNativeSession.CaptureExtraResourceRetirement(contentDetach: false);
        lifetime.Retiring = true;
        return () =>
        {
            if (lifetime.Ended) return;
            lifetime.Ended = true; // Both postfix and finalizer may run; claim before any callback/native call.
            try
            {
                if (lifetime.Generation != _generation) return;
                _retiredHolders.Add(lifetime);
                RetireHolder(lifetime);
            }
            catch (Exception error)
            {
                Once("holder-retirement:" + lifetime.Pointer,
                "weapon.gear-part-extra-refused: phase=holder-retirement cleanup incomplete: " + error.GetType().Name);
            }
        };
    }

    private void ForgetDestroyedHolders()
    {
        for (int index = _retiredHolders.Count - 1; index >= 0; index--)
        {
            var lifetime = _retiredHolders[index];
            if (lifetime.Holder != null) continue;
            if (_holders.TryGetValue(lifetime.Pointer, out var current) && ReferenceEquals(current, lifetime))
                _holders.Remove(lifetime.Pointer);
            _retiredHolders.RemoveAt(index);
        }
    }

    internal void ReportHolderRetirementFailure(Exception error)
        => Once("holder-retirement-capture:" + error.GetType().Name,
            "weapon.gear-part-extra-refused: phase=holder-retirement native holder capture unavailable: " + error.GetType().Name);

    private void RetireHolder(HolderLifetime lifetime)
    {
        if (lifetime.Generation != _generation) return; // A delayed destructor cannot retire a replacement content lifetime.
        foreach (var key in new List<(HolderLifetime Holder, IntPtr Parent, string Name)>(_requests.Keys))
            if (ReferenceEquals(key.Holder, lifetime)) _requests.Remove(key);
        var abandoned = new List<Pending>();
        foreach (var shard in new List<string>(_waiting.Keys))
        {
            var queue = _waiting[shard];
            queue.RemoveAll(item => { if (!ReferenceEquals(item.Lifetime, lifetime)) return false; abandoned.Add(item); return true; });
            if (queue.Count == 0) _waiting.Remove(shard);
        }
        foreach (var item in abandoned) Complete(item, GearPartExtraOutcome.Cancelled("the extra-part holder was destroyed before placement completed"));
        if (lifetime.Constructing != 0) lifetime.LifetimeUnknown = true;
        List<Exception>? errors = null;
        DestroyHolderCopies(lifetime, ref errors);
        foreach (var pin in new List<ShardPin>(_pins.Values))
        {
            if (!pin.Holders.Remove(lifetime)) continue;
            pin.LifetimeUnknown |= lifetime.LifetimeUnknown;
            if (pin.Holders.Count != 0) continue;
            _pins.Remove(pin.Id);
            // Nested destructors can both have begun while this receipt still had multiple consumers.
            if (pin.RetirementCurrent == null && pin.Owned && pin.Current != null && pin.Current())
                pin.RetirementCurrent = WeaponNativeSession.CaptureExtraResourceRetirement(contentDetach: false);
            ReleasePin(pin, pin.LifetimeUnknown);
        }
        if (errors != null) throw new AggregateException("Extra-part holder cleanup failed.", errors);
    }

    private void DestroyHolderCopies(HolderLifetime lifetime, ref List<Exception>? errors)
    {
        if (lifetime.ClearingCopies) { lifetime.LifetimeUnknown = true; return; }
        if (!_placed.TryGetValue(lifetime, out var parents)) return;
        lifetime.ClearingCopies = true;
        try
        {
            foreach (var parent in new List<IntPtr>(parents.Keys))
            {
                var owned = parents[parent];
                foreach (var name in new List<string>(owned.Keys))
                {
                    try
                    {
                        var clone = owned[name];
                        if (clone != null)
                        {
                            if (clone.transform.parent != null && clone.transform.parent.Pointer == parent)
                            {
                                DestroyCopy(clone, lifetime);
                                if (clone != null) throw new InvalidOperationException("The extra-part copy is still alive after destruction.");
                            }
                            else lifetime.LifetimeUnknown = true;
                        }
                        owned.Remove(name);
                    }
                    catch (Exception error) { lifetime.LifetimeUnknown = true; (errors ??= new()).Add(error); }
                }
                if (owned.Count == 0) parents.Remove(parent);
            }
            if (parents.Count == 0) _placed.Remove(lifetime);
        }
        finally { lifetime.ClearingCopies = false; }
    }

    public void Dispose()
    {
        PrepareResourceRetirement();
        _disposed = true;
        Clear();
    }

    public void Clear()
    {
        if (_clearing) return;
        PrepareResourceRetirement();
        _clearing = true;
        var pins = new List<ShardPin>(_pins.Values);
        _pins.Clear();
        _generation++;
        _requests.Clear();
        foreach (var lifetime in _holders.Values)
        {
            lifetime.Retiring = true;
            if (lifetime.Constructing != 0) lifetime.LifetimeUnknown = true;
        }
        _holders.Clear();
        _retiredHolders.Clear();
        try
        {
            // Dropping callbacks ends placement permission, not ownership of a native pin.
            var abandoned = new List<Pending>();
            foreach (var queue in _waiting.Values) { abandoned.AddRange(queue); queue.Clear(); }
            _waiting.Clear();
            _foreign.Clear();
            foreach (var item in abandoned) Complete(item, GearPartExtraOutcome.Cancelled("the extra-part owner ended before placement completed"));
            List<Exception>? errors = null;
            if (_constructing != 0) _cloneLifetimeUnknown = true;
            foreach (var lifetime in new List<HolderLifetime>(_placed.Keys))
            {
                DestroyHolderCopies(lifetime, ref errors);
                _cloneLifetimeUnknown |= lifetime.LifetimeUnknown;
            }
            try
            {
                if (_staging != null) DestroyCopy(_staging);
                if (_staging != null) throw new InvalidOperationException("The extra-part staging object is still alive after destruction.");
                _staging = null;
            }
            catch (Exception error) { _cloneLifetimeUnknown = true; (errors ??= new()).Add(error); }
            foreach (var pin in pins) ReleasePin(pin, _cloneLifetimeUnknown || pin.LifetimeUnknown);
            if (errors != null) throw new AggregateException("Extra-part ownership release failed.", errors);
        }
        finally { _clearing = false; }
    }

    /// <summary>The model data the part names: the block's general data, or for a tool part with types the entry of
    /// the named type.</summary>
    private static GearPartGeneralData? GeneralData(GearPartAddedPart part, out string? refusal)
    {
        refusal = null;
        GearPartGeneralData? data = part.Component switch
        {
            GearPartSlot.FrontPart => GameDataBlockBase<GearFrontPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.ReceiverPart => GameDataBlockBase<GearReceiverPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.StockPart => GameDataBlockBase<GearStockPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.SightPart => GameDataBlockBase<GearSightPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.MagPart => GameDataBlockBase<GearMagPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.FlashlightPart => GameDataBlockBase<GearFlashlightPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.ToolMainPart => GameDataBlockBase<GearToolMainPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.ToolGripPart => GameDataBlockBase<GearToolGripPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.ToolScreenPart => GameDataBlockBase<GearToolScreenPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.MeleeHeadPart => GameDataBlockBase<GearMeleeHeadPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.MeleeNeckPart => GameDataBlockBase<GearMeleeNeckPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.MeleeHandlePart => GameDataBlockBase<GearMeleeHandlePartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.MeleePommelPart => GameDataBlockBase<GearMeleePommelPartDataBlock>.GetBlock(part.PartId)?.General,
            GearPartSlot.ToolDeliveryPart => Delivery(part),
            GearPartSlot.ToolPayloadPart => Payload(part),
            GearPartSlot.ToolTargetingPart => Targeting(part),
            _ => null,
        };
        if (data == null) refusal = "no part block " + part.PartId + " of " + part.Component
            + (part.Variant == null ? "" : " with type " + part.Variant) + " is loaded";
        return data;
    }

    private static GearPartGeneralData? Delivery(GearPartAddedPart part)
    {
        var entries = GameDataBlockBase<GearToolDeliveryPartDataBlock>.GetBlock(part.PartId)?.GeneralDatas;
        if (entries == null) return null;
        foreach (var entry in entries)
            if (entry != null && entry.Type.ToString() == part.Variant) return entry.Data;
        return null;
    }

    private static GearPartGeneralData? Payload(GearPartAddedPart part)
    {
        var entries = GameDataBlockBase<GearToolPayloadPartDataBlock>.GetBlock(part.PartId)?.GeneralDatas;
        if (entries == null) return null;
        foreach (var entry in entries)
            if (entry != null && entry.Type.ToString() == part.Variant) return entry.Data;
        return null;
    }

    private static GearPartGeneralData? Targeting(GearPartAddedPart part)
    {
        var entries = GameDataBlockBase<GearToolTargetingPartDataBlock>.GetBlock(part.PartId)?.GeneralDatas;
        if (entries == null) return null;
        foreach (var entry in entries)
            if (entry != null && entry.Type.ToString() == part.Variant) return entry.Data;
        return null;
    }

    /// <summary>Copies <paramref name="asset"/> under <paramref name="parent"/> at the authored pose, replacing the
    /// copy an earlier call left there.</summary>
    private string? Copy(GearPartHolder holder, Transform parent, GearPartAddedPart part, GameObject asset, Func<bool> current,
        HolderLifetime lifetime)
    {
        if (!current()) return "the placement owner ended before native construction";
        _constructing++;
        lifetime.Constructing++;
        try
        {
            var name = NamePrefix + part.Name;
            if (!_placed.TryGetValue(lifetime, out var parents))
                _placed[lifetime] = parents = new();
            if (!parents.TryGetValue(parent.Pointer, out var owned))
                parents[parent.Pointer] = owned = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            owned.TryGetValue(part.Name, out var previous);
            var clone = UnityEngine.Object.Instantiate<GameObject>(asset, Staging().transform);
            try
            {
                clone.name = name;
                if (!Strip(clone))
                {
                    DestroyCopy(clone, lifetime);
                    return "the part model keeps a component that is not presentation";
                }
                ForgeVisual.Models.SetLayer(clone.transform, parent.gameObject.layer);
                var transform = clone.transform;
                transform.SetParent(parent, false);
                transform.localPosition = part.Position;
                transform.localEulerAngles = part.EulerAngles;
                transform.localScale = part.Scale;
                try
                {
                    holder.m_gearPartSpawner?.UpdateMaterials(clone);
                }
                catch (Exception exception)
                {
                    Once("materials:" + part.Name, "weapon.gear-part-extra-materials: name=" + part.Name + " keeps the model's own materials: "
                        + exception.GetType().Name + ": " + exception.Message);
                }
                if (_disposed || !current())
                {
                    DestroyCopy(clone, lifetime);
                    return "the placement owner ended during native construction";
                }
                // Commit only after construction succeeds. A failed replacement leaves the old copy intact.
                if (previous != null)
                {
                    if (previous.transform.parent == parent)
                    {
                        DestroyCopy(previous, lifetime);
                        if (previous != null)
                        {
                            DestroyCopy(clone, lifetime);
                            return "the previous extra-part copy is still alive after destruction";
                        }
                    }
                    else lifetime.LifetimeUnknown = _cloneLifetimeUnknown = true;
                }
                owned[part.Name] = clone;
                return null;
            }
            catch
            {
                DestroyCopy(clone, lifetime);
                throw;
            }
        }
        catch { lifetime.LifetimeUnknown = _cloneLifetimeUnknown = true; throw; }
        finally { _constructing--; lifetime.Constructing--; }
    }

    private void DestroyCopy(GameObject clone, HolderLifetime? lifetime = null)
    {
        UnityEngine.Object.DestroyImmediate(clone);
        if (clone != null)
        {
            _cloneLifetimeUnknown = true;
            if (lifetime != null) lifetime.LifetimeUnknown = true;
        }
    }

    /// <summary>The inactive object a copy is made under, so no script on the part model runs Awake before it is
    /// stripped. It lives for the session and holds nothing between copies.</summary>
    private GameObject Staging()
    {
        if (_staging != null) return _staging;
        _staging = new GameObject("ForgeExtra.Staging");
        _staging.SetActive(false);
        _staging.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(_staging);
        return _staging;
    }

    /// <summary>Removes every component that is not presentation. A component another one requires is removed on a
    /// later pass once its dependent is gone; false when one still survives.</summary>
    private static bool Strip(GameObject root)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool left = false;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || Presentation(component)) continue;
                left = true;
                UnityEngine.Object.DestroyImmediate(component);
            }
            if (!left) return true;
        }
        foreach (var component in root.GetComponentsInChildren<Component>(true))
            if (component != null && !Presentation(component)) return false;
        return true;
    }

    private static bool Presentation(Component component)
        => component.TryCast<Transform>() != null || component.TryCast<MeshFilter>() != null
            || component.TryCast<MeshRenderer>() != null || component.TryCast<SkinnedMeshRenderer>() != null
            || component.TryCast<LODGroup>() != null;

    private void Once(string key, string message, bool expected = false)
    {
        // Diagnostics must not interrupt the game's shared callback queue or a cleanup batch.
        try { (expected ? _noted : _once).Report(key, message); }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.Once", failure); }
    }

    private void ReportError(string shard, string phase, Exception error)
    {
        // The raw BepInEx reporter preserves ToString (including inner exceptions and stack),
        // unlike bounded structured detail. Only an identical complete diagnostic is suppressed.
        var message = "weapon.gear-part-shard-error: shard=" + shard + " phase=" + phase
            + " exception=" + error.GetType().FullName + "\n" + error;
        var first = _once.Count(message) == 0;
        try
        {
            var log = first ? WeaponNativeSession.CaptureExtraLog() : null;
            if (log != null && log.Logs(RuntimeLogLevel.Error))
                log.Write(RuntimeLogLevel.Error, "weapon.gear-part-shard-error", new RuntimeLogFacts
                {
                    Values = new[] { RuntimeLogValue.Quote("shard", shard), RuntimeLogValue.Of("phase", phase),
                        RuntimeLogValue.Quote("exception", error.GetType().FullName ?? error.GetType().Name) },
                    Detail = RuntimeLogValue.Quote("message", error.Message).Text
                });
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.ReportError", failure); } // A structured observer failure must not suppress the complete raw exception.
        try { _once.Report(message, message); }
        catch (Exception failure) { NativeCallFailure.Note("GearPartExtraAssets.ReportError#2", failure); } // Diagnostics never interrupt the native shared callback queue.
    }

    private sealed class ShardPin
    {
        internal ShardPin(string shard, long generation, long id, GameShardState before, Func<bool>? current, WeaponLog? log)
        { Shard = shard; Generation = generation; Id = id; Before = before; Current = current; Log = log; }
        internal string Shard { get; }
        internal long Generation { get; }
        internal long Id { get; }
        internal GameShardState Before { get; }
        internal Func<bool>? Current { get; }
        internal WeaponLog? Log { get; }
        internal Func<bool>? RetirementCurrent;
        internal readonly HashSet<HolderLifetime> Holders = new();
        internal GameShardState? Registration;
        internal object? ResetReceipt;
        internal int? AfterCount, ReleaseCount;
        internal bool Owned, Ended, UnloadAttempted, LifetimeUnknown, StartedLoadProofPending;
    }

    private sealed class HolderLifetime
    {
        internal HolderLifetime(GearPartHolder holder, long generation)
        { Holder = holder; Pointer = holder.Pointer; Generation = generation; }
        internal readonly GearPartHolder Holder;
        internal readonly IntPtr Pointer;
        internal readonly long Generation;
        internal bool Retiring, Ended, LifetimeUnknown, ClearingCopies;
        internal int Constructing;
    }

    /// <summary>Requests waiting on a native load of their shard that another caller started.</summary>
    private sealed class ForeignLoad
    {
        internal ForeignLoad(GearPartGeneralData data, List<Pending> queue, long startedMs, int firstCount)
        { Data = data; Queue = queue; StartedMs = startedMs; FirstCount = firstCount; }
        internal GearPartGeneralData Data { get; }
        internal List<Pending> Queue { get; }
        internal long StartedMs { get; }
        internal int FirstCount { get; }
    }

    private sealed class Pending
    {
        internal Pending(GearPartHolder holder, Transform parent, GearPartAddedPart part, string model, Func<bool> current,
            Action<string?>? completed, WeaponLog? log, HolderLifetime lifetime, Func<bool> owner)
        {
            Holder = holder; Parent = parent; Part = part; Model = model; Current = current; Completed = completed; Log = log; Lifetime = lifetime;
            Owner = owner;
        }
        /// <summary>The owner lease (session, world, content generation) without this request's own identity; a shared shard
        /// receipt is taken under it so a superseded request does not void the receipt other waiters share.</summary>
        internal Func<bool> Owner { get; }
        internal Action<string?>? Completed { get; }
        internal WeaponLog? Log { get; }
        internal bool Ended;
        internal GearPartHolder Holder { get; }
        internal Transform Parent { get; }
        internal GearPartAddedPart Part { get; }
        internal string Model { get; }
        internal Func<bool> Current { get; }
        internal HolderLifetime Lifetime { get; }
    }
}
