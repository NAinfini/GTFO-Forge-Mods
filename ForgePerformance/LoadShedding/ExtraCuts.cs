using System;
using System.Collections.Generic;
using System.Globalization;
using CellMenu;
using Decals;
using Enemies;
using FX_EffectSystem;
using Il2CppInterop.Runtime;
using IRF;
using LevelGeneration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgePerformance.LoadShedding;

// Extra local presentation cuts. Graphics values are set when a level starts and put back when it
// ends. The hooks run on rare events (death, break, spawn, level end), never per frame, except the two
// opt-in IRF hooks that live in IrfCulling. Nothing here submits a game or network command, and a failed
// cut is counted and logged without touching the original call.
internal sealed class ExtraCuts
{
    internal const float SpitterBias = 10f;
    private const int QueueCapacity = 256;
    private const float TickInterval = 0.25f;
    private const float MirrorOpacityVanilla = 0.4f, ReflectionOpacityVanilla = 0.05f;
    private static readonly string[] SpitterTextures = { "_MainTex", "_MetallicGlossMap", "_SSSMap", "_MainTex_B" };

    private readonly LoadSheddingController _owner;
    private readonly SheddingSettings _settings;
    private readonly RenderCuts _render;

    private readonly GraphicsValue<float> _shadow = new();
    private readonly GraphicsValue<bool> _soft = new();
    private StackTraceLogType _traceLog, _traceWarning;
    private bool _traceTrimmed, _mirrorSaved;
    private float _mirrorOpacity, _reflectionOpacity;

    private readonly DueQueue<Decay> _decayQueue = new(QueueCapacity);
    private readonly Dictionary<IntPtr, DecayHook> _decays = new();
    private readonly DueQueue<DestructionObjects> _debrisQueue = new(QueueCapacity);
    private readonly Dictionary<IntPtr, DebrisState> _frozenDebris = new();
    private readonly Dictionary<IntPtr, MipState> _spitterMips = new();
    private readonly HashSet<IntPtr> _recycledGlue = new();
    private readonly System.Random _debrisRandom = new();
    private float _nextTick;
    private int _failedItems, _glueFrame = -1;

    private long _decaySeen, _decayCleared, _decayReused, _decayDropped;
    private long _debrisSeen, _debrisFrozen, _debrisRestored, _debrisDropped;
    private long _leakAssets, _leakLights, _leakIrf, _leakEnemy, _leakTerminals, _leakMessages, _leakHeaders;
    private long _spitterSeen, _spitterChanged, _spitterRestored, _spitterDecals;
    private long _glueSeen, _glueRecycled, _recycleSeen, _recycleChanged;

    internal ExtraCuts(LoadSheddingController owner, SheddingSettings settings)
    { _owner = owner; _settings = settings; _render = new RenderCuts(owner, settings); }

    // ---- plugin lifetime -------------------------------------------------------------------

    internal void Install()
    {
        if (_settings.StackTraceTrim)
        {
            _traceLog = Application.GetStackTraceLogType(LogType.Log);
            _traceWarning = Application.GetStackTraceLogType(LogType.Warning);
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
            _traceTrimmed = true;
            _owner.LogItem("stack-trace-trim", 2, 2, 0, "kinds=Log,Warning\terrors=unchanged");
        }
        if (_settings.GuiMirrorOff) ApplyMirror(true);
        IrfCulling.Configure(_settings);
    }

    internal void Dispose()
    {
        _render.Dispose();
        IrfCulling.Reset();
        MeleePacketReuse.Reset();
        if (_traceTrimmed)
        {
            _traceTrimmed = false;
            Application.SetStackTraceLogType(LogType.Log, _traceLog);
            Application.SetStackTraceLogType(LogType.Warning, _traceWarning);
        }
        if (_mirrorSaved) ApplyMirror(false);
        foreach (var entry in _decays.Values)
            if (entry.Decay != null) entry.Decay.remove_OnDecaySafeToDespawnRenderer(entry.Callback);
        _decays.Clear();
    }

    // ---- level lifetime --------------------------------------------------------------------

    internal void BeginLevel()
    {
        _decaySeen = _decayCleared = _decayReused = _decayDropped = 0;
        _debrisSeen = _debrisFrozen = _debrisRestored = _debrisDropped = 0;
        _spitterSeen = _spitterChanged = _spitterRestored = _spitterDecals = 0;
        _glueSeen = _glueRecycled = _recycleSeen = _recycleChanged = 0;
        _failedItems = 0; _nextTick = 0;
        IrfCulling.BeginLevel();
        _shadow.Reset(); _soft.Reset();
        // Each setting is its own queued piece: the render settings are native calls that rebuild resources, and they must not land on one frame together (nor with the scans).
        _owner.Work.Add("shadow-distance", () => Guard("graphics", ApplyShadowDistance));
        _owner.Work.Add("soft-particles", () => Guard("graphics", ApplySoftParticles));
        _render.BeginLevel();
    }

    // Spitters the level builds are awake before the level is active, so they are swept here like the other cuts' objects.
    internal void BuildDone()
    {
        if (_settings.LowQualitySpitter)
            Guard("low-quality-spitter", () => { foreach (var spitter in Object.FindObjectsOfType<InfectionSpitter>()) SpitterAwake(spitter); });
    }

    private void ApplyShadowDistance()
    {
        if (_settings.ShadowDistance)
        {
            var current = QualitySettings.shadowDistance;
            if (!_shadow.Captured || current == _shadow.Original)
            {
                if (!_shadow.Captured) _shadow.Capture(current);
                var target = ExtraPolicy.ShadowDistance(_shadow.Original, _settings.ShadowDistanceFactor);
                if (target != current) { QualitySettings.shadowDistance = target; _shadow.Written = target; _shadow.Changed = true; }
            }
        }
    }

    private void ApplySoftParticles()
    {
        if (_settings.SoftParticlesOff)
        {
            var current = QualitySettings.softParticles;
            if (!_soft.Captured || current == _soft.Original)
            {
                if (!_soft.Captured) _soft.Capture(current);
                if (current) { QualitySettings.softParticles = false; _soft.Written = false; _soft.Changed = true; }
            }
        }
    }

    internal void Tick()
    {
        var now = Time.time;
        if (now < _nextTick) return;
        _nextTick = now + TickInterval;
        if (_decayQueue.Count != 0)
        {
            try { DrainDecay(now); } catch (Exception error) { Failed("decay-cleanup", error); }
        }
        if (_debrisQueue.Count != 0)
        {
            try { DrainDebris(now); } catch (Exception error) { Failed("debris-freeze", error); }
        }
    }

    private void DrainDecay(float now)
    {
        for (var steps = 0; steps < 8 && _decayQueue.TryTakeDue(now, out var decay, out var generation); steps++)
        {
            if (decay == null) continue;
            if (!_decays.TryGetValue(decay.Pointer, out var entry) || entry.Generation != generation) { _decayReused++; continue; }
            var particles = decay.m_particles;
            if (particles == null) continue;
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _decayCleared++;
        }
    }

    private void DrainDebris(float now)
    {
        for (var steps = 0; steps < 8 && _debrisQueue.TryTakeDue(now, out var debris, out var attempts); steps++)
        {
            var go = debris.m_DestructionObject;
            if (go == null) continue;
            var body = go.GetComponent<Rigidbody>();
            if (body != null && ExtraPolicy.DebrisShouldWait(!body.IsSleeping(), body.velocity.sqrMagnitude, attempts))
            {
                if (!_debrisQueue.Enqueue(debris, now + ExtraPolicy.DebrisRetryDelay, attempts + 1)) _debrisDropped++;
                continue;
            }
            var collider = go.GetComponent<MeshCollider>();
            var state = new DebrisState(collider != null && collider.enabled, body != null && !body.isKinematic, body != null && body.detectCollisions);
            if (collider != null) collider.enabled = false;
            if (body != null) { body.isKinematic = true; body.detectCollisions = false; }
            _frozenDebris[go.Pointer] = state;
            _debrisFrozen++;
        }
    }

    internal void EndLevel()
    {
        Exception? first = null;
        void Try(Action action) { try { action(); } catch (Exception error) { first ??= error; } }
        Try(() =>
        {
            if (_shadow.Changed && QualitySettings.shadowDistance == _shadow.Written) QualitySettings.shadowDistance = _shadow.Original;
            if (_soft.Changed && !QualitySettings.softParticles) QualitySettings.softParticles = _soft.Original;
        });
        Try(_render.EndLevel);
        Try(() =>
        {
            foreach (var mip in _spitterMips.Values)
                if (mip.Texture != null && mip.Texture.mipMapBias == SpitterBias) { mip.Texture.mipMapBias = mip.Original; _spitterRestored++; }
        });
        var (irfConsidered, irfSkipped, irfRedirected, irfFailure) = IrfCulling.Counts;
        if (_settings.SummaryLog)
        {
            if (_settings.ShadowDistance) _owner.LogItem("shadow-distance", _shadow.Captured ? 1 : 0, _shadow.Changed ? 1 : 0, _shadow.Changed ? 1 : 0,
                "original=" + F(_shadow.Original) + "\tfactor=" + F(_settings.ShadowDistanceFactor) + "\twritten=" + F(_shadow.Written) + "\tfloor_m=" + F(ExtraPolicy.ShadowDistanceFloor));
            if (_settings.SoftParticlesOff) _owner.LogItem("soft-particles-off", _soft.Captured ? 1 : 0, _soft.Changed ? 1 : 0, _soft.Changed ? 1 : 0, "original=" + _soft.Original);
            if (_settings.DecayCleanup) _owner.LogItem("decay-cleanup", _decaySeen, _decayCleared, 0,
                "hooked_decays=" + _decays.Count + "\treused_before_clear=" + _decayReused + "\tqueue_full_dropped=" + _decayDropped);
            if (_settings.DebrisFreeze) _owner.LogItem("debris-freeze", _debrisSeen, _debrisFrozen, _debrisRestored,
                "queue_full_dropped=" + _debrisDropped + "\tstill_moving_retries_max=" + ExtraPolicy.DebrisMaximumRetries);
            if (_settings.LowQualitySpitter) _owner.LogItem("low-quality-spitter", _spitterSeen, _spitterChanged, _spitterRestored, "decals_removed=" + _spitterDecals);
            if (_settings.GlueSoundRecycle) _owner.LogItem("glue-sound-recycle", _glueSeen, _glueRecycled, 0, "scope=sound_player_only");
            if (_settings.CorpseRecycle) _owner.LogItem("corpse-recycle", _recycleSeen, _recycleChanged, 0,
                "cap_s=" + F(_settings.CorpseRecycleSeconds) + "\tdespawn=native");
            if (_settings.IrfOffscreenSkip) _owner.LogItem("irf-offscreen-skip", irfConsidered, irfSkipped, 0, "bounds=static_main_camera_frustum");
            if (_settings.IrfWorldCameraOnly) _owner.LogItem("irf-world-camera-only", irfRedirected, irfRedirected, 0, "camera=main");
            if (_settings.MeleePacketReuse)
            {
                var (setups, reused, adopted) = MeleePacketReuse.Counts;
                _owner.LogItem("melee-packet-reuse", setups, reused, 0, "shared_packets_adopted=" + adopted + "\tslots=kept_vanilla_indices");
            }
        }
        if (irfFailure != null) _owner.LogExtraFailure("irf-culling", irfFailure);
        if (_failedItems != 0) _owner.LogItem("extra-failures", _failedItems, 0, 0, "scope=level");
        _decayQueue.Clear(); _debrisQueue.Clear(); _frozenDebris.Clear(); _spitterMips.Clear(); _recycledGlue.Clear();
        PruneDecays();
        if (first != null) throw first;
    }

    private void PruneDecays()
    {
        List<IntPtr>? dead = null;
        foreach (var pair in _decays)
            if (pair.Value.Decay == null) (dead ??= new()).Add(pair.Key);
        if (dead != null) foreach (var key in dead) _decays.Remove(key);
    }

    // ---- hook entry points -----------------------------------------------------------------

    internal void DecayInitialized(Decay decay)
    {
        if (decay == null) return;
        Guard("decay-cleanup", () =>
        {
            var key = decay.Pointer;
            if (!_decays.TryGetValue(key, out var entry))
            {
                var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => DecaySafe(key)));
                entry = new DecayHook(decay, callback!);
                _decays.Add(key, entry);
            }
            entry.Generation++;
            _decaySeen++;
            // Initialize runs once per use of a pooled decay: remove first so a reused instance holds one callback.
            decay.remove_OnDecaySafeToDespawnRenderer(entry.Callback);
            decay.add_OnDecaySafeToDespawnRenderer(entry.Callback);
        });
    }

    private void DecaySafe(IntPtr key)
    {
        try
        {
            if (!_decays.TryGetValue(key, out var entry) || entry.Decay == null) return;
            if (!_decayQueue.Enqueue(entry.Decay, Time.time + ExtraPolicy.DecayClearDelay, entry.Generation)) _decayDropped++;
        }
        catch (Exception error) { Failed("decay-cleanup", error); }
    }

    // Before the native break: undo an earlier freeze so the object breaks as vanilla does again.
    internal void DebrisStarting(DebrisSpawner spawner)
    {
        if (spawner == null) return;
        Guard("debris-freeze", () =>
        {
            var list = spawner.m_destructionObjects;
            if (list == null) return;
            var now = Time.time;
            for (var i = 0; i < list.Count; i++)
            {
                var debris = list[i];
                var go = debris?.m_DestructionObject;
                if (debris == null || go == null) continue;
                if (_frozenDebris.Remove(go.Pointer, out var state)) Unfreeze(go, state);
                // A local generator: a presentation cut never advances Unity's global random sequence.
                var delay = ExtraPolicy.DebrisFreezeMinimum + (float)(_debrisRandom.NextDouble() * (ExtraPolicy.DebrisFreezeMaximum - ExtraPolicy.DebrisFreezeMinimum));
                if (_debrisQueue.Enqueue(debris, now + delay)) _debrisSeen++;
                else _debrisDropped++;
            }
        });
    }

    private void Unfreeze(GameObject go, DebrisState state)
    {
        var collider = go.GetComponent<MeshCollider>();
        if (collider != null && state.ColliderEnabled) collider.enabled = true;
        var body = go.GetComponent<Rigidbody>();
        if (body != null) { body.isKinematic = !state.Dynamic; body.detectCollisions = state.DetectCollisions; }
        _debrisRestored++;
    }

    internal void LeakAfterLevel()
    {
        Guard("leak-cleanup", () =>
        {
            foreach (var asset in Object.FindObjectsOfType<TextAsset>())
                if (asset.bytes.Length == 0) { Object.Destroy(asset); _leakAssets++; }
            // The game empties this queue only when a warden objective recalls; a level that ends otherwise keeps it.
            var intel = GuiManager.PlayerLayer?.m_wardenIntel;
            if (intel != null)
            {
                _leakMessages += intel.m_subObjectiveMessageQueue?.Count ?? 0;
                intel.ResetSubObjectiveMesssagQueue();
            }
            var total = _leakAssets + _leakLights + _leakIrf + _leakEnemy + _leakTerminals + _leakMessages + _leakHeaders;
            // Level teardown runs after EndLevel, so the counts are reported here and then reset.
            _owner.LogItem("leak-cleanup", total, total, 0, "text_assets=" + _leakAssets + "\teffect_lights=" + _leakLights
                + "\tinstanced_render=" + _leakIrf + "\tflyer_materials=" + _leakEnemy + "\tterminal_callbacks=" + _leakTerminals
                + "\thint_messages=" + _leakMessages + "\tmenu_headers=" + _leakHeaders);
            _leakAssets = _leakLights = _leakIrf = _leakEnemy = _leakTerminals = _leakMessages = _leakHeaders = 0;
        });
    }

    // A terminal subscribes a method of itself to the factory's build-done event in Awake and never takes it off,
    // so every destroyed terminal stays reachable through that static delegate. The handler is taken from the
    // delegate's own invocation list, so removing it matches the game's own entry exactly.
    internal void LeakTerminal(LG_ComputerTerminal terminal)
    {
        // Runs inside OnDestroy: a reference check, not Unity's null check, which may already report the object gone.
        if (terminal is null) return;
        Guard("leak-cleanup", () =>
        {
            var handlers = LG_Factory.OnFactoryBuildDone?.GetInvocationList();
            if (handlers == null) return;
            var owner = terminal.Pointer;
            foreach (var handler in handlers)
            {
                if (handler?.Target == null || handler.Target.Pointer != owner) continue;
                LG_Factory.remove_OnFactoryBuildDone(handler.Cast<Il2CppSystem.Action>());
                _leakTerminals++;
            }
        });
    }

    // AddHeader puts a pooled header into the non-content list every time it is shown and ResetHeaders hides the
    // headers without taking them out, so the list grows by one entry per header per reset. Headers are hidden at
    // this point, so their entries are dropped and the next AddHeader adds each shown one once.
    internal void LeakScrollHeaders(CM_ScrollWindow window)
    {
        if (window == null) return;
        Guard("leak-cleanup", () =>
        {
            var headers = window.m_headers;
            var items = window.m_nonContentItems;
            if (headers == null || items == null || items.Count == 0) return;
            var targets = new HashSet<IntPtr>();
            foreach (var header in headers)
                if (header != null) targets.Add(header.Pointer);
            _leakHeaders += ReusePolicy.RemoveMatching(items.Count, i => items[i]?.Pointer ?? IntPtr.Zero, items.RemoveAt, targets);
        });
    }

    internal void MeleeSetup(Gear.MeleeWeaponFirstPerson weapon)
    {
        if (weapon == null) return;
        Guard("melee-packet-reuse", () => MeleePacketReuse.AfterSetup(weapon));
    }

    internal void LeakEffectLights()
    {
        Guard("leak-cleanup", () =>
        {
            var lights = FX_Manager.s_effectPointLights;
            if (lights != null)
                foreach (var light in lights)
                    if (light != null) { Object.Destroy(light.gameObject); _leakLights++; }
            FX_Manager.s_activeLights?.Clear();
            FX_Manager.s_freeLights?.Clear();
        });
    }

    internal void LeakInstancedRender(InstancedRenderFeature feature)
    {
        if (feature == null) return;
        Guard("leak-cleanup", () =>
        {
            // The template object has no parent and shares its buffers; only level copies are released. Only while the
            // level is being torn down: a feature destroyed during play (an enemy's, whose buffers its siblings may share) keeps them.
            if (_owner.Active || feature.transform.parent == null) return;
            var renderer = feature.m_renderer;
            if (renderer != null && renderer.m_compute != null)
            { Object.Destroy(renderer.m_compute); renderer.m_compute = null; _leakIrf++; }
            var materials = feature.m_controller?.Materials;
            if (materials == null) return;
            foreach (var material in materials)
                if (material != null) { Object.Destroy(material); _leakIrf++; }
        });
    }

    internal void LeakEnemy(EnemyAgent enemy)
    {
        if (enemy == null) return;
        Guard("leak-cleanup", () =>
        {
            var flyer = enemy.GetComponentInChildren<FlyerAnimationController>(true);
            var bulbs = flyer?.m_fleshBulbs;
            if (bulbs != null && bulbs.m_material != null) { Object.Destroy(bulbs.m_material); _leakEnemy++; }
        });
    }

    internal void SpitterAwake(InfectionSpitter spitter)
    {
        if (spitter == null) return;
        _spitterSeen++;
        Guard("low-quality-spitter", () =>
        {
            var renderer = spitter.m_renderer;
            var material = renderer == null ? null : renderer.sharedMaterial;
            if (material != null)
                foreach (var name in SpitterTextures)
                {
                    if (!material.HasProperty(name)) continue;
                    var texture = material.GetTexture(name);
                    if (texture == null || _spitterMips.ContainsKey(texture.Pointer)) continue;
                    _spitterMips.Add(texture.Pointer, new MipState(texture, texture.mipMapBias));
                    texture.mipMapBias = SpitterBias;
                }
            var decal = spitter.GetComponentInChildren<Decal>(true);
            if (decal != null) { Object.Destroy(decal); _spitterDecals++; }
            _spitterChanged++;
        });
    }

    internal void GlueDestroying(GlueGunProjectile projectile)
    {
        if (projectile == null) return;
        _glueSeen++;
        Guard("glue-sound-recycle", () =>
        {
            // SyncDestroy ends in Object.Destroy, which takes effect at the end of the frame; after that the
            // Unity null check above stops a second call. So the guard against recycling twice only has to
            // span one frame. Keeping pointers longer named nothing alive: a later ball whose object reused a
            // collected one's address was taken for recycled and kept its sound player.
            var frame = Time.frameCount;
            if (frame != _glueFrame) { _glueFrame = frame; _recycledGlue.Clear(); }
            var sound = projectile.m_sound;
            if (sound == null || !_recycledGlue.Add(projectile.Pointer)) return;
            sound.Recycle();
            _glueRecycled++;
        });
    }

    // Called from the native corpse timer, first step only. The game still despawns the body itself.
    internal void RecycleCorpse(ES_Dead._DecayDelayed_d__20 iterator)
    {
        Guard("corpse-recycle", () =>
        {
            _recycleSeen++;
            ExtraPolicy.RecycleTimer(iterator.delay, iterator.duration, _settings.CorpseRecycleSeconds, out var delay, out var duration);
            if (delay == iterator.delay && duration == iterator.duration) return;
            iterator.delay = delay; iterator.duration = duration;
            _recycleChanged++;
        });
    }

    internal void MirrorBefore()
    {
        try { UI_Apply.s_mirrorOpacity = 0f; UI_Apply.s_reflectionOpacity = 0f; }
        catch (Exception error) { Failed("gui-mirror", error); }
    }

    private void ApplyMirror(bool off)
    {
        if (off)
        {
            _mirrorOpacity = UI_Apply.s_mirrorOpacity; _reflectionOpacity = UI_Apply.s_reflectionOpacity;
            _mirrorSaved = true;
            UI_Apply.s_mirrorOpacity = 0f; UI_Apply.s_reflectionOpacity = 0f;
            UI_Apply.ApplySetttings();
            _owner.LogItem("gui-mirror-off", 1, 1, 0, "original_mirror=" + F(_mirrorOpacity) + "\toriginal_reflection=" + F(_reflectionOpacity));
            return;
        }
        _mirrorSaved = false;
        UI_Apply.s_mirrorOpacity = _mirrorOpacity > 0 ? _mirrorOpacity : MirrorOpacityVanilla;
        UI_Apply.s_reflectionOpacity = _reflectionOpacity > 0 ? _reflectionOpacity : ReflectionOpacityVanilla;
        UI_Apply.ApplySetttings();
    }

    // ---- failure handling ------------------------------------------------------------------

    private void Guard(string item, Action action)
    {
        try { action(); }
        catch (Exception error) { Failed(item, error); }
    }

    private void Failed(string item, Exception error)
    {
        _failedItems++;
        if (_failedItems <= 8) _owner.LogExtraFailure(item, error.GetType().Name + ": " + error.Message);
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class GraphicsValue<T> where T : struct
    {
        internal bool Captured, Changed;
        internal T Original, Written;
        internal void Capture(T value) { Original = value; Written = value; Captured = true; }
        internal void Reset() { Captured = Changed = false; Original = Written = default; }
    }
    private sealed class DecayHook
    {
        internal readonly Decay Decay;
        internal readonly Il2CppSystem.Action Callback;
        internal int Generation;
        internal DecayHook(Decay decay, Il2CppSystem.Action callback) { Decay = decay; Callback = callback; }
    }
    private readonly record struct DebrisState(bool ColliderEnabled, bool Dynamic, bool DetectCollisions);
    private readonly record struct MipState(Texture Texture, float Original);
}
