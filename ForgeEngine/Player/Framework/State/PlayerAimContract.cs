using System;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The `forge.query.player.aim` row: where one player is aiming from and the direction they aim in, as a
/// point in metres and a unit vector.
///
/// The row answers the aim ray and nothing else: which targets lie along it, whether terrain blocks it and what is
/// launched along it belong to the range, line-of-sight and launch cards a plan wires the two ports into. The
/// native half reads the camera ray of the player this machine drives and the synced look of every other player
/// (see <c>GenericPlayerActions.ReadAim</c>); both are the game's own values and neither is recomputed here.
///
/// The read writes nothing. A player this process cannot resolve is refused by name, and so is a sample whose
/// origin is not finite or whose direction is not a usable vector — a zero direction handed on would read as "aim
/// straight along no axis" to every card downstream.</summary>
internal static class PlayerAimContract
{
    public const string CapabilityId = Blocks.QueryPlayerAim.Id;
    public const string HandlerName = "gtfo.player.aim";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.player.aim";

    /// <summary>The refusal an input that names something other than a player gets.</summary>
    public const string InputKindCode = "aim-input-kind";
    /// <summary>The refusal a player this provider no longer holds gets, or one whose aim did not read.</summary>
    public const string InputStaleCode = "aim-input-stale";
    /// <summary>The refusal a non-finite origin or an unusable direction gets.</summary>
    public const string UnreadableCode = "aim-unreadable";

    /// <summary>Below this length a direction carries no axis, so it is refused instead of normalised.</summary>
    private const double MinimumDirectionLength = 0.000001;

    public static readonly HandlerShape Shape = new HandlerShape().Inputs("player").Outputs("origin", "direction");

    /// <summary>One reading of a player's aim ray, as the native half answers it.</summary>
    public readonly record struct AimSample(double OriginX, double OriginY, double OriginZ,
        double DirectionX, double DirectionY, double DirectionZ);

    /// <summary>The one native read this row needs, handed in by the half that can make it.</summary>
    public readonly record struct AimReaders(Func<EntityReference, AimSample?> Read);

    public static EvaluatorHandler Evaluator(AimReaders readers) => (context, outputs) => Evaluate(context, outputs, readers);

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, AimReaders readers)
    {
        var player = RuntimeJson.Entity(context.RequiredInput("player"));
        if (!EntityKinds.Is(player, EntityKinds.Player))
            throw new RuntimeContractException(InputKindCode, "This provider reads the aim of a player, not of " + player.Id);
        var sample = readers.Read(player)
            ?? throw new RuntimeContractException(InputStaleCode, "The player is not current: " + player.Id);
        var length = Math.Sqrt(sample.DirectionX * sample.DirectionX + sample.DirectionY * sample.DirectionY
            + sample.DirectionZ * sample.DirectionZ);
        if (!double.IsFinite(sample.OriginX) || !double.IsFinite(sample.OriginY) || !double.IsFinite(sample.OriginZ)
            || !double.IsFinite(length) || length < MinimumDirectionLength)
            throw new RuntimeContractException(UnreadableCode, "The native aim ray did not read as a finite origin and a usable direction.");
        outputs.Vector(0, sample.OriginX, sample.OriginY, sample.OriginZ);
        outputs.Vector(1, sample.DirectionX / length, sample.DirectionY / length, sample.DirectionZ / length);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.QueryPlayerAim.Id, HandlerName, "observe", Array.Empty<string>(), Shape)
    };
}
