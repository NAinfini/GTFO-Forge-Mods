using System.Globalization;
using System.Text;
using ForgeVisual;
using TMPro;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Readback of the layer order of the boards in the level: every text standing beside or over a backdrop
/// (ForgeCore WorldBackdrop: a sign, a world label, a model label with a background). Each material the text draws with,
/// the main one and each fallback-font sub-mesh's, must be in <see cref="WorldBackdrop.TextQueue"/> with the depth test
/// LEqual (4) in each depth-test property its shader declares, and the backdrop in <see cref="WorldBackdrop.Queue"/>. Called by name from the console
/// (<c>call static:ForgeDevelopment.Native.SignLayers Read</c>).</summary>
internal static class SignLayers
{
    private static readonly int ZTestMode = Shader.PropertyToID("_ZTestMode"), GuiZTestMode = Shader.PropertyToID("unity_GUIZTestMode");

    /// <summary><c>boards=;materials=;subMeshes=;wrong=;noDepthProperty=;first=</c>: <c>first</c> names the first wrong
    /// material (<c>none</c> when all are right); <c>noDepthProperty</c> counts text materials whose shader declares
    /// neither depth-test property, so their depth test could not be read.</summary>
    public static string Read()
    {
        int boards = 0, materials = 0, subMeshes = 0, wrong = 0, noDepthProperty = 0;
        string first = "none";
        string Depth(Material material, int id) => material.HasProperty(id) ? material.GetFloat(id).ToString(CultureInfo.InvariantCulture) : "none";
        void Check(string owner, Material? material, int queue, bool depth)
        {
            materials++;
            bool right = material != null && material.renderQueue == queue;
            if (right && depth)
            {
                bool any = false;
                foreach (var id in new[] { ZTestMode, GuiZTestMode })
                    if (material!.HasProperty(id)) { any = true; right &= material.GetFloat(id) == 4; }
                if (!any) noDepthProperty++;
            }
            if (right) return;
            wrong++;
            if (first == "none")
                first = owner + "/" + (material == null ? "no-material" : material.name + " queue=" + material.renderQueue.ToString(CultureInfo.InvariantCulture)
                    + " ztest=" + Depth(material, ZTestMode) + "/" + Depth(material, GuiZTestMode));
        }
        foreach (var text in Object.FindObjectsOfType<TextMeshPro>())
        {
            if (text == null || !text.gameObject.activeInHierarchy) continue;
            var backdrop = Backdrop(text.transform) ?? (text.transform.parent != null ? Backdrop(text.transform.parent) : null);
            if (backdrop == null) continue;
            boards++;
            var name = text.gameObject.name;
            Check(name, backdrop.sharedMaterial, WorldBackdrop.Queue, false);
            Check(name, text.fontSharedMaterial, WorldBackdrop.TextQueue, true);
            var subs = text.m_subTextObjects;
            if (subs == null) continue;
            for (var i = 1; i < subs.Length && subs[i] != null; i++)
            {
                subMeshes++;
                Check(name + "#" + i.ToString(CultureInfo.InvariantCulture), subs[i].sharedMaterial, WorldBackdrop.TextQueue, true);
            }
        }
        return new StringBuilder().Append("boards=").Append(boards).Append(";materials=").Append(materials)
            .Append(";subMeshes=").Append(subMeshes).Append(";wrong=").Append(wrong).Append(";noDepthProperty=").Append(noDepthProperty).Append(";first=").Append(first).ToString();
    }

    /// <summary>The backdrop renderer among <paramref name="parent"/>'s direct children (WorldBackdrop names it "… background …").</summary>
    private static MeshRenderer? Backdrop(Transform parent)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (!child.gameObject.activeSelf || !child.gameObject.name.Contains("background")) continue;
            var renderer = child.GetComponent<MeshRenderer>();
            if (renderer != null) return renderer;
        }
        return null;
    }
}
