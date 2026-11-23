using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeMap;

// Offline package admission: stages an exported Forge package the way the mod manager installs it, then runs the
// game-independent admission code the mod itself runs at startup: Rundown/Map intake (ForgeMap.MapPackageIntake),
// content pack discovery (ContentPackDiscovery.Scan) and plan discovery with its prerequisite check
// (ContentPlanAdmission), then every plan through the kernel's own plan load against the registry all providers declare
// (OfflineRuntime). Native-table and domain-consumer checks need the running game and are not part of this runner.
// A package that references other packs is admitted together with them: each `--with <package-dir-or-zip>` is installed
// first, beside the package, as the mod manager installs a dependency; only refusals inside the admitted package itself
// are reported. A package whose files cannot be read as a Forge package is refused in the "package" layer.
// `--plans <directory>` loads every `*.plan.json` under the directory, outside any package, through the same kernel load
// (the previous release's frozen plan set is checked this way); its report rows are {plan, loaded, code, detail}.
// Usage: PackageAdmission <package-dir-or-zip>... [--with <dependency-dir-or-zip>]... [--json <report.json>]
//        PackageAdmission --plans <directory> [--json <report.json>]
// Exit code: 0 all accepted, 1 at least one package or plan rejected, 2 usage or missing input path.
var inputs = new List<string>();
var dependencies = new List<string>();
string? jsonPath = null, plansPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
    else if (args[i] == "--with" && i + 1 < args.Length) dependencies.Add(args[++i]);
    else if (args[i] == "--plans" && i + 1 < args.Length) plansPath = args[++i];
    else inputs.Add(args[i]);
}
// Exactly one of the two input kinds: packages, or a plan directory.
bool packagesGiven = inputs.Count != 0, plansGiven = plansPath != null;
if (packagesGiven == plansGiven) { Console.Error.WriteLine("Usage: PackageAdmission <package-dir-or-zip>... [--with <dependency-dir-or-zip>]... [--json <report.json>]\n       PackageAdmission --plans <directory> [--json <report.json>]"); return 2; }

// The runtime a plan is loaded against is this release's ForgeEngine, read from the release identity built into the runner.
RuntimeIdentity runtime;
using (var release = JsonDocument.Parse(typeof(OfflineRuntime).Assembly.GetManifestResourceStream("ForgeEngine.ReleaseIdentity.json")!))
{
    var host = release.RootElement.GetProperty("packages").EnumerateArray().Single(row =>
        row.GetProperty("providerId") is { ValueKind: JsonValueKind.String } id && id.GetString() == OfflineRuntime.HostProviderId);
    // No plan check reads the game build, which only the game-bound host knows.
    runtime = new RuntimeIdentity(OfflineRuntime.HostProviderId, host.GetProperty("version").GetString()!, RuntimeKernel.ApiVersion, "offline");
}
var report = new JsonArray();
bool rejectedAny = false;
if (plansPath != null)
{
    if (!Directory.Exists(plansPath)) { Console.Error.WriteLine("Not a plan directory: " + plansPath); return 2; }
    var files = Directory.EnumerateFiles(plansPath, "*.plan.json", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.Ordinal).ToArray();
    var loose = new RuntimeKernel(runtime);
    OfflineRuntime.Register(loose, module => module);
    // The plans are not one install's set (two of them may share a mount), so each is loaded on its own and released again.
    foreach (var file in files)
    {
        var outcome = loose.LoadPlans(new[] { PlanCandidate.Loaded(Path.GetRelativePath(plansPath, file), File.ReadAllText(file)) })[0];
        if (outcome.Loaded) loose.UnloadPlan(outcome.PlanId!);
        string plan = ForgeCore.PosixPath.ToPosix(outcome.Path);
        if (!outcome.Loaded) Console.WriteLine("REJECT " + plan + " code=" + outcome.Code + " detail=" + outcome.Detail);
        rejectedAny |= !outcome.Loaded;
        report.Add(new JsonObject { ["plan"] = plan, ["loaded"] = outcome.Loaded, ["code"] = outcome.Code, ["detail"] = outcome.Detail });
    }
    if (jsonPath != null) File.WriteAllText(jsonPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return rejectedAny ? 1 : 0;
}
foreach (var input in inputs)
{
    string stage = Path.Combine(Path.GetTempPath(), "forge-admission-" + Guid.NewGuid().ToString("N"));
    try
    {
        if (!File.Exists(input) && !Directory.Exists(input)) { Console.Error.WriteLine("Not a package directory or zip: " + input); return 2; }
        string root = Path.Combine(stage, "BepInEx");
        string packageDir = Path.GetFileNameWithoutExtension(input);
        try
        {
            for (int copy = 0; copy < dependencies.Count; copy++)
                Install(dependencies[copy], Path.Combine(stage, "with-" + copy), root);
            packageDir = Install(input, Path.Combine(stage, "input"), root);
        }
        catch (Exception e) when (e is PackageRefusal or IOException or InvalidDataException or System.Text.Json.JsonException
            or UnauthorizedAccessException)
        {
            // The exception text of the framework names staging paths; the report states the cause without them.
            string cause = e is PackageRefusal ? e.Message
                : e is System.Text.Json.JsonException ? "manifest.json or a package file is not valid JSON"
                : e is InvalidDataException ? "the ZIP archive is damaged"
                : e is UnauthorizedAccessException ? "a package file cannot be read: access denied"
                : "a package file cannot be read";
            Console.WriteLine("REJECT " + packageDir + " [package] " + cause);
            rejectedAny = true;
            report.Add(new JsonObject
            {
                ["input"] = input,
                ["package"] = packageDir,
                ["accepted"] = false,
                ["rejections"] = new JsonArray(new JsonObject
                { ["layer"] = "package", ["document"] = Path.GetFileName(input), ["path"] = "", ["code"] = "package-unreadable", ["detail"] = cause })
            });
            continue;
        }
        string ownPrefix = "plugins/" + packageDir + "/";

        var result = MapPackageIntake.Run(root);
        var rejections = result.Stages.SelectMany(s => s.Rejections)
            .Select(row => new Row("map", row.Document, row.Path, row.Reason, row.Detail)).ToList();
        var catalog = ContentPackDiscovery.Scan(root);
        rejections.AddRange(catalog.Rejections.Select(row => new Row("content", row.Entry, row.Path, row.Code,
            row.Detail + (row.Other is { Length: > 0 } ? " other=" + row.Other : ""))));
        var admission = new ContentPlanAdmission(root, runtime,
            catalog, new HashSet<string>(catalog.Accepted.Select(row => row.Entry.Id), StringComparer.Ordinal));
        // Every plan the package carries is loaded the way the game loads it: through the kernel's own plan load, against
        // the registry every provider declares. A plan the game could not load is refused here with the game's own code.
        // The kinds the game reads from the installed files (area fields, weapon property profiles) are read from the staged
        // install the same way, so a compiled reference is checked against the revision the installed file holds; a file the
        // game would refuse is refused here. A refusal line is `<code> file=<path>: <reason>` (PublishedResources.Diagnostic)
        // or `<code>: <reason>` for the whole set.
        void InstallRefused(string line)
        {
            int file = line.IndexOf(" file=", StringComparison.Ordinal), end = file < 0 ? -1 : line.IndexOf(": ", file, StringComparison.Ordinal);
            string relative = end < 0 ? "" : line[(file + 6)..end];
            rejections.Add(new Row("content", relative.Length == 0 ? "resources" : Path.GetFileName(relative), relative,
                line[..(file >= 0 ? file : Math.Max(0, line.IndexOf(':')))], end < 0 ? line : line[(end + 2)..]));
        }
        var kernel = new RuntimeKernel(runtime);
        OfflineRuntime.Register(kernel, module => module, new OfflineRuntime.Install(
            (reader, kind, codes, refused) => ContentPackages.ReadResources(root, reader, kind, codes, refused), InstallRefused),
            ForgeEngine.Presentation.CustomSoundLibrary.Provider());
        // The custom sounds the plans name are found by hash in each plan's own package before the kernel resolves
        // references, exactly as GameRuntimeBridge.Admit does.
        var admitted = admission.Check(PlanDiscovery.Scan(root));
        ForgeEngine.Presentation.CustomSoundLibrary.UsePlans(root,
            admitted.Where(plan => plan.Document is { SyntaxError: null }).Select(plan => (plan.Path, plan.Document!.Syntax)),
            failure => rejections.Add(new Row("content", Path.GetFileName(failure.Relative), failure.Relative, failure.Code, failure.Reason)));
        var plans = kernel.LoadPlans(admitted);
        rejections.AddRange(plans.Where(plan => !plan.Loaded)
            .Select(plan => new Row("plan", Path.GetFileName(plan.Path), plan.Path, plan.Code!, plan.Detail ?? "")));

        // Dependencies were admitted when they were published; only the package under test is judged here. A refusal of
        // the installed resource set names no file (InstallRefused), so it may be this package's own and is kept.
        if (dependencies.Count != 0)
            rejections = rejections.Where(row => row.Path.Length == 0 && row.Document == "resources"
                || ForgeCore.PosixPath.ToPosix(row.Path).StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var row in rejections)
            Console.WriteLine("REJECT " + packageDir + " [" + row.Layer + "] " + row.Document + " code=" + row.Code + " detail=" + row.Detail);
        bool discovered = result.Packs.Count != 0 || catalog.Packs.Count != 0;
        if (!discovered && rejections.Count == 0)
        {
            Console.WriteLine("REJECT " + packageDir + " no Rundown wrapper or content pack discovered");
            rejectedAny = true;
        }
        if (rejections.Count == 0 && discovered)
            Console.WriteLine("ACCEPT " + packageDir + " rundowns=" + result.Packs.Count + " documents="
                + result.Stages.Sum(s => s.Documents.Count) + " contentPacks=" + catalog.Packs.Count + " contentEntries="
                + catalog.Accepted.Count + " plans=" + plans.Count);
        rejectedAny |= rejections.Count != 0;
        report.Add(new JsonObject
        {
            ["input"] = input,
            ["package"] = packageDir,
            ["accepted"] = rejections.Count == 0 && discovered,
            ["rejections"] = new JsonArray(rejections.Select(row => (JsonNode)new JsonObject
            { ["layer"] = row.Layer, ["document"] = row.Document, ["path"] = row.Path, ["code"] = row.Code, ["detail"] = row.Detail }).ToArray())
        });
    }
    finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
}
if (jsonPath != null) File.WriteAllText(jsonPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
return rejectedAny ? 1 : 0;

// Stages one package the way the mod manager installs it and returns the directory name it installs under.
static string Install(string input, string workDir, string root)
{
    string source = input;
    if (File.Exists(input) && input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        source = Path.Combine(workDir, "zip");
        ZipFile.ExtractToDirectory(input, source);
    }
    if (!Directory.Exists(source)) throw new PackageRefusal("the input is not a package directory or zip");
    string manifestFile = Path.Combine(source, "manifest.json");
    if (!File.Exists(manifestFile)) throw new PackageRefusal("the package has no manifest.json");
    var manifest = JsonNode.Parse(File.ReadAllText(manifestFile));
    string pluginFolder = Path.Combine(source, Directory.Exists(Path.Combine(source, "plugins")) ? "plugins" : "");
    if (!Directory.Exists(Path.Combine(source, "plugins")) && !Directory.Exists(Path.Combine(source, "forge")))
        throw new PackageRefusal("the package has no plugins/ or forge/ folder");
    // A Thunderstore manifest has no team, so the package's own identity document names the directory.
    string rundownFile = Path.Combine(pluginFolder, "forge", "rundown.forge");
    string contentPackFile = Path.Combine(pluginFolder, "forge", "content", "pack.forge");
    string packageDir = File.Exists(rundownFile) ? Text(JsonNode.Parse(File.ReadAllText(rundownFile))?["package"], "rundown.forge package")
        : File.Exists(contentPackFile) ? Text(JsonNode.Parse(File.ReadAllText(contentPackFile))?["package"], "content pack.forge package")
        : Text(manifest?["name"], "manifest.json name");
    string installed = Path.Combine(root, "plugins", packageDir);
    // The mod manager installs the zip's plugins/ folder as the package's own directory.
    // A mod's own source folder (manifest.json beside forge/) installs the same way.
    // A zip was just extracted into this stage, so its folder is moved into place instead of written a second time.
    if (Directory.Exists(Path.Combine(source, "plugins")) && source != input && !Directory.Exists(installed))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        Directory.Move(Path.Combine(source, "plugins"), installed);
    }
    else if (Directory.Exists(Path.Combine(source, "plugins"))) Copy(Path.Combine(source, "plugins"), installed);
    else Copy(Path.Combine(source, "forge"), Path.Combine(installed, "forge"));
    return packageDir;
}

static void Copy(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);
    }
}

// A field a package must carry as text, or a refusal that names it.
static string Text(JsonNode? value, string field)
    => value is JsonValue text && text.TryGetValue(out string? result) && result.Length != 0
        ? result : throw new PackageRefusal(field + " is missing or not text");

record Row(string Layer, string Document, string Path, string Code, string Detail);

/// <summary>A package the runner cannot stage, with a cause that names no staging path.</summary>
sealed class PackageRefusal : Exception
{
    public PackageRefusal(string cause) : base(cause) { }
}
