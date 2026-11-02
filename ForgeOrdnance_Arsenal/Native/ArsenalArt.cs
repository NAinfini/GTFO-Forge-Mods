using System;
using System.Collections.Generic;
using System.IO;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>forge-ordnance-arsenal.bundle next to the plugin (built by tools/build_assets.ps1 with ForgeOrdnance's pipeline), registered with
/// ForgeVisual as the pack <c>Arsenal</c>: the strike, heavy weapon and turret models (every one is <c>Arsenal:name</c> to any mod) and the
/// strike effect textures, registered as ForgeVisual sprite sheets (<see cref="ArsenalSheets"/>). The pack is registered when the plugin loads (the models' handles
/// and the turret looks need it then); the effect textures are taken after the first level is built, when the game's gear shader exists.
/// Sounds are separate loose Ogg files registered by <see cref="ArsenalAudio"/>. Without this bundle every strike and
/// weapon still works; the models and effect looks are missing and the log says why once.</summary>
internal static class ArsenalArt
{
    internal const string BundleFile = "forge-ordnance-arsenal.bundle";
    internal const string Pack = "Arsenal";

    /// <summary>Exported models face −Z (glTF +Z front through Blender's Y/Z swap); this turns their front to +Z.</summary>
    internal static readonly Quaternion FrontToForward = Quaternion.Euler(0f, 180f, 0f);
    /// <summary>A shell or bomblet with its nose (model front) pointing down.</summary>
    internal static readonly Quaternion NoseDown = Quaternion.Euler(-90f, 0f, 0f);

    private static bool _tried;
    private static BundleHandle? _pack;

    /// <summary>Set once <see cref="Load"/> has taken the effect textures; null before, and when the bundle is unusable.</summary>
    internal static BundleHandle? Bundle { get; private set; }
    internal static ModelHandle? Shell, Bomblet, Canister, Emitter;
    /// <summary>The heavy weapon models the accepted payloads name (<see cref="CheckWeaponModel"/>), by model id.</summary>
    private static readonly Dictionary<string, ModelHandle> Weapons = new(StringComparer.Ordinal);
    /// <summary>The sprite sheets of the blast, trail, burn and cloud effects; null before <see cref="Load"/>, and when the bundle is unusable.</summary>
    internal static ArsenalSheets? Sheets { get; private set; }
    internal static string Failure { get; private set; } = "the level has not been built yet";

    /// <summary>Plugin load: registers the pack with ForgeVisual, takes the model handles and registers the turret looks (definition files
    /// are read after this, and a turret definition's <c>Look</c> must already be registered).</summary>
    internal static void Register()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(ArsenalArt).Assembly.Location) ?? throw new InvalidOperationException("The plugin folder is unknown.");
            var path = Path.Combine(folder, BundleFile);
            if (!File.Exists(path)) throw new FileNotFoundException("Install " + BundleFile + " next to ForgeOrdnanceArsenal.dll.", path);
            var pack = Bundles.Register(Plugin.Id, Pack, path) ?? throw new InvalidDataException("ForgeVisual refused " + BundleFile + " (see its error above).");
            ModelHandle Model(string name) => pack.TryModel(name, out var model) ? model : throw new InvalidDataException(BundleFile + " has no model " + name + ".");
            Shell = Model("shell");
            Bomblet = Model("bomblet");
            Canister = Model("gas-canister");
            Emitter = Model("stasis-emitter");
            _pack = pack;
            ArsenalTurrets.Register(Model);
        }
        catch (Exception error)
        {
            Failure = error.ToString();
            Plugin.Error("Arsenal art could not be registered; strikes, heavy weapons and turrets work without their models and effect looks: " + error);
        }
    }

    /// <summary>After the first level is built: takes the effect textures (they need the game's shader) and builds the strike effects.</summary>
    internal static void Load()
    {
        if (_tried) return;
        _tried = true;
        if (_pack == null) return;
        try
        {
            var bundle = _pack;
            SpriteSheet Sheet(string item, string texture, int columns, int rows, FxBlend blend, bool sheddable = false)
            {
                if (!bundle.TryTexture(texture, out var image)) throw new InvalidDataException(BundleFile + " has no effect texture " + texture + ".");
                return SpriteSheet.Register(Plugin.Id, Pack + ":" + item, image, columns, rows, blend, sheddable)
                    ?? throw new InvalidDataException("ForgeVisual refused the sprite sheet " + Pack + ":" + item + " (see its error above).");
            }
            // Smoke and explosions blend by alpha; fire, flashes, glows, sparks and shockwaves add light.
            Sheets = new ArsenalSheets
            {
                Smoke = Sheet("smoke", "vfx-smoke", 5, 3, FxBlend.Alpha, sheddable: true),
                Blast = Sheet("blast", "vfx-blast", 5, 5, FxBlend.Alpha),
                Fireball = Sheet("fireball", "vfx-fireball", 8, 8, FxBlend.Additive),
                Flash = Sheet("flash", "vfx-flash", 8, 8, FxBlend.Additive),
                Flame = Sheet("flame", "vfx-flame", 16, 4, FxBlend.Additive),
                Flare = Sheet("flare", "vfx-flare", 1, 1, FxBlend.Additive),
                Ring = Sheet("ring", "vfx-ring", 1, 1, FxBlend.Additive),
                Scorch = Sheet("scorch", "vfx-scorch", 1, 1, FxBlend.Alpha),
                Streak = Sheet("streak", "vfx-streak", 1, 1, FxBlend.Additive),
                Glow = Sheet("glow", "vfx-glow", 1, 1, FxBlend.Additive),
                Mist = Sheet("mist", "vfx-mist", 2, 2, FxBlend.Alpha),
                Fire = Sheet("fire", "vfx-fire", 2, 2, FxBlend.Alpha),
                Spark = Sheet("spark", "vfx-spark", 1, 1, FxBlend.Alpha),
            };
            Bundle = bundle;
            Failure = "";
        }
        catch (Exception error)
        {
            Sheets = null;
            Failure = error.ToString();
            Plugin.Error("Arsenal art could not be loaded; strikes and heavy weapons work without their models and effect looks: " + error);
            return;
        }
        try { StrikeBursts.Prepare(); }
        catch (Exception error) { Plugin.Error("Building the strike effects at level load failed; they are built at the first strike instead: " + error); }
    }

    internal static ModelHandle? Look(ShellLook look) => look switch
    {
        ShellLook.Bomblet => Bomblet,
        ShellLook.Canister => Canister,
        ShellLook.Emitter => Emitter,
        _ => Shell,
    };

    /// <summary>Accepts <paramref name="id"/> (<c>Pack:item</c>) as a heavy weapon model, or says what it lacks: it must be registered with ForgeVisual
    /// (its pack in the owning plugin's <c>Load</c>, before definition files are read) and have the sockets <see cref="Models.Dress"/> puts a gun
    /// on the game's by: <c>RightHand</c>, <c>LeftHand</c> and the aim point <c>SightLook</c>. Null when accepted.</summary>
    internal static string? CheckWeaponModel(string id)
    {
        if (Weapons.ContainsKey(id)) return null;
        var model = Models.Get(id);
        if (model == null) return "no ForgeVisual model " + id + " is registered (its pack must be registered in its plugin's Load).";
        foreach (var socket in new[] { GunNodes.RightHandSocket, GunNodes.LeftHandSocket, GunNodes.SightSocket })
            if (Models.Find(model.Prefab.transform, socket) == null) return "the model " + id + " has no " + socket + " child (a heavy weapon model needs RightHand, LeftHand and SightLook).";
        Weapons.Add(id, model);
        return null;
    }

    /// <summary>The model of an accepted heavy weapon payload; null for an id no payload named.</summary>
    internal static ModelHandle? Weapon(string id) => Weapons.TryGetValue(id, out var model) ? model : null;
}

/// <summary>The effect textures as ForgeVisual sprite sheets (<c>Arsenal:smoke</c> ...): what every blast, trail, cloud and beam sprite is cut from.</summary>
internal sealed class ArsenalSheets
{
    internal SpriteSheet Smoke = null!, Blast = null!, Fireball = null!, Flash = null!, Flame = null!, Flare = null!, Ring = null!, Scorch = null!, Streak = null!, Glow = null!, Mist = null!, Fire = null!, Spark = null!;

    internal SpriteSheet[] All => new[] { Smoke, Blast, Fireball, Flash, Flame, Flare, Ring, Scorch, Streak, Glow, Mist, Fire, Spark };
}