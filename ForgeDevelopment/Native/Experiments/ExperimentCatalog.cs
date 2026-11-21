using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace ForgeDevelopment.Native;

/// <summary>
/// Command files live in the repository under <c>probes/commands</c> and are copied once into
/// <c>BepInEx/ForgeDevelopment/commands</c>. After that the development directory is the source of truth,
/// so a new experiment is a new file and never a rebuild.
/// </summary>
internal static class ExperimentCatalog
{
    private const string ConfigFolder = "commands";
    // One drive client writes its command as drivesession-<id>.forge; the folder keeps the newest receipts.
    private const int KeptDriveSessionFiles = 8;

    private static readonly List<ExperimentCommand> Commands = new();
    private static readonly List<string> Problems = new();

    private static Action? deferredLoad;
    private static ExceptionDispatchInfo? loadFailure;
    internal static void BeginLoadAttempt()
    {
        deferredLoad = null;
        loadFailure = null;
        Problems.Clear();
    }
    internal static void DeferLoad(Action load)
    {
        deferredLoad = load ?? throw new ArgumentNullException(nameof(load));
        loadFailure = null; // Only an explicit new load may clear a failed attempt.
    }
    internal static void RecordLoadFailure(Exception error)
    {
        if (loadFailure != null) return;
        loadFailure = ExceptionDispatchInfo.Capture(error);
        Problems.Add("command loading failed: " + error.GetType().Name + ": " + error.Message);
        Plugin.PluginLog.LogError(error);
    }
    private static void EnsureLoaded()
    {
        loadFailure?.Throw();
        var load = deferredLoad;
        deferredLoad = null; // Clear before the loader reads All/LoadProblems; explicit reload stays immediate.
        try { load?.Invoke(); }
        catch (Exception error) { RecordLoadFailure(error); }
        loadFailure?.Throw(); // Includes a failure reported by the panel's explicit reload entry.
    }
    internal static IReadOnlyList<ExperimentCommand> All { get { EnsureLoaded(); return Commands; } }
    internal static IReadOnlyList<string> LoadProblems { get { EnsureLoaded(); return Problems; } }
    internal static string DirectoryPath => Path.Combine(DevelopmentPaths.Root, ConfigFolder);

    internal static void Load()
    {
        BeginLoadAttempt();
        Commands.Clear();
        var directory = DirectoryPath;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception e)
        {
            Problems.Add("the command directory could not be created: " + e.Message);
            return;
        }
        TransportRetention.PruneFiles(directory, "drivesession-*.forge", KeptDriveSessionFiles);

        var files = System.IO.Directory.GetFiles(directory, "*.forge", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Problems.Add("no command files in " + directory + "; copy probes/commands there to run an experiment");
            return;
        }
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                Problems.Add(Path.GetFileName(file) + ": " + e.Message);
                continue;
            }
            var parsed = ExperimentParser.Parse(text, Path.GetFileName(file), out var error);
            if (error.Length != 0)
            {
                Problems.Add(error);
                continue;
            }
            if (parsed.Count > ExperimentParser.MaximumCommandsPerFile)
                Problems.Add(Path.GetFileName(file) + ": " + parsed.Count + " commands; the limit is " + ExperimentParser.MaximumCommandsPerFile);
            foreach (var command in parsed.Take(ExperimentParser.MaximumCommandsPerFile))
            {
                if (command.Valid) Commands.Add(command);
                else Problems.Add(command.Source + ": " + string.Join("; ", command.Problems));
            }
        }
    }

    /// <summary>Why an installed command id is not runnable: names the command file and every step problem (steps[n]), never loosens admission.</summary>
    internal static string Refusal(string id, IEnumerable<ExperimentCommand> installed)
    {
        var same = installed.Where(command => command.Id == id).ToArray();
        if (same.Length == 0) return "command '" + id + "' is not installed";
        if (same.Length > 1) return "command '" + id + "' is installed " + same.Length + " times: " + string.Join(", ", same.Select(command => command.Source));
        return same[0].Valid ? "command '" + id + "' is valid but was not admitted"
            : "command '" + id + "' (" + same[0].Source + ") is invalid; a command file may hold many steps but every step is validated; not run: " + string.Join("; ", same[0].Problems);
    }

    /// <summary>First-run seeding from the shipped <c>probes/commands</c> folder, if the config folder is empty.
    /// Returns a sentence describing what happened, never null.</summary>
    internal static string Seed(string sourceDirectory)
    {
        if (!System.IO.Directory.Exists(sourceDirectory)) return "no shipped commands at " + sourceDirectory;
        var directory = DirectoryPath;
        var copied = 0;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach (var file in System.IO.Directory.GetFiles(sourceDirectory, "*.forge", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var target = Path.Combine(directory, name);
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                copied++;
            }
        }
        catch (Exception e)
        {
            return "seeding commands failed: " + e.Message;
        }
        return copied == 0 ? "no new command files to seed" : "seeded " + copied + " command files";
    }
}
