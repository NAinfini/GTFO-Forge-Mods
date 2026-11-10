using System;
using CullingSystem;
using Expedition;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgePerformance.LoadShedding;

// Shared discovery/classification, followed by independent consumer setup.
internal sealed partial class DecorationShedding
{
    internal void BeginLevel()
    {
        Array.Clear(_classes, 0, _classes.Length); _classified.Clear(); _decalRisks.Clear();
        _shadowSeen = _shadowChanged = _shadowRestored = _shadowCalls = 0;
        _cullSeen = _cullChanged = _cullRestored = _cullCalls = 0;
        _cullReleased = 0;
        _nativeShown = null;
        if (Settings.DecorationCulling) _owner.SetupCut(SheddingCut.DecorationCulling,
            () => _nativeShown = C_CullingManager.GetCurrentlyShown());
        _animationSeen = _animationChanged = _animationRestored = 0;
        _decalSeen = _decalChanged = _decalSkipped = _decalRestored = _decalRetired = 0;
        _lightSeen = _lightChanged = _lightSkipped = _lightRestored = 0;
        _lightReleased = _visibilitySequence = 0;
        _impactDepth = 0;
        _inspectionFailures = 0; _inspectionReason = "none";
    }

    // Queued, never run in one go: the classification costs thousands of native calls (see LevelWork).
    internal void ScanLevel()
    {
        if (!_owner.Active) return;
        if (Settings.DecorationCulling && _shaderAssetVerified && _nonOccludingShader == null)
            _owner.Work.Add("decoration-shader", ScanShader);
        // Classification is shared; native discovery runs once for all consumers.
        if (Settings.DecorationShadows && !_owner.IsRefused(SheddingCut.DecorationShadows)
            || Settings.DecorationCulling && !_owner.IsRefused(SheddingCut.DecorationCulling)
            || Settings.DecorationAnimation && !_owner.IsRefused(SheddingCut.DecorationAnimation))
            _owner.Work.Add("decoration-find", ScanVisuals);
        // Queues the light scans now, ahead of the per-object pieces the other scans queue, so every whole-scene scan runs first.
        if (Settings.DecorationLights) _owner.SetupCut(SheddingCut.DecorationLights, ScanLights);
    }

    private void ScanShader()
    {
        if (!_owner.Active || _nonOccludingShader != null) return;
        _owner.SetupCut(SheddingCut.DecorationCulling, () =>
        {
            _nonOccludingShader = Shader.Find(DecorationClassifier.NonOccludingShaderName);
            var matches = 0;
            foreach (var shader in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Shader>()))
                if (shader != null && shader.name == DecorationClassifier.NonOccludingShaderName)
                {
                    matches++;
                    if (_nonOccludingShader == null || shader.Pointer != _nonOccludingShader.Pointer) matches += 2;
                }
            if (matches != 1)
            {
                _nonOccludingShader = null; _inspectionFailures++;
                _inspectionReason = "shader_identity_ambiguous_or_missing";
                _owner.LogInspectionFailure(_inspectionReason);
            }
        });
    }

    // One piece per rotator; the first piece that finds the native binding unprovable denies each dependent cut for the level.
    private void ScanVisuals()
    {
        foreach (var rotator in UnityEngine.Object.FindObjectsOfType<LG_AnimatorRotator>())
        {
            var one = rotator;
            _owner.Work.Add("decoration", () =>
            {
                if (!_owner.Active) return;
                try { RegisterVisual(one); }
                catch (Exception error)
                {
                    // An unprovable shared native binding denies each dependent cut,
                    // without blocking independent lights or the rest of the level start.
                    if (Settings.DecorationShadows) _owner.SetupCut(SheddingCut.DecorationShadows, () => throw error);
                    if (Settings.DecorationCulling) _owner.SetupCut(SheddingCut.DecorationCulling, () => throw error);
                    if (Settings.DecorationAnimation) _owner.SetupCut(SheddingCut.DecorationAnimation, () => throw error);
                }
            });
        }
    }

    private void Classify(IntPtr identity, DecorationClass category)
    {
        if (_classified.TryGetValue(identity, out var old))
        { if (old == category) return; _classes[(int)old]--; }
        _classified[identity] = category; _classes[(int)category]++;
    }

    private void RegisterVisual(LG_AnimatorRotator rotator)
    {
        if (rotator == null || _classified.ContainsKey(rotator.Pointer)) return;
        var risks = Hierarchy(rotator.gameObject);
        var culler = rotator.CullBucket;
        // Native buckets can collect a room, items, doors, or callback owners.
        // A candidate must belong exactly to this visual-only native owner.
        if (culler == null || culler.m_owner == null || culler.m_owner.gameObject.Pointer != rotator.gameObject.Pointer)
            risks |= DecorationRisk.UnknownComponent;
        var dynamic = culler?.TryCast<C_DynamicCuller>();
        if (dynamic == null || dynamic.m_updateCuller == null || dynamic.m_updateCuller.Pointer != rotator.Pointer
            || culler!.SpecialRenderableObjects == null || culler.SpecialRenderableObjects.Count != 0
            || culler.ExtraShadowRenderers == null || culler.ExtraShadowRenderers.Count != 0)
            risks |= DecorationRisk.UnknownComponent;
        if (culler != null)
        {
            if (!OwnedRenderers(culler.Renderers, rotator.transform) || !OwnedRenderers(culler.ShadowRenderers, rotator.transform))
                risks |= DecorationRisk.UnknownComponent;
        }
        var category = risks.HasFlag(DecorationRisk.Gameplay) || risks.HasFlag(DecorationRisk.Collider)
            ? DecorationClass.Gameplay : risks.HasFlag(DecorationRisk.UnknownComponent) ? DecorationClass.Unknown : DecorationClass.VisualRotator;
        if (category != DecorationClass.VisualRotator || culler == null)
        {
            Classify(rotator.Pointer, category);
            if (Settings.DecorationShadows) _shadowSeen++;
            if (Settings.DecorationCulling) _cullSeen++;
            if (Settings.DecorationAnimation) _animationSeen++;
            return;
        }
        var room = rotator.GetComponentInParent<LG_Geomorph>();
        var entry = new VisualEntry(rotator, culler, risks,
            room != null && room.m_assignedSubComplexFromZone != SubComplex.Gardens && culler.ShadowRenderers.Count != 0);
        Classify(rotator.Pointer, category);
        if (Settings.DecorationShadows) _shadowSeen++;
        if (Settings.DecorationCulling) _cullSeen++;
        if (Settings.DecorationAnimation) _animationSeen++;
        _visuals[culler.Pointer] = entry;
        _shadowPlans.Clear(); // the eligible set changed, so recorded shadow removals are stale
        if (DecorationClassifier.Allows(DecorationClass.VisualRotator, risks, DecorationUse.Distance)) _distanceCandidates.Add(entry);
        if (Settings.DecorationAnimation && DecorationClassifier.Allows(category, risks, DecorationUse.Animation))
            _owner.SetupCut(SheddingCut.DecorationAnimation, () =>
        {
            foreach (var skin in rotator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                entry.Skins.Add((skin, skin.updateWhenOffscreen));
                if (skin.updateWhenOffscreen) { skin.updateWhenOffscreen = false; _animationChanged++; }
            }
            foreach (var animator in rotator.GetComponentsInChildren<Animator>(true))
            {
                entry.Animators.Add((animator, animator.cullingMode));
                if (animator.cullingMode != AnimatorCullingMode.CullCompletely)
                { animator.cullingMode = AnimatorCullingMode.CullCompletely; _animationChanged++; }
            }
        });
    }

}

