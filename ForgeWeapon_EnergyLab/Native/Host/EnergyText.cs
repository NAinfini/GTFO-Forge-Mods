using System.Collections.Generic;
using System.IO;
using ForgeUI;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>Every player-visible Energy Lab text. The chat line and the default description come from <c>lang/&lt;language&gt;.forge</c>
/// beside the plugin; each gun's name and description come from the <c>lang</c> folder beside its definition file
/// (<c>&lt;Id&gt;.Name</c>, <c>&lt;Id&gt;.Description</c>), in the language ForgeUI has chosen, else as the file wrote them.</summary>
/// <remarks>The gear packet's public name travels in the game's synced gear packet and in saved loadouts, so it stays the file's
/// <c>Name</c> on every machine; ForgeUI draws the translated name on each player's own screens only (<see cref="Register"/>).</remarks>
internal static class EnergyText
{
    private static readonly UIKitMod Ui = UIKit.Mod(Plugin.Id);
    private static readonly Dictionary<string, UITexts> Files = new();

    /// <summary>Remembers the language files beside the definition file that registered <paramref name="id"/>.</summary>
    internal static void Bind(string id, string folder)
    {
        var languages = Path.Combine(folder, "lang");
        if (Directory.Exists(languages)) Files[id] = UIKit.Texts(languages);
    }

    private static bool Translated(EnergyGear gear, string field, out string text)
    {
        text = "";
        return Files.TryGetValue(gear.Definition.Id, out var texts) && texts.TryText(gear.Definition.Id + "." + field, out text);
    }

    /// <summary>The gun's name in the current language.</summary>
    internal static string Name(EnergyGear gear) => Translated(gear, "Name", out var name) ? name : gear.Name;

    /// <summary>The gun's description in the current language: its translation, else the file's, else the default one.</summary>
    internal static string Description(EnergyGear gear)
        => Translated(gear, "Description", out var text) ? text : gear.Definition.Description ?? Ui.Text("weapon.description");

    /// <summary>The chat line shown when another player's Energy Lab build or tuning differs.</summary>
    internal static string Mismatch(string player) => Ui.Text("chat.mismatch", player);

    /// <summary>Tells ForgeUI to show each gun's stable packet name, and each description, in the player's language on the lobby's gear
    /// slots and weapon window and the in-level weapon list. Safe to call again at every gear installation.</summary>
    /// <remarks>ForgeUI replaces by text, so the rule is: a gun's own <c>&lt;Id&gt;.Name</c> and <c>&lt;Id&gt;.Description</c> keys are always
    /// registered (whether or not its file wrote a <c>Description</c>), the first gun in list order that holds a text claims it, and a
    /// later gun holding the same text (or the default description) is skipped with a line, never refused. Such guns share the claimant's
    /// translation on screens already drawn; the datablocks still carry each gun's own text.</remarks>
    internal static void Register()
    {
        var claimed = new Dictionary<string, string>(System.StringComparer.Ordinal);
        void Claim(EnergyGear gear, UITexts texts, string find, string key, bool packet)
        {
            if (claimed.TryGetValue(find, out var holder))
            {
                if (holder != gear.Definition.Id)
                    Plugin.Logger.ErrorOnce(key + ": the text \"" + find + "\" is already shown for " + holder + "; this gun shares that translation on drawn screens.");
                return;
            }
            claimed.Add(find, gear.Definition.Id);
            if (packet) UIKit.GearName(texts, find, key); else UIKit.GearText(texts, find, key);
        }
        foreach (var gear in EnergyGears.All)
        {
            if (!Files.TryGetValue(gear.Definition.Id, out var texts)) continue;
            if (texts.TryText(gear.Definition.Id + ".Name", out _)) Claim(gear, texts, gear.Name, gear.Definition.Id + ".Name", true);
            if (texts.TryText(gear.Definition.Id + ".Description", out _))
                Claim(gear, texts, gear.Definition.Description ?? Ui.Text("weapon.description"), gear.Definition.Id + ".Description", false);
        }
        var fallback = Ui.Text("weapon.description");
        if (!claimed.ContainsKey(fallback)) UIKit.GearText(Ui.Texts, fallback, "weapon.description");
    }
}
