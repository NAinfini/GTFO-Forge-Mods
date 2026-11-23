using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the one map-object state row: the interaction switch
/// (`forge.action.map.interaction_state`).
///
/// Three objects carry it. A terminal changes availability through its authoritative `ChangeState` request
/// (`TERM_State.Sleeping` is dormant; `TERM_State.Awake` is the state a player may walk up to). A
/// security door carries four own interactables — `LG_SecurityDoor_Locks.m_intOpenDoor`, `m_intUseKeyItem`,
/// `m_intHack` and `m_intCustomMessage`, each an `Interact_Base` — switched through `SetActive`, which owns
/// the native active flag, component and collider. An authored interaction point carries the same native
/// `Interact_Timed.IsActive` flag and is switched through that same entry. Placement remains owned by the level.
///
/// Both rows are host-authoritative world state, so both refuse on a peer that is not the host or a session that
/// is not ready, and neither is retried after a call that threw after it was entered.</summary>
internal sealed class MapStateActions : IDisposable
{
    internal const string DoorStateKey = "forge.map.interaction-policy";
    internal const int MaximumDoorEntries = 256;
    internal const string DoorLedgerPendingCode = "door-interaction-ledger-door-unresolved";

    /// <summary>One recipient reference as the terminal it addresses, or null when no terminal this session
    /// knows stands behind it. The session supplies the reader, so this half never reaches a second lookup of its
    /// own and a test can hand it one.</summary>
    internal delegate LG_ComputerTerminal? TerminalResolver(EntityReference? reference);

    /// <summary>One recipient reference as the security door it addresses, or null when no entrance door this
    /// session knows stands behind it. A weak door has no entrance to switch and answers null, exactly as the
    /// door execute rows treat it.</summary>
    internal delegate LG_SecurityDoor? DoorResolver(EntityReference? reference);

    /// <summary>One recipient reference as the authored interaction point it addresses, or null when the point is
    /// not placed in this world. The returned body owns the native active flag used for both the write and readback.</summary>
    internal delegate Interact_Timed? InteractionPointResolver(EntityReference? reference);

    /// <summary>One recipient reference as the root Forge built for a placed model or interaction point, or null when
    /// this world holds no such object. The visibility switch writes and reads back this root only.</summary>
    internal delegate GameObject? VisibilityResolver(EntityReference? reference);

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly TerminalResolver _terminals;
    private readonly DoorResolver _doors;
    private readonly InteractionPointResolver _interactionPoints;
    private readonly VisibilityResolver _visibility;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _doorState;
    private readonly List<DoorStateEntry> _doorEntries = new();
    private readonly List<ObjectStateEntry> _objectEntries = new();
    // The confirmed show/hide switches; `Enabled` is "shown".
    private readonly List<ObjectStateEntry> _visibilityEntries = new();
    private readonly HashSet<(string Target, int Revision, string Exception)> _doorApplyFailures = new();
    private int _doorRevision;
    private bool _doorPendingReported, _disposed;
    // The door replay is event-driven: it runs when an entry is written or restored, when the world changes, while a
    // door is still unbound or unreadable (retried each tick), and from the door's own OnDoorState postfix.
    private bool _doorDirty;
    private long _doorEpoch = -1;

    /// <summary>One authored door policy. Null means Forge does not own that part; a value is the absolute state
    /// the host confirmed. The address is stable across late join, checkpoint instance replacement and migration,
    /// while the cached reference is rebuilt for the receiving world's epoch.</summary>
    internal sealed class DoorStateEntry
    {
        internal readonly string Target;
        internal readonly long LifeEpoch;
        internal readonly bool? Open, UseKey, Hacking, CustomMessage;
        internal readonly int Revision;
        internal EntityReference? Reference;
        internal LG_SecurityDoor? Door;
        internal LG_SecurityDoor_Locks? Locks;

        internal DoorStateEntry(string target, long lifeEpoch, bool? open, bool? useKey, bool? hacking,
            bool? customMessage, int revision)
        {
            Target = target; LifeEpoch = lifeEpoch; Open = open; UseKey = useKey; Hacking = hacking;
            CustomMessage = customMessage; Revision = revision;
        }
    }

    /// <summary>The absolute switch the host confirmed for one authored interaction point or one level interaction
    /// trigger. Their native `SetActive` writes only the local machine, so every machine replays this entry.</summary>
    internal sealed class ObjectStateEntry
    {
        internal readonly string Target;
        internal readonly long LifeEpoch;
        internal readonly bool Enabled;
        internal readonly int Revision;
        internal ObjectStateEntry(string target, long lifeEpoch, bool enabled, int revision)
        { Target = target; LifeEpoch = lifeEpoch; Enabled = enabled; Revision = revision; }
    }

    internal MapStateActions(IForgeEngine kernel, Func<bool> ready,
        TerminalResolver terminals, DoorResolver doors, InteractionPointResolver interactionPoints, VisibilityResolver visibility,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _interactionPoints = interactionPoints ?? throw new ArgumentNullException(nameof(interactionPoints));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _doorState = DomainStateChannel.RegisterNativeState(kernel, DoorStateKey, CaptureDoorState, RestoreDoorState);
    }

    // ---- forge.action.map.interaction_state --------------------------------------------------------------

    internal CommandResult HandleInteraction(CommandContext context)
    {
        if (!_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Count(MapStateContract.InteractionRecipients) < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (context.Text(MapStateContract.InteractionOperation) switch
        { "enable" => true, "disable" => false, _ => (bool?)null } is not { } enable)
            return Outcome.Rejected(ResultCode.MapStateUnknownOperation);
        if (Interaction(context.Text(MapStateContract.InteractionKind)) is not { } interaction)
            return Outcome.Rejected(ResultCode.MapStateUnknownInteraction);
        var request = new InteractionRequest(this, enable, interaction);
        return context.EachTarget(ref request, InteractionStep, maximum: CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapInteractionState.RowTargetCount);
    }

    // ---- forge.action.map.object_visibility ---------------------------------------------------------------

    internal CommandResult HandleVisibility(CommandContext context)
    {
        if (!_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Count(MapStateContract.VisibilityRecipients) < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (context.Text(MapStateContract.VisibilityOperation) switch
        { "show" => true, "hide" => false, _ => (bool?)null } is not { } show)
            return Outcome.Rejected(ResultCode.MapStateUnknownOperation);
        var request = new VisibilityRequest(this, show);
        return context.EachTarget(ref request, VisibilityStep, maximum: CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapObjectVisibility.RowTargetCount);
    }

    private readonly record struct VisibilityRequest(MapStateActions Self, bool Show);

    private static readonly TargetStep<VisibilityRequest> VisibilityStep = static (ref VisibilityRequest request,
        EntityReference target, int index, RowWriter rows) =>
    {
        var (status, commit, code) = request.Self.Show(target, request.Show);
        rows.End(status, commit, code);
    };

    /// <summary>One recipient's show/hide through Unity's own `GameObject.SetActive`, read back through `activeSelf`.</summary>
    private (Status Status, Commit Commit, string Code) Show(EntityReference target, bool show)
    {
        if (!_kernel.IsEntityCurrent(target)) return Refused(ResultCode.StaleEntity);
        if (!IsVisibilityTarget(target.Id) || _visibility(target) is not { } root)
            return Refused(ResultCode.MapStateUnsupportedRecipient);
        var result = Run(() =>
        {
            if (root.activeSelf == show) return MapActionOutcome.AlreadyInState(ResultCode.Committed);
            root.SetActive(show);
            return MapActionOutcome.Issued(ResultCode.Committed);
        }, () => !root.WasCollected && root.activeSelf == show);
        if (result.Status == Status.Succeeded) Record(_visibilityEntries, target, show);
        return result;
    }

    private static bool IsVisibilityTarget(string target)
        => target.StartsWith(EntityKinds.MapObject + ":" + PlacedModelAddress.Category + "/", StringComparison.Ordinal)
            || target.StartsWith(EntityKinds.MapObject + ":" + InteractionPointAddress.Category + "/", StringComparison.Ordinal);

    /// <summary>What one recipient's step needs from the request.</summary>
    private readonly record struct InteractionRequest(MapStateActions Self, bool Enable, string Interaction);

    private static readonly TargetStep<InteractionRequest> InteractionStep = static (ref InteractionRequest request,
        EntityReference target, int index, RowWriter rows) =>
    {
        var (status, commit, code) = request.Self.Interact(target, request.Enable, request.Interaction);
        rows.End(status, commit, code);
    };

    /// <summary>One recipient's switch and its conclusion.</summary>
    private (Status Status, Commit Commit, string Code) Interact(EntityReference target, bool enable, string interaction)
    {
        if (!(target.Id.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal)
            ? target.WorldEpoch == _kernel.WorldEpoch && VanillaWorldObjects.IsCurrent(target) : _kernel.IsEntityCurrent(target)))
            return Refused(ResultCode.StaleEntity);
        if (target.Id.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal))
        {
            if (interaction != "all") return Refused(ResultCode.MapStateInteractionPart);
            if (!CanRecordObject(target.Id)) return Refused(ResultCode.DoorInteractionLedgerFull);
            var objectResult = Run(() => VanillaWorldObjects.Interaction(target.WorldEpoch,
                target.Id.Substring(EntityKinds.MapObject.Length + 1), enable));
            if (objectResult.Status == Status.Succeeded) RecordObject(target, enable);
            return objectResult;
        }
        if (target.Id.StartsWith(EntityKinds.MapObject + ":" + InteractionPointAddress.Category + "/", StringComparison.Ordinal))
        {
            if (interaction != "all") return Refused(ResultCode.MapStateInteractionPart);
            // The reference is the right kind and this world holds no point behind it.
            if (_interactionPoints(target) is not { } point) return Refused(ResultCode.StaleEntity);
            if (!CanRecordObject(target.Id)) return Refused(ResultCode.DoorInteractionLedgerFull);
            var pointResult = Run(() => Switch(point, enable), () => !point.WasCollected && point.IsActive == enable);
            if (pointResult.Status == Status.Succeeded) RecordObject(target, enable);
            return pointResult;
        }
        // A door is switched through its own four interactables; every other map object is a terminal, whose
        // one state carries the whole object and therefore accepts only `all`.
        if (_doors(target) is { } door)
        {
            if (!CanRecordDoor(target.Id)) return Refused(ResultCode.DoorInteractionLedgerFull);
            var doorResult = Run(() => SwitchDoor(door, interaction, enable), () => DoorMatches(door, interaction, enable));
            if (doorResult.Status == Status.Succeeded) RecordDoor(target, door, interaction, enable);
            return doorResult;
        }
        if (interaction != "all") return Refused(ResultCode.MapStateInteractionPart);
        if (_terminals(target) is not { } terminal) return Refused(ResultCode.MapStateUnsupportedRecipient);
        return Run(() => Switch(terminal, enable),
            () => terminal.CurrentStateName == (enable ? TERM_State.Awake : TERM_State.Sleeping));
    }

    private static (Status, Commit, string) Refused(string code) => (Status.Rejected, Commit.None, code);

    /// <summary>Request the manager's authoritative transition, never invoke its sync receiver directly.
    /// CurrentStateName is written by the real transition; Run confirms that readback before reporting success.</summary>
    private static MapActionOutcome Switch(LG_ComputerTerminal terminal, bool enable)
    {
        var wanted = enable ? TERM_State.Awake : TERM_State.Sleeping;
        var code = enable ? ResultCode.TerminalAwake : ResultCode.TerminalSleeping;
        if (terminal.CurrentStateName == wanted) return MapActionOutcome.AlreadyInState(code);
        terminal.ChangeState(wanted);
        return MapActionOutcome.Issued(code);
    }

    /// <summary>Switch the authored point through the native interaction body's own setter; the caller reads the same
    /// body's active flag before accepting the write.</summary>
    private static MapActionOutcome Switch(Interact_Timed point, bool enable)
    {
        if (point.IsActive == enable) return MapActionOutcome.AlreadyInState(ResultCode.Committed);
        point.SetActive(enable);
        return MapActionOutcome.Issued(ResultCode.Committed);
    }

    /// <summary>The interaction read of `forge.query.map.interaction_state`: whether the requested interaction of
    /// one map object is available right now. A security door's parts carry their own `Interact_Base.IsActive` and
    /// `all` is the conjunction of the four; a terminal's one state is available when it reads
    /// `TERM_State.Awake`. Both are the same members <see cref="Set"/> reads before it writes, so a read and a
    /// write of one object cannot describe two different states. An object this world cannot resolve, or one
    /// whose own parts did not read, answers null and the row refuses instead of reporting it disabled.</summary>
    internal MapStateContract.InteractionReading? ReadInteraction(EntityReference target, string interaction)
    {
        if (!(target.Id.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal)
            ? target.WorldEpoch == _kernel.WorldEpoch && VanillaWorldObjects.IsCurrent(target) : _kernel.IsEntityCurrent(target))) return null;
        if (target.Id.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal))
        {
            if (interaction != "all") return null;
            return VanillaWorldObjects.TryResolve(target.WorldEpoch, target.Id.Substring(EntityKinds.MapObject.Length + 1),
                out var component, out _) && component is LG_InteractWorldEventTrigger interact
                ? new MapStateContract.InteractionReading(interact.IsActive) : null;
        }
        if (target.Id.StartsWith(EntityKinds.MapObject + ":" + InteractionPointAddress.Category + "/", StringComparison.Ordinal))
        {
            if (interaction != MapStateContract.InteractionTargets[0])
                throw new RuntimeContractException(MapStateContract.QueryInteractionCode,
                    "An authored interaction point carries no part interactions: " + interaction);
            return _interactionPoints(target) is { } point
                ? new MapStateContract.InteractionReading(point.IsActive) : null;
        }
        if (_doors(target) is { } door)
        {
            var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
            if (locks == null || locks.WasCollected) return null;
            if (Parts(locks, interaction) is not { } parts) return null;
            bool enabled = true;
            foreach (var part in parts)
            {
                if (part == null || part.WasCollected) return null;
                enabled &= part.IsActive;
            }
            return new MapStateContract.InteractionReading(enabled);
        }
        if (interaction != MapStateContract.InteractionTargets[0])
            throw new RuntimeContractException(MapStateContract.QueryInteractionCode,
                "A terminal carries no part interactions: " + interaction);
        if (_terminals(target) is not { } terminal) return null;
        return new MapStateContract.InteractionReading(terminal.CurrentStateName == TERM_State.Awake);
    }

    /// <summary>Switch only the named parts through their native active flag. Validate the complete recipient
    /// before entering any setter; never move interactions or replay a cached level placement.</summary>
    private MapActionOutcome SwitchDoor(LG_SecurityDoor door, string interaction, bool enable)
    {
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks == null || locks.WasCollected) return MapActionOutcome.Refused(ResultCode.DoorInteractionUnreadable);
        if (Parts(locks, interaction) is not { } parts) return MapActionOutcome.Refused(ResultCode.MapStateUnknownInteraction);
        var code = enable ? ResultCode.DoorInteractionsEnabled : ResultCode.DoorInteractionsDisabled;
        var wrote = false;
        foreach (var part in parts)
            if (part == null || part.WasCollected) return MapActionOutcome.Refused(ResultCode.DoorInteractionUnreadable);
        foreach (var part in parts)
        {
            // All requested parts were validated before any write.
            if (part!.IsActive == enable) continue;
            part.SetActive(enable);
            wrote = true;
        }
        return wrote ? MapActionOutcome.Issued(code) : MapActionOutcome.AlreadyInState(code);
    }

    private static bool DoorMatches(LG_SecurityDoor door, string interaction, bool enable)
    {
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks == null || locks.WasCollected || Parts(locks, interaction) is not { } parts) return false;
        foreach (var part in parts)
            if (part == null || part.WasCollected || part.IsActive != enable) return false;
        return true;
    }

    /// <summary>The game has no replicated interaction mask: `Interact_Base.SetActive` only writes this machine's
    /// active bit, Behaviour and Collider. The confirmed absolute policy therefore travels in Runtime's existing
    /// native-state snapshot/delta document. No door status is forged and no second network event is introduced.</summary>
    private bool CanRecordDoor(string target)
        => _doorEntries.Count < MaximumDoorEntries || _doorEntries.Exists(entry => entry.Target == target);

    private bool CanRecordObject(string target)
        => _objectEntries.Count < MaximumDoorEntries || _objectEntries.Exists(entry => entry.Target == target);

    private void RecordObject(EntityReference target, bool enabled) => Record(_objectEntries, target, enabled);

    private void Record(List<ObjectStateEntry> entries, EntityReference target, bool enabled)
    {
        var entry = new ObjectStateEntry(target.Id, target.LifeEpoch, enabled, ++_doorRevision);
        int index = entries.FindIndex(held => held.Target == target.Id);
        if (index < 0) entries.Add(entry); else entries[index] = entry;
        _doorPendingReported = false;
        _doorDirty = true;
        _doorState.Changed();
    }

    /// <summary>A rebuilt interaction point or placed model starts in its authored state; the confirmed switches are
    /// replayed over it.</summary>
    internal void Reapply() => _doorDirty = true;

    private void RecordDoor(EntityReference target, LG_SecurityDoor door, string interaction, bool enabled)
    {
        int index = _doorEntries.FindIndex(entry => entry.Target == target.Id);
        var previous = index < 0 ? null : _doorEntries[index];
        bool? open = previous?.Open, useKey = previous?.UseKey, hacking = previous?.Hacking,
            customMessage = previous?.CustomMessage;
        switch (interaction)
        {
            case "all": open = useKey = hacking = customMessage = enabled; break;
            case "open": open = enabled; break;
            case "use_key": useKey = enabled; break;
            case "hacking": hacking = enabled; break;
            case "custom_message": customMessage = enabled; break;
            default: throw new InvalidOperationException(ResultCode.MapStateUnknownInteraction);
        }
        var entry = new DoorStateEntry(target.Id, target.LifeEpoch, open, useKey, hacking, customMessage, ++_doorRevision)
        {
            Reference = target,
            Door = door,
            Locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>()
        };
        if (index < 0) _doorEntries.Add(entry); else _doorEntries[index] = entry;
        _doorPendingReported = false;
        _doorDirty = true;
        _doorState.Changed();
    }

    private JsonElement CaptureDoorState() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = FormatVersion.Current,
        revision = _doorRevision,
        entries = _doorEntries.ConvertAll(entry => new
        {
            target = entry.Target,
            lifeEpoch = entry.LifeEpoch,
            open = entry.Open,
            useKey = entry.UseKey,
            hacking = entry.Hacking,
            customMessage = entry.CustomMessage,
            revision = entry.Revision
        }),
        objects = _objectEntries.ConvertAll(entry => new
        {
            target = entry.Target,
            lifeEpoch = entry.LifeEpoch,
            enabled = entry.Enabled,
            revision = entry.Revision
        }),
        visibility = _visibilityEntries.ConvertAll(entry => new
        {
            target = entry.Target,
            lifeEpoch = entry.LifeEpoch,
            shown = entry.Enabled,
            revision = entry.Revision
        })
    });

    internal void RestoreDoorState(JsonElement data)
    {
        if (_disposed) return;
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("door-interaction-state-object");
        RequireFields(data, new[] { "schemaVersion", "revision", "entries", "objects", "visibility" }, "door-interaction-state-field");
        if (data.GetProperty("schemaVersion").GetString() != FormatVersion.Current)
            throw new FormatException("door-interaction-state-version");
        int revision = data.GetProperty("revision").GetInt32();
        if (revision < 0) throw new FormatException("door-interaction-state-revision");
        var array = data.GetProperty("entries");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumDoorEntries)
            throw new FormatException("door-interaction-state-entries");
        var entries = new List<DoorStateEntry>(array.GetArrayLength());
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            RequireFields(item, new[] { "target", "lifeEpoch", "open", "useKey", "hacking", "customMessage", "revision" },
                "door-interaction-state-entry-field");
            string target = item.GetProperty("target").GetString() ?? "";
            long lifeEpoch = item.GetProperty("lifeEpoch").GetInt64();
            int entryRevision = item.GetProperty("revision").GetInt32();
            bool? open = OptionalBoolean(item.GetProperty("open"));
            bool? useKey = OptionalBoolean(item.GetProperty("useKey"));
            bool? hacking = OptionalBoolean(item.GetProperty("hacking"));
            bool? customMessage = OptionalBoolean(item.GetProperty("customMessage"));
            var candidate = new EntityReference(target, _kernel.WorldEpoch, lifeEpoch);
            if (target.Length > 512 || !DoorActionCommands.IsDoor(candidate) || DoorActionCommands.Address(candidate) == null
                || lifeEpoch < 0 || entryRevision <= 0 || entryRevision > revision || !targets.Add(target)
                || open == null && useKey == null && hacking == null && customMessage == null)
                throw new FormatException("door-interaction-state-entry");
            entries.Add(new DoorStateEntry(target, lifeEpoch, open, useKey, hacking, customMessage, entryRevision));
        }
        var objectArray = data.GetProperty("objects");
        if (objectArray.ValueKind != JsonValueKind.Array || objectArray.GetArrayLength() > MaximumDoorEntries)
            throw new FormatException("door-interaction-state-entries");
        var objects = new List<ObjectStateEntry>(objectArray.GetArrayLength());
        var objectTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objectArray.EnumerateArray())
        {
            RequireFields(item, new[] { "target", "lifeEpoch", "enabled", "revision" }, "door-interaction-state-entry-field");
            string target = item.GetProperty("target").GetString() ?? "";
            long lifeEpoch = item.GetProperty("lifeEpoch").GetInt64();
            int entryRevision = item.GetProperty("revision").GetInt32();
            bool enabled = OptionalBoolean(item.GetProperty("enabled")) ?? throw new FormatException("door-interaction-state-boolean");
            if (target.Length > 512 || !IsObjectTarget(target) || lifeEpoch < 0 || entryRevision <= 0 || entryRevision > revision
                || !objectTargets.Add(target))
                throw new FormatException("door-interaction-state-entry");
            objects.Add(new ObjectStateEntry(target, lifeEpoch, enabled, entryRevision));
        }
        var visibility = new List<ObjectStateEntry>();
        var visibilityTargets = new HashSet<string>(StringComparer.Ordinal);
        var visibilityArray = data.GetProperty("visibility");
        if (visibilityArray.ValueKind != JsonValueKind.Array) throw new FormatException("door-interaction-state-entries");
        foreach (var item in visibilityArray.EnumerateArray())
        {
            RequireFields(item, new[] { "target", "lifeEpoch", "shown", "revision" }, "door-interaction-state-entry-field");
            string target = item.GetProperty("target").GetString() ?? "";
            long lifeEpoch = item.GetProperty("lifeEpoch").GetInt64();
            int entryRevision = item.GetProperty("revision").GetInt32();
            bool shown = OptionalBoolean(item.GetProperty("shown")) ?? throw new FormatException("door-interaction-state-boolean");
            if (target.Length > 512 || !IsVisibilityTarget(target) || lifeEpoch < 0 || entryRevision <= 0 || entryRevision > revision
                || !visibilityTargets.Add(target))
                throw new FormatException("door-interaction-state-entry");
            visibility.Add(new ObjectStateEntry(target, lifeEpoch, shown, entryRevision));
        }
        _doorEntries.Clear();
        _doorEntries.AddRange(entries);
        _objectEntries.Clear();
        _objectEntries.AddRange(objects);
        _visibilityEntries.Clear();
        _visibilityEntries.AddRange(visibility);
        _doorRevision = Math.Max(_doorRevision, revision);
        _doorPendingReported = false;
        _doorDirty = true;
        Frame();
    }

    private static void RequireFields(JsonElement value, string[] fields, string code)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException(code);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name)) throw new FormatException(code);
        if (seen.Count != fields.Length) throw new FormatException(code);
    }

    private static bool? OptionalBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("door-interaction-state-boolean")
    };

    /// <summary>Rebinds each logical door in this world and enforces only the parts Forge owns. It runs only when
    /// something changed (an entry written or restored, a new world) or while a door is still pending; a later
    /// native OnDoorState that derived a different local active flag is repaired by <see cref="DoorStateChanged"/>.
    /// The walk is bounded and allocates only when a new world requires a logical address to be rebound.</summary>
    internal void Frame()
    {
        if (_disposed) return;
        long epoch = _kernel.WorldEpoch;
        if (!_doorDirty && _doorEpoch == epoch) return;
        _doorEpoch = epoch;
        bool complete = true;
        string? pendingTarget = null;
        foreach (var entry in _doorEntries)
        {
            try
            {
                var door = entry.Door;
                if (door is not { WasCollected: false } || door == null)
                {
                    if (entry.Reference == null || entry.Reference.WorldEpoch != _kernel.WorldEpoch)
                        entry.Reference = new EntityReference(entry.Target, _kernel.WorldEpoch, entry.LifeEpoch);
                    door = _doors(entry.Reference);
                    entry.Door = door;
                    entry.Locks = null;
                }
                if (door == null || !ApplyDoorState(door, entry))
                {
                    complete = false;
                    pendingTarget ??= entry.Target;
                }
            }
            catch (Exception error)
            {
                complete = false;
                pendingTarget ??= entry.Target;
                MapLog.Contained("map-state.door-apply", error);
                if (_doorApplyFailures.Add((entry.Target, entry.Revision, error.GetType().Name)))
                    try { _report("map.door-interaction-apply-exception: " + entry.Target + ":" + error.GetType().Name); }
                    catch (Exception reportError) { MapLog.Contained("map-state.door-apply-report", reportError); }
            }
        }
        foreach (var entry in _objectEntries)
        {
            try
            {
                if (!ApplyObjectState(entry)) { complete = false; pendingTarget ??= entry.Target; }
            }
            catch (Exception error)
            {
                complete = false;
                pendingTarget ??= entry.Target;
                MapLog.Contained("map-state.object-apply", error);
            }
        }
        foreach (var entry in _visibilityEntries)
        {
            try
            {
                var root = _visibility(new EntityReference(entry.Target, _kernel.WorldEpoch, entry.LifeEpoch));
                if (root == null) { complete = false; pendingTarget ??= entry.Target; continue; }
                if (root.activeSelf != entry.Enabled) root.SetActive(entry.Enabled);
            }
            catch (Exception error)
            {
                complete = false;
                pendingTarget ??= entry.Target;
                MapLog.Contained("map-state.visibility-apply", error);
            }
        }
        _doorDirty = !complete;
        if (complete) _doorPendingReported = false;
        else if (!_doorPendingReported)
        {
            _doorPendingReported = true;
            try { _report("map." + DoorLedgerPendingCode + ": " + pendingTarget); }
            catch (Exception reportError) { MapLog.Contained("map-state.door-pending-report", reportError); }
        }
    }

    /// <summary>The door's own OnDoorState ran (after the game's body): replays the authored policy on that door
    /// only. An entry whose door is not bound yet is left to the pending retry.</summary>
    internal void DoorStateChanged(LG_SecurityDoor_Locks locks)
    {
        if (_disposed || _doorEntries.Count == 0) return;
        foreach (var entry in _doorEntries)
        {
            try
            {
                var door = entry.Door;
                if (door is not { WasCollected: false } || door == null) { _doorDirty = true; continue; }
                var owned = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
                if (owned == null || owned.Pointer != locks.Pointer) continue;
                if (!ApplyDoorState(door, entry)) _doorDirty = true;
            }
            catch (Exception error)
            {
                _doorDirty = true;
                MapLog.Contained("map-state.door-apply", error);
            }
        }
    }

    private static bool ApplyDoorState(LG_SecurityDoor door, DoorStateEntry entry)
    {
        var locks = entry.Locks;
        if (locks is not { WasCollected: false } || locks == null)
        {
            locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
            entry.Locks = locks;
        }
        if (locks == null || locks.WasCollected
            || !Readable(locks.m_intOpenDoor, entry.Open) || !Readable(locks.m_intUseKeyItem, entry.UseKey)
            || !Readable(locks.m_intHack, entry.Hacking) || !Readable(locks.m_intCustomMessage, entry.CustomMessage))
            return false;
        Write(locks.m_intOpenDoor, entry.Open);
        Write(locks.m_intUseKeyItem, entry.UseKey);
        Write(locks.m_intHack, entry.Hacking);
        Write(locks.m_intCustomMessage, entry.CustomMessage);
        return Matches(locks.m_intOpenDoor, entry.Open) && Matches(locks.m_intUseKeyItem, entry.UseKey)
            && Matches(locks.m_intHack, entry.Hacking) && Matches(locks.m_intCustomMessage, entry.CustomMessage);
    }

    private static bool Readable(Interact_Base? part, bool? value)
        => value == null || part is { WasCollected: false } && part != null;

    private static void Write(Interact_Base? part, bool? value)
    {
        if (value is { } enabled && part!.IsActive != enabled) part.SetActive(enabled);
    }

    private static bool Matches(Interact_Base? part, bool? value)
        => value == null || part!.IsActive == value.Value;

    private static bool IsObjectTarget(string target)
        => target.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal)
            || target.StartsWith(EntityKinds.MapObject + ":" + InteractionPointAddress.Category + "/", StringComparison.Ordinal);

    /// <summary>Replays one confirmed point or trigger switch on this machine; false while the object is not in this
    /// world yet (a point is placed after the factory, a trigger is found when the level is built).</summary>
    private bool ApplyObjectState(ObjectStateEntry entry)
    {
        var target = new EntityReference(entry.Target, _kernel.WorldEpoch, entry.LifeEpoch);
        if (entry.Target.StartsWith(EntityKinds.MapObject + ":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal))
            return VanillaWorldObjects.IsCurrent(target) && VanillaWorldObjects.Interaction(_kernel.WorldEpoch,
                entry.Target.Substring(EntityKinds.MapObject.Length + 1), entry.Enabled).Commit
                is MapActionCommit.Issued or MapActionCommit.AlreadyInState;
        if (_interactionPoints(target) is not { WasCollected: false } point) return false;
        if (point.IsActive != entry.Enabled) point.SetActive(entry.Enabled);
        return point.IsActive == entry.Enabled;
    }

    internal void ClearDoorState()
    {
        _doorEntries.Clear();
        _objectEntries.Clear();
        _visibilityEntries.Clear();
        _doorApplyFailures.Clear();
        _doorRevision = 0;
        _doorPendingReported = false;
        _doorDirty = false;
        _doorEpoch = -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearDoorState();
        _doorState.Dispose();
    }

    /// <summary>The parts one security door interaction names: the four together for `all` and the one part each
    /// other member addresses. It is the one mapping the read and the write both use, so a part a request names is
    /// the part both rows mean. A member the row does not carry is refused by its own code at the caller.</summary>
    private static Interact_Base?[]? Parts(LG_SecurityDoor_Locks locks, string interaction) => interaction switch
    {
        "all" => new Interact_Base?[]
        {
            locks.m_intOpenDoor, locks.m_intUseKeyItem, locks.m_intHack, locks.m_intCustomMessage
        },
        "open" => new Interact_Base?[] { locks.m_intOpenDoor },
        "use_key" => new Interact_Base?[] { locks.m_intUseKeyItem },
        "hacking" => new Interact_Base?[] { locks.m_intHack },
        "custom_message" => new Interact_Base?[] { locks.m_intCustomMessage },
        _ => null
    };

    /// <summary>Runs one terminal's own native write and turns its outcome into a result row. An entry that was
    /// invoked with a matching readback is confirmed; a request not yet observed or a call that threw is unknown.
    /// Unknown commits are never retried.</summary>
    private (Status Status, Commit Commit, string Code) Run(Func<MapActionOutcome> entry, Func<bool>? readback = null)
    {
        MapActionOutcome outcome;
        try
        {
            outcome = entry();
            if (outcome.Commit == MapActionCommit.Issued && readback != null && !readback())
                return (Status.Failed, Commit.Unknown, ResultCode.MapStateReadbackPending);
        }
        catch (Exception error)
        {
            _report("map.state-commit-exception: " + error.GetType().Name);
            return (Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        switch (outcome.Commit)
        {
            case MapActionCommit.Issued:
            case MapActionCommit.AlreadyInState:
                return (Status.Succeeded, Commit.Confirmed, outcome.Code);
            default:
                return (Status.Rejected, Commit.None, outcome.Code);
        }
    }

    /// <summary>Which interaction the request is about, or null when the parameter is present but names a member
    /// this row does not carry. A plan that left it out asked for the whole object, which is the member the row
    /// declares first.</summary>
    private static string? Interaction(string? member)
    {
        if (member == null) return MapStateContract.InteractionTargets[0];
        return Array.IndexOf(MapStateContract.InteractionTargets, member) < 0 ? null : member;
    }
}
