using ForgeUI;

/// <summary>Gear names: registration, which texts match, and the language a name is shown in.</summary>
internal static class GearNameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Language.Folders["plugins/Gear.Mod/lang"] = new List<KeyValuePair<string, string>>
        {
            new("en.json", "{ \"pulse\": \"Pulse Rifle\", \"beam\": \"Beam Gun\", \"desc\": \"A prototype weapon.\", \"only.en\": \"Only English\" }"),
            new("zh.json", "{ \"pulse\": \"脉冲步枪\", \"beam\": \"光束枪\", \"desc\": \"原型武器。\", \"only.en\": \"仅英文\" }"),
        };
        Language.Folders["plugins/Other.Mod/lang"] = new List<KeyValuePair<string, string>> { new("en.json", "{ \"pulse\": \"Other Pulse\" }") };
        var texts = UIKit.Mod("Gear.Mod").Texts;
        UITexts.SetLanguage("en");

        var book = new GearNameBook();
        check(book.IsEmpty && !book.HasPackets && book.Display("Pulse Rifle") == "Pulse Rifle", "an empty book changes nothing");

        // Registration: a text and a packet name; an empty text or key is refused.
        check(book.Add(texts, "Energy Pulse", texts, "pulse", true) == null, "register a gear by its packet name");
        check(book.Add(texts, "Energy Beam", texts, "beam", false) == null, "register a gear by a text");
        check(book.Add(texts, "", texts, "pulse", true) != null && book.Add(texts, "x", texts, "", true) != null, "an empty text or key is refused");
        check(book.HasPackets && book.ForPacket("Energy Pulse") != null && book.ForPacket("Energy Beam") == null && book.ForPacket(null) == null,
            "only a gear registered by packet name is recognised by it");

        // Matching: a text holding a registered text is rewritten wherever it sits; any other text is the very same string.
        check(book.Display("Energy Pulse") == "Pulse Rifle", "the whole text is replaced");
        check(book.Display("[Energy Beam] ready") == "[Beam Gun] ready", "a registered text inside a longer one is replaced");
        var other = new string("Vanilla Rifle".ToCharArray());
        check(ReferenceEquals(book.Display(other), other) && book.Display(null) == "" && book.Display("") == "", "an unregistered text comes back unchanged");
        check(book.Display("Pulse Rifle") == "Pulse Rifle", "a text already in the current language is left alone");

        // Language: the name follows the local language; screens drawn earlier in another language are translated too.
        var drawnEnglish = book.Display("Energy Pulse");
        UITexts.SetLanguage("zh");
        check(book.ForPacket("Energy Pulse")!.Shown == "脉冲步枪" && book.Display("Energy Pulse") == "脉冲步枪", "the name is resolved in the local language");
        check(book.Display(drawnEnglish) == "脉冲步枪" && book.Display("Beam Gun") == "光束枪", "a text drawn in the earlier language follows the language");
        UITexts.SetLanguage("fr");
        check(book.Display("Energy Pulse") == "Pulse Rifle" && book.Display("脉冲步枪") == "Pulse Rifle", "a language with no file shows the English text");
        UITexts.SetLanguage("zh");

        // Owners: the same owner registering again keeps what it learnt; another owner cannot take a registered text.
        check(book.Add(texts, "Energy Pulse", texts, "pulse", true) == null && book.Display("Pulse Rifle") == "脉冲步枪", "registering again keeps the earlier languages");
        var rival = UIKit.Mod("Other.Mod").Texts;
        check(book.Add(rival, "Energy Pulse", rival, "pulse", true) != null && book.Display("Energy Pulse") == "脉冲步枪", "another owner's registration of the same text is refused");

        // A description registered as a text follows the language as well.
        check(book.Add(texts, "A prototype weapon.", texts, "desc", false) == null && book.Display("A prototype weapon.") == "原型武器。", "a description registered as a text is translated");
        UITexts.SetLanguage("en");
        check(book.Display("原型武器。") == "A prototype weapon." && book.Display("脉冲步枪") == "Pulse Rifle", "switching back restores every English text");

        // A name given by a function follows the language the same way and is read again after each change.
        var name = "Live Name";
        check(book.Add(texts, "Live Packet", () => name, true) == null && book.Display("Live Packet") == "Live Name", "a name given by a function is shown");
        name = "Live Nom";
        UITexts.SetLanguage("zh");
        check(book.Display("Live Packet") == "Live Nom" && book.Display("Live Name") == "Live Nom", "the function runs again after a language change");
        UITexts.SetLanguage("en");
    }
}
