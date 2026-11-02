using System;
using System.Collections.Generic;
using FX_EffectSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>How an effect request ended.</summary>
public enum FxResult
{
    Played,
    /// <summary>The effect exists but this level has not loaded it (the game builds some pools only when gear that uses them is in
    /// the level), or its pack's art could not be loaded.</summary>
    Unavailable,
    /// <summary>The request does not fit the effect: an end for an effect that is not a line, or no end for one that is.</summary>
    Unsupported,
}

/// <summary>An effect resolved by name: a <c>Game:</c> effect or one a pack registered (<see cref="Fx.Register"/>).</summary>
public sealed class FxHandle
{
    internal FxHandle(string name, GameEffect? game, FxPrefab? prefab)
    {
        Name = name;
        Game = game;
        Prefab = prefab;
    }

    public string Name { get; }

    /// <summary>True for an effect drawn from a point to an end (<see cref="Fx.Line"/>).</summary>
    public bool Line => Game?.Line ?? Prefab?.Options.Line ?? false;

    /// <summary>True for an effect that can be held (<see cref="Fx.Hold"/>).</summary>
    public bool Sustained => Prefab != null && Prefab.Options.HoldSeconds > 0f;

    internal GameEffect? Game { get; }
    internal FxPrefab? Prefab { get; }
    internal FX_Pool? Pool;
    internal EX_SpriteMuzzleFlash?[]? Flashes;
    internal int FlashCursor;
}

/// <summary>How a pack's own effect prefab plays.</summary>
public sealed class FxOptions
{
    /// <summary>The size the prefab is authored at (m); a request's size scales it by size / this. NaN: the effect takes no size.</summary>
    public float DefaultSize { get; set; } = float.NaN;

    /// <summary>The prefab's one-metre +Z length is stretched from the point to the end (<see cref="Fx.Line"/>).</summary>
    public bool Line { get; set; }

    /// <summary>Above 0: a sustained effect (the prefab loops at its root). A hold keeps emitting this long after the last call.</summary>
    public float HoldSeconds { get; set; }

    /// <summary>A hold within this many metres of a live copy moves that copy instead of starting another.</summary>
    public float HoldReach { get; set; } = 1.5f;

    /// <summary>Copies kept; a play past that restarts the oldest.</summary>
    public int Copies { get; set; } = 16;
}

/// <summary>
/// Effects by name. <c>Game:</c> is the game's own catalog (<c>Game:FX_Tripmine</c>, <c>Game:FX_Tracer</c>,
/// <c>Game:Sentrygun_MuzzleFlash_3RD</c>, ...), taken through the entries the game itself uses; a pack's own particle prefabs are
/// registered with <see cref="Register"/> and played the same way by any mod. Resolve a name once with <see cref="Find"/>; playing
/// looks nothing up and allocates nothing. Effects are drawn on this peer only.
/// </summary>
public static class Fx
{
    private static readonly NameTable<FxHandle> Registered = new();
    private static readonly Dictionary<string, FxHandle> GameHandles = new(StringComparer.Ordinal);
    private static readonly List<FxPrefab> Prefabs = new();
    private static FxHandle? _tracer;
    private static GameObject? _root;

    /// <summary>The effect <paramref name="name"/>, or null (logged once) when it does not exist.</summary>
    public static FxHandle? Find(string name)
    {
        if (name != null && name.StartsWith(VisualNames.Game + ":", StringComparison.Ordinal))
        {
            if (GameHandles.TryGetValue(name, out var known)) return known;
            if (GameCatalog.TryGet(name, out var effect)) return GameHandles[name] = new FxHandle(name, effect, null);
            Plugin.Logger.ErrorOnce("ForgeVisual: the game's effect catalog has no " + name + ".");
            return null;
        }
        if (Registered.TryGet(name, out var handle)) return handle;
        Plugin.Logger.ErrorOnce("ForgeVisual has no effect " + name + ": " + Missing.Why(name, Bundles.Known, "effect") + ".");
        return null;
    }

    /// <summary>Every <c>Game:</c> effect name.</summary>
    public static IEnumerable<string> GameNames
    {
        get { foreach (var e in GameCatalog.All) yield return e.Name; }
    }

    /// <summary>Registers the particle prefab <paramref name="item"/> of <paramref name="bundle"/> as <c>Pack:item</c>. Null (logged once)
    /// when the name is taken or the prefab has no particle system at its root.</summary>
    public static FxHandle? Register(BundleHandle bundle, string item, FxOptions options)
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        options ??= new FxOptions();
        var name = VisualNames.Join(bundle.Pack, item);
        var log = ForgeCore.Log.For(bundle.ModId);
        if (!bundle.TryModel(item, out var model)) { log.ErrorOnce("ForgeVisual effect " + name + " not registered: the bundle has no prefab " + item + "."); return null; }
        if (model.Prefab.GetComponent<ParticleSystem>() == null) { log.ErrorOnce("ForgeVisual effect " + name + " not registered: its prefab has no particle system at its root."); return null; }
        var prefab = new FxPrefab(name, model.Prefab, options);
        var handle = new FxHandle(name, null, prefab);
        if (Registered.Add(name, handle) is { } why) { log.ErrorOnce("ForgeVisual effect " + name + " not registered: " + why + "."); return null; }
        Prefabs.Add(prefab);
        return handle;
    }

    /// <summary>Plays <paramref name="fx"/> once at <paramref name="position"/>, facing <paramref name="direction"/> (zero: unrotated).</summary>
    public static FxResult Play(FxHandle fx, Vector3 position, Vector3 direction) => Play(fx, position, direction, float.NaN, Color.white);

    /// <summary>Plays <paramref name="fx"/> with a size (m) and a tint. A <c>Game:</c> effect keeps the game's own size and colour;
    /// a pack's effect without a <see cref="FxOptions.DefaultSize"/> ignores the size. The tint goes to the prefab's renderers as
    /// <c>_Color</c> (ForgeVisual's particle shader multiplies by it).</summary>
    public static FxResult Play(FxHandle fx, Vector3 position, Vector3 direction, float size, Color colour)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (fx.Line) return Refuse(fx, "is drawn from a point to an end; use Fx.Line");
        var rotation = direction.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(direction) : Quaternion.identity;
        return fx.Game != null ? PlayGame(fx, position, rotation) : Instances.Play(fx.Prefab!, position, rotation, Vector3.one * Scale(fx.Prefab!, size), colour, false);
    }

    /// <summary>Plays a line effect from <paramref name="from"/> to <paramref name="to"/> (the game's tracer reads its far end from
    /// <c>FX_Manager.EffectTargetPosition</c>, which is written first, the way a weapon does). A line effect registered with
    /// <see cref="FxOptions.HoldSeconds"/> is held like <see cref="Hold"/>: calling this again every frame moves and stretches the one live copy.</summary>
    public static FxResult Line(FxHandle fx, Vector3 from, Vector3 to)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (!fx.Line) return Refuse(fx, "is not a line effect; use Fx.Play");
        var travel = to - from;
        var rotation = travel.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(travel) : Quaternion.identity;
        if (fx.Game == null) return Instances.Play(fx.Prefab!, from, rotation, new Vector3(1f, 1f, travel.magnitude), Color.white, fx.Sustained);
        FX_Manager.EffectTargetPosition = to;
        return PlayGame(fx, from, rotation);
    }

    /// <summary>The game's bullet tracer from <paramref name="from"/> to <paramref name="to"/> (<c>Game:FX_Tracer</c>); very short
    /// shots (under half a metre) draw nothing.</summary>
    public static FxResult Tracer(Vector3 from, Vector3 to)
    {
        if ((to - from).sqrMagnitude < 0.25f) return FxResult.Played;
        _tracer ??= Find(VisualNames.Join(VisualNames.Game, "FX_Tracer"));
        return _tracer == null ? FxResult.Unavailable : Line(_tracer, from, to);
    }

    /// <summary>Holds a sustained effect at a point: a hold near a live copy within <see cref="FxOptions.HoldReach"/> moves it and keeps
    /// it emitting; a copy not held again within <see cref="FxOptions.HoldSeconds"/> stops emitting, lets its particles finish and is
    /// free again.</summary>
    public static FxResult Hold(FxHandle fx, Vector3 position, Vector3 direction, float size, Color colour)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (!fx.Sustained) return Refuse(fx, "is not a sustained effect (register it with HoldSeconds)");
        var rotation = direction.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(direction) : Quaternion.identity;
        return Instances.Play(fx.Prefab!, position, rotation, Vector3.one * Scale(fx.Prefab!, size), colour, true);
    }

    /// <summary>A copy of a <c>Game:</c> muzzle flash under <paramref name="parent"/> (a gun hand, a muzzle), played with
    /// <see cref="AttachedFx.Play"/>; null (logged once) when the flash is not loaded or <paramref name="fx"/> is not a muzzle flash.</summary>
    public static AttachedFx? Attach(FxHandle fx, Transform parent)
    {
        if (fx?.Game is not { Source: GameSource.MuzzleFlash } effect)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual: " + fx?.Name + " is not a muzzle flash; only those can be attached.");
            return null;
        }
        var flash = MakeFlash(effect, parent);
        return flash == null ? null : new AttachedFx(flash);
    }

    private static FxResult PlayGame(FxHandle fx, Vector3 position, Quaternion rotation)
    {
        var effect = fx.Game!;
        if (effect.Source == GameSource.MuzzleFlash)
        {
            var flash = NextFlash(fx);
            if (flash == null) return Unavailable(fx);
            flash.transform.SetPositionAndRotation(position, rotation);
            flash.Play();
            return FxResult.Played;
        }
        var pool = PoolOf(fx);
        if (pool == null) return Unavailable(fx);
        var instance = pool.AquireEffect();
        if (instance == null) return Unavailable(fx);
        instance.Play(null, position, rotation);
        return FxResult.Played;
    }

    private static FX_Pool? PoolOf(FxHandle fx)
    {
        if (fx.Pool != null && !fx.Pool.WasCollected) return fx.Pool;
        fx.Pool = null;
        var effect = fx.Game!;
        try
        {
            if (effect.Source == GameSource.PreloadedPool)
            {
                fx.Pool = FX_Manager.GetPreloadedEffectPool(effect.Native);
                if (fx.Pool == null && effect.AssetPath != null)
                {
                    var prefab = ForgeCore.GameAssets.Get<GameObject>(effect.AssetPath);
                    if (prefab != null) fx.Pool = FX_Manager.GetEffectPool(prefab);
                }
            }
            else
            {
                var prefab = Resources.Load(effect.Native)?.TryCast<GameObject>();
                if (prefab != null) fx.Pool = FX_Manager.GetEffectPool(prefab);
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("ForgeVisual: looking up the game's effect " + fx.Name + " failed: " + error); }
        return fx.Pool;
    }

    private const int FlashCopies = 4;

    private static EX_SpriteMuzzleFlash? NextFlash(FxHandle fx)
    {
        var copies = fx.Flashes ??= new EX_SpriteMuzzleFlash?[FlashCopies];
        var i = fx.FlashCursor++ % copies.Length;
        if (fx.FlashCursor > 1 << 20) fx.FlashCursor = 0;
        var flash = copies[i];
        if (flash != null && !flash.WasCollected) return flash;
        flash = MakeFlash(fx.Game!, Root().transform);
        copies[i] = flash;
        return flash;
    }

    private static EX_SpriteMuzzleFlash? MakeFlash(GameEffect effect, Transform parent)
    {
        var prefab = ForgeCore.GameAssets.Get<GameObject>(effect.Native);
        if (prefab == null) return null;
        var instance = Object.Instantiate(prefab, parent, false);
        var flash = instance.GetComponent<EX_SpriteMuzzleFlash>();
        if (flash != null) return flash;
        Object.Destroy(instance);
        Plugin.Logger.ErrorOnce("ForgeVisual: the game's " + effect.Name + " prefab has no EX_SpriteMuzzleFlash.");
        return null;
    }

    private static FxResult Unavailable(FxHandle fx)
    {
        Plugin.Logger.ErrorOnce("ForgeVisual: the game's effect " + fx.Name + " is not loaded in this level; it is not drawn.");
        return FxResult.Unavailable;
    }

    private static FxResult Refuse(FxHandle fx, string why)
    {
        Plugin.Logger.ErrorOnce("ForgeVisual effect " + fx.Name + " " + why + ".");
        return FxResult.Unsupported;
    }

    private static float Scale(FxPrefab prefab, float size)
        => float.IsNaN(prefab.Options.DefaultSize) || float.IsNaN(size) || prefab.Options.DefaultSize <= 0f ? 1f : size / prefab.Options.DefaultSize;

    internal static GameObject Root()
    {
        if (_root != null && !_root.WasCollected) return _root;
        _root = new GameObject("ForgeVisual effects");
        Object.DontDestroyOnLoad(_root);
        ForgeCore.Assets.Keep(_root);
        return _root;
    }

    internal static void Tick(float now)
    {
        for (var i = 0; i < Prefabs.Count; i++) Prefabs[i].Tick(now);
    }

    /// <summary>Level left: the game's pools belong to its level and are looked up again; every pack effect stops.</summary>
    internal static void Clear()
    {
        foreach (var handle in GameHandles.Values) handle.Pool = null;
        for (var i = 0; i < Prefabs.Count; i++) Prefabs[i].Clear();
    }

    private static class Instances
    {
        internal static FxResult Play(FxPrefab prefab, Vector3 position, Quaternion rotation, Vector3 scale, Color colour, bool hold)
            => prefab.Play(position, rotation, scale, colour, hold) ? FxResult.Played : FxResult.Unavailable;
    }
}

/// <summary>A copy of a game muzzle flash attached to something.</summary>
public sealed class AttachedFx
{
    private readonly EX_SpriteMuzzleFlash _flash;

    internal AttachedFx(EX_SpriteMuzzleFlash flash) => _flash = flash;

    public bool Alive => _flash != null && !_flash.WasCollected;
    public Transform? Transform => Alive ? _flash.transform : null;

    public void Play()
    {
        if (Alive) _flash.Play();
    }

    public void Destroy()
    {
        if (Alive) Object.Destroy(_flash.gameObject);
    }
}

/// <summary>A pack's effect prefab: a fixed set of copies made on first use, placed by each request and recycled once none of their
/// particle systems is alive; sustained copies follow <see cref="HoldBook"/>.</summary>
internal sealed class FxPrefab
{
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private readonly GameObject _prefab;
    private readonly HoldBook _book;
    private readonly GameObject?[] _objects;
    private readonly ParticleSystem?[] _systems;
    private readonly ParticleSystemRenderer[]?[] _renderers;
    private readonly MaterialPropertyBlock _block = new();
    private int _playing;

    internal FxPrefab(string name, GameObject prefab, FxOptions options)
    {
        Name = name;
        _prefab = prefab;
        Options = options;
        var copies = Math.Max(1, options.Copies);
        _book = new HoldBook(copies);
        _objects = new GameObject?[copies];
        _systems = new ParticleSystem?[copies];
        _renderers = new ParticleSystemRenderer[copies][];
    }

    internal string Name { get; }
    internal FxOptions Options { get; }

    internal bool Play(Vector3 position, Quaternion rotation, Vector3 scale, Color colour, bool hold)
    {
        var now = Scene.Now;
        var window = hold ? Options.HoldSeconds : 0f;
        if (hold)
        {
            var near = _book.Near(position.x, position.y, position.z, Options.HoldReach);
            if (near >= 0 && Ready(near))
            {
                _book.Extend(near, position.x, position.y, position.z, now, window);
                Place(near, position, rotation, scale, colour);
                return true;
            }
        }
        var copy = _book.Take();
        if (!Ready(copy) && !Make(copy)) return false;
        var wasPlaying = _book.Playing(copy);
        _book.Start(copy, position.x, position.y, position.z, now, window);
        Place(copy, position, rotation, scale, colour);
        var o = _objects[copy]!;
        var system = _systems[copy]!;
        o.SetActive(true);
        system.Clear(true);
        system.Play(true);
        if (!wasPlaying) _playing++;
        return true;
    }

    private void Place(int copy, Vector3 position, Quaternion rotation, Vector3 scale, Color colour)
    {
        var t = _objects[copy]!.transform;
        t.SetPositionAndRotation(position, rotation);
        t.localScale = scale;
        _block.SetColor(ColorId, colour);
        var renderers = _renderers[copy]!;
        for (var i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(_block);
    }

    private bool Ready(int copy) => _objects[copy] is { } o && !o.WasCollected;

    private bool Make(int copy)
    {
        try
        {
            var o = Object.Instantiate(_prefab, Fx.Root().transform, false);
            o.name = Name;
            o.SetActive(false);
            _objects[copy] = o;
            _systems[copy] = o.GetComponent<ParticleSystem>();
            _renderers[copy] = o.GetComponentsInChildren<ParticleSystemRenderer>(true);
            if (_book.Playing(copy)) { _book.Finished(copy); _playing--; }
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual effect " + Name + " could not be made: " + error);
            return false;
        }
    }

    internal void Tick(float now)
    {
        if (_playing == 0) return;
        for (var i = 0; i < _objects.Length; i++)
        {
            if (!_book.Playing(i)) continue;
            var system = _systems[i];
            if (!Ready(i) || system == null || system.WasCollected) { _book.Finished(i); _playing--; continue; }
            if (_book.Expired(i, now)) system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (system.IsAlive(true)) continue;
            _objects[i]!.SetActive(false);
            _book.Finished(i);
            _playing--;
        }
    }

    internal void Clear()
    {
        for (var i = 0; i < _objects.Length; i++)
        {
            if (!_book.Playing(i)) continue;
            _book.Finished(i);
            if (Ready(i))
            {
                _systems[i]!.Clear(true);
                _objects[i]!.SetActive(false);
            }
        }
        _playing = 0;
    }
}
