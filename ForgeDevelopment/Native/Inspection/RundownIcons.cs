using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CellMenu;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Readback of the Rundown page's expedition icons as the screen shows them: for each icon its tier, index,
/// screen position and the screen rectangle of every renderer it draws (the icon and its three text lines), and how many
/// pairs of icons overlap. Called by name from the console (<c>call static:ForgeDevelopment.Native.RundownIcons Read</c>);
/// <c>Select</c> presses a rundown button of the page the way the button itself does.</summary>
internal static class RundownIcons
{
    private static CM_PageRundown_New? Page => GuiManager.MainMenuLayer?.PageRundownNew;

    /// <summary>Invokes the press callback of the page's rundown button at <paramref name="index"/> (0-based), the callback
    /// the page itself registered for that button.</summary>
    public static string Select(int index)
    {
        var page = Page;
        if (page == null) return "no-page";
        var buttons = page.m_rundownSelections;
        if (buttons == null || index < 0 || index >= buttons.Count) return "no-button";
        var button = buttons[index];
        button.OnBtnPressCallback?.Invoke(button.ID);
        return "pressed " + index + " " + button.RundownKey;
    }

    /// <summary>Presses the page's own "select rundown" button (back to the rundown buttons) through its press callback.</summary>
    public static string Back()
    {
        var button = Page?.m_selectRundownButton;
        if (button == null) return "no-button";
        button.OnBtnPressCallback?.Invoke(button.ID);
        return "pressed select-rundown";
    }

    /// <summary><c>rundown=;page=;tierSpacing=;icons=;overlaps=;tiers=…|icon …</c>; each icon is
    /// <c>tier/index name center=x,y rect=xMin,yMin,xMax,yMax scale=</c> in screen pixels (origin bottom left).</summary>
    public static string Read()
    {
        var page = Page;
        if (page == null) return "no-page";
        // The tier lists PlaceTier fills (m_expIconsAll is filled only once progression arrives).
        var icons = new List<CM_ExpeditionIcon_New>();
        foreach (var tier in new[] { page.m_expIconsTier1, page.m_expIconsTier2, page.m_expIconsTier3, page.m_expIconsTier4, page.m_expIconsTier5 })
            if (tier != null) foreach (var icon in tier) icons.Add(icon);
        var text = new StringBuilder();
        text.Append("rundown=").Append(page.m_currentRundownData?.persistentID.ToString(CultureInfo.InvariantCulture) ?? "none")
            .Append(";page=").Append(GuiManager.MainMenuLayer.m_currentPageEnum)
            .Append(";tierSpacing=").Append(F(page.m_tierSpacing))
            .Append(";screen=").Append(Screen.width).Append('x').Append(Screen.height);
        // How many icons stand off the native PlaceTier position ((t − ½)·1500·s, −2·min(t, 1 − t)·450·s·ScaleYModifier) with
        // t = i/(n − 1) over the tier's icons in list order (icons above the tier left out).
        var visuals = page.m_currentRundownData?.StorytellingData?.Visuals;
        if (visuals != null)
        {
            var data = new[] { visuals.TierAVisuals, visuals.TierBVisuals, visuals.TierCVisuals, visuals.TierDVisuals, visuals.TierEVisuals };
            var lists = new[] { page.m_expIconsTier1, page.m_expIconsTier2, page.m_expIconsTier3, page.m_expIconsTier4, page.m_expIconsTier5 };
            int native = 0, moved = 0;
            for (int tier = 0; tier < lists.Length; tier++)
            {
                if (lists[tier] == null || data[tier] == null) continue;
                var row = new List<CM_ExpeditionIcon_New>();
                foreach (var icon in lists[tier])
                    if (icon != null && icon.DataBlock is { PutIconAboveTier: false }) row.Add(icon);
                float s = data[tier].Scale, depth = 450f * s * data[tier].ScaleYModifier;
                for (int i = 0; i < row.Count; i++)
                {
                    float t = row.Count < 2 ? 0f : i / (float)(row.Count - 1);
                    var expected = new Vector2((t - .5f) * 1500f * s, -2f * Mathf.Min(t, 1f - t) * depth);
                    var actual = (Vector2)row[i].transform.localPosition;
                    if ((actual - expected).sqrMagnitude < .01f && Mathf.Abs(row[i].transform.localScale.x * s - 1f) < 1e-3f) native++;
                    else moved++;
                }
            }
            text.Append(";native=").Append(native).Append(";relaid=").Append(moved);
        }
        var rects = new List<(string Name, Rect Rect)>();
        var lines = new StringBuilder();
        foreach (var icon in icons)
        {
            if (icon == null || !icon.gameObject.activeInHierarchy) continue;
            var camera = CameraFor(icon.gameObject.layer);
            if (camera == null) continue;
            bool any = false;
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (var renderer in icon.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude <= 0) continue;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    var screen = camera.WorldToScreenPoint(point);
                    xMin = Mathf.Min(xMin, screen.x); xMax = Mathf.Max(xMax, screen.x);
                    yMin = Mathf.Min(yMin, screen.y); yMax = Mathf.Max(yMax, screen.y);
                }
                any = true;
            }
            if (!any) continue;
            var center = camera.WorldToScreenPoint(icon.transform.position);
            string name = (int)icon.Tier + "/" + icon.ExpIndex;
            rects.Add((name, Rect.MinMaxRect(xMin, yMin, xMax, yMax)));
            lines.Append("|icon ").Append(name).Append(' ').Append(icon.m_publicName != null ? icon.m_publicName.text : "")
                .Append(" center=").Append(F(center.x)).Append(',').Append(F(center.y))
                .Append(" rect=").Append(F(xMin)).Append(',').Append(F(yMin)).Append(',').Append(F(xMax)).Append(',').Append(F(yMax))
                .Append(" local=").Append(V(icon.transform.localPosition)).Append(" scale=").Append(F(icon.transform.localScale.x));
            var tier = icon.transform.parent;
            if (tier != null) lines.Append(" tier=").Append(V(tier.localPosition)).Append('x').Append(F(tier.localScale.x));
        }
        // The screen's corners in the tiers' parent frame (the page units the tier layout uses), at the first icon's depth.
        foreach (var icon in icons)
        {
            var holder = icon != null ? icon.transform.parent?.parent : null;
            var camera = icon != null ? CameraFor(icon.gameObject.layer) : null;
            if (holder == null || camera == null) continue;
            float depth = camera.WorldToScreenPoint(icon!.transform.position).z;
            var low = holder.InverseTransformPoint(camera.ScreenToWorldPoint(new Vector3(0, 0, depth)));
            var high = holder.InverseTransformPoint(camera.ScreenToWorldPoint(new Vector3(Screen.width, Screen.height, depth)));
            text.Append(";camera=").Append(camera.name).Append(";view=").Append(V(low)).Append("..").Append(V(high));
            break;
        }
        int overlaps = 0;
        string first = "none";
        for (int a = 0; a < rects.Count; a++)
            for (int b = a + 1; b < rects.Count; b++)
                if (rects[a].Rect.Overlaps(rects[b].Rect))
                {
                    overlaps++;
                    if (first == "none") first = rects[a].Name + "~" + rects[b].Name;
                }
        return text.Append(";icons=").Append(rects.Count).Append(";overlaps=").Append(overlaps).Append(";first=").Append(first).Append(lines).ToString();
    }

    // The camera that draws the page: no enabled camera in Camera.allCameras renders the icons' layer, so the menu camera
    // first, then any camera in an active object (enabled or rendered by hand) whose culling mask has the layer.
    private static Camera? CameraFor(int layer)
    {
        var menu = CM_Camera.Current != null ? CM_Camera.Current.Camera : null;
        if (menu != null && (menu.cullingMask & (1 << layer)) != 0) return menu;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Camera>()))
            if (found.TryCast<Camera>() is { } camera && camera.gameObject.activeInHierarchy && (camera.cullingMask & (1 << layer)) != 0) return camera;
        return null;
    }

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string V(Vector3 value) => F(value.x) + "," + F(value.y) + "," + F(value.z);
}
