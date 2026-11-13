using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using ForgeCore;
using ForgeEmote.Api;
using ForgeUI;
using UnityEngine;

namespace ForgeEmote;

/// <summary>The wheel: its key and pages. ForgeUI draws it and handles the key; this only fills it and starts what is chosen.</summary>
internal static class EmoteWheel
{
    private static HudWheel? _wheel;
    private static readonly List<(WheelItem Item, EmoteDefinition Definition)> Items = new();
    private static PlayerMotion _lastMotion = PlayerMotion.Empty;

    /// <summary>The wheel is reading the mouse and the keys, or the button that closed it is still down: what those do belongs to the wheel.</summary>
    internal static bool HoldsInput => _wheel is { HoldsInput: true };

    /// <summary>The wheel is up now: the only time its greying needs the player's motion.</summary>
    internal static bool IsOpen => _wheel is { IsOpen: true };

    private static readonly List<string> Favourites = new(), Recent = new(), PageKeys = new();

    /// <summary>What the player keeps beside the wheel's own file: the starred actions and the last ones used, in ForgeCore's local store (so <c>wheel.forge</c> stays the player's to write).</summary>
    private sealed class Memory
    {
        public List<string>? Favourites { get; set; }
        public List<string>? Recent { get; set; }
    }

    private static void LoadMemory()
    {
        if (Store.Read<Memory>(Plugin.Id) is not { Status: StoreStatus.Read, Value: { } memory }) return;
        Favourites.AddRange(memory.Favourites ?? new List<string>());
        Recent.AddRange(memory.Recent ?? new List<string>());
    }

    // The file is written once the wheel is down, never from inside a choice made on it.
    private static bool _unsaved;

    /// <summary>Writes the stars and the recent actions if they changed and the wheel is not up; called every frame.</summary>
    internal static void Flush()
    {
        if (!_unsaved || IsOpen) return;
        _unsaved = false;
        Store.Write(Plugin.Id, new Memory { Favourites = Favourites, Recent = Recent });
    }

    /// <summary>An action was started: it goes to the front of the recent ones.</summary>
    internal static void Used(string id)
    {
        if (!EmoteRules.Remember(Recent, id)) return;
        _unsaved = true;
        Fill();
    }

    /// <summary>Stars or unstars an action (the wheel's second button and <see cref="EmoteControl"/>); false when a page of stars is full.</summary>
    internal static bool Toggle(string id)
    {
        if (!EmoteRules.Toggle(Favourites, id, Installed)) { Plugin.Logger.ErrorOnce("ForgeEmote: the favourites page holds " + EmoteRules.SlotsPerPage + " actions; take one off first."); return false; }
        _unsaved = true;
        Fill();
        return true;
    }

    private static bool Installed(string id) => Emotes.Find(id) != null;

    internal static bool IsFavourite(string id) => Favourites.Contains(id);

    internal static IReadOnlyList<string> Starred => Favourites;

    internal static void Load()
    {
        var ui = UIKit.Mod(Plugin.Id);
        // Without the key there is no wheel, but watching and syncing the others' actions goes on (as when the wheel itself is refused).
        var key = ui.AddKeyAction("Wheel", "keys.wheel", "keys.group", "H", KeyMode.Hold, "on-foot");
        if (key == null) { Plugin.Logger.Error("ForgeEmote: ForgeUI refused the wheel key; the wheel is off (see its error)."); return; }
        _wheel = ui.AddWheel("Wheel", key);
        if (_wheel == null) return;
        _wheel.Chosen += item => { if (Emotes.Find(item.Id) is { } entry) EmoteRunner.Start(entry); };
        _wheel.Secondary += item => Toggle(item.Id);
        LoadMemory();
        ReadPlayerFile();
        _wheel.CanOpen = static () => Players.Local is { Alive: true };
        Emotes.Changed += Fill;
        UIKit.LanguageChanged += Fill;
        Fill();
    }

    // The player's own page file (page name to action IDs), read once when the wheel loads: its pages, and what was wrong with it.
    private static Dictionary<string, string[]>? _playerPages;
    private static string? _playerProblem;

    private static void ReadPlayerFile()
    {
        var path = Path.Combine(Paths.ConfigPath, "ForgeEmote", "wheel.forge");
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new StrictJsonException("", "must be an object of page names to action lists.");
            StrictJson.UniqueMembers(root);
            var pages = new Dictionary<string, string[]>();
            foreach (var page in root.EnumerateObject())
            {
                if (page.Value.ValueKind != JsonValueKind.Array) throw new StrictJsonException(page.Name, "must be a list of action IDs.");
                var ids = new List<string>();
                foreach (var id in page.Value.EnumerateArray())
                    ids.Add(id.ValueKind == JsonValueKind.String ? id.GetString()! : throw new StrictJsonException(page.Name, "must hold only action IDs (text)."));
                pages[page.Name] = ids.ToArray();
            }
            _playerPages = pages;
        }
        catch (StrictJsonException problem) { _playerProblem = path + ": " + (problem.Field.Length == 0 ? "" : problem.Field + " ") + problem.Message; }
        catch (Exception problem) when (problem is IOException or JsonException) { _playerProblem = path + ": " + problem.Message; }
    }

    private static void Fill()
    {
        if (_wheel == null) return;
        var problems = new List<string>();
        if (_playerProblem != null) problems.Add(_playerProblem);
        // The favourites page appears and goes, shifting the others: the wheel keeps the page it was on, not its number.
        var key = _wheel.Page < PageKeys.Count ? PageKeys[_wheel.Page] : null;
        var definitions = new List<EmoteDefinition>();
        foreach (var entry in Emotes.Entries) definitions.Add(entry.Definition);
        var pages = new List<WheelPage>();
        Items.Clear();
        PageKeys.Clear();
        _lastMotion = PlayerMotion.Empty;
        WheelItem[] Make(IEnumerable<EmoteDefinition> chosen)
        {
            var items = new List<WheelItem>();
            foreach (var definition in chosen)
            {
                var entry = Emotes.Find(definition.Id)!;
                Sprite? icon = definition.Icon == null ? null : UIKit.Icon(definition.Icon);
                var item = new WheelItem(definition.Id, entry.Texts.Text(definition.Name), icon);
                items.Add(item);
                Items.Add((item, definition));
            }
            return items.ToArray();
        }
        var starred = EmoteRules.FavouritePage(Favourites, Recent, Installed);
        if (starred.Count > 0)
        {
            pages.Add(new WheelPage(UIText.Key("page.favorites"), Make(starred.Select(id => Emotes.Find(id)!.Definition))));
            PageKeys.Add("favorites");
        }
        foreach (var (page, chosen) in EmoteRules.Arrange(definitions, _playerPages, problems))
        {
            var title = page is "tactical" or "gesture" or "dance" ? UIText.Key("page." + page) : Emotes.Find(chosen[0].Id)!.Texts.Text("page." + page);
            pages.Add(new WheelPage(title, Make(chosen)));
            PageKeys.Add(page);
        }
        _wheel.SetPages(pages.ToArray());
        var at = key == null ? -1 : PageKeys.IndexOf(key);
        if (at >= 0) _wheel.Page = at;
        foreach (var problem in problems) Plugin.Logger.ErrorOnce("ForgeEmote: " + problem);
    }

    /// <summary>Lights the wheel's slots for these action IDs and no others.</summary>
    internal static void Highlight(HashSet<string> ids) => _wheel?.Highlight(ids);

    /// <summary>Greys what cannot start now.</summary>
    internal static void Refresh(PlayerMotion motion)
    {
        if (motion == _lastMotion) return;
        _lastMotion = motion;
        foreach (var (item, definition) in Items) item.Usable = EmoteRules.CanStart(motion, definition.Move);
    }
}
