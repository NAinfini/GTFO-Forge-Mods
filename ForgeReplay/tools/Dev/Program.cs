using ForgeReplay;

// Development: REPLAY_RATING_DIR=<a folder of .frpl> with REPLAY_NATIVE_ROWS and REPLAY_GAMEDATA prints the numbers the member rating reads.
if (Environment.GetEnvironmentVariable("REPLAY_RATING_DIR") is { Length: > 0 } ratingFolder)
    RatingCalibration.Report(ratingFolder, Environment.GetEnvironmentVariable("REPLAY_NATIVE_ROWS") ?? throw new InvalidOperationException("REPLAY_RATING_DIR needs REPLAY_NATIVE_ROWS."),
        Environment.GetEnvironmentVariable("REPLAY_GAMEDATA"), Environment.GetEnvironmentVariable("REPLAY_RATING_TABLE"));

// Development: REPLAY_FILE=<a .frpl recorded in game> prints what it holds, to compare with the log of that run.
if (Environment.GetEnvironmentVariable("REPLAY_FILE") is { Length: > 0 } file)
{
    using var stream = File.OpenRead(file);
    var reader = ForgeCore.ChunkLog.Open(stream, ReplayHeader.FormatVersion);
    var decoder = new SampleDecoder();
    var events = new SortedDictionary<ushort, int>();
    var times = new Dictionary<ushort, List<uint>>();
    var dataIdOf = new Dictionary<int, int>();
    var tongues = new List<(int Entity, int Phase, uint TimeMs)>();
    var hurts = new List<(int Entity, uint TimeMs)>();
    var hits = new List<(int Entity, uint TimeMs, int Type)>();
    var perEntity = new SortedDictionary<int, (int Count, byte MinState, byte MaxState, Record First, Record Last)>();
    var looks = new List<(int Slot, uint TimeMs, float Pitch)>();
    // Degrees above the horizon of the direction a recorded rotation looks along (the recorder stores LookRotation of the player's look direction).
    static float LookPitch(Record r)
    {
        var y = 2f * (r.Qy * r.Qz - r.Qw * r.Qx); // the y of q * (0,0,1)
        return MathF.Asin(Math.Clamp(y, -1f, 1f)) * 180f / MathF.PI;
    }
    foreach (var chunk in reader.Chunks)
    {
        if (chunk.Type == 0 /* header */ || chunk.Type == DataChunks.Footer) continue;
        try
        {
            decoder.Decode(reader.Read(chunk), chunk.RawLength, chunk.StartMs, chunk.Type == DataChunks.Keyframe, r =>
        {
            if (r.Kind == RecordKind.Event)
            {
                events[r.EventType] = events.GetValueOrDefault(r.EventType) + 1;
                if (!times.TryGetValue(r.EventType, out var list)) times[r.EventType] = list = new List<uint>();
                list.Add(r.TimeMs);
                if (r.EventType == EventKind.EnemySpawn) { var p = new PayloadReader(r.Payload!); var entity = (int)p.U(); dataIdOf[entity] = (int)p.U(); }
                if (r.EventType == EventKind.EnemyHurt) { var p = new PayloadReader(r.Payload!); hurts.Add(((int)p.U(), r.TimeMs)); }
                if (r.EventType == EventKind.EnemyHit) { var p = new PayloadReader(r.Payload!); var entity = (int)p.U(); hits.Add((entity, r.TimeMs, (int)p.U())); }
                if (r.EventType == EventKind.Tongue) { var p = new PayloadReader(r.Payload!); tongues.Add(((int)p.U(), (int)p.U(), r.TimeMs)); }
                if (r.EventType == EventKind.Wake) { var p = new PayloadReader(r.Payload!); Console.WriteLine($"wake at {r.TimeMs} ms: enemy {p.U()} propagated {p.U()} distance {p.U() / 10f} m delay {p.U()} ms propagator {p.U()}"); }
                return;
            }
            if (r.Entity < 4) looks.Add((r.Entity, r.TimeMs, LookPitch(r)));
            perEntity.TryGetValue(r.Entity, out var e);
            perEntity[r.Entity] = e.Count == 0 ? (1, r.State, r.State, r, r) : (e.Count + 1, Math.Min(e.MinState, r.State), Math.Max(e.MaxState, r.State), e.First, r);
        });
        }
        catch (FormatException error) { Console.WriteLine($"BAD chunk type {chunk.Type} {chunk.StartMs}-{chunk.EndMs} ms, {chunk.RawLength} bytes: {error.Message}"); }
    }
    Console.WriteLine($"complete={reader.IsComplete} chunks={reader.Chunks.Count} bytes={stream.Length}");
    var names = typeof(EventKind).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).ToDictionary(f => (ushort)f.GetRawConstantValue()!, f => f.Name);
    foreach (var (type, count) in events)
    {
        // A kind a keyframe repeats (state of level objects) has hundreds of entries; the times matter for the rare ones, where a screenshot is wanted.
        var when = count <= 40 ? " at " + string.Join(",", times[type].Distinct().Take(14)) + " ms" : "";
        Console.WriteLine($"event {type} {names.GetValueOrDefault(type, "?")}: {count}{when}");
    }
    // A hit reaction (EnemyHit) is played by the game's own state on the puppet and posts the hurt voice itself; how many hurts (EnemyHurt) it comes with,
    // by the enemy's data block, tells where the presenter's own hurt voice would play twice. Hurts are counted once per entity and 400 ms, as the presenter does.
    foreach (var group in hurts.GroupBy(h => dataIdOf.GetValueOrDefault(h.Entity, -1)).OrderBy(g => g.Key))
    {
        var voiced = 0; var withHit = 0; var last = new Dictionary<int, uint>();
        foreach (var (entity, time) in group.OrderBy(h => h.TimeMs))
        {
            if (last.TryGetValue(entity, out var before) && time - before < 400) continue;
            last[entity] = time; voiced++;
            if (hits.Any(h => h.Entity == entity && Math.Abs((long)h.TimeMs - time) <= 100)) withHit++;
        }
        Console.WriteLine($"hurt voices of enemy data block {group.Key}: {voiced} played by the presenter, {withHit} with a hit reaction of the same enemy within 100 ms");
    }
    // The tongue / tentacle events by the enemy that made them (its data block id): a tank's tentacle is a different id from a striker's tongue.
    foreach (var group in tongues.GroupBy(t => dataIdOf.GetValueOrDefault(t.Entity, -1)).OrderBy(g => g.Key))
        Console.WriteLine($"tongue events of enemy data block {group.Key}: {group.Count()} (phase 0 out {group.Count(t => t.Phase == 0)}, phase 1 back {group.Count(t => t.Phase == 1)}), first at {group.Min(t => t.TimeMs)} ms");
    foreach (var chunk in reader.Chunks)
    {
        if (chunk.Type == 0)
        {
            var h = ReplayHeader.Decode(reader.Read(chunk));
            Console.WriteLine($"header: {h.Players.Count} players, loadout lines {string.Join("/", h.Players.Select(p => p.Loadout.Split('\n').Count(l => l.Length > 0)))}, recorder host={h.RecorderIsHost}");
            // The whole header on one line, so two runs of the same level and seeds can be compared field by field.
            Console.WriteLine("header-json: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                h.ReplayVersion,
                h.GameRevision,
                level = new { h.RundownId, h.Tier, h.Index },
                seeds = new { build = h.BuildSeed, hostId = h.HostIdSeed, session = h.SessionSeed },
                h.RecorderName,
                h.RecorderIsHost,
                start = h.Start.ToString(),
                players = h.Players.Select(p => new { p.Slot, p.Name, p.Loadout }),
                generation = new { whole = h.GenerationHash, sections = h.GenerationSections.Select(s => new { s.Name, s.Hash }) },
                plugins = h.Plugins.Select(p => new { p.Guid, p.Version, p.DllHash }),
            }));
        }
        if (chunk.Type == DataChunks.Footer) { var f = RunSummary.Decode(reader.Read(chunk)); Console.WriteLine($"footer: {f.Outcome} {f.DurationMs} ms, players " + string.Join(" | ", f.Players.Select((p, i) => $"{i}: dmg {p.BulletDamage + p.MeleeDamage + p.ExplosionDamage:F1} kills {p.Kills} downs {p.Downs} revives {p.Revives} rays {p.Rays}/{p.RayHits} taken {p.DamageTaken:F1}"))); }
    }
    // The look of each recorded player, ten seconds at a time: the pitch the eye view and the first-person view show.
    foreach (var slot in looks.Select(l => l.Slot).Distinct().OrderBy(s => s))
    {
        var mine = looks.Where(l => l.Slot == slot).ToList();
        Console.WriteLine($"look of player {slot}: {mine.Count} samples, pitch {mine.Min(l => l.Pitch):F1}..{mine.Max(l => l.Pitch):F1} degrees, {mine.Count(l => l.Pitch > 45f)} samples above 45");
        foreach (var bucket in mine.GroupBy(l => l.TimeMs / 10000).OrderBy(g => g.Key))
            Console.WriteLine($"  {bucket.Key * 10}-{bucket.Key * 10 + 10} s: pitch {bucket.Min(l => l.Pitch):F1}..{bucket.Max(l => l.Pitch):F1}, last {bucket.Last().Pitch:F1}");
    }
    // The client-side inferences (PLAN 5.6) run on this file, set against what the host recorded exactly.
    stream.Position = 0;
    var readback = ForgeCore.ChunkLog.Open(stream, uint.TryParse(Environment.GetEnvironmentVariable("REPLAY_FORMAT"), out var format) ? format : ReplayHeader.FormatVersion);
    var timeline = new Timeline(readback);
    // REPLAY_GAMEDATA=<a folder with the game's Enemy.json, EnemyDetection.json and Player.json data blocks> gives the inference the hearing
    // distances and the full health the game gives in game (the website's copy of the game data is one: Web/public/data/maps/game).
    var fullHealth = 0f; Func<ulong, Hearing?>? hearingOf = null;
    if (Environment.GetEnvironmentVariable("REPLAY_GAMEDATA") is { Length: > 0 } gameData)
    {
        static IEnumerable<System.Text.Json.JsonElement> Blocks(string path) => System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Blocks").EnumerateArray();
        var detection = Blocks(Path.Combine(gameData, "EnemyDetection.json")).ToDictionary(b => b.GetProperty("persistentID").GetUInt32(),
            b => new Hearing(b.GetProperty("movementDetectionDistance").GetSingle(), b.GetProperty("weaponDetectionDistanceMax").GetSingle()));
        var hearing = new Dictionary<ulong, Hearing>();
        foreach (var b in Blocks(Path.Combine(gameData, "Enemy.json")))
            if (detection.TryGetValue(b.GetProperty("DetectionDataId").GetUInt32(), out var h)) hearing[b.GetProperty("persistentID").GetUInt32()] = h;
        hearingOf = id => hearing.TryGetValue(id, out var h) ? h : null;
        fullHealth = Blocks(Path.Combine(gameData, "Player.json")).First(b => b.GetProperty("persistentID").GetUInt32() == 1).GetProperty("health").GetSingle();
        Console.WriteLine($"game data: hearing of {hearing.Count} enemy types, full health {fullHealth}");
    }
    var markers = Analysis.Run(timeline, exact: true, hearing: hearingOf);
    var inferred = markers.Inferred!;
    if (markers.Report is not null) Console.WriteLine("report: taken per slot " + string.Join("/", markers.Players.Select(p => p.DamageTaken.ToString("0.0"))));
    var exactHurt = new Dictionary<int, float>(); var exactWakes = new List<uint>(); var exactRevives = 0; var packs = 0; var noises = 0;
    // The player who woke an enemy by the host's own Wake event: not propagated, and the one player whose distance to the enemy at that
    // moment matches the recorded distance (within 1.5 m; two players that close leave it unknown).
    var hostWakers = new List<(uint TimeMs, int Entity, int Slot)>();
    timeline.Seek(0);
    timeline.Advance(timeline.DurationMs, r =>
    {
        var p = new PayloadReader(r.Payload!);
        if (r.EventType == EventKind.PlayerHurt) { var slot = (int)p.U() & 3; p.U(); exactHurt[slot] = exactHurt.GetValueOrDefault(slot) + p.U() / 10f; }
        else if (r.EventType == EventKind.Wake)
        {
            exactWakes.Add(r.TimeMs);
            var entity = (int)p.U(); var propagated = p.U() == 1; var distance = p.U() / 10f;
            ref var e = ref timeline.State.Poses[entity];
            if (propagated || !e.Present) return;
            var match = -1; var matches = 0;
            for (var slot = 0; slot < 4; slot++)
            {
                ref var q = ref timeline.State.Poses[slot];
                if (!q.Present) continue;
                var d = MathF.Sqrt((q.X - e.X) * (q.X - e.X) + (q.Y - e.Y) * (q.Y - e.Y) + (q.Z - e.Z) * (q.Z - e.Z));
                if (Math.Abs(d - distance) <= 1.5f) { match = slot; matches++; }
            }
            hostWakers.Add((r.TimeMs, entity, matches == 1 ? match : -1));
        }
        else if (r.EventType == EventKind.Revive) exactRevives++;
        else if (r.EventType == EventKind.Pack) packs++;
        else if (r.EventType == EventKind.Noise) noises++;
    });
    // The spawn mode of every recorded ItemSpawn (the game's ItemMode number; only 2, a pickup, is put on the ground in playback).
    Console.WriteLine($"item spawns: {timeline.State.ItemSpawns.Count}, by mode: {string.Join(", ", timeline.State.ItemSpawns.GroupBy(s => s.Mode).OrderBy(g => g.Key).Select(g => g.Key + " x" + g.Count()))}");
    Console.WriteLine($"inference: rebuilt noise actions {inferred.Actions.Count} ({string.Join(", ", inferred.Actions.GroupBy(a => a.Action).Select(g => g.Key + " " + g.Count()))}); hearing distances {(hearingOf == null ? "not at hand offline (only recorded noises and running are tried)" : "set")}");
    var known = hostWakers.Where(w => w.Slot >= 0).ToList();
    var agree = known.Count(w => inferred.Wakes.Any(i => i.Entity == w.Entity && Math.Abs((long)i.TimeMs - w.TimeMs) <= 500 && i.NoiseSlot == w.Slot));
    var named = inferred.Wakes.Count(w => w.NoiseSlot >= 0);
    Console.WriteLine($"inference: wake sources: host wake-ups not propagated {hostWakers.Count}, with one player at the recorded distance {known.Count}; inferred wakes naming a player {named} of {inferred.Wakes.Count} (ambiguous {inferred.Wakes.Count(w => w.Ambiguous)}); agreeing with the host's player {agree} of {known.Count}");
    Console.WriteLine($"inference: hurts {inferred.Hurts.Count} (by source: {string.Join(", ", inferred.Hurts.GroupBy(h => h.Source).Select(g => g.Key + " " + g.Count()))}), host PlayerHurt per slot: {string.Join(", ", exactHurt.Select(kv => kv.Key + "=" + kv.Value.ToString("F1")))} units");
    foreach (var (slot, units) in exactHurt)
        if (inferred.TakenFraction(slot) > 0f) Console.WriteLine($"inference: player {slot} lost {inferred.TakenFraction(slot):P1} of full health; the host's {units:F1} units make the full health {units / inferred.TakenFraction(slot):F1}"
            + (fullHealth > 0f ? $"; at the game's full health {fullHealth} the estimate is {inferred.TakenFraction(slot) * fullHealth:F1} units, error {inferred.TakenFraction(slot) * fullHealth - units:+0.0;-0.0} ({(inferred.TakenFraction(slot) * fullHealth - units) / units:P0})" : ""));
    Console.WriteLine($"inference: wakes {inferred.Wakes.Count} (host recorded {exactWakes.Count}, noises recorded {noises}), mines {inferred.Mines.Count}, packs {inferred.Packs.Count} (host {packs}), dodges {inferred.Dodges.Count}, revives {inferred.Revives.Count} (host {exactRevives})");
    if (exactWakes.Count > 0 && inferred.Wakes.Count > 0)
    {
        var errors = exactWakes.Select(t => inferred.Wakes.Min(w => Math.Abs((long)w.TimeMs - t))).ToList();
        Console.WriteLine($"inference: each host wake-up has an inferred one within median {errors.OrderBy(e => e).ElementAt(errors.Count / 2)} ms, worst {errors.Max()} ms");
    }
    // The automatic camera over the real file: which slot it follows, stepped every 100 ms.
    var current = -1; var lastSwitch = 0u; var switches = 0;
    for (var t = 0u; t <= timeline.DurationMs; t += 100)
    {
        timeline.Seek(t);
        var pick = Director.Choose(timeline.State, markers, t, current, lastSwitch);
        if (pick != current) { if (current >= 0) switches++; current = pick; lastSwitch = t; }
    }
    Console.WriteLine($"director: followed slot {current} at the end, {switches} switches over {timeline.DurationMs} ms, {markers.Markers.Count} markers ({string.Join(", ", markers.Markers.GroupBy(m => m.Kind).Select(g => g.Key + " " + g.Count()))})");
    // A game killed mid-write: the file cut at several places (a whole number of bytes, so mostly inside a block) must list as incomplete, and play to its last whole block.
    {
        var whole = File.ReadAllBytes(file);
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "replay-kill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            uint lastEnd = 0; var bad = 0; var cuts = 0;
            for (var cut = whole.Length / 10; cut < whole.Length; cut += whole.Length / 10 + 7)
            {
                var path = System.IO.Path.Combine(folder, "20261102-000000_cut.frpl");
                File.WriteAllBytes(path, whole.AsSpan(0, cut).ToArray());
                var entry = ReplayLibrary.List(folder).Single();
                using var cutStream = new MemoryStream(whole, 0, cut);
                var tl = new Timeline(ForgeCore.ChunkLog.Open(cutStream, ReplayHeader.FormatVersion));
                tl.Seek(tl.DurationMs);
                cuts++;
                if (entry.Complete || entry.Refusal != null || entry.DurationMs != tl.DurationMs || tl.DurationMs < lastEnd) bad++;
                lastEnd = tl.DurationMs;
                Console.WriteLine($"killed: cut at {cut}/{whole.Length} bytes: complete={entry.Complete} playable={entry.Refusal == null} plays to {tl.DurationMs} ms");
            }
            Console.WriteLine($"killed: {cuts} cuts, {bad} listed or played wrongly");
        }
        finally { Directory.Delete(folder, true); }
    }
    foreach (var (id, e) in perEntity) Console.WriteLine($"entity {id}: {e.Count} samples, state {e.MinState}..{e.MaxState}, first {e.First.TimeMs} ms ({e.First.X:F2},{e.First.Y:F2},{e.First.Z:F2}), last {e.Last.TimeMs} ms ({e.Last.X:F2},{e.Last.Y:F2},{e.Last.Z:F2})");
}
// Development: REPLAY_NOISE_DIR=<the game's replay folder> reads every host recording in it and sets each Noise event the host recorded (the
// game's own radius) against the player actions NoiseRebuild reads: whether a noise radius per action can be read off real recordings.
if (Environment.GetEnvironmentVariable("REPLAY_NOISE_DIR") is { Length: > 0 } noiseDir)
{
    const uint window = 150; const float reach = 3f;
    var matched = new Dictionary<NoiseAction, List<float>>(); var silent = new Dictionary<NoiseAction, int>(); var made = new Dictionary<NoiseAction, int>();
    var unmatched = new Dictionary<string, int>(); var files = 0; var total = 0;
    foreach (var path in Directory.GetFiles(noiseDir, "*.frpl").OrderBy(p => p))
    {
        List<(uint TimeMs, int Slot, int Type, float X, float Y, float Z, float Radius)> noises = new();
        NoiseRebuild rebuild;
        try
        {
            using var s = File.OpenRead(path);
            var log = ForgeCore.ChunkLog.Open(s, ReplayHeader.FormatVersion);
            if (!ReplayHeader.Decode(log.Read(log.Chunks.First(c => c.Type == ChunkTypes.Header))).RecorderIsHost) continue;
            var tl = new Timeline(log);
            rebuild = new NoiseRebuild(tl.State);
            tl.Seek(0);
            tl.Advance(tl.DurationMs, r =>
            {
                var p = new PayloadReader(r.Payload!);
                if (r.EventType == EventKind.Noise) { var slot = (int)p.U(); var type = (int)p.U(); var (x, y, z) = p.Pos(); noises.Add((r.TimeMs, slot, type, x, y, z, p.U() / 10f)); }
                else rebuild.Take(r.TimeMs, r.EventType, new PayloadReader(r.Payload!));
            });
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or IOException or ForgeCore.ChunkLogFormatException) { Console.WriteLine($"noise: {Path.GetFileName(path)} skipped: {error.Message}"); continue; }
        files++; total += noises.Count;
        var used = new bool[noises.Count];
        foreach (var action in rebuild.Actions)
        {
            made[action.Action] = made.GetValueOrDefault(action.Action) + 1;
            var best = -1;
            for (var i = 0; i < noises.Count; i++)
            {
                if (used[i] || Math.Abs((long)noises[i].TimeMs - action.TimeMs) > window) continue;
                // A noise whose maker is not a player agent (a weapon, a door) names no slot: it is matched by place instead.
                var (nx, ny, nz) = (noises[i].X, noises[i].Y, noises[i].Z);
                if (noises[i].Slot <= 3 ? noises[i].Slot != action.Slot : (nx - action.X) * (nx - action.X) + (ny - action.Y) * (ny - action.Y) + (nz - action.Z) * (nz - action.Z) > reach * reach) continue;
                if (best < 0 || Math.Abs((long)noises[i].TimeMs - action.TimeMs) < Math.Abs((long)noises[best].TimeMs - action.TimeMs)) best = i;
            }
            if (best < 0) { silent[action.Action] = silent.GetValueOrDefault(action.Action) + 1; continue; }
            used[best] = true;
            if (!matched.TryGetValue(action.Action, out var radii)) matched[action.Action] = radii = new List<float>();
            radii.Add(noises[best].Radius);
        }
        for (var i = 0; i < noises.Count; i++)
            if (!used[i]) { var key = $"slot {(noises[i].Slot > 3 ? "none" : "player")} type {noises[i].Type} radius {noises[i].Radius:F1}"; unmatched[key] = unmatched.GetValueOrDefault(key) + 1; }
    }
    Console.WriteLine($"noise: {files} host recordings, {total} Noise events, actions matched within {window} ms of a noise of the same player:");
    foreach (NoiseAction action in Enum.GetValues(typeof(NoiseAction)))
    {
        var radii = matched.GetValueOrDefault(action) ?? new List<float>();
        var spread = radii.Count == 0 ? "no noise" : $"radius {string.Join(", ", radii.GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => g.Key.ToString("F1") + " m x" + g.Count()))}";
        Console.WriteLine($"noise:   {action}: {made.GetValueOrDefault(action)} actions, {radii.Count} with a noise ({spread}), {silent.GetValueOrDefault(action)} without");
    }
    foreach (var (key, count) in unmatched.OrderByDescending(kv => kv.Value)) Console.WriteLine($"noise:   not matched: {key} x{count}");
}
if (Environment.GetEnvironmentVariable("REPLAY_CALIBRATE_DIR") is { Length: > 0 } calibrate) DamageCalibration.Report(calibrate, Environment.GetEnvironmentVariable("REPLAY_NATIVE_ROWS")!);
