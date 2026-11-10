namespace ForgeReplay;

internal enum LoadReportKind
{
    /// <summary>The limit passed and the level is still not entered.</summary>
    StillLoading,
    /// <summary>The level was entered, after the limit.</summary>
    EnteredSlow,
    /// <summary>The load ended without the level being entered (it failed or was abandoned).</summary>
    Failed,
}

/// <summary>What to write about a replay load: which case, and the stage times (milliseconds since play was chosen; <paramref name="BuildingAtMs"/>
/// is when the game started building the level, -1 if it never did).</summary>
internal readonly record struct LoadReport(LoadReportKind Kind, double TotalMs, double BuildingAtMs);

/// <summary>Decides when loading a replay level is worth a log line. A load that is quick and ends in the level says nothing. A slow one writes
/// once when the limit passes without the level entered and once more when it is entered; one that ends without the level entered (a failed or
/// abandoned load) writes once, however long it took. Every line carries the stage times, so a slow load can be told from a stuck or failed
/// one. Time is passed in, so the decisions are the same in the game and in a test. Main thread only.</summary>
internal sealed class LoadWatch
{
    internal const double SlowMs = 60_000;
    private bool _loading, _limitWritten;
    private double _buildingAtMs = -1;

    internal bool Loading => _loading;

    /// <summary>Play was chosen.</summary>
    internal void Start() { _loading = true; _limitWritten = false; _buildingAtMs = -1; }

    /// <summary>The game started building the level.</summary>
    internal void BuildStarted(double nowMs) { if (_loading) _buildingAtMs = nowMs; }

    /// <summary>Called every frame while loading: the one line when the limit has passed with the level not entered.</summary>
    internal LoadReport? Tick(double nowMs)
    {
        if (!_loading || _limitWritten || nowMs < SlowMs) return null;
        _limitWritten = true;
        return new LoadReport(LoadReportKind.StillLoading, nowMs, _buildingAtMs);
    }

    /// <summary>The level was entered: a line only if it took past the limit.</summary>
    internal LoadReport? Entered(double nowMs)
    {
        if (!_loading) return null;
        _loading = false;
        return nowMs >= SlowMs ? new LoadReport(LoadReportKind.EnteredSlow, nowMs, _buildingAtMs) : null;
    }

    /// <summary>The load ended without the level entered: always one line (none when no load was running).</summary>
    internal LoadReport? Failed(double nowMs)
    {
        if (!_loading) return null;
        _loading = false;
        return new LoadReport(LoadReportKind.Failed, nowMs, _buildingAtMs);
    }
}
