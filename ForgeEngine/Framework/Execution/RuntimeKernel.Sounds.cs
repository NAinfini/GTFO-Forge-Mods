using System;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    private const string SoundEnvelope = "$sound";
    internal sealed record SoundInstance(JsonElement Handle, string Source, PresentationOutput Request)
    {
        /// <summary>The handle as the value it is, read once where the instance is recorded.</summary>
        internal RuntimeHandle Value { get; } = RuntimeHandle.TryRead(Handle, out var value) ? value : default;
    }
    internal sealed record LocalSound(string Plan, string Scope, string Source, Action Stop);

    private static bool StartsSound(ResolvedStep step) => step.ProducesSound;
    private static bool StopsSound(ResolvedStep step) => step.StopsSoundInstance;

    private static JsonElement WithSound(JsonElement inputs, string id, string scope, string source, bool stop)
    {
        var fields = inputs.EnumerateObject().Where(field => field.Name != SoundEnvelope)
            .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        fields[SoundEnvelope] = RuntimeJson.From(new { id, scope, source, stop });
        return RuntimeJson.From(fields);
    }

    private static CommandResult SoundStopped(bool stopped) => CommandResult.Create(CommandStatuses.Partial,
        CommitStates.None, ResultCode.Partial, stopped ? "Sound instance stopped." : "Sound instance is stopped or invalid.",
        RuntimeJson.From(new { stopped }));

    private bool TryStopSound(ResolvedStep step, JsonElement inputs, out CommandResult result)
    {
        result = null!;
        if (!StopsSound(step) || !inputs.TryGetProperty("instance", out var handle)) return false;
        if (inputs.TryGetProperty("sound", out _) || inputs.TryGetProperty("filter", out _))
        {
            result = CommandResult.Rejected("precondition-failed", "Choose a sound instance or a sound event, not both.");
            return true;
        }
        int address; RuntimeHandle stopped;
        try
        {
            stopped = ReadHandle(handle);
            kernelEffects.CheckHandle(stopped, step.SoundInputPort, step.NodeId, out address);
        }
        catch (RuntimeContractException) { result = SoundStopped(false); return true; }
        if (!kernelEffects.sounds.TryGetValue(address, out var sound) || stopped.Provider != sound.Value.Provider)
        { result = SoundStopped(false); return true; }
        kernelEffects.sounds.Remove(address);
        kernelEffects.ReleaseHandle(address);
        QueueSoundStop(sound.Request);
        result = SoundStopped(true);
        return true;
    }

    private void QueueSoundStop(PresentationOutput request)
    {
        var meta = request.Inputs.GetProperty(SoundEnvelope);
        var id = RuntimeJson.Text(meta, "id");
        var inputs = WithSound(RuntimeJson.EmptyObject, id, RuntimeJson.Text(meta, "scope"), RuntimeJson.Text(meta, "source"), true);
        var stop = request with
        {
            Inputs = inputs,
            CommandId = request.CommandId + ".stop",
            SimulationTick = Math.Max(0, CurrentTick),
            IsCleanup = true,
            RequestSequence = checked(++eventQueue.sequence)
        };
        if (advancing) kernelScheduler.presentationOutputs.Add(stop);
        else kernelEffects.pendingSoundStops.Add(stop);
        kernelEffects.StopLocalSound(id);
    }

    private void ClearSounds(string? plan = null, string? source = null, string? scope = null, bool broadcast = true)
    {
        foreach (var pair in kernelEffects.sounds.ToArray())
        {
            var sound = pair.Value;
            if (plan != null && sound.Request.PlanId != plan || source != null && sound.Source != source
                || scope != null && sound.Request.ScopeId != scope) continue;
            kernelEffects.sounds.Remove(pair.Key);
            kernelEffects.ReleaseHandle(pair.Key);
            if (broadcast)
            {
                try { QueueSoundStop(sound.Request); }
                catch (Exception error) { log.LogCallbackFailed("sound-stop-queue-failed handle=" + pair.Key, error); } // the stop is queued and the handle released; teardown continues
            }
        }
        foreach (var pair in kernelEffects.localSounds.ToArray())
        {
            var sound = pair.Value;
            if (plan != null && sound.Plan != plan || source != null && sound.Source != source
                || scope != null && sound.Scope != scope) continue;
            try { kernelEffects.StopLocalSound(pair.Key); }
            catch (Exception error) { log.LogCallbackFailed("sound-local-stop-failed handle=" + pair.Key, error); } // teardown continues through the other local instances
        }
        if (plan == null && source == null && scope == null)
        {
            kernelEffects.soundHistory.Clear();
            if (!broadcast) kernelEffects.pendingSoundStops.Clear();
        }
    }

    /// <summary>A detached machine cannot keep presentation from the session it left.</summary>
    internal void ClearSessionSounds()
    {
        ReadThread();
        ClearSounds(broadcast: false);
    }
}
