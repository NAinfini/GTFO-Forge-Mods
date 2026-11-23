using System;
using BepInEx.Configuration;
using ForgeEngine.Framework;

namespace ForgeEngine;

/// <summary>Host-owned config keys. Plans are discovered under BepInEx/plugins (I-PACK D-009), not configured here.</summary>
internal static class RuntimeSettings
{
    internal static RuntimeMode Mode { get; private set; } = RuntimeMode.Off;
    internal static RuntimeLogLevel LogLevel { get; private set; } = RuntimeLogLevel.Off;
    /// <summary>For building and testing maps: the Rundown page opens every level (read by <c>RundownMenu.ApplyUnlockAll</c>).</summary>
    internal static ConfigEntry<bool>? UnlockAllLevels { get; private set; }
    internal static void Bind(ConfigFile config)
    {
        Mode = RuntimeMode.Off;
        LogLevel = RuntimeLogLevel.Off;
        // The host's own file is its ForgeCore settings file, the one configuration path every Forge package uses.
        ForgeCore.Settings.Adopt(Plugin.PluginGuid, config);
        // Bind raw text so ConfigFile cannot silently replace a malformed enum with the default.
        // Play is the default: Rundown packages ship no base-package cfg (D-011), and authors opt into Authoring.
        var mode = ForgeCore.Settings.Bind(Plugin.PluginGuid, "Runtime", "Mode", "Play",
            "Play = framework and game bindings without diagnostic collectors; Authoring = Play plus authoring diagnostics; Off = no components or hooks. Restart required. No mode enables a plan automatically.");
        Mode = mode.Value.Trim().ToLowerInvariant() switch
        {
            "off" or "0" => RuntimeMode.Off,
            "authoring" or "1" => RuntimeMode.Authoring,
            "play" or "2" => RuntimeMode.Play,
            _ => throw new InvalidOperationException("Unsupported Forge Engine.Mode; use Off, Play or Authoring. Native startup was not attempted.")
        };
        // Raw text for the same reason as Mode; trace is deliberately not accepted here, only ForgeDevelopment elevation reaches it.
        var level = ForgeCore.Settings.Bind(Plugin.PluginGuid, "Logging", "Level", "error",
            "Forge Engine records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error records are mirrored to the console, info records only with ForgeCore's developer logs (NAinfini.ForgeCore.cfg Logging.DeveloperLogs, or ForgeDevelopment installed). Runtime.Mode Off starts no writer. Restart required.");
        LogLevel = RuntimeLogConfiguration.ParseLevel(level.Value);
        UnlockAllLevels = ForgeCore.Settings.Bind(Plugin.PluginGuid, "Rundowns", "UnlockAllLevels", false,
            "For building and testing maps: every level of every Rundown can be started, whatever this computer has cleared. Clears are still counted; turn it off and the levels lock again by your own clears. Applies the next time the Rundown page refreshes.");
    }
}
