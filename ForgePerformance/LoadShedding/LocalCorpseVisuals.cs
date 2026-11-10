using System;
using System.Collections.Generic;
using Enemies;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgePerformance.LoadShedding;

// Native decay also sets m_allowDespawn: its host path calls the replicator.
// Keep that coroutine and all agent lifetimes intact. The corpse renderers have a
// shorter local display lifetime, and once every renderer is hidden the corpse
// colliders go off too, so a body nobody can see no longer stops bullets or shoves.
// They are switched back on with the renderers. No replacement game timer.
internal sealed class LocalCorpseVisuals
{
    // Corpses wait in a deadline-ordered queue; only the earliest deadline is
    // looked at each frame. An expired corpse moves to _hiding until all of its
    // renderers are off, so the per-frame renderer budget still bounds the work.
    private readonly PriorityQueue<Entry, (float Deadline, long Order)> _due = new();
    private long _order;
    private readonly List<Entry> _hiding = new(128);
    private readonly Dictionary<IntPtr, Entry> _states = new();
    private readonly Dictionary<IntPtr, Entry> _enemies = new();
    private readonly float _lifetime;

    internal LocalCorpseVisuals(float lifetime) { _lifetime = lifetime; }
    internal long Seen, Hidden, RendererChanges, Restored, Checks;
    internal float MaximumHideLateness;
    internal const int CheckBudget = 64, RendererBudget = 128;

    internal void BeginLevel()
    {
        Seen = Hidden = RendererChanges = Restored = Checks = 0; MaximumHideLateness = 0;
        _due.Clear(); _hiding.Clear();
    }
    internal void Schedule(ES_Dead state, float originalDelay, float originalDuration)
    {
        if (_states.TryGetValue(state.Pointer, out var existing))
        {
            // Nothing polls the queue before a deadline, so a row whose life
            // ended (native object recycled at the same address) is dropped here.
            if (!Stale(existing)) return;
            Remove(existing);
        }
        var enemy = state.m_enemyAgent;
        if (enemy == null || enemy.Alive) return;
        var lifetime = SheddingPolicy.CorpseLifetime(originalDelay, originalDuration, _lifetime);
        var renderers = enemy.GetComponentsInChildren<Renderer>(true);
        var parts = new Part[renderers.Length];
        for (var i = 0; i < parts.Length; i++) parts[i] = new(renderers[i], renderers[i].enabled);
        var colliders = enemy.GetComponentsInChildren<Collider>(true);
        var solids = new Solid[colliders.Length];
        for (var i = 0; i < solids.Length; i++) solids[i] = new(colliders[i], colliders[i].enabled);
        var deadline = Time.time + lifetime;
        var entry = new Entry(state, enemy, parts, solids, deadline);
        _due.Enqueue(entry, (deadline, _order++)); _states.Add(state.Pointer, entry); _enemies[enemy.Pointer] = entry; Seen++;
    }
    internal void Tick()
    {
        var now = Time.time;
        while (_due.TryPeek(out var top, out var due) && due.Deadline <= now)
        {
            _due.Dequeue();
            if (!top.Removed) _hiding.Add(top);
        }
        if (_hiding.Count == 0) return;
        var checks = CheckBudget;
        var rendererVisits = RendererBudget;
        var kept = 0;
        for (var i = 0; i < _hiding.Count; i++)
        {
            var entry = _hiding[i];
            if (entry.Removed) continue;
            if (checks == 0 || rendererVisits == 0) { _hiding[kept++] = entry; continue; }
            checks--; Checks++;
            if (Stale(entry)) { Remove(entry); continue; }
            while (entry.NextPart < entry.Parts.Length && rendererVisits > 0)
            {
                ref var part = ref entry.Parts[entry.NextPart++]; rendererVisits--;
                if (part.Renderer == null || !part.OriginallyEnabled || !part.Renderer.enabled) continue;
                part.Renderer.enabled = false; part.OurHidden = true; RendererChanges++;
                // The culler's shadow list draws disabled renderers; only Off leaves it (LoadSheddingController.RefreshShadow).
                if (part.Renderer.shadowCastingMode != ShadowCastingMode.Off) { part.Shadow = part.Renderer.shadowCastingMode; part.OurShadowOff = true; part.Renderer.shadowCastingMode = ShadowCastingMode.Off; }
                if (!entry.Changed) { entry.Changed = true; Hidden++; }
            }
            if (entry.NextPart == entry.Parts.Length)
            {
                if (entry.Changed)
                {
                    MaximumHideLateness = Math.Max(MaximumHideLateness, now - entry.Deadline);
                    for (var s = 0; s < entry.Solids.Length; s++)
                    {
                        ref var solid = ref entry.Solids[s];
                        if (solid.Collider == null || !solid.OriginallyEnabled || !solid.Collider.enabled) continue;
                        solid.Collider.enabled = false; solid.OurOff = true;
                    }
                    LoadSheddingController.RefreshShadow(entry.Enemy);
                }
                continue;
            }
            _hiding[kept++] = entry;
        }
        _hiding.RemoveRange(kept, _hiding.Count - kept);
    }
    private static bool Stale(Entry entry)
        => entry.Enemy == null || entry.Enemy.m_isBeingDestroyed || entry.Enemy.Alive || entry.State.m_didHide;
    internal void NativeHidden(ES_Dead state)
    {
        if (_states.TryGetValue(state.Pointer, out var entry)) Remove(entry);
    }
    internal void BeforeEnemySetup(EnemyAgent enemy)
    {
        if (!_enemies.TryGetValue(enemy.Pointer, out var entry)) return;
        // A pooled enemy must not inherit our presentation flags in its new life.
        Restore(entry); Remove(entry);
    }
    internal void EndLevel()
    {
        Exception? first = null;
        foreach (var entry in _states.Values)
            try { if (entry.Enemy != null && !entry.State.m_didHide) Restore(entry); }
            catch (Exception error) { first ??= error; }
        _due.Clear(); _hiding.Clear(); _states.Clear(); _enemies.Clear();
        if (first != null) throw first;
    }
    private void Restore(Entry entry)
    {
        var changed = false;
        for (var i = 0; i < entry.Solids.Length; i++)
        {
            ref var solid = ref entry.Solids[i];
            if (!solid.OurOff || solid.Collider == null) continue;
            solid.Collider.enabled = solid.OriginallyEnabled; solid.OurOff = false;
        }
        for (var i = 0; i < entry.Parts.Length; i++)
        {
            ref var part = ref entry.Parts[i];
            if (!part.OurHidden || part.Renderer == null) continue;
            part.Renderer.enabled = part.OriginallyEnabled; part.OurHidden = false; changed = true;
            if (part.OurShadowOff && part.Renderer.shadowCastingMode == ShadowCastingMode.Off) part.Renderer.shadowCastingMode = part.Shadow;
            part.OurShadowOff = false;
        }
        if (changed) { Restored++; LoadSheddingController.RefreshShadow(entry.Enemy); }
    }
    // Queue and _hiding drop a removed row lazily when they next reach it.
    private void Remove(Entry entry)
    {
        entry.Removed = true;
        _states.Remove(entry.State.Pointer); _enemies.Remove(entry.Enemy.Pointer);
    }
    private struct Part
    {
        internal readonly Renderer Renderer;
        internal readonly bool OriginallyEnabled;
        internal bool OurHidden;
        internal ShadowCastingMode Shadow;
        internal bool OurShadowOff;
        internal Part(Renderer renderer, bool enabled) { Renderer = renderer; OriginallyEnabled = enabled; OurHidden = false; Shadow = ShadowCastingMode.Off; OurShadowOff = false; }
    }
    private struct Solid
    {
        internal readonly Collider Collider;
        internal readonly bool OriginallyEnabled;
        internal bool OurOff;
        internal Solid(Collider collider, bool enabled) { Collider = collider; OriginallyEnabled = enabled; OurOff = false; }
    }
    private sealed class Entry
    {
        internal readonly ES_Dead State;
        internal readonly EnemyAgent Enemy;
        internal readonly Part[] Parts;
        internal readonly Solid[] Solids;
        internal readonly float Deadline;
        internal int NextPart;
        internal bool Changed, Removed;
        internal Entry(ES_Dead state, EnemyAgent enemy, Part[] parts, Solid[] solids, float deadline)
        { State = state; Enemy = enemy; Parts = parts; Solids = solids; Deadline = deadline; }
    }
}
public sealed class LoadSheddingWatch : MonoBehaviour
{
    public LoadSheddingWatch(IntPtr pointer) : base(pointer) { }
    public void Update() => LoadSheddingController.Current?.Tick();
    public void OnDestroy() { if (!Plugin.Quitting) LoadSheddingController.Current?.EndLevel(); }
}
