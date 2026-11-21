using System;

namespace ForgeCore;

/// <summary>The active drop attempt's monotonic identity, read from the attempt clock a measuring package bound
/// (<see cref="GenerationAttempt.Bind"/>); inactive (default) when none is. A stale generation token cannot append a boundary to a
/// later attempt.</summary>
public readonly record struct GenerationAttemptTiming(int Generation, long StartedAt, long Frequency, bool Active,
    bool FactoryDone, bool Ready, bool Entered);

/// <summary>The one drop attempt clock: a measuring package (ForgeDevelopment's drop timing) binds it, and a level generator
/// (ForgeEngine's Map domain) reads it and marks its own boundaries on it. With no clock bound the timing is inactive and every
/// boundary is dropped by design, so a player's install measures nothing. Main thread only.</summary>
public static class GenerationAttempt
{
    private static Func<GenerationAttemptTiming>? _clock;
    private static Action<int, string, string, int>? _boundary;

    /// <summary>Binds (or, with nulls, unbinds) the clock and the boundary sink.</summary>
    public static void Bind(Func<GenerationAttemptTiming>? clock, Action<int, string, string, int>? boundary)
    { _clock = clock; _boundary = boundary; }

    public static GenerationAttemptTiming Timing => _clock?.Invoke() ?? default;

    public static void MarkBoundary(int generation, string phase, string origin, int calls = 1)
        => _boundary?.Invoke(generation, phase, origin, calls);
}
