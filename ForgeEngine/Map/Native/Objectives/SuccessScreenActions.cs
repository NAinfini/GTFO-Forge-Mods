using System;
using System.Collections.Generic;
using CellMenu;
using ForgeEngine.Framework;
using ForgeEngine.Presentation;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.presentation.success_screen`: the title and the music of the game's own
/// expedition-success page, written on the machine of a viewer the plan named.
///
/// <b>Where the members live.</b> The page is `GuiManager.MainMenuLayer` (static property, dump.cs 551386; getter
/// 551459, RVA 0x13A6560) → `MainMenuGuiLayer.PageExpeditionSuccess` (field, dump.cs 552023, offset 0xF0) — the one
/// `CellMenu.CM_PageExpeditionSuccess` (dump.cs 669381) the game shows when `GS_ExpeditionSuccess` enters the menu.
/// The title is that page's own header text, `m_header` (a `TextMeshPro`, dump.cs 669386, offset 0x180), written
/// through `TMP_Text.text` (dump.cs 449591). The music is the page's own `m_overrideSuccessMusic` (a `uint` sound
/// event id, dump.cs 669403, offset 0x1E8), which the page's own `OnEnable` (dump.cs 669440, RVA 0x124AC10) reads
/// when the page is shown; zero is the game's own "no override".
///
/// <b>Whose machine.</b> `viewers` is the explicit recipient list. A viewer is written only when it is the player
/// sitting at this machine (`PlayerAgent.IsLocallyOwned`, dump.cs 650632): the page is one machine's menu, so a
/// viewer of another machine is refused by name here and presented by its own machine. A reference of another
/// world is refused as `stale-entity` and a player this process cannot resolve as `unsupported-recipient`; nothing
/// is retargeted.
///
/// <b>Restore.</b> The first write of a world records the page's original header text and music; every later write
/// of the same world overwrites the values (the last write wins), and the originals are put back when the world
/// changes (the game's level cleanup after the success page is left) or the runtime stops, so an override never
/// outlives the run it was written in.
/// </summary>
internal sealed class SuccessScreenActions : IDisposable
{
    /// <summary>The page values before this world's first override, and the page they were read from.</summary>
    private sealed record Original(IntPtr Pointer, string? Title, uint Music);

    private readonly Func<CM_PageExpeditionSuccess?> _page;
    private readonly Func<EntityReference, PlayerAgent?> _agents;
    private readonly Action<string> _report;
    private RuntimeLifecycleSubscription? _lifecycle;
    private Original? _original;
    private bool _disposed;
    private readonly MapTextLanguage _language;
    private string? _titleReference;
    private Localization.Language? _titleLanguage;
    private string? _titleFailure;

    internal SuccessScreenActions(RuntimeModuleHandle registration, Func<CM_PageExpeditionSuccess?> page,
        Func<EntityReference, PlayerAgent?> agents, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _language = new();
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _lifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
                || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) Restore();
        });
    }

    /// <summary>The production wiring: the game's own page, read when the step runs; the agent the player
    /// directory names; and the install's Text blocks.</summary>
    internal static SuccessScreenActions For(RuntimeModuleHandle registration, IPlayerDirectory players, Action<string> report)
        => new(registration, NativePage, reference => players.AgentOf(reference), report);

    private static CM_PageExpeditionSuccess? NativePage()
    {
        var layer = GuiManager.MainMenuLayer;
        if (layer == null) return null;
        var page = layer.PageExpeditionSuccess;
        return page == null ? null : page;
    }

    /// <summary>Whether <see cref="Dispose"/> finished: the originals are back and the lifecycle is released.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Halves whose session ended while the page restore was still failing. The recorded originals belong
    /// to the game's page, which outlives the session, so the next session retries these before it writes anything.</summary>
    private static readonly List<SuccessScreenActions> Unrestored = new();

    /// <summary>Keeps a half whose <see cref="Dispose"/> failed so its restore can be retried; one entry per half.</summary>
    internal static void RetainUnrestored(SuccessScreenActions half)
    {
        ArgumentNullException.ThrowIfNull(half);
        if (!half._disposed && !Unrestored.Contains(half)) Unrestored.Add(half);
    }

    /// <summary>Retries every retained restore. A half whose restore succeeds is disposed and forgotten; one that
    /// fails again stays retained, and the failures are thrown after every half was tried.</summary>
    internal static void RetryUnrestored()
    {
        List<Exception>? errors = null;
        foreach (var half in Unrestored.ToArray())
        {
            try { half.Dispose(); Unrestored.Remove(half); }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }
        if (errors != null) throw new AggregateException("Success page restore is still pending.", errors);
    }

    internal CommandResult Handle(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PresentationAudience.TryCheck(context, SuccessScreenContract.Viewers, out var refusal)) return refusal;
        // Both entries retain a text reference until the local player language is selected.
        if (!TextResourceContract.TryText(context, SuccessScreenContract.Title, SuccessScreenContract.TitleId,
                out var cardText, out string? missing))
            return Outcome.Rejected(missing!);
        string? title = cardText.Literal;
        var reference = TextCardValue.Reference(cardText);
        Localization.Language? language = null;
        if (StationTextValue.RequiresLanguage(reference))
        {
            if (!_language.TryRead(out var selected, out var code, out _)) return Outcome.Rejected(code);
            if (!StationTextValue.TryRender(reference, selected, out title))
                return Outcome.Rejected(ResultCode.TextResourceMissing);
            language = selected;
        }
        if (cardText.Stored && title == null) return Outcome.Rejected(ResultCode.TextResourceMissing);
        if (string.IsNullOrEmpty(title)) title = null;
        if (!TryMusic(context, out uint? music)) return Outcome.Rejected(ResultCode.SuccessScreenMusicInvalid);
        if (title == null && music == null) return Outcome.Rejected(ResultCode.SuccessScreenEmpty);

        // A presentation row commits nothing. The skeleton refuses a viewer of another world (`stale-entity`) and
        // writes the viewer count on every row.
        var request = new PageRequest { Self = this, Title = title, Music = music, Reference = reference, Language = language };
        return context.EachTargetPresented(ref request, PageStep, countColumn: Blocks.ActionPresentationSuccessScreen.RowTargetCount);
    }

    private struct PageRequest
    {
        internal SuccessScreenActions Self;
        internal string? Title;
        internal uint? Music;
        internal string? Reference;
        internal Localization.Language? Language;
        /// <summary>The page's answer once it was written: two viewers of one machine share one page.</summary>
        internal string? Written;
    }

    private static readonly TargetStep<PageRequest> PageStep = PageOne;

    private static void PageOne(ref PageRequest request, EntityReference viewer, int index, RowWriter rows)
    {
        var self = request.Self;
        // The audience read admitted player references only, so a viewer of this world that resolves to no
        // agent, or whose ownership cannot be read, is a recipient this machine cannot present to.
        if (self._agents(viewer) is not { } agent)
        {
            rows.End(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient);
            return;
        }
        bool local;
        try { local = agent.IsLocallyOwned; }
        catch (Exception ownerError)
        {
            MapLog.Contained("success-screen.local-owner", ownerError);
            rows.End(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient);
            return;
        }
        // A viewer another machine draws gets the page there, and that machine answers it.
        if (!local) { rows.EndElsewhere(Status.Rejected, Commit.None, ResultCode.SuccessScreenViewerRemote); return; }
        if (request.Written == null)
        {
            try
            {
                request.Written = self.Write(request.Title, request.Music);
                if (request.Written == ResultCode.SuccessScreenWritten && request.Title != null)
                {
                    self._titleReference = request.Language.HasValue ? request.Reference : null;
                    self._titleLanguage = request.Language;
                    self._titleFailure = null;
                }
            }
            catch (Exception error)
            {
                self.Report("map.success-screen-failed: " + error.GetType().Name + ": " + error.Message);
                request.Written = ResultCode.NativeCommitException;
            }
        }
        var written = request.Written;
        rows.End(written == ResultCode.SuccessScreenWritten ? Status.Partial
            : written == ResultCode.NativeCommitException ? Status.Failed
            : Status.Rejected, Commit.None, written);
    }

    /// <summary>Writes the page this machine shows, recording its originals on the first write of the world. A
    /// value the request did not name is left as the page has it.</summary>
    private string Write(string? title, uint? music)
    {
        var page = _page();
        if (page == null) return ResultCode.SuccessScreenUnavailable;
        var header = page.m_header;
        if (title != null && header == null) return ResultCode.SuccessScreenUnavailable;
        if (_original != null && _original.Pointer != page.Pointer) _original = null;
        _original ??= new Original(page.Pointer, header == null ? null : header.text, page.m_overrideSuccessMusic);
        string? priorTitle = header?.text;
        uint priorMusic = page.m_overrideSuccessMusic;
        try
        {
            if (title != null) header!.text = title;
            if (music is { } id) page.m_overrideSuccessMusic = id;
            if (title != null && !string.Equals(header!.text, title, StringComparison.Ordinal)
                || music is { } expected && page.m_overrideSuccessMusic != expected)
                throw new InvalidOperationException("Success page did not retain the requested override.");
        }
        catch
        {
            // Undo this request, retaining any earlier override of the same world. Restore() still owns the
            // world's originals if the native page cannot accept this rollback.
            try
            {
                try { if (title != null) header!.text = priorTitle!; }
                finally { if (music.HasValue) page.m_overrideSuccessMusic = priorMusic; }
                if (title != null && !string.Equals(header!.text, priorTitle, StringComparison.Ordinal)
                    || music.HasValue && page.m_overrideSuccessMusic != priorMusic)
                    throw new InvalidOperationException("Success page rollback was not retained.");
            }
            catch (Exception error)
            { Report("map.success-screen-restore-failed: " + error.GetType().Name + ": " + error.Message); }
            throw;
        }
        return ResultCode.SuccessScreenWritten;
    }

    /// <summary>Reflows the retained title only when the local player language changes.</summary>
    internal void TickLanguage()
    {
        if (_disposed || _titleReference == null || _original == null) return;
        if (!_language.TryRead(out var language, out var code, out _))
        { TitleRefused(code); return; }
        if (_titleLanguage == language) return;
        var page = _page();
        if (page == null || page.WasCollected || page.Pointer != _original.Pointer
            || page.m_header == null || page.m_header.WasCollected)
        { TitleRefused("success-title-readback-unavailable"); return; }
        if (!StationTextValue.TryRender(_titleReference, language, out var text))
        { TitleRefused(ResultCode.TextResourceMissing); return; }
        page.m_header.SetText(text);
        if (page.m_header.text != text)
        { TitleRefused("success-title-readback-mismatch"); return; }
        _titleLanguage = language; _titleFailure = null;
    }

    private void TitleRefused(string code)
    {
        if (_titleFailure == code) return;
        _titleFailure = code; Report("map." + code);
    }

    internal void Restore()
    {
        var original = _original;
        if (original == null) return;
        try
        {
            var page = _page();
            if (page == null) throw new InvalidOperationException("Success page is unavailable for restore.");
            if (page.Pointer != original.Pointer) { _original = null; _titleReference = null; _titleLanguage = null; return; }
            var header = page.m_header;
            try
            {
                if (original.Title != null)
                {
                    if (header == null) throw new InvalidOperationException("Success page header is unavailable for restore.");
                    header.text = original.Title;
                }
            }
            finally { page.m_overrideSuccessMusic = original.Music; }
            if (original.Title != null && !string.Equals(header!.text, original.Title, StringComparison.Ordinal)
                || page.m_overrideSuccessMusic != original.Music)
                throw new InvalidOperationException("Success page restore was not retained.");
            _original = null; _titleReference = null; _titleLanguage = null;
        }
        catch (Exception error)
        {
            Report("map.success-screen-restore-failed: " + error.GetType().Name + ": " + error.Message);
            throw;
        }
    }

    /// <summary>The optional `music` entry: absent or null is no music override; a present value must be an
    /// integer sound event id the page's `uint` member can carry.</summary>
    private static bool TryMusic(CommandContext context, out uint? music)
    {
        music = null;
        if (!context.Present(SuccessScreenContract.Music)) return true;
        if (context.Integer(SuccessScreenContract.Music) is not { } value || value < 0 || value > uint.MaxValue)
            return false;
        music = (uint)value;
        return true;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("success-screen.report", reportError); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        if (_original != null) throw new InvalidOperationException("Success page restore is still pending.");
        _lifecycle?.Dispose();
        _lifecycle = null;
        _disposed = true;
    }
}
