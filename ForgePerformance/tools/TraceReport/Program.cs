// Where GTFO's CPU time goes, from a WPR trace with CPU sampling and context switches (wpr -start CPU).
//   dotnet run -c Release -- <trace.etl> <dump.cs> <out.md> [--process GTFO.exe] [--symbols <symbol path>] [--main <thread id>]
//                             [--gameassembly <GTFO>\GameAssembly.dll] [--focus <frame text>]... [--windows <label/start/end tsv>]
// GameAssembly.dll has no PDB: its frames are named from Il2CppDumper's dump.cs, whose methods carry "// RVA: 0x..."; a frame
// takes the nearest method at or below its RVA. Other images (UnityPlayer.dll, Windows, drivers) use the symbol path, by default
// Unity's and Microsoft's public symbol servers. Threads are grouped by name: main (the thread running the player loop),
// render (UnityGfxDeviceWorker), workers (Unity job and loading threads) and other.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;

static string Arg(string[] args, string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: TraceReport <trace.etl> <dump.cs> <out.md> [--process GTFO.exe] [--symbols <path>] [--main <tid>]");
    return 2;
}
var etl = args[0];
var processName = Arg(args, "--process", "GTFO.exe");
var cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, "symcache");
var symbolPath = Arg(args, "--symbols",
    $"srv*{cache}*https://symbolserver.unity3d.com/;srv*{cache}*https://msdl.microsoft.com/download/symbols");
var forcedMain = int.TryParse(Arg(args, "--main", ""), out var m) ? m : -1;

var methods = DumpMethods.Load(args[1], Arg(args, "--gameassembly", @"E:\SteamLibrary\steamapps\common\GTFO\GameAssembly.dll"));
Console.WriteLine($"dump.cs methods with an RVA: {methods.Count}");

using var trace = TraceProcessor.Create(etl, new TraceProcessorSettings { AllowLostEvents = true });
var samplesResult = trace.UseCpuSamplingData();
var symbolsResult = trace.UseSymbols();
var schedulingResult = trace.UseCpuSchedulingData();
trace.Process();
await symbolsResult.Result.LoadSymbolsAsync(new SymCachePath(cache), new SymbolPath(symbolPath));

var samples = samplesResult.Result.Samples
    .Where(s => s.Process != null && string.Equals(s.Process.ImageName, processName, StringComparison.OrdinalIgnoreCase))
    .ToList();
if (samples.Count == 0) { Console.Error.WriteLine("no samples for " + processName); return 1; }
var first = samples.Min(s => (double)s.Timestamp.RelativeTimestamp.TotalMilliseconds);
var last = samples.Max(s => (double)s.Timestamp.RelativeTimestamp.TotalMilliseconds);
var wallMs = last - first;

string FrameName(StackFrame frame)
{
    var image = frame.Image;
    if (image == null) return "?!" + frame.Address.Value.ToString("X", CultureInfo.InvariantCulture);
    var file = image.FileName ?? "?";
    if (file.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
        return "GameAssembly!" + methods.Name((ulong)(frame.Address.Value - image.AddressRange.BaseAddress.Value));
    var symbol = frame.Symbol?.FunctionName;
    return file + "!" + (string.IsNullOrEmpty(symbol) ? "?" : symbol);
}

string ThreadName(ICpuSample s) => s.Thread?.Name ?? "";
// Group threads; the main thread is the unnamed one whose stacks most often hold GameAssembly MonoBehaviour updates.
var threadSamples = samples.GroupBy(s => s.Thread!.Id).ToDictionary(g => g.Key, g => g.ToList());
int mainThread = forcedMain;
if (mainThread < 0)
{
    mainThread = threadSamples
        .Where(kv => string.IsNullOrEmpty(ThreadName(kv.Value[0])))
        .OrderByDescending(kv => kv.Value.Count(s => s.Stack?.Frames.Any(f => FrameName(f).Contains("PlayerLoop", StringComparison.Ordinal)) == true))
        .ThenByDescending(kv => kv.Value.Count)
        .First().Key;
}
string Group(ICpuSample s)
{
    if (s.Thread!.Id == mainThread) return "main";
    var name = ThreadName(s);
    if (name.Contains("Gfx", StringComparison.OrdinalIgnoreCase) || name.Contains("Render", StringComparison.OrdinalIgnoreCase)) return "render";
    if (name.Contains("Worker", StringComparison.OrdinalIgnoreCase) || name.Contains("Job", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Loading", StringComparison.OrdinalIgnoreCase)) return "workers";
    return "other";
}

var report = new StringBuilder();
report.AppendLine("# GTFO CPU sampling").AppendLine();
report.AppendLine($"Trace `{Path.GetFileName(etl)}`, process {processName}, sampled span {wallMs / 1000:0.0} s, {samples.Count} samples, main thread {mainThread}, UTC {samples.Min(s => s.Timestamp.DateTimeOffset).UtcDateTime:HH:mm:ss.fff}–{samples.Max(s => s.Timestamp.DateTimeOffset).UtcDateTime:HH:mm:ss.fff}.").AppendLine();
report.AppendLine("## Threads").AppendLine();
report.AppendLine("| thread | name | group | CPU ms | CPU % of span |").AppendLine("|---|---|---|---|---|");
foreach (var (id, list) in threadSamples.OrderByDescending(kv => kv.Value.Sum(s => (double)s.Weight.TotalMilliseconds)).Take(25))
{
    var ms = list.Sum(s => (double)s.Weight.TotalMilliseconds);
    report.AppendLine($"| {id} | {ThreadName(list[0])} | {Group(list[0])} | {ms:0} | {100 * ms / wallMs:0.0} |");
}

// Main-thread scheduling: time on a CPU against time waiting, from the context switches.
var slices = schedulingResult.Result.CpuTimeSlices.Where(t => t.Process != null
    && string.Equals(t.Process.ImageName, processName, StringComparison.OrdinalIgnoreCase) && t.Thread?.Id == mainThread).ToList();
if (slices.Count > 0)
{
    var running = slices.Where(t => (double)t.StartTime.RelativeTimestamp.TotalMilliseconds >= first && (double)t.StartTime.RelativeTimestamp.TotalMilliseconds <= last)
        .Sum(t => (double)t.Duration.TotalMilliseconds);
    report.AppendLine().AppendLine($"Main thread on a CPU {running:0} ms of {wallMs:0} ms ({100 * running / wallMs:0.0}%), from {slices.Count} context-switch slices.");
}

foreach (var group in new[] { "main", "render", "workers", "other" })
{
    var list = samples.Where(s => Group(s) == group).ToList();
    if (list.Count == 0) continue;
    var total = list.Sum(s => (double)s.Weight.TotalMilliseconds);
    var inclusive = new Dictionary<string, double>();
    var exclusive = new Dictionary<string, double>();
    foreach (var s in list)
    {
        var frames = s.Stack?.Frames;
        if (frames == null || frames.Count == 0) continue;
        var w = (double)s.Weight.TotalMilliseconds;
        var leaf = FrameName(frames[0]);
        exclusive[leaf] = exclusive.GetValueOrDefault(leaf) + w;
        foreach (var name in frames.Select(FrameName).Distinct())
            inclusive[name] = inclusive.GetValueOrDefault(name) + w;
    }
    report.AppendLine().AppendLine($"## {group}: {total:0} ms CPU ({total / (wallMs / 1000):0.0} ms per second)").AppendLine();
    foreach (var (title, table) in new[] { ("inclusive", inclusive), ("exclusive", exclusive) })
    {
        report.AppendLine($"Top 20 by {title} time:").AppendLine().AppendLine("| function | ms | % of group |").AppendLine("|---|---|---|");
        foreach (var (name, ms) in table.OrderByDescending(kv => kv.Value).Take(20))
            report.AppendLine($"| `{name.Replace("|", "\\|")}` | {ms:0} | {100 * ms / total:0.0} |");
        report.AppendLine();
    }
    if (group != "main") continue;
    // Main thread by player-loop phase (the first frame under the innermost ExecutePlayerLoop, past the hook stubs), by script
    // entry (the outermost named managed method, i.e. which Update/LateUpdate/coroutine Unity called) and by the innermost
    // named managed method (managed code plus the engine calls it makes itself).
    static bool Wrapper(string n) => n.StartsWith("BepInEx", StringComparison.Ordinal) || n.StartsWith("ForgeDevelopment", StringComparison.Ordinal)
        || n.StartsWith("coreclr.dll", StringComparison.Ordinal) || n.StartsWith("?!", StringComparison.Ordinal);
    static bool Managed(string n) => n.StartsWith("GameAssembly!", StringComparison.Ordinal) && !n.StartsWith("GameAssembly![export]", StringComparison.Ordinal)
        && !n.StartsWith("GameAssembly![il2cpp runtime]", StringComparison.Ordinal);
    var phase = new Dictionary<string, double>();
    var entry = new Dictionary<string, double>();
    var innermost = new Dictionary<string, double>();
    foreach (var s in list)
    {
        var frames = s.Stack?.Frames;
        if (frames == null || frames.Count == 0) continue;
        var w = (double)s.Weight.TotalMilliseconds;
        var names = frames.Select(FrameName).Reverse().ToList(); // root first
        var loop = names.FindLastIndex(n => n == "UnityPlayer.dll!ExecutePlayerLoop");
        var p = loop < 0 ? "(outside the player loop)" : names.Skip(loop + 1).FirstOrDefault(n => !Wrapper(n)) ?? "(player loop itself)";
        phase[p] = phase.GetValueOrDefault(p) + w;
        var e = names.FirstOrDefault(Managed) ?? "(no managed frame)";
        entry[e] = entry.GetValueOrDefault(e) + w;
        var i = names.LastOrDefault(Managed) ?? "(no managed frame)";
        innermost[i] = innermost.GetValueOrDefault(i) + w;
    }
    foreach (var (title, table, take) in new[] { ("player-loop phase", phase, 25), ("script entry (outermost managed method)", entry, 30),
                 ("innermost managed method", innermost, 30) })
    {
        report.AppendLine($"Main thread by {title}:").AppendLine().AppendLine("| function | ms | ms per second | % of group |").AppendLine("|---|---|---|---|");
        foreach (var (name, ms) in table.OrderByDescending(kv => kv.Value).Take(take))
            report.AppendLine($"| `{name.Replace("|", "\\|")}` | {ms:0} | {ms / (wallMs / 1000):0.00} | {100 * ms / total:0.0} |");
        report.AppendLine();
    }
    foreach (var prefix in new[] { "GameAssembly!Enemies.", "GameAssembly!Player.", "GameAssembly!LevelGeneration.", "GameAssembly!AIGraph." })
    {
        var ms = entry.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value);
        report.AppendLine($"Script entries in `{prefix[13..]}*`: {ms:0} ms ({ms / (wallMs / 1000):0.00} ms per second).");
    }
}
// A plugin frame is code loaded from BepInEx\plugins (Forge packages and other mods), named by its own module.
bool PluginFrame(StackFrame f) => f.Image?.Path is { } p && p.Contains(@"\BepInEx\plugins\", StringComparison.OrdinalIgnoreCase);
static bool Wrapping(string n) => n.Contains("NativeFunctionTimingInstallation", StringComparison.Ordinal);
static void Table(StringBuilder r, string title, Dictionary<string, double> t, double total, double seconds, int take)
{
    r.AppendLine($"{title}:").AppendLine().AppendLine("| function | ms | ms per second | % |").AppendLine("|---|---|---|---|");
    foreach (var (name, ms) in t.OrderByDescending(kv => kv.Value).Take(take))
        r.AppendLine($"| `{name.Replace("|", "\\|")}` | {ms:0} | {ms / seconds:0.00} | {100 * ms / total:0.0} |");
    r.AppendLine();
}
void Add(Dictionary<string, double> t, string k, double w) => t[k] = t.GetValueOrDefault(k) + w;
// Main-thread time per plugin module and per innermost plugin method, and in the CLR garbage collector.
{
    var main = samples.Where(s => s.Thread!.Id == mainThread).ToList();
    var total = main.Sum(s => (double)s.Weight.TotalMilliseconds);
    var modules = new Dictionary<string, double>();
    var inner = new Dictionary<string, double>();
    double clrGc = 0;
    foreach (var s in main)
    {
        var frames = s.Stack?.Frames;
        if (frames == null) continue;
        var w = (double)s.Weight.TotalMilliseconds;
        foreach (var file in frames.Where(PluginFrame).Select(f => f.Image!.FileName).Distinct()) Add(modules, file!, w);
        var hit = frames.Where(PluginFrame).Select(FrameName).FirstOrDefault(n => !Wrapping(n));
        if (hit != null) Add(inner, hit, w);
        if (frames.Any(f => f.Image?.FileName == "coreclr.dll" && (f.Symbol?.FunctionName ?? "").Contains("gc_heap", StringComparison.Ordinal))) clrGc += w;
    }
    report.AppendLine("## main thread: plugins (code under BepInEx\\plugins)").AppendLine();
    report.AppendLine($"CLR garbage collection on the main thread: {clrGc:0} ms ({clrGc / (wallMs / 1000):0.00} ms per second).").AppendLine();
    Table(report, "Plugin modules on the stack (inclusive, a timing wrapper counts its whole callee)", modules, total, wallMs / 1000, 20);
    Table(report, "Innermost plugin method (timing wrapper skipped)", inner, total, wallMs / 1000, 30);
}
// --focus <text> (repeatable): every sample whose stack holds a matching frame, by thread group and by the callers above it.
for (var a = 0; a < args.Length - 1; a++)
{
    if (args[a] != "--focus") continue;
    var focus = args[a + 1];
    var chains = new Dictionary<string, double>();
    var callees = new Dictionary<string, double>();
    var plugins = new Dictionary<string, double>();
    double total = 0;
    foreach (var s in samples)
    {
        var frames = s.Stack?.Frames;
        if (frames == null) continue;
        var names = frames.Select(FrameName).Reverse().ToList(); // root first
        var at = names.FindIndex(n => n.Contains(focus, StringComparison.Ordinal));
        if (at < 0) continue;
        var w = (double)s.Weight.TotalMilliseconds;
        total += w;
        var callers = names.Take(at).Where(n => !n.StartsWith("ntdll", StringComparison.Ordinal) && !n.StartsWith("kernel32", StringComparison.Ordinal)
            && !n.StartsWith("BepInEx", StringComparison.Ordinal) && !Wrapping(n) && !n.Contains("[il2cpp runtime]", StringComparison.Ordinal)).TakeLast(6);
        Add(chains, Group(s) + ": " + string.Join(" > ", callers) + " > **" + names[at] + "**", w);
        Add(callees, names.Count > at + 1 ? names[at + 1] : "(itself)", w);
        // frames runs leaf first, so the frames below the match are the first Count - 1 - at; the last plugin one is nearest the match.
        var below = frames.Take(frames.Count - 1 - at).Where(PluginFrame).Select(FrameName).LastOrDefault(n => !Wrapping(n));
        Add(plugins, below ?? "(no plugin frame below)", w);
    }
    report.AppendLine($"## focus `{focus}`: {total:0} ms ({total / (wallMs / 1000):0.00} ms per second)").AppendLine();
    Table(report, "Caller chains (thread group: callers > match)", chains, Math.Max(total, 1), wallMs / 1000, 15);
    Table(report, "Direct callees of the match", callees, Math.Max(total, 1), wallMs / 1000, 10);
    Table(report, "Outermost plugin method below the match", plugins, Math.Max(total, 1), wallMs / 1000, 10);
}
// --windows <tsv of label, start, end in UTC ISO 8601>: main-thread samples inside those windows (e.g. hitch frames), per label.
var windowFile = Arg(args, "--windows", "");
if (windowFile.Length > 0)
{
    var windows = File.ReadAllLines(windowFile).Select(l => l.Split('\t')).Where(p => p.Length >= 3)
        .Select(p => (label: p[0], start: DateTimeOffset.Parse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            end: DateTimeOffset.Parse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal))).ToList();
    foreach (var label in windows.Select(w => w.label).Distinct())
    {
        var mine = windows.Where(w => w.label == label).ToList();
        var inside = samples.Where(s => s.Thread!.Id == mainThread && mine.Any(w => s.Timestamp.DateTimeOffset >= w.start && s.Timestamp.DateTimeOffset <= w.end)).ToList();
        var total = inside.Sum(s => (double)s.Weight.TotalMilliseconds);
        var span = mine.Sum(w => (w.end - w.start).TotalMilliseconds);
        var entry = new Dictionary<string, double>();
        var innermost = new Dictionary<string, double>();
        var leaf = new Dictionary<string, double>();
        var plugin = new Dictionary<string, double>();
        foreach (var s in inside)
        {
            var frames = s.Stack?.Frames;
            if (frames == null || frames.Count == 0) continue;
            var w = (double)s.Weight.TotalMilliseconds;
            var names = frames.Select(FrameName).Reverse().ToList();
            bool Managed(string n) => n.StartsWith("GameAssembly!", StringComparison.Ordinal) && !n.Contains("[il2cpp runtime]", StringComparison.Ordinal)
                && !n.StartsWith("GameAssembly![export]", StringComparison.Ordinal);
            Add(entry, names.FirstOrDefault(Managed) ?? "(no managed frame)", w);
            Add(innermost, names.LastOrDefault(Managed) ?? "(no managed frame)", w);
            Add(leaf, names[^1], w);
            Add(plugin, frames.Where(PluginFrame).Select(FrameName).FirstOrDefault(n => !Wrapping(n)) ?? "(no plugin frame)", w);
        }
        report.AppendLine($"## windows `{label}`: {mine.Count} windows, {span:0} ms of wall time, {total:0} ms of main-thread samples").AppendLine();
        Table(report, "Script entry", entry, Math.Max(total, 1), Math.Max(span, 1) / 1000, 15);
        Table(report, "Innermost managed method", innermost, Math.Max(total, 1), Math.Max(span, 1) / 1000, 15);
        Table(report, "Innermost plugin method", plugin, Math.Max(total, 1), Math.Max(span, 1) / 1000, 10);
        Table(report, "Leaf (exclusive)", leaf, Math.Max(total, 1), Math.Max(span, 1) / 1000, 15);
    }
}
File.WriteAllText(args[2], report.ToString());
Console.WriteLine("wrote " + args[2]);
return 0;

sealed class DumpMethods
{
    private readonly ulong[] _rva;
    private readonly string[] _name;
    private DumpMethods(ulong[] rva, string[] name) { _rva = rva; _name = name; }
    public int Count => _rva.Length;

    // il2cpp's runtime (invoke, GC, allocation) is not in dump.cs; GameAssembly.dll exports its il2cpp_* entry points by name.
    static IEnumerable<(ulong, string)> Exports(string dll)
    {
        if (!File.Exists(dll)) { Console.Error.WriteLine("no GameAssembly.dll at " + dll + "; runtime frames stay unnamed"); yield break; }
        var bytes = File.ReadAllBytes(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (dir.Size == 0) yield break;
        int Offset(int rva) => pe.PEHeaders.TryGetDirectoryOffset(new System.Reflection.PortableExecutable.DirectoryEntry(rva, 1), out var o) ? o : -1;
        int I32(int at) => BitConverter.ToInt32(bytes, at);
        int U16(int at) => BitConverter.ToUInt16(bytes, at);
        var e = Offset(dir.RelativeVirtualAddress);
        int count = I32(e + 24), functions = Offset(I32(e + 28)), names = Offset(I32(e + 32)), ordinals = Offset(I32(e + 36));
        for (var i = 0; i < count; i++)
        {
            var nameAt = Offset(I32(names + 4 * i));
            var end = Array.IndexOf(bytes, (byte)0, nameAt);
            yield return ((uint)I32(functions + 4 * U16(ordinals + 2 * i)), "[export] " + Encoding.ASCII.GetString(bytes, nameAt, end - nameAt));
        }
    }

    public static DumpMethods Load(string path, string gameAssembly)
    {
        var rows = new List<(ulong, string)>(Exports(gameAssembly));
        var type = "";
        ulong pending = 0;
        var typeLine = new Regex(@"^(?:\[.*\]\s*)?(?:public|internal|private|protected|sealed|abstract|static|\s)*(class|struct|interface|enum)\s+([^\s:{]+)");
        var rvaLine = new Regex(@"// RVA: 0x([0-9A-Fa-f]+) Offset");
        var methodLine = new Regex(@"^\s+[^/\[].*?\s([^\s(]+)\(");
        var genericRva = new Regex(@"^\s*\|-RVA: 0x([0-9A-Fa-f]+)");
        string ns = "";
        ulong generic = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("// Namespace: ", StringComparison.Ordinal)) { ns = line[14..].Trim(); continue; }
            var t = typeLine.Match(line);
            if (t.Success && !line.StartsWith('\t') && !line.StartsWith(' ')) { type = (ns.Length > 0 ? ns + "." : "") + t.Groups[2].Value; continue; }
            // Generic instances are listed in a comment block: "|-RVA: 0x…" and then the instance names, one per "|-" line.
            var g = genericRva.Match(line);
            if (g.Success) { generic = ulong.Parse(g.Groups[1].Value, NumberStyles.HexNumber); continue; }
            if (generic != 0)
            {
                var text = line.TrimStart();
                if (text.StartsWith("|-", StringComparison.Ordinal)) { rows.Add((generic, text[2..].Trim())); generic = 0; continue; }
                if (!text.StartsWith('|')) generic = 0;
            }
            // The RVA comment may follow an attribute on the same line.
            var r = rvaLine.Match(line);
            if (r.Success) { pending = ulong.Parse(r.Groups[1].Value, NumberStyles.HexNumber); continue; }
            if (pending != 0)
            {
                var mm = methodLine.Match(line);
                if (mm.Success) rows.Add((pending, type + "." + mm.Groups[1].Value));
                pending = 0;
            }
        }
        // Identical bodies share one RVA (the linker folds them); the name says how many others share it.
        var merged = rows.GroupBy(r => r.Item1).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Count() == 1 ? g.First().Item2 : $"{g.First().Item2} (+{g.Count() - 1} sharing this body)")).ToList();
        return new DumpMethods(merged.Select(r => r.Key).ToArray(), merged.Select(r => r.Item2).ToArray());
    }

    public string Name(ulong rva)
    {
        var i = Array.BinarySearch(_rva, rva);
        if (i < 0) i = ~i - 1;
        // Exports are sparse: past a short distance the address is il2cpp runtime code no export or dump.cs method names.
        if (i < 0 || (_name[i].StartsWith("[export]", StringComparison.Ordinal) && rva - _rva[i] > 0x400))
            return "[il2cpp runtime] 0x" + rva.ToString("X", CultureInfo.InvariantCulture);
        return _name[i];
    }
}
