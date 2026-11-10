using System;
using System.Collections.Generic;
using Enemies;
using ForgeCore;
using ForgeUI;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// The enemies' outlines while a replay plays (PLAN 5.4): the puppets the library's own selection picks (<see cref="EnemyOutlineTracker"/>:
/// within 100 m, in front of the camera, the nearest first, at most the library's limit), coloured by how alert the recording says each
/// was (asleep, awake, hunting) and drawn only where the camera sees them. ForgeVisual's automatic enemy outline reads the game's AI mode,
/// which is off on a puppet, so the selected puppets are outlined one by one (<c>Outlines.Add</c>) instead; an outline is added again
/// only when its enemy's recorded state or puppet changes. The recorded alert is settled against the recorded locomotion (ForgeCore
/// <c>EnemyAlerts.Settle</c>): a client's recording kept a woken enemy asleep for seconds or for good. The enemy the camera watches
/// (<see cref="ReplayCamera.Enemy"/>) is always outlined, in the menu's highlight colour.
/// </summary>
internal static class EnemyMarks
{
    private const float Every = 0.2f;
    // Every puppet is a candidate: puppets exist within 70 m of the followed player (Puppeteer.SpawnRadius), and the library's defaults (35 m,
    // the nearest 24) left whole rooms of sleepers unmarked (11-06).
    private static readonly EnemyOutlineStyle Style = new() { Range = 100f, FadeDistance = 10f, MaxEnemies = EnemyOutlineStyle.MaxEnemiesLimit };
    // The tracker's slots are the recording's entities; a puppet's instance id stands for the slot's generation, so a new puppet of the
    // same entity is a new enemy to the selection.
    private static readonly EnemyOutlineTracker Tracker = new();
    private static readonly Dictionary<int, EnemyAgent> Agents = new();
    private static readonly Dictionary<int, (IntPtr Agent, EnemyAlert Alert, bool Watched, OutlineHandle? Handle)> Shown = new();
    private static readonly HashSet<int> Keep = new();
    private static readonly List<int> Drop = new();
    private static float _next;

    internal static bool On { get; set; }

    /// <summary>How many enemies are outlined now.</summary>
    internal static int Count => Shown.Count;

    internal static void Tick(WorldState state, Vector3 camera, Vector3 forward)
    {
        if (!On) { Clear(); return; }
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _next) return;
        _next = now + Every;
        Agents.Clear();
        foreach (var (entity, agent) in Puppeteer.Visible())
        {
            if (agent == null || agent.WasCollected) continue;
            var p = agent.transform.position;
            Tracker.Set(entity, agent.GetInstanceID(), p.x, p.y, p.z, EnemyAlerts.Settle((EnemyAlert)(state.Poses[entity].State & 3), state.EnemyLocomotion[entity]));
            Agents[entity] = agent;
        }
        for (var slot = 0; slot < Tracker.Capacity; slot++)
            if (Tracker.Used(slot) && !Agents.ContainsKey(slot)) Tracker.Release(slot);
        Tracker.Select(Style, camera.x, camera.y, camera.z, forward.x, forward.y, forward.z);
        Keep.Clear();
        for (var i = 0; i < Tracker.SelectedCount; i++) Mark(Tracker.SelectedSlot(i), Tracker.SelectedState(i));
        var watched = ReplayCamera.Enemy;
        if (watched >= 0 && !Keep.Contains(watched) && Agents.ContainsKey(watched)) Mark(watched, EnemyAlert.Hunting);
        Drop.Clear();
        foreach (var entity in Shown.Keys) if (!Keep.Contains(entity)) Drop.Add(entity);
        foreach (var entity in Drop) { Shown[entity].Handle?.Remove(); Shown.Remove(entity); }
    }

    private static void Mark(int entity, EnemyAlert alert)
    {
        var agent = Agents[entity];
        var watched = entity == ReplayCamera.Enemy;
        Keep.Add(entity);
        if (Shown.TryGetValue(entity, out var was) && was.Agent == agent.Pointer && was.Alert == alert && was.Watched == watched && was.Handle is { Active: true }) return;
        was.Handle?.Remove();
        var colour = watched ? new OutlineColor(UIKitColors.MenuOver.r, UIKitColors.MenuOver.g, UIKitColors.MenuOver.b, 1f) : Colour(alert);
        Shown[entity] = (agent.Pointer, alert, watched, Outlines.Add(Plugin.Id, agent, colour));
    }

    // The game's colours at the library style's opacities: asleep in the HUD text colour, awake in its warning, hunting in its red.
    private static OutlineColor Colour(EnemyAlert alert)
    {
        var colour = alert == EnemyAlert.Hunting ? UIKitColors.Alert : alert == EnemyAlert.Awake ? UIKitColors.Warn : UIKitColors.GameText;
        return new OutlineColor(colour.r, colour.g, colour.b, Style.ColorOf(alert).A);
    }

    internal static void Clear()
    {
        if (Shown.Count == 0) return;
        foreach (var shown in Shown.Values) shown.Handle?.Remove();
        Shown.Clear();
        Tracker.ClearSelection();
        _next = 0f;
    }
}
