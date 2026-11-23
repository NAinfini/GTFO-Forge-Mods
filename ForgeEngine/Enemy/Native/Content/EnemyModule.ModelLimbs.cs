using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using UnityEngine;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>
/// The module side of model attachments: placement on every machine's spawn, the host's crack decision, and the
/// stage table that carries it. The table rides Forge's existing native-state snapshot and delta (the same channel
/// the Map objective text and global light use), so late joiners and checkpoints receive it too; no packet of its
/// own exists. Every machine registers the key, so a table never arrives without a reader.
/// </summary>
internal sealed partial class EnemyModule
{
    internal const string ModelLimbStageKey = "forge.enemy.model-limb-stages";
    private const string TumorLimbPrefix = "EggSack";
    private EnemyModelAssemblies? _models;
    private DomainStateChannel.NativeStateSource? _modelLimbStages;
    private static GameObject? _breakEffect;
    private static bool _breakEffectSearched;

    internal EnemyModelAssemblies Models => _models ??= EnemyModelAssemblies.Empty(Log, EnemyLabel);

    partial void CreateModelLimbStages()
        => _modelLimbStages = DomainStateChannel.RegisterNativeState(_kernel, ModelLimbStageKey,
            EnemyModelAssemblies.CaptureStages, RestoreModelLimbStages);

    /// <summary>Per enemy type, the native limb ids whose part is removed or replaced (resolved with the prefab).</summary>
    private readonly Dictionary<uint, int[]> _hiddenLimbs = new();

    /// <summary>
    /// On every machine from the same data: the limbs of removed or replaced parts lose their
    /// colliders first (the limb keeps its array slot, so every id after it keeps its meaning), then the placements add
    /// their own hit parts. When parts were taken out, no native limb is left to hit and not one placement's hit part could
    /// be placed, the hidden colliders come back and <c>enemy.hit-fallback</c> says so: an enemy is never unkillable.
    /// </summary>
    partial void ApplyModels(EnemyAgent enemy)
    {
        uint? type = _enemyType?.Invoke(enemy);
        List<Collider>? hidden = null;
        if (type is { } id && _hiddenLimbs.TryGetValue(id, out var limbs) && enemy.Damage is { } damage)
            hidden = EnemyHiddenLimbs.Disable(damage, limbs);
        var placed = _models?.Apply(enemy, _enemyType, BreakEffect) ?? default;
        if (hidden == null || hidden.Count == 0) return;
        if (placed.Placed != 0 || enemy.Damage is not { } receiver || EnemyHiddenLimbs.AnyHittable(receiver))
        {
            // The placed parts can break later; the last one to go gives the hidden colliders back.
            if (placed.Placed != 0)
                EnemyModelHitParts.KeepHidden(enemy.GlobalID, hidden,
                    () => enemy.Damage is { } now && EnemyHiddenLimbs.AnyHittable(now),
                    count => ReportHitFallback(type!.Value, "last-placed-part-broke", count, placed.Requested,
                        "the last placed part broke and every vanilla part was removed or replaced; the vanilla colliders were switched back on"));
            return;
        }
        foreach (var collider in hidden) if (collider != null) collider.enabled = true;
        ReportHitFallback(type!.Value, "hit-parts-unplaced", hidden.Count, placed.Requested,
            "every part was removed or replaced and no placement's hit part could be placed; the vanilla colliders were switched back on");
    }

    private void ReportHitFallback(uint type, string reason, int hiddenColliders, int hitAssemblies, string detail)
    {
        if (Log.Logs(RuntimeLogLevel.Error))
            WriteTypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.HitFallback, type, null, reason, detail, new[]
            {
                RuntimeLogValue.Of("hiddenColliders", (long)hiddenColliders), RuntimeLogValue.Of("hitAssemblies", (long)hitAssemblies)
            });
    }

    partial void RemoveModels(EnemyAgent enemy)
    {
        EnemyModelHitParts.ForgetHidden(enemy.GlobalID);
        if (_models?.Remove(enemy) == true && Session.IsHost) _modelLimbStages?.Changed();
    }

    partial void ClearModels()
    {
        _breakEffectSearched = false;
        EnemyModelHitParts.ClearHidden();
        _models?.ClearWorld();
    }

    partial void DisposeModels()
    {
        _modelLimbStages?.Dispose();
        _modelLimbStages = null;
        _models?.Dispose();
        _models = null;
    }

    /// <summary>The host's reading of one landed hit; a crossed stage is shown here and replicated.</summary>
    internal void AfterModelLimbDamage(Dam_EnemyDamageBase receiver, int limbID)
    {
        if (!Session.IsHost || _models == null) return;
        if (_models.AdvanceFromHit(receiver, limbID)) _modelLimbStages?.Changed();
    }

    /// <summary>
    /// A received table. The host keeps its own limb health as the authority and only catches up from it; a client
    /// shows the host's stages. A malformed table throws, which the native-state restore reports.
    /// </summary>
    private void RestoreModelLimbStages(JsonElement state)
    {
        var rows = EnemyModelAssemblies.ReadStages(state);
        if (_models == null) return;
        if (Session.IsHost) _models.AdvanceFromHealth();
        else _models.RestoreStages(rows);
    }

    /// <summary>The limbs of removed or replaced parts: their colliders are what every bullet, explosion and melee query resolves a
    /// receiver through, so a switched-off collider lets a shot pass on to whatever stands behind it. The limb also leaves
    /// the destruction list so nothing breaks the part later.</summary>
    internal static class EnemyHiddenLimbs
    {
        internal static List<Collider> Disable(Dam_EnemyDamageBase damage, int[] limbIds)
        {
            var switched = new List<Collider>();
            var limbs = damage.DamageLimbs;
            if (limbs == null) return switched;
            foreach (int id in limbIds)
            {
                if ((uint)id >= (uint)limbs.Length || limbs[id] is not { } limb) continue;
                foreach (var collider in limb.GetComponents<Collider>())
                    if (collider != null && collider.enabled) { collider.enabled = false; switched.Add(collider); }
                damage.DamageLimbsWithDestruction?.Remove(limb);
            }
            return switched;
        }

        /// <summary>Whether some limb of the receiver still has a collider a shot can reach.</summary>
        internal static bool AnyHittable(Dam_EnemyDamageBase damage)
        {
            var limbs = damage.DamageLimbs;
            if (limbs == null) return false;
            foreach (var limb in limbs)
            {
                if (limb == null || !limb.gameObject.activeInHierarchy) continue;
                foreach (var collider in limb.GetComponents<Collider>()) if (collider != null && collider.enabled) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The vanilla tumor burst: the linked effect of the first enabled enemy type, in persistent-ID order, whose
    /// built prefab carries an EggSack `Dam_EnemyDamageLimb_Custom` with a destruction effect (the Tank in the
    /// shipped data). Searched once per world, only when a breakable part is placed.
    /// </summary>
    private static GameObject? BreakEffect()
    {
        if (_breakEffect != null) return _breakEffect;
        if (_breakEffectSearched) return null;
        _breakEffectSearched = true;
        var ids = new List<uint>();
        foreach (var block in GameDataBlockBase<EnemyDataBlock>.GetAllBlocks())
            if (block != null && block.internalEnabled) ids.Add(block.persistentID);
        ids.Sort();
        foreach (uint id in ids)
        {
            var prefab = EnemyPrefabManager.GetEnemyPrefab(id);
            if (prefab == null) continue;
            foreach (var limb in prefab.GetComponentsInChildren<Dam_EnemyDamageLimb_Custom>(true))
            {
                if (limb == null || !limb.gameObject.name.StartsWith(TumorLimbPrefix, StringComparison.Ordinal)) continue;
                var effect = limb.m_effectOnDestroy;
                if (effect != null && effect.m_linkedEffect != null) return _breakEffect = effect.m_linkedEffect;
            }
        }
        return null;
    }
}
