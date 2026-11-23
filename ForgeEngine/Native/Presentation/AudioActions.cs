using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// The `forge.action.presentation.audio_play` and `audio_stop` commands: the one implementation of the sound rows
/// every domain shares. A play names exactly one source. `sound` is a game sound event, run through the game's own
/// level-event entry (globally, or on the level object a filter names), posted on a point emitter at a world point,
/// or posted on a followed game object. `clip` is a custom sound the author ships, played by the ForgeAudio library
/// globally, at a world point or on a followed object. Without a position, filter or followed object a sound is
/// global, and the three places are exclusive.
///
/// They run on each addressed client, which is what the `presentation` tier means: the host decided which step runs
/// and for whom, the write is the recipient's own, and nothing here commits world state, so every row is the tier's
/// `partial + none`; the play row also carries the audience size, the stop row the four fixed columns alone. A stop
/// handle reaches exactly the playback it was issued for, whichever source started it.
/// What belongs to a domain's own objects and text (a followed map object, a subtitle that names an author text,
/// a door's alarm emitter) is answered by the Map domain (<see cref="ForgeMap.Native.MapSoundPlaces"/>).
/// </summary>
internal sealed class AudioActions
{
    internal const string DoorFilterPrefix = "door/";
    /// <summary>The game's own bound on one line of text: the subtitle is handed to a `LocalizedText`, whose string
    /// form the game renders into one bounded label.</summary>
    internal const int MaximumTextLength = 1024;

    private static readonly HashSet<string> SilentCues = new(StringComparer.Ordinal);
    /// <summary>Forgets which lost sounds were already named, so the next world names them once more.</summary>
    internal static void ResetSilentCues() => SilentCues.Clear();

    private readonly Func<EntityReference, UnityEngine.GameObject?> _playerObject;
    private readonly Func<ForgeMap.Native.MapSoundPlaces?> _places;
    private readonly Action<string> _report;

    internal AudioActions(Func<EntityReference, UnityEngine.GameObject?> playerObject, Func<ForgeMap.Native.MapSoundPlaces?> places,
        Action<string> report)
    {
        _playerObject = playerObject ?? throw new ArgumentNullException(nameof(playerObject));
        _places = places ?? throw new ArgumentNullException(nameof(places));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal CommandResult Play(CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationContract.AudioViewers, out var refusal))
            return refusal;
        bool hasClip = context.Present(PresentationContract.AudioClip);
        bool hasSound = SoundId(context, PresentationContract.AudioSound, out uint sound);
        if (hasClip && context.Present(PresentationContract.AudioSound)) return Outcome.Rejected(ResultCode.AudioSourceInvalid);
        if (!hasClip && !hasSound) return Outcome.Rejected(ResultCode.SoundIdRequired);
        string? subtitle = context.Text(PresentationContract.AudioSubtitle);
        if (subtitle != null)
        {
            if (_places() is { } places)
            {
                if (!places.TryRenderSubtitle(subtitle, out var rendered, out var renderRefusal))
                    return Outcome.Rejected(renderRefusal ?? ResultCode.AudioSoundInvalid);
                subtitle = rendered;
            }
        }
        if (subtitle is { Length: > MaximumTextLength }) return Outcome.Rejected(ResultCode.SoundSubtitleTooLong);
        // Absent and null both mean the plan named no point; a value that is present and is not three finite
        // numbers is refused rather than narrowed to the origin.
        bool positioned = context.Present(PresentationContract.AudioPosition);
        var position = default(UnityEngine.Vector3);
        if (positioned && !PresentationRows.UnityVector(context.Vector(PresentationContract.AudioPosition), out position))
            return Outcome.Rejected(ResultCode.SoundPositionInvalid);
        string? filter = context.Text(PresentationContract.AudioFilter);
        if (positioned && filter is { Length: > 0 }) return Outcome.Rejected(ResultCode.SoundPositionFilterConflict);
        bool following = context.Present(PresentationContract.AudioFollow);
        if (following && (positioned || filter is { Length: > 0 })) return Outcome.Rejected(ResultCode.AudioFollowConflict);
        if (hasClip) return PlayClip(context, subtitle, positioned ? position : null, filter, following);
        if (following) return PlayFollowing(context, sound, subtitle);
        if (context.SoundInstanceId != null)
        {
            if (!string.IsNullOrEmpty(subtitle) && !EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
            try { context.RegisterSoundPlayback(LibraryPointSounds.PlayInstance(sound, positioned ? position : null, filter)); }
            catch (Exception contained)
            {
                PresentationLog.Contained("audio.sound-point", contained);
                return Outcome.Failed(ResultCode.NativeCommitException);
            }
            return string.IsNullOrEmpty(subtitle) ? Played(context, ResultCode.SoundPlayedAtPoint) : PostEvent(context, 0, null, subtitle, PlayCount);
        }
        if (positioned) return PlayAtPoint(context, sound, position, subtitle);
        if (!EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        return PostEvent(context, sound, filter, subtitle, PlayCount);
    }

    /// <summary>The play row's audience-size column; the stop row declares none.</summary>
    private const int PlayCount = Blocks.ActionPresentationAudioPlay.RowTargetCount;

    private static Outcome Played(CommandContext context, string code) => PresentationRows.Presented(context, code, PlayCount);

    /// <summary>One custom sound on this client, played through the ForgeAudio library: globally, at the world point,
    /// or on the followed object. A client that did not load the sound answers the library's own refusal by name, and a
    /// followed target that cannot be resolved is refused, never replaced by a fixed point. The playback is registered
    /// so a stop handle reaches exactly this sound. The subtitle goes through the level event as in the game-sound
    /// placements.</summary>
    private Outcome PlayClip(CommandContext context, string? subtitle, UnityEngine.Vector3? position, string? filter,
        bool following)
    {
        if (filter is { Length: > 0 }) return Outcome.Rejected(ResultCode.AudioClipFilterUnsupported);
        if (!context.TryResource(PresentationContract.AudioClip, out ResourceRef clip))
            return Outcome.Rejected(ResultCode.AudioSoundNotLoaded);
        if (CustomSounds.Live is not { } player) return Outcome.Rejected(ResultCode.AudioEngineUnavailable);
        if (player.Refusal(clip.ResourceId) is { } refusal)
        {
            if (refusal != ResultCode.AudioSoundNotLoaded) return Outcome.Rejected(refusal);
            // A sound whose file this install lost plays nothing; the rest of the plan runs on. Named once per world.
            if (SilentCues.Add(clip.ResourceId)) _report(ResultCode.AudioSoundNotLoaded + ": id=" + clip.ResourceId);
            return Played(context, ResultCode.AudioSoundNotLoaded);
        }
        if (!string.IsNullOrEmpty(subtitle) && !EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        UnityEngine.GameObject? target = null;
        if (following)
        {
            if (!context.TryEntity(PresentationContract.AudioFollow, out var reference)
                || FollowObject(context, reference) is not { } followed)
                return Outcome.Rejected(ResultCode.AudioFollowUnavailable);
            target = followed;
        }
        var start = player.Play(clip.ResourceId, position, target);
        if (start.Code != null) return Outcome.Rejected(start.Code);
        if (context.SoundInstanceId != null && start.Stop != null) context.RegisterSoundPlayback(start.Stop);
        if (!string.IsNullOrEmpty(subtitle)) return PostEvent(context, 0, null, subtitle, PlayCount);
        return Played(context, following ? ResultCode.SoundFollowing : ResultCode.SoundClipPlayed);
    }

    /// <summary>One game sound that follows a game object on this client. The engine moves the sound with the object
    /// and stops it when the object is destroyed (<see cref="LibraryFollowSounds"/>); a target that cannot be resolved to a
    /// live game object, or that the engine cannot attach to, is refused by name and the sound is never moved to a
    /// fixed point instead. The subtitle goes through the level event as in the other placements.</summary>
    private Outcome PlayFollowing(CommandContext context, uint sound, string? subtitle)
    {
        if (!string.IsNullOrEmpty(subtitle) && !EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        if (!context.TryEntity(PresentationContract.AudioFollow, out var reference)
            || FollowObject(context, reference) is not { } target)
            return Outcome.Rejected(ResultCode.AudioFollowUnavailable);
        Action? stop;
        try { stop = LibraryFollowSounds.Play(sound, target); }
        catch (Exception error)
        {
            Report("presentation.sound-follow-failed: " + error.GetType().Name + ": " + error.Message);
            return Outcome.Rejected(ResultCode.AudioFollowUnavailable);
        }
        if (stop == null) return Outcome.Rejected(ResultCode.AudioPostFailed);
        if (context.SoundInstanceId != null) context.RegisterSoundPlayback(stop);
        if (!string.IsNullOrEmpty(subtitle)) return PostEvent(context, 0, null, subtitle, PlayCount);
        return Played(context, ResultCode.SoundFollowing);
    }

    /// <summary>The live game object behind a followed reference: the player's agent, or the object the domain package
    /// that owns the entity resolves the reference to. Null when the reference no longer names one.</summary>
    private UnityEngine.GameObject? FollowObject(CommandContext context, EntityReference reference)
    {
        if (reference.Id != null && reference.Id.StartsWith(PresentationAudience.PlayerEntityKind + ":", StringComparison.Ordinal))
            return _playerObject(reference);
        return _places()?.FollowObject(context, reference);
    }

    /// <summary>The `sound` input of the two sound rows. A sound id is the game's unsigned 32-bit event id — a Wwise
    /// short id or a Forge derived id, both of which are often above the signed range — so it is read as one, and a
    /// value that is absent, negative, fractional or wider than 32 bits is no sound.</summary>
    private static bool SoundId(CommandContext context, HandlerSlot slot, out uint sound)
    {
        long? value = context.Integer(slot);
        sound = value is >= 0 and <= uint.MaxValue ? (uint)value.Value : 0;
        return value is >= 0 and <= uint.MaxValue;
    }

    /// <summary>One game sound at a world point on this client. The subtitle is the level event's own line and is
    /// still shown through that entry, as an event that carries the line and no sound, so the sound is posted once —
    /// at the point — and the line reads the same as an unplaced sound's.</summary>
    private Outcome PlayAtPoint(CommandContext context, uint sound, UnityEngine.Vector3 position, string? subtitle)
    {
        if (!string.IsNullOrEmpty(subtitle) && !EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        try
        {
            LibraryPointSounds.Play(sound, position);
        }
        catch (Exception error)
        {
            Report("presentation.sound-point-failed: " + error.GetType().Name + ": " + error.Message);
            return PresentationRows.Failed(context, ResultCode.NativeCommitException, PlayCount);
        }
        return string.IsNullOrEmpty(subtitle) ? Played(context, ResultCode.SoundPlayedAtPoint)
            : PostEvent(context, 0, null, subtitle, PlayCount);
    }

    /// <summary>The stop row: a loop the author picked, stopped on this client. The game stops a loop by running the
    /// stop event the audio bank pairs with it, and that event goes through the same `PlaySound` entry the play row
    /// uses, so this row carries the stop event's id and nothing else. A loop posted at a world point lives on a point
    /// emitter and a stop event acts on the sound object it is posted to, so the same stop is posted on every kept
    /// emitter as well. A door filter stops that door's own alarm emitter instead, through the domain package. A
    /// playback handle is stopped by the kernel before this handler runs; the handler only carries game events.</summary>
    internal CommandResult Stop(CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationContract.AudioStopViewers, out var refusal))
            return refusal;
        var filter = context.Text(PresentationContract.AudioStopFilter);
        if (filter != null && filter.StartsWith(DoorFilterPrefix, StringComparison.Ordinal))
            return _places() is { } places ? places.StopDoorAlarm(context, filter, Array.Empty<EntityReference>())
                : Outcome.Rejected(ResultCode.LevelEventUnavailable);
        if (!SoundId(context, PresentationContract.AudioStopSound, out uint sound)) return Outcome.Rejected(ResultCode.SoundIdRequired);
        if (!EventsAvailable) return Outcome.Rejected(ResultCode.LevelEventUnavailable);
        Exception? stopFailure = null;
        try { LibraryPointSounds.Stop(sound); }
        catch (Exception error) { stopFailure = error; }
        bool posted = Post(sound, filter is { Length: > 0 } ? filter : null, null);
        if (stopFailure != null)
            Report("presentation.sound-point-stop-failed: " + stopFailure.GetType().Name + ": " + stopFailure.Message);
        // The stop row's declared fields are the four fixed columns alone, so it hands over no count.
        return posted && stopFailure == null ? PresentationRows.Presented(context, ResultCode.LevelEventPresented)
            : PresentationRows.Failed(context, ResultCode.NativeCommitException);
    }

    private static bool EventsAvailable => LevelSoundEvents.Available;

    /// <summary>One level event posted and answered for every viewer. The entry is fire-and-forget and the tier commits
    /// nothing, so a failure is definite for every viewer.</summary>
    private Outcome PostEvent(CommandContext context, uint sound, string? filter, string? subtitle, int countColumn)
        => Post(sound, filter, subtitle) ? PresentationRows.Presented(context, ResultCode.LevelEventPresented, countColumn)
            : PresentationRows.Failed(context, ResultCode.NativeCommitException, countColumn);

    private bool Post(uint sound, string? filter, string? subtitle)
    {
        try
        {
            LevelSoundEvents.Post(sound, filter, subtitle);
            return true;
        }
        catch (Exception error)
        {
            Report("presentation.sound-event-failed: " + error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* A report is diagnostic; its writer must not change the row's result. */ }
    }
}
