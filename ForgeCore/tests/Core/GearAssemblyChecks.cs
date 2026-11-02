namespace ForgeCore.Tests;

internal static class GearAssemblyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // The new gun's part spawner (one part per frame in the game) is finished in one go, in order, and ends with the game's callback.
        var spawned = new List<int>();
        var parts = 12;
        var step = 0;
        var calledBack = false;
        bool Spawner() { if (step < parts) { spawned.Add(step++); return true; } calledBack = true; return false; }
        check(GearAssembly.Finish(Spawner) == parts && calledBack && spawned.SequenceEqual(Enumerable.Range(0, parts)),
            "a swap finishes every remaining part of the new gun at once, in order, ending with the game's all-parts callback");
        check(GearAssembly.Finish(Spawner) == 0 && spawned.Count == parts, "a spawner already done spawns nothing more");
        var runaway = 0;
        check(GearAssembly.Finish(() => { runaway++; return true; }) == -1 && runaway == GearAssembly.MaxSteps,
            "a coroutine that never ends is left to the game after a bounded number of steps");
    }
}
