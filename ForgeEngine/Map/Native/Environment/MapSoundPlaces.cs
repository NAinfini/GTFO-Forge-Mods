using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.Presentation;
using ForgeEngine.Presentation.Native;

namespace ForgeMap.Native;

/// <summary>
/// What the shared sound rows ask the Map package, because the answer belongs to map objects and map text: the game
/// object behind a followed terminal, door or other map instance, a subtitle that names an author Text block rendered
/// in the viewer's language, and the alarm emitter of a door's chained puzzle. The host plays and stops every sound;
/// this only resolves the Map's own things for it.
/// </summary>
internal sealed class MapSoundPlaces
{
    public bool TryRenderSubtitle(string text, out string rendered, out string? refusal)
    {
        refusal = null;
        try
        {
            if (StationTextValue.TryRender(text, out rendered)) return true;
            refusal = ResultCode.TextResourceMissing;
            return false;
        }
        catch (RuntimeContractException error)
        {
            rendered = text;
            refusal = error.Code;
            return false;
        }
    }

    public UnityEngine.GameObject? FollowObject(CommandContext context, EntityReference reference)
    {
        object? native = context.EntityInstance(reference)
            ?? TerminalObjectActions.ResolveByAddress(reference) ?? DoorActionCommands.ResolveByAddress(reference);
        return native is UnityEngine.Component { WasCollected: false } component && component.gameObject is { WasCollected: false } gameObject
            ? gameObject : null;
    }

    public CommandResult StopDoorAlarm(CommandContext context, string address, IReadOnlyList<EntityReference> viewers)
    {
        Outcome Refused(string reason)
        {
            MapLog.Record(RuntimeLogLevel.Error, "map.alarm-sound-refused", "Alarm sound stop refused.",
                RuntimeLogValue.Of("address", address), RuntimeLogValue.Of("reason", reason));
            return Outcome.Rejected(reason);
        }
        if (context.Present(PresentationContract.AudioStopSound)) return Refused(ResultCode.AlarmSoundSourceConflict);
        var parsed = MapObjectDoorAddress.TryParse(address);
        if (parsed == null) return Refused(ResultCode.AlarmSoundAddressInvalid);
        var door = DoorObservation.ByAddress(parsed);
        var puzzle = door == null ? null : AlarmActions.PuzzleOf(door, parsed);
        if (puzzle == null || puzzle.Data == null || puzzle.m_sound == null || puzzle.m_sound.WasCollected
            || puzzle.Data.AlarmSoundStop == 0) return Refused(ResultCode.AlarmSoundUnavailable);
        try
        {
            // ChainedPuzzleInstance.OnStateChange posts this paired stop on its own CellSoundPlayer. Post uses
            // that player's ID (native 12197AC / 12197CF), so another puzzle's identical event is not retargeted.
            puzzle.m_sound.Post(puzzle.Data.AlarmSoundStop);
            MapLog.Record(RuntimeLogLevel.Info, "map.alarm-sound-submitted", "Alarm sound stop submitted on its own emitter.",
                RuntimeLogValue.Of("address", address), RuntimeLogValue.Of("sound", (long)puzzle.Data.AlarmSoundStop));
        }
        catch (Exception containedError)
        {
            MapLog.Contained("environment-presentation.alarm-sound-stop", containedError);
            MapLog.Record(RuntimeLogLevel.Error, "map.alarm-sound-refused", "Alarm emitter threw.",
                RuntimeLogValue.Of("address", address), RuntimeLogValue.Of("reason", ResultCode.AlarmSoundStopException));
            return Outcome.Failed(ResultCode.AlarmSoundStopException);
        }
        return PresentationRows.Presented(context, ResultCode.AlarmSoundStopSubmitted);
    }
}
