using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeWeaponEnergyLab;

var failures = 0;
void Check(bool ok, string what) { if (!ok) failures++; Console.WriteLine((ok ? "PASS " : "FAIL ") + what); }

var mod = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "../.."));
static string Here([CallerFilePath] string path = "") => path;
Dictionary<string, string> Load(string folder, string name) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(mod, folder, name + ".json")))!;

// The plugin's own language files: the default description and the mismatch chat line, in both languages.
var en = Load("lang", "en");
var zh = Load("lang", "zh");
Check(en.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(zh.Keys.OrderBy(k => k, StringComparer.Ordinal)), "lang: zh.json and en.json have the same keys");
Check(en["weapon.description"] != zh["weapon.description"], "lang: the default description exists in both languages");

var work = Path.Combine(Path.GetTempPath(), "energylab-core-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    EnergyLab.Initialize(Path.Combine(work, "config"));

    // The shipped file: exactly the eight guns, each with a Chinese and an English name beside it.
    var shipped = Path.Combine(mod, "ForgeEnergyLab");
    var shippedFile = Path.Combine(shipped, "energylab.json");
    var shippedErrors = new List<string>();
    var definitions = EnergyContent.Parse(shippedFile, File.ReadAllText(shippedFile), shippedErrors);
    Check(shippedErrors.Count == 0, "shipped file: no parse errors " + string.Join("; ", shippedErrors));
    Check(definitions.Count == 8, "shipped file: exactly eight guns, found " + definitions.Count);
    Check(definitions.Select(d => d.Mode).Distinct().Count() == 8, "shipped file: the eight behaviours, once each");
    var enNames = Load("ForgeEnergyLab/lang", "en");
    var zhNames = Load("ForgeEnergyLab/lang", "zh");
    Check(enNames.Count == 8 && zhNames.Count == 8, "shipped lang: one name per gun in each language and nothing else");
    const string Donor = "{\"Ver\":1,\"Name\":\"Donor Gun\",\"Packet\":{\"Comps\":{\"Length\":5,\"a\":{\"c\":2,\"v\":4},\"b\":{\"c\":3,\"v\":108},"
        + "\"d\":{\"c\":5,\"v\":34},\"f\":{\"c\":7,\"v\":5},\"n\":{\"c\":21,\"v\":7}},\"MatTrans\":{},\"publicName\":{\"data\":\"Donor Gun\"}}}";
    foreach (var definition in definitions)
    {
        var gear = new EnergyGear(definition);
        var packet = JsonNode.Parse(EnergyGearJson.Build(Donor, gear))!.AsObject();
        Check(packet["Packet"]!["publicName"]!["data"]!.GetValue<string>() == gear.Name && gear.Name != "Donor Gun", definition.Id + ": the gear packet carries the gun's name");
        Check(gear.Name.Length <= 25, definition.Id + ": the packet name fits the 25-character gear name");
        Check(enNames.TryGetValue(definition.Id + ".Name", out var enName) && enName == gear.Name, definition.Id + ": the English name is the packet name");
        Check(zhNames.TryGetValue(definition.Id + ".Name", out var zhName) && !string.IsNullOrWhiteSpace(zhName), definition.Id + ": has a Chinese name");
        Check(Path.GetDirectoryName(gear.ModelPath) == Path.GetFullPath(shipped) && Path.GetFileName(gear.ModelPath) == "forge-energy-model-" + definition.Model.Key + ".bundle",
            definition.Id + ": its bundle sits beside the file");
    }
    Check(definitions.Select(d => d.Name).Distinct().Count() == 8, "every shipped gun has its own name");
    Check(definitions.Select(d => d.Mode).SequenceEqual(new[] { EnergyMode.Beam, EnergyMode.ArcChain, EnergyMode.Flame, EnergyMode.PlasmaBlast,
        EnergyMode.PlasmaArc, EnergyMode.Blast, EnergyMode.Disc, EnergyMode.BlackHole }), "shipped file: the eight guns are in the list order");
    // All eight register together: their native IDs do not collide.
    var shippedRefused = definitions.Select(d => EnergyLab.Register(d, out var why) ? "" : d.Id + ": " + why).Where(s => s.Length != 0).ToArray();
    Check(shippedRefused.Length == 0, "shipped file: all eight register together " + string.Join("; ", shippedRefused));

    // Definition files: a bad entry costs only itself.
    var pack = Path.Combine(work, "pack");
    Directory.CreateDirectory(Path.Combine(pack, "models"));
    File.WriteAllText(Path.Combine(pack, "guns.json"), """
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
    var guns = Path.Combine(pack, "guns.json");
    var ids = loaded.Select(l => l.Definition.Id).ToArray();
    Check(ids.SequenceEqual(new[] { "demo.pack.longbeam", "demo.pack.orb" }) && loaded.All(l => l.Folder == pack), "pack: the two good entries register, the rest are skipped");
    var beam = loaded[0].Definition;
    Check(beam.Model.BundlePath == Path.Combine(pack, "models", "long.bundle") && beam.Description == "A beam.", "pack: a relative Bundle resolves inside the file's folder");
    Check(beam.Tuning is BeamTuning { Damage: 9, Magazine: 20 }, "pack: Tuning fields merge over the behaviour's defaults");
    string Line(string needle) => errors.FirstOrDefault(e => e.Contains(needle, StringComparison.Ordinal)) ?? "";
    Check(Line("pack.short").Contains(guns) && Line("pack.short").Contains("id"), "error: a two-segment Id names the file, the Id and the field");
    Check(Line("demo.pack.escape").Contains("Model.Bundle") && Line("demo.pack.escape").Contains(".."), "error: '..' in Bundle is refused");
    Check(Line("demo.pack.nested").Contains("Model.Bundle"), "error: a backslash '..' step is refused");
    Check(Line("demo.pack.rooted").Contains("Model.Bundle"), "error: an absolute Bundle is refused");
    Check(Line("demo.pack.wrongfield").Contains("Tuning.blastRadius"), "error: a field of another behaviour is refused");
    Check(Line("demo.pack.off").Contains("Mode"), "error: Mode Off is refused");
    Check(Line("demo.pack.badslot").Contains("Slot"), "error: an unknown Slot is refused");
    Check(Line("demo.pack.range").Contains("interval"), "error: an out-of-range tuning value names its field");
    Check(errors.Any(e => e.Contains("Duplicate weapon id: demo.pack.longbeam")), "error: a repeated Id is refused");
    Check(errors.Any(e => e.Contains("[11].Id")), "error: an entry without Id is named by its position");
    Check(errors.Count == 10, "pack: one line per bad entry, " + errors.Count + " lines");

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
    Check(!EnergyLab.Register(null!, out var none) && none != null, "register: null is refused, not thrown");

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
    Check(gears.Select(g => g.Definition.Id).SequenceEqual(definitions.Select(d => d.Id).Concat(new[] { "demo.pack.longbeam", "demo.pack.orb", first! })),
        "freeze: the list order is the registration order, not the Id order");
    var tuned = gears.Single(g => g.Definition.Id == "demo.pack.longbeam").Definition.Tuning;
    Check(tuned is BeamTuning { Damage: 11.5f, Magazine: 20 }, "overrides: a third-party Id is tuned, other values follow the definition");
    Check(tuningErrors.Count(e => e.Contains("removed.pack.gone")) == 1 && tuningErrors.Count(e => e.Contains("demo.pack.orb")) == 1 && tuningErrors.Count == 2,
        "overrides: an unknown Id and a bad entry each give one line: " + string.Join(" | ", tuningErrors));
    Check(gears.Single(g => g.Definition.Id == "demo.pack.orb").Definition.Tuning is PlasmaBlastTuning { Damage: 12 }, "overrides: a bad entry keeps that gun's own tuning");
    Check(File.ReadAllText(Path.Combine(config, EnergyConfiguration.DefaultsFile)).Contains("demo.pack.longbeam"), "defaults file: lists third-party guns too");
    Check(!EnergyLab.Register(Gun("demo.late.gun"), out var late) && late!.Contains("closed"), "register: refused after freeze");
}
finally { Directory.Delete(work, true); }
return failures == 0 ? 0 : 1;
