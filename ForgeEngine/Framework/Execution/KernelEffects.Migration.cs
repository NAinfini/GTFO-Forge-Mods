using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

internal sealed partial class KernelEffects
{
    /// <summary>This component's parts of a host-migration image: the provider indices handles carry, the live handle
    /// slots, the live effects with their layers, and the two counters new ones are numbered from. A handle whose
    /// schedule or wait the flow does not carry is left out with it; <paramref name="ownerLeases"/> are the owner
    /// proxies, the one kind of cancellation the kernel rebuilds from data.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image, MigrationSurvivors survivors, IReadOnlyCollection<OwnerLease> ownerLeases)
    {
        image["providers"] = providerIndex.OrderBy(entry => entry.Value)
            .Select(entry => new { provider = entry.Key, index = entry.Value }).ToArray();
        image["handles"] = handleSlots.Select((slot, index) => (slot, index))
            .Where(entry => entry.slot is { Active: true } slot && !survivors.OmittedWaitHandles.Contains(entry.index)
                && (slot.ScheduleId == null || survivors.ScheduleKeys.Contains((slot.ProviderId, slot.ScheduleId))
                    || survivors.WaitHandles.Contains(entry.index)))
            .Select(entry => new
            {
                slot = entry.index,
                kind = entry.slot!.Kind,
                lifetime = entry.slot.Lifetime,
                provider = entry.slot.ProviderId,
                generation = entry.slot.Generation,
                scheduleId = entry.slot.ScheduleId,
                effectId = entry.slot.EffectId,
                statusKind = entry.slot.StatusKind,
                native = entry.slot.Native != null,
                liveness = entry.slot.NativeIsLive != null,
                cancel = entry.slot.Cancel != null,
                portable = entry.slot.Native == null && entry.slot.NativeIsLive == null && (entry.slot.Cancel == null
                    || ownerLeases.Any(lease => lease.Local.GetProperty("local").GetInt32() == entry.index
                        && lease.Local.GetProperty("lifeEpoch").GetInt32() == entry.slot.Generation))
            }).ToArray();
        image["effects"] = effects.Values.OrderBy(slot => slot.Id).Select(slot => new
        {
            id = slot.Id,
            provider = slot.ProviderId,
            binding = slot.BindingId,
            capability = slot.CapabilityId,
            plan = slot.PlanId,
            resource = slot.ResourceId,
            revision = slot.ResourceRevision,
            entry = slot.EntryId,
            node = slot.NodeId,
            statusKind = slot.StatusKind,
            removal = slot.Removal,
            subject = slot.Subject,
            layers = slot.Layers.Select(layer => new
            {
                sourceProvider = layer.SourceProvider,
                scopeId = layer.ScopeId,
                origin = layer.Origin,
                inputs = layer.Inputs,
                parameters = layer.Parameters,
                commandPrefix = layer.CommandPrefix,
                commandId = layer.CommandId,
                ownerApplication = layer.OwnerApplication,
                remainingTicks = Math.Max(0, layer.ExpiresAtTick - world.Tick),
                nextPulseTicks = layer.NextPulseTick < 0 ? -1 : Math.Max(0, layer.NextPulseTick - world.Tick)
            }).ToArray(),
            peak = slot.Peak,
            endsOn = slot.Options.EndTriggers.Select(ending => ending.BindingId).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            origin = slot.Origin,
            inputs = slot.Inputs,
            parameters = slot.Parameters,
            commandPrefix = slot.CommandPrefix,
            commandId = slot.CommandId,
            handle = slot.Handle,
            handleSlot = slot.HandleSlot,
            pulses = slot.Pulses
        }).ToArray();
        image["effectSequence"] = effectSequence;
        image["handleGeneration"] = handleGeneration;
    }

    /// <summary>Reads the provider indices, the handle slots and the two counters of an image into this staged
    /// component. A slot comes back as data only: its native object, liveness and cancellation are rebound by the
    /// provider handover. The effect rows are decoded by the facade, because an effect's inputs and liveness cells bind
    /// to the kernel that will pulse it, and added through <see cref="AdoptMigrationEffect"/>.</summary>
    internal void RestoreMigration(JsonElement snapshot)
    {
        providerIndex.Clear();
        foreach (var entry in RuntimeJson.Rows(snapshot, "providers", 2048))
        {
            RuntimeJson.Shape(entry, "provider index");
            providerIndex.Add(RuntimeJson.Text(entry, "provider"),
                (int)RuntimeJson.Integer(entry.GetProperty("index"), 0, 2047));
        }
        handleGeneration = (int)RuntimeJson.Integer(snapshot.GetProperty("handleGeneration"), 0, int.MaxValue - 1);
        foreach (var entry in RuntimeJson.Rows(snapshot, "handles", limits.MaxLiveHandles))
        {
            RuntimeJson.Shape(entry, "slot kind lifetime provider generation scheduleId effectId statusKind native liveness cancel portable");
            var address = (int)RuntimeJson.Integer(entry.GetProperty("slot"), 0, limits.MaxLiveHandles - 1);
            while (handleSlots.Count <= address) handleSlots.Add(null);
            var schedule = entry.GetProperty("scheduleId");
            handleSlots[address] = new HandleSlot
            {
                Kind = RuntimeJson.Text(entry, "kind"),
                Lifetime = RuntimeJson.Text(entry, "lifetime"),
                ProviderId = RuntimeJson.Text(entry, "provider"),
                Generation = (int)RuntimeJson.Integer(entry.GetProperty("generation"), 1, int.MaxValue - 1),
                Active = true,
                ScheduleId = schedule.ValueKind == JsonValueKind.Null ? null : RuntimeJson.Text(entry, "scheduleId"),
                EffectId = (int)RuntimeJson.Integer(entry.GetProperty("effectId"), -1, int.MaxValue - 1),
                StatusKind = entry.GetProperty("statusKind").GetString() ?? ""
            };
        }
        effectSequence = (int)RuntimeJson.Integer(snapshot.GetProperty("effectSequence"), 0, int.MaxValue - 1);
    }

    /// <summary>Takes one effect the facade decoded from an image into this staged component's tables.</summary>
    internal void AdoptMigrationEffect(EffectSlot slot)
    {
        effects.Add(slot.Id, slot);
        effectIdentity.Add(slot.Identity, slot.Id);
        IndexEffectEndings(slot);
    }
}
