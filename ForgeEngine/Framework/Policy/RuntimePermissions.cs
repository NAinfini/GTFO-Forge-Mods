using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Shared reviewed permissions or an extension's exact provider namespace. The checked-in official release manifest is the existing
/// reviewed source; runtime registrations cannot add names to this set.</summary>
internal static class RuntimePermissions
{
    private static readonly Lazy<HashSet<string>> Reviewed = new(Load);

    internal static bool IsAllowed(string providerId, string permission)
        => Reviewed.Value.Contains(permission) || permission.StartsWith(providerId + ".", StringComparison.Ordinal);

    private static HashSet<string> Load()
    {
        var content = ReadResource("ForgeEngine.Framework.ReviewedRuntimeManifest.json");
        var hashes = ReadResource("ForgeEngine.Framework.ReviewedReleaseHashes.json");
        return ParseReviewed(content, hashes);
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(RuntimePermissions).Assembly.GetManifestResourceStream(name)
            ?? throw new RuntimeContractException("permissions-catalog-missing", "The reviewed permission resource is not embedded: " + name);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    // Pure parsing is separate from the assembly-owned trust boundary. Parsing provider data does not change Reviewed.
    internal static HashSet<string> ParseReviewed(byte[] content, byte[] hashes)
    {
        using var index = JsonDocument.Parse(hashes);
        RuntimeJson.Require(ForgeCore.FormatVersion.Matches(index.RootElement),
            "permissions-catalog", "Unsupported release hash index.");
        string? expected = null;
        foreach (var row in index.RootElement.GetProperty("files").EnumerateArray())
            if (RuntimeJson.Text(row, "path") == "runtime-manifest.json")
            {
                RuntimeJson.Require(expected is null, "permissions-catalog", "Duplicate runtime manifest hash.");
                expected = RuntimeJson.Text(row, "sha256");
            }
        var actual = ForgeCore.Hash.Sha256Hex(content);
        RuntimeJson.Require(expected == actual, "permissions-catalog", "Reviewed runtime manifest hash mismatch.");
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        RuntimeJson.Require(ForgeCore.FormatVersion.Matches(root),
            "permissions-catalog", "Unsupported reviewed runtime manifest.");
        RuntimeJson.Require(RuntimeJson.Text(root.GetProperty("runtime"), "apiVersion") == RuntimeKernel.ApiVersion,
            "permissions-catalog", "Reviewed runtime API version mismatch.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in root.GetProperty("bindingSupport").EnumerateArray())
            foreach (var row in binding.GetProperty("requiredPermissions").EnumerateArray())
            {
                var id = RuntimeJson.Text(row);
                RuntimeJson.Require(RuntimeJson.IsId(id), "permissions-catalog", "Invalid reviewed permission.");
                result.Add(id);
            }
        return result;
    }
}
