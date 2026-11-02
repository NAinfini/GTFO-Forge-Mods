using System.Collections.Generic;
using ForgeCore;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>
/// Every peer, from the host's stasis zone records: enemies inside a stasis field move and attack slower. The slowing is ForgeCore's
/// <c>Enemies.SlowZone</c> (one speed lease per enemy inside, the slowest of overlapping leases wins without compounding, the speed comes
/// back when the enemy leaves, dies, is reused or the zone ends, enemies held by the game's foam are skipped). This class keeps what is
/// Arsenal's: one zone per throw, ended when the field is removed or the level, a checkpoint or the host changes, and the error when a
/// field's speed writes do not read back.
/// </summary>
internal static class StasisSlow
{
    private const string Owner = "Forge Ordnance Arsenal stasis";

    private sealed class Field
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal SlowZoneLease Zone = null!;
    }

    private static readonly List<Field> Fields = new();

    internal static void Add(ulong summoner, uint throwId, Vector3 centre, float radius, float factor, float seconds)
    {
        Remove(summoner, throwId);
        Fields.Add(new Field { Summoner = summoner, ThrowId = throwId, Zone = ForgeCore.Enemies.SlowZone(centre, radius, factor, seconds, Owner) });
    }

    internal static void Remove(ulong summoner, uint throwId)
    {
        for (var i = Fields.Count - 1; i >= 0; i--)
            if (Fields[i].Summoner == summoner && Fields[i].ThrowId == throwId) Retire(i, "ended");
    }

    private static void Retire(int index, string why)
    {
        var f = Fields[index];
        Fields.RemoveAt(index);
        f.Zone.Dispose();
        if (f.Zone.Mismatches > 0)
            Plugin.Error("Stasis field of throw " + f.ThrowId + " " + why + ": slowed " + f.Zone.Slowed + " enemy(ies), but " + f.Zone.Mismatches + " speed write(s) did not read back; those enemies were not slowed.");
    }

    /// <summary>Writes the end line of every field whose zone ran out (ForgeCore ends the zone itself; the passes need no call from here).</summary>
    internal static void Tick(float now)
    {
        for (var i = Fields.Count - 1; i >= 0; i--)
            if (!Fields[i].Zone.Active) Retire(i, "timed out on this peer");
    }

    internal static void Clear()
    {
        while (Fields.Count > 0) Retire(Fields.Count - 1, "cleared");
    }
}
