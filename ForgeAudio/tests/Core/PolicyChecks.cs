using ForgeAudio;

namespace ForgeAudio.Tests;

internal static class PolicyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var uninitialized = default(Result<int>);
        check(!uninitialized.Success && uninitialized.Error.Code != AudioError.None
            && !string.IsNullOrWhiteSpace(uninitialized.Error.Detail),
            "default Result is an explicit diagnosable failure");

        check(ControlRouting.Decide(true, true) == ControlRoute.Run && ControlRouting.Decide(true, false) == ControlRoute.Queue
            && ControlRouting.Decide(false, true) == ControlRoute.Drop && ControlRouting.Decide(false, false) == ControlRoute.Drop,
            "a stop runs on the main thread, is queued from another thread and is dropped silently after the kit shut down");

        check(VoicePolicy.Capacity == 64, "the real voice budget is 64");

        var victims = new int[8];
        var seats = new[] { new BusMember(100, 1, 1), new BusMember(50, 1, 2), new BusMember(50, 1, 3) };
        check(BusPolicy.Evict(seats.AsSpan(0, 2), 3, 10, 1, victims) == 0, "a free seat evicts nobody");
        check(BusPolicy.Evict(seats.AsSpan(0, 1), 1, 100, 1, victims) == 1 && victims[0] == 0, "a single-seat bus replaces its holder at equal priority");
        check(BusPolicy.Evict(seats.AsSpan(0, 1), 1, 99, 1, victims) == -1, "a lower-priority sound is refused by a full single-seat bus");
        check(BusPolicy.Evict(seats, 3, 60, 1, victims) == 1 && victims[0] == 1, "the lowest priority, then oldest, holder is replaced");
        check(BusPolicy.Evict(seats, 2, 60, 1, victims) == 2 && victims[0] == 1 && victims[1] == 2, "a shrunken bus evicts as many holders as needed");
        check(BusPolicy.Evict(seats, 2, 80, 1, victims) == 2 && BusPolicy.Evict(seats, 1, 60, 1, victims) == -1,
            "an incoming sound that cannot outrank the holders it must replace is refused");
        var distance = new[] { new BusMember(100, .9f, 1), new BusMember(100, .2f, 2) };
        check(BusPolicy.Evict(distance, 2, 100, .5f, victims) == 1 && victims[0] == 1, "at equal priority the farther, quieter holder is replaced first");
        check(BusPolicy.Evict(distance.AsSpan(0, 1), 1, 100, .3f, victims) == -1 && BusPolicy.Evict(distance.AsSpan(0, 1), 1, 100, .45f, victims) == 1,
            "a sound much farther than the near holder at equal priority is refused");
        check(MathF.Abs(DuckPolicy.Factor(6) - .501f) < .001f, "6 dB ducking halves the amplitude");
        var down = DuckPolicy.Step(1, .5f, .04f); var up = DuckPolicy.Step(.5f, 1, .04f);
        check(down < .75f && up < .62f && DuckPolicy.Step(1, .5f, 1) < .501f, "ducking falls fast and recovers slowly");

        check(CategoryVolume.Map(AudioCategory.Weapon, .8f, .5f, .25f, .75f, false, true) == .4f,
            "a sound-effect category uses master times the sound-effects slider");
        check(new[] { AudioCategory.Ui, AudioCategory.Explosion, AudioCategory.Debris, AudioCategory.Ambience, AudioCategory.Vehicle, AudioCategory.Device }
                .All(c => CategoryVolume.Map(c, .8f, .5f, .25f, .75f, false, true) == .4f),
            "every category but music and voice follows the sound-effects slider");
        check(CategoryVolume.Map(AudioCategory.Music, .8f, .5f, .25f, .75f, false, true) == .2f,
            "music uses music slider");
        check(CategoryVolume.Map(AudioCategory.Voice, .8f, .5f, .25f, .75f, false, true) == .6f,
            "voice uses dialogue slider");
        check(MathF.Abs(CategoryVolume.Map(AudioCategory.Weapon, 1, 1, 1, 1, false, true, .5f, .5f) - .25f) < 1e-6f
            && CategoryVolume.Map(AudioCategory.Weapon, 1, 1, 1, 1, false, true, 1, 0) == 0,
            "the kit's master and category volumes multiply the game's sliders");
        check(CategoryVolume.Map(AudioCategory.Weapon, 1, 1, 1, 1, true, false) == 0,
            "background mute silences every category while unfocused");
        check(CategoryVolume.Map(AudioCategory.Weapon, 1, .5f, 1, 1, true, true) == .5f
            && CategoryVolume.Map(AudioCategory.Weapon, 1, .5f, 1, 1, false, false) == .5f,
            "focus regain restores gain and disabled background mute preserves playback");

        // The global category limit holds across every mod's sounds: room below the limit, then the weakest (lowest priority,
        // then farthest) is pushed out by a stronger or nearer newcomer, else the newcomer is refused.
        var full = new[] { new BusMember(100, .8f, 1), new BusMember(100, .2f, 2), new BusMember(150, .1f, 3) };
        check(CategoryPolicy.Admit(full.AsSpan(0, 2), 3, 0, 0, out _) == CategoryAdmission.Room, "a category below its limit lets any sound in");
        check(CategoryPolicy.Admit(full, 3, 100, .5f, out var pushed) == CategoryAdmission.PushOut && pushed == 1,
            "at its limit a nearer sound of equal priority pushes out the farthest holder");
        check(CategoryPolicy.Admit(full, 3, 100, .1f, out _) == CategoryAdmission.Refuse, "a farther sound of equal priority is refused");
        check(CategoryPolicy.Admit(full, 3, 101, 0, out pushed) == CategoryAdmission.PushOut && pushed == 1,
            "a higher priority sound pushes out the weakest holder even when quieter");
        check(CategoryPolicy.Admit(full, 3, 99, 1, out _) == CategoryAdmission.Refuse, "a lower priority sound is refused however near");
        check(AudioCategories.Names.Length == AudioCategories.Count && AudioCategories.Limits.Length == AudioCategories.Count
            && AudioCategories.Meanings.Length == AudioCategories.Count && AudioCategories.Limits.All(l => l is > 0 and <= VoicePolicy.Capacity)
            && AudioCategories.TryParse("vehicle", out var vehicle) && vehicle == AudioCategory.Vehicle && !AudioCategories.TryParse("sfx", out _),
            "every category has a name, a meaning and a limit within the voice budget");
        // Ducking crosses mods: one mod's explosion bus lowers another mod's debris sound, never its own sound or other categories.
        var modA = CueCatalog.Build(new[] { "boom.ogg" },
            "{\"buses\":{\"boom\":{\"category\":\"explosion\",\"duck\":{\"categories\":[\"debris\",\"explosion\"],\"db\":6}}},\"cues\":{\"boom\":{\"bus\":\"boom\"}}}");
        var modB = CueCatalog.Build(new[] { "rubble.ogg", "shot.ogg" },
            "{\"buses\":{\"rubble\":{\"category\":\"debris\"},\"gun\":{\"category\":\"weapon\"}},\"cues\":{\"rubble\":{\"bus\":\"rubble\"},\"shot\":{\"bus\":\"gun\"}}}");
        var ducker = modA.Buses["boom"];
        var duckers = new[] { new DuckSource(0, ducker.DuckMask, DuckPolicy.Factor(ducker.DuckDb)) };
        check(MathF.Abs(DuckPolicy.Target(duckers, 1, modB.Buses["rubble"].Category) - .501f) < .001f,
            "one mod's ducking bus lowers another mod's sound of a ducked category");
        check(DuckPolicy.Target(duckers, 2, modB.Buses["gun"].Category) == 1 && DuckPolicy.Target(duckers, 0, ducker.Category) == 1
            && DuckPolicy.Target(ReadOnlySpan<DuckSource>.Empty, 1, AudioCategory.Debris) == 1,
            "a category it does not duck, the ducker itself and no duckers leave a sound at full level");
        var stronger = new[] { duckers[0], new DuckSource(3, AudioCategories.Bit(AudioCategory.Debris), DuckPolicy.Factor(12)) };
        check(MathF.Abs(DuckPolicy.Target(stronger, 1, AudioCategory.Debris) - DuckPolicy.Factor(12)) < 1e-6f, "the deepest active duck applies");


        check(ListenerPolicy.Decide(true, false, 0) == ListenerDecision.CreateOwned,
            "active camera without a listener gets one owned listener");
        check(ListenerPolicy.Decide(true, true, 0) == ListenerDecision.KeepOwned,
            "same camera retains its owned listener instead of adding every frame");
        check(ListenerPolicy.Decide(true, false, 1) == ListenerDecision.ReuseExternal,
            "existing enabled active listener prevents a second listener");
        check(ListenerPolicy.Decide(true, true, 1) == ListenerDecision.ReuseExternal,
            "external listener appearing retires the owned listener");
        check(ListenerPolicy.Decide(false, true, 0) == ListenerDecision.Unavailable
            && ListenerPolicy.Decide(false, false, 0) == ListenerDecision.Unavailable,
            "camera loss or inactive camera releases owned output without creating another");
        check(ListenerPolicy.Decide(false, true, 1) == ListenerDecision.ReuseExternal,
            "camera-free 2D playback may reuse existing output");
        check(ListenerPolicy.Decide(true, true, 2) == ListenerDecision.ReuseExternal,
            "multiple foreign listeners never cause Kit to add or retain a duplicate");

        var progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(0, false, 1, 1, 10, .05) == OneShotState.Playing,
            "native start latency does not prematurely reclaim a one-shot");
        check(progress.Update(0, false, 1, 1, 10.1, .1) == OneShotState.Completed,
            "finished one-shot is reclaimed even when Unity resets its playback position");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(1, true, 1, 1, 10, .01) == OneShotState.Completed,
            "clip end position reclaims a voice even if isPlaying is stale");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(0, true, 1, 1, 11.2, 1.2) == OneShotState.Playing
            && progress.Update(0, true, 1, 1, 11.25, 1.25) == OneShotState.Completed,
            "DSP deadline reclaims stale playing voices with a bounded output grace");
        progress = new OneShotProgress(.75, 10, 0);
        check(progress.Update(.75, true, 1, 1, 10.5, .5) == OneShotState.Completed,
            "DSP reclamation includes a nonzero starting phase");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(.5, true, 1, 1, 10.5, .5) == OneShotState.Playing
            && progress.Update(.5, true, 1, .5f, 11.5, 1.5) == OneShotState.Playing
            && progress.Update(.5, true, 1, .5f, 12, 2) == OneShotState.Completed,
            "DSP lifetime integrates pitch changes instead of using the initial duration");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(0, true, 5, 1, 9, 1) == OneShotState.Playing
            && progress.Update(0, true, 5, 1, 10, 1.5) == OneShotState.Playing,
            "DSP reset never subtracts progress or instantly completes a voice");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(0, true, 5, 1, 10, 1.99) == OneShotState.Playing
            && progress.Update(0, true, 5, 1, 10, 2) == OneShotState.Stalled,
            "stalled position plus stalled DSP fails explicitly and cannot leak a voice");
        progress = new OneShotProgress(0, 10, 0);
        check(progress.Update(.1, true, 5, 1, 10, 2) == OneShotState.Playing,
            "advancing playback position remains valid when the global DSP clock is paused");

        var repeated = new VoiceRank?[VoicePolicy.Capacity];
        var reclaimed = true;
        for (var wave = 0; wave < 3; wave++)
        {
            for (var n = 0; n < repeated.Length; n++)
            {
                var slot = VoicePolicy.Select(repeated, new VoiceRank(0, 1, wave), wave);
                if (slot < 0) { reclaimed = false; break; }
                repeated[slot] = new VoiceRank(0, 1, wave);
            }
            for (var n = 0; n < repeated.Length; n++)
            {
                progress = new OneShotProgress(0, wave * 2, wave * 2);
                if (progress.Update(0, true, .5, 1, wave * 2 + 1, wave * 2 + 1) == OneShotState.Completed)
                    repeated[n] = null;
            }
        }
        check(reclaimed && repeated.All(v => v == null),
            "three full 64-voice one-shot waves reclaim every slot despite stale native playing flags");
        check(CategoryVolume.Map(AudioCategory.Weapon, 2, -1, 1, 1, false, true) == 0
            && CategoryVolume.Map(AudioCategory.Music, float.NaN, 1, 1, 1, false, true) == 0,
            "volume inputs clamp and non-finite fails silent");

        var old = new VoiceRank(40, .5f, 1);
        check(!VoicePolicy.CanSteal(new VoiceRank(255, 1, 1.05), old, 1.05), "minimum hold prevents early stealing");
        check(VoicePolicy.CanSteal(new VoiceRank(41, .1f, 2), old, 1.1), "higher priority steals after hold");
        check(!VoicePolicy.CanSteal(new VoiceRank(39, 1, 2), old, 1.1), "lower priority never steals");
        check(!VoicePolicy.CanSteal(new VoiceRank(40, .55f, 2), old, 1.1), "equal-priority hysteresis boundary holds");
        check(VoicePolicy.CanSteal(new VoiceRank(40, .551f, 2), old, 1.1), "audibly stronger peer steals past hysteresis");
        check(VoicePolicy.Less(new VoiceRank(1, 1, 5), new VoiceRank(2, 0, 0))
            && VoicePolicy.Less(new VoiceRank(2, .2f, 5), new VoiceRank(2, .3f, 0))
            && VoicePolicy.Less(new VoiceRank(2, .3f, 1), new VoiceRank(2, .3f, 2)),
            "victim ranking is priority, audibility, then oldest");

        var voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(40, .5f, 0), VoicePolicy.Capacity).ToArray();
        voices[17] = null;
        check(VoicePolicy.Select(voices, new VoiceRank(0, 0, 1), 1) == 17,
            "64-slot allocator uses the first free slot before applying steal policy");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(40, .5f, 0), VoicePolicy.Capacity).ToArray();
        voices[42] = new VoiceRank(1, 1, 0);
        check(VoicePolicy.Select(voices, new VoiceRank(41, .1f, 1), 1) == 42,
            "full 64-slot allocator selects the lowest-priority voice");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(40, .5f, 0), VoicePolicy.Capacity).ToArray();
        voices[21] = new VoiceRank(40, .1f, .2);
        voices[22] = new VoiceRank(40, .1f, .05);
        check(VoicePolicy.Select(voices, new VoiceRank(40, .8f, 1), 1) == 22,
            "full allocator breaks equal-rank ties by selecting the oldest voice");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(1, 0, .95), VoicePolicy.Capacity).ToArray();
        check(VoicePolicy.Select(voices, new VoiceRank(255, 1, 1), 1) == -1,
            "full allocator preserves every voice during its minimum hold");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(40, .5f, 0), VoicePolicy.Capacity).ToArray();
        check(VoicePolicy.Select(voices, new VoiceRank(40, .55f, 1), 1) == -1,
            "full allocator rejects equal-priority input at the audibility hysteresis boundary");
        check(VoicePolicy.Select(voices, new VoiceRank(40, .551f, 1), 1) == 0,
            "full allocator admits equal-priority input past the audibility hysteresis boundary");
        check(VoicePolicy.Select(voices, new VoiceRank(39, 1, 1), 1) == -1,
            "full allocator rejects lower-priority input even when it is more audible");

        check(VoicePolicy.Group(AudioCategory.Voice, true, 255) == VoiceGroup.Dialogue,
            "the voice category's group takes precedence over loop and warning priority");
        check(VoicePolicy.Group(AudioCategory.Weapon, false, 200) == VoiceGroup.Warning
            && VoicePolicy.Group(AudioCategory.Ambience, true, 200) == VoiceGroup.Warning,
            "priority 200 maps non-voice cues to warning");
        check(VoicePolicy.Group(AudioCategory.Music, true, 199) == VoiceGroup.EnvironmentLoop
            && VoicePolicy.Group(AudioCategory.Ambience, true, 0) == VoiceGroup.EnvironmentLoop,
            "music and ambience loops map to environment-loop");
        check(VoicePolicy.Group(AudioCategory.Music, false, 199) == VoiceGroup.General
            && VoicePolicy.Group(AudioCategory.Weapon, true, 199) == VoiceGroup.General,
            "other cues map to general");
        check(VoicePolicy.Reserved(VoiceGroup.General) == 0
            && VoicePolicy.Reserved(VoiceGroup.Dialogue) == 8
            && VoicePolicy.Reserved(VoiceGroup.Warning) == 8
            && VoicePolicy.Reserved(VoiceGroup.EnvironmentLoop) == 8,
            "only the three important groups reserve eight voices each");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(40, .5f, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        check(VoicePolicy.Select(voices, new VoiceRank(41, .5f, 1, VoiceGroup.General), 1) == 0,
            "general voices borrow all 64 slots when reserved groups are unused");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(255, 1, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        check(VoicePolicy.Select(voices, new VoiceRank(0, 0, 1, VoiceGroup.Dialogue), 1) == 0,
            "dialogue below its floor reclaims an aged general voice regardless of priority");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(199, 1, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        for (var i = 0; i < 7; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.Dialogue);
        check(VoicePolicy.Select(voices, new VoiceRank(1, 0, 1, VoiceGroup.Dialogue), 1) == 7,
            "dialogue below its floor reclaims a borrowed slot before replacing its own weaker voice");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(100, .5f, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        for (var i = 0; i < 8; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.Dialogue);
        check(VoicePolicy.Select(voices, new VoiceRank(0, 0, 1, VoiceGroup.Warning), 1) == 8,
            "cross-group reclaim preserves all eight reserved dialogue voices");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(100, .5f, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        for (var i = 0; i < 8; i++) voices[i] = new VoiceRank(100, .5f, 0, VoiceGroup.Dialogue);
        voices[3] = new VoiceRank(0, 0, 0, VoiceGroup.Dialogue);
        check(VoicePolicy.Select(voices, new VoiceRank(1, 0, 1, VoiceGroup.Dialogue), 1) == 3,
            "a group at its floor may still replace a weaker voice in the same group");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(255, 1, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        for (var i = 0; i < 8; i++) voices[i] = new VoiceRank(i == 0 ? 0 : 100, .5f, 0, VoiceGroup.Dialogue);
        for (var i = 8; i < 16; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.Warning);
        for (var i = 16; i < 24; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.EnvironmentLoop);
        check(VoicePolicy.Select(voices, new VoiceRank(1, .5f, 1, VoiceGroup.Dialogue), 1) == 0,
            "allocator falls back to a weaker same-group voice when other floors are protected");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(255, 1, 0, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        for (var i = 0; i < 7; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.Dialogue);
        for (var i = 7; i < 15; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.Warning);
        for (var i = 15; i < 23; i++) voices[i] = new VoiceRank(0, 0, 0, VoiceGroup.EnvironmentLoop);
        check(VoicePolicy.Select(voices, new VoiceRank(0, 0, 1, VoiceGroup.Dialogue), 1) == 23,
            "dialogue floor reclaim does not invade warning or environment-loop floors");

        voices = Enumerable.Repeat<VoiceRank?>(new VoiceRank(0, 0, .95, VoiceGroup.General), VoicePolicy.Capacity).ToArray();
        check(VoicePolicy.Select(voices, new VoiceRank(255, 1, 1, VoiceGroup.Dialogue), 1) == -1,
            "minimum hold protects voices from under-floor cross-group reclaim");

        var eligible = new bool[VoicePolicy.Capacity];
        for (var i = 0; i < 6; i++) eligible[i] = true;
        var schedule = new OcclusionSchedule();
        var first = Enumerable.Range(0, 4).Select(_ => schedule.Next(1, 0, eligible)).ToArray();
        check(first.SequenceEqual(new[] { 0, 1, 2, 3 }) && schedule.Next(1, 0, eligible) == -1,
            "occlusion allows no more than four rays per frame");
        check(schedule.Next(2, 0, eligible) == 4 && schedule.Next(2, 0, eligible) == 5
            && schedule.Next(2, 0, eligible) == -1, "occlusion scheduler reaches remaining eligible voices fairly");
        check(schedule.Next(3, .199, eligible) == -1, "occlusion does not update faster than five hertz");
        check(schedule.Next(4, .2, eligible) == 0, "occlusion becomes due at five hertz");
        schedule.Reset(2);
        var resetSeen = false;
        for (var frame = 5; frame < 8; frame++)
            for (var ray = 0; ray < OcclusionSchedule.RaysPerFrame; ray++)
                resetSeen |= schedule.Next(frame, .1, eligible) == 2;
        check(resetSeen, "reset makes a reused slot immediately eligible");

        var gate = new EndGate();
        check(!gate.Ended && gate.End() && gate.Ended && !gate.End(), "ended gate transitions exactly once");
    }
}
