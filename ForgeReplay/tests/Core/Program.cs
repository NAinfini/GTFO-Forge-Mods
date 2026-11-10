using ForgeReplay;

var passed = 0; var failed = 0;
void Check(bool ok, string label) { if (Environment.GetEnvironmentVariable("REPLAY_TEST_VERBOSE") != null) Console.WriteLine((ok ? "ok   " : "FAIL ") + label); if (ok) passed++; else { failed++; Console.WriteLine("FAIL " + label); } }

var header = new ReplayHeader
{
    ReplayVersion = "1.0.0",
    GameRevision = 34873,
    Plugins = { new PluginEntry("NAinfini.ForgeCore", "1.0.0", "ab12"), new PluginEntry("NAinfini.ForgeReplay", "1.0.0", "") },
    RundownId = 37,
    RundownName = "ALT://Rundown 8",
    Tier = 1,
    Index = 0,
    BuildSeed = -5,
    HostIdSeed = int.MaxValue,
    SessionSeed = int.MinValue,
    GenerationHash = "deadbeef",
    GenerationSections = { new NamedHash("zones", "aa"), new NamedHash("native", "bb") },
    RecorderName = "玩家一",
    RecorderSlot = 3,
    RecorderIsHost = true,
    Start = StartMode.MidJoin,
    Players = { new PlayerEntry(0, "玩家一"), new PlayerEntry(3, "玩家一") },
    Content = { new ContentEntry("Team-Mobs", "aa"), new ContentEntry("Team-Skins", "bb") },
};
var bytes = header.Encode();
var back = ReplayHeader.Decode(bytes);
Check(back.Encode().AsSpan().SequenceEqual(bytes), "header: decode then encode gives the same bytes");
Check(back.GameRevision == 34873 && back.RundownId == 37 && back.RundownName == "ALT://Rundown 8" && back.BuildSeed == -5 && back.HostIdSeed == int.MaxValue && back.SessionSeed == int.MinValue
    && back.RecorderName == "玩家一" && back.RecorderIsHost && back.Start == StartMode.MidJoin
    && back.Plugins.Count == 2 && back.Plugins[0].DllHash == "ab12"
    && back.GenerationSections[1].Hash == "bb" && back.Players[1] == new PlayerEntry(3, "玩家一") && back.RecorderSlot == 3, "header: every field survives, seed extremes and non-ASCII names included, and the recorder is the slot named, not the first player of that name");
// The content fingerprints close the header; a header with no packages reads with none, and what differs is named.
var withoutContent = ReplayHeader.Decode(new ReplayHeader { ReplayVersion = "1", Players = { new PlayerEntry(0, "n") } }.Encode());
var contentDiff = ContentDifference.Of(header.Content, new[] { new ContentEntry("Team-Skins", "cc") });
Check(back.Content.Count == 2 && back.Content[1] == new ContentEntry("Team-Skins", "bb") && withoutContent.Content.Count == 0
    && contentDiff.Missing.SequenceEqual(new[] { "Team-Mobs" }) && contentDiff.Changed.SequenceEqual(new[] { "Team-Skins" }) && !ContentDifference.Of(withoutContent.Content, header.Content).Any,
    "header: content fingerprints survive, an empty list stays empty, and a missing or changed package is named");
// byte lock: a minimal header
var minimal = new ReplayHeader { ReplayVersion = "1", GameRevision = 1, RundownId = 1, Tier = 1, Index = 0, GenerationHash = "g", RecorderName = "n" };
Check(Convert.ToHexString(minimal.Encode()) == "0131" + "02" + "00" + "01" + "02" + "00" + "00" + "00" + "00" + "0167" + "00" + "016E" + "01" + "00" + "00" + "00" + "00" + "00",
    "header: byte lock of a minimal header (got " + Convert.ToHexString(minimal.Encode()) + ")");
var threw = 0;
for (var cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 40))
    try { ReplayHeader.Decode(bytes.AsSpan(0, cut).ToArray()); } catch (FormatException) { threw++; }
Check(threw > 30, "header: a truncated header is refused, never half-read");
var extra = bytes.Concat(new byte[] { 0 }).ToArray();
var rejectedExtra = false; try { ReplayHeader.Decode(extra); } catch (FormatException) { rejectedExtra = true; }
Check(rejectedExtra, "header: bytes after the last field are refused");
var huge = new byte[] { 0x01, 0x31, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
var rejectedHuge = false; try { ReplayHeader.Decode(huge); } catch (FormatException) { rejectedHuge = true; }
Check(rejectedHuge, "header: a list claiming a billion items is refused before anything is allocated");
RecordingChecks.Run(Check);
Console.WriteLine(passed + " passed, " + failed + " failed");
return failed == 0 ? 0 : 1;

