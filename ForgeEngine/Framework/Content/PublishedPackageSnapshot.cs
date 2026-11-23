using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeEngine.Framework;

/// <summary>One file's exact admitted bytes and digest. Relative paths begin with <c>forge/</c>.</summary>
internal sealed record PublishedPackageFile(string Relative, long Length, string Sha256, byte[] Bytes);

/// <summary>A bounded cold snapshot of every file beneath one installed package's forge directory. Failure
/// withdraws the whole snapshot; no caller can mistake a partial file set for the package's identity.</summary>
internal sealed class PublishedPackageSnapshot
{
    /// <summary>The complete package shares the published-document byte limit, including binary payloads.</summary>
    public const int MaximumBytes = PublishedResources.MaximumFileBytes;
    /// <summary>Files and directories count together, including empty directories.</summary>
    public const int MaximumEntries = 4096;
    /// <summary>The forge directory is depth zero.</summary>
    public const int MaximumDepth = 32;

    private static readonly PublishedBytesCodes Codes = new("package-path", "package-unreadable", "package-file-size");

    public IReadOnlyList<PublishedPackageFile> Files { get; }
    public string ContentHash { get; }

    private PublishedPackageSnapshot(List<PublishedPackageFile> files)
    {
        Files = files.AsReadOnly();
        ContentHash = ForgeCore.Hash.Sha256Hex(string.Concat(files.Select(file => file.Relative + "\n" + file.Sha256 + "\n")));
    }

    /// <summary>Captures only <c>plugins/&lt;pluginDirectory&gt;/forge/</c>. The plugin argument is exactly one
    /// directory name, never a path. Every refusal names a BepInEx-relative path and a package-* code. Directory
    /// membership is checked again after reading, so a changed set cannot produce an apparently complete hash.</summary>
    public static bool TryCapture(string bepInExRoot, string pluginDirectory,
        Action<PublishedResourceFailure> rejected, out PublishedPackageSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        snapshot = null;
        var current = "plugins";
        try
        {
            if (pluginDirectory.IndexOf('/') >= 0 || !ForgeCore.PosixPath.IsRelativeFile(pluginDirectory))
                throw new InvalidDataException("The package must be exactly one directory directly beneath plugins.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot));
            var package = "plugins/" + pluginDirectory + "/";
            current = package + "forge";
            PublishedResources.ResolveWithinRoot(root, current);
            using var rootIdentity = PublishedFileIdentity.OpenRoot(root);
            var budget = new PublishedReadBudget { RootIdentity = rootIdentity, RootPath = root };
            var before = Enumerate(root, package, ref current);
            var files = new List<PublishedPackageFile>();
            foreach (var entry in before)
            {
                if (entry.Directory) continue;
                current = package + entry.Relative;
                var document = new PublishedResource(current, PublishedResources.ResolveWithinRoot(root, current),
                    entry.Length, root)
                { ReadBudget = budget };
                if (!PublishedResources.TryReadBytes(document, MaximumBytes, Codes, rejected, out var bytes)) return false;
                files.Add(new(entry.Relative, bytes!.LongLength, ForgeCore.Hash.Sha256Hex(bytes), bytes));
            }
            var after = Enumerate(root, package, ref current);
            if (!before.SequenceEqual(after))
            {
                current = package + "forge";
                throw new InvalidDataException("The package directory members or file sizes changed while its bytes were read.");
            }
            PublishedFileIdentity.VerifyRoot(rootIdentity, root);
            snapshot = new(files);
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            rejected(new(current, (error is InvalidDataException or ArgumentException or NotSupportedException
                ? Codes.PathCode : Codes.UnreadableCode), error.Message));
            return false;
        }
    }

    private readonly record struct Entry(string Relative, bool Directory, long Length);

    private static List<Entry> Enumerate(string root, string package, ref string current)
    {
        var entries = new List<Entry>();
        var pending = new Stack<(string Relative, int Depth)>();
        pending.Push(("forge", 0));
        long bytes = 0;
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            current = package + directory.Relative;
            var path = PublishedResources.ResolveWithinRoot(root, current);
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                var relative = directory.Relative + "/" + entry.Name;
                current = package + relative;
                // Inspect the entry before recursing: never ask an enumeration API to follow a link.
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package paths cannot traverse links or junctions.");
                PublishedResources.ResolveWithinRoot(root, current);
                if (entries.Count == MaximumEntries)
                    throw new InvalidDataException("The package exceeds the " + MaximumEntries + " file/directory entry budget.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var length = isDirectory ? 0 : ((FileInfo)entry).Length;
                if (length > MaximumBytes || (bytes += length) > MaximumBytes)
                    throw new InvalidDataException("The package exceeds the " + MaximumBytes + " byte budget.");
                entries.Add(new(relative, isDirectory, length));
                if (!isDirectory) continue;
                if (directory.Depth == MaximumDepth)
                    throw new InvalidDataException("The package exceeds the " + MaximumDepth + " directory depth budget.");
                pending.Push((relative, directory.Depth + 1));
            }
        }
        entries.Sort((left, right) => string.CompareOrdinal(left.Relative, right.Relative));
        return entries;
    }
}
