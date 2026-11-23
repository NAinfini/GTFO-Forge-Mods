using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeEngine.HookEvidence.Enemy;

/// <summary>
/// Regenerates the source references of evidence/enemy-behavior-hooks.json from the built ForgeEngine.dll and its pdb. The
/// table is a reviewed record of build 20403457: its ids, roles, declarations, RVAs, dump lines, offsets and observations are
/// hand-written input this run reads and never rewrites, and a row missing one of them stops the run. What it rewrites is
/// what moves with Forge's own code: where Forge calls a member.
///   - hookMembers[].usedBy in the call-site form (`File.cs:line Method; ...`) and the matching `used by ...` observation;
///   - the `declared by ... at dump.cs N` and `body at 0x...` observations of those generated rows, from the row's own dumpLine and nativeRva;
///   - the callers after `X reads this field` in a readFields note, from the call sites of the getter it names.
/// A usedBy written as prose (a hook attribute, a subscription, "not hooked") is a hand note and is kept as written.
/// </summary>
internal static class EnemyBehaviorEvidence
{
    // The callers that belong to this record: the Enemy domain and the engine's shared game bindings it calls through.
    private static readonly string[] CallerNamespaces = { "ForgeEnemy", "ForgeEngine.GameBindings" };
    private const string NoCaller = "no call site in the Enemy domain or the engine game bindings";
    private static readonly Regex CallSites = new(@"^[A-Za-z0-9_.]+\.cs:\d+ [^\s;]+(; [A-Za-z0-9_.]+\.cs:\d+ [^\s;]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex FieldRead = new(@"^(?<head>(?:.*; )?(?<getter>\S+) reads this field)(?:; .*)?$", RegexOptions.CultureInvariant);

    internal static void Run(string artifacts, string specPath)
    {
        var spec = JsonNode.Parse(File.ReadAllText(specPath))!.AsObject();
        var sites = Sites(Path.Combine(artifacts, "bin/ForgeEngine/release/ForgeEngine.dll"));
        string UsedBy(string type, string member)
            => sites.TryGetValue((Short(type), member), out var found) ? string.Join("; ", found) : NoCaller;

        // Only the generated string values change; the file is rewritten by splicing them into its own text, so the
        // hand-written layout (one-line rows, inline arrays) stays as it is.
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(JsonNode? node, string value) { if ((string?)node != value) replacements[node!.GetPath()] = value; }
        var problems = new List<string>();
        foreach (var row in spec["hookMembers"]!.AsArray().OfType<JsonObject>())
        {
            string id = Required(row, "id", "hookMembers", problems), type = Required(row, "type", id, problems), name = Required(row, "name", id, problems);
            Required(row, "role", id, problems);
            if (row["observations"] is not JsonArray observations) { problems.Add(id + ": observations missing"); continue; }
            string current = Required(row, "usedBy", id, problems);
            string generated = UsedBy(type, name);
            if (current != NoCaller && !CallSites.IsMatch(current)) continue;
            Set(row["usedBy"], generated);
            if (observations.Count == 3 && ((string?)observations[0] ?? "").StartsWith("declared by `", StringComparison.Ordinal)
                && ((string?)observations[1] ?? "").StartsWith("body at ", StringComparison.Ordinal)
                && ((string?)observations[2] ?? "").StartsWith("used by ", StringComparison.Ordinal))
            {
                string owner = id[..id.LastIndexOf("." + name, StringComparison.Ordinal)];
                Set(observations[0], "declared by `" + owner + "` at dump.cs " + Required(row, "dumpLine", id, problems));
                Set(observations[1], "body at " + Required(row, "nativeRva", id, problems));
                Set(observations[2], "used by " + generated);
            }
        }
        foreach (var row in spec["readFields"]!.AsArray().OfType<JsonObject>())
        {
            string type = Required(row, "type", "readFields", problems);
            string declaration = Required(row, "declaration", type, problems);
            Required(row, "offset", type + " " + declaration, problems);
            var match = FieldRead.Match(Required(row, "note", type + " " + declaration, problems));
            if (!match.Success) continue;
            string getter = match.Groups["getter"].Value;
            string generated = UsedBy(type, getter[(getter.LastIndexOf('.') + 1)..]);
            Set(row["note"], match.Groups["head"].Value + (generated == NoCaller ? "" : "; " + generated));
        }
        if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems));
        File.WriteAllBytes(specPath, Splice(File.ReadAllBytes(specPath), replacements));
        Console.WriteLine("Regenerated enemy behaviour source references: " + specPath + " values changed=" + replacements.Count);
    }

    /// <summary>Replaces the string values at the given paths (JsonNode.GetPath spelling) in the document's own bytes.</summary>
    private static byte[] Splice(byte[] text, IReadOnlyDictionary<string, string> replacements)
    {
        var edits = new List<(int Start, int Length, byte[] Value)>();
        var encode = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var containers = new Stack<(string Path, bool Array, int Index)>();
        string property = "";
        var reader = new Utf8JsonReader(text);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName) { property = reader.GetString()!; continue; }
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { containers.Pop(); continue; }
            string path = "$";
            if (containers.Count > 0)
            {
                var parent = containers.Pop();
                path = parent.Array ? parent.Path + "[" + parent.Index + "]" : parent.Path + "." + property;
                containers.Push(parent with { Index = parent.Index + 1 });
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                containers.Push((path, reader.TokenType == JsonTokenType.StartArray, 0));
            else if (reader.TokenType == JsonTokenType.String && replacements.TryGetValue(path, out var value))
                edits.Add(((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, encode))));
        }
        if (edits.Count != replacements.Count) throw new InvalidOperationException("Not every regenerated value was found in the table text.");
        var output = new List<byte>(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            output.RemoveRange(edit.Start, edit.Length);
            output.InsertRange(edit.Start, edit.Value);
        }
        return output.ToArray();
    }

    private static string Required(JsonObject row, string key, string where, List<string> problems)
    {
        if (row[key] is JsonValue value) return value.ToJsonString().Trim('"');
        problems.Add(where + ": " + key + " missing");
        return "";
    }

    private static string Short(string type) => type[(Math.Max(type.LastIndexOf('.'), type.LastIndexOf('/')) + 1)..];

    /// <summary>Every call of a game member from the caller namespaces, keyed by the callee's type short name and member name, as
    /// `File.cs:line Method` per calling method (its first call), sorted by method. A lambda or iterator is reported under the
    /// method the compiler moved it out of.</summary>
    private static Dictionary<(string Type, string Member), string[]> Sites(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { ReadSymbols = true });
        var found = new Dictionary<(string, string), SortedSet<(string Method, string File, int Line)>>();
        foreach (var type in assembly.MainModule.Types
                     .Where(t => CallerNamespaces.Any(ns => t.Namespace == ns || t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal))).SelectMany(Walk))
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var seen = new HashSet<(string, string)>();
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn) || instruction.Operand is not MethodReference callee) continue;
                    if (callee.DeclaringType.Scope is not AssemblyNameReference scope || scope.Name == assembly.Name.Name) continue;
                    var key = (Short(callee.DeclaringType.GetElementType().FullName), callee.Name);
                    if (!seen.Add(key)) continue;
                    var point = Point(method, instruction);
                    if (point == null) continue;
                    if (!found.TryGetValue(key, out var set)) found[key] = set = new SortedSet<(string, string, int)>();
                    set.Add((UserMethod(method), Path.GetFileName(point.Document.Url), point.StartLine));
                }
            }
        return found.ToDictionary(p => p.Key, p => p.Value.GroupBy(s => (s.Method, s.File)).Select(g => g.Key.File + ":" + g.Min(s => s.Line) + " " + g.Key.Method).ToArray());
    }

    private static SequencePoint? Point(MethodDefinition method, Instruction instruction)
    {
        var points = method.DebugInformation.SequencePoints;
        SequencePoint? best = null;
        foreach (var point in points)
            if (point.Offset <= instruction.Offset && !point.IsHidden) best = point;
        return best;
    }

    // `<Place>b__3_0` and `<Run>d__5.MoveNext` belong to Place and Run.
    private static string UserMethod(MethodDefinition method)
    {
        var name = Regex.Match(method.Name, @"^<([^>]+)>");
        if (name.Success) return name.Groups[1].Value.Split('|')[0];
        for (var type = method.DeclaringType; type != null; type = type.DeclaringType)
        {
            var owner = Regex.Match(type.Name, @"^<([^>]+)>d__");
            if (owner.Success) return owner.Groups[1].Value;
        }
        return method.Name;
    }

    private static IEnumerable<TypeDefinition> Walk(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Walk));
}
