using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeOrdnance;
using ForgeOrdnanceArsenal;

ForgeCore.Log.Sinks = _ => ForgeOrdnanceArsenal.Tests.NativeChecks.LogLines.Add;
var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

T Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T error) { checks++; return error; }
    throw new Exception("FAIL: " + name + " did not throw " + typeof(T).Name);
}

// ---- the shipped definitions --------------------------------------------------------------------------------

// The package folder, from this source file's own location (the build output lives under artifacts/).
var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile.Path())!, "..", ".."));
var registry = new PayloadRegistry();
var categories = new CategoryRegistry(id => () => id);
registry.Register(StrikeSpec.TypeName, new StrikeParser());
registry.Register(HeavyWeaponSpec.TypeName, new HeavyParser());
registry.Register("Item", new ItemParser());
registry.Register("Gear", new GearParser());
var file = Path.Combine(root, "ForgeOrdnance", "arsenal.json");
var parsed = DefinitionParser.ParseFile("arsenal.json", File.ReadAllText(file), registry, categories);
Check(parsed.Errors.Count == 0, "shipped definitions load without errors: " + string.Join(" | ", parsed.Errors));
Check(parsed.Definitions.Count == 20, "five supplies, three turrets, nine strikes and three heavy weapons");

// Plan §5: default cooldown and uses per call-in.
var expected = new Dictionary<string, (double Cooldown, int Uses)>
{
    ["arsenal.strike.precision"] = (90, -1), ["arsenal.strike.area"] = (240, -1), ["arsenal.strike.creeping"] = (240, -1),
    ["arsenal.strike.cluster"] = (150, -1), ["arsenal.strike.incendiary"] = (180, -1), ["arsenal.strike.gas"] = (180, -1),
    ["arsenal.strike.stasis"] = (120, -1), ["arsenal.strike.beam"] = (300, 3), ["arsenal.strike.hunter"] = (210, -1),
    ["arsenal.weapon.machinegun"] = (300, -1), ["arsenal.weapon.antimateriel"] = (300, -1), ["arsenal.weapon.grenadelauncher"] = (300, -1),
    ["arsenal.supply.mixed"] = (180, -1), ["arsenal.supply.ammo"] = (150, -1), ["arsenal.supply.medical"] = (180, -1),
    ["arsenal.supply.tool"] = (150, -1), ["arsenal.supply.consumables"] = (240, -1),
    ["arsenal.turret.watch"] = (180, -1), ["arsenal.turret.bulwark"] = (210, -1), ["arsenal.turret.scythe"] = (180, -1),
};
var beamColours = new Dictionary<string, uint>
{
    ["arsenal.strike.precision"] = 0xFF3B30, ["arsenal.strike.area"] = 0xFF6A1F,
    ["arsenal.strike.creeping"] = 0xFFB000, ["arsenal.strike.cluster"] = 0xFFD60A,
    ["arsenal.strike.incendiary"] = 0xFF4500, ["arsenal.strike.gas"] = 0x58D66B,
    ["arsenal.strike.stasis"] = 0x22D3EE, ["arsenal.strike.beam"] = 0xFF2DFF,
    ["arsenal.strike.hunter"] = 0xB3122D,
};
string CategoryOf(string type) => type switch
{
    StrikeSpec.TypeName => CategoryRegistry.Offensive,
    HeavyWeaponSpec.TypeName => CategoryRegistry.Weapon,
    "Item" => CategoryRegistry.Supply,
    _ => CategoryRegistry.Defensive,
};
foreach (var d in parsed.Definitions)
{
    Check(expected.TryGetValue(d.Id, out var e), d.Id + " is a planned call-in");
    Check(d.Cooldown == e.Cooldown && d.Uses == e.Uses, d.Id + " cooldown and uses follow the plan");
    Check(d.Category == CategoryOf(d.PayloadType), d.Id + " category");
    var expectedColour = beamColours.TryGetValue(d.Id, out var colour) ? colour : d.Category switch
    {
        CategoryRegistry.Supply => 0x3FA9FF,
        CategoryRegistry.Defensive => 0x7CFF8A,
        _ => 0xFFFFFF,
    };
    Check(d.BeamColour == expectedColour, d.Id + " has the owner's beam colour");
    // tools/build_icons.ps1 renders <group>-<name>.png for arsenal.<group>.<name>; the release zip ships it beside the file.
    Check(d.Icon == d.Id.Substring("arsenal.".Length).Replace('.', '-') + ".png", d.Id + " names its icon");
}

// Plan §5 contents: the supply pods' pickups (item persistentIDs from the game's ItemDataBlock) and the sentry per turret.
string Pickups(string id) => string.Join(" ", ItemList.Expand((ItemEntry[])parsed.Definitions.Single(d => d.Id == id).Parameters, out _));
Check(Pickups("arsenal.supply.mixed") == "101 101 102 127", "mixed pod: ammo x2, medi x1, tool x1");
Check(Pickups("arsenal.supply.ammo") == "101 101 101", "ammo pod: ammo x3");
Check(Pickups("arsenal.supply.medical") == "102 102 132", "medical pod: medi x2, disinfection x1");
Check(Pickups("arsenal.supply.tool") == "127 127", "tool pod: tool refill x2");
Check(ItemParser.ConsumableSets.Contains(Pickups("arsenal.supply.consumables")), "consumables pod carries one of the planned sets");
Check(ItemParser.ConsumableSets.All(set => set.Split(' ').Length <= ItemList.MaxPickups), "every consumables set fits the pod");
foreach (var (id, gear) in new[] { ("arsenal.turret.watch", 24u), ("arsenal.turret.bulwark", 23u), ("arsenal.turret.scythe", 25u) })
{
    var turret = (TurretSpec)parsed.Definitions.Single(d => d.Id == id).Parameters;
    Check(turret.Gear == gear && turret.Lifetime == 180f && !turret.Pickable, id + " is sentry gear " + gear + ", stays 180 s, cannot be picked up");
}
Check(Throws<PayloadParameterException>(() => new GearParser().Parse(Json("{\"Gear\":38}")), "a gun as turret").Field == "Gear", "a turret must be a sentry");
var kinds = parsed.Definitions.Where(d => d.Parameters is StrikeSpec).Select(d => ((StrikeSpec)d.Parameters).Kind).ToHashSet();
Check(kinds.Count == 9 && Enum.GetValues<StrikeKind>().All(kinds.Contains), "every strike kind has a definition");

// Codes: none equal or a prefix of another, here, in ForgeOrdnance's test definitions (test profile
// ForgeOrdnance/test-callins.json), in Frames' test definitions (test-frames.json) and in Frames' shipped definitions.
var codes = parsed.Definitions.Select(d => string.Join(",", d.Code)).ToList();
codes.AddRange(new[]
{
    "Down,Down,Up,Right", "Down,Down,Up,Left", "Down,Down,Left,Left", "Down,Down,Right,Up", "Down,Right,Down,Up", "Down,Right,Down,Down", "Down,Right,Down,Left",
    "Left,Left,Down,Up,Right", "Left,Left,Down,Up,Left", "Left,Left,Down,Up,Down", "Left,Left,Down,Up,Up",
});
var frames = Path.Combine(root, "..", "ForgeOrdnance_Frames", "Definitions", "frames.json");
if (File.Exists(frames))
    foreach (var entry in JsonDocument.Parse(File.ReadAllText(frames)).RootElement.EnumerateArray())
        codes.Add(string.Join(",", entry.GetProperty("Code").EnumerateArray().Select(c => c.GetString())));
else Console.WriteLine("Frames definitions not found beside this package; their codes were not checked.");
for (var i = 0; i < codes.Count; i++)
for (var j = 0; j < codes.Count; j++)
    if (i != j) Check(!(codes[j] + ",").StartsWith(codes[i] + ",", StringComparison.Ordinal), "code " + codes[i] + " is not a prefix of " + codes[j]);

// ---- strike parameters ---------------------------------------------------------------------------------------

{
    var area = StrikeSpec.Parse(Json("{\"Type\":\"arsenal.Strike\",\"Kind\":\"Area\"}"));
    Check(area.Kind == StrikeKind.Area && area.Shells == 7 && area.Spread == 15 && area.Duration == 5, "area defaults: 7 shells, 15 m, 5 s");
    var custom = StrikeSpec.Parse(Json("{\"Kind\":\"Area\",\"Shells\":8,\"Spread\":12}"));
    Check(custom.Shells == 8 && custom.Spread == 12, "overrides apply");
    var e = Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Area\",\"TickDamage\":3}")), "field of another kind");
    Check(e.Field == "TickDamage", "names the foreign field");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"area\"}")), "lower-case kind").Field == "Kind", "kind is case-sensitive");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"7\"}")), "numeric kind").Field == "Kind", "numeric kind text rejected");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{}")), "no kind").Field == "Kind", "kind required");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Area\",\"Shells\":2.5}")), "fraction").Field == "Shells", "whole numbers only");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Area\",\"Shells\":0}")), "range").Field == "Shells", "range checked");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Gas\",\"Duration\":\"5\"}")), "string").Field == "Duration", "numbers only");
    var beam = StrikeSpec.Parse(Json("{\"Kind\":\"Beam\"}"));
    Check(beam.Duration == 10 && beam.Tick > 0 && beam.TickDamage > 0, "beam lasts about 10 s");
    var stasis = StrikeSpec.Parse(Json("{\"Kind\":\"Stasis\"}"));
    Check(Math.Abs(stasis.Slow - 0.35f) < 1e-6f && stasis.ZoneRadius > 0, "stasis slows enemies to 35 % by default");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Stasis\",\"Foam\":1}")), "foam").Field == "Foam", "stasis no longer takes foam");
    Check(Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Stasis\",\"Slow\":1.2}")), "no slow").Field == "Slow", "stasis must actually slow");
}

// ---- strike patterns ----------------------------------------------------------------------------------------

{
    var area = StrikeSpec.Parse(Json("{\"Kind\":\"Area\"}"));
    var a = StrikePlan.Build(area, 42);
    Check(a.Length == 7, "one entry per shell");
    Check(a.All(s => MathF.Sqrt(s.Right * s.Right + s.Forward * s.Forward) <= area.Spread + 1e-4f), "area shells stay inside the spread");
    Check(a.All(s => s.Time >= 0 && s.Time <= area.Duration) && a[0].Time == 0, "area shells fall within the duration, the first at once");
    var again = StrikePlan.Build(area, 42);
    Check(a.Zip(again).All(p => p.First.Right == p.Second.Right && p.First.Time == p.Second.Time), "same seed, same pattern");
    var other = StrikePlan.Build(area, 43);
    Check(a.Zip(other).Any(p => p.First.Right != p.Second.Right), "another seed, another pattern");

    var creeping = StrikeSpec.Parse(Json("{\"Kind\":\"Creeping\"}"));
    var c = StrikePlan.Build(creeping, 7);
    Check(c.Length == creeping.Rows * creeping.Shells, "rows times shells per row");
    for (var row = 1; row < creeping.Rows; row++)
    {
        var prev = c.Skip((row - 1) * creeping.Shells).Take(creeping.Shells);
        var cur = c.Skip(row * creeping.Shells).Take(creeping.Shells);
        Check(cur.Average(s => s.Forward) > prev.Average(s => s.Forward) + creeping.Length * 0.5f, "row " + row + " lies further forward");
        Check(cur.Min(s => s.Time) > prev.Min(s => s.Time), "row " + row + " falls later");
    }

    var cluster = StrikeSpec.Parse(Json("{\"Kind\":\"Cluster\"}"));
    var k = StrikePlan.Build(cluster, 1);
    Check(MathF.Abs(k.Max(s => s.Right) - k.Min(s => s.Right) - cluster.Length) < 1e-3f, "cluster line has the configured length, across the throw");
    Check(k.All(s => MathF.Abs(s.Forward) <= 0.75f), "cluster bomblets stay on the line");

    foreach (var single in new[] { StrikeKind.Precision, StrikeKind.Incendiary, StrikeKind.Gas, StrikeKind.Stasis })
        Check(StrikePlan.Build(StrikeSpec.Parse(Json("{\"Kind\":\"" + single + "\"}")), 5).Length == 1, single + " is one shell on the beacon");
    foreach (var live in new[] { StrikeKind.Beam, StrikeKind.Hunter })
        Check(StrikePlan.Build(StrikeSpec.Parse(Json("{\"Kind\":\"" + live + "\"}")), 5).Length == 0, live + " targets at run time");
    Check(StrikePlan.Look(StrikeKind.Cluster) == ShellLook.Bomblet && StrikePlan.Look(StrikeKind.Gas) == ShellLook.Canister
          && StrikePlan.Look(StrikeKind.Stasis) == ShellLook.Emitter && StrikePlan.Look(StrikeKind.Area) == ShellLook.Shell, "shell looks");
    Check(StrikePlan.Seed(1, 2) == StrikePlan.Seed(1, 2) && StrikePlan.Seed(1, 2) != StrikePlan.Seed(2, 1), "seed depends on summoner and throw");
}

// ---- targeting ----------------------------------------------------------------------------------------------

Check(Targeting.Largest(new float[] { 20, 1000, 1000, 120 }, new float[] { 1, 9, 4, 2 }) == 2, "largest enemy wins, the nearer of equals");
Check(Targeting.Largest(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty) == -1, "no enemy, no target");
Check(Targeting.Nearest(new float[] { 5, 2, 9 }) == 1, "nearest enemy");

// Choosing a reachable target: nearest first, unreachable ones skipped, a budget of path searches, the input untouched.
{
    var probe = new ListReach(new[] { false, true, true, false });
    var dist = new float[] { 1, 2, 3, 4 };
    var scratch = new float[4];
    Check(Targeting.NearestReachable(dist, scratch, 6, ref probe) == 1, "the nearest enemy that can be reached is chosen, nearer unreachable ones are skipped");
    Check(probe.Asked.SequenceEqual(new[] { 0, 1 }) && dist.SequenceEqual(new float[] { 1, 2, 3, 4 }), "candidates are asked nearest first, one search each, and the distances are left alone");
    probe = new ListReach(new[] { false, false, false, false });
    Check(Targeting.NearestReachable(dist, scratch, 6, ref probe) == -1 && probe.Asked.Count == 4, "no enemy can be reached: no target");
    probe = new ListReach(new[] { false, false, true, true });
    Check(Targeting.NearestReachable(dist, scratch, 2, ref probe) == -1 && probe.Asked.Count == 2, "a scan spends at most its budget of path searches");
    probe = new ListReach(new[] { true });
    Check(Targeting.NearestReachable(ReadOnlySpan<float>.Empty, scratch, 6, ref probe) == -1 && probe.Asked.Count == 0, "no candidates, no search");
    probe = new ListReach(new[] { true, true });
    Check(Targeting.NearestReachable(new float[] { float.PositiveInfinity, float.PositiveInfinity }, scratch, 6, ref probe) == -1, "candidates already ruled out are never offered");
}

// The cutting beam's route: walks the navigation corners at a constant speed, never exceeds it, and refuses a move that the level blocks.
{
    var none = new Wall(1000f);
    var route = new BeamRoute(0f, 0f, 0f);
    route.Begin();
    Check(route.Add(10f, 0f, 0f) && route.Add(10f, 0f, 10f), "corners are added");
    Check(route.Step(4f, ref none) == RouteStep.Moving && Math.Abs(route.X - 4f) < 1e-4f && Math.Abs(route.Moved - 4f) < 1e-4f, "a step moves exactly speed times time along the first leg");
    Check(route.Step(8f, ref none) == RouteStep.Moving && Math.Abs(route.X - 10f) < 1e-4f && Math.Abs(route.Z - 2f) < 1e-4f && Math.Abs(route.Moved - 8f) < 1e-4f, "a step goes round the corner and keeps its length (it does not cut it)");
    Check(route.Step(100f, ref none) == RouteStep.Arrived && Math.Abs(route.Z - 10f) < 1e-4f && Math.Abs(route.Moved - 8f) < 1e-4f, "the route ends exactly on its last corner and does not overshoot");
    Check(route.Step(1f, ref none) == RouteStep.Arrived && route.Moved == 0f, "an arrived route stays put");

    // A wall across the first leg at x = 3: the point walks up to the last clear move and then stops; it never ends up beyond it.
    var wall = new Wall(3f);
    route = new BeamRoute(0f, 0f, 0f);
    route.Begin(); route.Add(10f, 0f, 0f);
    var result = RouteStep.Moving;
    var walked = 0;
    for (; walked < 600 && result == RouteStep.Moving; walked++) result = route.Step(0.5f, ref wall);
    Check(result == RouteStep.Blocked && route.X <= 3f && route.X >= 2.4f, "a wall across the path stops the point in front of it");
    var stopped = route.X;
    Check(route.Step(0.5f, ref wall) == RouteStep.Blocked && route.X == stopped && route.Moved == 0f, "a blocked point stays where it is");

    // Detour: the corners go round the wall, so the same wall does not stop the walk.
    route = new BeamRoute(0f, 0f, 0f);
    route.Begin(); route.Add(0f, 0f, 6f); route.Add(10f, 0f, 6f); route.Add(10f, 0f, 0f);
    wall = new Wall(3f, 0f, 4f);
    var steps = 0;
    for (result = RouteStep.Moving; steps < 2000 && result == RouteStep.Moving; steps++) result = route.Step(0.07f, ref wall);
    Check(result == RouteStep.Arrived && Math.Abs(route.X - 10f) < 1e-3f && Math.Abs(route.Z) < 1e-3f, "a path around the wall is walked to its end without ever being blocked");

    route = new BeamRoute(0f, 0f, 0f);
    route.Begin();
    var added = true;
    for (var i = 0; i < BeamRoute.Capacity; i++) added &= route.Add(i + 1f, 0f, 0f);
    Check(added && !route.Add(99f, 0f, 0f) && route.Count == 0, "a path with more corners than fit is refused whole, never cut short");
}
var beamDefault = StrikeSpec.Parse(Json("{\"Kind\":\"Beam\"}"));
Check(beamDefault.Speed == 4f, "the beam's chase speed defaults to 4 m/s");
Check(StrikeSpec.Parse(Json("{\"Kind\":\"Beam\",\"Speed\":7.5}")).Speed == 7.5f, "the beam's chase speed is a definition field");
Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Beam\",\"Speed\":0}")), "a beam speed of zero is refused");
Throws<ForgeCore.StrictJsonException>(() => StrikeSpec.Parse(Json("{\"Kind\":\"Area\",\"Speed\":4}")), "only the beam has a chase speed");

// ---- heavy weapons ------------------------------------------------------------------------------------------

{
    var mg = HeavyWeaponSpec.Parse(Json("{\"Type\":\"arsenal.HeavyWeapon\",\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38}"));
    Check(mg.Model == "Arsenal:heavy-mg" && mg.GearId == 38 && mg.GearName == null && mg.AmmoRatio == 1 && mg.Grenade == null, "machine gun by gear id, full ammo, no grenade");
    var named = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Mine:rifle\",\"Gear\":\"HELRifleSemi\",\"AmmoRatio\":0.5}"));
    Check(named.Model == "Mine:rifle" && named.GearName == "HELRifleSemi" && named.GearId == null && named.AmmoRatio == 0.5f, "a third party's model and gear by name");
    var gl = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:grenade-launcher\",\"Gear\":15,\"Grenade\":{\"Radius\":5}}"));
    Check(gl.Grenade is { Radius: 5 } grenade && grenade.Damage == 140 && grenade.Speed == 32, "grenade object: written radius, default damage and speed");
    Check(HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:grenade-launcher\",\"Gear\":15,\"Grenade\":{}}")).Grenade == new GrenadeSpec(140, 4, 32), "an empty grenade object is all defaults");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"GrenadeRadius\":5}")), "flat grenade field").Field == "GrenadeRadius", "the flat grenade fields are gone");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Grenade\":{\"Size\":5}}")), "unknown grenade member").Field == "Grenade.Size", "unknown grenade member named");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Grenade\":{\"Radius\":0}}")), "grenade radius").Field == "Grenade.Radius", "grenade radius range named");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Grenade\":5}")), "grenade not an object").Field == "Grenade", "grenade must be an object");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\"}")), "no gear").Field == "Gear", "gear required");
    foreach (var bad in new[] { "\"MachineGun\"", "\"Arsenal:\"", "\":heavy-mg\"", "\"A:b:c\"", "\"Arsenal: heavy-mg\"", "\"\"", "5" })
        Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":" + bad + ",\"Gear\":38}")), "bad model " + bad).Field == "Model", "model must be Pack:item: " + bad);
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Gear\":38}")), "no model").Field == "Model", "model required");

    // The marker on the lent gear and the table that turns it, and the wire hash, back into a model id.
    HeavyModels.Clear();
    Check(HeavyModels.Register("Arsenal:heavy-mg") == null && HeavyModels.Register("Arsenal:heavy-mg") == null, "registering the same model twice is fine");
    var mark = HeavyModels.Marker(mg.ModelHash);
    Check(mark.Length == 20 && mark.StartsWith("Forge Heavy ") && HeavyModels.TryMarker(mark, out var markHash) && markHash == mg.ModelHash
        && HeavyModels.TryId(markHash, out var markId) && markId == "Arsenal:heavy-mg", "the marker names the model by its hash and maps back to its id");
    Check(!HeavyModels.TryMarker("Arsenal HMG", out _) && !HeavyModels.TryMarker(null, out _) && !HeavyModels.TryMarker("Forge Heavy 1234567", out _)
        && !HeavyModels.TryMarker("Forge Heavy ABCDEF12", out _) && !HeavyModels.TryMarker("Forge Heavy +bcdef12", out _) && !HeavyModels.TryMarker(mark + "0", out _), "names that are not a marker are not read as one");
    Check(HeavyModels.TryMarkerId(mark, out var directId) && directId == "Arsenal:heavy-mg" && !HeavyModels.TryMarkerId(null, out _) && !HeavyModels.TryMarkerId("Forge Heavy 1234567", out _), "a marker maps to its model id in one lookup");
    Check(!HeavyModels.TryId(HeavyModels.Hash("Arsenal:other"), out _), "a model never registered has no id");
    Check(mg.Marker == mark && !HeavyModels.TryMarker(HeavyModels.Marker(HeavyModels.Hash("Arsenal:other")), out _), "a spec carries its marker, and a well-formed marker of a model never accepted is not one");
    // Two ids with one hash: found by walking numbered ids (about 77,000 of them for an even chance).
    var seen = new Dictionary<uint, string>();
    string first = "", second = "";
    for (var i = 0; second.Length == 0; i++)
    {
        var id = "Test:" + i;
        if (seen.TryGetValue(HeavyModels.Hash(id), out var other)) { first = other; second = id; }
        else seen[HeavyModels.Hash(id)] = id;
    }
    HeavyModels.Clear();
    var clash = HeavyModels.Register(second);
    Check(clash == null, "the first of two clashing ids is accepted");
    clash = HeavyModels.Register(first);
    Check(clash != null && clash.Contains(first) && clash.Contains(second) && HeavyModels.TryId(HeavyModels.Hash(first), out var kept) && kept == second,"the later of two clashing ids is refused and both ids are named");
    HeavyModels.Clear();

    var loan = new HeavyLoan(10f);
    Check(loan.Update(10.5f, 0, 0, false, false) == LoanEnd.None, "empty readings right after the swap are ignored");
    Check(loan.Update(12f, 5, 0, false, false) == LoanEnd.None, "rounds left in the clip keep the weapon");
    Check(loan.Update(12.1f, 0, 0, false, false) == LoanEnd.Empty, "clip and reserve empty: back");
    Check(loan.Update(12.2f, 0, 0, false, false) == LoanEnd.None && loan.Ended == LoanEnd.Empty, "reported once");
    Check(new HeavyLoan(0f).Update(0.1f, 30, 100, true, false) == LoanEnd.Dropped, "drop key gives it back at once");
    Check(new HeavyLoan(0f).Update(0.1f, 30, 100, true, true) == LoanEnd.Down, "going down wins over the drop key");

    var book = new LoanBook();
    book.Commit(1, 1, 8, 2);
    Check(book.HasLoan(8), "a committed claim records an active loan");
    Check(!book.Release(1, 1, 8, 1) && !book.Release(1, 2, 8, 2) && book.HasLoan(8), "return is scoped to its loan identity");
    Check(book.Release(1, 1, 8, 2) && !book.HasLoan(8), "the matching return ends the loan");
    book.Commit(2, 1, 8, 3);
    book.Clear();
    Check(!book.HasLoan(8), "cleared with the level");}

// ---- wire ---------------------------------------------------------------------------------------------------

{
    var shell = new ShellRecord(ShellLook.Bomblet, StrikeKind.Cluster, 1.5f, -2f, 3.25f, 12f, 0.9f, 3.5f);
    var bytes = Wire.Shell(shell);
    Check(bytes.Length == Wire.ShellBytes && bytes.Length <= 192, "shell record size");
    Check(Wire.TryShell(bytes, out var back) && back.Look == shell.Look && back.Kind == shell.Kind && back.X == 1.5f && back.Top == 12f && back.Blast == 3.5f, "shell round trip");
    Check(Convert.ToHexString(bytes) == "02040000C03F000000C000005040000040416666663F00006040", "shell wire bytes unchanged");
    Check(Convert.ToHexString(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 7, 12))) ==
        "060000803F00000040000040400000E040000040410000803F", "zone wire bytes carry the slow share");
    Check(Convert.ToHexString(Wire.Beam(new BeamRecord(new Guid("00112233-4455-6677-8899-aabbccddeeff"), 1, 2, 3, 4, 5, 6, true))) ==
        "010000803F0000004000004040000080400000A0400000C04033221100554477668899AABBCCDDEEFF", "beam wire bytes unchanged");
    bytes[0] = 99;
    Check(!Wire.TryShell(bytes, out _), "unknown look rejected");
    Check(!Wire.TryShell(new byte[3], out _), "short record rejected");
    var nan = Wire.Shell(new ShellRecord(ShellLook.Shell, StrikeKind.Area, float.NaN, 0, 0, 0, 1, 1));
    Check(!Wire.TryShell(nan, out _), "non-finite rejected");

    Check(Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 7, 12)), out var zone) && zone.Kind == StrikeKind.Gas && zone.Radius == 7 && zone.Seconds == 12, "zone round trip");
    Check(!Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Gas, 1, 2, 3, 0, 12)), out _), "zone without radius rejected");
    Check(Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Stasis, 1, 2, 3, 8, 8, 0.35f)), out var slowZone) && slowZone.Slow == 0.35f, "stasis zone round trip keeps its slow share");
    Check(!Wire.TryZone(Wire.Zone(new ZoneRecord(StrikeKind.Stasis, 1, 2, 3, 8, 8, 0f)), out _), "a zone that stops enemies outright is rejected");
    var generation = Guid.NewGuid();
    Check(Wire.TryBeam(Wire.Beam(new BeamRecord(generation, 1, 2, 3, 4, 5, 6, true)), out var beam) && beam.HasTarget && beam.TZ == 6 && beam.Generation == generation, "beam round trip");
    Check(Wire.TryBeam(Wire.Beam(new BeamRecord(generation, 1, 2, 3, 0, 0, 0, false)), out var idle) && !idle.HasTarget, "beam without target");
    Check(!Wire.TryBeam(new byte[25], out _), "old beam layout rejected");
    Check(!Wire.TryBeam(Wire.Beam(new BeamRecord(Guid.Empty, 0, 1, 0, 0, 0, 0, false)), out _), "beam needs a reset generation");


    // Rack state, replayed to late joiners: the weapon rebuilt without the definition file, the name cut on a character boundary.
    HeavyModels.Register("Arsenal:grenade-launcher");
    HeavyModels.Register("Arsenal:heavy-mg");
    var launcher = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:grenade-launcher\",\"Gear\":15,\"AmmoRatio\":0.5,\"Grenade\":{\"Damage\":200}}"));
    var rack = Wire.Rack(new RackRecord(launcher, 15, 1, 2, 3, 90, "Grenade Launcher"));
    // The record is the earlier one-byte-model layout with the model's 4-byte hash in front of the gear id.
    Check(Convert.ToHexString(rack) == Convert.ToHexString(BitConverter.GetBytes(launcher.ModelHash)) + "0F000000" + "0000003F00004843000080400000004200" + "00803F00000040000040400000B4420000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF0000C0FF4772656E616465204C61756E63686572"
        && Wire.RackBytes == 88, "rack wire bytes");
    Check(Wire.TryRack(rack, out var racked) && racked.Spec.Model == "Arsenal:grenade-launcher" && racked.GearId == 15 && racked.Spec.GearId == 15
        && racked.Spec.AmmoRatio == 0.5f && racked.Spec.Grenade == new GrenadeSpec(200, 4, 32) && racked.Yaw == 90f && racked.Id == "Grenade Launcher", "rack round trip");
    var longName = Wire.Rack(new RackRecord(launcher, 15, 1, 2, 3, 0, new string('重', 100)));
    Check(longName.Length <= Wire.MaxDataBytes && Wire.TryRack(longName, out var cut) && cut.Id.Length == (Wire.MaxDataBytes - Wire.RackBytes) / 3, "long rack name cut to whole characters");
    var gun = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38}"));
    Check(Wire.TryRack(Wire.Rack(new RackRecord(gun, 38, 0, 0, 0, 0, "")), out var bare) && bare.Spec.Model == "Arsenal:heavy-mg" && bare.Spec.Grenade == null && bare.Id == "", "rack without name or grenade");
    Check(!Wire.TryRack(Wire.Rack(new RackRecord(gun, 0, 0, 0, 0, 0, "x")), out _), "rack without gear rejected");
    var unknown = (byte[])rack.Clone();
    unknown[0] ^= 0xFF;
    Check(!Wire.TryRack(unknown, out _) && Wire.UnknownRackModel(unknown, out var unknownHash) && unknownHash != launcher.ModelHash && !Wire.UnknownRackModel(rack, out _), "rack with an unknown model hash is dropped whole and says why");
    HeavyModels.Clear();
    Check(!Wire.TryRack(rack, out _) && Wire.UnknownRackModel(rack, out _), "a model this install never accepted is not shown");
    HeavyModels.Register("Arsenal:heavy-mg");
    Check(HeavyWeaponSpec.Restore("Arsenal:heavy-mg", 38, 1f, new GrenadeSpec(140, 0.1f, 32), ArchetypeTuning.None) == null, "grenade values outside their range rejected");
    Check(HeavyWeaponSpec.Restore("MachineGun", 38, 1f, null, ArchetypeTuning.None) == null, "a model that is not Pack:item rejected");

    // Factory settings: only the written fields are set, and they survive the rack record.
    var tuned = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Damage\":5.6,\"Magazine\":80,\"Pierce\":2,\"AmmoCost\":1.9}"));
    Check(tuned.Tuning.Damage == 5.6f && tuned.Tuning.Magazine == 80 && tuned.Tuning.Pierce == 2 && tuned.Tuning.AmmoCost == 1.9f
        && float.IsNaN(tuned.Tuning.ShotDelay) && float.IsNaN(tuned.Tuning.Chargeup) && !tuned.Tuning.IsEmpty && HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38}")).Tuning.IsEmpty, "factory settings parse, unset stay unset");
    Check(Wire.TryRack(Wire.Rack(new RackRecord(tuned, 38, 0, 0, 0, 0, "mg")), out var tunedBack) && tunedBack.Spec.Tuning.SameAs(tuned.Tuning), "factory settings round trip");
    var vanillaNumbers = new ArchetypeTuning(new[] { 4f, .08f, 0f, 0f, 50f, 2.5f, 1f, 1f, 0f, 3f, 40f, 80f });
    var inForce = tuned.Tuning.Over(vanillaNumbers);
    Check(inForce.Damage == 5.6f && inForce.ShotDelay == .08f && inForce.Magazine == 80 && inForce.AmmoCost == 1.9f && inForce.Valid()
        && inForce.Rounds(1f, 230f) == Math.Floor(230d / 1.9f) + 80, "factory settings over the game's numbers; the budget counts reserve plus magazine");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"Magazine\":2.5}")), "fractional magazine").Field == "Magazine", "magazine is whole");
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"AmmoCost\":0}")), "free ammunition").Field == "AmmoCost", "round cost is positive");
    Check(ArchetypeTuning.FromWire(new[] { float.NaN, 0f, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN }) == null, "tuning outside its range rejected on the wire");
    // Damage falloff in metres: both or neither, the end beyond the start; unset takes the game block's own.
    Check(Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffStart\":80}")), "falloff start alone").Field == "FalloffStart"
        && Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffEnd\":200}")), "falloff end alone").Field == "FalloffStart"
        && Throws<ForgeCore.StrictJsonException>(() => HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffStart\":80,\"FalloffEnd\":80}")), "falloff end not beyond start").Field == "FalloffEnd", "falloff needs both fields, end beyond start");
    var falloff = HeavyWeaponSpec.Parse(Json("{\"Model\":\"Arsenal:heavy-mg\",\"Gear\":38,\"FalloffStart\":80,\"FalloffEnd\":200}"));
    var falloffNative = falloff.Tuning.Over(vanillaNumbers);
    Check(falloff.Tuning.FalloffStart == 80f && falloff.Tuning.FalloffEnd == 200f && falloffNative.FalloffStart == 80f && falloffNative.FalloffEnd == 200f
        && inForce.FalloffStart == 40f && inForce.FalloffEnd == 80f
        && Wire.TryRack(Wire.Rack(new RackRecord(falloff, 38, 0, 0, 0, 0, "")), out var falloffBack) && falloffBack.Spec.Tuning.SameAs(falloff.Tuning),
        "falloff round trips the rack; unset takes the game block's");
    Check(ArchetypeTuning.FromWire(new[] { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, 80f, 80f }) == null
        && ArchetypeTuning.FromWire(new[] { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, 80f, float.NaN }) == null, "a bad falloff pair is rejected on the wire");

    Check(Marshal.SizeOf<GrenadeShot>() == 60 && (int)Marshal.OffsetOf<GrenadeShot>("VX") == 40 && (int)Marshal.OffsetOf<GrenadeShot>("Radius") == 56, "grenade shot layout");
    Check(Marshal.SizeOf<GrenadeBurst>() == 40 && (int)Marshal.OffsetOf<GrenadeBurst>("Sequence") == 24
        && (int)Marshal.OffsetOf<GrenadeBurst>("X") == 28, "grenade settlement layout");
}

var nativeChecks = 0;
ForgeOrdnanceArsenal.Tests.NativeChecks.Run((value, name) => { Check(value, name); nativeChecks++; });
ForgeOrdnanceArsenal.Tests.NetworkChecks.Run((value, name) => { Check(value, name); nativeChecks++; });
ForgeOrdnanceArsenal.Tests.LoanChecks.Run(Check);
ForgeOrdnanceArsenal.Tests.LangChecks.Run(Check);
ForgeOrdnanceArsenal.Tests.StrikeChecks.Run((value, name) => { Check(value, name); nativeChecks++; });
Console.WriteLine("Arsenal native lifecycle: " + nativeChecks + " checks passed.");

Console.WriteLine("ForgeOrdnance_Arsenal Core: " + checks + " checks passed.");

internal sealed class StrikeParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload) => StrikeSpec.Parse(payload);
}

internal sealed class HeavyParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload) => HeavyWeaponSpec.Parse(payload);
}

/// <summary>The framework's <c>Item</c> parser (its Core list rules) with the game's item persistentIDs this package uses.</summary>
internal sealed class ItemParser : IOrdnancePayloadParser
{
    // ItemDataBlock: AmmoPackWeapon 101, MediPack 102, AmmoPackTool 127, DisinfectionPack 132, CONSUMABLE_GlueGrenade 115,
    // CONSUMABLE_Tripmine_Explosive 139, CONSUMABLE_FogRepeller 117, CONSUMABLE_GlowStick 114.
    private static readonly HashSet<uint> Known = new() { 101, 102, 127, 132, 115, 139, 117, 114 };

    /// <summary>Plan §5: the consumables pod carries one of these (C-foam grenade x2, trip mine x2, fog repeller x1, glow stick x4).</summary>
    internal static readonly string[] ConsumableSets = { "115 115", "139 139", "117", "114 114 114 114" };

    public object Parse(JsonElement payload) => ItemList.Parse(payload, (id, name) => id.HasValue && Known.Contains(id.Value) ? id : null);
}

internal sealed record TurretSpec(uint Gear, float AmmoRatio, float Lifetime, bool Pickable, string Look);

/// <summary>The framework's <c>Gear</c> fields; the gear must be one of the game's three player sentries.</summary>
internal sealed class GearParser : IOrdnancePayloadParser
{
    // PlayerOfflineGearDataBlock: SentryGun_Semi_Sniper 23, SentryGun_Burst 24, SentryGun_ShotgunSemi 25.
    private static readonly HashSet<uint> Sentries = new() { 23, 24, 25 };

    public object Parse(JsonElement payload)
    {
        PayloadReader.OnlyKnown(payload, "Gear", "AmmoRatio", "Lifetime", "Pickable", "Look", "DamageScale", "FireRateScale", "AmmoScale", "RangeScale", "TurnScale", "AcquireScale");
        var (id, _) = PayloadReader.Reference(payload, "Gear");
        if (!id.HasValue || !Sentries.Contains(id.Value)) throw new PayloadParameterException("Gear", "must be a player sentry gear block (23, 24 or 25).");
        var look = PayloadReader.Look(payload, lookId => lookId is "Arsenal:turret-burst" or "Arsenal:turret-sniper" or "Arsenal:turret-shotgun");
        return new TurretSpec(id.Value, (float)PayloadReader.Number(payload, "AmmoRatio", 1, 0, 1), (float)PayloadReader.Number(payload, "Lifetime", 0, 0, 3600), PayloadReader.Bool(payload, "Pickable", true), look);
    }
}

internal static class SourceFile
{
    internal static string Path([CallerFilePath] string path = "") => path;
}

sealed class ListReach : IReach
{
    private readonly bool[] _reach;
    public readonly List<int> Asked = new();
    public ListReach(bool[] reach) => _reach = reach;
    public bool Reaches(int index) { Asked.Add(index); return _reach[index]; }
}

/// <summary>A wall across the x axis at <c>x</c> (limited to z in [zFrom, zTo] when given): a move whose ends lie on either side crosses it.</summary>
struct Wall : IBlocker
{
    private readonly float _x, _z0, _z1;
    public Wall(float x, float z0 = float.NegativeInfinity, float z1 = float.PositiveInfinity) { _x = x; _z0 = z0; _z1 = z1; }
    public bool Blocked(float ax, float ay, float az, float bx, float by, float bz)
        => az >= _z0 && az <= _z1 && ((ax < _x && bx >= _x) || (ax > _x && bx <= _x));
}
