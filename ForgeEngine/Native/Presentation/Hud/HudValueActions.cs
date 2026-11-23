using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeUI;
using Localization;
using UnityEngine;
using RowStatus = ForgeEngine.Framework.Status;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// `forge.action.presentation.hud_value`: one value drawn on the addressed client's own screen, at the placement
/// and for the audience the author picked. Everything here is local by construction, so this class writes no world
/// state, publishes no fact and answers with the tier's own non-committing result.
///
/// `top_center` borrows the objective timer while the game is not using it, `status_bar` writes the game's own
/// shield readout beside the health bar (both through the library's screen-text slots, <see cref="ScreenTimer"/>
/// and <see cref="ScreenStatus"/>), and `teammate_overhead` asks the service owned by the player provider to write
/// under a teammate's name marker. This provider never resolves or touches that marker. `screen` is the one placement
/// the game has no piece for: a library <see cref="HudPlaced"/> at the place the author chose, one per element name,
/// which the cards naming that element show, update and hide.
///
/// `audience=self` means "the player this value belongs to, and nobody else", so this machine draws only
/// when the player sitting here is one of the named viewers. `self` with
/// `teammate_overhead` is refused by name: the local player has no overhead marker.
///
/// Every readout this class writes is owned by it: hidden when the plan hides it and released when the world it
/// belonged to is replaced. The world epoch, read from every command and from every language tick, decides the
/// last case.
/// </summary>
internal sealed class HudValueActions : IDisposable
{
    internal const int MaximumLabelLength = 64;
    /// <summary>The body line of the two-line top readout.</summary>
    internal const int MaximumTextLength = 200;
    /// <summary>A `screen` element's text may run over several lines.</summary>
    internal const int MaximumScreenLabelLength = 1024;
    /// <summary>The message of the exception a lost status readout raises; its row answers `hud-write-exception`.</summary>
    private const string OwnershipLost = "hud-status-readout-ownership-lost";

    /// <summary>What a readout was last written from, so a language change can write it again.</summary>
    private class Readout
    {
        internal string Form = "number", LabelReference = "", TextReference = "";
        internal double Value, Maximum;
        internal bool Localized;

        internal void Capture(string form, double value, double maximum, string? reference, bool localized)
        {
            Form = form; Value = value; Maximum = maximum; LabelReference = reference ?? ""; Localized = localized;
        }
    }

    /// <summary>Ownership of the timer for showing is the command's installed-plan/node record, never the timer's title or
    /// text. Reference comparison needs neither string matching nor a dictionary on writes. Hiding is not tied to it.</summary>
    private sealed class TimerReadout(CommandContext context) : Readout
    {
        private readonly string _plan = context.PlanId, _node = context.NodeId;
        internal bool SameOrigin(CommandContext other)
            => ReferenceEquals(_plan, other.PlanId) && ReferenceEquals(_node, other.NodeId);
    }

    /// <summary>One author-named element of the `screen` placement.</summary>
    private sealed class ScreenReadout(HudPlaced placed) : Readout
    {
        internal readonly HudPlaced Placed = placed;
    }

    private sealed class OverheadReadout(EntityReference viewer) : Readout
    {
        internal readonly EntityReference Viewer = viewer;
        internal Color? Color;
    }

    private readonly IForgeEngine _kernel;
    private readonly HudLanguage _language;
    private readonly IPlayerDirectory _players;
    private readonly Action<string> _report;
    private readonly ScreenTimer _timerSlot = new();
    private readonly ScreenStatus _statusSlot = new();
    private readonly ScreenProgress _progressSlot = new();
    private readonly List<OverheadReadout> _overheads = new();
    private readonly Dictionary<string, ScreenReadout> _screens = new(StringComparer.Ordinal);
    private int _screenCount;
    /// <summary>How long ago the command being replayed for a recovered viewer was issued; 0 for a live command.</summary>
    private double _elapsed;
    private long _epoch;
    private bool _epochKnown;
    private TimerReadout? _timer, _progress;
    private Readout? _status;
    private bool _overheadWritten;
    private bool _hasLocalizedReadout;
    private bool _languageKnown;
    private Language _lastLanguage;
    private string? _languageFailure;
    /// <summary>A localized status readout the game took back: the next write is refused as a lost ownership.</summary>
    private bool _statusLost;
    private bool _disposed;

    internal HudValueActions(IForgeEngine kernel, HudLanguage language, IPlayerDirectory players, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The command, dispatched by the kernel on every addressed machine. `audience=team` draws for every
    /// viewer it reached; `audience=self` draws only when the player sitting at this machine is one of the named
    /// viewers.</summary>
    internal CommandResult Handle(CommandContext context)
    {
        if (_disposed) return Outcome.Rejected(ResultCode.HudWriteException);
        DropForNewWorld(context.WorldEpoch);
        if (!PresentationAudience.TryCheck(context, PresentationContract.HudValueViewers, out var refusal)) return refusal;
        string? form = context.Text(PresentationContract.HudValueForm);
        if (form == null || Array.IndexOf(PresentationContract.HudForms, form) < 0) return Outcome.Rejected(ResultCode.HudFormUnsupported);
        // `text` is the two-line top readout and carries no value; every other form needs one.
        double value = 0;
        if (form != "text")
        {
            if (context.Number(PresentationContract.HudValueValue) is not { } given || !double.IsFinite(given))
                return Outcome.Rejected(ResultCode.HudValueRequired);
            value = given;
        }
        if (context.Boolean(PresentationContract.HudValueVisible) is not { } visible)
            return Outcome.Rejected(ResultCode.HudVisibleRequired);
        string? labelReference = context.Text(PresentationContract.HudValueLabel);
        string? textReference = form == "text" ? context.Text(PresentationContract.HudValueText) : null;
        double maximum = context.Number(PresentationContract.HudValueMaximum) ?? 0;

        string? placement = context.Text(PresentationContract.HudValuePlacement);
        if (placement == null || Array.IndexOf(PresentationContract.HudPlacements, placement) < 0)
            return Outcome.Rejected(ResultCode.HudPlacementUnsupported);
        // `bar` fills the shield bar or the interaction line's bar; `text` is the top readout's; the interaction
        // line carries nothing but a bar.
        bool screen = placement == "screen";
        if (form == "bar" && placement is not ("status_bar" or "interaction" or "screen")) return Outcome.Rejected(ResultCode.HudFormUnsupported);
        if (form == "text" && placement is not ("top_center" or "screen")) return Outcome.Rejected(ResultCode.HudFormUnsupported);
        if (form == "segments" && !screen) return Outcome.Rejected(ResultCode.HudFormUnsupported);
        if (placement == "interaction" && form != "bar") return Outcome.Rejected(ResultCode.HudFormUnsupported);
        // The interaction line writes its text as given; the other two forms' labels may name a stored text.
        bool interaction = placement == "interaction";
        string? label = labelReference, body = textReference;
        bool useLabel = visible && (form != "bar" || interaction || screen);
        int labelLimit = screen ? MaximumScreenLabelLength : MaximumLabelLength;
        bool localizedLabel = useLabel && !interaction && ForgeUI.GameText.IsReference(labelReference);
        bool localizedBody = visible && ForgeUI.GameText.IsReference(textReference);
        if (useLabel && !TryRenderLabel(labelReference, labelLimit, out label, out _, out var labelCode))
            return Outcome.Rejected(labelCode);
        if (visible && form == "text" && !TryRenderText(textReference, out body, out var bodyCode))
            return Outcome.Rejected(bodyCode);
        if (label?.Length > labelLimit || body is { Length: > MaximumTextLength })
            return Outcome.Rejected(ResultCode.HudLabelTooLong);
        string? audience = context.Text(PresentationContract.HudValueAudience);
        if (audience == "self" && placement == "teammate_overhead") return Outcome.Rejected(ResultCode.HudSelfOverheadUnsupported);
        if (audience == "self")
        {
            // This value belongs to the viewers it names; the machine draws it only for the player sitting here.
            if (_players.LocalAgent is not { } local || _players.ReferenceOf(local) is not { } localViewer
                || !PresentationAudience.Names(context, PresentationContract.HudValueViewers, localViewer)) return Outcome.Rejected(ResultCode.HudAudienceNotAddressed);
        }
        else if (audience != "team") return Outcome.Rejected(ResultCode.HudAudienceUnsupported);
        if (!HudValueFormat.TryColor(context.Text(PresentationContract.HudValueColor), out var color))
            return Outcome.Rejected(ResultCode.HudColorInvalid);

        if (interaction && color != null) return Outcome.Rejected(ResultCode.HudColorUnsupported);
        if (!ScreenStatus.LayerPresent && (placement == "status_bar" || placement == "teammate_overhead"))
            return Outcome.Rejected(ResultCode.HudLayerUnavailable);
        if (placement == "status_bar" && visible && form != "bar" && !ScreenStatus.TextPresent)
            return Outcome.Rejected(ResultCode.HudLayerUnavailable);
        string line = HudValueFormat.Line(form, value, maximum, label);

        // The overhead placement answers per viewer and is not wrapped in the status-bar path's own catch: each
        // viewer's resolve-and-draw is guarded on its own, so a failure for one viewer can neither rewrite a row
        // an earlier viewer already earned nor claim a later one failed.
        if (placement == "teammate_overhead")
            return Overhead(context, form, value, maximum, labelReference, localizedLabel, line, color, visible);

        try
        {
            string? code = placement switch
            {
                "top_center" => TopCenter(context, form, value, maximum, labelReference, label, textReference, body,
                    localizedLabel || localizedBody, color, visible),
                "interaction" => Interaction(context, label, value, maximum, visible),
                "screen" => Screen(context, form, value, maximum, labelReference, label, textReference, body,
                    localizedLabel || localizedBody, color, visible),
                _ => null
            };
            if (code != null) return Outcome.Rejected(code);
            if (placement == "status_bar")
                Status(form, value, maximum, labelReference, label, localizedLabel, color, visible);
            // The local HUD is one readout on this machine: every addressed viewer is answered by the write that
            // just landed, so each gets the presented row its recipient is owed.
            return PresentationRows.Presented(context, ResultCode.HudValuePresented);
        }
        catch (Exception error)
        {
            Report("presentation.hud-value-failed: " + error.GetType().Name + ": " + error.Message);
            // The tier commits nothing and the failure is definite for every viewer, so each still gets a row.
            return PresentationRows.Failed(context, ResultCode.HudWriteException);
        }
    }

    /// <summary>Shows a retained `screen` line again for a recovered viewer: its value has moved on by its rate since it
    /// was issued <paramref name="elapsed"/> seconds ago.</summary>
    internal CommandResult ShowRecovered(CommandContext context, double elapsed)
    {
        _elapsed = elapsed;
        try { return Handle(context); }
        finally { _elapsed = 0; }
    }

    /// <summary>The element the command names (its own card when it names none), made on first showing and updated in place
    /// after that. Every setting comes from the command, so a card that only updates a value carries the place it stands.</summary>
    private string? Screen(CommandContext context, string form, double value, double maximum, string? labelReference,
        string? label, string? textReference, string? body, bool localized, Color? color, bool visible)
    {
        string element = context.Text(PresentationContract.HudValueElement) is { Length: > 0 } named ? named : context.NodeId;
        _screens.TryGetValue(element, out var held);
        if (!visible)
        {
            if (held != null) { held.Placed.Remove(); _screens.Remove(element); UpdateLocalizedFlag(); }
            return null;
        }
        int anchor = Array.IndexOf(PresentationContract.HudAnchors, context.Text(PresentationContract.HudValueAnchor) ?? "top");
        float textPx = (float)(context.Number(PresentationContract.HudValueTextPx) ?? 20);
        if (anchor < 0 || !(textPx > 0)) return ResultCode.HudPlacementUnsupported;
        if (!HudValueFormat.TryColor(context.Text(PresentationContract.HudValueLowColor), out var lowColor)) return ResultCode.HudColorInvalid;
        var placement = new HudPlacement((HudAnchor)anchor, (float)(context.Number(PresentationContract.HudValueX) ?? 0),
            (float)(context.Number(PresentationContract.HudValueY) ?? 160), (float)(context.Number(PresentationContract.HudValueWidth) ?? 300),
            (float)(context.Number(PresentationContract.HudValueHeight) ?? 40));
        if (held == null)
        {
            // The element's name is the author's own text; the library's registration only needs to be unique.
            if (UIKit.Mod(PresentationContract.ProviderId).AddPlaced("Screen" + ++_screenCount, placement) is not { } added) return ResultCode.HudLayerUnavailable;
            _screens.Add(element, held = new ScreenReadout(added));
        }
        var placed = held.Placed;
        placed.Placement = placement;
        placed.Kind = form switch { "bar" => HudPlacedKind.Bar, "segments" => HudPlacedKind.Segments, "time" => HudPlacedKind.Time, _ => HudPlacedKind.Text };
        placed.Text = HudValueFormat.ScreenText(form, label, body, maximum);
        placed.Colour = color;
        placed.LowColour = lowColor;
        placed.LowBelow = (float)(context.Number(PresentationContract.HudValueLowBelow) ?? double.NegativeInfinity);
        placed.TextPx = textPx;
        placed.Rate = context.Number(PresentationContract.HudValueRate) ?? 0;
        placed.Set(value, maximum, _elapsed);
        held.Capture(form, value, maximum, labelReference, localized);
        held.TextReference = textReference ?? "";
        UpdateLocalizedFlag();
        return null;
    }

    /// <summary>Takes every `screen` element down.</summary>
    internal void ClearScreen()
    {
        foreach (var held in _screens.Values) held.Placed.Remove();
        _screens.Clear();
        UpdateLocalizedFlag();
    }

    /// <summary>The native countdown flag and counters are checked only at command entry: a native change
    /// relinquishes the timer before a write or world cleanup can hide it. Existing host-only objective hooks
    /// cannot observe every client's timer activation, so this is not an immediate takeover notification and adds
    /// no frame poll.</summary>
    private void RelinquishNativeTimer()
    {
        if (!_timerSlot.Reconcile()) return;
        _timer = null;
        UpdateLocalizedFlag();
    }

    /// <summary>The interaction message line's bar: text and a 0..1 fill (the value over its maximum, or the value
    /// itself without one). Ownership is the plan/node record, as for the timer; the game's own line is never taken
    /// from it, and the line is taken down when hidden or when the world is replaced.</summary>
    private string? Interaction(CommandContext context, string? label, double value, double maximum, bool visible)
    {
        bool owned = _progress != null && (!visible || _progress.SameOrigin(context));
        switch (_progressSlot.Check(owned))
        {
            case ScreenTextRefusal.NoLayer: return ResultCode.HudLayerUnavailable;
            case ScreenTextRefusal.Busy: return ResultCode.HudPlacementBusy;
        }
        if (!visible)
        {
            if (owned) _progressSlot.Hide();
            _progress = null;
            return null;
        }
        float fraction = (float)(maximum > 0 ? value / maximum : value);
        if (_progressSlot.Show(label ?? "", fraction) != ScreenTextRefusal.None) return ResultCode.HudLayerUnavailable;
        _progress ??= new TimerReadout(context);
        return null;
    }

    private string? TopCenter(CommandContext context, string form, double value, double maximum,
        string? labelReference, string? label, string? textReference, string? body, bool localized, Color? color,
        bool visible)
    {
        if (!visible)
        {
            // Any node may take the readout down while this slot holds it: the slot's own receipt is the ownership
            // test, and a native timer the game itself shows is left alone by the slot. The show record and the
            // slot's readiness must not decide a hide, because relinquishing a stale borrow would otherwise leave
            // the frame this class activated on screen with nothing left to take it down.
            _timerSlot.Hide();
            _timer = null;
            UpdateLocalizedFlag();
            return null;
        }
        RelinquishNativeTimer();
        // Only showing must come from the node that holds the readout.
        bool owned = _timer != null && _timer.SameOrigin(context);
        switch (_timerSlot.Check(owned))
        {
            case ScreenTextRefusal.NoLayer: return ResultCode.HudLayerUnavailable;
            case ScreenTextRefusal.Busy: return ResultCode.HudPlacementBusy;
        }
        _timer ??= new TimerReadout(context);
        var written = _timerSlot.Show(label ?? "",
            form == "text" ? body ?? "" : HudValueFormat.Line(form, value, maximum, null), color);
        if (written != ScreenTextRefusal.None)
        {
            if (!_timerSlot.Holding) _timer = null;
            return written == ScreenTextRefusal.ColorUnsupported ? ResultCode.HudColorUnsupported : ResultCode.HudLayerUnavailable;
        }
        _timer.Capture(form, value, maximum, labelReference, localized);
        _timer.TextReference = textReference ?? "";
        UpdateLocalizedFlag();
        return null;
    }

    /// <summary>The status bar's own readout, which is the game's. Its bar is always given the value — that is the
    /// game's own normalisation and it is what makes `bar` a form — and the text forms additionally write the one
    /// line that reads the value the way the author asked for.</summary>
    private void Status(string form, double value, double maximum, string? labelReference, string? label,
        bool localized, Color? color, bool visible)
    {
        if (_status == null && _statusLost) throw new InvalidOperationException(OwnershipLost);
        string? line = visible && form != "bar"
            ? HudValueFormat.SingleLine(HudValueFormat.Line(form, value, maximum, label)) : null;
        try { _statusSlot.Write(visible, value, line, color); }
        catch (ScreenTextLostException) when (!_statusSlot.Holding)
        {
            if (_status is { Localized: true }) _statusLost = true;
            _status = null;
            UpdateLocalizedFlag();
            throw;
        }
        if (!_statusSlot.Holding) _status = null;
        if (line == null)
        {
            _statusLost = false;
            UpdateLocalizedFlag();
            return;
        }
        if (!ScreenStatus.TextPresent) return;
        _status ??= new Readout();
        _status.Capture(form, value, maximum, labelReference, localized);
        _statusLost = false;
        UpdateLocalizedFlag();
    }

    /// <summary>
    /// The overhead placement: one line under the named teammates' own name markers, and one answer per viewer in
    /// the order the plan named them. A viewer this process cannot name — a reference it does not track, or one
    /// whose marker the game has not built — has no head to draw under, so its row is a refusal rather than the
    /// presented row of a line that was never stored. Each viewer's own presentation is isolated: a draw that
    /// throws is that viewer's definite failure, and it neither rewrites a line an earlier viewer already had
    /// drawn nor claims a later, unattempted viewer failed. The other teammates are still drawn.
    /// </summary>
    private Outcome Overhead(CommandContext context, string form, double value, double maximum, string? labelReference,
        bool localized, string line, Color? color, bool visible)
    {
        var rows = context.Rows;
        for (int index = 0; index < rows.Count; index++)
        {
            // A row whose viewer the frame does not hold is that row's refusal, never a draw for a default viewer.
            if (!context.TryEntity(PresentationContract.HudValueViewers, index, out var viewer))
            {
                rows.Begin(index).End(RowStatus.Rejected, Commit.None, ResultCode.StaleEntity);
                continue;
            }
            RowStatus status; Commit commit; string code;
            try
            {
                var result = Overhead()
                    .Show(viewer, visible ? line : "", visible ? color : null);
                status = (RowStatus)Array.IndexOf(Outcomes.StatusNames, result.Status);
                commit = (Commit)Array.IndexOf(Outcomes.CommitNames, result.CommitState);
                code = result.Code;
                if (status == RowStatus.Partial)
                {
                    if (visible) RememberOverhead(viewer, form, value, maximum, labelReference, localized, color);
                    else ForgetOverhead(viewer);
                }
            }
            catch (RuntimeContractException error) { status = RowStatus.Rejected; commit = Commit.None; code = error.Code; }
            catch (Exception error)
            {
                Report("presentation.hud-overhead-failed: " + error.GetType().Name + ": " + error.Message);
                status = RowStatus.Failed; commit = Commit.None; code = ResultCode.HudWriteException;
            }
            // This machine's own player has no marker here; the other machines draw the line over them and answer it.
            if (code == ResultCode.HudSelfOverheadUnsupported) rows.Begin(index, viewer).EndElsewhere(status, commit, code);
            else rows.Begin(index, viewer).End(status, commit, code);
        }
        _overheadWritten = _overheads.Count != 0;
        UpdateLocalizedFlag();
        return rows.ConcludePresented();
    }

    private void RememberOverhead(EntityReference viewer, string form, double value, double maximum,
        string? reference, bool localized, Color? color)
    {
        OverheadReadout? readout = null;
        for (int index = 0; index < _overheads.Count; index++)
            if (_overheads[index].Viewer == viewer) { readout = _overheads[index]; break; }
        if (readout == null) _overheads.Add(readout = new OverheadReadout(viewer));
        readout.Capture(form, value, maximum, reference, localized);
        readout.Color = color;
    }

    private void ForgetOverhead(EntityReference viewer)
    {
        for (int index = _overheads.Count - 1; index >= 0; index--)
            if (_overheads[index].Viewer == viewer) _overheads.RemoveAt(index);
    }

    private bool TryRenderLabel(string? reference, int limit, out string? rendered, out Language language, out string code)
    {
        rendered = reference;
        language = default;
        code = "";
        if (!ForgeUI.GameText.IsReference(reference)) return true;
        if (!_language.TryRead(out language, out code)) { rendered = null; return false; }
        if (!TryRenderBounded(reference!, language, out var value, out code, limit))
        {
            rendered = null;
            return false;
        }
        rendered = value;
        return true;
    }

    private static bool TryRenderBounded(string reference, Language language, out string rendered, out string code, int limit = MaximumLabelLength)
    {
        if (!LevelText.TryRender(reference, language, out rendered, out var error))
        {
            if (error != null) PresentationLog.Contained("hud-value.render", error);
            code = ResultCode.TextResourceMissing;
            return false;
        }
        if (rendered.Length > limit)
        {
            code = ResultCode.HudLabelTooLong;
            return false;
        }
        code = "";
        return true;
    }

    private bool TryRenderText(string? reference, out string? rendered, out string code)
    {
        rendered = reference;
        code = "";
        if (!ForgeUI.GameText.IsReference(reference)) return true;
        if (!_language.TryRead(out var language, out code)) { rendered = null; return false; }
        if (!TryRenderBounded(reference!, language, out var value, out code, MaximumTextLength)) { rendered = null; return false; }
        rendered = value;
        return true;
    }

    private void UpdateLocalizedFlag()
    {
        bool overhead = false;
        for (int index = 0; index < _overheads.Count; index++) overhead |= _overheads[index].Localized;
        foreach (var held in _screens.Values) overhead |= held.Localized;
        _hasLocalizedReadout = (_timer?.Localized ?? false) || (_status?.Localized ?? false) || overhead;
        if (!_hasLocalizedReadout) { _languageKnown = false; _languageFailure = null; }
    }

    /// <summary>Refreshes only the readouts whose native ownership receipt still matches. Stable frames read one
    /// cached service binding and one language enum; references are rendered only when that enum changes.</summary>
    internal void TickLanguage()
    {
        if (_disposed) return;
        DropForNewWorld(_kernel.WorldEpoch);
        if (!_hasLocalizedReadout) return;
        if (!_language.TryRead(out var language, out var code))
        {
            _languageKnown = false;
            // Reported once per refusal code, not thrown every tick: a missing provider is an install fact.
            if (_languageFailure != code)
            {
                _languageFailure = code;
                Report(code == PlayerTextLanguageService.MissingCode
                    ? "presentation.hud-service-unresolved: site=hud-value.language; code=" + code
                    : "presentation.hud-value-language-refused: " + code);
            }
            return;
        }
        _languageFailure = null;
        if (_languageKnown && _lastLanguage == language) return;
        _lastLanguage = language;
        _languageKnown = true;
        RefreshTimer(language);
        RefreshStatus(language);
        RefreshOverheads(language);
        RefreshScreens(language);
    }

    private void RefreshScreens(Language language)
    {
        foreach (var held in _screens.Values)
        {
            if (!held.Localized) continue;
            string label = held.LabelReference, body = held.TextReference, code = "";
            if (ForgeUI.GameText.IsReference(held.LabelReference) && !TryRenderBounded(held.LabelReference, language, out label, out code, MaximumScreenLabelLength)
                || ForgeUI.GameText.IsReference(held.TextReference) && !TryRenderBounded(held.TextReference, language, out body, out code, MaximumTextLength))
            {
                Report("presentation.hud-value-screen-refresh-refused: " + code);
                continue;
            }
            held.Placed.Text = HudValueFormat.ScreenText(held.Form, label, body, held.Maximum);
        }
    }

    private void RefreshTimer(Language language)
    {
        RelinquishNativeTimer();
        if (_timer is not { Localized: true } readout) return;
        string rendered = readout.LabelReference, body = readout.TextReference, code = "";
        if (ForgeUI.GameText.IsReference(readout.LabelReference)
            && !TryRenderBounded(readout.LabelReference, language, out rendered, out code)
            || ForgeUI.GameText.IsReference(readout.TextReference)
            && !TryRenderBounded(readout.TextReference, language, out body, out code, MaximumTextLength))
        {
            Report("presentation.hud-value-top-center-refresh-refused: " + code);
            return;
        }
        try
        {
            if (!_timerSlot.Retext(rendered, readout.Form == "text" ? body
                : HudValueFormat.Line(readout.Form, readout.Value, readout.Maximum, null)))
            {
                _timer = null;
                UpdateLocalizedFlag();
                return;
            }
        }
        catch (Exception error)
        {
            Report("presentation.hud-value-top-center-refresh-failed: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void RefreshStatus(Language language)
    {
        if (_status is not { Localized: true } readout) return;
        if (!TryRenderBounded(readout.LabelReference, language, out var rendered, out var code))
        {
            _statusLost = true;
            Report("presentation.hud-value-status-refresh-refused: " + code);
            return;
        }
        try
        {
            string line = HudValueFormat.SingleLine(HudValueFormat.Line(readout.Form, readout.Value, readout.Maximum, rendered));
            if (!_statusSlot.Retext(line))
            {
                _statusLost = true;
                _status = null;
                UpdateLocalizedFlag();
                return;
            }
            _statusLost = false;
        }
        catch (Exception error)
        {
            _statusLost = true;
            Report("presentation.hud-value-status-refresh-failed: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void RefreshOverheads(Language language)
    {
        for (int index = 0; index < _overheads.Count; index++)
        {
            var readout = _overheads[index];
            if (!readout.Localized) continue;
            if (!TryRenderBounded(readout.LabelReference, language, out var rendered, out var code))
            {
                Report("presentation.hud-value-overhead-refresh-refused: " + code);
                continue;
            }
            try
            {
                var result = Overhead().Show(readout.Viewer,
                    HudValueFormat.Line(readout.Form, readout.Value, readout.Maximum, rendered), readout.Color);
                if (result.Status != CommandStatuses.Partial)
                    Report("presentation.hud-value-overhead-refresh-refused: " + result.Code);
            }
            catch (RuntimeContractException)
            {
                _languageKnown = false;
                throw;
            }
            catch (Exception error)
            {
                Report("presentation.hud-value-overhead-refresh-failed: " + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    /// <summary>The Player domain's teammate markers, refused by name while the Player domain is not running.</summary>
    private static ForgePlayer.Native.PlayerTeammateOverhead Overhead()
        => ForgePlayer.Native.PlayerHost.Session?.Overhead
            ?? throw new RuntimeContractException(ResultCode.TeammateOverheadMissing, "The Player domain is not running.");

    /// <summary>The overhead lines hold the game's own extra-information row, which the player provider's service
    /// restores, so a world the kernel has moved past releases them through that service.</summary>
    private void DropForNewWorld(long epoch)
    {
        if (_epochKnown && _epoch == epoch) return;
        ReleaseOwnedReadouts();
        _epoch = epoch;
        _epochKnown = true;
    }

    private void ReleaseOwnedReadouts()
    {
        _statusSlot.Release();
        _status = null;
        _timerSlot.Hide();
        _timer = null;
        _progressSlot.Hide();
        _progress = null;
        foreach (var held in _screens.Values) held.Placed.Remove();
        _screens.Clear();
        if (_overheadWritten)
        {
            try
            {
                var cleared = Overhead().Clear();
                if (cleared.Status is CommandStatuses.Rejected or CommandStatuses.Failed)
                    Report("presentation.hud-overhead-clear-refused: " + cleared.Code);
            }
            catch (RuntimeContractException error)
            {
                Report("presentation.hud-overhead-clear-refused: " + error.Code + ": " + error.Message);
            }
            catch (Exception error)
            {
                Report("presentation.hud-overhead-clear-failed: " + error.GetType().Name + ": " + error.Message);
            }
        }
        _overheads.Clear();
        _overheadWritten = false;
        _statusLost = false;
        _hasLocalizedReadout = _languageKnown = false;
        _languageFailure = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseOwnedReadouts();
        _disposed = true;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { PresentationLog.Contained("hud-value.report", reportError); }
    }
}
