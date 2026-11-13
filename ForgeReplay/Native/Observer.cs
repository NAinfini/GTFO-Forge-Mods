using System;
using System.Collections.Generic;
using Agents;
using ForgeCore;
using Player;

namespace ForgeReplay;

/// <summary>The local player while a recording plays: an observer the game cannot hurt and nothing can sense. No damage and no infection
/// reaches anyone (the game's own god mode for the local player, and every hit and infection write refused through ForgeCore
/// <see cref="PlayerHits"/>, which covers the bot puppets too); an enemy that is not a puppet is switched off as soon as it exists, so
/// nothing the game spawns for itself (waves, alarms, wake-ups) ever hunts the observer. Main thread only.</summary>
internal static class Observer
{
    private static bool _on;
    private static readonly HashSet<IntPtr> Puppets = new();

    // The game warps the local player's picture (its damage feedback): the observer is nobody, and the world and the outlines over it stay where they are.
    private static PE_FPSDamageFeedback? _warp;

    // The game decides for itself that the expedition is won (an objective with nothing left to solve, items the replay set): that ends the level
    // in the result screen in the middle of a replay. While the observer is on, the check is skipped (ForgeCore patches the method once).
    private static bool _subscribed;

    internal static void Enter()
    {
        _on = true;
        if (!_subscribed)
        {
            _subscribed = true;
            try { GameHooks.ExpeditionSuccessCheck.Skip(static _ => _on); }
            catch (Exception error) { Plugin.Logger.Error("The replay cannot keep the expedition from ending: " + error); }
            // The observer is the host, and on the host a door's state handler starts the door's puzzle for real: a real alarm, its waves and music, which
            // no jump can stop. What the recording shows goes through the state handlers, as on a client, which is all a client ever has.
            try { GameHooks.ChainedPuzzleInteract.Skip(static _ => _on); }
            catch (Exception error) { Plugin.Logger.Error("The replay cannot keep the level's alarms from starting: " + error); }
            try { GameHooks.SurvivalWaveStarted.Before(static wave => { if (_on) wave.Refuse(); }); }
            catch (Exception error) { Plugin.Logger.Error("The replay cannot keep the level's waves from starting: " + error); }
        }
        PlayerHits.Redirect(Plugin.Id, (_, _, _) => 0f);
        PlayerHits.GateInfection(Plugin.Id, (_, _) => false);
        try { PlayerManager.WantToSetGodMode(true, true); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The observer could not be made untouchable: " + error.Message); }
    }

    internal static void Exit()
    {
        if (!_on) return;
        _on = false;
        if (_warp != null && !_warp.WasCollected) _warp.enabled = true;
        _warp = null;
        PlayerHits.Redirect(Plugin.Id, null);
        PlayerHits.GateInfection(Plugin.Id, null);
        try { PlayerManager.WantToSetGodMode(false, false); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The observer's protection could not be lifted: " + error.Message); }
    }

    /// <summary>Takes every living enemy that is not one of the replay's puppets out of the level (the game's own despawn; switched off where
    /// it has no replicator): the level's own spawning runs during a replay, so this is the ordinary case. A switched-off enemy stays in the
    /// culling system's nodes and lights at its place, where its shadow could still be drawn (11-06: enemy shadows where no enemy was).</summary>
    internal static void Sweep()
    {
        if (!_on) return;
        var warp = Players.Local?.FPSCamera?.m_fpsDamageFeedback;
        if (warp != null && warp.enabled) { warp.enabled = false; _warp = warp; }
        Puppets.Clear();
        Puppeteer.CollectEnemies(Puppets);
        foreach (var enemy in ForgeCore.Enemies.Live)
        {
            if (enemy == null || enemy.WasCollected || !enemy.Alive || Puppets.Contains(enemy.Pointer) || enemy.AI is not { } ai || ai == null || ai.Mode == AgentMode.Off) continue;
            try
            {
                if (enemy.Sync?.Replicator is { } replicator) replicator.Despawn();
                else enemy.Disable();
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A game-spawned enemy could not be taken out of the level: " + error.Message); }
        }
    }
}
