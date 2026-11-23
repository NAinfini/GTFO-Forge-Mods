using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeMap.Native;

internal sealed partial class EnvironmentActions
{
    /// <summary>The `forge.action.player.voice` command: a `PlayerDialog` line said by the player the plan wired as
    /// `speaker`, asked once, by the host. The game's own entry (`PlayerDialogManager.WantToStartDialogForced`) is an
    /// authoritative action: on the master it validates in the same call and broadcasts the start, so every machine
    /// hears one line (`Player/evidence/player-voice-dialog.json`). A machine that is not the host never asks, which
    /// is what keeps one press from becoming one request per player. The row is confirmed by the manager's own
    /// active-dialog id changing to the requested one in that call; anything else is an unknown commit, because the
    /// entry returns nothing and a refusal inside the game's validation is not an answer it gives.</summary>
    internal CommandResult PlayerVoice(CommandContext context, IPlayerDirectory players)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        long? voiceValue = context.Integer(EnvironmentContract.Voice);
        if (voiceValue is not (>= 0 and <= uint.MaxValue)) return Outcome.Rejected(ResultCode.VoiceIdRequired);
        if (PlayerDialogManager.Current is not { } manager || manager.Pointer == IntPtr.Zero)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var request = new VoiceRequest { Players = players, Manager = manager, Voice = (uint)voiceValue.Value };
        return context.EachTarget(ref request, VoiceStep, EntityKinds.Player, report: _voiceFailed);
    }

    private struct VoiceRequest
    {
        internal IPlayerDirectory Players;
        internal PlayerDialogManager Manager;
        internal uint Voice;
    }

    private static readonly TargetStep<VoiceRequest> VoiceStep = VoiceTarget;

    private static void VoiceTarget(ref VoiceRequest request, EntityReference speaker, int index, RowWriter rows)
    {
        // The game's entry takes the speaking agent, so a speaker this process has no agent for is refused by name.
        if (request.Players.AgentOf(speaker) is not { } agent)
        {
            rows.End(Status.Rejected, Commit.None, ResultCode.PlayerSpeakerUnsupported);
            return;
        }
        var before = request.Manager.m_activeDialogID;
        PlayerDialogManager.WantToStartDialogForced(request.Voice, agent);
        // A line that was already the active one cannot be told from a request the game refused.
        if (before != request.Voice && request.Manager.m_activeDialogID == request.Voice)
            rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        else rows.End(Status.Failed, Commit.Unknown, ResultCode.ReadbackUnconfirmed);
    }

    private void VoiceFailed(EntityReference speaker, Exception error)
        => Report("map.player-voice-failed: " + error.GetType().Name + ": " + error.Message);
}
