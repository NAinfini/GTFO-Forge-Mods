using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ForgeCore.Tests;

/// <summary>Every game method ForgeCore patches exists in the interop assemblies (read as metadata, nothing is loaded). Needs the
/// BepInEx profile named by GTFOBepInExPath; without it the check says so and is not counted as a pass.</summary>
internal static class PatchTargetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var dir = typeof(PatchTargetChecks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Interop")?.Value;
        if (dir == null || !Directory.Exists(dir)) { Console.WriteLine("patch targets: needs local resources (interop assemblies), not run"); return; }
        var methods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (type.IsNested) continue;
                var ns = reader.GetString(type.Namespace);
                var name = (ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name);
                if (!methods.TryGetValue(name, out var set)) methods[name] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in type.GetMethods()) set.Add(reader.GetString(reader.GetMethodDefinition(method).Name));
            }
        }
        foreach (var (module, targets) in Modules.Patched)
            foreach (var target in targets)
                check(methods.TryGetValue(target.Type, out var found) && found.Contains(target.Method),
                    "patch target " + module + ": " + target.Type + "." + target.Method + " is in the interop assemblies");
    }
}
