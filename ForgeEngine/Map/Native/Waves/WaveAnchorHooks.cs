using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The one entry every wave of a level passes through: the master's own survival-wave start, at its
/// position-taking overload. Every vanilla caller of the node-taking overload is forwarded to this one
/// unconditionally, and the forwarded call is a direct native call to this method's address — so a prefix here
/// sees every alarm, objective, reactor, terminal, event and behaviour-graph wave, and nothing has to be patched
/// beside it. The node-taking overload itself is not patched: a wave started through it reaches this one anyway,
/// and patching both would resolve one wave twice.
///
/// The hook only rewrites where a wave spawns, and only for the settings ids this install authored an anchor for:
/// the settings id is the cloned wave settings block the website pointed the wave at, and the anchor table of the
/// world being played is what turns it into a spawn type, a course node and — for a point anchor — a world
/// position. A named anchor instead selects the native OnSpawnPoints branch and supplies its exact world-event
/// object filter. Every other argument of the entry, including the population, the delay and the area distance,
/// is left exactly as the caller passed it.
///
/// Three outcomes are told apart. An id no anchor of this level claims leaves the call untouched, which is how a
/// wave the author did not position runs its own data blocks. An id whose anchor cannot be resolved is refused
/// outright: the result is false, the event id is cleared and the original method does not run, because a wave
/// that spawned at the vanilla position instead would be a wave the author did not write. Only the host resolves
/// at all — the master's own entry is master-side, and a client that rewrote its arguments would be describing a
/// world it does not own.
///

/// ForgeCore patches the entry once (`GameHooks.SurvivalWaveStarted`); <see cref="MapGameHooks"/> subscribes the rewrite
/// below to its `Before`, which runs ahead of every other patch of the method.
/// </summary>
internal static class SurvivalWaveAnchorRewrite
{
    /// <summary>Subscribed to ForgeCore's <c>GameHooks.SurvivalWaveStarted</c> (its <c>Before</c>, which runs ahead of every other patch of the entry).</summary>
    internal static void Rewrite(ForgeCore.SurvivalWaveStart call)
    {
        WaveHostFrame.Caller = null;
        if (!Session.IsHost) return;
        // The caller's own frame, before any anchor rewrite: the host reference a later Forge start reuses.
        WaveHostFrame.Caller = new WaveHostFrame(call.RefNode, call.SpawnType, call.AreaDistance, call.Position, call.CustomSpawnDirection,
            call.SpawnDirection, call.WorldEventFilter ?? "", call.SettingsId, call.PopulationDataId);
        if (WaveAnchorSession.Current is { } anchors)
        {
            if (anchors.TryResolve(call.SettingsId, out var target, out var anchorRefusal))
            {
                if (target!.RefNode is { } anchoredNode) call.RefNode = anchoredNode;
                call.SpawnType = target.SpawnType;
                if (target.Positioned) call.Position = target.Position;
                if (target.WorldEventFilter is { } filter) call.WorldEventFilter = filter;
            }
            else if (anchorRefusal != null)
            {
                WaveHostFrame.Caller = null;
                call.Refuse();
                return;
            }
        }
        var effective = WaveSpawnValidation.EffectiveType(call.SettingsId, call.SpawnType);
        if (WaveSpawnValidation.Refusal(call.RefNode, effective) is { } refusal)
        {
            WaveSpawnValidation.Record(refusal, call.RefNode, call.SettingsId, call.PopulationDataId, effective);
            WaveHostFrame.Caller = null;
            call.Refuse();
        }
    }
}
