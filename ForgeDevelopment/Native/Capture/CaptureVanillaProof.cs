using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Enemies;
using HarmonyLib;
using UnityEngine;

namespace ForgeDevelopment.Native;

// These packages load profiles/content from plugins/*/forge, and actions need a loaded plan.
// Absence is checked both at startup (profiles are frozen there) and at the read, not inferred
// from an official level name. Any installed content is conservatively rejected, even if inactive.
internal static class CaptureVanillaProof
{
    internal sealed record Evidence(string Rule, bool Qualified, int? LoadedPlans, bool ExperimentRunning, int ExperimentExecutions,
        bool LocalRundownOverride, string? NativeDataOverrideFailure,
        string[] SessionForgeInputs, string[] ForgeInputs, object[] Patches, string? Failure);
    private static string[]? _startup;
    private static string[] _current = Array.Empty<string>();
    private static int _frame = -1;
    private static string? _startupDataRefusal, _currentDataRefusal;
    // Third-party patches reviewed by decompiling the exact assembly bytes. CheatConsole 0.14.0's
    // EnemyAgent.Setup postfix only attaches its nav/scout path overlay component; it writes no
    // health, limb, prefab or DataBlock state and runs after the agent's limbs are set up.
    // Any other build of that assembly is unverified again.
    private static readonly (string Owner, string Method, string Kind, string Patch, string AssemblySha256)[] VerifiedForeign =
    {
        ("CheatConsole.Harmony", "Enemies.EnemyAgent.Setup", "postfix", "_XA1cOty3nGGtL54EapPvhC2RSgw.Postfix",
            "1875534daedc057f70f2d8a9d8042d3b3b95fddaede0171a3d322b7d9ed29724"),
    };
    private static readonly Dictionary<string, string> AssemblyHashes = new(StringComparer.OrdinalIgnoreCase);
    private static bool Verified(string owner, string method, string kind, MethodInfo? patch)
    {
        var name = patch?.DeclaringType?.FullName + "." + patch?.Name;
        var location = patch?.DeclaringType?.Assembly.Location;
        var expected = VerifiedForeign.Where(entry => entry.Owner == owner && entry.Method == method && entry.Kind == kind
            && entry.Patch == name).Select(entry => entry.AssemblySha256).ToArray();
        if (expected.Length == 0 || string.IsNullOrEmpty(location)) return false;
        if (!AssemblyHashes.TryGetValue(location, out var hash))
        {
            hash = ForgeCore.Hash.Sha256Hex(File.ReadAllBytes(location));
            AssemblyHashes[location] = hash;
        }
        return expected.Contains(hash);
    }
    internal static void Start()
    {
        _startup = Inputs();
        _startupDataRefusal = LevelGuideCapturePurity.Refusal(Paths.BepInExRootPath, true);
    }
    private static string[] Inputs()
    {
        var root = Path.Combine(Paths.BepInExRootPath, "plugins");
        var inputs = new List<string>();
        if (!Directory.Exists(root)) return inputs.ToArray();
        foreach (var package in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cannot prove capture inputs through a linked package: " + package);
            var forge = Path.Combine(package, "forge");
            if (!Directory.Exists(forge)) continue;
            Visit(forge);
        }
        return inputs.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cannot prove capture inputs through a link: " + directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked capture input: " + path);
                if ((attributes & FileAttributes.Directory) != 0) Visit(path);
                else if (path.EndsWith(".forge", StringComparison.OrdinalIgnoreCase)) inputs.Add(Path.GetRelativePath(Paths.BepInExRootPath, path));
            }
        }
    }
    internal static Evidence Read(MethodBase? method, string[] owners)
    {
        if (_frame != Time.frameCount)
        {
            _current = Inputs(); _frame = Time.frameCount;
            _currentDataRefusal = LevelGuideCapturePurity.Refusal(Paths.BepInExRootPath, true);
        }
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var methods = new[] { (typeof(EnemyPrefabManager), "BuildEnemyPrefab"), (typeof(EnemyAgent), "Setup"),
                (typeof(Dam_EnemyDamageBase), "Setup"), (typeof(EnemySync), "OnSpawn") }
            .SelectMany(entry => entry.Item1.GetMethods(flags).Where(target => target.Name == entry.Item2))
            .Cast<MethodBase?>().Append(method);
        var patches = new List<object>();
        var unknown = false;
        foreach (var target in methods.Where(value => value != null).Distinct())
        {
            var info = Harmony.GetPatchInfo(target!);
            if (info == null) continue;
            var manifest = DevelopmentNativeHooks.Shared.TryGetManifest(target!);
            if (manifest?.DamageWriterActive == true || info.ILManipulators.Count != 0) unknown = true;
            Add(info.Prefixes, "prefix"); Add(info.Postfixes, "postfix");
            Add(info.Transpilers, "transpiler"); Add(info.Finalizers, "finalizer");
            void Add(IEnumerable<Patch> entries, string kind)
            {
                foreach (var patch in entries)
                {
                    var methodName = target!.DeclaringType?.FullName + "." + target.Name;
                    if (patch.owner == CaptureHookProvenance.SharedOwner)
                    {
                        if (!CaptureHookProvenance.Physical(patch, manifest, kind)) unknown = true;
                    }
                    else if (!owners.Contains(patch.owner) && !Verified(patch.owner, methodName, kind, patch.PatchMethod)) unknown = true;
                    patches.Add(new
                    {
                        method = methodName,
                        kind,
                        owner = patch.owner,
                        patch = patch.PatchMethod?.DeclaringType?.FullName + "." + patch.PatchMethod?.Name,
                        priority = patch.priority,
                        consumerManifest = patch.owner == CaptureHookProvenance.SharedOwner ? CaptureHookProvenance.Describe(manifest) : null
                    });
                }
            }
        }
        int? plans;
        try { plans = EngineLookup.Installed ? EngineLookup.Call<int>("LoadedPlans") : 0; }
        catch (InvalidOperationException) { plans = null; } // An Engine that is not in Authoring mode cannot say; the proof fails as unavailable.
        var experiment = ExperimentRunner.Running;
        var executions = ExperimentRunner.SessionExecutions;
        var local = Globals.Global.UseLocalRundownOverride;
        var dataRefusal = _startupDataRefusal ?? _currentDataRefusal;
        var failure = _startup == null ? "startup-inputs-unverified"
            : _startup.Length != 0 || _current.Length != 0 ? "forge-content-present"
            : plans != 0 ? "forge-plans-active-or-unavailable"
            : experiment || executions != 0 ? "forge-experiment-executed"
            : local || dataRefusal != null ? "native-data-overridden" : unknown ? "unverified-patch-owner" : null;
        return new("no-forge-content-or-plans", failure == null, plans, experiment, executions, local, dataRefusal,
            _startup ?? Array.Empty<string>(), _current, patches.ToArray(), failure);
    }
}
