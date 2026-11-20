using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeCore;

/// <summary>Per-frame work for mods, so no mod injects a driver component of its own. The callbacks run from ForgeCore's one driver,
/// in registration order: <see cref="OnUpdate"/> in the game's <c>Update</c>, <see cref="OnLateUpdate"/> in <c>LateUpdate</c> (after
/// the game's animation), <see cref="OnFixedUpdate"/> in <c>FixedUpdate</c>, <see cref="OnQuit"/> when the driver is destroyed at shutdown. A callback that throws is reported once
/// under the mod's ID and never stops the others or the next frame. Nothing here allocates on an ordinary frame; keep the callback a
/// static method or a delegate stored once.</summary>
public static class Frame
{
    internal static readonly FrameBook Updates = new("update"), LateUpdates = new("late update"), FixedUpdates = new("fixed update"), Quits = new("quit");

    /// <summary><c>Time.unscaledTime</c> and <c>Time.unscaledDeltaTime</c> as of the start of the driver's last <c>Update</c>: one native read per frame for every
    /// package that times a fade, a wait or a hold, in place of one read at each of its call sites. A reader that runs before the driver in a frame sees the previous
    /// frame's (at most one frame old); anything that must tell whether two reads are of the same frame keeps <c>Time.frameCount</c>.</summary>
    public static float UnscaledTime { get; private set; }

    public static float UnscaledDeltaTime { get; private set; }

    /// <summary><c>Time.time</c> (scaled game time), <c>Screen.width</c> and <c>Screen.height</c> as of the same moment, by the same rule.</summary>
    public static float GameTime { get; private set; }

    /// <summary><c>Time.deltaTime</c> (scaled) as of the same moment, by the same rule.</summary>
    public static float DeltaTime { get; private set; }

    public static int ScreenWidth { get; private set; }

    public static int ScreenHeight { get; private set; }

    /// <summary><c>Time.frameCount</c> as of the same moment: a per-frame cache keyed on it is read again once per driver frame. Input edges, which must
    /// match the game's own input frame, keep <c>Time.frameCount</c>.</summary>
    public static int Count { get; private set; }

    /// <summary>The game's focus state (<c>FocusStateManager.CurrentState</c>: play, the map, the menu, a terminal, a pop-up) for the HUD, the input
    /// leases, the wheel and any mod: read from the game by the first reader of a frame and kept for that frame (keyed on <c>Time.frameCount</c>), so a
    /// reader before the driver sees this frame's focus too. Read it here instead of asking the game.</summary>
    public static eFocusState Focus
    {
        get
        {
            var frame = Time.frameCount;
            if (frame == _focusFrame) return _focus;
            _focusFrame = frame;
            try { _focus = FocusStateManager.CurrentState; }
            catch (Exception error)
            {
                if (!_focusFailed)
                {
                    _focusFailed = true;
                    Plugin.Logger.ErrorOnce("core-reading-focus-state-failed", "Reading the game's focus state failed: " + error);
                }
            }
            return _focus;
        }
    }

    private static eFocusState _focus;
    private static int _focusFrame = -1;
    private static bool _focusFailed;

    /// <summary>Read once at the start of the driver's <c>Update</c>, and once when ForgeCore loads so a reader before the first frame sees real values.</summary>
    internal static void Stamp()
    {
        UnscaledTime = Time.unscaledTime; UnscaledDeltaTime = Time.unscaledDeltaTime; GameTime = Time.time; DeltaTime = Time.deltaTime;
        ScreenWidth = Screen.width; ScreenHeight = Screen.height;
        Count = Time.frameCount;
    }

    /// <summary>Runs in the game's <c>FixedUpdate</c>, before the physics step.</summary>
    public static void OnFixedUpdate(string modId, Action callback) => FixedUpdates.Add(modId, callback);

    public static void OnUpdate(string modId, Action callback) => Updates.Add(modId, callback);

    public static void OnLateUpdate(string modId, Action callback) => LateUpdates.Add(modId, callback);

    public static void OnQuit(string modId, Action callback) => Quits.Add(modId, callback);

    /// <summary>Runs just before each camera culls (<c>Camera.onPreCull</c>), with that camera: after every <c>LateUpdate</c> of the frame, the game's
    /// included, and once per camera that renders. ForgeCore owns the one native subscription: it is installed with the first callback and
    /// removed when the last handle is disposed, so with none nothing runs. Dispose the handle to stop; a callback that throws is reported once.
    /// The interop wraps the camera for each call while any callback is subscribed.</summary>
    public static IDisposable OnPreCull(string modId, Action<Camera> callback) => PreCull.Add(modId, callback);

    /// <summary>Runs as the game's HUD compositor (<c>UI_Apply.OnRenderImage</c>, the image effect that blits a camera's frame with the HUD to its
    /// destination) starts, on every camera that has one, in registration order, with that <c>UI_Apply</c> and the frame it is about to composite;
    /// a step draws onto the frame. Where a stand-in composites for another <c>UI_Apply</c> (<see cref="StandInForHud"/>), the steps get the one
    /// it stands in for and the stand-in's frame. ForgeCore owns the one hook on that method: it goes on with the first step or stand-in and does
    /// nothing once every handle is disposed. A step that throws is reported once and the next step and the game's compositing still run.
    /// A compositor whose material is gone (the game's method would throw on every frame) copies its frame to the destination without the
    /// HUD, reported once.</summary>
    public static IDisposable OnHudComposite(string modId, Action<UI_Apply, RenderTexture> step) => HudComposite.AddStep(modId, step);

    /// <summary><paramref name="standIn"/> composites in place of <paramref name="original"/>, which the caller switches off (a mod that renders the
    /// world camera off screen and puts the result on the screen through a <c>UI_Apply</c> of its own). On the stand-in's composite the frame is
    /// first replaced by what <paramref name="frame"/> returns (null keeps the stand-in's own), then every step runs with
    /// <paramref name="original"/>, so what a step lays over that camera's frame goes onto the picture actually shown. Dispose to end it.
    /// The game destroys a <c>UI_Apply</c>'s material instance in that component's <c>OnDestroy</c>: a stand-in that composites with
    /// <paramref name="original"/>'s instance takes it off its own component when it ends, so neither outlives the other's material.</summary>
    public static IDisposable StandInForHud(string modId, UI_Apply original, UI_Apply standIn, Func<RenderTexture?> frame) => HudComposite.AddStandIn(modId, original, standIn, frame);

    internal static class HudComposite
    {
        private sealed class Entry : IDisposable
        {
            internal Entry(string modId) { ModId = modId; }
            internal readonly string ModId;
            internal Action<UI_Apply, RenderTexture>? Step;
            internal UI_Apply? Original, StandIn;
            internal Func<RenderTexture?>? Frame;
            internal bool Ended, Reported;
            public void Dispose() => Ended = true;
        }

        private static readonly List<Entry> Steps = new(), StandIns = new();
        private static bool _patched;

        internal static IDisposable AddStep(string modId, Action<UI_Apply, RenderTexture> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            return Add(Steps, new Entry(Named(modId)) { Step = step });
        }

        internal static IDisposable AddStandIn(string modId, UI_Apply original, UI_Apply standIn, Func<RenderTexture?> frame)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (standIn == null) throw new ArgumentNullException(nameof(standIn));
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            return Add(StandIns, new Entry(Named(modId)) { Original = original, StandIn = standIn, Frame = frame });
        }

        private static string Named(string modId) =>
            string.IsNullOrWhiteSpace(modId) ? throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId)) : modId;

        private static IDisposable Add(List<Entry> list, Entry entry)
        {
            list.RemoveAll(static ended => ended.Ended);
            list.Add(entry);
            if (!_patched)
            {
                _patched = true;
                Patches.Lazy(Plugin.Id, harmony => harmony.PatchAll(typeof(HudCompositePatch))).Ensure();
            }
            return entry;
        }

        internal static void Run(UI_Apply hud, ref RenderTexture frame)
        {
            var pointer = hud.Pointer;
            for (var i = 0; i < StandIns.Count; i++)
            {
                var entry = StandIns[i];
                if (entry.Ended || entry.StandIn!.Pointer != pointer) continue;
                hud = entry.Original!;
                try
                {
                    var replaced = entry.Frame!();
                    if (replaced != null) frame = replaced;
                }
                catch (Exception error) { Report(entry, error); }
                break;
            }
            for (var i = 0; i < Steps.Count; i++)
            {
                var entry = Steps[i];
                if (entry.Ended) continue;
                try { entry.Step!(hud, frame); }
                catch (Exception error) { Report(entry, error); }
            }
        }

        private static void Report(Entry entry, Exception error)
        {
            if (entry.Reported) return;
            entry.Reported = true;
            Log.For(entry.ModId).ErrorOnce("core-callback-failed", entry.ModId + " HUD composite " + (entry.Step != null ? "step" : "stand-in") + " failed: " + error);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(UI_Apply), nameof(UI_Apply.OnRenderImage))]
    private static class HudCompositePatch
    {
        // The game's compositor is Graphics.Blit(src, dest, its material instance) with no check, and Blit throws on a material that is
        // null or destroyed. The game makes the instance in the UI_Apply's Start and destroys it only in that UI_Apply's OnDestroy, so a
        // compositor that still draws without one shares it with another that was destroyed (a stand-in holding its camera's instance):
        // the game's method would throw on every frame. The field is read at its offset, without a wrapper.
        private static readonly int MaterialField = NativeObjects.FieldOffset<UI_Apply>(nameof(UI_Apply.m_UIBlitterMaterial_Instance)),
            NativeField = NativeObjects.FieldOffset<UnityEngine.Object>(nameof(UnityEngine.Object.m_CachedPtr));
        private static bool _reported;

        private static bool Prefix(UI_Apply __instance, ref RenderTexture src, RenderTexture dest)
        {
            HudComposite.Run(__instance, ref src);
            var material = Marshal.ReadIntPtr(__instance.Pointer, MaterialField);
            if (material != IntPtr.Zero && Marshal.ReadIntPtr(material, NativeField) != IntPtr.Zero) return true;
            Graphics.Blit(src, dest); // the frame goes on without the HUD instead of not at all
            if (_reported) return false;
            _reported = true;
            Plugin.Logger.ErrorOnce("core-hud-compositor-without-material", "The game's HUD compositor on " + __instance.name
                + " has no material (" + (material == IntPtr.Zero ? "none set" : "destroyed") + "): its frames are shown without the HUD. Reported once.");
            return false;
        }
    }

    private static class PreCull
    {
        private sealed class Entry : IDisposable
        {
            internal Entry(string modId, Action<Camera> callback) { ModId = modId; Callback = callback; }
            internal readonly string ModId;
            internal readonly Action<Camera> Callback;
            internal bool Ended, Reported;
            public void Dispose() { if (Ended) return; Ended = true; Prune(); }
        }

        private static readonly List<Entry> Entries = new();
        private static Camera.CameraCallback? _native;
        private static bool _running;

        internal static IDisposable Add(string modId, Action<Camera> callback)
        {
            if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var entry = new Entry(modId, callback);
            Entries.Add(entry);
            if (_native == null)
            {
                _native = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(Run));
                Camera.onPreCull = Camera.onPreCull == null ? _native : Il2CppSystem.Delegate.Combine(Camera.onPreCull, _native).Cast<Camera.CameraCallback>();
            }
            return entry;
        }

        private static void Run(Camera camera)
        {
            _running = true;
            var count = Entries.Count;
            for (var i = 0; i < count; i++)
            {
                var entry = Entries[i];
                if (entry.Ended) continue;
                try { entry.Callback(camera); }
                catch (Exception error)
                {
                    if (entry.Reported) continue;
                    entry.Reported = true;
                    Log.For(entry.ModId).ErrorOnce("core-callback-failed", entry.ModId + " pre-cull callback failed: " + error);
                }
            }
            _running = false;
            Prune();
        }

        // Drops ended handles (after a run, never during one) and takes the native callback off once none is left.
        private static void Prune()
        {
            if (_running) return;
            Entries.RemoveAll(static entry => entry.Ended);
            if (Entries.Count != 0 || _native == null) return;
            Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, _native)?.Cast<Camera.CameraCallback>();
            _native = null;
        }
    }
}
