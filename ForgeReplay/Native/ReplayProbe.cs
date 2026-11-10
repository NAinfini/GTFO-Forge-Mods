using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Gear;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Read-only checks for a person or a driver (<c>drive_cli.py call static:ForgeReplay.ReplayProbe Held</c>): what every recorded player's body holds against what the
/// recording says, and the sound events posted over a stretch of playback. It changes nothing in the replay and runs nothing until called (no switch, no per-frame code). Main thread only.</summary>
public static class ReplayProbe
{
    /// <summary>One JSON object per recorded slot: the recorded wield state now, what the body was asked to show, what it holds (the game's wielded slot and item for a puppet,
    /// the shown weapon for a body copy) and whether each agrees; <c>itemOk</c> compares the held item's data block with the one the gear line names.</summary>
    public static string Held()
    {
        var state = Replayer.Track?.State;
        if (state == null) return "no replay";
        var rows = new List<string>();
        for (var slot = 0; slot < 4; slot++)
        {
            if (!state.Poses[slot].Present) { rows.Add("{\"slot\":" + slot + ",\"present\":false}"); continue; }
            var recorded = -1;
            if (state.PlayerStatus[slot] is { } status) { var read = new PayloadReader(status); read.U(); read.U(); recorded = (int)read.U(); }
            var asked = Puppeteer.WieldedOf(slot);
            var loadout = Puppeteer.LoadoutOf(slot).Split('\n');
            var line = asked switch { 1 => 0, 2 => 1, 10 => 2, 3 => 3, _ => -1 };
            var expected = line >= 0 && line < loadout.Length && loadout[line].Trim().Length > 0 ? new GearIDRange(loadout[line].Trim()).GetCompID(eGearComponent.BaseItem) : 0u;
            var puppet = Puppeteer.PlayerOf(slot);
            var cosmetic = Puppeteer.CosmeticOf(slot);
            string body, item = "";
            int heldSlot = -1;
            uint heldId = 0;
            if (puppet != null)
            {
                body = "puppet";
                var inventory = puppet.Agent.Inventory;
                heldSlot = (int)inventory.WieldedSlot;
                var held = inventory.WieldedItem;
                heldId = held?.ItemDataBlock?.persistentID ?? 0;
                item = held == null ? "" : held.ItemDataBlock?.name + "/" + held.name;
            }
            else if (cosmetic != null) { body = "cosmetic"; heldSlot = cosmetic.HeldSlot; item = cosmetic.HeldName; heldId = cosmetic.HeldId; }
            else body = "none";
            var agrees = recorded < 0 || asked == recorded || recorded is not (1 or 2 or 3 or 10);
            rows.Add("{\"slot\":" + slot + ",\"body\":\"" + body + "\",\"recordedWield\":" + recorded + ",\"asked\":" + asked + ",\"heldSlot\":" + heldSlot
                + ",\"slotOk\":" + (heldSlot == asked ? "true" : "false") + ",\"askedMatchesRecording\":" + (agrees ? "true" : "false") + ",\"expectedItemId\":" + expected
                + ",\"heldItemId\":" + heldId + ",\"itemOk\":" + (expected == 0 || expected == heldId ? "true" : "false") + ",\"item\":\"" + item.Replace("\"", "'") + "\"}");
        }
        return "[" + string.Join(",", rows) + "]";
    }

    /// <summary>Points the replay camera at recorded slot <paramref name="slot"/> (the observer's view only; nothing in the replay changes).</summary>
    public static string Watch(int slot) { Replayer.Follow = slot & 3; return ReplayControl.State(); }

    /// <summary>Starts collecting every sound event posted from now (the replay's own and the game's).</summary>
    public static string TraceStart() { ReplaySounds.Trace = new List<(float, uint, bool)>(); return "tracing"; }

    /// <summary>Stops collecting; one line per post: seconds since the first, the event name, "replay" or "game".</summary>
    public static string TraceStop()
    {
        var trace = ReplaySounds.Trace;
        ReplaySounds.Trace = null;
        if (trace == null) return "not tracing";
        var names = new Dictionary<uint, string>();
        foreach (var field in typeof(AK.EVENTS).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.FieldType == typeof(uint) && field.GetValue(null) is uint id) names.TryAdd(id, field.Name);
        var text = new StringBuilder();
        var first = trace.Count > 0 ? trace[0].Time : 0f;
        foreach (var (time, id, own) in trace) text.Append((time - first).ToString("0.000")).Append(' ').Append(names.TryGetValue(id, out var name) ? name : "#" + id).Append(' ').Append(own ? "replay" : "game").Append('\n');
        return text.ToString();
    }
}
