using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ForgeCore;

/// <summary>Puts a module's Harmony patches on the first time the module is used (<see cref="Ensure"/>), never at startup. At
/// startup only <see cref="CheckTargets"/> runs: a module whose game method is missing logs one error and every later call to it
/// throws instead of running without its patch. A mod's own hooks that serve one rarely used feature use <see cref="Lazy"/> the same way.</summary>
public static class Patches
{
    private static readonly Harmony Harmony = new(Plugin.Id);
    private static readonly Dictionary<string, string> Missing = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Applied = new(StringComparer.Ordinal);

    /// <summary>Patches that go on the first time <see cref="LazyPatches.Ensure"/> is called (the first use of the feature they serve),
    /// so a session that never uses it never pays for them. Once on they stay: the game keeps a detour for a patched method after an
    /// unpatch, so taking them off would not make the method any cheaper. <paramref name="install"/> runs once with a Harmony instance
    /// for <paramref name="modId"/>; if it throws the error is reported once and the feature stays unpatched.</summary>
    public static LazyPatches Lazy(string modId, Action<Harmony> install)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        return new LazyPatches(modId, install ?? throw new ArgumentNullException(nameof(install)));
    }

    internal static void CheckTargets()
    {
        // Assembly.GetType per loaded assembly, never AccessTools.TypeByName: that calls GetTypes on every assembly for every name,
        // and HarmonyX logs a ReflectionTypeLoadException warning for UnityEngine.CoreModule on each scan. The game's types are spread
        // over many interop assemblies (Modules-ASM, SNet_ASM, GameData-ASM ...), so every assembly is asked.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var (module, targets) in Modules.Patched)
            foreach (var target in targets)
            {
                Type? type = null;
                foreach (var assembly in assemblies)
                    if ((type = assembly.GetType(target.Type, false)) != null) break;
                // By name only: most targets are overloaded (Fire, ActivateState ...), so the method name is the whole check here; each patch class pins its exact overload.
                if (type != null && AccessTools.GetDeclaredMethods(type).Exists(m => m.Name == target.Method)) continue;
                Missing[module] = target.Type + "." + target.Method;
                Plugin.Logger.Error("ForgeCore module " + module + " is unavailable: the game has no " + target.Type + "." + target.Method + ".");
            }
    }

    /// <summary>The module's patches are on (first call puts them on); throws when the module is unavailable.</summary>
    internal static void Ensure(string module, Type patches)
    {
        if (Applied.Contains(module)) return;
        if (Missing.TryGetValue(module, out var method))
            throw new InvalidOperationException("ForgeCore module " + module + " is unavailable: " + method + " cannot be patched.");
        Harmony.PatchAll(patches);
        Applied.Add(module);
    }
}

/// <summary>A mod's patch set that goes on at first use (<see cref="Patches.Lazy"/>).</summary>
public sealed class LazyPatches
{
    private readonly string _modId;
    private readonly Action<Harmony> _install;
    private bool _tried;

    internal LazyPatches(string modId, Action<Harmony> install) { _modId = modId; _install = install; }

    /// <summary>Puts the patches on the first time; every later call returns at once.</summary>
    public void Ensure()
    {
        if (_tried) return;
        _tried = true;
        try { _install(new Harmony(_modId + ".lazy")); }
        catch (Exception error) { Log.For(_modId).ErrorOnce("Hooks of " + _modId + " could not be put on; the feature they serve stays unpatched: " + error); }
    }
}
