namespace ForgeDevelopment.Native;

internal static class DeferredCatalogCases
{
    internal static void Run(string probes, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-boot-commands-" + Guid.NewGuid().ToString("N"));
        DevelopmentPaths.Root = root;
        int loads = 0;
        try
        {
            ExperimentCatalog.DeferLoad(() => { loads++; ExperimentCatalog.Seed(Path.Combine(probes, "commands")); ExperimentCatalog.Load(); });
            check(loads == 0 && !Directory.Exists(root), "startup defers all command directory IO and parsing");
            var commands = ExperimentCatalog.All;
            check(loads == 1 && commands.Count > 0 && ExperimentCatalog.LoadProblems.Count == 0, "first panel/capture/Run lookup seeds and validates the real catalog");
            check(ReferenceEquals(commands, ExperimentCatalog.All) && loads == 1, "subsequent consumers do not reload commands");
            File.WriteAllText(Path.Combine(ExperimentCatalog.DirectoryPath, "invalid.forge"), "{");
            ExperimentCatalog.Load();
            check(ExperimentCatalog.LoadProblems.Count > 0, "explicit reload still reports malformed commands");
            var failure = new IOException("catalog loader failure");
            var attempts = 0;
            var errors = Plugin.PluginLog.Errors.Count;
            ExperimentCatalog.DeferLoad(() => { attempts++; throw failure; });
            foreach (var access in new Action[] { () => _ = ExperimentCatalog.All, () => _ = ExperimentCatalog.LoadProblems,
                () => _ = ExperimentCatalog.All, () => _ = ExperimentCatalog.LoadProblems })
            {
                try { access(); check(false, "failed catalog became usable"); }
                catch (IOException error) { check(ReferenceEquals(error, failure), "every catalog access retains the original failure"); }
            }
            check(attempts == 1 && Plugin.PluginLog.Errors.Count == errors + 1
                && ReferenceEquals(Plugin.PluginLog.Errors[^1], failure), "loader is attempted once and logs the original exception once");
            ExperimentCatalog.Load();
            check(ExperimentCatalog.All.Count > 0, "explicit reload clears the sticky failure");
            Console.WriteLine($"BOOT commands loadsBeforeUse=0 loadsAfterRepeatedUse={loads} files={Directory.GetFiles(ExperimentCatalog.DirectoryPath, "*.forge").Length}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
// Only the external BepInEx root is substituted; discovery, files and parsers are production code.
internal static class DevelopmentPaths { internal static string Root { get; set; } = ""; }
internal static class Plugin
{
    internal static readonly Logger PluginLog = new();
    internal sealed class Logger
    {
        internal readonly List<object> Errors = new();
        internal void LogError(object error) => Errors.Add(error);
    }
}
