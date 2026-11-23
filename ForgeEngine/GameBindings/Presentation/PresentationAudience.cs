using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation;

/// <summary>
/// The one spelling of the presentation family's audience rule: which viewer set a presentation request
/// addressed, and the refusals a request that named something that is not a player, or too many players, is
/// answered with. An empty set never arrives: the host's walk answers it `no-targets` and sends nothing. Every
/// viewer-addressed presentation row reads its audience here, the host's shared rows and the domain packages' own,
/// so no two rows can disagree about who a presentation step reached.
///
/// The port is a routing list the kernel's own recipient resolver owns; what this layer checks is that every
/// member is a player reference, because a step addressed to something else cannot be presented to it. The references come back because they are the rows' own recipients: a presentation
/// row names the viewer it reached, one row per viewer.
/// </summary>
internal static class PresentationAudience
{
    /// <summary>The player entity kind a viewer reference has to carry. It is the contract's own spelling, which
    /// is the same namespace the player identity half registers.</summary>
    public static string PlayerEntityKind => EntityKinds.Player;

    /// <summary>The audience a presentation request declares, in the order the plan supplied it, or the refusal
    /// that names what was wrong with it: `unsupported-recipient` for a viewer that is not a player entity, and
    /// `too-many-targets` for a set past the result-row bound, refused before any native entry runs because every
    /// presentation row writes one row per viewer.</summary>
    public static bool TryRead(CommandContext context, HandlerSlot slot,
        out List<EntityReference> viewers, out Outcome refusal)
    {
        viewers = new List<EntityReference>();
        return Check(context, slot, viewers, out refusal);
    }

    /// <summary>The same rule for a row that writes its rows by position (<see cref="CommandContext.Rows"/>), whose
    /// targets the kernel writes from the recipients itself: nothing is collected and nothing is allocated.</summary>
    public static bool TryCheck(CommandContext context, HandlerSlot slot, out Outcome refusal)
        => Check(context, slot, null, out refusal);

    /// <summary>Whether the audience names <paramref name="viewer"/>.</summary>
    public static bool Names(CommandContext context, HandlerSlot slot, EntityReference viewer)
    {
        for (int index = 0, count = context.Count(slot); index < count; index++)
            if (context.TryEntity(slot, index, out var reference) && reference == viewer) return true;
        return false;
    }

    private static bool Check(CommandContext context, HandlerSlot slot, List<EntityReference>? viewers, out Outcome refusal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(slot);
        refusal = default;
        int count = context.Count(slot);
        // The recipient contract makes the port required, so a request that carries no collection lacks a required input.
        if (count < 0) { refusal = Outcome.Rejected(ResultCode.MissingField); return false; }
        string kind = PlayerEntityKind;
        for (int index = 0; index < count; index++)
        {
            if (!context.TryEntity(slot, index, out var reference) || !EntityKinds.Is(reference, kind))
            {
                refusal = Outcome.Rejected(ResultCode.UnsupportedRecipient);
                return false;
            }
            viewers?.Add(reference);
        }
        if (count > CommandResult.MaximumFacts)
        {
            refusal = Outcome.Rejected(ResultCode.TooManyTargets);
            return false;
        }
        return true;
    }
}
