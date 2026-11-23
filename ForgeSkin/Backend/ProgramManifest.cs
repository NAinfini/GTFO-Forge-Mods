using System.Text.Json;

namespace ForgeSkin.Backend;

/// <summary>One BepInEx package a program ships and keeps in step in a profile. `Source`: its folder inside the shipped plugin folder
/// (default the name). `MinVersion`: the lowest installed version the program accepts (reported, `ProfileInfo.Packages`).</summary>
public sealed record PackageSpec(string Name, string Dll, string Team = "NAinfini", string? Source = null, string? MinVersion = null)
{
    public string Folder => Source ?? Name;
}

/// <summary>A plugin the program needs installed by the mod manager (checked and reported, never installed), found by its GUID.</summary>
public sealed record DependencySpec(string Guid, string Name, string Package, string? MinVersion = null);

/// <summary>What one program built on this backend ships and may call: the packages it installs (in install order), the plugins it only
/// checks, and the methods it may use. ForgeSkin's editor runs on <see cref="ForgeSkin"/>; another program gives `--manifest file.json`
/// (and/or `--methods a,b,x.*`).</summary>
public sealed record ProgramManifest
{
    public List<PackageSpec> Packages { get; init; } = [];
    public List<DependencySpec> Dependencies { get; init; } = [];
    /// <summary>The package whose version `ProfileInfo.PluginVersion` reports and that `plugin.uninstall` removes; default the last one.</summary>
    public string? Primary { get; init; }
    /// <summary>Where a checkout stages the shipped plugin folder, relative to the repository root (the folder holding .git).</summary>
    public string PluginRoot { get; init; } = "artifacts/forge-skin-release/publish/plugin";
    /// <summary>Whether the profile also carries ForgeSkin's skins folder and the files older ForgeSkin editors left (moved or cleared at install).</summary>
    public bool SkinProfile { get; init; } = true;
    /// <summary>The methods that may be called: exact names, `prefix.*` or `*`; null allows all.</summary>
    public string[]? Methods { get; init; }

    public PackageSpec PrimaryPackage => Packages.FirstOrDefault(p => p.Name == Primary) ?? Packages[^1];

    public static readonly ProgramManifest ForgeSkin = new()
    {
        Packages = [new("ForgeCore", "ForgeCore.dll"), new("ForgeSkin", "ForgeSkin.Plugin.dll")],
        Primary = "ForgeSkin",
    };

    // Set once at start (Program.Main); tests set it directly.
    public static ProgramManifest Current { get; set; } = ForgeSkin;

    public bool Allows(string method) =>
        Methods == null || Methods.Any(m => m == "*" || m == method || (m.EndsWith(".*") && method.StartsWith(m[..^1], StringComparison.Ordinal)));

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static ProgramManifest Load(string file)
    {
        ProgramManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ProgramManifest>(File.ReadAllText(file), Options); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new BackendException("bad-manifest", $"{file}: {e.Message}");
        }
        if (manifest == null || manifest.Packages.Count == 0 || manifest.Packages.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Dll)))
            throw new BackendException("bad-manifest", $"{file}: Packages must list at least one package with a name and a dll");
        if (manifest.Primary != null && manifest.Packages.All(p => p.Name != manifest.Primary))
            throw new BackendException("bad-manifest", $"{file}: Primary {manifest.Primary} is not among Packages");
        return manifest;
    }
}
