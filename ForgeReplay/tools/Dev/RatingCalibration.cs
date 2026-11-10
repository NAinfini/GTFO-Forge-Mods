using System.Globalization;
using System.Text.Json;
using ForgeCore;
using ForgeReplay;

/// <summary>Development: REPLAY_RATING_DIR=<folder of .frpl> REPLAY_NATIVE_ROWS=<the website's vanilla-values-native.json>
/// REPLAY_GAMEDATA=<a folder with Player.json> prints what the member rating reads, per member, from the recordings as they are:
/// the numbers the owner and low anchors are computed from. A worked-out number is prefixed with ~.</summary>
internal static class RatingCalibration
{
    internal static void Report(string folder, string nativeRows, string? gameData, string? tablePath)
    {
        var table = string.IsNullOrEmpty(tablePath) ? null : RatingTable.Parse(File.ReadAllText(tablePath));
        var data = DamageCalibration.Tables(nativeRows);
        var fullHealth = 0f;
        if (!string.IsNullOrEmpty(gameData))
        {
            var player = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(gameData, "Player.json"))).RootElement.GetProperty("Blocks").EnumerateArray()
                .First(b => b.GetProperty("persistentID").GetUInt32() == 1);
            fullHealth = player.GetProperty("health").GetSingle();
        }
        foreach (var path in Directory.GetFiles(folder, "*.frpl").OrderBy(p => p))
        {
            using var stream = File.OpenRead(path);
            var log = ChunkLog.Open(stream, ReplayHeader.FormatVersion);
            var header = ReplayHeader.Decode(log.Read(log.Chunks.First(c => c.Type == ChunkTypes.Header)));
            var timeline = new Timeline(log);
            var analysis = Analysis.Run(timeline, header.RecorderIsHost, header.RecorderSlot, damage: data, players: header.Players);
            var minutes = timeline.DurationMs / 60000.0;
            Console.WriteLine($"rating: {Path.GetFileName(path)} rundown {header.RundownId} tier {header.Tier} index {header.Index} recorder {header.RecorderName} slot {header.RecorderSlot} host={header.RecorderIsHost} {minutes:0.00} min, full health {fullHealth:0.#}");
            for (var slot = 0; slot < 4; slot++)
            {
                if (!ReplayFeed.EverHeld(slot, header.Players, analysis.Presence)) continue;
                var values = MemberValues.Read(analysis, slot, timeline.DurationMs, fullHealth);
                if (!values.Any) continue;
                var name = header.Players.FirstOrDefault(p => p.Slot == slot).Name ?? "?";
                string Num(int i) => values.Value[i] is { } v
                    ? ((values.Estimated >> i & 1) != 0 ? "~" : "") + v.ToString("0.###", CultureInfo.InvariantCulture)
                    : "—";
                double? Survival() => values.Value[3] is { } taken && values.Value[4] is { } downs ? taken + fullHealth * downs : null;
                double? Support() => values.Value[5] is { } rescues && values.Value[6] is { } gave ? rescues + gave : null;
                string Dim(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "—";
                Console.WriteLine($"rating:   slot {slot} {name}: damage {Num(0)} kills {Num(1)} weak {Num(2)} taken {Num(3)} downs {Num(4)} rescues {Num(5)} gave {Num(6)} interactions {Num(7)}"
                    + $" | output {Dim(values.Value[0])} kills {Dim(values.Value[1])} accuracy {Dim(values.Value[2])} survival {Dim(Survival())} support {Dim(Support())} mission {Dim(values.Value[7])}");
                if (table is not null)
                {
                    var rating = MemberRating.Rate(table, values);
                    var dimensions = string.Join(" ", RatingMetrics.Dimensions.Select((d, i) => d + " " + Dim(rating.Dimensions[i].Score)));
                    Console.WriteLine($"rating:     grade {(rating.Grade >= 0 ? table.Grades[rating.Grade].Name : "—")} score {Dim(rating.Score)} | {dimensions}");
                }
            }
        }
    }
}
