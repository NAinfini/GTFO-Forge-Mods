using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using CullingSystem;
using Decals;
using FX_EffectSystem;
using Il2CppInterop.Runtime;
using LevelGeneration;
using UnityEngine;

namespace ForgePerformance.LoadShedding;

internal sealed partial class DecorationShedding
{
    private readonly LoadSheddingController _owner;
    private SheddingSettings Settings => _owner.Settings;
    private readonly Dictionary<IntPtr, VisualEntry> _visuals = new();
    private readonly Dictionary<IntPtr, LightEntry> _lights = new();
    private readonly Dictionary<IntPtr, DecalEntry> _decals = new();
    private readonly Dictionary<IntPtr, DecorationClass> _classified = new();
    private readonly Dictionary<IntPtr, DecorationRisk> _decalRisks = new();
    private readonly HashSet<IntPtr> _controlledLights = new();
    private readonly long[] _classes = new long[5];
    private readonly DecalBudget<DecalEntry> _decalBudget;
    private readonly Action<DecalEntry> _retire;
    private int _impactDepth;
    private long _shadowSeen, _shadowChanged, _shadowRestored, _shadowCalls;
    private long _cullSeen, _cullChanged, _cullRestored, _cullCalls;
    private long _cullReleased;
    private Il2CppSystem.Collections.Generic.List<C_Cullable>? _nativeShown;
    private long _animationSeen, _animationChanged, _animationRestored;
    private long _decalSeen, _decalChanged, _decalSkipped, _decalRestored, _decalRetired;
    private long _lightSeen, _lightChanged, _lightSkipped, _lightRestored;
    private long _lightReleased, _visibilitySequence;
    private bool _lightReferencesResolved;
    private long _inspectionFailures;
    private string _inspectionReason = "none";
    private readonly bool _shaderAssetVerified;
    private readonly string _shaderAssetReason = "disabled";
    private Shader? _nonOccludingShader;
    private readonly LightEntry?[] _lightCandidates = new LightEntry[64];
    private readonly ShadowScope[] _shadowScopes = { new(), new(), new(), new() };
    // Entries the distance cut may hide, fixed when they register, and the ones
    // currently hidden by it. The camera hooks visit only these two lists.
    private readonly List<VisualEntry> _distanceCandidates = new();
    private readonly List<VisualEntry> _hiddenByDistance = new();
    // One removal plan per native cullable list; dropped whenever the eligible set changes.
    private readonly Dictionary<IntPtr, RemovalPlan<VisualEntry>> _shadowPlans = new();

    internal DecorationShedding(LoadSheddingController owner)
    {
        _owner = owner; _decalBudget = new DecalBudget<DecalEntry>(owner.Settings.DecalLimit); _retire = Retire;
        if (!owner.Settings.DecorationCulling) return;
        try
        {
            using var input = File.OpenRead(Path.Combine(BepInEx.Paths.GameRootPath, "GTFO_Data", "globalgamemanagers.assets"));
            using var sha = SHA256.Create();
            _shaderAssetVerified = Convert.ToHexString(sha.ComputeHash(input)) == DecorationClassifier.ShaderAssetSha256;
            _shaderAssetReason = _shaderAssetVerified ? "verified_single_additive_pass" : "asset_fingerprint_mismatch";
        }
        catch (Exception error) { _shaderAssetReason = "asset_unavailable:" + error.GetType().Name + ": " + error.Message; }
    }

    private DecorationRisk Hierarchy(GameObject root, bool decal = false)
    {
        try { return InspectHierarchy(root, decal); }
        catch (Exception error)
        {
            _inspectionFailures++; _inspectionReason = error.GetType().Name + ": " + error.Message;
            return DecorationRisk.UnknownComponent;
        }
    }
    private DecorationRisk InspectHierarchy(GameObject root, bool decal)
    {
        var risks = DecorationRisk.None;
        // Components and their exact IL2CPP types are inspected once, not in a
        // camera/animation loop. An unrecognized component is a hard denial.
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null) { risks |= DecorationRisk.UnknownComponent; continue; }
            if (component.TryCast<Collider>() != null || component.TryCast<Rigidbody>() != null)
                risks |= DecorationRisk.Collider;
            var type = component.GetIl2CppType().FullName;
            if (!(decal ? DecorationClassifier.IsDecalComponent(type) : DecorationClassifier.IsVisualComponent(type)))
                risks |= DecorationRisk.UnknownComponent;
            var animator = component.TryCast<Animator>();
            if (animator != null)
            {
                if (animator.hasRootMotion || animator.applyRootMotion) risks |= DecorationRisk.RootMotion;
                if (animator.hasBoundPlayables || animator.InternalGetBehaviours(Il2CppType.Of<StateMachineBehaviour>()).Length != 0)
                    risks |= DecorationRisk.StateBehaviour;
                var controller = animator.runtimeAnimatorController;
                if (controller == null) risks |= DecorationRisk.UnknownComponent;
                else foreach (var clip in controller.animationClips)
                    if (clip == null || clip.events.Length != 0) risks |= DecorationRisk.AnimationEvents;
            }
            var renderer = component.TryCast<Renderer>();
            if (renderer != null)
                foreach (var material in renderer.sharedMaterials)
                    // Material property names alone say nothing about actual
                    // render passes. Only the asset-reviewed, no-fallback,
                    // single pass SrcAlpha+One additive shader can be hidden.
                    if (material == null || material.shader == null || _nonOccludingShader == null
                        || material.shader.Pointer != _nonOccludingShader.Pointer
                        || !DecorationClassifier.NonOccludingShader(material.shader.name, _shaderAssetVerified,
                            material.shader.isSupported, material.passCount, material.renderQueue))
                        risks |= DecorationRisk.BlocksSight;
        }
        // Stop at the room container, never classify that container as a prop.
        // Between the leaf and room, even unknown parent scripts are denied.
        // A combat decal has its own visual-only prefab and reviewed native
        // provenance. Its attachment surface/FX pool is not its object tree:
        // never classify or modify that enemy, item, wall, or pool as decoration.
        for (var parent = decal ? null : root.transform.parent; parent != null; parent = parent.parent)
        {
            if (parent.GetComponent<LG_Geomorph>() != null) break;
            foreach (var component in parent.GetComponents<Component>())
                if (component == null || component.GetIl2CppType().FullName != "UnityEngine.Transform")
                    risks |= DecorationRisk.Gameplay;
        }
        return risks;
    }

    private static bool OwnedRenderers(Il2CppSystem.Collections.Generic.List<Renderer>? renderers, Transform root)
    {
        if (renderers == null) return false;
        foreach (var renderer in renderers)
            if (renderer == null || renderer.transform == null
                || (renderer.transform.Pointer != root.Pointer && !renderer.transform.IsChildOf(root))) return false;
        return true;
    }

    internal void CameraBefore()
    {
        if (!_owner.Active) return;
        var hidden = _hiddenByDistance;
        for (var i = 0; i < hidden.Count; i++)
        {
            var entry = hidden[i];
            if (!entry.Hidden || entry.Rotator == null) continue;
            // Restore first so the native portal/frustum pass decides whether
            // it wants this object shown; never Show after that native pass.
            RestoreVisibility(entry);
        }
        hidden.Clear();
    }
    private void RestoreVisibility(VisualEntry entry)
    {
        if (entry.Rotator.gameObject.activeInHierarchy && entry.Culler.IsRegistered && _nativeShown?.Contains(entry.Culler) == true)
        { entry.Culler.Show(); _cullRestored++; }
        else _cullReleased++; // Native unregistration/visibility supersedes us.
        entry.Hidden = false;
    }
    internal void CameraAfter()
    {
        if (!_owner.Active || _owner.IsRefused(SheddingCut.DecorationCulling)) return;
        var camera = ClusteredRendering.Current?.m_camera;
        if (camera == null) return;
        var position = camera.transform.position;
        var candidates = _distanceCandidates;
        for (var i = 0; i < candidates.Count; i++)
        {
            var entry = candidates[i];
            if (entry.Rotator == null || !entry.Culler.IsShown) continue;
            _cullCalls++;
            // Distance to nearest bounds point, not centre, protects a nearby
            // part of a large decorative object from premature disappearance.
            if (!DecorationPolicy.Beyond((entry.Culler.Bounds.ClosestPoint(position) - position).sqrMagnitude, Settings.CullDistance)) continue;
            entry.Culler.HideSafe();
            if (!entry.Hidden) _hiddenByDistance.Add(entry);
            entry.Hidden = true;
            if (!entry.Culled) { entry.Culled = true; _cullChanged++; }
        }
    }

    internal ShadowScope? Shadows(CL_ShadowGenerator generator)
    {
        if (!_owner.Active || _owner.IsRefused(SheddingCut.DecorationShadows)) return null;
        ShadowScope? scope = null;
        var nested = false;
        foreach (var candidate in _shadowScopes)
        {
            if (candidate.Active) nested = true;
            else scope ??= candidate;
        }
        if (scope == null) throw new InvalidOperationException("Decoration shadow nesting exceeds four calls.");
        scope.Active = true;
        // A nested call sees the outer call's already filtered lists, which no
        // recorded plan describes, so only the outermost call uses plans.
        scope.UsesPlans = !nested;
        try
        {
            FilterShadows(generator.StaticCullables, scope);
            FilterShadows(generator.MovingCullables, scope);
            FilterShadows(generator.DynamicCullables, scope);
            return scope;
        }
        catch { scope.Restore(); throw; }
    }
    private void FilterShadows(Il2CppSystem.Collections.Generic.List<C_Cullable> list, ShadowScope scope)
    {
        if (list == null) return;
        if (!scope.UsesPlans) { ScanShadows(list, scope, null); return; }
        var id = list.Pointer;
        var version = list._version;
        var count = list.Count;
        if (!_shadowPlans.TryGetValue(id, out var plan)) _shadowPlans[id] = plan = new RemovalPlan<VisualEntry>();
        if (plan.Matches(id, version, count))
            for (var i = 0; i < plan.Length; i++)
            {
                scope.Remove(list, plan.IndexAt(i), plan.ItemAt(i).Culler); _shadowCalls++;
            }
        else
        {
            plan.Begin(id, version, count);
            ScanShadows(list, scope, plan);
            plan.Commit();
        }
        scope.Track(plan, list);
    }
    private void ScanShadows(Il2CppSystem.Collections.Generic.List<C_Cullable> list, ShadowScope scope,
        RemovalPlan<VisualEntry>? plan)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var culler = list[i];
            if (culler == null || !_visuals.TryGetValue(culler.Pointer, out var entry)
                || !entry.ShadowEligible
                || !DecorationClassifier.Allows(DecorationClass.VisualRotator, entry.Risks, DecorationUse.Shadow)) continue;
            scope.Remove(list, i, entry.Culler); _shadowCalls++;
            plan?.Add(i, entry);
            if (!entry.Shadowed) { entry.Shadowed = true; _shadowChanged++; }
        }
    }

    // Only this overload's native callers are combat impact paths. The scope
    // is always closed by Harmony Finalizer, including exceptions/nesting.
    internal int ImpactBegin(FX_GroupName group)
    {
        var before = _impactDepth;
        _impactDepth = _owner.Active && DecorationClassifier.CombatImpact((int)group) ? before + 1 : 0;
        return before;
    }
    internal void ImpactEnd(int previous) => _impactDepth = previous;
    internal void DecalPlay(FX_Decal effect)
    {
        if (!_owner.Active || effect == null || effect.m_decal == null) return;
        _decalSeen++;
        var decal = effect.m_decal;
        var category = _impactDepth != 0 && decal.m_decalType == Decal.DecalType.Generic
            ? DecorationClass.CombatDecal : DecorationClass.Unknown;
        Classify(decal.Pointer, category);
        // Only a combat decal can be shortened, so the (costly) hierarchy check runs for those alone, once per pooled effect.
        if (category != DecorationClass.CombatDecal) { _decalSkipped++; return; }
        if (!_decalRisks.TryGetValue(effect.Pointer, out var risks))
            _decalRisks[effect.Pointer] = risks = Hierarchy(effect.gameObject, true);
        if (!DecorationClassifier.Allows(category, risks, DecorationUse.Decal)
            || !float.IsFinite(effect.m_uniqueLifeTime) || effect.m_uniqueLifeTime <= 0
            || effect.m_feedMaterialLife && (!float.IsFinite(effect.m_feedMaterialLifeStartAt)
                || effect.m_feedMaterialLifeStartAt <= 0 || effect.m_instancedMaterialRef == null))
        { _decalSkipped++; return; }
        DecalReturn(effect);
        var original = effect.m_uniqueLifeTime;
        var reduced = DecorationPolicy.Lifetime(original, Settings.DecalLifetime);
        var entry = new DecalEntry(effect.Pointer, effect, original, effect.m_fadeoutMod, Time.time + original);
        _decals.Add(effect.Pointer, entry);
        effect.m_uniqueLifeTime = reduced;
        if (effect.m_feedMaterialLife && effect.m_feedMaterialLifeStartAt > 0 && effect.m_instancedMaterialRef != null)
        {
            effect.m_fadeoutMod = 1f / effect.m_feedMaterialLifeStartAt / reduced;
            effect.m_instancedMaterialRef.SetFloat(FX_Decal.s_decalLifeIDHash, effect.m_fadeoutMod);
        }
        _decalBudget.Add(entry, Time.time + reduced, _retire); _decalChanged++;
    }
    internal void DecalReturn(FX_Decal effect)
    {
        if (!_decals.Remove(effect.Pointer, out var entry)) return;
        _decalBudget.Remove(entry); RestoreDecal(entry);
    }
    private void RestoreDecal(DecalEntry entry)
    {
        var effect = entry.Effect;
        if (effect == null) return;
        effect.m_uniqueLifeTime = entry.Lifetime; effect.m_fadeoutMod = entry.Fade; effect.m_endTime = entry.End;
        if (effect.m_feedMaterialLife && effect.m_instancedMaterialRef != null)
        {
            effect.m_instancedMaterialRef.SetFloat(FX_Decal.s_decalLifeIDHash, entry.Fade);
            effect.m_instancedMaterialRef.SetFloat(FX_Decal.s_decalLifeEndIDHash, entry.End);
        }
        _decalRestored++;
    }
    private void Retire(DecalEntry entry)
    {
        // The wrapper is gone, so only the pointer captured at registration can still name its row.
        if (entry.Effect == null) { _decals.Remove(entry.Key); return; }
        _decalRetired++;
        entry.Effect.ReturnToPool();
    }
    internal void TickDecals() => _decalBudget.Expire(Time.time, _retire);

    // The lights the game or an objective controls are marked first, one piece per scan of the scene; every ambient light is classified after all of
    // them (the queue keeps its order), one piece each.
    private void ScanLights()
    {
        _lightReferencesResolved = true;
        void Scan(string name, Action scan) => _owner.Work.Add(name, () => _owner.SetupCut(SheddingCut.DecorationLights, scan));
        Scan("light-generators", () =>
        {
            foreach (var objective in UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>()) ControlledLights(objective.m_lightCollection);
        });
        Scan("light-reactors", () =>
        {
            foreach (var reactor in UnityEngine.Object.FindObjectsOfType<LG_WardenObjective_Reactor>())
            {
                ControlledLights(reactor.m_lightCollection);
                MarkLights(reactor.m_lightsWhenOn); MarkLights(reactor.m_lightsWhenOff);
            }
            var environment = EnvironmentStateManager.Current;
            if (environment == null) _lightReferencesResolved = false;
            else ControlledLights(environment.m_expeditionLightCollection);
        });
        Scan("light-sequences", () =>
        {
            foreach (var sequencer in UnityEngine.Object.FindObjectsOfType<LG_RoomLightSequencer>())
            {
                if (sequencer.m_sequence == null) { _lightReferencesResolved = false; continue; }
                foreach (var step in sequencer.m_sequence)
                    if (step == null) _lightReferencesResolved = false; else MarkLights(step.Lights);
            }
        });
        Scan("light-overrides", () =>
        {
            foreach (var lightOverride in UnityEngine.Object.FindObjectsOfType<LG_LightSettingsOverride>()) MarkLights(lightOverride.m_selectedLights);
        });
        Scan("light-end-rooms", () =>
        {
            foreach (var endRoom in UnityEngine.Object.FindObjectsOfType<TGA_End_Room>()) MarkLights(endRoom.m_allLightsToDisable);
        });
        Scan("light-ambient-find", () =>
        {
            foreach (var light in UnityEngine.Object.FindObjectsOfType<LG_SpotLightAmbient>())
            {
                var one = light;
                _owner.Work.Add("light", () => { if (_owner.Active) _owner.SetupCut(SheddingCut.DecorationLights, () => ScanLight(one)); });
            }
        });
    }

    private void ScanLight(LG_SpotLightAmbient light)
    {
        if (light == null) return;
        var native = light.GetC_Light();
        if (native == null || _lights.ContainsKey(native.Pointer)) return;
        var risk = _lightReferencesResolved ? DecorationRisk.None : DecorationRisk.UnverifiedLightReceiver;
        if (light.m_category != LG_Light.LightCategory.General || !native.IsStatic || native.LightUpdator != null)
            risk |= DecorationRisk.ControlledLight;
        if (_controlledLights.Contains(light.Pointer)) risk |= DecorationRisk.ControlledLight;
        if (native.TryCast<C_LightDynamic>()?.IsFPSPlayerLight == true || native.m_clusterLight == null
            || native.m_clusterLight.TryCast<CL_SpotLight>()?.IsFPSFlashlight == true)
            risk |= DecorationRisk.PlayerLight;
        foreach (var component in light.GetComponentsInChildren<Component>(true))
            if (component == null || component.GetIl2CppType().FullName is not
                ("UnityEngine.Transform" or "UnityEngine.Light" or "LevelGeneration.LG_SpotLightAmbient" or "CL_SpotLight"))
                risk |= DecorationRisk.UnknownComponent;
        for (var parent = light.transform.parent; parent != null; parent = parent.parent)
        {
            if (parent.GetComponent<LG_Geomorph>() != null) break;
            foreach (var component in parent.GetComponents<Component>())
                if (component == null || component.GetIl2CppType().FullName != "UnityEngine.Transform") risk |= DecorationRisk.Gameplay;
        }
        var entry = new LightEntry(native, risk);
        _lights.Add(native.Pointer, entry);
        Classify(native.Pointer, DecorationClassifier.Allows(DecorationClass.StaticGeneralLight, risk, DecorationUse.Light)
            ? DecorationClass.StaticGeneralLight : (risk & (DecorationRisk.ControlledLight | DecorationRisk.PlayerLight | DecorationRisk.Gameplay)) != 0
                ? DecorationClass.Gameplay : DecorationClass.Unknown);
        _lightSeen++;
    }
    private void MarkLights(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LG_Light>? lights)
    {
        if (lights == null) { _lightReferencesResolved = false; return; }
        foreach (var light in lights)
        {
            if (light == null) { _lightReferencesResolved = false; continue; }
            MarkLight(light);
        }
    }
    private void MarkLight(LG_Light light)
    {
        _controlledLights.Add(light.Pointer);
        var culler = light.GetC_Light();
        if (culler != null && _lights.TryGetValue(culler.Pointer, out var entry))
        { entry.Risks |= DecorationRisk.ControlledLight; Classify(culler.Pointer, DecorationClass.Gameplay); }
    }
    internal void ControlledLights(LG_LightCollection? collection)
    {
        if (!_owner.Active || _owner.IsRefused(SheddingCut.DecorationLights)) return;
        if (collection?.collectedLights == null) { _lightReferencesResolved = false; return; }
        foreach (var value in collection.collectedLights)
        {
            if (value?.light == null) { _lightReferencesResolved = false; continue; }
            MarkLight(value.light);
        }
    }
    internal void LightsAfter()
    {
        if (!_owner.Active || _owner.IsRefused(SheddingCut.DecorationLights)) return;
        _visibilitySequence++;
        if (!_lightReferencesResolved) return;
        var count = 0;
        GatherLights(C_CullingManager.VisibleSpotLights, ref count);
        GatherLights(C_CullingManager.VisiblePointLights, ref count);
        // Descending native m_priority, encounter order for ties. At most the
        // two native 32-entry visible lists, with cached wrappers only.
        for (var i = 1; i < count; i++)
        {
            var value = _lightCandidates[i]!; var j = i;
            while (j > 0 && value.Light.m_priority > _lightCandidates[j - 1]!.Light.m_priority)
            { _lightCandidates[j] = _lightCandidates[j - 1]; j--; }
            _lightCandidates[j] = value;
        }
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            if (DecorationPolicy.KeepLight(true, ref kept, Settings.LightLimit)) continue;
            var entry = _lightCandidates[i]!;
            entry.OriginalActiveId = entry.Cluster.ActiveID;
            entry.Cluster.ActiveID = -1;
            if (entry.List!.Remove(entry.Light))
            { if (!entry.Touched) { entry.Touched = true; _lightChanged++; } entry.Dropped = true; }
        }
        Array.Clear(_lightCandidates, 0, count);
    }
    private void GatherLights(Il2CppSystem.Collections.Generic.List<C_Light> list, ref int count)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var native = list[i];
            if (native == null || !_lights.TryGetValue(native.Pointer, out var entry)
                || !DecorationClassifier.Allows(DecorationClass.StaticGeneralLight, entry.Risks, DecorationUse.Light))
            { _lightSkipped++; continue; }
            if (entry.Dropped) { entry.Dropped = false; _lightReleased++; }
            if (count == _lightCandidates.Length) throw new InvalidOperationException("Native visible light capacity exceeded 64.");
            entry.List = list; entry.OriginalIndex = i; entry.Sequence = _visibilitySequence;
            _lightCandidates[count++] = entry;
        }
    }
    private void RestoreLights()
    {
        var count = 0;
        foreach (var entry in _lights.Values)
        {
            if (!entry.Dropped) continue;
            if (entry.Sequence != _visibilitySequence || entry.Light == null || !entry.Light.IsRegistered || !entry.Light.IsShown)
            { entry.Dropped = false; _lightReleased++; continue; }
            _lightCandidates[count++] = entry;
        }
        // Ascending original indices restore each native list exactly, even
        // when important lights were interleaved with the removed decoration.
        for (var i = 1; i < count; i++)
        {
            var value = _lightCandidates[i]!; var j = i;
            while (j > 0 && value.OriginalIndex < _lightCandidates[j - 1]!.OriginalIndex)
            { _lightCandidates[j] = _lightCandidates[j - 1]; j--; }
            _lightCandidates[j] = value;
        }
        try
        {
            for (var i = 0; i < count; i++)
            {
                var entry = _lightCandidates[i]!;
                if (!entry.List!.Contains(entry.Light)) entry.List.Insert(entry.OriginalIndex, entry.Light);
                entry.Cluster.ActiveID = entry.OriginalActiveId; entry.Dropped = false; _lightRestored++;
            }
        }
        finally { Array.Clear(_lightCandidates, 0, count); }
    }

    internal void EndLevel()
    {
        Exception? first = null;
        foreach (var scope in _shadowScopes) try { _shadowRestored += scope.Restore(); } catch (Exception e) { first ??= e; }
        foreach (var entry in _visuals.Values)
        {
            if (entry.Rotator == null) continue;
            try
            {
                if (entry.Hidden) RestoreVisibility(entry);
                foreach (var (skin, original) in entry.Skins)
                    if (skin != null && original && !skin.updateWhenOffscreen)
                    { skin.updateWhenOffscreen = original; _animationRestored++; }
                foreach (var (animator, original) in entry.Animators)
                    if (animator != null && original != AnimatorCullingMode.CullCompletely
                        && animator.cullingMode == AnimatorCullingMode.CullCompletely)
                    { animator.cullingMode = original; _animationRestored++; }
            }
            catch (Exception e) { first ??= e; }
        }
        foreach (var entry in _decals.Values) try { RestoreDecal(entry); } catch (Exception e) { first ??= e; }
        try { RestoreLights(); } catch (Exception e) { first ??= e; }
        _decals.Clear(); _decalBudget.Clear(); _visuals.Clear(); _distanceCandidates.Clear(); _hiddenByDistance.Clear(); _shadowPlans.Clear(); _lights.Clear(); _controlledLights.Clear(); _nativeShown = null;
        if (first != null) _owner.RestorationFailed();
        void Log(bool enabled, string item, long seen, long changed, long restored, string extra)
        { if (enabled) _owner.LogItem(item, seen, changed, restored, extra); }
        Log(Settings.DecorationShadows, "decoration-shadows", _shadowSeen, _shadowChanged, _shadowRestored, "draws_skipped=" + _shadowCalls);
        Log(Settings.DecorationCulling, "decoration-culling", _cullSeen, _cullChanged, _cullRestored, "distance_m=" + Settings.CullDistance
            + "\tchecks=" + _cullCalls + "\tnative_scope_released=" + _cullReleased + "\tshader_asset_verified=" + _shaderAssetVerified
            + "\tshader_found=" + (_nonOccludingShader != null) + "\tshader_reason=" + _shaderAssetReason);
        Log(Settings.DecorationAnimation, "decoration-animation", _animationSeen, _animationChanged, _animationRestored, "skinning_offscreen=false\tanimator=CullCompletely");
        Log(Settings.CombatDecals, "combat-decals", _decalSeen, _decalChanged, _decalRestored, "limit=" + Settings.DecalLimit + "\tlifetime_s=" + Settings.DecalLifetime + "\tclassified_skipped=" + _decalSkipped + "\tretired=" + _decalRetired);
        Log(Settings.DecorationLights, "decoration-lights", _lightSeen, _lightChanged, _lightRestored, "limit=" + Settings.LightLimit + "\tclassified_skipped=" + _lightSkipped + "\tcontrol_references_resolved=" + _lightReferencesResolved + "\tnative_scope_released=" + _lightReleased + "\trestore=visible_list_and_active_id;buffer_uploaded_by_native_next_frame");
        if (Settings.DecorationShadows || Settings.DecorationCulling || Settings.DecorationAnimation || Settings.DecorationLights || Settings.CombatDecals)
            for (var i = 0; i < _classes.Length; i++) _owner.LogItem("decoration-classification", _classes[i], 0, 0,
                "class=" + (DecorationClass)i + "\tinspection_failed=" + _inspectionFailures + "\tinspection_reason=" + _inspectionReason);
        if (first != null) throw first;
    }
    internal Exception? ShadowRestore(ShadowScope? scope, Exception? error)
    {
        try { if (scope != null) _shadowRestored += scope.Restore(); return error; }
        catch (Exception restore) { _owner.RestorationFailed(); return error == null ? restore : new AggregateException(error, restore); }
    }
    internal sealed class VisualEntry
    {
        internal readonly LG_AnimatorRotator Rotator;
        internal readonly C_CullBucket Culler;
        internal readonly DecorationRisk Risks;
        internal readonly bool ShadowEligible;
        internal readonly List<(SkinnedMeshRenderer, bool)> Skins = new();
        internal readonly List<(Animator, AnimatorCullingMode)> Animators = new();
        internal bool Hidden, Culled, Shadowed;
        internal VisualEntry(LG_AnimatorRotator rotator, C_CullBucket culler, DecorationRisk risks, bool shadowEligible)
        { Rotator = rotator; Culler = culler; Risks = risks; ShadowEligible = shadowEligible; }
    }
    private sealed class LightEntry
    {
        internal readonly C_Light Light;
        internal readonly CL_Light Cluster;
        internal DecorationRisk Risks;
        internal Il2CppSystem.Collections.Generic.List<C_Light>? List;
        internal bool Touched, Dropped;
        internal int OriginalIndex, OriginalActiveId;
        internal long Sequence;
        internal LightEntry(C_Light light, DecorationRisk risks)
        { Light = light; Cluster = light.m_clusterLight; Risks = risks; }
    }
    private sealed record DecalEntry(IntPtr Key, FX_Decal Effect, float Lifetime, float Fade, float End);

    internal sealed class ShadowScope : ListScope<C_Cullable>
    {
        private readonly RemovalPlan<VisualEntry>?[] _plans = new RemovalPlan<VisualEntry>[3];
        private readonly Il2CppSystem.Collections.Generic.List<C_Cullable>?[] _lists = new Il2CppSystem.Collections.Generic.List<C_Cullable>[3];
        private int _tracked;
        internal bool UsesPlans;
        internal void Track(RemovalPlan<VisualEntry> plan, Il2CppSystem.Collections.Generic.List<C_Cullable> list)
        { _plans[_tracked] = plan; _lists[_tracked++] = list; }
        // Only after every removal was reinserted: the plans then learn the lists' new versions.
        internal new int Restore()
        {
            var count = base.Restore();
            for (var i = 0; i < _tracked; i++)
            { _plans[i]!.Reverted(_lists[i]!._version); _plans[i] = null; _lists[i] = null; }
            _tracked = 0; UsesPlans = false;
            return count;
        }
    }
    internal class ListScope<T> where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        private readonly (Il2CppSystem.Collections.Generic.List<T> list, int index, T value)[] _removed = new (Il2CppSystem.Collections.Generic.List<T>, int, T)[4096];
        private int _count;
        internal bool Active;
        internal void Remove(Il2CppSystem.Collections.Generic.List<T> list, int index, T value)
        {
            if (_count == _removed.Length) throw new InvalidOperationException("Decoration list exceeds 4096 removals.");
            _removed[_count++] = (list, index, value); list.RemoveAt(index);
        }
        internal int Restore()
        {
            var count = _count;
            while (_count != 0)
            {
                var entry = _removed[_count - 1];
                entry.list.Insert(entry.index, entry.value);
                _removed[--_count] = default;
            }
            Active = false; return count;
        }
    }
}
