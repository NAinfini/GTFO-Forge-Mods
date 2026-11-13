using System.Text;
using ForgeCore;
using SNetwork;
using ForgeOrdnance.Frames;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The exosuits' methods of the mod's driver entry (<c>drive_cli.py call static:ForgeOrdnance.OrdnanceControl Suits</c>):
/// what holding the use key behind a suit and holding the exit key inside it do, sent to the host as the keys send it. A suit is brought in with
/// <see cref="PlaceAhead"/> (arrival "direct").</summary>
public static partial class OrdnanceControl
{
    /// <summary>Every suit in the level, one line each: call number, state, pilot, place and distance from the local player.</summary>
    public static string Suits()
    {
        var all = FrameWorld.All;
        if (all.Count == 0) return "no suits";
        var me = Players.Local;
        var local = SNet.LocalPlayer?.Lookup ?? 0;
        var text = new StringBuilder();
        for (var i = 0; i < all.Count; i++)
        {
            var r = all[i].Record;
            if (i > 0) text.Append('\n');
            text.Append("suit ").Append(r.ThrowId).Append(' ').Append(r.State).Append(r.HasPilot ? (r.Pilot == local ? " pilot=local" : " pilot=other") : "").Append(" armor=").Append(r.Armor.ToString("0.#")).Append(" magazine=").Append(r.Magazine).Append(" reserve=").Append(r.Reserve)
                .Append(" at ").Append(r.X.ToString("0.0")).Append(',').Append(r.Y.ToString("0.0")).Append(',').Append(r.Z.ToString("0.0"));
            if (me != null && !me.WasCollected) text.Append(" distance=").Append(Vector3.Distance(me.Position, new Vector3(r.X, r.Y, r.Z)).ToString("0.0"));
        }
        return text.ToString();
    }

    /// <summary>Asks the host to board the local player into the nearest idle suit, as a completed hold of the use key does. The host's rules (behind the suit, close) still decide;
    /// read <see cref="Suits"/> a few seconds later for "pilot=local".</summary>
    public static string BoardSuit()
    {
        var unit = NearestSuit(FrameState.Idle);
        if (unit == null) return "no idle suit";
        FrameWorld.Request(unit, FrameRequestKind.Enter);
        return "board asked suit " + unit.Record.ThrowId;
    }

    /// <summary>Asks the host to let the local player out of the suit they wear, as a completed hold of the exit key does.</summary>
    public static string ExitSuit()
    {
        var unit = FrameWorld.PilotUnit(SNet.LocalPlayer?.Lookup ?? 0);
        if (unit == null) return "not in a suit";
        FrameWorld.Request(unit, FrameRequestKind.Exit);
        return "exit asked suit " + unit.Record.ThrowId;
    }

    private static FrameUnit? NearestSuit(FrameState state)
    {
        var me = Players.Local;
        if (me == null) return null;
        FrameUnit? best = null;
        var bestDistance = float.MaxValue;
        foreach (var unit in FrameWorld.All)
        {
            var r = unit.Record;
            if (r.State != state) continue;
            var d = Vector3.Distance(me.Position, new Vector3(r.X, r.Y, r.Z));
            if (d < bestDistance) { best = unit; bestDistance = d; }
        }
        return best;
    }
}
