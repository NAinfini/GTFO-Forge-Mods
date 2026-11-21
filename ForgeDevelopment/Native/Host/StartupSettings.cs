namespace ForgeDevelopment.Native;

internal static class StartupSettings
{
    /// <summary>Binds every startup key in one ForgeCore settings batch: the file is saved once, and a failed bind still
    /// fails startup instead of being replaced by a save failure.</summary>
    internal static void Bind()
        => ForgeCore.Settings.Batch(Plugin.PluginGuid, config =>
        {
            DropMeasurement.Bind(config);
            Settings.Bind(config);
            Settings.BindAuthoring(config);
            Settings.BindRecorder();
            NetSim.Bind(config);
        });
}
