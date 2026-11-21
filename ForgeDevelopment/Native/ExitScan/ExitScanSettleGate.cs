using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

/// <summary>One native player scanner of a scan core, read as the vanilla fields that drive its scan callbacks.
/// <see cref="Running"/> is the scanner's own coroutine state; <see cref="Status"/> is the core's replicated bioscan
/// status, which restarts the scanner when it is Waiting or Scanning.</summary>
public sealed record ExitScanScannerState(bool ScanActive, bool ScanRoutine, bool CloseRoutine, int Players, string Status)
{
    public bool Running => ScanActive || ScanRoutine || CloseRoutine;
    public bool RestartPending => Status is "Waiting" or "Scanning";
    public bool Settled => !Running && !RestartPending;
    public override string ToString()
        => "scanActive=" + ScanActive + " scanRoutine=" + ScanRoutine + " closeRoutine=" + CloseRoutine
            + " players=" + Players + " status=" + Status;
}

/// <summary>A scan core's player scanner and the vanilla call that stops it (<c>CP_PlayerScanner.StopScan</c>,
/// the call the core's own Disabled state change makes).</summary>
public sealed record ExitScanScannerProbe(string Name, Func<ExitScanScannerState> Read, Action Stop);

public sealed record ExitScanProbe(
    string Source,
    long Identity,
    Func<bool> IsActive,
    Func<bool> IsSolved,
    Action Deactivate,
    IReadOnlyList<ExitScanScannerProbe> Scanners);

public sealed class ExitScanSettleException : Exception
{
    public ExitScanSettleException(string code, string detail, Exception? inner = null)
        : base(code + ": " + detail, inner)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public string Detail { get; }
}

/// <summary>One frame-driven gate shared by every package that leaves a live world through the game's own quit path
/// (the game drive steps). An inactive puzzle instance does not stop its cores' player scanners: a
/// scanner keeps running while a player stands in it and keeps calling the landing back after the floor is gone. The
/// gate therefore deactivates each active puzzle through its vanilla interaction, stops every still-running scanner of
/// a deactivated or inactive puzzle once through the vanilla <c>StopScan</c>, and opens only on a later frame that
/// reads every scanner stopped with no Waiting or Scanning state left to restart it. Anything still running at the
/// frame budget is a named failure.</summary>
public sealed class ExitScanSettleGate
{
    private sealed class Scanner
    {
        internal Scanner(ExitScanScannerProbe probe) => Probe = probe;
        internal ExitScanScannerProbe Probe { get; }
        internal bool StopSubmitted { get; set; }
    }

    private sealed class Scan
    {
        internal Scan(ExitScanProbe probe)
        {
            Identity = probe.Identity;
            IsActive = probe.IsActive;
            IsSolved = probe.IsSolved;
            Deactivate = probe.Deactivate;
            Sources.Add(probe.Source);
            foreach (var scanner in probe.Scanners ?? throw new ArgumentNullException(nameof(probe.Scanners)))
                Scanners.Add(new Scanner(scanner));
        }

        internal long Identity { get; }
        internal Func<bool> IsActive { get; }
        internal Func<bool> IsSolved { get; }
        internal Action Deactivate { get; }
        internal List<string> Sources { get; } = new();
        internal List<Scanner> Scanners { get; } = new();
        internal bool DeactivateSubmitted { get; set; }
    }

    private readonly long _worldEpoch;
    private readonly int _startedFrame;
    private readonly int _maximumFrames;
    private readonly Scan[] _scans;
    private readonly Action _prepareNativeReturn;
    private int? _preparedFrame;
    private int _lastActiveFrame = -1;
    private string _lastActiveScans = "none";

    public ExitScanSettleGate(long worldEpoch, int startedFrame, int maximumFrames, IEnumerable<ExitScanProbe> probes,
        Action prepareNativeReturn)
    {
        if (worldEpoch < 0)
            throw new ExitScanSettleException("exit-scan.world-unavailable", "the current world epoch is unavailable");
        if (startedFrame < 0)
            throw new ExitScanSettleException("exit-scan.frame-unavailable", "the current Unity frame is unavailable");
        if (maximumFrames < 1) throw new ArgumentOutOfRangeException(nameof(maximumFrames));

        _worldEpoch = worldEpoch;
        _startedFrame = startedFrame;
        _maximumFrames = maximumFrames;
        _prepareNativeReturn = prepareNativeReturn ?? throw new ArgumentNullException(nameof(prepareNativeReturn));
        var scans = new Dictionary<long, Scan>();
        foreach (var probe in probes ?? throw new ArgumentNullException(nameof(probes)))
        {
            if (probe.Identity == 0)
                throw new ExitScanSettleException("exit-scan.identity-unavailable", probe.Source + " has no live native identity");
            if (scans.TryGetValue(probe.Identity, out var existing))
            {
                if (!existing.Sources.Exists(source => string.Equals(source, probe.Source, StringComparison.Ordinal)))
                    existing.Sources.Add(probe.Source);
                continue;
            }
            scans.Add(probe.Identity, new Scan(probe));
        }
        _scans = new Scan[scans.Count];
        scans.Values.CopyTo(_scans, 0);
    }

    /// <returns><see langword="true"/> only in a later frame after the return preparation callback, which itself
    /// runs only on a frame after every puzzle read inactive and every scanner read stopped.</returns>
    public bool Tick(long worldEpoch, int frame)
    {
        if (worldEpoch != _worldEpoch)
            throw new ExitScanSettleException("exit-scan.world-changed",
                "refusing to settle world " + _worldEpoch + " from world " + worldEpoch);
        if (frame < _startedFrame)
            throw new ExitScanSettleException("exit-scan.frame-regressed",
                "the Unity frame moved from " + _startedFrame + " to " + frame);

        if (_preparedFrame is { } prepared) return frame > prepared;

        var waiting = false;
        var pending = new List<string>();
        foreach (var scan in _scans)
        {
            var active = Read(scan, scan.IsActive);
            if (active)
            {
                waiting = true;
                pending.Add(Name(scan));
                if (!Read(scan, scan.IsSolved) && !scan.DeactivateSubmitted)
                {
                    scan.DeactivateSubmitted = true;
                    try { scan.Deactivate(); }
                    catch (Exception error)
                    {
                        throw new ExitScanSettleException("exit-scan.deactivation-failed",
                            Name(scan) + " rejected the vanilla Deactivate interaction", error);
                    }
                }
            }
            // A completed puzzle that still reports active keeps its own scanners until it turns inactive.
            if (active && !scan.DeactivateSubmitted) continue;
            foreach (var scanner in scan.Scanners)
            {
                var state = Read(scan, scanner);
                var name = Name(scan) + "/" + scanner.Probe.Name;
                if (state.Running && !scanner.StopSubmitted)
                {
                    scanner.StopSubmitted = true;
                    try { scanner.Probe.Stop(); }
                    catch (Exception error)
                    {
                        throw new ExitScanSettleException("exit-scan.scanner-stop-failed",
                            name + " rejected the vanilla StopScan (" + state + ")", error);
                    }
                    waiting = true;
                    pending.Add(name + " stop submitted (" + state + ")");
                    continue;
                }
                if (state.Settled) continue;
                waiting = true;
                pending.Add(name + (scanner.StopSubmitted ? " still running after StopScan (" : " (") + state + ")");
            }
        }

        if (waiting)
        {
            _lastActiveFrame = Math.Max(_lastActiveFrame, frame);
            _lastActiveScans = string.Join(", ", pending);
        }
        else if (frame > _lastActiveFrame)
        {
            // Every puzzle is inactive and every scanner stopped with nothing left to restart it, on a later frame.
            try { _prepareNativeReturn(); }
            catch (Exception error)
            {
                throw new ExitScanSettleException("exit-scan.return-preparation-failed",
                    "native return preparation failed before floor teardown: " + error.GetType().Name + ": " + error.Message, error);
            }
            _preparedFrame = frame;
            return false;
        }
        if (frame - _startedFrame >= _maximumFrames)
            throw new ExitScanSettleException("exit-scan.deactivation-timeout",
                "still active after " + _maximumFrames + " frames: " + _lastActiveScans);
        return false;
    }

    private static bool Read(Scan scan, Func<bool> read)
    {
        try { return read(); }
        catch (Exception error)
        {
            throw new ExitScanSettleException("exit-scan.read-failed", Name(scan) + " could not be read", error);
        }
    }

    private static ExitScanScannerState Read(Scan scan, Scanner scanner)
    {
        try { return scanner.Probe.Read(); }
        catch (Exception error)
        {
            throw new ExitScanSettleException("exit-scan.read-failed",
                Name(scan) + "/" + scanner.Probe.Name + " could not be read", error);
        }
    }

    private static string Name(Scan scan)
        => string.Join("+", scan.Sources) + "@0x" + scan.Identity.ToString("X");
}
