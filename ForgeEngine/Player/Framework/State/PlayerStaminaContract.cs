using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The `forge.action.player.stamina_change` row (ruling R1, generic-player batch): one immediate change to
/// one player's stamina, as an absolute value or as a signed amount.
///
/// Stamina is a value, not a status, and it is not replicated: `PlayerStamina` keeps `m_currentStamina` in the
/// process that simulates the player (movement and melee spend it there) and none of its entries sends a packet.
/// So the row is the `owner` tier: the host decides when and for whom, and the machine of the player named by the
/// `target` port runs the handler. The kernel routes one subject to one holder and takes one receipt, so a row that
/// changes several players is a `for_each` over the players, not a many-recipient port.
///
/// The handler takes the game's own request where its semantics match and a minimal adapter where nothing does:
/// a pure subtraction without a cap is `PlayerStamina.UseStamina`, a change that ends at full stamina is
/// `PlayerStamina.ResetStamina`, and everything else (an arbitrary set, an add, a result clamped by `cap`) has no
/// equivalent native request and is one write of the same member those requests end in. The result reports the
/// change the receiver's own readback showed, never the one that was asked for.
///
/// The ruling's "pause natural regeneration" half of the same idea is a duration and belongs to the continuous
/// batch; the `AllowRegen` member exists and is deliberately not written here.
///
/// `operation` is a structural parameter and not an input, so the whole command is refused when it names an
/// operation this handler does not implement. `amount` is an input, in the native `0..1` ratio, so a plan can
/// compute it from a weapon's own numbers instead of hard-coding one.</summary>
internal static class PlayerStaminaContract
{
    public const string CapabilityId = Blocks.ActionPlayerStaminaChange.Id;
    public const string HandlerName = "gtfo.player.stamina_change";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.player.stamina_change";

    /// <summary>The permission this row writes under: stamina belongs to a player, so a plan declares that it may
    /// change that player's own reserve before it can.</summary>
    public const string Permission = "stamina.modify";

    /// <summary>The three operations, in the order the row documents them. `set` writes the amount, `add` and
    /// `subtract` move the current value by it.</summary>
    public static readonly string[] Operations = { "set", "add", "subtract" };

    /// <summary>The one shape of the handler: the recipient, the amount, and the operation parameter the whole
    /// command is validated against.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("target", "amount", "cap", "amount_in_combat").Outputs("result")
        .Parameters("operation", "reset_recovery", "reset_recovery_in_combat");

    public static readonly HandlerSlot TargetSlot = Shape.InputSlot("target");
    public static readonly HandlerSlot AmountSlot = Shape.InputSlot("amount");
    public static readonly HandlerSlot CapSlot = Shape.InputSlot("cap");
    /// <summary>The in-combat half of the game's own cost (`ActionCost.baseStaminaCostInCombat`); without it both
    /// halves carry `amount`.</summary>
    public static readonly HandlerSlot AmountInCombatSlot = Shape.InputSlot("amount_in_combat");
    public static readonly HandlerSlot OperationSlot = Shape.ParameterSlot("operation");
    /// <summary>`ActionCost.resetRestingTimerOutOfCombat` / `resetRestingTimerInCombat`.</summary>
    public static readonly HandlerSlot ResetRecoverySlot = Shape.ParameterSlot("reset_recovery");
    public static readonly HandlerSlot ResetRecoveryInCombatSlot = Shape.ParameterSlot("reset_recovery_in_combat");

    /// <summary>The owner-tier resolver table for this row: the session of the player the step names. A
    /// registration without a resolver carries no answer, and the kernel refuses the step with `owner-holder`.</summary>
    public static IReadOnlyDictionary<string, Func<EntityReference, string?>> OwnerSessions(Func<EntityReference, string?> holder)
        => new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal) { [CapabilityId] = holder };
    // ------------------------------------------------------------------ the read

    public const string ValueHandlerName = "gtfo.player.stamina";
    public const string ValueBindingId = ModuleDefinition.ProviderId + ".binding.query.player.stamina";

    /// <summary>The refusal an input that names something other than a player gets.</summary>
    public const string ValueInputKindCode = "stamina-input-kind";
    /// <summary>The refusal a player this provider no longer holds gets, or one whose stamina half did not
    /// read.</summary>
    public const string ValueInputStaleCode = "stamina-input-stale";
    /// <summary>The refusal a native reading that is not a finite number gets. A reserve that did not read is not
    /// a reserve of zero.</summary>
    public const string ValueUnreadableCode = "stamina-value-unreadable";

    /// <summary>The read's ports: the player it is about and the three numbers it answers.</summary>
    public static readonly HandlerShape ValueShape = new HandlerShape()
        .Inputs("player").Outputs("value", "maximum", "fraction");

    /// <summary>One reading of a player's reserve, as the native half answers it: the three members, each read
    /// and none derived.</summary>
    public readonly record struct StaminaSample(double Value, double Maximum, double Fraction);

    /// <summary>The one native read this row needs, handed in by the half that can make it.</summary>
    public readonly record struct StaminaReaders(Func<EntityReference, StaminaSample?> Read);

    /// <summary>The evaluator, built from the native read. A refusal is an exception carrying this row's own
    /// code, which the runtime turns into the query step's failure.</summary>
    public static EvaluatorHandler ValueEvaluator(StaminaReaders readers) => (context, outputs) => ReadValue(context, outputs, readers);

    private static void ReadValue(EvaluationContext context, EvaluationOutputs outputs, StaminaReaders readers)
    {
        var player = RuntimeJson.Entity(context.RequiredInput("player"));
        if (!EntityKinds.Is(player, EntityKinds.Player))
            throw new RuntimeContractException(ValueInputKindCode, "This provider reads the stamina of a player, not of " + player.Id);
        var sample = readers.Read(player)
            ?? throw new RuntimeContractException(ValueInputStaleCode, "The player is not current: " + player.Id);
        if (!double.IsFinite(sample.Value) || !double.IsFinite(sample.Maximum) || !double.IsFinite(sample.Fraction))
            throw new RuntimeContractException(ValueUnreadableCode, "The native stamina members did not read as finite numbers.");
        outputs.Number(0, sample.Value); outputs.Number(1, sample.Maximum); outputs.Number(2, sample.Fraction);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPlayerStaminaChange.Id, HandlerName, "execute", new[] { Permission }, Shape),
        new ProviderBinding(ValueBindingId, Blocks.QueryPlayerStamina.Id, ValueHandlerName, "observe", Array.Empty<string>(), ValueShape)
    };
}
