using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeOrdnance;

/// <summary>The shipped language files: zh and en have the same keys and placeholders, and every key the code asks for exists.</summary>
internal static class LangChecks
{
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[}:,]", RegexOptions.Compiled);
    private static readonly Regex Asked = new(@"(?:\bT|UIText\.Key)\(""([^""]+)""", RegexOptions.Compiled);

    internal static void Run(Action<bool, string> check, [CallerFilePath] string here = "")
    {
        var mod = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../.."));
        var en = Load(Path.Combine(mod, "lang/en.json"));
        var zh = Load(Path.Combine(mod, "lang/zh.json"));
        check(en.Keys.OrderBy(k => k).SequenceEqual(zh.Keys.OrderBy(k => k), StringComparer.Ordinal), "lang: zh.json and en.json have the same keys");
        var mismatched = en.Keys.Where(k => zh.TryGetValue(k, out var text) && Numbers(text) != Numbers(en[k])).ToList();
        check(mismatched.Count == 0, "lang: zh and en texts use the same placeholders (" + string.Join(", ", mismatched) + ")");
        var missing = Directory.GetFiles(Path.Combine(mod, "Native"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Asked.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Where(key => !key.EndsWith('.') && !en.ContainsKey(key)).Distinct().ToList();
        check(missing.Count == 0, "lang: every key the code asks for is in en.json (" + string.Join(", ", missing) + ")");
        foreach (var category in new[] { CategoryRegistry.Supply, CategoryRegistry.Defensive, CategoryRegistry.Weapon, CategoryRegistry.Offensive })
            check(en.ContainsKey("category." + category), "lang: category " + category + " has a name");
        foreach (Rejection reason in Enum.GetValues(typeof(Rejection)))
            check(reason == Rejection.None || en.ContainsKey("reject." + reason), "lang: refusal " + reason + " has a text");
    }

    private static Dictionary<string, string> Load(string path) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static string Numbers(string text) => string.Join(",", Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n));
}
