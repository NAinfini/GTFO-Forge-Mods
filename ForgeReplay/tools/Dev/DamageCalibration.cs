using System.Text.Json;
using ForgeCore;
using ForgeReplay;

/// <summary>Development: REPLAY_CALIBRATE_DIR=<folder of .frpl> REPLAY_NATIVE_ROWS=<the website's vanilla-values-native.json> sets the estimate of
/// PLAN 5.5, fed only what a client sees of a player (rays and swings), against what is known of that player: the host's settlement for everyone in a
/// host recording, the recorder's own readings capped by the enemy's health in a client one. Limb shares come from the other recordings, as a
/// recorder's stand for a teammate's. Two passes: without any bonus from behind against the readings as an older recorder wrote them, and with it by the
/// enemy's recorded facing against those readings corrected the same on the hits they flag as from behind. Melee has its own line and a check of which swings landed.
/// Enemies killed with the recorder's hits alone say whether the bonus is real: readings below the enemy's health cannot be its whole damage.</summary>
internal static class DamageCalibration
{
    private sealed class Row { internal float Truth, TruthMelee, Estimate, EstimateMelee, SoloHealth, SoloEstimate; internal int SoloKills, Rays, Swings; }
    private sealed record Run(string Name, bool Host, int Recorder, Row[] Rows, int[,] Limbs, float KilledHealth, int Killed, int Unseen, float UnseenHealth, int Tp, int Fp, int Fn, int Solo, int ShortRaw, int ShortFixed);

    internal static void Report(string folder, string nativeRows)
    {
        var data = Tables(nativeRows);
        var files = Directory.GetFiles(folder, "*.frpl").OrderBy(p => p).ToList();
        var limbs = files.Select(f => One(f, data, new int[2, 3], false).Limbs).ToList();
        foreach (var back in new[] { false, true })
        {
            Console.WriteLine(back ? "calibrate: --- with the bonus from behind by the enemy's recorded facing, the truth corrected the same on the hits the recorder's game flagged as from behind" : "calibrate: --- without any bonus from behind, against the readings as recorded");
            var errors = new List<float>(); var signed = new List<float>(); var meleeErrors = new List<float>();
            foreach (var path in files)
            {
                var borrowed = new int[2, 3];
                for (var i = 0; i < files.Count; i++) if (files[i] != path) for (var a = 0; a < 2; a++) for (var b = 0; b < 3; b++) borrowed[a, b] += limbs[i][a, b];
                var run = One(path, data, borrowed, back);
                if (back) Shown(path, data);
                Console.WriteLine($"calibrate: {run.Name}: recorder slot {run.Recorder}; killed {run.Killed} with {run.KilledHealth:F0} health, {run.Unseen} ({run.UnseenHealth:F0} health) with no ray or swing seen");
                if (run.Recorder < 0) continue;
                var r = run.Rows[run.Recorder];
                if (r.Truth > 0f) { var relative = (r.Estimate - r.Truth) / r.Truth; errors.Add(Math.Abs(relative)); signed.Add(relative); Console.WriteLine($"calibrate:   all    truth {r.Truth:F0} estimate {r.Estimate:F0} {relative:+0%;-0%}"); }
                if (r.TruthMelee > 0f) { var relative = (r.EstimateMelee - r.TruthMelee) / r.TruthMelee; meleeErrors.Add(Math.Abs(relative)); Console.WriteLine($"calibrate:   melee  truth {r.TruthMelee:F0} estimate {r.EstimateMelee:F0} {relative:+0%;-0%}; swings: {run.Tp} landed and hit, {run.Fp} landed but no hit in the file, {run.Fn} hit but not landed"); }
                if (r.Truth - r.TruthMelee > 0f) Console.WriteLine($"calibrate:   bullet truth {r.Truth - r.TruthMelee:F0} estimate {r.Estimate - r.EstimateMelee:F0} {(r.Estimate - r.EstimateMelee - r.Truth + r.TruthMelee) / (r.Truth - r.TruthMelee):+0%;-0%}");
                Console.WriteLine($"calibrate:   killed with the recorder's hits alone: {run.Solo} enemies; readings as recorded fall short of the health on {run.ShortRaw}, corrected on {run.ShortFixed}; of the health the estimate reaches {r.SoloEstimate / Math.Max(1f, r.SoloHealth):P0}");
            }
            if (errors.Count > 0) Console.WriteLine($"calibrate: {errors.Count} recorders: mean absolute error {errors.Average():P0}, worst {errors.Max():P0}, mean signed {signed.Average():+0%;-0%}; melee mean absolute {(meleeErrors.Count > 0 ? meleeErrors.Average() : 0f):P0}");
        }
    }

    /// <summary>The game's damage tables, copied from the website's vanilla rows for the dev tools.</summary>
    internal static DamageData Tables(string nativeRows)
    {
        var native = JsonDocument.Parse(File.ReadAllBytes(nativeRows)).RootElement.GetProperty("nativeRows");
        var archetype = native.GetProperty("Archetype"); var melee = native.GetProperty("MeleeArchetype"); var category = native.GetProperty("GearCategory");
        var enemyRows = native.GetProperty("Enemy"); var balancing = native.GetProperty("EnemyBalancing");
        GunDamage? Gun(ulong id)
        {
            if (!archetype.TryGetProperty(id.ToString(), out var row)) return null;
            var v = row.GetProperty("value"); var f = v.GetProperty("DamageFalloff");
            return new GunDamage(v.GetProperty("Damage").GetSingle(), f.GetProperty("x").GetSingle(), f.GetProperty("y").GetSingle(), v.GetProperty("PrecisionDamageMulti").GetSingle(), 0, v.GetProperty("PiercingBullets").GetBoolean(), v.GetProperty("Sentry_LongRangeThreshold").GetSingle(), v.GetProperty("Sentry_ShortRangeThreshold").GetSingle());
        }
        EnemyLimbs? Enemy(ulong id)
        {
            if (!enemyRows.TryGetProperty(id.ToString(), out var row) || !balancing.TryGetProperty(row.GetProperty("value").GetProperty("BalancingDataId").GetUInt32().ToString(), out var b)) return null;
            var h = b.GetProperty("value").GetProperty("Health");
            return new EnemyLimbs(h.GetProperty("WeakspotDamageMulti").GetSingle(), h.GetProperty("ArmorDamageMulti").GetSingle(), b.GetProperty("value").GetProperty("AllowDamgeBonusFromBehind").GetBoolean());
        }
        SwingDamage? Swing(uint gear)
        {
            if (!category.TryGetProperty(gear.ToString(), out var c) || !melee.TryGetProperty(c.GetProperty("value").GetProperty("MeleeArchetype").GetUInt32().ToString(), out var m)) return null;
            var v = m.GetProperty("value");
            var set = native.GetProperty("MeleeAnimationSet").GetProperty(v.GetProperty("MeleeAnimationSet").GetUInt32().ToString()).GetProperty("value");
            float F(string name) => v.GetProperty(name).GetSingle();
            float Time(string attack, string name) => set.GetProperty(attack).GetProperty(name).GetSingle();
            return new SwingDamage(F("LightAttackDamage"), F("ChargedAttackDamage"), F("LightPrecisionMulti"), F("ChargedPrecisionMulti"), F("LightBackstabberMulti"), F("ChargedBackstabberMulti"), F("LightSleeperMulti"), F("ChargedSleeperMulti"),
                set.GetProperty("MaxDamageChargeTime").GetSingle(), F("CameraDamageRayLength") + F("AttackSphereRadius"), v.GetProperty("CanHitMultipleEnemies").GetBoolean(),
                Time("FPAttackMissRight", "DamageStartTime"), Time("FPAttackMissRight", "DamageEndTime"), Time("FPAttackChargeUpReleaseRight", "DamageStartTime"), Time("FPAttackChargeUpReleaseRight", "DamageEndTime"));
        }
        ulong? SentryGun(ulong mode)
        {
            foreach (var row in archetype.EnumerateObject())
            {
                var v = row.Value.GetProperty("value");
                if (v.GetProperty("DamageBoosterEffect").GetString() != "SentryGunDamage") continue;
                var fire = v.GetProperty("FireMode").GetString();
                var its = v.GetProperty("ShotgunBulletCount").GetInt32() > 0 ? 13UL : fire == "Auto" ? 11UL : fire == "Burst" ? 12UL : 10UL;
                if (its == mode) return ulong.Parse(row.Name);
            }
            return null;
        }
        return new DamageData(Gun, Enemy, Swing, null, SentryGun);
    }

    // What the statistics show for a playable file: the real analysis with the recorder's own numbers and limbs.
    private static void Shown(string path, DamageData data)
    {
        using var stream = File.OpenRead(path);
        ChunkReader log;
        try { log = ChunkLog.Open(stream, ReplayHeader.FormatVersion); } catch (ChunkLogFormatException) { return; }
        var header = ReplayHeader.Decode(log.Read(log.Chunks.First(c => c.Type == ChunkTypes.Header)));
        var recorder = header.RecorderSlot;
        var analysis = Analysis.Run(new Timeline(log), header.RecorderIsHost, recorder, damage: data, players: header.Players);
        var slots = Enumerable.Range(0, 4).Where(s => ReplayFeed.EverHeld(s, header.Players, analysis.Presence)).ToList();
        Console.WriteLine($"calibrate: {Path.GetFileName(path)} as shown: " + string.Join(", ", slots.Select(s => $"slot {s} {ReplayFeed.Cell(StatRow.Damage, analysis.Players[s], analysis, s, 0f).Text} (mine {analysis.Players[s].ExplosionDamage:F0}, sentry {analysis.Report?.SentryDamage[s]:F0})")) + $"; team {ReplayFeed.TeamDamage(analysis.Players, analysis, slots)}");
    }

    private static Run One(string path, DamageData data, int[,] borrowed, bool back)
    {
        using var stream = File.OpenRead(path);
        var timeline = new Timeline(ChunkLog.Open(stream, ReplayHeader.FormatVersion));
        var state = timeline.State;
        var rows = new[] { new Row(), new Row(), new Row(), new Row() };
        var swings = new SwingDamage?[4];
        var own = new int[2, 3]; var recorder = -1; var host = false;
        // The first pass: whose readings these are, and each melee weapon by the gear category of its hits.
        timeline.Seek(0);
        timeline.Advance(timeline.DurationMs, r =>
        {
            if (r.EventType != EventKind.EnemyHurt) return;
            var p = new PayloadReader(r.Payload!); p.U(); var kind = p.U(); p.U(); var attacker = p.U(); p.U(); var settled = p.U() == 1;
            for (var i = 0; i < 6; i++) p.U();
            var gear = (uint)p.U();
            if (attacker > 3) return;
            host |= settled;
            if (!settled) recorder = (int)attacker;
            if (kind == 1) swings[attacker] = data.Swing(gear);
        }, r => { });
        var match = new HitMatch(-1);
        var wielded = new[] { -1, -1, -1, -1 };
        var enemies = new Dictionary<int, DamageEstimate>(); var left = new Dictionary<int, float>(); var seen = new Dictionary<int, HashSet<int>>();
        int killed = 0, unseen = 0; float killedHealth = 0, unseenHealth = 0;
        DamageEstimate Of(int entity) { if (!enemies.TryGetValue(entity, out var e)) enemies[entity] = e = new DamageEstimate(data) { Limbs = borrowed }; return e; }
        var locomotion = new Dictionary<int, int>(); var rays = new (float X, float Y, float Z)[4];
        var ownSwings = new List<uint>(); var landedSwings = new HashSet<uint>(); var hitSwings = new HashSet<uint>();
        var raw = new Dictionary<int, float>(); var fixedUp = new Dictionary<int, float>(); int solo = 0, shortRaw = 0, shortFixed = 0;
        // Why nothing was seen of a kill: what the file holds in the window before it, first match.
        uint now = 0;
        var booms = new List<(uint Ms, float X, float Y, float Z, bool Owned)>(); var sentryHits = new Dictionary<int, uint>(); var recentShots = new List<(uint Ms, float Ox, float Oy, float Oz, float Ex, float Ey, float Ez, bool Limb)>(); var recentSwings = new List<(uint Ms, int Slot)>(); uint lastCast = 0;
        var sentryOwned = new HashSet<ulong>(); var mineAt = new List<(float X, float Y, float Z)>(); var sentryMatch = new HitMatch(-1); var causes = new Dictionary<string, (int N, float H)>();
        void Why(int entity, float full)
        {
            string cause = "unknown: no teammate within 10 m, no shot, swing, mine or sentry";
            if (state.Place(entity, now, out var x, out var y, out var z, out _, out _, out _, out _))
            {
                float Dist(float a, float b, float c) => MathF.Sqrt((a - x) * (a - x) + (b - y) * (b - y) + (c - z) * (c - z));
                bool Recent(uint ms) => now - ms <= HitMatch.WindowMs;
                float Off((uint Ms, float Ox, float Oy, float Oz, float Ex, float Ey, float Ez, bool Limb) s)
                {
                    var (dx, dy, dz) = (s.Ex - s.Ox, s.Ey - s.Oy, s.Ez - s.Oz); var l = MathF.Max(0.01f, dx * dx + dy * dy + dz * dz);
                    var t = Math.Clamp(((x - s.Ox) * dx + (y + 0.9f - s.Oy) * dy + (z - s.Oz) * dz) / l, 0f, 1f);
                    return Dist(s.Ox + dx * t, s.Oy + dy * t - 0.9f, s.Oz + dz * t);
                }
                bool Mate(float metres) { for (var slot = 0; slot < 4; slot++) if (slot != recorder && state.Place(slot, now, out var px, out var py, out var pz, out _, out _, out _, out _) && Dist(px, py, pz) <= metres) return true; return false; }
                if (booms.Exists(b => Recent(b.Ms) && b.Owned && Dist(b.X, b.Y, b.Z) <= ClientInference.ExplosionReach)) cause = "mine, placer known";
                else if (booms.Exists(b => Recent(b.Ms) && Dist(b.X, b.Y, b.Z) <= ClientInference.ExplosionReach)) cause = "mine, placer unknown";
                else if (sentryHits.TryGetValue(entity, out var ms) && Recent(ms)) cause = "sentry";
                else if (recentShots.Exists(s => Recent(s.Ms) && s.Limb && Dist(s.Ex, s.Ey, s.Ez) <= 6f)) cause = "a teammate's ray ended on a limb 2.5 to 6 m away";
                else if (recentShots.Exists(s => Recent(s.Ms) && Off(s) <= 0.6f)) cause = "a teammate's ray within 0.6 m, not on a limb";
                else if (recentShots.Exists(s => Recent(s.Ms) && Off(s) <= HitMatch.Reach)) cause = "a teammate's ray within 2.5 m";
                else if (recentSwings.Exists(s => Recent(s.Ms) && state.Place(s.Slot, now, out var px, out var py, out var pz, out _, out _, out _, out _) && Dist(px, py, pz) <= 5f)) cause = "a teammate within 5 m swung";
                else if (recentShots.Exists(s => Recent(s.Ms) && Dist(s.Ox, s.Oy, s.Oz) <= 40f)) cause = "a teammate fired within 40 m";
                else if (Mate(10f)) cause = "a teammate within 10 m, no shot or swing";
            }
            var was = causes.GetValueOrDefault(cause); causes[cause] = (was.N + 1, was.H + full);
        }
        void Landed(uint swungMs, uint ms, int slot, int kind, float held, int entity)
        {
            if (slot == recorder) landedSwings.Add(swungMs);
            if (!state.Enemies.TryGetValue(entity, out var e) || !state.Place(slot, ms, out var x, out var y, out var z, out _, out _, out _, out _) || !state.Place(entity, ms, out var ex, out var ey, out var ez, out _, out _, out _, out _)) return;
            rows[slot].Swings++;
            if (!seen.TryGetValue(entity, out var set)) seen[entity] = set = new(); set.Add(slot);
            Of(entity).Swing(ms, entity, e.DataId, e.FullHealth, slot, wielded[slot], kind, held, swings[slot], back ? DamageEstimate.BackDot(state, entity, ms, ex - x, ey - y, ez - z) : -1f, locomotion.GetValueOrDefault(entity) == 14);
        }
        uint otherMs = 0; var other = false;
        void Close(int entity, bool dead, float full)
        {
            var who = seen.Remove(entity, out var set) ? set : new HashSet<int>();
            if (dead && who.Count == 1 && who.First() == recorder && raw.TryGetValue(entity, out var read))
            {
                solo++;
                if (read < full * 0.98f) shortRaw++;
                if (fixedUp[entity] < full * 0.98f) shortFixed++;
            }
            if (dead) { killed++; killedHealth += full; if (who.Count == 0) { unseen++; unseenHealth += full; Why(entity, full); } }
            left.Remove(entity);
            match.Forget(entity);
            if (!enemies.Remove(entity, out var estimate)) return;
            estimate.Gone(entity, 0f, dead && !(other && now - otherMs <= HitMatch.WindowMs)); estimate.Finish();
            foreach (var hit in estimate.Hits)
            {
                rows[hit.Slot].Estimate += hit.Damage;
                if (hit.Kind == 1) rows[hit.Slot].EstimateMelee += hit.Damage;
                if (dead && who.Count == 1) rows[hit.Slot].SoloEstimate += hit.Damage;
            }
            if (dead && who.Count == 1) { var alone = rows[who.First()]; alone.SoloKills++; alone.SoloHealth += full; }
        }
        timeline.Seek(0);
        timeline.Advance(timeline.DurationMs, r =>
        {
            var p = new PayloadReader(r.Payload!); now = r.TimeMs;
            match.Watch(r.TimeMs, state, Landed);
            switch (r.EventType)
            {
                case EventKind.EnemyState: { var entity = (int)p.U(); locomotion[entity] = (int)p.U(); break; }
                case EventKind.Sentry: { var key = p.U(); var st = p.U(); if (p.U() != 0 && st == 0) sentryOwned.Add(key); break; }
                case EventKind.Mine: { p.U(); var st = p.U(); var o = p.U(); var (x, y, z) = p.Pos(); if (st == 0 && o != 0) mineAt.Add((x, y, z)); break; }
                case EventKind.SentryShot:
                    {
                        other = true; otherMs = r.TimeMs;
                        p.U(); var (x, y, z) = p.Pos(); var key = p.U(); var (ax, ay, az) = p.Pos();
                        var hit = sentryOwned.Contains(key) ? sentryMatch.Sentry(r.TimeMs, 0, x, y, z, ax, ay, az, state, out _) : -1;
                        if (hit >= 0) sentryHits[hit] = r.TimeMs;
                        break;
                    }
                case EventKind.Explosion:
                    {
                        other = true; otherMs = r.TimeMs;
                        var kind = p.U(); var (x, y, z) = p.Pos();
                        if (kind == 0) booms.Add((r.TimeMs, x, y, z, mineAt.Exists(k => (k.X - x) * (k.X - x) + (k.Y - y) * (k.Y - y) + (k.Z - z) * (k.Z - z) <= 1f)));
                        break;
                    }
                case EventKind.MeleeCharge: { var slot = (int)p.U() & 3; match.Charge(r.TimeMs, slot, p.U() == 1); break; }
                case EventKind.PlayerStatus: { var slot = (int)p.U() & 3; p.U(); wielded[slot] = (int)p.U(); break; }
                case EventKind.Shot:
                    {
                        var slot = (int)p.U() & 3; var gun = p.U(); var (ox, oy, oz) = p.Pos(); var (ex, ey, ez) = p.Pos();
                        if (slot == recorder) rays[slot] = (ex - ox, ey - oy, ez - oz);
                        // A teammate's piercing gun casts once here (BulletWeaponSynced.Fire), the recorder's once per thing it passes: the recorder's fed as a teammate's would come.
                        if (slot == recorder && data.Gun(gun) is { Piercing: true } && r.TimeMs == lastCast) break;
                        if (slot == recorder) lastCast = r.TimeMs;
                        else { p.U(); recentShots.Add((r.TimeMs, ox, oy, oz, ex, ey, ez, p.U() == 2)); }
                        recentShots.RemoveAll(s => s.Ms + HitMatch.WindowMs < r.TimeMs);
                        var found = match.Shot(r.TimeMs, new PayloadReader(r.Payload!), state);
                        if (found < 0 || !state.Enemies.TryGetValue(found, out var e)) break;
                        rows[slot].Rays++;
                        if (!seen.TryGetValue(found, out var set)) seen[found] = set = new(); set.Add(slot);
                        Of(found).Shot(r.TimeMs, found, e.DataId, e.FullHealth, slot, wielded[slot], gun, MathF.Sqrt((ex - ox) * (ex - ox) + (ey - oy) * (ey - oy) + (ez - oz) * (ez - oz)), back ? DamageEstimate.BackDot(state, found, r.TimeMs, ex - ox, ey - oy, ez - oz) : -1f);
                        break;
                    }
                case EventKind.Melee:
                    {
                        var slot = (int)p.U() & 3; var kind = (int)p.U();
                        if (slot == recorder && kind <= 1) ownSwings.Add(r.TimeMs);
                        if (slot != recorder && kind <= 1) { recentSwings.Add((r.TimeMs, slot)); recentSwings.RemoveAll(s => s.Ms + HitMatch.WindowMs < r.TimeMs); }
                        match.Swung(r.TimeMs, slot, kind, swings[slot]);
                        break;
                    }
                case EventKind.EnemyHurt:
                    {
                        var entity = (int)p.U(); var kind = (int)p.U(); var damage = p.U() / 10f; var attacker = p.U(); p.U();
                        var settled = p.U() == 1; var taken = p.U() / 10f; p.U(); p.U(); var limb = (int)p.U(); p.U(); var behind = p.U() == 1;
                        if (attacker > 3 || kind > 1) break;
                        if (attacker == (ulong)recorder || settled) own[kind, Math.Clamp(limb, 0, 2)]++;
                        state.Enemies.TryGetValue(entity, out var e);
                        if (!left.TryGetValue(entity, out var l)) l = e.FullHealth > 0f ? e.FullHealth : float.MaxValue;
                        var dot = -1f;
                        if (kind == 0) dot = DamageEstimate.BackDot(state, entity, r.TimeMs, rays[(int)attacker].X, rays[(int)attacker].Y, rays[(int)attacker].Z);
                        else if (state.Place((int)attacker, r.TimeMs, out var ax, out var ay, out var az, out _, out _, out _, out _) && state.Place(entity, r.TimeMs, out var ex, out var ey, out var ez, out _, out _, out _, out _)) dot = DamageEstimate.BackDot(state, entity, r.TimeMs, ex - ax, ey - ay, ez - az);
                        var bonus = behind ? 1f + Math.Clamp(dot + 0.25f, 0f, 1f) : 1f;
                        raw[entity] = raw.GetValueOrDefault(entity) + damage; fixedUp[entity] = fixedUp.GetValueOrDefault(entity) + damage * bonus;
                        if (kind == 1 && attacker == (ulong)recorder) { var swing = ownSwings.FindLast(ms => ms <= r.TimeMs && r.TimeMs - ms <= 1000); if (swing != 0) hitSwings.Add(swing); }
                        var took = settled ? taken : Math.Min(back ? damage * bonus : damage, l);
                        left[entity] = l - took;
                        rows[attacker].Truth += took;
                        if (kind == 1) rows[attacker].TruthMelee += took;
                        break;
                    }
                case EventKind.EnemyGone:
                    {
                        var entity = (int)p.U(); var dead = p.U() == 1;
                        Close(entity, dead, state.Enemies.TryGetValue(entity, out var e) ? e.FullHealth : 0f);
                        break;
                    }
            }
        }, r => { });
        foreach (var entity in enemies.Keys.ToList()) Close(entity, false, 0f);
        if (back) foreach (var (cause, n) in causes.OrderByDescending(c => c.Value.N)) Console.WriteLine($"calibrate:   no hit seen on a kill: {n.N} ({n.H:F0} health): {cause}");
        return new Run(Path.GetFileName(path), host, recorder, rows, own, killedHealth, killed, unseen, unseenHealth, ownSwings.Count(ms => hitSwings.Contains(ms) && landedSwings.Contains(ms)), ownSwings.Count(ms => landedSwings.Contains(ms) && !hitSwings.Contains(ms)), ownSwings.Count(ms => hitSwings.Contains(ms) && !landedSwings.Contains(ms)), solo, shortRaw, shortFixed);
    }
}
