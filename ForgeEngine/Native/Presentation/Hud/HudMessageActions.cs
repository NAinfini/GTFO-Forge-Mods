using System;
using System.Globalization;
using ForgeEngine.Framework;
using ForgeUI;
using Localization;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// `forge.action.presentation.hud_message`: one line on each addressed client's warden-intel feed. The text is the
/// literal a plan computed or a reference to an original Text block; both are resolved in this client's player
/// text language before the feed receives the line, through the library's text service and screen message
/// (<see cref="GameText"/>, <see cref="ScreenMessage"/>).
///
/// The row writes the recipient's own screen and commits no world state, so every answer is the tier's
/// `partial + none`. A line that came from a reference keeps the feed run it started: while that exact run is still
/// on screen, a change of this client's language rewrites its label in place, without replaying the event or
/// extending the run.
/// </summary>
internal sealed class HudMessageActions
{
    /// <summary>The game renders the line into one bounded label; a longer text is refused whole.</summary>
    internal const int MaximumTextLength = 1024;

    /// <summary>The viewer count the capability's result schema declares for each answer row.</summary>
    private const int CountColumn = Blocks.ActionPresentationHudMessage.RowTargetCount;

    private readonly HudLanguage _language;
    private readonly Action<string> _report;
    private ScreenMessage? _shown;
    private string _reference = "", _open = "", _close = "";
    private Language _shownLanguage;
    private string? _languageFailure;

    internal HudMessageActions(HudLanguage language, Action<string> report)
    {
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal CommandResult Handle(CommandContext context) => Show(context, null);

    /// <summary>Shows a retained line again for the part of the feed's lifetime that is still left, through the
    /// same entry and text path as the first showing.</summary>
    internal CommandResult ShowRemaining(CommandContext context, float seconds)
        => Show(context, seconds > 0f && seconds <= ScreenMessage.DefaultSeconds ? seconds
            : throw new ArgumentOutOfRangeException(nameof(seconds)));

    private CommandResult Show(CommandContext context, float? seconds)
    {
        if (!PresentationAudience.TryCheck(context, PresentationContract.HudMessageViewers, out var refusal))
            return refusal;
        if (!HudTextEntry.TryRead(context, PresentationContract.HudMessageText, PresentationContract.HudMessageTextId,
                out var text, out string? missing))
            return Outcome.Rejected(missing!);
        if (text.Length == 0) return Outcome.Rejected(ResultCode.IntelTextRequired);

        bool refreshable = ForgeUI.GameText.IsReference(text);
        string rendered = text;
        Language language = default;
        if (refreshable)
        {
            if (!_language.TryRead(out language, out string code)) return Outcome.Rejected(code);
            if (!LevelText.TryRender(text, language, out rendered, out var error))
            {
                if (error != null) PresentationLog.Contained("hud-message.render", error);
                return Outcome.Rejected(ResultCode.TextResourceMissing);
            }
        }
        if (rendered.Length > MaximumTextLength) return Outcome.Rejected(ResultCode.SoundSubtitleTooLong);
        if (!TryStyle(context, out var open, out var close, out var styleRefusal)) return Outcome.Rejected(styleRefusal!);
        if (!ScreenMessage.Available) return Outcome.Rejected(ResultCode.LevelEventUnavailable);

        ScreenMessage? shown;
        try { shown = ScreenMessage.Show(open + rendered + close, seconds); }
        catch (Exception error)
        {
            Report("presentation.hud-message-failed: " + error.GetType().Name + ": " + error.Message);
            // The entry is fire-and-forget and the tier commits nothing, so the failure is definite for every
            // viewer; each of them still gets the row its recipient is owed.
            return PresentationRows.Failed(context, ResultCode.NativeCommitException, CountColumn);
        }
        var result = PresentationRows.Presented(context, ResultCode.LevelEventPresented, CountColumn);
        if (result.Status == Status.Partial)
        {
            _shown = refreshable ? shown : null;
            _reference = text;
            _open = open;
            _close = close;
            _shownLanguage = language;
        }
        return result;
    }

    /// <summary>The optional colour and size as the rich-text tags the game's own warden-intel lines use
    /// (`&lt;color=…&gt;`, `&lt;size=N%&gt;`): the feed's entry takes nothing but the line, so the style travels in it.
    /// Neither set leaves the line exactly as written.</summary>
    private static bool TryStyle(CommandContext context, out string open, out string close, out string? refusal)
    {
        open = close = ""; refusal = null;
        string? color = context.Text(PresentationContract.HudMessageColor);
        if (!HudValueFormat.TryColor(color, out var parsed)) { refusal = ResultCode.HudColorInvalid; return false; }
        if (context.Number(PresentationContract.HudMessageSize) is { } size)
        {
            if (!double.IsFinite(size) || size <= 0) { refusal = ResultCode.HudTextSizeInvalid; return false; }
            open = "<size=" + (size * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%>";
            close = "</size>";
        }
        if (parsed.HasValue)
        {
            open += "<color=#" + color!.TrimStart('#') + ">";
            close = "</color>" + close;
        }
        return true;
    }

    /// <summary>Forgets the retained line (a world change); the feed itself is the game's.</summary>
    internal void Clear() => _shown = null;

    /// <summary>Refreshes the retained line only while the exact run Forge started is still on screen and still
    /// shows what Forge wrote. Language changes add no scheduler: the host's frame calls this.</summary>
    internal void TickLanguage()
    {
        if (_shown is not { } line) return;
        if (line.ReadText() is not { } now || !string.Equals(now, line.Text, StringComparison.Ordinal))
        {
            _shown = null;
            return;
        }
        if (!_language.TryRead(out var language, out string code))
        {
            if (_languageFailure != code)
            { _languageFailure = code; Report("hud-message.text-refused: reason=" + code); }
            return;
        }
        _languageFailure = null;
        if (language == _shownLanguage) return;
        if (!LevelText.TryRender(_reference, language, out var rendered, out var error))
        {
            if (error != null) PresentationLog.Contained("hud-message.render", error);
            _shown = null;
            return;
        }
        if (!line.TrySetText(_open + rendered + _close))
        {
            _shown = null;
            return;
        }
        _shownLanguage = language;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { PresentationLog.Contained("hud-message.report", reportError); }
    }
}
