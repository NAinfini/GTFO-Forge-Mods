using System;
using Gear;
using UnityEngine;

namespace ForgeDevelopment.Native;

// Damage and weapon sampling is an opt-in (Authoring/CombatSamplingKey, F11 by default). Its two game methods belong to ForgeCore's one patch of each; the subscription is made
// the first time sampling starts and cannot be withdrawn, so the callbacks do nothing while sampling is off.
internal static class CombatSampling
{
    internal static bool Active { get; private set; }

    internal static void Sync(bool enabled)
    {
        if (enabled) DevelopmentGameHooks.SubscribeCombat();
        Active = enabled;
    }

    internal static void Stop() => Active = false;

    internal static void Toggle()
    {
        try { Sync(!Active); }
        catch (Exception error)
        {
            RuntimeDiagnostics.Safe(() => Plugin.PluginLog.LogError("Forge damage sampling change failed: " + error));
            return;
        }
        RuntimeDiagnostics.Safe(() =>
        {
            RuntimeDiagnostics.Report?.Event("combat_test", Active ? "begin" : "end", "local_capture", new() { ["authority"] = ForgeCore.Session.IsHost ? "host" : "client_observation" });
            Plugin.PluginLog.LogInfo("Forge damage sampling " + (Active ? "started" : "stopped") + ". " + Settings.ExportKey.Value + " exports the report.");
        });
    }
}

internal static class DamageSample
{
    internal static float? Before(ForgeCore.EnemyDamageCall call)
    {
        if (!CombatSampling.Active) return null;
        return ReadHealth(call.Receiver);
    }

    // Keep the capture in its own method: the C# compiler allocates a display class at method entry,
    // even when a source-level conditional precedes the lambda.
    private static float? ReadHealth(Dam_EnemyDamageBase receiver)
    {
        float? health = null;
        RuntimeDiagnostics.Safe(() => health = receiver.Health);
        return health;
    }

    internal static void After(ForgeCore.EnemyDamageCall call, float? before, bool ran)
    {
        if (!before.HasValue) return;
        RecordDamage(call.Receiver, call.Damage, call.LimbId, call.GearCategoryId, before.Value);
    }

    private static void RecordDamage(Dam_EnemyDamageBase receiver, float damage, int limb, uint gear, float before)
    {
        RuntimeDiagnostics.Safe(() =>
        {
            var after = receiver.Health;
            RuntimeDiagnostics.Report?.Event("damage_sample", "received_damage", receiver.Owner == null ? "unknown_enemy" : receiver.Owner.GetInstanceID().ToString(), new()
            {
                ["enemyDataId"] = receiver.Owner?.EnemyDataID.ToString() ?? "unavailable",
                ["gearCategoryId"] = gear.ToString(),
                ["limbIndex"] = limb.ToString(),
                ["inputDamage"] = RuntimeDiagnostics.Number(damage),
                ["healthBefore"] = RuntimeDiagnostics.Number(before),
                ["healthAfter"] = RuntimeDiagnostics.Number(after),
                ["effectiveDamage"] = RuntimeDiagnostics.Number(Math.Max(0, Math.Max(0, before) - Math.Max(0, after))),
                ["authority"] = ForgeCore.Session.IsHost ? "host" : "client_observation",
                ["frame"] = Time.frameCount.ToString()
            });
        });
    }
}

internal static class WeaponSample
{
    internal static void Wielded(BulletWeapon __instance)
    {
        if (!CombatSampling.Active) return;
        RuntimeDiagnostics.Safe(() =>
        {
            var data = __instance.m_archeType?.m_archetypeData;
            if (data == null) return;
            RuntimeDiagnostics.Report?.Event("effective_weapon", "wield", data.persistentID.ToString(), new()
            {
                ["name"] = __instance.ArchetypeName,
                ["damage"] = RuntimeDiagnostics.Number(data.Damage),
                ["clipSize"] = __instance.ClipSize.ToString(),
                ["reloadSeconds"] = RuntimeDiagnostics.Number(__instance.ReloadTime),
                ["scope"] = "Observed native effective values. Per-shot third-party behavior is represented by separate damage samples, not inferred from these fields."
            });
        });
    }
}
