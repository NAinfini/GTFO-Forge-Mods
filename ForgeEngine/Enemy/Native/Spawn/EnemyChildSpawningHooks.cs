using Enemies;
using ForgeEnemy.Profile;
using GameData;
using HarmonyLib;

namespace ForgeEnemy.Native;

internal static class EnemyChildSpawningHooks
{
    internal static bool Wanted(out EnemyPluginSession session) => EnemyAbilityFieldHooks.Wanted(EnemyAbilityHooks.ChildSpawning, out session);
}

[HarmonyPatch(typeof(EAB_Birthing), nameof(EAB_Birthing.Trigger))]
internal static class EnemyChildBirthTrigger
{
    [HarmonyPrefix]
    private static bool Prefix(EAB_Birthing __instance, ref bool __result)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return true;
        // A fault leaves the attack allowed: the game's own birthing runs.
        var call = (Birthing: __instance, Allowed: true);
        session.Guard(ref call, static (EnemyModule module, ref (EAB_Birthing Birthing, bool Allowed) c) => c.Allowed = module.ChildAttackAllowed(c.Birthing));
        if (!call.Allowed) __result = false;
        return call.Allowed;
    }
}

// Native .ctor inlines TryGetPopulationData; AddEnemyData is the first real read of the chosen roles.
[HarmonyPatch(typeof(EnemyGroupScoreSpawnData), nameof(EnemyGroupScoreSpawnData.AddEnemyData))]
internal static class EnemyChildPopulation
{
    [HarmonyPrefix]
    private static void Prefix(EnemyGroupScoreSpawnData __instance, ref eEnemyRole role, ref eEnemyRoleDifficulty diff)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return;
        var call = (Data: __instance, Role: role, Difficulty: diff);
        session.Guard(ref call, static (EnemyModule module,
            ref (EnemyGroupScoreSpawnData Data, eEnemyRole Role, eEnemyRoleDifficulty Difficulty) value)
            => module.SelectChildPopulation(value.Data, ref value.Role, ref value.Difficulty));
        role = call.Role;
        diff = call.Difficulty;
    }
}

[HarmonyPatch(typeof(EnemyGroupScoreSpawnData), nameof(EnemyGroupScoreSpawnData.GeneratePlacements))]
internal static class EnemyChildGroupLimit
{
    [HarmonyPrefix]
    private static void Prefix(EnemyGroupScoreSpawnData __instance, ref int wantedPlacements)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return;
        var call = (Data: __instance, Count: (int?)null);
        session.Guard(ref call, static (EnemyModule module, ref (EnemyGroupScoreSpawnData Data, int? Count) value)
            => value.Count = module.LimitChildGroup(value.Data));
        if (call.Count is int count) wantedPlacements = count;
    }
}

[HarmonyPatch(typeof(EAB_SpawnChildrenWave), nameof(EAB_SpawnChildrenWave.DoTrigger))]
internal static class EnemyChildWaveTrigger
{
    [HarmonyPrefix]
    private static bool Prefix(EAB_SpawnChildrenWave __instance)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return true;
        var call = (Wave: __instance, Allowed: true);
        session.Guard(ref call, static (EnemyModule module, ref (EAB_SpawnChildrenWave Wave, bool Allowed) c) => c.Allowed = module.ChildAttackAllowed(c.Wave));
        return call.Allowed;
    }
}

// The child wave halves of two targets the wave hooks own (`EnemyWaveSpawn`, `EnemyWaveGroupSpawning`): one patch class
// per game method, so these run from those classes' callbacks, after the wave observation of the same call.
internal static class EnemyChildWaveCalls
{
    internal static void Created(SurvivalWave wave)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return;
        session.Guard(ref wave, static (EnemyModule module, ref SurvivalWave value) => module.ChildWaveSpawned(value));
    }

    /// <summary>Opens the group's child scope; false means the original body must not run (its result is already set).</summary>
    internal static bool BeforeGroup(EnemyGroup group, ref bool result, out EnemyModule.ChildGroupScope? scope)
    {
        scope = null;
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return true;
        var call = (Group: group, Scope: (EnemyModule.ChildGroupScope?)null, Allowed: true);
        session.Guard(ref call, static (EnemyModule module, ref (EnemyGroup Group, EnemyModule.ChildGroupScope? Scope, bool Allowed) value)
            => value.Allowed = module.BeforeChildWaveGroup(value.Group, out value.Scope));
        scope = call.Scope;
        if (!call.Allowed) result = session.Module.ChildGroupFinished(group);
        return call.Allowed;
    }

    internal static void AfterGroup(EnemyModule.ChildGroupScope? scope, ref bool result)
    {
        if (scope == null) return;
        try
        {
            if (EnemyHost.Offer(EnemyFeature.ChildSpawning) is { } session)
            {
                var call = (Scope: scope, Completed: false);
                session.Guard(ref call, static (EnemyModule module, ref (EnemyModule.ChildGroupScope Scope, bool Completed) value) => value.Completed = module.AfterChildWaveGroup(value.Scope));
                if (call.Completed) result = true;
            }
        }
        finally { scope.Dispose(); }
    }
}
[HarmonyPatch(typeof(EnemyCostManager), nameof(EnemyCostManager.GetEnemyCost))]
internal static class EnemyChildWaveCost
{
    [HarmonyPrefix]
    private static bool Prefix(ref float __result)
    {
        if (!EnemyChildSpawningHooks.Wanted(out var session)) return true;
        var call = (Cost: 0f, Applies: false);
        session.Guard(ref call, static (EnemyModule module, ref (float Cost, bool Applies) c) => c.Applies = module.ChildWaveCost(out c.Cost));
        if (!call.Applies) return true;
        __result = call.Cost;
        return false;
    }
}
