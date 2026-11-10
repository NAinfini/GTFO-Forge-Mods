using System;
using System.Collections.Generic;
using ForgeAudio;
using ForgeCore;
using LevelGeneration;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The replay level's terminal screens show what the recorded ones did: the lines they printed, what a player typed and whether a player
/// stood at them, through ForgeCore's <see cref="WorldStates"/> (the terminal's own output queue and receive handlers, on this machine only). A
/// terminal is found by the place the recording names for it (<see cref="Analysis.TerminalPlaces"/>), the nearest within <see cref="Reach"/> metres.
/// Playing with every sound (up to 2x), a line prints through the game's queue with its print sound and typing plays the game's key sound per
/// recorded character; faster, and after a jump, the screen is set at once to the moment's lines without a sound and without the lines between.
/// The screen a terminal had when the level was built (its start-up text, which the recording leaves out) stays above the recorded lines.
/// Main thread only.</summary>
internal static class Terminals
{
    private const float Reach = 3f;
    // The recorded lines rebuilt into a screen: more than any terminal screen holds.
    private const int Kept = 64;
    private static LG_ComputerTerminal[]? _all;
    private static readonly Dictionary<ulong, LG_ComputerTerminal?> ByKey = new();
    // The screen each terminal had before the replay first wrote to it.
    private static readonly Dictionary<ulong, List<string>> Built = new();
    // What each terminal's screen shows: the recorded lines printed by then and the input line.
    private static readonly Dictionary<ulong, (int Lines, string Typed)> Shown = new();
    // The state (and user slot + 1, above 8 bits) each terminal was last put into.
    private static readonly Dictionary<ulong, ulong> StateShown = new();
    private static readonly HashSet<ulong> Dirty = new();
    private static readonly List<string> Lines = new();
    private static readonly List<uint> Caught = new();
    // The keys whose missing terminal was reported; a terminal found later takes its report back.
    private static readonly HashSet<ulong> Missing = new();
    private static readonly List<ulong> Set = new();

    internal static void Reset()
    {
        _all = null; ByKey.Clear(); Built.Clear(); Shown.Clear(); StateShown.Clear(); Dirty.Clear(); Missing.Clear(); Set.Clear(); Lines.Clear(); Caught.Clear();
    }

    /// <summary>One recorded terminal event passed while playing; <paramref name="sound"/> when the speed tier plays every sound.</summary>
    internal static void Present(Record r, bool sound)
    {
        var analysis = Replayer.Report;
        if (analysis == null) return;
        var read = new PayloadReader(r.Payload!);
        var key = read.U();
        var terminal = Find(analysis, key);
        if (terminal == null) return;
        switch (r.EventType)
        {
            case EventKind.TerminalState:
                {
                    var state = read.U();
                    var user = read.U();
                    SetState(key, terminal, state, user, sound);
                    return;
                }
            case EventKind.TerminalLine when sound && !Dirty.Contains(key):
                {
                    var line = ReplayExtras.Printed(r.Payload!);
                    // A line printed after typing: the typed line was sent (the game empties it as it runs the command, which is not run here).
                    if (Shown.TryGetValue(key, out var before) && before.Typed.Length > 0) WorldStates.TerminalTyping(terminal, "", false);
                    WorldStates.TerminalPrint(terminal, line.Type == 255 ? null : (TerminalLineType)line.Type, line.Text, line.WaitS);
                    Shown[key] = (analysis.TerminalScreenAt(key, r.TimeMs, 0, null, out var typed), typed);
                    return;
                }
            case EventKind.TerminalInput when sound && !Dirty.Contains(key):
                {
                    var count = analysis.TerminalScreenAt(key, r.TimeMs, 0, null, out var typed);
                    WorldStates.TerminalTyping(terminal, typed, true);
                    Shown[key] = (count, typed);
                    return;
                }
            default:
                // Faster: the screen is set as it stands at the frame's moment (Frame).
                Dirty.Add(key);
                return;
        }
    }

    /// <summary>Per frame: the terminals whose screen was left to be set are set to <paramref name="nowMs"/>.</summary>
    internal static void Frame(uint nowMs)
    {
        var analysis = Replayer.Report;
        if (Dirty.Count == 0 || analysis == null) return;
        Set.Clear();
        foreach (var key in Dirty)
        {
            try { if (Screen(analysis, key, nowMs)) Set.Add(key); }
            catch (Exception error) { Skipped.Note("a recorded terminal screen could not be shown", error.Message); Set.Add(key); }
        }
        // A screen whose terminal is not there yet stays to be set again: the level may not have built it on the first look.
        foreach (var key in Set) Dirty.Remove(key);
        Set.Clear();
    }

    /// <summary>After a jump: every terminal the recording names is put into its state at the moment (Sleeping before its first), set at once with
    /// any sound its handler plays stopped, and its screen is set to the moment's lines (next frame).</summary>
    internal static void Jumped(WorldState state)
    {
        var analysis = Replayer.Report;
        if (analysis == null) return;
        foreach (var key in analysis.TerminalPlaces.Keys)
        {
            var terminal = Find(analysis, key);
            if (terminal == null) continue;
            ulong value = 0, user = 0;
            if (state.Extras.TryGetValue((EventKind.TerminalState, key), out var payload))
            {
                var read = new PayloadReader(payload);
                read.U(); value = read.U(); user = read.U();
            }
            if (StateShown.ContainsKey(key) || value != 0) SetState(key, terminal, value, user, false);
            if (Shown.ContainsKey(key) || analysis.TerminalScreenAt(key, uint.MaxValue, 0, null, out _) > 0) Dirty.Add(key);
        }
    }

    private static void SetState(ulong key, LG_ComputerTerminal terminal, ulong state, ulong user, bool sound)
    {
        var seen = state | user << 8;
        if (StateShown.TryGetValue(key, out var had) && had == seen) return;
        var agent = user is >= 1 and <= 4 ? Puppeteer.PlayerOf((int)user - 1)?.Agent : null;
        if (!sound) GameVoices.Catch(Caught);
        try
        {
            if (WorldStates.TerminalState(terminal, (TERM_State)(int)state, agent)) StateShown[key] = seen;
        }
        catch (Exception error) { Skipped.Note("a recorded terminal state could not be shown", error.Message); }
        finally
        {
            if (!sound)
            {
                GameVoices.Catch(null);
                foreach (var id in Caught) AudioKit.StopGameEvent(id);
                Caught.Clear();
            }
        }
    }

    // False while the replay's terminal is not there yet (it is tried again next frame); true once the screen is at the moment.
    private static bool Screen(Analysis analysis, ulong key, uint nowMs)
    {
        var terminal = Find(analysis, key);
        if (terminal == null) return false;
        var count = analysis.TerminalScreenAt(key, nowMs, 0, null, out var typed);
        if (Shown.TryGetValue(key, out var had) && had == (count, typed)) return true;
        Lines.Clear();
        if (Built.TryGetValue(key, out var built)) Lines.AddRange(built);
        analysis.TerminalScreenAt(key, nowMs, Kept, Lines, out _);
        WorldStates.TerminalScreen(terminal, Lines, typed);
        Shown[key] = (count, typed);
        return true;
    }

    // The replay level's terminal at the recorded one's place, looked for again while it is missing: the level was not built yet on the first
    // look (the first jump happens as the level is entered), and a terminal the game has switched off is still the terminal to set. Its screen
    // as the level built it is kept once it is here, before the replay writes to it.
    private static LG_ComputerTerminal? Find(Analysis analysis, ulong key)
    {
        if (ByKey.TryGetValue(key, out var kept) && kept != null && !kept.WasCollected) return kept;
        ByKey.Remove(key);
        LG_ComputerTerminal? best = null;
        if (analysis.TerminalPlaces.TryGetValue(key, out var place))
        {
            var near = new Vector3(place.X, place.Y, place.Z);
            var distance = Reach;
            var all = _all ??= LevelFind.All<LG_ComputerTerminal>();
            if (all.Length == 0) _all = null;
            foreach (var candidate in all)
            {
                if (candidate == null) continue;
                var d = Vector3.Distance(candidate.transform.position, near);
                if (d <= distance) { best = candidate; distance = d; }
            }
        }
        if (best == null)
        {
            if (Missing.Add(key)) Skipped.Note(NoTerminal, Example(key, place));
            return null;
        }
        ByKey[key] = best;
        if (Missing.Remove(key)) Skipped.Resolve(NoTerminal, Example(key, place));
        var built = new List<string>();
        WorldStates.TerminalLines(best, built);
        Built[key] = built;
        return best;
    }

    private const string NoTerminal = "a recorded terminal has no terminal of the replay level at its recorded place";

    private static string Example(ulong key, (float X, float Y, float Z) place)
        => "key " + key + " near " + place.X.ToString("0.0") + ", " + place.Y.ToString("0.0") + ", " + place.Z.ToString("0.0");
}
