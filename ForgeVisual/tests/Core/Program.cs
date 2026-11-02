using ForgeVisual;

var failures = 0;
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + name);
}
bool Near(float a, float b, float e = 1e-4f) => Math.Abs(a - b) <= e;

// Names: Pack:item, Game and Forge reserved, the first registration keeps a name, a miss says why.
Check(VisualNames.TrySplit("Ordnance:pod", out var pack, out var item, out _) && pack == "Ordnance" && item == "pod", "split Pack:item");
Check(!VisualNames.TrySplit("pod", out _, out _, out var why1) && why1 != null, "bare item refused");
Check(!VisualNames.TrySplit("A:b:c", out _, out _, out _), "two colons refused");
Check(!VisualNames.TrySplit("A:", out _, out _, out _) && !VisualNames.TrySplit(":b", out _, out _, out _), "empty half refused");
Check(!VisualNames.TrySplit("A b:c", out _, out _, out _), "space in pack refused");
Check(VisualNames.TrySplit("A:b.c-d_e", out _, out _, out _), "item may hold '.', '-' and '_'");
Check(!VisualNames.TrySplit("A.b:c", out _, out _, out _), "pack may not hold '.'");
Check(VisualNames.CheckPack("Game") != null && VisualNames.CheckPack("game") != null, "Game reserved");
Check(VisualNames.CheckPack("Forge") != null && VisualNames.CheckPack("Forge", builtIn: true) == null, "Forge reserved for the library");
Check(VisualNames.CheckPack("Arsenal") == null, "ordinary pack accepted");
var table = new NameTable<string>();
var first = new string('a', 1);
Check(table.Add("Arsenal:spark", first) == null, "add");
Check(table.Add("Arsenal:spark", "b") != null, "duplicate refused");
Check(table.TryGet("Arsenal:spark", out var got) && ReferenceEquals(got, first), "first registration kept");
Check(table.Add("bad", "c") != null && table.Count == 1, "bad name refused");
Check(!table.TryGet(null, out _), "null misses");
Check(table.Remove("Arsenal:spark") && table.Count == 0 && table.Names.Count == 0, "remove");
Check(Missing.Why("Frames:thing", _ => false, "model").Contains("no pack Frames"), "missing pack named");
Check(Missing.Why("Frames:thing", _ => true, "model").Contains("has no model"), "missing item named");
Check(Missing.Why("thing", _ => true, "model").Contains("not Pack:item"), "not a name");

// Slots: free first, then the lowest-priority oldest unpinned slot; pinned and equal-or-higher priority are never taken.
var book = new SlotBook(3);
var s0 = book.Take(1, false, out var r0);
var s1 = book.Take(2, false, out _);
var s2 = book.Take(1, true, out _);
Check(s0 >= 0 && s1 >= 0 && s2 >= 0 && !r0 && book.LiveCount == 3, "three free slots");
var g0 = book.Generation(s0);
var s3 = book.Take(2, false, out var r3);
Check(s3 == s0 && r3 && book.LiveCount == 3, "higher priority takes the oldest low unpinned slot");
Check(book.Generation(s0) != g0, "a taken-over slot changes generation");
Check(book.Take(1, false, out _) == -1, "nothing lower: dropped");
Check(book.Take(2, false, out _) == -1, "equal priority never takes over");
book.Unpin(s2);
Check(book.Take(2, false, out _) == s2, "an unpinned slot can be taken");
var g1 = book.Generation(s1);
book.Free(s1);
Check(!book.Live(s1) && book.Generation(s1) != g1 && book.LiveCount == 2, "free ends the handle");
book.Free(s1);
Check(book.LiveCount == 2, "double free harmless");
book.Clear();
Check(book.LiveCount == 0, "clear frees every slot");

// Decals: a free slot first; when full the oldest mark is recycled and its handle goes stale.
var decals = new DecalSlots(3);
var d0 = decals.Take(); var d1 = decals.Take(); var d2 = decals.Take();
var dg0 = decals.Generation(d0);
Check(d0 != d1 && d1 != d2 && d0 != d2 && decals.LiveCount == 3, "decals fill free slots first");
Check(decals.Take() == d0 && decals.LiveCount == 3, "full set recycles the oldest mark");
Check(!decals.Current(d0, dg0) && decals.Current(d0, decals.Generation(d0)), "a recycled mark's handle goes stale");
decals.End(d2);
Check(decals.Take() == d2, "an ended mark's slot is free before the oldest is recycled");
Check(decals.Take() == d1, "then the next oldest");
var dg1 = decals.Generation(d1);
decals.End(d1); decals.End(d1);
Check(!decals.Current(d1, dg1) && decals.LiveCount == 2, "ending twice is harmless");
decals.Clear();
Check(decals.LiveCount == 0, "clear frees every mark");
Check(Near(DecalMath.Hold(20f, 4f), 0.8f) && DecalMath.Hold(2f, 4f) == 0f && DecalMath.Hold(5f, 0f) == 1f && DecalMath.Hold(0f, 1f) == 0f,
    "decal fade starts its seconds before the end");

// Effect lights: free first, then the flash nearest its end; held lights are never taken.
var used = new bool[3];
var held = new bool[3];
var ends = new float[3];
Check(LightSlots.Pick(used, held, ends, 0f) == 0, "a free light first");
used[0] = used[1] = used[2] = true;
ends[0] = 5f; ends[1] = 2f; ends[2] = 3f;
Check(LightSlots.Pick(used, held, ends, 1f) == 1, "budget full: the flash nearest its end is taken");
held[1] = true;
Check(LightSlots.Pick(used, held, ends, 1f) == 2, "a held light is never taken");
held[0] = held[2] = true;
Check(LightSlots.Pick(used, held, ends, 1f) == -1, "every light held: none");
used[2] = false;
Check(LightSlots.Pick(used, held, ends, 1f) == 2, "a freed slot is free again");
Check(LightSlots.Blink(0.1f, 2f) == 1f && LightSlots.Blink(0.3f, 2f) == 0f && LightSlots.Blink(0.6f, 2f) == 1f && LightSlots.Blink(9f, 0f) == 1f,
    "a blinking light is on for the first half of each blink");

// Camera shake: distance fall-off, the player's setting, and a cap on shakes running together.
Check(ShakeMath.Setting(0.4f) == 0.4f && ShakeMath.Setting(-1f) == 0f && ShakeMath.Setting(3f) == 1f && ShakeMath.Setting(float.NaN) == 1f, "shake setting clamped");
Check(ShakeMath.Falloff(0f, 0f, 40f) == 1f && Near(ShakeMath.Falloff(20f, 0f, 40f), 0.5f) && ShakeMath.Falloff(40f, 0f, 40f) == 0f, "linear fall-off to the range");
Check(ShakeMath.Falloff(5f, 10f, 40f) == 1f && Near(ShakeMath.Falloff(25f, 10f, 40f), 0.5f), "full strength inside the inner radius");
Check(ShakeMath.Falloff(5f, 0f, 0f) == 0f && ShakeMath.Falloff(float.NaN, 0f, 10f) == 0f, "no range or no distance: no shake");
var shakes = new ShakeStack(4);
Check(Near(shakes.Admit(0f, 0.6f, 1f, 1f), 0.6f), "a first shake is admitted whole");
Check(Near(shakes.Admit(0f, 0.6f, 1f, 1f), 0.4f) && Near(shakes.Live(0f), 1f), "a second only fills up to the cap");
Check(shakes.Admit(0f, 0.5f, 1f, 1f) == 0f, "nothing past the cap");
Check(Near(shakes.Live(0.5f), 0.5f), "running shakes fade linearly");
Check(Near(shakes.Admit(0.5f, 0.8f, 1f, 1f), 0.5f), "fading frees room");
Check(shakes.Live(2f) == 0f && Near(shakes.Admit(2f, 0.3f, 1f, 1f), 0.3f), "ended shakes free the cap");
for (var i = 0; i < 8; i++) shakes.Admit(3f, 0.1f, 1f, 1f);
Check(Near(shakes.Live(3f), 0.4f), "more shakes than entries: the one with the least share left is replaced");
shakes.Clear();
Check(shakes.Live(3f) == 0f && shakes.Admit(0f, 0f, 1f, 1f) == 0f && shakes.Admit(0f, 1f, 0f, 1f) == 0f, "cleared; empty shakes refused");

// Holds: a request near a held copy extends it instead of starting another; an expired one stops emitting but plays out.
var holds = new HoldBook(2);
var c0 = holds.Take();
holds.Start(c0, 0f, 0f, 0f, 0f, 0.5f);
Check(holds.Playing(c0) && holds.Emitting(c0), "held copy emits");
Check(holds.Near(1f, 0f, 0f, 1.5f) == c0 && holds.Near(3f, 0f, 0f, 1.5f) == -1, "near within reach only");
holds.Extend(c0, 1f, 0f, 0f, 0.4f, 0.5f);
Check(!holds.Expired(c0, 0.8f), "extended window not expired");
Check(holds.Expired(c0, 0.95f) && holds.Playing(c0) && !holds.Emitting(c0), "expired stops emitting, keeps playing");
Check(holds.Near(1f, 0f, 0f, 1.5f) == -1, "a copy no longer emitting is not extended");
var c1 = holds.Take();
Check(c1 != c0, "free copy before a playing one");
holds.Start(c1, 0f, 0f, 0f, 1f, 0f);
Check(holds.Playing(c1) && !holds.Emitting(c1), "one-shot plays without a hold");
Check(holds.Take() == c0, "all playing: the oldest restarts");
holds.Finished(c0);
Check(!holds.Playing(c0) && holds.Take() == c0, "finished copy is free");

// Ribbon: newest first, one point per step, a stall does not fill every missed sample.
var ribbon = new RibbonRing<float>(4, 0.1f);
ribbon.Reset(0f, 0f);
ribbon.Sample(1f, 0.05f);
Check(ribbon[0] == 1f && ribbon[1] == 0f, "head follows between samples");
ribbon.Sample(2f, 0.1f);
ribbon.Sample(3f, 0.2f);
Check(ribbon[0] == 3f && ribbon[1] == 2f && ribbon[2] == 1f, "one point pushed per step");
ribbon.Sample(9f, 5f);
Check(ribbon[0] == 9f && ribbon[1] == 3f, "a long stall restarts the clock");
var threwRibbon = false;
try { _ = new RibbonRing<float>(1, 0.1f); } catch (ArgumentOutOfRangeException) { threwRibbon = true; }
Check(threwRibbon, "a ribbon needs two points");

// Marker numbers.
Check(Near(MarkerMath.Reveal(0f, 0f, 2f), 1f) && Near(MarkerMath.Reveal(100f, 0f, 2f), 0f), "hidden rim only near");
Check(Near(MarkerMath.Reveal(0f, 100f, 2f), 0f), "hidden rim not from far above");
Check(Near(MarkerMath.Sweep(0.3f, 0.6f), 0.5f) && Near(MarkerMath.Sweep(5f, 0.6f), 1f) && Near(MarkerMath.Sweep(0f, 0f), 1f), "sweep");
Check(Near(MarkerMath.Advance(0.9f, 2f, 0.1f), 0.1f), "ping phase wraps");
Check(Near(MarkerMath.Blink(0f), 1f) && MarkerMath.Blink(1f) < 0.01f && MarkerMath.Blink(-1f) == 0f, "blink decays");

// Overdraw: nothing shed under budget, never more than the cap, fade by stable cut.
Check(Overdraw.Shed(4f, 3f, 5f) == 0f, "under budget sheds nothing");
Check(Near(Overdraw.Shed(6f, 4f, 5f), 0.25f), "sheds the excess share");
Check(Near(Overdraw.Shed(50f, 1f, 5f), Overdraw.MaxShed), "shed capped");
Check(Overdraw.Fade(0.5f, 0f) == 1f && Overdraw.Fade(0.1f, 0.5f) == 0f && Overdraw.Fade(0.99f, 0.5f) == 1f, "fade by cut");
Check(Overdraw.Coverage(1f, 0.001f, 0.6f) <= 1f && Overdraw.Coverage(0.1f, 50f, 0.6f) < 1e-3f, "coverage bounded and small far away");
var rnd = new FxRandom(7);
var inRange = true;
for (var i = 0; i < 10000; i++) { var v = rnd.Next(); if (v < 0f || v >= 1f) inRange = false; }
Check(inRange, "random in [0, 1)");

// Rate debt: the count over any frame times matches the rate.
var debt = new RateDebt();
var total = 0;
for (var i = 0; i < 600; i++) total += debt.Take(14f, i % 3 == 0 ? 1f / 30f : 1f / 144f);
var seconds = 200 * (1f / 30f) + 400 * (1f / 144f);
Check(Math.Abs(total - 14f * seconds) <= 1f, "rate debt keeps the rate");
Check(debt.Take(0f, 1f) == 0 && debt.Take(10f, 0f) == 0, "no rate, no time: nothing");

// Model profile: format, keys, unknown surfaces.
const string goodProfile = "{\"format\":1,\"surfaces\":[{\"key\":\"s\",\"albedo\":\"a\",\"metallic\":\"m\"}],\"prefabs\":[{\"name\":\"pod\",\"renderers\":[{\"name\":\"r\",\"surfaces\":[\"s\"]}]}],\"textures\":[\"t\"]}";
Check(ModelProfile.Read(goodProfile, out _) is { Prefabs.Count: 1 }, "profile read");
Check(ModelProfile.Read("{", out var bad1) == null && bad1!.Contains("JSON"), "bad JSON refused");
Check(ModelProfile.Read(goodProfile.Replace("\"format\":1", "\"format\":2"), out var bad2) == null && bad2!.Contains("format"), "other format refused");
Check(ModelProfile.Read(goodProfile.Replace("[\"s\"]", "[\"x\"]"), out var bad3) == null && bad3!.Contains("unknown surface"), "unknown surface refused");
Check(ModelProfile.Read(goodProfile.Replace("\"textures\":[\"t\"]", "\"textures\":[\"t\",\"t\"]"), out _) == null, "repeated texture refused");
Check(ModelProfile.Read(goodProfile.Replace("\"name\":\"pod\"", "\"name\":\"p od\""), out _) == null, "bad prefab name refused");

// Game catalog: unique names, every one a usable item, the tracer draws a line.
var catalogNames = new HashSet<string>();
var catalogOk = true;
foreach (var e in GameCatalog.All)
    if (!catalogNames.Add(e.Name) || VisualNames.CheckItem(e.Item) != null || (e.Source != GameSource.MuzzleFlash && e.Native.Length == 0)) catalogOk = false;
Check(catalogOk && GameCatalog.All.Count >= 20, "game catalog names unique and valid");
Check(GameCatalog.TryGet("Game:FX_Tracer", out var tracer) && tracer.Line, "tracer is a line effect");
Check(!GameCatalog.TryGet("Game:Nope", out _) && !GameCatalog.TryGet(null, out _), "unknown game effect misses");

// A released pooled body goes inactive while it is still live (a beam left in the map after its call-in ended), is parked only while its
// object exists, and is not released twice.
var bodies = new PooledBodies<PoolBody>();
var beamA = new PoolBody();
var beamB = new PoolBody();
bodies.Add(beamA);
bodies.Add(beamB);
Check(bodies.Release(beamA, b => { b.HiddenWhileLive = b.Live; b.Hides++; }, true) && beamA.HiddenWhileLive && beamA.Hides == 1, "release hides the body while it is still live");
beamA.Live = false;
Check(bodies.LiveCount == 1 && bodies.FreeCount == 1, "release moves it from live to parked");
Check(!bodies.Release(beamA, _ => beamA.Hides++, true) && beamA.Hides == 1, "releasing twice hides nothing again");
Check(!bodies.Release(new PoolBody(), _ => { }, true), "a body that was never added is not released");
beamA.Destroyed = true;
Check(bodies.Release(beamB, _ => { }, false) && bodies.FreeCount == 1, "a body whose object is gone is not parked");
Check(bodies.Reuse(b => !b.Destroyed) == null && bodies.FreeCount == 0, "reuse drops parked bodies whose object is gone");
var beamC = new PoolBody();
bodies.Add(beamC);
bodies.Release(beamC, _ => { }, true);
Check(ReferenceEquals(bodies.Reuse(b => !b.Destroyed), beamC), "reuse gives back a usable parked body");

// A particle system is a fixed pool: a full one refuses priority 0, recycles for the rest, and an unbudgeted one takes everything.
Check(ParticleBudget.Admits(0, 40, 0) && ParticleBudget.Admits(39, 40, 0), "a system below its capacity takes every request");
Check(!ParticleBudget.Admits(40, 40, 0) && !ParticleBudget.Admits(60, 40, 0), "a full system refuses priority 0 (the caller counts it)");
Check(ParticleBudget.Admits(40, 40, 1) && ParticleBudget.Admits(40, 40, 5), "a full system takes a higher priority by recycling its oldest particle");
Check(ParticleBudget.Admits(1000, 0, 0), "a system given no capacity is not limited");
var budgetSink = 0;
for (var i = 0; i < 100; i++) budgetSink += ParticleBudget.Admits(i % 50, 40, i % 2) ? 1 : 0;
var budgetBefore = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 20000; i++) budgetSink += ParticleBudget.Admits(i % 50, 40, i % 2) ? 1 : 0;
Check(GC.GetAllocatedBytesForCurrentThread() - budgetBefore == 0 && budgetSink > 0, "the capacity check allocates nothing");

// The per-frame paths allocate nothing.
var hot = new SlotBook(64);
var hotHolds = new HoldBook(16);
var hotRibbon = new RibbonRing<float>(12, 1f / 60f);
hotRibbon.Reset(0f, 0f);
var hotDebt = new RateDebt();
var hotRnd = new FxRandom(3);
var hotDecals = new DecalSlots(16);
var hotShakes = new ShakeStack(16);
var sink = 0f;
void Frame(int i)
{
    var t = i / 60f;
    var mark = hotDecals.Take();
    if (i % 3 == 0) hotDecals.End(mark);
    sink += hotShakes.Admit(t, 0.2f, 0.5f, 1f) + LightSlots.Pick(used, held, ends, t) + LightSlots.Blink(t, 3f) + ShakeMath.Falloff(i % 50, 5f, 40f);
    var slot = hot.Take(i % 3, false, out _);
    if (slot >= 0 && i % 2 == 0) hot.Free(slot);
    var c = hotHolds.Near(i % 5, 0f, 0f, 1.5f);
    if (c < 0) hotHolds.Start(hotHolds.Take(), i % 5, 0f, 0f, t, 0.3f);
    else hotHolds.Extend(c, i % 5, 0f, 0f, t, 0.3f);
    for (var k = 0; k < hotHolds.Copies; k++) if (hotHolds.Expired(k, t)) hotHolds.Finished(k);
    hotRibbon.Sample(t, t);
    sink += hotDebt.Take(30f, 1f / 60f) + hotRnd.Next() + MarkerMath.Sweep(t, 0.6f) + MarkerMath.Reveal(1f, 1f, 2f)
        + Overdraw.Fade(hotRnd.Next(), Overdraw.Shed(6f, 3f, Overdraw.ScreenBudget)) + Overdraw.Coverage(0.5f, 3f, 0.6f);
}
for (var i = 0; i < 200; i++) Frame(i);
var before = GC.GetAllocatedBytesForCurrentThread();
for (var i = 200; i < 20200; i++) Frame(i);
var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated == 0, "per-frame core paths allocate nothing (" + allocated + " bytes)");
GC.KeepAlive(sink);

OutlineChecks.Run(Check);
BodyChecks.Run(Check);

// GunFit: the model's hand points go on the game's hands, its bore on the game's bore.
{
    var nR = new System.Numerics.Vector3(0.02f, -0.05f, 0.10f);
    var nL = new System.Numerics.Vector3(0.00f, -0.12f, 0.45f);
    var nM = new System.Numerics.Vector3(0.015f, 0.10f, 0.80f);
    var mR = new System.Numerics.Vector3(0f, -0.035f, 0.02f);
    var mL = new System.Numerics.Vector3(0f, 0.035f, 0.34f);
    var mM = new System.Numerics.Vector3(0f, 0.12f, 0.70f);
    var fit = GunFit.Solve(nR, nL, nM, mR, mL, mM, 0.7f, 1.4f)!.Value;
    Check(Near(fit.Scale, 0.35f / 0.32f), "gun fit: scale makes the hand points as far apart along the barrel as the game's");
    Check(Near(fit.Scale * mR.Z + fit.Move.Z, nR.Z) && Near(fit.Scale * mL.Z + fit.Move.Z, nL.Z), "gun fit: both hand points land on the game's along the barrel");
    Check(Near(fit.Scale * mM.X + fit.Move.X, nM.X), "gun fit: the bore lands on the game's bore across");
    var rightY = fit.Scale * mR.Y + fit.Move.Y - nR.Y;
    var leftY = fit.Scale * mL.Y + fit.Move.Y - nL.Y;
    Check(Near(rightY + leftY, 0f), "gun fit: the height error of the two hands is shared, not put on one");
    var noMuzzle = GunFit.Solve(nR, nL, null, mR, mL, null, 0.7f, 1.4f)!.Value;
    Check(Near(noMuzzle.Scale * mR.X + noMuzzle.Move.X, nR.X), "gun fit: without a bore the right hand point sets the side");
    var far = GunFit.Solve(nR, nL, null, mR, new System.Numerics.Vector3(0f, 0f, 0.12f), null, 0.7f, 1.4f)!.Value;
    Check(Near(far.Scale, 1.4f), "gun fit: the scale stops at its limit");
    var one = GunFit.Solve(nR, nR, null, mR, mL, null, 0.7f, 1.4f)!.Value;
    Check(Near(one.Scale, 1f), "gun fit: hands on one spot give no scale");
    Check(GunFit.Solve(nR, nL, null, mR, new System.Numerics.Vector3(float.NaN, 0, 0), null, 0.7f, 1.4f) == null, "gun fit: a position that is not a number gives no fit");
    Check(GunFit.Solve(nR, nL, null, mR, mL, null, 0f, 1.4f) == null, "gun fit: scale limits must be positive");
    var nS = new System.Numerics.Vector3(0.01f, 0.06f, 0.30f);
    var mS = new System.Numerics.Vector3(0f, 0.09f, 0.25f);
    var fixedFit = GunFit.Solve(nR, nL, nM, mR, mL, mM, 0.85f, 0.85f, nS, mS)!.Value;
    Check(Near(fixedFit.Scale, 0.85f), "gun fit: equal scale limits fix the scale");
    Check(Near(fixedFit.Scale * mS.Y + fixedFit.Move.Y, nS.Y), "gun fit: with sight points the model's sight lands at the game's sight height");
    Check(Near(fixedFit.Scale * mR.Z + fixedFit.Move.Z, nR.Z) && Near(fixedFit.Scale * mM.X + fixedFit.Move.X, nM.X), "gun fit: the sight changes only the height");
    Check(GunFit.Solve(nR, nL, nM, mR, mL, mM, 0.7f, 1.4f, nS, new System.Numerics.Vector3(float.NaN, 0, 0)) == null, "gun fit: a sight position that is not a number gives no fit");
}

// The shipped effect bundle is the one its manifest records (local media, not in Git: a missing file is reported, never counted as a pass).
static string VfxRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../Assets/Vfx"));
var vfxBundle = Path.Combine(VfxRoot(), "forge-vfx.bundle");
if (!File.Exists(vfxBundle)) Console.WriteLine("needs local resources, not run: " + vfxBundle);
else
{
    using var vfxManifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(VfxRoot(), "forge-vfx.manifest.json")));
    var vfxBytes = File.ReadAllBytes(vfxBundle);
    Check(vfxManifest.RootElement.GetProperty("bytes").GetInt32() == vfxBytes.Length, "effect bundle size matches its manifest");
    Check(vfxManifest.RootElement.GetProperty("sha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(vfxBytes)).ToLowerInvariant(), "effect bundle hash matches its manifest");
}

Console.WriteLine(failures == 0 ? "ForgeVisual core: " + checks + " checks passed" : "ForgeVisual core: " + failures + " of " + checks + " checks failed");
return failures == 0 ? 0 : 1;

sealed class PoolBody
{
    internal bool Live = true, HiddenWhileLive, Destroyed;
    internal int Hides;
}
