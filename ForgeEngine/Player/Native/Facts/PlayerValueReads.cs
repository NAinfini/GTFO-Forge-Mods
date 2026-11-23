using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgePlayer.Native;

/// <summary>What the tool row reads: the held item the two ammunition numbers belong to, the class-ammunition
/// pool the game itself spends as tool energy, and the backpack's own consumable stacks.</summary>
internal readonly record struct PlayerTool(EntityReference? Item, int Ammo, int AmmoMaximum, int Count, int Stacks);
internal readonly record struct PlayerBackpackSlot(bool Occupied, long? ItemId, long? GearId, int? Status, bool Loaded)
{
    internal void Answer(EvaluationOutputs outputs)
    {
        outputs.Boolean(0, Occupied);
        if (ItemId is { } item) outputs.Integer(1, item); else outputs.Null(1);
        if (GearId is { } gear) outputs.Integer(2, gear); else outputs.Null(2);
        if (Status is { } status) outputs.Integer(3, status); else outputs.Null(3);
        outputs.Boolean(4, Loaded);
    }
}

/// <summary>The seven player-domain Data read operators not already owned by ForgeEngine.
///
/// Every one of them is a `query` step, and every one of them reads the world exactly once through the kernel's
/// budgeted session: the player it is about is resolved through `TrySnapshot`, which is what charges the query
/// budget, proves the reference still names a life and carries shared state such as life-state and position. The
/// fields the shared snapshot does not carry — infection, the wielded gear, its
/// ammunition and the carried item — are read by this provider's own native half from the same life the snapshot
/// just verified; that read is not a second world query and adds no second identity check, which is why this file
/// takes the reference from the snapshot and never from the frame a second time.
///
/// A value that cannot be read is a refusal with a code, never a zero and never an empty answer: a player
/// holding no weapon has no clip (`no-wielded-gear`), and a source that is not attached at all refuses by name
/// instead of answering from a table that was never filled.</summary>
internal sealed class PlayerValueReads
{
    /// <summary>The code a value read answers when the provider publishes no source. It is a refusal and not an
    /// empty value: the row exists, this process just cannot read it.</summary>
    internal const string SourceUnavailableCode = "player-value-source-unavailable";
    internal const string NoWieldedGearCode = "no-wielded-gear";

    internal static PlayerValueReads? Installed { get; private set; }

    private readonly NativePlayerValues _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    internal PlayerValueReads(NativePlayerValues source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    internal static PlayerValueReads Attach(NativePlayerValues source)
    {
        Installed = new PlayerValueReads(source);
        return Installed;
    }

    internal static void Detach(PlayerValueReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table this provider's registration composes: one entry per value row, each name
    /// declared with a shape in `PlayerStateContract.ValueShapes`.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [PlayerStateContract.InfectionValueHandler] = Evaluate(Infection),
        [PlayerStateContract.WieldedGearValueHandler] = Evaluate(WieldedGear),
        [PlayerStateContract.CarriedItemValueHandler] = Evaluate(CarriedItem),
        [PlayerStateContract.ToolValueHandler] = Evaluate(Tool),
        [PlayerBackpackContract.QueryHandler] = (context, outputs) =>
        {
            if (Installed is not { } reads) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
            reads.CheckThread();
            var (reference, _) = Read(context);
            if (!context.Inputs.TryGetProperty("slot", out var raw) || !raw.TryGetInt32(out int slot))
                throw new RuntimeContractException("backpack-slot-invalid", "A native inventory slot is required.");
            if (!reads._source.TryBackpackSlot(reference, slot, out var value, out var code))
                throw new RuntimeContractException(code, "The native backpack slot could not be read.");
            value.Answer(outputs);
        }
    };

    private static EvaluatorHandler Evaluate(Action<PlayerValueReads, EntityReference, RuntimeEntitySnapshot, EvaluationOutputs> answer)
        => (context, outputs) =>
        {
            if (Installed is not { } reads) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
            reads.CheckThread();
            var (reference, snapshot) = Read(context);
            answer(reads, reference, snapshot, outputs);
        };

    /// <summary>The one budgeted read every value row starts with: the player the row is about, resolved through
    /// the session's own snapshot read. The row's `player` port is required, so a frame without one is refused by
    /// name; the entity is the row's, never the event's or the mount's.</summary>
    private static (EntityReference Reference, RuntimeEntitySnapshot Snapshot) Read(EvaluationContext context)
    {
        if (!context.Inputs.TryGetProperty("player", out var value) || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(ResultCode.MissingField, "player");
        var reference = RuntimeJson.Entity(value);
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Player read failed: " + code);
        return (reference, snapshot!);
    }

    // The seven answers, each over the one budgeted read the evaluator already made.

    internal static void AnswerInfection(NativePlayerValues? source, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryInfection(reference, out var value, out var code))
            throw new RuntimeContractException(code, "Infection read failed: " + code);
        PlayerStateContract.InfectionAnswer(value, outputs);
    }

    internal static void AnswerWieldedGear(NativePlayerValues? source, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryWieldedGear(reference, out var equipment, out var slot, out var code))
            throw new RuntimeContractException(code, "Wielded gear read failed: " + code);
        PlayerStateContract.WieldedGearAnswer(equipment, slot, outputs);
    }

    internal static void AnswerCarriedItem(NativePlayerValues? source, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryCarriedItem(reference, out var item, out var code))
            throw new RuntimeContractException(code, "Carried item read failed: " + code);
        PlayerStateContract.CarriedItemAnswer(item, outputs);
    }

    /// <summary>The tool row's answer: the class-ammunition pool of the item the player holds and the backpack's
    /// consumable stacks, read by the source in one call so the five ports describe one moment. Neither half is
    /// derived from the other: a player holding a weapon that is not a tool still has its class pool, and a
    /// backpack with no pocket item answers zero rather than a missing port.</summary>
    internal static void AnswerTool(NativePlayerValues? source, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
    {
        if (source == null) throw new RuntimeContractException(SourceUnavailableCode, "No player value source is attached.");
        if (!source.TryTool(reference, out var tool, out var code))
            throw new RuntimeContractException(code, "Tool read failed: " + code);
        PlayerStateContract.ToolAnswer(tool.Item, tool.Ammo, tool.AmmoMaximum, tool.Count, tool.Stacks, outputs);
    }

    private static void Infection(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerInfection(reads._source, reference, snapshot, outputs);

    private static void WieldedGear(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerWieldedGear(reads._source, reference, snapshot, outputs);

    private static void CarriedItem(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerCarriedItem(reads._source, reference, snapshot, outputs);

    private static void Tool(PlayerValueReads reads, EntityReference reference, RuntimeEntitySnapshot snapshot, EvaluationOutputs outputs)
        => AnswerTool(reads._source, reference, snapshot, outputs);

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Player value reads require the runtime's own simulation thread.");
    }
}
