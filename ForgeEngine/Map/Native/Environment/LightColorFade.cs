using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// One colour and intensity transition over the light objects one request named, and the registry that drives the
/// transitions in flight.
///
/// The game has no gradient entry for a light: `LG_Light` carries `ChangeColor`/`ChangeIntensity` and a
/// `GetIntensity` read, and every vanilla colour scheme is applied in one step by `LG_BuildZoneLightsJob`. A
/// transition therefore has to be written frame by frame by this package, which is what this file is — the one
/// place the mod holds these native field receipts. A receipt survives completion so cleanup can restore it.
///
/// A transition's start values are sampled when it is scheduled and never when it was first written, so a request
/// that arrives while another is still running continues from the colour and intensity on the light and the two
/// never fight over it: the newer request replaces the older one for its own zone and category, and the older one
/// is dropped rather than left to drag the light to a value nobody asked for any more.
///
/// The intensity a transition reaches is absolute; the zone ledger computes it as the author's set value times the
/// light's native intensity (<see cref="ZoneOwnership.OriginalIntensity"/>), so a repeated request does not compound,
/// and a request that only changes colour passes no intensity at all.
/// </summary>
internal sealed class LightColorFade
{
    // Compare native colour components without boxing an interop struct on each frame.
    private static bool SameColor(Color a, Color b)
        => a.r.Equals(b.r) && a.g.Equals(b.g) && a.b.Equals(b.b) && a.a.Equals(b.a);
    /// <summary>One zone and one light category a transition drives. It is the registry's key because it is also
    /// what "the same request again" means: a second transition for the same zone and category replaces the first,
    /// and a transition over a different category of the same zone is a request about other lights and leaves it
    /// running.</summary>
    internal readonly struct Key : IEquatable<Key>
    {
        internal Key(int dimension, int layer, int zone, int category)
        { Dimension = dimension; Layer = layer; Zone = zone; Category = category; }

        internal int Dimension { get; }
        internal int Layer { get; }
        internal int Zone { get; }
        /// <summary>The `LG_Light.LightCategory` the transition is limited to, or <see cref="EveryCategory"/>.</summary>
        internal int Category { get; }

        public bool Equals(Key other) => Dimension == other.Dimension && Layer == other.Layer
            && Zone == other.Zone && Category == other.Category;
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode() => HashCode.Combine(Dimension, Layer, Zone, Category);
    }

    /// <summary>The key member that means "the request named no category", so every light of the zone is driven.</summary>
    internal const int EveryCategory = -1;

    private sealed class Target
    {
        internal LG_Light Light = null!;
        internal Color FromColor;
        internal Color ToColor;
        internal float FromIntensity;
        internal float ToIntensity;
        internal bool ChangeColor, ChangeIntensity;
        internal ZoneOwnership? Zone;
    }

    // A completed transition still owns its fields until world/session cleanup. Successive requests share
    // this receipt, so cancellation restores the native baseline rather than an earlier Forge colour.
    internal sealed class ZoneOwnership
    {
        internal readonly LG_Light Light;
        private ForgeVisual.FieldReceipt<Color> _color;
        private ForgeVisual.FieldReceipt<float> _intensity;

        internal ZoneOwnership(LG_Light light)
        { Light = light; _color = new(light.m_color, SameColor); _intensity = new(light.GetIntensity()); }

        /// <summary>The light's native intensity before the first Forge write: the baseline an absolute
        /// brightness (a compounded multiplier) is applied against.</summary>
        internal float OriginalIntensity => _intensity.Original;

        internal void Write(Color color, float intensity, bool changeColor, bool changeIntensity)
        {
            if (changeColor && !_color.Accepts(Light.m_color)
                || changeIntensity && !_intensity.Accepts(Light.GetIntensity()))
                throw new InvalidOperationException("light-color-ownership-lost");
            if (changeColor)
            {
                try { Light.ChangeColor(color); }
                finally { if (SameColor(Light.m_color, color)) _color.Written(color); }
                if (!SameColor(Light.m_color, color)) throw new InvalidOperationException("light-color-readback");
            }
            if (changeIntensity)
            {
                try { Light.ChangeIntensity(intensity); }
                finally { if (Light.GetIntensity().Equals(intensity)) _intensity.Written(intensity); }
                if (!Light.GetIntensity().Equals(intensity)) throw new InvalidOperationException("light-intensity-readback");
            }
        }

        internal void Restore()
        {
            if (Light == null || Light.WasCollected) return;
            if (_color.Release(Light.m_color))
            {
                Light.ChangeColor(_color.Original);
                if (!SameColor(Light.m_color, _color.Original)) throw new InvalidOperationException("light-color-restore-readback");
            }
            if (_intensity.Release(Light.GetIntensity()))
            {
                Light.ChangeIntensity(_intensity.Original);
                if (!Light.GetIntensity().Equals(_intensity.Original)) throw new InvalidOperationException("light-intensity-restore-readback");
            }
        }

        internal void Rollback(Color color, float intensity, bool changeColor, bool changeIntensity)
        {
            if (changeColor && !SameColor(Light.m_color, color))
            {
                if (!_color.Release(Light.m_color)) throw new InvalidOperationException("light-color-rollback-ownership");
                Light.ChangeColor(color);
                if (!SameColor(Light.m_color, color)) throw new InvalidOperationException("light-color-rollback-readback");
                _color.Written(color);
            }
            if (changeIntensity && !Light.GetIntensity().Equals(intensity))
            {
                if (!_intensity.Release(Light.GetIntensity())) throw new InvalidOperationException("light-intensity-rollback-ownership");
                Light.ChangeIntensity(intensity);
                if (!Light.GetIntensity().Equals(intensity)) throw new InvalidOperationException("light-intensity-rollback-readback");
                _intensity.Written(intensity);
            }
        }
    }

    private readonly List<Target> _targets;
    private readonly float _seconds;
    private float _elapsed;

    private LightColorFade(List<Target> targets, float seconds)
    { _targets = targets; _seconds = seconds; }

    /// <summary>Whether the transition has written its last value. A transition of no length is applied when it is
    /// scheduled and answered as already done, which is how `transition: 0` is the write itself.</summary>
    internal bool Done => _elapsed >= _seconds;

    /// <summary>How many light objects the transition drives, which is what a caller reports as the work a request
    /// asked for. A request that matched no light is refused rather than answered as a transition over none.</summary>
    internal int Lights => _targets.Count;

    /// <summary>The transition toward values the caller names per light: the colour and the absolute intensity each
    /// light of the category should reach, or null for a field it leaves alone. A light that changes neither is
    /// not part of the transition; null when no light changes. An immediate transition is returned completed, so
    /// scheduling can still supersede older writers of those exact native fields. The start values are what the
    /// light shows now, so a request that arrives mid-transition continues from there.</summary>
    internal static LightColorFade? Toward(IReadOnlyList<LG_Light> lights, int category, float seconds,
        Func<LG_Light, Color?> color, Func<LG_Light, float?> intensityOf)
    {
        var targets = new List<Target>(lights.Count);
        foreach (var light in lights)
        {
            if (light == null) continue;
            if (category != EveryCategory && (int)light.m_category != category) continue;
            var to = color(light);
            var level = intensityOf(light);
            if (to == null && level == null) continue;
            float intensity = light.GetIntensity();
            var current = light.m_color;
            targets.Add(new Target
            {
                Light = light,
                FromColor = current,
                ToColor = to ?? current,
                FromIntensity = intensity,
                ToIntensity = level ?? intensity,
                ChangeColor = to.HasValue,
                ChangeIntensity = level.HasValue,
                Zone = LightColorFades.Own(light)
            });
        }
        if (targets.Count == 0) return null;
        var fade = new LightColorFade(targets, Math.Max(0f, seconds));
        // A transition with a length is driven by the frames that follow and writes nothing here; one without a
        // length has no frames of its own, so the value it asked for is written now and it is done.
        if (!fade.Done) return fade;
        try { fade.Write(1f); }
        catch (Exception error)
        {
            try { fade.Rollback(); }
            catch (Exception rollback) { throw new AggregateException("light-color-rollback-failed", error, rollback); }
            throw;
        }
        return fade;
    }

    internal void Rollback()
    {
        List<Exception>? failures = null;
        foreach (var target in _targets)
        {
            if (target.Zone == null) continue;
            try { target.Zone.Rollback(target.FromColor, target.FromIntensity, target.ChangeColor, target.ChangeIntensity); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("light-color-rollback-failed", failures);
    }

    /// <summary>One frame of the transition. A frame that arrives after the last one writes nothing, so a caller
    /// that ticks a transition it already finished cannot restart it.</summary>
    internal void Advance(float deltaSeconds)
    {
        if (Done) return;
        _elapsed += Math.Max(0f, deltaSeconds);
        Write(_seconds <= 0f ? 1f : Math.Min(1f, _elapsed / _seconds));
    }

    internal bool SupersedeBy(LightColorFade newer)
    {
        var active = false;
        foreach (var target in _targets)
        {
            if (target.Zone == null) { active = true; continue; }
            foreach (var replacement in newer._targets)
            {
                if (replacement.Zone == null || replacement.Light.Pointer != target.Light.Pointer) continue;
                if (replacement.ChangeColor) target.ChangeColor = false;
                if (replacement.ChangeIntensity) target.ChangeIntensity = false;
            }
            active |= target.ChangeColor || target.ChangeIntensity;
        }
        return active;
    }

    /// <summary>Writes the transition's value at one point of its own length, linearly. Nothing here reads the
    /// clock: the caller owns the frame and the seconds it stands for.</summary>
    private void Write(float progress)
    {
        foreach (var target in _targets)
        {
            if (target.Light == null) continue;
            target.Zone!.Write(Color.Lerp(target.FromColor, target.ToColor, progress),
                target.FromIntensity + (target.ToIntensity - target.FromIntensity) * progress,
                target.ChangeColor, target.ChangeIntensity);
        }
    }
}

/// <summary>
/// The transitions one world has in flight, keyed by the zone and category each is about. There is no ticker
/// here: the frame is the game's own, and the one patch that owns it calls <see cref="Tick"/>.
///
/// The world the transitions belong to is carried with them, because a light object does not survive the level it
/// was built in: a tick from a later world drops the whole table instead of reaching into objects the level
/// teardown already took, which is the one failure a frame-by-frame write could otherwise walk into.
/// </summary>
internal static class LightColorFades
{
    private readonly record struct Entry(LightColorFade.Key Key, LightColorFade Fade);
    private static readonly List<Entry> Active = new();
    private static readonly List<LightColorFade.ZoneOwnership> Owned = new();
    private static long _world;

    internal static LightColorFade.ZoneOwnership Own(LG_Light light)
    {
        foreach (var receipt in Owned)
            if (receipt.Light.Pointer == light.Pointer) return receipt;
        var added = new LightColorFade.ZoneOwnership(light); Owned.Add(added); return added;
    }

    /// <summary>Hands the given lights back to the level: each receipt this package holds for one of them restores the
    /// native colour and intensity it found, and is dropped. Only a field still showing this package's last write is touched.</summary>
    internal static void Release(IEnumerable<LG_Light> lights)
    {
        foreach (var light in lights)
            for (var index = Owned.Count - 1; index >= 0; index--)
                if (Owned[index].Light.Pointer == light.Pointer) { Owned[index].Restore(); Owned.RemoveAt(index); }
    }

    internal static void Prepare(long world)
    {
        if (_world == world) return;
        Clear(); _world = world;
    }

    /// <summary>How many transitions are in flight. It is what a caller reads to see the table without walking it,
    /// and it is the whole state this package keeps between frames.</summary>
    internal static int Count => Active.Count;
    internal static void Cancel(LightColorFade.Key key) { for (int i = Active.Count - 1; i >= 0; i--) if (Active[i].Key.Equals(key)) Active.RemoveAt(i); }

    /// <summary>Drops every transition of one zone, whatever its category.</summary>
    internal static void CancelZone(int dimension, int layer, int zone)
    {
        for (int i = Active.Count - 1; i >= 0; i--)
            if (Active[i].Key.Dimension == dimension && Active[i].Key.Layer == layer && Active[i].Key.Zone == zone) Active.RemoveAt(i);
    }

    /// <summary>A host migration keeps the level and its lights: the table moves to the new world number instead
    /// of being dropped, which would hand every light back to the level.</summary>
    internal static void FollowWorld(long from, long to)
    {
        if (_world == from) _world = to;
    }

    /// <summary>Announced when this table goes from empty to holding a transition. The table has no clock of its
    /// own — the session's <see cref="MapClock"/> drives it — and its existing lifecycle registration
    /// observes the new work without attempting to register during command dispatch. Unset means nobody drives
    /// this table, which is what the tests that advance it by hand want.</summary>
    internal static Action? WorkScheduled { get; set; }

    /// <summary>Puts one transition under its key, replacing whatever the key already held — which is exactly the
    /// "the same request again" case: the new transition already sampled the lights' current values, so the one it
    /// replaces would only drag them back. A transition over no light is not scheduled at all.
    ///
    /// The first schedule of a world adopts that world and drops anything an earlier one left behind.</summary>
    internal static void Schedule(long world, LightColorFade.Key key, LightColorFade? fade)
    {
        if (fade == null) return;
        if (world != _world) { Active.Clear(); _world = world; }
        for (var index = Active.Count - 1; index >= 0; index--)
            if (!Active[index].Fade.SupersedeBy(fade)) Active.RemoveAt(index);
        Cancel(key);
        if (fade.Done) return;
        Active.Add(new(key, fade));
        WorkScheduled?.Invoke();
    }

    /// <summary>One frame for every transition of the given world. A tick from another world is the level teardown
    /// between them, so the table is dropped rather than advanced: its lights are not the standing level's.</summary>
    internal static void Tick(long world, float deltaSeconds)
    {
        if (world != _world) { Clear(); return; }
        if (Active.Count == 0) return;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var fade = Active[i].Fade;
            try { fade.Advance(deltaSeconds); }
            catch (Exception error)
            {
                Active.RemoveAt(i);
                MapLog.Record(ForgeEngine.Framework.RuntimeLogLevel.Error, MapLogCodes.NativeReport,
                    "A native light transition failed.",
                    ForgeEngine.Framework.RuntimeLogValue.Quote("source", "map.light-color-failed"),
                    ForgeEngine.Framework.RuntimeLogValue.Quote("detail", error.GetType().Name));
                try { fade.Rollback(); }
                catch (Exception rollback) { throw new AggregateException("light-color-rollback-failed", error, rollback); }
                throw;
            }
            if (fade.Done) Active.RemoveAt(i);
        }
    }

    /// <summary>Drops every transition. The session calls it when it goes away, so a clock that is somehow still
    /// registered for a frame after teardown finds nothing to write into.</summary>
    internal static void Clear()
    {
        Active.Clear();
        List<Exception>? failures = null;
        for (var index = Owned.Count - 1; index >= 0; index--)
        {
            try { Owned[index].Restore(); Owned.RemoveAt(index); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("light-color-restore-failed", failures);
        _world = 0;
    }
}
