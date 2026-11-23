using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>One world of owner receipts. An application pays for every possible cleanup receipt before
/// it can write. Cleanup spends its own named reservation. A reservation or receipt is never dropped while its
/// cleanup is open; only a settled application is evicted, oldest tick first, once the budget is under pressure, and
/// the newest <see cref="RetainedApplications"/> are always kept. Everything is cleared when the world ends.</summary>
internal sealed class OwnerCommandLedger
{
    internal const int Capacity = 8192;
    /// <summary>The applications kept for duplicate and receipt answers however full the budget is.</summary>
    internal const int RetainedApplications = 1024;
    internal sealed class Application
    {
        internal readonly OwnerOutput Request;
        internal readonly string[] Slots;
        internal readonly Dictionary<string, Entry> Controls = new(StringComparer.Ordinal);
        // A spontaneous notification does not spend the matching control reservation. One first outcome per
        // already reserved obligation bounds duplicates while leaving explicit cancel/restore available.
        internal readonly Dictionary<string, Ending> Endings = new(StringComparer.Ordinal);
        internal bool Suppressed, Terminal;
        internal Entry? Self;
        internal Application(OwnerOutput request, string[] slots) { Request = request; Slots = slots; }
        /// <summary>Nothing of it can change any more: cancelled and confirmed, no returned handle to find again, no
        /// ending whose outcome is unknown. A ledger that records no results (the transport's correlation table)
        /// treats a cancelled application as settled.</summary>
        internal bool IsSettled(bool resultsKnown) => Terminal && !Slots.Any(slot => slot.StartsWith("handle:", StringComparison.Ordinal))
            && (!resultsKnown || Controls.TryGetValue("cancel-command", out var cancel) && cancel.Result?.CommitState == CommitStates.Confirmed
                && Endings.Values.All(ended => ended.Result.CommitState != CommitStates.Unknown)
                && (!Controls.TryGetValue("restore", out var restore) || restore.Result?.CommitState == CommitStates.Confirmed));
    }
    internal sealed record Ending(string Slot, JsonElement Handle, string Reason, CommandResult Result);
    internal sealed class Entry
    {
        internal readonly OwnerOutput Request;
        internal readonly Application Application;
        internal readonly bool IsApplication;
        internal CommandResult? Result;
        internal Entry(OwnerOutput request, Application application, bool isApplication)
        { Request = request; Application = application; IsApplication = isApplication; }
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private int budget;
    private readonly bool resultsKnown;
    internal OwnerCommandLedger(bool resultsKnown = true) { this.resultsKnown = resultsKnown; }
    // Applications in admission order. Entries are enumerated from here, application before its controls, because a
    // dictionary does not keep that order once something has been removed from it.
    private readonly List<Application> order = new();
    // The newest tick among evicted applications: a command at or below it that the ledger no longer knows is a
    // replay of something already settled, never a new command.
    private long evictedThrough = -1;
    internal IEnumerable<Entry> Entries
    {
        get
        {
            foreach (var application in order)
            {
                yield return application.Self!;
                foreach (var control in application.Controls.Values) yield return control;
            }
        }
    }
    internal void Clear() { entries.Clear(); order.Clear(); budget = 0; evictedThrough = -1; }

    /// <summary>Frees the budget of settled applications older than the newest <see cref="RetainedApplications"/>,
    /// whole ticks at a time so that the tick horizon above stays below every application that is kept.</summary>
    private void Evict()
    {
        var cut = order.Count - RetainedApplications;
        if (cut <= 0) return;
        var keepFrom = order[cut].Request.SimulationTick;
        order.RemoveAll(application =>
        {
            if (!application.IsSettled(resultsKnown) || application.Request.SimulationTick >= keepFrom) return false;
            entries.Remove(application.Self!.Request.CommandId);
            foreach (var control in application.Controls.Values) entries.Remove(control.Request.CommandId);
            budget -= application.Slots.Length + 1;
            evictedThrough = Math.Max(evictedThrough, application.Request.SimulationTick);
            return true;
        });
    }
    internal bool TryGet(string commandId, out Entry entry) => entries.TryGetValue(commandId, out entry!);

    internal static string[] Slots(JsonElement contract, bool restore)
    {
        var slots = new List<string> { "cancel-command" };
        if (restore) slots.Add("restore");
        foreach (var port in RuntimeJson.Rows(contract, "outputs"))
            if (port.TryGetProperty("handleKind", out _)) slots.Add("handle:" + RuntimeJson.Text(port, "id"));
        return slots.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
    internal static JsonElement Data(JsonElement data, string[] slots, string? slot = null)
    {
        var fields = data.EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value, StringComparer.Ordinal);
        fields["cleanupSlots"] = slots;
        if (slot != null) fields["cleanupSlot"] = slot;
        return RuntimeJson.From(fields);
    }
    internal static bool IsEnding(OwnerOutput request)
        => request.Data.ValueKind == JsonValueKind.Object && request.Data.TryGetProperty("endedSlot", out _);

    internal static OwnerOutput WithEnding(OwnerOutput application, string slot, JsonElement handle)
        => application with
        {
            Data = RuntimeJson.From(new
            {
                application = application.Data,
                endedSlot = slot,
                endedHandle = handle.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : handle
            })
        };

    internal static OwnerOutput ReadEnding(OwnerOutput notification, out string slot, out JsonElement handle)
    {
        RuntimeJson.Require(notification.Operation == "apply", "owner-cleanup-contract", "Only an application reports a spontaneous ending.");
        RuntimeJson.Shape(notification.Data, "application endedSlot endedHandle");
        slot = RuntimeJson.Text(notification.Data, "endedSlot");
        handle = notification.Data.GetProperty("endedHandle");
        var application = notification with { Data = notification.Data.GetProperty("application") };
        RuntimeJson.Require(Declaration(application).Contains(slot, StringComparer.Ordinal)
            && (slot == "cancel-command" || slot.StartsWith("handle:", StringComparison.Ordinal)),
            "owner-cleanup-contract", "The ending does not name a reserved obligation.");
        if (slot == "cancel-command")
            RuntimeJson.Require(handle.ValueKind == JsonValueKind.Null, "owner-cleanup-contract", "A writer ending does not name a handle.");
        else
        {
            RuntimeJson.Handle(handle);
            RuntimeJson.Require(RuntimeJson.Integer(handle.GetProperty("worldEpoch")) == application.WorldEpoch,
                "stale-world", "The ended handle belongs to another world.");
        }
        return application;
    }

    internal static bool EndingMatches(Entry application, string slot, JsonElement handle)
        => slot == "cancel-command" || application.Result == null
            || application.Result.Outputs.ValueKind == JsonValueKind.Object
                && application.Result.Outputs.TryGetProperty(slot[7..], out var output)
                && SameJson(output, handle);

    internal static string[] Declaration(OwnerOutput request)
    {
        RuntimeJson.Require(request.Data.ValueKind == JsonValueKind.Object, "owner-cleanup-contract", "Missing cleanup declaration.");
        RuntimeJson.Require(request.Data.TryGetProperty("cleanupSlots", out var declaration)
            && declaration.ValueKind == JsonValueKind.Array, "owner-cleanup-contract", "Missing cleanup slots.");
        RuntimeJson.Require(declaration.GetArrayLength() > 0 && declaration.GetArrayLength() < Capacity,
            "owner-cleanup-contract", "Cleanup declaration exceeds the ledger budget.");
        var slots = RuntimeJson.Strings(declaration).ToArray();
        RuntimeJson.Require(slots.Length > 0 && slots.Length < Capacity
            && slots.Contains("cancel-command", StringComparer.Ordinal)
            && slots.SequenceEqual(slots.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)),
            "owner-cleanup-contract", "Cleanup slots must be unique and ordered.");
        RuntimeJson.Require(slots.All(slot => slot is "cancel-command" or "restore"
            || slot.StartsWith("handle:", StringComparison.Ordinal) && slot.Length > 7),
            "owner-cleanup-contract", "Unknown cleanup slot.");
        return slots;
    }
    internal static string ControlSlot(OwnerOutput request)
    {
        var slot = RuntimeJson.Text(request.Data, "cleanupSlot");
        RuntimeJson.Require(request.Operation switch
        {
            "cancel-command" => slot == "cancel-command",
            "restore" => slot == "restore",
            "cancel" => slot.StartsWith("handle:", StringComparison.Ordinal) && slot.Length > 7,
            _ => false
        }, "owner-cleanup-contract", "The control does not match its reserved slot.");
        return slot;
    }

    /// <summary>The receiver also supplies the slots read from its loaded plan. The sender reads the declaration
    /// created by EnterOwner, and every control must match the declaration of that exact application.</summary>
    internal CommandResult? Admit(OwnerOutput request, string[]? expectedSlots, out Entry? entry, out bool duplicate)
    {
        entry = null; duplicate = false;
        try
        {
            var slots = Declaration(request);
            RuntimeJson.Require(expectedSlots == null || slots.SequenceEqual(expectedSlots),
                "owner-cleanup-contract", "Cleanup declaration differs from the loaded step.");
            if (request.SimulationTick <= evictedThrough && !entries.ContainsKey(request.CommandId)
                && (request.Operation == "apply" || !entries.ContainsKey(request.EventId)))
                return CommandResult.Rejected("owner-command-stale");
            if (entries.TryGetValue(request.CommandId, out var prior))
            {
                if (prior.IsApplication && prior.Application.Suppressed && request.Operation == "apply")
                    return SameApplication(prior.Application.Request, request)
                        ? CommandResult.Rejected("owner-command-cancelled") : CommandResult.Rejected("owner-command-conflict");
                if (!Same(prior.Request, request)) return CommandResult.Rejected("owner-command-conflict");
                entry = prior; duplicate = true; return null;
            }
            var apply = request.Operation == "apply";
            var id = apply ? request.CommandId : request.EventId;
            var slot = apply ? null : ControlSlot(request);
            Application application;
            if (entries.TryGetValue(id, out var applicationEntry))
            {
                RuntimeJson.Require(applicationEntry.IsApplication && SameApplication(applicationEntry.Application.Request, request)
                    && applicationEntry.Application.Slots.SequenceEqual(slots), "owner-command-conflict", id);
                application = applicationEntry.Application;
            }
            else
            {
                RuntimeJson.Require(apply || request.Operation == "cancel-command", "owner-command-missing", id);
                if (budget > Capacity - slots.Length - 1) Evict();
                if (budget > Capacity - slots.Length - 1) return CommandResult.Rejected("owner-ledger-budget");
                application = new Application(request, slots) { Suppressed = !apply };
                var original = apply ? request : request with { CommandId = id, Operation = "apply" };
                applicationEntry = new Entry(original, application, true);
                application.Self = applicationEntry; order.Add(application);
                if (!apply) applicationEntry.Result = CommandResult.Rejected("owner-command-cancelled");
                entries.Add(id, applicationEntry); budget += slots.Length + 1;
                if (apply) { entry = applicationEntry; return null; }
            }
            RuntimeJson.Require(!apply, "owner-command-conflict", id);
            RuntimeJson.Require(application.Slots.Contains(slot!, StringComparer.Ordinal), "owner-cleanup-contract", slot!);
            if (application.Terminal) return CommandResult.Rejected("owner-command-ended");
            if (application.Controls.ContainsKey(slot!)) return CommandResult.Rejected("owner-control-spent");
            entry = new Entry(request, application, false);
            entries.Add(request.CommandId, entry); application.Controls.Add(slot!, entry);
            if (request.Operation == "cancel-command") application.Terminal = true;
            return null;
        }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code, CommandResult.TruncateDetail(error.Message)); }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or JsonException)
        { return CommandResult.Rejected("owner-cleanup-contract", CommandResult.TruncateDetail(error.Message)); }
    }

    internal static bool SameApplication(OwnerOutput first, OwnerOutput second)
        => first.WorldEpoch == second.WorldEpoch && first.Provider == second.Provider && first.PlanId == second.PlanId
            && first.NodeId == second.NodeId && first.BindingId == second.BindingId && first.StepIndex == second.StepIndex
            && first.Holder == second.Holder && first.HolderIdentity == second.HolderIdentity && first.ScopeId == second.ScopeId
            && first.SimulationTick == second.SimulationTick && SameJson(first.Inputs, second.Inputs);
    internal static bool Same(OwnerOutput first, OwnerOutput second)
        => SameApplication(first, second) && first.Operation == second.Operation && first.EventId == second.EventId
            && SameJson(first.Identities, second.Identities) && SameJson(first.Data, second.Data);
    private static bool SameJson(JsonElement first, JsonElement second)
        => first.ValueKind == JsonValueKind.Undefined || second.ValueKind == JsonValueKind.Undefined
            ? first.ValueKind == second.ValueKind : first.GetRawText() == second.GetRawText();
}
