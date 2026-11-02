using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ForgeAudio;

public static partial class AudioKit
{
    private static readonly Dictionary<string, uint> GameEventIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plays one of the game's own sound events (a Wwise event ID such as <c>AK.EVENTS.DOOR_ALARM</c>) at a world
    /// position, the way the game's <c>CellSound</c> does: the game's own mix, sliders and occlusion apply and the kit's categories,
    /// voice limits and cue files do not. Use it only for a vanilla sound you want unchanged; your own sounds are cues of an
    /// <see cref="AudioLibrary"/>. Main thread only. Returns false (and logs why) when the event ID is 0, the kit is not running, the
    /// call is off the main thread or the game refuses the event; never throws. An accepted event can still stay silent: the game's
    /// grenade, mine and enemy explosion events (FRAGGRENADEEXPLODE, STICKYMINEEXPLODE, EXPLODEREXPLODE, ENEMYEXPLODE) start no voice
    /// when posted this way, so a mod's explosion is a cue of its own.</summary>
    /// <example><c>AudioKit.PostGameEvent(AK.EVENTS.DOOR_ALARM, point);</c></example>
    public static bool PostGameEvent(uint eventId, Vector3 position)
    {
        if (eventId == 0) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a non-zero event ID."); return false; }
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "PostGameEvent must be called on the Unity main thread after the kit started."); return false; }
        try
        {
            CellSound.Post(eventId, position);
            return true;
        }
        catch (Exception error)
        {
            AudioLog.Report(AudioError.VoiceFailed, "Game sound event " + eventId + " could not be posted: " + error);
            return false;
        }
    }

    /// <summary>As <see cref="PostGameEvent(uint, Vector3)"/>, naming the event as the game's event list does (<c>AK.EVENTS</c>, case
    /// ignored), so a mod needs no reference to the game's Wwise assembly. The name is looked up once and the ID kept. Returns false
    /// (and logs why) when the game has no event of that name.</summary>
    /// <example><c>AudioKit.PostGameEvent("DOOR_ALARM", point);</c></example>
    public static bool PostGameEvent(string eventName, Vector3 position)
    {
        if (string.IsNullOrEmpty(eventName)) { AudioLog.Report(AudioError.InvalidOptions, "A game sound event needs a name."); return false; }
        if (!OnMainThread) { AudioLog.Report(AudioError.WrongThread, "PostGameEvent must be called on the Unity main thread after the kit started."); return false; }
        if (!GameEventIds.TryGetValue(eventName, out var id))
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
                var events = typeof(AK.EVENTS);
                var value = events.GetProperty(eventName, flags)?.GetValue(null) ?? events.GetField(eventName, flags)?.GetValue(null);
                if (value == null) { AudioLog.Report(AudioError.CueNotFound, "The game has no sound event named '" + eventName + "' (AK.EVENTS)."); return false; }
                id = Convert.ToUInt32(value);
            }
            catch (Exception error)
            {
                AudioLog.Report(AudioError.CueNotFound, "The game sound event '" + eventName + "' could not be read: " + error);
                return false;
            }
            GameEventIds[eventName] = id;
        }
        return PostGameEvent(id, position);
    }
}
