using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CellMenu;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// The rows of <see cref="UIKitMod.AddSettings"/>, after the game's rows of one window of a settings page (<see cref="SettingsBlock"/>).
/// A choice is a copy of the window's dropdown row and a toggle of its on/off row; the game's own row code runs for them (popup, sounds,
/// hover, its On and Off), so they look and behave like the game's. Each copy's control gets a setting id of its own above the game's
/// (<see cref="FirstId"/>), the page itself as its receiver (as the game's setup gives it) and, for a dropdown, a value table and a
/// localisation callback of ours. The game's value calls never reach the game's settings for those ids: the page's receivers
/// <c>CM_PageSettings.SetIntValue</c> (a picked option) and <c>ToggleValue</c> (a press) answer from the row instead, so the game's save
/// and apply never see one. The two calls that would read the game's settings for a copy are skipped for the copies alone (decided per
/// instance): the dropdown's <c>UpdateValueWithSelected</c> (after a pick, and in its <c>ResetValue</c>) and the toggle's
/// <c>ResetValue</c>; both show the row's value instead. The popup's heading is the row's title (<c>CreateAndShowEnumPopup</c> writes the
/// game's title of the id first). Every hooked method is a native body of its own, reached by a call or through an interface. Values,
/// the enabled state and hints are read again every 0.5 s while the window shows (at once after a pick or a press); titles, options and
/// the game's On/Off again when the language changes.
/// <para>A row's hint shows in the game's own tooltip, the box <c>CM_PageBase.UpdateCursorRaycast</c> spawns beside the cursor for the
/// item under it when that item's <c>TooltipInfo</c> asks for one (the game's own settings rows get theirs in
/// <c>CM_SettingsItem.UpdateText</c>, from <c>CellSettingGlobals.m_tooltips</c>). The row alone carries the hint, as the game's own rows do (the control passes its hover on to the row). The game writes the tooltip's text only when it spawns the box, and keeps the box while the
/// cursor goes straight from one item with a tooltip to the next, so between neighbouring rows the first row's text would stay:
/// <see cref="FollowTooltip"/> writes the hovered row's hint into the box, and has the game spawn it again when the cursor left for a
/// row of the game's.</para>
/// </summary>
internal sealed class OptionRows : PageRows
{
    private sealed class Row
    {
        internal OptionRows Group = null!;
        internal SettingsRow Source = null!;
        internal CM_SettingsItem Item = null!;
        internal TMPro.TextMeshPro Title = null!;
        internal int Id;
        internal CM_SettingsEnumDropdownButton? Choice;
        internal TMPro.TextMeshPro? Value;
        internal Il2CppSystem.Collections.Generic.Dictionary<int, string>? Names;
        internal CM_SettingsToggleButton? Toggle;
        internal int Shown = int.MinValue;
        internal bool? ShownEnabled;
        /// <summary>The hint as shown, and what the game's tooltip is given for it; both null without one.</summary>
        internal string? Hint;
        internal TooltipInfo? Tip;
    }

    private const float RefreshInterval = .5f, DisabledAlpha = .3f;
    /// <summary>Where the game puts a hint's box: under the row, as for its own settings rows (<c>CM_SettingsItem.UpdateText</c>).
    /// <c>CM_Tooltip.MoveTooltip</c> parents the box to the item and offsets it by the item's size; <c>FollowMouse</c> instead sets the
    /// box's anchored position to the cursor's local position, which is no position in the page's tooltip parent (the box showed at the
    /// top left).</summary>
    private const TooltipPositionType HintPlace = TooltipPositionType.UnderElement;
    /// <summary>The first setting id a copied control holds: the game's own ids end below 2000.</summary>
    private const int FirstId = 1_000_000;
    private static int _nextId = FirstId;
    private static readonly Dictionary<int, Row> ById = new();
    private static readonly Dictionary<IntPtr, Row> ByControl = new();
    // Read every frame the window shows, straight from the game's memory: a property of these would be a call into the game each.
    private static readonly int TooltipField = ForgeCore.NativeObjects.FieldOffset<CM_PageBase>(nameof(CM_PageBase.m_tooltip)),
        HoveringField = ForgeCore.NativeObjects.FieldOffset<CM_Item>(nameof(CM_Item.m_isHovering)),
        NativeField = ForgeCore.NativeObjects.FieldOffset<Object>(nameof(Object.m_CachedPtr));

    private readonly UIKitMod _mod;
    private readonly SettingsBlock _block;
    private readonly IReadOnlyList<SettingsRow> _sources;
    private readonly List<Row> _rows = new();
    private readonly Action? _onReset;
    private CM_ScrollWindow? _window;
    private CM_PageSettings? _page;
    private int _titlesVersion = -1;
    private float _nextRefresh;
    private bool _showing, _hinted;
    // The tooltip box this group last wrote, and the row and text it wrote.
    private IntPtr _tooltip;
    private Row? _tooltipRow;
    private string? _tooltipText;

    internal OptionRows(UIKitMod mod, string id, SettingsBlock block, IReadOnlyList<SettingsRow> rows, Action? onReset)
    {
        _mod = mod;
        Id = id;
        _block = block;
        _sources = rows;
        _onReset = onReset;
    }

    /// <summary>One line per copied row as the game laid it out (<see cref="UIKitMod.SettingsReadback"/>). A row's extent and the
    /// window's are distances down the content, so a row is in view when the two overlap.</summary>
    internal string Readback()
    {
        if (_window == null || _window.WasCollected) return Owner + ": the game has not built the window yet";
        float top = Math.Abs(_window.m_posOffset), height = _window.m_windowHeight;
        var text = new System.Text.StringBuilder(Owner + ": window active=" + _window.gameObject.activeInHierarchy + " shows " + top + ".." + (top + height)
            + " of content " + _window.m_contentContainerHeight + ", " + _rows.Count + " of " + _sources.Count + " rows");
        foreach (var row in _rows)
        {
            var item = row.Item;
            text.Append("\n  ").Append(row.Source.GetType().Name);
            if (item == null || item.WasCollected) { text.Append(" exists=False"); continue; }
            var ends = item.PosYStartEnd;
            float start = Math.Min(Math.Abs(ends.x), Math.Abs(ends.y)), end = Math.Max(Math.Abs(ends.x), Math.Abs(ends.y));
            text.Append(" exists=True active=").Append(item.gameObject.activeSelf).Append(" shown=").Append(item.gameObject.activeInHierarchy)
                .Append(" y=").Append(start).Append("..").Append(end).Append(" height>0=").Append(end - start > 0f)
                .Append(" inView=").Append(end > top && start < top + height)
                .Append(" text=\"").Append(row.Title.text).Append("\" hint=\"").Append(row.Hint).Append('"');
        }
        return text.ToString();
    }

    internal string Id { get; }
    internal override PageBuilder Builder => PageBuilder.Submenu;
    internal override eSettingsSubMenuId Page => PageOf(_block);
    internal override string Owner => "the settings rows " + Id;

    /// <summary>The page <paramref name="block"/> is on.</summary>
    internal static eSettingsSubMenuId PageOf(SettingsBlock block) => block switch
    {
        SettingsBlock.AdvancedGraphics => eSettingsSubMenuId.Video,
        _ => throw new ArgumentOutOfRangeException(nameof(block)),
    };

    /// <summary>The window of the block: the one the game builds from the block's own list of settings. The page it is on is kept: its
    /// tooltip box is the one the rows' hints show in.</summary>
    internal override bool Takes(CM_PageSettings? settings, IntPtr list)
    {
        if (settings == null) return false;
        var own = _block switch
        {
            SettingsBlock.AdvancedGraphics => settings.m_settingsAdvancedGraphics,
            _ => null,
        };
        if (own == null || own.Pointer != list) return false;
        _page = settings;
        return true;
    }

    internal override void Update()
    {
        if (_window == null || _window.WasCollected || _rows.Count == 0) return;
        try
        {
            var now = ForgeCore.Frame.UnscaledTime;
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + RefreshInterval;
                _showing = _window.gameObject.activeInHierarchy;
                if (_showing)
                {
                    if (_titlesVersion != UITexts.Version) Retitle();
                    foreach (var row in _rows) Show(row, false);
                }
            }
            if (_showing && _hinted) FollowTooltip();
        }
        catch (Exception error) { Plugin.Error("ui-settings-rows-failed-update", Owner + " failed to update: " + error); }
    }

    /// <summary>Keeps the game's tooltip box on the hint of the row under the cursor (the class summary says why the game does not).
    /// While no box shows this is two reads of the game's memory a frame, with one more per row while one does.</summary>
    private void FollowTooltip()
    {
        var tooltip = Marshal.ReadIntPtr(_page!.Pointer, TooltipField);
        if (tooltip == IntPtr.Zero || Marshal.ReadIntPtr(tooltip, NativeField) == IntPtr.Zero)
        {
            _tooltip = IntPtr.Zero;
            return;
        }
        Row? hovered = null;
        foreach (var row in _rows)
            if (row.Tip != null && Marshal.ReadByte(row.Item.Pointer, HoveringField) != 0) { hovered = row; break; }
        if (hovered == null)
        {
            // The cursor went from a row with a hint straight to an item that keeps the box (a row of the game's with a tooltip):
            // destroyed, the box is spawned again by the game with that item's text.
            if (_tooltip == tooltip) Object.Destroy(new CM_Tooltip(tooltip).gameObject);
            _tooltip = IntPtr.Zero;
            return;
        }
        if (_tooltip == tooltip && _tooltipRow == hovered && _tooltipText == hovered.Hint) return;
        new CM_Tooltip(tooltip).SetTooltip(hovered.Tip);
        (_tooltip, _tooltipRow, _tooltipText) = (tooltip, hovered, hovered.Hint);
    }

    internal override void PageReset()
    {
        _onReset?.Invoke();
        foreach (var row in _rows) Show(row, true);
    }

    internal override void Append(CM_ScrollWindow window, Il2CppSystem.Collections.Generic.List<iScrollWindowContent> content)
    {
        Release();
        CM_SettingsItem? choiceRow = null, toggleRow = null;
        CM_SettingsEnumDropdownButton? choice = null;
        CM_SettingsToggleButton? toggle = null;
        for (var i = 0; i < content.Count; i++)
        {
            var row = content[i]?.TryCast<CM_SettingsItem>();
            if (row == null) continue;
            if (choice == null && row.GetComponentInChildren<CM_SettingsEnumDropdownButton>(true) is { } dropdown && dropdown != null) (choiceRow, choice) = (row, dropdown);
            else if (toggle == null && row.GetComponentInChildren<CM_SettingsToggleButton>(true) is { } button && button != null) (toggleRow, toggle) = (row, button);
        }
        var anyRow = toggleRow ?? choiceRow;
        if (anyRow == null)
        {
            Plugin.Error("ui-settings-block-has-no-row", "The game's " + _block + " settings have no dropdown or on/off row to copy; " + Owner + " are not added.");
            return;
        }
        _window = window;
        SettingsPages.CopyRows(this, anyRow, "ForgeUI.OptionRows." + Id, content, holder =>
        {
            foreach (var source in _sources)
            {
                var row = source switch
                {
                    SettingsChoice when choice != null => CopyChoice(choiceRow!, choice, holder, source),
                    SettingsToggle when toggle != null => CopyToggle(toggleRow!, toggle, holder, source),
                    _ => null,
                };
                if (row == null)
                {
                    Plugin.Error("ui-settings-block-has-no-row", "The game's " + _block + " settings have no " + (source is SettingsChoice ? "dropdown" : "on/off") + " row to copy; a row of " + Owner + " is not added.");
                    continue;
                }
                _rows.Add(row);
                _hinted |= source is SettingsChoice { Hint: not null } or SettingsToggle { Hint: not null };
            }
            var items = new List<CM_SettingsItem>();
            foreach (var row in _rows) items.Add(row.Item);
            return items;
        });
        Retitle();
    }

    private Row CopyChoice(CM_SettingsItem template, CM_SettingsEnumDropdownButton game, Transform holder, SettingsRow source)
    {
        var item = Copy(template, holder, "Choice" + _rows.Count);
        var control = item.GetComponentInChildren<CM_SettingsEnumDropdownButton>(true);
        var row = new Row { Group = this, Source = source, Item = item, Title = item.m_title, Id = _nextId++, Choice = control, Value = control.m_currentValue, Names = new() };
        // What the game's Setup gives its dropdown, with this row's id, value table and localisation callback.
        control.m_basePage = game.m_basePage;
        control.m_popupWindow = game.m_popupWindow;
        control.m_receiver = game.m_receiver;
        control.m_settingId = (eCellSettingID)row.Id;
        control.m_valuesAndName = row.Names;
        control.m_getLocalizations = (Action)(() => Run(row, r => r.Group.Name(r)));
        if (game.m_hasParentCMItem) control.ParentCMItem = new iCellMenuCursorItem(item.Pointer);
        if (game.ID != 0) control.ID = CM_PageBase.NextCellItemID();
        control.m_isSetup = true;
        ById[row.Id] = row;
        ByControl[control.Pointer] = row;
        return row;
    }

    private Row CopyToggle(CM_SettingsItem template, CM_SettingsToggleButton game, Transform holder, SettingsRow source)
    {
        var item = Copy(template, holder, "Toggle" + _rows.Count);
        var control = item.GetComponentInChildren<CM_SettingsToggleButton>(true);
        var row = new Row { Group = this, Source = source, Item = item, Title = item.m_title, Id = _nextId++, Toggle = control };
        // What the game's SetupInputItem gives its toggle, with this row's id.
        control.m_root = control.transform;
        control.m_settingId = (eCellSettingID)row.Id;
        control.m_receiver = game.m_receiver;
        if (game.m_hasParentCMItem) control.ParentCMItem = new iCellMenuCursorItem(item.Pointer);
        if (game.ID != 0) control.ID = CM_PageBase.NextCellItemID();
        ById[row.Id] = row;
        ByControl[control.Pointer] = row;
        return row;
    }

    private CM_SettingsItem Copy(CM_SettingsItem template, Transform holder, string name)
    {
        var go = Object.Instantiate(template.gameObject, holder);
        go.name = "ForgeUI.Option." + Id + "." + name;
        // The game's localized-text components would write the game's own title back over ours.
        NativeUi.StripLocalization(go);
        var item = go.GetComponent<CM_SettingsItem>();
        SettingsPages.Copies.Add(item.Pointer);
        return item;
    }

    /// <summary>Takes the rows of an earlier build out of the window's list and destroys them.</summary>
    private void Release()
    {
        var items = new List<CM_SettingsItem>();
        foreach (var row in _rows)
        {
            ById.Remove(row.Id);
            Component? control = row.Choice != null ? row.Choice : row.Toggle;
            if (control != null && !control.WasCollected) ByControl.Remove(control.Pointer);
            items.Add(row.Item);
        }
        SettingsPages.Drop(_window, items);
        _rows.Clear();
        _window = null;
        _hinted = false;
        (_tooltip, _tooltipRow) = (IntPtr.Zero, null);
    }

    /// <summary>Titles, option names and the game's On/Off in the current language.</summary>
    private void Retitle()
    {
        _titlesVersion = UITexts.Version;
        foreach (var row in _rows)
        {
            switch (row.Source)
            {
                case SettingsChoice choice: NativeUi.SetText(row.Title, choice.Title.In(_mod.Texts)); Name(row); break;
                case SettingsToggle toggle: NativeUi.SetText(row.Title, toggle.Title.In(_mod.Texts)); break;
            }
            Show(row, true);
        }
    }

    /// <summary>The dropdown's value table (index to option name), which its popup lists.</summary>
    private void Name(Row row)
    {
        if (row.Names == null || row.Source is not SettingsChoice choice) return;
        row.Names.Clear();
        for (var i = 0; i < choice.Options.Count; i++) row.Names.Add(i, choice.Options[i].In(_mod.Texts));
    }

    /// <summary>The row's value, enabled state and hint as its source has them; only what changed is written.</summary>
    private void Show(Row row, bool all)
    {
        switch (row.Source)
        {
            case SettingsChoice choice:
                {
                    var value = choice.Get();
                    if (all || value != row.Shown)
                    {
                        row.Shown = value;
                        if ((uint)value < (uint)choice.Options.Count) NativeUi.SetText(row.Value!, choice.Options[value].In(_mod.Texts));
                        else
                        {
                            Plugin.Error("ui-settings-choice-value-out-of-range", Owner + ": " + choice.Title.In(_mod.Texts) + " has the value " + value + ", which is none of its " + choice.Options.Count + " options; it shows no option.");
                            NativeUi.SetText(row.Value!, "");
                        }
                    }
                    var enabled = choice.Enabled?.Invoke() ?? true;
                    if (all || enabled != row.ShownEnabled)
                    {
                        row.ShownEnabled = enabled;
                        row.Choice!.SetEnabled(enabled);
                    }
                    break;
                }
            case SettingsToggle toggle:
                {
                    var value = toggle.Get() ? 1 : 0;
                    var enabled = toggle.Enabled?.Invoke() ?? true;
                    if (!all && value == row.Shown && enabled == row.ShownEnabled) break;
                    row.Shown = value;
                    row.ShownEnabled = enabled;
                    row.Toggle!.m_value = value == 1;
                    row.Toggle.OnValueUpdate();
                    // The game's toggle has no disabled look of its own: its On/Off is dimmed as the game dims a disabled dropdown's value.
                    if (row.Toggle.m_text != null) row.Toggle.m_text.alpha = enabled ? 1f : DisabledAlpha;
                    break;
                }
        }
        ShowHint(row, all);
    }

    /// <summary>The row's hint as its source has it now, given to the row as what the game shows in its tooltip (the control hands its
    /// hover to the row through <c>ParentCMItem</c>, and only the row carries a tooltip, as the game's own rows do). A row without one is given an empty tooltip: the game's row it was copied from may carry
    /// the tooltip of its own setting.</summary>
    private void ShowHint(Row row, bool all)
    {
        var source = row.Source switch { SettingsChoice choice => choice.Hint, SettingsToggle toggle => toggle.Hint, _ => null };
        var hint = source?.Invoke()?.In(_mod.Texts);
        if (hint is "") hint = null;
        if (!all && hint == row.Hint) return;
        row.Hint = hint;
        // Zeroed: the interop's new TooltipInfo() leaves the header garbage, and the game's tooltip writes it as text.
        row.Tip = hint == null ? null : new TooltipInfo(ForgeCore.NativeObjects.Zeroed<TooltipInfo>()) { UseTooptip = true, PositionType = HintPlace, TooltipText = hint };
        row.Item.TooltipInfo = row.Tip ?? new TooltipInfo(ForgeCore.NativeObjects.Zeroed<TooltipInfo>());
    }

    private static void Run(Row row, Action<Row> action)
    {
        try { action(row); }
        catch (Exception error) { Plugin.Error("ui-settings-row-failed", row.Group.Owner + " failed: " + error); }
    }

    /// <summary>A picked option of a copied dropdown: the row's <see cref="SettingsChoice.Set"/> instead of the game's setting.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.SetIntValue))]
    private static class PickValue
    {
        [HarmonyPrefix]
        private static bool Prefix(eCellSettingID setting, int value, ref int __result)
        {
            if (!ById.TryGetValue((int)setting, out var row) || row.Source is not SettingsChoice choice) return true;
            __result = value;
            Run(row, _ => choice.Set(value));
            row.Group._nextRefresh = 0f; // the other rows' state and hints may follow this value
            return false;
        }
    }

    /// <summary>A press on a copied toggle: the row's value turned over through <see cref="SettingsToggle.Set"/> instead of the game's setting.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_PageSettings), nameof(CM_PageSettings.ToggleValue))]
    private static class PressToggle
    {
        [HarmonyPrefix]
        private static bool Prefix(eCellSettingID setting, ref bool __result)
        {
            if (!ById.TryGetValue((int)setting, out var row) || row.Source is not SettingsToggle toggle) return true;
            var on = false;
            Run(row, r =>
            {
                on = toggle.Get();
                // The game writes its On/Off again after this call: a disabled row is dimmed again at the next refresh.
                r.ShownEnabled = null;
                if (toggle.Enabled?.Invoke() == false) return;
                toggle.Set(on = !on);
                r.Shown = on ? 1 : 0;
            });
            row.Group._nextRefresh = 0f; // this row's hint and the other rows' state may follow this value
            __result = on;
            return false;
        }
    }

    /// <summary>A copied control shows its row's value: the game would read its own setting of the copy's id.</summary>
    private static bool ShowOwn(IntPtr control)
    {
        if (!ByControl.TryGetValue(control, out var row)) return true;
        Run(row, r => r.Group.Show(r, true));
        return false;
    }

    /// <summary>The dropdown's <c>UpdateValueWithSelected</c> (after a pick, and in its <c>ResetValue</c>).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_SettingsEnumDropdownButton), nameof(CM_SettingsEnumDropdownButton.UpdateValueWithSelected))]
    private static class ChoiceShow
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_SettingsEnumDropdownButton __instance) => ShowOwn(__instance.Pointer);
    }

    /// <summary>The toggle's <c>ResetValue</c> (the page's reset).</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_SettingsToggleButton), nameof(CM_SettingsToggleButton.ResetValue))]
    private static class ToggleShow
    {
        [HarmonyPrefix]
        private static bool Prefix(CM_SettingsToggleButton __instance) => ShowOwn(__instance.Pointer);
    }

    /// <summary>A copied dropdown's popup is headed by the row's title.</summary>
    [PatchGroup(PatchGroup.Settings)]
    [HarmonyPatch(typeof(CM_SettingsEnumDropdownButton), nameof(CM_SettingsEnumDropdownButton.CreateAndShowEnumPopup))]
    private static class PopupTitle
    {
        [HarmonyPostfix]
        private static void Postfix(CM_SettingsEnumDropdownButton __instance)
        {
            if (!ByControl.TryGetValue(__instance.Pointer, out var row) || row.Source is not SettingsChoice choice) return;
            Run(row, r => r.Choice!.m_popupWindow?.SetHeader(choice.Title.In(r.Group._mod.Texts)));
        }
    }
}
