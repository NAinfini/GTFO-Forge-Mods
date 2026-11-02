using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeOrdnanceArsenal.Tests;

/// <summary>The shipped language files: Arsenal's own and the definitions' have the same keys and placeholders in zh and en, every key
/// the code asks for exists, and every call-in in <c>arsenal.json</c> has a name and description in both languages.</summary>
internal static class LangChecks
{
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[}:,]", RegexOptions.Compiled);
    private static readonly Regex Asked = new(@"\bT\(""([^""]+)""", RegexOptions.Compiled);

    internal static void Run(Action<bool, string> check, [CallerFilePath] string here = "")
    {
        var mod = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../.."));
        foreach (var folder in new[] { "lang", "ForgeOrdnance/lang" })
        {
            var en = Load(Path.Combine(mod, folder, "en.json"));
            var zh = Load(Path.Combine(mod, folder, "zh.json"));
            check(en.Keys.OrderBy(k => k).SequenceEqual(zh.Keys.OrderBy(k => k), StringComparer.Ordinal), folder + ": zh.json and en.json have the same keys");
            var mismatched = en.Keys.Where(k => zh.TryGetValue(k, out var text) && Numbers(text) != Numbers(en[k])).ToList();
            check(mismatched.Count == 0, folder + ": zh and en texts use the same placeholders (" + string.Join(", ", mismatched) + ")");
        }
        var own = Load(Path.Combine(mod, "lang/en.json"));
        var missing = Directory.GetFiles(Path.Combine(mod, "Native"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Asked.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Where(key => !own.ContainsKey(key)).Distinct().ToList();
        check(missing.Count == 0, "lang: every key the code asks for is in en.json (" + string.Join(", ", missing) + ")");
        var names = Load(Path.Combine(mod, "ForgeOrdnance/lang/zh.json"));
        // The in-level weapon list draws each lent heavy weapon under these call-ins' names (ArsenalText.NameHeavyWeapon).
        var weaponIds = new[] { "arsenal.weapon.machinegun", "arsenal.weapon.antimateriel", "arsenal.weapon.grenadelauncher" };
        var enNames = Load(Path.Combine(mod, "ForgeOrdnance/lang/en.json"));
        check(weaponIds.All(id => names.ContainsKey(id + ".Name") && enNames.ContainsKey(id + ".Name")), "ForgeOrdnance/lang: every heavy weapon has a name in both languages");
        check(ForgeOrdnanceArsenal.HeavyModels.Marker(uint.MaxValue).Length <= 25, "the heavy weapons' packet names fit the 25-character gear name");
        using var definitions =JsonDocument.Parse(File.ReadAllText(Path.Combine(mod, "ForgeOrdnance/arsenal.json")));
        var heavy = definitions.RootElement.EnumerateArray().Select(d => d.GetProperty("Payload")).Where(p => p.GetProperty("Type").GetString() == "arsenal.HeavyWeapon").ToList();
        check(heavy.Count == 3 && heavy.All(p => p.GetProperty("Model").GetString()!.StartsWith("Arsenal:") && ForgeOrdnanceArsenal.HeavyModels.IsId(p.GetProperty("Model").GetString())), "arsenal.json: the three heavy weapons name their model Arsenal:item");
        var untranslated = definitions.RootElement.EnumerateArray().Select(d => d.GetProperty("Id").GetString()!)
            .Where(id => !names.ContainsKey(id + ".Name") || !names.ContainsKey(id + ".Description")).ToList();
        check(untranslated.Count == 0, "ForgeOrdnance/lang: every call-in has a name and description (" + string.Join(", ", untranslated) + ")");
    }

    private static Dictionary<string, string> Load(string path) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static string Numbers(string text) => string.Join(",", Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n));
}
