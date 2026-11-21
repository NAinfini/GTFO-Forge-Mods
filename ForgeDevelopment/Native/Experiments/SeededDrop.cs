using System;
using System.Globalization;
using ForgeCore;

namespace ForgeDevelopment.Native;

/// <summary>A solo start with bots and chosen level seeds through ForgeCore <see cref="SoloDrop"/>, so two runs build the same map (the drive's
/// <c>drop</c> keeps the game's own seeds). Called by name: <c>drive_cli.py call static:ForgeDevelopment.Native.SeededDrop Start 37-B1 "1,2,3" 3</c>,
/// then <c>State</c>. A lobby or level the game is in is left first; the drop starts once the menu is up with no lobby open.</summary>
internal static class SeededDrop
{
    private static string? _level;
    private static LevelSeeds? _seeds;
    private static int _bots;
    private static float _since = -1f;
    private static bool _ticking;

    /// <summary><paramref name="level"/>: a coordinate such as 37-B1 or a level label. <paramref name="seeds"/>: "build,hostId,session", Int32 each,
    /// or empty for the game's own.</summary>
    internal static string Start(string level, string seeds = "", int bots = 3)
    {
        var part = seeds.Split(',');
        if (seeds.Length > 0 && part.Length != 3) return "the seeds are build,hostId,session";
        try { _seeds = seeds.Length == 0 ? null : new LevelSeeds(Seed(part[0]), Seed(part[1]), Seed(part[2])); }
        catch (FormatException error) { return error.Message; }
        _level = level; _bots = bots; _since = -1f;
        if (!_ticking) { _ticking = true; Frame.OnUpdate("NAinfini.ForgeDevelopment.seeded-drop", Tick); }
        if (SNetwork.SNet.IsInLobby || SNetwork.SNet.LocalPlayer?.IsInSessionHub == true) SNetwork.SNet.SessionHub.LeaveHub();
        return "drop of " + level + " waits for the menu";
    }

    internal static string State() => _level != null ? "pending" : SoloDrop.State + (SoloDrop.Failure.Length > 0 ? " (" + SoloDrop.Failure + ")" : "");

    private static int Seed(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException("a seed is not an Int32: " + text);

    // The rundown data is not loaded when the game starts, and SoloDrop starts only from the menu with no lobby open.
    private static void Tick()
    {
        if (_level == null || GameStateManager.CurrentStateName != eGameStateName.NoLobby || SNetwork.SNet.IsInLobby) return;
        if (_since < 0f) _since = UnityEngine.Time.realtimeSinceStartup;
        LevelId level;
        try
        {
            if (!LevelId.TryParse(_level, out level) && !Level.TryFindLevel(_level, out level))
            {
                if (UnityEngine.Time.realtimeSinceStartup - _since > 240f && Level.Expeditions().Count > 0)
                {
                    Plugin.PluginLog.LogWarning("seeded drop: " + _level + " is not an active expedition; available: "
                        + string.Join(", ", Level.Expeditions().ConvertAll(e => e.Label.Length > 0 ? e.Label + "=" + e.Id.Coordinate : e.Id.Coordinate)));
                    _level = null;
                }
                return;
            }
        }
        catch (Exception) { return; }
        var text = _level;
        _level = null;
        try { SoloDrop.Start(level, _seeds, _bots); }
        catch (Exception error) { Plugin.PluginLog.LogWarning("seeded drop: " + text + " did not start: " + error.Message); }
    }
}
