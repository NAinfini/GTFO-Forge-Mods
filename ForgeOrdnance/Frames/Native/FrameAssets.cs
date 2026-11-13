using System;
using System.IO;
using System.Linq;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance.Frames;

/// <summary>Frames/forge-frames.bundle next to the plugin, registered with ForgeVisual (<see cref="Bundles.Register"/>) as the pack
/// <see cref="Pack"/> at plugin load. Registration reads the bundle's names and prefabs at once; the gear-shaded materials follow on the
/// first spawn, once a level has been built (<see cref="LevelBuilt"/>). A suit's model and its launcher's parts are ForgeVisual
/// <c>Pack:item</c> names of any pack (this one or another mod's). Each suit prefab holds one skinned renderer and a legacy Animation
/// with the opening clips open and close; a worn suit's limbs follow its pilot's own game body.</summary>
internal static class FrameAssets
{
    internal const string Pack = "Frames";
    internal const string BundleFile = "Frames/forge-frames.bundle";
    private static BundleHandle? _registered;

    /// <summary>This plugin's own pack once a level has been built (before that nothing can be spawned or worn), else null.</summary>
    internal static BundleHandle? Bundle { get; private set; }
    internal static string? Failure { get; private set; }

    /// <summary>Plugin load: registers the pack, so definitions that name its prefabs pass admission.</summary>
    internal static void Register()
    {
        try
        {
            _registered = Bundles.Register(Plugin.Id, Pack, BundleFile)
                ?? throw new InvalidDataException("ForgeVisual did not register " + BundleFile + " (its error is in the log)");
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Plugin.Logger.ErrorOnce("Exosuit art could not be loaded; suits cannot be shown or boarded: " + error);
        }
    }

    internal static void LevelBuilt()
    {
        Bundle = _registered;
        FrameHudArt.Settle();
    }

    /// <summary>Definition admission runs before the gear shader exists. Check the source prefabs through ForgeVisual's registered
    /// packs without creating materials or instances, so missing/invalid art never becomes a selectable, chargeable call. The suit's
    /// model (a skinned renderer, an Animation when it opens, every bone and node its definition names, <see cref="FrameBodyRig"/>),
    /// the cockpit mark (<see cref="FrameHudArt.Expect"/>: checked once after the first level is built, when every icon pack is registered;
    /// ForgeUI writes the one error line when it is missing, the suit still works) and, with rockets,
    /// its launcher (one mesh renderer and the tube <see cref="FrameRocketRack.FirstMuzzle"/>) and its rocket (one mesh renderer).
    /// The opening clips are read from the spawned suit (<see cref="FrameVisual"/>): a legacy Animation's clips are not read from a
    /// prefab that was never instantiated.</summary>
    internal static void RequireArt(FrameSpec spec)
    {
        var launcher = spec.Rockets?.Launcher;
        var field = "Model";
        try
        {
            GameObject Prefab(string name)
            {
                if (!ForgeCore.PackItem.TrySplit(name, out var pack, out var item)) throw new InvalidDataException("'" + name + "' is not Pack:item.");
                if (!Bundles.TryGet(pack, out var bundle))
                    throw new InvalidDataException("no ForgeVisual pack '" + pack + "' is registered" + (pack == Pack && Failure != null ? " (" + Failure + ")" : "; its mod must register it with Bundles.Register in its plugin Load"));
                return bundle.TryModel(item, out var model) ? model.Prefab : throw new InvalidDataException("pack '" + pack + "' has no prefab '" + item + "'.");
            }
            var suit = Prefab(spec.Model);
            if (suit.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
                throw new InvalidDataException("Model '" + spec.Model + "' has no skinned renderer.");
            var missing = FrameBodyRig.MissingNodes(spec, name => Models.Find(suit.transform, name) != null).ToList();
            if (missing.Count > 0)
            {
                field = missing[0].Field;
                throw new InvalidDataException("Model '" + spec.Model + "' lacks " + string.Join(", ", missing.Select(m => m.Field + " -> '" + m.Node + "'")) + ".");
            }
            field = "Opening";
            if (spec.Opening != null && suit.GetComponentInChildren<Animation>(true) == null)
                throw new InvalidDataException("Model '" + spec.Model + "' has no Animation for its open and close clips.");
            if (launcher != null)
            {
                field = "Rockets.Launcher.Pod";
                var pod = Prefab(launcher.Pod);
                if (pod.GetComponentsInChildren<MeshRenderer>(true).Length != 1) throw new InvalidDataException("Launcher '" + launcher.Pod + "' must have one mesh renderer.");
                if (Models.Find(pod.transform, FrameRocketRack.FirstMuzzle) == null) throw new InvalidDataException("Launcher '" + launcher.Pod + "' has no tube " + FrameRocketRack.FirstMuzzle + ".");
                field = "Rockets.Launcher.Round";
                if (Prefab(launcher.Round).GetComponentsInChildren<MeshRenderer>(true).Length != 1) throw new InvalidDataException("Rocket '" + launcher.Round + "' must have one mesh renderer.");
            }
        }
        catch (Exception error) { throw new ForgeCore.StrictJsonException(field, error.Message); }
        FrameHudArt.Expect(spec.Model);
    }

    /// <summary>A new instance of the suit model <paramref name="model"/> (<c>Pack:item</c>) with its game-shaded materials (ForgeVisual's
    /// <see cref="ModelHandle.Spawn"/>, which covers skinned renderers), or null (logged) when the art is missing.</summary>
    internal static GameObject? Spawn(string model, Vector3 position, Quaternion rotation)
    {
        if (Bundle == null || Models.Get(model) is not { } handle) return null;
        var instance = handle.Spawn(position, rotation);
        if (instance == null) return null;
        var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (renderers.Length == 0) { Object.Destroy(instance); Plugin.Logger.ErrorOnce("Exosuit model '" + model + "' has no skinned renderer."); return null; }
        return instance;
    }

    /// <summary>A new instance of the part <paramref name="prefab"/> (<c>Pack:item</c>, a launcher or a rocket, checked by
    /// <see cref="RequireArt"/>) under <paramref name="parent"/>, or null (logged) when the art is missing.</summary>
    internal static GameObject? SpawnProp(string prefab, Vector3 position, Quaternion rotation, Transform? parent)
        => Bundle == null || Models.Get(prefab) is not { } handle ? null : handle.Spawn(position, rotation, parent);
}
