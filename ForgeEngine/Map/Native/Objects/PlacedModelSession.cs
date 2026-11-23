using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using Localization;
using ForgeVisual;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

/// <summary>Places generic room-part models on every peer, owns their explicit hit colliders and binds their
/// native bodies to the one gtfo.map_object identity used by mounted behavior.</summary>
internal sealed partial class PlacedModelSession : IDisposable
{
    private sealed class PlacedImageException : Exception
    {
        internal PlacedImageException(string code) : base(code) { }
    }

    private sealed class Entry
    {
        internal readonly PlacedModelInstance Instance;
        internal readonly GameObject Root;
        internal readonly Transform RootTransform;
        internal readonly PlacedModelTransformState Transform;
        internal readonly int Order;
        internal BoxCollider? ShotCollider;
        // The text, the backdrop behind a free label or an image board, the sign face and the image board are ForgeVisual's;
        // Map decides which exist, when they show and what they say.
        internal WorldText? LabelText;
        internal WorldBackdrop? Backdrop;
        internal SignBoard? Sign;
        internal ImageBoard? Image;
        internal TextMeshPro? Label => LabelText?.Text;
        internal GameObject? LabelRoot => LabelText?.Root;
        internal Transform? LabelTransform => LabelText?.Root.transform;
        internal float ImageWidth;
        internal float ImageHeight;
        internal Language EffectiveAtDisplay;
        internal bool EffectiveAtDisplayKnown;
        internal bool MovingQueued;
        internal bool BillboardQueued;
        // What a behaviour put in place of the model's own text and look; null keeps the model's.
        internal string? TextOverride;
        internal Color? ColorOverride;
        internal Color? BackdropOverride;
        internal string? ShownText => TextOverride ?? Instance.Model.Text;
        // The model's declaration to a recording (ForgeCore Recording.Track), ended with the model.
        internal IDisposable? Recorded;

        internal Entry(PlacedModelInstance instance, GameObject root, int order)
        {
            Instance = instance; Root = root; Order = order;
            RootTransform = root.transform;
            Transform = new(RootTransform.position, RootTransform.rotation, RootTransform.localScale);
        }
    }

    private readonly IForgeEngine _kernel;
    private readonly MapTextLanguage _language;
    private readonly MapObjectModule _mapObjects;
    private readonly Func<MapLevelReference?> _level;
    private readonly Action<string> _report;
    private readonly IReadOnlyList<PlacedModel> _models;
    private readonly IReadOnlyDictionary<(MapLevelReference Level, string Id), MapModelAssembly> _assemblies;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly DomainStateChannel.NativeStateSource _transformState;
    private readonly Func<bool> _authority;
    private readonly SpawnRuleSession? _rules;
    private IReadOnlyDictionary<string, SpawnRuleSession.ModelSite> _slotSites = new Dictionary<string, SpawnRuleSession.ModelSite>();
    private bool _slotsWaiting;
    private readonly PlacedImageTable _images;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _movingEntries = new();
    private readonly List<Entry> _languageEntries = new();
    private readonly List<Entry> _billboardEntries = new();
    private readonly HashSet<string> _pendingModels = new(StringComparer.Ordinal);
    private readonly ImageTextures _textures = new();
    private GameObject? _container;
    private long _placedWorld = long.MinValue;
    private bool _textLanguageMissingReported;
    private bool _disposed;
    internal static PlacedModelSession? Current { get; private set; }
    internal bool IsPending(string id) => _pendingModels.Contains(id);

    /// <summary>Resolve a presentation receiver from the published model identity only when its native body exists.
    /// The entity provider owns PlacedModelInstance, not a Unity component; presentation retains this exact root.</summary>
    internal static Transform? LabelReceiver(EntityReference target)
    {
        var session = Current;
        const string prefix = EntityKinds.MapObject + ":";
        if (session == null || session._disposed || target.WorldEpoch != session._kernel.WorldEpoch
            || !target.Id.StartsWith(prefix, StringComparison.Ordinal)
            || PlacedModelAddress.TryParse(target.Id[prefix.Length..]) is not { } address
            || session._mapObjects.AddressReference(address.ToString()) != target
            || !session._entries.TryGetValue(address.Key, out var entry)
            || !ReferenceEquals(entry.Instance, session._mapObjects.PlacedModels.ById(address.Key))
            || entry.Root == null || entry.Root.WasCollected) return null;
        return entry.Root.transform;
    }

    /// <summary>The placed models of this world in <paramref name="group"/>, added to <paramref name="members"/>.</summary>
    internal void GroupMembers(string group, List<EntityReference> members)
    {
        if (_disposed) return;
        foreach (var entry in _entries.Values)
            if (entry.Instance.Model.Group == group && entry.Root != null && !entry.Root.WasCollected
                && _mapObjects.AddressReference(PlacedModelAddress.Of(entry.Instance.Model.Id)!.ToString()) is { } reference)
                members.Add(reference);
    }

    /// <summary>A read at interaction-point creation/rebuild, never a scene search or a per-frame scan.
    /// Bounds are world-space, including model scale. Independent signs use their authored face and collider;
    /// ordinary models exclude attached text and its background.</summary>
    internal bool TryReadBody(string id, MapLevelReference level, long world,
        out PlacedModelBodySnapshot body, out string reason)
    {
        body = default;
        reason = "model-session-disposed";
        if (_disposed) return false;
        reason = "model-world-mismatch";
        if (world != _kernel.WorldEpoch) return false;
        reason = "model-level-mismatch";
        if (_level() != level) return false;
        var model = _models.FirstOrDefault(value => value.Id == id && value.Level == level);
        reason = "model-missing";
        if (model == null) return false;
        reason = "model-not-placed";
        if (_placedWorld != world) return false;
        // Place attempts every authored model of the level before publishing its instance table.
        // The existing placement error row carries the underlying refusal (room, prefab, image, ...).
        reason = "model-placement-refused";
        if (!_entries.TryGetValue(id, out var entry)) return false;
        reason = "model-instance-destroyed";
        if (entry.Root == null || entry.Root.WasCollected) return false;
        reason = "model-instance-stale";
        if (!ReferenceEquals(entry.Instance, _mapObjects.PlacedModels.ById(id))) return false;

        var found = false;
        var bounds = default(Bounds);
        if (model.Sign != null || model.Image != null)
        {
            // Media bodies use the face we created plus their real hit collider, including transparent signs.
            // No text glyph bounds or donor prefab is needed to resolve their interaction body.
            float width = (float)(model.Sign?.Width ?? (entry.Backdrop != null ? model.Image!.Width : entry.ImageWidth));
            float height = (float)(model.Sign?.Height ?? (entry.Backdrop != null ? model.Image!.Height : entry.ImageHeight));
            var geometry = new Bounds(Vector3.zero, new Vector3(width, height, 0));
            var collider = entry.Root.GetComponent<BoxCollider>();
            if (collider == null || collider.WasCollected) { reason = "model-collider-unavailable"; return false; }
            geometry.Encapsulate(new Bounds(collider.center, collider.size));
            var faceMatrix = Matrix4x4.TRS(entry.Root.transform.position, entry.Root.transform.rotation, entry.Root.transform.lossyScale);
            for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 2; z++)
                    {
                        var corner = faceMatrix.MultiplyPoint3x4(new Vector3(x == 0 ? geometry.min.x : geometry.max.x,
                            y == 0 ? geometry.min.y : geometry.max.y, z == 0 ? geometry.min.z : geometry.max.z));
                        if (!ForgeCore.Vec.Finite(corner)) { reason = "model-rendered-bounds-invalid"; return false; }
                        if (found) bounds.Encapsulate(corner);
                        else { bounds = new Bounds(corner, Vector3.zero); found = true; }
                    }
        }
        if (model.Model != null || model.Content.HasValue)
            foreach (var renderer in entry.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.WasCollected) continue;
                var transform = renderer.transform;
                if (entry.LabelRoot != null && (transform.Pointer == entry.LabelRoot.transform.Pointer
                    || transform.IsChildOf(entry.LabelRoot.transform))) continue;
                if (entry.Backdrop is { } backdrop && (transform.Pointer == backdrop.Root.transform.Pointer
                    || transform.IsChildOf(backdrop.Root.transform))) continue;
                // Renderer.bounds can be empty during room culling or LOD selection. Mesh geometry
                // and its world matrix remain available without enabling or showing the model.
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                var mesh = skinned != null ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.WasCollected) continue;
                var geometry = mesh.bounds;
                reason = "model-rendered-bounds-invalid";
                if (!ForgeCore.Vec.Finite(geometry.center) || !ForgeCore.Vec.Finite(geometry.size)
                    || geometry.size.x < 0 || geometry.size.y < 0 || geometry.size.z < 0) return false;
                if (geometry.size == Vector3.zero) continue;
                var matrix = renderer.localToWorldMatrix;
                var lower = geometry.min;
                var upper = geometry.max;
                var rendered = new Bounds(matrix.MultiplyPoint3x4(lower), Vector3.zero);
                for (var x = 0; x < 2; x++)
                    for (var y = 0; y < 2; y++)
                        for (var z = 0; z < 2; z++)
                        {
                            var corner = matrix.MultiplyPoint3x4(new Vector3(x == 0 ? lower.x : upper.x,
                                y == 0 ? lower.y : upper.y, z == 0 ? lower.z : upper.z));
                            if (!ForgeCore.Vec.Finite(corner)) return false;
                            rendered.Encapsulate(corner);
                        }
                if (found) bounds.Encapsulate(rendered);
                else { bounds = rendered; found = true; }
            }
        // Imported models are batched draws, not renderers: their box comes from the draws' own meshes.
        if (_assemblies.TryGetValue((model.Level, id), out var imported) && imported.TryImportedBounds(out var importedBounds))
        {
            reason = "model-rendered-bounds-invalid";
            if (!ForgeCore.Vec.Finite(importedBounds.center) || !ForgeCore.Vec.Finite(importedBounds.size)) return false;
            if (found) bounds.Encapsulate(importedBounds);
            else { bounds = importedBounds; found = true; }
        }
        reason = "model-rendered-bounds-unavailable";
        if (!found) return false;
        reason = "model-rendered-bounds-invalid";
        if (!ForgeCore.Vec.Finite(bounds.center) || !ForgeCore.Vec.Finite(bounds.size)) return false;
        var pose = entry.Root.transform;
        reason = "model-pose-invalid";
        if (!ForgeCore.Vec.Finite(pose.position) || !ForgeCore.Vec.Finite(pose.lossyScale)
            || !float.IsFinite(pose.rotation.x) || !float.IsFinite(pose.rotation.y)
            || !float.IsFinite(pose.rotation.z) || !float.IsFinite(pose.rotation.w)) return false;
        body = new PlacedModelBodySnapshot(entry.Instance, entry.Root.GetInstanceID(), world,
            pose, pose.position, pose.rotation, pose.lossyScale, bounds);
        reason = "";
        return true;
    }



    private PlacedModelSession(IForgeEngine kernel, MapObjectModule mapObjects, Func<MapLevelReference?> level,
        Action<string> report, IReadOnlyList<PlacedModel> models, PlacedImageTable images,
        IReadOnlyList<MapModelAssembly> assemblies, Func<bool>? authority, SpawnRuleSession? rules)
    {
        _kernel = kernel; _mapObjects = mapObjects; _level = level; _report = report; _models = models; _images = images;
        _authority = authority ?? (() => true); _rules = rules;
        _language = new();
        _assemblies = assemblies.ToDictionary(model => (model.Model.Level, model.Model.Id));
        var transformState = DomainStateChannel.RegisterNativeState(kernel, "forge.map.placed-model-transform",
            CaptureTransformState, RestoreTransformState);
        try
        {
            _lifecycle = mapObjects.Registration.ObserveLifecycle(value =>
        {
            if (value.IsMigration)
            {
                // A host migration keeps the level and its models: only the world number they are stamped with moves.
                if (_placedWorld == value.PreviousWorldEpoch) _placedWorld = value.Current.WorldEpoch;
                if (_transformReadyWorld == value.PreviousWorldEpoch) _transformReadyWorld = value.Current.WorldEpoch;
            }
            else if (value.Kind == RuntimeLifecycleKind.WorldChanged
                || value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) ClearWorld();
        }, replayCurrent: false);
        }
        catch { transformState.Dispose(); throw; }
        _transformState = transformState;
        _recordingProvider = ForgeCore.Recording.Provide(RecordingPrefix, new RecordedModels(this));
    }

    internal static PlacedModelSession Start(IForgeEngine kernel, MapObjectModule mapObjects,
        Func<MapLevelReference?> level, Action<string> report, IReadOnlyList<PlacedModel> models,
        PlacedImageTable? images = null, IReadOnlyList<MapModelAssembly>? assemblies = null, Func<bool>? authority = null,
        SpawnRuleSession? rules = null)
    {
        if (Current != null) throw new InvalidOperationException("Placed model session is already active.");
        var session = new PlacedModelSession(kernel, mapObjects, level, report, models, images ?? PlacedImageTable.Empty,
            assemblies ?? Array.Empty<MapModelAssembly>(), authority, rules);
        Current = session;
        mapObjects.PlacedModels.Load(Array.Empty<PlacedModelInstance>());
        return session;
    }

    internal void Place()
    {
        if (_disposed) return;
        if (_placedWorld != _kernel.WorldEpoch)
        {
            // A world is marked placed only once its level is known; marking it first would leave its models unplaced
            // for good and report the world ready with none of them.
            if (_level() is not { } level) return;
            var pendingTransformState = _pendingTransformState;
            ClearWorld();
            _pendingTransformState = pendingTransformState;
            _placedWorld = _kernel.WorldEpoch;
            _container = new GameObject("Forge placed models");
            // A random model rule's slots wait for the level to finish generating: their spots are the free markers left then.
            _slotsWaiting = _rules?.HasModelRules == true;
            foreach (var model in _models.Where(value => value.Level == level && !(_slotsWaiting && _rules!.IsModelSlot(value.Id))))
                _pendingModels.Add(model.Id);
        }
        if (_slotsWaiting && _rules!.ModelSites() is { } sites)
        {
            _slotsWaiting = false;
            _slotSites = sites;
            foreach (var id in sites.Keys) _pendingModels.Add(id);
        }
        if (_pendingModels.Count == 0) return;
        var placedLevel = _level();
        var placedBefore = _entries.Count;
        for (var index = 0; index < _models.Count; index++)
        {
            var model = _models[index];
            if (model.Level != placedLevel || !_pendingModels.Contains(model.Id)) continue;
            // Runtime still owns an asynchronous shard load; the model stays pending for Map's next frame.
            if (_assemblies.TryGetValue((model.Level, model.Id), out var loading) && loading.Loading) continue;
            try { if (Place(model)) _pendingModels.Remove(model.Id); }
            catch (PlacedImageException error)
            {
                _pendingModels.Remove(model.Id);
                ImageRefused(model, error.Message);
            }
            catch (Exception error)
            {
                _pendingModels.Remove(model.Id);
                Refused(model, error.GetBaseException().Message);
            }
        }
        // A model waiting for its resource changes nothing, so the address table is rebuilt only when one was placed.
        if (_entries.Count != placedBefore) _mapObjects.PlacedModels.Load(_entries.Values.Select(value => value.Instance));
        if (_pendingModels.Count == 0)
            _rules?.RegisterModels(id => _entries.ContainsKey(id) ? _mapObjects.AddressReference(PlacedModelAddress.Of(id)!.ToString()) : null);
        ApplyPendingTransformState();
        if (_pendingModels.Count == 0 && _transformReadyWorld != _placedWorld)
        {
            _transformReadyWorld = _placedWorld;
            if (_authority()) _transformState.Changed();
        }
    }

    /// <summary>False when the model has to wait for a resource (the game's gear shader loads with the first built level): nothing stays
    /// built and it stays pending.</summary>
    private bool Place(PlacedModel model)
    {
        LG_Area? area;
        IReadOnlyList<double> position, rotation;
        if (_slotSites.TryGetValue(model.Id, out var site))
        {
            // A random model rule's instance stands on the free marker its spot was drawn on.
            area = site.Area; position = site.Position; rotation = site.Rotation;
        }
        else
        {
            if (!RoomResolver.TryPlace(_kernel.WorldEpoch, model.Room, out var room, out var roomRefusal))
                throw new InvalidOperationException(ForgeEngine.Logging.DynamicDiagnosticCodes.Of("room-{roomRefusal}", roomRefusal!));
            area = room!.Area;
            position = room.Pose.Point(model.Position);
            rotation = room.Pose.Compose(model.Rotation);
        }
        if (area == null || area.WasCollected || area.m_zone == null || area.m_zone.WasCollected)
            throw new InvalidOperationException("room-area-unavailable");
        var root = new GameObject("Forge model " + model.Id);
        GameObject? copy = null;
        Entry? entry = null;
        try
        {
            root.transform.SetParent(_container!.transform, false);
            // The navigation build collects its sources from the geomorph roots only: a native-collision model hangs below its room's area, a plain model below the shared container.
            if (model.NativeCollision) root.transform.SetParent(area.transform, false);
            root.transform.position = V(position);
            root.transform.rotation = Q(rotation);
            root.transform.localScale = V(model.Scale);
            var instance = new PlacedModelInstance(model, position);
            entry = new Entry(instance, root, _entries.Count);
            if (model.Content.HasValue)
            {
                if (!_assemblies.TryGetValue((model.Level, model.Id), out var assembly) || !ReferenceEquals(assembly.Model, model))
                    throw new InvalidOperationException("model-assembly-unregistered");
                int layer = LayerManager.LAYER_DEFAULT;
                if (layer < 0 || layer > 31) throw new InvalidOperationException("model-layer-unavailable");
                root.layer = layer;
                if (assembly.Place(root.transform) is { } refusal)
                {
                    if (!refusal.StartsWith("model-resource-loading:", StringComparison.Ordinal)) throw new InvalidOperationException(refusal);
                    assembly.ClearWorld();
                    Object.Destroy(root);
                    return false;
                }
            }
            else if (model.Model is { } prototype)
            {
                copy = Object.Instantiate<GameObject>(RoomRecipeParts.Get(prototype.SourcePrefab, prototype.SourcePath), root.transform);
                copy.name = model.Id + " prototype";
                copy.transform.localPosition = Vector3.zero; copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one; copy.SetActive(true);
                var spawner = copy.GetComponent<LG_PrefabSpawner>()
                    ?? throw new InvalidOperationException("prototype-not-spawner");
                spawner.m_isBuilt = false; spawner.m_area = area; spawner.m_zone = area.m_zone;
                spawner.m_applyScale = true; spawner.m_disableCollision = false; spawner.m_useNoGraphLayerOverride = false;
                var body = spawner.OnBuild(false);
                if (body == null || body.WasCollected) throw new InvalidOperationException("native-build-returned-no-model");
                if (body.transform.parent == null || body.transform.parent.Pointer != root.transform.Pointer)
                    body.transform.SetParent(root.transform, true);
                Collider? nativeCollider = null;
                foreach (var candidate in body.GetComponentsInChildren<Collider>(true))
                    if (candidate != null && !candidate.WasCollected && !candidate.isTrigger) { nativeCollider = candidate; break; }
                if (nativeCollider == null) throw new InvalidOperationException("native-hit-layer-unavailable");
                root.layer = nativeCollider.gameObject.layer;
            }
            else if (model.Image != null || model.Sign != null)
            {
                // The PackTest address-shot path uses the native default layer only after checking it against both
                // MASK_BULLETWEAPON_RAY and MASK_CAMERA_RAY. The game acceptance pass repeats that check for boards.
                int layer = LayerManager.LAYER_DEFAULT;
                if (layer < 0 || layer > 31 || (LayerManager.MASK_BULLETWEAPON_RAY & (1 << layer)) == 0
                    || (LayerManager.MASK_CAMERA_RAY & (1 << layer)) == 0)
                {
                    if (model.Image != null) throw new PlacedImageException("image-collider-layer-unavailable");
                    throw new InvalidOperationException("sign-collider-layer-unavailable");
                }
                root.layer = layer;
                var style = model.TextStyle ?? (model.Sign != null ? PlacedModelTextStyle.SignDefault : PlacedModelTextStyle.Default);
                Color? backdrop = style.BackgroundOpacity > 0 ? HexColor(style.BackgroundColor, style.BackgroundOpacity) : null;
                var backdropName = "Forge model text background " + model.Id;
                if (model.Sign is { } sign)
                    entry.Sign = SignBoard.TryCreate(root.transform, backdropName, (float)sign.Width, (float)sign.Height, style.Padding, backdrop)
                        ?? throw new InvalidOperationException("model-text-background-shader-unavailable");
                else
                {
                    var image = model.Image!;
                    if (backdrop is { } color)
                    {
                        entry.Backdrop = WorldBackdrop.TryCreate(root.transform, backdropName, color)
                            ?? throw new PlacedImageException("image-shader-unavailable");
                        entry.Backdrop.Place(new Vector2((float)image.Width, (float)image.Height), default);
                    }
                    CreateImage(entry, image);
                }
            }
            else throw new InvalidOperationException("placed-model-source-missing");
            // A declared disabled hit box still owns native geometry for readback; enabled alone controls physics.
            if (model.ShotCollider is { } shotCollider)
                entry.ShotCollider = CreateShotCollider(entry, shotCollider);
            if (!model.Content.HasValue || model.HasSolidCollider)
            {
                var hit = root.AddComponent<BoxCollider>();
                hit.center = V(model.ColliderCenter); hit.size = V(model.ColliderSize); hit.isTrigger = false; hit.enabled = true;
            }
            _entries.Add(model.Id, entry); PlacedModelHitRegistry.Register(root, instance);
            entry.Recorded = ForgeCore.Recording.Track(RecordingKey(model.Id), root);
            if (model.Text != null)
            {
                if (StationTextValue.RequiresLanguage(model.Text))
                {
                    _languageEntries.Add(entry);
                    if (TryEffectiveLanguage(out var language)
                        && !Show(entry, language, true, out var textCode)) TextRefused(model, textCode);
                }
                else if (!Show(entry, default, true, out var textCode)) TextRefused(model, textCode);
                TrackBillboard(entry);
            }
            var values = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Quote("id", model.Id),
                RuntimeLogValue.Quote("level", model.Level.ToString()),
                RuntimeLogValue.Quote("room", model.Room.RoomId),
                RuntimeLogValue.Of("address", PlacedModelAddress.Of(model.Id)!.ToString()),
                RuntimeLogValue.Of("colliderLayer", (long)root.layer),
                RuntimeLogValue.Of("shotColliderPresent", entry.ShotCollider != null),
                RuntimeLogValue.Of("shotColliderEnabled", entry.ShotCollider?.enabled ?? false),
                RuntimeLogValue.Of("shotColliderLayer", (long)(entry.ShotCollider?.gameObject.layer ?? -1))
            };
            if (_assemblies.TryGetValue((model.Level, model.Id), out var placedAssembly))
            {
                values.Add(RuntimeLogValue.Quote("assembly", placedAssembly.Assembly.Id));
                values.Add(RuntimeLogValue.Of("parts", (long)(placedAssembly.Assembly.Parts.Count + placedAssembly.Assembly.ImportedParts.Count)));
            }
            // log-values-max: 16
            values.AddRange(PlacedModelRecordValues.Of(model, root, V(position), Q(rotation),
                _assemblies.TryGetValue((model.Level, model.Id), out var lookAssembly) ? lookAssembly : null));
            if (model.Model is { } placed)
            {
                values.Add(RuntimeLogValue.Of("sourcePrefab", placed.SourcePrefab));
                values.Add(RuntimeLogValue.Of("sourcePath", placed.SourcePath));
            }
            else if (model.Image != null)
            {
                values.Add(RuntimeLogValue.Of("image", model.Image.Reference.Id));
                values.Add(RuntimeLogValue.Of("revision", model.Image.Reference.Revision));
                values.Add(RuntimeLogValue.Of("pngSha256", model.Image.PngSha256));
            }
            MapLog.Record(RuntimeLogLevel.Info, "map.placed-model", "Placed model registered.", values.ToArray());
            return true;
        }
        catch
        {
            if (_assemblies.TryGetValue((model.Level, model.Id), out var assembly)) assembly.ClearWorld();
            if (entry != null)
            {
                entry.Recorded?.Dispose();
                DestroyVisuals(entry);
                _languageEntries.Remove(entry);
                _billboardEntries.Remove(entry);
            }
            _entries.Remove(model.Id); PlacedModelHitRegistry.Unregister(root);
            if (root != null) Object.Destroy(root);
            throw;
        }
        finally
        {
            if (copy != null && !copy.WasCollected && copy.GetComponent<LG_PrefabSpawner>() != null) Object.Destroy(copy);
        }
    }

    /// <summary>The box a model's text sits on: the root's own collider, or a model-library model's shot collider.
    /// Creating the text and following the model's later moves read the same one.</summary>
    private static BoxCollider? LabelAnchor(Entry entry) => entry.Root.GetComponent<BoxCollider>() ?? entry.ShotCollider;

    private static BoxCollider CreateShotCollider(Entry entry, PlacedModelShotCollider definition)
    {
        int layer = LayerMask.NameToLayer("ProjectileBlocker");
        int playerSynced = LayerManager.LAYER_PLAYER_SYNCED, playerMover = LayerManager.LAYER_PLAYER_MOVER;
        if (layer != 22) throw new InvalidOperationException("model-shot-collider-layer-name");
        if (playerSynced is < 0 or > 31 || playerMover is < 0 or > 31)
            throw new InvalidOperationException("model-shot-collider-player-layer");
        if ((LayerManager.MASK_BULLETWEAPON_RAY & (1 << layer)) == 0)
            throw new InvalidOperationException("model-shot-collider-ray-mask");
        if ((LayerManager.MASK_UNITY_NAVMESH_GENERATION & (1 << layer)) != 0)
            throw new InvalidOperationException("model-shot-collider-nav-mask");
        if (!Physics.GetIgnoreLayerCollision(layer, playerSynced)
            || !Physics.GetIgnoreLayerCollision(layer, playerMover))
            throw new InvalidOperationException("model-shot-collider-player-collision");
        var body = new GameObject("Forge model shot collider " + entry.Instance.Model.Id) { layer = layer };
        body.transform.SetParent(entry.Root.transform, false);
        body.transform.localPosition = Vector3.zero; body.transform.localRotation = Quaternion.identity;
        body.transform.localScale = Vector3.one;
        var collider = body.AddComponent<BoxCollider>();
        collider.center = V(definition.Center); collider.size = V(definition.Size);
        collider.isTrigger = false; collider.enabled = definition.Enabled;
        return collider;
    }

    private void CreateImage(Entry entry, PlacedModelImage image)
    {
        var resource = _images.Find(image.PngSha256)
            ?? throw new PlacedImageException("image-file-missing");
        int limit = Math.Min(PlacedImageResources.MaximumTextureDimension, SystemInfo.maxTextureSize);
        if (limit <= 0 || resource.Width > limit || resource.Height > limit)
        {
            throw new PlacedImageException("image-dimensions-unsupported");
        }
        double scale = Math.Min(image.Width / resource.Width, image.Height / resource.Height);
        float fittedWidth = (float)(resource.Width * scale);
        float fittedHeight = (float)(resource.Height * scale);
        if (!float.IsFinite(fittedWidth) || !float.IsFinite(fittedHeight) || fittedWidth <= 0 || fittedHeight <= 0)
            throw new PlacedImageException("image-board-size-unsupported");
        entry.ImageWidth = fittedWidth; entry.ImageHeight = fittedHeight;
        if (!_textures.TryGet(image.PngSha256, resource.Bytes, resource.Width, resource.Height, out var texture, out var decodeCode, out var decodeError))
        {
            if (decodeError != null) MapLog.Contained("placed-model.image-decode", decodeError);
            throw new PlacedImageException(decodeCode);
        }
        entry.Image = ImageBoard.TryCreate(entry.Root.transform, "Forge image board " + entry.Instance.Model.Id, entry.Root.layer,
                texture!, entry.ImageWidth, entry.ImageHeight)
            ?? throw new PlacedImageException("image-shader-unavailable");
    }

    internal void Tick()
    {
        if (_disposed) return;
        if (_pendingModels.Count != 0 || _slotsWaiting) Place();
        AdvanceTransforms();
        if (_languageEntries.Count != 0 && TryEffectiveLanguage(out var effective))
        {
            foreach (var entry in _languageEntries)
            {
                if (!entry.EffectiveAtDisplayKnown || entry.EffectiveAtDisplay != effective)
                {
                    if (!Show(entry, effective, true, out var code))
                    {
                        entry.LabelText?.SetShown(false);
                        entry.EffectiveAtDisplay = effective;
                        entry.EffectiveAtDisplayKnown = true;
                        TextRefused(entry.Instance.Model, code);
                    }
                    TrackBillboard(entry);
                }
            }
        }
        if (_billboardEntries.Count == 0) return;
        var camera = MapCamera.Transform();
        if (camera == null || camera.WasCollected) return;
        // ForgeVisual turns each label just before the cameras render; this only hands over the current camera (the same one is a no-op).
        foreach (var entry in _billboardEntries)
        {
            if (entry.Root == null || entry.Root.WasCollected || entry.LabelText is not { Alive: true } label) continue;
            label.FaceCamera(camera);
        }
    }

    /// <summary>A prototype or model-library body carries its text on a detached camera-facing label whose backdrop is a child of that label; signs and image boards keep their text and backdrop on their own face.</summary>
    private static bool FreeLabel(PlacedModel model) => model.Sign == null && model.Image == null;

    private void TrackBillboard(Entry entry)
    {
        if (entry.BillboardQueued || !FreeLabel(entry.Instance.Model) || entry.LabelRoot == null) return;
        entry.BillboardQueued = true;
        _billboardEntries.Add(entry);
    }

    /// <summary>Shows this model's owned text on this client. Routing and recipient validation stay in the
    /// presentation action; this method can affect only the local model instance.</summary>
    /// <param name="text">A text shown in place of the model's own (a template's output); null shows the model's.</param>
    internal static bool TryShowText(EntityReference model, Language language, string? text, out string code)
    {
        code = ResultCode.ModelTextModelStale;
        if (!TryEntry(model, out var session, out var entry)) return false;
        if (text != null) session.Replace(entry, text);
        if (entry.ShownText == null) { code = ResultCode.ModelTextMissing; return false; }
        if (!Show(entry, language, false, out code)) return false;
        session.TrackBillboard(entry);
        code = "model-text-shown";
        return true;
    }

    /// <summary>Restyles this model's shown text on this client and returns the result code. A new text follows the player's language from then on.</summary>
    internal static string Restyle(EntityReference target, TextRestyle restyle)
    {
        // The handler has already refused a target from another world, so a target this table does not hold shows nothing here.
        if (!TryEntry(target, out var session, out var entry)) return ResultCode.TextStyleNotShown;
        if (restyle.Text != null) session.Replace(entry, restyle.Text);
        else if (entry.ShownText == null) return ResultCode.TextStyleNotShown;
        if (restyle.Color is { } color) entry.ColorOverride = color;
        if (restyle.Background != null || restyle.Opacity != null)
        {
            var style = entry.Instance.Model.TextStyle ?? (entry.Instance.Model.Sign != null ? PlacedModelTextStyle.SignDefault : PlacedModelTextStyle.Default);
            var current = entry.BackdropOverride ?? HexColor(style.BackgroundColor, style.BackgroundOpacity);
            var next = restyle.Background is { } rgb ? new Color(rgb.r, rgb.g, rgb.b, current.a) : current;
            if (restyle.Opacity is { } opacity) next.a = opacity;
            var backdrop = entry.Backdrop ?? entry.Sign?.Backdrop;
            // A sign or an image board draws its backdrop when it is built; one built without cannot gain it later.
            if (backdrop == null && next.a > 0 && !FreeLabel(entry.Instance.Model)) return ResultCode.TextStyleInvalid;
            entry.BackdropOverride = next;
        }
        if (!session.TryEffectiveLanguage(out var language) && StationTextValue.RequiresLanguage(entry.ShownText))
            return ResultCode.TextStyleNotShown;
        if (!Show(entry, language, true, out var code)) return code;
        session.TrackBillboard(entry);
        return TextStyleActions.AppliedCode;
    }

    /// <summary>The native text and backdrop of this model's shown text; null when the table does not hold the model.</summary>
    internal static (TextMeshPro? Text, WorldBackdrop? Backdrop)? InspectTextLook(EntityReference target)
        => TryEntry(target, out _, out var entry) ? (entry.Label, entry.Backdrop ?? entry.Sign?.Backdrop) : null;

    private static bool TryEntry(EntityReference model, out PlacedModelSession session, out Entry entry)
    {
        session = Current!; entry = null!;
        if (session == null || session._disposed || model.WorldEpoch != session._kernel.WorldEpoch
            || session._placedWorld != session._kernel.WorldEpoch) return false;
        const string prefix = EntityKinds.MapObject + ":";
        return model.Id.StartsWith(prefix, StringComparison.Ordinal)
            && PlacedModelAddress.TryParse(model.Id[prefix.Length..]) is { } address
            && session._mapObjects.AddressReference(address.ToString()) == model
            && session._entries.TryGetValue(address.Key, out entry!)
            && ReferenceEquals(entry.Instance, session._mapObjects.PlacedModels.ById(address.Key))
            && entry.Root != null && !entry.Root.WasCollected;
    }

    /// <summary>Puts <paramref name="text"/> in place of the model's own text; a reference keeps following the
    /// player's language the way the model's own reference does.</summary>
    private void Replace(Entry entry, string text)
    {
        entry.TextOverride = text;
        entry.EffectiveAtDisplayKnown = false;
        if (StationTextValue.RequiresLanguage(text) && !_languageEntries.Contains(entry)) _languageEntries.Add(entry);
    }

    /// <summary>Takes back every private text, colour and backdrop this client wrote over the placed models, so the model
    /// shows its own again; the recipient ledger then replays the lines the restored state still holds.</summary>
    internal void ResetPrivateText()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.TextOverride == null && entry.ColorOverride == null && entry.BackdropOverride == null) continue;
            entry.TextOverride = null; entry.ColorOverride = null; entry.BackdropOverride = null;
            entry.EffectiveAtDisplayKnown = false;
            DestroyLabel(entry);
            var own = entry.Instance.Model.Text;
            if (own == null || !StationTextValue.RequiresLanguage(own)) _languageEntries.Remove(entry);
            // A model with its own text that needs no language is shown again here; the rest follow the language in the Tick below.
            if (own != null && !StationTextValue.RequiresLanguage(own) && !Show(entry, default, true, out var code))
                TextRefused(entry.Instance.Model, code);
        }
        foreach (var entry in _languageEntries)
        {
            entry.LabelText?.SetShown(false);
            entry.EffectiveAtDisplayKnown = false;
        }
        Tick();
    }

    internal static EntityReference? TextReference(string id)
    {
        const string prefix = EntityKinds.MapObject + ":";
        var session = Current;
        return session != null && !session._disposed && id.StartsWith(prefix, StringComparison.Ordinal)
            && PlacedModelAddress.TryParse(id[prefix.Length..]) is { } address
                ? session._mapObjects.AddressReference(address.ToString()) : null;
    }

    private static bool Show(Entry entry, Language language, bool followsEffectiveLanguage, out string code)
    {
        code = ResultCode.ModelTextMissing;
        var owned = entry.ShownText;
        if (owned == null || !StationTextValue.TryRenderOptional(owned, language,
                out var text, out bool empty)) return false;
        if (!ReferenceEquals(entry.LabelRoot, null)
            && (entry.LabelRoot == null || entry.LabelRoot.WasCollected || entry.Label == null || entry.Label.WasCollected
                || entry.LabelTransform == null || entry.LabelTransform.WasCollected))
        { code = ResultCode.ModelTextReadbackUnavailable; return false; }
        if (entry.LabelRoot != null && (entry.Label!.gameObject.Pointer != entry.LabelRoot.Pointer
            || entry.LabelTransform!.Pointer != entry.LabelRoot.transform.Pointer))
        { code = ResultCode.ModelTextReadbackMismatch; return false; }
        if (empty && entry.Instance.Model.Sign == null)
        {
            entry.LabelText?.SetShown(false);
            if (entry.LabelRoot != null && entry.LabelRoot.activeSelf)
            { code = ResultCode.ModelTextReadbackMismatch; return false; }
            RememberEffectiveLanguage(entry, language, followsEffectiveLanguage);
            TellRecording(entry, "");
            return true;
        }
        var textStyle = entry.Instance.Model.TextStyle ?? PlacedModelTextStyle.Default;
        float lineWidth = (float)(textStyle.LineWidth ?? entry.Instance.Model.Sign?.Width
            ?? (entry.Instance.Model.Image != null ? entry.ImageWidth : PlacedModelTextStyle.DefaultLineWidth));
        var textName = "Forge model text " + entry.Instance.Model.Id;
        bool onImage = entry.Instance.Model.Image != null;
        // A model-library model has no solid collider on its root; its text sits on top of the shot collider, whose
        // object keeps the root's own pose, so the box centre and size are read in the same local frame.
        var collider = entry.Label == null ? LabelAnchor(entry) : null;
        if (entry.Label == null && (collider == null || collider.WasCollected)) { code = ResultCode.ModelTextModelStale; return false; }
        var look = new WorldTextStyle
        {
            FontSize = (float)textStyle.FontSize,
            Color = entry.ColorOverride ?? HexColor(textStyle.Color, 1),
            LineSpacing = PlacedModelTextStyle.LineSpacing,
            SceneDepth = true,
            Alignment = onImage ? TextAlignmentOptions.Top : TextAlignmentOptions.Bottom,
            Pivot = onImage ? new Vector2(.5f, 1) : new Vector2(.5f, 0)
        };
        Vector2 preferred = default;
        try
        {
            if (entry.Sign is { } board)
            {
                entry.LabelText = board.ShowText(textName, look, text, lineWidth);
            }
            else
            {
                if (entry.LabelText == null)
                {
                    bool onBoard = onImage;
                    var anchor = entry.Root.transform.TransformPoint(collider!.center + new Vector3(0, collider.size.y * .5f, 0));
                    var created = WorldText.Create(onBoard ? entry.Root.transform : null, textName, look);
                    entry.LabelText = created;
                    if (onBoard) created.SetLocalPosition(new Vector3(0, entry.ImageHeight * .5f, -.02f));
                    else
                    {
                        created.Root.transform.position = anchor;
                        created.Root.transform.rotation = MapCamera.Transform()?.rotation ?? entry.Root.transform.rotation;
                    }
                    if ((entry.BackdropOverride?.a ?? (float)textStyle.BackgroundOpacity) > 0 && !onBoard) CreateLabelBackdrop(entry, textStyle);
                }
                entry.LabelText.SetShown(true);
                // Keep the layout width fixed; preferred width may shrink for short text, but changing the rect to it
                // would change TMP's next wrap. Preferred bounds size the backdrop independently.
                preferred = entry.LabelText.SetContent(text, lineWidth);
            }
        }
        catch { DestroyLabel(entry); throw; }
        // The text object keeps the colour it was made with; a restyle recolours it in place.
        if (entry.ColorOverride is { } recolour && entry.Label != null && !entry.Label.WasCollected) entry.Label.color = recolour;
        if (entry.BackdropOverride is { } backdropColour)
        {
            var backdrop = entry.Backdrop ?? entry.Sign?.Backdrop;
            if (backdropColour.a <= 0 && FreeLabel(entry.Instance.Model)) { backdrop?.Destroy(); entry.Backdrop = null; }
            else if (backdrop == null && backdropColour.a > 0 && FreeLabel(entry.Instance.Model) && entry.LabelRoot != null) CreateLabelBackdrop(entry, textStyle);
            else backdrop?.SetColor(backdropColour);
        }
        if (entry.Backdrop != null && FreeLabel(entry.Instance.Model))
        {
            var bounds = new Vector2(Math.Min(lineWidth, preferred.x), preferred.y);
            var padding = (entry.Instance.Model.TextStyle ?? PlacedModelTextStyle.Default).Padding;
            // Positive local Z is behind the text plane.
            entry.Backdrop.Place(new Vector2(bounds.x + padding * 2, bounds.y + padding * 2), new Vector2(0, bounds.y * .5f));
        }
        if (entry.LabelRoot == null || entry.LabelRoot.WasCollected || entry.Label == null || entry.Label.WasCollected
            || entry.LabelTransform == null || entry.LabelTransform.WasCollected)
        { code = ResultCode.ModelTextReadbackUnavailable; return false; }
        if (!entry.LabelRoot.activeSelf || entry.Label.gameObject.Pointer != entry.LabelRoot.Pointer
            || entry.LabelTransform.Pointer != entry.LabelRoot.transform.Pointer
            || !string.Equals(entry.Label.text, text, StringComparison.Ordinal)
            || !float.IsFinite(entry.Label.rectTransform.sizeDelta.x) || !float.IsFinite(entry.Label.rectTransform.sizeDelta.y))
        { code = ResultCode.ModelTextReadbackMismatch; return false; }
        RememberEffectiveLanguage(entry, language, followsEffectiveLanguage);
        TellRecording(entry, text);
        StationSignPlacementReadback.Record(Current!._kernel.WorldEpoch, entry.Instance,
            FreeLabel(entry.Instance.Model) ? entry.LabelRoot.transform : entry.Root.transform, entry.Label);
        return true;
    }

    private bool TryEffectiveLanguage(out Language language)
    {
        if (_language.TryRead(out language, out var code, out _))
        {
            _textLanguageMissingReported = false;
            return true;
        }
        if (!_textLanguageMissingReported)
        {
            _report("placed-model-text-refused: reason=" + code);
            _textLanguageMissingReported = true;
        }
        return false;
    }

    private static void RememberEffectiveLanguage(Entry entry, Language language, bool followsEffectiveLanguage)
    {
        if (!StationTextValue.RequiresLanguage(entry.ShownText))
        {
            entry.EffectiveAtDisplayKnown = false;
            return;
        }
        if (followsEffectiveLanguage)
        {
            entry.EffectiveAtDisplay = language;
            entry.EffectiveAtDisplayKnown = true;
            return;
        }
        if (Current?.TryEffectiveLanguage(out var effective) == true)
        {
            entry.EffectiveAtDisplay = effective;
            entry.EffectiveAtDisplayKnown = true;
        }
        else entry.EffectiveAtDisplayKnown = false;
    }

    private static Color HexColor(string hex, double opacity)
        => new(byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f,
            byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f,
            byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f, (float)opacity);

    /// <summary>The backdrop of a free label hangs below the label, so it follows the label's pose.</summary>
    private static void CreateLabelBackdrop(Entry entry, PlacedModelTextStyle style)
        => entry.Backdrop = WorldBackdrop.TryCreate(entry.LabelRoot!.transform, "Forge model text background " + entry.Instance.Model.Id,
                entry.BackdropOverride ?? HexColor(style.BackgroundColor, style.BackgroundOpacity))
            ?? throw new InvalidOperationException("model-text-background-shader-unavailable");

    private static void DestroyLabel(Entry entry)
    {
        if (entry.Sign != null) entry.Sign.DestroyText();
        else entry.LabelText?.Destroy();
        entry.LabelText = null;
        if (FreeLabel(entry.Instance.Model)) { entry.Backdrop?.Destroy(); entry.Backdrop = null; }
    }

    private static void DestroyVisuals(Entry entry)
    {
        DestroyLabel(entry);
        entry.Backdrop?.Destroy(); entry.Backdrop = null;
        entry.Sign?.Destroy(); entry.Sign = null;
        entry.Image?.Destroy(); entry.Image = null;
    }

    private void Refused(PlacedModel model, string reason)
    {
        if (model.Content.HasValue)
        {
            _report("placed-model-refused: id=" + model.Id + " room=" + model.Room.RoomId + " reason=" + reason);
            MapLog.Record(RuntimeLogLevel.Error, "map.placed-model-refused", "Placed model refused.",
                RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("level", model.Level.ToString()),
                RuntimeLogValue.Quote("room", model.Room.RoomId), RuntimeLogValue.Quote("reason", reason));
            return;
        }
        if (model.Model is { } prototype)
        {
            _report("placed-model-refused: id=" + model.Id + " sourcePrefab=" + prototype.SourcePrefab
                + " sourcePath=" + prototype.SourcePath + " reason=" + reason);
            MapLog.Record(RuntimeLogLevel.Error, "map.placed-model-refused", "Placed model refused.",
                RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("sourcePrefab", prototype.SourcePrefab),
                RuntimeLogValue.Quote("sourcePath", prototype.SourcePath), RuntimeLogValue.Quote("reason", reason));
            return;
        }
        if (model.Sign != null)
        {
            _report("placed-model-refused: id=" + model.Id + " reason=" + reason);
            MapLog.Record(RuntimeLogLevel.Error, "map.placed-model-refused", "Placed text board refused.",
                RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("reason", reason));
            return;
        }
        var image = model.Image!;
        _report("placed-model-refused: id=" + model.Id + " image=" + image.Reference.Id + "@"
            + image.Reference.Revision + " pngSha256=" + image.PngSha256 + " reason=" + reason);
        MapLog.Record(RuntimeLogLevel.Error, "map.placed-model-refused", "Placed model refused.",
            RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("image", image.Reference.Id),
            RuntimeLogValue.Quote("revision", image.Reference.Revision), RuntimeLogValue.Quote("pngSha256", image.PngSha256),
            RuntimeLogValue.Quote("reason", reason));
    }

    private void ImageRefused(PlacedModel model, string reason)
    {
        var image = model.Image!;
        _report("placed-image-refused: id=" + model.Id + " image=" + image.Reference.Id + "@"
            + image.Reference.Revision + " pngSha256=" + image.PngSha256 + " reason=" + reason);
        MapLog.Record(RuntimeLogLevel.Error, "map.placed-image-refused", "Placed image board refused.",
            RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("image", image.Reference.Id),
            RuntimeLogValue.Quote("revision", image.Reference.Revision), RuntimeLogValue.Quote("pngSha256", image.PngSha256),
            RuntimeLogValue.Quote("reason", reason));
    }

    private void TextRefused(PlacedModel model, string reason)
    {
        _report("placed-model-text-refused: id=" + model.Id + " reason=" + reason);
        MapLog.Record(RuntimeLogLevel.Error, "map.placed-model-text-refused", "Placed model text refused.",
            RuntimeLogValue.Quote("id", model.Id), RuntimeLogValue.Quote("reason", reason));
    }

    private void ClearWorld()
    {
        CancelTransforms();
        foreach (var assembly in _assemblies.Values) assembly.ClearWorld();
        _mapObjects.PlacedModels.Load(Array.Empty<PlacedModelInstance>());
        foreach (var entry in _entries.Values)
        {
            entry.Recorded?.Dispose();
            DestroyVisuals(entry);
            if (entry.Root != null) Object.Destroy(entry.Root);
        }
        _textures.Dispose();
        _entries.Clear(); PlacedModelHitRegistry.Clear();
        _movingEntries.Clear(); _languageEntries.Clear(); _billboardEntries.Clear();
        _pendingModels.Clear();
        _slotSites = new Dictionary<string, SpawnRuleSession.ModelSite>(); _slotsWaiting = false;
        if (_container != null) Object.Destroy(_container);
        _container = null;
        _placedWorld = long.MinValue;
        _transformReadyWorld = long.MinValue;
        _pendingTransformState = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle.Dispose();
        _recordingProvider.Dispose();
        ClearWorld();
        _transformState.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private static Vector3 V(IReadOnlyList<double> value) => new((float)value[0], (float)value[1], (float)value[2]);
    private static Quaternion Q(IReadOnlyList<double> value) => new((float)value[0], (float)value[1], (float)value[2], (float)value[3]);
}

internal readonly record struct PlacedModelBodySnapshot(PlacedModelInstance Instance, int NativeInstanceId,
    long World, Transform RootTransform, Vector3 Position, Quaternion Rotation, Vector3 Scale, Bounds WorldBounds);

/// <summary>The single collider-root registration used by the weapon impact resolver. A generated model can carry
/// several native colliders plus the explicit authored BoxCollider; climbing their shared root always returns the
/// same model self.</summary>
internal static class PlacedModelHitRegistry
{
    private static readonly Dictionary<int, PlacedModelInstance> Roots = new();
    internal static void Register(GameObject root, PlacedModelInstance model) => Roots.Add(root.GetInstanceID(), model);
    internal static void Unregister(GameObject root) => Roots.Remove(root.GetInstanceID());
    internal static PlacedModelInstance? Resolve(Component component)
    {
        var current = component.transform;
        while (current != null && !current.WasCollected)
        {
            if (Roots.TryGetValue(current.gameObject.GetInstanceID(), out var model)) return model;
            current = current.parent;
        }
        return null;
    }
    internal static void Clear() => Roots.Clear();
}
