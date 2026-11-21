using BepInEx.Configuration;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal static partial class Settings
{
    internal static ConfigEntry<KeyCode> ExportKey = null!;
    internal static ConfigEntry<KeyCode> EnemyAbilityExportKey = null!;
    internal static ConfigEntry<KeyCode> CombatSamplingKey = null!;
    internal static ConfigEntry<float> InspectionBudget = null!;
    internal static ConfigEntry<string> ProjectManifest = null!;
    internal static void BindAuthoring(ConfigFile config)
    {
        ExportKey = config.Bind("Authoring", "ExportReportKey", KeyCode.F10, "Export the current structured report. Does not change game state.");
        EnemyAbilityExportKey = config.Bind("Authoring", "ExportEnemyAbilityKey", KeyCode.F3,
            "Write loaded enemy ability definitions, already-loaded numeric prefab fields and live cooldown table samples. Repeated exports preserve elapsed observations; cooldown units remain unverified.");
        CombatSamplingKey = config.Bind("Authoring", "CombatSamplingKey", KeyCode.F11, "Toggle damage and weapon sampling. Does not change game state.");
        InspectionBudget = config.Bind("Authoring", "InspectionBudgetMilliseconds", 2f,
            new ConfigDescription("Cooperative inspection budget per frame. A native call cannot be interrupted; actual maximum cost is reported.", new AcceptableValueRange<float>(0.25f, 5f)));
        if (!float.IsFinite(InspectionBudget.Value)) InspectionBudget.Value = 2f;
        ProjectManifest = config.Bind("Authoring", "ProjectManifest", "", "Optional local Forge project manifest path, relative to BepInEx. Used for declared dependencies, expected objects and source identifiers. Never downloads or executes code.");
        // The recorder's own keys live in their own section; see RecSettings.cs.
    }
}
