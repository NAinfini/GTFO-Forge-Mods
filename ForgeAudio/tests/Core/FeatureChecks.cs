using System.Runtime.InteropServices;

namespace ForgeAudio.Tests;

/// <summary>The pure halves of the volume rows, plays every player hears and volume ranges.</summary>
internal static class FeatureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Gain(check);
        RowVolume(check);
        Sync(check);
    }

    private static bool Near(float a, float b, float tolerance = 1e-4f) => MathF.Abs(a - b) <= tolerance;

    private static void Gain(Action<bool, string> check)
    {
        const string Json = """
        { "buses": { "b": { "category": "weapon" } },
          "cues": { "fixed": { "bus": "b", "gain": 0.5 }, "linear": { "bus": "b", "gain": [0.25, 1] }, "db": { "bus": "b", "gainDb": [-12, 0] },
                    "reversed": { "bus": "b", "gain": [1, 0.5] }, "both": { "bus": "b", "gain": [0.5, 1], "gainDb": -3 } } }
        """;
        var catalog = CueCatalog.Build(new[] { "fixed.ogg", "linear.ogg", "db.ogg", "reversed.ogg", "both.ogg" }, Json);
        var fixedCue = catalog.Cues["fixed"]; var linear = catalog.Cues["linear"]; var db = catalog.Cues["db"];
        check(fixedCue.GainMin == .5f && fixedCue.Gain == .5f, "a single gain is a fixed volume");
        check(linear.GainMin == .25f && linear.Gain == 1, "gain [min, max] is a volume range");
        check(Near(db.GainMin, MathF.Pow(10, -12 / 20f)) && Near(db.Gain, 1), "gainDb [min, max] is a volume range in decibels");
        check(catalog.Failures.ContainsKey("reversed") && catalog.Failures.ContainsKey("both"), "a range whose max is below its min, or gain with gainDb, makes the cue unavailable");

        check(GainPick.At(.25f, 1, 0) == .25f && GainPick.At(.25f, 1, 1) == 1, "the range's ends are its quietest and loudest picks");
        check(Near(GainPick.At(.25f, 1, .5), .5f), "the middle of a range is the middle in decibels (-6 dB between -12 and 0)");
        check(GainPick.At(.7f, .7f, .3) == .7f, "a fixed volume always picks itself");
        check(Near(GainPick.At(0, 1, .5), .5f), "a range from silence picks linearly");
        var inside = true;
        var random = new Random(7);
        for (var i = 0; i < 2000; i++) { var g = GainPick.At(.1f, 2, random.NextDouble()); inside &= g >= .1f && g <= 2; }
        check(inside, "every pick stays inside the range");
    }

    private static void RowVolume(Action<bool, string> check)
    {
        // The volume every sound of a category ends up with: the game's master and slider times the kit's master and the row's value.
        check(Near(CategoryVolume.Map(AudioCategory.Weapon, .8f, .5f, 1, 1, false, true, .5f, .5f), .1f), "a row's value multiplies the game's and the kit's master");
    }

    private static void Sync(Action<bool, string> check)
    {
        check(Marshal.SizeOf<SyncedPlay>() == 50, "a play every player hears is 50 bytes on the wire");
        check(SyncKey.Of("Author.Mod/Boom") == SyncKey.Of("author.mod/boom") && SyncKey.Of("a") != SyncKey.Of("b") && SyncKey.Of("") != 0,
            "network names ignore case like cue names, differ between names and are never 0");
        check(SyncKey.Of("author.mod/boom") == 0xB186395668BFC7A0ul, "a network name hashes as FNV-1a 64 over its lower-case UTF-16 bytes (the value other players compute)");
        var library = SyncKey.Of("author.mod"); var cue = SyncKey.Of("boom");
        var sent = SyncCodec.Encode(library, cue, SyncPlace.World, 1, 2, 3, .9f, .5f, 1.1f, .2f, 2, 3);
        var bytes = new byte[Marshal.SizeOf<SyncedPlay>()];
        MemoryMarshal.Write(bytes, ref sent);
        var received = MemoryMarshal.Read<SyncedPlay>(bytes);
        check(SyncCodec.TryDecode(received, out var request, out _) && request == new SyncRequest(library, cue, SyncPlace.World, 1, 2, 3, .9f, .5f, 1.1f, .2f, 2, 3),
            "a world play arrives with the same library, cue, place, position, volumes, pitch, start, dimension and variant");
        var flat = SyncCodec.Encode(library, cue, SyncPlace.TwoD, 9, 9, 9, 1, 1, 1, 0, 5, 0);
        check(SyncCodec.TryDecode(flat, out var flatRequest, out _) && flatRequest.Dimension == -1 && flatRequest.Place == SyncPlace.TwoD, "a 2D play carries no dimension");

        bool Refused(SyncedPlay message) => !SyncCodec.TryDecode(message, out _, out var reason) && reason.Length > 0;
        var bad = sent; bad.Place = 7; check(Refused(bad), "an unknown placement is dropped");
        bad = sent; bad.Pitch = 5; check(Refused(bad), "a pitch out of range is dropped");
        bad = sent; bad.Gain = float.NaN; check(Refused(bad), "a non-finite gain is dropped");
        bad = sent; bad.CueGain = 9; check(Refused(bad), "a cue volume out of range is dropped");
        bad = sent; bad.X = float.PositiveInfinity; check(Refused(bad), "a non-finite position is dropped");
        bad = sent; bad.Dimension = -1; check(Refused(bad), "a world play without a dimension is dropped");
        bad = sent; bad.Cue = 0; check(Refused(bad), "a play naming no cue is dropped");
        bad = sent; bad.Variant = 255; check(Refused(bad), "a variant out of range is dropped");
        check(SyncCodec.Encode(library, cue, SyncPlace.World, 0, 0, 0, 1, 1, 1, 0, 0, 999).Variant == SyncCodec.MaxVariant, "an encoded variant never overflows its byte");
    }
}
