using System;
using System.Collections.Generic;
using System.Linq;
using ChainedPuzzles;
using ForgeCore;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Per-round authoring on top of the native uplink, timed-terminal and reactor machines. Every peer runs the
/// same terminal commands and the same hooks, so nothing here decides a result of its own: it edits a value the native
/// body reads next (a queued line's wait, a round's start stamp or receiver seed), or holds one command until a scene
/// scan is solved and then lets the native body run it unchanged. An objective with no row is never touched.
///
/// Holding before the native body means the round index stays where it was while the scan runs, so a failure, a recall
/// or a late joiner sees the plain native state; the player types the command again and nothing else remembers it.</summary>
internal sealed class ObjectiveRoundsNative
{
    private sealed record Gate(ChainedPuzzleInstance Scan, Action Replay);

    private readonly ILookup<uint, ObjectiveRoundsRow> _rows;
    private readonly Func<MapLevelReference?> _level;
    private readonly Dictionary<IntPtr, Gate> _gates = new();
    private readonly HashSet<IntPtr> _rewrittenRounds = new();
    /// <summary>Per timed sequence (<see cref="TimeKey"/>), the row of <see cref="MapNet.RoundTime"/> this machine's stamp already follows.</summary>
    private readonly Dictionary<int, TimedRoundTime> _moved = new();
    private bool _replaying;

    internal ObjectiveRoundsNative(IReadOnlyList<ObjectiveRoundsRow> rows, Func<MapLevelReference?> level)
    {
        _rows = rows.ToLookup(row => row.Objective);
        _level = level;
        MapNet.RoundTime.Changed += TimeChanged;
    }

    internal void Dispose() => MapNet.RoundTime.Changed -= TimeChanged;

    private ObjectiveRoundsRow? Row(uint objective)
    {
        if (!_rows.Contains(objective)) return null;
        var level = _level();
        return _rows[objective].FirstOrDefault(row => row.Level == level);
    }

    private ObjectiveRoundsRow? Row(LG_LayerType layer, int chain)
        => _rows.Count != 0 && WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(layer, chain, out var data) && data != null
            ? Row(data.persistentID) : null;

    // ---- the held command ----------------------------------------------------------------------------

    /// <summary>True when the native command must not run now: a scan was started for it, or is still running.</summary>
    private bool Hold(IntPtr machine, uint? scanId, string prompt, LG_ComputerTerminalCommandInterpreter terminal, Action replay)
    {
        if (_gates.ContainsKey(machine)) { Prompt(terminal, prompt); return true; }
        if (scanId is not { } id) return false;
        var scan = ChainedPuzzleLookup.Find(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var ambiguous);
        if (scan == null)
        {
            // A round must stay winnable: a scan the level did not build (or built twice) is reported and the round goes on.
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable,
                (ambiguous ? "objective-round-scan-ambiguous scan=" : "objective-round-scan-missing scan=") + id);
            return false;
        }
        if (scan.IsSolved) return false;
        _gates[machine] = new Gate(scan, replay);
        if (Session.IsHost && !scan.IsActive) scan.AttemptInteract(eChainedPuzzleInteraction.Activate);
        Prompt(terminal, prompt);
        return true;
    }

    private static void Prompt(LG_ComputerTerminalCommandInterpreter terminal, string prompt)
    {
        if (prompt.Length != 0) terminal.AddOutput(prompt);
    }

    internal void ScanSolved(ChainedPuzzleInstance scan)
    {
        if (_gates.Count == 0) return;
        foreach (var (machine, gate) in _gates.ToArray())
        {
            if (gate.Scan.Pointer != scan.Pointer) continue;
            _gates.Remove(machine);
            _replaying = true;
            try { gate.Replay(); }
            catch (Exception error) { MapLog.Contained("objective-round.replay", error); }
            finally { _replaying = false; }
        }
    }

    /// <summary>A recall put every machine and scan back; a held command is simply gone, as if it was never typed, and
    /// what the host moved belongs to the time that was dropped, so its rows go.</summary>
    internal void Recalled()
    {
        LevelEnded();
        if (MapNet.Channel.IsHost) MapNet.RoundTime.Clear();
    }

    internal void LevelEnded() { _gates.Clear(); _rewrittenRounds.Clear(); _moved.Clear(); }

    // ---- uplink --------------------------------------------------------------------------------------

    /// <summary>The waits the verification will queue, read before the native body moves the round.</summary>
    internal readonly record struct UplinkVerify(int Queued, UplinkRoundRow? Round, bool Correct);

    /// <summary>Returns false when the native body must not run this time.</summary>
    internal bool BeforeUplinkVerify(LG_ComputerTerminalCommandInterpreter terminal, string? code, string? second, out UplinkVerify state)
    {
        state = default;
        var owner = terminal.m_terminal;
        var puzzle = owner?.UplinkPuzzle;
        if (owner == null || puzzle == null || !puzzle.Connected) return true;
        var row = Row(owner.OriginLayer, owner.WardenObjectiveChainIndex);
        if (row == null) return true;
        var round = row.Uplink.FirstOrDefault(entry => entry.Round == puzzle.m_roundIndex);
        var current = puzzle.CurrentRound;
        bool correct = current != null && string.Equals(code?.ToUpperInvariant(), current.CorrectCode?.ToUpperInvariant(), StringComparison.Ordinal);
        if (!_replaying && (_gates.ContainsKey(puzzle.Pointer) || correct)
            && Hold(puzzle.Pointer, round?.EndScan, row.ScanPrompt, terminal, () => terminal.TerminalUplinkVerify(code!, second!))) return false;
        state = new UplinkVerify(terminal.m_outputQueue?.Count ?? 0, round, correct);
        return true;
    }

    /// <summary>The native body has queued its lines with its literal 5 and 6 second waits. Each is the only line with
    /// that wait in the path taken, so the literal names the wait; the native queue then runs it as usual.</summary>
    internal void AfterUplinkVerify(LG_ComputerTerminalCommandInterpreter terminal, in UplinkVerify state)
    {
        if (state.Round is not { } round || terminal.m_outputQueue is not { } queue) return;
        // The native queue offers no indexed read, so one turn around it reaches the lines just added, in order.
        for (int index = 0, count = queue.Count; index < count; index++)
        {
            var line = queue.Dequeue();
            if (index >= state.Queued)
            {
                if (line.Time == 5f && round.StartSeconds is { } start) line.Time = start;
                else if (line.Time == 6f && (state.Correct ? round.CompleteSeconds : round.RestoreSeconds) is { } wait) line.Time = wait;
            }
            queue.Enqueue(line);
        }
    }

    // ---- timed terminals -----------------------------------------------------------------------------

    /// <summary>Returns false when the native confirmation must wait for this round's scan.</summary>
    internal bool BeforeTimedVerify(LG_ComputerTerminalCommandInterpreter terminal, string? first, string? second)
    {
        if (_replaying) return true;
        var puzzle = terminal.m_terminal?.TimedSequencePuzzle;
        var round = puzzle?.CurrentRound;
        if (puzzle == null || round == null || !puzzle.Connected || puzzle.Solved || !round.HasSentForConfirmation
            || puzzle.ConfirmationIsOutOfTime) return true;
        var row = Row(puzzle.m_wardenObjectiveData.persistentID);
        if (row == null) return true;
        var authored = row.Timed.FirstOrDefault(entry => entry.Round == puzzle.m_roundIndex);
        return !Hold(puzzle.Pointer, authored?.EndScan, row.ScanPrompt, terminal, () => terminal.TimedTerminalSequenceVerify(first!, second!));
    }

    /// <summary>Picks the receiver of every round not picked yet. The native seed stays the only randomness: it is
    /// already the same on every peer, and it is replaced by the picked terminal's index, which the native lookup reads
    /// modulo the terminal count. A round object is rewritten once; a failed round gets a new object and a new pick.</summary>
    internal void PickReceivers(TimedTerminalSequencePuzzle puzzle)
    {
        var row = Row(puzzle.m_wardenObjectiveData.persistentID);
        var terminals = puzzle.m_terminal?.TimedTerminalSequenceTerminals;
        if (row == null || terminals == null || terminals.Count == 0) return;
        int count = terminals.Count;
        var rounds = puzzle.m_rounds;
        var used = new HashSet<int>();
        for (int index = 0; index < rounds.Count; index++)
            if (_rewrittenRounds.Contains(rounds[index].Pointer)) used.Add((int)(rounds[index].TargetTerminalSeed % (uint)count));
        for (int index = 0; index < rounds.Count; index++)
        {
            var round = rounds[index];
            if (_rewrittenRounds.Contains(round.Pointer)) continue;
            var zones = row.Timed.FirstOrDefault(entry => entry.Round == index)?.ReceiverZones ?? Array.Empty<int>();
            if (zones.Count == 0 && !row.NoRepeat) { used.Add((int)(round.TargetTerminalSeed % (uint)count)); continue; }
            var pool = Enumerable.Range(0, count).Where(at => zones.Count == 0 || zones.Contains((int)(terminals[at].SpawnNode?.m_zone?.LocalIndex ?? (eLocalZoneIndex)(-1)))).ToArray();
            var fresh = row.NoRepeat ? pool.Where(at => !used.Contains(at)).ToArray() : pool;
            var choice = fresh.Length != 0 ? fresh : pool;
            if (choice.Length == 0)
            {
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.CapabilityUnavailable, "objective-round-receiver-missing round=" + index);
                used.Add((int)(round.TargetTerminalSeed % (uint)count));
                continue;
            }
            int pick = choice[(int)(round.TargetTerminalSeed % (uint)choice.Length)];
            round.TargetTerminalSeed = (uint)pick;
            _rewrittenRounds.Add(round.Pointer);
            used.Add(pick);
        }
    }

    /// <summary>The native timer is the data block's single value minus the time since the round's stamp, so moving the
    /// stamp gives this round its own length everywhere the native code reads the timer.</summary>
    internal void TimedRoundStarted(TimedTerminalSequencePuzzle puzzle, int index)
    {
        var data = puzzle.m_wardenObjectiveData;
        if (Row(data.persistentID)?.Timed.FirstOrDefault(entry => entry.Round == index)?.Seconds is { } seconds)
            puzzle.m_rounds[index].StartTime += seconds - data.TimedTerminalSequence_TimePerRound;
        // A new round, or a new attempt at the same one: nothing of it is moved yet. The host's row is of an earlier
        // phase and goes; a client may already hold the host's row for this one.
        int key = TimeKey(puzzle.m_terminal.OriginLayer, puzzle.m_terminal.WardenObjectiveChainIndex);
        _moved.Remove(key);
        if (MapNet.Channel.IsHost) MapNet.RoundTime.Remove(key);
        else if (MapNet.RoundTime.TryGet(key, out var row)) Follow(key, row);
    }

    internal void TimedConfirmationStarted(TimedTerminalSequencePuzzle puzzle)
    {
        var data = puzzle.m_wardenObjectiveData;
        var row = Row(data.persistentID);
        if (row != null)
            puzzle.ConfirmationTime = row.Timed.FirstOrDefault(entry => entry.Round == puzzle.m_roundIndex)?.ConfirmSeconds
                ?? data.TimedTerminalSequence_TimeForConfirmation;
    }

    /// <summary>A round that ran out of time drops its held confirmation and puts its scan back.</summary>
    internal void TimedRoundReset(TimedTerminalSequencePuzzle puzzle)
    {
        if (_gates.Remove(puzzle.Pointer, out var gate) && Session.IsHost && gate.Scan.IsActive)
            gate.Scan.AttemptInteract(eChainedPuzzleInteraction.Deactivate);
        if (Row(puzzle.m_wardenObjectiveData.persistentID) != null) PickReceivers(puzzle);
    }

    /// <summary>Every peer keeps the round's timers by itself and no clock is shared, so what the host owns is how far
    /// the running phase's stamp is moved in total. It writes that into <see cref="MapNet.RoundTime"/>, and every machine,
    /// the host included, moves its own stamp to the row: a player who joins late gets the same total as everyone.</summary>
    internal string MoveRoundTime(LG_LayerType layer, int chain, double seconds, double minimum)
    {
        int key = TimeKey(layer, chain);
        var puzzle = TimedPuzzle(key);
        var round = puzzle?.CurrentRound;
        if (puzzle == null || round == null || !puzzle.Connected || puzzle.Solved || !round.HasStarted) return ResultCode.ObjectiveTimedRoundNotRunning;
        float remaining = round.HasSentForConfirmation
            ? puzzle.ConfirmationTime - (Clock.Time - round.ConfirmationStartTime)
            : puzzle.m_wardenObjectiveData.TimedTerminalSequence_TimePerRound - (Clock.Time - round.StartTime);
        float delta = (float)Math.Max(remaining + seconds, Math.Min(minimum, remaining)) - remaining;
        int phase = Phase(puzzle, round);
        MapNet.RoundTime.Set(key, new TimedRoundTime { Phase = phase, Seconds = Moved(key, phase) + delta });
        return ObjectiveActions.TimedRoundMoved;
    }

    internal static int TimeKey(LG_LayerType layer, int chain) => (int)layer << 16 | chain;

    private static int Phase(TimedTerminalSequencePuzzle puzzle, TimedTerminalSequencePuzzleRound round)
        => puzzle.m_roundIndex * 2 + (round.HasSentForConfirmation ? 1 : 0);

    private float Moved(int key, int phase) => _moved.TryGetValue(key, out var row) && row.Phase == phase ? row.Seconds : 0f;

    /// <summary>The table's one handler, on the host as on every client. A removed row is the host beginning another
    /// phase; a client that began the same round again before that had taken the earlier attempt's row for its own.</summary>
    private void TimeChanged(int key, TimedRoundTime row, bool set)
    {
        if (set) Follow(key, row);
        else if (_moved.Remove(key, out var was) && Running(key, was.Phase) is { } round) Move(round, round.HasSentForConfirmation, -was.Seconds);
    }

    /// <summary>Moves this machine's stamp to the row's total when it is in the phase the row names. A row that comes
    /// before the phase began here is taken when it begins (<see cref="TimedRoundStarted"/>).</summary>
    private void Follow(int key, TimedRoundTime row)
    {
        if (Running(key, row.Phase) is not { } round) return;
        Move(round, round.HasSentForConfirmation, row.Seconds - Moved(key, row.Phase));
        _moved[key] = row;
    }

    private static TimedTerminalSequencePuzzleRound? Running(int key, int phase)
        => TimedPuzzle(key) is { } puzzle && puzzle.CurrentRound is { HasStarted: true } round && Phase(puzzle, round) == phase ? round : null;

    private static void Move(TimedTerminalSequencePuzzleRound round, bool confirming, float delta)
    {
        if (confirming) round.ConfirmationStartTime += delta;
        else round.StartTime += delta;
    }

    private static TimedTerminalSequencePuzzle? TimedPuzzle(int key)
    {
        var terminals = LG_ComputerTerminalManager.Current?.m_terminals;
        if (terminals == null) return null;
        foreach (var entry in terminals)
            if (entry.Value is { WasCollected: false } terminal && terminal.TimedSequencePuzzle is { } puzzle
                && TimeKey(terminal.OriginLayer, terminal.WardenObjectiveChainIndex) == key) return puzzle;
        return null;
    }

    // ---- reactor -------------------------------------------------------------------------------------

    private ReactorWaveRow? EventWave(LG_WardenObjective_Reactor? reactor)
    {
        if (reactor == null || reactor.WasCollected || reactor.m_currentState.status != eReactorStatus.Startup_waitForVerify) return null;
        return Row(reactor.OriginLayer, reactor.WardenObjectiveChainIndex)?.Reactor.FirstOrDefault(row => row.Wave == reactor.m_currentWaveCount - 1);
    }

    /// <summary>Returns false when this wave takes no code: the terminal answers with the wave's own line.</summary>
    internal bool BeforeReactorVerify(LG_ComputerTerminalCommandInterpreter terminal)
    {
        if (EventWave(terminal.m_terminal?.ConnectedReactor) is not { } wave) return true;
        Prompt(terminal, wave.Text);
        return false;
    }

    /// <summary>The native HUD line of a wave waiting for verification carries the code. An event wave has none to show.</summary>
    internal string? ReactorMessage(LG_WardenObjective_Reactor reactor) => EventWave(reactor)?.Text;
}
