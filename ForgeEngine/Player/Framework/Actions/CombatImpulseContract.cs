using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The `forge.action.combat.impulse` Outcome: apply one pure push to player or enemy recipients.
///
/// The behavior graph computes the direction and scalar strength before this Outcome runs. Normalization, distance
/// falloff, source exclusion and target selection are composition concerns and stay in Operators/Selectors rather
/// than being duplicated as hidden action parameters. No damage entry is submitted. Two bindings answer the row,
/// chosen by the recipients' kind: the Player module writes `PlayerLocomotion.AddExternalPushForce` on the machine
/// that simulates the player, and the Enemy module submits the receiver's force channel on the host. Both shape
/// the force with <see cref="TryForce"/>, so one request pushes either kind by the same vector.</summary>
internal static class CombatImpulseContract
{
    public const string CapabilityId = Blocks.ActionCombatImpulse.Id;
    public const string HandlerName = "gtfo.combat.impulse";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.combat.impulse";

    /// <summary>The permission this row writes under. A push moves somebody else's body, so a plan declares that
    /// it may apply an impulse before it can.</summary>
    public const string Permission = "combat.impulse";

    /// <summary>The one shape of the handler: the player recipients plus the direction and strength Data already
    /// computed by the behavior graph. Falloff, normalization and source filtering are separate Operators.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "direction", "strength", "vertical_scale", "horizontal_cap", "vertical_cap")
        .Outputs("result").Parameters("normalize");

    public static readonly HandlerSlot TargetsSlot = Shape.InputSlot("targets");
    public static readonly HandlerSlot DirectionSlot = Shape.InputSlot("direction");
    public static readonly HandlerSlot StrengthSlot = Shape.InputSlot("strength");
    public static readonly HandlerSlot VerticalScaleSlot = Shape.InputSlot("vertical_scale");
    public static readonly HandlerSlot HorizontalCapSlot = Shape.InputSlot("horizontal_cap");
    public static readonly HandlerSlot VerticalCapSlot = Shape.InputSlot("vertical_cap");
    public static readonly HandlerSlot NormalizeSlot = Shape.ParameterSlot("normalize");

    /// <summary>The force vector one request submits, shaped from the row's ports: the direction at `strength`, the
    /// vertical part scaled, renormalized to `strength` when asked, then bounded by the two caps — the order the
    /// row's own description reads in. A refusal names the port that made the request unusable.</summary>
    public static bool TryForce(CommandContext context, out double x, out double y, out double z, out string? code)
    {
        x = y = z = 0;
        code = null;
        double strength = context.Number(StrengthSlot) ?? double.NaN;
        if (!double.IsFinite(strength)) { code = ResultCode.ImpulseStrengthRequired; return false; }
        // Strength is the push's size (zero pushes nothing); the direction carries the sign. No ceiling: a force the
        // channel cannot carry is refused by the finite check at the end and by each binding's float conversion.
        if (strength < 0) { code = ResultCode.StrengthOutOfRange; return false; }
        if (context.Vector(DirectionSlot) is not { } direction) { code = ResultCode.ImpulseDirectionInvalid; return false; }
        double length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        if (!double.IsFinite(length) || length <= 0) { code = ResultCode.ImpulseDirectionInvalid; return false; }
        // The vertical scale reshapes the direction, so a scale that is not a number is a direction refusal.
        var verticalScale = context.Number(VerticalScaleSlot);
        if (context.Present(VerticalScaleSlot) && verticalScale == null) { code = ResultCode.ImpulseDirectionInvalid; return false; }
        // A cap that is not a number, or a negative one, names no bound; zero is the row's own "no cap".
        var horizontalCap = context.Number(HorizontalCapSlot);
        var verticalCap = context.Number(VerticalCapSlot);
        if (context.Present(HorizontalCapSlot) && horizontalCap == null || context.Present(VerticalCapSlot) && verticalCap == null
            || horizontalCap < 0 || verticalCap < 0) { code = ResultCode.InvalidCap; return false; }
        x = direction.X / length * strength;
        y = direction.Y / length * strength * (verticalScale ?? 1);
        z = direction.Z / length * strength;
        if (context.Boolean(NormalizeSlot) == true)
        {
            double magnitude = Math.Sqrt(x * x + y * y + z * z);
            if (magnitude > 0) { x *= strength / magnitude; y *= strength / magnitude; z *= strength / magnitude; }
        }
        if (horizontalCap is > 0 and var horizontalBound)
        {
            double horizontal = Math.Sqrt(x * x + z * z);
            if (horizontal > horizontalBound) { x *= horizontalBound / horizontal; z *= horizontalBound / horizontal; }
        }
        if (verticalCap is > 0 and var verticalBound) y = Math.Clamp(y, -verticalBound, verticalBound);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) { code = ResultCode.ImpulseDirectionInvalid; return false; }
        return true;
    }

    /// <summary>The owner-tier resolver table for this row: the session of the player the first target names (the
    /// push is added to the body on the machine that simulates it, and the engine then replicates the resulting
    /// movement), or the host's for an enemy. A registration without a resolver carries no answer, and the kernel
    /// refuses the step with `owner-unsupported`.</summary>
    public static IReadOnlyDictionary<string, Func<EntityReference, string?>> OwnerSessions(Func<EntityReference, string?> holder)
        => new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal) { [CapabilityId] = holder };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionCombatImpulse.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
