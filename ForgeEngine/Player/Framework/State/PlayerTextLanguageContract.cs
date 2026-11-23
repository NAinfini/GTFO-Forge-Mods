using System;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The one player text-language contract: a host-side read of the language reported for an explicit
/// current player life, and a presentation write applied only by each addressed player's own client.</summary>
internal static class PlayerTextLanguageContract
{
    public const string QueryHandlerName = "gtfo.player.text_language";
    public const string SetHandlerName = "gtfo.player.set_text_language";
    public const string QueryBindingId = ModuleDefinition.ProviderId + ".binding.query.player.text_language";
    public const string SetBindingId = ModuleDefinition.ProviderId + ".binding.action.player.set_text_language";
    public const string Permission = "player.text_language";

    public const string InputKindCode = "text-language-player-kind";
    public const string InputStaleCode = "text-language-player-stale";
    public const string UnreportedCode = "text-language-unreported";
    public const string FollowGame = "follow_game";

    public static readonly HandlerShape QueryShape = new HandlerShape().Inputs("player").Outputs("language");
    public static readonly HandlerShape SetShape = new HandlerShape().Inputs("recipients", "language").Outputs("result");
    public static readonly HandlerSlot PlayerSlot = QueryShape.InputSlot("player");
    public static readonly HandlerSlot RecipientsSlot = SetShape.InputSlot("recipients");
    public static readonly HandlerSlot LanguageSlot = SetShape.InputSlot("language");

    public readonly record struct Readers(Func<EntityReference, bool> IsCurrent,
        Func<EntityReference, int?> ReadLanguage);

    public static EvaluatorHandler Evaluator(Readers readers) => (context, outputs) =>
    {
        if (!context.TryEntity(PlayerSlot, out var player))
            throw new RuntimeContractException(ResultCode.MissingField, "player");
        Answer(player, readers, outputs);
    };

    internal static void Answer(EntityReference player, Readers readers, EvaluationOutputs outputs)
    {
        if (!EntityKinds.Is(player, EntityKinds.Player))
            throw new RuntimeContractException(InputKindCode, "The text-language query requires a player.");
        if (!readers.IsCurrent(player))
            throw new RuntimeContractException(InputStaleCode, "The player life is not current: " + player.Id);
        int language = readers.ReadLanguage(player)
            ?? throw new RuntimeContractException(UnreportedCode, "The current player has not reported a text language.");
        string? member = LanguageMember(language);
        if (member == null)
            throw new RuntimeContractException(UnreportedCode, "The current player's reported text language is unsupported.");
        outputs.Text(0, member);
    }

    public static int LanguageIndex(string? member) => PlayerTextLanguages.LanguageIndex(member);

    public static string? LanguageMember(int index) => PlayerTextLanguages.LanguageName(index);

    public static bool TrySetting(string? member, out int? language)
    {
        if (string.Equals(member, FollowGame, StringComparison.Ordinal))
        { language = null; return true; }
        int index = LanguageIndex(member);
        language = index < 0 ? null : index;
        return index >= 0;
    }

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        new ProviderBinding(QueryBindingId, Blocks.QueryPlayerTextLanguage.Id, QueryHandlerName, "observe", Array.Empty<string>(), QueryShape),
        new ProviderBinding(SetBindingId, Blocks.ActionPlayerSetTextLanguage.Id, SetHandlerName, "execute", new[] { Permission }, SetShape)
    };
}
