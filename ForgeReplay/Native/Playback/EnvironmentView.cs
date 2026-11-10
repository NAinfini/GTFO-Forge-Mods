using System;
using ForgeCore;

namespace ForgeReplay;

/// <summary>Shows the recording's fog (the Environment state event's fog data block id) in the replay level, in this game only
/// (ForgeCore <see cref="WorldStates.Fog"/>): the level's replicated environment state and its rules are left alone. The observer's fog
/// clearing (<see cref="ReplayAssist.Fog"/>) wins while it is on: nothing is set then, and when it is switched off (which shows the level's
/// own fog again) the recorded fog is put back. The recording's light sequence (section count and place) names no zone and no on or off, so
/// no light mode is set from it. Main thread only.</summary>
internal static class EnvironmentView
{
    private static uint _recorded, _shown;
    private static bool _cleared, _changed;

    internal static void Begin()
    {
        _recorded = 0; _changed = false; _cleared = ReplayAssist.Fog;
        try { _shown = WorldStates.CurrentFog(); }
        catch (Exception error) { _shown = 0; Plugin.Logger.ErrorOnce("The level's fog could not be read: " + error.Message); }
    }

    /// <summary>The recording's fog from this moment on (a state change while playing, or the state after a jump).</summary>
    internal static void Recorded(uint fogId)
    {
        _recorded = fogId;
        Apply();
    }

    /// <summary>Per frame: puts the recorded fog back once the observer's fog clearing is switched off.</summary>
    internal static void Frame()
    {
        var cleared = ReplayAssist.Fog;
        if (cleared == _cleared) return;
        _cleared = cleared;
        if (cleared) return;
        // Switching the clearing off showed the level's own fog again.
        _changed = false;
        try { _shown = WorldStates.CurrentFog(); } catch (Exception error) { Plugin.Logger.ErrorOnce("The level's fog could not be read: " + error.Message); }
        Apply();
    }

    private static void Apply()
    {
        if (ReplayAssist.Fog) return;
        try
        {
            if (_recorded == 0)
            {
                // No fog recorded yet at this moment (a jump back before the first change): the level's own fog.
                if (!_changed) return;
                WorldStates.LevelFog();
                _shown = WorldStates.CurrentFog(); _changed = false;
                return;
            }
            if (_recorded == _shown) return;
            if (!WorldStates.Fog(_recorded)) { Plugin.Logger.ErrorOnce("The recorded fog " + _recorded + " could not be shown: the level has no environment manager."); return; }
            _shown = _recorded; _changed = true;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The fog " + _recorded + " (0: the level's own) could not be shown: " + error.Message); }
    }

    /// <summary>When the replay ends: the level's own fog again, if the recorded one was shown.</summary>
    internal static void Reset()
    {
        if (_changed && !ReplayAssist.Fog)
            try { WorldStates.LevelFog(); } catch (Exception error) { Plugin.Logger.ErrorOnce("The level's fog could not be put back: " + error.Message); }
        _recorded = _shown = 0; _changed = false;
    }
}
