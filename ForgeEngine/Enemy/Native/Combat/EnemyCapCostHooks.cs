using Enemies;

namespace ForgeEnemy.Native;

/// <summary>The on-screen cap share hook (contract §4.5 D4, `EnemyModule.CapCost.cs`): `EnemyAI.ModeChange`
/// (0x155D720, reviewed in evidence/enemy-behavior-hooks.json) is the game's one allocation of a life's share, reached
/// from `AgentAI.set_Mode` whenever the mode changes. The wave budget is not read here.</summary>
internal static class EnemyCapCostHooks
{
    /// <summary>The hook's inputs and outputs, passed by reference through the closure-free guard.</summary>
    internal struct Call
    {
        internal EnemyAI? AI;
        internal float Before;
    }
}

// ForgeCore patches `EnemyAI.ModeChange` once for every package (`GameHooks.EnemyModeChanged`). Both halves return before touching the game when no loaded profile authors a share, and neither allocates: the
// guard callbacks are static lambdas the compiler caches, and the call data is a struct passed by reference.
internal static class EnemyCapCostAllocation
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyModeChanged.Around(Before, After);

    internal static float Before(EnemyAI ai)
    {
        if (EnemyHost.Offer(EnemyFeature.CapCost) is not { } session || !session.Module.CapCostAuthored) return 1f;
        var call = new EnemyCapCostHooks.Call { AI = ai, Before = 1f };
        session.Guard(ref call, static (EnemyModule module, ref EnemyCapCostHooks.Call c) => c.Before = module.CapCostBefore(c.AI!));
        return call.Before;
    }

    internal static void After(EnemyAI ai, float before, bool ran)
    {
        if (before > 0f || EnemyHost.Offer(EnemyFeature.CapCost) is not { } session || !session.Module.CapCostAuthored) return;
        var call = new EnemyCapCostHooks.Call { AI = ai, Before = before };
        session.Guard(ref call, static (EnemyModule module, ref EnemyCapCostHooks.Call c) => module.AfterModeChange(c.AI!, c.Before));
    }
}
