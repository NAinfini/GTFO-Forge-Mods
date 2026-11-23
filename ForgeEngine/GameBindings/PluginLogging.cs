using BepInEx.Configuration;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>A base package's Forge log level, bound in its own <c>BepInEx/config/&lt;plugin GUID&gt;.cfg</c> through
/// ForgeCore's one configuration path (<see cref="ForgeCore.Settings"/>), which also holds the package's other keys.</summary>
public static class PluginLogging
{
    /// <summary>Adopts the plugin's own <paramref name="config"/> as its ForgeCore settings file and binds Logging/Level.</summary>
    public static RuntimeLogLevel Bind(ConfigFile config, string pluginGuid)
    {
        ForgeCore.Settings.Adopt(pluginGuid, config);
        return RuntimeLogConfiguration.ParseLevel(ForgeCore.Settings.Bind(pluginGuid, "Logging", "Level", "error",
            "Forge log level: off, error or info. Default error. Restart required.").Value);
    }
}
