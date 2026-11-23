using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>
/// The one row of the field family: `forge.selector.target.area`, the candidates that stand inside an authored
/// area field. The field is a definition — a shape and its sizes — and its centre is the consumer's: the row's own
/// optional `center` port names one, and without one the field's own live body would have to, which this build has
/// none of (see the `unloaded` policy below). The row never creates a field, never enumerates a world of its own
/// and never infers a faction: it measures the candidates the plan handed it.
///
/// Two structural policies answer the two empty cases the catalog distinguishes:
/// <list type="bullet">
/// <item>`empty` — the field is there and nothing stands inside it. It is the same collection policy every other
/// selector of this family carries, applied to the answer once it is measured.</item>
/// <item>`unloaded` — the definition this world's table does not hold, or a field whose centre cannot be read
/// because no port and no live body name one. The two are answered the same way on purpose: the row's own
/// measurement needs a definition and a centre, and a volume measured around an invented centre would be a
/// different field from the one the author named. `fail` refuses the step with `area-field-unloaded`;
/// `emit-empty` answers the empty selection, never a silently truncated set.</item>
/// </list>
/// </summary>
internal static class AreaFieldNodes
{
    /// <summary>The row this family declares.</summary>
    public const string CapabilityId = Blocks.SelectorTargetArea.Id;

    /// <summary>The refusal of a field this world cannot measure, which is what the `unloaded` policy's `fail`
    /// member answers with.</summary>
    public const string UnloadedCode = "area-field-unloaded";

    /// <summary>The family, reading the definitions through the provider's own session.</summary>
    public static ObservedFamily Family(AreaFieldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ObservedFamily.Declare(
            ObservedDeclaration.Row(CapabilityId,
                new HandlerShape().Inputs("field", "candidates", "center").Outputs("targets").Parameters("empty", "unloaded"),
                (context, outputs) => Area(context, outputs, session)));
    }

    /// <summary>The row's own read: the plan's field reference, the centre it carries or the field's own body
    /// would, and the candidates measured against it. The field is read first, so a plan naming a field this world
    /// does not hold is answered by its own policy before any candidate is observed.</summary>
    private static void Area(EvaluationContext context, EvaluationOutputs outputs, AreaFieldSession session)
    {
        var definition = session.Current.Definition(FieldId(context));
        if (definition == null) { Unloaded(context, outputs, "The named area field is not loaded in this world."); return; }
        if (!TryCenter(context, out var center))
        {
            Unloaded(context, outputs, "The named area field has no centre in this world: no `center` port carries one.");
            return;
        }
        var selected = Definite(context.Query, ObservedEvaluation.Candidates(context, "candidates"), definition, center);
        ObservedEvaluation.Targets(context, outputs, selected);
    }

    /// <summary>
    /// The candidates inside the definition's volume around the centre, in the identity order every collection row
    /// of this family answers with. One budgeted observation of the whole explicit candidate set, and a set the
    /// kernel could not observe completely is refused rather than measured — a selector that answered with the
    /// half it happened to see would silently drop the targets it never looked at.
    /// </summary>
    private static IReadOnlyList<EntityReference> Definite(RuntimeQuerySession query,
        IReadOnlyList<EntityReference> candidates, AreaFieldDefinition definition, IReadOnlyList<double> center)
        => ObservedSpaceNodes.IdentityOrder(ObservedSpaceNodes.ObserveCurrent(query, candidates)
            .Where(snapshot => definition.Contains(snapshot.Position, center))
            .Select(snapshot => snapshot.Ref));

    /// <summary>The id the row's `field` resource port carries. A plan compiles an author's own block reference,
    /// and a value an upstream step produced arrives as the frame's two-field form; the id is the instance either
    /// way, so both spellings are read here and the provider's own table decides whether this world holds it.</summary>
    private static string FieldId(EvaluationContext context)
    {
        var value = context.RequiredInput("field");
        if (value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(RuntimeAbiCodes.ResourceKind, "The field port carries no reference.");
        if (value.TryGetProperty("resourceKind", out _))
        {
            var resource = RuntimeJson.ResourceRefOf(value);
            if (resource.ResourceKind != AreaFieldDocument.ResourceKind)
                throw new RuntimeContractException(RuntimeAbiCodes.ResourceKind, resource.ResourceKind);
            return resource.ResourceId;
        }
        if (!value.TryGetProperty("id", out var compiled) || compiled.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-field", "field");
        return compiled.GetString()!;
    }

    /// <summary>The centre the row measures around, when the plan wired one. The port is optional in the catalog,
    /// and an absent one is not a zero: the field's own live body would have to answer, and this build has none, so
    /// the caller answers its `unloaded` policy instead of measuring around an origin nobody named.</summary>
    private static bool TryCenter(EvaluationContext context, out IReadOnlyList<double> center)
    {
        center = Array.Empty<double>();
        if (!context.Inputs.TryGetProperty("center", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return false;
        var coordinates = new double[3];
        var index = 0;
        foreach (var coordinate in value.EnumerateArray())
        {
            if (coordinate.ValueKind != JsonValueKind.Number || !coordinate.TryGetDouble(out var number)
                || !double.IsFinite(number)) return false;
            coordinates[index++] = number;
        }
        center = coordinates;
        return true;
    }

    /// <summary>The `unloaded` policy's answer: `fail` refuses the step with this row's own code, and the two
    /// members that ask for an empty selection answer exactly that. The empty answer a policy asks for is not run
    /// through the `empty` policy as if the field had been measured — the field was never read, and the two facts
    /// stay told apart.</summary>
    private static void Unloaded(EvaluationContext context, EvaluationOutputs outputs, string reason)
    {
        var policy = ObservedEvaluation.Policy(context.Parameters, "unloaded");
        if (policy == EmptySelectionPolicy.Fail) throw new RuntimeContractException(UnloadedCode, reason);
        outputs.Entities(0, Array.Empty<EntityReference>());
    }
}
