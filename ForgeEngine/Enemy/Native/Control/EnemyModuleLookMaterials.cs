using System;
using System.Collections.Generic;
using System.Text;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The instance material pass: the `MaterialSets` float-property writes, run once per live enemy
/// after the region assembly. Every material the instance's renderers and its
/// <c>CharacterMaterialHandler.m_materialRefs</c> hold is a game asset shared with other types, so nothing is
/// written to it: the first write to a material makes one instance copy (<c>new Material(Material)</c>, dump.cs
/// 262545), and the renderer slots and material references that held the original are pointed at the copy. Every
/// copy is released through the existing despawn/world lifecycle, on every peer.
///
/// A property is written only where <c>Material.HasProperty(string)</c> (262588) says the material has it. A
/// `MaterialSets` entry whose `From` finds no material, a property the named material lacks, and a `full` set
/// property that no material of the type carries are each refused by name; the pass never guesses another property. The glow is not touched:
/// the game drives it through its own glow properties at run time, and this pass writes only the properties named.
/// </summary>
internal sealed partial class EnemyModule
{
    private void ApplyMaterialPass(GameObject prefab, EnemyLookProfile look, uint typeId, string? source, List<Action> undo)
    {
        EnemyMaterialOutcome outcome;
        try { outcome = EnemyMaterialPass.Apply(prefab, look); }
        catch (Exception error) { EnemyLog.Contained(error); outcome = EnemyMaterialOutcome.Crashed(error.GetType().Name); }
        undo.AddRange(outcome.Undo);
        foreach (var (reason, detail) in outcome.Failures)
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, source, reason, detail);
        // The written properties are named in the detail, so a session log says which property the pass wrote.
        if (outcome.Applied is { } applied
            && TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId, "material-pass"))
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.AppearanceApplied, typeId, source, null, "wrote=" + applied.Wrote, new[]
            {
                RuntimeLogValue.Of("copies", applied.Copies), RuntimeLogValue.Of("rebound", applied.Rebound),
                RuntimeLogValue.Of("properties", applied.Properties)
            });
    }
}

/// <summary>What one material pass wrote: the per-type copies, the renderer slots and material references pointed at
/// them, the property writes, and those writes named.</summary>
internal sealed record EnemyMaterialCounts(int Copies, int Rebound, int Properties, string Wrote);

/// <summary>What one material pass came to: the success counts (null when nothing was written) and every named
/// refusal.</summary>
internal sealed class EnemyMaterialOutcome
{
    internal EnemyMaterialCounts? Applied { get; set; }
    internal List<(string Reason, string Detail)> Failures { get; } = new();
    internal List<Action> Undo { get; } = new();

    internal static EnemyMaterialOutcome Crashed(string detail)
    {
        var outcome = new EnemyMaterialOutcome();
        outcome.Failures.Add(("native-error", detail));
        return outcome;
    }
}

internal static class EnemyMaterialPass
{
    private const string InstanceSuffix = " (Instance)";

    /// <summary>Gives every renderer of the instance private copies of its materials and returns the map from each
    /// original material's instance id to the copy that replaced it.</summary>
    internal static Dictionary<int, Material> Isolate(GameObject instance, List<Action> undo)
    {
        var copies = new Dictionary<int, Material>();
        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            var original = renderer.sharedMaterials;
            if (original == null || original.Length == 0) continue;
            var own = new Material[original.Length];
            for (int i = 0; i < own.Length; i++)
            {
                var material = original[i];
                if (material == null) { own[i] = material!; continue; }
                if (!copies.TryGetValue(material.GetInstanceID(), out var copy))
                {
                    copy = new Material(material);
                    copies.Add(material.GetInstanceID(), copy);
                    var resource = copy;
                    undo.Add(() => UnityEngine.Object.DestroyImmediate(resource));
                }
                own[i] = copy;
                var written = copy;
                undo.Add(() => EnemyAppearanceAssembler.RestoreMaterials(renderer, written, original));
            }
            renderer.sharedMaterials = new Il2CppReferenceArray<Material>(own);
        }
        return copies;
    }

    /// <summary>One distinct material the prefab holds, and its per-type copy once a write needs one.</summary>
    private sealed class Slot
    {
        private readonly List<Action> _undo;
        internal Slot(Material original, List<Action> undo) { Original = original; Name = Strip(original.name); _undo = undo; }
        internal Material Original { get; }
        internal string Name { get; }
        internal Material? Copy { get; set; }
        internal Material Writable
        {
            get
            {
                if (Copy != null) return Copy;
                var copy = new Material(Original);
                _undo.Add(() => UnityEngine.Object.DestroyImmediate(copy));
                return Copy = copy;
            }
        }
    }

    internal static EnemyMaterialOutcome Apply(GameObject prefab, EnemyLookProfile look)
    {
        var outcome = new EnemyMaterialOutcome();
        try { Apply(prefab, look, outcome); }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            EnemyAppearanceAssembler.Undo(outcome.Undo);
            outcome.Applied = null;
            outcome.Failures.Add(("native-error", error.GetType().Name));
        }
        return outcome;
    }

    private static void Apply(GameObject prefab, EnemyLookProfile look, EnemyMaterialOutcome outcome)
    {
        var slots = new Dictionary<int, Slot>();
        var order = new List<Slot>();
        var renderers = prefab.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            var shared = renderer.sharedMaterials;
            if (shared == null) continue;
            for (int index = 0; index < shared.Length; index++) Track(shared[index], slots, order, outcome.Undo);
        }
        var handler = prefab.GetComponent<EnemyAgent>()?.MaterialHandler;
        var references = handler?.m_materialRefs;
        if (references != null)
            for (int index = 0; index < references.Count; index++) Track(references[index]?.m_material, slots, order, outcome.Undo);

        int properties = 0;
        // Every written property is named in the success line, so a session log says which material property the
        // pass wrote, not only how many.
        var wrote = new StringBuilder();
        foreach (var set in look.MaterialSets)
        {
            if (set.AllMaterials) { properties += WriteAll(set, order, outcome, wrote); continue; }
            bool found = false;
            foreach (var slot in order)
            {
                if (!string.Equals(slot.Name, set.From, StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                foreach (var (name, value) in set.Floats)
                {
                    if (!slot.Original.HasProperty(name)) { outcome.Failures.Add(("material-property-missing", Describe(slot) + "." + name)); continue; }
                    slot.Writable.SetFloat(name, value);
                    properties++;
                    Wrote(wrote, slot, name);
                }
            }
            if (!found) outcome.Failures.Add(("material-set-from-missing", set.From));
        }

        int copies = 0;
        foreach (var slot in order) if (slot.Copy != null) copies++;
        if (copies == 0) return;

        // The handler belongs to the live enemy, but its list/reference objects may come from a prefab copy.
        // Detach the container before replacing entries; leave the original container and references untouched.
        if (handler != null && references != null)
        {
            var own = new Il2CppSystem.Collections.Generic.List<MaterialRef>();
            for (int i = 0; i < references.Count; i++) own.Add(references[i]);
            handler.m_materialRefs = own;
            references = own;
        }

        var undo = outcome.Undo;
        try
        {
            int rebound = 0;
            foreach (var renderer in renderers)
            {
                var shared = renderer.sharedMaterials;
                if (shared == null || shared.Length == 0) continue;
                var replaced = new Material[shared.Length];
                bool changed = false;
                for (int index = 0; index < shared.Length; index++)
                {
                    var material = shared[index];
                    replaced[index] = material;
                    if (material != null && slots.TryGetValue(material.GetInstanceID(), out var slot) && slot.Copy != null)
                    { replaced[index] = slot.Copy; changed = true; }
                }
                if (!changed) continue;
                var target = renderer;
                foreach (var slot in order)
                    if (slot.Copy != null) undo.Add(() => EnemyAppearanceAssembler.RestoreMaterials(target, slot.Copy, shared));
                renderer.sharedMaterials = new Il2CppReferenceArray<Material>(replaced);
                rebound++;
            }
            if (references != null)
                for (int index = 0; index < references.Count; index++)
                {
                    var reference = references[index];
                    var material = reference?.m_material;
                    if (reference == null || material == null || !slots.TryGetValue(material.GetInstanceID(), out var slot) || slot.Copy == null) continue;
                    int at = index;
                    var copyRef = new MaterialRef
                    {
                        m_material = slot.Copy,
                        m_renderers = reference.m_renderers,
                        m_featureSupport = reference.m_featureSupport,
                        m_hasBeenMadeUnique = true
                    };
                    var ownRefs = references;
                    undo.Add(() => { if (at < ownRefs.Count && ownRefs[at]?.Pointer == copyRef.Pointer) ownRefs[at] = reference; });
                    references[index] = copyRef;
                    rebound++;
                }
            outcome.Applied = new EnemyMaterialCounts(copies, rebound, properties, wrote.ToString());
        }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            EnemyAppearanceAssembler.Undo(undo);
            outcome.Applied = null;
            outcome.Failures.Add(("native-error", error.GetType().Name));
        }
    }

    /// <summary>The `full` set: each property goes on every material that has it. A material without the property
    /// is left as it is (a type's materials do not share one shader); a property no material has is refused by name
    /// with the shaders it looked at, so the author sees why nothing changed.</summary>
    private static int WriteAll(EnemyMaterialSetProfile set, List<Slot> order, EnemyMaterialOutcome outcome, StringBuilder wrote)
    {
        int properties = 0;
        foreach (var (name, value) in set.Floats) properties += WriteEach(order, name, outcome, wrote, material => material.SetFloat(name, value));
        return properties;
    }

    private static int WriteEach(List<Slot> order, string name, EnemyMaterialOutcome outcome, StringBuilder wrote, Action<Material> write)
    {
        int written = 0;
        var without = new StringBuilder();
        foreach (var slot in order)
        {
            if (!slot.Original.HasProperty(name))
            {
                if (without.Length != 0) without.Append(',');
                without.Append(Describe(slot));
                continue;
            }
            write(slot.Writable);
            written++;
            Wrote(wrote, slot, name);
        }
        if (written == 0)
            outcome.Failures.Add(("material-set-property-nowhere", "full." + name + " shaders=" + (without.Length == 0 ? "(no material)" : without.ToString())));
        return written;
    }

    private static void Track(Material? material, Dictionary<int, Slot> slots, List<Slot> order, List<Action> undo)
    {
        if (material == null) return;
        int id = material.GetInstanceID();
        if (slots.ContainsKey(id)) return;
        var slot = new Slot(material, undo);
        slots.Add(id, slot);
        order.Add(slot);
    }

    /// <summary>The material's name as EEC matches it: the ` (Instance)` suffix Unity adds to a runtime copy is not
    /// part of the name an author writes.</summary>
    private static string Strip(string name)
        => name.EndsWith(InstanceSuffix, StringComparison.Ordinal) ? name[..^InstanceSuffix.Length] : name;

    private static void Wrote(StringBuilder wrote, Slot slot, string property)
    {
        if (wrote.Length != 0) wrote.Append(',');
        wrote.Append(Describe(slot)).Append('.').Append(property);
    }

    private static string Describe(Slot slot)
    {
        string shader;
        try { shader = slot.Original.shader?.name ?? "-"; }
        catch (Exception error) { NativeFault.Note("enemy.material.shader-name", error); shader = "?"; }
        return slot.Name + "(" + shader + ")";
    }
}
