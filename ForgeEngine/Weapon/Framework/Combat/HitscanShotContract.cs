using System;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>What one ray does when its first impact is world geometry rather than a damageable target. `none`
/// submits the hit as it landed, `avoid` re-casts once from the origin along the reflected direction so the shot
/// clears the wall, and `ricochet` continues from the impact point along the reflected direction.</summary>
internal enum HitscanWallHandling
{
    None,
    Avoid,
    Ricochet
}

/// <summary>One node's structural parameters, resolved once per dispatch from the plan's own constant frame. The
/// offsets are read from the immutable input text without a fixed fan buffer or a per-shot allocation.</summary>
internal struct HitscanShotParameters
{
    public string? Offsets;
    public int OffsetPairs;
    public bool IncludeSelf;
    public bool IgnoreFalloff;
    public bool RunHitTriggers;
    public HitscanWallHandling WallHandling;
    /// <summary>Whether the native hit body may apply its own directional (from-behind) damage bonus. The row's
    /// own default is `true`, which is what the game's own hit entry defaults to; `false` turns the bonus off for
    /// every hit this node submits.</summary>
    public bool DirectionalBonus;
}

/// <summary>
/// The `forge.action.combat.hitscan_shot` row: one native hitscan shot group. The binding, the support line and
/// the handler shape are written here; the row is the generated block table's (<see cref="Blocks"/>), which is
/// the one place a port can be renamed.
///
/// <b>Why the parameters are read here and not in the native body.</b> A structural parameter arrives in the
/// plan's constant frame as JSON, and the body has to turn it into values before any game object is touched. The
/// reading is a pure function of that frame — it names no game type and allocates nothing — so it lives in the
/// game-free assembly, is exercised by this package's own tests, and the native half only consumes the struct.
///
/// <b>The offsets format.</b> `offsets` is `"x y, x y, …"`: comma-separated pairs of degrees, each pair's two
/// numbers separated by whitespace. It is a string and not a list port because the plan's constant frame carries
/// scalars. The frame's string budget bounds the text; tokens outside the finite Single range are refused.
/// `ray i` uses pair `i % pairs` — the same modulo walk the
/// ledger's Shrapnel source uses for its own offsets — so one pair repeats across the requested count.
/// </summary>
internal static class HitscanShotContract
{
    /// <summary>The catalog's own row id.</summary>
    public const string CapabilityId = Blocks.ActionCombatHitscanShot.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.hitscan_shot";
    /// <summary>The handler name the binding resolves to, and the key its shape is registered under.</summary>
    public const string Handler = "gtfo.weapon.hitscan_shot";

    /// <summary>The permission the row is submitted under: it reads the shot's own combat context and submits
    /// damage through the game's hit entry, which is the same combat reading the shot and hit facts carry.</summary>
    public const string Permission = ModuleDefinition.CombatReadPermission;

    private static readonly JsonElement Row = Blocks.Capability(Blocks.ActionCombatHitscanShot.Index);

    /// <summary>The card's own member lists, read back from the block table's row. A compiled
    /// plan writes a structural enum parameter as the member's index, so the reader has to turn that index into
    /// the member the card declares; reading the list from the row keeps one declaration of the vocabulary.</summary>
    private static readonly string[] WallHandlingMembers = ShotParameters.Members(Row, "wall_handling");

    /// <summary>The ports the handler really reads, resolved at registration against the row's own ports: the
    /// source, the ray's origin, direction, count, amount, spread and range, the entities to ignore, the aim
    /// offset scale and the wall-handling reach, with the six structural parameters the body reads.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("source", "origin", "direction", "count", "amount", "spread", "range", "ignore",
            "aim_offset_scale", "wall_handling_distance")
        .Outputs("result")
        .Parameters("offsets", "include_self", "ignore_falloff", "run_hit_triggers", "wall_handling",
            "directional_bonus");
    public static readonly HandlerSlot SourceSlot = Shape.InputSlot("source");
    public static readonly HandlerSlot OriginSlot = Shape.InputSlot("origin");
    public static readonly HandlerSlot DirectionSlot = Shape.InputSlot("direction");
    public static readonly HandlerSlot CountSlot = Shape.InputSlot("count");
    public static readonly HandlerSlot AmountSlot = Shape.InputSlot("amount");
    public static readonly HandlerSlot SpreadSlot = Shape.InputSlot("spread");
    public static readonly HandlerSlot RangeSlot = Shape.InputSlot("range");
    public static readonly HandlerSlot IgnoreSlot = Shape.InputSlot("ignore");
    public static readonly HandlerSlot AimOffsetScaleSlot = Shape.InputSlot("aim_offset_scale");
    public static readonly HandlerSlot WallHandlingDistanceSlot = Shape.InputSlot("wall_handling_distance");
    public static readonly HandlerSlot OffsetsSlot = Shape.ParameterSlot("offsets");
    public static readonly HandlerSlot IncludeSelfSlot = Shape.ParameterSlot("include_self");
    public static readonly HandlerSlot IgnoreFalloffSlot = Shape.ParameterSlot("ignore_falloff");
    public static readonly HandlerSlot RunHitTriggersSlot = Shape.ParameterSlot("run_hit_triggers");
    public static readonly HandlerSlot WallHandlingSlot = Shape.ParameterSlot("wall_handling");
    public static readonly HandlerSlot DirectionalBonusSlot = Shape.ParameterSlot("directional_bonus");

    /// <summary>Reads the handler's pre-resolved parameter slots directly from its execution frame.</summary>
    public static bool TryRead(CommandContext context, out HitscanShotParameters parsed,
        out string code)
    {
        parsed = new HitscanShotParameters { RunHitTriggers = true, DirectionalBonus = true };
        code = "";
        if (context.Kind(OffsetsSlot) is not (ValueKind.Missing or ValueKind.Null))
        {
            var declared = context.Text(OffsetsSlot);
            if (declared == null || !TryParseOffsets(declared, out var pairs, out code)) return false;
            parsed.Offsets = declared;
            parsed.OffsetPairs = pairs;
        }
        if (!ShotParameters.TryEnum(context, WallHandlingSlot, WallHandlingMembers,
                out var wallHandling, out code)) return false;
        switch (wallHandling)
        {
            case "none": parsed.WallHandling = HitscanWallHandling.None; break;
            case "avoid": parsed.WallHandling = HitscanWallHandling.Avoid; break;
            case "ricochet": parsed.WallHandling = HitscanWallHandling.Ricochet; break;
            default: code = ResultCode.ParameterInvalid; return false;
        }
        if (!ShotParameters.TryFlag(context, IncludeSelfSlot, false, out parsed.IncludeSelf, out code)) return false;
        if (!ShotParameters.TryFlag(context, IgnoreFalloffSlot, false, out parsed.IgnoreFalloff, out code)) return false;
        if (!ShotParameters.TryFlag(context, RunHitTriggersSlot, true, out parsed.RunHitTriggers, out code)) return false;
        return ShotParameters.TryFlag(context, DirectionalBonusSlot, true,
            out parsed.DirectionalBonus, out code);
    }

    /// <summary>
    /// One node's structural parameters. An absent key is the row's own default — the first member of the
    /// parameter's own list, which is also what the compiler materializes an omitted optional enum to, `false`
    /// for the two opt-ins, `true` for hit triggers and the directional bonus, and `none` for wall handling — and
    /// a present value that is
    /// not a member of its vocabulary is refused by code rather than coerced. A structural enum arrives as the
    /// member's index in a compiled plan and as the member's name in a hand-built frame; both are read, and the
    /// index is resolved against the card's own list. Offsets retain the immutable input text and its pair count.
    /// </summary>
    public static bool TryRead(JsonElement parameters, out HitscanShotParameters parsed, out string code)
    {
        parsed = new HitscanShotParameters { RunHitTriggers = true, DirectionalBonus = true };
        code = "";
        var bag = parameters.ValueKind == JsonValueKind.Object ? parameters : default;
        if (bag.ValueKind == JsonValueKind.Object
            && bag.TryGetProperty("offsets", out var declared) && declared.ValueKind != JsonValueKind.Null)
        {
            if (declared.ValueKind != JsonValueKind.String
                || !TryParseOffsets(declared.GetString(), out var pairs, out code)) return false;
            parsed.Offsets = declared.GetString();
            parsed.OffsetPairs = pairs;
        }
        if (!ShotParameters.TryEnum(bag, "wall_handling", WallHandlingMembers, out var wallHandling, out code)) return false;
        switch (wallHandling)
        {
            case "none": parsed.WallHandling = HitscanWallHandling.None; break;
            case "avoid": parsed.WallHandling = HitscanWallHandling.Avoid; break;
            case "ricochet": parsed.WallHandling = HitscanWallHandling.Ricochet; break;
            default: code = ResultCode.ParameterInvalid; return false;
        }
        if (!ShotParameters.TryFlag(bag, "include_self", false, out parsed.IncludeSelf, out code)) return false;
        if (!ShotParameters.TryFlag(bag, "ignore_falloff", false, out parsed.IgnoreFalloff, out code)) return false;
        if (!ShotParameters.TryFlag(bag, "run_hit_triggers", true, out parsed.RunHitTriggers, out code)) return false;
        if (!ShotParameters.TryFlag(bag, "directional_bonus", true, out parsed.DirectionalBonus, out code)) return false;
        return true;
    }

    /// <summary>
    /// The `offsets` value as pairs of degrees, with no heap allocation: the text is walked once, each pair's two
    /// numbers are read from their own slice. An empty or
    /// absent value is zero pairs, which is a node that declared no fan and not a malformed one.
    /// </summary>
    public static bool TryParseOffsets(string? text, out int pairs, out string code)
    {
        pairs = 0;
        code = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        var span = text.AsSpan();
        var index = 0;
        while (true)
        {
            while (index < span.Length && (span[index] == ',' || char.IsWhiteSpace(span[index]))) index++;
            if (index >= span.Length) return true;
            if (!TryReadOffset(span, ref index, out _, out _)) { code = ResultCode.HitscanOffsetsInvalid; return false; }
            pairs++;
        }
    }

    /// <summary>Reads one pair at the cursor. Callers wrap the cursor to zero to repeat the authored fan.</summary>
    public static bool TryReadOffset(ReadOnlySpan<char> text, ref int index, out float x, out float y)
    {
        x = y = 0f;
        while (index < text.Length && (text[index] == ',' || char.IsWhiteSpace(text[index]))) index++;
        if (index >= text.Length || !TryNumber(text, ref index, out x)) return false;
        var before = index;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index == before || !TryNumber(text, ref index, out y)) return false;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index >= text.Length || text[index] == ',';
    }

    /// <summary>One number token: the slice up to the next comma or whitespace, parsed invariantly and refused
    /// when it is not finite. A token the format cannot read is the whole value's refusal, never a zero.</summary>
    private static bool TryNumber(ReadOnlySpan<char> span, ref int index, out float value)
    {
        value = 0f;
        var start = index;
        while (index < span.Length && span[index] != ',' && !char.IsWhiteSpace(span[index])) index++;
        if (index == start) return false;
        return float.TryParse(span[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value);
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, Handler, "execute", new[] { Permission }, Shape)
    } : Array.Empty<ProviderBinding>();
}
