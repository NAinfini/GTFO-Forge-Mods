using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ForgeEngine.HookEvidence;
using static ForgeEngine.HookEvidence.HookRows;

namespace ForgeMap.Tests.MapNativeLayout;

/// <summary>
/// Rewrites the member rows of `evidence/map-hooks.json` from the compiled plugin and the interop assemblies:
/// one readback row per game member the plugin reads, one write row per non-getter member it calls, and one hook
/// row per Harmony patch class with the target the interop assembly really declares.
///
/// The audit in this project is what decides whether those rows are right, and it compares the whole set both
/// ways, so this is not a second opinion: it is the same reading of the same assemblies, written down. A row that
/// already exists keeps its prose; its RVA is re-read by exact parameter types when the pinned dump is open,
/// otherwise its historical RVA is retained. A member the plugin stopped calling loses its row.
/// </summary>
internal static class EvidenceRegenerator
{
    /// <summary>The compiled plugin a regeneration read: its own bytes and metadata identity.</summary>
    internal sealed record AssemblyFingerprint(string File, string Sha256, string Mvid)
    {
        internal JsonObject Json() => new() { ["file"] = File, ["sha256"] = Sha256, ["mvid"] = Mvid };
    }

    /// <summary>An interop assembly a regeneration read: its generation-independent signature.</summary>
    internal sealed record InteropFingerprint(string File, string SignatureSha256)
    {
        internal JsonObject Json() => new() { ["file"] = File, ["signatureSha256"] = SignatureSha256 };
    }

    /// <summary>Everything the rows are written from: the interop types, the plugin's calls and patch classes,
    /// and the identities of the actual files opened, independent of historical native evidence.</summary>
    internal sealed record Inputs(IReadOnlyList<TypeDefinition> GameTypes, IReadOnlyList<TypeDefinition> NativeTypes,
        IReadOnlyList<Call> Calls, IReadOnlyList<JsonObject> Hooks,
        AssemblyFingerprint CompiledPlugin, IReadOnlyList<InteropFingerprint> InteropAssemblies,
        NativeProvenance? NativeSource = null);

    internal sealed record NativeProvenance(string BuildId, string GameAssemblySha256, string DumpSha256);
    internal static NativeProvenance? NativeSource(JsonObject spec)
    {
        string? build = spec["buildId"]?.ToJsonString(), game = (string?)spec["gameAssemblySha256"], dump = (string?)spec["dumpSha256"];
        return string.IsNullOrWhiteSpace(build) || build == "\"\"" || string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(dump)
            ? null : new(build, game.ToUpperInvariant(), dump.ToUpperInvariant());
    }

    private static AssemblyFingerprint Fingerprint(AssemblyDefinition assembly)
    {
        using var stream = File.OpenRead(assembly.MainModule.FileName);
        return new(Path.GetFileName(assembly.MainModule.FileName), Convert.ToHexString(SHA256.HashData(stream)),
            assembly.MainModule.Mvid.ToString());
    }

    /// <summary>Reads the plugin and the interop assemblies the way the audit does and answers the sets the rows
    /// are written from. The assemblies are the caller's: this only reads them.</summary>
    internal static Inputs Read(AssemblyDefinition engine, AssemblyDefinition framework, IReadOnlyList<AssemblyDefinition> gameAssemblies, JsonDocument spec)
    {
        var gameTypes = GameTypes(gameAssemblies);
        var nativeTypes = ForgeTestInputs.DomainTypes.Native(engine, framework, "Map");
        var calls = Calls(nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Concat(MethodInfoCalls(m, gameTypes))), gameTypes, gameAssemblies);
        var hooks = spec.RootElement.GetProperty("hooks").EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())!.AsObject()).ToArray();
        return new Inputs(gameTypes, nativeTypes, calls, hooks, Fingerprint(engine), gameAssemblies.Select(a => new InteropFingerprint(Path.GetFileName(a.MainModule.FileName), ForgeTestInputs.InteropSignature.Sha256(a))).ToArray(),
            NativeSource(JsonNode.Parse(spec.RootElement.GetRawText())!.AsObject()));
    }

    /// <summary>Resolve a raw interop invocation back to the same generated MethodInfo field and its native
    /// wrapper declaration. An indirect call cannot disappear from evidence just because its out wrapper is broken.</summary>
    internal static IEnumerable<MethodReference> MethodInfoCalls(MethodDefinition caller, IReadOnlyList<TypeDefinition> gameTypes)
    {
        if (!caller.HasBody || !caller.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Any(m => m.DeclaringType.FullName == "Il2CppInterop.Runtime.IL2CPP" && m.Name == "il2cpp_runtime_invoke")) yield break;
        var tokens = caller.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldtoken)
            .Select(i => i.Operand).OfType<TypeReference>().Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var name in caller.Body.Instructions.Select(i => i.Operand).OfType<string>()
            .Where(s => s.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            var fields = gameTypes.Where(t => tokens.Contains(t.FullName)).SelectMany(t => t.Fields)
                .Where(f => f.Name == name && f.IsStatic && f.FieldType.FullName == "System.IntPtr").ToArray();
            if (fields.Length != 1) throw new InvalidOperationException("Unresolved native MethodInfo field: " + name);
            var targets = fields[0].DeclaringType.Methods.Where(m => m.HasBody && m.Name != ".cctor"
                && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.FullName == fields[0].FullName)).ToArray();
            if (targets.Length != 1) throw new InvalidOperationException("Ambiguous native MethodInfo target: " + name);
            yield return targets[0];
        }
    }

    internal static void Run(Inputs inputs, string specPath, string outputPath, string? dumpPath = null)
    {
        var spec = JsonNode.Parse(File.ReadAllText(specPath))!.AsObject();
        var (previous, previousWrites) = Previous(spec, inputs.GameTypes);
        var previousHooks = inputs.Hooks.ToDictionary(h => HookKey((string)h["hook"]!, (string)h["signature"]!), StringComparer.Ordinal);
        // The dump is optional: the hook rows' RVAs are read out of it when the build it was made from is the
        // one the spec froze, and the rows keep the RVAs they already carry when it is not.
        var dump = Dump.Open(dumpPath ?? Environment.GetEnvironmentVariable("FORGE_GTFO_DUMP") ?? "",
            (string?)spec["dumpSha256"] ?? "", message => Console.WriteLine(message));

        var writes = new JsonArray();
        // One row per declaration and not per name: a name the game overloads has one row per form the plugin
        // calls, because the audit compares the whole signature set and a dropped form reads as a call nobody
        // reviewed.
        var writeCalls = inputs.Calls.Where(c => IsWrite(c, ReadSpellings, QueryMembers)).ToArray();
        var writeMembers = writeCalls.Select(c => c.Member).ToHashSet(StringComparer.Ordinal);
        var callSites = inputs.NativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Concat(MethodInfoCalls(m, inputs.GameTypes))
                .Select(r => (Caller: m.FullName, Target: Resolve(inputs.GameTypes, r)?.FullName)))
            .Where(x => x.Target != null).GroupBy(x => x.Target!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Caller).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var forms = writeCalls.GroupBy(c => c.Member, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var call in writeCalls)
        {
            var write = previousWrites.TryGetValue(call.Signature, out var row) ? row.DeepClone().AsObject()
                : WriteRow(call, forms[call.Member], inputs.GameTypes);
            write["callSites"] = new JsonArray(callSites[call.Signature]
                .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            writes.Add(write);
        }
        var unclassified = new List<string>();
        var readbacks = Readbacks(inputs.Calls.Where(c => !writeMembers.Contains(c.Member)), previous, inputs.GameTypes,
            member => GameRead(member) || MapObjectRead(member), unclassified);

        // These fingerprints describe the files opened for this metadata pass, separately from the
        // historical native build/dump and interop capture. A changed interop signature must never silently
        // inherit the historical build's identity just because its member names still resolve.
        var historical = spec["assemblies"]!.AsArray().OfType<JsonObject>().ToArray();
        spec["metadataRegeneration"] = new JsonObject
        {
            ["compiledPlugin"] = inputs.CompiledPlugin.Json(),
            ["interopAssemblies"] = new JsonArray(inputs.InteropAssemblies.Select(a => (JsonNode)a.Json()).ToArray()),
            ["historicalInteropSnapshotMatched"] = historical.Length != 0 && historical.All(previous =>
                inputs.InteropAssemblies.Any(actual => actual.File == (string?)previous["file"]
                    && string.Equals(actual.SignatureSha256, (string?)previous["signatureSha256"], StringComparison.OrdinalIgnoreCase))),
            ["dumpReopenedMatched"] = dump != null,
            ["note"] = "Actual inputs for compiled metadata regeneration only. historicalInteropSnapshotMatched compares only the interop fingerprints already recorded in top-level assemblies; it does not establish game-build identity. Top-level buildId, gameAssemblySha256, dumpSha256 and assemblies remain the historical native evidence source. Retained RVAs and readback prose refer to that source; new targets without matching dump evidence have null RVAs."
        };
        spec["gameVerified"] = false;
        spec["gameVerifiedNote"] = "Static metadata only: metadataRegeneration identifies the actual compiled plugin and interop inputs used to regenerate member rows. The top-level buildId, gameAssemblySha256, dumpSha256 and assemblies identify historical native evidence; matching interop signatures alone do not establish the build, which the top-level game identity checks do. Retained RVAs and readback prose remain historical evidence. Missing RVAs remain unverified. No hook was installed and no game code was executed by this audit; native call timing, gameplay and multiplayer behavior are not verified.";
        // A new observer can share the exact target of an existing hook. Its target-address evidence may be
        // reused only inside the same frozen native build/game/dump identity; metadata fingerprints cannot
        // supply that identity. The hook-specific primary lookup remains unchanged.
        var sameNativeSource = inputs.NativeSource != null && inputs.NativeSource == NativeSource(spec);
        spec["hooks"] = Hooks(inputs, previousHooks, dump, sameNativeSource);
        spec["readbacks"] = readbacks;
        spec["writes"] = writes;
        Write(spec, outputPath);
        if (unclassified.Count != 0)
            Console.WriteLine("game reads the audit does not classify: " + string.Join(" | ", unclassified));
    }

    // The layout audit beside this file reads these two rules under this class's name.
    internal static bool Engine(string member) => HookRows.Engine(member);
    internal static string ParameterKey(ParameterDefinition parameter) => HookRows.ParameterKey(parameter);

    /// <summary>One row per target of each patch class, with the target, the static flag, the patch kind and
    /// that kind's parameter shape read off the compiled plugin and the interop assembly. A row that already
    /// existed keeps its readback prose, which is the reading of the binary this run does not repeat, and its
    /// `rva` when the dump it was read from is the one this run can open.</summary>
    private static JsonArray Hooks(Inputs inputs, IReadOnlyDictionary<string, JsonObject> previous, Dump? dump, bool sameNativeSource)
    {
        var rows = new JsonArray();
        var hooks = inputs.NativeTypes.Where(Patch).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        var declared = DeclaredHookClasses(inputs.NativeTypes);
        if (!declared.ToHashSet(StringComparer.Ordinal).SetEquals(hooks.Select(hook => hook.FullName)))
            throw new InvalidDataException("MapNativeHooks.Declared does not match the compiled Harmony patch classes; patched but undeclared: "
                + string.Join(", ", hooks.Select(hook => hook.FullName).Except(declared)) + "; declared but not patched: "
                + string.Join(", ", declared.Except(hooks.Select(hook => hook.FullName))));
        foreach (var hook in hooks)
        {
            foreach (var target in HookTargets(inputs.GameTypes, hook))
            {
                var type = target.DeclaringType;
                // The primary observation stage does not replace the prefix's independent metadata.
                var prefix = hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix"));
                var observer = hook.Methods.SingleOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyFinalizer")
                    && m.Parameters.Any(p => p.Name == "__runOriginal"));
                var body = observer ?? hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix"))
                    ?? prefix ?? hook.Methods.FirstOrDefault(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyFinalizer"))
                    ?? throw new InvalidOperationException("No Harmony patch method on " + hook.Name);
                string kind = body.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix") ? "postfix"
                    : body.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix") ? "prefix" : "finalizer";
                // A finalizer that observes whether the original ran is the observation stage; its required
                // non-suppressing prefix is emitted separately below. Existing prefix/postfix rows retain their
                // selection. A finalizer-only exception observer is also represented as its actual stage.
                if (kind == "finalizer" && FinalizerObservationError(body) is { } error)
                    throw new InvalidDataException(hook.Name + ": " + error);
                bool originalRunPostfix = kind == "postfix" && body.Parameters.Any(p => p.Name == "__runOriginal");
                if (originalRunPostfix && PostfixObservationError(body) is { } postfixError)
                    throw new InvalidDataException(hook.Name + ": " + postfixError);
                // The guard is the session entry the whole patch reaches the half through. A hook that guards inside
                // a lambda the compiler moved into the patch class's own display class still names the same entry, so
                // the search covers the body and every method under the class.
                // A hook that reaches the session names the guard its whole callback runs inside. A hook that only
                // maintains patch-level state — the damage-kind token a receive entry sets for the accept path to
                // read, and the native extra-information row a line of this model owns — has no guard: what it
                // touches is this package's own table, and it answers nothing while the half it belongs to is not
                // attached. `session: off` is how such a row says so instead of naming a guard it does not call.
                var guard = Guard(body);
                string? session = guard == null ? null : "on";
                if (guard == null && Facts(body) is { } factsGuard) { guard = factsGuard; session = "on"; }
                var arguments = new JsonArray();
                var reads = new JsonArray();
                var writes = new JsonArray();
                foreach (var parameter in body.Parameters)
                {
                    if (parameter.Name == "__instance") continue;
                    // `__state` and `__result` are Harmony's own slots rather than arguments of the patched method,
                    // so they are not part of the parameter list a row declares.
                    if (parameter.Name is "__state" or "__result" || FinalizerInjected(parameter)) continue;
                    arguments.Add(new JsonObject { ["name"] = parameter.Name, ["type"] = parameter.ParameterType.FullName });
                }
                foreach (string read in Loaded(body))
                    reads.Add(read);
                // The write set is the one the audit reads off the compiled body: a parameter the patched method
                // reads back changed. The only parameter a patch can change is a by-ref one, and only where the body
                // stores through it — a `ref` argument the body hands to a helper the audit does not open is not a
                // store the audit sees, so it is not a write this row may claim either. Harmony's own `__state` is not
                // an argument of the patched method at all — a prefix writes it for the postfix to read — so it is a
                // parameter of the patch and never a write of the patch's.
                foreach (var parameter in body.Parameters)
                    if (parameter.ParameterType.IsByReference && parameter.Name != "__state" && Stored(body, parameter))
                        writes.Add(parameter.Name);
                previous.TryGetValue(HookKey(hook.Name, target.FullName), out var known);
                var nativeAddress = known ?? (sameNativeSource ? MatchingNativeTarget(previous.Values, target) : null);
                var instance = body.Parameters.SingleOrDefault(p => p.Name == "__instance");
                if (instance != null && !InstanceCompatible(inputs.GameTypes, target, instance.ParameterType.FullName))
                    throw new InvalidDataException(hook.Name + " has an incompatible __instance for " + target.FullName);
                var row = new JsonObject
                {
                    ["hook"] = hook.Name,
                    ["installationOwner"] = "ForgeEngine",
                    ["declaration"] = "MapNativeHooks.Declared",
                    ["assembly"] = target.Module.Assembly.Name.Name + ".dll",
                    ["type"] = target.DeclaringType.FullName,
                    ["name"] = target.Name,
                    ["signature"] = target.FullName,
                    ["isVirtual"] = target.IsVirtual,
                    ["isStatic"] = target.IsStatic,
                    ["rva"] = dump != null ? dump.Rva(target) : nativeAddress?["rva"]?.DeepClone(),
                    ["patch"] = kind,
                    ["session"] = session ?? "off",
                    [kind] = new JsonObject
                    {
                        ["instanceParameter"] = instance?.Name,
                        ["instanceType"] = instance?.ParameterType.FullName,
                        ["arguments"] = arguments,
                        ["reads"] = reads,
                        ["writes"] = writes,
                        ["guard"] = guard
                    }
                };
                if (kind == "finalizer" || originalRunPostfix)
                {
                    row[kind]!["returnType"] = body.ReturnType.FullName;
                    row[kind]!["injectedArguments"] = new JsonArray(body.Parameters.Where(p => FinalizerInjected(p) || p.Name == "__state")
                        .Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.ParameterType.FullName }).ToArray());
                }
                if (observer != null || (originalRunPostfix && prefix != null)) row["observationPrefix"] = ObservationSignature(prefix!);
                if (kind != "prefix" && prefix != null)
                    row["prefix"] = PrefixShape(prefix);
                // The `readback` prose names the native member the callback really reads. Deriving that from the
                // binary is what this run does not do — the reading runs through the half's own observers — so a row
                // that already carries a reading keeps it and a hook nobody has read yet carries no field at all. A
                // sentence invented here would be prose the evidence does not support, which is worse than silence.
                if (known?["readback"]?.DeepClone() is { } reading) row["readback"] = reading;
                // The shared-address vocabulary is a reviewed reading of the dump, not of the compiled plugin: a hook
                // whose target address holds more than one declaration has to name the others and say why the patch is
                // right for each. This run cannot re-derive that list, so it carries the row's own fields over rather
                // than dropping a reviewed fact on every regeneration. `MapNativeEvidence` is what checks them.
                foreach (string shared in new[] { "rvaShared", "dumpEntriesAtRva", "sharedWith", "sharedReason" })
                    if (nativeAddress?[shared]?.DeepClone() is { } value) row[shared] = value;
                rows.Add(row);
            }
        }
        return rows;
    }

    private static JsonObject? MatchingNativeTarget(IEnumerable<JsonObject> rows, MethodDefinition target)
    {
        var candidates = rows.Where(row => (string?)row["assembly"] == target.Module.Assembly.Name.Name + ".dll"
            && (string?)row["type"] == target.DeclaringType.FullName && (string?)row["signature"] == target.FullName
            && (bool?)row["isStatic"] == target.IsStatic && (bool?)row["isVirtual"] == target.IsVirtual
            && row["rva"] is JsonValue).ToArray();
        if (candidates.Length == 0) return null;
        // Conflicting retained address/shared-body evidence is not resolved by guessing a preferred hook.
        return candidates.Skip(1).All(row => new[] { "rva", "rvaShared", "dumpEntriesAtRva", "sharedWith", "sharedReason" }
            .All(key => JsonNode.DeepEquals(row[key], candidates[0][key]))) ? candidates[0] : null;
    }

    // These are Harmony-owned observations, never native target arguments. Finalizer support is deliberately
    // limited to a void observer: it cannot replace/suppress an exception through its return or ref slots.
    internal static bool FinalizerInjected(ParameterDefinition parameter)
        => parameter.Name is "__runOriginal" or "__exception";

    // A __runOriginal postfix also depends on a prefix in recovered Harmony 2.10.1. Declare that
    // dependency and its exact bool injection instead of silently treating it as a native argument.
    internal static string? PostfixObservationError(MethodDefinition body)
    {
        if (!body.IsStatic || body.ReturnType.FullName != "System.Void" || !FrameworkType(body.ReturnType))
            return "postfix observation must have a static void signature";
        var run = body.Parameters.Where(p => p.Name == "__runOriginal").ToArray();
        if (run.Length != 1 || run[0].ParameterType.FullName != "System.Boolean" || !FrameworkType(run[0].ParameterType))
            return "__runOriginal must be a read-only System.Boolean injection";
        if (body.Parameters.Any(p => p.ParameterType.IsByReference || p.Name == "__exception"))
            return "postfix observation cannot write parameters or request a finalizer exception slot";
        if (body.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return "postfix observation has duplicate parameter names";
        if (body.Parameters.Any(p => p.Name.StartsWith("__", StringComparison.Ordinal)
            && p.Name is not ("__instance" or "__runOriginal" or "__state" or "__result")))
            return "unsupported postfix observation injection";
        var prefixes = body.DeclaringType.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix")).ToArray();
        // The prefix exists to capture the instance the postfix reads. A postfix on a static native call reads no
        // instance, so it carries no capture prefix; any prefix it does carry is still held to the full rule.
        if (prefixes.Length == 0 && body.Parameters.All(p => p.Name != "__instance")) return null;
        if (prefixes.Length != 1 || !prefixes[0].IsStatic || prefixes[0].ReturnType.FullName != "System.Void"
            || !FrameworkType(prefixes[0].ReturnType))
            return "__runOriginal postfix requires its own single non-suppressing void prefix signature";
        if (prefixes[0].Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return "postfix observation prefix has duplicate parameter names";
        var capturedInstance = prefixes[0].Parameters.SingleOrDefault(p => p.Name == "__instance");
        var observedInstance = body.Parameters.SingleOrDefault(p => p.Name == "__instance");
        if (capturedInstance == null || observedInstance == null || capturedInstance.IsOut || observedInstance.IsOut
            || capturedInstance.ParameterType.IsByReference
            || !SameType(capturedInstance.ParameterType, observedInstance.ParameterType))
            return "observation prefix and postfix must read the same instance type";
        return null;
    }

    internal static string? FinalizerObservationError(MethodDefinition body)
    {
        if (!body.IsStatic || body.ReturnType.FullName != "System.Void" || !FrameworkType(body.ReturnType))
            return "finalizer observation must be static void";
        if (body.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            return "finalizer has duplicate parameter names";
        foreach (var parameter in body.Parameters)
        {
            if (parameter.ParameterType.IsByReference)
                return "finalizer observation cannot write " + parameter.Name;
            if (FinalizerInjected(parameter))
            {
                string expected = parameter.Name == "__runOriginal" ? "System.Boolean" : "System.Exception";
                if (parameter.ParameterType.FullName != expected || !FrameworkType(parameter.ParameterType))
                    return parameter.Name + " must be a read-only " + expected + " injection";
            }
            else if (parameter.Name.StartsWith("__", StringComparison.Ordinal)
                && parameter.Name is not ("__instance" or "__state" or "__result"))
                return "unsupported finalizer injection " + parameter.Name;
        }
        if (body.Parameters.Any(p => p.Name == "__runOriginal"))
        {
            var patches = body.DeclaringType.Methods.Where(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Namespace == "HarmonyLib" && a.AttributeType.Name is
                    "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
            var prefixes = patches.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix")).ToArray();
            // The restored Harmony 2.10.1 emitter creates __runOriginal only when a prefix exists. Capture
            // per-call state with that prefix; an unrelated plugin must never be required to make this valid.
            if (patches.Length != 2 || prefixes.Length != 1 || !prefixes[0].IsStatic
                || prefixes[0].ReturnType.FullName != "System.Void" || !FrameworkType(prefixes[0].ReturnType))
                return "__runOriginal observation requires its own single non-suppressing void prefix signature";
            var prefix = prefixes[0];
            if (prefix.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                return "observation prefix has duplicate parameter names";
            var capturedInstance = prefix.Parameters.SingleOrDefault(p => p.Name == "__instance");
            var observedInstance = body.Parameters.SingleOrDefault(p => p.Name == "__instance");
            if (capturedInstance == null || observedInstance == null
                || !SameType(capturedInstance.ParameterType, observedInstance.ParameterType))
                return "observation prefix and finalizer must read the same instance type";
            if (prefix.Parameters.Any(p => p.Name is not ("__instance" or "__state")))
                return "observation prefix may only read __instance and capture __state";
            var captured = prefix.Parameters.SingleOrDefault(p => p.Name == "__state");
            var observed = body.Parameters.SingleOrDefault(p => p.Name == "__state");
            if (captured == null || !captured.IsOut || captured.ParameterType is not ByReferenceType reference
                || observed == null || observed.ParameterType.IsByReference || !SameType(reference.ElementType, observed.ParameterType))
                return "observation prefix and finalizer must share one typed per-call state";
        }
        return null;
    }

    // Preserve every actual parameter and the return shape of the meaningful state-capture prefix. It is
    // independently audited next to the finalizer, so a bool takeover cannot hide behind an observer row.
    internal static JsonObject ObservationSignature(MethodDefinition method) => new()
    {
        ["signature"] = method.FullName,
        ["returnType"] = method.ReturnType.FullName,
        ["parameters"] = new JsonArray(method.Parameters.Select(p => (JsonNode)new JsonObject
        { ["name"] = p.Name, ["type"] = p.ParameterType.FullName, ["isOut"] = p.IsOut }).ToArray()),
        ["priority"] = method.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority")
            ?.ConstructorArguments.FirstOrDefault().Value is int priority ? JsonValue.Create(priority) : null
    };

    internal static JsonObject PrefixShape(MethodDefinition method)
    {
        var shape = ObservationSignature(method);
        var instance = method.Parameters.SingleOrDefault(p => p.Name == "__instance");
        shape["instanceParameter"] = instance?.Name;
        shape["instanceType"] = instance?.ParameterType.FullName;
        shape["arguments"] = new JsonArray(method.Parameters.Where(p => p.Name is not ("__instance" or "__state" or "__result")
                && !FinalizerInjected(p)).Select(p => (JsonNode)new JsonObject
                { ["name"] = p.Name, ["type"] = p.ParameterType.FullName }).ToArray());
        shape["reads"] = new JsonArray(Loaded(method).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        shape["writes"] = new JsonArray(method.Parameters.Where(p => p.ParameterType.IsByReference && p.Name != "__state" && Stored(method, p))
            .Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray());
        shape["guard"] = Guard(method) ?? Facts(method);
        return shape;
    }

    internal static string HookKey(string hook, string signature) => hook + " -> " + signature;

    internal static bool InstanceCompatible(IReadOnlyList<TypeDefinition> gameTypes, MethodDefinition target, string instanceType)
    {
        if (target.IsStatic) return false;
        for (TypeReference? current = target.DeclaringType; current != null;)
        {
            if (current.FullName == instanceType) return true;
            current = (gameTypes.FirstOrDefault(t => t.FullName == current.FullName) ?? current.Resolve())?.BaseType;
        }
        return false;
    }

    /// <summary>Resolve every target without loading the plugin or executing game code. For TargetMethods,
    /// interpret literal MethodBase/Type arrays and Type.GetMethod(string[, Type[]]) lookups. Any control flow,
    /// unknown call, ambiguous lookup, duplicate or unfilled slot is rejected, never skipped.</summary>
    internal static MethodDefinition[] HookTargets(IReadOnlyList<TypeDefinition> gameTypes, TypeDefinition hook)
    {
        var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        if (patch.ConstructorArguments.Count != 0)
        {
            var ownerName = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single().FullName;
            var name = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            var owner = gameTypes.SingleOrDefault(t => t.FullName == ownerName);
            var target = owner == null ? null : Target(gameTypes, owner, patch, name);
            return new[] { target ?? throw new InvalidDataException(hook.Name + " has an unresolved target " + ownerName + "::" + name) };
        }
        var factory = hook.Methods.SingleOrDefault(m => m.Name == "TargetMethods")
            ?? throw new InvalidDataException(hook.Name + " has no TargetMethods");
        if (!factory.IsStatic || factory.Parameters.Count != 0 || !factory.HasBody
            || factory.ReturnType.FullName is not ("System.Reflection.MethodBase[]" or "System.Collections.Generic.IEnumerable`1<System.Reflection.MethodBase>"))
            throw new InvalidDataException(hook.Name + " has an unsupported TargetMethods signature");
        var stack = new Stack<object>();
        T Pop<T>() => stack.TryPop(out var value) && value is T typed ? typed
            : throw new InvalidDataException(hook.Name + " has an invalid TargetMethods stack value");
        foreach (var instruction in factory.Body.Instructions)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Nop: break;
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                    stack.Push((int)instruction.OpCode.Code - (int)Code.Ldc_I4_0); break;
                case Code.Ldc_I4: case Code.Ldc_I4_S: stack.Push(Convert.ToInt32(instruction.Operand)); break;
                case Code.Newarr when instruction.Operand is TypeReference element
                    && FrameworkType(element) && element.FullName is "System.Reflection.MethodBase" or "System.Type":
                    int count = Pop<int>();
                    if (count < 0 || count > factory.Body.Instructions.Count
                        || (count == 0 && element.FullName == "System.Reflection.MethodBase"))
                        throw new InvalidDataException(hook.Name + " has an invalid literal array count");
                    stack.Push(element.FullName == "System.Type" ? new ReflectedType[count] : (object)new MethodDefinition[count]); break;
                case Code.Dup:
                    if (!stack.TryPeek(out var copy)) throw new InvalidDataException(hook.Name + " has an empty TargetMethods stack");
                    stack.Push(copy); break;
                case Code.Ldtoken when instruction.Operand is TypeReference type: stack.Push(type); break;
                case Code.Ldstr: stack.Push((string)instruction.Operand); break;
                case Code.Call when instruction.Operand is MethodReference call && !call.HasThis && FrameworkType(call.DeclaringType)
                    && call.FullName == "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)":
                    stack.Push(new ReflectedType(Pop<TypeReference>())); break;
                case Code.Call or Code.Callvirt when instruction.Operand is MethodReference lookup && lookup.HasThis && FrameworkType(lookup.DeclaringType)
                    && lookup.FullName is "System.Reflection.MethodInfo System.Type::GetMethod(System.String)"
                        or "System.Reflection.MethodInfo System.Type::GetMethod(System.String,System.Type[])":
                    var parameters = lookup.Parameters.Count == 2 ? Pop<ReflectedType[]>() : null;
                    if (parameters != null && parameters.Any(p => p == null))
                        throw new InvalidDataException(hook.Name + " has an incomplete parameter type array");
                    var methodName = Pop<string>();
                    var reflectedType = Pop<ReflectedType>().Type;
                    var reflectedOwner = gameTypes.SingleOrDefault(t => SameType(t, reflectedType))
                        ?? throw new InvalidDataException(hook.Name + " targets an unknown type " + reflectedType.FullName);
                    var candidates = Chain(gameTypes, reflectedOwner).SelectMany(t => t.Methods.Where(m => m.IsPublic && m.Name == methodName))
                        .Where(m => parameters == null || m.Parameters.Count == parameters.Length
                            && m.Parameters.Zip(parameters).All(p => SameType(p.First.ParameterType, p.Second.Type)))
                        // Keep the most-derived declaration for an inherited signature, but never hide
                        // duplicate declarations on that owner: those are an ambiguous reflection lookup.
                        .GroupBy(m => string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName + "@" + (FrameworkType(p.ParameterType) ? "framework" : TypeScope(p.ParameterType)))), StringComparer.Ordinal)
                        .SelectMany(group => group.Where(m => m.DeclaringType == group.First().DeclaringType)).ToArray();
                    if (candidates.Length != 1) throw new InvalidDataException(hook.Name + " has an ambiguous or missing target " + reflectedType.FullName + "::" + methodName);
                    stack.Push(candidates[0]); break;
                case Code.Stelem_Ref:
                    var value = Pop<object>();
                    int index = Pop<int>();
                    var array = Pop<object>();
                    if (array is MethodDefinition[] methods && value is MethodDefinition method
                        && index >= 0 && index < methods.Length && methods[index] == null)
                        methods[index] = method;
                    else if (array is ReflectedType[] types && value is ReflectedType parameter
                        && index >= 0 && index < types.Length && types[index] == null)
                        types[index] = parameter;
                    else throw new InvalidDataException(hook.Name + " has an invalid literal array slot");
                    break;
                case Code.Ret:
                    if (instruction != factory.Body.Instructions.Last() || stack.Count != 1 || stack.Pop() is not MethodDefinition[] result
                        || result.Any(m => m == null) || result.DistinctBy(m => m.FullName).Count() != result.Length)
                        throw new InvalidDataException(hook.Name + " has an incomplete or duplicate target array");
                    return result;
                default: throw new InvalidDataException(hook.Name + " has unsupported TargetMethods IL: " + instruction);
            }
        }
        throw new InvalidDataException(hook.Name + " has no returned targets");
    }

    // Keep a runtime Type distinct from a raw ldtoken: only the known GetTypeFromHandle call can
    // turn that token into a value allowed in a Type[] or used as a reflection receiver.
    private sealed record ReflectedType(TypeReference Type);

    private static string TypeScope(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        Mono.Cecil.ModuleDefinition module => module.Assembly.Name.Name,
        _ => ""
    };

    // The plugin and restored interop can reference different framework facades. Other assemblies are
    // never interchangeable: a lookalike game/parameter type from the plugin does not name the native type.
    private static bool FrameworkType(TypeReference type) => TypeScope(type)
        is "System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard";

    private static bool SameType(TypeReference left, TypeReference right) => left.FullName == right.FullName
        && (TypeScope(left) == TypeScope(right) || FrameworkType(left) && FrameworkType(right));

    /// <summary>The `dump.cs` this run reads RVAs from, or null when it was not handed one. The hook rows keep
    /// their `rva` across a regeneration only while the dump the spec was read from is the one that can be
    /// opened here, because an RVA means nothing without the binary it was read out of.</summary>
    internal sealed record Dump(ulong Sha256, string[] Lines)
    {
        private static readonly Regex RvaLine = new(@"^\s*// RVA: 0x([0-9A-F]+) ", RegexOptions.CultureInvariant);
        private static readonly Regex NamespaceLine = new("^// Namespace: ", RegexOptions.CultureInvariant);

        /// <summary>Opens the dump and answers null when its bytes are not the ones the spec froze, so a row
        /// whose RVA cannot be re-read keeps the RVA it already carries instead of a value from another build.</summary>
        internal static Dump? Open(string path, string expectedSha256, Action<string> report)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                report("dump " + path + " is not the build this spec froze (" + actual + ")");
                return null;
            }
            return new Dump(0, File.ReadAllLines(path));
        }

        /// <summary>The one RVA the dump declares for the method a row names, or null when the dump names none
        /// or the name stays overloaded and the dump cannot say which declaration the row meant. The declaration
        /// is found the way <c>MapNativeEvidence</c> finds it: the type's own class line, the `// Namespace:`
        /// line above it, and the `// RVA:` line above the method. The dump spells a signature in the
        /// decompiler's own C# types, so match the complete parameter type sequence after translating its
        /// aliases and the interop's object, collection, array and nested-type spellings. Never fall back to
        /// parameter count or to an address shared by otherwise mismatching declarations.
        /// </summary>
        internal string? Rva(MethodDefinition target)
        {
            string fullName = target.DeclaringType.FullName;
            int dot = fullName.LastIndexOf('.');
            string ns = dot < 0 ? "" : fullName[..dot], shortName = dot < 0 ? fullName : fullName[(dot + 1)..];
            // Cecil uses '/' for nested types; interop replaces compiler-generated angle brackets with '_'.
            // Normalize the dump's declaration by that same spelling, never supply an RVA by hook name.
            shortName = shortName.Replace('/', '.');
            var declaration = new Regex(@"^(public|internal|private|protected)[\w ]* class ([^\s:]+)( |$)", RegexOptions.CultureInvariant);
            var method = new Regex(@"\s" + Regex.Escape(target.Name) + @"\(", RegexOptions.CultureInvariant);
            var found = new HashSet<ulong>();
            for (int i = 1; i < Lines.Length; i++)
            {
                var declared = declaration.Match(Lines[i]);
                if (!declared.Success || declared.Groups[2].Value.Replace('<', '_').Replace('>', '_') != shortName) continue;
                int namespaceIndex = i - 1;
                while (namespaceIndex >= 0 && !NamespaceLine.IsMatch(Lines[namespaceIndex])) namespaceIndex--;
                if (namespaceIndex < 0) continue;
                // Il2CppDumper flattens compiler-generated nested classes into an empty namespace.
                string dumpNamespace = Lines[namespaceIndex]["// Namespace:".Length..].Trim();
                if (dumpNamespace != ns && !(fullName.Contains('/') && dumpNamespace.Length == 0)) continue;
                for (int j = i + 1; j < Lines.Length && !NamespaceLine.IsMatch(Lines[j]); j++)
                {
                    if (!method.IsMatch(Lines[j]) || !MatchesParameters(Lines[j], target)) continue;
                    var match = RvaLine.Match(Lines[j - 1]);
                    if (!match.Success) continue;
                    ulong rva = Convert.ToUInt64(match.Groups[1].Value, 16);
                    found.Add(rva);
                }
            }
            if (found.Count == 1) return "0x" + found.Single().ToString("X");
            Console.WriteLine("dump RVA unresolved for " + target.FullName + ": "
                + (found.Count == 0 ? "no declaration with the exact parameter types and an RVA"
                    : "multiple RVAs for the exact parameter types: " + string.Join(", ", found.Select(rva => "0x" + rva.ToString("X")))));
            return null;
        }

        /// <summary>Shared with the native audit: normalize declaration syntax, then require every parameter
        /// to match its pinned interop type. A qualified name may only use that type's own namespace.</summary>
        internal static bool MatchesParameters(string line, MethodDefinition target)
        {
            int open = line.IndexOf('('), close = line.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            var parameters = SplitTypes(line[(open + 1)..close]);
            if (parameters.Length != target.Parameters.Count) return false;
            for (int index = 0; index < parameters.Length; index++)
            {
                string parameter = parameters[index];
                int equals = parameter.IndexOf('=');
                if (equals >= 0) parameter = parameter[..equals];
                parameter = Regex.Replace(parameter.Trim(), @"\s+@?\w+$", "");
                if (Regex.IsMatch(parameter, @"^(ref|out|in)\s+"))
                    parameter = Regex.Replace(parameter, @"^(ref|out|in)\s+", "") + "&";
                parameter = Regex.Replace(parameter, @"\s+", "").Replace('/', '.');
                if (!Regex.IsMatch(parameter, "^" + TypePattern(target.Parameters[index].ParameterType) + "$",
                    RegexOptions.CultureInvariant)) return false;
            }
            return true;
        }

        private static string[] SplitTypes(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) return Array.Empty<string>();
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int index = 0; index < list.Length; index++)
            {
                char c = list[index];
                if (quote != '\0')
                {
                    if (c == '\\') index++;
                    else if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '"' or '\'') quote = c;
                else if (c is '(' or '<' or '[') depth++;
                else if (c is ')' or '>' or ']') depth--;
                else if (c == ',' && depth == 0) { parts.Add(list[start..index]); start = index + 1; }
            }
            parts.Add(list[start..]);
            return parts.ToArray();
        }

        private static string TypePattern(TypeReference type)
        {
            if (type is ByReferenceType byRef) return TypePattern(byRef.ElementType) + "&";
            if (type is PointerType pointer) return TypePattern(pointer.ElementType) + @"\*";
            if (type is ArrayType array) return TypePattern(array.ElementType) + @"\[" + new string(',', array.Rank - 1) + @"\]";
            if (type is GenericInstanceType generic)
            {
                if (generic.ElementType.FullName is "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1"
                    or "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1")
                    return TypePattern(generic.GenericArguments[0]) + @"\[\]";
                return TypePattern(generic.ElementType) + "<" + string.Join(",", generic.GenericArguments.Select(TypePattern)) + ">";
            }
            string full = Regex.Replace(type.FullName.Replace('/', '.'), @"`\d+", "");
            string native = full.StartsWith("Il2CppSystem.", StringComparison.Ordinal) ? full[6..] : full;
            TypeReference outer = type;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            string shortName = outer.Namespace.Length == 0 ? full : full[(outer.Namespace.Length + 1)..];
            string? alias = native switch
            {
                "System.Boolean" => "bool",
                "System.Byte" => "byte",
                "System.SByte" => "sbyte",
                "System.Int16" => "short",
                "System.UInt16" => "ushort",
                "System.Int32" => "int",
                "System.UInt32" => "uint",
                "System.Int64" => "long",
                "System.UInt64" => "ulong",
                "System.Single" => "float",
                "System.Double" => "double",
                "System.Decimal" => "decimal",
                "System.Char" => "char",
                "System.String" => "string",
                "System.Object" => "object",
                "System.IntPtr" => "nint",
                "System.UIntPtr" => "nuint",
                _ => null
            };
            var names = new[] { full, native, shortName, alias }.Where(name => name != null).Distinct();
            return "(?:" + string.Join("|", names.Select(name => Regex.Escape(name!))) + ")";
        }
    }

    /// <summary>The native arguments a patch body reads. Harmony identifies the instance, result, state and
    /// finalizer slots by name, also when TargetMethods patches derived receivers through a base-typed __instance;
    /// that instance's type is checked separately for every resolved target.</summary>
    private static IEnumerable<string> Loaded(MethodDefinition body)
        => HookRows.Loaded(body, p => p.Name is "__instance" or "__result" or "__state" || FinalizerInjected(p));

    private static bool Patch(TypeDefinition type)
        => type.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");

    /// <summary>The callback classes the compiled package hands to Runtime. Map's own list and the world-object
    /// feature list are both materialized as type tokens in their static initializers; reading those tokens keeps
    /// regeneration and the layout audit on the same declaration instead of preserving hand-written ownership.</summary>
    internal static string[] DeclaredHookClasses(IReadOnlyList<TypeDefinition> nativeTypes)
        => new[] { "MapNativeHooks", "ObjectNativeHooks" }
            .SelectMany(owner => nativeTypes.Single(t => t.Name == owner).Methods
                .Where(method => method.IsConstructor && method.IsStatic && method.HasBody))
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode == OpCodes.Ldtoken)
            .Select(instruction => instruction.Operand).OfType<TypeReference>()
            .Select(type => type.FullName).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Whether the audit classifies a member as one of the player/identity reads. The spellings are
    /// the audit's own, including the two members it counts as reads although their names do not say so: the
    /// active expedition the rundown manager looks up, and Unity's hierarchy lookup the hit path climbs. The
    /// level's own presentation and inventory reads of a player are of the same kind: the HUD manager, the local
    /// player's status layer and its text figures, and the item a player carries.</summary>
    internal static bool GameRead(string member) =>
        member is "Gear.GearIDRange::get_PlayfabItemId" or "Gear.GearIDRange::get_PlayfabItemInstanceId" or "RectTransformComp::get_IsVisible"
        || member is "PLOC_Base::get_m_owner" or "PLOC_InElevator::get_m_seat" or "ElevatorSeat::get_m_landingPoint"
            or "ElevatorSeat::get_m_cameraAlign" or "FPSCamera::get_m_posOffset" or "FPSCamera::get_m_posOffsetTransDuration"
            or "LookCameraController::get_LastLookInput"
            or "PLOC_Fall::get_m_enterTime" or "StateMachines.StateMachine`1::get_CurrentState"
        || member.StartsWith("Player.", StringComparison.Ordinal)
        || member.StartsWith("SNetwork.", StringComparison.Ordinal) || member.StartsWith("Agents.", StringComparison.Ordinal)
        || member.StartsWith("GuiManager::", StringComparison.Ordinal) || member.StartsWith("PlayerGuiLayer::", StringComparison.Ordinal)
        || member.StartsWith("PUI_", StringComparison.Ordinal)
        || member == "GuiLayerComp::get_Visible"
        || member.StartsWith("ItemEquippable::", StringComparison.Ordinal) || member.StartsWith("Item::", StringComparison.Ordinal)
        || member.StartsWith("ItemReplicator::", StringComparison.Ordinal)
        || member.StartsWith("ItemReplicationManager::", StringComparison.Ordinal)
        || member.StartsWith("NavMarkerLayer::", StringComparison.Ordinal)
        || member.StartsWith("NavMarker::", StringComparison.Ordinal)
        || member.StartsWith("NavMarkerComponent::", StringComparison.Ordinal)
        // The player's own audio settings a custom clip's output volume is bound to: the settings manager, its
        // data block and audio section, and the setting value wrapper they are read through.
        || member.StartsWith("CellSettingsManager::", StringComparison.Ordinal) || member.StartsWith("CellSettingsData", StringComparison.Ordinal)
        || member.StartsWith("CS_Value`1::", StringComparison.Ordinal)
        // The player's own first-person camera the custom-audio listener follows: `FPSCamera` inherits its Unity
        // camera field from `CameraController`, so the call site binds to the base type's getter.
        || member.StartsWith("CameraController::", StringComparison.Ordinal)
        // The sender's own interaction component: the host's range ruling for an authored interaction point reads
        // the search radius the game's interaction search uses for that player.
        // and the camera it searches from with the second radius the proximity check uses (PlayerInteractionReach).
        || member is "PlayerInteraction::get_m_searchRadius" or "PlayerInteraction::get_m_playerCam" or "PlayerInteraction::get_m_proximityRadius"
        // The game's static interaction switches, read back into a hold stimulus's failure detail.
        || member is "PlayerInteraction::get_InteractionEnabled" or "PlayerInteraction::get_SphereCheckInteractionEnabled" or "PlayerInteraction::get_CameraRayInteractionEnabled" or "PlayerInteraction::get_LadderInteractionEnabled"
        // The authoring world's player-health baseline and bounded native Use-input lease.
        || member is "Dam_SyncedDamageBase::get_Health" or "Dam_SyncedDamageBase::get_HealthMax"
            or "Dam_SyncedDamageBase::get_IsSetup" or "Dam_PlayerDamageBase::get_m_nextRegen"
            or "InputMapper::get_GetButton" or "InputMapper::get_GetButtonDown"
        // The success page a viewer's own menu shows: the main-menu layer that holds it (declared in no
        // namespace) and the page's own header and music members, read before the success-screen row writes them.
        || member.StartsWith("MainMenuGuiLayer::", StringComparison.Ordinal)
        || member.StartsWith("CellMenu.CM_PageExpeditionSuccess::", StringComparison.Ordinal)
        // The map test's own HUD and interaction observation: the fields of the game's compiler-generated coroutine
        // state machines (blink, letter reveal, intel text) and the interactors an interaction prompt lists.
        || member.StartsWith("CoroutineManager/_", StringComparison.Ordinal) || member.StartsWith("PlayerGuiLayer/_DoShowIntelText", StringComparison.Ordinal)
        || member is "Interact_Timed::get_m_interactors" or "Interact_Timed/InteractorInfo::get_Agent";

    /// <summary>Whether the audit classifies a member as one of the map-object reads: the level generation the
    /// zone table is built from, the level's data block, the course node a terminal reaches its zone through,
    /// the localization value type the door's own no-key prompt is built from, and the level identity a `level`
    /// mount is compared against — plus the level's own objects and machines beside that table: the asset a room
    /// reference loads, the alarm and scan puzzles, the generators and HSUs, the objective machine and its state
    /// block, the environment state, the encounter director, the world event manager, the elevator landing and
    /// the zone index a lighting read takes.</summary>
    internal static bool MapObjectRead(string member) =>
        member.StartsWith("IRF.InstancedRenderFeature", StringComparison.Ordinal)
        // The briefing window's own level data and ALL CLEARED icon, read by BriefingSectorIcons.
        || member.StartsWith("CellMenu.CM_ExpeditionWindow::", StringComparison.Ordinal)
        // The Rundown page's placed rundown and tier icon lists, and each icon's level, read by RundownTierLayout.
        || member is "CellMenu.CM_PageRundown_New::get_m_currentRundownData" or "CellMenu.CM_ExpeditionIcon_New::get_DataBlock"
            or "CellMenu.CM_PageRundown_New::get_m_tierSpacing"
        // The selection buttons, the load list and the intel button the menu places again after a content generation (RundownMenu.PlaceAgain).
        || member is "CellMenu.CM_PageRundown_New::get_m_rundownSelections" or "Globals.Global::get_ActiveRundownIds"
            or "CellMenu.CM_PageRundown_New::get_m_rundownIntelButton"
        || member.StartsWith("CellMenu.CM_PageRundown_New::get_m_expIconsTier", StringComparison.Ordinal)
        || member == "RundownManager::HasAllCompletetionPossibility"
        || member is "RundownManager::get_ActiveExpedition" or "RundownManager::get_ActiveRundownKey"
            or "RundownManager::get_RundownProgression" or "RundownManager::GetRundownProgressionExpeditionKey"
            or "DropServer.RundownProgression::get_Expeditions"
        // The fog repeller's infection shield sphere, found among the level's effect volumes by EnvironmentQuery.
        || member is "EffectVolumeManager::get_volumes" or "EffectVolume::get_modification" or "EffectVolume::get_contents"
            or "EV_Sphere::get_position" or "EV_Sphere::get_maxRadius"
        || member == "LayerManager::get_MASK_WORLD"
        // A standalone image board uses the same default ray layer PackTest probes, and refuses it unless both
        // native weapon and camera masks include it. These are metadata reads; gameplay remains game-unverified.
        || member is "LayerManager::get_LAYER_DEFAULT" or "LayerManager::get_MASK_BULLETWEAPON_RAY"
            or "LayerManager::get_MASK_CAMERA_RAY" or "LayerManager::get_MASK_DEFAULT"
        // Collision layers/masks read by authored geometry and native navigation generation, not game writes.
        || member is "LayerManager::get_LAYER_PLAYER_MOVER" or "LayerManager::get_LAYER_PLAYER_SYNCED"
            or "LayerManager::get_MASK_UNITY_NAVMESH_GENERATION" or "LayerManager::get_MASK_NODE_GENERATION"
        || member.StartsWith("LightBase::", StringComparison.Ordinal)
        // The culling node of a built room: its portals, lights and static cullables, read back to say whether the room is culled correctly.
        || member is "CullingSystem.C_Node::get_m_portals" or "CullingSystem.C_Node::get_m_cLights" or "CullingSystem.C_Node::get_m_staticCullables"
        // The temporary point light checks that a lease the game's light pool handed out is still in use before
        // writing or returning it; a read of the pool's own flag, not a game write.
        || member == "FX_EffectSystem.FX_PointLight::get_m_isInUse"
        // The game's random group pool cache by type and difficulty, which EnemyGroupPool clears at a build's start and fills
        // with the pool of the level's own Rundown package before the game builds one.
        || member == "Enemies.EnemySpawnManager::get_m_groupRandomizers"
        || member is "Enemies.EnemyAgent::get_AI" or "Enemies.EnemyAI::get_m_behaviour" or "Enemies.EnemyAI::get_m_locomotion"
            or "Enemies.EnemyBehaviour::get_m_currentStateName" or "Enemies.EnemyLocomotion::get_CurrentStateEnum"
            or "Enemies.EnemySync::get_m_agent"
            or "Enemies.pEnemySpawnData::get_mode" or "Enemies.pEnemySpawnData::get_groupReplicatorKey"
            or "Enemies.EnemyAI::get_m_scoutPath"
            or "Enemies.ScoutPath::get_IsValid" or "Enemies.ScoutPath::get_m_pathData"
            or "Enemies.EnemyAgent::get_EnemyBehaviorData"
            or "Enemies.EnemyAgent::get_Sync" or "Enemies.EnemySync::get_Replicator"
            or "Enemies.EnemyAgent::get_Damage" or "Enemies.EnemyAgent::get_IsSetup" or "Enemies.EnemyAgent::get_Position"
            or "Enemies.EnemyGroup::get_Members" or "Enemies.EnemyGroup::get_Replicator" or "Enemies.EnemyGroup::get_SurvivalWave"
            // The random enemy rules (contract M16) read the data block, the survival-wave flag and the node of a group.
            or "Enemies.EnemyGroup::get_m_groupDataBlock" or "Enemies.EnemyGroup::get_m_isSurvivalWave" or "Enemies.EnemyGroup::get_CurrentCourseNode"
        || member is "ExteriorRendering.ExteriorCamera::get_s_current" or "ExteriorRendering.ExteriorCamera::get_s_globalSwitch"
            or "ExteriorLight2::get_Enabled"
        || member == "ClusteredRendering::get_IN_LEVEL_VIEW_DISTANCE"
        || member == "GameStateManager::get_CurrentStateName"
        || member.StartsWith("LevelGeneration.", StringComparison.Ordinal)
        || member is "iWardenObjectiveItem::get_PickedUpByPlayer" or "iWardenObjectiveItem::get_PickupItemStatus" or "iWardenObjectiveItem::get_transform"
        || member is "HashRandom::get_Random" or "HashRandom::get_Seed"
        || member.StartsWith("GameData.", StringComparison.Ordinal) || member.StartsWith("AIGraph.", StringComparison.Ordinal)
        // Native DimensionData lives in the global namespace; M12 reads these exact frozen inputs.
        || member is "DimensionData::get_DimensionGeomorph" or "DimensionData::get_DimensionResourceSetID"
            or "DimensionData::get_IsStaticDimension" or "DimensionData::get_StaticLightSeed"
            or "DimensionData::get_StaticMarkerSeed" or "DimensionData::get_StaticZoneSeed" or "DimensionData::get_LevelLayoutData"
            or "DimensionData::get_VerticalExtentsDown" or "DimensionData::get_VerticalExtentsUp"
            or "DimensionData::get_AerialScale" or "DimensionData::get_AmbientIntensity"
            or "DimensionData::get_AtmosphereData" or "DimensionData::get_AtmosphereDensity"
            or "DimensionData::get_CloudsCoverage" or "DimensionData::get_CloudsCrawling"
            or "DimensionData::get_CloudsData" or "DimensionData::get_CloudsDensity"
            or "DimensionData::get_CloudsFade" or "DimensionData::get_CloudsShadowOpacity"
            or "DimensionData::get_CloudsSharpness" or "DimensionData::get_CloudsTimescale"
            or "DimensionData::get_Exposure" or "DimensionData::get_GodrayExponent"
            or "DimensionData::get_GodrayRange" or "DimensionData::get_IsOutside"
            or "DimensionData::get_LightAzimuth" or "DimensionData::get_LightElevation"
            or "DimensionData::get_LightIntensity" or "DimensionData::get_MieG"
            or "DimensionData::get_MieScattering" or "DimensionData::get_MultipleScattering"
            or "DimensionData::get_ReflectionsIntensity" or "DimensionData::get_Sandstorm"
            or "DimensionData::get_SandstormEdgeA" or "DimensionData::get_SandstormEdgeB"
            or "DimensionData::get_SandstormMinFog"
        || member.StartsWith("Localization.", StringComparison.Ordinal)
        || member.StartsWith("AssetShards.", StringComparison.Ordinal) || member.StartsWith("ChainedPuzzles.", StringComparison.Ordinal)
        || member.StartsWith("CheckpointManager::", StringComparison.Ordinal)
        || member.StartsWith("EnemyCostManager::", StringComparison.Ordinal)
        || member.StartsWith("EnemyPopulationManager::", StringComparison.Ordinal)
        // The heat rows of that manager (`EnemyPopulationManager.HeatType`): read to record the value a global wave
        // heat write replaces, so the world's end can give it back.
        || member.StartsWith("EnemyPopulationManager/HeatType::", StringComparison.Ordinal)
        || member.StartsWith("Interact_ComputerTerminal::", StringComparison.Ordinal)
        // The security door's own four interactables: the map object's parts the interaction row switches and
        // reads back, declared in no namespace like the terminal's own interaction component.
        || member.StartsWith("Interact_Base::", StringComparison.Ordinal)
        // An authored interaction point's own timed interactable, whose running timer the hold is read from, and
        // the game's interaction layer the point's object is built on so the game's interaction search finds it.
        || member == "Interact_Timed::get_TimerIsActive" || member == "Interact_Timed::get_InteractDuration"
            || member == "Interact_Timed::get_InteractionTimerRel"
            // Localized authored interaction prompts are verified against the actual native displayed text.
            || member == "Interact_Timed::get_InteractionMessage"
            || member == "LayerManager::get_LAYER_INTERACTION"
        || member.StartsWith("EnvironmentStateManager::", StringComparison.Ordinal) || member.StartsWith("WardenObjective", StringComparison.Ordinal)
        || member is "FogState::GetFogBlend" or "pEnvironmentState::get_FogStates"
            or "pEnvironmentState::get_LatestLightSequenceInteraction"
        || member.StartsWith("IWardenObjective::", StringComparison.Ordinal)
        // A bulkhead controller's own registry: one static list the bulkhead objective keeps, which is the
        // level's own answer to which controllers exist. Its type lives in the game's global namespace.
        || member.StartsWith("ProgressionObjective", StringComparison.Ordinal)
        // A bulkhead key's own pickup: the object an authored key row moves, declared in the global namespace
        // beside the bulkhead objective's registry above.
        || member.StartsWith("KeyItemPickup_Core::", StringComparison.Ordinal)
        || member.StartsWith("pWardenObjectiveState::", StringComparison.Ordinal) || member.StartsWith("WO_", StringComparison.Ordinal)
        // The encounter director and the event type it declares inside itself. A nested type is spelled with
        // the slash the metadata uses, so the director's own prefix does not reach its event: the wave row reads
        // the event's id through it, and that read is a read of the same machine.
        || member.StartsWith("LG_", StringComparison.Ordinal) || member.StartsWith("Mastermind::", StringComparison.Ordinal)
        || member.StartsWith("Mastermind/", StringComparison.Ordinal)
        || member is "SurvivalWave::get_m_courseNode" or "SurvivalWave::get_m_overrideWorldEventObjectFilter"
            or "SurvivalWave::get_m_populationDataID" or "SurvivalWave::get_m_settings"
            or "SurvivalWave::get_m_spawnType" or "SurvivalWave::get_m_state" or "SurvivalWave::get_m_populationPointsSpentTotal"
            or "SurvivalWave::get_m_groupSpawnTimer" or "SurvivalWave::get_m_waveSpawnTimer"
            or "SurvivalWave::get_m_populationPointsPerWaveMax" or "SurvivalWave::get_m_populationPointsSpentDuringWave"
        || member is "SyncTrigger::get_HasBeenTriggered" or "SyncTrigger::get_m_stateReplicator"
        || member == "iInteractPickup::get_IsActive"
        || member.StartsWith("WorldEventManager::", StringComparison.Ordinal) || member.StartsWith("WorldEventManager/", StringComparison.Ordinal)
        || member.StartsWith("ElevatorShaftLanding::", StringComparison.Ordinal)
        // The exact native noise packet a weak-door explosion submitted. Only the fields its fingerprint reads
        // are admitted; another packet member still has to earn its own evidence row and classification.
        || member is "NM_NoiseData::get_includeToNeightbourAreas" or "NM_NoiseData::get_node"
            or "NM_NoiseData::get_noiseMaker" or "NM_NoiseData::get_position"
            or "NM_NoiseData::get_radiusMax" or "NM_NoiseData::get_radiusMin"
            or "NM_NoiseData::get_raycastFirstNode" or "NM_NoiseData::get_type" or "NM_NoiseData::get_yScale"
        // The world event condition state a slot is read from: a struct in the game's global namespace, so the
        // manager prefix does not reach it, exactly like the objective state beside it above.
        || member.StartsWith("pWorldEventManagerState::", StringComparison.Ordinal)
        || member.StartsWith("pWorldEventSustainData::", StringComparison.Ordinal)
        || member.StartsWith("GlobalZoneIndex::", StringComparison.Ordinal)
        // The clock the objective machine's countdown is taken against: `Clock.ExpeditionProgressionTime`, the
        // same static block `Clock.get_Time` returns the level's own `Time` from. It is a static property in the
        // interop, so it is a read like any other even though its type sits in the game's global namespace.
        || member.StartsWith("Clock::", StringComparison.Ordinal)
        // The fixed-placement half's own reads: the item tables a row is placed through, the group spawn data a
        // fixed enemy is built from, and the global state a placed fog repeller carries. Its writes are the spawn
        // entries declared as write rows, exactly as the audit classifies them.
        || member.StartsWith("ItemSpawnManager::", StringComparison.Ordinal)
        || member is "Enemies.EnemyGroup::GetSpawnData" or "Enemies.EnemyAllocator::get_Current"
        // The placed repeller is addressed through its exact global state, not the pickup's delayed field.
        || member is "HeavyFogRepellerGlobalState::get_CurrentState" or "HeavyFogRepellerGlobalState::get_m_repellerSphere"
        // The native terminal sequence's rounds, timers and receivers; each read still requires an exact evidence row.
        || member.StartsWith("TimedTerminalSequencePuzzle::get_", StringComparison.Ordinal)
        || member.StartsWith("TimedTerminalSequencePuzzleRound::get_", StringComparison.Ordinal)
        // Fixed enemy placement verifies the delivered definition, AI mode/state and the native spawn data.
        // These reads remain in the placement path after the retired whole-wave debug path is removed.
        || member is "Enemies.EnemyAgent::get_EnemyDataID"
            or "Enemies.EnemyAgent::get_AI" or "Enemies.EnemyAI::get_m_behaviour" or "Enemies.EnemyAI::get_m_locomotion"
            or "Enemies.EnemyBehaviour::get_m_currentStateName" or "Enemies.EnemyLocomotion::get_CurrentStateEnum"
            or "Enemies.EnemySync::get_m_agent"
            or "Enemies.pEnemySpawnData::get_mode" or "Enemies.pEnemySpawnData::get_groupReplicatorKey"
        // The placed fog repeller sphere the `fog_repeller` level-object category is read from: the state the row
        // reports before it writes, read off the sphere the level built.
        || member.StartsWith("FogRepeller_Sphere::", StringComparison.Ordinal)
        // The glue manager's own members the door foam row reads and writes: the manager's sync sequence is a
        // read although its name does not say `get_`, and the two door members a case classifies beside it.
        || member.StartsWith("ProjectileManager::", StringComparison.Ordinal)
        || member is "Globals.Global::get_RundownIdToLoad" or "RundownManager::GetActiveExpeditionData"
            or "RundownManager::get_ActiveExpeditionUniqueKey" or "RundownManager::get_ActiveExpeditionBalanceData"
        || member.StartsWith("pActiveExpedition::", StringComparison.Ordinal)
        // The system object appliers' own reads: the elevator ride's cage, its cargo cage and the cage's placement
        // slots the cargo is built on, and the conversion that hands the level sentry's completion callback to the
        // gear spawn. All of them are declared in the game's global namespace.
        || member.StartsWith("ElevatorRide::", StringComparison.Ordinal) || member.StartsWith("ElevatorCage::", StringComparison.Ordinal)
        || member.StartsWith("ElevatorRide/_ElevatorEnd_d__115::", StringComparison.Ordinal)
        || member.StartsWith("ElevatorRide/_PlayerExit_d__119::", StringComparison.Ordinal)
        || member.StartsWith("ElevatorCargoCage", StringComparison.Ordinal)
        || member == "ItemReplicationManager/delItemCallback::op_Implicit";

    /// <summary>The pure query spellings: game members whose name does not start with `get_` and which are
    /// reads all the same — the cast and operator spellings the identity code uses, the one game accessor spelled
    /// without `get_`, the rundown manager's lookup of the active expedition, and Unity's hierarchy lookup the
    /// hit path climbs a bullet's collider with. The audit reads this list; there is no second copy.</summary>
    internal static readonly string[] ReadSpellings =
        { "TryCast", "op_Equality", "op_Inequality", "op_Implicit", "PlayerSlotIndex", "GetActiveExpeditionData", "GetComponentInParent" };

    /// <summary>The query members whose names the spellings above do not catch: game members that return
    /// information without changing any, because the game spells them as a lookup, a question or a value
    /// construction. Each is named here rather than matched by a spelling rule, and each is declared as a read: a
    /// pure query counted as a write would make the observation half that calls it look like an action layer.
    /// The generator writes a readback row for each one instead of a write row, and the audit classifies by this
    /// same list — one list, so the two cannot disagree.</summary>
    internal static readonly string[] QueryMembers =
    {
        // Native 0x14E3670: formats the supplied text with the current layer's label; no game-state mutation.
        "PUI_GameObjectives::StyleMainObjText",
        // dump.cs: reads the expedition's layer data and returns whether an additional and a secondary layer both exist; no game-state mutation.
        "RundownManager::HasAllCompletetionPossibility",
        // Native 0x15D6980: reads gate Type and returns fixed opening bounds; no game-state mutation.
        "LevelGeneration.LG_Gate::GetBoundingSize",
        // dump.cs:682723; termcase proves a dictionary read followed by String.ToUpper(), with no game-state write.
        "LevelGeneration.LG_ComputerTerminalCommandInterpreter::TryGetCommandName",
        "LevelGeneration.LG_ComputerTerminalManager::GetTerminal",
        "AssetShards.AssetShardManager::GetLoadedAsset",
        "ChainedPuzzles.ChainedPuzzleInstance::NRofPuzzles",
        // ScanStepIndex and ChainedPuzzleLookup read the existing slot; this lookup does not activate a puzzle.
        "ChainedPuzzles.ChainedPuzzleInstance::GetPuzzle",
        // Native read-only call chains are pinned in evidence/environment-fog-state.json.
        "FogState::GetFogBlend",
        "LevelGeneration.Dimension::GetDimension",
        "LevelGeneration.LG_Floor::GetDimension",
        "EnvironmentStateManager::GetLightMode",
        "GameData.GameDataBlockBase`1::GetAllBlocks",
        "GameData.GameDataBlockBase`1::GetBlock",
        "RundownManager::GetRundownProgressionExpeditionKey",
        "GlobalZoneIndex::.ctor",
        "ItemEquippable::GetCurrentClip",
        "ItemEquippable::GetMaxClip",
        "AIGraph.AIG_CourseNode::TryGetCourseNode",
        "AIGraph.pCourseNode::Set",
        "AIGraph.pCourseNode::TryGet",
        "SNetwork.SNetStructs/pReplicator::SetID",
        "SNetwork.SNetStructs/pReplicator::IsValid",
        "SNetwork.SNetStructs/pReplicator::TryGetID",
        "SNetwork.SNet_DynamicReplicator`1::GetSpawnData",
        // 20403457 RVA 0x1BB7420 returns the named capture buffer's isValid flag; no state write. The checkpoint save row's readback.
        "SNetwork.SNet_Capture::GotBuffer",
        "Item::Get_pItemData",
        "LevelGeneration.LG_ResourceContainer_Sync::GetStateReplicator",
        "LevelGeneration.LG_PickupItem_Sync::GetCurrentState",
        // 20403457 RVA 0x320030 returns the nullable pickup state replicator; no state write.
        "LevelGeneration.LG_PickupItem_Sync::GetStateReplicator",
        "WorldEventManager::GetWorldEventObjectsNonAlloc",
        "WorldEventManager::CheckWorldEventObjectPlaceFilter",
        "Enemies.EnemyGroup::GetSpawnData",
        "ItemSpawnManager::ItemHasMode",
        "ItemSpawnManager::GetItemPrefabs",
        "UnityEngine.GameObject::Find",
        "LevelGeneration.LG_ComputerTerminal::CommandIsHidden",
        "LevelGeneration.LG_ComputerTerminalCommandInterpreter::TryGetCommand",
        "Mastermind::TryGetEvent",
        // The item-slot rows' two lookups of the level item an insertion or a removal names: the level item the
        // inserted item's data names (the lookup the game's own insertion validation makes) and the level item's
        // pickup sync. Each returns information and changes none, although neither is spelled `get_`.
        "Player.PlayerBackpackManager::TryGetItemInLevelFromItemData",
        // The host's own player agent the level sentry is spawned for: a lookup of the local agent, spelled without
        // `get_`.
        "Player.PlayerManager::GetLocalPlayerAgent",
        // The pool the level sentry's ammunition fractions are taken of: the host's backpack lookup, spelled
        // `Try...`, and the class-ammunition cap of the item in its class slot, spelled without `get_`.
        "Player.PlayerBackpackManager::TryGetBackpack",
        "ItemEquippable::GetClassAmmoMaxCap",
        // The landing solver ForgePlayer uses (dump RVA 0x1305D90): a static
        // Boolean query with an out Vector3, whose exact readback declaration is still compared both ways.
        "Player.PlayerAgent::SampleWarpPosition",
        "LevelGeneration.ItemInLevel::GetSyncComponent",
        "SNetwork.SNetStructs/pPlayer::TryGetPlayer",
        // The localization read a stored Text block is turned into text with: `Text.Get` is the game's own
        // per-language value for one block id, a pure lookup that writes nothing, and the printed-line row reads it
        // because the terminal's own `AddLine` takes the line as a plain string.
        "Localization.Text::Get",
        // The glue manager's own sequence and the two door members the foam row reads: the manager's next sync id
        // and the door's own replicator provider and attached foam volume are all reads, although only one of the
        // three is spelled `get_`.
        "ProjectileManager::GetNextSyncID",
        "LevelGeneration.LG_SecurityDoor::GetSyncStruct",
        "LevelGeneration.LG_WeakDoor::GetSyncStruct",
        "LevelGeneration.LG_WeakDoor::GetAttachedGlueVolume",
        "LevelGeneration.LG_SecurityDoor::GetAttachedGlueVolume",
        "WardenObjectiveManager::HasWardenObjectiveDataForLayer",
        "WardenObjectiveManager::TryGetWardenObjective",
        // Native RVA 0x1515D20 returns the selected layer's existing objective-object list. The HSU guard
        // observes membership; registering objects still requires the separately declared AddObjectiveGameObject.
        "WardenObjectiveManager::GetObjectiveGameObjects",
        "pWardenObjectiveState::GetChainIndexForLayer",
        "pWardenObjectiveState::GetLayerStatus",
        "pWardenObjectiveState::GetLayerSubStatus",
        "pWardenObjectiveState::GetStartTimeFromLayer",
        // The world event condition slot's own getter: the machine's answer for one slot of its replicated
        // `WorldEventConditions` array, read by `forge.query.map.world_event_condition` exactly as the action
        // writes it through the same state.
        "pWorldEventManagerState::GetWorldEventCondition",
        // Whether a sustained world event slot is running: the readback `fog_cycle` takes after starting or stopping the
        // repeating fog, from the same replicated state the condition slots above live in.
        "pWorldEventSustainData::IsValid"
    };

    /// <summary>The door/terminal fact half's own guard, which is the session gate a door or terminal readback
    /// runs inside when its body does not call a session guard directly. `Guard` is the same session entry the
    /// session's `Run` is: it runs the callback on the owning thread and drops it while the half is not attached.</summary>
    private static string? Facts(MethodDefinition body)
    {
        foreach (var holder in new[] { body }.Concat(body.DeclaringType.NestedTypes.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            if (holder.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Any(m => m.DeclaringType.FullName == "ForgeMap.Native.DoorTerminalFacts" && m.Name.StartsWith("Guard", StringComparison.Ordinal)))
                return "Guard";
        }
        return null;
    }
    /// <summary>The session guard (`MapPluginSession.Run`, which takes the half as its owner) a patch reaches the
    /// half through. `Build` is the same boundary for level-building callbacks, which also run in a level that only shows a
    /// recording; `ApplyFailRules` is it for the one readback whose callback takes a `ref` answer.</summary>
    private static string? Guard(MethodDefinition body)
        => HookRows.Guard(body, "ForgeMap.Native.MapPluginSession", name => name is "Run" or "Build" or "ApplyFailRules");
}
