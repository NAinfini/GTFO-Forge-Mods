using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

internal sealed partial class KernelOwner
{
    /// <summary>This component's part of a host-migration image: the commands issued to holders and the ones this
    /// machine received, the owner-run effects, the proxy leases, the endings and receipts not yet reported, and the
    /// flights still waiting on an answer. <paramref name="live"/> is the facade's answer to whether any of it is
    /// still unsettled, which also counts the settlements it has not sent.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image, bool live) => image["owner"] = new
    {
        live,
        controlSequence = ownerControlSequence,
        issued = CaptureMigrationOwnerLedger(ownerIssued),
        receiver = CaptureMigrationReceiver(),
        effects = kernelEffects.ownerEffects.Values.Select(row => new
        { request = row.Request, effect = row.Slot.Id, cause = CaptureMigrationCause(row.Cause) }).ToArray(),
        leases = ownerLeases.Values.Select(row => new { request = row.Request, remote = row.Remote, local = row.Local }).ToArray(),
        earlyEnds = ownerEarlyEnds.Select(row => new { command = row.Key, result = CaptureMigrationOwnerResult(row.Value) }).ToArray(),
        receipts = ownerEndReceipts.Select(CaptureMigrationReceipt).ToArray(),
        flights = ownerFlights.Values.Select(flight => new
        {
            request = flight.Request,
            cause = CaptureMigrationCause(flight.Pending),
            work = flight.Pulse == null ? SerializeMigrationWork(flight.Work) : null,
            step = flight.Step,
            inputs = flight.Inputs.Json,
            cancelSent = flight.CancelSent,
            result = CaptureMigrationOwnerResult(flight.Result),
            effect = flight.Pulse?.Effect.Id,
            costHeld = flight.Hold != null,
            costs = CaptureProviderCosts(flight),
            frames = flight.Frames.Select(row => new { step = row.Key, value = row.Value }).ToArray(),
            storedValues = SerializeMigrationStoredValues(flight.Stored),
            rows = flight.Rows.Select(row => new { step = row.Key, value = CaptureMigrationOwnerRows(flight, row.Value) }).ToArray(),
            walk = SerializeMigrationWalk(flight.Walk),
            play = SerializeMigrationPlay(flight.Play)
        }).ToArray()
    };

    /// <summary>What this machine holds as a receiver: its ledger of applied commands, the cleanup each one owes and
    /// the endings queued to report. It stays with this machine across a migration, so the facade also reads it back
    /// when it validates an incoming image.</summary>
    internal object CaptureMigrationReceiver()
    {
        var descriptors = new List<JsonElement>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var cleanup = ownerCleanup.Select(pair =>
        {
            var descriptor = RuntimeJson.From(new
            {
                generation = pair.Value.ProviderGeneration,
                capability = pair.Value.CapabilityId,
                slots = pair.Value.Slots,
                ports = pair.Value.HandlePorts,
                maxStacks = pair.Value.MaxStacks,
                status = pair.Value.StatusKind,
                subject = pair.Value.Subject
            });
            var key = RuntimeJson.StableText(descriptor);
            if (!indices.TryGetValue(key, out var index))
            { index = descriptors.Count; indices.Add(key, index); descriptors.Add(descriptor); }
            return new { command = pair.Key, descriptor = index };
        }).ToArray();
        return new
        {
            ledger = CaptureMigrationOwnerLedger(ownerReceived),
            descriptors,
            cleanup,
            endings = ownerEndResults.Select(row =>
            {
                OwnerCommandLedger.ReadEnding(row.Request, out var slot, out _);
                return new { command = row.Request.CommandId, slot };
            }).ToArray()
        };
    }

    private object CaptureMigrationCause(Pending cause) => new
    {
        provider = cause.Provider,
        @event = cause.Event,
        sequence = cause.Sequence,
        tabled = cause.Tabled,
        work = cause.Work.Where(plans.WorkLive).Select(SerializeMigrationWork).ToArray()
    };

    /// <summary>The resource reservations one flight still holds, each named by the provider that owns its kind.</summary>
    internal object[] CaptureProviderCosts(OwnerFlight flight)
    {
        if (flight.Hold == null) return Array.Empty<object>();
        return flight.Hold.Held.Select((item, index) =>
        {
            var provider = registry.Tables.ResourceProviders.TryGetValue(item.Kind, out var registered)
                && ReferenceEquals(registered.Provider.Cost, item.Owner) ? registered.Owner : "";
            RuntimeJson.Require(provider.Length != 0, "migration-owner-cost", "A reservation lost its registered resource owner.");
            return (object)new { index, provider, kind = item.Kind, id = item.Id, amount = item.Amount };
        }).ToArray();
    }

    /// <summary>Whether a handle slot is the local proxy of a holder's handle: the one cancellation the kernel
    /// rebuilds from data instead of asking a provider to rebind it.</summary>
    internal bool IsOwnerProxy(int address, HandleSlot slot)
        => ownerLeases.Values.Any(lease => lease.Local.GetProperty("local").GetInt32() == address
            && lease.Local.GetProperty("lifeEpoch").GetInt32() == slot.Generation);

    /// <summary>Adopts the owner tables of a validated image into this staged component, without release or reapply
    /// callbacks and without settling a resource on either machine. Every machine keeps what it holds as a receiver;
    /// only the machine that becomes host takes the issued ledger, the proxy leases and the endings not yet reported.
    /// A restored proxy's cancellation queues on <paramref name="destination"/>, the live component the image is
    /// committed into. The owner-run effects and the flights are put back by the facade after this, because each
    /// resumes a dispatch.</summary>
    internal void RestoreMigration(MigrationOwners image, long targetEpoch, bool isHost, KernelOwner destination)
    {
        // The normal epoch reset would discard these tables on the next Advance. Adopt them once under the new epoch.
        ownerWorld = targetEpoch;
        ownerFlights.Clear(); ownerResumes.Clear(); ownerSplits.Clear(); ownerControls.Clear(); kernelEffects.ownerEffects.Clear(); ownerLeases.Clear();
        ownerCleanup.Clear(); ownerEndResults.Clear(); ownerEndReceipts.Clear(); ownerEarlyEnds.Clear();
        foreach (var pair in image.ReceiverHandles)
        {
            while (kernelEffects.handleSlots.Count <= pair.Key) kernelEffects.handleSlots.Add(null);
            kernelEffects.handleSlots[pair.Key] = pair.Value;
        }
        AdoptMigrationOwnerLedger(ownerReceived, image.Received);
        foreach (var pair in image.ReceiverCleanups) ownerCleanup.Add(pair.Key, pair.Value);
        foreach (var row in RuntimeJson.Rows(image.Receiver, "endings", OwnerCommandLedger.Capacity))
        {
            ownerReceived.TryGet(RuntimeJson.Text(row, "command"), out var application);
            var ended = application.Application.Endings[RuntimeJson.Text(row, "slot")];
            ownerEndResults.Enqueue((OwnerCommandLedger.WithEnding(application.Request, ended.Slot, ended.Handle), ended.Reason, ended.Result));
        }
        ownerIssued.Clear();
        if (!isHost) return;
        AdoptMigrationOwnerLedger(ownerIssued, image.Issued);
        ownerControlSequence = Math.Max(ownerControlSequence, RuntimeJson.Integer(image.State.GetProperty("controlSequence")));
        foreach (var row in RuntimeJson.Rows(image.State, "leases", limits.MaxLiveHandles))
        {
            var request = ReadMigrationOwnerRequest(row.GetProperty("request"), targetEpoch);
            var remote = row.GetProperty("remote"); var local = row.GetProperty("local");
            ownerLeases.Add(request.CommandId + "\n" + remote.GetRawText(), new OwnerLease(request, remote, local));
            var port = ownerIssued.Entries.First(entry => entry.Request.CommandId == request.CommandId).Result!.Outputs
                .EnumerateObject().First(field => field.Value.GetRawText() == remote.GetRawText()).Name;
            kernelEffects.handleSlots[local.GetProperty("local").GetInt32()]!.Cancel = () => destination.QueueOwnerControl(request,
                "cancel", RuntimeJson.From(new { handle = remote }), "handle:" + port);
        }
        foreach (var row in RuntimeJson.Rows(image.State, "earlyEnds", limits.MaxLiveHandles))
            ownerEarlyEnds.Add(RuntimeJson.Text(row, "command"), ReadMigrationOwnerResult(row.GetProperty("result"))!);
        foreach (var row in RuntimeJson.Rows(image.State, "receipts", OwnerCommandLedger.Capacity))
            ownerEndReceipts.Add(ReadMigrationOwnerReceipt(row, targetEpoch));
    }
}
