using System;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private sealed partial class Run
    {
        internal GateKeyItem[] BulkheadKeyPool = Array.Empty<GateKeyItem>();
        internal LG_Floor? BulkheadKeyFloor;
    }

    // RegisterBulkheadKey (0x18881B0) ignores keys until a bulkhead progression objective exists.
    // Distribution runs once per layer: a main-layer key can precede the optional layer's objective.
    // Retain the floor's original draw order until every layer has finished batch 16, then register
    // those same, actually consumed keys through the native entry before function builders use them.
    private static void CaptureBulkheadKeyPool(Run run, LG_Floor floor)
    {
        var pool = floor.m_availableBulkheadKeys
            ?? throw new GenerationRefusal("gen-stage-invalid", run.Level + "/bulkhead-key-pool");
        var keys = new GateKeyItem[pool.Count];
        for (int index = 0; index < keys.Length; index++) keys[index] = pool[index];
        run.BulkheadKeyPool = keys;
        run.BulkheadKeyFloor = floor;
    }

    private static void CompleteBulkheadKeys(Run run)
    {
        var floor = run.BulkheadKeyFloor
            ?? throw new GenerationRefusal("gen-stage-invalid", run.Level + "/bulkhead-key-floor");
        CompleteBulkheadKeyRegistration(floor, run.BulkheadKeyPool);
        run.BulkheadKeyPool = Array.Empty<GateKeyItem>();
        run.BulkheadKeyFloor = null;
    }

    internal static void CompleteBulkheadKeyRegistration(LG_Floor floor, IReadOnlyList<GateKeyItem> originalPool)
    {
        var available = floor.m_availableBulkheadKeys
            ?? throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/pool");
        var consumed = new List<GateKeyItem>();
        var draws = new HashSet<IntPtr>();
        foreach (var key in originalPool)
        {
            if (key == null || key.WasCollected)
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/stale-key");
            if (!draws.Add(key.Pointer))
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/duplicate-draw");
            if (available.Contains(key)) continue;
            if (key.SpawnZone == null || key.SpawnZone.WasCollected)
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/spawn-zone");
            consumed.Add(key);
        }
        if (consumed.Count == 0 || !ProgressionObjective_BulkheadKey.HasAnyBulkheadProgressionObjectives) return;
        var registered = ProgressionObjective_BulkheadKey.m_allBulkheadKeysInLevel
            ?? throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/registry");
        // The pool is not a second registry. All existing entries must still be one of these native draws.
        var identities = new HashSet<IntPtr>();
        foreach (var key in registered)
            if (key == null || key.WasCollected || !consumed.Exists(candidate => candidate.Pointer == key.Pointer))
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/foreign-key");
            else if (!identities.Add(key.Pointer))
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/duplicate-registration");
        // Register only a missed draw. Keep the live native registry and its existing entries;
        // move each owned key to its draw position instead of clearing and replaying the table.
        for (int index = 0; index < consumed.Count; index++)
        {
            var key = consumed[index];
            int position = -1;
            for (int candidate = index; candidate < registered.Count; candidate++)
                if (registered[candidate].Pointer == key.Pointer) { position = candidate; break; }
            if (position < 0)
            {
                position = registered.Count;
                ProgressionObjective_BulkheadKey.RegisterBulkheadKey(key);
                if (registered.Count != position + 1 || registered[position]?.Pointer != key.Pointer)
                    throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/registration-readback");
            }
            if (position == index) continue;
            registered.RemoveAt(position);
            registered.Insert(index, key);
        }
        if (registered.Count != consumed.Count)
            throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/registration-readback");
        for (int index = 0; index < consumed.Count; index++)
            if (registered[index].Pointer != consumed[index].Pointer)
                throw new GenerationRefusal("gen-stage-invalid", "bulkhead-keys/registration-order");
    }
}
