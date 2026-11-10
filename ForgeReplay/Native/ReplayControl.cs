using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgeReplay;

/// <summary>ForgeReplay's public interface to the replay list and the replay screen: what a player does with the mouse, callable from another
/// mod or by name (ForgeDevelopment's <c>drive_cli.py call static:ForgeReplay.ReplayControl &lt;Method&gt;</c>). Every method returns at once
/// with one line of text: what it did, or why it could not. Main thread only.</summary>
public static class ReplayControl
{
    /// <summary>The recordings the replay list shows, newest first, one line each: file, whether it plays (or why the file cannot be read),
    /// the game build it was recorded on and this one, complete or cut off, length and result.</summary>
    public static string List()
    {
        var text = new StringBuilder();
        foreach (var e in ReplayLibrary.List(Plugin.ReplayFolder))
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(Path.GetFileName(e.Path)).Append(e.Starred ? " starred" : "").Append(" rundown='").Append(ReplayList.RundownOf(e)).Append("' level=").Append(RundownNames.Code(e)).Append(" missing='").Append(e.Header == null ? "" : RundownNames.Refusal(e)).Append("'")
                .Append(e.Header != null ? " playable" : e.OtherFormat ? " unplayable=format" : " unplayable=damaged")
                .Append(" build=").Append(e.Header?.GameRevision).Append('/').Append(Version.Revision)
                .Append(e.Complete ? " complete" : " cut-off").Append(" duration=").Append(e.DurationMs).Append("ms outcome=").Append(e.Outcome);
        }
        return text.Length == 0 ? "no recordings in " + Plugin.ReplayFolder : text.ToString();
    }

    /// <summary>The recordings the replay list shows, newest first, for another mod (<see cref="ReplayEvents.RecordingFinished"/> hands over one as it is closed). Files that cannot be read are left out.</summary>
    public static RecordingInfo[] Recordings() => ReplayLibrary.List(Plugin.ReplayFolder).Where(e => e.Header != null).Select(ReplayEvents.Info).ToArray();

    /// <summary>Opens the replay list from the main menu, as its menu button does.</summary>
    public static string OpenList() => ReplayList.Open() ? "list open" : "the list cannot open now (game state " + GameStateManager.CurrentStateName + ")";

    /// <summary>The list's filters and chosen recording, as the clicks do: <paramref name="section"/> 0 all, 1 starred, 2 cleared, 3 not cleared, 4 the files that cannot be read;
    /// <paramref name="rundown"/> a name the list shows ("" all); <paramref name="file"/> a file name ("" keeps the chosen one); <paramref name="page"/> 0 run, 1 estimates, 2 file.</summary>
    public static string ListView(int section = 0, string rundown = "", string file = "", int page = 0) { ReplayList.Show(section, rundown, file, page); return "list section " + section + " rundown '" + rundown + "' page " + page; }

    /// <summary>Plays <paramref name="file"/> (a file name the list shows, or "latest" for the newest playable one) as the list's play button does,
    /// leaving a lobby first.</summary>
    public static string Play(string file = "latest")
    {
        var entries = ReplayLibrary.List(Plugin.ReplayFolder);
        var entry = entries.FirstOrDefault(e => e.Refusal == null && (file == "latest" || Path.GetFileName(e.Path) == file));
        if (entry == null) return "no playable recording matches " + file + " (" + entries.Count + " listed)";
        ReplayList.Play(entry.Path);
        return "playing " + Path.GetFileName(entry.Path);
    }

    /// <summary>Where the replay is: idle, loading, preparing, playing, paused or at end, with the position, length, speed, view and followed player.</summary>
    public static string State()
    {
        var clock = Replayer.Clock;
        if (!Replayer.Active || clock == null) return Replayer.Pending != null || Replayer.Starting ? "loading" : "idle";
        return (Replayer.Preparing ? "preparing" : clock.AtEnd ? "at end" : clock.Playing ? "playing" : "paused")
            + " " + clock.PositionMs + "/" + clock.DurationMs + " ms speed " + clock.Speed + " view " + ReplayCamera.Mode + " follow " + Replayer.Follow;
    }

    /// <summary>Jumps to <paramref name="ms"/> into the recording, as a click on the time bar does.</summary>
    public static string Jump(int ms)
    {
        if (!Replayer.Active || ms < 0) return "no replay";
        Replayer.Jump((uint)ms);
        return State();
    }

    /// <summary>The free camera <paramref name="back"/> metres from a place and <paramref name="up"/> above it, turned to look at it from <paramref name="yaw"/> degrees.</summary>
    public static string LookAt(float x, float y, float z, float yaw = 0f, float back = 3.5f, float up = 1.5f)
    {
        if (!Replayer.Active) return "no replay";
        var target = new UnityEngine.Vector3(x, y, z);
        var at = target - UnityEngine.Quaternion.Euler(0f, yaw, 0f) * UnityEngine.Vector3.forward * back + UnityEngine.Vector3.up * up;
        ReplayCamera.PlaceFree(at, yaw, UnityEngine.Mathf.Atan2(up, back) * UnityEngine.Mathf.Rad2Deg);
        return State();
    }

    /// <summary>Watches a recorded player, as a card's click does.</summary>
    public static string Watch(int slot) { ReplayCamera.Select(slot); return State(); }

    /// <summary>What stands in the scene: bodies (players and enemies), hands, doors or boxes.</summary>
    public static string Inspect(string what = "bodies") => what switch
    {
        "hands" => Carried.Inspect(),
        "doors" or "boxes" => EventPresenter.Setter?.Inspect(what) ?? "no setter",
        _ => Puppeteer.Inspect(),
    };

    /// <summary>Pauses the replay, as its pause button does.</summary>
    public static string Pause() { Replayer.Clock?.Pause(); return State(); }

    /// <summary>Plays the replay on from where it is.</summary>
    public static string Resume() { Replayer.Clock?.Play(); return State(); }

    /// <summary>Switches the view: Free, Follow, Eye (the followed player's first-person view), Overhead or Orbit.</summary>
    public static string View(string mode)
    {
        if (!Replayer.Active) return "no replay";
        if (!Enum.TryParse<ViewMode>(mode, true, out var view)) return "no view " + mode + "; one of " + string.Join(", ", Enum.GetNames(typeof(ViewMode)));
        ReplayCamera.SetMode(view);
        return State();
    }

    /// <summary>The automatic camera, as its button does: "on", "off" or "state" (the default), with the view and whom it follows.</summary>
    public static string Auto(string mode = "state")
    {
        if (!Replayer.Active) return "no replay";
        if (mode is "on" or "off" && ReplayScreen.Auto != (mode == "on")) ReplayScreen.ToggleAuto();
        return "auto " + (ReplayScreen.Auto ? "on" : "off") + " view " + ReplayCamera.Mode + " follow " + Replayer.Follow + (ReplayCamera.Enemy >= 0 ? " enemy " + ReplayCamera.Enemy : "");
    }

    /// <summary>Opens a panel or what a button opens, as a click does (ReplayScreen.Show: events, highlights, chat, stats, stats:weapons, board, keys, view, none...).</summary>
    public static string Screen(string what) => ReplayScreen.Show(what);

    /// <summary>Opens the highlights panel; with <paramref name="number"/> (1 up) also picks that highlight, as a click on its row does.</summary>
    public static string HighlightList(int number = 0) => ReplayScreen.OpenHighlights(number) + (number > 0 ? " | " + State() : "");

    /// <summary>The observer's assists, as the screen's switches: <c>Assist("fog", "on"|"off"|"state")</c> clears the fog, <c>Assist("brightness", "&lt;level&gt;"|"on"|"off"|"state")</c>
    /// raises the ambient light (a number is the strength, 0.2 is the switch's; "on" is 0.2). Returns "assist fog … brightness … | fog density … level-fog-block …": the switches, and the fog
    /// the volume draws now (read it a second after "fog on": density and boost 0 mean the fog is gone).</summary>
    public static string Assist(string what = "brightness", string value = "state")
    {
        if (!Replayer.Active) return "no replay";
        var on = value == "on";
        switch (what.ToLowerInvariant())
        {
            case "fog": if (on || value == "off") ReplayAssist.Fog = on; break;
            case "brightness":
                if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level) && level > 0f) ReplayAssist.BrightAt(level);
                else if (on) ReplayAssist.BrightAt(0.2f);
                else if (value == "off") ReplayAssist.Bright = false;
                break;
            default: return "no assist " + what + "; one of fog, brightness";
        }
        return ReplayAssist.State();
    }

    /// <summary>The damage numbers over the picture: "on", "off" or "state" (the default), with how many are shown and have been made.</summary>
    public static string DamageNumbers(string mode = "state")
    {
        if (mode is "on" or "off") ReplayScreen.NumbersOn = mode == "on";
        return ReplayScreen.NumbersState();
    }

    /// <summary>The film path by name: "mark" (a point where the free camera is), "play" (or stop), "clear" or "state" (the default).</summary>
    public static string Film(string action = "state") => ReplayCamera.Film(action);

    /// <summary>Writes the statistics file of <paramref name="file"/> (a name the list shows, or "latest"), as the list's export button does;
    /// "reading, ask again" while the recording is still being read.</summary>
    public static string ExportStats(string file = "latest", bool reveal = false)
    {
        var entry = ReplayLibrary.List(Plugin.ReplayFolder).FirstOrDefault(e => e.Header != null && (file == "latest" || Path.GetFileName(e.Path) == file));
        return entry == null ? "no readable recording matches " + file : ReplayList.ExportStats(entry, reveal);
    }

    /// <summary>What the replay folder takes and may take: the bytes of every recording, the limit in MB (0: none), how many recordings the list keeps
    /// and how many it holds. <paramref name="limitMb"/> (not negative) first sets the limit for this run.</summary>
    public static string Disk(int limitMb = -1)
    {
        if (limitMb >= 0) Plugin.DiskLimitMb.Value = limitMb;
        var entries = ReplayLibrary.List(Plugin.ReplayFolder);
        return "disk used=" + ReplayLibrary.TotalBytes(Plugin.ReplayFolder) + " bytes limit=" + Plugin.DiskLimitMb.Value + " MB keep=" + ReplayLibrary.Keep
            + " listed=" + entries.Count + " starred=" + entries.Count(e => e.Starred);
    }

    /// <summary>Deletes the recordings the limit and the keep count ask to go, as the end of a recording does; one line with how many went.</summary>
    public static string Prune()
    {
        var removed = ReplayLibrary.Prune(Plugin.ReplayFolder, Math.Max(0, Plugin.DiskLimitMb.Value) * 1048576L);
        return "pruned " + removed + " | " + Disk();
    }

    /// <summary>Leaves the replay, as its exit button does.</summary>
    public static string Exit()
    {
        if (!Replayer.Active) return "no replay";
        ReplayScreen.Exit();
        return "leaving the replay";
    }
}
