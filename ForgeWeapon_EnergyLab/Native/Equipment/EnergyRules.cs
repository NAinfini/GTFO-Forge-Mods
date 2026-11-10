using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameData;
using Gear;
using Player;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>The rundown's own rules for the energy guns (<see cref="EnergySettings"/>, <c>ForgeEnergyLab/Settings.forge</c> in the rundown's
/// custom folder): read once before registration closes (its Tuning is laid over every gun then), and, when it limits which guns players may
/// choose, ForgeCore's one offer rule (<c>Gear.RestrictOffers</c>) hides the guns the level does not allow from the picker and replaces a
/// saved choice of one. Only energy guns are ever refused; every other gear passes.</summary>
internal static class EnergyRules
{
    private static IDisposable? _rule;
    private static bool _resolved, _known;
    private static uint _rundown;
    private static int _tier, _index;
    private static HashSet<string>? _allowed;

    /// <summary>Reads <c>Settings.forge</c> of <paramref name="rundownFolder"/> (null: no custom rundown) into <see cref="EnergyLab.Settings"/>. A file with
    /// any error is ignored whole with one line saying so; a file that is absent is no error.</summary>
    internal static void Read(string? rundownFolder, List<string> errors)
    {
        if (rundownFolder == null) return;
        var path = Path.Combine(rundownFolder, EnergySettings.FileName);
        if (!File.Exists(path)) return;
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception error) { errors.Add(path + ": could not be read: " + error + "; default rules apply."); return; }
        var problems = new List<string>();
        var parsed = EnergySettings.Parse(json, problems);
        errors.AddRange(problems.Select(problem => path + ": " + problem));
        if (parsed == null) { errors.Add(path + ": ignored because of the errors above; default rules apply."); return; }
        EnergyLab.UseSettings(parsed);
    }

    /// <summary>Puts the offer rule on when the rundown limits the guns; once, however often gear is installed.</summary>
    internal static void Apply()
    {
        if (_rule != null || !EnergyLab.Settings.Restricts) return;
        _rule = ForgeCore.Gear.RestrictOffers(Plugin.Id, Keep);
    }

    private static bool Keep(InventorySlot slot, GearIDRange gear)
        => EnergyGears.ForCategory(gear.GetCompID(eGearComponent.Category)) is not { } energy || Allowed()?.Contains(energy.Definition.Id) != false;

    // The level's list, worked out again only when the selected expedition changes (the game builds its key string on every read).
    private static HashSet<string>? Allowed()
    {
        var known = ForgeCore.Level.TryActiveExpedition(out var rundown, out var tier, out var index);
        if (_resolved && known == _known && rundown == _rundown && tier == _tier && index == _index) return _allowed;
        _resolved = true; _known = known; _rundown = rundown; _tier = tier; _index = index;
        var key = known ? ForgeCore.Level.ActiveKey : null;
        var list = EnergyLab.Settings.AllowedFor(key);
        _allowed = list == null ? null : new HashSet<string>(list, StringComparer.Ordinal);
        return _allowed;
    }
}
