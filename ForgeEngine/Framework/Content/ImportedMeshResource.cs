namespace ForgeEngine.Framework;

/// <summary>One imported model's mesh sidecar (<c>&lt;sha256&gt;.fmesh</c>), hashed over exactly these bytes. ForgeVisual reads the format.</summary>
internal sealed record ImportedMeshResource(string Sha256, byte[] Bytes, string Path)
{
    public const string Extension = ".fmesh";

    public static bool TryRead(PublishedResource document, PublishedBytesCodes codes, out ImportedMeshResource? mesh, out PublishedResourceFailure failure)
    {
        mesh = null;
        if (!PublishedResources.TryReadHashed(document, Extension, codes, "imported-model-mesh-file-name",
                "A mesh filename is its 64-character lower-case SHA-256 followed by " + Extension + ".", "imported-model-mesh-file-size",
                "The mesh file is empty.", "imported-model-mesh-sha256", "The mesh bytes do not match the SHA-256 in the filename.",
                out string sha256, out byte[]? bytes, out failure))
            return false;
        mesh = new(sha256, bytes!, document.Relative);
        return true;
    }
}
