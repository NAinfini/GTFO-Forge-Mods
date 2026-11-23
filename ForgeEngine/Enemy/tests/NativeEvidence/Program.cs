using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeTestInputs;
using Mono.Cecil;

internal static class NativeEvidenceSuite
{
    internal static int Run(string[] args)
    {

        if (args.Length > 0 && args[0] == "--generate") return GenerateSpec.Run(args[1..]);
        if (args.Length != 5 && args.Length != 6)
        {
            Console.Error.WriteLine("Usage: NativeEvidence <BepInEx> <GTFO game root> <ForgeEngine.dll> <frozen spec.json> <report.json> [damage window.json]");
            Console.Error.WriteLine("       NativeEvidence --generate <BepInEx> <ForgeEngine.dll> <frozen spec.json> <output.json>");
            return 2;
        }
        var checks = new List<EvidenceCheck>();
        void Check(string id, bool passed, string detail) => checks.Add(new(id, passed, detail));
        string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); }
        IEnumerable<TypeDefinition> Walk(TypeDefinition type) => ForgeIl.Walk(type);
        void Shape(JsonElement element, string[] required, params string[] optional)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Specification entries must be objects.");
            var names = element.EnumerateObject().Select(p => p.Name).ToArray();
            var unknown = names.Except(required).Except(optional).ToArray();
            var missing = required.Except(names).ToArray();
            if (unknown.Length > 0 || missing.Length > 0)
                throw new InvalidDataException($"Specification shape mismatch: unknown [{string.Join(",", unknown)}], missing [{string.Join(",", missing)}].");
        }
        string[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();
        static string Open(string fullName) => ForgeIl.Open(fullName);
        static bool Resolves(JsonElement current, string[] segments)
        {
            if (segments.Length == 0) return true;
            string segment = segments[0];
            bool each = segment.EndsWith("[*]", StringComparison.Ordinal);
            if (each) segment = segment[..^3];
            int index = -1;
            if (!each && segment.EndsWith("]", StringComparison.Ordinal))
            {
                int open = segment.LastIndexOf('[');
                if (open <= 0 || !int.TryParse(segment[(open + 1)..^1], out index)) return false;
                segment = segment[..open];
            }
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return false;
            if (index >= 0) return next.ValueKind == JsonValueKind.Array && index < next.GetArrayLength() && Resolves(next[index], segments[1..]);
            if (!each) return Resolves(next, segments[1..]);
            return next.ValueKind == JsonValueKind.Array && next.GetArrayLength() > 0 && next.EnumerateArray().All(item => Resolves(item, segments[1..]));
        }

        var areas = new HashSet<string>(StringComparer.Ordinal) { "identity", "spawn-requirements", "health", "damage-limbs-death", "ai-perception",
    "movement-space", "abilities", "birthing-scout", "attacks-projectiles", "appearance-animation", "cleanup-replication" };
        // The packet layouts the damage window evidence cites, in declaration order. The offsets themselves come from
        // the Il2CppDumper map, so what is checked here is that each packet still declares exactly these fields.
        var expectedPacketFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["pBulletDamageData"] = new[] { "LowResVector3 localPosition", "LowResVector3_Normalized direction", "pAgent source", "UFloat16 damage", "UFloat16 staggerMulti", "UFloat16 precisionMulti", "byte limbID", "bool allowDirectionalBonus", "uint gearCategoryId" },
            ["pFullDamageData"] = new[] { "LowResVector3 localPosition", "LowResVector3_Normalized direction", "pAgent source", "UFloat16 damage", "UFloat16 staggerMulti", "UFloat16 precisionMulti", "UFloat16 backstabberMulti", "UFloat16 sleeperMulti", "bool skipLimbDestruction", "byte limbID", "byte damageNoiseLevel", "uint gearCategoryId" },
            ["pExplosionDamageData"] = new[] { "LowResVector3 localPosition", "LowResVector3 force", "UFloat16 damage", "byte limbID", "uint gearCategoryId" },
            ["pSmallDamageData"] = new[] { "UFloat16 damage", "pAgent source" },
            ["pSetHealthData"] = new[] { "SFloat16 health" },
            ["pAddHealthData"] = new[] { "SFloat16 health", "pAgent source" },
            ["pMiniDamageData"] = new[] { "UFloat16 damage" },
            ["pMediumDamageData"] = new[] { "UFloat16 damage", "pAgent source", "LowResVector3 localPosition", "byte limbID" },
        };
        var assemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        var dataFiles = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
        var distribution = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string key) => distribution[key] = distribution.GetValueOrDefault(key) + 1;
        string forgeHash = Hash(args[2]);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(args[3]));
            var spec = document.RootElement;
            Shape(spec, new[] { "schemaVersion", "buildId", "gameAssemblySha256", "metadataSha256", "verification", "gameExecuted", "assemblies",
        "dataEvidenceFiles", "methods", "enumConstants", "absentDeclaredMethods" });
            if (spec.GetProperty("schemaVersion").GetString() != ForgeCore.FormatVersion.Current || spec.GetProperty("methods").GetArrayLength() == 0
                || spec.GetProperty("gameExecuted").GetBoolean())
                throw new InvalidDataException("Unsupported, empty or game-executed frozen evidence specification.");
            NativeIdentity.CheckGame(spec, args[1], null, Check);
            NativeIdentity.CheckDetection(args[0], Check);
            var nativePath = Path.Combine(args[1], "GameAssembly.dll");
            var nativeHash = Hash(nativePath);
            foreach (var expected in spec.GetProperty("assemblies").EnumerateArray())
            {
                Shape(expected, new[] { "file", "signatureSha256" });
                assemblies.Add(expected.GetProperty("file").GetString()!, NativeIdentity.OpenInterop(expected, args[0], Check));
            }

            // Offline data files that give a native getter its asset-level values (never runtime readback).
            string specDirectory = Path.GetDirectoryName(Path.GetFullPath(args[3]))!;
            foreach (var file in spec.GetProperty("dataEvidenceFiles").EnumerateArray())
            {
                Shape(file, new[] { "path", "sha256" });
                string relative = file.GetProperty("path").GetString()!;
                if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
                    throw new InvalidDataException("Data evidence paths must stay under the specification directory.");
                string full = Path.Combine(specDirectory, relative);
                bool exists = File.Exists(full);
                Check("data-file." + relative, exists && Hash(full) == file.GetProperty("sha256").GetString(), exists ? Hash(full) : "missing");
                if (exists) dataFiles.Add(relative, JsonDocument.Parse(File.ReadAllBytes(full)));
            }

            // What the shipped Enemy plugin actually hooks and calls, read from its IL rather than asserted. The
            // specification generator reads the same IL through the same reader, so a frozen entry and a checked entry
            // cannot drift apart in how a caller or a patch kind is spelled.
            var gameScopes = assemblies.Keys.ToHashSet(StringComparer.Ordinal);
            var usage = ForgeIl.Read(args[2], gameScopes);
            var ilHooks = usage.Hooks;
            var ilCalls = usage.Calls;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var frozen = new HashSet<string>(StringComparer.Ordinal);
            var staticNative = new HashSet<string>(StringComparer.Ordinal);
            var hookOwners = new Dictionary<(string, string), List<string>>();
            foreach (var expected in spec.GetProperty("methods").EnumerateArray())
            {
                Shape(expected, new[] { "id", "area", "assembly", "type", "signature", "isStatic", "isVirtual", "isPublic",
            "evidenceLevel", "nativeCallPhase", "forgeHooks", "forgeCallers", "dataEvidence" }, "nativeRva");
                string id = expected.GetProperty("id").GetString()!;
                string area = expected.GetProperty("area").GetString()!;
                if (!ids.Add(id) || !areas.Contains(area)) throw new InvalidDataException("Duplicate method id or unknown area: " + id);
                string signature = expected.GetProperty("signature").GetString()!;
                string typeName = expected.GetProperty("type").GetString()!;
                if (!frozen.Add(signature)) throw new InvalidDataException("Duplicate frozen signature: " + signature);
                var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
                var type = assembly.MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == Open(typeName));
                var matches = type?.Methods.Where(m => ForgeIl.SignatureMatches(m, signature)).ToArray() ?? Array.Empty<MethodDefinition>();
                bool passed = matches.Length == 1 && matches[0].IsStatic == expected.GetProperty("isStatic").GetBoolean()
                    && matches[0].IsVirtual == expected.GetProperty("isVirtual").GetBoolean()
                    && matches[0].IsPublic == expected.GetProperty("isPublic").GetBoolean();
                Check(id, passed, signature);

                string level = expected.GetProperty("evidenceLevel").GetString()!;
                bool hasRva = expected.TryGetProperty("nativeRva", out var rva);
                bool reviewed = NativeHealth.ReviewedRvas.TryGetValue(signature, out int reviewedRva);
                bool levelHolds = level switch
                {
                    "static-native" => reviewed && hasRva && rva.GetString() == "0x" + reviewedRva.ToString("X"),
                    "metadata" => !reviewed && !hasRva,
                    _ => false,
                };
                Check("evidence-level." + id, levelHolds, level + "; static-native is limited to bodies NativeHealth decodes at a reviewed RVA.");
                if (level == "static-native" && levelHolds) staticNative.Add(signature);
                Count("evidence:" + level);

                Check("call-phase." + id, expected.GetProperty("nativeCallPhase").GetString() == "unknown",
                    "No runtime trace exists for this build, so the phase in which the game calls this method stays unknown.");

                string methodName = Regex.Match(signature, @"::([^(]+)\(").Groups[1].Value;
                var hooks = Strings(expected.GetProperty("forgeHooks"));
                var callers = Strings(expected.GetProperty("forgeCallers"));
                var ilHookKinds = ilHooks.TryGetValue((typeName, methodName), out var hook) ? hook.Kinds.ToArray() : Array.Empty<string>();
                var ilCallers = ilCalls.TryGetValue(signature, out var called) ? called.Callers.ToArray() : Array.Empty<string>();
                Check("forge-use." + id, hooks.SequenceEqual(ilHookKinds) && callers.SequenceEqual(ilCallers),
                    $"IL hooks [{string.Join(",", ilHookKinds)}], IL callers [{string.Join(",", ilCallers)}].");
                if (hooks.Length > 0)
                {
                    if (!hookOwners.TryGetValue((typeName, methodName), out var owners)) hookOwners[(typeName, methodName)] = owners = new List<string>();
                    owners.Add(id);
                }
                Count(hooks.Length > 0 ? "use:forge-hook" : callers.Length > 0 ? "use:forge-call" : "use:inventory");

                int index = 0;
                foreach (var data in expected.GetProperty("dataEvidence").EnumerateArray())
                {
                    Shape(data, new[] { "file", "pointer" });
                    string file = data.GetProperty("file").GetString()!;
                    string pointer = data.GetProperty("pointer").GetString()!;
                    Check($"data-evidence.{id}.{index++}", dataFiles.TryGetValue(file, out var dataDocument)
                        && Resolves(dataDocument.RootElement, pointer.Split('.')), file + "#" + pointer);
                }
            }
            var unmatchedHooks = ilHooks.Keys.Where(k => !hookOwners.TryGetValue(k, out var owners) || owners.Count != 1).Select(k => k.Type + "::" + k.Name).ToArray();
            Check("forge-use.hook-coverage", unmatchedHooks.Length == 0, "Hooks without exactly one frozen owner: [" + string.Join(",", unmatchedHooks) + "]");
            var unfrozenCalls = ilCalls.Keys.Where(s => !frozen.Contains(s)).ToArray();
            Check("forge-use.call-coverage", unfrozenCalls.Length == 0, "Game APIs called by Forge but not frozen: [" + string.Join(",", unfrozenCalls) + "]");
            var missingStatic = NativeHealth.ReviewedRvas.Keys.Where(s => !staticNative.Contains(s)).ToArray();
            Check("evidence-level.static-native-coverage", missingStatic.Length == 0, "Decoded bodies not frozen as static-native: [" + string.Join(",", missingStatic) + "]");

            // The damage window evidence file is a second frozen input. It is checked here because its assertions are
            // about the same build: the RVA lock, the declared methods and their Il2Cpp parameter names, the frozen
            // call edges (checked by re-decoding each reported site) and the packet field offsets it cites.
            if (args.Length == 6)
            {
                using var windowDocument = JsonDocument.Parse(File.ReadAllText(args[5]));
                var window = windowDocument.RootElement;
                Shape(window, new[] { "schemaVersion", "evidenceFile", "area", "buildId", "gameRevision", "gameAssemblySha256",
            "gameExecuted", "verification", "nativeRvaLock", "extraction", "damageMethods", "packetTypes",
            "reviewedEntries", "callEdges", "callSiteWindows", "enumValues", "conclusions" });
                Check("damage-window.schema", window.GetProperty("schemaVersion").GetString() == ForgeCore.FormatVersion.Current
                    && window.GetProperty("area").GetString() == "damage-window" && !window.GetProperty("gameExecuted").GetBoolean(),
                    "Damage window evidence must be schemaVersion 1, area damage-window, and must not claim game execution.");
                Check("damage-window.build", window.GetProperty("buildId").GetString() == spec.GetProperty("buildId").GetString()
                    && window.GetProperty("gameAssemblySha256").GetString() == nativeHash,
                    "Damage window evidence must freeze the same build and native hash as the API specification.");
                var windowMethods = window.GetProperty("damageMethods").EnumerateArray().ToArray();
                Check("damage-window.methods", windowMethods.Length > 0 && windowMethods.Select(m => m.GetProperty("id").GetString()).Distinct().Count() == windowMethods.Length,
                    "Damage window methods must be present and uniquely identified.");
                foreach (var expected in windowMethods)
                {
                    Shape(expected, new[] { "id", "role", "type", "name", "il2cppSignature", "rawDeclaration", "parameters",
                "nativeRva", "virtualSlot", "observations" }, "note");
                    string id = expected.GetProperty("id").GetString()!;
                    string owner = expected.GetProperty("type").GetString()!;
                    string name = expected.GetProperty("name").GetString()!;
                    var matches = assemblies.Values.SelectMany(a => a.MainModule.Types).SelectMany(Walk)
                        .Where(t => t.Name == owner && t.Namespace.Length == 0).SelectMany(t => t.Methods.Where(m => m.Name == name)).ToArray();
                    if (matches.Length != 1)
                    {
                        Check("damage-window.match." + id, false, $"{matches.Length} declarations named {owner}::{name} in the interop assemblies.");
                        continue;
                    }
                    var parameterNames = expected.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
                    Check("damage-window.parameters." + id, parameterNames.SequenceEqual(matches[0].Parameters.Select(p => p.Name)),
                        $"Declared [{string.Join(",", parameterNames)}], interop [{string.Join(",", matches[0].Parameters.Select(p => p.Name))}].");
                    int declaredRva = Convert.ToInt32(expected.GetProperty("nativeRva").GetString()![2..], 16);
                    string signature = expected.GetProperty("il2cppSignature").GetString()!;
                    bool reviewed = NativeHealth.ReviewedRvas.TryGetValue(signature, out int lockedRva);
                    Check("damage-window.rva." + id, !reviewed || lockedRva == declaredRva,
                        reviewed ? $"Reviewed RVA 0x{lockedRva:X} vs declared 0x{declaredRva:X}." : "Not a reviewed body; the RVA is evidence, not a lock.");
                }
                // Field offsets come from the Il2CppDumper map, so they cannot be re-derived here; what is checked is
                // that each packet the evidence cites declares the fields the damage path unpacks, in order.
                foreach (var packet in window.GetProperty("packetTypes").EnumerateArray())
                {
                    Shape(packet, new[] { "type", "fields" });
                    string packetType = packet.GetProperty("type").GetString()!;
                    var fields = packet.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("declaration").GetString()!).ToArray();
                    bool expectedShape = expectedPacketFields.TryGetValue(packetType, out var expectedFields) && fields.SequenceEqual(expectedFields);
                    Check("damage-window.packet." + packetType, expectedShape, $"Declared [{string.Join(",", fields)}].");
                }
                var edgeWindows = window.GetProperty("callSiteWindows").EnumerateArray()
                    .ToDictionary(w => w.GetProperty("site").GetString()!, w => w, StringComparer.Ordinal);
                foreach (var edge in window.GetProperty("callEdges").EnumerateArray())
                {
                    Shape(edge, new[] { "from", "to", "site", "targetRva", "section" });
                    string site = edge.GetProperty("site").GetString()!;
                    string to = edge.GetProperty("to").GetString()!;
                    string target = edge.GetProperty("targetRva").GetString()!;
                    bool known = edgeWindows.TryGetValue(site, out var windowEntry)
                        && windowEntry.GetProperty("targetMethod").GetString() == to;
                    Check("damage-window.edge." + site, known, $"Edge {edge.GetProperty("from").GetString()} -> {to} at {site} must have a frozen instruction window.");
                    // The edge is verified by looking for the decoded call to that exact target inside the frozen
                    // window. Instruction text prints the absolute address zero-padded to 16 hex digits, so the RVA is
                    // rebased onto the image base and padded the same way.
                    long absolute = Convert.ToInt64(target[2..], 16) + Convert.ToInt64(window.GetProperty("extraction").GetProperty("imageBase").GetString()![2..], 16);
                    string call = "call|" + absolute.ToString("X").PadLeft(16, '0') + "h";
                    bool calls = known && windowEntry.GetProperty("instructions").EnumerateArray()
                        .Any(i => i.GetProperty("text").GetString() == call);
                    Check("damage-window.edge-call." + site, calls, $"No decoded call to {target} inside the frozen window for {site}.");
                }
                foreach (var windowEntry in window.GetProperty("callSiteWindows").EnumerateArray())
                {
                    Shape(windowEntry, new[] { "site", "section", "caller", "target", "targetMethod", "instructions" });
                    string site = windowEntry.GetProperty("site").GetString()!;
                    var instructions = windowEntry.GetProperty("instructions").EnumerateArray().ToArray();
                    Check("damage-window.site-window." + site, instructions.Length > 0
                        && windowEntry.GetProperty("caller").GetString()?.Length > 0,
                        "A frozen call site must decode to instructions and be attributed to a caller.");
                }
                Check("damage-window.conclusions", window.GetProperty("conclusions").EnumerateObject().Count() == 4,
                    "The four damage window questions must each have a conclusion.");
                Count("use:damage-window");
                NativeHealth.VerifyDamageWindow(nativePath, nativeHash, Check);
            }

            foreach (var expected in spec.GetProperty("enumConstants").EnumerateArray())
            {
                Shape(expected, new[] { "id", "assembly", "type", "name", "value" });
                var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
                var type = assembly.MainModule.Types.SelectMany(Walk).SingleOrDefault(t => t.FullName == expected.GetProperty("type").GetString());
                var field = type is { IsEnum: true } ? type.Fields.SingleOrDefault(f => f.IsStatic && f.HasConstant && f.Name == expected.GetProperty("name").GetString()) : null;
                Check(expected.GetProperty("id").GetString()!, field != null && Convert.ToInt64(field.Constant) == expected.GetProperty("value").GetInt64(),
                    field == null ? "enum constant missing" : "metadata value " + field.Constant);
                Count("enum-constants");
            }
            foreach (var expected in spec.GetProperty("absentDeclaredMethods").EnumerateArray())
            {
                Shape(expected, new[] { "id", "assembly", "type", "name" });
                var assembly = assemblies[expected.GetProperty("assembly").GetString()!];
                var type = assembly.MainModule.Types.SelectMany(Walk).Single(t => t.FullName == expected.GetProperty("type").GetString());
                Check(expected.GetProperty("id").GetString()!, !type.Methods.Any(m => m.Name == expected.GetProperty("name").GetString()),
                    "No declaration on this type; inherited method existence does not prove an implemented receiver.");
            }
            NativeHealth.Verify(nativePath, nativeHash, Check);
            HookAdmission.Verify(nativePath, args[2], nativeHash, Check);
        }
        catch (Exception error) { Check("audit.error", false, error.GetType().Name + ": " + error.Message); }
        finally
        {
            foreach (var assembly in assemblies.Values) assembly.Dispose();
            foreach (var data in dataFiles.Values) data.Dispose();
        }
        int failed = checks.Count(c => !c.Passed);
        var report = new
        {
            schemaVersion = ForgeCore.FormatVersion.Current,
            verification = "metadata-static-native-and-forge-il",
            gameExecuted = false,
            forgeAssemblySha256 = forgeHash,
            utc = DateTimeOffset.UtcNow,
            distribution,
            passed = checks.Count - failed,
            failed,
            checks
        };
        string reportPath = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks.Where(c => !c.Passed)) Console.Error.WriteLine("FAIL " + check.Id + ": " + check.Detail);
        Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {checks.Count - failed}/{checks.Count} static evidence checks; "
            + string.Join(", ", distribution.Select(d => d.Key + "=" + d.Value)) + "; game execution and multiplayer NOT tested.");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed record EvidenceCheck(string Id, bool Passed, string Detail);
