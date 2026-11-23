using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The query module's registration tables: the families of on-demand rows this module publishes, joined in
/// composition order. Each family owns its declaration file — conditions, collection selectors, event-role
/// selectors, list predicates, entity-state reads — and this file adds one composition line per family, so a batch
/// that adds rows adds a file and a line instead of editing a shared table.
///
/// The rows are evaluated on demand by the kernel's `query` step, which hands each handler a
/// <see cref="RuntimeQuerySession"/> — the only way a step reaches the world — and the explicit actor roles of the
/// event being dispatched. The role selectors read those roles through <see cref="EvaluationContext.Actors"/> and
/// never rebuild a role-to-port mapping here; the collection selectors never read the world at all, because their
/// candidates arrive as ports and the references they answer with are checked by the kernel before a consumer sees
/// them. The space and ranking rows are declared by <see cref="ObservedSpaceDeclarations"/> and published through
/// <see cref="ObservedSpaceNodes"/>, which owns their selection algorithms.</summary>
internal static class ObservedQueryModule
{
    private static readonly ObservedFamily Families = ObservedFamily.Compose(
        ObservedConditionDeclarations.Family,
        ObservedCollectionDeclarations.Family,
        ObservedEntityDeclarations.Family,
        ObservedListDeclarations.Family,
        ObservedEntityStateDeclarations.Family,
        ObservedResultDeclarations.Family);

    /// <summary>The declared rows in registration order. Public because the contract tests iterate the same table
    /// the module registers instead of restating it.</summary>
    public static IReadOnlyList<ObservedNode> Nodes => Families.Nodes;

}
