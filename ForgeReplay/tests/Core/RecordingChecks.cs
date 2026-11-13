using ForgeCore;
using ForgeReplay;
using ForgeUI;

internal static class RecordingChecks
{
    private sealed class GatedStream : MemoryStream
    {
        internal readonly ManualResetEventSlim Gate = new(true);
        internal bool Throw;
        public override void Write(byte[] buffer, int offset, int count) { Gate.Wait(); if (Throw) throw new IOException("disk full"); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Gate.Wait(); if (Throw) throw new IOException("disk full"); base.Write(buffer); }
    }

    private sealed class CountingRecords : HostLink.IRecords
    {
        internal int Hits, Hurts, Supplies, Packs, Rays, Snapshots;
        internal uint Supplied, PacksTotal;
        public void OnHit(in HostHit hit) => Hits++;
        public void OnWake(in HostWake wake) { }
        public void OnSnapshot(int mask, ReadOnlySpan<HostTotals> totals)
        {
            Snapshots++;
            if ((mask & 1) != 0) { Supplied = totals[0].Supplied; PacksTotal = totals[0].Packs; }
        }
        public void OnHurt(in HostHurt hurt) => Hurts++;
        public void OnSupply(in HostSupply supply) => Supplies++;
        public void OnPack(in HostPack pack) => Packs++;
        public void OnRay(in HostRay ray) => Rays++;
    }

    private static (float X, float Y, float Z) Path(int entity, uint ms) => (entity * 10f + ms * 0.0031f, 5f - ms * 0.0007f * (entity + 1), 1.5f);

    private static void Feed(BlockPipeline pipeline, uint from, uint to, int entities, bool waitEachSecond = true)
    {
        for (var ms = from; ms < to; ms += 33)
        {
            for (var e = 0; e < entities; e++)
            {
                var (x, y, z) = Path(e, ms);
                pipeline.Sample(ms, e, x, y, z, 0f, MathF.Sin(ms * 0.0005f), 0f, MathF.Cos(ms * 0.0005f), (byte)(e + 1));
            }
            if (waitEachSecond && ms % 1000 < 33) pipeline.WaitForWriter();
            pipeline.Tick(ms);
        }
    }

    private readonly record struct Retired(bool Ok, int StatesAfter);

    // Foams and thrown items appear at 1 s and end between 2.5 s and 2.8 s; survivors of each never end. Feeds the same events either with a table that retires
    // (the recorder's) or one that keeps every payload (what it used to do); returns whether every check held, the table size after the ends, the raw bytes of the
    // keyframe block at 10 s, and the time spent writing keyframe states over the run.
    private static (Retired, double, int) Retirement(bool retire, int foams, int thrown, int survivors)
    {
        static byte[] Foam(ulong id, ulong phase) => new PayloadWriter().Reset().U(id).U(phase).U(1000).Pos(id, 2, 3).Pos(0, 0, 0).U(1000).U(1000).U(0).U(0).U(0).Span.ToArray();
        static byte[] Thrown(ulong id, ulong phase) => new PayloadWriter().Reset().U(id).U(phase).U(0).Pos(id, 2, 3).U(7).U(1).Span.ToArray();
        var states = new KeyframeStates();
        var plain = new Dictionary<(ushort, ulong), byte[]>();
        void Set(ushort kind, ulong id, byte[] payload) { if (retire) states.Set(kind, id, payload); else plain[(kind, id)] = payload; }
        var stream = new GatedStream();
        var watch = new System.Diagnostics.Stopwatch();
        var pipeline = new BlockPipeline(ChunkLog.Create(stream, 1), _ => { }, (p, ms) =>
        {
            watch.Start();
            if (retire) states.Write(p, ms); else foreach (var ((kind, _), payload) in plain) p.Event(ms, kind, payload);
            watch.Stop();
        }, 0, blockMs: 1000, keyframeMs: 5000);
        for (uint ms = 0; ms < 16000; ms += 33)
        {
            for (var i = 0; i < foams; i++)
            {
                var id = 1000UL + (ulong)i;
                if (ms >= 1000 && ms - 1000 < 33) { var e = Foam(id, 1); pipeline.Event(ms, EventKind.Foam, e); Set(EventKind.Foam, id, e); }
                if (i < foams - survivors && ms >= 2500 + (uint)i && ms - 2500 - (uint)i < 33) { var e = Foam(id, 2); pipeline.Event(ms, EventKind.Foam, e); Set(EventKind.Foam, id, e); }
            }
            for (var i = 0; i < thrown; i++)
            {
                var id = 5000UL + (ulong)i;
                if (ms >= 1000 && ms - 1000 < 33) { var e = Thrown(id, 1); pipeline.Event(ms, EventKind.Thrown, e); Set(EventKind.Thrown, id, e); }
                if (i < thrown - survivors && ms >= 2500 + (uint)i && ms - 2500 - (uint)i < 33) { var e = Thrown(id, 2); pipeline.Event(ms, EventKind.Thrown, e); Set(EventKind.Thrown, id, e); }
            }
            if (ms % 1000 < 33) pipeline.WaitForWriter();
            pipeline.Tick(ms);
        }
        pipeline.Finish(16000);
        var bytes = stream.ToArray();
        var after = retire ? states.Count : plain.Count;

        // Keyframe blocks after the ends: which objects they hold, and their raw size.
        var reader = ChunkLog.Open(new MemoryStream(bytes), 1);
        var decoder = new SampleDecoder();
        var keyframesClean = true; var rawBytes = 0;
        foreach (var chunk in reader.Chunks)
        {
            if (chunk.Type != DataChunks.Keyframe || chunk.StartMs < 4000) continue;
            var inside = new HashSet<(ushort, ulong)>();
            decoder.Decode(reader.Read(chunk), chunk.RawLength, chunk.StartMs, true, r =>
            {
                if (r.Kind == RecordKind.Event && (r.EventType == EventKind.Foam || r.EventType == EventKind.Thrown)) inside.Add((r.EventType, new PayloadReader(r.Payload!).U()));
            });
            keyframesClean &= inside.Count == 2 * survivors;
            if (chunk.StartMs is >= 10000 and < 11000) rawBytes += chunk.RawLength;
        }

        // Jumps before and after the ends, forwards and backwards, against playing through.
        var seeker = new Timeline(ChunkLog.Open(new MemoryStream(bytes), 1));
        var player = new Timeline(ChunkLog.Open(new MemoryStream(bytes), 1));
        var agree = true;
        string Digest(WorldState s) => string.Join(",", s.Objects.Where(o => o.Key.Kind == EventKind.Foam).Select(o => o.Key.Key + ":" + o.Value.Length).OrderBy(x => x))
            + "|" + string.Join(",", s.Extras.Where(o => o.Key.Kind == EventKind.Thrown).Select(o => o.Key.Key + ":" + o.Value.Length).OrderBy(x => x));
        var probes = new uint[] { 1500, 2000, 2650, 3500, 9000, 12000, 15000, 4000, 1500, 15500, 2650 };
        foreach (var at in probes)
        {
            seeker.Seek(at);
            var fresh = new Timeline(ChunkLog.Open(new MemoryStream(bytes), 1));
            for (uint ms = 100; ms <= at; ms += 100) fresh.Advance(ms, _ => { });
            fresh.Advance(at, _ => { });
            agree &= Digest(seeker.State) == Digest(fresh.State);
            var live = seeker.State.Objects.Count(o => o.Key.Kind == EventKind.Foam) + seeker.State.Extras.Count(o => o.Key.Kind == EventKind.Thrown);
            if (at == 1500) agree &= live == foams + thrown;
            if (at >= 4000) agree &= live == 2 * survivors;
        }
        for (uint ms = 100; ms <= 15500; ms += 100) player.Advance(ms, _ => { });
        agree &= player.State.Objects.Count(o => o.Key.Kind == EventKind.Foam) == survivors && player.State.Extras.Count(o => o.Key.Kind == EventKind.Thrown) == survivors;
        return (new Retired(keyframesClean && agree, after), watch.Elapsed.TotalMilliseconds, rawBytes);
    }
    internal static void Run(Action<bool, string> check)
    {
        // Encoder and decoder alone.
        var enc = new SampleEncoder();
        var key = new byte[16384];
        enc.Begin(key, 100, keyframe: true);
        enc.Sample(100, 3, 1.234f, -5.678f, 90.0f, 0, 0, 0, 1, 2);
        var afterFirst = enc.Length;
        enc.Sample(133, 3, 1.234f, -5.678f, 90.0f, 0, 0, 0, 1, 2);
        check(enc.Length == afterFirst, "sampleencoder: a keyframe block writes an entity in full once and skips it while unchanged");
        var keyLength = enc.Length;
        var buf = new byte[16384];
        enc.Begin(buf, 200, keyframe: false);
        enc.Sample(200, 3, 1.2349f, -5.678f, 90.0f, 0, 0, 0, 1, 2);
        check(enc.Length == 0, "sampleencoder: a delta block skips a sample that did not change within a quantization step");
        enc.Sample(233, 3, 1.30f, -5.678f, 90.0f, 0, 0, 0, 1, 2);
        var moved = enc.Length;
        check(moved > 0 && moved < 14, "sampleencoder: a moved entity costs a few bytes as a difference (" + moved + ")");
        enc.Sample(266, 3, 1.30f, -5.678f, 90.0f, 0, 0.1f, 0, 0.99498744f, 2); // turned where it stands
        enc.Event(300, 7, new byte[] { 1, 2, 3 });
        var decoder0 = new SampleDecoder();
        var got = new List<Record>();
        decoder0.Decode(key, keyLength, 100, true, got.Add);
        decoder0.Decode(buf, enc.Length, 200, false, got.Add);
        check(got.Count == 4 && got[3].Kind == RecordKind.Event && got[3].EventType == 7 && got[3].Payload!.SequenceEqual(new byte[] { 1, 2, 3 })
            && got[2].TimeMs == 266 && Math.Abs(got[2].X - 1.30f) < 0.0051f && Math.Abs(got[2].Z - 90f) < 0.0051f && Math.Abs(got[2].Qy - 0.1f) < 1e-4f
            && got[1].Kind == RecordKind.Sample && got[1].State == 2 && got[1].TimeMs == 233 && Math.Abs(got[1].X - 1.30f) < 0.0051f && Math.Abs(got[1].Qw - 1f) < 1e-4f
            && got[0].TimeMs == 100, "sampledecoder: a keyframe block then a delta block restore positions, the unchanged rotation, a turn in place, state, times and the event"); var lonely = new byte[] { 1, 0, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var rejected = false; try { new SampleDecoder().Decode(lonely, lonely.Length, 0, true, _ => { }); } catch (FormatException) { rejected = true; }
        check(rejected, "sampledecoder: a difference sample with nothing to continue from is refused");
        var cutRejected = 0;
        for (var cut = 1; cut < 12; cut++) { try { new SampleDecoder().Decode(key, cut, 100, true, _ => { }); } catch (FormatException) { cutRejected++; } catch (IndexOutOfRangeException) { cutRejected++; } }
        check(cutRejected > 5, "sampledecoder: a cut-off block is refused");

        // Pipeline end to end.
        var stream = new GatedStream();
        var errors = new List<string>();
        var writer = ChunkLog.Create(stream, 1);
        var active = new[] { 0, 1, 2 };
        var fullStates = 0;
        var pipeline = new BlockPipeline(writer, errors.Add, (p, ms) =>
        {
            fullStates++;
            foreach (var e in active) { var (x, y, z) = Path(e, ms); p.Sample(ms, e, x, y, z, 0f, MathF.Sin(ms * 0.0005f), 0f, MathF.Cos(ms * 0.0005f), (byte)(e + 1)); }
        }, 0, blockMs: 1000, keyframeMs: 5000);
        Feed(pipeline, 0, 12000, 3);
        pipeline.Event(12000, 9, new byte[] { 42 });
        pipeline.Finish(12000);
        var reader = ChunkLog.Open(new MemoryStream(stream.ToArray()), 1);
        var keyframes = reader.Chunks.Count(c => c.Type == DataChunks.Keyframe);
        check(reader.IsComplete && reader.Chunks.Count >= 11 && keyframes == 3 && reader.Chunks[0].Type == DataChunks.Keyframe && errors.Count == 0 && fullStates == 3,
            "pipeline: 12 s becomes about 12 one-second blocks with a keyframe block at 0, 5 and 10 s, and the file finishes clean (blocks " + reader.Chunks.Count + ", keyframes " + keyframes + ")");
        var worst = 0f; var worstTurn = 0f; var samples = 0; var sawEvent = false;
        var decoder = new SampleDecoder();
        foreach (var chunk in reader.Chunks)
            decoder.Decode(reader.Read(chunk), chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, record =>
            {
                if (record.Kind == RecordKind.Event) { sawEvent = record.EventType == 9 && record.Payload![0] == 42; return; }
                var (x, y, z) = Path(record.Entity, record.TimeMs);
                worst = Math.Max(worst, Math.Max(Math.Abs(x - record.X), Math.Max(Math.Abs(y - record.Y), Math.Abs(z - record.Z))));
                var (sy, cw) = (MathF.Sin(record.TimeMs * 0.0005f), MathF.Cos(record.TimeMs * 0.0005f));
                var sign = record.Qy * sy + record.Qw * cw < 0f ? -1f : 1f;
                worstTurn = Math.Max(worstTurn, Math.Max(Math.Max(Math.Abs(record.Qx), Math.Abs(record.Qz)), Math.Max(Math.Abs(record.Qy - sign * sy), Math.Abs(record.Qw - sign * cw))));
                samples++;
            });
        check(worst <= 0.0051f && worstTurn <= 1e-4f && samples > 3 * 12 * 25 && sawEvent, "pipeline: decoding every block in order restores every sample within half a centimetre and its turning rotation within 1e-4 per component, and the event (worst " + worst.ToString("0.0000") + ", rotation " + worstTurn.ToString("0.000000") + ", samples " + samples + ")");

        var jump = reader.Chunks.First(c => c.Type == DataChunks.Keyframe && c.StartMs >= 5000);
        var fromJump = new SampleDecoder(); var fromStart = new SampleDecoder();
        var last = new Dictionary<int, Record>(); var lastJump = new Dictionary<int, Record>();
        foreach (var chunk in reader.Chunks)
        {
            var body = reader.Read(chunk);
            fromStart.Decode(body, chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, r => { if (r.Kind == RecordKind.Sample) last[r.Entity] = r; });
            if (chunk.StartMs >= jump.StartMs) fromJump.Decode(body, chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, r => { if (r.Kind == RecordKind.Sample) lastJump[r.Entity] = r; });
        }
        check(last.Count == 3 && last.All(kv => lastJump.TryGetValue(kv.Key, out var o) && o.X == kv.Value.X && o.Y == kv.Value.Y && o.Z == kv.Value.Z && o.TimeMs == kv.Value.TimeMs),
            "pipeline: starting from a later keyframe block gives the same final state as decoding from the start");

        // Keyframes: the first block is one whatever time the recording starts at, and a checkpoint reload makes the next block one at once.
        var kStream = new GatedStream();
        var kStates = new List<uint>();
        var kPipeline = new BlockPipeline(ChunkLog.Create(kStream, 1), _ => { }, (p, ms) => kStates.Add(ms), 2500, blockMs: 1000, keyframeMs: 5000);
        Feed(kPipeline, 2500, 4800, 2);
        kPipeline.KeyframeNext();
        kPipeline.Tick(4810);
        Feed(kPipeline, 4843, 14000, 2);
        kPipeline.Finish(14000);
        var kReader = ChunkLog.Open(new MemoryStream(kStream.ToArray()), 1);
        var kKeys = kReader.Chunks.Where(c => c.Type == DataChunks.Keyframe).Select(c => c.StartMs).ToList();
        check(kReader.Chunks[0].Type == DataChunks.Keyframe && kReader.Chunks[0].StartMs == 2500 && kStates.Count > 0 && kStates[0] == 2500,
            "pipeline: the first block is a keyframe with every entity written in full, also for a recording that starts later than 0 ms (first " + kReader.Chunks[0].Type + " at " + kReader.Chunks[0].StartMs + ")");
        check(kKeys.Contains(4810) && kKeys.Count(ms => ms > 4810 && ms < 9810) == 0 && kKeys.Exists(ms => ms >= 9810 && ms < 10900) && kStates.Contains(4810),
            "pipeline: after a checkpoint reload the next tick cuts a keyframe block at once, and the five-second rhythm restarts from it (keyframes at " + string.Join(",", kKeys) + ")");

        // Writer behind: samples are dropped, events are not.
        var slow = new GatedStream();
        var slowWriter = ChunkLog.Create(slow, 1);
        slow.Gate.Reset();
        var slowErrors = new List<string>();
        var slowPipeline = new BlockPipeline(slowWriter, slowErrors.Add, (p, ms) => { }, 0, buffers: 2, bufferBytes: 12000);
        Feed(slowPipeline, 0, 9000, 3, waitEachSecond: false);
        for (var i = 0; i < 20; i++) slowPipeline.Event(9000 + (uint)i, 5, new byte[] { (byte)i });
        var lostSamples = slowPipeline.LostSamples; var lostEvents = slowPipeline.LostEvents;
        slow.Gate.Set();
        slowPipeline.Finish(9100);
        var slowReader = ChunkLog.Open(new MemoryStream(slow.ToArray()), 1);
        var events = 0;
        var slowDecoder = new SampleDecoder();
        foreach (var chunk in slowReader.Chunks) slowDecoder.Decode(slowReader.Read(chunk), chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, r => { if (r.Kind == RecordKind.Event) events++; });
        check(lostSamples > 0 && lostEvents == 0 && events == 20 && slowErrors.Count == 1 && slowReader.IsComplete,
            "pipeline: with the writer stuck, samples are dropped and reported once while every event is kept (lost samples " + lostSamples + ")");

        // Write failure: stop, report once, no completion record.
        var broken = new GatedStream();
        var brokenErrors = new List<string>();
        var brokenPipeline = new BlockPipeline(ChunkLog.Create(broken, 1), brokenErrors.Add, (p, ms) => { }, 0, blockMs: 1000);
        Feed(brokenPipeline, 0, 2000, 2);
        brokenPipeline.WaitForWriter();
        broken.Throw = true;
        Feed(brokenPipeline, 2000, 6000, 2);
        brokenPipeline.Finish(6000);
        var brokenReader = ChunkLog.Open(new MemoryStream(broken.ToArray()), 1);
        check(brokenPipeline.Failed && brokenErrors.Count == 1 && brokenErrors[0].Contains("disk full") && !brokenReader.IsComplete && brokenReader.Chunks.Count >= 1,
            "pipeline: a failed write stops the recording with one error, writes no completion record and keeps the blocks already on disk (failed " + brokenPipeline.Failed + ", errors " + string.Join(";", brokenErrors) + ", complete " + brokenReader.IsComplete + ", chunks " + brokenReader.Chunks.Count + ")");

        // Event payloads: numbers, positions in centimetres and text (including one too long to fit) survive a round trip.
        var payloadWriter = new PayloadWriter().Reset().U(3).U(1234567).S(-5).Pos(1.234f, -2.5f, 300f).Text("终端 reboot");
        var payload = payloadWriter.Span.ToArray();
        var read = new PayloadReader(payload);
        var okNumbers = read.U() == 3 && read.U() == 1234567 && read.S() == -5;
        var (px, py, pz) = read.Pos();
        check(okNumbers && MathF.Abs(px - 1.23f) < 0.006f && MathF.Abs(py + 2.5f) < 0.006f && MathF.Abs(pz - 300f) < 0.006f && read.Text() == "终端 reboot" && read.AtEnd,
            "payload: numbers, centimetre positions and text round trip");
        var longText = new PayloadWriter().Reset().Text(new string('终', 100)).Span.ToArray();
        check(new PayloadReader(longText).Text().Length == 66, "payload: a long text is cut at 200 bytes without splitting a character");

        // Presence: the name and the gear JSON (about 3 KB, longer than a 200-byte text) follow the joined flag.
        var gear = string.Join("\n", Enumerable.Range(0, 4).Select(i => "{\"Ver\":1,\"Name\":\"枪" + i + "\",\"Packet\":{" + new string('a', 900) + "}}"));
        var presence = new PayloadWriter().Reset().U(2).U(1).Text("队友二").Text(gear, PayloadWriter.Capacity).Span.ToArray();
        var presenceRead = new PayloadReader(presence);
        var presenceOk = presenceRead.U() == 2 && presenceRead.U() == 1 && !presenceRead.AtEnd && presenceRead.Text() == "队友二" && presenceRead.Text() == gear && presenceRead.AtEnd;
        var reused = new PayloadReader().Load(presence);
        var reusedOk = reused.U() == 2 && reused.U() == 1 && reused.Text() == "队友二" && reused.Text() == gear;
        // Loaded again with a shorter payload, the reader stops at its end, not in the longer payload's bytes behind it; a count larger than the rest is refused before anything is made.
        reused.Load(new byte[] { 2 }); reused.U();
        var pastEnd = false; try { reused.U(); } catch (FormatException) { pastEnd = true; }
        var hugeCount = false; try { new PayloadReader(new PayloadWriter().Reset().U(100_000_000).U(1).Span.ToArray()).Count(); } catch (FormatException) { hugeCount = true; }
        check(presenceOk && reusedOk && pastEnd && hugeCount,
            "payload: a Presence event carries the whole gear JSON after the name, the reused reader holds it and stops at the end of a shorter one, and an impossible count is refused (" + presence.Length + " bytes, past end " + pastEnd + ", count " + hugeCount + ")");
        var overlong = new PayloadWriter().Reset().Text(new string('x', PayloadWriter.Capacity), PayloadWriter.Capacity).Span.ToArray();
        var fullRead = new PayloadReader(overlong);
        check(fullRead.Text().Length == PayloadWriter.Capacity - Compact.MaxVarBytes && fullRead.AtEnd,
            "payload: a text longer than the payload is cut to what is left, so its count and bytes always agree (" + overlong.Length + " bytes)");
        var kinds = typeof(EventKind).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Select(field => (ushort)field.GetRawConstantValue()!).ToList();
        check(kinds.Distinct().Count() == kinds.Count && new[] { EventKind.Sentry, EventKind.Mine, EventKind.ResourceBox, EventKind.Reactor, EventKind.Environment, EventKind.WeaponSound }.All(Timeline.IsStateEvent) && !Timeline.IsStateEvent(EventKind.Noise),
            "eventkinds: every event type number is unique and the persistent object kinds are state events");

        // What the libraries showed: the layouts of events 94 to 97 survive a write and a read, and the name table is a state kept in every keyframe.
        var shownWriter = new PayloadWriter();
        var play = new ShownFxRecord(ShownCodec.Play, 7, 0, 1.25f, -2f, 30.5f, 0f, 0.6f, 0.8f, float.NaN, ShownCodec.White, true, 0, 0);
        var playBack = ShownCodec.ReadFx(new PayloadReader(ShownCodec.Write(shownWriter.Reset(), play).Span.ToArray()));
        var beam = new ShownFxRecord(ShownCodec.Beam, (1 << 24) + 2, 41, 0, 1, 2, 10, 11, 12, 0.5f, 0xFF3010A0, true, 1.5f, 0);
        var beamBack = ShownCodec.ReadFx(new PayloadReader(ShownCodec.Write(shownWriter.Reset(), beam).Span.ToArray()));
        var decal = new ShownFxRecord(ShownCodec.Decal, 9, 0, 5, 5, 5, 0, 1, 0, 2f, 0xFFFFFFFE, false, 12f, 3f);
        var decalBack = ShownCodec.ReadFx(new PayloadReader(ShownCodec.Write(shownWriter.Reset(), decal).Span.ToArray()));
        var releaseBytes = ShownCodec.Write(shownWriter.Reset(), new ShownFxRecord(ShownCodec.Release, 0, 41, 0, 0, 0, 0, 0, 0, float.NaN, ShownCodec.White, true, 0, 0)).Span.ToArray();
        var releaseBack = ShownCodec.ReadFx(new PayloadReader(releaseBytes));
        check(playBack.Name == 7 && playBack.Ax == 1.25f && playBack.Bz > 0.79f && playBack.Bz < 0.81f && float.IsNaN(playBack.Size) && playBack.Colour == ShownCodec.White
            && beamBack.Key == 41 && beamBack.Bz == 12f && beamBack.Size == 0.5f && beamBack.Colour == 0xFF3010A0 && beamBack.Seconds == 1.5f
            && decalBack.Colour == 0xFFFFFFFE && !decalBack.Head && decalBack.Seconds == 12f && decalBack.Fade == 3f && decalBack.By > 0.99f
            && releaseBack.Kind == ShownCodec.Release && releaseBack.Key == 41 && releaseBytes.Length <= 3
            && EventKind.IsExtraState(EventKind.ShownHeld) && !EventKind.Ends(EventKind.ShownHeld, ShownCodec.Write(shownWriter.Reset(), beam).Span) && EventKind.Ends(EventKind.ShownHeld, releaseBytes),
            "shown effects: a play, a held beam, a decal and a release come back as written, a release costs three bytes, and a held thing is a kept state its release retires");
        var soundBack = ShownCodec.ReadSound(new PayloadReader(ShownCodec.Write(shownWriter.Reset(), new ShownSoundRecord(5, 1, true, 3f, 4f, 5f, 2, 1.1f, 0.8f, 77)).Span.ToArray()));
        var loopBytes = ShownCodec.Write(shownWriter.Reset(), new ShownSoundRecord(5, 1, true, 3f, 4f, 5f, 2, 1.1f, 0.8f, 77)).Span.ToArray();
        var endBytes = ShownCodec.Write(shownWriter.Reset(), new ShownSoundRecord(0, 0, false, 0, 0, 0, 0, 0, 0, 77)).Span.ToArray();
        var endBack = ShownCodec.ReadSound(new PayloadReader(endBytes));
        var (nameId, nameText) = ShownCodec.ReadName(new PayloadReader(ShownCodec.Name(shownWriter.Reset(), 12, "Mod/laser_fire").Span.ToArray()));
        check(soundBack.Name == 5 && soundBack.Self && soundBack.Place == 1 && soundBack.Variant == 2 && MathF.Abs(soundBack.Pitch - 1.1f) < 0.001f && MathF.Abs(soundBack.Gain - 0.8f) < 0.01f && soundBack.Loop == 77
            && endBack.Name == 0 && endBack.Loop == 77 && nameId == 12 && nameText == "Mod/laser_fire" && EventKind.IsExtraState(EventKind.ShownName) && !Timeline.IsStateEvent(EventKind.ShownFx)
            && EventKind.IsExtraState(EventKind.ShownLoop) && !EventKind.Ends(EventKind.ShownLoop, loopBytes) && EventKind.Ends(EventKind.ShownLoop, endBytes),
            "shown sounds: a loop's play and its end, and a name table entry, come back as written; the table entries and a running loop are kept states");
        // A named clip on a body: start and stop survive a write and a read, the key tells the body and track apart, and a stop retires the kept state.
        var poseStart = new ShownPoseRecord(false, 2, 17, 3, 5, -1250, true, 0.8f, 0.1f, 0.3f, 0b1010_0000_0001UL, new byte[] { 100, 50, 25 }, 0);
        var poseBytes = ShownCodec.Write(shownWriter.Reset(), poseStart).Span.ToArray();
        var poseBack = ShownCodec.ReadPose(new PayloadReader(poseBytes));
        var poseStopBytes = ShownCodec.Write(shownWriter.Reset(), new ShownPoseRecord(true, 2, 17, 3, 0, 0, false, 0, 0, 0, 0, null, 0.15f)).Span.ToArray();
        var poseStop = ShownCodec.ReadPose(new PayloadReader(poseStopBytes));
        check(!poseBack.Stopped && poseBack.Anchor == 2 && poseBack.Id == 17 && poseBack.Track == 3 && poseBack.Name == 5 && poseBack.StartMs == -1250 && poseBack.Loop
            && MathF.Abs(poseBack.Weight - 0.8f) < 0.001f && MathF.Abs(poseBack.FadeOut - 0.3f) < 0.001f && poseBack.Joints == poseStart.Joints && poseBack.Weights!.SequenceEqual(poseStart.Weights!)
            && poseStop.Stopped && poseStop.Id == 17 && poseStop.Track == 3 && MathF.Abs(poseStop.StopFade - 0.15f) < 0.001f
            && ShownCodec.PoseKey(1, 3, 0) != ShownCodec.PoseKey(2, 3, 0) && ShownCodec.PoseKey(1, 3, 0) != ShownCodec.PoseKey(1, 3, 1)
            && EventKind.IsExtraState(EventKind.ShownPose) && !EventKind.Ends(EventKind.ShownPose, poseBytes) && EventKind.Ends(EventKind.ShownPose, poseStopBytes),
            "shown poses: a clip start with its mask weights and its stop come back as written, bodies and tracks have their own keys, and a stop retires the kept state");
        // A model and the renderers hidden under a thing: each a whole state kept per key (a model and a hidden list never share one), a world-rooted hide carries its place, an end retires the state.
        var modelKey = ShownCodec.ModelKey(900, false);
        var modelBytes = ShownCodec.Write(shownWriter.Reset(), new ShownModelRecord(modelKey, ShownCodec.ModelPresent, 6, 1, 2, "a/b", 1.5f, 2f, -3f, 0f, 0.7071f, 0f, 0.7071f, 1f, 2f, 1f)).Span.ToArray();
        var modelBack = ShownCodec.ReadModel(new PayloadReader(modelBytes));
        var hideBytes = ShownCodec.Write(shownWriter.Reset(), new ShownModelRecord(ShownCodec.ModelKey(900, true), ShownCodec.ModelHidden, 0, ShownCodec.WorldRoot, 0, "", 4f, 5f, 6f, 0, 0, 0, 1, 1, 1, 1)).Span.ToArray();
        var hideBack = ShownCodec.ReadModel(new PayloadReader(hideBytes));
        var modelEndBytes = ShownCodec.Write(shownWriter.Reset(), ShownCodec.EndedModel(modelKey)).Span.ToArray();
        check(modelBack.Key == modelKey && modelBack.Name == 6 && modelBack.Anchor == 1 && modelBack.Id == 2 && modelBack.Path == "a/b" && modelBack.Z == -3f && MathF.Abs(modelBack.Qy - 0.7071f) < 0.0001f && modelBack.Sy == 2f
            && hideBack.Phase == ShownCodec.ModelHidden && hideBack.Anchor == ShownCodec.WorldRoot && hideBack.X == 4f && hideBack.Z == 6f && hideBack.Key != modelKey
            && EventKind.IsExtraState(EventKind.ShownModelState) && !EventKind.Ends(EventKind.ShownModelState, modelBytes) && !EventKind.Ends(EventKind.ShownModelState, hideBytes) && EventKind.Ends(EventKind.ShownModelState, modelEndBytes),
            "shown models: a model's state and a hidden list come back as written, they have their own keys, and an end retires the kept state");
        // Timeline: a jump restores the same world as playing up to that moment, from the nearest keyframe block.
        var tStream = new GatedStream();
        var tErrors = new List<string>();
        var tWriter = ChunkLog.Create(tStream, 1);
        static byte[] Spawn() => new PayloadWriter().Reset().U(4).U(77).U(0).Pos(1, 2, 3).U(0).Span.ToArray();
        static byte[] Door(ulong state) => new PayloadWriter().Reset().U(900).U(state).U(0).Pos(5, 5, 5).U(0).Span.ToArray();
        var tPipeline = new BlockPipeline(tWriter, tErrors.Add, (p, ms) =>
        {
            var (x, y, z) = Path(0, ms);
            p.Sample(ms, 0, x, y, z, 0f, 0f, 0f, 1f, 5);
            if (ms >= 2200 && ms < 8000) { p.Event(ms, EventKind.EnemySpawn, Spawn()); p.Sample(ms, 4, 1, 2, 3, 0f, 0f, 0f, 1f, 9); }
            if (ms >= 3000) p.Event(ms, EventKind.Door, Door(1));
        }, 0, blockMs: 1000, keyframeMs: 5000);
        for (uint ms = 0; ms < 12000; ms += 33)
        {
            var (x, y, z) = Path(0, ms);
            tPipeline.Sample(ms, 0, x, y, z, 0f, 0f, 0f, 1f, 5);
            if (ms >= 2200 && ms < 8000) { if (ms - 2200 < 33) tPipeline.Event(ms, EventKind.EnemySpawn, Spawn()); tPipeline.Sample(ms, 4, 1, 2, 3 + ms * 0.001f, 0f, 0f, 0f, 1f, 9); }
            if (ms >= 8000 && ms - 8000 < 33) tPipeline.Event(ms, EventKind.EnemyGone, new PayloadWriter().Reset().U(4).U(1).Span);
            if (ms >= 3000 && ms - 3000 < 33) tPipeline.Event(ms, EventKind.Door, Door(1));
            if (ms % 1000 < 33) tPipeline.WaitForWriter();
            tPipeline.Tick(ms);
        }
        tPipeline.Finish(12000);
        var timeline = new Timeline(ChunkLog.Open(new MemoryStream(tStream.ToArray()), 1));
        timeline.Seek(7300);
        var jumped = timeline.State.Enemies.ContainsKey(4) && timeline.State.Objects.ContainsKey((EventKind.Door, 900UL)) && timeline.State.Place(0, 7300, out var jx, out _, out _, out _, out _, out _, out _)
            && MathF.Abs(jx - Path(0, 7300).X) < 0.2f;
        timeline.Seek(9000);
        var afterGone = !timeline.State.Enemies.ContainsKey(4) && !timeline.State.Poses[4].Present;
        timeline.Seek(1000);
        var before = timeline.State.Enemies.Count == 0 && timeline.State.Objects.Count == 0;
        var played = new Timeline(ChunkLog.Open(new MemoryStream(tStream.ToArray()), 1));
        var seen = 0;
        for (uint ms = 100; ms <= 7300; ms += 100) played.Advance(ms, _ => seen++);
        var samePlace = played.State.Place(0, 7300, out var px0, out _, out _, out _, out _, out _, out _) && timeline.State.Enemies.Count == 0
            && played.State.Enemies.ContainsKey(4) && MathF.Abs(px0 - Path(0, 7300).X) < 0.2f && seen >= 2;
        // Forward past every block read so far right after a jump back (the decoder then stands before the enemy existed).
        var swept = new Timeline(ChunkLog.Open(new MemoryStream(tStream.ToArray()), 1));
        bool far;
        try { swept.Seek(6500); swept.Seek(1000); swept.Seek(11500); far = !swept.State.Enemies.ContainsKey(4) && swept.State.Objects.ContainsKey((EventKind.Door, 900UL)); }
        catch (FormatException) { far = false; }
        check(jumped && afterGone && before && samePlace && far && timeline.DurationMs >= 11000 && tErrors.Count == 0,
            "timeline: a jump restores enemies, object states and positions as playing does, forward and backward, and never reports events (jumped " + jumped + ", gone " + afterGone + ", before " + before + ", played " + samePlace + ", far after back " + far + ", events " + seen + ")");

        // Between samples the placed position moves evenly: the largest step in 5 ms is at most twice the mean step (a held sample would make steps of zero and one jump).
        var smooth = new Timeline(ChunkLog.Open(new MemoryStream(tStream.ToArray()), 1));
        float prevX = 0f, biggest = 0f, total = 0f; var steps = 0;
        for (uint ms = 2000; ms <= 5000; ms += 5)
        {
            smooth.Advance(ms, _ => { });
            if (!smooth.State.Place(0, ms, out var sx, out _, out _, out _, out _, out _, out _)) continue;
            if (ms > 2100) { var step = MathF.Abs(sx - prevX); biggest = MathF.Max(biggest, step); total += step; steps++; }
            prevX = sx;
        }
        check(steps > 0 && total > 0f && biggest <= 2f * total / steps, "timeline: the placed position moves evenly between samples (largest step " + biggest + ", mean " + (steps > 0 ? total / steps : 0f) + ")");

        // Lasting events (a spawned item, a lost limb, an enemy's end) are not written again by a keyframe: a jump gets the ones before its keyframe from the blocks
        // it has read, or from a timeline that read the whole recording (the analysis' one, adopted). Both, and the cache of decoded blocks (30 blocks, 20 held)
        // across jumps forwards and backwards, must give the world playing from the start gives.
        var lStream = new GatedStream(); var lErrors = new List<string>();
        static byte[] Item(uint id) => new PayloadWriter().Reset().U(id).Pos(1, 2, 3).U(2).Span.ToArray();
        var lPipeline = new BlockPipeline(ChunkLog.Create(lStream, 1), lErrors.Add, (p, ms) =>
        {
            var (x, y, z) = Path(0, ms);
            p.Sample(ms, 0, x, y, z, 0f, 0f, 0f, 1f, 5);
            if (ms >= 2200 && ms < 9000) { p.Event(ms, EventKind.EnemySpawn, Spawn()); p.Sample(ms, 4, 1, 2, 3, 0f, 0f, 0f, 1f, 9); }
        }, 0, blockMs: 1000, keyframeMs: 4000);
        for (uint ms = 0; ms < 30000; ms += 33)
        {
            var (x, y, z) = Path(0, ms);
            lPipeline.Sample(ms, 0, x, y, z, 0f, 0f, 0f, 1f, 5);
            if (ms >= 2200 && ms < 9000) lPipeline.Sample(ms, 4, 1, 2, 3 + ms * 0.001f, 0f, 0f, 0f, 1f, 9);
            void At(uint when, ushort kind, byte[] payload) { if (ms >= when && ms - when < 33) lPipeline.Event(ms, kind, payload); }
            At(1500, EventKind.ItemSpawn, Item(70)); At(25500, EventKind.ItemSpawn, Item(71)); At(2200, EventKind.EnemySpawn, Spawn());
            At(3000, EventKind.EnemyLimb, new PayloadWriter().Reset().U(4).U(2).Span.ToArray());
            At(9000, EventKind.EnemyGone, new PayloadWriter().Reset().U(4).U(1).Span.ToArray());
            if (ms % 1000 < 33) lPipeline.WaitForWriter();
            lPipeline.Tick(ms);
        }
        lPipeline.Finish(30000);
        var lBytes = lStream.ToArray();
        string World(Timeline t) => t.State.ItemSpawns.Count + "|" + string.Join(",", t.State.Enemies.Keys.OrderBy(k => k)) + "|" + t.State.LimbsGone[4];
        var plain = new Timeline(ChunkLog.Open(new MemoryStream(lBytes), 1));
        var adopted = new Timeline(ChunkLog.Open(new MemoryStream(lBytes), 1));
        var whole = new Timeline(ChunkLog.Open(new MemoryStream(lBytes), 1));
        whole.Seek(0); whole.Advance(whole.DurationMs, _ => { });
        adopted.AdoptLasting(whole);
        var lasting = true; var held = 0;
        foreach (var at in new uint[] { 29500, 1000, 17000, 3500, 29500, 2500, 22000, 5000, 9500, 3500, 25500, 8000 })
        {
            var fresh = new Timeline(ChunkLog.Open(new MemoryStream(lBytes), 1));
            for (uint ms = 100; ms <= at; ms += 100) fresh.Advance(ms, _ => { });
            fresh.Advance(at, _ => { });
            plain.Seek(at); adopted.Seek(at);
            lasting &= World(plain) == World(fresh) && World(adopted) == World(fresh);
            held = Math.Max(held, plain.CachedCount);
            if (at == 3500) lasting &= World(fresh) == "1|4|" + (1UL << 2);
            if (at == 29500) lasting &= World(fresh) == "2||0";
        }
        check(lasting && held > 0 && held <= 20 && lErrors.Count == 0,
            "timeline: lasting events from a whole read (adopted) and from the blocks a jump reads agree with playing from the start, and jumps through the cache of decoded blocks give the same world (cached up to " + held + ")");

        // Retired objects: a keyframe carries only what still exists.
        Retirement(retire: true, foams: 200, thrown: 50, survivors: 5); // warm up the JIT before timing
        var (kept, msKept, sizeKept) = Retirement(retire: true, foams: 200, thrown: 50, survivors: 5);
        var (leaked, msLeaked, sizeLeaked) = Retirement(retire: false, foams: 200, thrown: 50, survivors: 5);
        check(kept.Ok && !leaked.Ok && kept.StatesAfter == 10 && leaked.StatesAfter == 250,
            "retirement: after foam and thrown items are gone no keyframe holds them, a jump before or after their end and playing through agree, and a table that never retires is caught (kept " + kept.StatesAfter + " of " + leaked.StatesAfter + ")");
        if (Environment.GetEnvironmentVariable("REPLAY_TEST_VERBOSE") != null)
            Console.WriteLine("retirement table entries after the ends " + leaked.StatesAfter + " -> " + kept.StatesAfter + "; keyframe raw bytes (5 s apart, after the ends) " + sizeLeaked + " -> " + sizeKept + "; keyframe state-write ms " + msLeaked.ToString("0.000") + " -> " + msKept.ToString("0.000"));
        var ears = new Hearing(12f, 40f);
        check(NoiseRebuild.Reach(NoiseAction.Run, ears) == 12f && NoiseRebuild.Reach(NoiseAction.Shot, ears) == 40f && NoiseRebuild.Reach(NoiseAction.Melee, ears) == 12f
            && NoiseRebuild.Reach(NoiseAction.DoorOpen, ears) == 12f && NoiseRebuild.Reach(NoiseAction.DoorBreak, ears) == 40f,
            "noise: every rebuilt player action has a hearing distance, the game data's own where it has one and the estimate of the like sound for a melee hit and the doors");

        // Library: keep 20 plain recordings and every starred one, mark an unfinished file; a recording of another game build still plays.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-replay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder));
        void Make(string where, string name, int revision, bool finish)
        {
            using var file = new FileStream(System.IO.Path.Combine(where, name + ".frpl"), FileMode.Create);
            var log = ChunkLog.Create(file, ReplayHeader.FormatVersion);
            log.Append(ChunkTypes.Header, 0, 0, new ReplayHeader { ReplayVersion = "1", GameRevision = revision, RundownId = 1, Tier = 1, Index = 0, GenerationHash = "g", RecorderName = "n" }.Encode());
            log.Append(DataChunks.Keyframe, 0, 1000, new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }.Take(0).ToArray());
            if (finish) { log.Append(DataChunks.Footer, 1000, 1000, new RunSummary { Outcome = Outcome.Success, DurationMs = 1000, StartUnixMs = 1000, EndUnixMs = 2000, Players = new[] { new PlayerStats { Kills = 2 } } }.Encode()); log.Finish(); }
        }
        for (var i = 0; i < 23; i++) Make(dir, "202610" + (i + 1).ToString("00") + "-120000_R1A1", 7, i != 22);
        Make(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder), "20260101-120000_R2B1", 7, true);
        Make(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder), "20260102-120000_R2B2", 8, true);
        var listed = ReplayLibrary.List(dir);
        var newest = listed[0];
        var starred = listed.Where(e => e.Starred).ToList();
        check(listed.Count == 22 && newest.Level == "R1A1" && !newest.Complete && newest.Refusal == null && listed[1].Outcome == Outcome.Success && listed[1].Complete && listed[1].DurationMs == 1000 && listed[1].Summary?.Players[0].Kills == 2 && newest.Summary == null
            && starred.Count == 2 && starred.All(e => e.Refusal == null), "library: 20 recent plus the starred ones, newest first, unfinished marked, another game build not refused (listed " + listed.Count + ")");
        var pruned = ReplayLibrary.Prune(dir);
        check(pruned == 3 && Directory.GetFiles(dir, "*.frpl").Length == 20 && Directory.GetFiles(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder)).Length == 2 && File.Exists(System.IO.Path.Combine(dir, "20261023-120000_R1A1.frpl")),
            "library: pruning removes the oldest plain recordings beyond 20 and never a starred one (removed " + pruned + ")");
        // The disk limit: the oldest plain recordings go until the folder fits; starred ones count but stay, and the newest stays even when nothing fits.
        var plainFiles = Directory.GetFiles(dir, "*.frpl").OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal).ToList();
        var starredBytes = Directory.GetFiles(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder)).Sum(f => new FileInfo(f).Length);
        var fits = starredBytes + plainFiles.Take(5).Sum(f => new FileInfo(f).Length);
        var overSize = ReplayLibrary.Prune(dir, fits);
        var fivePlain = Directory.GetFiles(dir, "*.frpl").Length == 5 && ReplayLibrary.TotalBytes(dir) <= fits;
        ReplayLibrary.Prune(dir, 1);
        check(overSize == 15 && fivePlain && Directory.GetFiles(dir, "*.frpl").Length == 1 && File.Exists(plainFiles[0]) && Directory.GetFiles(System.IO.Path.Combine(dir, ReplayLibrary.StarredFolder)).Length == 2,
            "library: the disk limit removes the oldest plain recordings until the folder fits, never a starred one nor the newest (removed " + overSize + ")");
        Directory.Delete(dir, true);

        // Analysis: markers, statistics and kill attribution from the events; the director holds a camera and leaves it for a clearly busier player.
        var aStream = new GatedStream();
        var aPipeline = new BlockPipeline(ChunkLog.Create(aStream, 1), _ => { }, (p, ms) => { }, 0);
        static byte[] Ev(Action<PayloadWriter> build) { var w = new PayloadWriter().Reset(); build(w); return w.Span.ToArray(); }
        var live = new Analysis(true); var liveReader = new PayloadReader();
        void Put(uint ms, ushort kind, byte[] payload) { aPipeline.Event(ms, kind, payload); live.Take(ms, kind, liveReader.Load(payload)); }
        Put(100, EventKind.Shot, Ev(w => w.U(1).U(5).Pos(0, 0, 0).Pos(1, 1, 1).U(1).U(2)));
        Put(110, EventKind.Shot, Ev(w => w.U(1).U(5).Pos(0, 0, 0).Pos(1, 1, 1).U(1).U(1)));
        Put(120, EventKind.EnemyHurt, Ev(w => w.U(4).U(0).U(50).U(1).U(2).U(1).U(50).U(0).U(0).U(0).U(100).U(0).U(0).U(0)));
        Put(130, EventKind.EnemyHurt, Ev(w => w.U(4).U(1).U(30).U(2).U(2).U(1).U(30).U(1).U(0).U(0).U(100).U(0).U(0).U(0)));
        Put(140, EventKind.EnemyGone, Ev(w => w.U(4).U(1)));
        Put(150, EventKind.EnemyGone, Ev(w => w.U(5).U(0)));
        Put(160, EventKind.PlayerStatus, Ev(w => { w.U(3); for (var i = 0; i < 6; i++) w.U(0); w.U(1); }));
        Put(170, EventKind.PlayerStatus, Ev(w => { w.U(3); for (var i = 0; i < 6; i++) w.U(0); w.U(1); }));
        Put(180, EventKind.PlayerHurt, Ev(w => w.U(3).U(3).U(125).U(5)));
        Put(190, EventKind.Revive, Ev(w => w.U(3)));
        Put(200, EventKind.Door, Ev(w => w.U(9).U(2).U(0).Pos(0, 0, 0).U(0)));
        Put(210, EventKind.Door, Ev(w => w.U(9).U(2).U(0).Pos(0, 0, 0).U(0)));
        Put(220, EventKind.Chat, Ev(w => w.U(2).Text("hi")));
        Put(230, EventKind.Note, Ev(w => w.Text("mod.x").Text("round").Text("{\"n\":1}", 1024).U(1)));
        Put(240, EventKind.Note, Ev(w => w.Text("mod.x").Text("quiet").Text("").U(0)));
        aPipeline.Finish(1000);
        var analysis = Analysis.Run(new Timeline(ChunkLog.Open(new MemoryStream(aStream.ToArray()), 1)), exact: true);
        var sum = RunSummary.Decode(RunSummary.Of(Outcome.Failed, 1000, 5, 9, live, new RecordingHealth()).Encode());
        check(sum.Outcome == Outcome.Failed && sum.DurationMs == 1000 && sum.StartUnixMs == 5 && sum.EndUnixMs == 9 && sum.Players.Length == 4
            && Enumerable.Range(0, 4).All(i => sum.Players[i].Rays == analysis.Players[i].Rays && sum.Players[i].Kills == analysis.Players[i].Kills && sum.Players[i].Downs == analysis.Players[i].Downs
                && sum.Players[i].Revives == analysis.Players[i].Revives && Math.Abs(sum.Players[i].BulletDamage - analysis.Players[i].BulletDamage) < 0.01f && Math.Abs(sum.Players[i].DamageTaken - analysis.Players[i].DamageTaken) < 0.01f),
            "summary: the counts taken live as events are written, written in the footer and read back, equal the analysis of the whole file");
        check(analysis.Players[1].Rays == 2 && analysis.Players[1].RayHits == 1 && Math.Abs(analysis.Players[1].BulletDamage - 5f) < 0.01f && Math.Abs(analysis.Players[2].MeleeDamage - 3f) < 0.01f
            && analysis.Players[2].Kills == 1 && analysis.Players[1].Kills == 0 && analysis.Players[3].Downs == 1 && Math.Abs(analysis.Players[3].DamageTaken - 12.5f) < 0.01f && analysis.Players[3].Revives == 1
            && analysis.Markers.Count(m => m.Kind == MarkerKind.Door) == 1 && analysis.Chat.Count == 1 && analysis.Chat[0].Text == "hi" && analysis.Chat[0].Slot == 2,
            "analysis: rays and hits (a ray that ended on a wall is no hit), damage by kind, the kill to the last hitter, one down, damage taken, a revive, one marker for a repeated door state and the chat");
        check(analysis.Notes.Count == 2 && analysis.Notes[0] == new NoteLine(230, "mod.x", "round", "{\"n\":1}", true) && !analysis.Notes[1].Mark && analysis.Markers.Count(m => m.Kind == MarkerKind.Note) == 1,
            "notes: a mod's note comes back with its mod, kind and payload, and only a marked one is on the timeline");
        // Blame: the host's Wake names the player, what they were doing and the enemy that passed a wake-up on; a Wake with no detection names nothing.
        // A client has no Wake events: its line comes from the change into the wake-up state, once, with no waker.
        var blame = new Analysis(true); var client = new Analysis(false); var blameReader = new PayloadReader();
        foreach (var e in new[] { 10, 11, 12 }) blame.Take(900, EventKind.EnemySpawn, blameReader.Load(Ev(w => w.U((ulong)e).U(30).U(0).Pos(0, 0, 0).U(0))));
        blame.Take(1000, EventKind.Wake, blameReader.Load(Ev(w => w.U(10).U(0).U(50).U(0).U(0).U(2).U(0).U(2).U(4))));
        blame.Take(1010, EventKind.WakeUp, blameReader.Load(Ev(w => w.U(10).U(0).U(1).U(0).U(0))));
        blame.Take(1200, EventKind.Wake, blameReader.Load(Ev(w => w.U(11).U(1).U(30).U(200).U(11).U(5).U(0).U(0).U(0))));
        blame.Take(1300, EventKind.Wake, blameReader.Load(Ev(w => w.U(12).U(0).U(40).U(0).U(0).U(255).U(0).U(0).U(0))));
        client.Take(2000, EventKind.EnemyState, blameReader.Load(Ev(w => w.U(13).U(15))));
        client.Take(2050, EventKind.WakeUp, blameReader.Load(Ev(w => w.U(13).U(1).U(2).U(0).U(1))));
        var first = blame.Wakes.Count > 0 ? blame.Wakes[0] : default;
        check(blame.Wakes.Count == 2 && first.Entity == 10 && first.Waker == 1 && first.Cause == WakeCause.Sensed && first.Action == 4 && first.Chain == 1 && first.EnemyDataId == 30
            && blame.Wakes[1].Waker == -1 && blame.Wakes[1].Cause == WakeCause.Unknown && client.Wakes.Count == 1 && client.Wakes[0].Waker == -1 && client.Wakes[0].Cause == WakeCause.Unknown,
            "blame: the host's wake-up names the player and what they did, a wake-up passed on counts on its first enemy, a wake-up with no detection or from a client names nobody (host lines " + blame.Wakes.Count + ", client lines " + client.Wakes.Count + ")");
        // Host records: a client that has the host's snapshot counts the host's hit as exact, and a later snapshot brings the totals level with the host's own.
        var hosted = new Analysis(false); var hostReader = new PayloadReader();
        hosted.Take(100, EventKind.EnemySpawn, hostReader.Load(Ev(w => w.U(20).U(30).U(0).Pos(0, 0, 0).U(1000))));
        hosted.Take(150, EventKind.HostLink, hostReader.Load(Ev(w => HostLink.WriteLink(w, true, 15, new HostTotals[4]))));
        hosted.Take(200, EventKind.HostHit, hostReader.Load(Ev(w => HostLink.WriteHit(w, 20, new HostHit(7, 0, 1, 0, false, false, 50, 0, 0, 0)))));
        var afterHit = hosted.Settled && Math.Abs(hosted.Players[1].BulletDamage - 5f) < 0.01f;
        var host = new HostTotals[4]; host[1] = new HostTotals { BulletTenths = 100, Landed = 2 };
        hosted.Take(900, EventKind.HostLink, hostReader.Load(Ev(w => HostLink.WriteLink(w, true, 2, host))));
        check(afterHit && Math.Abs(hosted.Players[1].BulletDamage - 10f) < 0.01f && hosted.Players[1].Landed == 2,
            "host records: the host's hit counts exactly once the snapshot is in, and a later snapshot makes the totals the host's (" + hosted.Players[1].BulletDamage + " damage)");
        // A packet cut off, of the wrong type or with a count the rest cannot hold is refused as a whole, and nothing of it is handed out.
        var sender = new HostLink.Writer(); sender.Hit(new HostHit(9, 0, 1, 0, false, false, 20, 0, 100, 0)); var good = sender.Seal();
        var halved = good[..^2]; var typed = (byte[])good.Clone(); typed[0] = 0; var sink = new CountingRecords();
        check(HostLink.Read(good, sink) && sink.Hits == 1 && !HostLink.Read(halved, sink) && !HostLink.Read(typed, sink) && sink.Hits == 1,
            "host records: a sound packet is read, a cut-off or foreign one is refused and hands out nothing");
        // The extended records: a player hurt, a gift and a pack use travel beside the hits, and the snapshot's packs and supplies level the client.
        var extended = new HostLink.Writer();
        extended.Hurt(new HostHurt(2, 3, -1, 0, 125, 0));
        extended.Supply(new HostSupply(0, 2, 1, 0));
        extended.Pack(new HostPack(0, 0));
        extended.Ray(new HostRay(1, true, 0));
        var extTotals = new HostTotals[4];
        extTotals[0] = new HostTotals { Packs = 2, Supplied = 3 };
        extTotals[2] = new HostTotals { Supplied = 4 };
        extended.Snapshot(5, extTotals);
        var extSink = new CountingRecords();
        var extPacket = extended.Seal();
        check(HostLink.Read(extPacket, extSink) && extSink.Hurts == 1 && extSink.Supplies == 1 && extSink.Packs == 1 && extSink.Rays == 1
            && extSink.Snapshots == 1 && extSink.PacksTotal == 2 && extSink.Supplied == 3,
            "host records: a player hurt, a gift, a pack use, a shot and the extended snapshot are read back");
        var hostFacts = new Analysis(false) { RecorderSlot = 2 };
        hostFacts.Take(50, EventKind.Supply, hostReader.Load(Ev(w => w.U(3).U(0).U(1))));
        hostFacts.Take(100, EventKind.HostLink, hostReader.Load(Ev(w => HostLink.WriteLink(w, true, 5, extTotals))));
        hostFacts.Take(150, EventKind.HostRay, hostReader.Load(Ev(w => HostLink.WriteRay(w, new HostRay(1, true, 0)))));
        hostFacts.Take(160, EventKind.HostHit, hostReader.Load(Ev(w => HostLink.WriteHit(w, 20, new HostHit(7, 0, 1, 0, false, false, 50, 0, 0, 0)))));
        hostFacts.Take(200, EventKind.HostSupply, hostReader.Load(Ev(w => HostLink.WriteSupply(w, new HostSupply(0, 3, 0, 0)))));
        hostFacts.Take(300, EventKind.HostPack, hostReader.Load(Ev(w => HostLink.WritePack(w, new HostPack(0, 0)))));
        // A mode 1 snapshot of an older recording carries no packs or supplies: it must leave those counters as the recording counted them.
        hostFacts.Take(400, EventKind.HostLink, hostReader.Load(Ev(w =>
        {
            w.U(1).U(5);
            for (var slot = 0; slot < 4; slot++) { if ((5 >> slot & 1) == 0) continue; for (var i = 0; i < 8; i++) w.U(0); }
        })));
        check(hostFacts.HostWhole && hostFacts.Players[1].Rays == 1 && hostFacts.Players[1].RayHits == 1 && hostFacts.Players[1].BulletHits == 1
            && hostFacts.Players[0].Packs == 3 && hostFacts.Players[3].Supplied == 1 && hostFacts.Players[2].Supplied == 4 && hostFacts.Players[0].Supplied == 4,
            "host records: a client counts the host's shots, packs and gifts, keeps a gift it recorded itself before the first snapshot, and an older mode 1 snapshot does not lower them ("
            + hostFacts.Players[1].Rays + "," + hostFacts.Players[0].Packs + "," + hostFacts.Players[3].Supplied + "," + hostFacts.Players[2].Supplied + "," + hostFacts.Players[0].Supplied + ")");
        var clockStats = new StatsClock(analysis);
        var early = clockStats.Advance(125) && clockStats.Players[1].Rays == 2 && clockStats.Players[1].Kills == 0 && Math.Abs(clockStats.Players[1].BulletDamage - 5f) < 0.01f;
        clockStats.Advance(500);
        var full = clockStats.Players[2].Kills == 1 && clockStats.Players[3].Revives == 1;
        clockStats.Advance(125);
        check(early && full && clockStats.Players[2].Kills == 0 && clockStats.Players[1].Rays == 2 && analysis.Markers.All(m => m.Kind != MarkerKind.Kill),
            "stats clock: numbers up to the moment, forward and back; no kill is marked when no enemy is notable");
        var crowd = new WorldState();
        crowd.Poses[0].Present = true; crowd.Poses[1].Present = true; crowd.Poses[1].X = 100;
        for (var e = 4; e < 12; e++) { crowd.Poses[e].Present = true; crowd.Poses[e].State = 3; crowd.Poses[e].X = 2; }
        var quiet = new Analysis(true);
        var pick = Director.Choose(crowd, quiet, 10000, -1, 0);
        var stay = Director.Choose(crowd, quiet, 12000, 1, 9000);
        var leave = Director.Choose(crowd, quiet, 22000, 1, 9000);
        check(pick == 0 && stay == 1 && leave == 0, "director: starts on the player in the middle of enemies, keeps a camera for 12 s, then leaves it for a clearly busier player (" + pick + "," + stay + "," + leave + ")");

        var nStream = new GatedStream();
        var nPipeline = new BlockPipeline(ChunkLog.Create(nStream, 1), _ => { }, (p, ms) => { }, 0);
        nPipeline.Sample(50, 0, 0, 0, 0, 0, 0, 0, 1, 100);
        nPipeline.Sample(50, 2, 10, 0, 0, 0, 0, 0, 1, 100);
        nPipeline.Event(100, EventKind.Scan, Ev(w => w.U(7).U(1).U(0).Pos(11, 0, 0)));
        nPipeline.Event(110, EventKind.Terminal, Ev(w => w.U(8).Pos(500, 0, 0).Text("x")));
        nPipeline.Event(120, EventKind.WorldEvent, Ev(w => w.U(5).U(1).U(0).U(0).U(0).U(0).U(250).U(0).U(1).U(0).U(0).U(0).U(0).U(0).U(0).Pos(0, 0, 0).Text("<color=red>Intel</color>").Text("").Text("").Text("Hello").Text("")));
        nPipeline.Event(125, EventKind.WorldEvent, Ev(w => w.U(5).U(1).U(0).U(0).U(0).U(0).U(0).U(0).U(1).U(0).U(0).U(0).U(0).U(0).U(0).Pos(0, 0, 0).Text("").Text("").Text("").Text("").Text("")));
        nPipeline.Event(130, EventKind.Bulkhead, Ev(w => w.U(3).U(1).U(0).Pos(20, 0, 0)));
        nPipeline.Event(140, EventKind.Holopath, Ev(w => w.U(4).U(1).U(0).Pos(30, 0, 0).Pos(0, 0, 0).Pos(30, 0, 0)));
        nPipeline.Finish(1000);
        var near = Analysis.Run(new Timeline(ChunkLog.Open(new MemoryStream(nStream.ToArray()), 1)), exact: true);
        check(near.Markers.Count(m => m.Kind == MarkerKind.Scan && m.Near == 2) == 1 && near.Markers.Count(m => m.Kind == MarkerKind.Terminal && m.Near == -1) == 1,
            "analysis: a scan at a place is tied to the nearest player, a terminal far from everybody to nobody");        // A level event with words is one mark with its type, delay and words kept for the hover, its subtitle a game line at the moment it happens; one with none is no mark.
        // The level machines (bulkhead, holopath, ...) are kept as the latest state per object, so a jump finds them.
        var worldTimeline = new Timeline(ChunkLog.Open(new MemoryStream(nStream.ToArray()), 1));
        worldTimeline.Seek(500);
        check(near.Markers.Count(m => m.Kind == MarkerKind.WorldEvent) == 1 && near.WorldEvents.Count == 1 && near.WorldEvents[0].Line.Intel == "Intel" && near.WorldEvents[0].Line.Type == 5 && Math.Abs(near.WorldEvents[0].Line.DelayS - 2.5f) < 0.01f
            && near.GameLines.Count == 1 && near.GameLines[0].Text == "Hello" && near.GameLines[0].TimeMs == 2620
            && worldTimeline.State.Extras.ContainsKey((EventKind.Bulkhead, 3UL)) && worldTimeline.State.Extras.ContainsKey((EventKind.Holopath, 4UL)),
            "world events: one mark for an accepted event with words, none for one without, the subtitle due after its delay; the level machines persist in the jump state");
        // A jump rebuilds scans and clusters from the state at the moment on any path: forward past the finish, back into the scan, forward again.
        var scanStream = new GatedStream();
        var scanPipeline = new BlockPipeline(ChunkLog.Create(scanStream, 1), _ => { }, (p, ms) => { }, 0);
        scanPipeline.Sample(50, 0, 0, 0, 0, 0, 0, 0, 1, 100);
        scanPipeline.Event(60, EventKind.EnemySpawn, Spawn());
        void Status(uint ms, ushort kind, ulong key, ulong status) => scanPipeline.Event(ms, kind, Ev(w => w.U(key).U(status).U(0).Pos(1, 0, 0)));
        Status(100, EventKind.Cluster, 9, 2);
        Status(110, EventKind.Scan, 7, 3);
        Status(120, EventKind.Holopath, 4, 1);
        Status(400, EventKind.Scan, 7, 4);
        Status(410, EventKind.Cluster, 9, 3);
        Status(420, EventKind.Scan, 8, 3);
        scanPipeline.Event(500, EventKind.EnemyGone, Ev(w => w.U(4).U(1)));
        scanPipeline.Finish(1000);
        var scanLine = new Timeline(ChunkLog.Open(new MemoryStream(scanStream.ToArray()), 1));
        static ulong StatusOf(PuzzleRebuild.Step step) { var read = new PayloadReader(step.Payload!); read.U(); return read.U(); }
        static string Plan(List<PuzzleRebuild.Step> steps) => string.Join(" ", steps.Select(s => s.Kind + ":" + s.Key + "=" + (s.Payload == null ? "off" : StatusOf(s).ToString())));
        var scanTouched = new[] { (EventKind.Holopath, 4UL), (EventKind.Scan, 8UL), (EventKind.Scan, 7UL), (EventKind.Cluster, 9UL) };
        scanLine.Seek(900);
        var finishedPlan = Plan(PuzzleRebuild.Steps(scanTouched, scanLine.State));
        scanLine.Seek(200);
        var midPlan = Plan(PuzzleRebuild.Steps(scanTouched, scanLine.State));
        scanLine.Seek(900);
        var againPlan = Plan(PuzzleRebuild.Steps(scanTouched, scanLine.State));
        check(finishedPlan.StartsWith("82:9=off 21:8=off 21:7=off 82:9=3 ") && finishedPlan.Contains("21:7=4") && finishedPlan.Contains("21:8=3") && !finishedPlan.Contains("83:")
            && midPlan.StartsWith("82:9=off 21:8=off 21:7=off 82:9=2 21:7=3 ") && midPlan.EndsWith("21:8=off") && againPlan == finishedPlan,
            "scan rebuild: off first, clusters before scans, each ends in its state at the moment on any jump path, a scan with no state yet stays off, the holopath is no step (" + finishedPlan + " | " + midPlan + ")");
        // A death and jumps round it: the enemy is in the world exactly until its end, however often it is passed; a body is hidden on time or at once by a jump, so a Tank that died never stays beside a new one.
        var enemyCounts = new List<int>();
        foreach (var ms in new uint[] { 300, 700, 300, 700, 300, 900 }) { scanLine.Seek(ms); enemyCounts.Add(scanLine.State.Enemies.Count); }
        var hiddenBodies = new List<string>();
        var bodies = new CorpseBook<string>();
        bodies.Add("played", 0f); bodies.Add("jumped", 10f);
        bodies.Due(CorpseBook<string>.KeepSeconds - 1f, hiddenBodies.Add);
        bodies.Due(CorpseBook<string>.KeepSeconds, hiddenBodies.Add);
        var bodiesLeft = bodies.Count;
        bodies.All(hiddenBodies.Add);
        check(string.Join("", enemyCounts) == "101010" && bodiesLeft == 1 && bodies.Count == 0 && string.Join(" ", hiddenBodies) == "played jumped",
            "enemy end: one enemy before its end and none after on every jump, bodies go on time or at a jump (" + string.Join("", enemyCounts) + " / " + string.Join(" ", hiddenBodies) + ")");
        var empty = new WorldState();
        empty.Poses[0].Present = true; empty.Poses[1].Present = true; empty.Poses[1].X = 50;
        var place = new Analysis(true);
        place.Markers.Add(new Marker(9000, MarkerKind.Alarm, -1, 1));
        place.Markers.Add(new Marker(9500, MarkerKind.Kill, 0));
        check(Director.Choose(empty, place, 10000, -1, 0) == 1 && Director.Choose(empty, new Analysis(true), 10000, -1, 0) == 0,
            "director: an alarm at a place counts for the player nearest to it and outweighs a kill");
        // A big enemy hunting next to a player is worth watching; asleep, or not big, it is not.
        var boss = new WorldState();
        boss.Poses[0].Present = true;
        boss.Poses[20].Present = true; boss.Poses[20].State = 3; boss.Poses[20].X = 5; boss.Enemies[20] = (77, 0, 0f);
        boss.Poses[21].Present = true; boss.Poses[21].State = 3; boss.Poses[21].X = 4; boss.Enemies[21] = (5, 0, 0f);
        var watchBoss = Director.Choose(boss, new Analysis(true), 10000, -1, 0, id => id == 77);
        boss.Poses[20].State = 1;
        var sleepingBoss = Director.Choose(boss, new Analysis(true), 10000, -1, 0, id => id == 77);
        check(watchBoss == 20 && sleepingBoss == 0, "director: a big enemy hunting beside a player is chosen over the player, a sleeping one is not (" + watchBoss + "," + sleepingBoss + ")");

        // Choosing an enemy: hunting first, big ones before small, then the nearest; beyond reach none. The one in the middle of the view.
        var foes = new WorldState();
        void Foe(int entity, ulong data, byte alert, float x) { foes.Poses[entity].Present = true; foes.Poses[entity].State = alert; foes.Poses[entity].X = x; foes.Enemies[entity] = (data, 0, 0f); }
        Foe(5, 1, 1, 2); Foe(6, 1, 3, 20); Foe(7, 77, 3, 30); Foe(8, 1, 3, 50);
        var ranked = new List<int>();
        EnemyPick.Ranked(foes, 0, 0, 0, id => id == 77, ranked);
        var aimed = EnemyPick.Aimed(foes, 0, 1, 0, 1, 0, 0);
        var aimedNone = EnemyPick.Aimed(foes, 0, 1, 0, 0, 0, 1);
        check(ranked.SequenceEqual(new[] { 7, 6, 5 }) && EnemyPick.Next(ranked, 7) == 6 && EnemyPick.Next(ranked, 5) == 7 && EnemyPick.Next(ranked, 99) == 7 && aimed == 5 && aimedNone == -1,
            "enemy pick: hunting before asleep, big before small, then nearest, out of reach left out; cycling wraps; the aimed one is the nearest the middle (" + string.Join(",", ranked) + " aimed " + aimed + ")");

        // Quiet stretches: ten seconds or more without activity play fast, ending two seconds before the next; nine seconds do not.
        var paced = new Analysis(true); var pacedReader = new PayloadReader();
        foreach (var ms in new uint[] { 500, 25500, 35500 }) paced.Take(ms, EventKind.Shot, pacedReader.Load(Ev(w => w.U(1).U(5).Pos(0, 0, 0).Pos(1, 1, 1).U(1).U(2))));
        var spans = Pacing.IdleSpans(paced.Busy, 40000);
        check(spans.Count == 1 && spans[0] == new IdleSpan(1000, 23000) && Pacing.Idle(spans, 10000) && !Pacing.Idle(spans, 23500) && !Pacing.Idle(spans, 30000),
            "pacing: a quiet stretch of ten seconds or more is found and ends two seconds before activity, a shorter one is not (" + string.Join(" ", spans) + ")");

        // Highlights: a streak of three kills, a boss kill (jumping back no earlier than it came), a revive by a player low on health, a wipe;
        // two kills too far apart, a revive at full health and one nobody stood by are not. A down rewinds five seconds.
        var moments = new Analysis(true);
        foreach (var (ms, slot) in new[] { (1000u, 1), (2000u, 1), (4500u, 1), (10000u, 2), (14000u, 2) }) moments.Kills.Add(new KillLine(ms, slot, 0, 1, 0, true));
        moments.Kills.Add(new KillLine(20000, 2, 0, 99, 0, true, 50, 17000));
        moments.Rescues.Add(new RescueLine(30000, 0, 3, 0.2f)); moments.Rescues.Add(new RescueLine(31000, 0, 3, 0.9f)); moments.Rescues.Add(new RescueLine(32000, 0, -1, 0.1f));
        moments.Wipes.Add(40000);
        moments.Markers.Add(new Marker(39000, MarkerKind.Down, 1));
        var picked = Highlights.Pick(moments, id => id == 99);
        check(picked.Count == 4 && picked[0].Kind == HighlightKind.MultiKill && picked[0].Slot == 1 && picked[0].Count == 3 && picked[0].JumpMs == 0
            && picked[1].Kind == HighlightKind.BossKill && picked[1].Entity == 50 && picked[1].JumpMs == 17000 && picked[2].Kind == HighlightKind.Rescue && picked[2].Slot == 3
            && picked[3].Kind == HighlightKind.Wipe && picked[3].JumpMs == 35000 && picked[3].Score > 12f
            && Highlights.DownAt(moments, 39000) == 1 && Highlights.Before(39000) == 34000 && Highlights.Before(3000) == 0,
            "highlights: a kill streak, a boss kill, a low-health revive and a wipe are picked and scored with what happened round them; a down rewinds five seconds (" + string.Join(" ", picked.Select(h => h.Kind)) + ")");

        // Member rating: fixed anchors put the owner at S and the low anchor at D, a dimension is the mean of its known numbers and the total the mean of
        // the known dimensions; too few dimensions give no grade, and the cache survives its file.
        var anchors = new Dictionary<string, string>
        {
            ["damagePerMinute"] = "{\"owner\":400,\"low\":0}",
            ["killsPerMinute"] = "{\"owner\":10,\"low\":0}",
            ["weakShare"] = "{\"owner\":1,\"low\":0}",
            ["takenPerMinute"] = "{\"owner\":0,\"low\":10}",
            ["downsPerMinute"] = "{\"owner\":0,\"low\":1}",
            ["rescuesPerMinute"] = "{\"owner\":1,\"low\":0}",
            ["gavePerMinute"] = "{\"owner\":1,\"low\":0}",
            ["interactionsPerMinute"] = "{\"owner\":1,\"low\":0}",
        };
        var metricsJson = string.Join(",", RatingMetrics.All.Select(m => "\"" + m.Key + "\":" + anchors[m.Key]));
        var ratingTable = RatingTable.Parse("{\"schemaVersion\":\"1.0.0\",\"minSeconds\":120,\"minDimensions\":3,\"grades\":[{\"name\":\"S\",\"from\":0.9},{\"name\":\"B\",\"from\":0.5},{\"name\":\"D\",\"from\":0}],\"metrics\":{" + metricsJson + "}}");
        MemberValues Member(double damage, double kills, double downs)
        {
            var m = new MemberValues();
            m.Value[RatingMetrics.IndexOf("damagePerMinute")] = damage;
            m.Value[RatingMetrics.IndexOf("killsPerMinute")] = kills;
            m.Value[RatingMetrics.IndexOf("weakShare")] = 0.5;
            m.Value[RatingMetrics.IndexOf("takenPerMinute")] = 5;
            m.Value[RatingMetrics.IndexOf("downsPerMinute")] = downs;
            m.Value[RatingMetrics.IndexOf("rescuesPerMinute")] = 0.5;
            m.Value[RatingMetrics.IndexOf("gavePerMinute")] = 0.5;
            m.Value[RatingMetrics.IndexOf("interactionsPerMinute")] = 0.5;
            m.Estimated = 1u << RatingMetrics.IndexOf("weakShare");
            return m;
        }
        var rated = MemberRating.Rate(ratingTable, Member(300, 5, 0.5));
        var thin = new MemberValues(); thin.Value[RatingMetrics.IndexOf("damagePerMinute")] = 300; thin.Value[RatingMetrics.IndexOf("downsPerMinute")] = 0.5;
        var fewDimensions = MemberRating.Rate(ratingTable, thin);
        var cache = new RatingCache();
        cache.Put("20261109-154432_34-D1.frpl", "f1", 300000, new[] { (2, Member(300, 5, 0.5)) });
        var again = RatingCache.Read(cache.Write());
        var wrongTaken = string.Join(",", RatingMetrics.All.Select(m => "\"" + m.Key + "\":" + (m.Key == "takenPerMinute" ? "{\"owner\":10,\"low\":0}" : anchors[m.Key])));
        var ratingRefused = 0;
        foreach (var bad in new[]
        {
            "{\"schemaVersion\":\"1.0.0\"}",
            "{\"schemaVersion\":\"1.0.0\",\"minSeconds\":120,\"minDimensions\":3,\"grades\":[{\"name\":\"S\",\"from\":0.9},{\"name\":\"D\",\"from\":0}],\"metrics\":{}}",
            "{\"schemaVersion\":\"1.0.0\",\"minSeconds\":120,\"minDimensions\":3,\"grades\":[{\"name\":\"S\",\"from\":0.9},{\"name\":\"D\",\"from\":0}],\"metrics\":{" + wrongTaken + "}}",
        })
            try { RatingTable.Parse(bad); } catch (FormatException) { ratingRefused++; }
        var killsAt = RatingMetrics.IndexOf("killsPerMinute");
        check(rated.State == RatingState.Rated && rated.Grade == 1 && Math.Abs(rated.Score!.Value - 0.5570833333333334) < 1e-9
            && Math.Abs(rated.Dimensions[0].Score!.Value - 0.7175) < 1e-9 && Math.Abs(rated.Dimensions[3].Score!.Value - 0.525) < 1e-9
            && !rated.Dimensions[0].Estimated && rated.Dimensions[2].Estimated
            && fewDimensions.State == RatingState.TooFewDimensions && fewDimensions.Grade == -1 && fewDimensions.Score == null
            && again.Recordings.Count == 1 && again.MemberOf("20261109-154432_34-D1.frpl", 2) is { } slotTwo && slotTwo.Value[killsAt] == 5 && again.MemberOf("20261109-154432_34-D1.frpl", 0) == null
            && ratingRefused == 3,
            "rating: fixed anchors put the owner at S and the low at D, a dimension is its known numbers' mean, too few dimensions give no grade, and the cache survives its file");
        var health = new EnemyHealthTrack();
        health.Spawned(7, 1000); health.Add(7, 2000, 40f); health.Add(7, 3000, 10f);
        health.Spawned(7, 5000); health.Add(7, 6000, 55f);
        var h1 = health.At(7, 2500, out var r1); var h2 = health.At(7, 3500, out var r2); var h3 = health.At(7, 1500, out _); var h4 = health.At(7, 5500, out _); var h5 = health.At(7, 6500, out var r5);
        check(h1 && r1 == 40f && h2 && r2 == 10f && !h3 && !h4 && h5 && r5 == 55f && !health.At(8, 2500, out _),
            "enemy health: the synced health at a moment is the one after the last loss of the spawn alive then, and a number used again by the next enemy starts its own run");
        // The screen's zones at 16:9, 16:10, 21:9 and 4:3 (HUD units: the height is the same at every resolution, 720p included), with the default and a wide
        // safe margin: none over another, the four cards equal and inside their zone, everything on the screen, and a deliberately overlapping pair caught.
        var zonesOk = !new HudBox(0f, 0f, 10f, 10f).Overlaps(new HudBox(10f, 0f, 20f, 10f)) && new HudBox(0f, 0f, 10f, 10f).Overlaps(new HudBox(9f, 9f, 20f, 20f));
        foreach (var width in new[] { 2560f, 2304f, 3360f, 1920f })
            foreach (var margin in new[] { 48f, 120f })
            {
                var screen = new HudBox(-width / 2f, -720f, width / 2f, 720f);
                var z = ReplayZones.Of(screen, margin, 204f + margin - 48f, 84f, 12f, 600f, 152f);
                var height = z.CardHeight(214f, 6f);
                var boxes = new List<HudBox> { z.Bottom, z.Mission, z.Feed, z.Middle };
                for (var i = 0; i < 4; i++) boxes.Add(z.Card(i, height, 6f));
                for (var a = 0; a < boxes.Count; a++)
                {
                    var box = boxes[a];
                    zonesOk &= box.Width > 0f && box.Height > 0f && box.Left >= screen.Left + margin && box.Right <= screen.Right - margin && box.Bottom >= screen.Bottom + margin && box.Top <= screen.Top - margin;
                    for (var b = a + 1; b < boxes.Count; b++) zonesOk &= !box.Overlaps(boxes[b]);
                }
                zonesOk &= height >= 180f && z.Card(3, height, 6f).Bottom >= z.Cards.Bottom && z.Control.Bottom > z.Bottom.Bottom && z.Control.Top == z.Bottom.Top && z.Panels(z.Work.Top, 12f).Right < z.Cards.Left;
            }
        check(zonesOk, "screen zones: at every screen shape and margin no zone covers another, the four equal cards fit, and an overlap is caught");
    }
}
