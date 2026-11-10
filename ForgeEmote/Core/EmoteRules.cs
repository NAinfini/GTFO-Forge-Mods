using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using ForgeEmote.Api;

namespace ForgeEmote;

/// <summary>The conflict rules with the game's own actions, and the wheel's order. No game calls: the mod's native side feeds them.</summary>
internal static class EmoteRules
{
    /// <summary>How many actions one page of the wheel holds.</summary>
    internal const int SlotsPerPage = 8;

    internal static readonly string[] Bodies = { "left-arm", "right-arm", "arms", "upper", "full" };

    /// <summary>The one check of a definition, shared by a definition file and <see cref="Emotes.Register"/>: the first field that is wrong and why, or null when it is whole. Files and clips are not opened here.</summary>
    internal static (string Field, string Message)? Validate(EmoteDefinition d)
    {
        if (string.IsNullOrEmpty(d.Id) || d.Id.IndexOf(':') <= 0 || d.Id[^1] == ':') return ("Id", "Id must be pack:entry.");
        if (string.IsNullOrEmpty(d.Name)) return ("Name", "Name must be text.");
        if (string.IsNullOrEmpty(d.Page)) return ("Page", "Page must be text.");
        if (string.IsNullOrEmpty(d.Clip)) return ("Clip", "Clip must be text.");
        if (d.Slot < 0 || d.Slot >= SlotsPerPage) return ("Slot", "Slot must be from 0 to " + (SlotsPerPage - 1) + ".");
        if (Array.IndexOf(Bodies, d.Body) < 0) return ("Body", "Body must be one of " + string.Join(", ", Bodies) + ".");
        if (!Enum.IsDefined(typeof(EmoteView), d.View)) return ("View", "View must be first-person or third-person.");
        if (!Enum.IsDefined(typeof(EmoteMove), d.Move)) return ("Move", "Move must be free or locked.");
        if (!(d.FadeIn >= 0 && d.FadeIn <= 5)) return ("FadeIn", "FadeIn must be from 0 to 5.");
        if (!(d.FadeOut >= 0 && d.FadeOut <= 5)) return ("FadeOut", "FadeOut must be from 0 to 5.");
        if (!(d.PairHold >= 0 && d.PairHold <= 60)) return ("PairHold", "PairHold must be from 0 to 60.");
        return null;
    }

    /// <summary>Whether an action may start in this state of the player.</summary>
    internal static bool CanStart(PlayerMotion motion, EmoteMove move) => motion switch
    {
        PlayerMotion.Stand or PlayerMotion.Crouch or PlayerMotion.StandStill => true,
        PlayerMotion.Run => move == EmoteMove.Free,
        _ => false,
    };

    /// <summary>States that end any action at once, on this peer and on those watching.</summary>
    internal static bool Interrupts(PlayerMotion motion) => motion is PlayerMotion.Downed or PlayerMotion.Stunned or PlayerMotion.ClimbLadder or PlayerMotion.OnTerminal
        or PlayerMotion.GrabbedByTrap or PlayerMotion.GrabbedByTank or PlayerMotion.GrabbedByPouncer;

    /// <summary>The favourites page: the starred actions in the order starred, then the ones used last that are not starred, to a page; what is no longer installed is left out.</summary>
    internal static List<string> FavouritePage(IReadOnlyList<string> favourites, IReadOnlyList<string> recent, Func<string, bool> installed)
        => favourites.Concat(recent).Distinct(StringComparer.Ordinal).Where(installed).Take(SlotsPerPage).ToList();

    /// <summary>Stars <paramref name="id"/>, or takes its star off; false when a page of stars is full. Stars of what is no longer installed stay in the list (they return with the pack) but take no place.</summary>
    internal static bool Toggle(List<string> favourites, string id, Func<string, bool> installed)
    {
        if (favourites.Remove(id)) return true;
        if (favourites.Count(installed) >= SlotsPerPage) return false;
        favourites.Add(id);
        return true;
    }

    /// <summary>Puts <paramref name="id"/> first among the last used (at most a page's worth); false when it already was first.</summary>
    internal static bool Remember(List<string> recent, string id)
    {
        if (recent.Count > 0 && recent[0] == id) return false;
        recent.Remove(id);
        recent.Insert(0, id);
        if (recent.Count > SlotsPerPage) recent.RemoveRange(SlotsPerPage, recent.Count - SlotsPerPage);
        return true;
    }

    /// <summary>Pages in the wheel's order: the three of the mod first, then others in the order first seen. A player's file (page to IDs) replaces a page's order; what it names that is not installed is reported and skipped; each page keeps at most <see cref="SlotsPerPage"/>.</summary>
    internal static List<(string Page, List<EmoteDefinition> Items)> Arrange(IReadOnlyList<EmoteDefinition> all, IReadOnlyDictionary<string, string[]>? player, List<string> problems)
    {
        var pages = new List<string> { "tactical", "gesture", "dance" };
        foreach (var definition in all) if (!pages.Contains(definition.Page)) pages.Add(definition.Page);
        var byId = all.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var result = new List<(string, List<EmoteDefinition>)>();
        foreach (var page in pages)
        {
            var items = all.Where(definition => definition.Page == page).OrderBy(definition => definition.Slot).ThenBy(definition => definition.Id, StringComparer.Ordinal).ToList();
            if (player != null && player.TryGetValue(page, out var order))
            {
                var chosen = new List<EmoteDefinition>();
                foreach (var id in order)
                {
                    if (byId.TryGetValue(id, out var definition)) { if (!chosen.Contains(definition)) chosen.Add(definition); }
                    else problems.Add("the wheel file names " + id + ", which is not installed");
                }
                items = chosen;
            }
            if (items.Count > SlotsPerPage)
            {
                problems.Add("page " + page + " has " + items.Count + " actions; the wheel shows the first " + SlotsPerPage);
                items = items.Take(SlotsPerPage).ToList();
            }
            if (items.Count > 0) result.Add((page, items));
        }
        return result;
    }
}
