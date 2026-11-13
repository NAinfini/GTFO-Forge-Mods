using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

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

    /// <summary>Every enemy inside a field wears a blue outline (ForgeVisual) for as long as it stays, so the slowed ones are told at a glance.</summary>
    private static readonly OutlineColor Blue = new(0.35f, 0.78f, 1f, 0.95f);
    private const float MarkEvery = 0.25f;

    private sealed class Field
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal SlowZoneLease Zone = null!;
        /// <summary>Each enemy inside: its outline and its aura's own hold key, so enemies standing together keep one aura each.</summary>
        internal readonly Dictionary<IntPtr, Slowed> Marks = new();
        internal readonly HashSet<IntPtr> Inside = new();
        internal readonly List<IntPtr> Left = new();
        internal float NextMark;
    }

    private sealed class Slowed
    {
        internal OutlineHandle? Mark;
        internal int AuraKey;
    }

    private static readonly List<Field> Fields = new();

    internal static void Add(ulong summoner, uint throwId, Vector3 centre, float radius, float factor, float seconds)
    {
        Remove(summoner, throwId);
        Fields.Add(new Field { Summoner = summoner, ThrowId = throwId, Zone = ForgeCore.Enemies.SlowZone(centre, radius, factor, seconds, Owner, enemy => ForgeCore.Level.HasSight(centre, ForgeCore.Enemies.AimPoint(enemy))) });
    }


    private static void Mark(Field f)
    {
        f.Inside.Clear();
        // A wall stops the field: an enemy on the other side of one is neither slowed nor marked.
        foreach (var found in ForgeCore.Enemies.NearVisible(f.Zone.Centre, f.Zone.Radius))
        {
            var enemy = found!;
            var pointer = enemy.Pointer;
            f.Inside.Add(pointer);
            if (!f.Marks.TryGetValue(pointer, out var held)) f.Marks[pointer] = held = new Slowed { AuraKey = Fx.NewKey() };
            if (ArsenalFxSlots.SlowAura is { } aura) Fx.Hold(aura, held.AuraKey, enemy.Position + Vec.Up, Vec.Up, float.NaN, Color.white);
            if (held.Mark is { Active: true }) continue;
            held.Mark = Outlines.Add(Plugin.Id, enemy, Blue, priority: 20);
        }
        f.Left.Clear();
        foreach (var key in f.Marks.Keys) if (!f.Inside.Contains(key)) f.Left.Add(key);
        foreach (var key in f.Left) Unmark(f, key);
    }

    private static void Unmark(Field f, IntPtr key)
    {
        if (f.Marks.Remove(key, out var held)) held.Mark?.Remove();
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
        foreach (var held in f.Marks.Values) held.Mark?.Remove();
        f.Marks.Clear();
        if (f.Zone.Mismatches > 0)
            Plugin.Logger.ErrorOnce("arsenal-stasis-slow-not-read-back", "stasis field of throw " + f.ThrowId + " " + why + ": slowed " + f.Zone.Slowed + " enemy(ies), but " + f.Zone.Mismatches + " speed write(s) did not read back; those enemies were not slowed.");
    }

    /// <summary>Writes the end line of every field whose zone ran out (ForgeCore ends the zone itself; the passes need no call from here).</summary>
    internal static void Tick(float now)
    {
        for (var i = Fields.Count - 1; i >= 0; i--)
        {
            var f = Fields[i];
            if (!f.Zone.Active) { Retire(i, "timed out on this peer"); continue; }
            if (now < f.NextMark || !Outlines.Enabled) continue;
            f.NextMark = now + MarkEvery;
            Mark(f);
        }
    }

    internal static void Clear()
    {
        while (Fields.Count > 0) Retire(Fields.Count - 1, "cleared");
    }
}
