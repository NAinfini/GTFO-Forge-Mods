using System.Text.RegularExpressions;
using Mono.Cecil;

// What the shipped Enemy domain hooks and calls, read from the IL of ForgeEngine.dll. The auditor and the specification
// generator share this reader so the strings the generator freezes are exactly the strings the auditor checks.
internal sealed record HookSite(string Type, string ScopeFile, SortedSet<string> Kinds);
internal sealed record CallSite(string DeclaringType, string ScopeFile, SortedSet<string> Callers);
internal sealed record ForgeUsage(
    Dictionary<(string Type, string Name), HookSite> Hooks,
    Dictionary<string, CallSite> Calls);

internal static class ForgeIl
{
    /// <summary>Whether a top-level type of ForgeEngine.dll belongs to the Enemy domain: its namespace, plus the shared native bodies it reads through that live in the host namespace.</summary>
    internal static bool IsEnemy(TypeDefinition type)
        => type.Namespace == "ForgeEnemy" || type.Namespace.StartsWith("ForgeEnemy.", StringComparison.Ordinal)
            || type.FullName is "ForgeEngine.GameBindings.GameAreaVolumes" or "ForgeEngine.GameBindings.GameCourseNodes";

    internal static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Walk)) yield return nested;
    }

    // A Forge call can reach a declaration through a generic instantiation (GameDataBlockBase<EnemyDataBlock>),
    // so such a signature spells the instantiation while the declaration itself sits on the open type. Type
    // arguments are dropped on both sides for the declaration match only; the raw signature stays what
    // forge-use.call-coverage compares against the IL reference, so a new instantiation is still a new claim.
    internal static string Open(string fullName)
    {
        while (Regex.IsMatch(fullName, "<[^<>]*>")) fullName = Regex.Replace(fullName, "<[^<>]*>", "");
        return fullName;
    }

    internal static bool SignatureMatches(MethodDefinition method, string reference)
    {
        string Normalize(string value)
        {
            foreach (var parameter in method.DeclaringType.GenericParameters.Concat(method.GenericParameters))
                value = Regex.Replace(value, @"(?<![\w.])" + Regex.Escape(parameter.Name) + @"(?![\w.])",
                    (parameter.Type == GenericParameterType.Method ? "!!" : "!") + parameter.Position);
            return Open(value);
        }
        return Normalize(method.FullName) == Normalize(reference);
    }

    // Compiler-generated lambdas, local functions and state machines are attributed to the source method that owns them.
    internal static string Caller(MethodDefinition method)
    {
        var type = method.DeclaringType; string name = method.Name;
        while (type.Name.StartsWith('<') && type.DeclaringType != null)
        {
            var generated = Regex.Match(type.Name, "^<([^>]+)>");
            if (generated.Success && !name.StartsWith('<')) name = generated.Groups[1].Value;
            type = type.DeclaringType;
        }
        var owner = Regex.Match(name, "^<([^>]+)>");
        return type.FullName + "::" + (owner.Success ? owner.Groups[1].Value : name);
    }

    internal static ForgeUsage Read(string pluginPath, IReadOnlySet<string> gameScopes)
    {
        var hooks = new Dictionary<(string Type, string Name), HookSite>();
        var calls = new Dictionary<string, CallSite>(StringComparer.Ordinal);
        using var forge = AssemblyDefinition.ReadAssembly(pluginPath);
        foreach (var type in forge.MainModule.Types.Where(IsEnemy).SelectMany(Walk))
        {
            foreach (var patch in type.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
            {
                // The third constructor argument, when present, is the argument-type array that tells overloads apart.
                if (patch.ConstructorArguments.Count is not (2 or 3) || patch.ConstructorArguments[0].Value is not TypeReference target
                    || patch.ConstructorArguments[1].Value is not string name
                    || patch.ConstructorArguments.Count == 3 && patch.ConstructorArguments[2].Value is not CustomAttributeArgument[])
                    throw new InvalidDataException("Unsupported HarmonyPatch shape on " + type.FullName);
                var kinds = type.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer"
                        || m.CustomAttributes.Any(a => a.AttributeType.FullName is "HarmonyLib.HarmonyPrefix" or "HarmonyLib.HarmonyPostfix" or "HarmonyLib.HarmonyFinalizer"))
                    .Select(m => m.Name == "Prefix" || m.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPrefix") ? "prefix" : m.Name == "Finalizer" || m.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyFinalizer") ? "finalizer" : "postfix");
                var key = (target.FullName, name);
                if (!hooks.TryGetValue(key, out var site))
                    hooks[key] = site = new HookSite(target.FullName, AssemblyFile(target.Scope), new SortedSet<string>(StringComparer.Ordinal));
                site.Kinds.UnionWith(kinds);
            }
            foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    if (!gameScopes.Contains(AssemblyFile(called.DeclaringType.Scope))) continue;
                    if (!calls.TryGetValue(called.FullName, out var site))
                        calls[called.FullName] = site = new CallSite(called.DeclaringType.FullName,
                            AssemblyFile(called.DeclaringType.Scope), new SortedSet<string>(StringComparer.Ordinal));
                    site.Callers.Add(Caller(method));
                }
        }
        return new ForgeUsage(hooks, calls);
    }

    // The scope of an IL reference to a frozen assembly spells the file the specification locks ("Modules-ASM.dll").
    private static string AssemblyFile(IMetadataScope scope)
        => scope.Name.EndsWith(".dll", StringComparison.Ordinal) ? scope.Name : scope.Name + ".dll";
}
