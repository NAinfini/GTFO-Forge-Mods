using System.Linq;
using System.Text;
using ForgeCore;
using ForgeEmote.Api;

namespace ForgeEmote;

/// <summary>ForgeEmote's public interface to the wheel: what a player does with the mouse, callable from another mod or by name (ForgeDevelopment's
/// <c>drive_cli.py call static:ForgeEmote.EmoteControl &lt;Method&gt; [JSON]</c>). Every method returns at once with text: what it did, or <c>refused: why</c>.
/// Playing goes through the same <c>EmoteRunner.Start</c> as a wheel click. Main thread only.</summary>
public static class EmoteControl
{
    private static string Effective(EmoteDefinition d) => EmoteRunner.ThirdPerson(d) ? "third" : "first";

    /// <summary>An action by ID, or by the part after <c>pack:</c> when only one has it.</summary>
    private static Emotes.Entry? Find(string? id, out string? refusal)
    {
        refusal = null;
        if (string.IsNullOrEmpty(id)) return Emotes.Entries.FirstOrDefault();
        var found = Emotes.Find(id);
        if (found != null) return found;
        var named = Emotes.Entries.Where(e => e.Definition.Id.EndsWith(":" + id)).ToList();
        if (named.Count == 1) return named[0];
        refusal = "refused: " + (named.Count == 0 ? "no action " + id : id + " matches " + string.Join(", ", named.Select(e => e.Definition.Id)));
        return null;
    }

    /// <summary>Every action, one line each: ID, wheel page, the view it plays in, loop, sound cue, pair, then the body part it moves and whether the player may keep walking.</summary>
    public static string List()
    {
        var text = new StringBuilder();
        foreach (var e in Emotes.Entries)
        {
            var d = e.Definition;
            if (text.Length > 0) text.Append('\n');
            text.Append("emote ").Append(d.Id).Append(" page=").Append(d.Page).Append(" view=").Append(Effective(d)).Append(" loop=").Append(d.Loop ? "true" : "false")
                .Append(" sound=").Append(d.Sound ?? "none").Append(" pair=").Append(d.Pair ? "true" : "false")
                .Append(" body=").Append(d.Body).Append(" move=").Append(d.Move == EmoteMove.Free ? "free" : "locked");
        }
        return text.Length == 0 ? "no actions" : text.ToString();
    }

    /// <summary>Plays the action (the first listed when none is named) on the local player as a wheel click does.</summary>
    public static string Play(string? id = null)
    {
        var entry = Find(id, out var refusal);
        if (entry == null) return refusal ?? "refused: no actions";
        if (EmoteRunner.Start(entry) is { } why) return "refused: " + why;
        return "playing " + entry.Definition.Id + " seq=" + EmoteRunner.Seq + " view=" + (EmoteRunner.ThirdPersonView ? "third" : "first") + " sound=" + (entry.Definition.Sound ?? "none");
    }

    /// <summary>Ends the local action.</summary>
    public static string Stop()
    {
        var current = EmoteRunner.Current;
        if (current == null) return "idle";
        EmoteRunner.StopLocal();
        return "stopped " + current.Definition.Id;
    }

    /// <summary>The local action: ID, seconds played, view, whether its sound is going, what it moves, and the player's own motion (walking during a tactical action is allowed).</summary>
    public static string State()
    {
        var current = EmoteRunner.Current;
        var motion = Players.Local is { } local ? Players.Motion(local).ToString() : "none";
        if (current == null) return "idle motion=" + motion;
        var d = current.Definition;
        return "playing " + d.Id + " elapsed=" + EmoteRunner.Elapsed.ToString("0.00") + " view=" + (EmoteRunner.ThirdPersonView ? "third" : "first")
            + " sound=" + (d.Sound == null ? "none" : EmoteRunner.SoundPlaying ? "playing" : "silent") + " body=" + d.Body + " move=" + (d.Move == EmoteMove.Free ? "free" : "locked") + " motion=" + motion;
    }

    /// <summary>Stars (<paramref name="mode"/> "on") or unstars ("off") an action, as the wheel's second button does; the stars hold at most 8.</summary>
    public static string Favourite(string? id, string mode = "on")
    {
        var entry = Find(id, out var refusal);
        if (entry == null || string.IsNullOrEmpty(id)) return refusal ?? "refused: no action named";
        if (mode is not ("on" or "off")) return "refused: mode is on or off";
        var key = entry.Definition.Id;
        if (EmoteWheel.IsFavourite(key) != (mode == "on") && !EmoteWheel.Toggle(key)) return "refused: " + Favourites();
        return "favourite " + key + " " + mode + " (" + EmoteWheel.Starred.Count + "/" + EmoteRules.SlotsPerPage + ")";
    }

    /// <summary>The starred actions in the order starred, with the count against the limit.</summary>
    public static string Favourites() => "favourites " + EmoteWheel.Starred.Count + "/" + EmoteRules.SlotsPerPage + (EmoteWheel.Starred.Count > 0 ? ": " + string.Join(" ", EmoteWheel.Starred) : "");

    /// <summary>The actions the wheel would light for the local player now: two-player ones a teammate in range is waiting on. None when alone.</summary>
    public static string Highlight()
    {
        if (Players.Local is not { } local) return "refused: no local player";
        var ids = EmoteRunner.Answers(local);
        return ids.Count == 0 ? "highlight none" : "highlight " + string.Join(" ", ids);
    }
}
