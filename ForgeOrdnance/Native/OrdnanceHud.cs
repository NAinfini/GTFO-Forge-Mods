using System.Collections.Generic;
using ForgeUI;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// The call-in list on the player HUD: ForgeUI's command list, fed every frame with what the session knows. ForgeUI draws it at the
/// right edge of the screen, middle height, in three forms: idle (one row per call-in with its uses or cooldown), entering a code (the
/// code's key cells, typed steps lit) and holding the ball (the armed call-in and THROW), with the cancel / throw hint and the session's
/// notice. This class only turns the loadout, cooldown book, category colours and controller state into the list's data, and keeps the
/// summoning ball out of the game's weapon list (it is not an item the player carries).
/// </summary>
internal sealed class OrdnanceHud
{
    private readonly HudCommandList? _list;
    private readonly Dictionary<OrdnanceDefinition, CommandItem> _items = new(ReferenceEqualityComparer.Instance);
    /// <summary>The rows handed to ForgeUI. ForgeUI measures a list (longest code, name widths) once per list object, so every new
    /// loadout gets a new list; refilling the old one left the previous loadout's code width on screen.</summary>
    private List<CommandItem> _shown = new();
    private readonly List<ArrowDirection> _typed = new();
    private IReadOnlyList<OrdnanceDefinition>? _shownFor;
    private OrdnancePhase _hintPhase = (OrdnancePhase)(-1);
    private bool _hidden;
    private int _typedShown = -1;
    private OrdnanceDefinition? _heldShown;
    private float _nextShow;

    /// <summary>How long the call-in list shows a notice.</summary>
    private const float NoticeSeconds = 3.5f;

    /// <summary>Cooldown seconds and bars are refreshed at this rate while nothing else changed.</summary>
    private const float RefreshSeconds = 1f / 30f;

    internal OrdnanceHud(OrdnanceController controller)
    {
        _list = UIKit.Mod(Plugin.Id).AddCommandList("CallIns", UIText.Key("hud.title"), UIText.Key("hud.ready"), UIText.Key("hud.throw"),
            Messages.MaxLoadout, DefinitionParser.MaxCodeLength);
        UIKit.HideInWeaponList(BeaconItem.BlockId);
        UIKit.LanguageChanged += LanguageChanged;
        controller.NoticePosted += text => _list?.Notify(text, NoticeSeconds);
    }

    /// <summary>The call-in names and hint words are made again in the new language on the next update.</summary>
    private void LanguageChanged()
    {
        _items.Clear();
        _shownFor = null;
        _hintPhase = (OrdnancePhase)(-1);
    }

    /// <summary>ForgeUI's arrow for a code step.</summary>
    internal static ArrowDirection Arrow(Direction direction) => direction switch
    {
        Direction.Up => ArrowDirection.Up,
        Direction.Down => ArrowDirection.Down,
        Direction.Left => ArrowDirection.Left,
        _ => ArrowDirection.Right
    };

    internal void Update(OrdnanceController controller, OrdnanceSession session)
    {
        if (_list == null) return;
        if (!session.InLevel || session.CallMenuOff)
        {
            if (!_hidden) _list.Hide();
            _hidden = true;
            return;
        }
        var loadout = session.LocalLoadout();
        var phase = controller.CurrentPhase;
        var typed = controller.Entered;
        var held = controller.Held;
        // A change of phase, loadout, typed code or held call-in is shown at once. Otherwise only cooldowns move: not at all while
        // the ball is in hand (they are not shown then), else at a fixed rate.
        var changed = _hidden || phase != _hintPhase || !ReferenceEquals(loadout, _shownFor) || typed.Count != _typedShown
                      || !ReferenceEquals(held, _heldShown);
        var now = Time.time;
        if (!changed && (phase == OrdnancePhase.Holding || now < _nextShow)) return;
        _hidden = false;
        _nextShow = now + RefreshSeconds;
        _typedShown = typed.Count;
        _heldShown = held;
        if (phase != _hintPhase) SetHint(phase);
        if (!ReferenceEquals(loadout, _shownFor))
        {
            _shownFor = loadout;
            _shown = new List<CommandItem>(loadout.Count);
            foreach (var definition in loadout) _shown.Add(ItemOf(definition));
        }
        if (phase != OrdnancePhase.Holding)
        {
            double? multiplier = null;
            for (var i = 0; i < loadout.Count; i++) Refresh(_shown[i], session, loadout[i], ref multiplier);
        }
        _typed.Clear();
        for (var i = 0; i < typed.Count; i++) _typed.Add(Arrow(typed[i]));
        _list.Show(phase switch
        {
            OrdnancePhase.Entering => CommandListMode.Entering,
            OrdnancePhase.Holding => CommandListMode.Holding,
            _ => CommandListMode.Idle
        }, _shown, _typed, held == null ? null : ItemOf(held));
    }

    /// <summary>The hint words for <paramref name="phase"/> from the player's current bindings; read once per phase change.</summary>
    private void SetHint(OrdnancePhase phase)
    {
        _hintPhase = phase;
        var summon = OrdnanceKeys.Summon.Name;
        switch (phase)
        {
            case OrdnancePhase.Entering:
                _list!.SetHint(HintPart.Word(UIText.Key("hud.release")), HintPart.Key(summon), HintPart.Word(UIText.Key("hud.cancel")));
                break;
            case OrdnancePhase.Holding:
                _list!.SetHint(HintPart.Key(UIKitKeys.Binding(InputAction.Fire, "Fire")), HintPart.Word(UIText.Key("hud.throwWord")), HintPart.Key(summon, wide: true), HintPart.Word(UIText.Key("hud.putAway")));
                break;
            default:
                _list!.SetHint();
                break;
        }
    }

    private CommandItem ItemOf(OrdnanceDefinition definition)
    {
        if (_items.TryGetValue(definition, out var item)) return item;
        var code = new ArrowDirection[definition.Code.Count];
        for (var i = 0; i < code.Length; i++) code[i] = Arrow(definition.Code[i]);
        item = new CommandItem(OrdnanceContent.Name(definition), code, ForgeCore.Colours.FromRgb(definition.CategoryBeamRgb));
        _items.Add(definition, item);
        return item;
    }

    /// <summary>Copies the session's uses and cooldown of <paramref name="definition"/> onto its list item.</summary>
    private static void Refresh(CommandItem item, OrdnanceSession session, OrdnanceDefinition definition, ref double? multiplier)
    {
        if (!session.Book.TryGet(OrdnanceSession.Local, definition.Key, Time.time, out var uses, out var cooldown))
        {
            item.Uses = 0;
            item.SetCooldown(0, 0);
            return;
        }
        item.Uses = uses;
        if (cooldown > 0 && multiplier == null && LobbyRules.TryCurrent(out var rules, out _)) multiplier = rules.CooldownMultiplier;
        // Without the lobby's rules the full length is unknown: the seconds still show, the bar stays empty.
        item.SetCooldown(cooldown, multiplier == null ? 0 : definition.Cooldown * multiplier.Value);
    }
}
