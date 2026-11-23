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

    private static string Depth(Material material, int id) => material.HasProperty(id) ? material.GetFloat(id).ToString(CultureInfo.InvariantCulture) : "none";

    /// <summary>Whether one material is in <paramref name="queue"/> and, when <paramref name="depth"/>, tests depth LEqual in every
    /// depth-test property its shader declares. <paramref name="unreadable"/> is set when a depth test was asked for and the shader declares
    /// neither property: its depth test cannot be read, so only its queue is judged (the real fallback-font shaders are like this) and the
    /// caller reports the count. The one judgement <see cref="Read"/> and
    /// <see cref="SelfTest"/> share, so the self test proves the detection that reads the level.</summary>
    internal static bool Judge(Material? material, int queue, bool depth, out bool unreadable)
    {
        unreadable = false;
        bool right = material != null && material.renderQueue == queue;
        if (!right || !depth) return right;
        bool any = false;
        foreach (var id in new[] { ZTestMode, GuiZTestMode })
            if (material!.HasProperty(id)) { any = true; right &= material.GetFloat(id) == 4; }
        unreadable = !any;
        return right;
    }

    /// <summary>Detection ability: judges a correct text material and three deliberately broken ones (queue one below, depth test Always, no
    /// material) with the same <see cref="Judge"/>. <c>selftest=ok;control=1;caught=3/3</c> is the only passing answer; a level with no text to
    /// copy a material from answers <c>selftest=no-text-material</c>.</summary>
    public static string SelfTest()
    {
        Material? sample = null;
        foreach (var text in Object.FindObjectsOfType<TextMeshPro>())
            if (text != null && text.fontSharedMaterial != null) { sample = text.fontSharedMaterial; break; }
        if (sample == null) return "selftest=no-text-material";
        var good = new Material(sample) { renderQueue = WorldBackdrop.TextQueue };
        // The depth test is written the way ForgeCore writes it onto a board's text (WorldText.OverBackdrop): both properties set, declared by the shader or not.
        foreach (var id in new[] { ZTestMode, GuiZTestMode }) good.SetFloat(id, 4);
        var wrongQueue = new Material(good) { renderQueue = WorldBackdrop.TextQueue - 1 };
        var wrongDepth = new Material(good);
        foreach (var id in new[] { ZTestMode, GuiZTestMode }) wrongDepth.SetFloat(id, 8);
        try
        {
            int control = Judge(good, WorldBackdrop.TextQueue, true, out var unreadable) && !unreadable ? 1 : 0, caught = 0;
            if (!Judge(wrongQueue, WorldBackdrop.TextQueue, true, out _)) caught++;
            if (!Judge(wrongDepth, WorldBackdrop.TextQueue, true, out unreadable) || unreadable) caught++;
            if (!Judge(null, WorldBackdrop.TextQueue, true, out _)) caught++;
            return "selftest=" + (control == 1 && caught == 3 ? "ok" : "broken") + ";control=" + control + ";caught=" + caught + "/3"
                + (control == 1 ? "" : ";controlMaterial=" + good.shader.name + " queue=" + good.renderQueue.ToString(CultureInfo.InvariantCulture) + " want=" + WorldBackdrop.TextQueue.ToString(CultureInfo.InvariantCulture)
                    + " ztest=" + Depth(good, ZTestMode) + "/" + Depth(good, GuiZTestMode) + " unreadable=" + unreadable);
        }
        finally { Object.Destroy(good); Object.Destroy(wrongQueue); Object.Destroy(wrongDepth); }
    }

    /// <summary><c>boards=;materials=;subMeshes=;wrong=;noDepthProperty=;unordered=;first=</c>: <c>first</c> names the first wrong
    /// material (<c>none</c> when all are right); <c>noDepthProperty</c> counts text materials whose shader declares
    /// neither depth-test property, so only their queue was judged; <c>unordered</c> counts boards whose
    /// text queue is not above their backdrop's.</summary>
    public static string Read()
    {
        int boards = 0, materials = 0, subMeshes = 0, wrong = 0, noDepthProperty = 0, unordered = 0;
        string first = "none";
        void Check(string owner, Material? material, int queue, bool depth)
        {
            materials++;
            bool right = Judge(material, queue, depth, out var unreadable);
            if (unreadable) noDepthProperty++;
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
            if (backdrop.sharedMaterial == null || text.fontSharedMaterial == null || backdrop.sharedMaterial.renderQueue >= text.fontSharedMaterial.renderQueue) unordered++;
            var subs = text.m_subTextObjects;
            if (subs == null) continue;
            for (var i = 1; i < subs.Length && subs[i] != null; i++)
            {
                subMeshes++;
                Check(name + "#" + i.ToString(CultureInfo.InvariantCulture), subs[i].sharedMaterial, WorldBackdrop.TextQueue, true);
            }
        }
        return new StringBuilder().Append("boards=").Append(boards).Append(";materials=").Append(materials)
            .Append(";subMeshes=").Append(subMeshes).Append(";wrong=").Append(wrong).Append(";noDepthProperty=").Append(noDepthProperty).Append(";unordered=").Append(unordered).Append(";first=").Append(first).ToString();
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
