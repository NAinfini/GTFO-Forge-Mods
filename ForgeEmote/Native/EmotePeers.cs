using System;
using System.Linq;
using ForgeCore;
using ForgeEmote.Api;

namespace ForgeEmote;

/// <summary>
/// Which of another player's actions this peer may show, matched action by action over ForgeCore's content list: an action both have with
/// the same clips plays; one this peer lacks or whose clips differ is not shown (never a wrong one in its place) and the log names it.
/// Players with different action packs still see every action they share.
/// </summary>
internal static class EmotePeers
{
    private static NetManifest _list = null!;

    /// <summary><paramref name="recheck"/> runs for a player whose list arrived or changed, so an action heard before the list is shown then.</summary>
    internal static void Load(NetChannel channel, Action<ulong> recheck)
    {
        _list = channel.Manifest("actions");
        Publish();
        Emotes.Changed += Publish;
        _list.Changed += player =>
        {
            Report(player);
            recheck(player);
        };
    }

    private static void Publish() => _list.Set(Emotes.Entries.Select(entry => (entry.NetId, Fnv.Hash64(entry.Hash))));

    /// <summary>True when <paramref name="player"/>'s action <paramref name="id"/> is one this peer has with the same clips.</summary>
    internal static bool Shows(ulong player, uint id)
    {
        switch (_list.Match(player, id))
        {
            case ContentMatch.Shared: return true;
            case ContentMatch.Mismatched:
                Plugin.Logger.ErrorOnce("ForgeEmote: " + EmoteRunner.NameOf(player) + " played " + Emotes.Find(id)!.Definition.Id + " but their copy has different clips; it is not shown.");
                return false;
            case ContentMatch.OnlyThere:
                Plugin.Logger.ErrorOnce("ForgeEmote: " + EmoteRunner.NameOf(player) + " played action #" + id + ", which this side does not have; their log names its pack.");
                return false;
            default: return false;
        }
    }

    private static void Report(ulong player)
    {
        var differences = _list.Differences(player);
        if (differences == null || differences.None) return;
        var name = EmoteRunner.NameOf(player);
        if (differences.OnlyHere.Count > 0)
            Plugin.Logger.ErrorOnce("ForgeEmote: " + name + " does not have " + Names(differences.OnlyHere) + "; they do not see these actions.");
        if (differences.Mismatched.Count > 0)
            Plugin.Logger.ErrorOnce("ForgeEmote: " + name + " has different clips for " + Names(differences.Mismatched) + "; these actions are refused both ways.");
        if (differences.OnlyThere.Count > 0)
            Plugin.Logger.ErrorOnce("ForgeEmote: " + name + " has " + differences.OnlyThere.Count + " action(s) this side does not have; they are not shown here.");
    }

    private static string Names(System.Collections.Generic.IReadOnlyList<uint> ids) => string.Join(", ", ids.Select(id => Emotes.Find(id)!.Definition.Id));
}
