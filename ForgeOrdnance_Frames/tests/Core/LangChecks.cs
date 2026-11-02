using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeOrdnanceFrames;

/// <summary>The shipped language files: Frames' own (<c>lang/</c>) and the definitions' (<c>Definitions/lang/</c>, installed as
/// <c>ForgeOrdnance/lang/</c>) have the same keys and placeholders in zh and en, every word the suit asks for exists, and every suit has a name and description in both languages.</summary>
internal static class LangChecks
{
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[}:,]", RegexOptions.Compiled);
    private static readonly Regex Asked = new(@"\btext\(""([^""]+)""\)", RegexOptions.Compiled);

    internal static string Mod([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../.."));

    internal static Dictionary<string, string> Load(string relative) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Mod(), relative)))!;

    /// <summary>The suit's words in a shipped language, as the native side makes them.</summary>
    internal static FrameWords Words(string language = "en")
    {
        var texts = Load("lang/" + language + ".json");
        return new FrameWords(key => texts.TryGetValue(key, out var text) ? text : key);
    }

    internal static void Run(Action<bool, string> check, IEnumerable<FrameSpec> models)
    {
        foreach (var folder in new[] { "lang", "Definitions/lang" })
        {
            var en = Load(folder + "/en.json");
            var zh = Load(folder + "/zh.json");
            check(en.Keys.OrderBy(k => k).SequenceEqual(zh.Keys.OrderBy(k => k), StringComparer.Ordinal), folder + ": zh.json and en.json have the same keys");
            var mismatched = en.Keys.Where(k => zh.TryGetValue(k, out var text) && Numbers(text) != Numbers(en[k])).ToList();
            check(mismatched.Count == 0, folder + ": zh and en texts use the same placeholders (" + string.Join(", ", mismatched) + ")");
        }
        var own = Load("lang/en.json");
        var asked = Asked.Matches(File.ReadAllText(Path.Combine(Mod(), "Core/FrameWords.cs"))).Select(m => m.Groups[1].Value).ToList();
        var missing = asked.Where(key => !own.ContainsKey(key)).ToList();
        check(asked.Count > 30 && missing.Count == 0, "lang: every word the suit asks for is in en.json (" + string.Join(", ", missing) + ")");
        check(own.ContainsKey("category.Exosuit") && File.ReadAllText(Path.Combine(Mod(), "Native/Plugin.cs")).Contains("Text(\"category.Exosuit\")"), "lang: the Exosuit tab name Frames registers with the loadout is in en.json");
        // The key action names and their heading in the game's settings (FrameKeys registers them with ForgeUI).
        var keys = Regex.Matches(File.ReadAllText(Path.Combine(Mod(), "Native/FrameKeys.cs")), @"""(keys\.[^""]+)""").Select(m => m.Groups[1].Value).ToList();
        check(keys.Count == 4 && keys.All(own.ContainsKey), "lang: every key action name and its heading are in en.json");
        var unused = own.Keys.Where(k => !asked.Contains(k) && !keys.Contains(k) && k != "category.Exosuit").ToList();
        check(unused.Count == 0, "lang: no word is shipped that nothing asks for (" + string.Join(", ", unused) + ")");
        var names = Load("Definitions/lang/zh.json");
        using var definitions = JsonDocument.Parse(File.ReadAllText(Path.Combine(Mod(), "Definitions/frames.json")));
        var untranslated = definitions.RootElement.EnumerateArray().Select(d => d.GetProperty("Id").GetString()!)
            .Where(id => !names.ContainsKey(id + ".Name") || !names.ContainsKey(id + ".Description")).ToList();
        check(untranslated.Count == 0, "Definitions/lang: every suit has a name and description (" + string.Join(", ", untranslated) + ")");
        var zhWords = Words("zh");
        check(zhWords.Lamp == "头灯" && Words().Lamp == "LAMP" && FrameWords.Format(zhWords.BoardHold, "E") == "按住 E 登乘", "the suit's words follow the language");
    }

    private static string Numbers(string text) => string.Join(",", Placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n));
}
