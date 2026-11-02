using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CellMenu;
using ForgeUI;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The lobby ORDNANCE button and the loadout picker behind it, both shown by ForgeUI: the slots on the left, the call-ins
/// allowed by the level's rules in the list (ALL plus up to four categories that have call-ins), and the focused call-in's
/// details with its code. A call-in whose code begins (or is begun by) a code already in another slot is refused on the spot.
/// Choices are saved at once (<see cref="LoadoutStore"/>).
/// </summary>
internal sealed class OrdnanceLoadout : PickerSource
{
    private static readonly OrdnanceLoadout Instance = new();

    private int _size;
    private OrdnanceLevelRules _rules = new();
    private List<OrdnanceDefinition> _available = new();
    /// <summary>Per tab: the category it lists, or null for ALL.</summary>
    private readonly List<string?> _tabs = new();
    private readonly List<string> _tabNames = new();
    private readonly List<Func<string>> _tabNameOf = new();

    internal static void Register()
    {
        LobbyRules.Changed += Instance.RulesChanged;
        UIKit.Mod(Plugin.Id).AddLobbyButton("Loadout", UIText.Key("lobby.button"), bar => Instance.Toggle(bar), Note,
            bar => bar.m_player != null && bar.m_player.IsLocal && !CallMenuOff);
    }

    /// <summary>The selected level's rules turned the players' call-ins off: there is nothing to pick, so the ORDNANCE button and the
    /// lobby row are not shown at all.</summary>
    internal static bool CallMenuOff => LobbyRules.TryCurrent(out var rules, out _) && !rules.CallMenu;

    private static string Note()
    {
        if (!LobbyRules.TryCurrent(out var rules, out _)) return OrdnanceText.T("lobby.wait");
        return LoadoutStore.Slots(rules.LoadoutSize).Count(s => s != null && rules.IsAllowed(s.Id)) + "/" + rules.LoadoutSize;
    }

    private void Toggle(CM_PlayerLobbyBar bar)
    {
        OrdnanceCues.Local(OrdnanceSound.Click);
        if (!UIKit.IsShowing(this))
        {
            if (!LobbyRules.TryCurrent(out var rules, out _))
            {
                return;
            }
            Load(rules);
        }
        UIKit.TogglePicker(bar, this);
    }

    /// <summary>Reads the rules and definitions fresh each time the picker opens.</summary>
    private void Load(OrdnanceLevelRules rules)
    {
        _rules = rules;
        _size = rules.LoadoutSize;
        _available = OrdnanceContent.Set.Definitions.Where(d => d.Callable && rules.IsAllowed(d.Id)).ToList();
        _tabs.Clear();
        _tabNameOf.Clear();
        _tabs.Add(null);
        _tabNameOf.Add(() => OrdnanceText.T("picker.all"));
        foreach (var category in OrdnanceApi.Categories.Ordered)
        {
            if (_tabs.Count == 5) break; // the window has five tabs; the rest appear under ALL
            if (!_available.Any(d => d.Category == category.Id)) continue;
            _tabs.Add(category.Id);
            _tabNameOf.Add(category.NameOf);
        }
    }

    private void RulesChanged()
    {
        if (!UIKit.IsShowing(this)) return;
        if (!LobbyRules.TryCurrent(out var rules, out _) || !rules.CallMenu) { UIKit.ClosePicker(); return; }
        Load(rules);
        UIKit.RefreshPicker();
    }

    public override int SlotCount => _size;

    /// <summary>The tab names in the current language.</summary>
    public override IReadOnlyList<string> Tabs
    {
        get
        {
            _tabNames.Clear();
            foreach (var name in _tabNameOf) _tabNames.Add(name());
            return _tabNames;
        }
    }

    public override int FirstSlot()
    {
        for (var i = 0; i < _size; i++) if (LoadoutStore.Get(i) == null) return i;
        return 0;
    }

    public override int SlotAfterPick(int slot)
    {
        for (var i = 1; i <= _size; i++)
        {
            var next = (slot + i) % _size;
            if (LoadoutStore.Get(next) == null) return next;
        }
        return slot;
    }

    /// <summary>The definition's own picture (its model preview PNG) when it has one, else none; ForgeUI loads and keeps it.</summary>
    private static Sprite? Icon(OrdnanceDefinition definition) => OrdnanceContent.TryGetIconPath(definition, out var path) ? UIKit.LoadIcon(path) : null;

    private List<OrdnanceDefinition> InTab(int tab) =>
        tab <= 0 || tab >= _tabs.Count ? _available : _available.Where(d => d.Category == _tabs[tab]).ToList();

    /// <summary>A category's colour (its default beam colour): the category mark and text on a row, a slot's edge, the glow behind the
    /// large picture. Pictures keep their own colours.</summary>
    private static Color Accent(OrdnanceDefinition definition)
    {
        var rgb = definition.CategoryBeamRgb;
        return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }

    public override PickerCard SlotCard(int slot, bool chosen)
    {
        var definition = LoadoutStore.Get(slot);
        return new PickerCard
        {
            Title = definition == null ? OrdnanceText.T("picker.empty") : OrdnanceContent.Name(definition).ToUpperInvariant(),
            Accent = definition == null ? null : Accent(definition),
            Icon = definition == null ? null : Icon(definition)
        };
    }

    public override IReadOnlyList<PickerCard> Entries(int tab)
    {
        var slots = LoadoutStore.Slots(_size);
        return InTab(tab).Select(d => new PickerCard
        {
            Subtitle = OrdnanceContent.CategoryName(d),
            Title = OrdnanceContent.Name(d).ToUpperInvariant(),
            Accent = Accent(d),
            Equipped = HeldIn(slots, d) + 1,
            Icon = Icon(d)
        }).ToList();
    }

    private static int HeldIn(IReadOnlyList<OrdnanceDefinition?> slots, OrdnanceDefinition definition)
    {
        for (var i = 0; i < slots.Count; i++)
            if (slots[i] != null && slots[i]!.Key == definition.Key) return i;
        return -1;
    }

    public override PickerInfo Info(int slot, int tab, int entry)
    {
        var list = InTab(tab);
        var definition = entry >= 0 && entry < list.Count ? list[entry] : LoadoutStore.Get(slot);
        if (definition == null) return new PickerInfo();
        var description = OrdnanceContent.Description(definition);
        var seconds = OrdnanceText.T("picker.unit.seconds");
        var cooldown = definition.Cooldown * _rules.CooldownMultiplier;
        var longestCooldown = _available.Count == 0 ? 0.0 : _available.Max(d => d.Cooldown) * _rules.CooldownMultiplier;
        var longestDelay = _available.Count == 0 ? 0.0 : _available.Max(d => d.CallInDelay);
        var mostUses = _available.Where(d => !d.Unlimited).Select(d => d.Uses).DefaultIfEmpty(0).Max();
        var uses = definition.Unlimited
            ? PickerReadout.Unlimited(OrdnanceText.T("picker.uses"))
            : new PickerReadout(OrdnanceText.T("picker.uses"), Math.Floor(definition.Uses * _rules.UsesMultiplier + 1e-9).ToString(CultureInfo.InvariantCulture), "",
                Share(definition.Uses, mostUses));
        return new PickerInfo
        {
            Title = OrdnanceContent.Name(definition).ToUpperInvariant(),
            Subtitle = OrdnanceContent.CategoryName(definition),
            Accent = Accent(definition),
            Body = description.ToUpperInvariant(),
            Icon = Icon(definition),
            Badges = definition.Code.Select(d => UIKitSprites.ArrowGlyph(OrdnanceHud.Arrow(d))).ToList(),
            Stats = definition.Stats.Select(s => new PickerStat(s.Name.ToUpperInvariant(), s.Value, s.Max)).ToList(),
            Readouts = new[]
            {
                new PickerReadout(OrdnanceText.T("picker.cooldown"), Number(cooldown), seconds, Share(cooldown, longestCooldown)),
                uses,
                new PickerReadout(OrdnanceText.T("picker.arrives"), Number(definition.CallInDelay), seconds, Share(definition.CallInDelay, longestDelay))
            }
        };
    }

    /// <summary>A value against the largest of its kind among the call-ins offered (the readout's bar).</summary>
    private static float Share(double value, double largest) => largest <= 0.0 ? 0f : (float)Math.Clamp(value / largest, 0.0, 1.0);

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    public override string? Pick(int slot, int tab, int entry)
    {
        var list = InTab(tab);
        var definition = entry >= 0 && entry < list.Count ? list[entry] : null;
        if (definition != null)
        {
            var clash = LoadoutValidator.CodeConflict(LoadoutStore.Slots(_size), slot, definition);
            if (clash != null) return OrdnanceText.T("picker.clash", OrdnanceContent.Name(clash).ToUpperInvariant());
        }
        LoadoutStore.Set(slot, definition);
        OrdnanceCues.Local(definition == null ? OrdnanceSound.PickClear : OrdnanceSound.PickConfirm);
        return null;
    }

    /// <summary>The game plays no click for the picker's own slots and cards; a slot gets its select sound, a card's sound comes
    /// with the pick (<see cref="Pick"/>).</summary>
    public override void Clicked(PickerClick click)
    {
        if (click == PickerClick.Slot) OrdnanceCues.Local(OrdnanceSound.SlotSelect);
    }
}
