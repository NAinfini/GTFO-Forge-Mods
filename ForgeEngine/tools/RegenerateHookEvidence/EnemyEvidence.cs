using Mono.Cecil;
using System.Reflection.PortableExecutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;

namespace ForgeEngine.HookEvidence.Enemy;

internal static class EnemyEvidence
{
    internal static int Run(string[] args)
    {

        // Regenerates evidence/enemy-hooks.json: one row per Harmony patch class of the Enemy domain in the built ForgeEngine.dll, with the
        // target Runtime's own declaration reader resolves for it, the target's RVA read from the pinned dump.cs, the first 32
        // bytes of the pinned GameAssembly.dll at that RVA and Runtime's byte-policy verdict for them. The target is resolved by
        // the same NativeHookRegistration.Definition that admits it in the game, called from the built ForgeEngine.dll, so the
        // evidence cannot read a target differently from the installer.
        const string ExpectedBuild = "20403457";
        const string ExpectedGameSha = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
        const string ExpectedDumpSha = "BF657C0ED162E781DAB49D8C9299130F07186F59B73DAE7F28E598FD396DE1CC";
        const string Writer = "forge.module.gtfo.enemy";
        const string PolicySource = "Mods/ForgeCore/Native/HookPrologue.cs";
        const string LengthSource = "Mods/ForgeCore/Native/HookPrologue.cs";

        string bep = Path.GetFullPath(args[0]), artifacts = Path.GetFullPath(args[1]);
        string outputPath = Path.GetFullPath(args[2]), dumpPath = Path.GetFullPath(args[3]);
        string gameRoot = Environment.GetEnvironmentVariable("FORGE_GTFO_GAME") ?? @"E:\SteamLibrary\steamapps\common\GTFO";
        string gamePath = Path.Combine(gameRoot, "GameAssembly.dll");
        string repository = Path.GetDirectoryName(outputPath)!;
        while (!File.Exists(Path.Combine(repository, PolicySource)))
            repository = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("The repository root holding " + PolicySource + " was not found above the output file.");

        string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        string SourceSha(string relative)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(repository, relative)).Replace("\r\n", "\n"))));
        if (!string.Equals(Sha(gamePath), ExpectedGameSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GameAssembly.dll is not the pinned build " + ExpectedBuild + ".");
        var dump = Dump.Open(dumpPath, ExpectedDumpSha, Console.Error.WriteLine)
            ?? throw new InvalidOperationException("dump.cs is not the dump of the pinned build " + ExpectedBuild + ".");

        var probes = new[]
        {
            Path.Combine(artifacts, "bin/ForgeEngine/release"), Path.Combine(artifacts, "bin/ForgeCore/release"), Path.Combine(artifacts, "bin/ForgeEngine.Framework/release"),
            Path.Combine(artifacts, "bin/ForgeAudio/release"), Path.Combine(artifacts, "bin/ForgeVisual/release"), Path.Combine(artifacts, "bin/ForgeUI/release"),
            Path.Combine(bep, "core"), Path.Combine(bep, "interop"), Path.Combine(bep, "plugins")
        };
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var directory in probes)
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var runtime = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(artifacts, "bin/ForgeEngine/release/ForgeEngine.dll"));
        var prologue = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(artifacts, "bin/ForgeCore/release/ForgeCore.dll")).GetType("ForgeCore.HookPrologue", true)!;
        var registration = runtime.GetType("ForgeEngine.GameBindings.NativeHookRegistration", true)!;
        var definitionMethod = registration.GetMethod("Definition", Any, new[] { typeof(string), typeof(Type) })!;
        var rejection = prologue!.GetMethod("Rejection", Any)!;
        int overwriteLength = (int)prologue.GetField("OverwriteLength", Any)!.GetRawConstantValue()!;
        int snapshotLength = (int)prologue.GetField("SnapshotLength", Any)!.GetRawConstantValue()!;
        var hooksType = runtime.GetType("ForgeEnemy.Native.EnemyNativeHooks", true)!;
        var declared = ((System.Collections.IEnumerable)hooksType.GetProperty("Types", Any)!.GetValue(null)!).Cast<Type>().ToHashSet();
        var pending = (System.Collections.IDictionary)hooksType.GetProperty("Pending", Any)!.GetValue(null)!;

        var patchClasses = runtime.GetTypes().Where(t => t.Namespace is { } ns && (ns == "ForgeEnemy" || ns.StartsWith("ForgeEnemy.", StringComparison.Ordinal))
            && t.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        if (patchClasses.Length == 0) throw new InvalidOperationException("No Enemy Harmony patch class found in ForgeEngine.dll.");
        foreach (var type in declared.Where(t => !patchClasses.Contains(t)))
            throw new InvalidOperationException("EnemyNativeHooks.Types lists " + type.Name + ", which carries no HarmonyPatch.");

        var interop = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        ModuleDefinition Interop(string file)
        {
            if (!interop.TryGetValue(file, out var module))
            {
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(Path.Combine(bep, "core"));
                resolver.AddSearchDirectory(Path.Combine(bep, "interop"));
                interop[file] = module = ModuleDefinition.ReadModule(Path.Combine(bep, "interop", file), new ReaderParameters { AssemblyResolver = resolver });
            }
            return module;
        }

        using var image = new PEReader(File.OpenRead(gamePath));
        var sections = image.PEHeaders.SectionHeaders;
        var rows = new JsonArray();
        var refusals = new JsonArray();
        int checkedTargets = 0, passes = 0;
        foreach (var patch in patchClasses)
        {
            var row = new JsonObject { ["hook"] = patch.Name, ["declared"] = declared.Contains(patch) };
            if (pending.Contains(patch)) row["pending"] = (string)pending[patch]!;
            MethodInfo target;
            object definition;
            try { definition = definitionMethod.Invoke(null, new object[] { Writer, patch })!; }
            catch (TargetInvocationException error) when (error.InnerException is NotSupportedException or ArgumentException)
            {
                row["resolution"] = error.InnerException!.Message;
                rows.Add(row);
                continue;
            }
            target = (MethodInfo)definition.GetType().GetProperty("Target")!.GetValue(definition)!;
            var kinds = new JsonArray();
            foreach (var kind in new[] { "Prefix", "Postfix", "Finalizer" })
                if (definition.GetType().GetProperty(kind)!.GetValue(definition) is MethodInfo body)
                    kinds.Add(JsonValue.Create(kind.ToLowerInvariant() + ":" + body.GetCustomAttributesData()
                        .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority").Select(a => a.ConstructorArguments[0].Value).FirstOrDefault() ?? "400"));
            var cecil = (MethodDefinition)Interop(target.Module.Name).LookupToken(target.MetadataToken);
            string? rva = dump.Rva(cecil);
            row["assembly"] = target.Module.Name;
            row["type"] = cecil.DeclaringType.FullName;
            row["name"] = cecil.Name;
            row["signature"] = cecil.FullName;
            row["isVirtual"] = cecil.IsVirtual;
            row["isStatic"] = cecil.IsStatic;
            row["rva"] = rva;
            row["callbacks"] = kinds;
            if (rva == null) { row["admission"] = "rva-unavailable"; rows.Add(row); continue; }
            ulong address = Convert.ToUInt64(rva[2..], 16);
            var section = sections.FirstOrDefault(s => address >= (ulong)s.VirtualAddress && address + (ulong)snapshotLength <= (ulong)s.VirtualAddress + (ulong)Math.Min(s.VirtualSize, s.SizeOfRawData));
            if (section.Name == null) { row["admission"] = "no-complete-prefix"; rows.Add(row); continue; }
            var bytes = image.GetSectionData(section.VirtualAddress).GetContent((int)(address - (ulong)section.VirtualAddress), snapshotLength).ToArray();
            string? verdict = (string?)rejection.Invoke(null, new object[] { bytes });
            row["first32Bytes"] = Convert.ToHexString(bytes).ToLowerInvariant();
            row["admission"] = verdict ?? "pass";
            if (declared.Contains(patch) || pending.Contains(patch))
            {
                checkedTargets++;
                if (verdict == null) passes++;
                else refusals.Add(new JsonObject { ["hook"] = patch.Name, ["rva"] = rva, ["first32Bytes"] = row["first32Bytes"]!.GetValue<string>(), ["code"] = verdict });
            }
            rows.Add(row);
        }

        // Entries the package deliberately does not hook, kept with their admission verdict so the choice stays checkable: the
        // pouncer's `ActivateState(Vector3)` stores the spit-out point and tail-calls the shared `ChangeToThisState`, which is hooked at
        // that call boundary instead (EnemySpitOutHook) because this entry's own first bytes do not pass the byte policy.
        var notHooked = new JsonArray();
        foreach (var (typeName, methodName) in new[] { ("Enemies.EB_SpitOut_Pouncer", "ActivateState") })
        {
            var method = Interop("Modules-ASM.dll").GetType(typeName).Methods.Single(m => m.Name == methodName);
            string? entryRva = dump.Rva(method);
            var entry = new JsonObject { ["type"] = method.DeclaringType.FullName, ["name"] = method.Name, ["signature"] = method.FullName, ["rva"] = entryRva };
            if (entryRva != null)
            {
                ulong entryAddress = Convert.ToUInt64(entryRva[2..], 16);
                var entrySection = sections.First(s => entryAddress >= (ulong)s.VirtualAddress && entryAddress + (ulong)snapshotLength <= (ulong)s.VirtualAddress + (ulong)Math.Min(s.VirtualSize, s.SizeOfRawData));
                var entryBytes = image.GetSectionData(entrySection.VirtualAddress).GetContent((int)(entryAddress - (ulong)entrySection.VirtualAddress), snapshotLength).ToArray();
                entry["first32Bytes"] = Convert.ToHexString(entryBytes).ToLowerInvariant();
                entry["admission"] = (string?)rejection.Invoke(null, new object[] { entryBytes }) ?? "pass";
            }
            notHooked.Add(entry);
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = ForgeCore.FormatVersion.Current,
            ["purpose"] = "forge-enemy-native-hook-spec",
            ["verification"] = "metadata-and-offline-image-byte-policy-only",
            ["gameVerified"] = false,
            ["gameVerifiedNote"] = "Static evidence only: interop metadata from the Forge-MapEditor-QA profile, dump.cs and GameAssembly.dll of the same build. No GTFO hook was installed or executed; a row names the target Runtime's declaration reader resolves for a patch class and what the pinned image holds at its entry, never that the callback runs or changes anything in a running game.",
            ["buildId"] = ExpectedBuild,
            ["gameAssemblySha256"] = ExpectedGameSha,
            ["dumpSha256"] = ExpectedDumpSha,
            ["rowFields"] = new JsonObject
            {
                ["hook"] = "the Enemy patch class of ForgeEngine.dll",
                ["declared"] = "true when EnemyNativeHooks.Types hands the class to Runtime; false for a class held back (see pending)",
                ["pending"] = "why a class is held back: the group waits for an interface Runtime has not published",
                ["resolution"] = "present only when Runtime's declaration reader refused the class: its native-hook-* reason",
                ["callbacks"] = "the callbacks the class declares with their Harmony priority",
                ["rva"] = "the entry RVA dump.cs declares for the resolved interop method",
                ["first32Bytes"] = "the 32 bytes of the pinned GameAssembly.dll at that RVA",
                ["admission"] = "pass, or the byte-policy refusal code Runtime gives those bytes",
                ["notHookedEntries"] = "entries the package does not hook although a hook would read naturally there, with the verdict that keeps it at a call boundary"
            },
            ["hooks"] = rows,
            ["notHookedEntries"] = notHooked,
            ["nativeAdmissionBytePolicy"] = new JsonObject
            {
                ["verification"] = "offline-pinned-image-byte-policy-only",
                ["gameVerified"] = false,
                ["physicalInstallationVerified"] = false,
                ["policySource"] = PolicySource,
                ["policySourceSha256"] = SourceSha(PolicySource).ToLowerInvariant(),
                ["overwriteLength"] = overwriteLength,
                ["overwriteLengthSource"] = LengthSource,
                ["overwriteLengthSourceSha256"] = SourceSha(LengthSource).ToLowerInvariant(),
                ["checkedTargets"] = checkedTargets,
                ["bytePolicyPasses"] = passes,
                ["refusals"] = refusals,
                ["meaning"] = "Byte-policy passes do not establish live native entry, backend ownership, installation or gameplay."
            }
        };
        File.WriteAllText(outputPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
        foreach (var module in interop.Values) module.Dispose();
        Console.WriteLine("Regenerated hook metadata: " + outputPath + " rows=" + rows.Count + " checked=" + checkedTargets + " passes=" + passes + " refusals=" + refusals.Count);
        return 0;
    }
}
