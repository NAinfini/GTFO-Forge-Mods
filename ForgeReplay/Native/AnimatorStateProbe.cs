using System;
using System.IO;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Writes what the body animators of the players this machine does not own play, to <c>animator-states.log</c> in the replay folder, one line
/// per change of a layer's state (the game's state hash, <c>Animator.StringToHash</c> of the state name), and one line for every interact, throw, reload
/// and melee event the recorder writes. A remote player's body is the only place the third-person swing, throw and interact states show, so one multiplayer
/// game with someone swinging, throwing and interacting gives the numbers a replay needs to play them on a body copy (<see cref="CosmeticPlayer"/>). Main thread only.</summary>
internal static class AnimatorStateProbe
{
    private const int Layers = 16, MaxLines = 20000;
    private const float IntervalSeconds = 0.1f;
    private static readonly int[,] Last = new int[4, Layers];
    private static StreamWriter? _file;
    private static float _next;
    private static int _lines;

    internal static void Start()
    {
        Stop();
        if (!Plugin.LogRemoteAnimatorStates.Value) return;
        try
        {
            Directory.CreateDirectory(Plugin.ReplayFolder);
            _file = new StreamWriter(Path.Combine(Plugin.ReplayFolder, "animator-states.log"), true) { AutoFlush = true };
            _file.WriteLine("# " + DateTime.Now.ToString("s") + " new recording; columns: t slot layer state next-state normalized-time, EVENT lines: kind and its numbers");
            Array.Clear(Last, 0, Last.Length);
            _lines = 0; _next = 0f;
        }
        catch (Exception error) { _file = null; Plugin.Logger.ErrorOnce("The animator state log could not be opened: " + error.Message); }
    }

    internal static void Stop() { _file?.Dispose(); _file = null; }

    /// <summary>One frame's look at one player; the local player's own body does not play the third-person states, so it is not read.</summary>
    internal static void Look(PlayerAgent agent, int slot)
    {
        if (_file == null || agent.IsLocallyOwned || _lines >= MaxLines) return;
        var animator = agent.AnimatorBody;
        if (animator == null || animator.WasCollected) return;
        var layers = Math.Min(animator.layerCount, Layers);
        for (var layer = 0; layer < layers; layer++)
        {
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            var hash = state.shortNameHash;
            if (hash == Last[slot, layer]) continue;
            Last[slot, layer] = hash;
            var next = animator.IsInTransition(layer) ? animator.GetNextAnimatorStateInfo(layer).shortNameHash : 0;
            Write(slot + " " + layer + " " + hash + " " + next + " " + state.normalizedTime.ToString("0.00"));
        }
    }

    /// <summary>Whether this frame is one to look at the players in (ten times a second while the log is open).</summary>
    internal static bool Due()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (_file == null || now < _next) return false;
        _next = now + IntervalSeconds;
        return true;
    }

    /// <summary>An event of the recording that the body animator may answer; its first three numbers (slot, then its own) go to the log.</summary>
    internal static void Mark(ushort kind, ReadOnlySpan<byte> payload)
    {
        if (_file == null || kind is not (EventKind.Interact or EventKind.Throw or EventKind.Reload or EventKind.Melee)) return;
        var read = Reader.Load(payload);
        var line = "EVENT " + kind;
        for (var n = 0; n < 3 && !read.AtEnd; n++) line += " " + read.U();
        Write(line);
    }

    private static readonly PayloadReader Reader = new();

    private static void Write(string line)
    {
        try { _file!.WriteLine(Time.unscaledTime.ToString("0.000") + " " + line); _lines++; }
        catch (Exception error) { _file = null; Plugin.Logger.ErrorOnce("The animator state log stopped: " + error.Message); }
    }
}
