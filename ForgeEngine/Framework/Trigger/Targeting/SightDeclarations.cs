using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The obstruction set one line-of-sight read is asked about, in the catalog's own member order: the level's
/// own geometry, or the set the game's sentry detection sees targets through.</summary>
internal enum SightBlockers
{
    World,
    SentryDetection
}

/// <summary>One line-of-sight read of the loaded world: true when nothing of the named obstruction set lies on the
/// segment between the two points. The native half answers it with the game's own cast; this assembly names no game
/// type, so the row is declared only by a registration that really has one.</summary>
internal delegate bool LineOfSightProbe(double fromX, double fromY, double fromZ, double toX, double toY, double toZ,
    SightBlockers blockers);

/// <summary>`forge.query.space.line_of_sight`: one obstruction check between two points. It answers whether the
/// segment is clear and selects nothing — which points to test and what to do with the answer are the plan's own
/// wiring. The row reads the world, so it only exists in a registration that was handed a probe: a process with no
/// game to cast against declares no binding rather than a row that could only refuse.</summary>
internal static class SightDeclarations
{
    public const string CapabilityId = Blocks.QuerySpaceLineOfSight.Id;

    public static ObservedFamily Family(LineOfSightProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return ObservedFamily.Declare(ObservedDeclaration.Row(CapabilityId,
            new HandlerShape().Inputs("from", "to").Outputs("visible").Parameters("blockers"),
            (context, outputs) => Evaluate(context, outputs, probe)));
    }

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, LineOfSightProbe probe)
    {
        var from = Point(context, "from");
        var to = Point(context, "to");
        var blockers = ObservedEvaluation.ParameterText(context, "blockers") switch
        {
            "world" => SightBlockers.World,
            "sentry_detection" => SightBlockers.SentryDetection,
            var other => throw new RuntimeContractException("pure-operation", "Unknown blockers member: " + other)
        };
        outputs.Boolean(0, probe(from.X, from.Y, from.Z, to.X, to.Y, to.Z, blockers));
    }

    /// <summary>One required point of the frame: three finite numbers, or a refusal by name. A point that does not
    /// read is never answered as the origin, because a cast from nowhere would read like an observed answer.</summary>
    private static (double X, double Y, double Z) Point(EvaluationContext context, string port)
    {
        var value = context.RequiredInput(port);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new RuntimeContractException("missing-field", "Point is not three numbers: " + port);
        var x = Component(value[0], port);
        var y = Component(value[1], port);
        var z = Component(value[2], port);
        return (x, y, z);
    }

    private static double Component(JsonElement value, string port)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new RuntimeContractException("pure-invalid-number", "Point component is not a finite number: " + port);
}
