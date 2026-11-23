using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

// The enemy-domain observation hooks this batch adds: the spawn of an enemy, the game's own tag
// transaction, the moment foam really lands on an enemy's own glue receiver, and the moment an enemy's own
// attack state begins an attack.
//
// Every hook here reads state the native call has already written and publishes a fact; none of them writes the
// world. They are listed in `EnemyNativeHooks.Types` with every other callback class.

/// <summary>The generic entity-spawn fact, published for an enemy from the enemy's own spawn hook. It
/// runs after the registration subscription, so the reference this publishes is the one the module already
/// tracks; the position is read from the same instance, and an instance that does not read publishes nothing. The
/// spawn packet the game handed the hook carries the mode the life entered the level with, which the module
/// records on that life for the `spawn_origin` read.</summary>
internal static class EnemyNodeSpawned
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemySyncSpawned.After(static (call, _) => Spawned(call.Sync, call.Data));

    internal static void Spawned(EnemySync sync, pEnemySpawnData spawnData)
    {
        if (EnemyHost.Offer(EnemyFeature.SpawnFact) is not { } session) return;
        (EnemySync Sync, pEnemySpawnData Data) call = (sync, spawnData);
        session.Guard(ref call, static (EnemyModule module, ref (EnemySync Sync, pEnemySpawnData Data) c) =>
        {
            if (c.Sync.m_agent != null) module.AfterSpawn(c.Sync.m_agent, c.Data.mode);
        });
    }
}

/// <summary>The game's own tag transaction, on the host: `DoTagEnemy` is what `SNet_AuthorativeAction` runs on the
/// master after a scanner client asked for a tag, and its packet carries the one enemy it tagged. The hook reads
/// that enemy back through this module's own registration, so a tag on an enemy this provider cannot name is not
/// a fact. The packet's own agent handle is read inside the module: a packet that arrives after its agent was torn
/// down answers nothing rather than throwing into the native action.</summary>
[HarmonyPatch(typeof(ToolSyncManager), nameof(ToolSyncManager.DoTagEnemy))]
internal static class EnemyNodeTagged
{
    [HarmonyPostfix]
    private static void Postfix(ToolSyncManager.pTagEnemy data) => EnemyHost.Offer(EnemyFeature.Tag)?.Guard(module => module.AfterTagged(data));
}

/// <summary>The moment foam lands on an enemy. `AddToTotalGlueVolume` is the game's own glue accumulation entry:
/// the glue gun's projectile calls it for every glued enemy, so a postfix here sees every application — one this
/// provider made and one the game made — and the two volumes it reads are what tells a real application from a
/// call that changed nothing.</summary>
[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.AddToTotalGlueVolume))]
internal static class EnemyNodeGlued
{
    // Every foam hit calls this pair, so both halves use the closure-free guard: a static lambda and the call's own
    // data by reference.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(Dam_EnemyDamageBase __instance, out EnemyModule.GlueObservation? __state)
    {
        __state = null;
        if (EnemyHost.Offer(EnemyFeature.Glue) is not { } session || !session.Module.GlueObserved) return;
        var call = new Call { Receiver = __instance };
        session.Guard(ref call, static (EnemyModule module, ref Call glue) => glue.Observation = module.BeforeGlue(glue.Receiver));
        __state = call.Observation;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Dam_EnemyDamageBase __instance, EnemyModule.GlueObservation? __state)
    {
        if (__state == null) return;
        if (EnemyHost.Offer(EnemyFeature.Glue) is not { } session) return;
        var call = new Call { Receiver = __instance, Observation = __state };
        session.Guard(ref call, static (EnemyModule module, ref Call glue) => module.AfterGlue(glue.Receiver, glue.Observation));
    }

    private struct Call
    {
        internal Dam_EnemyDamageBase Receiver;
        internal EnemyModule.GlueObservation? Observation;
    }
}

/// <summary>An enemy beginning an attack ability. `RecieveAttackStart` is what `ES_EnemyAttackBase` runs on every
/// peer when the attack-start packet arrives, and its body (`pES_EnemyAttackData`) is the game's own answer to
/// "which ability, at whom, for how long": the ability kind and index, the target agent handle and the duration.
/// The enemy the fact is about is read from the state that received it — a state belongs to exactly one AI, and
/// the AI's own agent is the public half of that pairing — so a state whose agent does not read publishes
/// nothing rather than a fact about a different enemy.
///
/// The publication gate is the module's own host authority, so the packets every peer receives produce a fact on
/// the master only, which is where this package's observations are read.</summary>
[HarmonyPatch(typeof(ES_EnemyAttackBase), "RecieveAttackStart")]
internal static class EnemyNodeAbilityUsed
{
    [HarmonyPostfix]
    private static void Postfix(ES_EnemyAttackBase __instance, pES_EnemyAttackData attackData, bool __runOriginal)
    {
        if (!__runOriginal) return;
        if (EnemyHost.Offer(EnemyFeature.AbilityUsed) is not { } session || !session.Module.AbilityUsedObserved) return;
        var call = new EnemyActionHooks.Call { Attack = __instance, AttackData = attackData };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) =>
            module.AfterAbilityUsed(c.Attack!.m_enemyAgent, c.AttackData, EnemyModule.AttackKindOf(c.Attack)));
    }
}
