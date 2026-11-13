using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ForgeCore;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// Records what the libraries draw and play (ForgeCore <see cref="Recording"/>: ForgeVisual's effects, lines, held effects, beams, trails, decals and models,
/// ForgeAudio's sounds) from any mod, without that mod knowing: one observer for the length of a recording. A name travels once as a table entry (event 94,
/// a state kept in every keyframe) and afterwards as its number; a held thing (a held effect, a beam, a trail) writes only when its quantised pose or look
/// changed and a release when nobody tells it any more; a model is a state kept in every keyframe (event 99: its name, anchor, pose and scale, rewritten when the quantised pose changes, ended when it is destroyed), and so are the renderers a library hid under a thing, so a jump can build them again. Layouts are in DEVELOPERS.md.
/// While no recording runs nothing observes, so the libraries' report sites cost one field read. Main thread only.
/// </summary>
internal sealed class ShownRecorder : IShownObserver
{
    private struct Held
    {
        internal ulong Kind, Name;
        internal long Ax, Ay, Az, Bx, By, Bz, Size, Colour, Seconds;
        internal int Frame;
    }

    private const int LookBase = 1 << 24, MoveEveryFrames = 3, StaleFrames = 3;

    private static readonly ShownRecorder Instance = new();
    private static IDisposable? _subscription, _notes;
    private static bool[] _named = new bool[256];
    private static readonly List<(object Look, int Id)> Looks = new();
    private static readonly Dictionary<int, Held> HeldNow = new();
    private static readonly List<int> Stale = new();
    private static readonly HashSet<long> Loops = new();
    private static readonly List<TrackedModel> Models = new();
    private static readonly List<(object Token, int Id)> Tokens = new();
    private static int _nextToken;

    /// <summary>Starts observing for a new recording (what an earlier one remembered is dropped).</summary>
    internal static void Start()
    {
        Stop();
        _subscription = Recording.Observe(Instance);
        _notes = Recording.ObserveNotes(Note);
    }

    // 101 Note: Text mod id, Text kind, Text payload (1024 bytes at most), U mark (1: on the timeline). Written on host and client alike, from whatever the mods on this machine note.
    private static void Note(RecordingNote note)
    {
        if (!Rec.On) return;
        try { Rec.Begin().Text(note.ModId).Text(note.Kind).Text(note.Payload, 1024).U(note.Mark ? 1UL : 0UL); Rec.Emit(EventKind.Note); }
        catch (Exception error) { Rec.Fail("note", error); }
    }

    /// <summary>Stops observing and forgets the tables.</summary>
    internal static void Stop()
    {
        _subscription?.Dispose(); _subscription = null;
        _notes?.Dispose(); _notes = null;
        Array.Clear(_named); Looks.Clear(); HeldNow.Clear(); Loops.Clear(); Models.Clear(); Tokens.Clear(); _nextToken = 0;
    }

    /// <summary>Per frame: releases what nobody has told for a few frames, and notes models the game destroyed.</summary>
    internal static void Tick()
    {
        if (_subscription == null || !Rec.On) return;
        var frame = Time.frameCount;
        if (HeldNow.Count > 0)
        {
            foreach (var (key, held) in HeldNow) if (frame - held.Frame >= StaleFrames) Stale.Add(key);
            foreach (var key in Stale) { HeldNow.Remove(key); WriteRelease(key); }
            Stale.Clear();
        }
        if (Models.Count > 0 && frame % MoveEveryFrames == 0)
            for (var i = Models.Count - 1; i >= 0; i--)
            {
                var tracked = Models[i];
                if (tracked.Model == null || tracked.Model.WasCollected)
                {
                    Models.RemoveAt(i);
                    WriteEnd(tracked.Key);
                    continue;
                }
                WriteState(tracked);
            }
    }

    // 99 phase 0: a model's whole state (name, anchor, pose, scale), kept in every keyframe so a jump builds it again. Written when it is made and whenever its
    // quantised pose or scale changed since (a pod falling, a frame following its pilot, a turret turning), so a model at rest writes nothing more.
    private static void WriteState(TrackedModel tracked)
    {
        var t = tracked.Place;
        var local = tracked.Anchor is 1 or 2;
        var p = local ? t.localPosition : t.position;
        var q = local ? t.localRotation : t.rotation;
        var s = t.localScale;
        var last = tracked.Last;
        var changed = !tracked.Written;
        void Put(int i, long v) { if (last[i] != v) { last[i] = v; changed = true; } }
        Put(0, Q(p.x)); Put(1, Q(p.y)); Put(2, Q(p.z));
        Put(3, (long)MathF.Round(q.x * 1000f)); Put(4, (long)MathF.Round(q.y * 1000f)); Put(5, (long)MathF.Round(q.z * 1000f)); Put(6, (long)MathF.Round(q.w * 1000f));
        Put(7, (long)MathF.Round(s.x * 100f)); Put(8, (long)MathF.Round(s.y * 100f)); Put(9, (long)MathF.Round(s.z * 100f));
        if (!changed) return;
        tracked.Written = true;
        ShownCodec.Write(Rec.Begin(), new ShownModelRecord(tracked.Key, ShownCodec.ModelPresent, tracked.Name, tracked.Anchor, tracked.Id, tracked.Path, p.x, p.y, p.z, q.x, q.y, q.z, q.w, s.x, s.y, s.z));
        Rec.EmitState(EventKind.ShownModelState, tracked.Key);
    }

    private static void WriteEnd(ulong key)
    {
        ShownCodec.Write(Rec.Begin(), ShownCodec.EndedModel(key));
        Rec.EmitState(EventKind.ShownModelState, key);
    }

    private sealed class TrackedModel
    {
        internal GameObject Model = null!;
        // Model's transform, read once when it is taken up: an object keeps its transform for its whole life, and Tick drops the model when its life ends.
        internal Transform Place = null!;
        internal ulong Key;
        internal int Name, Id;
        internal byte Anchor;
        internal string Path = "";
        internal bool Written;
        internal readonly long[] Last = new long[10];
    }

    // ---- names -------------------------------------------------------------------------------------------------------------------

    // The table entry of a name, written the first time it is used in this recording; written before the event that uses it (one payload buffer).
    private static ulong NameId(int id)
    {
        if (id == 0) return 0;
        if (id >= _named.Length) Array.Resize(ref _named, Math.Max(id + 1, _named.Length * 2));
        if (_named[id]) return (ulong)id;
        _named[id] = true;
        ShownCodec.Name(Rec.Begin(), id, RecordingNames.Name(id));
        Rec.EmitState(EventKind.ShownName, (ulong)id);
        return (ulong)id;
    }

    // A look object (beam or trail style) as its text, in the same table above the numbers of names.
    private static ulong LookId(object? look)
    {
        if (look == null) return 0;
        foreach (var (known, id) in Looks) if (ReferenceEquals(known, look)) return (ulong)id;
        var next = LookBase + Looks.Count;
        Looks.Add((look, next));
        ShownCodec.Name(Rec.Begin(), next, ForgeVisual.LookText.Write(look), 6000);
        Rec.EmitState(EventKind.ShownName, (ulong)next);
        return (ulong)next;
    }

    private static long Q(float v) => (long)MathF.Round(v * 100f);

    // ---- effects -----------------------------------------------------------------------------------------------------------------

    public void Effect(in ShownEffect e)
    {
        if (!Rec.On) return;
        if (e.Kind == ShownKind.Release)
        {
            if (e.Key != 0 && HeldNow.Remove(e.Key)) WriteRelease(e.Key);
            return;
        }
        var name = e.Kind is ShownKind.Beam or ShownKind.Trail ? LookId(e.Look) : NameId(e.Name);
        var isHeld = e.Kind is ShownKind.Hold or ShownKind.HeldLine or ShownKind.Beam or ShownKind.Trail;
        if (isHeld)
        {
            if (e.Key == 0) return;
            var now = new Held
            {
                Kind = (ulong)e.Kind,
                Name = name,
                Ax = Q(e.At.x),
                Ay = Q(e.At.y),
                Az = Q(e.At.z),
                Bx = Q(e.To.x),
                By = Q(e.To.y),
                Bz = Q(e.To.z),
                Size = float.IsNaN(e.Size) ? long.MinValue : (long)MathF.Round(e.Size * 1000f),
                Colour = Pack(e.Colour),
                Seconds = (long)MathF.Round(e.Seconds * 100f),
                Frame = Time.frameCount,
            };
            ref var held = ref CollectionsMarshal.GetValueRefOrAddDefault(HeldNow, e.Key, out var exists);
            var same = exists && held.Kind == now.Kind && held.Name == now.Name && held.Ax == now.Ax && held.Ay == now.Ay && held.Az == now.Az && held.Bx == now.Bx && held.By == now.By
                && held.Bz == now.Bz && held.Size == now.Size && held.Colour == now.Colour && held.Seconds == now.Seconds;
            held = now;
            if (same) return;
        }
        ShownCodec.Write(Rec.Begin(), new ShownFxRecord((byte)e.Kind, (int)name, (ulong)e.Key, e.At.x, e.At.y, e.At.z, e.To.x, e.To.y, e.To.z, e.Size, (uint)Pack(e.Colour), e.Head, e.Seconds, e.Fade));
        // A held thing is a kept state (102), so a jump lands knowing it; a one-shot (95) is only an event.
        if (isHeld) Rec.EmitState(EventKind.ShownHeld, (ulong)e.Key); else Rec.Emit(EventKind.ShownFx);
    }

    private static long Pack(Color colour)
    {
        Color32 c = colour;
        return (long)c.r | (long)c.g << 8 | (long)c.b << 16 | (long)c.a << 24;
    }

    private static void WriteRelease(int key)
    {
        ShownCodec.Write(Rec.Begin(), new ShownFxRecord(ShownCodec.Release, 0, (ulong)key, 0, 0, 0, 0, 0, 0, float.NaN, ShownCodec.White, true, 0, 0));
        Rec.EmitState(EventKind.ShownHeld, (ulong)key);
    }

    // ---- sounds ------------------------------------------------------------------------------------------------------------------

    public void Sound(in ShownSound s)
    {
        if (!Rec.On) return;
        var name = NameId(s.Name);
        if (name == 0) return;
        ShownCodec.Write(Rec.Begin(), new ShownSoundRecord((int)name, (byte)s.Place, s.Self, s.At.x, s.At.y, s.At.z, s.Variant, s.Pitch, s.Gain, s.Loop > 0 ? (ulong)s.Loop : 0UL));
        // A loop is a kept state (103, until its end), so a jump lands hearing it; any other sound is only an event.
        if (s.Loop > 0) { Loops.Add(s.Loop); Rec.EmitState(EventKind.ShownLoop, (ulong)s.Loop); }
        else Rec.Emit(EventKind.ShownSound);
    }

    public void SoundEnded(long loop)
    {
        if (loop == 0 || !Loops.Remove(loop) || !Rec.On) return;
        ShownCodec.Write(Rec.Begin(), new ShownSoundRecord(0, 0, false, 0, 0, 0, 0, 0, 0, (ulong)loop));
        Rec.EmitState(EventKind.ShownLoop, (ulong)loop);
    }

    // ---- models ------------------------------------------------------------------------------------------------------------------

    public void Model(in ShownModel m)
    {
        if (!Rec.On) return;
        switch (m.Kind)
        {
            case ShownModelKind.Spawned:
                {
                    if (m.Instance == null) return;
                    var tracked = new TrackedModel { Model = m.Instance, Place = m.Instance.transform, Key = ShownCodec.ModelKey(Rec.Key(m.Instance), false), Name = (int)NameId(m.Name) };
                    tracked.Anchor = Resolve(m.On, out tracked.Id, out tracked.Path);
                    Models.Add(tracked);
                    WriteState(tracked);
                    break;
                }
            case ShownModelKind.Hidden:
                {
                    if (m.Token == null || m.On == null) return;
                    // Under a player or an enemy the node is named; under anything else (a sentry the game placed) the root's place in the world finds it again.
                    var kind = Resolve(m.On, out var id, out var path);
                    var at = m.On.position;
                    if (kind == 0) { kind = ShownCodec.WorldRoot; id = 0; path = ""; }
                    var token = ++_nextToken;
                    Tokens.Add((m.Token, token));
                    ShownCodec.Write(Rec.Begin(), new ShownModelRecord(ShownCodec.ModelKey((ulong)token, true), ShownCodec.ModelHidden, 0, kind, id, path, at.x, at.y, at.z, 0, 0, 0, 1, 1, 1, 1));
                    Rec.EmitState(EventKind.ShownModelState, ShownCodec.ModelKey((ulong)token, true));
                    break;
                }
            case ShownModelKind.Shown:
                for (var i = 0; i < Tokens.Count; i++)
                {
                    if (!ReferenceEquals(Tokens[i].Token, m.Token)) continue;
                    WriteEnd(ShownCodec.ModelKey((ulong)Tokens[i].Id, true));
                    Tokens.RemoveAt(i);
                    break;
                }
                break;
        }
    }

    // ---- poses -------------------------------------------------------------------------------------------------------------------

    // A named joint clip on a player or an enemy: a kept state per body and track (a start rewrites it, a stop retires it), so a jump lands knowing what plays.
    // Only the name, the start and the play options are written, never the bones; a clip whose name a replay cannot resolve is skipped there.
    public void Pose(in ShownPose p)
    {
        if (!Rec.On) return;
        byte anchor;
        int id;
        var a = p.Anchor;
        if (a.Kind == AnchorKind.Player && a.Player != null && (uint)a.Player.PlayerSlotIndex < 4) { anchor = 1; id = a.Player.PlayerSlotIndex; }
        else if (a.Kind == AnchorKind.Enemy && a.Enemy != null && EnemyRecorder.Entity(a.Enemy) is var entity && entity >= 0) { anchor = 2; id = entity; }
        else return;
        var key = ShownCodec.PoseKey(anchor, id, p.Track);
        if (p.Kind == ShownPoseKind.Stop)
        {
            ShownCodec.Write(Rec.Begin(), new ShownPoseRecord(true, anchor, id, p.Track, 0, 0, false, 0, 0, 0, 0, null, p.FadeOut));
            Rec.EmitState(EventKind.ShownPose, key);
            return;
        }
        var name = NameId(p.Name);
        if (name == 0 || p.Joints == null) return;
        var bits = 0UL;
        var plain = true;
        for (var j = 0; j < p.Joints.Length; j++)
            if (p.Joints[j] > 0f) { bits |= 1UL << j; plain &= p.Joints[j] >= 0.995f; }
        byte[]? weights = null;
        if (!plain)
        {
            weights = new byte[System.Numerics.BitOperations.PopCount(bits)];
            var at = 0;
            for (var j = 0; j < p.Joints.Length; j++) if (p.Joints[j] > 0f) weights[at++] = (byte)Math.Clamp((int)MathF.Round(p.Joints[j] * 100f), 1, 100);
        }
        var start = (long)PlayerRecorder.Now - (long)MathF.Round(p.Elapsed * 1000f);
        ShownCodec.Write(Rec.Begin(), new ShownPoseRecord(false, anchor, id, p.Track, (int)name, start, p.Loop, p.Weight, p.FadeIn, p.FadeOut, bits, weights, 0));
        Rec.EmitState(EventKind.ShownPose, key);
    }

    // What the transform hangs on: kind (0 nothing, 1 player slot, 2 enemy entity), its number, and the node's name path under the thing's root ("~" first:
    // under the player's animated body, which need not hang below the agent). Kind 0 for a transform that hangs on neither.
    private static byte Resolve(Transform? on, out int id, out string path)
    {
        id = 0; path = "";
        var a = Recording.Anchor(on);
        if (a.Kind == AnchorKind.Player && a.Player != null && (uint)a.Player.PlayerSlotIndex < 4)
        {
            var found = a.Node == null ? "" : PathUnder(a.Node, a.Player.transform);
            if (found == null && a.Player.AnimatorBody != null) found = "~" + PathUnder(a.Node!, a.Player.AnimatorBody.transform);
            if (found != null) { id = a.Player.PlayerSlotIndex; path = found; return 1; }
        }
        else if (a.Kind == AnchorKind.Enemy && a.Enemy != null && EnemyRecorder.Entity(a.Enemy) is var entity && entity >= 0)
        {
            var found = a.Node == null ? "" : PathUnder(a.Node, a.Enemy.transform);
            if (found != null) { id = entity; path = found; return 2; }
        }
        return 0;
    }

    private static string? PathUnder(Transform node, Transform root)
    {
        var path = "";
        for (var t = node; t != null; t = t.parent)
        {
            if (t.Pointer == root.Pointer) return path;
            path = path.Length == 0 ? t.name : t.name + "/" + path;
        }
        return null;
    }
}
