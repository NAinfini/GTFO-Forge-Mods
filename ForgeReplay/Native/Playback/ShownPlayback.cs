using System.Collections.Generic;
using ForgeAudio;
using ForgeCore;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// Draws and plays again what the recording's libraries showed (<see cref="ShownRecorder"/>; layouts in DEVELOPERS.md) in the replay level, whichever mod made it:
/// an effect by its name (<see cref="Fx.Find"/>), a sound through the library of the same identity (<see cref="AudioLibrary.Find"/>), a model by its name
/// (<see cref="Models.Get"/>), put on the same-named node of the puppet it hung on. What this game or this install does not have is not made up: it goes to
/// <see cref="Skipped"/>. Held effects and beams are told again every frame until the recording releases them; a jump ends everything shown and
/// starts again the held things and looping sounds the recording kept as states (events 102 and 103) at that moment. Main thread only.
/// </summary>
internal static class ShownPlayback
{
    private sealed class HeldFx
    {
        internal ShownKind Kind;
        internal FxHandle? Handle;
        internal int Key;
        internal Vector3 At, To;
        internal float Size;
        internal Color Colour;
        internal bool Head;
        internal Beam? Beam;
        internal Trail? Trail;
    }

    private static readonly Dictionary<int, string> Names = new();
    private static readonly Dictionary<int, FxHandle?> Handles = new();
    private static readonly Dictionary<int, BeamStyle?> BeamStyles = new();
    private static readonly Dictionary<int, TrailStyle?> TrailStyles = new();
    private static readonly Dictionary<ulong, HeldFx> Held = new();
    private static readonly Dictionary<ulong, AudioHandle> Loops = new();
    private static readonly Dictionary<ulong, GameObject> Spawned = new();
    private static readonly Dictionary<ulong, List<Renderer>> Hidden = new();
    // A model or a hidden-renderers state (event 99) whose anchor is not there yet (a puppet or a replayed sentry that is still being made): tried again each frame.
    private static readonly Dictionary<ulong, (ShownModelRecord Record, WorldState State, float GiveUpAt)> Waiting = new();
    private static readonly List<ulong> WaitingDone = new();
    private static readonly List<byte[]> Restoring = new();
    private static WorldState? _restore;
    private static bool _restoreAudible;
    private const float WaitSeconds = 30f;

    /// <summary>One shown thing of the recording, at the moment it happened.</summary>
    internal static void Present(ushort kind, byte[] payload, WorldState state, SoundTier tier, uint timeMs)
    {
        var read = new PayloadReader(payload);
        switch (kind)
        {
            case EventKind.ShownFx:
            case EventKind.ShownHeld: Effect(read, state); break;
            case EventKind.ShownSound:
            case EventKind.ShownLoop: Sound(read, state, tier == SoundTier.All); break;
            case EventKind.ShownModelState: ModelState(ShownCodec.ReadModel(read), state); break;
            case EventKind.ShownPose: ShownPoses.Present(read, state, timeMs); break;
        }
    }

    /// <summary>Per frame: tells every held effect and beam again, as its owner did while it was held.</summary>
    internal static void Frame()
    {
        if (_restore != null)
        {
            // After a jump: every model and hidden list the recording had at that moment is built again from the kept states (the puppets they hang on exist by now).
            var state = _restore;
            _restore = null;
            Restoring.Clear();
            foreach (var ((kind, _), payload) in state.Extras) if (kind == EventKind.ShownModelState) Restoring.Add(payload);
            foreach (var payload in Restoring) ModelState(ShownCodec.ReadModel(new PayloadReader(payload)), state);
            // The held effects, beams, trails and looping sounds the recording had running at that moment start again from their kept state.
            Restoring.Clear();
            foreach (var ((kind, _), payload) in state.Extras) if (kind == EventKind.ShownHeld) Restoring.Add(payload);
            foreach (var payload in Restoring) Effect(new PayloadReader(payload), state);
            Restoring.Clear();
            foreach (var ((kind, _), payload) in state.Extras) if (kind == EventKind.ShownLoop) Restoring.Add(payload);
            foreach (var payload in Restoring) Sound(new PayloadReader(payload), state, _restoreAudible);
        }
        if (Waiting.Count > 0)
        {
            var now = ForgeCore.Frame.UnscaledTime;
            foreach (var (key, wait) in Waiting) if (now >= wait.GiveUpAt || ApplyModel(wait.Record, wait.State)) WaitingDone.Add(key);
            foreach (var key in WaitingDone) if (Waiting.Remove(key, out var gave) && now >= gave.GiveUpAt) Skipped.Note("what a shown model hangs on never appeared", "key " + key);
            WaitingDone.Clear();
        }
        foreach (var held in Held.Values)
        {
            switch (held.Kind)
            {
                case ShownKind.Hold: if (held.Handle != null) Fx.Hold(held.Handle, held.Key, held.At, held.To, held.Size, held.Colour); break;
                case ShownKind.HeldLine: if (held.Handle != null) Fx.Line(held.Handle, held.Key, held.At, held.To, held.Head, held.Colour); break;
                case ShownKind.Beam: if (held.Beam is { Alive: true } beam) beam.Show(held.At, held.To); break;
            }
        }
    }

    /// <summary>A jump or the end of the replay: everything shown goes (held things, looping sounds, models, hidden renderers back).</summary>
    internal static void Jumped(WorldState state, bool audible)
    {
        ShownPoses.Jumped(state);
        EndAll();
        _restore = state;
        _restoreAudible = audible;
    }

    private static void EndAll()
    {
        foreach (var held in Held.Values) Stop(held);
        Held.Clear();
        foreach (var loop in Loops.Values) loop.Stop();
        Loops.Clear();
        foreach (var model in Spawned.Values) if (model != null && !model.WasCollected) Object.Destroy(model);
        Spawned.Clear();
        foreach (var list in Hidden.Values) Models.ShowRenderers(list);
        Hidden.Clear();
        Waiting.Clear();
        _restore = null;
    }

    /// <summary>A new replay: the tables of the last one are dropped as well.</summary>
    internal static void Reset()
    {
        ShownPoses.Reset();
        EndAll();
        Names.Clear(); Handles.Clear(); BeamStyles.Clear(); TrailStyles.Clear();
    }

    private static void Stop(HeldFx held)
    {
        if (held.Handle != null) Fx.Release(held.Handle, held.Key);
        held.Beam?.Release();
        held.Trail?.End();
    }

    // The text of a table entry (event 94), read from the state the timeline holds at this moment.
    internal static string? Name(WorldState state, int id)
    {
        if (id == 0) return null;
        if (Names.TryGetValue(id, out var known)) return known;
        if (!state.Extras.TryGetValue((EventKind.ShownName, (ulong)id), out var entry)) { Skipped.Note("a shown thing's name is missing from the recording", "entry " + id); return null; }
        var r = new PayloadReader(entry); r.U();
        return Names[id] = r.Text();
    }

    private static FxHandle? Handle(WorldState state, int id)
    {
        if (Handles.TryGetValue(id, out var known)) return known;
        var name = Name(state, id);
        var handle = name == null ? null : Fx.Find(name);
        if (name != null && handle == null) Skipped.Note("an effect this game or install does not have", name);
        return Handles[id] = handle;
    }

    private static void Effect(PayloadReader r, WorldState state)
    {
        var e = ShownCodec.ReadFx(r);
        var kind = (ShownKind)e.Kind;
        if (kind == ShownKind.Release)
        {
            if (Held.Remove(e.Key, out var gone)) Stop(gone);
            return;
        }
        var at = new Vector3(e.Ax, e.Ay, e.Az);
        var to = new Vector3(e.Bx, e.By, e.Bz);
        var colour = (Color)new Color32((byte)e.Colour, (byte)(e.Colour >> 8), (byte)(e.Colour >> 16), (byte)(e.Colour >> 24));
        switch (kind)
        {
            case ShownKind.Play: if (Handle(state, e.Name) is { } play) Fx.Play(play, at, to, e.Size, colour); break;
            case ShownKind.Line: if (Handle(state, e.Name) is { } line) Fx.Line(line, at, to, e.Head, colour); break;
            case ShownKind.Hold:
            case ShownKind.HeldLine:
                if (Handle(state, e.Name) is not { } handle) break;
                if (!Held.TryGetValue(e.Key, out var held)) Held[e.Key] = held = new HeldFx { Kind = kind, Handle = handle, Key = Fx.NewKey() };
                held.At = at; held.To = to; held.Size = e.Size; held.Colour = colour; held.Head = e.Head;
                break;
            case ShownKind.Beam: BeamAt(state, e.Key, e.Name, at, to, e.Size, e.Seconds, colour); break;
            case ShownKind.Trail: TrailAt(state, e.Key, e.Name, at, e.Size); break;
            case ShownKind.Decal: DecalAt(state, e.Name, at, to, e.Size, colour, e.Seconds, e.Fade); break;
        }
    }
    private static void BeamAt(WorldState state, ulong key, int look, Vector3 from, Vector3 to, float width, float opacity, Color colour)
    {
        if (!Held.TryGetValue(key, out var held))
        {
            if (!BeamStyles.TryGetValue(look, out var style)) BeamStyles[look] = style = Name(state, look) is { } text ? LookText.Read<BeamStyle>(text) : null;
            if (style == null) return;
            var made = Beams.Create(colour, style);
            if (made == null) { Skipped.Note("a beam could not be made", "style " + look); return; }
            Held[key] = held = new HeldFx { Kind = ShownKind.Beam, Beam = made };
        }
        if (held.Beam is not { Alive: true } beam) return;
        beam.Colour = colour; beam.Width = float.IsNaN(width) ? 1f : width; beam.Opacity = opacity > 0f ? opacity : 1f;
        held.At = from; held.To = to;
        beam.Show(from, to);
    }

    private static void TrailAt(WorldState state, ulong key, int look, Vector3 at, float progress)
    {
        if (!Held.TryGetValue(key, out var held))
        {
            if (!TrailStyles.TryGetValue(look, out var style)) TrailStyles[look] = style = Name(state, look) is { } text ? LookText.Read<TrailStyle>(text) : null;
            if (style == null) return;
            var made = Trails.Rent(at, style);
            if (made == null) { Skipped.Note("a trail could not be made", "style " + look); return; }
            Held[key] = held = new HeldFx { Kind = ShownKind.Trail, Trail = made };
        }
        held.Trail?.Move(at, float.IsNaN(progress) ? 0f : progress);
    }

    private static void DecalAt(WorldState state, int name, Vector3 at, Vector3 normal, float size, Color colour, float life, float fade)
    {
        var sheetName = Name(state, name);
        if (sheetName == null) return;
        var sheet = SpriteSheet.Get(sheetName);
        if (sheet == null) { Skipped.Note("a sprite sheet this install does not have", sheetName); return; }
        ForgeVisual.Decals.Shared?.Place(sheet, at, normal, float.IsNaN(size) ? 1f : size, colour, life, fade);
    }

    // A loop's end is always played out, whatever the sound tier: dropped at a fast speed it would leave the loop sounding to the end of the replay.
    private static void Sound(PayloadReader r, WorldState state, bool audible)
    {
        var s = ShownCodec.ReadSound(r);
        if (s.Name == 0)
        {
            if (Loops.Remove(s.Loop, out var loop)) loop.Stop();
            return;
        }
        if (!audible) return;
        // The keyframe block writes a kept loop again at its start: a loop already sounding is not started twice.
        if (s.Loop != 0 && Loops.TryGetValue(s.Loop, out var sounding) && sounding.Reason == null) return;
        var name = s.Name;
        var at = new Vector3(s.X, s.Y, s.Z);
        var variant = s.Variant; var pitch = s.Pitch; var gain = s.Gain; var loopId = s.Loop; var full = Name(state, name);
        if (full == null) return;
        var slash = full.IndexOf('/');
        var library = slash < 0 ? null : AudioLibrary.Find(full.Substring(0, slash));
        var cue = slash < 0 ? full : full.Substring(slash + 1);
        if (library == null) { Skipped.Note("a sound library this install does not have", slash < 0 ? full : full.Substring(0, slash)); return; }
        if (!library.Has(cue)) { Skipped.Note("a sound this install's library does not have", full); return; }
        var options = new PlayOptions { Gain = gain, Pitch = pitch, Variant = variant };
        var result = s.Place == (byte)ShownPlace.TwoD ? library.Play(cue, options) : library.Play(cue, at, options);
        if (loopId != 0 && result.Handle != null) Loops[loopId] = result.Handle;
    }

    // Event 99: a model or hidden renderers as the recording had them. Built now, or tried every frame until its anchor is there.
    private static void ModelState(in ShownModelRecord m, WorldState state)
    {
        if (m.Phase == ShownCodec.ModelEnded) Waiting.Remove(m.Key);
        if (ApplyModel(m, state)) { Waiting.Remove(m.Key); return; }
        Waiting[m.Key] = (m, state, ForgeCore.Frame.UnscaledTime + WaitSeconds);
    }

    // True when the record is done (applied, or nothing can be done for it), false when its anchor is not there yet.
    private static bool ApplyModel(in ShownModelRecord m, WorldState state)
    {
        switch (m.Phase)
        {
            case ShownCodec.ModelEnded:
                if (Spawned.Remove(m.Key, out var gone) && gone != null && !gone.WasCollected) Object.Destroy(gone);
                if (Hidden.Remove(m.Key, out var shown)) Models.ShowRenderers(shown);
                return true;
            case ShownCodec.ModelHidden:
                {
                    if (Hidden.ContainsKey(m.Key)) return true;
                    var list = new List<Renderer>();
                    var hidden = 0;
                    if (m.Anchor == ShownCodec.WorldRoot)
                    {
                        // Not a player or an enemy: a sentry the game placed, whose look a mod covered, or a pickup a mod hid (Ordnance's
                        // supplies inside a shut pod). The replay's own sentry or item at that place is covered the same way.
                        var at = new Vector3(m.X, m.Y, m.Z);
                        var sentry = Deployables.SentryNear(at);
                        if (sentry != null) hidden = Models.HideSentryBody(sentry, list);
                        else if (EventPresenter.Setter?.ItemNear(at) is { } item) hidden = Models.HideRenderers(item.transform, null, list);
                        else return false;
                    }
                    else
                    {
                        var node = Anchor(m.Anchor, m.Id, m.Path);
                        if (node == null) return false;
                        hidden = Models.HideRenderers(node, null, list);
                    }
                    if (hidden > 0) Hidden[m.Key] = list;
                    return true;
                }
            default:
                {
                    var anchored = m.Anchor is 1 or 2;
                    var at = new Vector3(m.X, m.Y, m.Z);
                    var rotation = new Quaternion(m.Qx, m.Qy, m.Qz, m.Qw);
                    if (Spawned.TryGetValue(m.Key, out var live) && live != null && !live.WasCollected)
                    {
                        var node = live.transform;
                        if (anchored) { node.localPosition = at; node.localRotation = rotation; }
                        else node.SetPositionAndRotation(at, rotation);
                        node.localScale = new Vector3(m.Sx, m.Sy, m.Sz);
                        return true;
                    }
                    var name = Name(state, m.Name);
                    if (name == null) return true;
                    var parent = anchored ? Anchor(m.Anchor, m.Id, m.Path) : null;
                    if (anchored && parent == null) return false;
                    var model = Models.Get(name);
                    if (model == null) { Skipped.Note("a model this install does not have", name); return true; }
                    var made = model.Spawn(at, rotation, parent);
                    if (made == null) return true;
                    if (parent != null) { made.transform.localPosition = at; made.transform.localRotation = rotation; }
                    made.transform.localScale = new Vector3(m.Sx, m.Sy, m.Sz);
                    Spawned[m.Key] = made;
                    return true;
                }
        }
    }

    // The node the recording named (kind 0 nothing, 1 player slot, 2 enemy entity; then a name path), found on the puppet of the same player or enemy.
    private static Transform? Anchor(ulong kind, int id, string path)
    {
        if (kind == 0) return null;
        Transform? node;
        if (kind == 1 && path.StartsWith("~"))
        {
            // The player's animated body: a player puppet's, or the fourth player's body copy when it has no bot slot.
            node = Puppeteer.PlayerOf(id)?.Agent.AnimatorBody?.transform ?? Puppeteer.CosmeticOf(id)?.Animator.transform;
            path = path.Substring(1);
        }
        else node = kind == 1 ? Puppeteer.BodyOf(id)?.transform : Puppeteer.EnemyRoot(id);
        if (path.Length == 0 || node == null) return node;
        foreach (var part in path.Split('/'))
        {
            node = node.Find(part);
            if (node == null) return null;
        }
        return node;
    }
}
