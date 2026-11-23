using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeCore;
using ForgeEngine.Framework;
using IRF;
using LevelGeneration;
using UnityEngine;

namespace ForgeEngine.GameBindings;

/// <summary>
/// One process-wide native-model owner. Content registers exact, probe-admitted roots at startup; the level
/// boundary acquires each shard once, all domains clone from that closure, and cleanup destroys every owned copy
/// before releasing each owned shard increment once. The Model Library itself is never scanned or loaded here.
/// </summary>
internal static class NativeModelRuntime
{
    private const string CatalogResource = "ForgeEngine.NativeModelResources.json";
    /// <summary>The name prefix of every object a placement creates, so a host can tell its own renderers from placed ones.</summary>
    public const string CopyPrefix = "ForgeModel.";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NativeModelRegistration> Registrations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ShardLease> Shards = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ResourceState> Resources = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ActiveClosure = new(StringComparer.Ordinal);
    // Placed copies by `owner \0 instance`, then by assembly id: removing one instance (a dying enemy, a regenerated gun) is one lookup.
    private static readonly Dictionary<string, Dictionary<string, OwnedCopy>> Copies = new(StringComparer.Ordinal);
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly Dictionary<(IntPtr Source, string Look), SharedMaterial> SharedLooks = new();
    private static readonly Dictionary<string, SurfaceCopy> SurfaceCopies = new(StringComparer.Ordinal);
    private static HashSet<string>? _nextLevelClosure;
    // The build embeds only the runtime field projection; the generator records the source-byte hash and --check
    // verifies the exact derived bytes. Parsed on first admission, never at type initialisation: BeginLevel/EndLevel
    // must not depend on the catalog, and a catalog defect surfaces as refusal of the content that needed it.
    private static readonly Lazy<IReadOnlyDictionary<string, AdmittedRoute>> Admitted = new(() =>
    {
        using var stream = typeof(NativeModelRuntime).Assembly.GetManifestResourceStream(CatalogResource)
            ?? throw new InvalidDataException("model-probe-catalog-missing");
        return ReadAdmitted(stream);
    });
    private static GameObject? _staging;
    private static bool _levelActive;
    /// <summary>The level's report channel from <see cref="BeginLevel"/>; a placed model's refused appearance is
    /// reported through it instead of destroying the model.</summary>
    private static Action<string>? _levelReport;
    private static long _generation;

    /// <summary>A prepared content registration. Validation has no native or global side effect.</summary>
    public sealed class NativeModelRegistration
    {
        private readonly string _owner;
        private NativeModelRegistration? _previous;
        private bool _committed;
        internal NativeModelRegistration(string owner, NativeModelContent content, Func<bool>? selectedForLevel,
            IReadOnlyDictionary<string, ImportedModelPackage> imported)
        { _owner = owner; Content = content; SelectedForLevel = selectedForLevel; Imported = imported; }
        public NativeModelContent Content { get; }
        internal Func<bool>? SelectedForLevel { get; }
        /// <summary>The imported models this registration's assemblies use, by reference key; one whose file is lost is absent.</summary>
        internal IReadOnlyDictionary<string, ImportedModelPackage> Imported { get; }
        public void Commit()
        {
            lock (Gate)
            {
                if (_levelActive) throw new InvalidOperationException("model-registration-after-level-start: " + _owner);
                if (_committed) throw new InvalidOperationException("model-registration-already-committed: " + _owner);
                Registrations.TryGetValue(_owner, out _previous);
                Registrations[_owner] = this;
                _committed = true;
            }
        }
        /// <summary>The content generation that committed this registration stands: the registration it replaced is let go.</summary>
        public void Settle() { lock (Gate) _previous = null; }
        public void Rollback()
        {
            lock (Gate)
            {
                if (!_committed || !Registrations.TryGetValue(_owner, out var current)
                    || !ReferenceEquals(current, this)) return;
                if (_previous == null) Registrations.Remove(_owner);
                else Registrations[_owner] = _previous;
                _committed = false;
            }
        }
    }

    /// <summary>Checks probe admission and cross-content route agreement without mutating the active registry.</summary>
    public static NativeModelRegistration Prepare(string owner, NativeModelContent content)
        => Prepare(owner, content, null);

    /// <summary>Registers an explicit per-level selection with the same transaction as the probed content.
    /// The selector runs only at BeginLevel, before any shard is acquired, on the native generation thread.</summary>
    public static NativeModelRegistration Prepare(string owner, NativeModelContent content, Func<bool>? selectedForLevel)
        => Prepare(owner, content, selectedForLevel, null);

    /// <summary>The same registration for content that uses imported models: <paramref name="imported"/> holds the verified package of
    /// every one of them whose files are all there, by reference key. A model the content names and no package answers is not an error:
    /// its parts are skipped at placement and named once per level, so one lost file never stops the rest.</summary>
    public static NativeModelRegistration Prepare(string owner, NativeModelContent content, Func<bool>? selectedForLevel,
        IReadOnlyDictionary<string, ImportedModelPackage>? imported)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidDataException("model-owner-invalid");
        if (content == null) throw new ArgumentNullException(nameof(content));
        lock (Gate)
        {
            var routes = new Dictionary<string, NativeModelBinding>(StringComparer.Ordinal);
            foreach (var row in Registrations.Where(row => row.Key != owner).SelectMany(row => row.Value.Content.Bindings))
            {
                if (routes.TryGetValue(row.Resource.Key, out var prior)
                    && (prior.Shard != row.Shard || prior.GameBuild != row.GameBuild))
                    throw new InvalidDataException("model-binding-conflict: " + row.Resource.Id);
                routes[row.Resource.Key] = row;
            }
            foreach (var binding in content.Bindings)
            {
                var parts = content.Assemblies.SelectMany(row => row.Parts)
                    .Where(row => row.Resource.Key == binding.Resource.Key).ToArray();
                if (parts.Length == 0) throw new InvalidDataException("model-binding-unused: " + binding.Resource.Id);
                foreach (var part in parts) Admit(binding, part.Locator, part.Selection);
                if (routes.TryGetValue(binding.Resource.Key, out var prior)
                    && (prior.Shard != binding.Shard || prior.GameBuild != binding.GameBuild))
                    throw new InvalidDataException("model-binding-conflict: " + binding.Resource.Id);
                routes[binding.Resource.Key] = binding;
            }
            return new NativeModelRegistration(owner, content, selectedForLevel,
                imported ?? new Dictionary<string, ImportedModelPackage>());
        }
    }

    public static void Remove(string owner)
    {
        lock (Gate)
        {
            RemoveCopies(owner, "");
            Registrations.Remove(owner);
        }
    }

    /// <summary>
    /// Supplies the exact native resources selected by the next level package. Registration is only availability;
    /// it never implies that an installed weapon, enemy or community package belongs to this level.
    /// </summary>
    public static void ConfigureNextLevelClosure(IEnumerable<NativeModelResourceReference> resources)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        lock (Gate)
        {
            if (_levelActive) throw new InvalidOperationException("model-level-closure-during-level");
            var available = new HashSet<string>(Registrations.Values.SelectMany(row => row.Content.Bindings)
                .Select(row => row.Resource.Key), StringComparer.Ordinal);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in resources)
            {
                if (resource == null || !available.Contains(resource.Key))
                    throw new InvalidDataException("model-level-closure-dangling: " + (resource?.Id ?? "null"));
                closure.Add(resource.Key);
            }
            _nextLevelClosure = closure;
        }
    }

    public static void ClearNextLevelClosure()
    {
        lock (Gate)
        {
            if (_levelActive) throw new InvalidOperationException("model-level-closure-clear-during-level");
            _nextLevelClosure = null;
        }
    }
    /// <summary>Starts the exact registered content closure. Calls are asynchronous only when GTFO must load a shard.</summary>
    public static void BeginLevel(Action<string>? report = null)
    {
        lock (Gate)
        {
            EndLevelCore(report);
            _levelReport = report;
            ForgeVisual.InstancedMeshBatches.ViewReport = ReportDrawView;
            _generation++;
            _levelActive = true;
            ImportedModelPlacement.BeginLevel();
            ActiveClosure.Clear();
            var registered = Registrations.Values.SelectMany(row => row.Content.Assemblies)
                .SelectMany(row => row.Parts).ToArray();
            var closure = _nextLevelClosure;
            _nextLevelClosure = null;
            foreach (var registration in Registrations.Values)
            {
                if (registration.SelectedForLevel == null) continue;
                closure ??= new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (!registration.SelectedForLevel()) continue;
                    foreach (var binding in registration.Content.Bindings) closure.Add(binding.Resource.Key);
                    ImportedModelPlacement.Preload(registration.Imported.Values);
                }
                catch (Exception error)
                {
                    Report(report, "model-level-selection-failed:" + error.GetType().Name);
                    return; // A partial level closure never acquires native resources.
                }
            }
            if (registered.Length != 0 && closure == null)
            {
                Report(report, "model-level-closure-missing");
                return;
            }
            if (closure == null) return;
            var available = new HashSet<string>(registered.Select(row => row.Resource.Key), StringComparer.Ordinal);
            foreach (string key in closure)
                if (available.Contains(key)) ActiveClosure.Add(key);
                else Report(report, "model-level-closure-dangling: " + key);
            var resources = registered.Where(row => ActiveClosure.Contains(row.Resource.Key))
                .GroupBy(row => row.Resource.Key, StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            foreach (var group in resources.GroupBy(part => Binding(part).Shard, StringComparer.Ordinal))
                Acquire(group.Key, group.ToArray(), report);
        }
    }

    /// <summary>Destroys the current level's copies, then balances only pins whose acquisition was proven.</summary>
    public static void EndLevel(Action<string>? report = null)
    {
        lock (Gate) EndLevelCore(report);
    }

    /// <summary>True while an asynchronous shard load still owns a resource this assembly needs, or, for an assembly with imported
    /// models, while the textures the level preloaded are still decoding (level-wide: they decode during the build): a caller waits
    /// for it without building anything, instead of attempting <see cref="Place"/> and reading its loading refusal every frame.</summary>
    public static bool Loading(NativeModelAssembly assembly)
    {
        lock (Gate)
        {
            PollJoined();
            foreach (var part in assembly.Parts)
                if (Resources.TryGetValue(part.Resource.Key, out var resource) && resource.Generation == _generation
                    && resource.Status == ResourceStatus.Loading) return true;
            return assembly.ImportedParts.Count != 0 && _levelActive && ImportedModelPlacement.Decoding();
        }
    }

    /// <summary>
    /// Builds one assembly below an already resolved rigid host. A null return means the copy exists; otherwise the
    /// string is an explicit refusal. Every vanilla collider is stripped from the leaves. The optional prepare callback
    /// runs once per placement with the finished, still inactive placement root and the batched draws of its imported
    /// models (they have no renderer below the root), and owns domain additions such as the enemy's one hit part.
    /// Imported models draw on <paramref name="drawLayer"/> when given (a host whose own layer the
    /// camera does not draw, such as an enemy bone, passes the layer its body renders on), else on the host's layer.
    /// </summary>
    public static string? Place(string owner, NativeModelAssembly assembly, Transform host, string instance,
        Func<GameObject, IReadOnlyList<ForgeVisual.InstancedDraw>, string?>? prepare = null, ForgeVisual.SurfaceAppearance? appearance = null, bool collision = false,
        int? drawLayer = null)
    {
        if (host == null) return "model-host-missing";
        lock (Gate)
        {
            if (!_levelActive) return "model-level-inactive";
            PollJoined();
            if (!Registrations.TryGetValue(owner, out var registration)
                || !registration.Content.Assemblies.Contains(assembly)) return "model-assembly-unregistered";
            var root = new GameObject(CopyPrefix + assembly.Id);
            root.SetActive(false);
            root.transform.SetParent(Staging().transform, false);
            var copy = new OwnedCopy(root);
            var solidLayers = new List<(GameObject Node, int Layer)>();
            try
            {
                foreach (var part in assembly.Parts)
                {
                    if (!ActiveClosure.Contains(part.Resource.Key))
                    { copy.Destroy(); return "model-resource-outside-level-closure:" + part.Resource.Id; }
                    if (!Resources.TryGetValue(part.Resource.Key, out var resource) || resource.Generation != _generation)
                    { copy.Destroy(); return "model-resource-not-prepared:" + part.Resource.Id; }
                    if (resource.Status != ResourceStatus.Ready)
                    { copy.Destroy(); return "model-resource-" + resource.Status.ToString().ToLowerInvariant() + ":" + part.Resource.Id; }
                    var source = Loaded(part.Locator.ResourceName);
                    if (source == null) { copy.Destroy(); return "model-resource-missing:" + part.Resource.Id; }
                    Transform parent = root.transform;
                    for (int index = 0; index < part.Transforms.Count; index++)
                    {
                        var pose = part.Transforms[index];
                        var node = new GameObject(CopyPrefix + "Pose." + index);
                        node.transform.SetParent(parent, false);
                        node.transform.localPosition = Vector(pose.Position);
                        node.transform.localRotation = QuaternionValue(pose.Rotation);
                        node.transform.localScale = Vector(pose.Scale);
                        parent = node.transform;
                    }
                    GameObject clone;
                    Transform? selected;
                    string? refusal;
                    if (part.Selection == null)
                    {
                        clone = UnityEngine.Object.Instantiate<GameObject>(source, parent);
                        selected = Select(clone, null, out refusal);
                    }
                    else selected = CloneSelected(part, source, parent, collision, out clone, out refusal);
                    clone.name = CopyPrefix + string.Join(".", part.AuthorPath);
                    if (selected == null) { copy.Destroy(); return refusal; }
                    var strip = PruneAndStrip(clone, selected, collision, solidLayers);
                    if (strip != null) { copy.Destroy(); return strip + ":" + part.Resource.Id; }
                    PlaceSelectedAtPose(clone.transform, selected);
                    var look = ForgeVisual.SurfaceAppearance.Over(appearance, part.Appearance);
                    if (look != null)
                        Paint(selected.gameObject.GetComponentsInChildren<Renderer>(true), look, copy, instance);
                }
                // Imported models draw without a GameObject each: one batched draw per mesh below this root (camera-facing parts excepted:
                // each is a renderer on its own turning node, in skins). With collision, a model's own
                // collision shapes become collider nodes below the root (added to solidLayers with their layer).
                int nativeColliders = solidLayers.Count;
                var drawn = new List<ForgeVisual.InstancedDraw>(assembly.ImportedParts.Count);
                var skins = new List<GameObject>();
                if (assembly.ImportedParts.Count != 0)
                {
                    var refusal = ImportedModelPlacement.Build(registration.Imported, assembly.ImportedParts, root.transform, skins, appearance, (materials, slotOf, look) =>
                    {
                        var painted = new Material?[materials.Count];
                        bool[]? shadowOnly = null;
                        for (int index = 0; index < painted.Length; index++)
                        {
                            var own = look.ForSlot(slotOf(index));
                            if (own.ShadowOnly) (shadowOnly ??= new bool[painted.Length])[index] = true;
                            painted[index] = own.Empty ? materials[index] : Painted(materials[index], own, copy, instance);
                        }
                        return (painted, shadowOnly);
                    }, drawn, message => Report(_levelReport, message), collision ? solidLayers : null);
                    if (refusal != null) { copy.Destroy(); return refusal; }
                }
                ForgeVisual.Models.SetLayer(root.transform, host.gameObject.layer);
                // A native collider keeps the layer its source room gave it: navigation, culling and player movement
                // read those layers, while the host layer is the placement's hit layer.
                foreach (var (node, layer) in solidLayers) if (node != null) node.layer = layer;
                // A skinned imported model and an imported model's camera-facing parts draw where a batch would.
                if (drawLayer is int skinLayer) foreach (var skin in skins) ForgeVisual.Models.SetLayer(skin.transform, skinLayer);
                if (drawn.Count != 0)
                {
                    // Registered after the layer is set: the batch draws on the anchor's layer unless the caller named one.
                    ForgeVisual.InstancedMeshBatches.Register(root.transform, drawn, drawLayer, instanced: false);
                    copy.Imported = drawn;
                    Report(_levelReport, "model-imported-batched: instance=" + instance + " assembly=" + assembly.Id + " draws=" + drawn.Count);
                }
                if (collision) Report(_levelReport, "model-collision: instance=" + instance + " assembly=" + assembly.Id
                    + " colliders=" + solidLayers.Count + " importedColliders=" + (solidLayers.Count - nativeColliders)
                    + " navigableDefault=" + ForgeCore.StaticColliders.Navigable(LayerManager.LAYER_DEFAULT));
                if (prepare?.Invoke(root, drawn) is { } domainRefusal) { copy.Destroy(); return domainRefusal; }
                root.transform.SetParent(host, false);
                root.SetActive(true);
                string instanceKey = owner + "\0" + instance;
                if (!Copies.TryGetValue(instanceKey, out var placed))
                    Copies.Add(instanceKey, placed = new Dictionary<string, OwnedCopy>(StringComparer.Ordinal));
                if (placed.Remove(assembly.Id, out var previous)) previous.Destroy();
                placed[assembly.Id] = copy;
                return null;
            }
            catch
            {
                copy.Destroy();
                throw;
            }
        }
    }

    /// <summary>
    /// Paints the authored look on every renderer of one placed leaf (or one set of native weapon surfaces), slot by slot: a renderer's slots
    /// are counted over the renderers in order (the website's `selection.materialSlots`). ForgeVisual's `SurfaceLook` builds every material;
    /// the shared vanilla materials (and every other placement) stay untouched. An invisible slot moves to a shadow-only twin of its
    /// renderer, and a renderer whose every slot is invisible draws only its shadow. Runs once per placement.
    /// </summary>
    private static void Paint(IEnumerable<Renderer> renderers, ForgeVisual.SurfaceAppearance look, OwnedMaterials copy, string instance)
    {
        int slot = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            var shared = renderer.sharedMaterials;
            var replaced = new Material[shared.Length];
            bool[]? hidden = null;
            int left = shared.Length;
            for (int index = 0; index < shared.Length; index++, slot++)
            {
                var own = look.ForSlot(slot);
                replaced[index] = shared[index];
                if (own.ShadowOnly) { (hidden ??= new bool[shared.Length])[index] = true; left--; continue; }
                if (shared[index] != null && !own.Empty) replaced[index] = Painted(shared[index], own, copy, instance);
            }
            if (hidden == null) { renderer.sharedMaterials = replaced; continue; }
            if (left == 0) { renderer.sharedMaterials = replaced; copy.ShadowOnly(renderer); continue; }
            var split = ForgeVisual.SurfaceLook.LeaveOutSlots(renderer, replaced, hidden, out string reason);
            if (split == null)
            {
                Report(_levelReport, "model-slot-hide-failed: instance=" + instance + " renderer=" + renderer.name + " " + reason);
                renderer.sharedMaterials = replaced;
                continue;
            }
            copy.Split(renderer, split);
        }
    }

    /// <summary>The look's material for one source: one per (source, look) in the level, shared by every placement and life that paints
    /// the same source the same way, so the count does not grow with the number of copies. The placement holds a reference.</summary>
    private static Material Painted(Material source, ForgeVisual.SurfaceAppearance look, OwnedMaterials copy, string instance)
    {
        var key = (source.Pointer, look.Key());
        if (!SharedLooks.TryGetValue(key, out var entry))
            SharedLooks.Add(key, entry = new SharedMaterial(key, ForgeVisual.SurfaceLook.Paint(source, look)));
        copy.Use(entry);
        return entry.Material;
    }

    /// <summary>
    /// Paints already resolved native surfaces (a weapon's parts, a built room's parts) with the same material path as model attachments.
    /// <paramref name="owner"/> only names the copies; it needs no model registration. Captures original material arrays once and restores
    /// them on reapplication, removal or level exit; never owns the renderers.
    /// This is a construction-time operation, not a tick service. A failed batch restores every surface it touched.
    /// A slot without a material draws nothing and stays empty, so a renderer with no material at all (vanilla building parts carry one)
    /// is left as it is while the rest of the batch is painted.
    /// </summary>
    public static string? PaintSurfaces(string owner, string instance,
        IReadOnlyList<(Renderer Renderer, ForgeVisual.SurfaceAppearance Appearance)> surfaces)
    {
        lock (Gate)
        {
            if (!_levelActive) return "model-level-inactive";
            var seen = new HashSet<IntPtr>();
            foreach (var surface in surfaces)
                if (surface.Renderer == null || surface.Appearance == null || !seen.Add(surface.Renderer.Pointer))
                    return "model-surface-renderer-invalid";
            string key = owner + "\0" + instance;
            foreach (var prior in SurfaceCopies)
                if (prior.Key != key && prior.Value.Originals.Any(row => row.Renderer != null && seen.Contains(row.Renderer.Pointer)))
                    return "model-surface-already-owned";
            if (SurfaceCopies.TryGetValue(key, out var previous))
            { previous.Restore(); SurfaceCopies.Remove(key); }
            if (surfaces.Count == 0) return null;
            var copy = new SurfaceCopy();
            SurfaceCopies.Add(key, copy); // Retain ownership if native restoration itself fails.
            try
            {
                foreach (var surface in surfaces) copy.Originals.Add((surface.Renderer, surface.Renderer.sharedMaterials));
                foreach (var group in surfaces.GroupBy(surface => surface.Appearance))
                    Paint(group.Select(surface => surface.Renderer), group.Key, copy, "surface:" + instance);
                return null;
            }
            catch (Exception error)
            {
                copy.Restore();
                SurfaceCopies.Remove(key);
                return "model-surface-apply-failed:" + error.Message;
            }
        }
    }

    /// <summary>
    /// Darkens one placed copy to factor x its placed base colour (1 restores it). The first call gives each renderer
    /// below the copy its own copy of every material that declares a base colour, unless the placement already owns
    /// it, and records that base colour; later calls only set colours. Nothing here runs per frame.
    /// </summary>
    public static string? Shade(string owner, string instance, string assemblyId, float factor)
    {
        if (!float.IsFinite(factor) || factor < 0f) return "model-shade-factor";
        lock (Gate)
        {
            if (!TryCopy(owner, instance, assemblyId, out var copy))
                return "model-shade-copy-missing";
            if (copy.Shades == null)
            {
                var renderers = copy.Root.GetComponentsInChildren<Renderer>(true);
                bool any = false;
                foreach (var renderer in renderers)
                    if (renderer != null)
                        foreach (var material in renderer.sharedMaterials)
                            any |= material != null && material.HasProperty(ColorId);
                if (!any) return "model-shade-unsupported";
                var shades = new List<(Material Material, Color Base)>();
                var shaded = new Dictionary<IntPtr, Material>();
                foreach (var renderer in renderers)
                {
                    if (renderer == null) continue;
                    var materials = renderer.sharedMaterials;
                    for (int index = 0; index < materials.Length; index++)
                    {
                        var source = materials[index];
                        if (source == null || !source.HasProperty(ColorId)) continue;
                        if (!shaded.TryGetValue(source.Pointer, out var material))
                        {
                            material = copy.Owns(source) ? source : copy.Own(new Material(source) { name = source.name + " (Forge shade)" });
                            shaded.Add(source.Pointer, material);
                            shades.Add((material, material.GetColor(ColorId)));
                        }
                        materials[index] = material;
                    }
                    renderer.sharedMaterials = materials;
                }
                copy.Shades = shades;
            }
            foreach (var (material, colour) in copy.Shades)
                material.SetColor(ColorId, new Color(colour.r * factor, colour.g * factor, colour.b * factor, colour.a));
            return null;
        }
    }

    /// <summary>The box of one placed copy's imported models (their batched draws have no renderer to ask) in <paramref name="frame"/>'s own
    /// frame (null: the world), or false when the copy holds none.</summary>
    public static bool TryImportedBounds(string owner, string instance, string assemblyId, out Bounds bounds, Transform? frame = null)
    {
        bounds = default;
        lock (Gate)
            return TryCopy(owner, instance, assemblyId, out var copy) && copy.Imported != null
                && ImportedModelPlacement.TryBounds(copy.Root.transform, copy.Imported, frame, out bounds);
    }

    /// <summary>What the look materials one placed copy holds read back as (renderer slots and imported draws alike): each flat colour in
    /// the author's sRGB and its alpha, for a record that compares them with what was written. Empty when the copy is not placed or
    /// painted nothing.</summary>
    public static (float[] Srgb, float Alpha)[] PaintedTints(string owner, string instance, string assemblyId)
    {
        lock (Gate)
        {
            var read = new List<(float[] Srgb, float Alpha)>();
            if (TryCopy(owner, instance, assemblyId, out var copy))
                foreach (var material in copy.Held.Where(material => material != null).Distinct<Material>(ForgeCore.NativeObjects.Identity))
                    if (ForgeVisual.SurfaceLook.ReadTint(material, out var srgb, out var alpha)) read.Add((srgb, alpha));
            return read.ToArray();
        }
    }

    /// <summary>Live placed copies and the material copies they own; read by the development leak check.</summary>
    public static int CopyCount { get { lock (Gate) return Copies.Values.Sum(placed => placed.Count); } }
    public static int OwnedMaterialCount
    {
        get
        {
            lock (Gate) return Copies.Values.SelectMany(placed => placed.Values).Sum(copy => copy.Materials.Count)
        + SurfaceCopies.Values.Sum(copy => copy.Materials.Count) + SharedLooks.Count;
        }
    }
    public static int SurfaceCopyCount { get { lock (Gate) return SurfaceCopies.Count; } }

    private static bool TryCopy(string owner, string instance, string assemblyId, out OwnedCopy copy)
    {
        copy = null!;
        return Copies.TryGetValue(owner + "\0" + instance, out var placed) && placed.TryGetValue(assemblyId, out copy!);
    }

    /// <summary>Removes the copies and painted surfaces of one exact <paramref name="instance"/> of <paramref name="owner"/>, or of every
    /// instance when it is empty.</summary>
    public static void RemoveCopies(string owner, string instance)
    {
        lock (Gate)
        {
            if (instance.Length != 0) { RemoveInstance(owner + "\0" + instance); return; }
            string prefix = owner + "\0";
            foreach (var key in Copies.Keys.Concat(SurfaceCopies.Keys).Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                RemoveInstance(key);
        }
    }

    // Each copy leaves its table only once it is destroyed, so a failed native destroy keeps the rest owned.
    private static void RemoveInstance(string key)
    {
        if (Copies.TryGetValue(key, out var placed))
        {
            foreach (var assembly in placed.Keys.ToArray()) { placed[assembly].Destroy(); placed.Remove(assembly); }
            Copies.Remove(key);
        }
        if (SurfaceCopies.TryGetValue(key, out var surface)) { surface.Restore(); SurfaceCopies.Remove(key); }
    }

    /// <summary>The copy of one native leaf inside a placement root <see cref="Place"/> built (the instantiated source root
    /// when the leaf has no selection), or null. A domain's prepare callback uses it to reach the copy's own hierarchy.
    /// The placement root itself is never a leaf: it is named after the assembly id, which an author may also have
    /// given the assembly's one part.</summary>
    public static Transform? PlacedPartRoot(GameObject placement, NativeModelPart part)
    {
        string name = CopyPrefix + string.Join(".", part.AuthorPath);
        Transform? found = null;
        var root = placement.transform;
        foreach (var node in placement.GetComponentsInChildren<Transform>(true))
        {
            if (node == null || node.Pointer == root.Pointer || node.name != name) continue;
            if (found != null) return null;
            found = node;
        }
        return found;
    }

    /// <summary>The child path segment standing for the <c>LG_PrefabSpawner.m_prefab</c> hop: the path before it ends at a
    /// spawner object, the path after it starts at the root of the prefab that spawner references. Its index is 0.</summary>
    public const string SpawnedPrefabSegment = "LG_PrefabSpawner.m_prefab";

    /// <summary>
    /// <see cref="ResolveChild"/> across at most one spawner hop. At the hop it reads the spawner's <c>m_prefab</c> and asks
    /// <paramref name="prefabRoot"/> for an instance of it (cached by the runtime, throwaway in the probe) with the key of the
    /// spawner path; the rest of the path resolves inside that instance. <paramref name="treeRoot"/> is the root the returned
    /// node hangs under, <paramref name="root"/> without a hop.
    /// </summary>
    public static Transform? ResolveSelection(Transform root, IReadOnlyList<string> path, IReadOnlyList<int>? indices,
        Func<GameObject, string, Transform> prefabRoot, out Transform treeRoot, out string? refusal)
    {
        treeRoot = root;
        int hop = path.ToList().IndexOf(SpawnedPrefabSegment);
        if (hop < 0) return ResolveChild(root, path, indices, out refusal);
        var spawner = ResolveChild(root, path.Take(hop).ToArray(), indices?.Take(hop).ToArray(), out refusal);
        if (spawner == null) return null;
        var prefab = spawner.GetComponent<LG_PrefabSpawner>()?.m_prefab;
        if (prefab == null) { refusal = "model-spawner-prefab-missing"; return null; }
        treeRoot = prefabRoot(prefab, string.Join("/", path.Take(hop)) + "#" + string.Join(",", indices?.Take(hop) ?? Array.Empty<int>()));
        return ResolveChild(treeRoot, path.Skip(hop + 1).ToArray(), indices?.Skip(hop + 1).ToArray(), out refusal);
    }

    /// <summary>Resolves each direct child by exact sibling index and name, or by a unique name when no index exists.</summary>
    public static Transform? ResolveChild(Transform root, IReadOnlyList<string> path, IReadOnlyList<int>? indices,
        out string? refusal)
    {
        refusal = null;
        if (indices != null && indices.Count != path.Count) { refusal = "model-child-indices"; return null; }
        var node = root;
        for (int depth = 0; depth < path.Count; depth++)
        {
            Transform? found = null;
            if (indices != null)
            {
                int index = indices[depth];
                if (index < 0 || index >= node.childCount) { refusal = "model-child-index-missing:" + depth; return null; }
                found = node.GetChild(index);
                if (found == null || found.name != path[depth]) { refusal = "model-child-index-name:" + depth; return null; }
            }
            else
            {
                for (int index = 0; index < node.childCount; index++)
                {
                    var candidate = node.GetChild(index);
                    if (candidate == null || candidate.name != path[depth]) continue;
                    if (found != null) { refusal = "model-child-ambiguous:" + depth; return null; }
                    found = candidate;
                }
                if (found == null) { refusal = "model-child-missing:" + depth; return null; }
            }
            node = found;
        }
        return node;
    }

    private static void Acquire(string shard, NativeModelPart[] parts, Action<string>? report)
    {
        var lease = new ShardLease(shard, _generation, parts.Select(part => part.Resource.Key).Distinct(StringComparer.Ordinal).ToArray(), parts);
        Shards.Add(shard, lease);
        foreach (string id in lease.Resources) Resources[id] = new ResourceState(_generation, ResourceStatus.Loading);
        Start(lease, report);
    }

    /// <summary>Takes this level's pin on the lease's shard, or waits for a load already under way. A positive count without a
    /// manifest is somebody else's load in flight (the level's own bundle load, or one an earlier level of this process started and
    /// left before it finished): a second native load would reset the count to one, so it is joined, never restarted, and
    /// <see cref="PollJoined"/> pins the shard once its manifest registers. Its resources stay loading meanwhile, so a placement
    /// waits instead of being refused.</summary>
    private static void Start(ShardLease lease, Action<string>? report)
    {
        string shard = lease.Shard; var parts = lease.Parts;
        try
        {
            var before = GameAssets.ReadShard(shard);
            lease.Before = before;
            if (before.Registered)
            {
                lease.Joined = false;
                GameAssets.IncreaseShardDependency(shard);
                var after = GameAssets.ReadShard(shard);
                lease.Owned = before.SameRegistration(after) && before.Count > 0 && after.Count == before.Count + 1;
                if (!lease.Owned) { Fail(lease, "model-shard-pin-unproven", report); return; }
                Complete(lease, parts, report);
                return;
            }
            if (before.Count > 0)
            {
                if (!lease.Joined) Report(report, "model-shard-joined: shard=" + shard + " count=" + before.Count);
                lease.Joined = true;
                return;
            }
            lease.Joined = false;
            bool started = false, callReturned = false, callbackPending = false;
            Action<AsyncOperation> loaded = _ =>
            {
                lock (Gate)
                {
                    if (!callReturned) { callbackPending = true; return; }
                    try { FinishLoad(lease, parts, started, report); }
                    catch (Exception error) { FailOrReport(lease, "model-shard-callback:" + error.GetType().Name, report); }
                }
            };
            started = GameAssets.LoadShard(shard, loaded);
            lease.LoadStarted = started;
            callReturned = true;
            if (callbackPending)
            {
                FinishLoad(lease, parts, started, report);
                return;
            }
            if (!started)
            {
                var after = GameAssets.ReadShard(shard);
                lease.Owned = before.Registered && before.SameRegistration(after) && after.Count == before.Count + 1;
                // Native queues the callback for this path; it is the only place resources become ready.
                if (!lease.Owned) Fail(lease, "model-shard-existing-pin-unproven", report);
            }
        }
        catch (Exception error) { Fail(lease, "model-shard-load:" + error.GetType().Name, report); }
    }

    /// <summary>Retries every joined lease of this level: called under <see cref="Gate"/> by the placement queries, which a waiting
    /// placement repeats until its resources are ready. No shard state is read while no lease is joined.</summary>
    private static void PollJoined()
    {
        if (!_levelActive) return;
        foreach (var lease in Shards.Values)
            if (lease.Joined && !lease.Ended && lease.Failure == null) Start(lease, _levelReport);
    }

    private static void FinishLoad(ShardLease lease, NativeModelPart[] parts, bool started, Action<string>? report)
    {
        var after = GameAssets.ReadShard(lease.Shard);
        lease.Owned = started && after.Registered && after.Count > 0;
        if (!lease.Owned) { FailOrReport(lease, "model-shard-load-unproven", report); return; }
        if (!Current(lease)) { Release(lease, report); return; }
        Complete(lease, parts, report);
    }

    private static bool Current(ShardLease lease)
        => _levelActive && !lease.Ended && lease.Generation == _generation;

    private static void FailOrReport(ShardLease lease, string reason, Action<string>? report)
    {
        if (Current(lease)) { Fail(lease, reason, report); return; }
        Report(report, reason + ": shard=" + lease.Shard);
    }

    /// <summary>A report callback that throws must not change model loading, and its message must not vanish: it goes to
    /// the process error stream with the reporter's exception type.</summary>
    internal static void ReportDrawView(string message) => Report(_levelReport, message);

    private static void Report(Action<string>? report, string message)
    {
        try { report?.Invoke(message); }
        catch (Exception reporter) { Console.Error.WriteLine("Forge native model report failed (" + reporter.GetType().Name + "): " + message); }
    }

    private static void Complete(ShardLease lease, NativeModelPart[] parts, Action<string>? report)
    {
        foreach (var part in parts)
        {
            if (Loaded(part.Locator.ResourceName) == null)
            { Fail(lease, "model-native-name-missing:" + part.Locator.ResourceName, report); return; }
        }
        foreach (string id in lease.Resources) Resources[id] = new ResourceState(lease.Generation, ResourceStatus.Ready);
    }

    private static void Fail(ShardLease lease, string reason, Action<string>? report)
    {
        lease.Failure = reason;
        foreach (string id in lease.Resources) Resources[id] = new ResourceState(lease.Generation, ResourceStatus.Failed);
        Report(report, reason + ": shard=" + lease.Shard);
    }

    private static void EndLevelCore(Action<string>? report)
    {
        foreach (var placed in Copies.Values) foreach (var copy in placed.Values) copy.Destroy();
        Copies.Clear();
        foreach (var copy in SurfaceCopies.Values) copy.Restore();
        SurfaceCopies.Clear();
        foreach (var shared in SharedLooks.Values) Destroy(shared.Material);
        SharedLooks.Clear();
        ImportedModelPlacement.EndLevel();
        foreach (var lease in Shards.Values)
        {
            if (lease.Ended) continue;
            lease.Ended = true;
            Release(lease, report);
        }
        Shards.Clear(); Resources.Clear(); ActiveClosure.Clear(); SourceInstances.Clear(); _levelActive = false; _levelReport = null; ForgeVisual.InstancedMeshBatches.ViewReport = null;
        if (_staging != null) { Destroy(_staging); _staging = null; }
    }

    private static void Release(ShardLease lease, Action<string>? report)
    {
        if (!lease.Owned || lease.Released) return;
        lease.Released = true;
        try { GameAssets.UnloadShard(lease.Shard, "Forge model level closure " + lease.Generation); }
        catch (Exception error)
        { Report(report, "model-shard-release:" + lease.Shard + ":" + error.GetType().Name); }
    }

    private static NativeModelBinding Binding(NativeModelPart part)
    {
        foreach (var registration in Registrations.Values)
            foreach (var binding in registration.Content.Bindings)
                if (binding.Resource.Key == part.Resource.Key) return binding;
        throw new InvalidDataException("model-binding-missing: " + part.Resource.Id);
    }

    private static GameObject? Loaded(string resourceName) => GameAssets.Get<GameObject>(resourceName);

    private static Transform? Select(GameObject clone, NativeModelSelection? selection, out string? refusal)
    {
        refusal = null;
        return selection == null ? clone.transform
            : ResolveChild(clone.transform, selection.ChildPath, selection.ChildIndices, out refusal);
    }

    /// <summary>
    /// Keeps the selected leaf's presentation and drops everything else. With `collision` the leaf also keeps its
    /// solid colliders: the enabled non-trigger colliders on the leaf and below it, plus the collision siblings the
    /// game's room prefabs pair with a `g_`/`d_` mesh node (`c_&lt;name&gt;` and `cs_&lt;name&gt;`, with a trailing
    /// numeric index of the name ignored). A leaf with no collider at all gets one built from its own mesh. Each kept
    /// collider's layer is appended to `layers`. A component outside the presentation and collider whitelist below the
    /// leaf refuses the placement (`model-component-not-allowed`); nothing stateful is stripped silently. Returns null
    /// on success, else the refusal code.
    /// </summary>
    private static string? PruneAndStrip(GameObject root, Transform selected, bool collision, List<(GameObject Node, int Layer)> layers)
    {
        var donors = new HashSet<IntPtr>();
        if (collision)
        {
            if (NotAllowedBelow(selected) is { } blocked) return "model-component-not-allowed:" + blocked;
            // The clone holds only the collision siblings CloneSelected copied beside the leaf.
            if (selected.parent != null)
                foreach (var sibling in Children(selected.parent))
                    if (sibling.Pointer != selected.Pointer && IsCollisionName(sibling.name)) donors.Add(sibling.Pointer);
        }
        var keep = new HashSet<IntPtr>();
        for (var node = selected; node != null; node = node.parent)
        { keep.Add(node.Pointer); if (node == root.transform) break; }
        var kept = new List<Collider>();
        foreach (var component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            var transform = component.TryCast<Transform>();
            if (transform != null)
            {
                bool ancestor = keep.Contains(transform.Pointer);
                bool descendant = IsDescendant(selected, transform);
                if (!ancestor && !descendant && !donors.Contains(transform.Pointer)) Destroy(transform.gameObject);
                continue;
            }
            var node = component.transform;
            if (collision && (donors.Contains(node.Pointer) || IsDescendant(selected, node))
                && component.TryCast<Collider>() is { } collider)
            {
                if (SolidCollider(collider)) { kept.Add(collider); continue; }
                Destroy(component);
                continue;
            }
            bool onAncestor = keep.Contains(node.Pointer) && node.Pointer != selected.Pointer;
            if (onAncestor || donors.Contains(node.Pointer) || !Presentation(component)) Destroy(component);
        }
        // DestroyImmediate refuses a component another one still requires (a [RequireComponent] dependency such as an Animator
        // under a character script of a whole enemy prefab) and leaves it in place; the dependent is gone after the pass above,
        // so what is left is destroyed again until a pass removes nothing. A component that still stays refuses by its type name.
        for (var pass = 0; ; pass++)
        {
            string? leftover = null;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.TryCast<Transform>() != null || Presentation(component)
                    || collision && component.TryCast<Collider>() != null) continue;
                leftover ??= component.GetIl2CppType().Name;
                if (pass < MaxStripPasses) Destroy(component);
            }
            if (leftover == null) break;
            if (pass == MaxStripPasses) return "model-component-unsupported:" + leftover;
        }
        if (collision && kept.Count == 0 && MeshColliders(selected, kept) is { } meshRefusal) return meshRefusal;
        foreach (var collider in kept) layers.Add((collider.gameObject, collider.gameObject.layer));
        return null;
    }

    /// <summary>Dependency depth a prefab's components may chain through before the strip gives up and names the survivor.</summary>
    private const int MaxStripPasses = 4;

    private static readonly HashSet<string> CollisionAllowed = new(StringComparer.Ordinal)
    {
        // Presentation and collision data the rebuild keeps or drops by rule; the two render helpers are stateless
        // draw-order and instancing flags of the source room and have no meaning outside it.
        "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "LODGroup", "BoxCollider", "SphereCollider",
        "CapsuleCollider", "MeshCollider", "ColliderMaterial", "EarlyTransparentRenderer", "InstancedRenderFeature",
    };

    private static string? NotAllowedBelow(Transform selected)
    {
        foreach (var component in selected.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            string name = component.GetIl2CppType().Name;
            if (!CollisionAllowed.Contains(name)) return name;
        }
        return null;
    }

    private static bool SolidCollider(Collider collider)
    {
        if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeSelf) return false;
        string name = collider.gameObject.name;
        return !name.Contains("shadow", StringComparison.OrdinalIgnoreCase) && !name.Contains("Invwall", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCollisionName(string name)
        => name.StartsWith("c_", StringComparison.Ordinal) || name.StartsWith("cs_", StringComparison.Ordinal);

    /// <summary>The collision siblings of a source leaf, or the refusal when the leaf's name stem is not unique among its siblings.</summary>
    private static string? CollisionDonors(Transform selected, List<Transform> donors)
    {
        var parent = selected.parent;
        string name = selected.name;
        if (parent == null || name.Length < 3 || (name[0] != 'g' && name[0] != 'd') || name[1] != '_') return null;
        string stem = TrimIndex(name[2..]);
        foreach (var sibling in Children(parent))
        {
            if (sibling.Pointer == selected.Pointer) continue;
            string other = sibling.name;
            if (other.Length > 2 && other[0] == name[0] && other[1] == '_' && TrimIndex(other[2..]) == stem)
                return "model-collision-donor-ambiguous";
        }
        foreach (var sibling in Children(parent))
        {
            string other = sibling.name;
            int cut = other.StartsWith("cs_", StringComparison.Ordinal) ? 3 : other.StartsWith("c_", StringComparison.Ordinal) ? 2 : 0;
            if (cut != 0 && other[cut..].StartsWith(stem, StringComparison.Ordinal)) donors.Add(sibling);
        }
        return null;
    }

    private sealed record SourceInstance(GameObject Root, int Nodes);
    private static readonly Dictionary<string, SourceInstance> SourceInstances = new(StringComparer.Ordinal);

    /// <summary>
    /// The level's one unactivated instance of a source resource (a whole room prefab is thousands of nodes). Every
    /// part cut from that resource clones only its subtree out of this instance; EndLevel destroys it with the staging
    /// root. Logs the node count, the build time and the engine allocation delta of each first instantiation.
    /// </summary>
    private static SourceInstance SourceOf(NativeModelPart part, GameObject source, string spawnerKey = "")
    {
        // A spawned prefab is cached beside its root under the spawner path that references it.
        string key = spawnerKey.Length == 0 ? part.Resource.Key : part.Resource.Key + "\0" + spawnerKey;
        if (SourceInstances.TryGetValue(key, out var known) && known.Root != null) return known;
        long before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var root = UnityEngine.Object.Instantiate<GameObject>(source, Staging().transform);
        root.name = CopyPrefix + "Source." + part.Resource.Id + (spawnerKey.Length == 0 ? "" : ".Spawned");
        int nodes = root.GetComponentsInChildren<Transform>(true).Length;
        var made = new SourceInstance(root, nodes);
        SourceInstances[key] = made;
        Report(_levelReport, "model-source-cached: resource=" + part.Resource.Id + " name=" + part.Locator.ResourceName
            + (spawnerKey.Length == 0 ? "" : " spawned=" + spawnerKey) + " nodes=" + nodes
            + " ms=" + ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            + " allocatedDeltaBytes=" + (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - before));
        return made;
    }

    /// <summary>
    /// Clones one selected leaf (and, with collision, its collision siblings) out of the level's cached source instance
    /// under bare ancestor nodes that mirror the source chain's local poses. Ancestors never keep a component, so the
    /// mirror is equivalent to pruning a full clone. Always returns a clone root; the leaf is null with `refusal` set when
    /// the selection or donor rule fails.
    /// </summary>
    private static Transform? CloneSelected(NativeModelPart part, GameObject source, Transform parent, bool collision,
        out GameObject clone, out string? refusal)
    {
        clone = new GameObject(CopyPrefix + "Clone");
        clone.transform.SetParent(parent, false);
        var cached = SourceOf(part, source);
        var leaf = ResolveSelection(cached.Root.transform, part.Selection!.ChildPath, part.Selection.ChildIndices,
            (prefab, key) => SourceOf(part, prefab, key).Root.transform, out var treeRoot, out refusal);
        if (leaf == null) return null;
        var donors = new List<Transform>();
        if (collision && CollisionDonors(leaf, donors) is { } donorRefusal) { refusal = donorRefusal; return null; }
        var chain = new List<Transform>();
        for (var node = leaf.parent; node != null && node.Pointer != treeRoot.Pointer; node = node.parent) chain.Insert(0, node);
        var mirror = clone.transform;
        foreach (var node in chain)
        {
            var copy = new GameObject(node.name);
            copy.transform.SetParent(mirror, false);
            copy.transform.localPosition = node.localPosition;
            copy.transform.localRotation = node.localRotation;
            copy.transform.localScale = node.localScale;
            mirror = copy.transform;
        }
        // The leaf's mirrored parent holds the leaf and its donors; the leaf is found again by its sibling index below.
        var leafClone = UnityEngine.Object.Instantiate<GameObject>(leaf.gameObject, mirror);
        leafClone.name = leaf.name;
        if (BakeInstancedFeatures(leaf, leafClone.transform) is { } bakeRefusal) { refusal = bakeRefusal; return null; }
        foreach (var donor in donors) UnityEngine.Object.Instantiate<GameObject>(donor.gameObject, mirror).name = donor.name;
        refusal = null;
        return leafClone.transform;
    }

    /// <summary>
    /// The enabled instanced render features at and below <paramref name="leaf"/> as plain draws. The game's rooms draw these
    /// models only through the feature component (no MeshRenderer exists to clone), so a placement draws the same mesh and
    /// materials per instance through InstancedMeshBatches (chunked GPU instancing), at the instance's pose (parent, local position, forward/up look
    /// rotation, uniform size) expressed in the leaf's frame. The feature's wind and spring motion is not reproduced.
    /// A feature without a mesh, or an instance of size zero, contributes nothing.
    /// </summary>
    public static List<ForgeVisual.InstancedDraw>? InstancedDraws(Transform leaf, out string? refusal)
    {
        refusal = null;
        var draws = new List<ForgeVisual.InstancedDraw>();
        var toLeaf = leaf.worldToLocalMatrix;
        foreach (var feature in leaf.GetComponentsInChildren<InstancedRenderFeature>(true))
        {
            if (feature == null || !feature.enabled) continue;
            var descriptor = feature.m_descriptor;
            var mesh = descriptor?.Mesh;
            var list = feature.Instances;
            if (mesh == null || list == null) continue;
            var materials = descriptor!.Materials?.ToArray() ?? Array.Empty<Material>();
            for (int index = 0; index < list.Count; index++)
            {
                var instance = list[index];
                if (instance == null || Mathf.Abs(instance.Size) < 1e-7f) continue;
                var parent = instance.Parent != null ? instance.Parent : feature.transform;
                var forward = instance.LocalForward;
                var rotation = forward.sqrMagnitude < 1e-14f ? Quaternion.identity : Quaternion.LookRotation(forward, instance.LocalUp);
                draws.Add(new ForgeVisual.InstancedDraw(mesh, materials,
                    toLeaf * parent.localToWorldMatrix * Matrix4x4.TRS(instance.LocalPosition, rotation, Vector3.one * instance.Size)));
            }
        }
        return draws;
    }

    /// <summary>Hands the instances of every instanced feature the source leaf holds to the batched draw, anchored on the cloned leaf.</summary>
    private static string? BakeInstancedFeatures(Transform sourceLeaf, Transform leafClone)
    {
        var draws = InstancedDraws(sourceLeaf, out var refusal);
        if (draws == null) return refusal;
        if (draws.Count == 0) return null;
        ForgeVisual.InstancedMeshBatches.Register(leafClone, draws);
        Report(_levelReport, "model-instanced-batched: leaf=" + sourceLeaf.name + " instances=" + draws.Count);
        return null;
    }

    /// <summary>The name without one trailing `_&lt;digits&gt;` group, so `g_Table_b_01` and `c_Table_b_02` share a stem.</summary>
    private static string TrimIndex(string name)
    {
        int end = name.Length;
        int digits = end;
        while (digits > 0 && char.IsDigit(name[digits - 1])) digits--;
        return digits < end && digits > 0 && name[digits - 1] == '_' ? name[..(digits - 1)] : name;
    }

    private static IEnumerable<Transform> Children(Transform parent)
    {
        for (int index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child != null) yield return child;
        }
    }

    /// <summary>The leaf has no collider of its own and no collision sibling: its first-level-of-detail meshes become the
    /// collision, a MeshCollider on a readable mesh and the mesh's bounds box otherwise. Null on success.</summary>
    private static string? MeshColliders(Transform selected, List<Collider> kept)
    {
        foreach (var filter in selected.GetComponentsInChildren<MeshFilter>(false))
        {
            var mesh = filter == null ? null : filter.sharedMesh;
            var renderer = filter == null ? null : filter.GetComponent<MeshRenderer>();
            if (mesh == null || renderer == null || !renderer.enabled || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
            string name = filter!.gameObject.name;
            if (name.Contains("shadow", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, "LOD[1-9]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (mesh.isReadable)
            {
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                kept.Add(collider);
            }
            else
            {
                var box = filter.gameObject.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center; box.size = mesh.bounds.size;
                kept.Add(box);
            }
            Report(_levelReport, "model-collision-from-mesh: node=" + name + " mesh=" + mesh.name + " readable=" + mesh.isReadable);
        }
        return kept.Count == 0 ? "model-collision-none" : null;
    }

    /// <summary>
    /// A model is authored and previewed in the selected leaf's own frame: the exported mesh carries no placement of the
    /// prefab it came from. The instantiated prefab keeps its own placement (a scene prefab root can sit metres away and
    /// be turned), so the clone root is given the inverse of the root-to-leaf chain and the leaf lands exactly on the
    /// authored pose node. The pose chain and the leaf's own subtree are left alone.
    /// </summary>
    private static void PlaceSelectedAtPose(Transform root, Transform selected)
    {
        var chain = new List<Transform>();
        for (var node = selected; node != null && node.Pointer != root.Pointer; node = node.parent) chain.Insert(0, node);
        Vector3 position = Vector3.zero, scale = Vector3.one;
        Quaternion rotation = Quaternion.identity;
        foreach (var node in chain)
        {
            position += rotation * Vector3.Scale(scale, node.localPosition);
            rotation *= node.localRotation;
            scale = Vector3.Scale(scale, node.localScale);
        }
        var inverseScale = new Vector3(Inverse(scale.x), Inverse(scale.y), Inverse(scale.z));
        var inverseRotation = Quaternion.Inverse(rotation);
        root.localRotation = inverseRotation;
        root.localScale = inverseScale;
        root.localPosition = -(inverseRotation * Vector3.Scale(inverseScale, position));
    }

    private static float Inverse(float value) => Mathf.Abs(value) < 1e-6f ? 1f : 1f / value;

    private static bool IsDescendant(Transform root, Transform candidate)
    {
        for (var node = candidate; node != null; node = node.parent) if (node.Pointer == root.Pointer) return true;
        return false;
    }

    private static bool Presentation(Component component)
        => component.TryCast<MeshFilter>() != null || component.TryCast<MeshRenderer>() != null
            || component.TryCast<SkinnedMeshRenderer>() != null || component.TryCast<LODGroup>() != null;

    private static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
    private static Quaternion QuaternionValue(float[] value) => new(value[0], value[1], value[2], value[3]);
    private static void Destroy(UnityEngine.Object value) { if (value != null) UnityEngine.Object.DestroyImmediate(value); }

    private static GameObject Staging()
    {
        if (_staging != null) return _staging;
        _staging = new GameObject("ForgeModel.Staging");
        _staging.SetActive(false); _staging.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(_staging);
        return _staging;
    }

    private static void Admit(NativeModelBinding binding, NativeModelLocator locator, NativeModelSelection? selection)
    {
        string supportedBuild = "GameAssembly:sha256:" + GameRuntimeBridge.GameAssemblySha256.ToLowerInvariant();
        if (binding.GameBuild != supportedBuild)
            throw new InvalidDataException("model-probe-build-mismatch: " + binding.GameBuild);
        if (!Admitted.Value.TryGetValue(binding.Resource.Key, out var probe)
            || probe.Shard != binding.Shard || probe.GameBuild != binding.GameBuild
            || probe.ResourceName != locator.ResourceName
            || !probe.Selections.Contains(SelectionKey(selection)))
            throw new InvalidDataException("model-probe-unverified: " + binding.Resource.Id);
    }

    internal static IReadOnlyDictionary<string, AdmittedRoute> ReadAdmitted(Stream stream)
    {
        var result = new Dictionary<string, AdmittedRoute>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != "forge-native-resource-names"
            || !ForgeCore.FormatVersion.Matches(root))
            throw new InvalidDataException("model-probe-catalog-version");
        foreach (var resource in root.GetProperty("resources").EnumerateArray())
        {
            string resourceId = resource.GetProperty("id").GetString()
                ?? throw new InvalidDataException("model-probe-resource-id");
            var locatorValue = resource.GetProperty("locator");
            var locator = NativeModelContent.ReadLocator(locatorValue);
            var families = resource.GetProperty("modelFamilies");
            if (families.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("model-probe-families: " + resourceId);
            foreach (var family in families.EnumerateArray())
            {
                if (!family.TryGetProperty("runtimeReady", out var ready) || ready.ValueKind != JsonValueKind.True) continue;
                if (!family.TryGetProperty("runtimeBinding", out var bindingValue)
                    || bindingValue.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("model-probe-binding-missing: " + resourceId);
                ResourceDefinitions.VerifyNativeReference(bindingValue.GetProperty("resource"), locatorValue);
                var binding = NativeModelContent.ReadBinding(bindingValue);
                if (binding.Resource.Id != resourceId)
                    throw new InvalidDataException("model-probe-resource-mismatch: " + resourceId);
                var selection = NativeModelContent.ReadSelection(family.GetProperty("selection"), binding.Resource.Revision);
                if (!result.TryGetValue(binding.Resource.Key, out var probe))
                {
                    probe = new AdmittedRoute(binding.Shard, binding.GameBuild, locator.ResourceName);
                    result.Add(binding.Resource.Key, probe);
                }
                else if (probe.Shard != binding.Shard || probe.GameBuild != binding.GameBuild || probe.ResourceName != locator.ResourceName)
                    throw new InvalidDataException("model-probe-binding-conflict: " + resourceId);
                // Admission is the set of selections that have a spawn entry. Several families (an architecture piece and a gear
                // part, say) may name the same native root, so a repeated selection is already admitted, not a conflict.
                probe.Selections.Add(SelectionKey(selection));
            }
        }
        return result;
    }

    private static string SelectionKey(NativeModelSelection? selection) => JsonSerializer.Serialize(selection);

    internal sealed class AdmittedRoute
    {
        internal AdmittedRoute(string shard, string gameBuild, string resourceName)
        { Shard = shard; GameBuild = gameBuild; ResourceName = resourceName; }
        internal string Shard { get; }
        internal string GameBuild { get; }
        internal string ResourceName { get; }
        internal HashSet<string> Selections { get; } = new(StringComparer.Ordinal);
    }
    /// <summary>One painted material of the level, keyed by its source and look; destroyed when its last user lets go.</summary>
    private sealed class SharedMaterial
    {
        internal SharedMaterial((IntPtr, string) key, Material material) { Key = key; Material = material; }
        internal (IntPtr, string) Key { get; }
        internal Material Material { get; }
        internal int Users;
    }

    /// <summary>What one placement (or one set of native weapon surfaces) holds: its references to shared painted materials, the
    /// materials only it owns (a darkened copy), and the meshes it split to leave slots out.</summary>
    private abstract class OwnedMaterials
    {
        private readonly HashSet<IntPtr> _owned = new();
        private readonly List<SharedMaterial> _shared = new();
        internal List<Material> Materials { get; } = new();
        internal List<(Renderer Renderer, ForgeVisual.SurfaceSplit Split)> Splits { get; } = new();
        internal Material Own(Material material) { Materials.Add(material); _owned.Add(material.Pointer); return material; }
        internal bool Owns(Material material) => _owned.Contains(material.Pointer);
        internal IEnumerable<Material> Held => _shared.Select(row => row.Material).Concat(Materials);
        internal void Use(SharedMaterial material) { material.Users++; _shared.Add(material); }
        internal abstract void ShadowOnly(Renderer renderer);
        internal void Split(Renderer renderer, ForgeVisual.SurfaceSplit split) => Splits.Add((renderer, split));
        protected void DestroyMaterials()
        {
            foreach (var material in Materials) NativeModelRuntime.Destroy(material);
            Materials.Clear(); _owned.Clear();
            foreach (var material in _shared)
                if (--material.Users == 0 && SharedLooks.Remove(material.Key)) NativeModelRuntime.Destroy(material.Material);
            _shared.Clear();
            foreach (var (_, split) in Splits) split.Destroy();
            Splits.Clear();
        }
    }

    private sealed class OwnedCopy : OwnedMaterials
    {
        internal OwnedCopy(GameObject root) { Root = root; }
        internal GameObject Root { get; }
        internal List<(Material Material, Color Base)>? Shades { get; set; }
        /// <summary>The batched draws of this placement's imported models, in the root's frame; the batch ends with the root.</summary>
        internal List<ForgeVisual.InstancedDraw>? Imported { get; set; }
        /// <summary>The renderer is the placement's own and goes with it.</summary>
        internal override void ShadowOnly(Renderer renderer) => ForgeVisual.SurfaceLook.SetShadowOnly(renderer, true);
        internal void Destroy()
        {
            NativeModelRuntime.Destroy(Root);
            DestroyMaterials(); Shades = null; Imported = null;
        }
    }

    private sealed class SurfaceCopy : OwnedMaterials
    {
        internal List<(Renderer Renderer, Material[] Materials)> Originals { get; } = new();
        private readonly List<Renderer> _hidden = new();
        internal override void ShadowOnly(Renderer renderer) { ForgeVisual.SurfaceLook.SetShadowOnly(renderer, true); _hidden.Add(renderer); }
        internal void Restore()
        {
            foreach (var (renderer, split) in Splits) if (renderer != null) ForgeVisual.SurfaceLook.PutBack(renderer, split);
            foreach (var renderer in _hidden) if (renderer != null) ForgeVisual.SurfaceLook.SetShadowOnly(renderer, false);
            _hidden.Clear();
            foreach (var (renderer, materials) in Originals)
                if (renderer != null) renderer.sharedMaterials = materials;
            Originals.Clear();
            DestroyMaterials();
        }
    }

    private sealed record ResourceState(long Generation, ResourceStatus Status);
    private enum ResourceStatus { Loading, Ready, Failed }

    private sealed class ShardLease
    {
        internal ShardLease(string shard, long generation, string[] resources, NativeModelPart[] parts)
        { Shard = shard; Generation = generation; Resources = resources; Parts = parts; }
        internal string Shard { get; }
        internal long Generation { get; }
        internal string[] Resources { get; }
        internal NativeModelPart[] Parts { get; }
        internal GameShardState Before;
        /// <summary>Waiting for somebody else's load of the shard to register its manifest (see <see cref="Start"/>).</summary>
        internal bool Joined;
        internal bool LoadStarted, Owned, Ended, Released;
        internal string? Failure;
    }
}
