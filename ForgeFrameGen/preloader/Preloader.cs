using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace ForgeFrameGen;

/// <summary>ForgeFrameGen's preloader patcher (Docs/forge-upscale/PLAN.md §3.3): before Unity creates its graphics device it loads the
/// native bridge from the plugin folder and installs the graphics hooks, which replace the game's swap chain with the present bridge,
/// whatever the player's settings say: frame generation and DLSS then switch on and off while the game runs. A PC that cannot run frame
/// generation (the native side checks the card and the driver) keeps the game's own swap chain and logs why. It patches no assembly.</summary>
[PatcherPluginInfo("NAinfini.ForgeFrameGen.Preloader", "Forge FrameGen preloader", "1.0.0")]
public sealed unsafe class Preloader : BasePatcher
{
    public override void Initialize()
    {
        var log = BepInEx.Logging.Logger.CreateLogSource("ForgeFrameGen.Preloader");
        try
        {
            var natives = Directory.EnumerateFiles(Paths.PluginPath, PackageFiles.NativeFile, SearchOption.AllDirectories).ToList();
            if (natives.Count != 1)
            {
                log.LogError((natives.Count == 0 ? "No " : "More than one ") + PackageFiles.NativeFile + " under " + Paths.PluginPath
                    + "; graphics hooks not installed, frame generation is not available. Reinstall Forge FrameGen.");
                return;
            }
            var native = NativeLibrary.Load(natives[0]);
            // The frame generation the player picked, for the swap chain the game makes before the plugin loads: AMD FSR Frame Generation
            // first when it is picked, else DLSS Frame Generation first. The plugin passes later changes on while the game runs.
            var prefer = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(native, "ForgeFrameGen_SetFrameGenerationPreference");
            prefer(PickedFrameGeneration(log));
            var install = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(native, "ForgeFrameGen_InstallGraphicsHooks");
            // What the hooks did is queued in the native bridge and logged by the plugin once it loads.
            install(1);
            WatchForPlugin(native, log);
        }
        catch (Exception error)
        {
            log.LogError("Installing the graphics hooks failed; frame generation is not available: " + error);
        }
    }

    /// <summary>The plugin logs the bridge's lines once it loads. When it does not (a missing dependency such as GTFO-API leaves it out),
    /// the bridge parks on a plain swap chain at the game's first present; this says so once and writes the bridge's queued lines, which
    /// nothing else would. The thread ends once the plugin is there, the bridge has parked, or no bridge was built.</summary>
    private static void WatchForPlugin(IntPtr native, BepInEx.Logging.ManualLogSource log)
    {
        var parked = (delegate* unmanaged<int>)NativeLibrary.GetExport(native, "ForgeFrameGen_Parked");
        var pop = (delegate* unmanaged<byte*, int, int*, int>)NativeLibrary.GetExport(native, "ForgeFrameGen_PopBridgeMessage");
        var state = (delegate* unmanaged<int>)NativeLibrary.GetExport(native, "ForgeFrameGen_PresentState");
        new System.Threading.Thread(() =>
        {
            int now;
            while ((now = parked()) == 0 && state() >= 0) System.Threading.Thread.Sleep(1000);
            if (now != 1) return;
            // The bridge's failure lines; its developer lines are dropped, and counted in the closing line.
            var buffer = stackalloc byte[1024];
            int kind, length, dropped = 0;
            while ((length = pop(buffer, 1024, &kind)) >= 0)
                if (kind == 0) dropped++;
                else log.LogWarning(System.Text.Encoding.UTF8.GetString(buffer, length));
            log.LogWarning("The Forge FrameGen plugin did not load (see the errors above): frame generation is off and the game's frames pass through unchanged ("
                + dropped + " developer lines of the bridge not written).");
        })
        { IsBackground = true, Name = "ForgeFrameGen.Preloader" }.Start();
    }

    /// <summary>The plugin's [Frame Generation] Method setting as the native choice (Off and Auto 0, DLSS 1, FSR 2), read from its config
    /// file: the plugin itself loads only after the swap chain is made. No file or no entry yet (the first start) is Auto.</summary>
    private static int PickedFrameGeneration(BepInEx.Logging.ManualLogSource log)
    {
        var file = Path.Combine(Paths.ConfigPath, PackageFiles.PluginId + ".cfg");
        if (!File.Exists(file)) return 0;
        var section = "";
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (section == "Frame Generation" && line.StartsWith("Method") && line.IndexOf('=') is var at and > 0 && line[..at].Trim() == "Method")
            {
                var value = line[(at + 1)..].Trim();
                if (value == "FSR") return 2;
                if (value == "DLSS") return 1;
                if (value is not ("Auto" or "Off")) log.LogWarning("Method = " + value + " in " + file + " is not Off, Auto, DLSS or FSR; frame generation picks as for Auto.");
                return 0;
            }
        }
        return 0;
    }
}
