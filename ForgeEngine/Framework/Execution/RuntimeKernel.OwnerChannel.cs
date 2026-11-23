using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeModuleHandle
{
    public void RegisterOwnerIdentities(Func<ulong, JsonElement, JsonElement> export, Action<JsonElement> import,
        Func<string, ulong?>? session = null) => kernel.RegisterOwnerIdentities(this, export, import, session);
    public bool IsOwnerInvocation => kernel.kernelOwner.OwnerInvocation;
    public bool RestoreOwnerEffect(RuntimeEffectContext context) => kernel.kernelOwner.RestoreOwnerEffect(context);
    public void ReportOwnerEnd(RuntimeHandle handle, string reason, CommandResult result) => kernel.ReportOwnerEnd(handle, reason, result);
    public void ReportOwnerWriterEnd(EntityReference equipment, string plan, string node, CommandResult result)
        => kernel.ReportOwnerWriterEnd(equipment, plan, node, result);
}

public sealed partial class RuntimeKernel
{
    public const string OwnerUnsupportedCode = "owner-unsupported-receiver";
    /// <summary>Whether the command being applied arrived over the owner channel; the host reads it.</summary>
    internal bool OwnerInvocation => kernelOwner.OwnerInvocation;
    internal sealed record IdentityBinding(RuntimeModuleHandle Handle, Func<ulong, JsonElement, JsonElement> Export, Action<JsonElement> Import, Func<string, ulong?>? Session);
    internal sealed class OwnerFlight
    {
        internal OwnerOutput Request = null!;
        internal string SessionProvider = "<admitted>";
        internal Pending Pending = null!;
        internal Work Work = null!;
        internal int Step;
        internal StepInputs Inputs = null!;
        internal JsonElement Parameters;
        internal KernelExecutor.CostHold? Hold;
        internal bool CancelSent;
        internal CommandResult? Result;
        internal OwnerPulse? Pulse;
        internal Dictionary<int, JsonElement> Frames = new();
        /// <summary>The values the flow's variable reads and when-present steps stored before it suspended.</summary>
        internal StoredValueSnapshot[] Stored = Array.Empty<StoredValueSnapshot>();
        internal Dictionary<int, ResultRowSet> Rows = new();
        internal FrameValue[] Slots = Array.Empty<FrameValue>();
        internal byte[] Strings = Array.Empty<byte>();
        internal ControlFrame[] Walk = Array.Empty<ControlFrame>();
        /// <summary>The play origin of the flow the owner step suspended, resumed with it.</summary>
        internal PlayOrigin Play;
    }
    internal sealed record OwnerPulse(EffectSlot Effect, ResolvedStep Step);

    /// <summary>The per-machine requests of one many-recipient owner step. Request 0 is the step's own flight; the
    /// others are answered into <see cref="Results"/> and the walk resumes once every holder has answered. A
    /// migration does not carry this record: a restored flight resumes on its own holder's answer alone.</summary>
    internal sealed class OwnerSplit
    {
        internal readonly string[] Holders, RowHolder;
        internal readonly List<OwnerOutput> Requests;
        internal readonly CommandResult?[] Results;
        internal readonly bool[] CancelSent;
        internal OwnerSplit(string[] holders, string[] rowHolder, List<OwnerOutput> requests)
        {
            Holders = holders; RowHolder = rowHolder; Requests = requests;
            Results = new CommandResult?[requests.Count]; CancelSent = new bool[requests.Count];
        }
        internal bool Complete => Array.TrueForAll(Results, result => result != null);
    }
    internal void RegisterOwnerIdentities(RuntimeModuleHandle handle, Func<ulong, JsonElement, JsonElement> export, Action<JsonElement> import, Func<string, ulong?>? session)
    {
        Thread();
        RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
        if (kernelOwner.ownerIdentities.TryGetValue(handle.ProviderId, out var prior) && !prior.Handle.IsRegistered) kernelOwner.ownerIdentities.Remove(handle.ProviderId);
        RuntimeJson.Require(kernelOwner.ownerIdentities.TryAdd(handle.ProviderId, new(handle, export, import, session)), "owner-identity-provider", handle.ProviderId);
    }
    private const string OwnerImportDetailPrefix = "Owner identity import [";
    internal void ImportOwnerIdentities(JsonElement identities)
    {
        Thread(); SyncOwnerWorld();
        RuntimeJson.Require(identities.ValueKind == JsonValueKind.Object, OwnerUnsupportedCode, "Missing identity mapping.");
        foreach (var pair in identities.EnumerateObject())
        {
            try
            {
                RuntimeJson.Require(kernelOwner.ownerIdentities.TryGetValue(pair.Name, out var binding) && binding.Handle.IsRegistered, OwnerUnsupportedCode, pair.Name);
                binding!.Import(pair.Value);
            }
            catch (RuntimeContractException error)
            {
                // The receiver returns this unchanged code to the originating command. Keep the actual
                // importing provider in its detail so a holder refusal cannot be mistaken for routing failure.
                throw new RuntimeContractException(error.Code, OwnerImportDetailPrefix + pair.Name + "]: " + error.Message);
            }
        }
    }
    internal bool ReceiveOwnerResult(string commandId, string holder, long worldEpoch, CommandResult result)
    {
        Thread(); SyncOwnerWorld();
        if (worldEpoch != WorldEpoch) return false;
        kernelOwner.ownerSplits.TryGetValue(commandId, out var member);
        var split = member.Split;
        OwnerFlight? flight;
        if (split != null && member.Index != 0)
        {
            // Another machine's answer to the same step: kept for the merge, and the walk resumes with the last one.
            var request = split.Requests[member.Index];
            if (split.Results[member.Index] != null || request.Holder != holder
                || !kernelOwner.ownerFlights.TryGetValue(split.Requests[0].CommandId, out flight)) return false;
            if ((world.cancelled.Contains((flight.Pending.Provider, request.ScopeId)) || !plans.Loaded.ContainsKey(request.PlanId)) && !split.CancelSent[member.Index])
            { kernelOwner.QueueOwnerControl(request, "cancel-command", RuntimeJson.EmptyObject); split.CancelSent[member.Index] = true; }
            split.Results[member.Index] = CommandResultRules.TryValidate(result, out _) ? result : CommandResult.FailedUnknown("invalid-owner-result");
            kernelOwner.SettleOwnerLedger(commandId, split.Results[member.Index]!);
            return kernelOwner.CompleteOwnerSplit(flight, split);
        }
        if (!kernelOwner.ownerFlights.TryGetValue(commandId, out flight) || flight.Result != null || flight.Request.Holder != holder) return false;
        if ((world.cancelled.Contains((flight.Pending.Provider, flight.Request.ScopeId)) || !plans.Loaded.ContainsKey(flight.Request.PlanId)) && !flight.CancelSent)
        { kernelOwner.QueueOwnerControl(flight.Request, "cancel-command", RuntimeJson.EmptyObject); flight.CancelSent = true; kernelOwner.CancelOwnerSplit(flight); }
        flight.Result = CommandResultRules.TryValidate(result, out _) ? result : CommandResult.FailedUnknown("invalid-owner-result");
        if (flight.Result.Status == CommandStatuses.Rejected)
            TraceOwnerResultRefusal(flight, flight.Result);
        kernelOwner.SettleOwnerLedger(commandId, flight.Result);
        if (flight.Pulse != null) return true;
        if (split == null) { kernelOwner.EnqueueOwnerResume(flight); return true; }
        split.Results[0] = flight.Result;
        return kernelOwner.CompleteOwnerSplit(flight, split);
    }
    private void PrepareOwnerTick()
    {
        SyncOwnerWorld();
        if (kernelEffects.ownerEffects.Count != 0)
            foreach (var pair in kernelEffects.ownerEffects.Where(pair => !kernelEffects.effects.ContainsKey(pair.Value.Slot.Id)).ToArray())
                kernelEffects.ownerEffects.Remove(pair.Key);
        kernelOwner.ownerFlightScratch.Clear();
        foreach (var pair in kernelOwner.ownerFlights) kernelOwner.ownerFlightScratch.Add(pair.Value);
        for (var flightIndex = 0; flightIndex < kernelOwner.ownerFlightScratch.Count; flightIndex++)
        {
            var flight = kernelOwner.ownerFlightScratch[flightIndex];
            var ended = world.cancelled.Contains((flight.Pending.Provider, flight.Request.ScopeId))
                || !plans.Loaded.ContainsKey(flight.Request.PlanId) || !registry.IsRegistered(flight.Pending.Provider, flight.Pending.Generation)
                || (flight.Pulse is { } pulse && !kernelEffects.effects.ContainsKey(pulse.Effect.Id));
            if (ended && !flight.CancelSent)
            { kernelOwner.QueueOwnerControl(flight.Request, "cancel-command", RuntimeJson.EmptyObject); flight.CancelSent = true; }
            if (ended) kernelOwner.CancelOwnerSplit(flight);
            if (flight.Result == null && CurrentTick - flight.Request.SimulationTick >= 600)
            {
                if (!flight.CancelSent) kernelOwner.QueueOwnerControl(flight.Request, "cancel-command", RuntimeJson.EmptyObject);
                flight.CancelSent = true;
                ReceiveOwnerResult(flight.Request.CommandId, flight.Request.Holder, WorldEpoch, CommandResult.FailedUnknown("owner-result-timeout"));
            }
            if (kernelOwner.ownerSplits.TryGetValue(flight.Request.CommandId, out var member) && member.Index == 0)
                for (var index = 1; index < member.Split.Requests.Count; index++)
                {
                    var request = member.Split.Requests[index];
                    if (member.Split.Results[index] == null && CurrentTick - request.SimulationTick >= 600)
                    {
                        if (!member.Split.CancelSent[index]) kernelOwner.QueueOwnerControl(request, "cancel-command", RuntimeJson.EmptyObject);
                        member.Split.CancelSent[index] = true;
                        ReceiveOwnerResult(request.CommandId, request.Holder, WorldEpoch, CommandResult.FailedUnknown("owner-result-timeout"));
                    }
                }
            if (flight.Pulse is { } completed && flight.Result != null)
            {
                kernelOwner.ownerFlights.Remove(flight.Request.CommandId);
                var result = kernelOwner.ImportOwnerHandles(flight, completed.Step, flight.Result, null);
                CompleteEffectPulse(completed.Effect, completed.Step, flight.Inputs, flight.Hold, result,
                    OwnerEffectApplicationId(flight.Request), commandId: flight.Request.CommandId);
                flight.Hold = null;
            }
            else if (ended && flight.Result != null && kernelOwner.SplitComplete(flight))
            {
                var result = executor.SettleCost(flight.Result, flight.Hold, null);
                flight.Hold = null;
                kernelOwner.ForgetOwnerSplit(flight);
                kernelOwner.ownerFlights.Remove(flight.Request.CommandId); kernelOwner.ownerResumes.Remove(flight.Pending);
                kernelOwner.ownerEndReceipts.Add(new CommandReceipt(flight.Request.CommandId, flight.Request.EventId, null, flight.Request.EventId,
                    flight.Request.PlanId, "", "", flight.Request.NodeId, flight.Request.BindingId, WorldEpoch, CurrentTick, result));
            }
        }
        kernelOwner.ownerFlightScratch.Clear();
        foreach (var pair in kernelOwner.ownerResumes)
        {
            pair.Key.Admitted = plans.Loaded.TryGetValue(pair.Value.Work.Plan.Plan.Id, out var plan) && ReferenceEquals(plan, pair.Value.Work.Plan)
                ? pair.Key.Work : Array.Empty<Work>();
            pair.Key.PlanUsage = PlanUsageOf(pair.Key.Admitted);
            pair.Key.PlanUsageGeneration = world.lifecycleGeneration; pair.Key.DecidedTick = CurrentTick;
        }
        kernelOwner.ownerOutputs.AddRange(kernelOwner.ownerControls); kernelOwner.ownerControls.Clear();
        kernelScheduler.tickCommands.AddRange(kernelOwner.ownerEndReceipts); kernelOwner.ownerEndReceipts.Clear();
    }
    private void SyncOwnerWorld()
    {
        if (kernelOwner.ownerWorld == WorldEpoch) return;
        kernelOwner.ownerWorld = WorldEpoch;
        // A command still waiting on its holder belongs to the world that ended and is dropped unanswered.
        kernelOwner.ownerDeferrals.Clear(); ownerSettlements.Clear(); kernelOwner.ownerDeferralTaken = null;
        foreach (var flight in kernelOwner.ownerFlights.Values) if (flight.Hold != null) executor.ReleaseCost(flight.Hold);
        kernelOwner.ownerFlights.Clear(); kernelOwner.ownerResumes.Clear(); kernelOwner.ownerSplits.Clear(); kernelOwner.ownerControls.Clear(); kernelOwner.ownerReceived.Clear(); kernelOwner.ownerIssued.Clear(); kernelEffects.ownerEffects.Clear(); kernelOwner.ownerLeases.Clear(); kernelOwner.ownerEndResults.Clear();
        kernelOwner.ownerEndReceipts.Clear(); kernelOwner.ownerEarlyEnds.Clear(); kernelOwner.ownerCleanup.Clear();
    }
}
