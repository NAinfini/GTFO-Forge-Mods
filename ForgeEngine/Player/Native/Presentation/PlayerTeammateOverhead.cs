using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeUI;
using SNetwork;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>Resolves the exact recipient before handing the line to ForgeUI, which draws it above that teammate's own name marker
/// (<see cref="TeammateTagPlace.Overhead"/>, the marker hooks are ForgeUI's). The lines are kept here by player; ForgeUI reads them back
/// for each marker it refreshes. The local player has no marker of their own, so a line for them is refused by name.</summary>
internal sealed class PlayerTeammateOverhead
{
    private readonly IPlayerDirectory players;
    private readonly Dictionary<IntPtr, TeammateTag> lines = new();
    private TeammateTagSource? source;
    internal PlayerTeammateOverhead(IPlayerDirectory players) => this.players = players;

    public CommandResult Show(EntityReference recipient, string line, Color? color)
    {
        if (!EntityKinds.Is(recipient, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (players.AgentOf(recipient) is not { } agent) return Outcome.Rejected(ResultCode.StaleEntity);
        var owner = agent.Owner;
        if (owner == null || owner.Pointer == IntPtr.Zero) return Outcome.Rejected(ResultCode.StaleEntity);
        if (agent.IsLocallyOwned) return Outcome.Rejected(ResultCode.HudSelfOverheadUnsupported);
        source ??= UIKit.Mod(ForgeEngine.Plugin.PluginGuid).AddTeammateTag("Overhead", Read, TeammateTagPlace.Overhead);
        if (source == null) return Outcome.Rejected(ResultCode.TeammateOverheadUnavailable);
        // An empty line is a line this plan took away from that one teammate, not a sweep of the others.
        if (string.IsNullOrEmpty(line)) lines.Remove(owner.Pointer);
        else lines[owner.Pointer] = new TeammateTag { Text = line, Colour = color };
        return new Outcome(Status.Partial, Commit.None, ResultCode.HudValuePresented);
    }

    public CommandResult Clear()
    {
        lines.Clear();
        source?.Remove();
        source = null;
        return new Outcome(Status.Partial, Commit.None, "teammate-overhead-cleared");
    }

    private TeammateTag Read(SNet_Player player) => lines.TryGetValue(player.Pointer, out var tag) ? tag : TeammateTag.None;
}
