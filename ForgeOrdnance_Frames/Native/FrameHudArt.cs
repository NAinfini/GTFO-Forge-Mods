using System;
using System.Collections.Generic;
using System.IO;
using ForgeUI;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>
/// The suit's own cockpit art, registered with ForgeUI at plugin load as the <c>Frames:</c> icon pack from the plugin folder's
/// <c>hud/</c> (white shapes on transparency, shipped from <c>Assets/hud/</c> by <c>tools/import_hud_art.ps1</c>): the skill icons,
/// the gauge marks (shield, heat, objective, teammate, the arrow to the back), one mark per model and the shared four-layer damage
/// silhouette (front, back, left, right, the layer order of <see cref="Bearing.Quadrant"/>), plus the visor's crack drawing
/// (<c>hud/visor-cracks.png</c>, a plain file the visor reads itself). The @2x file is used above 1440 lines (ForgeUI's icon folder
/// registration reads the files). Nothing is drawn here: a missing file is logged by ForgeUI in one line, a skill slot without its icon
/// shows its name, a model without a mark shows none, a gauge without its mark draws none, and ForgeUI leaves out (and logs) a missing
/// silhouette layer.
/// </summary>
internal static class FrameHudArt
{
    internal const string Pack = FrameAssets.Pack;
    private static readonly string[] Skills = { "skill-charge", "skill-siege", "skill-scan", "skill-rockets", "skill-lamp", "skill-exit", "skill-carry" };
    private static readonly string[] Marks = { "shield", "heat", "objective", "teammate", "arrow-around" };
    private static readonly string[] Damage = { "damage-front", "damage-back", "damage-left", "damage-right" };
    private static readonly Dictionary<string, Sprite?> Registered = new();
    private static readonly string Folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "hud");

    /// <summary>The visor's crack drawing file.</summary>
    internal static string VisorCracks => Path.Combine(Folder, "visor-cracks.png");

    /// <summary>Registers every piece of art with ForgeUI (once, at plugin load); the model marks are whatever <c>hud/mark-*.png</c> holds.</summary>
    internal static void Register(UIKitMod ui)
    {
        var pack = ui.Icons(Pack);
        if (pack == null) return;
        var names = new List<string>(Skills);
        names.AddRange(Marks);
        names.AddRange(Damage);
        names.Add("mark-*");
        pack.RegisterFolder(Folder, names.ToArray());
    }

    /// <summary>True once every plugin has loaded (a level was built): from then on a missing icon is final and cached as missing too.
    /// Before, only found icons are cached, so an icon pack another mod registers late still counts.</summary>
    private static bool _settled;

    /// <summary>The model <c>Pack:item</c> names admitted so far, whose cockpit mark is checked once everything is registered.</summary>
    private static readonly HashSet<string> Expected = new();

    /// <summary>Every plugin has loaded: from now on a missing icon is final, and each admitted model's mark is looked up once (ForgeUI
    /// writes the one error line for a mark that is missing).</summary>
    internal static void Settle()
    {
        _settled = true;
        foreach (var model in Expected) Mark(model);
    }

    /// <summary>Definition admission: the mark of <paramref name="model"/> is looked up after all icon packs are registered (at once when
    /// that already happened), never at admission itself, when a third-party icon pack may not have registered yet. Each model once.</summary>
    internal static void Expect(string model)
    {
        if (Expected.Add(model) && _settled) Mark(model);
    }

    private static readonly Dictionary<string, string> FullNames = new();

    /// <summary>The icon of <paramref name="key"/> (a name of this pack, or a model <c>Pack:item</c> whose mark is meant), looked up
    /// once per key and read from the cache afterwards (the cockpit asks every frame, so nothing is built or allocated per call: the
    /// ForgeUI name is built once per key too); null when it is not registered (ForgeUI logs why, once).</summary>
    private static Sprite? Lookup(string key, bool mark)
    {
        if (Registered.TryGetValue(key, out var sprite)) return sprite;
        if (!FullNames.TryGetValue(key, out var name)) FullNames.Add(key, name = mark ? FrameModelName.MarkIcon(key) : Pack + ":" + key);
        sprite = UIKit.Icon(name);
        if (sprite != null || _settled) Registered[key] = sprite;
        return sprite;
    }

    /// <summary>This pack's own icon (null when the file is missing).</summary>
    private static Sprite? Get(string name) => Lookup(name, false);

    internal static Sprite? Shield => Get("shield");
    internal static Sprite? Heat => Get("heat");
    internal static Sprite? Objective => Get("objective");
    internal static Sprite? Teammate => Get("teammate");
    internal static Sprite? ArrowAround => Get("arrow-around");

    internal static Sprite? Skill(FrameSpecialKind kind) => kind switch
    {
        FrameSpecialKind.Charge => Get("skill-charge"),
        FrameSpecialKind.Siege => Get("skill-siege"),
        FrameSpecialKind.Scan => Get("skill-scan"),
        _ => null,
    };

    internal static Sprite? Rockets => Get("skill-rockets");
    internal static Sprite? Lamp => Get("skill-lamp");
    internal static Sprite? Exit => Get("skill-exit");
    internal static Sprite? Carry => Get("skill-carry");

    /// <summary>The cockpit mark of a model <c>Pack:item</c>: the icon <c>mark-item</c> of the ForgeUI icon pack named like the model's
    /// pack (any mod's), null when there is none.</summary>
    internal static Sprite? Mark(string model) => Lookup(model, true);

    /// <summary>The four silhouette layers in <see cref="Bearing.Quadrant"/> order, each the delivered art or null when its file is
    /// missing (ForgeUI logs a missing layer as an error and does not draw it).</summary>
    internal static IReadOnlyList<Sprite?> Silhouette => Array.ConvertAll(Damage, Get);
}
