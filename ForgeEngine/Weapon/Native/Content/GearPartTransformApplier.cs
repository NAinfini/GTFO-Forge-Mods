using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeEngine.Framework;
using Gear;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>Applies authored gear-part poses to a part holder the game has just finished spawning.
///
/// This is the one place in the package that writes presentation state, and what it writes is the presentation
/// layer only: the absolute local transform of a part and its children, whether that part is shown, and the added
/// parts hung beside them. It publishes no fact, sends nothing over the network and never touches game state, so
/// the effect of one package is the same on every machine that has the same package — each client poses the
/// models it can see (first person, the third person copy of another player's gear, the menu preview, an icon
/// render and a deployed sentry all assemble their own holder through the same native path).
///
/// Every value is absolute, which makes the write idempotent: a holder that spawns again, an icon that re-renders
/// or a checkpoint reload all read the same authored numbers instead of accumulating an offset.
///
/// Added parts are hung after the poses, through the spawner the native build supplies: each one is a copy of a
/// part model placed under the holder or under one of its parts, so it follows that part and never takes a slot,
/// an align or a grip. The spawner replaces the copy an earlier spawn of the same holder left, so they do not pile
/// up either.</summary>
internal sealed partial class GearPartTransformApplier
{
    private readonly GearPartTransformSnapshot _snapshot;
    private readonly WeaponLog _log;
    private readonly GearPartExtraAssets _extras;
    private readonly OnceReporter _once, _noted;

    internal GearPartTransformApplier(GearPartTransformSnapshot snapshot, Action<string> report, Action<string> note,
        WeaponLog log, GearPartExtraAssets extras)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        _noted = new OnceReporter(note ?? throw new ArgumentNullException(nameof(note)));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _extras = extras ?? throw new ArgumentNullException(nameof(extras));
    }

    /// <summary>Authored blocks this session will ever apply, so a test can tell an accepted document from a
    /// refused one without reading the log.</summary>
    internal int BlockCount => _snapshot.BlockCount;
    internal GearPartExtraAssets ExtraSpawner => _extras;

    /// <summary>One holder finished spawning; <paramref name="blockId"/> is its gear's block (the catalogue record, or the category's block for a gun
    /// rebuilt from another player's replicated components: <see cref="WeaponModelAssemblies.BlockOf"/>). A block with
    /// no authored pose is left exactly as the game built it.</summary>
    internal void Apply(GearPartHolder? holder, string? blockId)
    {
        if (holder == null) return;
        if (blockId == null || !_snapshot.TryGet(blockId, out var block)) return;
        foreach (var part in block.Parts)
        {
            var instance = Part(holder, part.Slot);
            if (instance == null)
            {
                Once("part:" + blockId + ":" + Number((byte)part.Slot), "weapon.gear-part-missing: block=" + blockId + " component=" + part.Slot);
                continue;
            }
            Write(instance.transform, part.Enabled, part.Position, part.EulerAngles, part.Scale);
            ApplyChildren(instance.transform, part.Slot, blockId, part.Children);
        }
        int placed = PlaceExtras(holder, blockId, block.Added, out int pending);
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.GearPartApplied, new RuntimeLogFacts
        {
            Values = block.Added.Length == 0
                ? new[] { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId), RuntimeLogValue.Of("parts", (long)block.Parts.Length) }
                : new[] { RuntimeLogValue.Of("block", blockId), EquipmentNativeAdapter.WeaponName(blockId), RuntimeLogValue.Of("parts", (long)block.Parts.Length),
                    RuntimeLogValue.Of("extras", Number(placed) + "/" + Number(block.Added.Length)), RuntimeLogValue.Of("pending", (long)pending) }
        });
    }

    /// <summary>Hangs every added part and counts only completed placements; native waits are separate pending results.
    /// The original native callback reports its eventual outcome through the same completion path. A parent slot the holder left empty and a refusal from the spawner are each
    /// reported once per added part.</summary>
    private int PlaceExtras(GearPartHolder holder, string blockId, GearPartAddedPart[] added, out int pending)
    {
        pending = 0;
        if (added.Length == 0) return 0;
        int placed = 0;
        foreach (var part in added)
        {
            var parent = part.Parent == null ? holder.transform : Part(holder, part.Parent.Value)?.transform;
            if (parent == null)
            {
                Once("extra-parent:" + blockId + ":" + part.Name, "weapon.gear-part-extra-parent-missing: block=" + blockId
                    + " name=" + part.Name + " parent=" + part.Parent);
                continue;
            }
            bool waiting = false, completed = false;
            var refusal = _extras.Place(holder, parent, part, result => { completed = true; ExtraCompleted(blockId, part, result); }, out waiting);
            if (completed) continue; // A reentrant native completion already reported this request's terminal result.
            if (refusal != null) { ExtraRefused(blockId, part, refusal, "request"); continue; }
            if (waiting) pending++;
            else placed++;
        }
        return placed;
    }

    private void ExtraRefused(string blockId, GearPartAddedPart part, string refusal, string phase)
    {
        var key = "extra:" + blockId + ":" + part.Name + ":" + phase + ":" + refusal;
        if (GearPartExtraOutcome.IsCancelled(refusal))
        {
            // The target ended while the request waited: nothing failed, so this is neither an I-DIAG error nor a
            // console warning (the game destroys menu-preview and icon holders as a matter of course). It is still told
            // once, as an info line, and never counted as a placement.
            try
            {
                _noted.Report(key, GearPartExtraOutcome.CancelledCode + ": block=" + blockId + " name=" + part.Name
                + " component=" + part.Component + " partId=" + Number(part.PartId) + " phase=" + phase + ": "
                + refusal.Substring(GearPartExtraOutcome.CancelledCode.Length + 2));
            }
            catch (Exception failure) { NativeCallFailure.Note("GearPartTransformApplier.ExtraRefused", failure); }
            return;
        }
        var first = _once.Count(key) == 0;
        try
        {
            if (first && _log.Logs(RuntimeLogLevel.Error))
            {
                var adjusted = new List<string>();
                var values = new List<RuntimeLogValue> { DiagnosticText("block", blockId, adjusted),
                    DiagnosticText("name", part.Name, adjusted), RuntimeLogValue.Of("component", part.Component.ToString()),
                    RuntimeLogValue.Of("partId", (long)part.PartId), RuntimeLogValue.Of("phase", phase),
                    RuntimeLogValue.Of("reason", refusal.StartsWith(WeaponLogCodes.GearPartShardRefused + ":", StringComparison.Ordinal)
                        ? "unregistered-dependency-key" : "placement-unavailable") };
                var detail = DiagnosticText("message", refusal, adjusted).Text;
                AddTextAdjustment(values, adjusted);
                _log.Write(RuntimeLogLevel.Error, WeaponLogCodes.GearPartExtraRefused, new RuntimeLogFacts { Values = values, Detail = detail });
            }
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartTransformApplier.ExtraRefused#2", failure); } // Diagnostic observers cannot turn a refusal into a successful placement.
        try
        {
            _once.Report(key, WeaponLogCodes.GearPartExtraRefused + ": block=" + blockId + " name=" + part.Name
            + " component=" + part.Component + " partId=" + Number(part.PartId) + ": " + refusal);
        }
        catch (Exception failure) { NativeCallFailure.Note("GearPartTransformApplier.ExtraRefused#3", failure); }
    }

    private void ExtraCompleted(string blockId, GearPartAddedPart part, string? refusal)
    {
        if (refusal != null) { ExtraRefused(blockId, part, refusal, "callback"); return; }
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        var adjusted = new List<string>();
        var values = new List<RuntimeLogValue> { DiagnosticText("block", blockId, adjusted),
            DiagnosticText("name", part.Name, adjusted), RuntimeLogValue.Of("parts", 0L), RuntimeLogValue.Of("extras", "1/1"),
            RuntimeLogValue.Of("pending", 0L), RuntimeLogValue.Of("phase", "callback") };
        AddTextAdjustment(values, adjusted);
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.GearPartApplied, new RuntimeLogFacts { Values = values });
    }

    internal static RuntimeLogValue DiagnosticText(string name, string value, List<string> adjusted)
    {
        // Preserve exact legal values. Unsafe/oversized inputs use the SDK's existing safe preview,
        // and explicitly identify which field is no longer an exact source string.
        var preview = RuntimeLogValue.Quote(name, value);
        bool safe = value.Length <= 256 && (preview.Text != "account-shaped" || value == "account-shaped");
        foreach (var character in value) if (character < ' ' || character == '\u007f') { safe = false; break; }
        if (safe) return RuntimeLogValue.Of(name, value);
        adjusted.Add(name);
        return preview;
    }

    internal static void AddTextAdjustment(List<RuntimeLogValue> values, List<string> adjusted)
    {
        if (adjusted.Count != 0) values.Add(RuntimeLogValue.Of("textAdjusted", string.Join(",", adjusted)));
    }

    /// <summary>Applies the whole authored child tree, not just its first level: a child entry may carry
    /// `children` of its own, and every node carries the full path from the part, so each one is resolved against
    /// the part's own transform — the hierarchy lookup shape the game itself understands. A path that is not
    /// there is reported once and its own subtree is dropped with it: nothing below a missing node can resolve,
    /// so the highest missing node is the one diagnostic that branch gets.</summary>
    private void ApplyChildren(Transform part, GearPartSlot slot, string blockId, GearPartChild[] children)
    {
        foreach (var child in children)
        {
            var target = part.Find(child.Path);
            if (target == null)
            {
                Once("child:" + blockId + ":" + Number((byte)slot) + ":" + child.Path, "weapon.gear-part-child-missing: block=" + blockId
                    + " component=" + slot + " path=" + child.Path);
                continue;
            }
            Write(target, child.Node.Enabled, child.Node.Position, child.Node.EulerAngles, child.Node.Scale);
            ApplyChildren(part, slot, blockId, child.Node.Children);
        }
    }

    /// <summary>Absolute local values, written only where the document said something: an omitted field keeps the
    /// pose the game built instead of being reset to a default the author never asked for.</summary>
    private static void Write(Transform transform, bool? enabled, Vector3? position, Vector3? eulerAngles, Vector3? scale)
    {
        if (enabled != null) transform.gameObject.SetActive(enabled.Value);
        if (position != null) transform.localPosition = position.Value;
        if (eulerAngles != null) transform.localEulerAngles = eulerAngles.Value;
        if (scale != null) transform.localScale = scale.Value;
    }

    /// <summary>The slot-to-property table, spelled out: the game's `eGearComponent` member to the holder field
    /// that holds that part. No name is ever reflected on, so a renamed field is a compile error, not a silent
    /// no-op at runtime.</summary>
    internal static GameObject? Part(GearPartHolder holder, GearPartSlot slot) => slot switch
    {
        GearPartSlot.FrontPart => holder.FrontPart,
        GearPartSlot.FrontPartAttachmentA => holder.FrontPartAttachmentA,
        GearPartSlot.FrontPartAttachmentB => holder.FrontPartAttachmentB,
        GearPartSlot.ReceiverPart => holder.ReceiverPart,
        GearPartSlot.ReceiverPartAttachment => holder.ReceiverPartAttachment,
        GearPartSlot.StockPart => holder.StockPart,
        GearPartSlot.SightPart => holder.SightPart,
        GearPartSlot.MagPart => holder.MagPart,
        GearPartSlot.FlashlightPart => holder.FlashlightPart,
        GearPartSlot.ToolMainPart => holder.ToolMainPart,
        GearPartSlot.ToolMainPartAttachment => holder.ToolMainPartAttachment,
        GearPartSlot.ToolGripPart => holder.ToolGripPart,
        GearPartSlot.ToolDeliveryPart => holder.ToolDeliveryPart,
        GearPartSlot.ToolDeliveryPartAttachment => holder.ToolDeliveryPartAttachment,
        GearPartSlot.ToolPayloadPart => holder.ToolPayloadPart,
        GearPartSlot.ToolTargetingPart => holder.ToolTargetingPart,
        GearPartSlot.ToolScreenPart => holder.ToolScreenPart,
        GearPartSlot.MeleeHeadPart => holder.MeleeHeadPart,
        GearPartSlot.MeleeNeckPart => holder.MeleeNeckPart,
        GearPartSlot.MeleeHandlePart => holder.MeleeHandlePart,
        GearPartSlot.MeleePommelPart => holder.MeleePommelPart,
        _ => null
    };

    // A holder spawns again on every view change and every icon render, so a refusal that repeated would flood the
    // log; one line per authored entry, and one per child path, is the whole diagnostic budget for it.
    private void Once(string key, string message) => _once.Report(key, message);

    private static string Number(byte value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A non-null completion result is a refusal, except when it was built by <see cref="Cancelled"/>: the holder,
/// part, session or request the placement was waiting for ended first (the game destroys menu-preview and icon holders
/// while their shard is still loading). A cancelled placement is not applied and not counted, but it is no failure.</summary>
internal static class GearPartExtraOutcome
{
    internal const string CancelledCode = "weapon.gear-part-extra-cancelled";
    internal static string Cancelled(string reason) => CancelledCode + ": " + reason;
    internal static bool IsCancelled(string refusal) => refusal.StartsWith(CancelledCode + ": ", StringComparison.Ordinal);
}
