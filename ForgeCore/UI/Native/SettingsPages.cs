using System;
using System.Collections.Generic;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>One slider a mod adds to a game settings page: <see cref="Id"/> names it, <see cref="TextKey"/> is the key of its label in
/// ForgeUI's language files (shown in the current language), <see cref="Get"/> and <see cref="Set"/> read and write its value, 0 to 1.</summary>
public sealed record SettingsSlider(string Id, string TextKey, Func<float> Get, Action<float> Set);

/// <summary>A block of the game's own settings that <see cref="UIKitMod.AddSettings"/> adds rows to: one window of a settings page.</summary>
public enum SettingsBlock
{
    /// <summary>The video page's ADVANCED GRAPHICS window, the last of its three windows.</summary>
    AdvancedGraphics,
}

/// <summary>A row added to a block of the game's settings (<see cref="UIKitMod.AddSettings"/>): <see cref="SettingsChoice"/> or
/// <see cref="SettingsToggle"/>, a name and a control like the game's own rows. The game's own row code runs for it, so it looks,
/// hovers, sounds and scrolls like the game's. Every value, <c>Enabled</c> and <c>Hint</c> is read again twice a second while the
/// page shows, so a change made elsewhere shows there. <c>Hint</c> (optional) answers a short text the game shows in its own tooltip
/// while the cursor is on the row, or null for none: what the setting does, or why it cannot be used now. The list holds no text
/// rows; put what a player must know into the option names.</summary>
public abstract record SettingsRow;

/// <summary>The game's dropdown row: <see cref="Title"/>, and a popup of <see cref="Options"/>; a value is an option's index.
/// <see cref="Set"/> runs when the player picks one. While <see cref="Enabled"/> (optional) answers false the dropdown has the game's
/// disabled look and does not open.</summary>
public sealed record SettingsChoice(UIText Title, IReadOnlyList<UIText> Options, Func<int> Get, Action<int> Set, Func<bool>? Enabled = null,
    Func<UIText?>? Hint = null) : SettingsRow;

/// <summary>The game's on/off row: <see cref="Title"/>, and the game's own On and Off. <see cref="Set"/> runs on every press. While
/// <see cref="Enabled"/> (optional) answers false the row is dimmed and a press changes nothing.</summary>
public sealed record SettingsToggle(UIText Title, Func<bool> Get, Action<bool> Set, Func<bool>? Enabled = null, Func<UIText?>? Hint = null) : SettingsRow;

/// <summary>Which game method builds a settings page: the key pages by <c>CreateKeyMappingSubMenu</c>, every other page by <c>CreateSubmenu</c>.</summary>
internal enum PageBuilder { KeyMapping, Submenu }

/// <summary>A group of rows one owner adds to one game settings page. <see cref="Items"/> is what it appended to the page's list.</summary>
internal abstract class PageRows
{
    internal abstract PageBuilder Builder { get; }
    internal abstract eSettingsSubMenuId Page { get; }
    /// <summary>What the rows are, for messages ("the mods' key actions").</summary>
    internal abstract string Owner { get; }
    internal readonly List<CM_SettingsItem> Items = new();

    /// <summary>Whether the rows go into this window of the page: a page built by several <c>CreateSubmenu</c> calls (the video page's
    /// three windows) has one list of the game's settings per window, <paramref name="list"/> (zero for a key page).</summary>
    internal virtual bool Takes(CM_PageSettings? settings, IntPtr list) => true;
    /// <summary>The game is about to lay out the page: drop the rows of an earlier build, copy new ones with <see cref="SettingsPages.CopyRows"/>.</summary>
    internal abstract void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content);
    /// <summary>The game's reset of the page.</summary>
    internal virtual void PageReset() { }
    /// <summary>Per frame.</summary>
    internal virtual void Update() { }
}

/// <summary>Adds rows to the game's own settings pages. The rows join the game's own build of the page: while the game builds a page, the
/// list it hands to its window (<c>CM_ScrollWindow.SetContentItems</c>) gets the rows after the game's, so the game lays out, scrolls,
/// hovers and registers cursor items for them in its single call exactly as for its own rows. The window is never given a list again: the
/// game's <c>SetContentItems</c> destroys every child of the content container before it lays out the new list, so a second call destroyed
/// the game's rows with ours (an empty page and a NullReferenceException on every hover). Each row is a copy of one of the game's rows
/// made while inactive. Rows are registered in a package's Load, before the game builds its settings; one added after that is refused
/// and logged.
/// </summary>
public static class SettingsPages
{
    private static readonly List<PageRows> Groups = new() { KeyBindSettings.Group };
    private static readonly HashSet<(PageBuilder, eSettingsSubMenuId)> Built = new();
    private static eSettingsSubMenuId? _keyMapping;
    private static (eSettingsSubMenuId Page, CM_PageSettings Settings, IntPtr List)? _submenu;
    private static (PageBuilder Builder, eSettingsSubMenuId Page, CM_PageSettings? Settings, IntPtr List) _laying;

    /// <summary>Every row a group copied: its title is the group's, so the game's own title refresh of the row never runs for it.</summary>
    internal static readonly HashSet<IntPtr> Copies = new();

    /// <summary>The rows of <see cref="UIKitMod.AddSettings"/>: false (and a logged error) when the name is taken or the game already
    /// built the page.</summary>
    internal static bool AddRows(UIKitMod mod, string id, SettingsBlock block, IReadOnlyList<SettingsRow> rows, Action? onReset)
    {
        if (!Enum.IsDefined(typeof(SettingsBlock), block)) throw new ArgumentOutOfRangeException(nameof(block));
        if (rows == null || rows.Count == 0) throw new ArgumentException("Give at least one row.", nameof(rows));
        foreach (var row in rows)
            if (row == null) throw new ArgumentException("A row is null.", nameof(rows));
        foreach (var group in Groups)
            if (group is OptionRows existing && existing.Id == id)
            {
                Plugin.Error("ui-settings-rows-name-taken", "The settings rows " + id + " are already added; these are not.");
                return false;
            }
        if (Built.Contains((PageBuilder.Submenu, OptionRows.PageOf(block))))
        {
            Plugin.Error("ui-settings-rows-were-added-after-game-built", "The settings rows " + id + " were added after the game built its settings and are not listed there; add them in the package's Load.");
            return false;
        }
        PatchGroups.Ensure(PatchGroup.Settings);
        Groups.Add(new OptionRows(mod, id, block, rows, onReset));
        return true;
    }

    /// <summary>The rows of <see cref="UIKitMod.SettingsReadback"/>.</summary>
    internal static string Readback(string id)
    {
        foreach (var group in Groups)
            if (group is OptionRows rows && rows.Id == id) return rows.Readback();
        return "no settings rows " + id;
    }

    /// <summary>Adds a heading and one slider row per entry of <paramref name="sliders"/> to the game's audio settings page, after the game's
    /// rows. The rows are copies of the game's sound-effects slider row (<c>Audio_SFXVolume</c>), so they look, drag and scroll like the
    /// game's. A drag writes through <see cref="SettingsSlider.Set"/> at once; the rows show <see cref="SettingsSlider.Get"/> again when it
    /// changes elsewhere. <paramref name="onPageReset"/> runs when the player resets the page. A <paramref name="sectionId"/> already added
    /// is ignored, so several packages may add the same section; false (and a logged error) when the game already built the page. The heading and the labels are keys of ForgeUI's own language files.</summary>
    /// <example><c>SettingsPages.AddAudioSliders("ForgeAudio.volumes", AudioVolumes.HeadingKey, rows, AudioVolumes.ResetAll);</c></example>
    public static bool AddAudioSliders(string sectionId, string headingKey, IReadOnlyList<SettingsSlider> sliders, Action? onPageReset = null)
    {
        const eSettingsSubMenuId page = eSettingsSubMenuId.Audio;
        if (string.IsNullOrWhiteSpace(sectionId)) throw new ArgumentException("Name the section.", nameof(sectionId));
        if (sliders == null || sliders.Count == 0) throw new ArgumentException("Give at least one slider.", nameof(sliders));
        foreach (var group in Groups)
            if (group is SliderRows existing && existing.Id == sectionId) return false;
        if (Built.Contains((PageBuilder.Submenu, page)))
        {
            Plugin.Error("ui-sliders-were-added-after-game-built", "The sliders of " + sectionId + " were added after the game built its audio settings page and are not listed there; add them in the package's Load.");
            return false;
        }
        PatchGroups.Ensure(PatchGroup.Settings);
        Groups.Add(new SliderRows(sectionId, page, eCellSettingID.Audio_SFXVolume, headingKey, sliders, onPageReset));
        return true;
    }

    /// <summary>Per frame.</summary>
    internal static void Update()
    {
        foreach (var group in Groups) group.Update();
    }

    /// <summary>Makes the rows of <paramref name="owner"/> as copies of <paramref name="template"/> under an inactive holder (so none of the
    /// copies' game components runs before <paramref name="copy"/> has set them up), then moves them under the template's parent and appends
    /// them to the list the game is about to lay out.</summary>
    internal static void CopyRows(PageRows owner, CM_SettingsItem template, string holderName,
        Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content, Func<Transform, IEnumerable<CM_SettingsItem>> copy)
    {
        var parent = template.transform.parent;
        var holder = new GameObject(holderName);
        holder.SetActive(false);
        holder.transform.SetParent(parent, false);
        try
        {
            owner.Items.AddRange(copy(holder.transform));
            foreach (var row in owner.Items)
            {
                row.transform.SetParent(parent, false);
                content.Add(new iScrollWindowContent(row.Pointer));
            }
        }
        finally { Object.Destroy(holder); }
    }

    /// <summary>Takes the <paramref name="rows"/> of an earlier build out of <paramref name="window"/>'s list and destroys them.</summary>
    internal static void Drop(CM_ScrollWindow? window, List<CM_SettingsItem> rows)
    {
        var dropped = new HashSet<IntPtr>();
        foreach (var row in rows)
            if (!row.WasCollected) dropped.Add(row.Pointer);
        if (dropped.Count == 0) return;
        if (window != null && !window.WasCollected && window.m_contentItems is { } items)
        {
            var kept = new Il2CppSystem.Collections.Generic.List<iScrollWindowContent>();
            for (var i = 0; i < items.Count; i++)
                if (items[i] != null && !dropped.Contains(items[i].Pointer)) kept.Add(items[i]);
            if (kept.Count != items.Count) window.m_contentItems = kept;
        }
        foreach (var row in rows)
        {
            if (row.WasCollected) continue;
            Copies.Remove(row.Pointer);
            Object.Destroy(row.gameObject);
        }
    }

    /// <summary>The game's list after its own layout must hold every row of the group, alive, under the window's content container.</summary>
    private static void CheckLaidOut(PageRows group, CM_ScrollWindow window)
    {
        var items = window.m_contentItems;
        var container = window.m_contentContainer == null ? null : window.m_contentContainer.transform;
        var listed = 0;
        if (items != null)
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i]?.TryCast<Component>() is not { } component || component == null)
                {
                    Plugin.Error("ui-games-settings-page-holds-empty", "The game's " + group.Page + " settings page holds an empty or destroyed row at " + i + " after laying out " + group.Owner + ".");
                    continue;
                }
                foreach (var row in group.Items)
                    if (!row.WasCollected && row.Pointer == component.Pointer) { listed++; break; }
            }
        var misplaced = 0;
        foreach (var row in group.Items)
            if (row.WasCollected || container == null || row.transform.parent != container) misplaced++;
        if (listed != group.Items.Count || misplaced > 0)
            Plugin.Error("ui-games-settings-page-laid-out-rows", "The game's " + group.Page + " settings page laid out " + listed + " of " + group.Items.Count + " rows of " + group.Owner + " (" + misplaced + " outside its content).");
    }

    /// <summary>Marks the build of a key page (the gamepad page is built by the same method with its own id).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.CreateKeyMappingSubMenu))]
    private static class KeyPageBuild
    {
        [HarmonyPrefix]
        private static void Prefix(eSettingsSubMenuId subMenuId) => _keyMapping = subMenuId;

        [HarmonyPostfix]
        private static void Postfix() => _keyMapping = null;
    }

    /// <summary>Marks the build of every other page (every one is built by the same method with its own id; the video page by three
    /// calls, one per window, each with its own list of the game's settings).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.CreateSubmenu))]
    private static class SubmenuBuild
    {
        [HarmonyPrefix]
        private static void Prefix(CM_PageSettings __instance, eSettingsSubMenuId subMenuId, Il2CppSystem.Collections.Generic.List<iSettingsFieldData> settingsData)
            => _submenu = (subMenuId, __instance, settingsData == null ? IntPtr.Zero : settingsData.Pointer);

        [HarmonyPostfix]
        private static void Postfix() => _submenu = null;
    }

    /// <summary>The game lays out each page once: the groups' rows go into that same list, then the result is checked.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_ScrollWindow), nameof(CM_ScrollWindow.SetContentItems))]
    private static class PageLayout
    {
        [HarmonyPrefix]
        private static void Prefix(CM_ScrollWindow __instance, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> contentItems, out bool __state)
        {
            __state = false;
            if (contentItems == null) return;
            if (_keyMapping is { } key) _laying = (PageBuilder.KeyMapping, key, null, IntPtr.Zero);
            else if (_submenu is { } sub) _laying = (PageBuilder.Submenu, sub.Page, sub.Settings, sub.List);
            else return;
            _keyMapping = null;
            _submenu = null;
            __state = true;
            var built = _laying;
            Built.Add((built.Builder, built.Page));
            foreach (var group in Groups)
            {
                if (!Lays(group, built)) continue;
                group.Items.Clear();
                try { group.Append(__instance, contentItems); }
                catch (Exception error) { Plugin.Error("ui-adding-games-settings-page-failed", "Adding " + group.Owner + " to the game's " + group.Page + " settings page failed: " + error); }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(CM_ScrollWindow __instance, bool __state)
        {
            if (!__state) return;
            var built = _laying;
            foreach (var group in Groups)
            {
                if (!Lays(group, built) || group.Items.Count == 0) continue;
                try { CheckLaidOut(group, __instance); }
                catch (Exception error) { Plugin.Error("ui-checking-games-settings-page-failed", "Checking " + group.Owner + " on the game's " + group.Page + " settings page failed: " + error); }
            }
        }

        private static bool Lays(PageRows group, (PageBuilder Builder, eSettingsSubMenuId Page, CM_PageSettings? Settings, IntPtr List) built)
            => group.Builder == built.Builder && group.Page == built.Page && group.Takes(built.Settings, built.List);
    }

    /// <summary>The copied rows keep the titles their group writes.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_SettingsItem), nameof(CM_SettingsItem.UpdateText))]
    private static class RowSkip
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_SettingsItem __instance) => !Copies.Contains(__instance.Pointer);
    }

    /// <summary>The game's reset of a page also resets what the groups on it hold.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.ResetPageSettings))]
    private static class PageReset
    {
        [HarmonyPostfix]
        private static void Postfix(eSettingsSubMenuId settingsMenuId)
        {
            foreach (var group in Groups)
            {
                if (group.Page != settingsMenuId) continue;
                try { group.PageReset(); }
                catch (Exception error) { Plugin.Error("ui-resetting-failed", "Resetting " + group.Owner + " failed: " + error); }
            }
        }
    }
}
