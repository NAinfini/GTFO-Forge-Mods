using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ForgeWeaponEnergyLab;

var failures = 0;
void Check(bool ok, string what) { if (!ok) failures++; Console.WriteLine((ok ? "PASS " : "FAIL ") + what); }

var mod = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "../.."));
static string Here([CallerFilePath] string path = "") => path;

var work = Path.Combine(Path.GetTempPath(), "energylab-core-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    EnergyLab.Initialize(Path.Combine(work, "config"));

    // The shipped file: exactly the eight guns.
    var shipped = Path.Combine(mod, "ForgeEnergyLab");
    var shippedFile = Path.Combine(shipped, "energylab.forge");
    var shippedErrors = new List<string>();
    var definitions = EnergyContent.Parse(shippedFile, File.ReadAllText(shippedFile), shippedErrors);
    Check(shippedErrors.Count == 0, "shipped file: no parse errors " + string.Join("; ", shippedErrors));
    Check(definitions.Count == 8, "shipped file: exactly eight guns, found " + definitions.Count);
    Check(definitions.Select(d => d.Mode).Distinct().Count() == 8, "shipped file: the eight behaviours, once each");
    const string Donor = "{\"Ver\":1,\"Name\":\"Donor Gun\",\"Packet\":{\"Comps\":{\"Length\":5,\"a\":{\"c\":2,\"v\":4},\"b\":{\"c\":3,\"v\":108},"
        + "\"d\":{\"c\":5,\"v\":34},\"f\":{\"c\":7,\"v\":5},\"n\":{\"c\":21,\"v\":7}},\"MatTrans\":{},\"publicName\":{\"data\":\"Donor Gun\"}}}";
    foreach (var definition in definitions)
    {
        var gear = new EnergyGear(definition);
        var packet = JsonNode.Parse(EnergyGearJson.Build(Donor, gear))!.AsObject();
        Check(packet["Packet"]!["publicName"]!["data"]!.GetValue<string>() == gear.Name && gear.Name != "Donor Gun", definition.Id + ": the gear packet carries the gun's name");
        Check(gear.Name.Length <= 25, definition.Id + ": the packet name fits the 25-character gear name");
        Check(Path.GetDirectoryName(gear.ModelPath) == Path.GetFullPath(shipped) && Path.GetFileName(gear.ModelPath) == "forge-energy-model-" + definition.Model.Key + ".bundle",
            definition.Id + ": its bundle sits beside the file");
    }
    Check(definitions.Select(d => d.Mode).SequenceEqual(new[] { EnergyMode.Beam, EnergyMode.ArcChain, EnergyMode.Flame, EnergyMode.PlasmaBlast,
        EnergyMode.PlasmaArc, EnergyMode.Blast, EnergyMode.Disc, EnergyMode.BlackHole }), "shipped file: the eight guns are in the list order");
    // All eight register together: their native IDs do not collide.
    var shippedRefused = definitions.Select(d => EnergyLab.Register(d, out var why) ? "" : d.Id + ": " + why).Where(s => s.Length != 0).ToArray();
    Check(shippedRefused.Length == 0, "shipped file: all eight register together " + string.Join("; ", shippedRefused));

    // Definition files: a bad entry costs only itself.
    var pack = Path.Combine(work, "pack");
    Directory.CreateDirectory(Path.Combine(pack, "models"));
    File.WriteAllText(Path.Combine(pack, "guns.forge"), """
    [
      { "Id": "demo.pack.longbeam", "Name": "Long Beam", "Description": "A beam.", "Slot": "Special", "Mode": "Beam",
        "Model": { "Key": "beam", "Bundle": "models/long.bundle" }, "Tuning": { "damage": 9, "magazine": 20 } },
      { "Id": "demo.pack.orb", "Name": "Orb", "Slot": "Main", "Mode": "PlasmaBlast", "Model": { "Key": "orb", "Bundle": "orb.bundle" } },
      { "Id": "pack.short", "Name": "Short Id", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" } },
      { "Id": "demo.pack.escape", "Name": "Escape", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "../outside.bundle" } },
      { "Id": "demo.pack.nested", "Name": "Nested", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "a\\..\\b.bundle" } },
      { "Id": "demo.pack.rooted", "Name": "Rooted", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "/abs.bundle" } },
      { "Id": "demo.pack.wrongfield", "Name": "Wrong", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" }, "Tuning": { "blastRadius": 3 } },
      { "Id": "demo.pack.off", "Name": "Off", "Slot": "Main", "Mode": "Off", "Model": { "Key": "k", "Bundle": "k.bundle" } },
      { "Id": "demo.pack.badslot", "Name": "Slot", "Slot": "Third", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" } },
      { "Id": "demo.pack.range", "Name": "Range", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" }, "Tuning": { "interval": 0 } },
      { "Id": "demo.pack.longbeam", "Name": "Again", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" } },
      { "Name": "No id", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" } }
    ]
    """);
    var errors = new List<string>();
    var loaded = EnergyContent.Load(new[] { pack }, errors);
    var guns = Path.Combine(pack, "guns.forge");
    var ids = loaded.Select(l => l.Definition.Id).ToArray();
    Check(ids.SequenceEqual(new[] { "demo.pack.longbeam", "demo.pack.orb" }) && loaded.All(l => l.Folder == pack), "pack: the two good entries register, the rest are skipped");
    var beam = loaded[0].Definition;
    Check(beam.Model.BundlePath == Path.Combine(pack, "models", "long.bundle") && beam.Description == "A beam.", "pack: a relative Bundle resolves inside the file's folder");
    string Line(string needle) => errors.FirstOrDefault(e => e.Contains(needle, StringComparison.Ordinal)) ?? "";
    Check(Line("pack.short").Contains(guns) && Line("pack.short").Contains("id"), "error: a two-segment Id names the file, the Id and the field");
    Check(Line("demo.pack.escape").Contains("Model.Bundle") && Line("demo.pack.escape").Contains(".."), "error: '..' in Bundle is refused");
    Check(Line("demo.pack.nested").Contains("Model.Bundle"), "error: a backslash '..' step is refused");
    Check(Line("demo.pack.rooted").Contains("Model.Bundle"), "error: an absolute Bundle is refused");
    Check(Line("demo.pack.wrongfield").Contains("Tuning.blastRadius"), "error: a field of another behaviour is refused");
    Check(Line("demo.pack.badslot").Contains("Slot"), "error: an unknown Slot is refused");
    Check(errors.Any(e => e.Contains("Duplicate weapon id: demo.pack.longbeam")), "error: a repeated Id is refused");

    // A native ID collision names both Ids.
    var seen = new Dictionary<uint, string>();
    string? first = null, second = null;
    for (var i = 0; second == null; i++)
    {
        var id = "demo.hash.gun" + i;
        var category = EnergyGears.Ids(id).Category;
        if (seen.TryGetValue(category, out var other)) { first = other; second = id; }
        else seen[category] = id;
    }
    WeaponDefinition Gun(string id) => new(id, "Gun", WeaponSlot.Main, EnergyMode.Beam, new BeamTuning(), new WeaponModel("beam", Path.Combine(pack, "k.bundle")));
    Check(EnergyLab.Register(Gun(first!), out _), "register: the first of two colliding Ids registers");
    Check(!EnergyLab.Register(Gun(second!), out var collision) && collision!.Contains(first!) && collision.Contains(second!), "register: a collision is refused and names both Ids");

    // Rundown rules (Settings.forge), Model.From and Look.
    var rules = new List<string>();
    var settings = EnergySettings.Parse("""
    { // comment
      "Allowed": ["demo.pack.longbeam", "demo.pack.orb"],
      "Tuning": { "demo.pack.longbeam": { "damage": 12 }, "demo.pack.orb": { "damage": 7 } },
      "Levels": { "Local_31_TierA_0": { "Allowed": ["demo.pack.orb"] }, "Local_31_TierA_1": {} },
    }
    """, rules);
    Check(settings != null && rules.Count == 0, "settings: a commented file with trailing commas parses " + string.Join("; ", rules));
    Check(settings!.AllowedFor("Local_31_TierA_0")!.SequenceEqual(new[] { "demo.pack.orb" })
          && settings.AllowedFor("Local_31_TierA_1")!.Count == 2 && settings.AllowedFor(null)!.Count == 2, "settings: a level's list wins, a level without one inherits");
    rules.Clear();
    Check(EnergySettings.Parse("""{ "Allow": [] }""", rules) == null && rules.Any(e => e.Contains("Allow")), "settings: an unknown field rejects the whole file");
    rules.Clear();
    Check(EnergySettings.Parse("""{ "Levels": { "k": { "Allowed": "x" } } }""", rules) == null && rules.Any(e => e.Contains("Levels.k.Allowed")), "settings: a mistyped list is refused and named");
    EnergyLab.UseSettings(settings);

    File.WriteAllText(Path.Combine(pack, "look.forge"), """
    [
      { "Id": "demo.look.base", "Name": "Base", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "beam", "Bundle": "models/long.bundle" } },
      { "Id": "demo.look.same", "Name": "Same", "Slot": "Main", "Mode": "Beam", "Model": { "From": "demo.look.base" },
        "Look": { "Sounds": { "fire": "my-fire", "beam-hit": "other/hit" }, "Fx": { "beam.muzzle": "Pack:flash" } } },
      { "Id": "demo.look.lost", "Name": "Lost", "Slot": "Main", "Mode": "Beam", "Model": { "From": "demo.nowhere.gun" } },
      { "Id": "demo.look.badkey", "Name": "Bad", "Slot": "Main", "Mode": "Beam", "Model": { "Key": "k", "Bundle": "k.bundle" }, "Look": { "Sounds": { "boom": "x" } } }
    ]
    """);
    var lookErrors = new List<string>();
    var looked = EnergyContent.Load(new[] { pack }, lookErrors).ToDictionary(l => l.Definition.Id, l => l.Definition);
    Check(looked.TryGetValue("demo.look.same", out var same) && same.Model.BundlePath == looked["demo.look.base"].Model.BundlePath && same.Model.Key == "beam", "From: takes the whole model of a gun in the files read together");
    Check(same!.Look!.Sounds["beam-start"] == "my-fire" && same.Look.Fx["energy.beam.muzzle"] == "Pack:flash", "Look: fire and short slot names read as the canonical cue and slot");
    Check(!looked.ContainsKey("demo.look.lost") && lookErrors.Any(e => e.Contains("Model.From") && e.Contains("demo.nowhere.gun")), "From: an unknown gun is named and the gun is skipped");
    Check(!looked.ContainsKey("demo.look.badkey") && lookErrors.Any(e => e.Contains("Look.Sounds.boom")), "Look: an unknown cue name is refused and named");
    Check(EnergyLookNames.Canonical(same.Look) != EnergyLookNames.Canonical(null), "Look: counts in the multiplayer check text");

    // Tuning files: accept third-party Ids, skip unknown Ids and bad entries.
    var config = Path.Combine(work, "config");
    Directory.CreateDirectory(config);
    File.WriteAllText(Path.Combine(config, EnergyConfiguration.OverridesFile), """
    { "weapons": {
        "demo.pack.longbeam": { "damage": 11.5 },
        "demo.pack.orb": { "damage": -1 },
        "removed.pack.gone": { "damage": 3 }
    } }
    """);
    var tuningErrors = new List<string>();
    var gears = EnergyLab.Freeze(tuningErrors);
    var tuned = gears.Single(g => g.Definition.Id == "demo.pack.longbeam").Definition.Tuning;
    Check(tuned is BeamTuning { Damage: 11.5f }, "layers: the player's override file wins over the rundown's Tuning");
    Check(gears.Single(g => g.Definition.Id == "demo.pack.orb").Definition.Tuning is { } orbTuning && (float)orbTuning.GetType().GetProperty("Damage")!.GetValue(orbTuning)! == 7f, "layers: the rundown's Tuning is laid over the definition's when the guns freeze");
    Check(!EnergyLab.Register(Gun("demo.late.gun"), out var late) && late!.Contains("closed"), "register: refused after freeze");
}
finally { Directory.Delete(work, true); }
return failures == 0 ? 0 : 1;
