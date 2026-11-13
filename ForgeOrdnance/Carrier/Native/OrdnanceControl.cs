using System.Text;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>ForgeOrdnance's by-name entry to the call-ins, for a driver with no keyboard (ForgeDevelopment's <c>drive_cli.py call static:ForgeOrdnance.OrdnanceControl &lt;Method&gt;</c>)
/// and for other mods: the same host-side placement <see cref="OrdnanceApi.Place"/> gives. Every method returns at once with one line: what it did, or why it could
/// not. Main thread only. The one driver entry of the mod: Arsenal and the exosuits add their methods to it from their own folders.</summary>
public static partial class OrdnanceControl
{
    /// <summary>The loaded call-ins, one line each: id, payload type and display name.</summary>
    public static string List()
    {
        var text = new StringBuilder();
        foreach (var d in OrdnanceApi.Definitions)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(d.Id).Append(" payload=").Append(d.PayloadType).Append(" name=").Append(OrdnanceApi.DisplayName(d));
        }
        return text.Length == 0 ? "no call-ins loaded" : text.ToString();
    }

    /// <summary>The level's two switches as the host set them, then every player's loadout (lookup, ids, uses left), one line each: the readback of <c>LobbyPicker</c>, <c>CallMenu</c> and <c>Loadouts</c>.</summary>
    public static string Loadouts()
    {
        var text = new StringBuilder();
        text.Append(LobbyRules.TryCurrent(out var rules, out var level) ? "rules level=" + level + " LobbyPicker=" + rules.LobbyPicker + " CallMenu=" + rules.CallMenu : "rules not known yet");
        var book = Plugin.Session.Book;
        foreach (var player in book.Players)
        {
            text.Append("\nplayer ").Append(player).Append(":");
            foreach (var key in book.LoadoutOf(player))
            {
                var found = OrdnanceApi.TryGetDefinition(key, out var d);
                book.TryGet(player, key, UnityEngine.Time.time, out var uses, out _);
                text.Append(' ').Append(found ? d.Id : key.ToString("x8")).Append("(uses ").Append(uses).Append(')');
            }
        }
        return text.ToString();
    }

    /// <summary>The arrivals this peer saw lately (id, call number, drop or direct, place), one line each.</summary>
    public static string Arrivals() => Plugin.Session.Arrivals.Count == 0 ? "no arrivals" : string.Join("\n", Plugin.Session.Arrivals);

    /// <summary>Brings call-in <paramref name="id"/> in at the world point (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) now, facing <paramref name="yaw"/> degrees;
    /// <paramref name="arrival"/> is "drop" (the vehicle falls first) or "direct"; <paramref name="overrides"/> is "Field=number;Field=number" (see <see cref="OrdnanceApi.Place"/>).
    /// The first word of the answer is "placed" or "refused".</summary>
    public static string Place(string id, float x, float y, float z, float yaw = 0f, string arrival = "drop", string overrides = "")
    {
        if (!SNet.IsMaster) return "refused: only the host places ordnance";
        var word = arrival.Trim().ToLowerInvariant();
        if (word is not ("drop" or "direct")) return "refused: arrival must be drop or direct";
        var mode = word == "direct" ? OrdnanceArrivalMode.Direct : OrdnanceArrivalMode.Drop;
        var values = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var pair in overrides.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return "refused: overrides must read Field=number;Field=number";
            values[parts[0].Trim()] = number;
        }
        var ok = OrdnanceApi.Place(id, new Vector3(x, y, z), yaw, mode, out var reason, -1, values.Count == 0 ? null : values);
        return (ok ? "placed " : "refused: ") + (ok ? id + " at " + x + "," + y + "," + z + " yaw " + yaw + " " + mode : reason);
    }

    /// <summary>As <see cref="Place"/>, <paramref name="metres"/> in front of the local player on their level, facing the way they look (so the player stands behind it).</summary>
    public static string PlaceAhead(string id, float metres = 6f, string arrival = "drop", string overrides = "")
    {
        var agent = ForgeCore.Players.Local;
        if (agent == null) return "refused: no local player";
        var look = (agent.FPSCamera != null ? agent.FPSCamera.transform.forward : agent.Rotation * Vec.Forward);
        look.y = 0f;
        look = Vec.SqrLength(look) < 1e-4f ? Vec.Forward : look.normalized;
        var at = agent.Position + look * metres;
        return Place(id, at.x, at.y, at.z, Quaternion.LookRotation(look).eulerAngles.y, arrival, overrides);
    }
}
