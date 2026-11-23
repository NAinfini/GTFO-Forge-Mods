using System;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The one place a level's own identity is read off the game. Both values the `level` matcher compares come
/// from the running expedition rather than from a name: `Global.RundownIdToLoad` is the rundown block the
/// process loaded, and `RundownManager.GetActiveExpeditionData()` carries the tier and the zero-based index of
/// the level inside it. The string key the game builds for the same expedition (`Local_31_TierA_0`) is written
/// to the log as a second reading of the same fact, never used for the comparison: it carries a transport
/// prefix this side would have to predict instead of read.
///
/// A read makes no claims and throws nothing. A process between levels, an unloaded rundown, a member that does
/// not read and a tier this vocabulary cannot spell all answer null, which the matcher reports once and treats
/// as "no level matches here" — never as "every level matches".
/// </summary>
internal static class LevelIdentity
{
    private static bool _written;
    private static MapLevelReference? _lastLevel;
    private static string? _lastKey;
    private static string? _lastFailure;

    /// <summary>The active expedition's identity, or null when this process cannot name one. Whenever the answer
    /// changes, one <see cref="MapLogCodes.LevelIdentity"/> record names the identity the matcher compares against and
    /// the game's own key for the same expedition, so a level whose behaviours never fired can be told from a level
    /// that never read its identity. Callers read it once per job or tick; the record is written once per change.</summary>
    internal static MapLevelReference? Read()
    {
        try
        {
            if (!ForgeCore.Level.TryActiveExpedition(out var rundown, out var tier, out var index))
            {
                _triple = false;
                Note(null, null, "expedition-unreadable");
                return null;
            }
            // The game's key string is built from these same three values, and reading it makes a managed string each time:
            // an unchanged triple is the unchanged identity, so the string is read only when the triple changes.
            if (_triple && _tripleRundown == rundown && _tripleTier == tier && _tripleIndex == index) return _lastLevel;
            var key = ForgeCore.Level.ActiveKey ?? "";
            var level = MapLevelReference.FromNative(rundown, tier, index);
            Note(level, key, null);
            _triple = true; _tripleRundown = rundown; _tripleTier = tier; _tripleIndex = index;
            return level;
        }
        catch (Exception error)
        {
            _triple = false;
            Note(null, null, error.GetType().Name);
            return null;
        }
    }

    private static bool _triple;
    private static uint _tripleRundown;
    private static int _tripleTier, _tripleIndex;

    private static void Note(MapLevelReference? level, string? key, string? failure)
    {
        if (_written && level == _lastLevel && string.Equals(key, _lastKey, StringComparison.Ordinal)
            && string.Equals(failure, _lastFailure, StringComparison.Ordinal)) return;
        _written = true;
        _lastLevel = level;
        _lastKey = key;
        _lastFailure = failure;
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Write(RuntimeLogLevel.Info, MapLogCodes.LevelIdentity, new RuntimeLogFacts
        {
            Values = failure == null
                ? new[] { RuntimeLogValue.Of("level", level is { } identity ? identity.ToString() : "unreadable"), RuntimeLogValue.Quote("activeKey", key ?? "") }
                : new[] { RuntimeLogValue.Of("level", "unreadable"), RuntimeLogValue.Of("failure", failure) }
        });
    }
}
