using System.Runtime.CompilerServices;
using CellMenu;
using ForgeUI;
using TMPro;
using UnityEngine;

namespace ForgeUI
{
    /// <summary>The test stand-in for the plugin's language file lookup: in-memory folders, and the kit's own <c>lang</c> folder read
    /// from the repository so the shipped files are checked too.</summary>
    internal static class Language
    {
        internal static readonly Dictionary<string, List<KeyValuePair<string, string>>> Folders = new();

        internal static UITexts ForPlugin(string modId) =>
            modId == Plugin.Id ? InFolder(modId, KitFolder()) : InFolder(modId, "plugins/" + modId + "/lang");

        internal static UITexts InFolder(string owner, string folder) => new(owner, () =>
        {
            if (Folders.TryGetValue(folder, out var files)) return files;
            if (!Directory.Exists(folder)) return Array.Empty<KeyValuePair<string, string>>();
            return Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new KeyValuePair<string, string>(Path.GetFileName(p), File.ReadAllText(p))).ToList();
        }, Plugin.Error);

        private static string KitFolder([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../lang"));
    }
}

internal static class TextChecks
{
    private static void Folder(string mod, params (string File, string Json)[] files) =>
        Language.Folders["plugins/" + mod + "/lang"] = files.Select(f => new KeyValuePair<string, string>(f.File, f.Json)).ToList();

    internal static void Run(Action<bool, string> check)
    {
        UITexts.Changed += UIKit.LanguageSwitched;
        UITexts.SetLanguage("en");

        // The kit's own files ship with the package: both languages load, match each other and log nothing wrong.
        Plugin.Errors.Clear();
        var kit = UIKit.Kit.Texts;
        check(kit.Text("picker.all") == "ALL" && kit.Languages.Contains("zh") && kit.Languages.Contains("en"), "the kit's own language files load");
        check(Plugin.Errors.Count == 0, "the kit's own zh and en files have the same keys and placeholders");

        Folder("Lang.Mod",
            ("en.json", "{ \"title\": \"ORDNANCE\", \"count\": \"{0} of {1}\", \"only.en\": \"ENGLISH\", \"place\": \"{0} left\" }"),
            ("zh.json", "{ \"title\": \"呼叫\", \"count\": \"{0}/{1}\", \"place\": \"剩余\", \"only.zh\": \"多余\", \"title\": \"重复\" }"),
            ("broken.json", "{ \"a\": 1 }"));
        var mod = UIKit.Mod("Lang.Mod");
        Plugin.Errors.Clear();
        var english = mod.Text("title");
        check(english == "ORDNANCE", "loads on first use and returns the current language's text");
        check(Plugin.Errors.Any(e => e.Contains("zh.json") && e.Contains("'title' appears more than once")), "a repeated key is logged");
        check(Plugin.Errors.Any(e => e.Contains("broken.json") && e.Contains("not a valid language file")), "a file that is not key → text is logged and not used");
        check(Plugin.Errors.Any(e => e.Contains("zh.json: missing 1") && e.Contains("only.en")), "a key missing from a language is logged");
        check(Plugin.Errors.Any(e => e.Contains("zh.json: 1 text(s)") && e.Contains("only.zh")), "an extra key is logged");
        check(Plugin.Errors.Any(e => e.Contains("'place' has placeholders none but en.json has {0}")), "different placeholders are logged");
        check(!Plugin.Errors.Any(e => e.Contains("'count'")), "the same placeholders written differently are not a mismatch");
        check(ReferenceEquals(mod.Text("title"), english), "a text without arguments is the cached string, not a new one");
        check(mod.Text("count", 2, 4) == "2 of 4", "placeholders are filled from the arguments");

        check(UITexts.SetLanguage("ZH ") && UIKit.Language == "zh" && !UITexts.SetLanguage("zh"), "a language code is trimmed and lower-cased; setting it again is no change");
        check(mod.Text("title") == "呼叫" && mod.Text("count", 2, 4) == "2/4", "after the switch texts come from the new language");
        Plugin.Errors.Clear();
        check(mod.Text("only.en") == "ENGLISH" && mod.Text("only.en") == "ENGLISH", "a key missing from the current language shows the en text");
        check(Plugin.Errors.Count(e => e.Contains("'only.en' has no zh text")) == 1, "the fallback is logged once per key");
        check(mod.Text("nowhere") == "nowhere" && Plugin.Errors.Count(e => e.Contains("'nowhere' has no text in zh or en")) == 1, "a key in no language shows the key itself, logged");
        check(mod.Texts.TryText("only.zh", out var extra) && extra == "多余" && !mod.Texts.TryText("nowhere.else", out _), "TryText finds optional keys and stays quiet without them");

        UITexts.SetLanguage("fr");
        check(mod.Text("title") == "ORDNANCE", "a language without a file shows en");

        // The chain is current → same language without its region or script → en → key.
        Folder("Near.Mod",
            ("en.json", "{ \"a\": \"A\", \"b\": \"B\", \"c\": \"C\" }"),
            ("zh.json", "{ \"a\": \"甲\", \"b\": \"乙\", \"c\": \"丙\" }"),
            ("zh-hant.json", "{ \"a\": \"甲繁\", \"b\": \"乙繁\", \"c\": \"丙繁\" }"),
            ("pt.json", "{ \"a\": \"A-pt\", \"b\": \"B-pt\", \"c\": \"C-pt\" }"));
        var near = UIKit.Mod("Near.Mod");
        Folder("Near.Gap", ("en.json", "{ \"a\": \"A\", \"b\": \"B\" }"), ("zh.json", "{ \"a\": \"甲\", \"b\": \"乙\" }"),
            ("zh-hant.json", "{ \"a\": \"甲繁\" }"));
        var gap = UIKit.Mod("Near.Gap");
        UITexts.SetLanguage("zh-hant");
        Plugin.Errors.Clear();
        check(near.Text("a") == "甲繁", "a language with its own file uses it");
        check(gap.Text("b") == "乙" && Plugin.Errors.Count(e => e.Contains("'b' has no zh-hant text; showing the zh text")) == 1, "zh-hant falls back to zh before en, logged once");
        UITexts.SetLanguage("pt-br");
        check(near.Text("b") == "B-pt", "pt-br without a file shows pt");
        check(gap.Text("a") == "A", "without the near relative the chain goes on to en");
        check(UITexts.Relative("zh-hant") == "zh" && UITexts.Relative("pt-br") == "pt" && UITexts.Relative("en") == "", "the near relative drops the region or script");

        check(UITexts.Placeholders("{0} {{1}} {2:0.0} {3,4} {x}") == 0b1101, "placeholders count {n}, {n:format} and {n,width} but not {{n}}");
        check(UITexts.GameLanguageCode("Chinese_Simplified") == "zh" && UITexts.GameLanguageCode("English") == "en"
              && UITexts.GameLanguageCode("Chinese_Traditional") == "zh-hant" && UITexts.GameLanguageCode("Klingon") == null,
            "the game's languages map to codes");

        // A key given to a registration follows the language; a plain string never changes.
        UITexts.SetLanguage("en");
        var key = UIText.Key("title");
        UIText plain = "AS WRITTEN";
        check(key.In(mod.Texts) == "ORDNANCE" && ReferenceEquals(key.In(mod.Texts), key.In(mod.Texts)) && plain.In(mod.Texts) == "AS WRITTEN",
            "a key resolves once per language; a plain string is shown as written");
        check(key.In(null) == "title", "a key with no language files shows the key");

        var bar = UiChecks.Lobby();
        var button = mod.AddLobbyButton("Switch", UIText.Key("title"), _ => { })!;
        var menu = UiChecks.Bar();
        menu.m_popupWindowPrefab = new GameObject
        {
            Factory = () =>
            {
                var window = UiChecks.Window(new GameObject().transform);
                var header = UiChecks.Child<CM_ScrollWindowHeader>(window.transform, "Header");
                var heading = UiChecks.Child<TextMeshPro>(header.transform, "Text");
                heading.gameObject.Factory = () => { var copy = new GameObject(); copy.AddComponent<TextMeshPro>(); return copy; };
                window.m_headers = new[] { header };
                return window.gameObject;
            }
        };
        MenuBarPage? built = null;
        var builds = 0;
        var page = mod.AddMenuBarPage("SwitchPage", UIText.Key("title"), p => { built = p; builds++; p.AddText(UIText.Key("count", 1, 2)); })!;
        LobbyButtons.Update();
        MenuBar.Update();
        Time.frameCount++;
        UiChecks.Press(UiChecks.CustomItem(menu, page.Id));
        var englishPage = built!;
        check(bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "ORDNANCE"), "a lobby button shows its key's text");
        check(englishPage.IsOpen && englishPage.Root.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "1 of 2"), "a page shows its keys' texts");

        var listened = 0;
        Action listener = () => listened++;
        UIKit.LanguageChanged += listener;
        UITexts.SetLanguage("zh");
        LobbyButtons.Update();
        MenuBar.Update();
        check(bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "呼叫") && !bar.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "ORDNANCE"),
            "after a language change the lobby button shows the new language on the next frame");
        check(UiChecks.CustomItem(menu, page.Id).m_text!.text == "呼叫", "the menu bar button title is written again");
        check(builds == 2 && !englishPage.IsOpen && built!.IsOpen && built.Root.GetComponentsInChildren<TextMeshPro>().Any(t => t.text == "1/2"),
            "an open page is built again in the new language and stays open");
        check(listened == 1, "mods' language listeners run once per change");
        UIKit.LanguageChanged -= listener;

        button.Remove();
        page.Remove();
        MenuBar.Update();
        UITexts.SetLanguage("en");
        UITexts.Changed -= UIKit.LanguageSwitched;
    }
}
