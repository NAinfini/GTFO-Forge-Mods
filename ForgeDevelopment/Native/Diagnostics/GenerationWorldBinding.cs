namespace ForgeDevelopment.Native;

/// <summary>Holds the inspection session of one generation and decides which world it belongs to. A generation
/// begins in the <c>LG_Factory.Setup</c> prefix, but the Runtime starts the world it runs in afterwards (the build
/// start callback advances the epoch), so the epoch read at that prefix is the previous world's. A session created
/// there never matches any job or inspection step of its own generation, which is why every job record and every
/// post-generation inspection was dropped. The session is therefore created at the first observation after the
/// generation began and every later check compares against that epoch.</summary>
internal sealed class GenerationWorldBinding
{
    internal ProjectInspectionSession? Session { get; private set; }

    /// <summary>A new generation started: the world it runs in is not decided yet.</summary>
    internal void Reset() => Session = null;

    internal bool IsBound => Session != null;

    /// <summary>Creates the session for the world the generation is running in. The scan, if any, must have been
    /// loaded for the same epoch.</summary>
    internal ProjectInspectionSession Bind(DiagnosticsReport report, ProjectObjectReferenceScan? scan, long epoch)
        => Session = new ProjectInspectionSession(report, scan, epoch);

    /// <summary>Whether a job observed now belongs to the world the session inspects.</summary>
    internal bool Admits(long epoch) => Session != null && Session.WorldEpoch == epoch;
}
