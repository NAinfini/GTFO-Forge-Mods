using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEngine.Presentation;
using Localization;
using ForgeCore;

namespace ForgePlayer.Native;


/// <summary>Resolves this player's effective text language and publishes it as the player's own row on ForgePlayer's
/// ForgeCore channel (<see cref="NetChannel.Lobby"/>): a row lasts the session, reaches a player who joins later and
/// does not depend on who hosts, so every machine reads any player's language the same way.</summary>
internal sealed class PlayerTextLanguageSession : IDisposable
{
    private static readonly Language[] Languages =
    {
        Language.English, Language.French, Language.Italian, Language.German, Language.Spanish,
        Language.Russian, Language.Portuguese_Brazil, Language.Polish, Language.Japanese,
        Language.Korean, Language.Chinese_Traditional, Language.Chinese_Simplified
    };

    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _owner;
    private readonly RuntimeServiceBinding<IPlayerTextLanguageDefaults> _defaults;
    private readonly PlayerDirectory _players;
    /// <summary>One byte per player: the index of its effective language in <see cref="Languages"/>.</summary>
    private readonly NetLobbyValue _row;
    private int? _override;
    private bool _hasChoice;
    private Language? _gameLanguage;
    private int? _gameLanguageIndex;
    private bool _disposed;

    private PlayerTextLanguageSession(IForgeEngine kernel, PlayerDirectory players, RuntimeModuleHandle owner)
    {
        _kernel = kernel; _owner = owner;
        _defaults = owner.BindService<IPlayerTextLanguageDefaults>();
        _players = players;
        _row = ForgeEngine.GameBindings.NetworkBinding.Channel.Lobby("player/text-language");
        _languageMoved = () => _effective = null;
        _expeditionMoved = (_, _, _) => _effective = null;
        _gameLanguageRead = GameLanguageIndex; _writeChoice = WriteChoice; _effectiveRead = EffectiveIndex;
        ForgeCore.GameEvents.LanguageChanged += _languageMoved;
        ForgeCore.GameEvents.ExpeditionSelected += _expeditionMoved;
    }

    internal static PlayerTextLanguageSession Start(IForgeEngine kernel, PlayerDirectory players, RuntimeModuleHandle owner)
        => new(kernel ?? throw new ArgumentNullException(nameof(kernel)),
            players ?? throw new ArgumentNullException(nameof(players)), owner ?? throw new ArgumentNullException(nameof(owner)));

    // The effective language's index as a managed value, for the text surfaces that ask every frame: what it depends on (the game's
    // language, the personal choice, the expedition and Rundown defaults, the world) all say when they move, so a stable frame reads
    // nothing native. A language not known yet is not kept, so the first read after the game's text service exists finds it.
    private int? _effective;
    private long _effectiveWorld;
    private IPlayerTextLanguageDefaults? _effectiveDefaults;
    private long _effectiveDefaultsRevision;
    private readonly Action _languageMoved;
    private readonly Action<uint?, int, int> _expeditionMoved;
    // The three steps of a language write, made once so a command allocates no delegate per recipient.
    private readonly Func<int?> _gameLanguageRead, _effectiveRead;
    private readonly Action<int?> _writeChoice;

    internal Language EffectiveLanguage
        => Resolve() is { } index ? Languages[index] : Text.TextLocalizationService?.CurrentLanguage ?? Language.English;

    /// <summary>The effective language's index, worked out again only when something it depends on moved. That is also where this
    /// player's row is written, so the row is always the language this machine's own text uses.</summary>
    private int? Resolve()
    {
        _defaults.TryGet(out var defaults);
        long world = _kernel.WorldEpoch, revision = defaults?.Revision ?? 0;
        if (_effective != null && world == _effectiveWorld && ReferenceEquals(defaults, _effectiveDefaults)
            && revision == _effectiveDefaultsRevision) return _effective;
        if (EffectiveIndex() is not { } index) return null;
        _effectiveWorld = world; _effectiveDefaults = defaults; _effectiveDefaultsRevision = revision;
        _effective = index;
        _row.Set(new[] { (byte)index });
        return index;
    }

    public Language CurrentLanguage => !_disposed ? EffectiveLanguage
        : throw new RuntimeContractException(PlayerTextLanguageService.MissingCode, "The player text-language session has ended.");

    /// <summary>The language the player of <paramref name="reference"/> published, on any machine; null when the life is not
    /// current or that player published none (a bot, a player whose text service is not up yet).</summary>
    internal int? Read(EntityReference reference)
    {
        if (_disposed || _kernel.WorldEpoch != reference.WorldEpoch || _players.PlayerOf(reference) is not { } player) return null;
        var lookup = player.Lookup;
        var value = lookup == Session.LocalLookup ? _row.Local : _row.TryGet(lookup, out var theirs) ? theirs : null;
        return value is { Length: 1 } && value[0] < Languages.Length ? value[0] : null;
    }

    internal CommandResult Set(CommandContext context)
    {
        if (!PlayerTextLanguageContract.TrySetting(context.Text(PlayerTextLanguageContract.LanguageSlot),
            out int? requested))
            return Outcome.Rejected(ResultCode.TextLanguageInvalid);
        if (!PresentationAudience.TryCheck(context, PlayerTextLanguageContract.RecipientsSlot, out var refusal)) return refusal;

        var command = new SetCommand { Session = this, Requested = requested };
        var concluded = context.EachTargetPresented(ref command, SetStep, report: CaptureCommit);
        // The row follows in the same command, so a read later in the plan sees the choice.
        if (command.Succeeded > 0) Resolve();
        return concluded;
    }

    private struct SetCommand
    {
        internal PlayerTextLanguageSession Session;
        internal int? Requested;
        internal int Succeeded;
    }

    private static readonly TargetStep<SetCommand> SetStep = SetTarget;
    private static readonly Action<EntityReference, Exception> CaptureCommit
        = static (_, error) => PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error);

    private static void SetTarget(ref SetCommand command, EntityReference recipient, int index, RowWriter rows)
    {
        var outcome = command.Session.SetOne(recipient, command.Requested);
        if (outcome.Language is { } language && PlayerTextLanguageContract.LanguageMember(language) != null)
            rows.Enum(Blocks.ActionPlayerSetTextLanguage.RowLanguage, language);
        else rows.Null(Blocks.ActionPlayerSetTextLanguage.RowLanguage);
        // A player of another machine sets the language there, and that machine answers it.
        if (outcome.Code == ResultCode.RecipientNotLocal) rows.EndElsewhere(outcome.Status, Commit.None, outcome.Code);
        else rows.End(outcome.Status, Commit.None, outcome.Code);
        if (outcome.Status == Status.Succeeded) command.Succeeded++;
    }

    private LanguageOutcome SetOne(EntityReference recipient, int? requested)
    {
        if (_disposed) return new(Status.Rejected, ResultCode.TextLanguageUnavailable, null);
        var player = _players.PlayerOf(recipient);
        if (recipient.WorldEpoch != _kernel.WorldEpoch || player == null) return new(Status.Rejected, ResultCode.StaleEntity, null);
        if (!_players.IsLocal(_players.SessionOf(player))) return new(Status.Rejected, ResultCode.RecipientNotLocal, null);
        return Apply(requested, _gameLanguageRead, _writeChoice, _effectiveRead);
    }

    private void WriteChoice(int? language)
    {
        _override = language;
        _hasChoice = true;
        _effective = null;
    }

    internal readonly record struct LanguageOutcome(Status Status, string Code, int? Language);

    /// <summary>The same write/readback transaction used by Set. Follow-game snapshots the native language before
    /// writing; the effective read is the source used by local text rendering, never the published row.</summary>
    internal static LanguageOutcome Apply(int? requested, Func<int?> gameLanguage, Action<int?> write,
        Func<int?> effectiveLanguage)
    {
        int? expected;
        try { expected = requested ?? gameLanguage(); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); return new(Status.Failed, ResultCode.ReadbackException, null); }
        if (expected == null) return new(Status.Rejected, ResultCode.TextLanguageUnavailable, null);
        try { write(requested); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return new(Status.Failed, ResultCode.NativeCommitException, null); }
        int? actual;
        try { actual = effectiveLanguage(); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); return new(Status.Failed, ResultCode.ReadbackException, null); }
        if (actual == null) return new(Status.Failed, ResultCode.TextLanguageUnavailable, null);
        return actual == expected ? new(Status.Succeeded, ResultCode.TextLanguageChanged, actual)
            : new(Status.Failed, ResultCode.TextLanguageReadbackMismatch, actual);
    }

    /// <summary>Runs outside kernel dispatch on the Player frame, so the row follows the language while no text surface asks for it.</summary>
    internal void Frame()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerLanguageFrame);
        if (!_disposed && _owner.IsRegistered) Resolve();
    }

    private int? EffectiveIndex()
    {
        if (_hasChoice) return _override ?? GameLanguageIndex();
        return (_defaults.TryGet(out var defaults) ? defaults.CurrentLanguage() : null)
            ?? GameLanguageIndex();
    }

    private int? GameLanguageIndex()
    {
        var service = Text.TextLocalizationService;
        if (service == null) { _gameLanguage = null; _gameLanguageIndex = null; return null; }
        // A live read: it runs only when a command or a resolution after something moved needs the value, so
        // a language the game set a moment ago is always the one read; only the enum-to-wire conversion is cached.
        var language = service.CurrentLanguage;
        if (_gameLanguage != language)
        {
            _gameLanguage = language;
            int index = Array.IndexOf(Languages, language);
            _gameLanguageIndex = index < 0 ? null : index;
        }
        return _gameLanguageIndex;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ForgeCore.GameEvents.LanguageChanged -= _languageMoved;
        ForgeCore.GameEvents.ExpeditionSelected -= _expeditionMoved;
        _row.Set(null);
    }
}
