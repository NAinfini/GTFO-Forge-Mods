using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>One node's structural parameters, resolved once per dispatch from the plan's constant frame.
/// <see cref="ProfileIndex"/> is the position of the profile in the card's own member list, which is also the
/// index of its native member in <see cref="BulletProjectileContract.NativeMembers"/>.</summary>
internal struct BulletProjectileParameters
{
    public int ProfileIndex;
    public int RicochetCount;
    public int PierceCount;
    public bool RunHitTriggers;
}

/// <summary>
/// The `forge.action.combat.bullet_projectile` row: one flying weapon projectile whose body is a native projectile
/// prefab, whose flight is stepped by this provider and whose hits are submitted through the game's own bullet hit
/// entry. The binding, the support line and the handler shape are this provider's own; the row is the generated
/// block table's (<see cref="Blocks"/>).
///
/// The profile vocabulary maps one-to-one onto the game's own `ProjectileType` members; the mapping lives here, in
/// the game-free assembly, so it is tested without the game and the native half resolves each member once by name.
/// </summary>
internal static class BulletProjectileContract
{
    public const string CapabilityId = Blocks.ActionCombatBulletProjectile.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.bullet_projectile";
    public const string Handler = "gtfo.weapon.bullet_projectile";
    /// <summary>The row submits damage through the game's hit entry, the same combat submission the hitscan row
    /// is declared under.</summary>
    public const string Permission = ModuleDefinition.CombatReadPermission;

    /// <summary>The most bounces or pierces one projectile may declare: a number a plan cannot mean is refused.</summary>
    public const int MaximumBounces = 16;

    private static readonly JsonElement Row = Blocks.Capability(Blocks.ActionCombatBulletProjectile.Index);

    /// <summary>The card's own profile members, in the card's order.</summary>
    public static readonly IReadOnlyList<string> Profiles = Array.AsReadOnly(ShotParameters.Members(Row, "profile"));

    /// <summary>The native `ProjectileType` member each profile names, index for index with <see cref="Profiles"/>.
    /// A profile the table does not know makes the type initializer fail rather than a launch pick another one.</summary>
    public static readonly IReadOnlyList<string> NativeMembers = Array.AsReadOnly(Profiles.Select(NativeMember).ToArray());

    /// <summary>One profile's native member name.</summary>
    public static string NativeMember(string profile) => profile switch
    {
        "targeting_small" => "TargetingSmall",
        "targeting_medium" => "TargetingMedium",
        "targeting_large" => "TargetingLarge",
        "semi_targeting_quick" => "SemiTargetingQuick",
        "not_targeting_small_fast" => "NotTargetingSmallFast",
        "glue_flying" => "GlueFlying",
        "infection_bomb" => "InfectionBomb",
        _ => throw new InvalidOperationException("The reviewed bullet_projectile profile " + profile
            + " has no native ProjectileType member in this build.")
    };

    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("source", "origin", "direction", "amount", "speed", "gravity", "lifetime", "range", "hit_size",
            "homing_target", "homing_rate", "ignore")
        .Outputs("result", ProjectilePort)
        .Parameters("profile", "ricochet_count", "pierce_count", "run_hit_triggers");
    public static readonly HandlerSlot SourceSlot = Shape.InputSlot("source");
    public static readonly HandlerSlot OriginSlot = Shape.InputSlot("origin");
    public static readonly HandlerSlot DirectionSlot = Shape.InputSlot("direction");
    public static readonly HandlerSlot AmountSlot = Shape.InputSlot("amount");
    public static readonly HandlerSlot SpeedSlot = Shape.InputSlot("speed");
    public static readonly HandlerSlot GravitySlot = Shape.InputSlot("gravity");
    public static readonly HandlerSlot LifetimeSlot = Shape.InputSlot("lifetime");
    public static readonly HandlerSlot RangeSlot = Shape.InputSlot("range");
    public static readonly HandlerSlot HitSizeSlot = Shape.InputSlot("hit_size");
    public static readonly HandlerSlot HomingTargetSlot = Shape.InputSlot("homing_target");
    public static readonly HandlerSlot HomingRateSlot = Shape.InputSlot("homing_rate");
    public static readonly HandlerSlot IgnoreSlot = Shape.InputSlot("ignore");
    public static readonly HandlerSlot ProfileSlot = Shape.ParameterSlot("profile");
    public static readonly HandlerSlot RicochetCountSlot = Shape.ParameterSlot("ricochet_count");
    public static readonly HandlerSlot PierceCountSlot = Shape.ParameterSlot("pierce_count");
    public static readonly HandlerSlot RunHitTriggersSlot = Shape.ParameterSlot("run_hit_triggers");

    /// <summary>The output the launch hands the flight's `projectile` handle out on; <see cref="ProjectileStateContract"/>
    /// answers where that flight is.</summary>
    public const string ProjectilePort = "projectile";

    /// <summary>Reads the handler's pre-resolved parameter slots directly from its execution frame.</summary>
    public static bool TryRead(CommandContext context, out BulletProjectileParameters parsed, out string code)
    {
        parsed = new BulletProjectileParameters { RunHitTriggers = true };
        code = "";
        var profile = context.Text(ProfileSlot);
        var profileIndex = -1;
        for (var candidate = 0; candidate < Profiles.Count; candidate++)
            if (string.Equals(Profiles[candidate], profile, StringComparison.Ordinal)) profileIndex = candidate;
        if (profileIndex < 0) { code = ResultCode.ParameterInvalid; return false; }
        parsed.ProfileIndex = profileIndex;
        if (!TryCount(context, RicochetCountSlot, out parsed.RicochetCount, out code)) return false;
        if (!TryCount(context, PierceCountSlot, out parsed.PierceCount, out code)) return false;
        return ShotParameters.TryFlag(context, RunHitTriggersSlot, true, out parsed.RunHitTriggers, out code);
    }

    /// <summary>
    /// One node's structural parameters. `profile` is required by the card and is refused when absent; it arrives
    /// as the member's index in a compiled plan or its name in a hand-built frame. The two counts are absent-as-zero
    /// and bounded by <see cref="MaximumBounces"/>; `run_hit_triggers` absent is true, the same default the hitscan
    /// row gives its own switch. Any other value is refused with <see cref="ResultCode.ParameterInvalid"/>.
    /// </summary>
    public static bool TryRead(JsonElement parameters, out BulletProjectileParameters parsed, out string code)
    {
        parsed = new BulletProjectileParameters { RunHitTriggers = true };
        code = "";
        var bag = parameters.ValueKind == JsonValueKind.Object ? parameters : default;
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty("profile", out var declared)
            || declared.ValueKind == JsonValueKind.Null)
        { code = ResultCode.ParameterInvalid; return false; }
        var index = -1;
        if (declared.ValueKind == JsonValueKind.String)
        {
            var name = declared.GetString();
            for (var candidate = 0; candidate < Profiles.Count; candidate++)
                if (string.Equals(Profiles[candidate], name, StringComparison.Ordinal)) index = candidate;
        }
        else if (declared.ValueKind == JsonValueKind.Number && declared.TryGetInt32(out var number)
            && number >= 0 && number < Profiles.Count) index = number;
        if (index < 0) { code = ResultCode.ParameterInvalid; return false; }
        parsed.ProfileIndex = index;
        if (!TryCount(bag, "ricochet_count", out parsed.RicochetCount, out code)) return false;
        if (!TryCount(bag, "pierce_count", out parsed.PierceCount, out code)) return false;
        return ShotParameters.TryFlag(bag, "run_hit_triggers", true, out parsed.RunHitTriggers, out code);
    }

    private static bool TryCount(JsonElement bag, string id, out int value, out string code)
    {
        value = 0;
        code = "";
        if (bag.ValueKind != JsonValueKind.Object || !bag.TryGetProperty(id, out var declared)
            || declared.ValueKind == JsonValueKind.Null) return true;
        if (declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt32(out value)
            || value < 0 || value > MaximumBounces)
        {
            value = 0;
            code = ResultCode.ParameterInvalid;
            return false;
        }
        return true;
    }

    private static bool TryCount(CommandContext context, HandlerSlot slot, out int value, out string code)
    {
        value = 0;
        code = "";
        if (context.Kind(slot) is ValueKind.Missing or ValueKind.Null) return true;
        var declared = context.Integer(slot);
        if (declared is >= 0 and <= MaximumBounces)
        {
            value = (int)declared.Value;
            return true;
        }
        code = ResultCode.ParameterInvalid;
        return false;
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, Handler, "execute", new[] { Permission }, Shape)
    } : Array.Empty<ProviderBinding>();
}
