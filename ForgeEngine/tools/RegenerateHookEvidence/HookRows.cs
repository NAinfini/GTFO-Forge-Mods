using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeEngine.HookEvidence;

/// <summary>
/// The readings of the compiled plugin and the interop assemblies that every domain's hook-evidence regenerator shares:
/// which game members a domain calls, which declaration a patch class targets, what a patch body reads and writes, and
/// how a table is written back. Each domain keeps its own table layout, classifiers and guard names; the Map and Player
/// layout audits compile this file beside their regenerator, so the audit and the generator read the same rules.
/// </summary>
internal static class HookRows
{
    /// <summary>One game member the plugin calls, with the declaration the call binds to along the type's base
    /// chain: the spellings an evidence row pins are these, not the call site's own.</summary>
    internal sealed record Call(string Member, string Signature, string Type, bool IsStatic);

    /// <summary>Opens the built engine, its framework assembly and the named interop assemblies (names under
    /// `interop/`, or full paths) with one resolver, hands them to <paramref name="run"/> and disposes them.</summary>
    internal static void WithAssemblies(string bepInEx, string artifacts, IEnumerable<string> interopFiles,
        Action<AssemblyDefinition, AssemblyDefinition, AssemblyDefinition[]> run)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(bepInEx, "core"));
        resolver.AddSearchDirectory(Path.Combine(bepInEx, "interop"));
        var reader = new ReaderParameters { AssemblyResolver = resolver };
        using var engine = AssemblyDefinition.ReadAssembly(Path.Combine(artifacts, "bin/ForgeEngine/release/ForgeEngine.dll"), reader);
        using var framework = AssemblyDefinition.ReadAssembly(Path.Combine(artifacts, "bin/ForgeEngine.Framework/release/ForgeEngine.Framework.dll"), reader);
        var interop = interopFiles.Select(file => AssemblyDefinition.ReadAssembly(Path.Combine(bepInEx, "interop", file), reader)).ToArray();
        try { run(engine, framework, interop); }
        finally { foreach (var assembly in interop) assembly.Dispose(); }
    }

    internal static TypeDefinition[] GameTypes(IEnumerable<AssemblyDefinition> gameAssemblies)
        => gameAssemblies.SelectMany(a => a.MainModule.Types.SelectMany(Walk)).ToArray();

    /// <summary>The game declarations a set of call sites binds to, one entry per declaration and not per name: a
    /// name the game overloads carries one row per form, so collapsing the calls would drop every form but the first.
    /// The engine's own interop members are not the game's types and carry no evidence row, which is the boundary the
    /// audit draws before it sorts anything.</summary>
    internal static Call[] Calls(IEnumerable<MethodReference> called, IReadOnlyList<TypeDefinition> gameTypes, IEnumerable<AssemblyDefinition> gameAssemblies)
    {
        var gameAssemblyNames = new HashSet<string>(gameAssemblies.Select(a => a.Name.Name), StringComparer.Ordinal);
        return called.Where(m => gameAssemblyNames.Contains(Scope(m)))
            .Select(m => Resolve(gameTypes, m))
            .Where(x => x != null)
            .Select(x => new Call(Member(x!), x!.FullName, x.DeclaringType.FullName, x.IsStatic))
            .Where(x => !Engine(x.Member))
            .DistinctBy(x => x.Signature, StringComparer.Ordinal)
            .OrderBy(x => x.Member, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The interop's own object base, collections, Unity members and Unity's text package, which are
    /// not the game's types and carry no evidence row of their own. A member of one of these cannot carry a row
    /// even in principle: the game interop assemblies the audit opens are the only ones a row resolves against,
    /// so an engine call left in the write set would be a write no row could ever satisfy. This is the one
    /// definition; the audits read it rather than keeping a second copy that can disagree with it.</summary>
    internal static bool Engine(string member) => member.StartsWith("Il2CppInterop.", StringComparison.Ordinal)
        || member.StartsWith("Il2CppSystem.", StringComparison.Ordinal)
        || member.StartsWith("UnityEngine.", StringComparison.Ordinal)
        || member.StartsWith("TMPro.", StringComparison.Ordinal);

    /// <summary>The rows a table already carries, keyed the way a regenerated row is matched: a readback by the
    /// member it documents, a write by its whole signature, so the forms of one overloaded name keep their own rows.
    /// A row naming a declaration the interop assemblies no longer have is stale and is dropped rather than kept.</summary>
    internal static (Dictionary<string, JsonObject> Readbacks, Dictionary<string, JsonObject> Writes) Previous(
        JsonObject spec, IReadOnlyList<TypeDefinition> gameTypes)
    {
        var readbacks = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in spec["readbacks"]!.AsArray().OfType<JsonObject>())
        {
            var owner = gameTypes.FirstOrDefault(t => t.FullName == (string?)row["type"]);
            var name = Name((string)row["signature"]!);
            var declared = owner == null ? null : Chain(gameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == name);
            if (declared != null) readbacks[Member(declared)] = row;
        }
        var writes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in spec["writes"]!.AsArray().OfType<JsonObject>())
        {
            var owner = gameTypes.FirstOrDefault(t => t.FullName == (string?)row["type"]);
            var signature = (string?)row["signature"];
            var declared = owner == null || signature == null
                ? null : Chain(gameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.FullName == signature);
            if (declared != null) writes[declared.FullName] = row;
        }
        return (readbacks, writes);
    }

    /// <summary>The members a domain writes: a non-getter game call that is not one of the pure query spellings
    /// the audit names and not one of the query members it names one by one. A query left in this set would be a
    /// read the audit refuses as an undeclared write.</summary>
    internal static bool IsWrite(Call call, string[] readSpellings, string[] queryMembers)
    {
        string name = Name(call.Member);
        return !name.StartsWith("get_", StringComparison.Ordinal) && Array.IndexOf(readSpellings, name) < 0
            && Array.IndexOf(queryMembers, call.Member) < 0;
    }

    /// <summary>A new write row. The row id only carries the parameters when a name has more than one form, so the
    /// ids of the single-form rows stay the ones the evidence already spelled.</summary>
    internal static JsonObject WriteRow(Call call, int forms, IReadOnlyList<TypeDefinition> gameTypes) => new()
    {
        ["id"] = forms == 1 ? Id(call.Member) : Id(call),
        ["assembly"] = Assembly(gameTypes, call.Type),
        ["type"] = call.Type,
        ["isStatic"] = call.IsStatic,
        ["signature"] = call.Signature,
        ["role"] = "a game entry this provider calls: a non-getter member whose effect is the action it names, reached through the handler of the row that declares it."
    };

    /// <summary>The readback rows: only the reads the audit sorts into the domain's declared sets carry a row. The
    /// master read is resolved from the compiled module like the rest and the audit appends it to the player set
    /// itself, so its row would be an extra there; a game read outside the declared sets is one the audit refuses,
    /// so it is reported rather than silently dropped. A read is one row per member, because the audit's read sets
    /// are compared by member and a second row for an overload would read as a read it never saw.</summary>
    internal static JsonArray Readbacks(IEnumerable<Call> reads, IReadOnlyDictionary<string, JsonObject> previous,
        IReadOnlyList<TypeDefinition> gameTypes, Func<string, bool> classified, List<string> unclassified)
    {
        var rows = new JsonArray();
        foreach (var call in reads.DistinctBy(c => c.Member, StringComparer.Ordinal))
        {
            if (call.Member == "SNetwork.SNet::get_IsMaster") continue;
            if (!classified(call.Member)) { unclassified.Add(call.Member); continue; }
            rows.Add(previous.TryGetValue(call.Member, out var row) ? row.DeepClone() : new JsonObject
            {
                ["id"] = Id(call.Member),
                ["assembly"] = Assembly(gameTypes, call.Type),
                ["type"] = call.Type,
                ["isStatic"] = call.IsStatic,
                ["signature"] = call.Signature
            });
        }
        return rows;
    }

    /// <summary>Writes a table back. The rows are written from parsed values, so the tables a file carries beside
    /// them are re-emitted the same way: a section left as the raw element the parser produced would be serialized
    /// as a verbatim copy of the old text, which is how a table a run did not rewrite would drift out of the
    /// document.</summary>
    internal static void Write(JsonObject spec, string path, string end = "\n")
    {
        foreach (string table in new[] { "callEdges", "arrays", "enums", "shapes" })
            if (spec[table] is JsonValue verbatim) spec[table] = JsonNode.Parse(verbatim.ToJsonString());
        File.WriteAllText(path, spec.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Values built here (JsonValue.Create) serialize through a type resolver; parsed values do not need one.
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).Replace("\r\n", "\n") + end);
    }

    /// <summary>The one method a patch class targets, resolved the way Harmony resolves it: the name is looked up
    /// on the type the `[HarmonyPatch]` attribute declares, and only a name that type does not carry at all is
    /// looked up along its base chain, where the first — most derived — declaration wins, which is how an
    /// override and the base it replaces stay one target.
    ///
    /// The form of an overloaded name is pinned by the parameter type list the attribute declares and by nothing
    /// else. The patch body's own arguments are how Harmony binds a body to a target it has already chosen, so
    /// reading a form out of them would answer one the attribute never named; a name the declared type carries
    /// more than once with no parameter list to choose between the forms is the ambiguity Harmony itself refuses,
    /// and it is reported instead of guessed.</summary>
    internal static MethodDefinition? Target(IReadOnlyList<TypeDefinition> gameTypes, TypeDefinition owner,
        CustomAttribute patch, string name)
    {
        // The attribute's parameter list, which is how a class with an overloaded target names the form it
        // means. Cecil carries `new[] { typeof(A), typeof(B) }` as an array of its own argument values.
        var declared = patch.ConstructorArguments.Count > 2
            ? (patch.ConstructorArguments[2].Value as CustomAttributeArgument[])?.Select(a => a.Value as TypeReference).ToArray()
            : null;
        // Harmony's own variation list, applied the way `HarmonyPatch.ParseSpecialArguments` applies it: a by-value
        // spelling with an `Out`/`Ref` variation becomes the by-ref form before the reflection lookup, and a
        // `Pointer` variation the pointer form. Without this a hook whose target takes a by-ref parameter — the
        // wave-anchor start's `out ushort eventID` — resolves no form at all, because the interop assembly spells
        // that parameter `System.UInt16&` and the attribute can only name `System.UInt16`.
        var variations = patch.ConstructorArguments.Count > 3
            ? (patch.ConstructorArguments[3].Value as CustomAttributeArgument[]) : null;
        var wanted = declared is { Length: > 0 } && declared.All(t => t != null)
            ? declared.Select(t => t!.FullName).ToArray() : null;
        if (wanted != null && variations is { Length: > 0 })
            for (int index = 0; index != wanted.Length && index != variations.Length; index++)
            {
                if (wanted[index].EndsWith("&", StringComparison.Ordinal)
                    || wanted[index].EndsWith("*", StringComparison.Ordinal)) continue;
                switch (Variation(variations[index]))
                {
                    case "Ref" or "Out": wanted[index] = wanted[index] + "&"; break;
                    case "Pointer": wanted[index] = wanted[index] + "*"; break;
                }
            }
        foreach (var type in Chain(gameTypes, owner))
        {
            var candidates = type.Methods.Where(m => m.Name == name).ToArray();
            if (wanted != null)
            {
                var form = candidates.FirstOrDefault(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted));
                if (form != null) return form;
                continue;
            }
            if (candidates.Length == 1) return candidates[0];
            if (candidates.Length > 1) return null;
        }
        return null;
    }

    /// <summary>The name of one `HarmonyLib.ArgumentType` constant. Cecil carries an enum array element as its
    /// underlying constant, so the enum's own fields are what turn the value back into the name Harmony compares
    /// by; a variation the enum does not declare answers null and changes no type.</summary>
    private static string? Variation(CustomAttributeArgument argument)
    {
        if (argument.Type?.Resolve() is not { IsEnum: true } definition) return null;
        foreach (var field in definition.Fields)
            if (field.HasConstant && Equals(field.Constant, argument.Value)) return field.Name;
        return null;
    }

    /// <summary>The parameters a patch body reads, by its own `ldarg`/`ldarga` and by the display-class fields
    /// the compiler hoists a captured parameter into: a parameter a lambda captures is not loaded by the body
    /// itself, so the assembly spells the read as a field of the body's own display class. The audit reads both
    /// spellings the same way. <paramref name="injected"/> names Harmony's own slots (the instance, `__result`,
    /// `__state` and whatever else the domain admits), which are not arguments of the patched method.</summary>
    internal static IEnumerable<string> Loaded(MethodDefinition body, Func<ParameterDefinition, bool> injected)
    {
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        string Path(string name) => name.Replace('+', '/');
        var display = body.DeclaringType.NestedTypes
            .Where(t => t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ParameterDefinition? last = null;
        foreach (var instruction in body.Body.Instructions)
        {
            var parameter = instruction.OpCode.Code switch
            {
                Code.Ldarg_0 => body.Parameters.ElementAtOrDefault(0),
                Code.Ldarg_1 => body.Parameters.ElementAtOrDefault(1),
                Code.Ldarg_2 => body.Parameters.ElementAtOrDefault(2),
                Code.Ldarg_3 => body.Parameters.ElementAtOrDefault(3),
                Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand as ParameterDefinition,
                _ => null
            };
            if (parameter != null)
            {
                last = parameter;
                if (!injected(parameter) && seen.Add(parameter.Name)) yield return parameter.Name;
                continue;
            }
            // The store into the display class is the parameter's other spelling: the argument loaded just before
            // it is the one being hoisted. Whether it was read is decided by the loads below, because a field the
            // body only ever writes is not a read.
            if (instruction.OpCode.Code == Code.Stfld && instruction.Operand is FieldReference field && last != null && !injected(last)
                && display.Any(t => Path(t.FullName) == Path(field.DeclaringType.FullName)))
                alias[field.Name] = last.Name;
        }
        if (alias.Count == 0) yield break;
        foreach (var holder in new[] { body }.Concat(display.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            foreach (var instruction in holder.Body.Instructions)
                if (instruction.OpCode.Code is Code.Ldfld or Code.Ldflda or Code.Ldsfld or Code.Ldsflda
                    && instruction.Operand is FieldReference field && alias.TryGetValue(field.Name, out var name)
                    && seen.Add(name))
                    yield return name;
        }
    }

    /// <summary>Whether one patch body changes a parameter it declares, which is the write side of the same
    /// declaration the read set comes from: a by-ref parameter the body stores into directly is one the patched
    /// method reads back changed. The rule is the audit's own, instruction for instruction — a store through a
    /// parameter's address (`<c>starg</c>`, or `<c>ldarga</c>`/by-ref `<c>ldarg</c>` followed by an indirect
    /// store) and nothing else — because the audit is what reads the row back.</summary>
    internal static bool Stored(MethodDefinition body, ParameterDefinition parameter)
    {
        var instructions = body.Body.Instructions;
        foreach (var instruction in instructions)
        {
            bool bound = instruction.OpCode.Code switch
            {
                Code.Ldarg_0 => body.Parameters.ElementAtOrDefault(0) == parameter,
                Code.Ldarg_1 => body.Parameters.ElementAtOrDefault(1) == parameter,
                Code.Ldarg_2 => body.Parameters.ElementAtOrDefault(2) == parameter,
                Code.Ldarg_3 => body.Parameters.ElementAtOrDefault(3) == parameter,
                Code.Ldarg or Code.Ldarg_S or Code.Ldarga or Code.Ldarga_S => instruction.Operand == parameter,
                _ => false
            };
            if (bound && instruction.OpCode.Code is Code.Starg or Code.Starg_S) return true;
            if (!bound) continue;
            int at = instructions.IndexOf(instruction);
            if (instruction.OpCode.Code is Code.Ldarga or Code.Ldarga_S)
            {
                if (at + 1 < instructions.Count && Indirect(instructions[at + 1].OpCode.Code)) return true;
                continue;
            }
            if (!parameter.ParameterType.IsByReference) continue;
            for (int next = at + 1; next < instructions.Count && next <= at + 3; next++)
                if (Indirect(instructions[next].OpCode.Code)) return true;
        }
        return false;
    }

    private static bool Indirect(Code code) => code is Code.Stind_I or Code.Stind_I1 or Code.Stind_I2
        or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Stind_Ref or Code.Stobj;

    /// <summary>The session guard a patch reaches its half through: the first call on <paramref name="session"/>
    /// whose name <paramref name="guard"/> accepts, found in the body, in the patch class's helpers the body calls
    /// and in the display classes the compiler moved a lambda into. Nothing is loaded until the session exists, so
    /// the guard is what a patch's whole callback runs inside.</summary>
    internal static string? Guard(MethodDefinition body, string session, Func<string, bool> guard)
    {
        var closures = body.DeclaringType.NestedTypes
            .Where(t => t.Name.StartsWith("<>", StringComparison.Ordinal) || t.Name.StartsWith("<", StringComparison.Ordinal));
        var helpers = body.DeclaringType.Methods.Where(m => m.HasBody && m != body
            && body.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Any(c => c.Name == m.Name && c.DeclaringType.FullName == body.DeclaringType.FullName));
        foreach (var holder in new[] { body }.Concat(helpers).Concat(closures.SelectMany(t => t.Methods)))
        {
            if (!holder.HasBody) continue;
            if (holder.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .FirstOrDefault(m => m.DeclaringType.FullName == session && guard(m.Name)) is { } found)
                return found.Name;
        }
        return null;
    }

    private static IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));

    private static string Scope(MethodReference method)
        => method.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";

    internal static string Member(MethodReference method)
        => (method.DeclaringType is GenericInstanceType generic ? generic.ElementType.FullName : method.DeclaringType.FullName)
            + "::" + method.Name;

    internal static IEnumerable<TypeDefinition> Chain(IReadOnlyList<TypeDefinition> gameTypes, TypeDefinition declared)
    {
        for (var current = declared; current != null;)
        {
            yield return current;
            var name = current.BaseType?.FullName;
            current = name == null ? null : gameTypes.FirstOrDefault(t => t.FullName == name);
        }
    }

    /// <summary>The declaration a call binds to, resolved along the declaring type's base chain by name and
    /// parameter types, exactly as the audit resolves it. A member the interop assemblies do not declare answers
    /// nothing and carries no row.</summary>
    internal static MethodDefinition? Resolve(IReadOnlyList<TypeDefinition> gameTypes, MethodReference call)
    {
        var ownerName = call.DeclaringType is GenericInstanceType generic ? generic.ElementType.FullName : call.DeclaringType.FullName;
        var owner = gameTypes.FirstOrDefault(t => t.FullName == ownerName);
        if (owner == null) return null;
        return Chain(gameTypes, owner).SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == call.Name
            && m.Parameters.Select(ParameterKey).SequenceEqual(call.Parameters.Select(ParameterKey)));
    }

    /// <summary>The spelling one parameter is matched by across a call site and a declaration. A generic
    /// type's own parameter is named `TState` on the declaration and by its position (`!1`) on a call through
    /// the closed type — the reactor's state replicator is such a call — so a generic parameter is compared by
    /// its position and every other type by its full name. The audits read this same rule.</summary>
    internal static string ParameterKey(ParameterDefinition parameter) => parameter.ParameterType switch
    {
        // TryGetItem(out T) has T& in the declaration and !0& at a closed queue call site.
        ByReferenceType { ElementType: GenericParameter g } => (g.Type == GenericParameterType.Method ? "!!" : "!") + g.Position + "&",
        GenericParameter { Type: GenericParameterType.Method } g => "!!" + g.Position,
        GenericParameter g => "!" + g.Position,
        var type => type.FullName
    };

    internal static string Name(string signature)
    {
        int start = signature.LastIndexOf("::", StringComparison.Ordinal) + 2;
        int end = signature.IndexOf('(', start);
        return end < 0 ? signature[start..] : signature[start..end];
    }

    private static string Assembly(IReadOnlyList<TypeDefinition> gameTypes, string typeName)
    {
        var owner = gameTypes.FirstOrDefault(t => t.FullName == typeName);
        return owner == null ? "Modules-ASM.dll" : owner.Module.Assembly.Name.Name + ".dll";
    }

    private static string Id(string member)
    {
        string owner = member[..member.LastIndexOf("::", StringComparison.Ordinal)];
        string name = member[(member.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        return (owner[(owner.LastIndexOf('.') + 1)..] + "-" + name).Replace('_', '-').ToLowerInvariant();
    }

    /// <summary>The id of a form of an overloaded name: the member's own id with the parameter list the form
    /// takes, so two rows of one name are two ids in the audit's messages instead of one.</summary>
    private static string Id(Call call)
    {
        int open = call.Signature.IndexOf('(');
        string parameters = open < 0 ? "" : call.Signature[(open + 1)..].TrimEnd(')');
        return parameters.Length == 0 ? Id(call.Member) : Id(call.Member) + "-" + parameters.Replace('_', '-').ToLowerInvariant();
    }
}

/// <summary>The `dump.cs` the Player, Enemy and Weapon tables read RVAs from, opened only when its bytes are the
/// build the spec froze: an RVA means nothing without the binary it was read out of. An overloaded name is told
/// apart by how many parameters the declaration takes. (The Map table matches the exact parameter types and keeps
/// its own reader for that.)</summary>
internal sealed class Dump
{
    private static readonly Regex RvaLine = new(@"^\s*// RVA: 0x([0-9A-F]+) ", RegexOptions.CultureInvariant);
    private static readonly Regex NamespaceLine = new("^// Namespace: ", RegexOptions.CultureInvariant);
    private readonly string[] _lines;
    private Dump(string[] lines) => _lines = lines;

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
        return new Dump(File.ReadAllLines(path));
    }

    // Il2CppInterop renames a compiler closure `<X>b__N_M` to `_X_b__N_M` in the interop assembly; the dump keeps the
    // angle-bracket form, so the RVA is looked up under the dump's name.
    private static string DumpName(string interopName)
    {
        var match = Regex.Match(interopName, @"^_(.+)_(b__\d+_\d+)$");
        return match.Success ? "<" + match.Groups[1].Value + ">" + match.Groups[2].Value : interopName;
    }

    private static string DumpTypeName(TypeDefinition type)
    {
        if (type.DeclaringType == null) return type.Name;
        var state = Regex.Match(type.Name, @"^_(.+)_(d__\d+)$");
        return DumpTypeName(type.DeclaringType) + "." + (state.Success ? "<" + state.Groups[1].Value + ">" + state.Groups[2].Value : type.Name);
    }

    internal string? Rva(MethodDefinition target)
    {
        // The dump spells a nested type `Outer.Inner` under the outermost type's namespace, and a compiler state machine
        // `Outer.<Name>d__N` where the interop assembly writes `Outer/_Name_d__N`.
        var outermost = target.DeclaringType;
        while (outermost.DeclaringType != null) outermost = outermost.DeclaringType;
        string ns = outermost.Namespace, shortName = DumpTypeName(target.DeclaringType);
        var declaration = new Regex(@"^(public|internal|private|protected)[\w ]* class " + Regex.Escape(shortName) + @"( |$)");
        var method = new Regex(@"\s" + Regex.Escape(DumpName(target.Name)) + @"\(", RegexOptions.CultureInvariant);
        var found = new List<(ulong Rva, int Parameters)>();
        for (int i = 1; i < _lines.Length; i++)
        {
            // Attribute lines (a compiler state machine carries [CompilerGeneratedAttribute]) sit between the namespace line and the declaration.
            int above = i - 1;
            while (above > 0 && _lines[above].StartsWith('[')) above--;
            if (!declaration.IsMatch(_lines[i]) || _lines[above] != "// Namespace: " + ns) continue;
            for (int j = i + 1; j < _lines.Length && !NamespaceLine.IsMatch(_lines[j]); j++)
            {
                if (!method.IsMatch(_lines[j])) continue;
                var match = RvaLine.Match(_lines[j - 1]);
                if (!match.Success) continue;
                var entry = (Convert.ToUInt64(match.Groups[1].Value, 16), Parameters(_lines[j]));
                if (!found.Contains(entry)) found.Add(entry);
            }
        }
        var counted = found.Where(x => x.Parameters == target.Parameters.Count).Select(x => x.Rva).Distinct().ToArray();
        if (counted.Length == 1) return "0x" + counted[0].ToString("X");
        var all = found.Select(x => x.Rva).Distinct().ToArray();
        return all.Length == 1 ? "0x" + all[0].ToString("X") : null;
    }

    /// <summary>How many parameters the dump's own spelling of a method declaration takes, by counting the
    /// top-level commas of its parameter list; generic arguments and array ranks carry commas of their own.</summary>
    private static int Parameters(string line)
    {
        int open = line.IndexOf('(');
        if (open < 0) return -1;
        int depth = 0, commas = 0;
        bool written = false;
        for (int i = open; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']')
            {
                depth--;
                if (depth == 0) return written ? commas + 1 : 0;
            }
            else if (depth == 1 && c == ',') commas++;
            else if (depth == 1 && !char.IsWhiteSpace(c)) written = true;
        }
        return -1;
    }
}
