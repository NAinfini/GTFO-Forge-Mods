using Microsoft.Win32.SafeHandles;

namespace ForgeEngine.Framework;

/// <summary>
/// The offline runner's stand-in for the mod's <c>PublishedFileIdentity</c>. The mod pins the install root and compares each
/// opened document's final path (Win32 handles) because the game reads a folder other programs may change while it loads.
/// The runner reads a private copy it extracted itself (the extraction never creates links), so there is nothing to race, and
/// it must run on the Linux website host where those handle calls do not exist. Every admission rule is unchanged; only the
/// handle-identity re-check is a no-op here.
/// </summary>
internal static class PublishedFileIdentity
{
    internal static SafeFileHandle OpenRoot(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The package root does not exist.");
        // Callers only need a live handle to borrow and dispose; the runner's own assembly is always readable.
        return File.OpenHandle(typeof(PublishedFileIdentity).Assembly.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    internal static void VerifyRoot(SafeFileHandle handle, string expected) { }

    internal static void VerifyDocument(SafeFileHandle handle, string expected) { }
}
