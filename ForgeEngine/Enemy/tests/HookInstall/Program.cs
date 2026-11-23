using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class HookInstallSuite
{
    internal static int Run(string[] args)
    {

        // The one invariant `EnemyNativeHooks` exists for, asserted against the assembly that ships: the domain start hands the
        // engine that one list, every Harmony patch class of the Enemy domain in `ForgeEngine.dll` is in it or is held back in `Pending`
        // with a reason, nothing is listed twice, and no Enemy code installs a patch itself (the engine is the only
        // installer). A hook written but left out of the list — the way a declared-but-never-installed family happens —
        // fails here instead of at run time in the game.
        //
        // The suite is reflection over the built assembly rather than a second compilation of the hook sources: a list
        // this file could compile would only agree with itself. The package is loaded with probe directories because
        // reading the attributes and base types of the classes the list names needs the BepInEx profile the plugin ships
        // against.
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: HookInstall <BepInEx> <ForgeEngine.dll> <report.json> [probeDir...]");
            return 2;
        }

        var bepinex = Path.GetFullPath(args[0]);
        var enemyPath = Path.GetFullPath(args[1]);
        var reportPath = Path.GetFullPath(args[2]);
        var probes = new List<string>
{
    Path.GetDirectoryName(enemyPath)!, Path.Combine(bepinex, "core"), Path.Combine(bepinex, "interop"),
    Path.Combine(bepinex, "plugins")
};
        probes.AddRange(args.Skip(3).Select(Path.GetFullPath));

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var directory in probes)
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        var checks = new List<HookCheckRow>();
        var hookNames = new List<string>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new HookCheckRow(id, passed, detail));
        string Names(IEnumerable<Type> types) => string.Join(",", types.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(enemyPath);
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        try { assembly.GetTypes(); Check("enemy.all-types-loaded", true); }
        catch (ReflectionTypeLoadException error)
        {
            Check("enemy.all-types-loaded", false, error.LoaderExceptions.Length + " type(s) could not be loaded: "
                + string.Join(" | ", error.LoaderExceptions.Take(3).Select(e => e?.Message)));
        }

        var hooks = assembly.GetType("ForgeEnemy.Native.EnemyNativeHooks", throwOnError: false);
        Check("enemy.hook-list-present", hooks != null);
        if (hooks == null) return Finish();

        var declared = ((System.Collections.IEnumerable)hooks.GetProperty("Types", statics)!.GetValue(null)!).Cast<Type>().ToArray();
        var pending = ((System.Collections.IDictionary)hooks.GetProperty("Pending", statics)!.GetValue(null)!).Keys.Cast<Type>().ToArray();

        // A list that answered with nothing would make every set comparison below trivially true.
        Check("enemy.declared-not-empty", declared.Length > 0, "declared=" + declared.Length);
        Check("enemy.no-duplicate-declarations", declared.Length == declared.Distinct().Count(),
            "declared=" + declared.Length + " distinct=" + declared.Distinct().Count());
        Check("enemy.pending-not-declared", !pending.Intersect(declared).Any(), "both=" + Names(pending.Intersect(declared)));

        // The direction that catches an omission: a patch class nothing declares is a hook the game never gets.
        var patchClasses = assembly.GetTypes().Where(t => IsEnemy(t) && IsHarmony(t)).ToArray();
        hookNames.AddRange(patchClasses.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
        var missing = patchClasses.Where(t => !declared.Contains(t) && !pending.Contains(t)).ToArray();
        Check("enemy.patch-classes-declared-or-pending", missing.Length == 0,
            "declared=" + Names(patchClasses.Where(declared.Contains)) + " pending=" + Names(pending) + " missing=" + Names(missing));

        // The other direction: a listed class that carries no patch is a name the Runtime would refuse.
        var notPatches = declared.Concat(pending).Where(t => !IsPatch(t)).ToArray();
        Check("enemy.declared-are-patch-classes", notPatches.Length == 0, "without-patch=" + Names(notPatches));

        // The list only decides anything if the load path hands it over, so the assembly is read for that call: `EnemyHost.Start` gives
        // it to the session as a callback, so the call site is the compiler's own closure method, which is still a method of a
        // type nested in `EnemyHost`. One site and no other.
        var declareMethod = hooks.GetMethod("Declare", statics)!;
        var declareSites = new List<MethodInfo>();
        var patchers = new List<string>();
        foreach (var type in assembly.GetTypes().Where(IsEnemy))
            foreach (var method in type.GetMethods(statics | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetMethodBody() == null) continue;
                if (CallsMethod(method, declareMethod)) declareSites.Add(method);
                // Constructing a Harmony instance or calling the old batch installer is a second installer.
                if (Calls(method, called => called.DeclaringType?.FullName is "HarmonyLib.Harmony" && called.IsConstructor
                        || called.DeclaringType?.FullName is "ForgeEngine.GameBindings.StartupPatchBatch"))
                    patchers.Add(type.FullName + "." + method.Name);
            }
        Check("enemy.host-declares-the-list",
            declareSites.Count == 1
            && declareSites[0].DeclaringType!.FullName!.StartsWith("ForgeEnemy.Native.EnemyHost", StringComparison.Ordinal),
            "call sites=" + string.Join(",", declareSites.Select(m => m.DeclaringType?.FullName + "." + m.Name)));
        Check("enemy.no-second-installer", patchers.Count == 0, "installers=" + string.Join(",", patchers));

        // A refused target stops only the function that owns it, so every listed class belongs to exactly one function, the
        // functions held back for evidence are exactly the pending classes, and no callback reads the live session directly:
        // the only way into the session from a hook is `EnemyHost.Offer(feature)`, which is null for a withdrawn function.
        var featureTable = ((System.Collections.IEnumerable)assembly.GetType("ForgeEnemy.Native.EnemyHookFeatures")!
            .GetProperty("Table", statics)!.GetValue(null)!).Cast<object>().ToArray();
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type[] HooksOf(object feature) => (Type[])feature.GetType().GetProperty("Hooks", members)!.GetValue(feature)!;
        string? EvidenceOf(object feature) => (string?)feature.GetType().GetProperty("Evidence", members)!.GetValue(feature);
        var owned = featureTable.SelectMany(HooksOf).ToArray();
        Check("enemy.every-class-has-one-function", owned.Length == owned.Distinct().Count()
            && !declared.Concat(pending).Except(owned).Any() && !owned.Except(declared.Concat(pending)).Any(),
            "unowned=" + Names(declared.Concat(pending).Except(owned)) + " unlisted=" + Names(owned.Except(declared.Concat(pending)))
            + " owned-twice=" + Names(owned.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key)));
        var held = featureTable.Where(feature => EvidenceOf(feature) != null).SelectMany(HooksOf).ToArray();
        Check("enemy.pending-is-the-evidence-functions", held.OrderBy(t => t.Name, StringComparer.Ordinal)
            .SequenceEqual(pending.OrderBy(t => t.Name, StringComparer.Ordinal)), "evidence=" + Names(held) + " pending=" + Names(pending));
        var sessionReaders = new List<string>();
        foreach (var type in owned.Concat(new[] { assembly.GetType("ForgeEnemy.Native.EnemyActionHooks")!, assembly.GetType("ForgeEnemy.Native.EnemyAbilityFieldHooks")!,
             assembly.GetType("ForgeEnemy.Native.EnemyScreamStart")! }).Distinct())
            foreach (var method in type.GetMethods(statics | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.GetMethodBody() != null && Calls(method, called => called.Name == "get_Session"
                        && called.DeclaringType?.FullName == "ForgeEnemy.Native.EnemyHost"))
                    sessionReaders.Add(type.Name + "." + method.Name);
        Check("enemy.callbacks-read-the-session-only-through-offer", sessionReaders.Count == 0, "readers=" + string.Join(",", sessionReaders));

        return Finish();
        bool IsEnemy(Type type) => type.Namespace is { } ns && (ns == "ForgeEnemy" || ns.StartsWith("ForgeEnemy.", StringComparison.Ordinal));
        bool IsHarmony(MemberInfo member) => member.GetCustomAttributesData()
            .Any(a => a.AttributeType.Namespace == "HarmonyLib");
        bool IsPatch(Type type) => type.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

        /// <summary>Whether one method's body calls the given method, read from the IL rather than from a source copy: a
        /// `call`/`callvirt` whose resolved token is that method.</summary>
        bool CallsMethod(MethodInfo caller, MethodInfo target) => Calls(caller, called => called == target);

        /// <summary>Whether one method's body calls or constructs anything the predicate accepts. The scan is byte-wise over
        /// `call`, `callvirt` and `newobj`, so a hit is a resolved method token, which is what makes it evidence rather than a
        /// text match.</summary>
        bool Calls(MethodInfo caller, Func<MethodBase, bool> accepts)
        {
            var il = caller.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
            for (int index = 0; index + 4 < il.Length; index++)
            {
                if (il[index] is not (0x28 or 0x6F or 0x73)) continue;
                try { if (caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1)) is { } called && accepts(called)) return true; }
                catch (Exception) { /* an operand that is not a method token is not this call */ }
            }
            return false;
        }
        int Finish()
        {
            int failed = checks.Count(c => !c.Passed), passed = checks.Count - failed;
            var report = new
            {
                schemaVersion = ForgeCore.FormatVersion.Current,
                verification = "built-enemy-assembly-reflected-for-the-one-native-declaration-list",
                gameExecuted = false,
                utc = DateTimeOffset.UtcNow,
                enemySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(enemyPath))),
                hookClasses = hookNames,
                passed,
                failed,
                checks
            };
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var row in checks.Where(c => !c.Passed))
                Console.Error.WriteLine("FAIL " + row.Id + ": " + row.Detail);
            Console.WriteLine($"PASS {passed}/{checks.Count} native declaration checks; failed {failed}");
            return failed == 0 ? 0 : 1;
        }

    }
}

internal sealed record HookCheckRow(string Id, bool Passed, string Detail);
