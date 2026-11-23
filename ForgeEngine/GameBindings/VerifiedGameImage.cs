namespace ForgeEngine.GameBindings;

/// <summary>The GameAssembly image this Runtime build is bound to.</summary>
internal static class VerifiedGameImage
{
    /// <summary>The lowercase SHA-256 of that image. Startup already checked the running image against it, so a consumer
    /// pinning the game assembly compares to this value instead of hashing the file again.</summary>
    public static string Sha256 => GameRuntimeBridge.GameAssemblySha256.ToLowerInvariant();
}
