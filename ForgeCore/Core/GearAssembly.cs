using System;

namespace ForgeCore;

/// <summary>The game assembles a gun's parts in a coroutine that spawns one part asset and then waits a frame
/// (<c>GearPartSpawner.SpawnAllParts</c>), so a gun of a dozen parts takes a dozen frames before it can be loaded and held. A swap in
/// the special slot (<see cref="LoanedGun"/>) finishes that coroutine at once: every remaining step runs now, in order, through the game's own enumerator.</summary>
public static class GearAssembly
{
    /// <summary>More steps than any gear has parts: a coroutine still running after this many is not the part spawner's and is left alone.</summary>
    public const int MaxSteps = 256;

    /// <summary>Runs <paramref name="moveNext"/> until the coroutine reports it is done. Returns the steps it took (0: it was already
    /// done), or -1 when it was still running after <see cref="MaxSteps"/>.</summary>
    public static int Finish(Func<bool> moveNext)
    {
        for (var steps = 0; steps < MaxSteps; steps++)
            if (!moveNext()) return steps;
        return -1;
    }
}
