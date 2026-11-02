using ForgeAudio;

namespace ForgeAudio.Tests;

/// <summary>Layer mixing, distance stretches, curves: the code the game and the preview tool share.</summary>
internal static class MixChecks
{
    private static DecodedAudio Tone(double hz, double seconds, int rate = 48000, int channels = 1, float amplitude = .5f)
    {
        var frames = (int)(seconds * rate);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++) samples[i * channels + c] = amplitude * MathF.Sin((float)(2 * Math.PI * hz * i / rate));
        return new DecodedAudio(samples, channels, rate);
    }
    private static double Rms(float[] samples, int from = 0, int count = -1)
    {
        count = count < 0 ? samples.Length - from : count;
        double sum = 0;
        for (var i = from; i < from + count; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / Math.Max(1, count));
    }

    internal static void Run(Action<bool, string> check)
    {
        var files = new Dictionary<string, DecodedAudio>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.ogg"] = Tone(440, 1), ["b.ogg"] = Tone(660, 1), ["hi.ogg"] = Tone(10000, 1), ["half.ogg"] = Tone(440, 1, 24000),
            ["st.ogg"] = Tone(440, 1, 48000, 2), ["a2.ogg"] = Tone(440, 2),
        };
        DecodedAudio Source(string file) => files[file];
        CueDefinition Cue(string json, params string[] names)
        {
            var cue = json == "{}" ? "{\"bus\":\"b\"}" : "{\"bus\":\"b\"," + json.Substring(1);
            var catalog = CueCatalog.Build(names.Length == 0 ? files.Keys : names, "{\"buses\":{\"b\":{\"category\":\"weapon\"}},\"cues\":{\"x\":" + cue + "}}");
            return catalog.Cues.TryGetValue("x", out var built) ? built : throw new InvalidOperationException(catalog.Failures["x"].Detail);
        }
        MixedAudio Mix(CueDefinition cue, int variant = 0, int stretch = 0) => CueMix.Render(cue, CueMix.Profiles(cue)[stretch], variant, Source)!;

        // Offsets overlap layers; the mix is as long as the latest layer end.
        var overlap = Cue("{\"layers\":[{\"clips\":[\"a.ogg\"]},{\"clips\":[\"b.ogg\"],\"offsetMs\":500}]}");
        var mixed = Mix(overlap);
        check(mixed.Frames == 72000 && mixed.Channels == 1 && mixed.SampleRate == 48000, "a layer starting 500 ms in overlaps the first and ends the mix at 1.5 s");
        check(Rms(mixed.Samples, 36000, 12000) > Rms(mixed.Samples, 0, 12000) * 1.3, "the overlapped stretch is louder than the first layer alone");

        var gained = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"gainDb\":-6}]}"));
        check(MathF.Abs((float)(Rms(gained.Samples) / Rms(files["a.ogg"].Samples)) - .501f) < .01f, "layer gain in decibels scales the layer");
        var trimmed = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"range\":[0.25,0.5]}]}"));
        check(trimmed.Frames == 12000, "a layer range keeps only the chosen seconds");
        var up = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"pitch\":2}]}"));
        check(Math.Abs(up.Frames - 24000) <= 1, "an octave up halves the layer's length");
        var faded = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"fadeInMs\":100,\"fadeOutMs\":100}]}"));
        check(MathF.Abs(faded.Samples[0]) < .001f && Rms(faded.Samples, 0, 480) < Rms(faded.Samples, 24000, 480) * .2 && Rms(faded.Samples, faded.Samples.Length - 480) < Rms(faded.Samples, 24000, 480) * .2,
            "layer fades shape both ends");
        var filtered = Mix(Cue("{\"layers\":[{\"clips\":[\"hi.ogg\"],\"lowpassHz\":500}]}"));
        check(Rms(filtered.Samples, 12000, 12000) < Rms(files["hi.ogg"].Samples) * .05, "a low-pass removes the highs");

        // A cap cuts the whole mix and fades it out.
        var capped = Mix(Cue("{\"maxSeconds\":0.8,\"layers\":[{\"clips\":[\"a.ogg\"]},{\"clips\":[\"b.ogg\"],\"offsetMs\":300}]}"));
        check(capped.Frames == 38400 && MathF.Abs(capped.Samples[^1]) < .02f && Rms(capped.Samples, 38400 - 7200, 480) > Rms(capped.Samples, 38400 - 480) * 3,
            "maxSeconds cuts the mix to exactly that length and fades it out");
        var shortCap = Mix(Cue("{\"maxSeconds\":5,\"layers\":[{\"clips\":[\"a.ogg\"]}]}"));
        check(shortCap.Frames == 48000 && MathF.Abs(shortCap.Samples[^2] - files["a.ogg"].Samples[^2]) < 1e-6f, "a mix shorter than its cap is left alone");

        // Rates and channels are matched: the first layer sets the rate, any stereo clip makes the mix stereo.
        var resampled = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"]},{\"clips\":[\"half.ogg\"]}]}"));
        check(resampled.SampleRate == 48000 && resampled.Frames == 48000, "a clip at another sample rate keeps its duration");
        var stereo = Mix(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"]},{\"clips\":[\"st.ogg\"]}]}"));
        check(stereo.Channels == 2 && stereo.Frames == 48000, "a stereo clip makes the mix stereo");

        // Variants: one per clip of the largest pool; layers cycle through their pools.
        var variants = Cue("{\"layers\":[{\"clips\":[\"a.ogg\",\"b.ogg\"]},{\"clips\":[\"hi.ogg\"],\"offsetMs\":100}]}");
        check(CueMix.Variants(variants) == 2 && !Mix(variants, 0).Samples.SequenceEqual(Mix(variants, 1).Samples), "a cue has one variant per clip of its largest pool");
        check(Mix(variants, 1).Samples.SequenceEqual(Mix(variants, 1).Samples), "mixing is deterministic");
        var random = Cue("{\"layers\":[{\"clips\":[\"a.ogg\",\"b.ogg\"],\"pitch\":[0.9,1.1]}]}");
        var p0 = CueMix.LayerPitch(random, random.Layers[0], 0, 0); var p1 = CueMix.LayerPitch(random, random.Layers[0], 1, 0);
        check(p0 >= .9f && p0 <= 1.1f && p1 >= .9f && p1 <= 1.1f && p0 != p1, "a layer's random pitch is picked per variant inside its range");

        // Distance stretches: a far layer replaces the near one.
        var split = Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"distance\":{\"max\":40}},{\"clips\":[\"b.ogg\"],\"distance\":{\"min\":40},\"lowpassHz\":800}]}");
        var profiles = CueMix.Profiles(split);
        check(profiles.Length == 2 && profiles[0].Mask == 1 && profiles[1].Mask == 2 && profiles[0].Max == 40 && float.IsPositiveInfinity(profiles[1].Max),
            "layers with distance ranges make one stretch each");
        check(CueMix.ProfileAt(profiles, 0) == 0 && CueMix.ProfileAt(profiles, 39.9f) == 0 && CueMix.ProfileAt(profiles, 40) == 1 && CueMix.ProfileAt(profiles, 900) == 1,
            "a distance picks its stretch");
        check(!Mix(split, 0, 0).Samples.SequenceEqual(Mix(split, 0, 1).Samples), "the far stretch plays its own mix");
        var farOnly = Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"distance\":{\"min\":10}}]}");
        var farProfiles = CueMix.Profiles(farOnly);
        check(farProfiles.Length == 2 && farProfiles[0].Silent && !farProfiles[1].Silent && CueMix.Render(farOnly, farProfiles[0], 0, Source) == null,
            "a stretch with no layer is silent");
        check(CueMix.Profiles(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"]},{\"clips\":[\"b.ogg\"],\"distance\":{\"min\":5,\"max\":9}}]}")).Length == 3
            && CueMix.Profiles(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"distance\":{\"min\":0}}]}")).Length == 1, "neighbouring stretches with the same layers merge");

        // A plain single layer is played as it is; anything else is mixed.
        check(Cue("{\"layers\":[{\"clips\":[\"a.ogg\"]}]}").IsDirect && !Cue("{\"maxSeconds\":1,\"layers\":[{\"clips\":[\"a.ogg\"]}]}").IsDirect
            && !Cue("{\"layers\":[{\"clips\":[\"a.ogg\"],\"gainDb\":-1}]}").IsDirect, "plain cues need no mix");

        // Distance curves.
        var space = new SpaceDefinition { Near = 10, Far = 100 };
        check(space.Attenuation(5) == 1 && space.Attenuation(10) == 1 && MathF.Abs(space.Attenuation(55) - .5f) < 1e-5f && space.Attenuation(100) == 0 && space.Attenuation(500) == 0,
            "the linear curve falls from 1 at near to 0 at far");
        space.Curve.Shape = DistanceCurve.Kind.Log;
        check(space.Attenuation(10) == 1 && MathF.Abs(space.Attenuation(20) - (.5f - .1f) / .9f) < 1e-5f && space.Attenuation(100) == 0 && space.Attenuation(50) < space.Attenuation(20),
            "the log curve falls as 1/distance and still ends at far");
        space.Curve = new DistanceCurve { Shape = DistanceCurve.Kind.Points, Distances = new[] { 0f, 20f, 60f }, Gains = new[] { 1f, .4f, .1f } };
        check(MathF.Abs(space.Attenuation(10) - .7f) < 1e-5f && MathF.Abs(space.Attenuation(40) - .25f) < 1e-5f && space.Attenuation(100) == 0,
            "custom points are interpolated and the far limit still culls");
        space.Cull = false;
        check(MathF.Abs(space.Attenuation(500) - .1f) < 1e-5f, "without culling the last point's gain holds beyond far");
        space.Cull = true; space.Curve = new DistanceCurve(); space.Blend = .5f;
        check(MathF.Abs(space.Attenuation(55) - .75f) < 1e-5f && space.Attenuation(100) == 0, "blend softens the distance loss but far still culls");
    }
}
