using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>Only exchanges explicitly hashed, author-owned fixture JSON; never reloads game data.</summary>
internal static class AuthoringFileSwap
{
    private const int MaximumPayload = 32 * 1024 * 1024;
    // Receipts hold the bytes a swap displaced; keep the latest few and a week, never the last hour.
    private const int KeptReceipts = 8;
    private static readonly object Gate = new();

    internal static string Swap(string bepInExRoot, string caseName, string variant)
    {
        // A development command has no throughput requirement; serialize this process's swaps.
        lock (Gate) return SwapLocked(bepInExRoot, caseName, variant);
    }

    private static string SwapLocked(string bepInExRoot, string caseName, string variant)
    {
        if (variant is not ("a" or "b")) throw new ArgumentException("variant must be a or b");
        Segment(caseName);
        var root = Path.GetFullPath(bepInExRoot);
        Physical(root);
        var input = Path.Combine(root, "ForgeDevelopment", "drive-inputs", caseName);
        Physical(input);
        var manifestPath = Path.Combine(input, "swap.json");
        using var manifest = JsonDocument.Parse(Read(manifestPath, 16 * 1024));
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (manifest.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("manifest must be an object");
        foreach (var property in manifest.RootElement.EnumerateObject())
        {
            if (property.Name is not ("format" or "schemaVersion" or "package" or "target" or "a" or "b" or "sha256A" or "sha256B") ||
                !fields.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("unknown or duplicate manifest field: " + property.Name);
        }
        string Text(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("missing or non-string field: " + name);
        if (fields.Count != 8 || Text("format") != "forge.development-file-swap" ||
            fields["schemaVersion"].ValueKind != JsonValueKind.String || fields["schemaVersion"].GetString() != ForgeCore.FormatVersion.Current)
            throw new InvalidDataException("unsupported file-swap manifest");
        var package = Text("package");
        Segment(package);
        foreach (var word in package.Split('-', '_', '.'))
            if (word.StartsWith("Forge", StringComparison.OrdinalIgnoreCase) || word.StartsWith("MTFO", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("base and shared-data packages cannot be swapped");
        var targetRelative = Text("target");
        Relative(targetRelative);
        var plan = targetRelative.StartsWith("forge/plans/", StringComparison.Ordinal) && targetRelative.EndsWith(".plan.forge", StringComparison.Ordinal);
        var fileName = Path.GetFileName(targetRelative);
        // MTFO reads a package's data tables from BepInEx/GameData/<package>; a data table target is relative to that folder.
        var dataBlock = fileName.StartsWith("GameData_", StringComparison.Ordinal) && fileName.EndsWith("DataBlock_bin.json", StringComparison.Ordinal);
        if (!plan && !dataBlock) throw new InvalidDataException("only owned forge/plans/*.plan.forge or GameData_*DataBlock_bin.json under the package's GameData folder may be swapped");
        var target = plan
            ? Path.Combine(root, "plugins", package, targetRelative.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(root, "GameData", package, targetRelative.Replace('/', Path.DirectorySeparatorChar));
        Physical(target);
        var nameA = Text("a");
        var nameB = Text("b");
        Segment(nameA);
        Segment(nameB);
        if (nameA.Equals(nameB, StringComparison.OrdinalIgnoreCase) || nameA.Equals("swap.json", StringComparison.OrdinalIgnoreCase) ||
            nameB.Equals("swap.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("fixture input names must be distinct from each other and the manifest");
        var hashA = Text("sha256A");
        var hashB = Text("sha256B");
        HashText(hashA);
        HashText(hashB);
        var a = Read(Path.Combine(input, nameA), MaximumPayload);
        var b = Read(Path.Combine(input, nameB), MaximumPayload);
        RequireHash(a, hashA);
        RequireHash(b, hashB);
        using var jsonA = JsonDocument.Parse(a);
        using var jsonB = JsonDocument.Parse(b);
        var desired = variant == "a" ? a : b;
        var fromHash = variant == "a" ? hashB : hashA;
        var toHash = variant == "a" ? hashA : hashB;

        // Allow atomic replacement but deny ordinary concurrent writers while validating this file.
        using var current = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var original = ReadStream(current, MaximumPayload);
        var actualHash = ForgeCore.Hash.Sha256Hex(original);
        if (actualHash != fromHash && actualHash != toHash) throw new InvalidDataException("current file hash changed; refusing swap");
        Envelope(jsonA.RootElement, plan);
        Envelope(jsonB.RootElement, plan);
        if (plan)
        {
            // Only execution contents may differ. All other top-level values, including
            // identity, permission/provider pins and unknown future locks, stay frozen.
            var locksA = jsonA.RootElement.EnumerateObject().Where(property => property.Name is not ("entrypoints" or "variables")).ToDictionary(property => property.Name, property => property.Value);
            var locksB = jsonB.RootElement.EnumerateObject().Where(property => property.Name is not ("entrypoints" or "variables")).ToDictionary(property => property.Name, property => property.Value);
            if (locksA.Count != locksB.Count || locksA.Any(pair => !locksB.TryGetValue(pair.Key, out var value) || !Same(pair.Value, value)))
                throw new InvalidDataException("plan identity, permissions or locked envelope changed");
        }
        if (actualHash == toHash)
        {
            RequireHash(Read(target, MaximumPayload), toHash);
            return Result("already-current", package, targetRelative, actualHash, toHash, "none");
        }

        var receipts = Path.Combine(input, "receipts");
        TransportRetention.PruneDirectories(receipts, KeptReceipts);
        CreatePhysicalDirectory(receipts);
        var receipt = Path.Combine(receipts, Guid.NewGuid().ToString("N"));
        CreatePhysicalDirectory(receipt);
        var saved = Path.Combine(receipt, "original");
        WriteNew(saved, original);
        RequireHash(Read(saved, MaximumPayload), fromHash);
        var displaced = Path.Combine(receipt, "at-replacement-original");
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".forge-swap-" + Guid.NewGuid().ToString("N") + ".tmp");
        var replaced = false;
        try
        {
            Physical(target);
            Physical(receipt);
            WriteNew(temporary, desired);
            RequireHash(Read(temporary, MaximumPayload), toHash);
            RequireHash(Read(target, MaximumPayload), fromHash);
            // Windows ReplaceFile requires the validation handle closed; its displaced-file
            // backup below verifies the actual version that was replaced across that boundary.
            current.Dispose();
            // Content is the contract; optional Windows ACL/metadata merging must not
            // turn a permitted fixture exchange into an access-denied failure.
            File.Replace(temporary, target, displaced, ignoreMetadataErrors: true);
            replaced = true;
            // The replacement's backup detects a rename/replacement between the last check and commit.
            RequireHash(Read(displaced, MaximumPayload), fromHash);
            RequireHash(Read(target, MaximumPayload), toHash);
            return Result("swapped", package, targetRelative, fromHash, toHash, receipt);
        }
        catch (Exception failure)
        {
            if (replaced || File.Exists(displaced))
            {
                try
                {
                    Physical(receipt);
                    // Never overwrite another writer's later version during recovery.
                    RequireHash(Read(target, MaximumPayload), toHash);
                    var rollback = Read(displaced, MaximumPayload);
                    var recovery = Path.Combine(Path.GetDirectoryName(target)!, ".forge-recover-" + Guid.NewGuid().ToString("N") + ".tmp");
                    WriteNew(recovery, rollback);
                    File.Replace(recovery, target, Path.Combine(receipt, "recovery-displaced"), ignoreMetadataErrors: true);
                    RequireHash(Read(target, MaximumPayload), ForgeCore.Hash.Sha256Hex(rollback));
                }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException("swap failed; recovery failed or unsafe; preserved bytes at " + receipt + " and fixture inputs", failure, recoveryFailure);
                }
                throw new IOException("swap failed; original at replacement restored; receipt=" + receipt, failure);
            }
            throw new IOException("swap refused before replacement; receipt=" + receipt, failure);
        }
        // Receipt and staging bytes intentionally survive failures for inspection; inputs are never deleted.
    }

    private static string Result(string status, string package, string target, string from, string to, string receipt) =>
        "status=" + status + ";package=" + package + ";target=" + target + ";from=" + from + ";to=" + to + ";receipt=" + receipt;

    // Envelope/lock checks only; runtime binding and execution admission remain in ForgeEngine.
    private static void Envelope(JsonElement root, bool plan)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("payload must be an object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("duplicate payload field");
        if (root.TryGetProperty("schemaVersion", out var version))
        {
            if (version.ValueKind != JsonValueKind.String || version.GetString() != ForgeCore.FormatVersion.Current)
                throw new InvalidDataException("unsupported payload schemaVersion");
        }
        else if (plan) throw new InvalidDataException("plan schemaVersion is required");
        if (!plan)
        {
            if (!root.TryGetProperty("Blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("DataBlock payload requires Blocks array");
            return;
        }
        if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "forge-engine-plan")
            throw new InvalidDataException("unsupported plan kind");
        foreach (var key in new[] { "planId", "domain", "layoutHash" })
            if (!root.TryGetProperty(key, out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(text.GetString()))
                throw new InvalidDataException("missing plan envelope field: " + key);
        foreach (var key in new[] { "resource", "runtime" })
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("missing plan object: " + key);
        foreach (var key in new[] { "permissions", "dependencies" })
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("missing plan lock array: " + key);
    }

    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            var b = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && Same(pair.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            var a = left.EnumerateArray().ToArray();
            var b = right.EnumerateArray().ToArray();
            return a.Length == b.Length && a.Where((value, index) => !Same(value, b[index])).Any() == false;
        }
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }

    private static void Segment(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.EndsWith('.') || value.Length > 180)
            throw new InvalidDataException("non-canonical path segment");
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
                throw new InvalidDataException("non-canonical path segment");
        var stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
            throw new InvalidDataException("reserved path segment");
    }

    private static void Relative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || Path.IsPathRooted(value) || value.Contains('\\'))
            throw new InvalidDataException("target must be a canonical slash-separated relative path");
        foreach (var segment in value.Split('/')) Segment(segment);
    }

    private static void Physical(string path)
    {
        var full = Path.GetFullPath(path);
        var anchor = Path.GetPathRoot(full)!;
        // Inspect ancestors outside BepInEx too: a linked profile parent is not a physical root.
        if ((File.GetAttributes(anchor) & FileAttributes.ReparsePoint) != 0) throw new IOException("linked path root");
        var walk = anchor;
        foreach (var segment in full.Substring(anchor.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            walk = Path.Combine(walk, segment);
            if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException("linked or reparse path: " + walk);
        }
    }

    private static void CreatePhysicalDirectory(string path)
    {
        Physical(Path.GetDirectoryName(path)!);
        RejectLinkedPath(path, "linked or reparse directory: " + path);
        Directory.CreateDirectory(path);
        Physical(path);
    }

    /// <summary>Throws when <paramref name="path"/> exists as a link or reparse point; a path that does not exist yet is the normal case.</summary>
    internal static void RejectLinkedPath(string path, string message)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(message);
        }
        catch (FileNotFoundException) { } // Probe: a path that does not exist yet is the normal case; any other failure still throws.
        catch (DirectoryNotFoundException) { } // Probe: a path that does not exist yet is the normal case; any other failure still throws.
    }

    private static byte[] Read(string path, int maximum)
    {
        Physical(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadStream(stream, maximum);
    }

    private static byte[] ReadStream(FileStream stream, int maximum)
    {
        if (stream.Length > maximum) throw new InvalidDataException("file exceeds byte budget");
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new IOException("file changed while reading");
            offset += count;
        }
        if (stream.ReadByte() != -1) throw new IOException("file grew while reading");
        return bytes;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        Physical(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static void RequireHash(byte[] bytes, string expected)
    {
        if (ForgeCore.Hash.Sha256Hex(bytes) != expected) throw new InvalidDataException("SHA-256 mismatch");
    }
    private static void HashText(string value)
    {
        if (value.Length != 64) throw new InvalidDataException("SHA-256 must be 64 lowercase hex digits");
        foreach (var c in value)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("SHA-256 must be 64 lowercase hex digits");
    }
}
