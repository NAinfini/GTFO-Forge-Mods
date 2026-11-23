using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

internal static class NativeLayoutSuite
{
    internal static int Run(string[] args)
    {
        if (args.Length != 4)
        { Console.Error.WriteLine("Usage: NativeLayout <BepInEx> <ForgeEngine.dll> <ForgeEngine.Framework.dll> <report.json>"); return 2; }
        var checks = new List<CheckRow>();
        void Check(string id, bool passed) => checks.Add(new(id, passed));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        string Hash(string p) { using var s = File.OpenRead(p); using var h = SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
        using var host = AssemblyDefinition.ReadAssembly(args[1]);
        using var sdk = AssemblyDefinition.ReadAssembly(args[2]);
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "Modules-ASM.dll"));
        using var navigation = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "interop", "UnityEngine.AIModule.dll"));
        var hostTypes = host.MainModule.Types.SelectMany(Walk).ToArray();
        var enemyTypes = ForgeTestInputs.DomainTypes.Native(host, sdk, "Enemy", "ForgeEngine.GameBindings.GameAreaVolumes", "ForgeEngine.GameBindings.GameCourseNodes");
        var gameTypes = game.MainModule.Types.SelectMany(Walk).ToArray();
        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var nativeHooks = enemyTypes.Where(Patch).ToArray();
        var worldHooks = hostTypes.Where(t => Patch(t) && t.Namespace == "ForgeEngine.GameBindings").ToArray();
        Check("enemy.single-receiver", enemyTypes.Count(t => t.Name == "EnemyModule") == 1);
        Check("enemy.unique-hook-types", nativeHooks.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == nativeHooks.Length);
        // The authoring movement evidence must read the original Unity navigation properties, through the exact receiver.
        var navType = navigation.MainModule.Types.Single(t => t.FullName == "UnityEngine.AI.NavMeshAgent");
        var moveReadback = enemyTypes.Single(t => t.Name == "EnemyModule").Methods.Single(m => m.Name == "TraceMoveReadback");
        var navReads = moveReadback.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        foreach (var member in new[] { "pathPending", "pathStatus", "hasPath", "remainingDistance" })
            Check("enemy.move-readback." + member, navType.Methods.Count(m => m.Name == "get_" + member && m.Parameters.Count == 0) == 1
                && navReads.Any(m => m.DeclaringType.FullName == navType.FullName && m.Name == "get_" + member));
        var navStatus = navigation.MainModule.Types.Single(t => t.FullName == "UnityEngine.AI.NavMeshPathStatus");
        foreach (var (name, value) in new[] { ("PathComplete", 0), ("PathPartial", 1), ("PathInvalid", 2) })
            Check("enemy.move-readback.path-status." + name, navStatus.Fields.Any(f => f.Name == name && Convert.ToInt32(f.Constant) == value));
        // The host's own game-binding patches: none. The level lifecycle and the checkpoint save/reload are GTFO-API
        // subscriptions, and spawn/despawn/damage belong to the enemy package, so the host's binding namespace carries no
        // patch class at all.
        Check("host.exact-hook-set", worldHooks.Length == 0);
        foreach (var hook in nativeHooks)
        {
            var target = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = target.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var name = target.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            var actualTypes = gameTypes.Where(t => t.FullName == type.FullName).ToArray();
            Check("native.target." + hook.Name, actualTypes.Length == 1
                && actualTypes[0].Methods.Count(m => m.Name == name) == 1);
            foreach (var method in hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix")))
            {
                Check("native.static." + hook.Name + "." + method.Name, method.IsStatic);
                // A hook either patches an instance method and names its receiver, or patches a static one and has no
                // `__instance` at all; the parameter it does carry has to be the target type, never a lookalike.
                var receiver = method.Parameters.SingleOrDefault(p => p.Name == "__instance");
                Check("native.instance." + hook.Name + "." + method.Name,
                    receiver == null || receiver.ParameterType.FullName == type.FullName);
            }
        }
        var start = enemyTypes.Single(t => t.FullName == "ForgeEnemy.Native.EnemyHost").Methods.Single(m => m.Name == "Start");
        var calls = start.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check("enemy.no-second-update-loop", !enemyTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));
        Check("enemy.no-unity-frame-component", !enemyTypes.Any(t => t.BaseType?.Name == "MonoBehaviour"));
        Check("enemy.frame-is-runtime-work-phase", calls.Any(m => m.DeclaringType.FullName == "ForgeEngine.GameBindings.DomainWorkPhases" && m.Name == ".ctor"));
        // The engine is the only installer: nothing the domain start does touches a Harmony instance, and the domain hands its one
        // list of callback classes over by declaration.
        Check("enemy.no-harmony", !enemyTypes.Where(t => t.FullName.StartsWith("ForgeEnemy.Native.EnemyHost", StringComparison.Ordinal))
            .SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .Any(m => m.DeclaringType.FullName == "HarmonyLib.Harmony"));
        Check("enemy.declares-through-the-runtime", enemyTypes.Where(t => t.Name == "EnemyNativeHooks").SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .Any(m => m.DeclaringType.FullName == "ForgeEngine.GameBindings.NativeHookRegistration" && m.Name == "Declare"));
        Check("host.no-implicit-enemy", !hostTypes.Single(t => t.Name == "GameRuntimeBridge").Methods
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .Any(m => m.DeclaringType.Name == "EnemyModule"));
        var report = new
        {
            verification = "compiled-assembly-metadata-only",
            gameExecuted = false,
            hostSha256 = Hash(args[1]),
            sdkSha256 = Hash(args[2]),
            hookClasses = nativeHooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            passed = checks.Count(c => c.Passed),
            failed = checks.Count(c => !c.Passed),
            checks
        };
        string output = Path.GetFullPath(args[3]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id);
        Console.WriteLine($"{(report.failed == 0 ? "PASS" : "FAIL")} {report.passed}/{checks.Count} native module layout checks; no GTFO execution.");
        return report.failed == 0 ? 0 : 1;

    }
}

internal sealed record CheckRow(string Id, bool Passed);
