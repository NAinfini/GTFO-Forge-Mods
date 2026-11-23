using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEngine.Presentation;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>What one restyle asks for: each field null keeps what is shown.</summary>
internal readonly record struct TextRestyle(string? Text, Color? Color, Color? Background, float? Opacity);

/// <summary>The presentation entry that restyles one shown level text. A world sign on the target is restyled first;
/// otherwise the target's placed-model text. The two display tables stay the only owners of what they draw; this
/// handler validates the local recipients and the values, then asks the owning table.</summary>
internal sealed class TextStyleActions
{
    /// <summary>What a display table answers when it restyled the text; any other answer is the refusal's code.</summary>
    internal const string AppliedCode = "text-style-applied";

    private readonly IPlayerDirectory _players;

    internal TextStyleActions(IPlayerDirectory players)
        => _players = players ?? throw new ArgumentNullException(nameof(players));

    internal CommandResult Handle(CommandContext context)
    {
        if (!context.TryEntity(TextStyleContract.TargetSlot, out var target)) return Outcome.Rejected(ResultCode.MissingField);
        if (context.WorldEpoch != target.WorldEpoch) return Outcome.Rejected(ResultCode.StaleEntity);
        if (!TryRead(context, out var restyle)) return Outcome.Rejected(ResultCode.TextStyleInvalid);
        if (!PresentationAudience.TryCheck(context, TextStyleContract.ViewersSlot, out var refusal))
            return refusal;
        var request = new StyleRequest { Players = _players, Target = target, Restyle = restyle };
        return context.EachTargetPresented(ref request, StyleStep, report: WriteFailed);
    }

    private struct StyleRequest
    {
        internal IPlayerDirectory Players;
        internal EntityReference Target;
        internal TextRestyle Restyle;
    }

    private static readonly TargetStep<StyleRequest> StyleStep = StyleOne;

    private static readonly Action<EntityReference, Exception> WriteFailed =
        static (_, error) => MapLog.Contained("text-style-actions.write", error);

    private static void StyleOne(ref StyleRequest request, EntityReference recipient, int index, RowWriter rows)
    {
        var players = request.Players;
        var player = players.PlayerOf(recipient);
        if (player == null) { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        if (!players.IsLocal(players.SessionOf(player))) { rows.End(Status.Rejected, Commit.None, ResultCode.RecipientNotLocal); return; }
        var code = MapLabels.Restyle(request.Target, request.Restyle) ?? PlacedModelSession.Restyle(request.Target, request.Restyle);
        if (code == AppliedCode) rows.End(Status.Partial, Commit.None, ResultCode.Partial);
        else rows.End(Status.Rejected, Commit.None, code);
    }

    /// <summary>The native text and backdrop a restyle of <paramref name="target"/> changes, from the table that owns them
    /// (the same order <see cref="Handle"/> asks); null when neither shows a text there.</summary>
    internal static (TMPro.TextMeshPro? Text, ForgeVisual.WorldBackdrop? Backdrop)? Inspect(EntityReference target)
        => MapLabels.InspectTextLook(target) ?? PlacedModelSession.InspectTextLook(target);

    private static bool TryRead(CommandContext context, out TextRestyle restyle)
    {
        restyle = default;
        var text = context.Text(TextStyleContract.TextSlot);
        if (text != null && text.Length == 0) return false;
        Color? color = null, background = null;
        if (context.Vector(TextStyleContract.ColorSlot) is { } c)
        {
            if (!GameVectors.TryUnity(c, out _)) return false;
            color = new Color((float)c.X, (float)c.Y, (float)c.Z, 1);
        }
        if (context.Vector(TextStyleContract.BackgroundColorSlot) is { } b)
        {
            if (!GameVectors.TryUnity(b, out _)) return false;
            background = new Color((float)b.X, (float)b.Y, (float)b.Z, 1);
        }
        float? opacity = null;
        if (context.Number(TextStyleContract.BackgroundOpacitySlot) is { } value)
        {
            if (!double.IsFinite(value) || value < 0 || value > 1) return false;
            opacity = (float)value;
        }
        restyle = new TextRestyle(text, color, background, opacity);
        return true;
    }
}
