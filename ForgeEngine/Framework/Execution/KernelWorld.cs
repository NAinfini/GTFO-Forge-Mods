using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

namespace ForgeEngine.Framework;

/// <summary>The kernel's clock as plain data: the tick being simulated and the world it belongs to. The facade
/// writes it; every component below reads it through the one object it was constructed with.</summary>
internal sealed class KernelWorld
{
    internal long WorldEpoch;
    internal long Tick = -1;

    internal readonly int threadId = Environment.CurrentManagedThreadId;

    internal HashSet<(string Provider, string Scope)> cancelled = new();

    /// <summary>Every registration or lifecycle change that can invalidate a decision already taken: a module
    /// registering or unregistering, a plan loading or unloading, a world beginning. Nothing derived from those
    /// tables — a schedule's captured validation, a queued dispatch's plan grouping — is recomputed while this
    /// number stands still, and everything derived from them that survives across frames records the generation it
    /// was derived at.</summary>
    internal long lifecycleGeneration;

    internal bool worldStarted;

    internal bool? worldHost;

    /// <summary>The rate the host reported for the running world, in ticks per second, or null while no world has
    /// reported one. It is the authority every tick-to-second conversion reads (contract §3.2 时间单位): the host
    /// reads <c>1/Time.fixedDeltaTime</c> once at the world's start and this is where that answer lives, so no
    /// provider has to carry a rate of its own and a world that never reported one has no rate to fall back to.</summary>
    internal double? tickRate;

    internal bool worldCountRestarts;

    /// <summary>Where the runtime stands between registering and ready; the facade's lifecycle writes it.</summary>
    internal RuntimeStartupState StartupState = RuntimeStartupState.Registering;

    /// <summary>This component's parts of a host-migration image: the world the image is of, the tick it was taken at
    /// and the cancelled scopes.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image)
    {
        image["worldEpoch"] = WorldEpoch;
        image["capturedTick"] = Tick;
        image["cancelled"] = cancelled.OrderBy(entry => entry.Provider, StringComparer.Ordinal)
            .ThenBy(entry => entry.Scope, StringComparer.Ordinal)
            .Select(entry => new { provider = entry.Provider, scope = entry.Scope }).ToArray();
    }

    /// <summary>Reads the cancelled scopes of an image into this staged component; the facade gave it the new epoch
    /// and the captured tick when it made the image.</summary>
    internal void RestoreMigration(JsonElement snapshot)
    {
        foreach (var entry in RuntimeJson.Rows(snapshot, "cancelled", RuntimeKernel.MaximumEventHistory))
        {
            RuntimeJson.Shape(entry, "provider scope");
            cancelled.Add((RuntimeJson.Text(entry, "provider"), RuntimeJson.Text(entry, "scope")));
        }
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelWorld other)
    {
        (cancelled, other.cancelled) = (other.cancelled, cancelled);
        (worldHost, other.worldHost) = (other.worldHost, worldHost);
        (lifecycleGeneration, other.lifecycleGeneration) = (other.lifecycleGeneration, lifecycleGeneration);
        (WorldEpoch, other.WorldEpoch) = (other.WorldEpoch, WorldEpoch);
        (Tick, other.Tick) = (other.Tick, Tick);
    }
}
