using System.IO;

namespace ForgeVisual;

/// <summary>The effect bundle Forge ships: particle prefabs built by <c>Tools/Vfx/ForgeVfx</c> (Unity 2019.4.21f1, the game's own version),
/// embedded in this DLL and registered once under the reserved pack <c>Forge</c> (<c>Forge:forge_sparks</c>). Every Forge mod draws its effects
/// from this one copy; Unity refuses a second load of the same bundle, so nothing else loads it. A DLL built without the file registers
/// nothing, and a model asked for by name is then refused by name.</summary>
internal static class EffectBundle
{
    internal const string File = "forge-vfx.bundle";
    internal const string Resource = "ForgeVisual.Vfx." + File;

    internal static void Register()
    {
        using var stream = typeof(EffectBundle).Assembly.GetManifestResourceStream(Resource);
        if (stream == null)
        {
            Plugin.Logger.ErrorOnce("ForgeVisual pack Forge was not registered: " + Resource + " is not embedded in this build, so the Forge effects are refused.");
            return;
        }
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var bundle = Bundles.RegisterBuiltIn(File, bytes.ToArray());
        if (bundle == null) return;
        foreach (var effect in ForgeEffects.All)
            Fx.Register(bundle, effect.Item, new FxOptions { DefaultSize = effect.DefaultSize, Line = effect.Line, HoldSeconds = effect.HoldSeconds, HoldReach = effect.HoldReach > 0f ? effect.HoldReach : 1.5f });
    }
}
