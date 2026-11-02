using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>The settings page patches (SettingsPages.cs, SliderRows.cs, KeyBindSettings.cs). From the source: the mod never gives a game scroll window a list itself (the game's native
/// SetContentItems destroys every child of the window's content container first, which emptied the key page), it patches no method of
/// the game's key field and none is OnEnable. From the game's interop assemblies (GTFOBepInExPath; without them reported as not run):
/// every target is declared by its own class and every parameter a patch method takes by name exists on the target.</summary>
internal static class PatchTargetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var profile = Environment.GetEnvironmentVariable("GTFOBepInExPath");
        var source = Find(new DirectoryInfo(AppContext.BaseDirectory));
        if (source == null) { check(false, "the settings page sources found for the patch target check"); return; }
        var text = string.Concat(new[] { "SettingsPages.cs", "SliderRows.cs", "KeyBindSettings.cs" }.Select(name => File.ReadAllText(Path.Combine(source, name))));
        var patches = Regex.Matches(text, @"\[HarmonyPatch\(typeof\((CM_\w+)\), nameof\(\1\.(\w+)\)\)\](?<body>(?:(?!\[HarmonyPatch).)*)", RegexOptions.Singleline)
            .Select(m => (Type: m.Groups[1].Value, Method: m.Groups[2].Value,
                Parameters: Regex.Matches(m.Groups["body"].Value, @"private static \w+ (?:Prefix|Postfix)\(([^)]*)\)")
                    .SelectMany(p => p.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(p => p.Trim().Split(' ').Last()).Where(p => !p.StartsWith("__")).Distinct().ToList()))
            .ToList();
        check(patches.Count >= 5 && patches.All(p => p.Parameters != null), "the patch target check reads the settings page patches");
        // Nothing is patched at startup: every patch class names the feature that puts it on at first use.
        var unplaced = Directory.GetFiles(source, "*.cs").Where(f =>
        {
            var code = File.ReadAllText(f);
            return Regex.Matches(code, @"\[HarmonyPatch").Count != Regex.Matches(code, @"\[PatchGroup\(").Count;
        }).Select(Path.GetFileName).ToList();
        check(unplaced.Count == 0, "every Harmony patch class is in a patch group" + (unplaced.Count == 0 ? "" : ": " + string.Join(", ", unplaced)));
        check(!File.ReadAllText(Path.Combine(source, "Plugin.cs")).Contains("PatchAll"), "the plugin patches nothing at startup");
        check(!Regex.IsMatch(text, @"\.SetContentItems\("), "the settings rows never call a scroll window's SetContentItems themselves");
        check(patches.Any(p => p.Type == "CM_ScrollWindow" && p.Method == "SetContentItems"), "the mods' rows join the game's own SetContentItems call");
        check(patches.Any(p => p.Type == "CM_PageSettings" && p.Method == "CreateSubmenu") && patches.Any(p => p.Type == "CM_PageSettings" && p.Method == "CreateKeyMappingSubMenu"), "both page builds are marked");
        check(!patches.Any(p => p.Type == "CM_SettingKeyBindingField"), "no method of the game's key field is patched");
        check(!patches.Any(p => p.Method == "OnEnable"), "no settings patch hooks OnEnable");
        var assemblyPath = profile == null ? null : Path.Combine(profile, "interop", "Modules-ASM.dll");
        if (assemblyPath == null || !File.Exists(assemblyPath)) { Console.WriteLine("needs local resources, not run: patch target declaring types (set GTFOBepInExPath)"); return; }
        var folders = new[] { "interop", "core", "unity-libs" }.Select(f => Path.Combine(profile!, f)).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = e.Name.Split(',')[0] + ".dll";
            foreach (var folder in folders)
                if (File.Exists(Path.Combine(folder, name))) return Assembly.LoadFrom(Path.Combine(folder, name));
            return null;
        };
        var assembly = Assembly.LoadFrom(assemblyPath);
        foreach (var (typeName, method, parameters) in patches)
        {
            var type = assembly.GetType("CellMenu." + typeName);
            var found = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == method).ToList();
            check(found is { Count: 1 } && found[0].DeclaringType == type, typeName + "." + method + " is declared once by " + typeName);
            if (found is not { Count: 1 }) continue;
            var names = found[0].GetParameters().Select(p => p.Name).ToHashSet();
            foreach (var parameter in parameters)
                check(names.Contains(parameter), typeName + "." + method + " has the parameter " + parameter + " its patch reads");
        }
    }

    /// <summary>The Native folder of ForgeUI.</summary>
    private static string? Find(DirectoryInfo? from)
    {
        for (; from != null; from = from.Parent)
        {
            var path = Path.Combine(from.FullName, "Native");
            if (File.Exists(Path.Combine(path, "SettingsPages.cs"))) return path;
            path = Path.Combine(from.FullName, "Mods", "ForgeUI", "Native");
            if (File.Exists(Path.Combine(path, "SettingsPages.cs"))) return path;
        }
        return null;
    }
}
