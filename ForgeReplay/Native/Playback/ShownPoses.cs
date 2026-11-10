using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// Plays again the named joint clips a recording's mods posed players and enemies with (<see cref="ShownRecorder.Pose"/>, event 98; layout in DEVELOPERS.md), on the
/// puppet of the same player or enemy, whichever mod made them. The clip is found by its name (<see cref="JointClip.Find"/>, or a game clip captured again from the
/// puppet's own skeleton); what this install does not have is not made up: it goes to <see cref="Skipped"/>. The replay's clock drives each clip every frame
/// (<see cref="JointTrack.Seek"/>), so a clip follows speed, pause and step; a jump stops every clip and starts again the ones the kept pose states say play at the
/// moment jumped to. Main thread only.
/// </summary>
internal static class ShownPoses
{
    private sealed class Playing
    {
        internal JointTrack Track;
        internal long StartMs;
    }

    private static readonly Dictionary<ulong, Playing> Tracks = new();
    private static readonly Dictionary<IntPtr, JointPlayer?> Players = new();
    private static readonly Dictionary<int, JointClip?> Clips = new();
    private static readonly List<ulong> Done = new();
    private static WorldState? _restore;
    // A pose whose body is not there yet (its enemy or player puppet spawns a frame later, or comes into range later): tried again every frame while the recording still has it playing.
    private static readonly Dictionary<ulong, ShownPoseRecord> Waiting = new();
    private static readonly List<ulong> Retrying = new();

    /// <summary>One pose event of the recording, at the moment it happened.</summary>
    internal static void Present(PayloadReader r, WorldState state, uint timeMs)
    {
        var p = ShownCodec.ReadPose(r);
        if (p.Stopped) Stop(ShownCodec.PoseKey(p.Anchor, p.Id, p.Track), p.StopFade);
        else Begin(p, state, timeMs);
    }

    /// <summary>Per frame, with the replay's moment: moves every playing clip to where the recording was in it, after a jump starts the ones that were playing.</summary>
    internal static void Frame(uint positionMs)
    {
        if (_restore != null)
        {
            var state = _restore;
            _restore = null;
            foreach (var (_, payload) in StatesOf(state)) Begin(ShownCodec.ReadPose(new PayloadReader(payload)), state, positionMs);
        }
        if (Waiting.Count > 0) Retry(positionMs);
        if (Tracks.Count == 0) return;
        foreach (var (key, playing) in Tracks) if (!playing.Track.Seek((positionMs - playing.StartMs) / 1000f)) Done.Add(key);
        foreach (var key in Done) Tracks.Remove(key);
        Done.Clear();
    }

    /// <summary>A jump: every clip stops at once; the ones playing at the new moment start on the next frame.</summary>
    internal static void Jumped(WorldState state)
    {
        StopAll();
        _restore = state;
    }

    /// <summary>A new replay: nothing plays and the bound bodies are forgotten.</summary>
    internal static void Reset()
    {
        StopAll();
        _restore = null;
        Players.Clear(); Clips.Clear(); _state = null;
    }

    private static IEnumerable<(ulong Key, byte[] Payload)> StatesOf(WorldState state)
    {
        foreach (var ((kind, key), payload) in state.Extras) if (kind == EventKind.ShownPose) yield return (key, payload);
    }

    private static WorldState? _state;

    private static void Retry(uint nowMs)
    {
        Retrying.AddRange(Waiting.Keys);
        foreach (var key in Retrying)
        {
            var p = Waiting[key];
            Waiting.Remove(key);
            if (_state == null || !_state.Extras.ContainsKey((EventKind.ShownPose, key)))
            {
                // An enemy puppet exists only near the followed player (Puppeteer SpawnRadius), so a clip of a far enemy never finds its body: not a fault, and a spawn the game refused is noted there.
                if (p.Anchor == 1) Skipped.Note("a clip's puppet was never there while it played", "pose " + key);
                continue;
            }
            Begin(p, _state, nowMs);
        }
        Retrying.Clear();
    }

    private static void StopAll()
    {
        Waiting.Clear();
        foreach (var playing in Tracks.Values) playing.Track.Stop();
        Tracks.Clear();
    }

    private static void Stop(ulong key, float fade)
    {
        Waiting.Remove(key);
        if (!Tracks.Remove(key, out var playing)) return;
        playing.Track.Stop(fade);
    }

    private static void Begin(in ShownPoseRecord p, WorldState state, uint nowMs)
    {
        var key = ShownCodec.PoseKey(p.Anchor, p.Id, p.Track);
        Stop(key, 0f);
        _state = state;
        var name = ShownPlayback.Name(state, p.Name);
        if (name == null) return;
        var elapsed = (nowMs - p.StartMs) / 1000f;
        var player = Body(p.Anchor, p.Id, out var animator);
        if (player == null)
        {
            Waiting[key] = p;
            return;
        }
        var clip = Clip(p.Name, name, player, animator);
        if (clip == null) return;
        if (!p.Loop && elapsed >= clip.Duration) return;
        var mask = new JointMask();
        var at = 0;
        for (var joint = 0; joint < ClipRig.Count; joint++)
        {
            if ((p.Joints & (1UL << joint)) == 0) continue;
            mask.AddJoints(new[] { joint }, p.Weights == null ? 1f : p.Weights[at++] / 100f);
        }
        try
        {
            var track = player.Play(clip, new JointPlayback(Time.time - Math.Max(0f, elapsed), p.Loop, mask, Math.Clamp(p.Weight, 0f, 1f), p.FadeIn, p.FadeOut));
            Tracks[key] = new Playing { Track = track, StartMs = p.StartMs };
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            Skipped.Note("a clip could not be played on its puppet", name + ": " + error.Message);
        }
    }

    // The player or enemy puppet's body, bound once; null (noted) when the puppet is not there or its body cannot take a clip.
    private static JointPlayer? Body(byte anchor, int id, out Animator? animator)
    {
        animator = null;
        if (anchor == 1)
        {
            var agent = Puppeteer.PlayerOf(id)?.Agent;
            if (agent != null && !agent.WasCollected && agent.AnimatorBody != null)
            {
                animator = agent.AnimatorBody;
                return Bound(agent.Pointer, () => Models.BindJoints(agent));
            }
            // The player with no slot of its own is a copy of a body: its animator is bound the same way, with no agent behind it.
            if (Puppeteer.CosmeticOf(id)?.Animator is not { } copy || copy.WasCollected) return null;
            animator = copy;
            return Bound(copy.Pointer, () => Models.BindJoints(copy, "player copy " + id, out _));
        }
        var enemy = Puppeteer.EnemyAgent(id);
        if (enemy == null || enemy.WasCollected) return null;
        animator = enemy.Anim;
        return Bound(enemy.Pointer, () => Models.BindJoints(enemy, out _));
    }

    private static JointPlayer? Bound(IntPtr body, Func<JointPlayer?> bind)
    {
        if (Players.TryGetValue(body, out var known) && (known == null || known.Root != null && !known.Root.WasCollected)) return known;
        var made = bind();
        if (made == null) Skipped.Note("a puppet's body cannot take a joint clip", "body " + body);
        return Players[body] = made;
    }

    // The clip of a name: one a mod parsed in this process, else a game clip (game/name) captured from the puppet's own skeleton.
    private static JointClip? Clip(int id, string name, JointPlayer player, Animator? animator)
    {
        if (Clips.TryGetValue(id, out var known) && known != null) return known;
        var clip = JointClip.Find(name);
        if (clip == null && name.StartsWith("game/", StringComparison.Ordinal) && animator != null)
        {
            clip = player.CaptureGameClip(name.Substring(5), animator, null, out var refusal);
            if (clip == null) Skipped.Note("a game clip the puppet cannot play", name + ": " + refusal);
        }
        else if (clip == null) Skipped.Note("a clip this install does not have (its mod is not installed)", name);
        return Clips[id] = clip;
    }
}
