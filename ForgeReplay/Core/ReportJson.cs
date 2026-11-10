using System.Linq;
using System.Text.Json;

namespace ForgeReplay;

/// <summary>The <c>report</c> object of the exported statistics (<see cref="StatsExport"/>): everything <see cref="RunReport"/> works out. Field
/// names are stable; a value that is worked out and not read from the game sits beside an <c>estimated</c> flag or list, one that the
/// recording cannot say is <c>null</c>.</summary>
internal static class ReportJson
{
    internal static void Write(Utf8JsonWriter json, Analysis analysis, RunSummary? footer)
    {
        var r = analysis.Report;
        json.WriteStartObject("report");
        json.WriteNumber("skippedEvents", analysis.Report?.SkippedEvents ?? 0);
        if (r == null) { json.WriteEndObject(); return; }
        void Row4(string name, System.Func<int, double> value) { json.WriteStartArray(name); for (var s = 0; s < 4; s++) json.WriteNumberValue(value(s)); json.WriteEndArray(); }

        json.WriteStartObject("friendlyFire");
        json.WriteBoolean("known", r.FriendlyFireKnown);
        json.WriteStartArray("damageByAttackerThenVictim");
        for (var a = 0; a < 4; a++) { json.WriteStartArray(); for (var v = 0; v < 4; v++) json.WriteNumberValue(System.Math.Round(r.FriendlyDamage[v, a], 1)); json.WriteEndArray(); }
        json.WriteEndArray();
        Row4("raysThatHitAPlayer", s => r.FriendlyShots[s]);
        json.WriteStartArray("takenKnown"); for (var s = 0; s < 4; s++) json.WriteBooleanValue(r.HurtKnown[s]); json.WriteEndArray();
        json.WriteEndObject();

        json.WriteStartObject("downs");
        json.WriteNumber("teamDownMs", r.TeamDownMs);
        Row4("downMs", s => r.DownMsOf[s]); Row4("rescued", s => r.RescuedOf[s]);
        Row4("meanRescueMs", s => r.RescuedOf[s] == 0 ? 0 : r.RescueMsOf[s] / r.RescuedOf[s]);
        json.WriteStartArray("list");
        foreach (var d in r.Downs)
        {
            json.WriteStartObject();
            json.WriteNumber("ms", d.TimeMs); json.WriteNumber("slot", d.Slot); json.WriteString("cause", d.Cause.ToString()); json.WriteNumber("grab", d.Grab);
            json.WriteNumber("enemy", d.EnemyDataId); json.WriteNumber("attackerSlot", d.Attacker); json.WriteNumber("downMs", d.DownMs);
            json.WriteBoolean("rescued", d.Rescued); json.WriteNumber("reviverSlot", d.Reviver); json.WriteBoolean("estimated", d.Estimated);
            json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();

        json.WriteStartArray("weapons");
        foreach (var w in r.Weapons.Values.OrderBy(w => w.Slot).ThenByDescending(w => w.Damage))
        {
            json.WriteStartObject();
            json.WriteNumber("slot", w.Slot); json.WriteNumber("wielded", w.Wielded); json.WriteNumber("shots", w.Shots); json.WriteNumber("hits", w.Hits);
            json.WriteNumber("weak", w.Weak); json.WriteNumber("backstabs", w.Backstabs); json.WriteNumber("damage", System.Math.Round(w.Damage, 1)); json.WriteNumber("kills", w.Kills);
            if (w.AimKnown) json.WriteNumber("aimedShots", w.AimedShots); else json.WriteNull("aimedShots");
            json.WriteBoolean("estimated", w.Estimated);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("enemies");
        foreach (var t in r.EnemyTypes())
        {
            json.WriteStartObject();
            json.WriteNumber("enemy", t.DataId); json.WriteNumber("appeared", t.Appeared); json.WriteNumber("killed", t.Killed); json.WriteNumber("escaped", t.Escaped);
            json.WriteNumber("meanKillSeconds", System.Math.Round(t.MeanTtkSeconds, 1)); json.WriteNumber("meanDistanceMetres", System.Math.Round(t.MeanDistance, 1));
            json.WriteNumber("healthTaken", System.Math.Round(t.HealthTaken, 1)); json.WriteNumber("overkillWasted", System.Math.Round(t.Overkill, 1));
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartObject("resources");
        json.WriteStartArray("ammoUsedPerPool"); for (var s = 0; s < 4; s++) { json.WriteStartArray(); for (var i = 0; i < 3; i++) json.WriteNumberValue(r.AmmoUsed[s, i]); json.WriteEndArray(); }
        json.WriteEndArray();
        json.WriteStartArray("ammoGainedPerPool"); for (var s = 0; s < 4; s++) { json.WriteStartArray(); for (var i = 0; i < 3; i++) json.WriteNumberValue(r.AmmoGained[s, i]); json.WriteEndArray(); }
        json.WriteEndArray();
        Row4("ammoPickups", s => r.AmmoPickups[s]);
        json.WriteStartArray("suppliesGivenPerKind"); for (var s = 0; s < 4; s++) { json.WriteStartArray(); for (var i = 0; i < 3; i++) json.WriteNumberValue(r.Gave[s, i]); json.WriteEndArray(); }
        json.WriteEndArray();
        json.WriteStartArray("suppliesGotPerKind"); for (var s = 0; s < 4; s++) { json.WriteStartArray(); for (var i = 0; i < 3; i++) json.WriteNumberValue(r.Got[s, i]); json.WriteEndArray(); }
        json.WriteEndArray();
        // Worked out from the rise of health, ammunition and infection after a pack (ClientInference): all estimates.
        Row4("healedShareOfFullHealthEstimated", s => System.Math.Round(r.HealedShare[s], 2)); Row4("ammoRestoredEstimated", s => r.AmmoRestored[s]); Row4("infectionClearedEstimated", s => System.Math.Round(r.InfectionCleared[s], 1));
        Row4("packPickups", s => r.PackPickupsBy[s]);
        Row4("boxesOpenedEstimated", s => r.BoxesBy[s]);
        json.WriteNumber("boxesOpenedByNobody", r.BoxesOpened.Count(b => b.Slot < 0));
        json.WriteEndObject();

        json.WriteStartObject("tension");
        json.WriteNumber("peak", r.PeakTension); json.WriteNumber("peakMs", r.PeakTensionMs);
        json.WriteStartArray("huntersPerSecond"); foreach (var n in r.Tension) json.WriteNumberValue(n); json.WriteEndArray();
        json.WriteEndObject();

        json.WriteStartObject("zones");
        json.WriteStartArray("list");
        foreach (var z in r.Zones.Values.OrderBy(z => z.FirstMs))
        {
            json.WriteStartObject();
            json.WriteNumber("dimension", z.Dimension); json.WriteNumber("layer", z.Layer); json.WriteNumber("zone", z.Zone);
            json.WriteNumber("firstMs", z.FirstMs); json.WriteNumber("firstSlot", z.FirstSlot); json.WriteNumber("stayMs", z.StayMs);
            json.WriteNumber("doorOpenedBySlotEstimated", z.OpenedBy);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("scans");
        foreach (var s in r.Scans) { json.WriteStartObject(); json.WriteNumber("fromMs", s.FromMs); json.WriteNumber("doneMs", s.DoneMs); json.WriteNumber("slotsSeenInside", s.Slots); json.WriteEndObject(); }
        json.WriteEndArray();
        json.WriteStartArray("terminals");
        foreach (var t in r.TerminalSpans) { json.WriteStartObject(); json.WriteNumber("slot", t.Slot); json.WriteNumber("fromMs", t.FromMs); json.WriteNumber("toMs", t.ToMs); json.WriteEndObject(); }
        json.WriteEndArray();
        json.WriteStartArray("objectives");
        foreach (var o in r.ObjectiveSpans) { json.WriteStartObject(); json.WriteNumber("layer", o.Layer); json.WriteNumber("fromMs", o.FromMs); json.WriteNumber("toMs", o.ToMs); json.WriteEndObject(); }
        json.WriteEndArray();
        json.WriteEndObject();

        json.WriteStartArray("waves");
        foreach (var w in r.Waves)
        {
            json.WriteStartObject();
            json.WriteNumber("startMs", w.StartMs); json.WriteNumber("endMs", w.EndMs); json.WriteBoolean("endRecorded", w.Ended); json.WriteNumber("wave", w.WaveId);
            json.WriteNumber("spawned", w.Spawned); json.WriteNumber("killedDuring", w.Killed); json.WriteNumber("playerDamage", System.Math.Round(w.DamageTaken, 1)); json.WriteNumber("downs", w.Downs);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("session");
        foreach (var l in r.Session) { json.WriteStartObject(); json.WriteNumber("ms", l.TimeMs); json.WriteString("kind", l.Kind.ToString()); json.WriteNumber("slot", l.Slot); json.WriteEndObject(); }
        json.WriteEndArray();

        json.WriteStartObject("deployables");
        Row4("sentriesPlaced", s => r.SentriesPlaced[s]); Row4("minesPlaced", s => r.MinesPlaced[s]); Row4("foamFired", s => r.FoamFired[s]);
        Row4("sentryDamageEstimated", s => System.Math.Round(r.SentryDamage[s], 1)); Row4("sentryKillsEstimated", s => r.SentryKills[s]);
        json.WriteEndObject();

        json.WriteStartObject("curves");
        void Curves(string name, Series[] series)
        {
            json.WriteStartArray(name);
            foreach (var s in series) { json.WriteStartArray(); for (var i = 0; i < s.Times.Count; i++) { json.WriteStartArray(); json.WriteNumberValue(s.Times[i]); json.WriteNumberValue(s.Values[i]); json.WriteEndArray(); } json.WriteEndArray(); }
            json.WriteEndArray();
        }
        Curves("healthOf127", r.Health); Curves("infectionPercent", r.Infection); Curves("staminaPercentRecorderOnly", r.Stamina);
        json.WriteEndObject();

        if (footer?.Health is { } h)
        {
            json.WriteStartObject("recording");
            json.WriteNumber("lostSamples", h.LostSamples); json.WriteNumber("lostEvents", h.LostEvents); json.WriteNumber("hookFailures", h.HookFailures); json.WriteNumber("maxWriteQueue", h.MaxQueue);
            json.WriteStartArray("frameBinsUpperMs"); foreach (var e in RecordingHealth.Edges) json.WriteNumberValue(e > 1e9 ? -1 : e); json.WriteEndArray();
            json.WriteStartArray("frameBins"); foreach (var b in h.Bins) json.WriteNumberValue(b); json.WriteEndArray();
            json.WriteNumber("hitches", h.Hitches);
            json.WriteStartArray("hitchList"); foreach (var (t, ms) in h.HitchList) { json.WriteStartObject(); json.WriteNumber("ms", t); json.WriteNumber("frameMs", ms); json.WriteEndObject(); }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }
}
