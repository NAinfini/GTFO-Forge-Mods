using System;

namespace ForgeEngine.Framework;

/// <summary>A standing policy consulted by native receivers, never a synchronous graph dispatch.</summary>
/// <summary>`Scale` applies <c>incoming * Factor - Flat</c>, in that order and never below zero: the percentage
/// reduction first, then the fixed one. `Flat` is 0 unless the plan wires the optional `flat` input.</summary>
public readonly record struct IncomingDamagePolicy(int Policy, float Floor, float Factor,
    EntityReference? Source, int? DamageKind, float Flat = 0)
{
    public const int Immune = 0, HealthFloor = 1, Scale = 2, Clear = 3;
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "floor", "factor", "source", "damage_kind", "flat").Outputs("result", "rejected").Parameters("policy");

    public static IncomingDamagePolicy Read(CommandContext context)
    {
        int policy = context.Text(Shape.ParameterSlot("policy")) switch
        {
            "immune" => Immune,
            "floor" => HealthFloor,
            "scale" => Scale,
            "clear" => Clear,
            _ => -1
        };
        if (policy < 0) throw new RuntimeContractException("policy-unsupported", "Unknown standing damage policy.");
        // Each number is read only by the policy that uses it: `floor` by the health floor, `factor` by scaling.
        double floor = 0, factor = 1;
        if (policy == HealthFloor)
        {
            floor = context.Number(Shape.InputSlot("floor")) ?? double.NaN;
            if (double.IsNaN(floor)) throw new RuntimeContractException("missing-field", "floor");
            if (floor < 0 || !float.IsFinite((float)floor))
                throw new RuntimeContractException("floor-out-of-range", "The native health floor must be a nonnegative Single.");
        }
        if (policy == Scale)
        {
            factor = context.Number(Shape.InputSlot("factor")) ?? double.NaN;
            if (double.IsNaN(factor)) throw new RuntimeContractException("missing-field", "factor");
            if (factor < 0 || !float.IsFinite((float)factor))
                throw new RuntimeContractException("factor-out-of-range", "The native damage factor must be a nonnegative Single.");
        }
        EntityReference? source = null;
        if (context.Present(Shape.InputSlot("source")))
        {
            if (!context.TryEntity(Shape.InputSlot("source"), out var value))
                throw new RuntimeContractException("source-filter-invalid", "An exact source entity is required.");
            source = value;
        }
        int? kind = null;
        if (context.Present(Shape.InputSlot("damage_kind")))
        {
            string? member = context.Text(Shape.InputSlot("damage_kind"));
            var members = RuntimeEnumSets.Members("damage_kind");
            for (int index = 0; index < members.Count; index++) if (members[index] == member) { kind = index; break; }
            if (kind == null) throw new RuntimeContractException("damage-kind-invalid", "An existing damage kind is required.");
        }
        double flat = 0;
        if (context.Present(Shape.InputSlot("flat")))
        {
            flat = context.Number(Shape.InputSlot("flat")) ?? double.NaN;
            if (!(flat >= 0) || !float.IsFinite((float)flat))
                throw new RuntimeContractException("flat-out-of-range", "The fixed damage reduction must be a nonnegative Single.");
        }
        return new(policy, (float)floor, (float)factor, source, kind, (float)flat);
    }

    public bool Matches(EntityReference? source, int? kind)
        => (Source == null || Source == source) && (DamageKind == null || DamageKind == kind);

    public float Adjust(float incoming, float health)
    {
        float adjusted = Policy switch
        {
            Immune => 0,
            HealthFloor => float.IsFinite(health) ? Math.Min(incoming, Math.Max(0, health - Floor)) : incoming,
            Scale => Math.Max(0, incoming * Factor - Flat),
            Clear => incoming,
            _ => throw new RuntimeContractException("policy-unsupported", "Unknown standing damage policy.")
        };
        if (!float.IsFinite(adjusted) || adjusted < 0)
            throw new RuntimeContractException("factor-out-of-range", "The adjusted native damage is not a nonnegative Single.");
        return adjusted;
    }
}

/// <summary>How one recipient of the standing damage rule ended: the row's status and commit state, the code that names
/// why, and what the rule it replaced had blocked.</summary>
public readonly record struct IncomingDamageReceipt(Status Status, Commit Commit, string Code, double Rejected);

/// <summary>The player domain's recipient/restore adapter, published through the existing typed service registry.
/// The single damage-rule action can route explicit player recipients without taking ownership of player state.
/// The text <c>Install</c>, <c>Restore</c> and <c>InstallEveryPlayer</c> take is the caller's own lease name: a
/// caller outside the kernel's handle pool names its rule with it and restores by the same name. It is never a
/// kernel handle's spelling — a rule filed under a handle goes through the player domain's own typed entry.</summary>
public sealed record IncomingDamageReceiver(
    Func<EntityReference, IncomingDamagePolicy, string?, IncomingDamageReceipt> Install,
    Action<string> Restore,
    Action Clear,
    Func<IncomingDamagePolicy, string, IncomingDamageReceipt> InstallEveryPlayer,
    Func<Action<ReducedIncomingDamage>, IDisposable> ObserveReduced);

/// <summary>One incoming damage a standing policy reduced: the damage kind and enemy attack kind indexes that the
/// `damage_kind` and `player_attack_kind` sets declare (null when the native entry names none), the source agent's type and
/// name, and the amount before and after the policy.</summary>
public readonly record struct ReducedIncomingDamage(int? DamageKind, int? AttackKind, string Source, float Incoming, float Adjusted);
