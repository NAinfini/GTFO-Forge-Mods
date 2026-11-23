using System;
using ForgeEngine.Framework;
using ForgeEngine.Presentation;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>The presentation entry for model-owned text. The placed-model table remains the only resolver and text
/// owner; this handler validates the selected local recipient and language, then asks that table to draw it.</summary>
internal sealed class ModelTextActions
{
    private readonly IPlayerDirectory _players;

    internal ModelTextActions(IPlayerDirectory players)
        => _players = players ?? throw new ArgumentNullException(nameof(players));

    internal Outcome Handle(CommandContext context)
    {
        if (!context.TryEntity(ModelTextContract.ModelSlot, out var model) || !EntityKinds.Is(model, EntityKinds.MapObject)
            || PlacedModelAddress.TryParse(model.Id[(EntityKinds.MapObject.Length + 1)..]) == null)
            return Outcome.Rejected(ResultCode.ModelTextModelKind);
        if (context.WorldEpoch != model.WorldEpoch) return Outcome.Rejected(ResultCode.ModelTextModelStale);
        if (!PlayerTextLanguageService.TryLanguage(context.Text(ModelTextContract.LanguageSlot), out var language))
            return Outcome.Rejected(ResultCode.ModelTextLanguageInvalid);
        var text = context.Text(ModelTextContract.TextSlot);
        if (text is { Length: 0 }) return Outcome.Rejected(ResultCode.ModelTextMissing);
        if (!PresentationAudience.TryCheck(context, ModelTextContract.RecipientsSlot, out var refusal))
            return refusal;
        var request = new TextRequest { Players = _players, Model = model, Language = language, Text = text };
        return context.EachTargetPresented(ref request, TextStep, report: WriteFailed);
    }

    private struct TextRequest
    {
        internal IPlayerDirectory Players;
        internal EntityReference Model;
        internal Localization.Language Language;
        internal string? Text;
    }

    private static readonly TargetStep<TextRequest> TextStep = TextOne;

    private static readonly Action<EntityReference, Exception> WriteFailed =
        static (_, error) => MapLog.Contained("model-text-actions.write", error);

    private static void TextOne(ref TextRequest request, EntityReference recipient, int index, RowWriter rows)
    {
        var players = request.Players;
        var player = players.PlayerOf(recipient);
        if (player == null) { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        if (!players.IsLocal(players.SessionOf(player))) { rows.End(Status.Rejected, Commit.None, ResultCode.RecipientNotLocal); return; }
        if (!PlacedModelSession.TryShowText(request.Model, request.Language, request.Text, out string code))
        {
            if (code is ResultCode.ModelTextReadbackUnavailable or ResultCode.ModelTextReadbackMismatch)
                rows.End(Status.Failed, Commit.None, code);
            else rows.End(Status.Rejected, Commit.None, code == ResultCode.ModelTextModelStale ? code : ResultCode.ModelTextMissing);
            return;
        }
        rows.End(Status.Partial, Commit.None, ResultCode.Partial);
    }
}
