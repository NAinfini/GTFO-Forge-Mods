namespace ForgeFrameGen;

/// <summary>Names the plugin and the preloader both use, so both compile this one definition: the native bridge's file and the plugin
/// ID, which also names the config file the preloader reads the upscaler from.</summary>
internal static class PackageFiles
{
    internal const string NativeFile = "ForgeFrameGen.Native.dll", PluginId = "NAinfini.ForgeFrameGen";
}
