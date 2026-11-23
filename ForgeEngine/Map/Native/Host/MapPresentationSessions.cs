using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>The session routing of this provider's presentation rows. The player-to-session conversion is the
/// Runtime's shared <see cref="IPlayerDirectory"/> service, so a Map row addressed to players is routed by exactly
/// the answer its installed provider gives; the one routing decision that is Map's own is what a step addressed to
/// map objects means.</summary>
internal static class MapPresentationSessions
{
    /// <summary>The sessions one Map presentation step is addressed to, from the entity references the step's own
    /// `recipients` port carries. Object prompts and markers draw on every machine that might look at that
    /// object, including captured native items and cabinets. A step whose recipients are all world objects is
    /// therefore addressed to every player in the level. Every other step — players, or a map object
    /// mixed with a player — is converted one reference at a time by the Player package and refuses as it does.</summary>
    internal static IReadOnlyList<string>? SessionsOf(IPlayerDirectory players, IReadOnlyList<EntityReference>? recipients)
    {
        if (recipients == null || recipients.Count == 0) return null;
        // A zone is drawn by every machine in the level — its lights are each machine's own copies — so a step that
        // names one is addressed to every player, and each machine writes only what it draws (the device row).
        if (AnyZone(recipients)) return players.EverySession();
        return AllWorldObjects(recipients) ? players.EverySession() : players.SessionsOf(recipients);
    }

    private static bool AnyZone(IReadOnlyList<EntityReference> recipients)
    {
        foreach (var reference in recipients)
            if (EntityKinds.Is(reference, EntityKinds.Zone)) return true;
        return false;
    }

    /// <summary>Whether every reference names a map object of this provider. The kind is the one prefix every
    /// map-object reference carries, read here rather than through a category parser: routing asks which entity
    /// namespace a step addresses, not which category inside it.</summary>
    private static bool AllWorldObjects(IReadOnlyList<EntityReference> recipients)
    {
        foreach (var reference in recipients)
            if (!EntityKinds.Is(reference, EntityKinds.MapObject) && !EntityKinds.Is(reference, EntityKinds.LevelObject)) return false;
        return true;
    }
}
