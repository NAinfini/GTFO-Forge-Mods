using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;

namespace ForgeReplay;

/// <summary>The last chunk of a finished recording: how the level ended, when it began and ended (UTC, milliseconds since 1970), how long it
/// ran, and the counts of every player slot, so the replay list shows a recording without reading it. The counts are the ones
/// <see cref="Analysis"/> makes; the recorder feeds it as it writes, so the footer and a full read of the file agree. Layout in DEVELOPERS.md.</summary>
/// <summary>How complete the recording is, written after the players in the footer: samples and events the writer had to drop, hooks that
/// failed, the deepest the write queue got, the frame times of the recording frames in <see cref="Edges"/> bins, and the frames that took over
/// <see cref="HitchMs"/> (the count and the first <see cref="HitchKept"/> with their moment).</summary>
internal sealed class RecordingHealth
{
    internal const double HitchMs = 100;
    internal const int HitchKept = 16;
    /// <summary>Upper edge in ms of each frame time bin; the last bin takes everything over the one before.</summary>
    internal static readonly double[] Edges = { 8, 12, 17, 25, 34, 50, 100, double.MaxValue };
    internal int LostSamples, LostEvents, HookFailures, MaxQueue, Hitches;
    internal readonly int[] Bins = new int[8];
    internal readonly List<(uint TimeMs, uint FrameMs)> HitchList = new();

    internal void Frame(uint nowMs, double frameMs)
    {
        var bin = 0;
        while (frameMs >= Edges[bin]) bin++;
        Bins[bin]++;
        if (frameMs < HitchMs) return;
        Hitches++;
        if (HitchList.Count < HitchKept) HitchList.Add((nowMs, (uint)Math.Min(frameMs, uint.MaxValue)));
    }

    internal int Frames => Bins.Sum();

    /// <summary>The frame time (ms, the bin's upper edge) that <paramref name="share"/> of the frames were at or under; 0 with no frames.</summary>
    internal double Percentile(double share)
    {
        var total = Frames; var run = 0;
        if (total == 0) return 0;
        for (var i = 0; i < Bins.Length; i++) { run += Bins[i]; if (run >= share * total) return i < Bins.Length - 1 ? Edges[i] : HitchMs; }
        return HitchMs;
    }

    internal void Write(byte[] bytes, ref int at)
    {
        Compact.WriteVar(bytes, ref at, (ulong)LostSamples); Compact.WriteVar(bytes, ref at, (ulong)LostEvents);
        Compact.WriteVar(bytes, ref at, (ulong)HookFailures); Compact.WriteVar(bytes, ref at, (ulong)MaxQueue);
        foreach (var b in Bins) Compact.WriteVar(bytes, ref at, (ulong)b);
        Compact.WriteVar(bytes, ref at, (ulong)Hitches); Compact.WriteVar(bytes, ref at, (ulong)HitchList.Count);
        foreach (var (time, ms) in HitchList) { Compact.WriteVar(bytes, ref at, time); Compact.WriteVar(bytes, ref at, ms); }
    }

    internal const int MaxBytes = (4 + 8 + 2 + 2 * HitchKept) * Compact.MaxVarBytes;

    internal static RecordingHealth Read(byte[] data, ref int at)
    {
        static int Int(byte[] d, ref int a) => checked((int)Compact.ReadVar(d, ref a));
        var h = new RecordingHealth { LostSamples = Int(data, ref at), LostEvents = Int(data, ref at), HookFailures = Int(data, ref at), MaxQueue = Int(data, ref at) };
        for (var i = 0; i < h.Bins.Length; i++) h.Bins[i] = Int(data, ref at);
        h.Hitches = Int(data, ref at);
        var kept = Int(data, ref at);
        if (kept > HitchKept) throw new FormatException("The footer lists " + kept + " frame hitches.");
        for (var i = 0; i < kept; i++) h.HitchList.Add((checked((uint)Compact.ReadVar(data, ref at)), checked((uint)Compact.ReadVar(data, ref at))));
        return h;
    }
}

internal sealed class RunSummary
{
    internal Outcome Outcome { get; init; }
    internal uint DurationMs { get; init; }
    internal long StartUnixMs { get; init; }
    internal long EndUnixMs { get; init; }
    internal PlayerStats[] Players { get; init; } = Array.Empty<PlayerStats>();
    /// <summary>How complete the recording is.</summary>
    internal RecordingHealth Health { get; init; } = new();

    internal byte[] Encode()
    {
        var bytes = new byte[2 + 3 * Compact.MaxVarBytes + Players.Length * 10 * Compact.MaxVarBytes + RecordingHealth.MaxBytes];
        var at = 0;
        bytes[at++] = (byte)Outcome;
        Compact.WriteVar(bytes, ref at, DurationMs);
        Compact.WriteVarSigned(bytes, ref at, StartUnixMs);
        Compact.WriteVarSigned(bytes, ref at, EndUnixMs);
        Compact.WriteVar(bytes, ref at, (ulong)Players.Length);
        foreach (var p in Players)
        {
            Compact.WriteVar(bytes, ref at, (ulong)p.Rays); Compact.WriteVar(bytes, ref at, (ulong)p.RayHits); Compact.WriteVar(bytes, ref at, (ulong)p.Kills);
            Compact.WriteVar(bytes, ref at, (ulong)p.Downs); Compact.WriteVar(bytes, ref at, (ulong)p.Revives); Compact.WriteVar(bytes, ref at, (ulong)p.Packs);
            Tenths(bytes, ref at, p.BulletDamage); Tenths(bytes, ref at, p.MeleeDamage); Tenths(bytes, ref at, p.ExplosionDamage); Tenths(bytes, ref at, p.DamageTaken);
        }
        Health.Write(bytes, ref at);
        return bytes.AsSpan(0, at).ToArray();
    }

    internal static RunSummary Decode(byte[] data)
    {
        if (data.Length == 0) throw new FormatException("The footer is empty.");
        var at = 1;
        var outcome = (Outcome)data[0];
        if (outcome > Outcome.Left) throw new FormatException("The outcome " + (int)outcome + " is unknown.");
        var duration = checked((uint)Compact.ReadVar(data, ref at));
        var start = Compact.ReadVarSigned(data, ref at);
        var end = Compact.ReadVarSigned(data, ref at);
        var count = Compact.ReadVar(data, ref at);
        if (count > 4) throw new FormatException("The footer lists " + count + " players.");
        var players = new PlayerStats[count];
        for (var i = 0; i < players.Length; i++)
        {
            players[i] = new PlayerStats
            {
                Rays = Int(data, ref at),
                RayHits = Int(data, ref at),
                Kills = Int(data, ref at),
                Downs = Int(data, ref at),
                Revives = Int(data, ref at),
                Packs = Int(data, ref at),
                BulletDamage = Compact.ReadVar(data, ref at) / 10f,
                MeleeDamage = Compact.ReadVar(data, ref at) / 10f,
                ExplosionDamage = Compact.ReadVar(data, ref at) / 10f,
                DamageTaken = Compact.ReadVar(data, ref at) / 10f,
            };
        }
        var health = RecordingHealth.Read(data, ref at);
        if (at != data.Length) throw new FormatException("The footer has " + (data.Length - at) + " unread bytes.");
        return new RunSummary { Outcome = outcome, DurationMs = duration, StartUnixMs = start, EndUnixMs = end, Players = players, Health = health };
    }

    /// <summary>The summary of a finished run, from the analysis that counted it live.</summary>
    internal static RunSummary Of(Outcome outcome, uint durationMs, long startUnixMs, long endUnixMs, Analysis counted, RecordingHealth health)
        => new() { Outcome = outcome, DurationMs = durationMs, StartUnixMs = startUnixMs, EndUnixMs = endUnixMs, Players = counted.Players, Health = health };

    private static void Tenths(byte[] bytes, ref int at, float value) => Compact.WriteVar(bytes, ref at, (ulong)Math.Max(0L, (long)MathF.Round(value * 10f)));

    private static int Int(byte[] data, ref int at) => checked((int)Compact.ReadVar(data, ref at));
}
