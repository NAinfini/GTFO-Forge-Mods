using System;
using System.Collections.Generic;
using System.Globalization;

namespace ForgeReplay;

/// <summary>One line of the kill feed: when, who (slot 0 to 3), how (EnemyHurt kind: 0 bullet, 1 melee, 2 explosion), the enemy's data
/// block, what the killer held (the PlayerStatus wielded slot, -1 unknown) and whether the attribution is the game's own settlement.
/// <see cref="Entity"/> is the enemy's number in the recording and <see cref="SpawnMs"/> when it came, to watch it before it died.</summary>
internal readonly record struct KillLine(uint TimeMs, int Killer, int Kind, ulong EnemyDataId, int Wielded, bool Exact, int Entity = -1, uint SpawnMs = 0);

/// <summary>What the game names as the cause of a wake-up (the host's Wake event): it sensed a player while asleep, heard a noise, was
/// hurt, was woken by another enemy that named no propagator, or nothing the game says (a client's recording, an older one). Door, Explosion and Sentry
/// are worked out by the analysis from the events just before (a door opened or broken, a blast, a sentry's gunfire) for a wake-up that names nothing.</summary>
internal enum WakeCause : byte { Unknown, Sensed, Noise, Damage, Passed, Door, Explosion, Sentry }

/// <summary>One wake-up of the blame feed: when, which enemy, who woke it (slot 0 to 3, -1 when the game does not say), how, what that player
/// was doing as the game's target says (Agent.NoiseType, 0 none) and how many sleepers its wake-up passed on to (the game's own
/// propagation, counted on the first enemy of the chain).</summary>
internal readonly record struct WakeLine(uint TimeMs, int Entity, ulong EnemyDataId, int Waker, WakeCause Cause, int Action, int Chain);

/// <summary>How a statistics cell is shown (PLAN 5.5): exact, estimated, or empty because nothing in the recording says it.</summary>
internal enum StatQuality : byte { Exact, Estimate, Empty }

/// <summary>A row of the statistics, in the order the panel shows them; its language key is <c>stats.</c> and the name in lower case.</summary>
internal enum StatRow : byte { Kills, Damage, Hits, WeakSpots, Backstabs, Taken, Downs, Revives, Rescues, Packs, Supplied, Woken, Mines, Dodges }

/// <summary>One statistics cell: the value (— when empty) and how it is shown; the screens write an estimate as "about N" in their language
/// (a ~ stuck to a number reads as a minus in the game's font).</summary>
internal readonly record struct StatCell(string Text, StatQuality Quality);

/// <summary>Who is in a player slot at a moment: in the slot or not, the name and the loadout (gear JSON lines, as the file header keeps
/// them). <see cref="Known"/> is false when neither the header nor a join or leave up to the moment speaks of the slot.</summary>
internal readonly record struct SlotHolder(bool Filled, string Name, string Loadout, bool Known);

/// <summary>What the spectator screen and the list read from a recording besides <see cref="Analysis"/>: the kill feed, the statistics
/// cells with their quality, who holds each slot when.</summary>
internal static class ReplayFeed
{
    internal static readonly StatRow[] Rows = (StatRow[])Enum.GetValues(typeof(StatRow));

    /// <summary>The rows that may be worked out by <see cref="ClientInference"/> (a ~ in the cell says when one is).</summary>
    internal static readonly StatRow[] InferredRows = { StatRow.Taken, StatRow.Rescues, StatRow.Packs, StatRow.Supplied, StatRow.Woken, StatRow.Mines, StatRow.Dodges };

    internal static string Key(StatRow row) => "stats." + row.ToString().ToLowerInvariant();

    /// <summary>The cell of <paramref name="row"/> for <paramref name="slot"/> from the numbers up to a moment (<paramref name="p"/>) and the
    /// whole recording's <paramref name="analysis"/>. <paramref name="fullHealth"/> is the game's full player health (PlayerDataBlock), 0 when
    /// unknown: the damage taken read from a health bar is a share of it.</summary>
    internal static StatCell Cell(StatRow row, PlayerStats p, Analysis analysis, int slot, float fullHealth)
    {
        var host = analysis.Settled; var recorder = analysis.RecorderSlot;
        switch (row)
        {
            // Kills and damage: the host's settlement, or on a client the host's synced health (an estimate only where several players hit at once), or without that sync worked out from the rays.
            case StatRow.Kills:
                return analysis.KillsKnown(slot) ? Show(p.Estimated(StatKind.Kill) ? StatQuality.Estimate : StatQuality.Exact, Whole(p.Kills)) : Show(StatQuality.Empty, "");
            case StatRow.Damage:
                {
                    if (!analysis.DamageKnown(slot)) return Show(StatQuality.Empty, "");
                    var estimated = p.Estimated(StatKind.Bullet) || p.Estimated(StatKind.Melee) || p.Estimated(StatKind.Explosion);
                    return Show(estimated ? StatQuality.Estimate : StatQuality.Exact, Amount(p.BulletDamage + p.MeleeDamage + p.ExplosionDamage));
                }
            case StatRow.WeakSpots:
                if (!analysis.HitsKnown(slot)) return Show(StatQuality.Empty, "");
                return Show(StatQuality.Exact, p.Landed == 0 ? "0" : p.Weak + "/" + p.Landed + "  " + (100f * p.Weak / p.Landed).ToString("0", CultureInfo.InvariantCulture) + "%");
            case StatRow.Backstabs: return analysis.HitsKnown(slot) ? Show(StatQuality.Exact, Whole(p.Backstabs)) : Show(StatQuality.Empty, "");
            case StatRow.Hits:
                {
                    if (p.Rays == 0) return Show(StatQuality.Exact, "0");
                    // A player's own ray is what the game itself traced only on the recorder's machine (and for the host's bots); the host's
                    // settled bullets say it for everyone on the host; on a client another player's ray is only this machine's picture of it.
                    var mine = slot == recorder;
                    var hits = host && !mine ? Math.Min(p.BulletHits, p.Rays) : p.RayHits;
                    return Show(host || mine ? StatQuality.Exact : StatQuality.Estimate, hits + "/" + p.Rays + "  " + (100f * hits / p.Rays).ToString("0", CultureInfo.InvariantCulture) + "%", hits != 0);
                }
            case StatRow.Taken:
                if (analysis.HurtExact(slot)) return Show(StatQuality.Exact, Amount(p.DamageTaken));
                return analysis.Estimable(StatKind.TakenShare, slot) && fullHealth > 0f ? Show(StatQuality.Estimate, Amount(p.TakenShare * fullHealth)) : Show(StatQuality.Empty, "");
            case StatRow.Downs: return Show(StatQuality.Exact, Whole(p.Downs));
            case StatRow.Revives: return Show(StatQuality.Exact, Whole(p.Revives));
            case StatRow.Rescues: return Counted(analysis, p, StatKind.Rescue, slot, p.Rescues);
            case StatRow.Packs: return Counted(analysis, p, StatKind.Pack, slot, p.Packs);
            case StatRow.Supplied: return Counted(analysis, p, StatKind.Supplied, slot, p.Supplied);
            case StatRow.Woken: return Counted(analysis, p, StatKind.Woken, slot, p.Woken);
            case StatRow.Mines: return Counted(analysis, p, StatKind.Mine, slot, p.Mines);
            default: return Counted(analysis, p, StatKind.Dodge, slot, p.Dodges);
        }
    }

    /// <summary>The team's damage over <paramref name="slots"/>: empty when anyone's is empty (a sum of some players is not the team's), an estimate when anyone's is one.</summary>
    internal static StatCell TeamDamage(PlayerStats[] players, Analysis analysis, IEnumerable<int> slots)
    {
        var sum = 0f; var estimated = false;
        foreach (var slot in slots)
        {
            var cell = Cell(StatRow.Damage, players[slot], analysis, slot, 0f);
            if (cell.Quality == StatQuality.Empty) return Show(StatQuality.Empty, "");
            estimated |= cell.Quality == StatQuality.Estimate;
            sum += players[slot].BulletDamage + players[slot].MeleeDamage + players[slot].ExplosionDamage;
        }
        return Show(estimated ? StatQuality.Estimate : StatQuality.Exact, Amount(sum));
    }

    // A number shown as an estimate only when part of it so far was worked out; nothing counted is 0, unless the recording has such things and none of them could be put to a player.
    private static StatCell Counted(Analysis analysis, PlayerStats p, StatKind kind, int slot, int count)
    {
        if (count > 0) return Show(p.Estimated(kind) ? StatQuality.Estimate : StatQuality.Exact, Whole(count));
        return analysis.Estimable(kind, slot) ? Show(StatQuality.Exact, "0") : Show(StatQuality.Empty, "");
    }

    // An estimate of nothing is simply nothing: a 0 is not shown as an estimate.
    private static StatCell Show(StatQuality quality, string value, bool nonZero = true)
    {
        if (quality == StatQuality.Estimate && (value == "0" || !nonZero)) quality = StatQuality.Exact;
        return new(quality == StatQuality.Empty ? "—" : value, quality);
    }

    /// <summary>The health from which a mini boss or a boss class enemy is one worth a mark on the bar: the game's big shooters and giants
    /// (150 and 120 to 640 health) carry those classes too and come in numbers; a tank or mother starts at 1000.</summary>
    internal const float BossHealth = 1000f;

    /// <summary>Whether a kill is marked on the bar: a scout (an enemy the game's population data gives the scout role), or a boss (mini boss
    /// or boss class with <see cref="BossHealth"/> or more). <paramref name="enemyClass"/> is the game's eEnemyType number.</summary>
    internal static bool NotableKill(bool scoutRole, int enemyClass, float health) => scoutRole || Boss(enemyClass, health);

    /// <summary>A boss: mini boss or boss class with <see cref="BossHealth"/> or more (a tank, a mother). Its kill is a highlight.</summary>
    internal static bool Boss(int enemyClass, float health) => enemyClass >= MiniBossClass && health >= BossHealth;

    /// <summary>A big enemy the automatic camera may watch while it hunts a player: mini boss class and up, whatever its health.</summary>
    internal static bool Big(int enemyClass) => enemyClass >= MiniBossClass;

    private const int MiniBossClass = 3;

    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Amount(float value) => value.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Who holds <paramref name="slot"/> at <paramref name="timeMs"/>: the file header's player until the slot's first Presence line,
    /// then the newest line at or before the moment (a join with the name and loadout it carries, the header's where it carries none; a
    /// leave empties the slot). <paramref name="fallback"/> names a slot nobody named.</summary>
    internal static SlotHolder Holder(int slot, uint timeMs, IReadOnlyList<PlayerEntry> header, IReadOnlyList<PresenceLine> presence, string fallback)
    {
        var filled = false; var known = false; var name = fallback; var loadout = "";
        foreach (var p in header)
            if (p.Slot == slot) { filled = known = true; name = p.Name; loadout = p.Loadout; }
        foreach (var line in presence)
        {
            if (line.TimeMs > timeMs) break;
            if (line.Slot != slot) continue;
            filled = line.Joined; known = true;
            if (!line.Joined) continue;
            if (line.Name != null) name = line.Name;
            if (line.Loadout != null) loadout = line.Loadout;
        }
        return new SlotHolder(filled, name, filled ? Completed(slot, timeMs, loadout, presence) : loadout, known);
    }

    private const int LoadoutLines = 4;

    /// <summary>A recorder that is a client reads another player's gear only as it is synced to it: the header and the first Presence line can
    /// have gear lines still empty that a later Presence line of the same slot has. The gear is chosen in the lobby, so a line empty now is
    /// taken from the slot's first later line that has it (a tool swapped in the level changes a line that was not empty). The loadout
    /// itself is returned when no line is empty, so callers can compare it by reference.</summary>
    private static string Completed(int slot, uint timeMs, string loadout, IReadOnlyList<PresenceLine> presence)
    {
        var lines = loadout.Split('\n');
        var empty = false;
        for (var i = 0; i < LoadoutLines; i++) if (i >= lines.Length || lines[i].Trim().Length == 0) empty = true;
        if (!empty) return loadout;
        var filled = new string[LoadoutLines];
        for (var i = 0; i < LoadoutLines; i++) filled[i] = i < lines.Length ? lines[i] : "";
        foreach (var line in presence)
        {
            if (line.Slot != slot || !line.Joined || line.Loadout == null) continue;
            var later = line.Loadout.Split('\n');
            for (var i = 0; i < LoadoutLines && i < later.Length; i++)
                if (filled[i].Trim().Length == 0 && later[i].Trim().Length > 0) filled[i] = later[i];
        }
        return string.Join("\n", filled);
    }

    /// <summary>How many Presence lines lie at or before <paramref name="timeMs"/>: the screen rebuilds the slots only when this changes.</summary>
    internal static int PresenceUpTo(IReadOnlyList<PresenceLine> presence, uint timeMs)
    {
        var n = 0;
        while (n < presence.Count && presence[n].TimeMs <= timeMs) n++;
        return n;
    }

    /// <summary>True when <paramref name="slot"/> is held at any moment of the recording (a column of the statistics).</summary>
    internal static bool EverHeld(int slot, IReadOnlyList<PlayerEntry> header, IReadOnlyList<PresenceLine> presence)
    {
        foreach (var p in header) if (p.Slot == slot) return true;
        foreach (var line in presence) if (line.Slot == slot && line.Joined) return true;
        return false;
    }

    /// <summary>The index of the first chat line at or after <paramref name="timeMs"/> (the next one to pop up).</summary>
    internal static int NextChat(IReadOnlyList<ChatLine> chat, uint timeMs)
    {
        var i = 0;
        while (i < chat.Count && chat[i].TimeMs < timeMs) i++;
        return i;
    }

    /// <summary>The value of the gear JSON's "Name" in one loadout line, or "" (the header keeps each slot's gear JSON on its own line).</summary>
    internal static string GearName(string loadout, int line)
    {
        var lines = loadout.Split('\n');
        if (line < 0 || line >= lines.Length) return "";
        var json = lines[line];
        var key = json.IndexOf("\"Name\":\"", StringComparison.Ordinal);
        if (key < 0) return "";
        var start = key + 8;
        var end = json.IndexOf('"', start);
        return end < 0 ? "" : json.Substring(start, end - start);
    }
}
