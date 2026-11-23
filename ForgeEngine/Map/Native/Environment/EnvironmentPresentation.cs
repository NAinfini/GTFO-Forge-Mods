using System;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>The Map contribution to Runtime's recipient state: the private model text and world labels each player
/// account was shown (<see cref="RecipientLedger"/>).</summary>
internal sealed class MapRecipientPresentationState : RecipientLedger
{
    internal const string StateKey = "forge.map.recipient-presentation";
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    private readonly IPlayerDirectory _players;

    internal MapRecipientPresentationState(IForgeEngine kernel, IPlayerDirectory players) : base(kernel, players, StateKey)
        => _players = players;

    protected override bool Handles(PresentationOutput request) => request.Provider == ModuleDefinition.ProviderId
        && request.BindingId is ModelTextContract.BindingId
            or ModuleDefinition.ProviderId + ".binding.world_label";

    protected override (HandlerSlot Audience, string Surface) Locate(PresentationOutput request, CommandContext context)
    {
        bool model = request.BindingId == ModelTextContract.BindingId;
        if (!context.TryEntity(model ? ModelTextContract.ModelSlot : WorldLabelContract.Shape.InputSlot("target"), out var target))
            throw new RuntimeContractException(InvalidCode, "Private display has no exact target.");
        return (model ? ModelTextContract.RecipientsSlot : WorldLabelContract.Shape.InputSlot("viewers"), (model ? "model:" : "label:") + target.Id);
    }

    protected override void LocalReplaced()
    {
        MapLabels.ClearWorldLabels("recipient-replaced");
        PlacedModelSession.Current?.ResetPrivateText();
    }

    protected override (bool Applied, string Code) Apply(Saved saved)
    {
        bool model = saved.Request.BindingId == ModelTextContract.BindingId;
        var inputs = saved.Inputs.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.Ordinal);
        inputs[model ? "recipients" : "viewers"] = RuntimeJson.From(new[] { LocalLife! });
        string targetPort = model ? "model" : "target";
        if (inputs.TryGetValue(targetPort, out var target))
        {
            var reference = target.Deserialize<EntityReference>(Json)!;
            if (EntityKinds.Is(reference, EntityKinds.MapObject)
                && PlacedModelAddress.TryParse(reference.Id[(EntityKinds.MapObject.Length + 1)..]) != null)
            {
                // Model life is assigned by the placed-model table, not always zero. Restore the same
                // authored model address to its current placed generation instead of forging that life.
                reference = PlacedModelSession.TextReference(reference.Id)
                    ?? throw new RuntimeContractException(ResultCode.ModelTextModelStale, "The retained authored model is not placed yet.");
                inputs[targetPort] = RuntimeJson.From(reference);
            }
            // Only authored persistent addresses and zone coordinates may be rebound to another world.
            // Captured vanilla instance handles retain their original epochs and fail explicitly.
            if (reference.LifeEpoch == 0 && (EntityKinds.Is(reference, EntityKinds.Zone)
                || EntityKinds.Is(reference, EntityKinds.MapObject)
                    && !reference.Id.Contains(":" + VanillaWorldObjects.Prefix, StringComparison.Ordinal)))
                inputs[targetPort] = RuntimeJson.From(reference with { WorldEpoch = Kernel.WorldEpoch });
        }
        bool applied = false;
        string? code = null;
        DomainStateChannel.RebuildRecipientContext(Kernel, saved.Request with { WorldEpoch = Kernel.WorldEpoch },
            RuntimeJson.From(inputs), saved.Parameters, context =>
            {
                // A rebuilt context has no row region, so the label's own answer is read without writing rows.
                if (model)
                {
                    var result = new ModelTextActions(_players).Handle(context);
                    applied = result.Status is Status.Succeeded or Status.Partial;
                    code = result.Code;
                }
                else
                {
                    var answer = MapLabels.WorldLabelAnswer(context, _players, out _);
                    applied = answer.Status is Status.Succeeded or Status.Partial;
                    code = answer.Code;
                }
            });
        return (applied, code ?? InvalidCode);
    }

    protected override void Restored(string surface, string code) => Report(surface, "restored", code);

    protected override void Pending(string surface, string code, Saved saved) => Report(surface, "pending", code, saved);

    /// <summary>One recovery step of a recipient's retained line. A step that stays pending names the inputs it was
    /// restoring (clipped), so a text that cannot be rendered says which text block and language it was.</summary>
    private void Report(string surface, string phase, string code, Saved? pending = null)
    {
        if (!Once.ShouldReport(surface + ":" + phase + ":" + code)) return;
        var level = phase is "restored" ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
        if (pending == null)
        {
            MapLog.Record(level, MapLogCodes.NativeReport, "Recipient presentation recovery.",
                RuntimeLogValue.Of("surface", surface), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", code));
            return;
        }
        var inputs = pending.Inputs.GetRawText();
        MapLog.Record(level, MapLogCodes.NativeReport, "Recipient presentation recovery.",
            RuntimeLogValue.Of("surface", surface), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", code),
            RuntimeLogValue.Of("inputs", inputs.Length <= 200 ? inputs : inputs[..200]));
    }
}
