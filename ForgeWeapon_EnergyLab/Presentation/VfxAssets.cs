using System;
using System.Collections.Generic;
using System.IO;
using ForgeWeaponEnergyLabExperimental.Native;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Unity refuses duplicate bundle loads, so reuse loaded bundles. Prefabs face +Z; line members are
/// one metre long. Missing resources fail preparation rather than substituting another effect.</summary>
internal static class VfxLibrary
{
    internal const string File = "forge-vfx.bundle";
    private const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly Dictionary<string, GameObject> Prefabs = new(StringComparer.Ordinal);

    internal static System.Collections.IEnumerator Preload()
    {
        foreach (var file in new[] { File, "forge-energy-disc.bundle" })
        {
            AssetBundle? bundle = null;
            foreach (var loaded in AssetBundle.GetAllLoadedAssetBundles_Native())
                if (loaded != null && loaded.name == file) { bundle = loaded; break; }
            if (bundle == null)
            {
                var resource = "ForgeWeaponEnergyLabExperimental.Vfx." + file;
                using var stream = typeof(VfxLibrary).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidDataException("Missing " + resource);
                if (stream.Length < 32 || stream.Length > MaximumBytes) throw new InvalidDataException("VFX bundle size is invalid.");
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                var native = new Il2CppStructArray<byte>(bytes.ToArray());
                var request = AssetBundle.LoadFromMemoryAsync(native);
                while (!request.isDone) yield return null;
                bundle = request.assetBundle; GC.KeepAlive(native);
                if (bundle == null) throw new InvalidDataException("Unity refused " + file);
            }
            var members = file == File ? new[] { "forge_black_hole", "forge_energy_beam", "forge_explosion",
                "forge_flame", "forge_flame_impact", "forge_lightning_arc", "forge_plasma_discharge", "forge_plasma_explosion",
                "forge_plasma_orb", "forge_sparks" } : new[] { "forge_ricochet_disc" };
            foreach (var member in members)
            {
                var request = bundle.LoadAssetAsync(member, Il2CppType.Of<GameObject>());
                while (!request.isDone) yield return null;
                var prefab = AssetRetention.Keep(request.asset?.TryCast<GameObject>() ?? throw new InvalidDataException("Missing VFX " + member));
                if (member == "forge_ricochet_disc")
                {
                    var renderer = prefab.GetComponentInChildren<MeshRenderer>();
                    var mesh = prefab.GetComponentInChildren<MeshFilter>()?.sharedMesh;
                    if (renderer == null || mesh == null || mesh.subMeshCount != 4 || renderer.sharedMaterials.Length != 4)
                        throw new InvalidDataException("Disc prefab requires four surfaces.");
                    // Baked material parameters use the game's shaders, just like native disc construction.
                    for (var i = 0; i < 4; i++)
                    {
                        var material = renderer.sharedMaterials[i] ?? throw new InvalidDataException("Missing disc material.");
                        material.shader = Shader.Find(i == 3 ? "Sprites/Default" : "Standard")
                            ?? throw new InvalidOperationException("Native disc shader is unavailable.");
                        if (i < 3) material.EnableKeyword("_EMISSION");
                    }
                }
                Prefabs[member] = prefab;
                yield return null;
            }
        }
    }

    internal static GameObject Prefab(string member)
    {
        if (Prefabs.TryGetValue(member, out var cached) && cached != null) return cached;
        throw new InvalidOperationException("Energy VFX was not prepared: " + member + ". No synchronous gameplay load is allowed.");
    }
}

/// <summary>One-shots recycle when particles finish; held effects recycle after release. Tinted and untinted
/// instances use separate pools.</summary>
internal sealed class VfxPool : IDisposable
{
    private readonly GameObject _root;
    private readonly List<VfxInstance> _instances = new();
    private bool _disposed;

    internal VfxPool(string name)
    {
        _root = new GameObject(name);
        Object.DontDestroyOnLoad(_root);
    }

    internal VfxInstance Take(string member, Color? tint = null)
    {
        for (var index = _instances.Count - 1; index >= 0; index--)
        {
            var instance = _instances[index];
            if (!instance.Valid) { _instances.RemoveAt(index); continue; }
            if (instance.Is(member, tint) && instance.Free) return instance.Reserve();
        }
        return Create(member, tint).Reserve();
    }

    // One actual instance per preparation step, including particle/light scans, before first wield.
    internal IEnumerable<object?> Prepare(string member, int count, Color? tint = null)
    {
        for (var i = 0; i < count; i++) { Create(member, tint); yield return null; }
    }

    private VfxInstance Create(string member, Color? tint)
    {
        var made = Object.Instantiate(VfxLibrary.Prefab(member), _root.transform, false);
        made.name = tint is { } color ? member + "#" + color : member;
        var created = new VfxInstance(member, tint, made);
        _instances.Add(created);
        return created;
    }

    internal void Clear()
    {
        foreach (var instance in _instances) if (instance.Valid) instance.Kill();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.Clear();
        if (_root != null) Object.Destroy(_root);
    }
}

/// <summary>One pooled prefab instance. Its layers and their names are read once; its Unity particle lights are
/// removed, since <see cref="EnergyLights"/> lights the energy effects with the game's own effect lights.</summary>
internal sealed class VfxInstance
{
    /// <summary>Child states one instance can play at once (e.g. an orb's `charging` and `charged`).</summary>
    private const int StateCapacity = 4;
    private readonly string _member;
    private readonly Color? _tint;
    private readonly GameObject _object;
    private readonly Transform _transform;
    private readonly ParticleSystem _system;
    private readonly ParticleSystem[] _layers;
    private readonly string[] _names;
    private readonly GameObject?[] _states = new GameObject?[StateCapacity];
    private readonly string?[] _stateNames = new string?[StateCapacity];
    private bool _held, _reserved, _started;
    private string? _emitPrefix;
    private bool _emitOn;

    internal VfxInstance(string member, Color? tint, GameObject gameObject)
    {
        _member = member;
        _tint = tint;
        _object = gameObject;
        _transform = gameObject.transform;
        _system = gameObject.GetComponent<ParticleSystem>() ?? throw new InvalidOperationException(member + " has no ParticleSystem at its root.");
        var layers = gameObject.GetComponentsInChildren<ParticleSystem>(true);
        _layers = new ParticleSystem[layers.Length];
        _names = new string[layers.Length];
        for (var index = 0; index < layers.Length; index++)
        {
            var layer = _layers[index] = layers[index];
            _names[index] = layer.name;
            if (tint is { } color) Tint(layer, color);
        }
        // The game's interop exposes no particle lights module, so the module is silenced by removing the template
        // lights it copies (each on its own `<layer>_light` child); the instance spawns none of its own.
        foreach (var template in gameObject.GetComponentsInChildren<Light>(true))
            if (template != null) Object.DestroyImmediate(template.gameObject);
        _object.SetActive(false);
    }

    internal bool Is(string member, Color? tint) => string.Equals(_member, member, StringComparison.Ordinal) && Nullable.Equals(_tint, tint);
    internal bool Valid => _object != null;
    internal bool Free => !_reserved && !_held && (!_started || !_system.IsAlive(true));

    internal VfxInstance Reserve()
    {
        ClearState();
        _reserved = true;
        _held = false;
        _started = false;
        return this;
    }

    internal VfxInstance Hold()
    {
        _held = true;
        return this;
    }

    internal void Place(Vector3 position, Quaternion rotation, float scale = 1f)
    {
        _transform.SetPositionAndRotation(position, rotation);
        _transform.localScale = new Vector3(scale, scale, scale);
    }

    /// <summary>A line member from <paramref name="start"/> to <paramref name="end"/>: its one-metre +Z length stretched.</summary>
    internal void Stretch(Vector3 start, Vector3 end)
    {
        var travel = end - start;
        var length = travel.magnitude;
        _transform.SetPositionAndRotation(start, length > 0.0001f ? Quaternion.LookRotation(travel) : Quaternion.identity);
        _transform.localScale = new Vector3(1f, 1f, Mathf.Max(length, 0.001f));
    }

    internal void Emit(string prefix, bool on)
    {
        // The game's interop can set emission but not read it, so the last switch is remembered here.
        if (_emitPrefix == prefix && _emitOn == on) return;
        _emitPrefix = prefix;
        _emitOn = on;
        for (var index = 0; index < _layers.Length; index++)
        {
            if (!_names[index].StartsWith(prefix, StringComparison.Ordinal)) continue;
            var emission = _layers[index].emission;
            emission.enabled = on;
        }
    }

    internal void Start()
    {
        _object.SetActive(true);
        _system.Clear(true);
        _system.Play(true);
        _started = true;
        _reserved = false;
    }

    internal void Keep()
    {
        if (!_started) Start();
    }

    /// <summary>Lets a held instance go: emission stops, live particles finish, then the pool reuses it.</summary>
    internal void Release()
    {
        if (!_held) return;
        _held = false;
        _reserved = false;
        if (_started && Valid) _system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    /// <summary>Activates and plays the child state <paramref name="child"/> (inactive in the prefab) alongside the
    /// states already playing; a state already playing restarts. A missing child, or more than
    /// <see cref="StateCapacity"/> states at once, is an explicit error, not a silent no-op.</summary>
    internal void PlayState(string child)
    {
        var slot = -1;
        for (var index = 0; index < StateCapacity; index++)
        {
            if (string.Equals(_stateNames[index], child, StringComparison.Ordinal)) { slot = index; break; }
            if (slot < 0 && _stateNames[index] is null) slot = index;
        }
        if (slot < 0) throw new InvalidOperationException(_member + " plays more than " + StateCapacity + " states.");
        var state = _transform.Find(child);
        if (state == null) throw new InvalidOperationException(_member + " has no state child '" + child + "'.");
        var system = state.GetComponent<ParticleSystem>()
            ?? throw new InvalidOperationException(_member + " state '" + child + "' has no ParticleSystem.");
        state.gameObject.SetActive(true);
        system.Clear(true);
        system.Play(true);
        _states[slot] = state.gameObject;
        _stateNames[slot] = child;
    }

    internal void ClearState()
    {
        for (var index = 0; index < StateCapacity; index++)
        {
            var state = _states[index];
            _states[index] = null;
            _stateNames[index] = null;
            if (state is not null && state != null) state.SetActive(false);
        }
    }

    internal void Kill()
    {
        ClearState();
        _held = _reserved = _started = false;
        _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        _object.SetActive(false);
    }

    // A single particle color is stored in the maximum; tint both ends for two-color layers.
    private static void Tint(ParticleSystem layer, Color tint)
    {
        var main = layer.main;
        var color = main.startColor;
        color.m_ColorMin *= tint;
        color.m_ColorMax *= tint;
        main.startColor = color;
    }
}
