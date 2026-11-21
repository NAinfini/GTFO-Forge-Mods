using ForgeDevelopment.Native;

// Parse the shipped files and exercise source-linked lookup/reader safeguards with native boundary doubles.
// This does not install Harmony patches or invoke game methods.
var configOnly = args.Length == 2 && args[0] == "--config-only";
if ((!configOnly && args.Length != 1) || !Directory.Exists(args[configOnly ? 1 : 0]))
{
    Console.Error.WriteLine("Usage: ProbeAssets <probes-directory> | --config-only <installed-config-directory>");
    return 2;
}
var root = Path.GetFullPath(args[configOnly ? 1 : 0]);
var failures = 0;
var passed = 0;
void Check(bool ok, string message)
{
    if (ok) { passed++; Console.WriteLine("PASS: " + message); }
    else { failures++; Console.WriteLine("FAIL: " + message); }
}
TraceProfileCases.Run(root, Check);
if (!configOnly) DeferredCatalogCases.Run(root, Check);
LifecycleReadbackCases.Run(Check);
LoadedComponentCases.Run(Check);
ObservationReadCases.Run(Check);
var commandFiles = Directory.GetFiles(Path.Combine(root, "commands"), "*.forge").OrderBy(x => x, StringComparer.Ordinal).ToArray();
var traceFiles = Directory.GetFiles(Path.Combine(root, "trace"), "*.forge").OrderBy(x => x, StringComparer.Ordinal).ToArray();
Check(commandFiles.Length > 0, "command files present");
Check(traceFiles.Length > 0, "trace files present");
// ExperimentCatalog and ExperimentRunner key commands by ID across all files.
// A per-file set lets one command silently replace another at runtime.
var commandIds = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var file in commandFiles)
{
    var commands = ExperimentParser.Parse(File.ReadAllText(file), Path.GetFileName(file), out var error);
    Check(error.Length == 0 && commands.Count > 0, Path.GetFileName(file) + " parsed: " + error);
    foreach (var command in commands)
    {
        Check(command.Problems.Count == 0, command.Source + ": " + string.Join("; ", command.Problems));
        if (commandIds.TryGetValue(command.Id, out var firstSource))
            Check(false, "duplicate command ID " + command.Id + ": " + firstSource + " and " + command.Source);
        else
        {
            commandIds.Add(command.Id, command.Source);
            Check(true, command.Source + " id is globally unique");
        }
    }
}
var profiles = RecTracer.Parse(traceFiles);
C4LandingReadbackCases.Run(profiles, Check, !configOnly);
NoiseProfileCases.Run(profiles, Check);
var profileNames = new HashSet<string>(StringComparer.Ordinal);
foreach (var profile in profiles)
{
    Check(profile.Error is null && profile.Entries.Count > 0, profile.Source + " parsed: " + profile.Error);
    Check(profileNames.Add(profile.Name), profile.Source + " profile is unique");
}
// A multi-step command file is validated per step: a good combination is admitted, a broken one is refused naming the step.
{
    const string step = "{\"target\":\"static:ForgeDevelopment.Native.GameInputDrive\",\"call\":\"HoldKey\",\"args\":[\"Fire\"]}";
    const string bad = "{\"target\":\",static:ForgeDevelopment.Native.GameInputDrive,\",\"call\":\"Look\"}";
    string Compose(params string[] steps) => "{\"id\":\"ds-x\",\"target\":\"localPlayer\",\"steps\":[" + string.Join(",", steps) + "]}";
    var good = ExperimentParser.Parse(Compose(step, "{\"wait\":{\"seconds\":1}}", step), "good.forge", out _);
    Check(good.Count == 1 && good[0].Valid, "multi-step command is valid: " + string.Join("; ", good.SelectMany(command => command.Problems)));
    var refusal = ExperimentCatalog.Refusal("ds-x", ExperimentParser.Parse(Compose(step, step, bad), "broken.forge", out _));
    Check(refusal.Contains("steps[2]") && refusal.Contains("target") && refusal.Contains("broken.forge"), "broken combination names step and reason: " + refusal);
}
if (configOnly) Console.WriteLine("CONFIG ONLY: command/trace parsers checked.");
Console.WriteLine($"{passed}/{passed + failures} passed; failed={failures}; production parsers/lookup/readers; native boundaries doubled; gameExecuted=false");
return failures == 0 ? 0 : 1;
