using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeCore;

namespace ForgeReplay;

/// <summary>A stretch of the recording where nothing happened (<see cref="Analysis.Busy"/>), played fast while the automatic camera is on.</summary>
internal readonly record struct IdleSpan(uint FromMs, uint ToMs);

/// <summary>Skipping the quiet (PLAN 2.2): a run of seconds with no shot, hit, hurt player, interaction or walk that lasts
/// <see cref="IdleLeastMs"/> or more plays at <see cref="Fast"/>, and stops <see cref="LeadMs"/> before the activity that ends it.</summary>
internal static class Pacing
{
    internal const uint IdleLeastMs = 10000, LeadMs = 2000;
    internal const float Fast = 8f;

    internal static List<IdleSpan> IdleSpans(IReadOnlyList<bool> busy, uint durationMs)
    {
        var spans = new List<IdleSpan>();
        var seconds = (int)((durationMs + 999) / 1000);
        var from = -1;
        for (var s = 0; s <= seconds; s++)
        {
            var quiet = s < seconds && (s >= busy.Count || !busy[s]);
            if (quiet) { if (from < 0) from = s; continue; }
            if (from < 0) continue;
            var start = (uint)from * 1000u;
            var end = Math.Min(durationMs, (uint)s * 1000u);
            if (end - start >= IdleLeastMs && end - LeadMs > start) spans.Add(new IdleSpan(start, end - LeadMs));
            from = -1;
        }
        return spans;
    }

    /// <summary>True when <paramref name="nowMs"/> lies in one of the spans.</summary>
    internal static bool Idle(List<IdleSpan> spans, uint nowMs)
    {
        foreach (var span in spans) if (nowMs >= span.FromMs && nowMs < span.ToMs) return true;
        return false;
    }
}

internal enum HighlightKind : byte { MultiKill, Rescue, BossKill, Wipe }

/// <summary>A moment worth watching again: when, what, the player it is about (-1 none), how many (kills in the streak; for a rescue the slot
/// revived), its score, and for a boss kill the enemy, its data block and when it came.</summary>
internal readonly record struct Highlight(uint TimeMs, HighlightKind Kind, int Slot, int Count, float Score, int Entity = -1, ulong EnemyDataId = 0, uint SpawnMs = 0)
{
    /// <summary>Where a click on it goes: <see cref="Highlights.LeadMs"/> before, not before the boss came.</summary>
    internal uint JumpMs => Highlights.Before(TimeMs, Kind == HighlightKind.BossKill ? SpawnMs : 0);
}

/// <summary>The highlights list (PLAN 2.2): streaks of kills, revives by a player low on health, boss kills and the moments before a wipe,
/// each scored by its own weight and the director's weights (<see cref="Director.Weight"/>) of what happened around it; the best
/// <see cref="Most"/> in time order. Also where every jump to an event lands: <see cref="LeadMs"/> before it.</summary>
internal static class Highlights
{
    internal const uint LeadMs = 5000, StreakGapMs = 3000, BackGraceMs = 2000;
    internal const int StreakLeast = 3, Most = 20;
    internal const float LowHealth = 0.25f;

    internal static uint Before(uint timeMs, uint notBefore = 0) => Math.Max(notBefore, timeMs > LeadMs ? timeMs - LeadMs : 0);

    internal static List<Highlight> Pick(Analysis analysis, Func<ulong, bool>? boss)
    {
        var found = new List<Highlight>();
        var streaks = new (uint First, uint Last, int Count)[4];
        void Flush(int slot)
        {
            var s = streaks[slot];
            if (s.Count >= StreakLeast) found.Add(new Highlight(s.First, HighlightKind.MultiKill, slot, s.Count, 3f * s.Count));
            streaks[slot] = default;
        }
        foreach (var k in analysis.Kills)
        {
            var slot = k.Killer & 3;
            ref var s = ref streaks[slot];
            if (s.Count > 0 && k.TimeMs - s.Last <= StreakGapMs) { s.Count++; s.Last = k.TimeMs; }
            else { Flush(slot); streaks[slot] = (k.TimeMs, k.TimeMs, 1); }
            if (boss?.Invoke(k.EnemyDataId) == true) found.Add(new Highlight(k.TimeMs, HighlightKind.BossKill, slot, 1, 10f, k.Entity, k.EnemyDataId, k.SpawnMs));
        }
        for (var slot = 0; slot < 4; slot++) Flush(slot);
        foreach (var r in analysis.Rescues)
            if (r.Reviver >= 0 && r.Health <= LowHealth) found.Add(new Highlight(r.TimeMs, HighlightKind.Rescue, r.Reviver, r.Revived, 8f));
        foreach (var w in analysis.Wipes) found.Add(new Highlight(w, HighlightKind.Wipe, -1, 0, 12f));
        for (var i = 0; i < found.Count; i++)
        {
            var h = found[i]; var score = h.Score;
            foreach (var m in analysis.Markers)
                if (m.TimeMs + Director.WindowBeforeMs >= h.TimeMs && m.TimeMs <= h.TimeMs + Director.WindowAfterMs) score += Director.Weight(m.Kind);
            found[i] = h with { Score = score };
        }
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (found.Count > Most) found.RemoveRange(Most, found.Count - Most);
        found.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return found;
    }

    /// <summary>Where the next or previous mark lands: each mark lands <see cref="Before"/> itself, so the steps compare landings. <paramref name="lastLanding"/>,
    /// the landing of the last step, stands in for a clock that has played on less than <see cref="BackGraceMs"/> past it, so stepping back while playing never lands on the same mark again.
    /// -1 when there is none.</summary>
    internal static long MarkLanding(Analysis analysis, uint nowMs, bool next, long lastLanding)
    {
        var from = !next && lastLanding >= 0 && nowMs >= lastLanding && nowMs < lastLanding + BackGraceMs ? (uint)lastLanding : nowMs;
        long found = -1;
        foreach (var m in analysis.Markers)
        {
            var landing = Before(m.TimeMs);
            if (next ? landing > from + 100 && (found < 0 || landing < found) : landing < from && landing > found) found = landing;
        }
        return found;
    }

    /// <summary>The slot whose down is marked at exactly <paramref name="timeMs"/> (a click on its mark); -1 when none is.</summary>
    internal static int DownAt(Analysis analysis, uint timeMs)
    {
        foreach (var m in analysis.Markers) if (m.Kind == MarkerKind.Down && m.TimeMs == timeMs) return m.Slot;
        return -1;
    }
}

/// <summary>Choosing an enemy to watch (PLAN 5.4): the ones near a place ranked hunting first, big ones next, then the nearest; the one
/// nearest the middle of the view.</summary>
internal static class EnemyPick
{
    internal const float Reach = 40f, AimDegrees = 6f, AimReach = 80f, BodyUp = 1f;

    internal static void Ranked(WorldState state, float x, float y, float z, Func<ulong, bool>? big, List<int> into)
    {
        into.Clear();
        var keys = new List<(int Entity, int Rank, float Squared)>();
        foreach (var (entity, enemy) in state.Enemies)
        {
            ref var p = ref state.Poses[entity];
            if (!p.Present) continue;
            var dx = p.X - x; var dy = p.Y - y; var dz = p.Z - z;
            var squared = dx * dx + dy * dy + dz * dz;
            if (squared > Reach * Reach) continue;
            var hunting = EnemyAlerts.Settle((EnemyAlert)(p.State & 3), state.EnemyLocomotion[entity]) == EnemyAlert.Hunting;
            keys.Add((entity, (hunting ? 0 : 2) + (big?.Invoke(enemy.DataId) == true ? 0 : 1), squared));
        }
        keys.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Squared.CompareTo(b.Squared));
        foreach (var k in keys) into.Add(k.Entity);
    }

    /// <summary>The one after <paramref name="current"/> in <paramref name="ranked"/> (the first when it is not there); -1 when empty.</summary>
    internal static int Next(List<int> ranked, int current) => ranked.Count == 0 ? -1 : ranked[(ranked.IndexOf(current) + 1) % ranked.Count];

    /// <summary>The enemy whose body is nearest the middle of a view from (x, y, z) looking along the unit (fx, fy, fz), within
    /// <see cref="AimDegrees"/> and <see cref="AimReach"/>; -1 when none.</summary>
    internal static int Aimed(WorldState state, float x, float y, float z, float fx, float fy, float fz)
    {
        var best = -1; var bestCos = MathF.Cos(AimDegrees * MathF.PI / 180f);
        foreach (var (entity, _) in state.Enemies)
        {
            ref var p = ref state.Poses[entity];
            if (!p.Present) continue;
            var dx = p.X - x; var dy = p.Y + BodyUp - y; var dz = p.Z - z;
            var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.1f || length > AimReach) continue;
            var cos = (dx * fx + dy * fy + dz * fz) / length;
            if (cos > bestCos) { bestCos = cos; best = entity; }
        }
        return best;
    }
}

/// <summary>One stop of a camera path: where, the turn (degrees, yaw unwrapped against the stop before) and the seconds from the stop before.</summary>
internal readonly record struct PathKey(float X, float Y, float Z, float Yaw, float Pitch, float Seconds);

/// <summary>The film camera's path (PLAN 5.4): stops marked in the free camera, flown through on a Catmull-Rom curve, each leg taking its
/// length at the free camera's speed when it was marked (at least <see cref="LeastSeconds"/>), eased in at the start and out at the end.
/// Only the replay uses it; no library has a spline.</summary>
internal sealed class CameraPath
{
    internal const float LeastSeconds = 1f;
    internal List<PathKey> Keys { get; } = new();
    internal float Seconds { get; private set; }

    internal void Add(float x, float y, float z, float yaw, float pitch, float speed)
    {
        var seconds = 0f;
        if (Keys.Count > 0)
        {
            var last = Keys[^1];
            yaw = last.Yaw + ((yaw - last.Yaw) % 360f + 540f) % 360f - 180f;
            var dx = x - last.X; var dy = y - last.Y; var dz = z - last.Z;
            seconds = MathF.Max(LeastSeconds, MathF.Sqrt(dx * dx + dy * dy + dz * dz) / MathF.Max(speed, 0.1f));
        }
        Keys.Add(new PathKey(x, y, z, yaw, pitch, seconds));
        Seconds += seconds;
    }

    internal void Clear() { Keys.Clear(); Seconds = 0f; }

    /// <summary>The camera <paramref name="at"/> seconds into the flight; false with fewer than two stops.</summary>
    internal bool Sample(float at, out float x, out float y, out float z, out float yaw, out float pitch)
    {
        x = y = z = yaw = pitch = 0f;
        if (Keys.Count < 2 || Seconds <= 0f) return false;
        var share = Math.Clamp(at / Seconds, 0f, 1f);
        var left = share * share * (3f - 2f * share) * Seconds;
        var i = 1;
        while (i < Keys.Count - 1 && left > Keys[i].Seconds) { left -= Keys[i].Seconds; i++; }
        var t = Math.Clamp(left / Keys[i].Seconds, 0f, 1f);
        PathKey a = Keys[Math.Max(0, i - 2)], b = Keys[i - 1], c = Keys[i], d = Keys[Math.Min(Keys.Count - 1, i + 1)];
        x = Curve(a.X, b.X, c.X, d.X, t); y = Curve(a.Y, b.Y, c.Y, d.Y, t); z = Curve(a.Z, b.Z, c.Z, d.Z, t);
        yaw = Curve(a.Yaw, b.Yaw, c.Yaw, d.Yaw, t); pitch = Curve(a.Pitch, b.Pitch, c.Pitch, d.Pitch, t);
        return true;
    }

    private static float Curve(float p0, float p1, float p2, float p3, float t)
        => 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t + (3f * p1 - p0 - 3f * p2 + p3) * t * t * t);
}

/// <summary>The run's statistics as JSON beside the recording (<c>name.stats.forge</c>), for balancing: the level, each player's numbers with
/// which of them were worked out, every kill and the highlights.</summary>
internal static class StatsExport
{
    internal static string PathOf(string recording) => Path.ChangeExtension(recording, ".stats.forge");

    /// <summary>The export as JSON text; the caller writes it with <c>Store.WriteText</c> (a temporary file, then over the old one), so a crash or a full disk never leaves half a file.</summary>
    internal static string Json(Analysis analysis, ReplayHeader header, uint durationMs, IReadOnlyList<Highlight> highlights, RunSummary? footer = null)
    {
        using var stream = new MemoryStream();
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", 1);
        json.WriteNumber("rundown", header.RundownId); json.WriteNumber("tier", header.Tier); json.WriteNumber("index", header.Index);
        json.WriteString("recorder", header.RecorderName); json.WriteBoolean("host", header.RecorderIsHost); json.WriteNumber("durationMs", durationMs);
        json.WriteStartArray("players");
        for (var slot = 0; slot < 4; slot++)
        {
            var p = analysis.Players[slot];
            json.WriteStartObject();
            json.WriteNumber("slot", slot);
            foreach (var player in header.Players) if (player.Slot == slot) json.WriteString("name", player.Name);
            json.WriteNumber("rays", p.Rays); json.WriteNumber("rayHits", p.RayHits); json.WriteNumber("bulletHits", p.BulletHits);
            json.WriteNumber("landed", p.Landed); json.WriteNumber("weak", p.Weak); json.WriteNumber("backstabs", p.Backstabs);
            json.WriteString("damageQuality", ReplayFeed.Cell(StatRow.Damage, p, analysis, slot, 0f).Quality.ToString().ToLowerInvariant());
            json.WriteNumber("bulletDamage", p.BulletDamage); json.WriteNumber("meleeDamage", p.MeleeDamage); json.WriteNumber("explosionDamage", p.ExplosionDamage);
            json.WriteNumber("damageTaken", p.DamageTaken); json.WriteNumber("takenShare", p.TakenShare);
            json.WriteNumber("kills", p.Kills); json.WriteNumber("downs", p.Downs); json.WriteNumber("revives", p.Revives); json.WriteNumber("rescues", p.Rescues);
            json.WriteNumber("packs", p.Packs); json.WriteNumber("supplied", p.Supplied); json.WriteNumber("woken", p.Woken); json.WriteNumber("mines", p.Mines); json.WriteNumber("dodges", p.Dodges);
            json.WriteStartArray("estimated");
            foreach (StatKind kind in Enum.GetValues(typeof(StatKind))) if (p.Estimated(kind)) json.WriteStringValue(kind.ToString());
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        // The team's damage: "empty" (null damage) when anyone's is unknown, "estimate" when anyone's was worked out.
        var team = ReplayFeed.TeamDamage(analysis.Players, analysis, Enumerable.Range(0, 4).Where(slot => ReplayFeed.EverHeld(slot, header.Players, analysis.Presence)));
        json.WriteStartObject("team");
        json.WriteString("damageQuality", team.Quality.ToString().ToLowerInvariant());
        if (team.Quality == StatQuality.Empty) json.WriteNull("damage"); else json.WriteNumber("damage", double.Parse(team.Text, System.Globalization.CultureInfo.InvariantCulture));
        json.WriteEndObject();
        json.WriteStartArray("kills");
        foreach (var k in analysis.Kills)
        {
            json.WriteStartObject();
            json.WriteNumber("ms", k.TimeMs); json.WriteNumber("slot", k.Killer); json.WriteNumber("kind", k.Kind); json.WriteNumber("enemy", k.EnemyDataId); json.WriteBoolean("exact", k.Exact);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("highlights");
        foreach (var h in highlights)
        {
            json.WriteStartObject();
            json.WriteNumber("ms", h.TimeMs); json.WriteString("kind", h.Kind.ToString()); json.WriteNumber("slot", h.Slot); json.WriteNumber("count", h.Count);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("notes");
        foreach (var n in analysis.Notes)
        {
            json.WriteStartObject();
            json.WriteNumber("ms", n.TimeMs); json.WriteString("mod", n.ModId); json.WriteString("kind", n.Kind); json.WriteString("payload", n.Payload); json.WriteBoolean("mark", n.Mark);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        ReportJson.Write(json, analysis, footer);
        json.WriteEndObject();
        json.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
