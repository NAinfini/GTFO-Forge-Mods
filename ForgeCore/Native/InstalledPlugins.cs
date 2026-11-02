using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Unity.IL2CPP;

namespace ForgeCore;

/// <summary>One loaded BepInEx plugin: its GUID, version and the SHA-256 of its DLL (empty when the file cannot be read).</summary>
public readonly record struct PluginPrint(string Guid, string Version, string DllHash);

/// <summary>The BepInEx plugins this process loaded.</summary>
public static class InstalledPlugins
{
    /// <summary>Every loaded plugin, ordered by GUID.</summary>
    public static List<PluginPrint> List()
    {
        var plugins = new List<PluginPrint>();
        foreach (var info in IL2CPPChainloader.Instance.Plugins.Values)
        {
            var hash = "";
            try
            {
                using var stream = File.OpenRead(info.Location);
                using var sha = SHA256.Create();
                hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Plugin " + info.Metadata.GUID + " could not be hashed (" + info.Location + "); its DLL hash is empty: " + error); }
            plugins.Add(new PluginPrint(info.Metadata.GUID, info.Metadata.Version.ToString(), hash));
        }
        plugins.Sort((a, b) => string.CompareOrdinal(a.Guid, b.Guid));
        return plugins;
    }

    /// <summary>One hash over <see cref="List"/>: equal exactly when the same plugins of the same versions and DLL contents are loaded.</summary>
    public static string Fingerprint()
    {
        var fingerprint = new ForgeCore.Fingerprint();
        foreach (var plugin in List()) fingerprint.Add(plugin.Guid, plugin.Version + "|" + plugin.DllHash);
        return fingerprint.Hex();
    }
}