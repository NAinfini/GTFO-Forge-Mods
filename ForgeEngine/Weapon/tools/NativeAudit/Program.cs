using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

// Reads metadata as data. Never loads a game/mod assembly into the CLR or calls GTFO.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AUDIT ERROR: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] known = { "--bepinex", "--game", "--output", "--contract", "--pattern" };
        for (int i = 0; i < args.Length; i += 2)
            if (!known.Contains(args[i]) || i + 1 == args.Length || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Expected unique option/value pairs: " + string.Join(" ", known));
        string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Missing " + key);
        string bep = Path.GetFullPath(Required("--bepinex"));
        string game = Path.GetFullPath(Required("--game"));
        string output = Path.GetFullPath(Required("--output"));
        if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new ArgumentException("Output must be a NEW .json file; existing files are never overwritten.");
        AuditContract? contract = options.TryGetValue("--contract", out var contractPath)
            ? JsonSerializer.Deserialize<AuditContract>(File.ReadAllText(contractPath), Json)
                ?? throw new InvalidDataException("Empty audit contract.") : null;
        if (contract != null) ValidateContract(contract);
        if (contract != null && options.ContainsKey("--pattern"))
            throw new ArgumentException("--pattern is capture-only; verification must use exact contract types.");
        var pattern = new Regex(options.GetValueOrDefault("--pattern", "Weapon|Bullet|Ammo|Backpack|Gear|Melee|Sentry|Mine|Consumable|Syringe|FogRepeller|PlayerAgent|ItemEquippable|ItemInLevel"),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        string steam = Path.Combine(Directory.GetParent(game)!.Parent!.FullName, "appmanifest_493520.acf");
        var build = Regex.Match(File.ReadAllText(steam), "\"buildid\"\\s+\"(?<build>[0-9]+)\"");
        if (!build.Success) throw new InvalidDataException("Steam build ID is missing.");
        string revision = File.ReadAllText(Path.Combine(game, "revision.txt")).Trim('\uFEFF', '\r', '\n', ' ');
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game/GameAssembly.dll"] = Path.Combine(game, "GameAssembly.dll"),
            ["game/global-metadata.dat"] = Path.Combine(game, "GTFO_Data/il2cpp_data/Metadata/global-metadata.dat"),
            ["game/GTFO.exe"] = Path.Combine(game, "GTFO.exe"),
            ["game/revision.txt"] = Path.Combine(game, "revision.txt"),
            ["core/Mono.Cecil.dll"] = Path.Combine(bep, "core/Mono.Cecil.dll")
        };
        string[] assemblies = { "Modules-ASM.dll", "GameData-ASM.dll", "SNet_ASM.dll" };
        foreach (string name in assemblies) paths["interop/" + name] = Path.Combine(bep, "interop", name);
        var files = paths.Select(p => Fingerprint(p.Key, p.Value)).ToArray();
        var requested = contract?.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var types = new List<TypeEvidence>();
        foreach (string name in assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(paths["interop/" + name],
                new ReaderParameters { InMemory = true, ReadSymbols = false });
            foreach (var type in assembly.MainModule.Types.SelectMany(Walk))
            {
                if (requested != null ? !requested.Contains(type.FullName) : !pattern.IsMatch(type.FullName)) continue;
                types.Add(new TypeEvidence(name, type.FullName, type.BaseType?.FullName ?? "",
                    type.Methods.Where(m => !m.IsConstructor).Select(m => m.FullName).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    type.Properties.Select(p => p.PropertyType.FullName + " " + p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    type.Fields.Where(f => !f.Name.StartsWith("Native", StringComparison.Ordinal))
                        .Select(f => f.FieldType.FullName + " " + f.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            }
        }
        // Detect an input changing during capture. Fingerprint equality is evidence, not proof of native semantics.
        foreach (var file in files)
            if (Fingerprint(file.Name, paths[file.Name]) != file)
                throw new IOException("Input changed while auditing: " + file.Name);
        var errors = new List<string>();
        int checks = 0;
        void Check(bool ok, string description) { checks++; if (!ok) errors.Add(description); }
        if (contract != null)
        {
            Check(contract.GameBuild == build.Groups["build"].Value, "game build drift");
            Check(contract.GameRevision == revision, "game revision drift");
            Check(contract.Files.Length == files.Length, "input fingerprint coverage drift");
            foreach (var expected in contract.Files)
                Check(files.Contains(expected), "file hash/size drift: " + expected.Name);
            foreach (var expected in contract.Types)
            {
                var matches = types.Where(t => t.Name == expected.Name && t.Assembly == expected.Assembly).ToArray();
                Check(matches.Length == 1, "missing/ambiguous type: " + expected.Name);
                if (matches.Length != 1) continue;
                var actual = matches[0];
                Check(actual.BaseType == expected.BaseType, "base type drift: " + expected.Name);
                foreach (string method in expected.Methods) Check(actual.Methods.Contains(method), "method drift: " + method);
                foreach (string property in expected.Properties) Check(actual.Properties.Contains(property), "property drift: " + expected.Name + "." + property);
                foreach (string field in expected.Fields) Check(actual.Fields.Contains(field), "field drift: " + expected.Name + "." + field);
            }
        }
        var report = new
        {
            schemaVersion = "1.0.0",
            verification = "metadata-only",
            gameExecuted = false,
            generatedUtc = DateTimeOffset.UtcNow,
            gameBuild = build.Groups["build"].Value,
            gameRevision = revision,
            files,
            types = types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(),
            checks,
            errors,
            status = contract == null ? "captured-not-verified" : errors.Count == 0 ? "metadata-lock-matched" : "metadata-lock-rejected"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, report, Json);
        Console.WriteLine($"W1 METADATA ONLY: types={types.Count}, checks={checks}, errors={errors.Count}; gameExecuted=false");
        foreach (string error in errors) Console.Error.WriteLine(error);
        Console.WriteLine(output);
        return errors.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<TypeDefinition> Walk(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(Walk)) yield return nested;
    }

    // An interop assembly is generated per profile, so it is held to its signature; every other input is one file for everyone.
    private static FileEvidence Fingerprint(string name, string path)
    {
        if (name.StartsWith("interop/", StringComparison.Ordinal))
            return new FileEvidence(name, null, null, ForgeTestInputs.InteropSignature.Sha256(path));
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return new FileEvidence(name, stream.Length, Convert.ToHexString(hash.ComputeHash(stream)));
    }

    private static void ValidateContract(AuditContract c)
    {
        if (c.SchemaVersion != "1.0.0" || c.Verification != "metadata-only" || string.IsNullOrWhiteSpace(c.GameBuild)
            || string.IsNullOrWhiteSpace(c.GameRevision) || c.Files is not { Length: > 0 } || c.Types is not { Length: > 0 })
            throw new InvalidDataException("Incomplete or unsupported metadata contract.");
        if (c.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Name) || !Regex.IsMatch((f.SignatureSha256 ?? (f.Length > 0 ? f.Sha256 : null)) ?? "", "^[0-9A-F]{64}$")
            || f.Name.StartsWith("interop/", StringComparison.Ordinal) != (f.SignatureSha256 != null))
            || c.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != c.Files.Length)
            throw new InvalidDataException("Invalid or duplicated input fingerprint.");
        if (c.Types.Any(t => t == null || string.IsNullOrWhiteSpace(t.Assembly) || string.IsNullOrWhiteSpace(t.Name)
            || t.BaseType == null || t.Methods == null || t.Properties == null || t.Fields == null
            || t.Methods.Concat(t.Properties).Concat(t.Fields).Any(string.IsNullOrWhiteSpace)
            || t.Methods.Length + t.Properties.Length + t.Fields.Length == 0)
            || c.Types.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != c.Types.Length)
            throw new InvalidDataException("Invalid or duplicated type/member requirement.");
    }
}

internal sealed record FileEvidence(string Name, long? Length, string? Sha256, string? SignatureSha256 = null);
internal sealed record TypeEvidence(string Assembly, string Name, string BaseType,
    string[] Methods, string[] Properties, string[] Fields);
internal sealed record AuditContract(string SchemaVersion, string Verification, string GameBuild, string GameRevision,
    FileEvidence[] Files, TypeEvidence[] Types);
