using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Enemies;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal static class CaptureVanillaContext
{
    private static float[] Vec(Vector3 value) => new[] { value.x, value.y, value.z };
    private static float[] Quat(Quaternion value) => new[] { value.x, value.y, value.z, value.w };

    private static readonly HashSet<(long Epoch, int Instance)> StartedMarkers = new();
    private static long _markerEpoch;
    // Only the current world's markers are ever read, so a new world drops the old ones.
    internal static void MarkerStarted(NavMarkerComponent marker)
    {
        var epoch = RuntimeDiagnostics.WorldEpoch;
        if (epoch != _markerEpoch) { StartedMarkers.Clear(); _markerEpoch = epoch; }
        StartedMarkers.Add((epoch, marker.GetInstanceID()));
    }
    internal static uint? NativeId(object? value)
    {
        if (value == null) return null;
        if (RecReflect.Probe.TryScalar(value, out var scalar)) value = scalar;
        try { var id = Convert.ToUInt32(value, CultureInfo.InvariantCulture); return id == 0 ? null : id; }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    internal static bool Contextual(string field, string when) => field.Contains("[]", StringComparison.Ordinal)
        || field.StartsWith("MainModelTransform", StringComparison.Ordinal) || field == "m_itemModelHolder"
        || field.StartsWith("m_", StringComparison.Ordinal) && when.Contains("per-model", StringComparison.Ordinal)
        || when.Contains("state=", StringComparison.Ordinal) || when.Contains("arguments", StringComparison.Ordinal);

    internal static bool PhaseReached(object instance, string field, string when, MethodBase? nativeMethod, string phase, bool costAllocated = false)
    {
        if (phase != "after-native-before-forge") return false;
        var method = nativeMethod?.Name;
        if (field.StartsWith("MainModelTransform", StringComparison.Ordinal) || field.StartsWith("MaterialHandler", StringComparison.Ordinal))
            return instance is EnemyAgent modelEnemy && VanillaValueReading.ModelPhaseReached(
                nativeMethod?.DeclaringType?.FullName + "." + method, phase, ModelInitialized(modelEnemy));
        if (instance is Dam_EnemyDamageLimb limb)
            return method == "Setup" && LimbInitialized(limb);
        if (field == "m_itemModelHolder") return method == "OnGearSpawnComplete";
        if (field == "m_interactDuration") return method == "PlayerCanInteract";
        if (field == "m_enemyCost") return method == "ModeChange" && costAllocated;
        if (when.Contains("state=", StringComparison.Ordinal) && instance is EnemyAgent scanner)
        {
            var required = when.Split("state=", StringSplitOptions.None)[1].Split(';')[0];
            return method == "UpdateScannerData" && ScannerState(scanner) == required;
        }
        if (when.Contains("selected-population", StringComparison.Ordinal)) return method == "TryGetEnemyData";
        if (field.StartsWith("m_tagMarker", StringComparison.Ordinal))
            return method == "SyncPlaceNavMarkerTag" && instance is EnemyAgent marked
                && marked.m_tagMarker?.m_enemySubObj is { } marker
                && StartedMarkers.Contains((RuntimeDiagnostics.WorldEpoch, marker.GetInstanceID()));
        if (when.Contains("UpdateDetectionAnim", StringComparison.Ordinal)) return method == "UpdateDetectionAnim";
        if (when.Contains("InterpolateGlow", StringComparison.Ordinal)) return method == "InterpolateGlow";
        if (when.Contains("OnSetupFromGenerator", StringComparison.Ordinal)) return method == "OnSetupFromGenerator";
        if (when.Contains("scanner-update", StringComparison.Ordinal)) return method == "UpdateScannerData";
        return method is "Setup" or "SetupFromItem";
    }

    internal static bool ModelInitialized(EnemyAgent enemy)
        => enemy.MainModelTransform != null
            && VanillaValueReading.ModelInitialized(enemy.MainModelData?.ModelFile, enemy.MainModelTransform.GetInstanceID());

    internal static VanillaValueReading.ReadResult ReadOutcome(object instance, string field, string[] members, string when)
    {
        try
        {
            if (instance is EnemyAgent costEnemy && field == "m_enemyCost") return new("value", costEnemy.m_enemyCost);
            if (instance is EnemyAgent materialEnemy && field.StartsWith("MaterialHandler.", StringComparison.Ordinal))
                return Materials(materialEnemy, when);
            if (instance is Dam_EnemyDamageLimb limb)
            {
                var fields = members.Length == 0 ? new[] { field } : members;
                var reads = fields.ToDictionary(name => name, name => VanillaValueReading.ReadMember(limb, name));
                var failed = reads.Values.FirstOrDefault(read => !read.Available);
                if (failed != null) return failed;
                return new("value", new
                {
                    instance = limb.GetInstanceID(),
                    objectName = limb.gameObject.name,
                    limbId = limb.m_limbID,
                    limbType = limb.m_type.ToString(),
                    hasDestruction = limb.HasDestruction(),
                    modelFile = limb.m_base?.Owner?.MainModelData?.ModelFile,
                    health = limb.m_health,
                    healthMax = limb.m_healthMax,
                    weakspotDamageMulti = limb.m_weakspotDamageMulti,
                    armorDamageMulti = limb.m_armorDamageMulti,
                    fields = reads.ToDictionary(row => row.Key, row => Scalar(row.Value.Value)),
                    memberReads = reads.ToDictionary(row => row.Key, row => row.Value.Evidence)
                });
            }
            if (field.StartsWith("EnemySFXData.", StringComparison.Ordinal))
            {
                var selected = VanillaValueReading.ReadMember(instance, "EnemySFXData");
                if (!selected.Available) return selected;
                if (selected.Value == null) return new("field-unavailable", null, "EnemySFXData");
                var read = VanillaValueReading.ReadMember(selected.Value, field["EnemySFXData.".Length..]);
                // Read both the event and identity from the same selected native block, never a guessed fallback.
                var identity = VanillaValueReading.ReadMember(selected.Value, "persistentID");
                var selectedId = identity.Outcome == "value" ? NativeId(identity.Value) : null;
                return read.Outcome == "explicit-null" ? new("field-unavailable", null, "EnemySFXData." + read.NullTarget)
                    : read with
                    {
                        Value = Scalar(read.Value),
                        SelectedBlock = selectedId.HasValue
                        ? new { type = "EnemySFX", id = selectedId.Value, path = "EnemySFXData.persistentID" } : null
                    };
            }
            var value = Read(instance, field);
            return new(value == null ? "field-unavailable" : "value", value);
        }
        catch (Exception error) { return VanillaValueReading.Failed(error); }
    }

    internal static bool LimbInitialized(Dam_EnemyDamageLimb limb)
    {
        var damage = limb.m_base;
        var owner = damage?.Owner;
        var limbs = damage?.DamageLimbs;
        return damage != null && damage.IsSetup && owner != null && owner.EnemyDataID != 0
            && owner.Damage?.Pointer == damage.Pointer && limbs != null && limb.m_limbID >= 0
            && limb.m_limbID < limbs.Length && limbs[limb.m_limbID]?.Pointer == limb.Pointer;
    }

    internal static string ScannerState(EnemyAgent enemy)
    {
        var locomotion = enemy.AI?.m_locomotion;
        if (locomotion == null) return "phase-not-reached";
        var state = locomotion.CurrentStateEnum;
        if (state == ES_StateEnum.HibernateWakeUp) return "Wakeup";
        if (state == ES_StateEnum.ScoutDetection) return "ScoutFeeler";
        if (state == ES_StateEnum.PathMove && enemy.AI?.m_behaviour?.m_currentStateName == EB_States.Patrolling) return "ScoutPatrol";
        if (state == ES_StateEnum.Hibernate)
        {
            var hibernate = locomotion.Hibernate;
            if (hibernate == null) return "phase-not-reached";
            if (hibernate.m_detectData.detectOn) return "Detection";
            if (hibernate.m_heartbeatActive) return "Heartbeat";
            return "Default";
        }
        return state is ES_StateEnum.PathMove or ES_StateEnum.StandStill ? "Default" : "phase-not-reached";
    }

    /// <summary>The remaining reads after <see cref="ReadOutcome"/> has answered the cost, material and limb
    /// requests itself.</summary>
    private static object? Read(object instance, string field)
    {
        if (instance is EnemyAgent enemy)
        {
            if (field.StartsWith("MainModelTransform.", StringComparison.Ordinal))
            {
                if (!ModelInitialized(enemy)) return null;
                var model = enemy.MainModelTransform;
                if (model == null) return null;
                var member = field.Substring("MainModelTransform.".Length);
                return new Dictionary<string, object?>
                {
                    ["model"] = enemy.MainModelData?.ModelFile,
                    [member] = member switch
                    {
                        "localPosition" => Vec(model.localPosition),
                        "localEulerAngles" => Vec(model.localEulerAngles),
                        "localScale" => Vec(model.localScale),
                        _ => throw new InvalidOperationException("Unrequested main model transform member: " + member)
                    }
                };
            }
            if (field == "m_tagMarker.m_enemySubObj.m_sprites[].color")
            {
                var sprites = enemy.m_tagMarker?.m_enemySubObj?.m_sprites;
                if (sprites == null) return null;
                return sprites.Select(sprite => sprite == null ? null : new
                {
                    instance = sprite.GetInstanceID(),
                    name = sprite.name,
                    rgba = new[] { sprite.color.r, sprite.color.g, sprite.color.b, sprite.color.a }
                }).ToArray();
            }
        }
        if (field == "m_itemModelHolder" && instance is Item item)
            return item.m_itemModelHolder == null ? null : Transforms(item.m_itemModelHolder);
        return Scalar(RecReflect.ReadPath(instance, field));
    }

    private static object? Scalar(object? value)
    {
        if (value == null) return null;
        if (RecReflect.Probe.TryScalar(value, out var scalar)) return scalar;
        return value switch
        {
            Vector3 v => Vec(v),
            Vector4 v => new[] { v.x, v.y, v.z, v.w },
            Quaternion q => Quat(q),
            Color c => new[] { c.r, c.g, c.b, c.a },
            UnityEngine.Object obj => new
            {
                nativeInstanceId = obj.GetInstanceID(),
                name = obj.name,
                type = RecReflect.Probe.NativeClassName(RecReflect.Probe.NativeClass(obj))
            },
            _ => value
        };
    }

    // Context trees contain managed scalars, strings and arrays. Preserve every bone/part/property rather than
    // applying the recorder's diagnostic reflection depth/element truncation to baseline data.
    internal static void WriteRaw(Utf8JsonWriter json, object? value) => JsonSerializer.Serialize(json, value);

    private static object Transforms(Transform root)
    {
        var rows = new List<object>();
        var pending = new Stack<(Transform Node, string Path)>(); pending.Push((root, root.name));
        while (pending.Count > 0)
        {
            if (rows.Count >= 65536) throw new InvalidOperationException("Native transform observation budget exceeded; no partial baseline accepted.");
            var (node, path) = pending.Pop();
            rows.Add(new
            {
                instance = node.GetInstanceID(),
                path,
                name = node.name,
                localPosition = Vec(node.localPosition),
                localRotation = Quat(node.localRotation),
                localEulerAngles = Vec(node.localEulerAngles),
                localScale = Vec(node.localScale),
                components = node.GetComponents<Component>().Where(component => component != null)
                    .Select(component => RecReflect.Probe.NativeClassName(RecReflect.Probe.NativeClass(component))).ToArray()
            });
            for (var index = node.childCount - 1; index >= 0; index--)
            {
                var child = node.GetChild(index); pending.Push((child, path + "/" + index + ":" + child.name));
            }
        }
        return rows;
    }

    private static VanillaValueReading.ReadResult Materials(EnemyAgent enemy, string when)
    {
        if (!ModelInitialized(enemy)) return new("uninitialized", null);
        var handler = enemy.MaterialHandler;
        if (ReferenceEquals(handler, null)) return new("explicit-null", null, "MaterialHandler");
        if (handler == null) return new("uninitialized", null); // A destroyed Unity target is not a successfully read native null.
        var refs = handler.m_materialRefs;
        if (refs == null) return new("explicit-null", null, "MaterialHandler.m_materialRefs");
        var rows = new List<object>();
        var validMaterials = 0;
        foreach (var reference in refs)
        {
            if (reference == null) continue;
            var material = reference.m_material;
            if (material == null) continue;
            var shader = material.shader;
            if (shader == null) continue;
            validMaterials++;
            var renderers = new List<string>();
            if (reference.m_renderers != null)
                for (var index = 0; index < reference.m_renderers.Count; index++)
                    if (reference.m_renderers[index] is { } renderer) renderers.Add(renderer.name);
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                var name = shader.GetPropertyName(index); var type = shader.GetPropertyType(index).ToString();
                if (type is not ("Color" or "Float" or "Range")) continue;
                if (when.Contains("raw color", StringComparison.Ordinal) && type != "Color") continue;
                if (when.Contains("raw float", StringComparison.Ordinal) && type is not ("Float" or "Range")) continue;
                var present = material.HasProperty(name);
                if (!present) continue;
                object? raw = null;
                if (present && type == "Color") { var c = material.GetColor(name); raw = new[] { c.r, c.g, c.b, c.a }; }
                else if (present && type is "Float" or "Range") raw = material.GetFloat(name);
                rows.Add(new
                {
                    instance = material.GetInstanceID(),
                    material = material.name,
                    shader = shader.name,
                    renderers,
                    property = name,
                    type,
                    hasProperty = present,
                    rawValue = raw
                });
            }
        }
        return rows.Count > 0 ? new("value", rows) : new(validMaterials == 0 ? "empty-materials" : "empty-properties", null);
    }
}
