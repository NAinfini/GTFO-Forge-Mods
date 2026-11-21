using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Development only: puts ForgeVisual's toon shader on the local player's wielded item (or a named object) through the same
/// <see cref="ToonDress"/> a character skin's toon materials use, to compare the two lightings in one scene, and puts the original materials
/// back. Console: <c>toon forward|deferred|off [object]</c>; by name: <c>drive_cli.py call static:ForgeDevelopment.Native.ToonCompare Apply forward</c>.
/// Opaque surfaces with a base-colour map are made toon; others (lens glass, holograms) stay. Changing weapon, a gun model refreshing its look or a
/// level change can replace the materials: run it again.</summary>
internal static class ToonCompare
{
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");
    private static readonly List<ToonDress> Dressed = new();

    internal static string Apply(string mode, string target = "")
    {
        foreach (var dress in Dressed) dress.Dispose();
        Dressed.Clear();
        if (mode == "off") return "toon: original materials back";
        ToonLighting lighting;
        if (mode == "forward") lighting = ToonLighting.Forward;
        else if (mode == "deferred") lighting = ToonLighting.Deferred;
        else return "toon: mode is forward, deferred or off";
        var root = Root(target);
        if (root == null) return target.Length == 0 ? "toon: the local player wields nothing" : "toon: no active object named " + target;
        int renderers = 0, converted = 0, kept = 0;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer.TryCast<MeshRenderer>() == null && renderer.TryCast<SkinnedMeshRenderer>() == null) continue;
            var materials = renderer.sharedMaterials;
            var toon = new bool[materials.Length];
            var any = false;
            for (var i = 0; i < toon.Length; i++)
            {
                var source = materials[i];
                toon[i] = source != null && source.renderQueue < 2500 && source.HasProperty(MainTex) && source.GetTexture(MainTex) != null;
                if (toon[i]) { any = true; converted++; }
                else if (source != null) kept++;
            }
            if (!any) continue;
            var dress = ToonDress.Wear(renderer, toon);
            if (!dress.Set(lighting)) return "toon: ForgeVisual's shaders are not loaded (see the log)";
            Dressed.Add(dress);
            renderers++;
        }
        if (converted == 0) return "toon: nothing on " + root.name + " has an opaque base-colour surface";
        return "toon " + mode + ": " + renderers + " renderers, " + converted + " surfaces on " + root.name + (kept > 0 ? ", " + kept + " left as they were" : "");
    }

    private static GameObject? Root(string target)
    {
        if (target.Length > 0) return GameObject.Find(target);
        var item = ForgeCore.Players.Local?.FPItemHolder?.WieldedItem;
        return item == null || item.WasCollected ? null : item.gameObject;
    }
}
