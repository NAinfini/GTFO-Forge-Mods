using BepInEx.Unity.IL2CPP;

namespace ForgeWeapon.Native;

/// <summary>Where a loadout policy's pinned bytes come from in a real install: the runtime's verified game assembly digest, the loader's own per-guid plugin
/// locations, and the files under the BepInEx root. This is the one
/// place that knows those three, which is what keeps <see cref="LoadoutPolicyData"/> a function of a root directory
/// and an injected source — and so keeps its suite runnable with no profile and no game.</summary>
internal static class LoadoutPolicyWiring
{
    /// <summary>The production pin source. The game assembly digest is the one the runtime verified against the running
    /// image at startup, not a file a policy names, and a GUID this install has not installed answers null rather than a
    /// guessed location.</summary>
    internal static LoadoutPinSource PinSource() => new(
        () => ForgeEngine.GameBindings.VerifiedGameImage.Sha256,
        guid => IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var plugin) ? plugin.Location : null,
        LoadoutPolicyData.HashBounded);
}
