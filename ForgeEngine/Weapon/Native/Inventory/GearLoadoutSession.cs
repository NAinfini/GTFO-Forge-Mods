using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ForgeCore;
using ForgeEngine.Framework;
using GameData;
using Gear;
using Player;

namespace ForgeWeapon.Native;

/// <summary>Per-call offer intersection and local gear admission. Never writes a shared pool or favorites.</summary>
internal sealed class GearLoadoutSession
{
    private static readonly LoadoutSlot[] Slots = { LoadoutSlot.GearStandard, LoadoutSlot.GearSpecial, LoadoutSlot.GearClass };
    private readonly LoadoutPolicySnapshot _policies;
    private readonly Action<string> _report;
    private readonly WeaponLog _log;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly OnceReporter _once = new();
    private ForgeLoadoutPolicy? _policy;
    private bool _resolved, _failed, _disposed;

    internal GearLoadoutSession(LoadoutPolicySnapshot policies, Action<string> report, WeaponLog log)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        GameEvents.ExpeditionSelected += Reselect;
    }

    private void Reselect(uint? rundown, int tier, int index) => _resolved = false;

    internal LoadoutSlotPolicy? SlotPolicy(LoadoutSlot slot)
    {
        CheckThread();
        if (_disposed || _failed || GameStateManager.IsInExpedition) return null;
        return Policy()?.Slot(slot);
    }

    internal bool Projects(InventorySlot slot) => IsCovered(slot) && SlotPolicy((LoadoutSlot)slot) != null;

    internal IReadOnlyList<GearIDRange>? Offer(InventorySlot slot, IReadOnlyList<GearIDRange>? offered)
    {
        CheckThread();
        if (offered == null || !Projects(slot)) return null;
        try
        {
            var decision = GearLoadoutFilter.Filter((LoadoutSlot)slot, offered, Identity, Policy()!);
            Report(decision);
            if (_failed) return offered;
            if (decision.Outcome == GearLoadoutOutcome.Failed)
                Fault(new InvalidOperationException(decision.Diagnostics.Count == 0 ? "The projection failed." : decision.Diagnostics[0]));
            return decision.Keep;
        }
        catch (Exception error) { Fault(error); return offered; }
    }

    /// <summary>Saved favorites and predefined gear bypass the picker. Resolve the same BaseItem/slot the native
    /// body reads, then intersect the current original offers. Only this call's
    /// argument changes. Null refuses an empty allowed slot.</summary>
    internal GearIDRange? Admit(GearIDRange requested)
    {
        CheckThread();
        if (_disposed || _failed || GameStateManager.IsInExpedition) return requested;
        try
        {
            if (Policy() == null) return requested;
            var item = ItemDataBlock.GetBlock(requested.GetCompID(eGearComponent.BaseItem));
            if (item == null) throw new InvalidOperationException("The requested gear has no base item block.");
            var slot = item.inventorySlot;
            if (!Projects(slot)) return requested;
            var offered = GearManager.GetAllGearForSlot(slot);
            if (offered == null) throw new InvalidOperationException("The gear offer is unavailable.");
            var keep = Offer(slot, GearOffersForSlot.Items(offered));
            if (_failed || keep == null) return requested;
            var wanted = new HashSet<uint>(Policy()!.Slot((LoadoutSlot)slot).OfflineGearIds);
            if (GearLoadoutFilter.Match(Identity(requested), wanted, out var requestedId))
                foreach (var gear in keep)
                    if (GearLoadoutFilter.Match(Identity(gear), wanted, out var offeredId) && requestedId == offeredId)
                        return requested;
            return keep.Count == 0 ? null : keep[0];
        }
        catch (Exception error) { Fault(error); return requested; }
    }

    internal void Fault(Exception error)
    {
        _failed = true;
        _policy = null;
        try { _report("weapon.loadout-policy-failed: " + RuntimeDiagnostics.Describe(error)); }
        catch (Exception reporting) { NativeFault.Note("weapon.loadout.report", reporting); }
    }

    internal void Dispose()
    {
        CheckThread();
        _disposed = true;
        GameEvents.ExpeditionSelected -= Reselect;
        _policy = null;
        _once.Reset();
    }

    private ForgeLoadoutPolicy? Policy()
    {
        if (_resolved) return _policy;
        if (!Level.TryActiveExpedition(out var rundown, out _, out _)) return null;
        _resolved = true;
        if (!_policies.TryGet(rundown, out var policy))
        {
            _policy = null;
            Once("inactive:" + Number(rundown), "weapon.loadout-policy-inactive reason=rundown-mismatch rundown=" + Number(rundown));
            return null;
        }
        _policy = policy;
        Once("active:" + Number(rundown), "weapon.loadout-policy-active rundown=" + Number(rundown)
            + " policy=" + policy.RelativePath + " slots=" + SlotSummary(policy));
        return _policy;
    }

    private static GearLoadoutIdentity Identity(GearIDRange? gear)
        => gear == null ? new GearLoadoutIdentity(null, 0)
            : new GearLoadoutIdentity(gear.PlayfabItemInstanceId, gear.GetCompID(eGearComponent.Category));

    private static bool IsCovered(InventorySlot slot)
        => slot is InventorySlot.GearStandard or InventorySlot.GearSpecial or InventorySlot.GearClass;

    private static string SlotSummary(ForgeLoadoutPolicy policy)
    {
        var text = new StringBuilder(64);
        foreach (var slot in Slots)
        {
            if (text.Length != 0) text.Append(',');
            text.Append(slot).Append('=').Append(string.Join("|", policy.Slot(slot).OfflineGearIds));
        }
        return text.ToString();
    }

    private void Report<T>(GearLoadoutDecision<T> decision)
    {
        foreach (var line in decision.Diagnostics)
        {
            var end = line.StartsWith("weapon.loadout-slot ", StringComparison.Ordinal)
                ? line.IndexOf(" offered=", StringComparison.Ordinal)
                : line.StartsWith("weapon.loadout-policy-unmatched ", StringComparison.Ordinal)
                    ? line.IndexOf(" missing=", StringComparison.Ordinal) : -1;
            Once(end < 0 ? line : line[..end], line);
        }
    }

    private void Once(string key, string line)
    {
        if (!_once.ShouldReport(key)) return;
        var space = line.IndexOf(' ');
        var code = (space < 0 ? line : line[..space]).TrimEnd(':');
        var level = code == WeaponLogCodes.LoadoutPolicyFailed ? RuntimeLogLevel.Error : RuntimeLogLevel.Info;
        try { _log.Text(level, code, space < 0 ? null : line[(space + 1)..]); }
        catch (Exception reporting) { Fault(reporting); }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The loadout policy requires the session's own simulation thread.");
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
