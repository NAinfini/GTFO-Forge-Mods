using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeVisual;

namespace ForgeEngine.GameBindings;

/// <summary>Everything one imported model needs, already read and checked by its package's reader: the descriptor and the bytes it names
/// (the PNGs in the descriptor's texture order). ForgeVisual builds the model from exactly these.</summary>
internal sealed record ImportedModelPackage(NativeModelResourceReference Resource, ImportedModelDescriptor Descriptor,
    byte[] Mesh, IReadOnlyList<byte[]> Textures);

/// <summary>
/// The package side of an imported model (a player's uploaded glb or a geomorph's own model, Docs/forge-contract FORGE-FRAMEWORK §8): the
/// `model` runtime resource that holds the descriptor and the mesh sidecar. Both ride the existing resource channel
/// (`forge/resources/&lt;revision&gt;.resource.forge` and `&lt;sha256&gt;.&lt;ext&gt;`), the textures are the `&lt;sha256&gt;.png` files of the same
/// folder that image boards already use. Each file is found by its hash (I-RESOLVE) and proved to be what its name says; this class checks the descriptor; which model a level draws and how
/// much of it fits is decided by <see cref="NativeModelRuntime"/> and <see cref="ImportedModelPlacement"/>.
/// </summary>
internal static class ImportedModelFiles
{
    public const string ResourceKind = "model";
    public const string MeshExtension = ".fmesh";
    public const string FormatCode = "imported-model-document", ReferenceCode = "imported-model-reference",
        RevisionCode = "imported-model-revision";

    /// <summary>A `model` resource document as its locked reference and descriptor, or why it is none. The revision is the content hash a
    /// placement's reference was written against, so it is recomputed from the payload and must match; the descriptor's own rules are
    /// ForgeVisual's.</summary>
    public static bool TryReadDescriptor(string text, out NativeModelResourceReference? reference,
        out ImportedModelDescriptor? descriptor, out string? code, out string? reason)
    {
        reference = null; descriptor = null;
        if (!PublishedResources.TryResourceKind(text, AreaFieldDocument.Format, FormatCode, FormatCode, out var kind, out code, out reason))
            return false;
        if (kind != ResourceKind)
        {
            code = FormatCode; reason = "The document declares kind " + kind + ", not " + ResourceKind + ".";
            return false;
        }
        using var parsed = JsonDocument.Parse(text);
        var root = parsed.RootElement;
        if (!ForgeCore.FormatVersion.Matches(root))
        {
            code = FormatCode; reason = "Unsupported runtime resource schemaVersion.";
            return false;
        }
        if (!root.TryGetProperty("reference", out var referenceValue) || referenceValue.ValueKind != JsonValueKind.Object
            || !referenceValue.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !referenceValue.TryGetProperty("revision", out var revisionValue) || revisionValue.ValueKind != JsonValueKind.String)
        {
            code = ReferenceCode; reason = "A runtime resource carries an {id, revision} reference.";
            return false;
        }
        string id = idValue.GetString()!, revision = revisionValue.GetString()!;
        if (!id.StartsWith(NativeModelContent.ImportedIdPrefix, StringComparison.Ordinal) || !PublishedResources.IsSha256(revision))
        {
            code = ReferenceCode; reason = "An imported model is {id: imported:<uuid>, revision: <lower-case SHA-256>}.";
            return false;
        }
        if (!root.TryGetProperty("payload", out var payload))
        {
            code = FormatCode; reason = "The model document carries no descriptor.";
            return false;
        }
        var semantic = "{\"format\":\"" + AreaFieldDocument.Format + "\",\"id\":" + JsonSerializer.Serialize(id)
            + ",\"kind\":\"" + ResourceKind + "\",\"payload\":" + RuntimeJson.Canonical(payload) + ",\"schemaVersion\":\"" + ForgeCore.FormatVersion.Current + "\"}";
        if (ForgeCore.Hash.Sha256Hex(semantic) != revision)
        {
            code = RevisionCode; reason = "The descriptor differs from the revision " + revision + " it was locked at.";
            return false;
        }
        descriptor = ImportedModelDescriptor.Parse(payload.GetRawText(), out var failure);
        if (descriptor == null)
        {
            code = failure.Code; reason = failure.Detail;
            return false;
        }
        reference = new NativeModelResourceReference(id, revision);
        code = null; reason = null;
        return true;
    }

    private static readonly PublishedTextCodes EntryCodes = new(FormatCode, FormatCode, FormatCode, FormatCode, FormatCode);

    /// <summary>The imported models one content entry uses, for <c>NativeModelRuntime.Prepare</c>: every domain's content registration
    /// (weapon, enemy) reads them here. Each file is found by its hash through <see cref="ContentPackages.FindResource"/> (the entry's own
    /// package, then its dependencies), so a model package several works depend on is shipped once. <paramref name="entryRelative"/> is the
    /// entry's BepInEx-relative path; null when the content uses no imported model. A missing or failing file is named through
    /// <paramref name="rejected"/> and that model is left out.</summary>
    public static IReadOnlyDictionary<string, ImportedModelPackage>? ForEntry(string bepInExRoot, string entryRelative,
        NativeModelContent content, Action<PublishedResourceFailure> rejected)
        => content.ImportedResources.Any() ? ForReferences(bepInExRoot, entryRelative, content.ImportedResources, rejected) : null;

    /// <summary>The same lookup for every imported model a package's document set names, each model read once.</summary>
    public static Dictionary<string, ImportedModelPackage> ForReferences(string bepInExRoot, string entryRelative,
        IEnumerable<NativeModelResourceReference> references, Action<PublishedResourceFailure> rejected)
    {
        string package = PublishedResources.PackageRoot(entryRelative);
        var models = new Dictionary<string, ImportedModelPackage>(StringComparer.Ordinal);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
            if (tried.Add(reference.Key) && Read(bepInExRoot, package, reference, entryRelative, rejected) is { } model)
                models[reference.Key] = model;
        return models;
    }

    // Checked models by (root, package, reference) while some registration holds them: the entries of one package that share a model
    // (several enemies, guns and maps) read and hash its files once. The bytes are proved by their hashes, so a later read finds the same.
    private static readonly Dictionary<(string Root, string Package, string Key), WeakReference<ImportedModelPackage>> Checked = new();

    private static ImportedModelPackage? Read(string bepInExRoot, string package, NativeModelResourceReference wanted, string requester,
        Action<PublishedResourceFailure> rejected)
    {
        var key = (bepInExRoot, package, wanted.Key);
        lock (Checked)
            if (Checked.TryGetValue(key, out var held) && held.TryGetTarget(out var known)) return known;
        var model = ReadChecked(bepInExRoot, package, wanted, requester, rejected);
        if (model != null) lock (Checked) Checked[key] = new WeakReference<ImportedModelPackage>(model);
        return model;
    }

    private static ImportedModelPackage? ReadChecked(string bepInExRoot, string package, NativeModelResourceReference wanted, string requester,
        Action<PublishedResourceFailure> rejected)
    {
        string who = requester + " (" + wanted.Id + ")";
        if (ContentPackages.FindResource(bepInExRoot, package, wanted.Revision, ".resource.forge", who, rejected) is not { } document
            || !PublishedResources.TryRead(document, PublishedResources.MaximumResourceBytes, EntryCodes, rejected, out var text, out _))
            return null;
        if (!TryReadDescriptor(text!, out var reference, out var descriptor, out var code, out var reason) || reference!.Key != wanted.Key)
        {
            rejected(new(document.Relative, code ?? ReferenceCode, reason ?? "The file holds " + reference!.Key + ", not " + wanted.Key + "."));
            return null;
        }
        if (ContentPackages.FindResource(bepInExRoot, package, descriptor!.MeshSha256, MeshExtension, who, rejected) is not { } meshFile) return null;
        if (!ImportedMeshResource.TryRead(meshFile, EntryCodes, out var mesh, out var failure)) { rejected(failure); return null; }
        var pngs = new List<byte[]>(descriptor.Textures.Count);
        foreach (var texture in descriptor.Textures)
        {
            if (ContentPackages.FindResource(bepInExRoot, package, texture.Sha256, ".png", who, rejected) is not { } png) return null;
            if (!PublishedResources.TryReadHashed(png, ".png", EntryCodes, "imported-model-texture-file-name",
                    "A texture filename is its 64-character lower-case PNG SHA-256 followed by .png.", "imported-model-texture-file-size",
                    "The texture file is empty.", "imported-model-texture-sha256", "The texture bytes do not match the SHA-256 in the filename.",
                    out _, out byte[]? bytes, out failure)) { rejected(failure); return null; }
            pngs.Add(bytes!);
        }
        return new ImportedModelPackage(reference, descriptor, mesh!.Bytes, pngs);
    }
}
