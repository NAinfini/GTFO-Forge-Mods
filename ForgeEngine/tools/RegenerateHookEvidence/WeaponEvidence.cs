using Mono.Cecil;
using System.Text.Json.Nodes;

namespace ForgeEngine.HookEvidence.Weapon;

internal static class WeaponEvidence
{
    // Rewrites the `hooks` table of evidence/w1-native-hooks.json from the Weapon domain of the compiled ForgeEngine.dll and the game's
    // interop assemblies: one row per Harmony callback class the plugin declares, with the target the interop assembly really
    // declares and its RVA read from dump.cs. Every other top-level table of the spec is kept as it is. A row keeps the
    // `readback` prose it already carries; a new row carries none, because that reading is a human review of the binary.
    internal static void Run(string bepInEx, string artifacts, string specPath, string dumpPath)
        => HookRows.WithAssemblies(bepInEx, artifacts, Directory.GetFiles(Path.Combine(bepInEx, "interop"), "*.dll"), (engine, framework, interop) =>
        {
            var gameTypes = HookRows.GameTypes(interop);
            var spec = JsonNode.Parse(File.ReadAllText(specPath))!.AsObject();
            var previous = (spec["hooks"]?.AsArray() ?? new JsonArray()).OfType<JsonObject>()
                .ToDictionary(row => (string)row["hook"]!, StringComparer.Ordinal);
            var dump = Dump.Open(dumpPath, (string?)spec["dumpSha256"] ?? "", Console.WriteLine);
            var rows = new JsonArray();
            var unresolved = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hook in ForgeTestInputs.DomainTypes.Native(engine, framework, "Weapon")
                .Where(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!names.Add(hook.Name)) throw new InvalidOperationException("duplicate hook class name " + hook.Name);
                var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
                var owner = patch.ConstructorArguments[0].Value as TypeReference
                    ?? throw new InvalidOperationException("HarmonyPatch on " + hook.Name + " names no type");
                var name = patch.ConstructorArguments.Count > 1 ? patch.ConstructorArguments[1].Value as string : null;
                var ownerType = gameTypes.FirstOrDefault(t => t.FullName == owner.FullName);
                var target = name == null || ownerType == null ? null : HookRows.Target(gameTypes, ownerType, patch, name);
                if (target == null)
                {
                    unresolved.Add(hook.Name + " -> " + owner.FullName + "::" + name);
                    continue;
                }
                var row = new JsonObject
                {
                    ["hook"] = hook.Name,
                    ["assembly"] = target.Module.Assembly.Name.Name + ".dll",
                    ["type"] = target.DeclaringType.FullName,
                    ["name"] = target.Name,
                    ["signature"] = target.FullName,
                    ["isVirtual"] = target.IsVirtual,
                    ["isStatic"] = target.IsStatic,
                    ["rva"] = dump?.Rva(target) ?? (previous.TryGetValue(hook.Name, out var known) ? (string?)known["rva"] : null)
                };
                // Reviewed fields of a row survive a regeneration: whatever the row already carries that this tool does not
                // compute (readback, detour prologue, dump line, RVA sharing) is copied back. The computed keys above win.
                if (previous.TryGetValue(hook.Name, out var kept))
                    foreach (var (key, value) in kept)
                        if (!row.ContainsKey(key) && value != null) row[key] = value.DeepClone();
                rows.Add(row);
            }
            spec["hooks"] = rows;
            HookRows.Write(spec, specPath);
            Console.WriteLine("Regenerated " + rows.Count + " hook rows: " + specPath);
            if (unresolved.Count != 0)
                Console.WriteLine("unresolved hooks (no interop method): " + string.Join(" | ", unresolved));
        });
}
