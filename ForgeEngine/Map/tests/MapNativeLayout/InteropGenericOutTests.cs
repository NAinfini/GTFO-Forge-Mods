using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeMap.Tests.MapNativeLayout;

// Reads only checked-in files (call evidence and plugin sources), so it runs without the game's interop assemblies.
public sealed class InteropGenericOutTests
{
    // A game member that returns a collection through an out/ref parameter is generated with a constructor of an open
    // Il2CppSystem generic scoped to Modules-ASM. The runtime cannot load that type, so the first call throws
    // TypeLoadException. Such a member is never called through its wrapper: read the field it answers from, or invoke
    // the native method and wrap the returned pointer in the closed type (FixedEnemyPlacement.TryGetPlacements).
    private static readonly Regex ByRefGeneric = new(@"Il2CppSystem\.Collections\.Generic\.\w+`\d+<[^&]*>&", RegexOptions.CultureInvariant);

    // Members known to be unloadable that no evidence file declares (they are reached only from plugin sources).
    private static readonly string[] KnownUnloadable = { "TryGetCommandEvents" };

    private static string ModsRoot([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../.."));

    private static IEnumerable<string> EvidenceFiles()
        => Directory.EnumerateFiles(ModsRoot(), "*.json", SearchOption.AllDirectories)
            .Where(path => path.Contains(Path.DirectorySeparatorChar + "evidence" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static IEnumerable<string> Signatures(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name == "signature" && property.Value.ValueKind == JsonValueKind.String) yield return property.Value.GetString()!;
                else foreach (var inner in Signatures(property.Value)) yield return inner;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) foreach (var inner in Signatures(item)) yield return inner;
    }

    // Every hook-evidence row of every base package that records a call site.
    [Fact]
    public void no_game_member_is_called_through_an_unloadable_generic_out_parameter()
    {
        var offenders = new List<string>();
        foreach (var path in EvidenceFiles().Where(path => Path.GetFileName(path).Contains("hooks", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in hooks.EnumerateArray())
                if (row.TryGetProperty("callSites", out var sites) && sites.ValueKind == JsonValueKind.Array && sites.GetArrayLength() > 0
                    && row.TryGetProperty("signature", out var signature) && ByRefGeneric.IsMatch(signature.GetString() ?? ""))
                    offenders.Add(Path.GetFileName(path) + ": " + signature.GetString());
        }
        Assert.True(offenders.Count == 0, "Called through an unloadable wrapper: " + string.Join(" | ", offenders));
    }

    // Packages whose plugin code has no per-call evidence (ForgeDevelopment, for one) are covered by reading their
    // sources: any call of an unloadable member that passes an out argument fails.
    [Fact]
    public void no_plugin_source_calls_an_unloadable_member_with_an_out_argument()
    {
        var names = new HashSet<string>(KnownUnloadable, StringComparer.Ordinal);
        foreach (var path in EvidenceFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var signature in Signatures(document.RootElement).Where(signature => ByRefGeneric.IsMatch(signature)))
            {
                int colon = signature.IndexOf("::", StringComparison.Ordinal), paren = signature.IndexOf('(');
                if (colon >= 0 && paren > colon) names.Add(signature[(colon + 2)..paren]);
            }
        }
        var call = new Regex(@"\.(" + string.Join("|", names.Select(Regex.Escape)) + @")\s*\([^;]*\bout\b", RegexOptions.CultureInvariant);
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(ModsRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains(sep + "Native" + sep, StringComparison.Ordinal)
                && !path.Contains(sep + "obj" + sep, StringComparison.Ordinal) && !path.Contains(sep + "bin" + sep, StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (path, line, index))
                .Where(row => !row.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && call.IsMatch(row.line))
                .Select(row => Path.GetRelativePath(ModsRoot(), row.path) + ":" + (row.index + 1)))
            .ToArray();
        Assert.True(offenders.Length == 0, "Plugin source calls an unloadable member with an out argument: " + string.Join(" | ", offenders));
    }
}
