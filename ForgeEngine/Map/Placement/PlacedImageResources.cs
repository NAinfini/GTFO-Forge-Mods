using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>One PNG frozen during package admission. Its key is the hash of these exact exported bytes.</summary>
internal sealed record PlacedImageResource(string PngSha256, byte[] Bytes, int Width, int Height, string Path);

/// <summary>The admitted PNG payloads image boards may decode. Disk is never reopened during a level.</summary>
internal sealed class PlacedImageTable
{
    private readonly IReadOnlyDictionary<string, PlacedImageResource> _images;

    private PlacedImageTable(IReadOnlyDictionary<string, PlacedImageResource> images) => _images = images;

    internal static PlacedImageTable Empty { get; } = new(
        new ReadOnlyDictionary<string, PlacedImageResource>(
            new Dictionary<string, PlacedImageResource>(StringComparer.Ordinal)));

    internal PlacedImageResource? Find(string pngSha256)
        => _images.TryGetValue(pngSha256, out var image) ? image : null;

    internal IEnumerable<PlacedImageResource> All() => _images.Values;

    internal static PlacedImageTable FromPrepared(IEnumerable<PlacedImageResource> resources)
    {
        var images = new Dictionary<string, PlacedImageResource>(StringComparer.Ordinal);
        foreach (var group in resources.GroupBy(resource => resource.PngSha256, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(resource => !resource.Bytes.AsSpan().SequenceEqual(first.Bytes)))
                throw new InvalidOperationException("Prepared image hash collision: " + first.PngSha256 + ".");
            images.Add(first.PngSha256, first);
        }
        return new(new ReadOnlyDictionary<string, PlacedImageResource>(images));
    }
}

/// <summary>Reads exported PNG files through the package discovery path and verifies their filename hash and IHDR.</summary>
internal static class PlacedImageResources
{
    internal const int MaximumTextureDimension = 16384;

    internal static bool TryRead(PublishedResource document, out PlacedImageResource? image,
        out PublishedResourceFailure failure)
    {
        image = null;
        if (!PublishedResources.TryReadHashed(document, ".png", MapPackageDocuments.Codes, "placed-image-file-name",
                "An image filename must be its 64-character lower-case PNG SHA-256 followed by .png.", "placed-image-file-size",
                "The image file is empty.", "placed-image-png-sha256", "The exported PNG bytes do not match the SHA-256 in the filename.",
                out string revision, out byte[]? bytes, out failure))
            return false;
        if (!ForgeCore.Png.TrySize(bytes!, out int width, out int height))
            return Refuse(document, "placed-image-png", "The file has no valid PNG signature and IHDR dimensions.", out failure);
        if (width > MaximumTextureDimension || height > MaximumTextureDimension)
            return Refuse(document, "placed-image-dimensions-unsupported", "The image is " + width + "x" + height
                + "; a side may be at most " + MaximumTextureDimension + " pixels.", out failure);
        image = new(revision, bytes!, width, height, document.Relative);
        failure = default;
        return true;
    }

    private static bool Refuse(PublishedResource document, string code, string reason,
        out PublishedResourceFailure failure)
    {
        failure = new(document.Relative, code, reason);
        return false;
    }
}
