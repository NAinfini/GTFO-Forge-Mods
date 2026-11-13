using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using ForgeUI;
using MTFO.API;

namespace ForgeOrdnance;

/// <summary>The definitions and level rules. Definitions come from every <c>ForgeOrdnance</c> folder under <c>BepInEx/plugins</c>
/// (content mods and loose files) and from the active rundown's <c>CustomPath/ForgeOrdnance</c>; the level rules
/// (<c>Settings.forge</c>) come only from the rundown. A definition folder may hold a <c>lang</c> folder of language files whose
/// <c>&lt;Id&gt;.Name</c> and <c>&lt;Id&gt;.Description</c> texts are shown instead of the file's <c>Name</c> and <c>Description</c>.</summary>
internal static class OrdnanceContent
{
    internal const string FolderName = "ForgeOrdnance";
    internal const string SettingsFile = "Settings.forge";
    internal const string LanguageFolder = "lang";

    internal static DefinitionSet Set { get; private set; } = DefinitionSet.Empty;
    internal static OrdnanceSettings Settings { get; private set; } = OrdnanceSettings.Default;
    private static Dictionary<uint, string> _iconPaths = new();
    private static Dictionary<uint, Texts> _texts = new();

    /// <summary>A definition's translations: its folder's language files and the two keys, made once at load.</summary>
    private sealed record Texts(UITexts Files, string NameKey, string DescriptionKey);

    /// <summary>The definition's name in the current language: its folder's <c>&lt;Id&gt;.Name</c> text, else the file's <c>Name</c>.</summary>
    internal static string Name(OrdnanceDefinition definition) =>
        _texts.TryGetValue(definition.Key, out var texts) && texts.Files.TryText(texts.NameKey, out var name) ? name : definition.Name;

    /// <summary>The definition's description in the current language: its folder's <c>&lt;Id&gt;.Description</c> text, else the file's.</summary>
    internal static string Description(OrdnanceDefinition definition) =>
        _texts.TryGetValue(definition.Key, out var texts) && texts.Files.TryText(texts.DescriptionKey, out var text) ? text : definition.Description;

    /// <summary>The definition's category name in the current language.</summary>
    internal static string CategoryName(OrdnanceDefinition definition) =>
        OrdnanceApi.Categories.TryGet(definition.Category, out var category) ? category.Name : definition.Category;

    /// <summary>Full path of the definition's own icon PNG; false when it uses the built-in icon of its category.</summary>
    internal static bool TryGetIconPath(OrdnanceDefinition definition, out string path) => _iconPaths.TryGetValue(definition.Key, out path!);

    /// <summary>Reads every file once. Each problem is logged by file and field; what is valid still loads.</summary>
    internal static void Load()
    {
        OrdnanceApi.DefinitionsLoaded = true;
        var errors = new List<string>();
        var definitions = new List<OrdnanceDefinition>();
        var icons = new Dictionary<OrdnanceDefinition, string>();
        var languages = new Dictionary<OrdnanceDefinition, UITexts>();
        var rundownFolder = MTFOPathAPI.HasCustomPath ? Path.GetFullPath(Path.Combine(MTFOPathAPI.CustomPath, FolderName)) : null;
        var folders = new SortedSet<string>(ForgeCore.ContentFolders.Find(FolderName, errors), StringComparer.OrdinalIgnoreCase);
        if (rundownFolder != null && Directory.Exists(rundownFolder)) folders.Add(rundownFolder);

        foreach (var folder in folders)
        {
            var languageFolder = Path.Combine(folder, LanguageFolder);
            var folderTexts = Directory.Exists(languageFolder) ? UIKit.Texts(languageFolder) : null;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.forge"); }
            catch (Exception error) { errors.Add(Shown(folder) + ": could not enumerate definition files: " + error); continue; }
            foreach (var path in files.OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                var name = Shown(path);
                if (string.Equals(Path.GetFileName(path), SettingsFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(folder, rundownFolder, StringComparison.OrdinalIgnoreCase))
                        errors.Add(name + ": ignored; level rules are read only from the rundown's custom folder.");
                    continue;
                }
                try
                {
                    var parsed = DefinitionParser.ParseFile(name, File.ReadAllText(path), OrdnanceApi.Registry, OrdnanceApi.Categories);
                    errors.AddRange(parsed.Errors);
                    foreach (var definition in parsed.Definitions)
                    {
                        if (definition.Icon.Length != 0)
                        {
                            var iconPath = Path.Combine(folder, definition.Icon);
                            if (!File.Exists(iconPath))
                            {
                                errors.Add(name + ": '" + definition.Id + "' Icon: " + definition.Icon + " is not in " + Shown(folder) + "; this entry is skipped.");
                                continue;
                            }
                            icons[definition] = iconPath;
                        }
                        if (folderTexts != null) languages[definition] = folderTexts;
                        definitions.Add(definition);
                    }
                }
                catch (Exception error) { errors.Add(name + ": could not be read: " + error); }
            }
        }
        Set = DefinitionSet.Build(definitions, errors);
        // Payload registration closed when the definitions were read, so the types are final: ForgeCore refuses a player whose
        // call-in files or payload types differ.
        OrdnanceNetwork.Channel.Content = Set.Hash.ToString("x16", CultureInfo.InvariantCulture) + "/" + OrdnanceApi.Registry.TypesHash.ToString("x16", CultureInfo.InvariantCulture);
        _iconPaths = new Dictionary<uint, string>();
        _texts = new Dictionary<uint, Texts>();
        foreach (var definition in Set.Definitions)
        {
            if (icons.TryGetValue(definition, out var iconPath)) _iconPaths[definition.Key] = iconPath;
            if (languages.TryGetValue(definition, out var texts)) _texts[definition.Key] = new Texts(texts, definition.Id + ".Name", definition.Id + ".Description");
        }

        var settingsPath = rundownFolder == null ? null : Path.Combine(rundownFolder, SettingsFile);
        var fileSettings = OrdnanceSettings.Default;
        if (settingsPath != null && File.Exists(settingsPath))
        {
            try
            {
                var settingsErrors = new List<string>();
                var parsed = OrdnanceSettings.Parse(File.ReadAllText(settingsPath), settingsErrors);
                errors.AddRange(settingsErrors.Select(error => Shown(settingsPath) + ": " + error));
                if (parsed == null) errors.Add(Shown(settingsPath) + ": ignored; default rules apply.");
                else fileSettings = parsed;
            }
            catch (Exception error) { errors.Add(Shown(settingsPath) + ": could not be read: " + error + "; default rules apply."); }
        }
        // The file's rules and the levels plugins declared become one effective setting, checked the same way.
        Settings = fileSettings.WithDeclared(OrdnanceApi.DeclaredLevels, errors).WithUsable(Set, OrdnanceApi.SupportsDirect, OrdnanceApi.CheckOverrides, errors);
        foreach (var id in Settings.UnknownAllowed(Set)) errors.Add("Level rules Allowed: '" + id + "' is not a loaded definition.");
        foreach (var error in errors) Plugin.Logger.Error(error);
        LobbyRules.Invalidate();
        OrdnanceApi.RaiseDefinitionsReady();
    }

    /// <summary>A path as players see it in the log: relative to the BepInEx folder when inside it.</summary>
    private static string Shown(string path)
    {
        var relative = Path.GetRelativePath(Paths.BepInExRootPath, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : ForgeCore.PosixPath.ToPosix(relative);
    }

    /// <summary>The rules of the level being played: the expedition the local game selected, the global rules before one is.</summary>
    internal static OrdnanceLevelRules Rules => Settings.For(ForgeCore.Level.ActiveKey);
}
