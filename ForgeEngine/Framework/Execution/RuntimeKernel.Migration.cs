using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeEngine.Framework;

/// <summary>One prepared native/callback obligation, using the exact target handle supplied to the provider.
/// Native objects and delegates stay local; each required native object, liveness check and cancellation must
/// be rebound without changing the handle's provider, slot or life identity.</summary>
public sealed record ProviderHandleAdoption(RuntimeHandle Handle, object? Native = null,
    Func<object, bool>? IsLive = null, Action? Cancel = null);

/// <summary>Read-only preflight's complete handle rebindings and the callback that switches prepared private
/// tables. Rollback must restore the prior tables even when Adopt changed them before throwing. Neither callback
/// may replay native effects or reserve, settle or release resource quantities.</summary>
public sealed record ProviderHandoverAdoption(IReadOnlyList<ProviderHandleAdoption> Handles, Action Adopt, Action Rollback);

public sealed partial class RuntimeModuleHandle
{
    /// <summary>Registers this provider's single versioned handover during the registration window. Capture
    /// receives outstanding handles/costs and returns an object of at most 65536 UTF-8 bytes. Prepare receives
    /// sourceEpoch, targetEpoch, isHost, source/retained payloads, exact target handles and owner/receiver
    /// obligations; it must uniquely resolve recalled native identities and build tables/callbacks read-only.
    /// A missing or ambiguous identity must refuse explicitly, never retarget. Only Adopt switches the prepared
    /// tables, without applying effects or reserving, settling or cleaning up. Unregister removes the handover.</summary>
    public void RegisterProviderHandover(string schema, Func<JsonElement, JsonElement> capture,
        Func<JsonElement, ProviderHandoverAdoption> prepare)
        => kernel.RegisterProviderHandover(this, schema, capture, prepare);
}

/// <summary>
/// Host-migration state is captured beside the game's own migration buffer. It is deliberately separate from
/// <see cref="RuntimeKernel.BeginWorld"/>: vanilla recalls the world first, then this code changes only Forge's
/// authority epoch and restores the metadata which vanilla cannot know about.
/// </summary>
public sealed partial class RuntimeKernel
{
    private const string MigrationSnapshotVersion = "1.0.0";

    internal sealed record ProviderHandoverRegistration(RuntimeModuleHandle Owner, string Schema,
        Func<JsonElement, JsonElement> Capture, Func<JsonElement, ProviderHandoverAdoption> Prepare);
    private readonly Dictionary<string, ProviderHandoverRegistration> providerHandovers = new(StringComparer.Ordinal);
    private bool readingProviderHandover;

    internal void RegisterProviderHandover(RuntimeModuleHandle owner, string schema,
        Func<JsonElement, JsonElement> capture, Func<JsonElement, ProviderHandoverAdoption> prepare)
    {
        Mutable();
        RuntimeJson.Require(IsRegistrationOpen && owner.IsRegistered, "registration-closed", owner.ProviderId);
        RuntimeJson.Text(schema); ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(prepare);
        if (providerHandovers.TryGetValue(owner.ProviderId, out var previous) && !previous.Owner.IsRegistered)
            providerHandovers.Remove(owner.ProviderId);
        RuntimeJson.Require(providerHandovers.TryAdd(owner.ProviderId, new(owner, schema, capture, prepare)),
            "migration-provider-contract", "A provider has exactly one current handover schema.");
    }

    internal sealed record MigrationArrival(string Provider, long Generation, RuntimeEvent Event, LedgerHash Fingerprint,
        IReadOnlyList<Work> Work, EventSubject EndSubject, IReadOnlyList<PendingPlan> Usage);

    private readonly List<MigrationArrival> migrationArrivals = new();
    internal bool migrationPaused;

    /// <summary>True while the native master manager is choosing and recalling a replacement host.</summary>
    public bool HostMigrationPaused { get { ReadThread(); return migrationPaused; } }

    /// <summary>
    /// Stops dispatch without ending the world. Publishers may still arrive during the native migration window;
    /// those arrivals are retained and are admitted against the recalled snapshot once, on the replacement host.
    /// </summary>
    internal void BeginHostMigration()
    {
        Thread();
        if (!world.worldStarted) return;
        migrationPaused = true;
    }

    /// <summary>
    /// A read-only image of the Forge state associated with one native migration buffer. Unlike
    /// <see cref="ExportVariables"/> and <see cref="ExportVariableDelta"/>, this does not settle the variable touch
    /// set: taking a migration image is not a network acknowledgement that the next variable delta travelled.
    /// </summary>
    internal string CaptureHostMigration()
    {
        ReadThread();
        RuntimeJson.Require(world.worldStarted, "migration-lifecycle", "A migration snapshot needs a live world.");
        RequireMigrationOwnerSettled();
        RuntimeJson.Require(kernelEffects.endingOwnedLifetimes == 0 && !kernelEffects.handleSlots.Any(slot => slot?.OwnedLifetime != null), "migration-owned-lifetime",
            "An owned native lifetime cannot be captured without a data-defined ownership handover.");
        RuntimeJson.Require(!kernelEffects.effects.Values.Any(slot => slot.Layers.Any(layer => layer.RestorePending || layer.AdditionalOwnerApplications.Count != 0)),
            "migration-provider-data", "Pending effect restoration or additional holder cleanup cannot be captured as portable active state.");

        // The flow decides once which schedules and waits are still owed; every component writes its parts of the
        // image against that answer, in this order, and the facade adds the two parts that are the host layer's:
        // the native domains' state inside the variable part and the providers' own handover payloads.
        var survivors = kernelFlow.SurvivingMigration();
        var image = new Dictionary<string, object?>(StringComparer.Ordinal) { ["schemaVersion"] = MigrationSnapshotVersion };
        kernelVariables.CaptureMigration(image);
        if (NativeStateCapture != null)
            image["variables"] = RuntimeJson.Parse(WithNativeState(((JsonElement)image["variables"]!).GetRawText(), NativeCaptureMode.Complete));
        eventQueue.CaptureMigration(image);
        world.CaptureMigration(image);
        kernelEffects.CaptureMigration(image, survivors, kernelOwner.ownerLeases.Values);
        kernelFlow.CaptureMigration(image, survivors);
        kernelOwner.CaptureMigration(image, HasLiveOwnerState());
        image["providerHandover"] = CaptureProviderHandovers();
        kernelScheduler.CaptureMigration(image);
        return RuntimeJson.StableText(RuntimeJson.From(image));
    }

    /// <summary>
    /// Restores Forge after vanilla recalled the matching migration buffer. Native effects are not replayed here:
    /// the game has already recalled its world, and replaying an action would duplicate damage, spawns or costs.
    /// Only Forge metadata, clocks, handles and ledgers are adopted under the new epoch.
    /// </summary>
    /// <returns>The captured simulation tick from which the replacement host continues.</returns>
    internal long RestoreHostMigration(string json, long newWorldEpoch, bool isHost)
    {
        Mutable(); AcceptRuntimeWork(true);
        RequireMigrationOwnerSettled();
        RuntimeJson.Integer(newWorldEpoch);
        RuntimeJson.Require(kernelEffects.endingOwnedLifetimes == 0 && !kernelEffects.handleSlots.Any(slot => slot?.OwnedLifetime != null), "migration-owned-lifetime",
            "Migration adoption cannot discard a locally owned native lifetime.");
        RuntimeJson.Require(!kernelEffects.effects.Values.Any(slot => slot.Layers.Any(layer => layer.RestorePending || layer.AdditionalOwnerApplications.Count != 0)),
            "migration-provider-data", "Pending effect restoration or additional local holder cleanup cannot be discarded by migration adoption.");
        // Only a restarted count may name a lower epoch (see FollowWorld); the restart is spent when the image commits.
        RuntimeJson.Require(world.worldStarted && (world.worldCountRestarts ? newWorldEpoch != WorldEpoch : newWorldEpoch > WorldEpoch), "world-epoch",
            "A migrated world must advance its epoch.");
        var snapshot = RuntimeJson.Parse(json);
        RuntimeJson.Shape(snapshot,
            "schemaVersion owner providerHandover deferredResults worldEpoch capturedTick tickUsage sequence scheduleSequence effectSequence handleGeneration variables history historyFloor cancelled providers handles effects schedules waits pending");
        RuntimeJson.Require(RuntimeJson.Text(snapshot, "schemaVersion") == MigrationSnapshotVersion,
            "migration-version", "Unsupported Forge migration snapshot.");
        var sourceEpoch = RuntimeJson.Integer(snapshot.GetProperty("worldEpoch"));
        RuntimeJson.Require(sourceEpoch == WorldEpoch, "migration-world", "Migration snapshot belongs to another world.");
        var capturedTick = RuntimeJson.Integer(snapshot.GetProperty("capturedTick"), -1, RuntimeJson.MaxSafeInteger);
        var (addressedSnapshot, receiverHandles) = ReaddressMigrationOwnerHandles(snapshot);
        snapshot = addressedSnapshot;
        var owners = ValidateMigrationOwners(snapshot, sourceEpoch, newWorldEpoch) with { ReceiverHandles = receiverHandles };
        var deferred = ReadDeferredResults(snapshot.GetProperty("deferredResults"), sourceEpoch, newWorldEpoch);
        var variablesState = RebaseWorldEpoch(snapshot.GetProperty("variables"), sourceEpoch, newWorldEpoch);
        var variableState = SplitNativeState(variablesState.GetRawText(), out var nativeState);
        var preparedVariables = kernelVariables.PrepareMigration(variableState, variablesState.GetProperty("gates").GetRawText(),
            capturedTick, kernelFlow.DeserializeMigrationGateRelease);
        var preparedNative = PrepareNativeState(nativeState);
        var handover = PrepareProviderHandovers(snapshot, owners, sourceEpoch, newWorldEpoch, isHost);
        // Endings are declarations of the loaded action, not authority supplied by a migration image. Reject a
        // missing/stale step or altered end_on set before changing the world or discarding any live bookkeeping.
        foreach (var entry in RuntimeJson.Rows(snapshot, "effects", Limits.MaxLiveHandles)) ResolveMigrationEffectOptions(entry);
        // Subject decoding adds admission checks of historical facts. Run the same decoder in the source epoch
        // before clearing any state; the later restore only rebases those already-validated identities.
        if (isHost)
        {
            ValidateMigrationPulseClaims(snapshot);
            foreach (var row in RuntimeJson.Rows(snapshot, "schedules", MaximumLiveSchedules(Limits)))
                if (row.GetProperty("resume").ValueKind == JsonValueKind.Null)
                    kernelFlow.ReadMigrationEndSubject(RuntimeJson.Text(row, "provider"), ReadMigrationEvent(row.GetProperty("template")));
            foreach (var row in RuntimeJson.Rows(snapshot, "pending", Limits.MaxQueuedEvents))
                if (row.GetProperty("schedule").ValueKind == JsonValueKind.Null
                    && row.GetProperty("wait").ValueKind == JsonValueKind.Null
                    && row.GetProperty("release").ValueKind == JsonValueKind.Null)
                    kernelFlow.ReadMigrationEndSubject(RuntimeJson.Text(row, "provider"), ReadMigrationEvent(row.GetProperty("event")));
        }
        // Every section is decoded into independent storage before any adopter or live state is touched.
        var image = NewMigrationImage(newWorldEpoch, capturedTick);
        (image.kernelVariables.variables, image.kernelVariables.triggerGates) = preparedVariables;
        var acceptedArrivals = new List<MigrationArrival>();
        try
        {
            image.ReadMigrationImage(snapshot, owners, deferred, handover, sourceEpoch, newWorldEpoch, isHost);
            if (isHost)
                foreach (var arrival in migrationArrivals)
                {
                    var key = new LedgerKey(arrival.Provider, LedgerSpace.Event, arrival.Event.EventId);
                    if (image.eventQueue.history.TryGetValue(key, out var existing))
                    {
                        RuntimeJson.Require(existing.Fingerprint == arrival.Fingerprint, "event-id-conflict", arrival.Event.EventId);
                        continue;
                    }
                    image.AcceptRuntimeWork();
                    RuntimeJson.Require(image.FactAdmissionClosure == null,
                        image.FactAdmissionClosure ?? "runtime-suspended", arrival.Event.EventId);
                    RuntimeJson.Require(image.IsRegistered(arrival.Provider, arrival.Generation), "migration-provider", arrival.Provider);
                    foreach (var usage in arrival.Usage)
                        RuntimeJson.Require(image.plans.Loaded.TryGetValue(usage.Plan.Plan.Id, out var loaded)
                            && ReferenceEquals(loaded, usage.Plan), "migration-work-plan", usage.Plan.Plan.Id);
                    EntityReference RebaseSubject(EntityReference value)
                    {
                        RuntimeJson.Require(value.WorldEpoch == sourceEpoch, "migration-world", value.Id);
                        return value with { WorldEpoch = newWorldEpoch };
                    }
                    var work = arrival.Work.Select(item => item with
                    { Subjects = item.Subjects.Select(RebaseSubject).ToArray() }).ToArray();
                    var subject = arrival.EndSubject.Count == 0 ? default
                        : arrival.EndSubject.Holder.Player != null ? new EventSubject(RebaseSubject(arrival.EndSubject.At(0)))
                        : new EventSubject(Enumerable.Range(0, arrival.EndSubject.Count)
                            .Select(index => RebaseSubject(arrival.EndSubject.At(index))).ToArray());
                    var rebased = RebaseEvent(arrival.Event, sourceEpoch, newWorldEpoch);
                    image.entityLives.OpenEntityMemo(rebased.Outputs, -1, null);
                    image.EnqueueAdmittedEvent(arrival.Provider, arrival.Generation, rebased, work, subject, arrival.Usage);
                    acceptedArrivals.Add(arrival with { Event = rebased });
                }
        }
        catch (RuntimeContractException) { throw; }
        catch (Exception error) { throw new RuntimeContractException("migration-image", error.GetType().Name + ": " + error.Message); }
        CommitMigrationImage(image, handover, preparedNative, sourceEpoch, isHost, acceptedArrivals);
        world.worldCountRestarts = false;
        return capturedTick;
    }

    /// <summary>
    /// A client's world takes the number its host counts it under. The client built the level before it heard that number, so
    /// the level, its objects and what the kernel holds of them stay and only the epoch moves: the kernel restores its own image
    /// under the new epoch, and every provider follows as it does after a host migration (<c>IsMigration</c>). After
    /// <see cref="RestartWorldCount"/> the number may be a lower one. A kernel whose state has no portable image refuses by the
    /// capture's or the restore's own code and stays in its world.
    /// </summary>
    /// <returns>The simulation tick the world continues from.</returns>
    internal long FollowWorld(long newWorldEpoch) => RestoreHostMigration(CaptureHostMigration(), newWorldEpoch, false);

    private void ReadMigrationImage(JsonElement snapshot, MigrationOwners owners, DeferredImage deferred,
        PreparedProviderState handover, long sourceEpoch, long newWorldEpoch, bool isHost)
    {
        world.worldHost = isHost;
        migrationPaused = false;
        // Every component reads its own parts into this staged kernel, in this order. What binds to the kernel that
        // will dispatch it (an effect's inputs and liveness cells, a flight's resumed dispatch) is decoded here.
        eventQueue.RestoreMigration(snapshot);
        kernelScheduler.RestoreMigration(snapshot, deferred, isHost);
        world.RestoreMigration(snapshot);
        kernelEffects.RestoreMigration(snapshot);
        foreach (var entry in RuntimeJson.Rows(snapshot, "effects", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(entry,
                "id provider binding capability plan resource revision entry node statusKind removal subject layers peak endsOn origin inputs parameters commandPrefix commandId handle handleSlot pulses");
            var slot = new EffectSlot
            {
                Id = (int)RuntimeJson.Integer(entry.GetProperty("id"), 1, int.MaxValue - 1),
                ProviderId = RuntimeJson.Text(entry, "provider"),
                BindingId = RuntimeJson.Text(entry, "binding"),
                CapabilityId = RuntimeJson.Text(entry, "capability"),
                PlanId = RuntimeJson.Text(entry, "plan"),
                ResourceId = RuntimeJson.Text(entry, "resource"),
                ResourceRevision = RuntimeJson.Text(entry, "revision"),
                EntryId = RuntimeJson.Text(entry, "entry"),
                NodeId = RuntimeJson.Text(entry, "node"),
                StatusKind = entry.GetProperty("statusKind").GetString() ?? "",
                Removal = RuntimeJson.Text(entry, "removal"),
                Peak = (int)RuntimeJson.Integer(entry.GetProperty("peak"), 1, int.MaxValue - 1),
                Origin = ReadMigrationEvent(RebaseWorldEpoch(entry.GetProperty("origin"), sourceEpoch, newWorldEpoch)),
                Inputs = RebaseWorldEpoch(entry.GetProperty("inputs"), sourceEpoch, newWorldEpoch),
                Parameters = RebaseWorldEpoch(entry.GetProperty("parameters"), sourceEpoch, newWorldEpoch),
                CommandPrefix = entry.GetProperty("commandPrefix").GetString() ?? "",
                WorldEpoch = newWorldEpoch,
                CommandId = entry.GetProperty("commandId").GetString() ?? "",
                Handle = RebaseWorldEpoch(entry.GetProperty("handle"), sourceEpoch, newWorldEpoch),
                HandleSlot = (int)RuntimeJson.Integer(entry.GetProperty("handleSlot"), -1, Limits.MaxLiveHandles - 1),
                Pulses = RuntimeJson.Integer(entry.GetProperty("pulses"), 0, RuntimeJson.MaxSafeInteger)
            };
            slot.Subject = RuntimeJson.Rows(entry, "subject", 256)
                .Select(subject => RuntimeJson.Entity(RebaseWorldEpoch(subject, sourceEpoch, newWorldEpoch))).ToArray();
            slot.Identity = EffectIdentity(slot.PlanId, slot.NodeId, slot.Subject);
            slot.Options = ResolveMigrationEffectOptions(entry);
            slot.Step = FindEffectStep(slot);
            slot.ProviderGeneration = kernelFlow.MigrationGeneration(slot.ProviderId);
            foreach (var layer in RuntimeJson.Rows(entry, "layers", slot.Options.MaxStacks))
            {
                RuntimeJson.Shape(layer, "sourceProvider scopeId origin inputs parameters commandPrefix commandId ownerApplication remainingTicks nextPulseTicks");
                var remaining = RuntimeJson.Integer(layer.GetProperty("remainingTicks"), 0, RuntimeJson.MaxSafeInteger);
                var pulse = RuntimeJson.Integer(layer.GetProperty("nextPulseTicks"), -1, RuntimeJson.MaxSafeInteger);
                var inputs = RebaseWorldEpoch(layer.GetProperty("inputs"), sourceEpoch, newWorldEpoch);
                var parameters = RebaseWorldEpoch(layer.GetProperty("parameters"), sourceEpoch, newWorldEpoch);
                var restoredLayer = new EffectLayer
                {
                    SourceProvider = RuntimeJson.Text(layer, "sourceProvider"),
                    ScopeId = RuntimeJson.Text(layer, "scopeId"),
                    Origin = ReadMigrationEvent(RebaseWorldEpoch(layer.GetProperty("origin"), sourceEpoch, newWorldEpoch)),
                    Inputs = inputs,
                    Parameters = parameters,
                    InputFrame = executor.ResolveBoundaryStepInputs(slot.Step!, inputs, out _, wireParameters: parameters,
                        validateEntityLifetimes: false),
                    CommandPrefix = layer.GetProperty("commandPrefix").GetString()!,
                    CommandId = RuntimeJson.Text(layer, "commandId"),
                    OwnerApplication = layer.GetProperty("ownerApplication").ValueKind == JsonValueKind.Null ? null
                        : ReadMigrationOwnerRequest(RebaseWorldEpoch(layer.GetProperty("ownerApplication"), sourceEpoch, newWorldEpoch), newWorldEpoch),
                    ExpiresAtTick = checked(CurrentTick + remaining),
                    NextPulseTick = pulse < 0 ? -1 : checked(CurrentTick + pulse)
                };
                BindMigratedEffectHotPath(slot, restoredLayer);
                slot.Layers.Add(restoredLayer);
            }
            kernelEffects.AdoptMigrationEffect(slot);
        }

        var destination = migrationDestination ?? this;
        kernelFlow.RestoreMigration(isHost ? RebaseWorldEpoch(snapshot, sourceEpoch, newWorldEpoch) : snapshot, destination, isHost);
        if (isHost) RefreshSubscriptionGates();
        kernelOwner.RestoreMigration(owners, newWorldEpoch, isHost, destination.kernelOwner);
        if (isHost) RestoreMigrationOwnerFlights(owners, newWorldEpoch);
        foreach (var pair in handover.Handles)
        {
            var slot = kernelEffects.handleSlots[pair.Key];
            RuntimeJson.Require(slot is { Active: true }, "migration-provider-handle", "Prepared handle was omitted during adoption.");
            slot!.Native = pair.Value.Native; slot.NativeIsLive = pair.Value.IsLive; slot.Cancel = pair.Value.Cancel;
        }
    }

    /// <summary>An accepted publish while native authority is paused. It is not entered into the world ledger until
    /// the replacement host adopts it, so an event already present in the captured ledger remains a duplicate.</summary>
    private DispatchResult BufferMigrationArrival(string provider, long generation, RuntimeEvent value,
        LedgerHash fingerprint, IReadOnlyList<Work> work, EventSubject endSubject, IReadOnlyList<PendingPlan> usage)
    {
        var existing = migrationArrivals.FirstOrDefault(entry => entry.Provider == provider && entry.Event.EventId == value.EventId);
        if (existing != null)
            return existing.Fingerprint == fingerprint
                ? new DispatchResult("duplicate", "duplicate-event", value.EventId)
                : new DispatchResult("rejected", "event-id-conflict", value.EventId);
        RuntimeJson.Require(migrationArrivals.Count < Limits.MaxQueuedEvents, "queue-budget", value.EventId);
        migrationArrivals.Add(new MigrationArrival(provider, generation, value, fingerprint, work, endSubject, usage));
        return new DispatchResult("queued", "migration-paused", value.EventId);
    }

    internal sealed record MigrationOwners(JsonElement State, JsonElement Receiver,
        OwnerCommandLedger Issued, OwnerCommandLedger Received)
    {
        internal IReadOnlyDictionary<int, HandleSlot> ReceiverHandles { get; init; } = new Dictionary<int, HandleSlot>();
        internal IReadOnlyDictionary<string, OwnerCleanupContext> ReceiverCleanups { get; init; } = new Dictionary<string, OwnerCleanupContext>();
    }

    private (JsonElement Snapshot, IReadOnlyDictionary<int, HandleSlot> Handles) ReaddressMigrationOwnerHandles(JsonElement snapshot)
    {
        var retained = new Dictionary<int, HandleSlot>();
        var shared = new HashSet<(int Provider, int Slot, int Generation)>();
        var sourceReceiver = ReadMigrationOwnerLedger(snapshot.GetProperty("owner").GetProperty("receiver").GetProperty("ledger"), WorldEpoch);
        foreach (var application in kernelOwner.ownerReceived.Entries.Where(entry => entry.IsApplication && entry.Result != null))
        {
            if (application.Result!.Outputs.ValueKind != JsonValueKind.Object) continue;
            foreach (var output in application.Result.Outputs.EnumerateObject())
            {
                var value = output.Value;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("local", out _)
                    || !value.TryGetProperty("provider", out _) || !value.TryGetProperty("worldEpoch", out _)
                    || !value.TryGetProperty("lifeEpoch", out _)) continue;
                RuntimeJson.Handle(value);
                var address = value.GetProperty("local").GetInt32();
                if (value.GetProperty("worldEpoch").GetInt64() != WorldEpoch || address >= kernelEffects.handleSlots.Count
                    || kernelEffects.handleSlots[address] is not { Active: true } slot || slot.Generation != value.GetProperty("lifeEpoch").GetInt32()) continue;
                retained[address] = CloneMigrationSlot(slot);
                if (sourceReceiver.TryGet(application.Request.CommandId, out var original) && original.IsApplication
                    && original.Request.HolderIdentity == application.Request.HolderIdentity)
                    shared.Add((value.GetProperty("provider").GetInt32(), address, slot.Generation));
            }
        }
        // Receiver handles keep their public identity. Only the imported host's local addresses move when a
        // promoted holder already uses those slots; cached remote results and replay requests must stay identical.
        var providers = new Dictionary<string, int>(kernelEffects.providerIndex, StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(snapshot, "providers", 2048))
            if (!providers.ContainsKey(RuntimeJson.Text(row, "provider"))) providers.Add(RuntimeJson.Text(row, "provider"), providers.Count);
        RuntimeJson.Require(providers.Count <= 2048, "migration-owner-budget", "Merged provider indices exceed the fixed budget.");
        var sourceProviders = RuntimeJson.Rows(snapshot, "providers", 2048).ToDictionary(
            row => row.GetProperty("index").GetInt32(), row => RuntimeJson.Text(row, "provider"));
        var used = retained.Keys.ToHashSet();
        var addresses = new Dictionary<(int Provider, int Slot, int Generation), (int Provider, int Slot)>();
        var slotMoves = new Dictionary<int, int>();
        foreach (var row in RuntimeJson.Rows(snapshot, "handles", Limits.MaxLiveHandles))
        {
            var provider = RuntimeJson.Text(row, "provider");
            var sourceIndex = sourceProviders.Single(pair => pair.Value == provider).Key;
            var address = row.GetProperty("slot").GetInt32(); var generation = row.GetProperty("generation").GetInt32();
            var key = (sourceIndex, address, generation);
            var next = address;
            if (used.Contains(next) && !shared.Contains(key))
            {
                next = 0;
                while (used.Contains(next)) next++;
            }
            RuntimeJson.Require(next < Limits.MaxLiveHandles, "migration-owner-budget", "Holder and host handles exceed the fixed live-handle budget.");
            used.Add(next); addresses.Add(key, (providers[provider], next)); slotMoves.Add(address, next);
        }
        int Address(JsonNode value) => (int)RuntimeJson.Integer(RuntimeJson.Parse(value.ToJsonString()), -1, int.MaxValue - 1);
        JsonNode Rewrite(JsonNode value)
        {
            if (value is JsonObject obj)
            {
                if (obj["handleSlot"] is JsonValue handleSlot && slotMoves.TryGetValue(Address(handleSlot), out var movedSlot))
                    obj["handleSlot"] = movedSlot;
                if (obj.ContainsKey("worldEpoch") && obj["provider"] is JsonValue && obj["local"] is JsonValue && obj["lifeEpoch"] is JsonValue
                    && addresses.TryGetValue((Address(obj["provider"]!), Address(obj["local"]!), Address(obj["lifeEpoch"]!)), out var moved))
                { obj["provider"] = moved.Provider; obj["local"] = moved.Slot; }
                foreach (var field in obj.ToArray())
                    if (field.Key != "ownerApplication" && field.Value != null) Rewrite(field.Value);
            }
            else if (value is JsonArray array) foreach (var row in array) if (row != null) Rewrite(row);
            return value;
        }
        var document = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        foreach (var field in document.ToArray()) if (field.Key != "owner" && field.Value != null) Rewrite(field.Value);
        foreach (var row in document["handles"]!.AsArray())
        {
            var provider = row!["provider"]!.GetValue<string>();
            var sourceIndex = sourceProviders.Single(pair => pair.Value == provider).Key;
            row["slot"] = addresses[(sourceIndex, row["slot"]!.GetValue<int>(), row["generation"]!.GetValue<int>())].Slot;
        }
        foreach (var pending in document["pending"]!.AsArray())
            if (pending!["wait"] is JsonValue wait) pending["wait"] = slotMoves[wait.GetValue<int>()];
        var owner = document["owner"]!;
        foreach (var lease in owner["leases"]!.AsArray()) Rewrite(lease!["local"]!);
        foreach (var flight in owner["flights"]!.AsArray())
        { Rewrite(flight!["frames"]!); Rewrite(flight["rows"]!); Rewrite(flight["walk"]!); }
        document["providers"] = JsonNode.Parse(RuntimeJson.From(providers.OrderBy(pair => pair.Value)
            .Select(pair => new { provider = pair.Key, index = pair.Value })).GetRawText());
        document["handleGeneration"] = Math.Max(kernelEffects.handleGeneration, snapshot.GetProperty("handleGeneration").GetInt32());
        return (RuntimeJson.Parse(document.ToJsonString()), retained);
    }

    internal static object CaptureMigrationOwnerLedger(OwnerCommandLedger ledger)
    {
        // Repeated step/recipient metadata is shared in the image, while every admitted application, reservation
        // and receipt keeps its own row. A full ledger must not require evicting dedupe rows to fit the JSON bound.
        var templates = new List<OwnerOutput>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<JsonElement>();
        var resultIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        object Request(OwnerOutput request)
        {
            var key = RuntimeJson.StableText(RuntimeJson.From(request with
            { CommandId = "", EventId = "", ScopeId = "", SimulationTick = 0 }));
            if (!indices.TryGetValue(key, out var index))
            { index = templates.Count; indices.Add(key, index); templates.Add(request); }
            return new { template = index, request.CommandId, request.EventId, request.ScopeId, request.SimulationTick };
        }
        int? Result(CommandResult? result)
        {
            if (result == null) return null;
            var value = RuntimeJson.From(CaptureMigrationOwnerResult(result));
            var key = RuntimeJson.StableText(value);
            if (!resultIndices.TryGetValue(key, out var index))
            { index = results.Count; resultIndices.Add(key, index); results.Add(value); }
            return index;
        }
        var entries = ledger.Entries.Where(entry => entry.IsApplication).Select(entry => new
        {
            request = Request(entry.Request),
            result = Result(entry.Result),
            suppressed = entry.Application.Suppressed,
            terminal = entry.Application.Terminal,
            endings = entry.Application.Endings.Values.Select(ended => new
            { slot = ended.Slot, handle = ended.Handle, reason = ended.Reason, result = Result(ended.Result) }).ToArray(),
            controls = entry.Application.Controls.Values.Select(control => new
            { request = Request(control.Request), result = Result(control.Result) }).ToArray()
        }).ToArray();
        return new { templates, results, entries };
    }

    internal static object? CaptureMigrationOwnerResult(CommandResult? result) => result == null ? null : new
    {
        status = result.Status,
        committed = result.CommitState,
        code = result.Code,
        detail = result.Detail,
        outputs = result.Outputs,
        facts = result.Facts
    };

    /// <summary>One receipt not yet reported, as a migration image carries it (the owner's endings and the
    /// scheduler's deferred receipts alike).</summary>
    internal static object CaptureMigrationReceipt(CommandReceipt row) => new
    {
        row.CommandId,
        row.EventId,
        row.CauseId,
        row.RootEventId,
        row.PlanId,
        row.ResourceId,
        row.ResourceRevision,
        row.NodeId,
        row.BindingId,
        row.WorldEpoch,
        row.SimulationTick,
        result = CaptureMigrationOwnerResult(row.Result)
    };

    internal static JsonElement CaptureMigrationOwnerRows(OwnerFlight flight, ResultRowSet rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            ResultRowAbi.WriteSet(writer, new Frame(flight.Slots, rows.Row.Slot, flight.Strings),
                rows.Row, rows.Count, rows.Set, settled: true);
        return RuntimeJson.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private object[] CaptureProviderHandles(string provider)
        => kernelEffects.handleSlots.Select((slot, address) => (slot, address))
            .Where(row => row.slot is { Active: true } slot && slot.ProviderId == provider
                && (slot.Native != null || slot.NativeIsLive != null || slot.Cancel != null && !kernelOwner.IsOwnerProxy(row.address, slot)))
            .Select(row => (object)new
            {
                handle = new RuntimeHandle((int)WorldEpoch, row.slot!.Generation, row.address, kernelEffects.ProviderIndex(provider)).ToJson(),
                kind = row.slot.Kind,
                lifetime = row.slot.Lifetime,
                native = row.slot.Native != null,
                liveness = row.slot.NativeIsLive != null,
                cancel = row.slot.Cancel != null
            }).ToArray();

    private T ProviderHandoverRead<T>(Func<T> callback)
    {
        // Like lifecycle observation, preflight may read data but cannot mutate the runtime. Prepared native
        // references and callbacks stay in memory; only the provider's bounded JSON payload crosses the wire.
        var previous = notifyingLifecycle;
        RuntimeJson.Require(!readingProviderHandover, "migration-provider-prepare", "Provider handover cannot reenter capture or adoption.");
        readingProviderHandover = true;
        notifyingLifecycle = true;
        try { return callback(); }
        catch (RuntimeContractException) { throw; }
        catch (Exception error) { throw new RuntimeContractException("migration-provider-prepare", error.GetType().Name); }
        finally { notifyingLifecycle = previous; readingProviderHandover = false; }
    }

    private object[] CaptureProviderHandovers()
        => providerHandovers.Where(pair => pair.Value.Owner.IsRegistered).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var obligations = RuntimeJson.From(new
                {
                    worldEpoch = WorldEpoch,
                    handles = CaptureProviderHandles(pair.Key),
                    costs = kernelOwner.ownerFlights.Values.SelectMany(flight => kernelOwner.CaptureProviderCosts(flight)
                        .Select(item => RuntimeJson.From(new { command = flight.Request.CommandId, item })))
                        .Where(row => RuntimeJson.Text(row.GetProperty("item"), "provider") == pair.Key).ToArray()
                });
                var payload = ProviderHandoverRead(() => pair.Value.Capture(obligations));
                RuntimeJson.Require(payload.ValueKind == JsonValueKind.Object
                    && RawJson.Bytes(payload).Length <= MaximumEventPayloadBytes,
                    "migration-provider-data", "Provider handover must be an object within the 65536-byte boundary.");
                return (object)new
                {
                    provider = pair.Key,
                    version = RuntimeJson.Text(registry.Tables.Providers[pair.Key], "version"),
                    schema = pair.Value.Schema,
                    payload = payload.Clone()
                };
            }).ToArray();

    private Dictionary<string, JsonElement> ReadProviderHandovers(JsonElement snapshot)
    {
        var rows = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(snapshot, "providerHandover", 2048))
        {
            RuntimeJson.Shape(row, "provider version schema payload");
            var provider = RuntimeJson.Text(row, "provider");
            RuntimeJson.Require(rows.TryAdd(provider, row) && providerHandovers.TryGetValue(provider, out var registered)
                && registered.Owner.IsRegistered && registered.Schema == RuntimeJson.Text(row, "schema")
                && registry.Tables.Providers.TryGetValue(provider, out var declaration)
                && RuntimeJson.Text(declaration, "version") == RuntimeJson.Text(row, "version"),
                "migration-provider-contract", "The current provider, version and handover schema must match: " + provider);
            var payload = row.GetProperty("payload");
            RuntimeJson.Require(payload.ValueKind == JsonValueKind.Object && RawJson.Bytes(payload).Length <= MaximumEventPayloadBytes,
                "migration-provider-data", "Invalid provider payload: " + provider);
        }
        return rows;
    }

    internal sealed record PreparedProvider(string Id, ProviderHandoverAdoption Adoption);
    internal sealed record PreparedProviderState(IReadOnlyList<PreparedProvider> Providers,
        IReadOnlyDictionary<int, ProviderHandleAdoption> Handles);

    private PreparedProviderState PrepareProviderHandovers(JsonElement snapshot, MigrationOwners owners,
        long sourceEpoch, long targetEpoch, bool isHost)
    {
        var source = ReadProviderHandovers(snapshot);
        var retainedImage = RuntimeJson.From(new { providerHandover = CaptureProviderHandovers() });
        var retained = ReadProviderHandovers(retainedImage);
        var indices = RuntimeJson.Rows(snapshot, "providers", 2048).ToDictionary(
            row => RuntimeJson.Text(row, "provider"), row => row.GetProperty("index").GetInt32(), StringComparer.Ordinal);
        var required = new Dictionary<int, (string Provider, JsonElement Row)>();
        foreach (var row in RuntimeJson.Rows(snapshot, "handles", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(row, "slot kind lifetime provider generation scheduleId effectId statusKind native liveness cancel portable");
            RuntimeJson.Require(row.GetProperty("native").ValueKind is JsonValueKind.True or JsonValueKind.False
                && row.GetProperty("liveness").ValueKind is JsonValueKind.True or JsonValueKind.False
                && row.GetProperty("cancel").ValueKind is JsonValueKind.True or JsonValueKind.False
                && row.GetProperty("portable").ValueKind is JsonValueKind.True or JsonValueKind.False
                && (!row.GetProperty("portable").GetBoolean()
                    || !row.GetProperty("native").GetBoolean() && !row.GetProperty("liveness").GetBoolean()),
                "migration-provider-handle", "A portable handle cannot hide native state or an invalid obligation marker.");
            if (row.GetProperty("portable").GetBoolean() && row.GetProperty("cancel").GetBoolean())
                RuntimeJson.Require(RuntimeJson.Rows(snapshot.GetProperty("owner"), "leases", Limits.MaxLiveHandles)
                    .Any(lease => RuntimeJson.Text(lease.GetProperty("request"), "provider") == RuntimeJson.Text(row, "provider")
                        && lease.GetProperty("local").GetProperty("local").GetInt32() == row.GetProperty("slot").GetInt32()
                        && lease.GetProperty("local").GetProperty("lifeEpoch").GetInt32() == row.GetProperty("generation").GetInt32()),
                    "migration-provider-handle", "Only a data-defined owner proxy can carry a kernel-rebuilt cancellation.");
            if (row.GetProperty("portable").ValueKind == JsonValueKind.True) continue;
            var provider = RuntimeJson.Text(row, "provider");
            RuntimeJson.Require(source.ContainsKey(provider), "migration-native-handle",
                "No data handover for native handle or callback: " + provider + "/" + RuntimeJson.Text(row, "kind"));
            required.Add(row.GetProperty("slot").GetInt32(), (provider, row));
        }
        foreach (var (slot, address) in kernelEffects.handleSlots.Select((slot, address) => (slot, address)))
        {
            if (slot is not { Active: true } || slot.Native == null && slot.NativeIsLive == null
                && (slot.Cancel == null || kernelOwner.IsOwnerProxy(address, slot))) continue;
            RuntimeJson.Require(retained.ContainsKey(slot.ProviderId) && owners.ReceiverHandles.ContainsKey(address),
                "migration-native-handle", "This holder has no data-defined retained handle: " + slot.ProviderId + "/" + slot.Kind);
            var row = RuntimeJson.From(new
            {
                slot = address,
                provider = slot.ProviderId,
                generation = slot.Generation,
                native = slot.Native != null,
                liveness = slot.NativeIsLive != null,
                cancel = slot.Cancel != null
            });
            if (required.TryGetValue(address, out var previous))
                RuntimeJson.Require(previous.Provider == slot.ProviderId && previous.Row.GetProperty("generation").GetInt32() == slot.Generation,
                    "migration-provider-handle", "A retained handle collides with a different source life.");
            required[address] = (slot.ProviderId, row);
        }
        foreach (var flight in RuntimeJson.Rows(snapshot.GetProperty("owner"), "flights", Limits.MaxLiveHandles))
        {
            var costs = RuntimeJson.Rows(flight, "costs", CostOptions.MaximumItems);
            RuntimeJson.Require(flight.GetProperty("costHeld").GetBoolean() == (costs.Length != 0),
                "migration-owner-cost", "A held reservation must carry all resource obligations.");
            foreach (var row in costs)
            {
                var provider = RuntimeJson.Text(row, "provider");
                RuntimeJson.Require(source.ContainsKey(provider) && registry.Tables.ResourceProviders.TryGetValue(RuntimeJson.Text(row, "kind"), out var registered)
                    && registered.Owner == provider && registered.Provider.Cost != null,
                    "migration-owner-cost", "The resource reservation has no data-defined owner: " + provider);
            }
        }
        var prepared = new List<PreparedProvider>();
        var handles = new Dictionary<int, ProviderHandleAdoption>();
        foreach (var provider in source.Keys.Concat(retained.Keys).Distinct().OrderBy(value => value, StringComparer.Ordinal))
        {
            var expected = required.Where(pair => pair.Value.Provider == provider).ToArray();
            var context = RuntimeJson.From(new
            {
                sourceEpoch,
                targetEpoch,
                isHost,
                source = source.TryGetValue(provider, out var incoming) ? incoming : (JsonElement?)null,
                retained = retained.TryGetValue(provider, out var local) ? local : (JsonElement?)null,
                handles = expected.Select(pair => new
                {
                    handle = new RuntimeHandle((int)targetEpoch,
                    pair.Value.Row.GetProperty("generation").GetInt32(), pair.Key, indices[provider]).ToJson(),
                    native = pair.Value.Row.GetProperty("native").GetBoolean(),
                    liveness = pair.Value.Row.GetProperty("liveness").GetBoolean(),
                    cancel = pair.Value.Row.GetProperty("cancel").GetBoolean()
                }).ToArray(),
                // Request/plan locks, cost declarations and receipts accompany the private payload. The provider
                // must validate its reservation tokens and balance against these exact, already validated rows.
                owner = owners.State,
                receiver = owners.Receiver
            });
            var adoption = ProviderHandoverRead(() => providerHandovers[provider].Prepare(context));
            RuntimeJson.Require(adoption != null && adoption.Adopt != null && adoption.Rollback != null && adoption.Handles != null
                && adoption.Handles.Count == expected.Length, "migration-provider-handle", "Every native/callback obligation needs exactly one rebind.");
            foreach (var binding in adoption!.Handles!)
            {
                var address = binding.Handle.Local;
                RuntimeJson.Require(required.TryGetValue(address, out var row) && row.Provider == provider
                    && binding.Handle.WorldEpoch == targetEpoch
                    && binding.Handle.LifeEpoch == row.Row.GetProperty("generation").GetInt32()
                    && binding.Handle.Provider == indices[provider]
                    && (binding.Native != null) == row.Row.GetProperty("native").GetBoolean()
                    && (binding.IsLive != null) == row.Row.GetProperty("liveness").GetBoolean()
                    && (binding.Cancel != null) == row.Row.GetProperty("cancel").GetBoolean()
                    && handles.TryAdd(address, binding), "migration-provider-handle", "A rebind changed identity or lost native cleanup.");
            }
            prepared.Add(new(provider, adoption));
        }
        return new(prepared, handles);
    }

    private static void ValidateMigrationCosts(JsonElement flight, ResolvedStep step)
    {
        var rows = RuntimeJson.Rows(flight, "costs", CostOptions.MaximumItems);
        RuntimeJson.Require(flight.GetProperty("costHeld").ValueKind is JsonValueKind.True or JsonValueKind.False
            && flight.GetProperty("costHeld").GetBoolean() == (rows.Length != 0),
            "migration-owner-cost", "Reservation marker and held data disagree.");
        if (rows.Length == 0) return;
        RuntimeJson.Require(step.Cost is { Required: true } cost && cost.Items.Count == rows.Length,
            "migration-owner-cost", "Every held cost must match the loaded declaration.");
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            RuntimeJson.Shape(row, "index provider kind id amount");
            var item = step.Cost!.Items[index];
            RuntimeJson.Require(RuntimeJson.Integer(row.GetProperty("index"), 0, CostOptions.MaximumItems - 1) == index
                && RuntimeJson.Text(row, "kind") == item.ResourceKind && RuntimeJson.Text(row, "id") == item.ResourceId
                && RuntimeJson.Integer(row.GetProperty("amount"), 1) == item.Amount,
                "migration-owner-cost", "A reservation changed its resource, amount or settlement order.");
            RuntimeJson.Text(row, "provider");
        }
    }

    internal static OwnerOutput ReadMigrationOwnerRequest(JsonElement row, long epoch)
    {
        var request = JsonSerializer.Deserialize<OwnerOutput>(row.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new RuntimeContractException("migration-owner", "Missing holder request.");
        return ValidateMigrationOwnerRequest(request, epoch);
    }

    private static OwnerOutput ValidateMigrationOwnerRequest(OwnerOutput request, long epoch)
    {
        RuntimeJson.CommandId(request.CommandId); RuntimeJson.Text(request.EventId); RuntimeJson.Text(request.ScopeId);
        RuntimeJson.Text(request.Provider); RuntimeJson.Text(request.PlanId); RuntimeJson.Text(request.NodeId);
        RuntimeJson.Text(request.BindingId); RuntimeJson.Text(request.Holder);
        RuntimeJson.Require(request.WorldEpoch == epoch && request.Inputs.ValueKind == JsonValueKind.Object
            && request.Identities.ValueKind == JsonValueKind.Object, "migration-owner", "Invalid holder request identity.");
        RuntimeJson.Integer(request.SimulationTick, 0);
        OwnerCommandLedger.Declaration(request);
        return request;
    }

    internal static CommandResult? ReadMigrationOwnerResult(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.Null) return null;
        RuntimeJson.Shape(row, "status committed code detail outputs facts");
        var facts = RuntimeJson.Rows(row, "facts").Select(fact => new RuntimeFact(
            RuntimeJson.Text(fact, "bindingId"), fact.GetProperty("outputs"))).ToArray();
        var result = CommandResult.Create(RuntimeJson.Text(row, "status"), RuntimeJson.Text(row, "committed"),
            row.GetProperty("code").GetString()!, row.GetProperty("detail").GetString()!, row.GetProperty("outputs"), facts);
        RuntimeJson.Require(CommandResultRules.TryValidate(result, out _), "migration-owner", "Invalid holder receipt.");
        return result;
    }

    // Admission remains 8192 application/reservation rows. A reserved obligation can retain both a
    // spontaneous notification and a separately requested cleanup result, so their deduplicated metadata
    // table has at most two results per admitted row. This does not admit another command or reservation.
    private const int MaximumOwnerResultTemplates = OwnerCommandLedger.Capacity * 2;

    private static OwnerCommandLedger ReadMigrationOwnerLedger(JsonElement image, long epoch)
    {
        RuntimeJson.Shape(image, "templates results entries");
        var templates = RuntimeJson.Rows(image, "templates", OwnerCommandLedger.Capacity)
            .Select(row => ReadMigrationOwnerRequest(row, epoch)).ToArray();
        var results = RuntimeJson.Rows(image, "results", MaximumOwnerResultTemplates)
            .Select(ReadMigrationOwnerResult).ToArray();
        RuntimeJson.Require(results.All(result => result != null), "migration-owner", "Null holder receipt template.");
        var rows = image.GetProperty("entries");
        RuntimeJson.Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() <= OwnerCommandLedger.Capacity,
            "migration-owner-budget", "Holder ledger exceeds its bounded reservation budget.");
        OwnerOutput Request(JsonElement row)
        {
            RuntimeJson.Shape(row, "template commandId eventId scopeId simulationTick");
            var index = (int)RuntimeJson.Integer(row.GetProperty("template"), 0, OwnerCommandLedger.Capacity - 1);
            RuntimeJson.Require(index < templates.Length, "migration-owner", "Missing holder request template.");
            return ValidateMigrationOwnerRequest(templates[index] with
            {
                CommandId = RuntimeJson.Text(row, "commandId"),
                EventId = RuntimeJson.Text(row, "eventId"),
                ScopeId = RuntimeJson.Text(row, "scopeId"),
                SimulationTick = RuntimeJson.Integer(row.GetProperty("simulationTick"), 0)
            }, epoch);
        }
        CommandResult? Result(JsonElement row)
        {
            if (row.ValueKind == JsonValueKind.Null) return null;
            var index = (int)RuntimeJson.Integer(row, 0, MaximumOwnerResultTemplates - 1);
            RuntimeJson.Require(index < results.Length, "migration-owner", "Missing holder receipt template.");
            return results[index];
        }
        var ledger = new OwnerCommandLedger();
        foreach (var row in rows.EnumerateArray())
        {
            RuntimeJson.Shape(row, "request result suppressed terminal endings controls");
            var request = Request(row.GetProperty("request"));
            RuntimeJson.Require(request.Operation == "apply", "migration-owner", "The ledger row is not an application.");
            var refusal = ledger.Admit(request, null, out var entry, out var duplicate);
            RuntimeJson.Require(refusal == null && !duplicate, refusal?.Code ?? "migration-owner", "Duplicate holder application.");
            entry!.Result = Result(row.GetProperty("result"));
            foreach (var ending in RuntimeJson.Rows(row, "endings", entry.Application.Slots.Length))
            {
                RuntimeJson.Shape(ending, "slot handle reason result");
                var slot = RuntimeJson.Text(ending, "slot"); var handle = ending.GetProperty("handle");
                OwnerCommandLedger.ReadEnding(OwnerCommandLedger.WithEnding(request, slot, handle), out _, out _);
                var outcome = Result(ending.GetProperty("result"));
                RuntimeJson.Require(outcome != null && OwnerCommandLedger.EndingMatches(entry, slot, handle)
                    && entry.Application.Endings.TryAdd(slot, new(slot, handle, RuntimeJson.Text(ending, "reason"), outcome!)),
                    "migration-owner", "Spontaneous ending lost its reserved handle or repeated an obligation.");
            }
            foreach (var control in RuntimeJson.Rows(row, "controls", OwnerCommandLedger.Declaration(request).Length))
            {
                RuntimeJson.Shape(control, "request result");
                var output = Request(control.GetProperty("request"));
                RuntimeJson.Require(output.EventId == request.CommandId && output.Operation != "apply",
                    "migration-owner", "Cleanup belongs to another application.");
                refusal = ledger.Admit(output, null, out var accepted, out duplicate);
                RuntimeJson.Require(refusal == null && !duplicate, refusal?.Code ?? "migration-owner", "Duplicate holder cleanup.");
                accepted!.Result = Result(control.GetProperty("result"));
            }
            entry.Application.Suppressed = row.GetProperty("suppressed").GetBoolean();
            RuntimeJson.Require(entry.Application.Terminal == row.GetProperty("terminal").GetBoolean(),
                "migration-owner", "Terminal state differs from the reserved cleanup receipts.");
        }
        return ledger;
    }

    private static Dictionary<string, OwnerCleanupContext> ReadMigrationOwnerCleanups(JsonElement receiver, OwnerCommandLedger ledger, long epoch)
    {
        RuntimeJson.Shape(receiver, "ledger descriptors cleanup endings");
        var descriptors = RuntimeJson.Rows(receiver, "descriptors", OwnerCommandLedger.Capacity).Select(row =>
        {
            RuntimeJson.Shape(row, "generation capability slots ports maxStacks status subject");
            var subject = RuntimeJson.Rows(row, "subject", 256).Select(RuntimeJson.Entity).ToArray();
            RuntimeJson.Require(subject.All(entity => entity.WorldEpoch == epoch), "migration-owner", "Cleanup subject belongs to another world.");
            return new OwnerCleanupContext(RuntimeJson.Integer(row.GetProperty("generation"), 1), RuntimeJson.Text(row, "capability"),
                RuntimeJson.Strings(row.GetProperty("slots")), RuntimeJson.Rows(row, "ports", OwnerCommandLedger.Capacity),
                (int)RuntimeJson.Integer(row.GetProperty("maxStacks"), 0), row.GetProperty("status").GetString()!, subject);
        }).ToArray();
        var cleanup = new Dictionary<string, OwnerCleanupContext>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(receiver, "cleanup", OwnerCommandLedger.Capacity))
        {
            RuntimeJson.Shape(row, "command descriptor");
            var command = RuntimeJson.Text(row, "command");
            var index = (int)RuntimeJson.Integer(row.GetProperty("descriptor"), 0, OwnerCommandLedger.Capacity - 1);
            RuntimeJson.Require(index < descriptors.Length && !cleanup.ContainsKey(command)
                && ledger.TryGet(command, out var application) && application.IsApplication
                && descriptors[index].Slots.SequenceEqual(application.Application.Slots),
                "migration-owner", "Cleanup lost its admitted application or descriptor.");
            cleanup.Add(command, descriptors[index]);
        }
        var endingIds = new HashSet<(string Command, string Slot)>();
        foreach (var row in RuntimeJson.Rows(receiver, "endings", OwnerCommandLedger.Capacity))
        {
            RuntimeJson.Shape(row, "command slot");
            var command = RuntimeJson.Text(row, "command"); var slot = RuntimeJson.Text(row, "slot");
            RuntimeJson.Require(endingIds.Add((command, slot)) && ledger.TryGet(command, out var application)
                && application.IsApplication && application.Application.Endings.ContainsKey(slot),
                "migration-owner", "Queued ending lost or repeated its retained notification identity.");
        }
        return cleanup;
    }

    internal static void AdoptMigrationOwnerLedger(OwnerCommandLedger target, OwnerCommandLedger source)
    {
        target.Clear();
        foreach (var entry in source.Entries)
        {
            var refusal = target.Admit(entry.Request, null, out var copy, out _);
            RuntimeJson.Require(refusal == null, refusal?.Code ?? "migration-owner", "Unable to adopt validated holder ledger.");
            copy!.Result = entry.Result;
            copy.Application.Suppressed = entry.Application.Suppressed;
            if (entry.IsApplication)
                foreach (var ended in entry.Application.Endings)
                    copy.Application.Endings.Add(ended.Key, ended.Value);
        }
    }

    private MigrationOwners ValidateMigrationOwners(JsonElement snapshot, long sourceEpoch, long targetEpoch)
    {
        var state = snapshot.GetProperty("owner");
        RuntimeJson.Shape(state, "live controlSequence issued receiver effects leases earlyEnds receipts flights");
        RuntimeJson.Require(state.GetProperty("live").ValueKind is JsonValueKind.True or JsonValueKind.False,
            "migration-owner", "The live holder marker must be boolean.");
        RuntimeJson.Integer(state.GetProperty("controlSequence"), 0, RuntimeJson.MaxSafeInteger);
        RuntimeJson.Require(kernelOwner.ownerFlights.Values.All(flight => flight.Hold == null || flight.Hold.Reserved
            && flight.Hold.Unreleased == 0 && flight.Hold.Held.All(item => registry.Tables.ResourceProviders.TryGetValue(item.Kind, out var registered)
                && ReferenceEquals(registered.Provider.Cost, item.Owner) && providerHandovers.TryGetValue(registered.Owner, out var adapter)
                && adapter.Owner.IsRegistered)), "migration-owner-cost",
            "This peer has an unsettled resource reservation without a current data adoption contract.");
        // Validate the predecessor's complete state even on replicas. A corrupt reservation must not be hidden
        // by promotion or by an empty receiver table on this machine.
        var issued = ReadMigrationOwnerLedger(state.GetProperty("issued"), sourceEpoch);
        var capturedReceiver = state.GetProperty("receiver");
        ReadMigrationOwnerCleanups(capturedReceiver, ReadMigrationOwnerLedger(capturedReceiver.GetProperty("ledger"), sourceEpoch), sourceEpoch);
        // Holder execution remains on its original machine. Preserve that machine's receipts and cleanup
        // descriptors, rather than installing the predecessor's receiver callbacks on every peer.
        var receiver = RuntimeJson.Parse(RuntimeJson.StableText(RuntimeJson.From(kernelOwner.CaptureMigrationReceiver())));
        foreach (var pair in ReadMigrationOwnerCleanups(receiver, kernelOwner.ownerReceived, sourceEpoch))
        {
            RuntimeJson.Require(kernelOwner.ownerReceived.TryGet(pair.Key, out var application)
                && registry.IsRegistered(application.Request.Provider, pair.Value.ProviderGeneration),
                "binding-lifecycle", "The holder cleanup provider generation changed before migration.");
        }
        var flightIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(state, "flights", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(row, "request cause work step inputs cancelSent result effect costHeld costs frames storedValues rows walk play");
            RestoreMigrationPlay(row);
            var request = ReadMigrationOwnerRequest(row.GetProperty("request"), sourceEpoch);
            RuntimeJson.Require(flightIds.Add(request.CommandId) && issued.TryGet(request.CommandId, out var application)
                && OwnerCommandLedger.Same(application.Request, request), "migration-owner", "Flight lost its application reservation.");
            ReadMigrationOwnerResult(row.GetProperty("result"));
            var cause = kernelFlow.ReadMigrationOwnerCause(row.GetProperty("cause"));
            if (row.GetProperty("effect").ValueKind == JsonValueKind.Null)
            {
                var work = kernelFlow.RequireMigrationWork(row.GetProperty("work"));
                var step = work.Entry.Steps[MigrationStep(work, row.GetProperty("step"))];
                RuntimeJson.Require(step.Frames.Index == request.StepIndex && step.BindingId == request.BindingId
                    && step.ProviderId == request.Provider && step.NodeId == request.NodeId && work.Plan.Plan.Id == request.PlanId,
                    "migration-owner", "Flight no longer names the loaded step.");
                executor.ResolveBoundaryStepInputs(step, row.GetProperty("inputs"), out _, validateEntityLifetimes: false);
                ValidateMigrationCosts(row, step);
                RestoreMigrationOwnerFrames(row, work, out _, out _, out _, out _);
                RestoreMigrationStoredValues(row, work);
                RestoreMigrationWalk(row, work);
            }
            else
            {
                var effect = RuntimeJson.Rows(snapshot, "effects", Limits.MaxLiveHandles)
                    .SingleOrDefault(effect => effect.GetProperty("id").GetInt32() == row.GetProperty("effect").GetInt32());
                RuntimeJson.Require(effect.ValueKind == JsonValueKind.Object && RuntimeJson.Text(effect, "plan") == request.PlanId
                    && RuntimeJson.Text(effect, "node") == request.NodeId && RuntimeJson.Text(effect, "binding") == request.BindingId,
                    "migration-owner", "Pulse lost its live effect.");
                var pulseStep = plans.Loaded[request.PlanId].Plan.Entries.SelectMany(entry => entry.Steps)
                    .Single(step => step.Frames.Index == request.StepIndex);
                ValidateMigrationCosts(row, pulseStep);
            }
            RuntimeJson.Require(cause.Event.WorldEpoch == sourceEpoch, "migration-owner", "Flight cause belongs to another world.");
        }
        var effectIds = new HashSet<int>();
        foreach (var row in RuntimeJson.Rows(state, "effects", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(row, "request effect cause");
            var request = ReadMigrationOwnerRequest(row.GetProperty("request"), sourceEpoch);
            RuntimeJson.Require(effectIds.Add(row.GetProperty("effect").GetInt32()) && issued.TryGet(request.CommandId, out var application)
                && OwnerCommandLedger.Same(application.Request, request)
                && RuntimeJson.Rows(snapshot, "effects", Limits.MaxLiveHandles).Any(effect => effect.GetProperty("id").GetInt32() == row.GetProperty("effect").GetInt32()),
                "migration-owner", "Effect lost its holder reservation or live slot.");
            kernelFlow.ReadMigrationOwnerCause(row.GetProperty("cause"));
        }
        var leaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(state, "leases", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(row, "request remote local");
            var request = ReadMigrationOwnerRequest(row.GetProperty("request"), sourceEpoch);
            RuntimeJson.Handle(row.GetProperty("remote")); RuntimeJson.Handle(row.GetProperty("local"));
            var local = row.GetProperty("local");
            RuntimeJson.Require(leaseIds.Add(request.CommandId + "\n" + row.GetProperty("remote").GetRawText())
                && issued.TryGet(request.CommandId, out var application)
                && OwnerCommandLedger.Same(application.Request, request)
                && application.Result != null && application.Result.Outputs.ValueKind == JsonValueKind.Object
                && application.Result.Outputs.EnumerateObject().Any(output =>
                    output.Value.GetRawText() == row.GetProperty("remote").GetRawText()
                    && application.Application.Slots.Contains("handle:" + output.Name, StringComparer.Ordinal))
                && RuntimeJson.Rows(snapshot, "handles", Limits.MaxLiveHandles).Any(handle =>
                    handle.GetProperty("slot").GetInt32() == local.GetProperty("local").GetInt32()
                    && handle.GetProperty("generation").GetInt32() == local.GetProperty("lifeEpoch").GetInt32()
                    && RuntimeJson.Text(handle, "provider") == request.Provider), "migration-owner", "Proxy lost its admitted handle.");
        }
        foreach (var effect in RuntimeJson.Rows(snapshot, "effects", Limits.MaxLiveHandles))
            foreach (var layer in RuntimeJson.Rows(effect, "layers", Limits.MaxLiveHandles))
            {
                RuntimeJson.Shape(layer, "sourceProvider scopeId origin inputs parameters commandPrefix commandId ownerApplication remainingTicks nextPulseTicks");
                RuntimeJson.Text(layer, "sourceProvider"); RuntimeJson.Text(layer, "scopeId");
                var origin = ReadMigrationEvent(layer.GetProperty("origin"));
                RuntimeJson.Require(origin.WorldEpoch == sourceEpoch && origin.ScopeId == RuntimeJson.Text(layer, "scopeId")
                    && registry.BindingViews.TryGetValue(origin.BindingId, out var binding)
                    && binding.ProviderId == RuntimeJson.Text(layer, "sourceProvider"),
                    "migration-owner", "Layer source differs from its admitted event.");
                var row = layer.GetProperty("ownerApplication");
                if (row.ValueKind == JsonValueKind.Null) continue;
                var request = ReadMigrationOwnerRequest(row, sourceEpoch);
                RuntimeJson.Require(issued.TryGet(request.CommandId, out var application)
                    && OwnerCommandLedger.Same(application.Request, request) && request.ScopeId == origin.ScopeId
                    && request.EventId == origin.EventId && request.CommandId == RuntimeJson.Text(layer, "commandId"),
                    "migration-owner", "Layer lost its restore reservation.");
            }
        var earlyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(state, "earlyEnds", Limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(row, "command result");
            RuntimeJson.Require(earlyIds.Add(RuntimeJson.Text(row, "command")) && issued.TryGet(RuntimeJson.Text(row, "command"), out var application)
                && application.IsApplication && ReadMigrationOwnerResult(row.GetProperty("result")) != null,
                "migration-owner", "Early ending lost its application.");
        }
        foreach (var row in RuntimeJson.Rows(state, "receipts", OwnerCommandLedger.Capacity)) ReadMigrationOwnerReceipt(row, sourceEpoch);
        var rebased = RebaseWorldEpoch(state, sourceEpoch, targetEpoch);
        var retained = RebaseWorldEpoch(receiver, sourceEpoch, targetEpoch);
        var received = ReadMigrationOwnerLedger(retained.GetProperty("ledger"), targetEpoch);
        return new MigrationOwners(rebased, retained, ReadMigrationOwnerLedger(rebased.GetProperty("issued"), targetEpoch), received)
        { ReceiverCleanups = ReadMigrationOwnerCleanups(retained, received, targetEpoch) };
    }

    private static void RestoreMigrationOwnerFrames(JsonElement row, Work work, out Dictionary<int, JsonElement> frames,
        out Dictionary<int, ResultRowSet> rows, out FrameValue[] slots, out byte[] strings)
    {
        frames = RuntimeJson.Rows(row, "frames", work.Plan.Plan.Limits.MaxTotalSteps).ToDictionary(
            entry => (int)RuntimeJson.Integer(entry.GetProperty("step"), 0, work.Plan.Plan.Limits.MaxTotalSteps - 1), entry => entry.GetProperty("value"));
        rows = new Dictionary<int, ResultRowSet>();
        var arena = new FrameSpace(1024, 256 * 1024);
        foreach (var entry in RuntimeJson.Rows(row, "rows", work.Plan.Plan.Limits.MaxTotalSteps))
        {
            var index = (int)RuntimeJson.Integer(entry.GetProperty("step"), 0, work.Plan.Plan.Limits.MaxTotalSteps - 1);
            var descriptor = work.Entry.Steps.First(step => step.Frames.Index == index).Frames.Row
                ?? throw new RuntimeContractException("migration-owner", "Captured rows have no loaded descriptor.");
            if (arena.SlotCapacity < descriptor.Slot + descriptor.Span) arena.Grow(descriptor.Slot + descriptor.Span);
            var value = entry.GetProperty("value");
            var set = value.ValueKind == JsonValueKind.Array;
            var count = set ? value.GetArrayLength() : 1;
            var writer = new FrameWriter(arena.Slots, descriptor.Slot, descriptor.Span, arena.Strings);
            for (var at = 0; at < count; at++)
            {
                var item = set ? value[at] : value;
                ResultRowAbi.Write(ref writer, descriptor, at, item, item.GetProperty("target"));
                ResultRowAbi.ValidatePairing(descriptor, at, item);
            }
            rows.Add(index, new ResultRowSet(descriptor, count, set) { Space = arena });
        }
        slots = arena.Slots; strings = arena.Strings.Memory.ToArray();
    }

    /// <summary>Puts back what resumes a dispatch on the machine that becomes host, after the owner component adopted
    /// its tables: each owner-run effect with the event that caused it, and each flight with its work, frames and
    /// resolved inputs. A flight whose answer already arrived settles here; the others ask their holder again.</summary>
    private void RestoreMigrationOwnerFlights(MigrationOwners image, long targetEpoch)
    {
        foreach (var row in RuntimeJson.Rows(image.State, "effects", Limits.MaxLiveHandles))
        {
            var slot = kernelEffects.effects[row.GetProperty("effect").GetInt32()];
            kernelEffects.ownerEffects.Add(slot.HandleId, new OwnerEffect(ReadMigrationOwnerRequest(row.GetProperty("request"), targetEpoch),
                kernelFlow.ReadMigrationOwnerCause(row.GetProperty("cause")), slot));
        }
        foreach (var row in RuntimeJson.Rows(image.State, "flights", Limits.MaxLiveHandles))
        {
            var request = ReadMigrationOwnerRequest(row.GetProperty("request"), targetEpoch);
            var flight = new OwnerFlight
            {
                Request = request,
                Pending = kernelFlow.ReadMigrationOwnerCause(row.GetProperty("cause")),
                CancelSent = row.GetProperty("cancelSent").GetBoolean(),
                Play = RestoreMigrationPlay(row)
            };
            ResolvedStep step;
            if (row.GetProperty("effect").ValueKind == JsonValueKind.Null)
            {
                flight.Work = kernelFlow.RequireMigrationWork(row.GetProperty("work"));
                flight.Step = MigrationStep(flight.Work, row.GetProperty("step")); step = flight.Work.Entry.Steps[flight.Step];
                RestoreMigrationOwnerFrames(row, flight.Work, out flight.Frames, out flight.Rows, out flight.Slots, out flight.Strings);
                flight.Stored = RestoreMigrationStoredValues(row, flight.Work);
                flight.Walk = RestoreMigrationWalk(row, flight.Work).ToArray();
            }
            else
            {
                var effect = kernelEffects.effects[row.GetProperty("effect").GetInt32()];
                step = FindEffectStep(effect)!; flight.Pulse = new OwnerPulse(effect, step);
            }
            flight.Inputs = executor.ResolveBoundaryStepInputs(step, row.GetProperty("inputs"), out flight.Parameters,
                validateEntityLifetimes: false);
            var costs = RuntimeJson.Rows(row, "costs", CostOptions.MaximumItems);
            if (costs.Length != 0)
            {
                flight.Hold = new KernelExecutor.CostHold { Reserved = true };
                foreach (var cost in costs)
                {
                    var kind = RuntimeJson.Text(cost, "kind");
                    flight.Hold.Held.Add((registry.Tables.ResourceProviders[kind].Provider.Cost!, kind,
                        RuntimeJson.Text(cost, "id"), RuntimeJson.Integer(cost.GetProperty("amount"), 1)));
                }
            }
            var result = ReadMigrationOwnerResult(row.GetProperty("result"));
            kernelOwner.ownerFlights.Add(request.CommandId, flight);
            if (result != null) ReceiveOwnerResult(request.CommandId, request.Holder, targetEpoch, result);
            else kernelOwner.ownerControls.Add(request); // The original application ID reaches the holder's retained dedupe row.
        }
        foreach (var entry in kernelOwner.ownerIssued.Entries)
            if (!entry.IsApplication && (entry.Result == null || entry.Result.CommitState == CommitStates.None))
                kernelOwner.ownerControls.Add(entry.Request);
    }

    internal IReadOnlyList<OwnerOutput> OwnerMigrationRequests => kernelOwner.ownerIssued.Entries.Select(entry => entry.Request).ToArray();

    internal static CommandReceipt ReadMigrationOwnerReceipt(JsonElement row, long epoch)
    {
        RuntimeJson.Shape(row, "commandId eventId causeId rootEventId planId resourceId resourceRevision nodeId bindingId worldEpoch simulationTick result");
        RuntimeJson.Require(RuntimeJson.Integer(row.GetProperty("worldEpoch")) == epoch,
            "migration-owner", "Cleanup receipt belongs to another world.");
        var result = ReadMigrationOwnerResult(row.GetProperty("result"));
        RuntimeJson.Require(result != null, "migration-owner", "Cleanup receipt has no result.");
        return new CommandReceipt(RuntimeJson.Text(row, "commandId"), RuntimeJson.Text(row, "eventId"),
            row.GetProperty("causeId").GetString(), RuntimeJson.Text(row, "rootEventId"), RuntimeJson.Text(row, "planId"),
            row.GetProperty("resourceId").GetString()!, row.GetProperty("resourceRevision").GetString()!, RuntimeJson.Text(row, "nodeId"),
            RuntimeJson.Text(row, "bindingId"), epoch, RuntimeJson.Integer(row.GetProperty("simulationTick")), result!);
    }

    private EffectOptions ResolveMigrationEffectOptions(JsonElement entry)
    {
        RuntimeJson.Shape(entry,
            "id provider binding capability plan resource revision entry node statusKind removal subject layers peak endsOn origin inputs parameters commandPrefix commandId handle handleSlot pulses");
        var slot = new EffectSlot
        {
            PlanId = RuntimeJson.Text(entry, "plan"),
            EntryId = RuntimeJson.Text(entry, "entry"),
            NodeId = RuntimeJson.Text(entry, "node"),
            BindingId = RuntimeJson.Text(entry, "binding")
        };
        var step = FindEffectStep(slot);
        RuntimeJson.Require(step?.Effect != null && plans.Loaded.TryGetValue(slot.PlanId, out var loaded)
            && loaded.Plan.ResourceId == RuntimeJson.Text(entry, "resource")
            && loaded.Plan.ResourceRevision == RuntimeJson.Text(entry, "revision")
            && step.ProviderId == RuntimeJson.Text(entry, "provider")
            && step.CapabilityId == RuntimeJson.Text(entry, "capability"), "migration-effect-plan",
            "The loaded plan no longer contains migrated effect " + slot.PlanId + "/" + slot.NodeId + ".");
        var options = step!.Effect!;
        var parameters = entry.GetProperty("parameters");
        RuntimeJson.Parameters(parameters, step.Capability);
        executor.ResolveBoundaryStepInputs(step, entry.GetProperty("inputs"), out _, wireParameters: parameters,
            validateEntityLifetimes: false);
        foreach (var layer in RuntimeJson.Rows(entry, "layers", options.MaxStacks))
        {
            RuntimeJson.Shape(layer, "sourceProvider scopeId origin inputs parameters commandPrefix commandId ownerApplication remainingTicks nextPulseTicks");
            var layerParameters = layer.GetProperty("parameters");
            RuntimeJson.Parameters(layerParameters, step.Capability);
            executor.ResolveBoundaryStepInputs(step, layer.GetProperty("inputs"), out _, wireParameters: layerParameters,
                validateEntityLifetimes: false);
        }
        var endings = RuntimeJson.Strings(entry.GetProperty("endsOn"));
        RuntimeJson.Require(endings.Distinct(StringComparer.Ordinal).Count() == endings.Length
            && endings.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(options.EndTriggers.Select(ending => ending.BindingId).OrderBy(value => value, StringComparer.Ordinal)),
            "migration-effect-plan", "Migrated endings disagree with the loaded effect " + slot.PlanId + "/" + slot.NodeId + ".");
        return options;
    }

    // A saved scheduled decision already charged its gates, so it must own the event row that admitted it.
    // Validate against the source epoch before mutation; restored rows are then rebased with their pending facts.
    private void ValidateMigrationPulseClaims(JsonElement snapshot)
    {
        var eventSchedules = RuntimeJson.Rows(snapshot, "schedules", MaximumLiveSchedules(Limits))
            .Where(row => row.GetProperty("resume").ValueKind == JsonValueKind.Null)
            .Select(row => (Provider: RuntimeJson.Text(row, "provider"), Id: RuntimeJson.Text(row, "id"))).ToHashSet();
        var claims = RuntimeJson.Rows(snapshot, "pending", Limits.MaxQueuedEvents)
            .Where(row => row.GetProperty("schedule").ValueKind != JsonValueKind.Null
                && row.GetProperty("decision").ValueKind != JsonValueKind.Null
                && eventSchedules.Contains((RuntimeJson.Text(row, "provider"), RuntimeJson.Text(row, "schedule")))).ToArray();
        if (claims.Length == 0) return;
        var rows = RuntimeJson.Rows(snapshot, "history", MaximumEventHistory).ToDictionary(
            row => LedgerKey.Parse(row.GetProperty("key").GetString() ?? ""),
            row => LedgerHash.Parse(RuntimeJson.Text(row, "fingerprint")));
        foreach (var row in claims)
        {
            var value = ReadMigrationEvent(row.GetProperty("event"));
            var key = new LedgerKey(RuntimeJson.Text(row, "provider"), LedgerSpace.Event, value.EventId);
            RuntimeJson.Require(rows.TryGetValue(key, out var fingerprint)
                && fingerprint == LedgerFingerprint(RuntimeJson.StableText(RuntimeJson.From(value))),
                "migration-pulse-ledger", "A decided scheduled occurrence must retain its own ledger row.");
        }
    }

    internal static object SerializeMigrationWork(Work work) => new
    {
        planId = work.Plan.Plan.Id,
        nodeId = work.Entry.NodeId,
        subjects = work.Subjects
    };

    internal static JsonElement SerializeMigrationGateRelease(object payload)
    {
        RuntimeJson.Require(payload is GateReleasePayload, "gate-migration", "Unknown debounce release payload.");
        var captured = (GateReleasePayload)payload;
        return RuntimeJson.From(new
        {
            provider = captured.Origin.Provider,
            @event = captured.Origin.Event,
            work = SerializeMigrationWork(captured.Item)
        });
    }

    /// <summary>A suspended flow's play origin (<see cref="PlayOrigin"/>) as its two numbers: the plays the flow names after
    /// the move are the plays it named before it.</summary>
    internal static long[] SerializeMigrationPlay(PlayOrigin play) => new[] { play.Root, play.Subject };

    internal static PlayOrigin RestoreMigrationPlay(JsonElement row)
    {
        var play = row.GetProperty("play");
        RuntimeJson.Require(play.ValueKind == JsonValueKind.Array && play.GetArrayLength() == 2, "migration-image",
            "A suspended flow carries its play origin.");
        return new PlayOrigin(RuntimeJson.Integer(play[0], long.MinValue, long.MaxValue), RuntimeJson.Integer(play[1], long.MinValue, long.MaxValue));
    }

    internal static object[] SerializeMigrationStoredValues(StoredValueSnapshot[] stored) => stored.Select(snapshot => (object)new
    {
        step = snapshot.Step,
        ports = snapshot.Values.Select((value, port) => new { port, value })
            .Where(row => row.value.ValueKind != JsonValueKind.Undefined).ToArray()
    }).ToArray();

    /// <summary>The entity and handle values a stored row holds, as the JSON identities the rest of the image carries, so
    /// the one world-epoch rebase and the one handle readdress that reach every other identity in the image reach these
    /// too. Only these slots are overwritten when the row is put back; every other value travels as its slot.</summary>
    private static object[] SerializeMigrationRowIdentities(ResultRowSnapshot snapshot, ResultRowDescriptor row)
    {
        var identities = new List<object>();
        for (var index = 0; index < snapshot.Count; index++)
            for (var column = 0; column < row.Columns.Length; column++)
            {
                var kind = column == 0 ? row.TargetKind : row.Columns[column].Kind;
                var slot = row.RowSlot(index) + (column == 0 ? 0 : row.Columns[column].Offset);
                var value = snapshot.Slots[slot];
                if (kind == ValueKind.Entity && value.Kind == ValueKind.String)
                    identities.Add(new
                    {
                        slot,
                        value = new { id = snapshot.Strings[slot], worldEpoch = snapshot.Slots[slot + 1].Integer, lifeEpoch = snapshot.Slots[slot + 2].Integer }
                    });
                else if (kind == ValueKind.Handle && value.Kind == ValueKind.Handle)
                    identities.Add(new
                    {
                        slot,
                        value = new { worldEpoch = value.Handle.WorldEpoch, lifeEpoch = value.Handle.LifeEpoch, local = value.Handle.Local, provider = (int)value.Integer }
                    });
            }
        return identities.ToArray();
    }

    internal static object SerializeMigrationFlow(FlowSnapshot flow, Work work) => new
    {
        storedValues = SerializeMigrationStoredValues(flow.StoredValues),
        actionFrames = flow.ActionFrames.Select(snapshot => new { step = snapshot.Step, frame = snapshot.Frame }).ToArray(),
        // A row region is typed slots, not JSON: its bytes travel as they are, with the text of each string slot
        // beside them because a string slot's offset names the sender's string area.
        resultRows = flow.Rows.Select(snapshot =>
        {
            var raw = (FrameValue[])snapshot.Slots.Clone();
            for (var slot = 0; slot < raw.Length; slot++)
                if (snapshot.Strings[slot] != null) { raw[slot].StringOffset = 0; raw[slot].StringLength = 0; }
            return new
            {
                step = snapshot.Step,
                count = snapshot.Count,
                set = snapshot.Set,
                slots = Convert.ToBase64String(MemoryMarshal.AsBytes<FrameValue>(raw)),
                strings = snapshot.Strings.Select((text, slot) => new { slot, text }).Where(cell => cell.text != null).ToArray(),
                identities = SerializeMigrationRowIdentities(snapshot, work.Entry.Steps[snapshot.Step].Frames.Row!)
            };
        }).ToArray()
    };

    internal static object[] SerializeMigrationWalk(IReadOnlyList<ControlFrame>? walk)
        => walk == null ? Array.Empty<object>() : walk.Select(frame => (object)new
        {
            step = frame.Step,
            output = frame.Output,
            round = frame.Round,
            rounds = frame.Rounds,
            loop = frame.Loop,
            forEach = frame.ForEach,
            positions = frame.Positions,
            items = frame.Items.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : frame.Items,
            group = frame.IsGroup,
            held = frame.Held
        }).ToArray();

    internal static int MigrationStep(Work work, JsonElement value)
        => (int)RuntimeJson.Integer(value, 0, work.Entry.Steps.Count - 1);

    internal static IReadOnlyList<ControlFrame> RestoreMigrationWalk(JsonElement value, Work work, string property = "walk")
    {
        var frames = new List<ControlFrame>();
        foreach (var row in RuntimeJson.Rows(value, property, work.Plan.Plan.Limits.MaxStepsPerEntrypoint))
        {
            RuntimeJson.Shape(row, "step output round rounds loop forEach positions items held", "group");
            var stepIndex = MigrationStep(work, row.GetProperty("step"));
            var step = work.Entry.Steps[stepIndex];
            var rounds = (int)RuntimeJson.Integer(row.GetProperty("rounds"), 0, work.Plan.Plan.Limits.MaxControlIterations);
            var loop = row.GetProperty("loop").GetBoolean();
            var group = row.TryGetProperty("group", out var marked) && marked.ValueKind == JsonValueKind.True;
            var held = row.GetProperty("held").GetBoolean();
            // Only a loop an interval series runs inside is held (ResumeActivation); a held mark on any other frame would
            // end the resumed walk at a frame that is not the series' loop.
            RuntimeJson.Require(!held || (loop && !group), "migration-image", "Only a loop frame can be held in a migrated walk.");
            frames.Add(new ControlFrame
            {
                Descriptor = step,
                Step = stepIndex,
                Output = (int)RuntimeJson.Integer(row.GetProperty("output"), 0, step.Successors.Count - 1),
                Round = (int)RuntimeJson.Integer(row.GetProperty("round"), 0, Math.Max(0, rounds - 1)),
                Rounds = rounds,
                Loop = loop,
                ForEach = row.GetProperty("forEach").GetBoolean(),
                Positions = row.GetProperty("positions").GetBoolean(),
                Items = row.GetProperty("items"),
                // A group frame is put back as a mark only: RestoreWalk names its group from the resumed owner.
                IsGroup = group,
                Detached = true,
                Held = held,
                Successors = step.Successors.Select(successor => successor ?? -1).ToArray()
            });
        }
        return frames;
    }

    internal static FlowSnapshot RestoreMigrationFlow(JsonElement owner, Work work)
    {
        var value = owner.GetProperty("flow");
        RuntimeJson.Shape(value, "storedValues actionFrames resultRows");
        return new FlowSnapshot(RestoreMigrationStoredValues(value, work), RestoreMigrationActionFrames(value, work),
            RestoreMigrationResultRows(value, work));
    }

    private static ResultRowSnapshot[] RestoreMigrationResultRows(JsonElement value, Work work)
    {
        var snapshots = new List<ResultRowSnapshot>();
        var seen = new HashSet<int>();
        foreach (var row in RuntimeJson.Rows(value, "resultRows", work.Plan.Plan.Limits.MaxStepsPerEntrypoint))
        {
            RuntimeJson.Shape(row, "step count set slots strings identities");
            var index = MigrationStep(work, row.GetProperty("step"));
            var descriptor = work.Entry.Steps[index].Frames.Row;
            RuntimeJson.Require(seen.Add(index) && descriptor != null, "migration-schedule",
                "A stored continuation result row must name one unique result-row step.");
            var count = (int)RuntimeJson.Integer(row.GetProperty("count"), 0, descriptor!.Capacity);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(row.GetProperty("slots").GetString() ?? ""); }
            catch (FormatException) { throw new RuntimeContractException("migration-schedule", "A stored result row is not valid slot data."); }
            var width = descriptor.Stride * count;
            RuntimeJson.Require(bytes.Length == width * Marshal.SizeOf<FrameValue>(), "migration-schedule",
                "A stored result row must carry exactly its rows' slots.");
            var slots = new FrameValue[width];
            bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(slots.AsSpan()));
            var strings = new string?[width];
            foreach (var cell in RuntimeJson.Rows(row, "strings", width))
            {
                RuntimeJson.Shape(cell, "slot text");
                var slot = (int)RuntimeJson.Integer(cell.GetProperty("slot"), 0, width - 1);
                RuntimeJson.Require(slots[slot].Kind == ValueKind.String && strings[slot] == null, "migration-schedule",
                    "A stored result row string must name one unique string slot.");
                // A row string is free text bounded by the frame string budget (it may be empty, long or multi-line),
                // not an identity name: the same range normal execution accepts.
                var text = cell.GetProperty("text").GetString();
                RuntimeJson.Require(text != null && text.Length <= Wire.PlanJson.MaxStringValueLength, "migration-schedule",
                    "A stored result row string is outside the frame string budget.");
                strings[slot] = text;
            }
            for (var slot = 0; slot < width; slot++)
            {
                RuntimeJson.Require(Enum.IsDefined(slots[slot].Kind) && (slots[slot].Kind == ValueKind.String) == (strings[slot] != null),
                    "migration-schedule", "A stored result row slot must be a defined kind with its string text.");
            }
            // The entity and handle values were rebased and readdressed with the rest of the image; they replace the
            // slots they were captured from, and only those.
            var identified = new HashSet<int>();
            foreach (var item in RuntimeJson.Rows(row, "identities", width))
            {
                RuntimeJson.Shape(item, "slot value");
                var slot = (int)RuntimeJson.Integer(item.GetProperty("slot"), 0, width - 1);
                var identity = item.GetProperty("value");
                RuntimeJson.Require(identified.Add(slot), "migration-schedule", "A stored result row identity must name one unique slot.");
                if (slots[slot].Kind == ValueKind.Handle)
                {
                    RuntimeJson.Handle(identity);
                    slots[slot] = FrameValue.Of(new FrameHandle((int)RuntimeJson.Integer(identity.GetProperty("worldEpoch"), 0, int.MaxValue),
                        (int)RuntimeJson.Integer(identity.GetProperty("lifeEpoch"), 0, int.MaxValue),
                        (int)RuntimeJson.Integer(identity.GetProperty("local"), 0, int.MaxValue)),
                        (int)RuntimeJson.Integer(identity.GetProperty("provider"), 0, int.MaxValue));
                    continue;
                }
                RuntimeJson.Require(slots[slot].Kind == ValueKind.String && slot + 2 < width, "migration-schedule",
                    "A stored result row identity must name a handle or an entity's slots.");
                var entity = RuntimeJson.EntityRow(identity);
                strings[slot] = entity.Id;
                slots[slot + 1] = FrameValue.Of(entity.WorldEpoch);
                slots[slot + 2] = FrameValue.Of(entity.LifeEpoch);
            }
            snapshots.Add(new ResultRowSnapshot(index, count, row.GetProperty("set").GetBoolean(), slots, strings));
        }
        return snapshots.ToArray();
    }

    private static StoredValueSnapshot[] RestoreMigrationStoredValues(JsonElement value, Work work)
    {
        var snapshots = new List<StoredValueSnapshot>();
        var seen = new HashSet<int>();
        foreach (var row in RuntimeJson.Rows(value, "storedValues", work.Plan.Plan.Limits.MaxStepsPerEntrypoint))
        {
            RuntimeJson.Shape(row, "step ports");
            var index = MigrationStep(work, row.GetProperty("step"));
            var step = work.Entry.Steps[index];
            RuntimeJson.Require(seen.Add(index) && step.StoredOutputs != null,
                "migration-schedule", "A stored continuation value must name one unique stored-value step.");
            var values = new JsonElement[step.StoredOutputs!.Length];
            var ports = RuntimeJson.Rows(step.Contract, "outputs");
            var seenPorts = new HashSet<int>();
            foreach (var cell in RuntimeJson.Rows(row, "ports", values.Length))
            {
                RuntimeJson.Shape(cell, "port value");
                var port = (int)RuntimeJson.Integer(cell.GetProperty("port"), 0, values.Length - 1);
                RuntimeJson.Require(seenPorts.Add(port) && RuntimeJson.Text(ports[port], "type") != "execution",
                    "migration-schedule", "A stored continuation value must name one unique value output.");
                var raw = cell.GetProperty("value");
                PortView.Of(ports[port]).Validate(raw, null);
                values[port] = raw;
            }
            snapshots.Add(new StoredValueSnapshot(index, values, new ResourceRef?[values.Length]));
        }
        return snapshots.ToArray();
    }

    private static ActionFrameSnapshot[] RestoreMigrationActionFrames(JsonElement value, Work work)
    {
        var snapshots = new List<ActionFrameSnapshot>();
        var seen = new HashSet<int>();
        foreach (var row in RuntimeJson.Rows(value, "actionFrames", work.Plan.Plan.Limits.MaxStepsPerEntrypoint))
        {
            RuntimeJson.Shape(row, "step frame");
            var index = MigrationStep(work, row.GetProperty("step"));
            var frame = row.GetProperty("frame");
            RuntimeJson.Require(seen.Add(index) && work.Entry.Steps[index].NodeKind == "action" && frame.ValueKind == JsonValueKind.Object,
                "migration-schedule", "A stored continuation action frame must name one unique action step.");
            snapshots.Add(new ActionFrameSnapshot(index, frame));
        }
        return snapshots.ToArray();
    }

    internal static string? RebaseMigrationCheckpoint(string? json, long sourceEpoch, long targetEpoch)
    {
        if (json == null) return null;
        var checkpoint = RuntimeJson.Parse(json);
        RuntimeJson.Shape(checkpoint, "worldEpoch entries once deferredResults", "gates nativeDomains");
        RuntimeJson.Require(RuntimeJson.Integer(checkpoint.GetProperty("worldEpoch")) == sourceEpoch,
            "migration-checkpoint", "The captured checkpoint belongs to another world.");
        return RebaseWorldEpoch(checkpoint, sourceEpoch, targetEpoch).GetRawText();
    }

    private static RuntimeEvent RebaseEvent(RuntimeEvent value, long sourceEpoch, long targetEpoch)
        => value with
        {
            WorldEpoch = targetEpoch,
            Outputs = RebaseWorldEpoch(value.Outputs, sourceEpoch, targetEpoch)
        };

    internal static RuntimeEvent ReadMigrationEvent(JsonElement value)
    {
        RuntimeJson.Shape(value, "eventId bindingId worldEpoch simulationTick scopeId outputs", "causeId rootEventId causalDepth");
        string? OptionalText(string name)
            => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;
        return new RuntimeEvent(RuntimeJson.Text(value, "eventId"), RuntimeJson.Text(value, "bindingId"),
            RuntimeJson.Integer(value.GetProperty("worldEpoch")), RuntimeJson.Integer(value.GetProperty("simulationTick")),
            RuntimeJson.Text(value, "scopeId"), value.GetProperty("outputs").Clone(), OptionalText("causeId"),
            OptionalText("rootEventId"), value.TryGetProperty("causalDepth", out var depth)
                ? (int)RuntimeJson.Integer(depth, 0, int.MaxValue) : 0);
    }

    /// <summary>Rewrites entity, handle, event and document epoch fields without touching any other number.</summary>
    private static JsonElement RebaseWorldEpoch(JsonElement value, long sourceEpoch, long targetEpoch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteRebased(writer, value, null, sourceEpoch, targetEpoch);
        return RuntimeJson.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteRebased(Utf8JsonWriter writer, JsonElement value, string? property,
        long sourceEpoch, long targetEpoch)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var child in value.EnumerateObject())
                {
                    writer.WritePropertyName(child.Name);
                    WriteRebased(writer, child.Value, child.Name, sourceEpoch, targetEpoch);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in value.EnumerateArray()) WriteRebased(writer, child, property, sourceEpoch, targetEpoch);
                writer.WriteEndArray();
                return;
            case JsonValueKind.Number when property == "worldEpoch" && value.TryGetDouble(out var epoch) && epoch == sourceEpoch:
                writer.WriteNumberValue(targetEpoch);
                return;
            default:
                value.WriteTo(writer);
                return;
        }
    }
}
